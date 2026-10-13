using System.Runtime.InteropServices;
using QuakeSharp.Core.Enhanced;
using Vortice.Vulkan;
using static QuakeSharp.Render.Vulkan.VulkanContext;

namespace QuakeSharp.Render.Vulkan;

/// <summary>
/// The enhanced view's brush models (design.md §11.6, step 3): the snapshot's models made ready on the GPU the first
/// time they are drawn (their vertices, indices, surfaces' lighting records, light data and textures, as palette
/// indices with the BSP's mips and as colours with full mip chains), dropped when the snapshots' numbering starts anew;
/// each frame's words (FrameWords: the view's projection, the lights, the palettes and the colormap); the world and the
/// brush entities drawn with world.slang.
/// </summary>
public sealed unsafe partial class VulkanPresenter
{
    /// <summary>The frame's words (frame.slang's offsets).</summary>
    private static class FrameWords
    {
        public const int ViewProjection = 0;
        public const int LightStyles = 16;
        public const int Dlights = 80;
        public const int DlightMinlights = 208;
        public const int Palette = 240;
        public const int BasePalette = 496;
        public const int ChannelTables = 752;
        public const int Ambient = 944;
        public const int DlightCount = 945;
        public const int Lighting = 946;
        public const int Fullbright = 947;
        public const int Time = 948;
        public const int ViewOrigin = 949;
        public const int Stepped = 952;
        public const int ViewAxes = 953;
        public const int SkyMap = 962;
        public const int SkySizes = 966;
        public const int DebugSurfaces = 1023;
        public const int Colormap = 1024;
        public const int Count = Colormap + (64 * 256 / 4);
        public const int MaxDlights = 32;
    }

    // world.slang's push constants
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawParams
    {
        public float M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23;
        public float TextureWidth, TextureHeight;
        public int Flags;
        public int Pad;
    }

    private sealed partial class WorldRenderer : IDisposable
    {
        private readonly VulkanPresenter presenter;
        private readonly VulkanContext context;
        private readonly VkDescriptorSetLayout frameSetLayout;
        private readonly VkDescriptorSetLayout modelSetLayout;
        private readonly VkDescriptorSetLayout textureSetLayout;
        private readonly VkPipelineLayout pipelineLayout;
        private readonly VkDescriptorPool framePool;
        private readonly VkDescriptorSet[] frameSets = new VkDescriptorSet[FramesInFlight];
        private readonly HostBuffer[] frameBuffers = new HostBuffer[FramesInFlight];
        private readonly Dictionary<int, GpuBrushModel> models = [];
        private VkSampler colorSampler;
        private (int Filter, float Anisotropy) samplerFor = (-1, -1);
        private VkPipeline pipeline;
        private VkSampleCountFlags pipelineSamples;
        private EnhancedModelNumbering? numbering;

        public WorldRenderer(VulkanPresenter presenter)
        {
            this.presenter = presenter;
            context = presenter.context;
            VkDeviceApi api = context.Api;
            frameSetLayout = presenter.CreateSetLayout(VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
                (VkDescriptorType.StorageBuffer, 0));
            modelSetLayout = presenter.CreateSetLayout(VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
                (VkDescriptorType.StorageBuffer, 0),
                (VkDescriptorType.StorageBuffer, 1));
            textureSetLayout = presenter.CreateSetLayout(
                (VkDescriptorType.SampledImage, 0),
                (VkDescriptorType.CombinedImageSampler, 1));

            VkDescriptorSetLayout* layouts = stackalloc VkDescriptorSetLayout[3] { frameSetLayout, modelSetLayout, textureSetLayout };
            VkPushConstantRange range = new()
            {
                stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
                size = (uint)sizeof(DrawParams),
            };
            VkPipelineLayoutCreateInfo layoutInfo = new()
            {
                setLayoutCount = 3,
                pSetLayouts = layouts,
                pushConstantRangeCount = 1,
                pPushConstantRanges = &range,
            };
            Check(api.vkCreatePipelineLayout(&layoutInfo, null, out pipelineLayout), "vkCreatePipelineLayout");

            VkDescriptorPoolSize size = new() { type = VkDescriptorType.StorageBuffer, descriptorCount = FramesInFlight };
            VkDescriptorPoolCreateInfo poolInfo = new() { maxSets = FramesInFlight, poolSizeCount = 1, pPoolSizes = &size };
            Check(api.vkCreateDescriptorPool(&poolInfo, null, out framePool), "vkCreateDescriptorPool");
            for (int i = 0; i < FramesInFlight; i++)
            {
                frameSets[i] = Allocate(framePool, frameSetLayout);
                frameBuffers[i] = new HostBuffer(context, FrameWords.Count * 4, VkBufferUsageFlags.StorageBuffer);
                particleBuffers[i] = new HostBuffer(context, MaxParticles * 16, VkBufferUsageFlags.StorageBuffer);
                VkDescriptorBufferInfo info = new() { buffer = frameBuffers[i].Buffer, range = FrameWords.Count * 4 };
                VkWriteDescriptorSet write = new()
                {
                    dstSet = frameSets[i],
                    dstBinding = 0,
                    descriptorCount = 1,
                    descriptorType = VkDescriptorType.StorageBuffer,
                    pBufferInfo = &info,
                };
                api.vkUpdateDescriptorSets(1, &write, 0, null);
            }

            CreateModelParts();
        }

        /// <summary>
        /// Before the frame's commands: the models it shows made ready (or all dropped when the numbering started anew),
        /// the pipeline for these samples, the frame's words written.
        /// </summary>
        public void Prepare(int f, EnhancedFrame frame, EnhancedLayout layout, EnhancedOptions options, VkSampleCountFlags samples,
            int targetWidth, int targetHeight)
        {
            if (!ReferenceEquals(frame.ModelNumbering, numbering))
            {
                context.WaitIdle();
                foreach (GpuBrushModel m in models.Values)
                {
                    m.Dispose();
                }

                models.Clear();
                DisposeModelParts(all: false);
                numbering = frame.ModelNumbering;
            }

            float anisotropy = Math.Min(Math.Max(1, options.Anisotropy), context.MaxAnisotropy);
            if (samplerFor != (options.Filter, anisotropy))
            {
                context.WaitIdle();
                foreach (GpuBrushModel m in models.Values)
                {
                    m.Dispose();
                }

                models.Clear();
                DisposeModelParts(all: false);
                if (colorSampler != VkSampler.Null)
                {
                    context.Api.vkDestroySampler(colorSampler);
                }

                colorSampler = CreateSampler(options.Filter != 0, anisotropy);
                samplerFor = (options.Filter, anisotropy);
            }

            if (pipeline == VkPipeline.Null || samples != pipelineSamples)
            {
                context.WaitIdle();
                if (pipeline != VkPipeline.Null)
                {
                    context.Api.vkDestroyPipeline(pipeline);
                }

                pipeline = CreateWorldPipeline(samples);
                CreateModelPipelines(samples);
                pipelineSamples = samples;
            }

            if (frame.InLevel)
            {
                Model(frame, frame.World, options);
                foreach (EnhancedEntity e in frame.Entities)
                {
                    Model(frame, e.Model, options);
                    OtherModel(frame, e.Model);
                }
            }

            UploadReplacements();
            useReplacements = options.Textures && options.Lighting == 0;

            WriteParticles(particleBuffers[f], frame);
            SetParticles(options.Particles != 0);

            WriteFrameWords(frameBuffers[f], frame, layout, options, targetWidth, targetHeight);
        }

        /// <summary>The world and the brush entities, inside the view's rendering.</summary>
        public void Draw(VkCommandBuffer cmd, int f, EnhancedFrame frame, EnhancedLayout layout)
        {
            if (!frame.InLevel || !frame.ViewDrawn)
            {
                return;
            }

            VkDeviceApi api = context.Api;
            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, pipeline);
            VkDescriptorSet frameSet = frameSets[f];
            api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, pipelineLayout, 0, 1, &frameSet, 0, null);

            float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
            if (models.TryGetValue(frame.World, out GpuBrushModel? world))
            {
                world.Draw(cmd, this, 0, identity, 0, frame.Time);
            }

            foreach (EnhancedEntity e in frame.Entities)
            {
                if (e.Model <= 0 || e.Model >= frame.Models.Count || frame.Models[e.Model] is not { } model)
                {
                    continue;
                }

                // r_drawentities 0 leaves the entities out, but for the view model (R_DrawViewModel's own test)
                if (!frame.DrawEntities && e.Id != EnhancedEntity.ViewModel)
                {
                    continue;
                }

                float[] transform = EnhancedCamera.BrushTransform(e.Origin, e.Angles);
                if (model.Brush != null && models.TryGetValue(e.Model, out GpuBrushModel? own))
                {
                    own.Draw(cmd, this, 0, transform, e.Frame, frame.Time);
                }
                else if (model.Parent != 0 && models.TryGetValue(model.Parent, out GpuBrushModel? parent))
                {
                    parent.Draw(cmd, this, model.Submodel, transform, e.Frame, frame.Time);
                }
            }

            DrawModels(cmd, f, frame, layout);
        }

        public void Dispose()
        {
            VkDeviceApi api = context.Api;
            loader?.Dispose();
            foreach (GpuBrushModel m in models.Values)
            {
                m.Dispose();
            }

            foreach (HostBuffer b in frameBuffers)
            {
                b.Dispose();
            }

            foreach (HostBuffer b in particleBuffers)
            {
                b.Dispose();
            }

            DisposeModelParts(all: true);

            if (pipeline != VkPipeline.Null)
            {
                api.vkDestroyPipeline(pipeline);
            }

            if (colorSampler != VkSampler.Null)
            {
                api.vkDestroySampler(colorSampler);
            }

            api.vkDestroyDescriptorPool(framePool);
            api.vkDestroyPipelineLayout(pipelineLayout);
            api.vkDestroyDescriptorSetLayout(frameSetLayout);
            api.vkDestroyDescriptorSetLayout(modelSetLayout);
            api.vkDestroyDescriptorSetLayout(textureSetLayout);
        }

        private void Model(EnhancedFrame frame, int id, EnhancedOptions options)
        {
            if (id <= 0 || id >= frame.Models.Count || models.ContainsKey(id) || frame.Models[id]?.Brush is not { } brush)
            {
                return;
            }

            models[id] = new GpuBrushModel(this, brush, frame.BasePalette, options.Mipmaps != 0);
        }

        private static void WriteFrameWords(HostBuffer buffer, EnhancedFrame frame, EnhancedLayout layout, EnhancedOptions options,
            int targetWidth, int targetHeight)
        {
            var words = new Span<uint>(buffer.Mapped, FrameWords.Count);
            words.Clear();
            if (layout.HasView)
            {
                float[] m = EnhancedCamera.ViewProjection(frame.View, frame.PixelAspect, (double)layout.View.Width / layout.View.Height);
                for (int i = 0; i < 16; i++)
                {
                    words[FrameWords.ViewProjection + i] = BitConverter.SingleToUInt32Bits(m[i]);
                }
            }

            for (int i = 0; i < 64; i++)
            {
                words[FrameWords.LightStyles + i] = (uint)frame.LightStyles[i];
            }

            int count = Math.Min(frame.Dlights.Length, FrameWords.MaxDlights);
            for (int i = 0; i < count; i++)
            {
                EnhancedDlight d = frame.Dlights[i];
                int b = FrameWords.Dlights + (i * 4);
                words[b] = BitConverter.SingleToUInt32Bits(d.Origin[0]);
                words[b + 1] = BitConverter.SingleToUInt32Bits(d.Origin[1]);
                words[b + 2] = BitConverter.SingleToUInt32Bits(d.Origin[2]);
                words[b + 3] = BitConverter.SingleToUInt32Bits(d.Radius);
                words[FrameWords.DlightMinlights + i] = BitConverter.SingleToUInt32Bits(d.Minlight);
            }

            for (int i = 0; i < 256; i++)
            {
                words[FrameWords.Palette + i] = Rgb(frame.Palette, i);
                words[FrameWords.BasePalette + i] = frame.BasePalette.Length >= 768 ? Rgb(frame.BasePalette, i) : Rgb(frame.Palette, i);
            }

            MemoryMarshal.AsBytes(words.Slice(FrameWords.ChannelTables, 192)).Clear();
            frame.ChannelTables.AsSpan(0, 768).CopyTo(MemoryMarshal.AsBytes(words.Slice(FrameWords.ChannelTables, 192)));
            words[FrameWords.Ambient] = (uint)frame.AmbientLight;
            words[FrameWords.DlightCount] = (uint)count;
            words[FrameWords.Lighting] = (uint)options.Lighting;
            bool worldUnlit = frame.World > 0 && frame.World < frame.Models.Count && frame.Models[frame.World]?.Brush is { LightData.Length: 0 };
            words[FrameWords.Fullbright] = frame.Fullbright || worldUnlit ? 1u : 0u;
            words[FrameWords.Time] = BitConverter.SingleToUInt32Bits((float)frame.Time);
            for (int i = 0; i < 3; i++)
            {
                words[FrameWords.ViewOrigin + i] = BitConverter.SingleToUInt32Bits(frame.View.Origin[i]);
            }

            // the software's look moves the sky and water in its steps, as it lights by its colormap, and maps the sky as
            // d_sky.c does: the view's axes, and the frame's pixel each of the view target's shows
            words[FrameWords.Stepped] = (uint)options.Lighting;
            words[FrameWords.DebugSurfaces] = options.DebugSurfaces ? 1u : 0u;
            (double[] forward, double[] right, double[] up) = EnhancedCamera.AngleVectors(frame.View.Angles[0], frame.View.Angles[1], frame.View.Angles[2]);
            double[][] axes = [forward, right, up];
            for (int a = 0; a < 3; a++)
            {
                for (int i = 0; i < 3; i++)
                {
                    words[FrameWords.ViewAxes + (a * 3) + i] = BitConverter.SingleToUInt32Bits((float)axes[a][i]);
                }
            }

            if (layout.HasView)
            {
                double sx = (double)layout.Frame.Width / frame.Width, sy = (double)layout.Frame.Height / frame.Height;
                double kx = (double)layout.View.Width / targetWidth, ky = (double)layout.View.Height / targetHeight;
                float[] map = [(float)(kx / sx), (float)((layout.View.X - layout.Frame.X) / sx), (float)(ky / sy), (float)((layout.View.Y - layout.Frame.Y) / sy)];
                float[] sizes = [Math.Max(frame.View.Width, frame.View.Height), frame.Width >> 1, frame.Height >> 1];
                for (int i = 0; i < 4; i++)
                {
                    words[FrameWords.SkyMap + i] = BitConverter.SingleToUInt32Bits(map[i]);
                }

                for (int i = 0; i < 3; i++)
                {
                    words[FrameWords.SkySizes + i] = BitConverter.SingleToUInt32Bits(sizes[i]);
                }
            }
            if (frame.Colormap.Length >= 64 * 256)
            {
                frame.Colormap.AsSpan(0, 64 * 256).CopyTo(MemoryMarshal.AsBytes(words.Slice(FrameWords.Colormap, 64 * 256 / 4)));
            }

            buffer.Flush();
        }

        private static uint Rgb(byte[] palette, int i) =>
            palette[i * 3] | ((uint)palette[(i * 3) + 1] << 8) | ((uint)palette[(i * 3) + 2] << 16);

        private VkSampler CreateSampler(bool linear, float anisotropy)
        {
            VkSamplerCreateInfo info = new()
            {
                magFilter = linear ? VkFilter.Linear : VkFilter.Nearest,
                minFilter = linear ? VkFilter.Linear : VkFilter.Nearest,
                mipmapMode = VkSamplerMipmapMode.Linear,
                addressModeU = VkSamplerAddressMode.Repeat,
                addressModeV = VkSamplerAddressMode.Repeat,
                addressModeW = VkSamplerAddressMode.Repeat,
                anisotropyEnable = anisotropy > 1,
                maxAnisotropy = Math.Max(1, anisotropy),
                maxLod = 16,
            };
            Check(context.Api.vkCreateSampler(&info, null, out VkSampler sampler), "vkCreateSampler");
            return sampler;
        }

        private VkDescriptorSet Allocate(VkDescriptorPool pool, VkDescriptorSetLayout layout)
        {
            VkDescriptorSetAllocateInfo info = new() { descriptorPool = pool, descriptorSetCount = 1, pSetLayouts = &layout };
            VkDescriptorSet set;
            Check(context.Api.vkAllocateDescriptorSets(&info, &set), "vkAllocateDescriptorSets");
            return set;
        }

        // world.slang into the view's target: its vertices, depth tested and written (reversed: nearer is greater)
        private VkPipeline CreateWorldPipeline(VkSampleCountFlags samples) =>
            CreateViewPipeline("world", pipelineLayout, samples, (uint)sizeof(EnhancedBrushVertex),
                [(VkFormat.R32G32B32Sfloat, 0), (VkFormat.R32G32Sfloat, 12), (VkFormat.R32Sint, 20)]);

        // A pipeline of a shader drawing into the view's target, depth tested and written; vertices of the given stride
        // and attributes (none: the shader makes its own)
        private VkPipeline CreateViewPipeline(string shader, VkPipelineLayout layout, VkSampleCountFlags samples, uint stride,
            (VkFormat Format, uint Offset)[] vertexAttributes)
        {
            VkDeviceApi api = context.Api;
            byte[] spirv = LoadShader(shader);
            VkShaderModule module;
            fixed (byte* code = spirv)
            {
                VkShaderModuleCreateInfo moduleInfo = new() { codeSize = (nuint)spirv.Length, pCode = (uint*)code };
                Check(api.vkCreateShaderModule(&moduleInfo, null, out module), "vkCreateShaderModule");
            }

            VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new() { stage = VkShaderStageFlags.Vertex, module = module, pName = EntryVertex };
            stages[1] = new() { stage = VkShaderStageFlags.Fragment, module = module, pName = EntryFragment };
            VkVertexInputBindingDescription binding = new() { binding = 0, stride = stride, inputRate = VkVertexInputRate.Vertex };
            VkVertexInputAttributeDescription* attributes = stackalloc VkVertexInputAttributeDescription[Math.Max(1, vertexAttributes.Length)];
            for (int i = 0; i < vertexAttributes.Length; i++)
            {
                attributes[i] = new() { location = (uint)i, binding = 0, format = vertexAttributes[i].Format, offset = vertexAttributes[i].Offset };
            }

            VkPipelineVertexInputStateCreateInfo vertexInput = vertexAttributes.Length == 0 ? new() : new()
            {
                vertexBindingDescriptionCount = 1,
                pVertexBindingDescriptions = &binding,
                vertexAttributeDescriptionCount = (uint)vertexAttributes.Length,
                pVertexAttributeDescriptions = attributes,
            };
            VkPipelineInputAssemblyStateCreateInfo inputAssembly = new() { topology = VkPrimitiveTopology.TriangleList };
            VkPipelineViewportStateCreateInfo viewport = new() { viewportCount = 1, scissorCount = 1 };
            VkPipelineRasterizationStateCreateInfo raster = new()
            {
                polygonMode = VkPolygonMode.Fill,
                cullMode = VkCullModeFlags.None,
                frontFace = VkFrontFace.CounterClockwise,
                lineWidth = 1f,
            };
            VkPipelineMultisampleStateCreateInfo multisample = new() { rasterizationSamples = samples };
            VkPipelineDepthStencilStateCreateInfo depth = new()
            {
                depthTestEnable = true,
                depthWriteEnable = true,
                depthCompareOp = VkCompareOp.Greater,
            };
            VkPipelineColorBlendAttachmentState blendAttachment = new() { colorWriteMask = VkColorComponentFlags.All };
            VkPipelineColorBlendStateCreateInfo blend = new() { attachmentCount = 1, pAttachments = &blendAttachment };
            VkDynamicState* dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            VkPipelineDynamicStateCreateInfo dynamic = new() { dynamicStateCount = 2, pDynamicStates = dynamicStates };
            VkFormat colorFormat = ViewFormat;
            VkPipelineRenderingCreateInfo rendering = new()
            {
                colorAttachmentCount = 1,
                pColorAttachmentFormats = &colorFormat,
                depthAttachmentFormat = DepthFormat,
            };
            VkGraphicsPipelineCreateInfo info = new()
            {
                pNext = &rendering,
                stageCount = 2,
                pStages = stages,
                pVertexInputState = &vertexInput,
                pInputAssemblyState = &inputAssembly,
                pViewportState = &viewport,
                pRasterizationState = &raster,
                pMultisampleState = &multisample,
                pDepthStencilState = &depth,
                pColorBlendState = &blend,
                pDynamicState = &dynamic,
                layout = layout,
            };
            VkPipeline created;
            Check(api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &info, &created), "vkCreateGraphicsPipelines");
            api.vkDestroyShaderModule(module);
            return created;
        }

        // Runs commands once and waits for them (uploads)
        private void Submit(Action<VkCommandBuffer> record)
        {
            VkDeviceApi api = context.Api;
            VkCommandBufferAllocateInfo allocateInfo = new() { commandPool = presenter.commandPool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
            VkCommandBuffer cmd;
            Check(api.vkAllocateCommandBuffers(&allocateInfo, &cmd), "vkAllocateCommandBuffers");
            VkCommandBufferBeginInfo beginInfo = new() { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
            api.vkBeginCommandBuffer(cmd, &beginInfo);
            record(cmd);
            api.vkEndCommandBuffer(cmd);
            VkCommandBufferSubmitInfo cmdInfo = new() { commandBuffer = cmd };
            VkSubmitInfo2 submit = new() { commandBufferInfoCount = 1, pCommandBufferInfos = &cmdInfo };
            Check(api.vkQueueSubmit2(context.Queue, 1, &submit, VkFence.Null), "vkQueueSubmit2");
            context.WaitIdle();
            api.vkFreeCommandBuffers(presenter.commandPool, 1, &cmd);
        }

        /// <summary>A brush model on the GPU.</summary>
        private sealed class GpuBrushModel : IDisposable
        {
            private readonly WorldRenderer owner;
            private readonly EnhancedBrushModel model;
            private readonly HostBuffer vertices, indices, surfaces, lightData;
            private readonly VkDescriptorPool pool;
            private readonly VkDescriptorSet modelSet;
            private readonly GpuTexture[] textures;

            public GpuBrushModel(WorldRenderer owner, EnhancedBrushModel model, byte[] basePalette, bool fullMips)
            {
                this.owner = owner;
                this.model = model;
                VulkanContext context = owner.context;
                vertices = Buffer(MemoryMarshal.AsBytes(model.Vertices.AsSpan()), VkBufferUsageFlags.VertexBuffer);
                indices = Buffer(MemoryMarshal.AsBytes(model.Indices.AsSpan()), VkBufferUsageFlags.IndexBuffer);
                surfaces = Buffer(MemoryMarshal.AsBytes(model.Surfaces.AsSpan()), VkBufferUsageFlags.StorageBuffer);
                lightData = Buffer(model.LightData, VkBufferUsageFlags.StorageBuffer);

                VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[3];
                sizes[0] = new() { type = VkDescriptorType.StorageBuffer, descriptorCount = 2 };
                // two sets a texture: the game's colours and a pack's (VulkanPresenter.Packs.cs)
                sizes[1] = new() { type = VkDescriptorType.SampledImage, descriptorCount = (uint)Math.Max(1, 2 * model.Textures.Length) };
                sizes[2] = new() { type = VkDescriptorType.CombinedImageSampler, descriptorCount = (uint)Math.Max(1, 2 * model.Textures.Length) };
                VkDescriptorPoolCreateInfo poolInfo = new() { maxSets = (uint)(1 + (2 * model.Textures.Length)), poolSizeCount = 3, pPoolSizes = sizes };
                Check(context.Api.vkCreateDescriptorPool(&poolInfo, null, out pool), "vkCreateDescriptorPool");
                modelSet = owner.Allocate(pool, owner.modelSetLayout);
                VkDescriptorBufferInfo surfacesInfo = new() { buffer = surfaces.Buffer, range = surfaces.Size };
                VkDescriptorBufferInfo lightInfo = new() { buffer = lightData.Buffer, range = lightData.Size };
                VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[2];
                writes[0] = new() { dstSet = modelSet, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.StorageBuffer, pBufferInfo = &surfacesInfo };
                writes[1] = new() { dstSet = modelSet, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.StorageBuffer, pBufferInfo = &lightInfo };
                context.Api.vkUpdateDescriptorSets(2, writes, 0, null);

                textures = [.. model.Textures.Select(t => new GpuTexture(owner, pool, t, basePalette, fullMips))];
                owner.RequestReplacements(model, textures);
            }

            public void Draw(VkCommandBuffer cmd, WorldRenderer renderer, int submodel, float[] transform, int frame, double time)
            {
                if (submodel < 0 || submodel >= model.Submodels.Length)
                {
                    return;
                }

                VkDeviceApi api = renderer.context.Api;
                VkBuffer vertexBuffer = vertices.Buffer;
                ulong offset = 0;
                api.vkCmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
                api.vkCmdBindIndexBuffer(cmd, indices.Buffer, 0, VkIndexType.Uint32);
                VkDescriptorSet set = modelSet;
                api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, renderer.pipelineLayout, 1, 1, &set, 0, null);

                foreach ((int texture, int first, int count) in model.Submodels[submodel])
                {
                    int shown = model.TextureAnimation(texture, frame, time);
                    if (shown < 0 || shown >= textures.Length)
                    {
                        continue;
                    }

                    GpuTexture t = textures[shown];
                    bool replaced = renderer.useReplacements && t.ReplacedSet != VkDescriptorSet.Null;
                    VkDescriptorSet textureSet = replaced ? t.ReplacedSet : t.Set;
                    api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, renderer.pipelineLayout, 2, 1, &textureSet, 0, null);
                    DrawParams p = new()
                    {
                        M00 = transform[0], M01 = transform[1], M02 = transform[2], M03 = transform[3],
                        M10 = transform[4], M11 = transform[5], M12 = transform[6], M13 = transform[7],
                        M20 = transform[8], M21 = transform[9], M22 = transform[10], M23 = transform[11],
                        TextureWidth = t.Width,
                        TextureHeight = t.Height,
                        Flags = replaced ? DrawReplaced : 0,
                    };
                    api.vkCmdPushConstants(cmd, renderer.pipelineLayout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, (uint)sizeof(DrawParams), &p);
                    api.vkCmdDrawIndexed(cmd, (uint)count, 1, (uint)first, 0, 0);
                }
            }

            public void Dispose()
            {
                foreach (GpuTexture t in textures)
                {
                    t.Dispose();
                }

                owner.context.Api.vkDestroyDescriptorPool(pool);
                vertices.Dispose();
                indices.Dispose();
                surfaces.Dispose();
                lightData.Dispose();
            }

            private HostBuffer Buffer(ReadOnlySpan<byte> data, VkBufferUsageFlags usage)
            {
                var buffer = new HostBuffer(owner.context, (ulong)Math.Max(4, (data.Length + 3) & ~3), usage);
                data.CopyTo(new Span<byte>(buffer.Mapped, data.Length));
                buffer.Flush();
                return buffer;
            }
        }

        /// <summary>
        /// A BSP texture on the GPU: its palette indices (the BSP's four mips) and its colours (a mip chain); once a
        /// pack's replacement is uploaded, a second set with the same indices and the pack's colours.
        /// </summary>
        private sealed class GpuTexture : IDisposable
        {
            private readonly WorldRenderer owner;
            private readonly VkDescriptorPool pool;
            private readonly Image indices, colors;
            private Image? replacement;
            private volatile bool disposed;

            public GpuTexture(WorldRenderer owner, VkDescriptorPool pool, EnhancedTexture texture, byte[] basePalette, bool fullMips)
            {
                this.owner = owner;
                this.pool = pool;
                Width = texture.Width;
                Height = texture.Height;
                VulkanContext context = owner.context;
                indices = new Image(context, (uint)texture.Width, (uint)texture.Height, VkFormat.R8Uint,
                    VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled, mipLevels: 4);
                List<(int Width, int Height, byte[] Rgba)> chain = EnhancedTextures.ColorMips(texture, basePalette, fullMips);
                colors = new Image(context, (uint)texture.Width, (uint)texture.Height, VkFormat.R8G8B8A8Unorm,
                    VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled, mipLevels: (uint)chain.Count);

                int total = texture.Pixels.Length + chain.Sum(m => m.Rgba.Length);
                using var staging = new HostBuffer(context, (ulong)total, VkBufferUsageFlags.TransferSrc);
                texture.Pixels.AsSpan().CopyTo(new Span<byte>(staging.Mapped, texture.Pixels.Length));
                int at = texture.Pixels.Length;
                var colorOffsets = new int[chain.Count];
                for (int m = 0; m < chain.Count; m++)
                {
                    colorOffsets[m] = at;
                    chain[m].Rgba.AsSpan().CopyTo(new Span<byte>(staging.Mapped + at, chain[m].Rgba.Length));
                    at += chain[m].Rgba.Length;
                }

                staging.Flush();
                owner.Submit(cmd =>
                {
                    Copy(owner, cmd, staging, indices, 4, m => (texture.MipOffsets[m], Math.Max(1, texture.Width >> m), Math.Max(1, texture.Height >> m)));
                    Copy(owner, cmd, staging, colors, chain.Count, m => (colorOffsets[m], chain[m].Width, chain[m].Height));
                });

                Set = TextureSet(colors);
            }

            public int Width { get; }

            public int Height { get; }

            public VkDescriptorSet Set { get; }

            /// <summary>The set with a pack's colours (null until one is uploaded).</summary>
            public VkDescriptorSet ReplacedSet { get; private set; }

            /// <summary>Gone (its level's models dropped): the loader skips it, its replacement is not uploaded.</summary>
            public bool Disposed => disposed;

            /// <summary>A pack's colours, uploaded, in place of the game's.</summary>
            public void Replace(Image image)
            {
                replacement?.Dispose();
                replacement = image;
                ReplacedSet = TextureSet(image);
            }

            public void Dispose()
            {
                disposed = true;
                indices.Dispose();
                colors.Dispose();
                replacement?.Dispose();
            }

            // world.slang's set 2: the indices, and these colours through the colour sampler
            private VkDescriptorSet TextureSet(Image colorImage)
            {
                VkDescriptorSet set = owner.Allocate(pool, owner.textureSetLayout);
                VkDescriptorImageInfo indicesInfo = new() { imageView = indices.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
                VkDescriptorImageInfo colorsInfo = new() { sampler = owner.colorSampler, imageView = colorImage.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
                VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[2];
                writes[0] = new() { dstSet = set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.SampledImage, pImageInfo = &indicesInfo };
                writes[1] = new() { dstSet = set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.CombinedImageSampler, pImageInfo = &colorsInfo };
                owner.context.Api.vkUpdateDescriptorSets(2, writes, 0, null);
                return set;
            }

            public static void Copy(WorldRenderer owner, VkCommandBuffer cmd, HostBuffer staging, Image image, int levels,
                Func<int, (int Offset, int Width, int Height)> level)
            {
                owner.presenter.Barrier(cmd, image.Handle, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
                    VkPipelineStageFlags2.None, VkAccessFlags2.None, VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite,
                    VkImageAspectFlags.Color, (uint)levels);
                for (int m = 0; m < levels; m++)
                {
                    (int offset, int width, int height) = level(m);
                    VkBufferImageCopy region = new()
                    {
                        bufferOffset = (ulong)offset,
                        imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, (uint)m, 0, 1),
                        imageExtent = new VkExtent3D(width, height, 1),
                    };
                    owner.context.Api.vkCmdCopyBufferToImage(cmd, staging.Buffer, image.Handle, VkImageLayout.TransferDstOptimal, 1, &region);
                }

                owner.presenter.Barrier(cmd, image.Handle, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal,
                    VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite, VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead,
                    VkImageAspectFlags.Color, (uint)levels);
            }
        }
    }
}
