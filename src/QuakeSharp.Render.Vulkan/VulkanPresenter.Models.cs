using System.Runtime.InteropServices;
using QuakeSharp.Core;
using QuakeSharp.Core.Enhanced;
using QuakeSharp.Core.Numerics;
using Vortice.Vulkan;
using static QuakeSharp.Render.Vulkan.VulkanContext;

namespace QuakeSharp.Render.Vulkan;

/// <summary>
/// The enhanced view's alias models, sprites and particles (design.md §11.6, step 5): made ready on the GPU the first
/// time they are drawn and dropped with the world's models, drawn after the world and the brush entities with
/// alias.slang, sprite.slang and particle.slang.
/// </summary>
public sealed unsafe partial class VulkanPresenter
{
    private const int MaxParticles = 2048;

    // alias.slang's push constants
    [StructLayout(LayoutKind.Sequential)]
    private struct AliasParams
    {
        public float M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23;
        public float LightX, LightY, LightZ, Shade;
        public float ScaleX, ScaleY, ScaleZ, Blend;
        public float OriginX, OriginY, OriginZ, Unused;
        public int Pose, Pose2, Ambient, SkinWidth, SkinHeight, Top, Bottom, Flags;
    }

    // sprite.slang's push constants
    [StructLayout(LayoutKind.Sequential)]
    private struct SpriteParams
    {
        public float OriginX, OriginY, OriginZ, Unused;
        public float UpX, UpY, UpZ, Up;
        public float RightX, RightY, RightZ, Down;
        public float Left, Right, Width, Height;
    }

    // particle.slang's push constants
    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleParams
    {
        public float PixelX, PixelY, Shrink;
        public int PixShift, PixMin, PixMax, AspectShift, Round;
    }

    private sealed partial class WorldRenderer
    {
        private readonly HostBuffer[] particleBuffers = new HostBuffer[FramesInFlight];
        private readonly Dictionary<int, GpuAliasModel> aliasModels = [];
        private readonly Dictionary<int, GpuSpriteModel> spriteModels = [];
        private int particleCount;
        private VkDescriptorSetLayout aliasSetLayout, particleSetLayout;
        private VkPipelineLayout aliasPipelineLayout, particlePipelineLayout;
        private VkDescriptorPool particlePool;
        private readonly VkDescriptorSet[] particleSets = new VkDescriptorSet[FramesInFlight];
        private HostBuffer? normals;
        private VkPipeline aliasPipeline, spritePipeline, particlePipeline;
        private bool roundParticles;

        private void CreateModelParts()
        {
            VkDeviceApi api = context.Api;
            aliasSetLayout = presenter.CreateSetLayout(VkShaderStageFlags.Vertex,
                (VkDescriptorType.StorageBuffer, 0),
                (VkDescriptorType.StorageBuffer, 1));
            particleSetLayout = presenter.CreateSetLayout(VkShaderStageFlags.Vertex, (VkDescriptorType.StorageBuffer, 0));
            aliasPipelineLayout = Layout([frameSetLayout, aliasSetLayout, textureSetLayout], (uint)sizeof(AliasParams));
            particlePipelineLayout = Layout([frameSetLayout, particleSetLayout], (uint)sizeof(ParticleParams));

            VkDescriptorPoolSize size = new() { type = VkDescriptorType.StorageBuffer, descriptorCount = FramesInFlight };
            VkDescriptorPoolCreateInfo poolInfo = new() { maxSets = FramesInFlight, poolSizeCount = 1, pPoolSizes = &size };
            Check(api.vkCreateDescriptorPool(&poolInfo, null, out particlePool), "vkCreateDescriptorPool");
            for (int i = 0; i < FramesInFlight; i++)
            {
                particleSets[i] = Allocate(particlePool, particleSetLayout);
                VkDescriptorBufferInfo info = new() { buffer = particleBuffers[i].Buffer, range = particleBuffers[i].Size };
                VkWriteDescriptorSet write = new()
                {
                    dstSet = particleSets[i],
                    dstBinding = 0,
                    descriptorCount = 1,
                    descriptorType = VkDescriptorType.StorageBuffer,
                    pBufferInfo = &info,
                };
                api.vkUpdateDescriptorSets(1, &write, 0, null);
            }

            // r_alias.c's r_avertexnormals, once
            normals = new HostBuffer(context, (ulong)(QuakeEngine.r_avertexnormals.Length * 4), VkBufferUsageFlags.StorageBuffer);
            QuakeEngine.r_avertexnormals.AsSpan().CopyTo(new Span<float>(normals.Mapped, QuakeEngine.r_avertexnormals.Length));
            normals.Flush();
        }

        private void CreateModelPipelines(VkSampleCountFlags samples)
        {
            VkDeviceApi api = context.Api;
            foreach (VkPipeline p in (ReadOnlySpan<VkPipeline>)[aliasPipeline, spritePipeline, particlePipeline])
            {
                if (p != VkPipeline.Null)
                {
                    api.vkDestroyPipeline(p);
                }
            }

            aliasPipeline = CreateViewPipeline("alias", aliasPipelineLayout, samples, (uint)sizeof(EnhancedAliasCorner),
                [(VkFormat.R32Sint, 0), (VkFormat.R32G32Sfloat, 4)]);
            spritePipeline = CreateViewPipeline("sprite", aliasPipelineLayout, samples, 0, []);
            particlePipeline = CreateViewPipeline("particle", particlePipelineLayout, samples, 0, []);
        }

        private void OtherModel(EnhancedFrame frame, int id)
        {
            if (id <= 0 || id >= frame.Models.Count || frame.Models[id] is not { } model)
            {
                return;
            }

            if (model.Alias is { } alias && !aliasModels.ContainsKey(id))
            {
                aliasModels[id] = new GpuAliasModel(this, alias, frame.BasePalette);
            }
            else if (model.Sprite is { } sprite && !spriteModels.ContainsKey(id))
            {
                spriteModels[id] = new GpuSpriteModel(this, sprite, frame.BasePalette);
            }
        }

        private void WriteParticles(HostBuffer buffer, EnhancedFrame frame)
        {
            particleCount = Math.Min(frame.Particles.Length, MaxParticles);
            var data = new Span<float>(buffer.Mapped, particleCount * 4);
            for (int i = 0; i < particleCount; i++)
            {
                EnhancedParticle p = frame.Particles[i];
                data[i * 4] = p.Origin[0];
                data[(i * 4) + 1] = p.Origin[1];
                data[(i * 4) + 2] = p.Origin[2];
                data[(i * 4) + 3] = p.Color;
            }

            buffer.Flush();
        }

        // The alias models and sprites of the frame's entities (the view model among them), then the particles
        private void DrawModels(VkCommandBuffer cmd, int f, EnhancedFrame frame, EnhancedLayout layout)
        {
            VkDeviceApi api = context.Api;
            VkDescriptorSet frameSet = frameSets[f];
            foreach (EnhancedEntity e in frame.Entities)
            {
                if ((!frame.DrawEntities && e.Id != EnhancedEntity.ViewModel) || e.Model <= 0 || e.Model >= frame.Models.Count)
                {
                    continue;
                }

                if (aliasModels.TryGetValue(e.Model, out GpuAliasModel? alias))
                {
                    api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, aliasPipeline);
                    api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, aliasPipelineLayout, 0, 1, &frameSet, 0, null);
                    alias.Draw(cmd, this, e, frame.Time);
                }
                else if (spriteModels.TryGetValue(e.Model, out GpuSpriteModel? sprite))
                {
                    api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, spritePipeline);
                    api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, aliasPipelineLayout, 0, 1, &frameSet, 0, null);
                    sprite.Draw(cmd, this, e, frame);
                }
            }

            if (particleCount == 0 || !layout.HasView)
            {
                return;
            }

            // d_init.c: D_ViewChanged's particle sizes for the software view's width, one pixel of the frame in
            // clip space, r_part.c's shrunk projection
            int width = frame.View.Width;
            ParticleParams p = new()
            {
                PixelX = 2f * layout.Frame.Width / (layout.View.Width * (float)frame.Width),
                PixelY = 2f * layout.Frame.Height / (layout.View.Height * (float)frame.Height),
                Shrink = (width - 6f) / width,
                PixShift = 8 - X86.FtoI((width / 320.0f) + 0.5),
                PixMin = Math.Max(1, width / 320),
                PixMax = Math.Max(1, X86.FtoI((width / (320.0f / 4.0f)) + 0.5)),
                AspectShift = frame.PixelAspect > 1.4 ? 1 : 0,
                Round = roundParticles ? 1 : 0,
            };
            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, particlePipeline);
            VkDescriptorSet* sets = stackalloc VkDescriptorSet[2] { frameSet, particleSets[f] };
            api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, particlePipelineLayout, 0, 2, sets, 0, null);
            api.vkCmdPushConstants(cmd, particlePipelineLayout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, (uint)sizeof(ParticleParams), &p);
            api.vkCmdDraw(cmd, 6, (uint)particleCount, 0, 0);
        }

        /// <summary>Round particles (enhanced_particles 1) or the software's squares.</summary>
        public void SetParticles(bool round) => roundParticles = round;

        private void DisposeModelParts(bool all)
        {
            foreach (GpuAliasModel m in aliasModels.Values)
            {
                m.Dispose();
            }

            foreach (GpuSpriteModel m in spriteModels.Values)
            {
                m.Dispose();
            }

            aliasModels.Clear();
            spriteModels.Clear();
            if (!all)
            {
                return;
            }

            VkDeviceApi api = context.Api;
            foreach (VkPipeline p in (ReadOnlySpan<VkPipeline>)[aliasPipeline, spritePipeline, particlePipeline])
            {
                if (p != VkPipeline.Null)
                {
                    api.vkDestroyPipeline(p);
                }
            }

            normals?.Dispose();
            api.vkDestroyDescriptorPool(particlePool);
            api.vkDestroyPipelineLayout(aliasPipelineLayout);
            api.vkDestroyPipelineLayout(particlePipelineLayout);
            api.vkDestroyDescriptorSetLayout(aliasSetLayout);
            api.vkDestroyDescriptorSetLayout(particleSetLayout);
        }

        private VkPipelineLayout Layout(VkDescriptorSetLayout[] sets, uint pushConstants)
        {
            VkPushConstantRange range = new() { stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, size = pushConstants };
            fixed (VkDescriptorSetLayout* layouts = sets)
            {
                VkPipelineLayoutCreateInfo info = new()
                {
                    setLayoutCount = (uint)sets.Length,
                    pSetLayouts = layouts,
                    pushConstantRangeCount = 1,
                    pPushConstantRanges = &range,
                };
                Check(context.Api.vkCreatePipelineLayout(&info, null, out VkPipelineLayout layout), "vkCreatePipelineLayout");
                return layout;
            }
        }

        // An 8-bit picture on the GPU: its palette indices and its colours (fullbright or opaque in alpha) with a chain
        private sealed class GpuPicture : IDisposable
        {
            private readonly Image indices, colors;

            public GpuPicture(WorldRenderer owner, VkDescriptorPool pool, int width, int height, byte[] pixels, byte[] basePalette, bool clear255)
            {
                VulkanContext context = owner.context;
                var texture = new EnhancedTexture
                {
                    Name = "",
                    Width = width,
                    Height = height,
                    Pixels = pixels,
                    MipOffsets = [0, 0, 0, 0],
                };
                List<(int Width, int Height, byte[] Rgba)> chain = EnhancedTextures.ColorMips(texture, basePalette, fullChain: true);
                if (clear255)
                {
                    // a sprite's 255 is clear: no colour, no alpha (the chain made again from it)
                    byte[] top = chain[0].Rgba;
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        top[(i * 4) + 3] = (byte)(pixels[i] == 255 ? 0 : 255);
                    }

                    chain = EnhancedTextures.Halve(top, width, height);
                }

                indices = new Image(context, (uint)width, (uint)height, VkFormat.R8Uint, VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled);
                colors = new Image(context, (uint)width, (uint)height, VkFormat.R8G8B8A8Unorm,
                    VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled, mipLevels: (uint)chain.Count);
                int total = pixels.Length + chain.Sum(m => m.Rgba.Length);
                using var staging = new HostBuffer(context, (ulong)total, VkBufferUsageFlags.TransferSrc);
                pixels.AsSpan().CopyTo(new Span<byte>(staging.Mapped, pixels.Length));
                int at = pixels.Length;
                var offsets = new int[chain.Count];
                for (int m = 0; m < chain.Count; m++)
                {
                    offsets[m] = at;
                    chain[m].Rgba.AsSpan().CopyTo(new Span<byte>(staging.Mapped + at, chain[m].Rgba.Length));
                    at += chain[m].Rgba.Length;
                }

                staging.Flush();
                owner.Submit(cmd =>
                {
                    GpuTexture.Copy(owner, cmd, staging, indices, 1, _ => (0, width, height));
                    GpuTexture.Copy(owner, cmd, staging, colors, chain.Count, m => (offsets[m], chain[m].Width, chain[m].Height));
                });

                Set = owner.Allocate(pool, owner.textureSetLayout);
                VkDescriptorImageInfo indicesInfo = new() { imageView = indices.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
                VkDescriptorImageInfo colorsInfo = new() { sampler = owner.colorSampler, imageView = colors.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
                VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[2];
                writes[0] = new() { dstSet = Set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.SampledImage, pImageInfo = &indicesInfo };
                writes[1] = new() { dstSet = Set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.CombinedImageSampler, pImageInfo = &colorsInfo };
                context.Api.vkUpdateDescriptorSets(2, writes, 0, null);
            }

            public VkDescriptorSet Set { get; }

            /// <summary>The palette indices (a pack's skin is drawn beside them).</summary>
            public VkImageView IndicesView => indices.View;

            public void Dispose()
            {
                indices.Dispose();
                colors.Dispose();
            }
        }

        /// <summary>
        /// An alias model on the GPU: its poses, its corners, its skins; and a pack's skins as entities show them (each
        /// skin with a player's colours its own, design.md §11.7), asked for the first time each is drawn.
        /// </summary>
        private sealed class GpuAliasModel : IDisposable
        {
            private readonly WorldRenderer owner;
            private readonly EnhancedAliasModel model;
            private readonly byte[] basePalette;
            private readonly HostBuffer poses, corners;
            private readonly VkDescriptorPool pool;
            private readonly VkDescriptorSet set;
            private readonly GpuPicture[] skins;
            private readonly (int Skin, int Frame)[] skinNames;
            private readonly Dictionary<(int Skin, int Top, int Bottom), GpuSkinReplacement> replacements = [];

            public GpuAliasModel(WorldRenderer owner, EnhancedAliasModel model, byte[] basePalette)
            {
                this.owner = owner;
                this.model = model;
                this.basePalette = basePalette;

                // each skin's name in a pack: its number, and its frame in a group
                skinNames = new (int, int)[model.Skins.Length];
                for (int n = 0; n < model.SkinGroups.Length; n++)
                {
                    (int[] group, float[] intervals) = model.SkinGroups[n];
                    for (int k = 0; k < group.Length; k++)
                    {
                        skinNames[group[k]] = (n, intervals.Length > 0 ? k : -1);
                    }
                }

                VulkanContext context = owner.context;
                poses = Upload(context, MemoryMarshal.AsBytes(model.Poses.AsSpan()), VkBufferUsageFlags.StorageBuffer);
                corners = Upload(context, MemoryMarshal.AsBytes(model.Corners.AsSpan()), VkBufferUsageFlags.VertexBuffer);

                VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[3];
                sizes[0] = new() { type = VkDescriptorType.StorageBuffer, descriptorCount = 2 };
                sizes[1] = new() { type = VkDescriptorType.SampledImage, descriptorCount = (uint)Math.Max(1, model.Skins.Length) };
                sizes[2] = new() { type = VkDescriptorType.CombinedImageSampler, descriptorCount = (uint)Math.Max(1, model.Skins.Length) };
                VkDescriptorPoolCreateInfo poolInfo = new() { maxSets = (uint)(1 + model.Skins.Length), poolSizeCount = 3, pPoolSizes = sizes };
                Check(context.Api.vkCreateDescriptorPool(&poolInfo, null, out pool), "vkCreateDescriptorPool");
                set = owner.Allocate(pool, owner.aliasSetLayout);
                VkDescriptorBufferInfo posesInfo = new() { buffer = poses.Buffer, range = poses.Size };
                VkDescriptorBufferInfo normalsInfo = new() { buffer = owner.normals!.Buffer, range = owner.normals.Size };
                VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[2];
                writes[0] = new() { dstSet = set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.StorageBuffer, pBufferInfo = &posesInfo };
                writes[1] = new() { dstSet = set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.StorageBuffer, pBufferInfo = &normalsInfo };
                context.Api.vkUpdateDescriptorSets(2, writes, 0, null);

                skins = [.. model.Skins.Select(s => new GpuPicture(owner, pool, model.SkinWidth, model.SkinHeight, s, basePalette, clear255: false))];
            }

            public void Draw(VkCommandBuffer cmd, WorldRenderer renderer, EnhancedEntity e, double time)
            {
                if (skins.Length == 0 || model.Corners.Length == 0)
                {
                    return;
                }

                VkDeviceApi api = renderer.context.Api;
                int pose = model.Pose(e.Frame, time + e.Syncbase);
                int from = e.PoseFrom >= 0 ? e.PoseFrom : pose;
                int skin = Math.Clamp(model.Skin(e.Skin, time + e.Syncbase), 0, skins.Length - 1);
                VkDescriptorSet skinSet = skins[skin].Set;
                int flags = e.Id == EnhancedEntity.ViewModel ? 1 : 0;
                if (renderer.useReplacements && renderer.presenter.TexturePack != null)
                {
                    bool translated = e.Top >= 0 || e.Bottom >= 0;
                    (int, int, int) key = (skin, translated ? e.Top : -1, translated ? e.Bottom : -1);
                    if (!replacements.TryGetValue(key, out GpuSkinReplacement? replacement))
                    {
                        replacements[key] = replacement = new GpuSkinReplacement(renderer, skins[skin].IndicesView);
                        renderer.RequestSkin(replacement, model, skinNames[skin], model.Skins[skin], basePalette, key.Item2, key.Item3);
                    }

                    if (replacement.Set != VkDescriptorSet.Null)
                    {
                        skinSet = replacement.Set;
                        flags |= AliasReplaced;
                    }
                }

                // r_alias.c: R_AliasSetUpTransform: forward, -right and up from the angles with the pitch turned
                (double[] fwd, double[] right, double[] up) = EnhancedCamera.AngleVectors(-e.Angles[0], e.Angles[1], e.Angles[2]);
                AliasParams p = new()
                {
                    M00 = (float)fwd[0], M01 = (float)-right[0], M02 = (float)up[0], M03 = e.Origin[0],
                    M10 = (float)fwd[1], M11 = (float)-right[1], M12 = (float)up[1], M13 = e.Origin[1],
                    M20 = (float)fwd[2], M21 = (float)-right[2], M22 = (float)up[2], M23 = e.Origin[2],
                    LightX = e.LightVector[0], LightY = e.LightVector[1], LightZ = e.LightVector[2], Shade = e.LightShade,
                    ScaleX = model.Scale[0], ScaleY = model.Scale[1], ScaleZ = model.Scale[2], Blend = e.PoseFrom >= 0 ? e.PoseBlend : 0,
                    OriginX = model.ScaleOrigin[0], OriginY = model.ScaleOrigin[1], OriginZ = model.ScaleOrigin[2],
                    Pose = from, Pose2 = pose, Ambient = e.LightAmbient,
                    SkinWidth = model.SkinWidth, SkinHeight = model.SkinHeight, Top = e.Top, Bottom = e.Bottom,
                    Flags = flags,
                };

                VkBuffer vertexBuffer = corners.Buffer;
                ulong offset = 0;
                api.vkCmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
                VkDescriptorSet* sets = stackalloc VkDescriptorSet[2] { set, skinSet };
                api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, renderer.aliasPipelineLayout, 1, 2, sets, 0, null);
                api.vkCmdPushConstants(cmd, renderer.aliasPipelineLayout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, (uint)sizeof(AliasParams), &p);
                api.vkCmdDraw(cmd, (uint)model.Corners.Length, 1, 0, 0);
            }

            public void Dispose()
            {
                foreach (GpuPicture s in skins)
                {
                    s.Dispose();
                }

                foreach (GpuSkinReplacement r in replacements.Values)
                {
                    r.Dispose();
                }

                owner.context.Api.vkDestroyDescriptorPool(pool);
                poses.Dispose();
                corners.Dispose();
            }
        }

        /// <summary>A sprite on the GPU: its pictures.</summary>
        private sealed class GpuSpriteModel : IDisposable
        {
            private readonly WorldRenderer owner;
            private readonly EnhancedSpriteModel model;
            private readonly VkDescriptorPool pool;
            private readonly GpuPicture[] pictures;

            public GpuSpriteModel(WorldRenderer owner, EnhancedSpriteModel model, byte[] basePalette)
            {
                this.owner = owner;
                this.model = model;
                VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[2];
                uint count = (uint)Math.Max(1, model.Pictures.Length);
                sizes[0] = new() { type = VkDescriptorType.SampledImage, descriptorCount = count };
                sizes[1] = new() { type = VkDescriptorType.CombinedImageSampler, descriptorCount = count };
                VkDescriptorPoolCreateInfo poolInfo = new() { maxSets = count, poolSizeCount = 2, pPoolSizes = sizes };
                Check(owner.context.Api.vkCreateDescriptorPool(&poolInfo, null, out pool), "vkCreateDescriptorPool");
                pictures = [.. model.Pictures.Select(p => new GpuPicture(owner, pool, p.Width, p.Height, p.Pixels, basePalette, clear255: true))];
            }

            public void Draw(VkCommandBuffer cmd, WorldRenderer renderer, EnhancedEntity e, EnhancedFrame frame)
            {
                if (pictures.Length == 0 || model.Axes(frame.View, e.Origin, e.Angles) is not { } axes)
                {
                    return;
                }

                int picture = model.Picture(e.Frame, frame.Time + e.Syncbase);
                EnhancedSpriteFrame pic = model.Pictures[picture];
                SpriteParams p = new()
                {
                    OriginX = (float)axes.Origin[0], OriginY = (float)axes.Origin[1], OriginZ = (float)axes.Origin[2],
                    UpX = (float)axes.Up[0], UpY = (float)axes.Up[1], UpZ = (float)axes.Up[2], Up = pic.Up,
                    RightX = (float)axes.Right[0], RightY = (float)axes.Right[1], RightZ = (float)axes.Right[2], Down = pic.Down,
                    Left = pic.Left, Right = pic.Right, Width = pic.Width, Height = pic.Height,
                };

                VkDeviceApi api = renderer.context.Api;
                VkDescriptorSet set = pictures[picture].Set;
                api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, renderer.aliasPipelineLayout, 2, 1, &set, 0, null);
                api.vkCmdPushConstants(cmd, renderer.aliasPipelineLayout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, (uint)sizeof(SpriteParams), &p);
                api.vkCmdDraw(cmd, 6, 1, 0, 0);
            }

            public void Dispose()
            {
                foreach (GpuPicture p in pictures)
                {
                    p.Dispose();
                }

                owner.context.Api.vkDestroyDescriptorPool(pool);
            }
        }

        private static HostBuffer Upload(VulkanContext context, ReadOnlySpan<byte> data, VkBufferUsageFlags usage)
        {
            var buffer = new HostBuffer(context, (ulong)Math.Max(4, (data.Length + 3) & ~3), usage);
            data.CopyTo(new Span<byte>(buffer.Mapped, data.Length));
            buffer.Flush();
            return buffer;
        }
    }
}
