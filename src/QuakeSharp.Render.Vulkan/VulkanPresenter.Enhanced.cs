using QuakeSharp.Core.Enhanced;
using Vortice.Vulkan;
using static QuakeSharp.Render.Vulkan.VulkanContext;

namespace QuakeSharp.Render.Vulkan;

/// <summary>The enhanced mode's settings the presenter uses (design.md §11.6, decision 61's file).</summary>
/// <param name="Scale">The 3D's resolution as a share of its rectangle of the window (enhanced_scale).</param>
/// <param name="Msaa">Samples per pixel of the 3D (enhanced_msaa; the device's most if it has fewer).</param>
/// <param name="Widescreen">The view widened to the window (enhanced_widescreen).</param>
/// <param name="Filter">Textures: 0 nearest, 1 linear (enhanced_filter).</param>
/// <param name="Mipmaps">Textures' mip levels: 0 the BSP's own four, 1 full chains (enhanced_mipmaps).</param>
/// <param name="Anisotropy">Anisotropic filtering, 1 to 16 (enhanced_anisotropy).</param>
/// <param name="Lighting">0 true colour with overbright, 1 the colormap (enhanced_lighting).</param>
/// <param name="Particles">0 square, 1 round (enhanced_particles).</param>
public sealed record EnhancedOptions(float Scale = 1, int Msaa = 4, bool Widescreen = true, int Filter = 0, int Mipmaps = 1,
    float Anisotropy = 16, int Lighting = 0, int Particles = 0)
{
    /// <summary>Frames between the game's at the display's pace (enhanced_interpolate).</summary>
    public bool Interpolate { get; init; } = true;

    /// <summary>Frames as the display shows them (enhanced_vsync), or as fast as <see cref="MaxFps"/> lets.</summary>
    public bool Vsync { get; init; } = true;

    /// <summary>The most frames a second without vsync (enhanced_maxfps; 0: no limit).</summary>
    public int MaxFps { get; init; }

    /// <summary>Alias models' poses blended (enhanced_lerpmodels).</summary>
    public bool LerpModels { get; init; }

    /// <summary>Monsters' steps smoothed (enhanced_lerpmove).</summary>
    public bool LerpMove { get; init; }

    /// <summary>Each pixel of the view the number of its surface (red the low byte, green the high; the tools' debugging).</summary>
    public bool DebugSurfaces { get; init; }
}

/// <summary>
/// The enhanced frame (design.md §11.6): the 3D view drawn at the window's resolution into its own multisampled target
/// and resolved, the 2D (the snapshot's frame where its mask says the 2D wrote it) scaled as the faithful presentation
/// scales it, and the two put together over the whole window, widened beside the 2D in a wide window.
/// </summary>
public sealed unsafe partial class VulkanPresenter
{
    private const VkFormat OverlayFormat = VkFormat.R8G8B8A8Unorm;
    private const VkFormat WeightFormat = VkFormat.R8Unorm;
    private const VkFormat ViewFormat = VkFormat.R8G8B8A8Unorm;
    private const VkFormat DepthFormat = VkFormat.D32Sfloat;

    private EnhancedTargets? enhanced;

    /// <summary>Shows an enhanced frame (from the enhanced renderer's thread; one caller at a time with <see cref="Present"/>).</summary>
    public void PresentEnhanced(EnhancedFrame frame, EnhancedOptions options)
    {
        lock (presenting)
        {
            PresentEnhancedLocked(frame, options);
        }
    }

    private void PresentEnhancedLocked(EnhancedFrame frame, EnhancedOptions options)
    {
        if (!BeginFrame(out int f, out uint imageIndex, options.Vsync))
        {
            return;
        }

        int windowWidth = (int)swapchain!.Extent.width, windowHeight = (int)swapchain.Extent.height;
        (EnhancedLayout layout, HostBuffer buffer) = PrepareEnhanced(f, frame, options, windowWidth, windowHeight, swapchain.Format);
        VkCommandBuffer cmd = StartCommands(f);
        enhanced!.Record(cmd, f, buffer, frame, layout, swapchain.Images[imageIndex], swapchain.Views[imageIndex],
            new VkRect2D(0, 0, (uint)windowWidth, (uint)windowHeight), options.Lighting != 0);
        EndFrame(f, imageIndex, cmd);
    }

    // The enhanced frame's layout in a target of this size, its targets made ready, its models, words and pixels
    // uploaded (frame f's staging buffer)
    private (EnhancedLayout Layout, HostBuffer Buffer) PrepareEnhanced(int f, EnhancedFrame frame, EnhancedOptions options,
        int width, int height, VkFormat format)
    {
        EnhancedLayout layout = EnhancedLayout.Compute(frame, width, height, SquarePixels, options.Widescreen);
        float scale = Math.Clamp(options.Scale, 0.25f, 2f);
        int viewWidth = Math.Max(1, (int)(layout.View.Width * scale));
        int viewHeight = Math.Max(1, (int)(layout.View.Height * scale));
        enhanced ??= new EnhancedTargets(this);
        enhanced.Ensure(frame, layout.Scale, viewWidth, viewHeight, options.Msaa, format);
        enhanced.World.Prepare(f, frame, layout, options, enhanced.ViewSamples, viewWidth, viewHeight);

        // the palette, the frame's pixels, its mask and the border's tile
        int pixels = frame.Width * frame.Height;
        int tile = Math.Max(1, frame.BacktileWidth * frame.BacktileHeight);
        HostBuffer buffer = EnsureStaging(f, PaletteBytes + (2 * pixels) + tile);
        WritePalette(buffer.Mapped, frame.Palette);
        frame.Pixels.AsSpan().CopyTo(new Span<byte>(buffer.Mapped + PaletteBytes, pixels));
        frame.Mask.AsSpan().CopyTo(new Span<byte>(buffer.Mapped + PaletteBytes + pixels, pixels));
        if (frame.Backtile.Length > 0)
        {
            frame.Backtile.AsSpan().CopyTo(new Span<byte>(buffer.Mapped + PaletteBytes + (2 * pixels), tile));
        }

        buffer.Flush();
        enhanced.UpdateSets(f, buffer);
        return (layout, buffer);
    }

    /// <summary>
    /// An enhanced frame drawn offscreen at <paramref name="width"/> x <paramref name="height"/> and read back, row
    /// after row of RGBA (the tools' comparison with the software frame; a headless presenter has no window).
    /// </summary>
    public byte[] RenderEnhanced(EnhancedFrame frame, EnhancedOptions options, int width, int height)
    {
        lock (presenting)
        {
            int f = Frame0;
            VkDeviceApi api = context.Api;
            VkFence fence = inFlight[f];
            api.vkWaitForFences(1, &fence, true, ulong.MaxValue);
            (EnhancedLayout layout, HostBuffer buffer) = PrepareEnhanced(f, frame, options, width, height, VkFormat.R8G8B8A8Unorm);

            using var target = new Image(context, (uint)width, (uint)height, VkFormat.R8G8B8A8Unorm,
                VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc);
            using var readback = new HostBuffer(context, (ulong)(width * height * 4), VkBufferUsageFlags.TransferDst);
            VkCommandBuffer cmd = StartCommands(f);
            enhanced!.Record(cmd, f, buffer, frame, layout, target.Handle, target.View, new VkRect2D(0, 0, (uint)width, (uint)height),
                options.Lighting != 0, VkImageLayout.TransferSrcOptimal);
            VkBufferImageCopy region = new()
            {
                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                imageExtent = new VkExtent3D(width, height, 1),
            };
            api.vkCmdCopyImageToBuffer(cmd, target.Handle, VkImageLayout.TransferSrcOptimal, readback.Buffer, 1, &region);
            api.vkEndCommandBuffer(cmd);
            VkCommandBufferSubmitInfo cmdInfo = new() { commandBuffer = cmd };
            VkSubmitInfo2 submit = new() { commandBufferInfoCount = 1, pCommandBufferInfos = &cmdInfo };
            Check(api.vkQueueSubmit2(context.Queue, 1, &submit, fence), "vkQueueSubmit2");
            api.vkWaitForFences(1, &fence, true, ulong.MaxValue);
            readback.Invalidate();
            return new Span<byte>(readback.Mapped, width * height * 4).ToArray();
        }
    }

    private void DisposeEnhanced() => enhanced?.Dispose();

    /// <summary>The enhanced frame's images, pipelines and descriptors, made again when their sizes change.</summary>
    private sealed class EnhancedTargets : IDisposable
    {
        private readonly VulkanPresenter presenter;
        private readonly VulkanContext context;
        private readonly VkDescriptorSetLayout overlaySetLayout;
        private readonly VkDescriptorSetLayout compositeSetLayout;
        private readonly VkPipelineLayout overlayPipelineLayout;
        private readonly VkPipelineLayout compositePipelineLayout;
        private readonly VkDescriptorPool pool;
        private readonly VkDescriptorSet[] overlaySets = new VkDescriptorSet[FramesInFlight];
        private readonly VkDescriptorSet[] compositeSets = new VkDescriptorSet[FramesInFlight];
        private readonly VkPipeline overlayPipeline;
        private readonly VkSampleCountFlags supportedSamples;
        private VkPipeline compositePipeline;
        private VkFormat compositeFormat;

        private Image? frameIndices, frameMask, backtile;
        private Image? overlay, weight;
        private Image? viewMultisampled, viewDepth, view;
        private int frameWidth, frameHeight, overlayScale, tileWidth, tileHeight;
        private int viewWidth, viewHeight;
        private VkSampleCountFlags viewSamples;

        public EnhancedTargets(VulkanPresenter presenter)
        {
            this.presenter = presenter;
            context = presenter.context;
            VkDeviceApi api = context.Api;

            overlaySetLayout = presenter.CreateSetLayout(
                (VkDescriptorType.SampledImage, 0),
                (VkDescriptorType.SampledImage, 1),
                (VkDescriptorType.StorageBuffer, 2));
            compositeSetLayout = presenter.CreateSetLayout(
                (VkDescriptorType.CombinedImageSampler, 0),
                (VkDescriptorType.CombinedImageSampler, 1),
                (VkDescriptorType.CombinedImageSampler, 2),
                (VkDescriptorType.SampledImage, 3),
                (VkDescriptorType.StorageBuffer, 4));
            overlayPipelineLayout = presenter.CreatePipelineLayout(overlaySetLayout, sizeof(OverlayParams));
            compositePipelineLayout = presenter.CreatePipelineLayout(compositeSetLayout, sizeof(CompositeParams));

            VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[3];
            sizes[0] = new() { type = VkDescriptorType.SampledImage, descriptorCount = 3 * FramesInFlight };
            sizes[1] = new() { type = VkDescriptorType.StorageBuffer, descriptorCount = 2 * FramesInFlight };
            sizes[2] = new() { type = VkDescriptorType.CombinedImageSampler, descriptorCount = 3 * FramesInFlight };
            VkDescriptorPoolCreateInfo poolInfo = new() { maxSets = 2 * FramesInFlight, poolSizeCount = 3, pPoolSizes = sizes };
            Check(api.vkCreateDescriptorPool(&poolInfo, null, out pool), "vkCreateDescriptorPool");
            for (int i = 0; i < FramesInFlight; i++)
            {
                overlaySets[i] = Allocate(overlaySetLayout);
                compositeSets[i] = Allocate(compositeSetLayout);
            }

            overlayPipeline = presenter.CreatePipeline("enhanced_overlay", overlayPipelineLayout, [OverlayFormat, WeightFormat]);

            VkPhysicalDeviceProperties properties = context.InstanceApi.vkGetPhysicalDeviceProperties(context.PhysicalDevice);
            supportedSamples = properties.limits.framebufferColorSampleCounts & properties.limits.framebufferDepthSampleCounts;
            World = new WorldRenderer(presenter);
        }

        /// <summary>The brush models and the frame's words.</summary>
        public WorldRenderer World { get; }

        /// <summary>The view's samples per pixel.</summary>
        public VkSampleCountFlags ViewSamples => viewSamples;

        // The images for a frame of this size, its 2D at this scale, a view of this size with these samples
        public void Ensure(EnhancedFrame frame, int scale, int width, int height, int msaa, VkFormat targetFormat)
        {
            VkSampleCountFlags samples = Samples(msaa);
            bool frameChanged = frameIndices == null || frame.Width != frameWidth || frame.Height != frameHeight;
            bool overlayChanged = frameChanged || scale != overlayScale;
            int tw = Math.Max(1, frame.BacktileWidth), th = Math.Max(1, frame.BacktileHeight);
            bool tileChanged = backtile == null || tw != tileWidth || th != tileHeight;
            bool viewChanged = view == null || width != viewWidth || height != viewHeight || samples != viewSamples;
            if (!(frameChanged || overlayChanged || tileChanged || viewChanged || targetFormat != compositeFormat))
            {
                return;
            }

            context.WaitIdle();
            if (frameChanged)
            {
                frameIndices?.Dispose();
                frameMask?.Dispose();
                frameIndices = new Image(context, (uint)frame.Width, (uint)frame.Height, VkFormat.R8Uint,
                    VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled);
                frameMask = new Image(context, (uint)frame.Width, (uint)frame.Height, VkFormat.R8Uint,
                    VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled);
                frameWidth = frame.Width;
                frameHeight = frame.Height;
            }

            if (overlayChanged)
            {
                overlay?.Dispose();
                weight?.Dispose();
                var size = ((uint)(frame.Width * scale), (uint)(frame.Height * scale));
                overlay = new Image(context, size.Item1, size.Item2, OverlayFormat, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled);
                weight = new Image(context, size.Item1, size.Item2, WeightFormat, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled);
                overlayScale = scale;
            }

            if (tileChanged)
            {
                backtile?.Dispose();
                backtile = new Image(context, (uint)tw, (uint)th, VkFormat.R8Uint, VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled);
                tileWidth = tw;
                tileHeight = th;
            }

            if (viewChanged)
            {
                viewMultisampled?.Dispose();
                viewDepth?.Dispose();
                view?.Dispose();
                viewMultisampled = samples == VkSampleCountFlags.Count1
                    ? null
                    : new Image(context, (uint)width, (uint)height, ViewFormat, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransientAttachment, samples);
                viewDepth = new Image(context, (uint)width, (uint)height, DepthFormat, VkImageUsageFlags.DepthStencilAttachment, samples,
                    VkImageAspectFlags.Depth);
                view = new Image(context, (uint)width, (uint)height, ViewFormat, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled);
                viewWidth = width;
                viewHeight = height;
                viewSamples = samples;
            }

            if (targetFormat != compositeFormat)
            {
                if (compositePipeline != VkPipeline.Null)
                {
                    context.Api.vkDestroyPipeline(compositePipeline);
                }

                compositePipeline = presenter.CreatePipeline("enhanced_composite", compositePipelineLayout, targetFormat);
                compositeFormat = targetFormat;
            }
        }

        public void UpdateSets(int f, HostBuffer buffer)
        {
            VkDescriptorImageInfo indicesInfo = new() { imageView = frameIndices!.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            VkDescriptorImageInfo maskInfo = new() { imageView = frameMask!.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            VkDescriptorBufferInfo paletteInfo = new() { buffer = buffer.Buffer, offset = 0, range = PaletteBytes };
            VkDescriptorImageInfo overlayInfo = new() { sampler = presenter.sampler, imageView = overlay!.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            VkDescriptorImageInfo weightInfo = new() { sampler = presenter.sampler, imageView = weight!.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            VkDescriptorImageInfo viewInfo = new() { sampler = presenter.sampler, imageView = view!.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            VkDescriptorImageInfo tileInfo = new() { imageView = backtile!.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };

            VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[8];
            writes[0] = Write(overlaySets[f], 0, VkDescriptorType.SampledImage, &indicesInfo, null);
            writes[1] = Write(overlaySets[f], 1, VkDescriptorType.SampledImage, &maskInfo, null);
            writes[2] = Write(overlaySets[f], 2, VkDescriptorType.StorageBuffer, null, &paletteInfo);
            writes[3] = Write(compositeSets[f], 0, VkDescriptorType.CombinedImageSampler, &overlayInfo, null);
            writes[4] = Write(compositeSets[f], 1, VkDescriptorType.CombinedImageSampler, &weightInfo, null);
            writes[5] = Write(compositeSets[f], 2, VkDescriptorType.CombinedImageSampler, &viewInfo, null);
            writes[6] = Write(compositeSets[f], 3, VkDescriptorType.SampledImage, &tileInfo, null);
            writes[7] = Write(compositeSets[f], 4, VkDescriptorType.StorageBuffer, null, &paletteInfo);
            context.Api.vkUpdateDescriptorSets(8, writes, 0, null);
        }

        public void Record(VkCommandBuffer cmd, int f, HostBuffer buffer, EnhancedFrame frame, EnhancedLayout layout,
            VkImage target, VkImageView targetView, VkRect2D window, bool stepped, VkImageLayout finalLayout = VkImageLayout.PresentSrcKHR)
        {
            VkDeviceApi api = context.Api;
            int pixels = frame.Width * frame.Height;

            // the frame's pixels, its mask and the border's tile into their images
            Upload(cmd, buffer, PaletteBytes, frameIndices!, frameWidth, frameHeight);
            Upload(cmd, buffer, (ulong)(PaletteBytes + pixels), frameMask!, frameWidth, frameHeight);
            Upload(cmd, buffer, (ulong)(PaletteBytes + (2 * pixels)), backtile!, tileWidth, tileHeight);

            // the 2D and the view's weight, at the integer scale
            var overlayRect = new VkRect2D(0, 0, (uint)(frameWidth * overlayScale), (uint)(frameHeight * overlayScale));
            ToAttachment(cmd, overlay!.Handle);
            ToAttachment(cmd, weight!.Handle);
            VkRenderingAttachmentInfo* overlayAttachments = stackalloc VkRenderingAttachmentInfo[2];
            overlayAttachments[0] = Attachment(overlay.View, VkAttachmentLoadOp.DontCare, default);
            overlayAttachments[1] = Attachment(weight.View, VkAttachmentLoadOp.DontCare, default);
            VkRenderingInfo overlayInfo = new() { renderArea = overlayRect, layerCount = 1, colorAttachmentCount = 2, pColorAttachments = overlayAttachments };
            api.vkCmdBeginRendering(cmd, &overlayInfo);
            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, overlayPipeline);
            VkDescriptorSet overlaySet = overlaySets[f];
            api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, overlayPipelineLayout, 0, 1, &overlaySet, 0, null);
            OverlayParams overlayParams = new() { Scale = overlayScale, ViewDrawn = layout.HasView ? 1 : 0 };
            api.vkCmdPushConstants(cmd, overlayPipelineLayout, VkShaderStageFlags.Fragment, 0, (uint)sizeof(OverlayParams), &overlayParams);
            presenter.SetViewport(cmd, overlayRect);
            api.vkCmdDraw(cmd, 3, 1, 0, 0);
            api.vkCmdEndRendering(cmd);
            ToSampled(cmd, overlay.Handle, VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);
            ToSampled(cmd, weight.Handle, VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);

            // the 3D view: cleared to r_clearcolor (as the software's where nothing is drawn), the brush models
            int c = frame.ClearColor * 3;
            var clear = new VkClearValue(frame.Palette[c] / 255f, frame.Palette[c + 1] / 255f, frame.Palette[c + 2] / 255f, 1f);
            var viewRect = new VkRect2D(0, 0, (uint)viewWidth, (uint)viewHeight);
            ToAttachment(cmd, view!.Handle);
            VkRenderingAttachmentInfo color;
            if (viewMultisampled != null)
            {
                ToAttachment(cmd, viewMultisampled.Handle);
                color = Attachment(viewMultisampled.View, VkAttachmentLoadOp.Clear, clear);
                color.storeOp = VkAttachmentStoreOp.DontCare;
                color.resolveMode = VkResolveModeFlags.Average;
                color.resolveImageView = view.View;
                color.resolveImageLayout = VkImageLayout.ColorAttachmentOptimal;
            }
            else
            {
                color = Attachment(view.View, VkAttachmentLoadOp.Clear, clear);
            }

            presenter.Barrier(cmd, viewDepth!.Handle, VkImageLayout.Undefined, VkImageLayout.DepthAttachmentOptimal,
                VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests, VkAccessFlags2.DepthStencilAttachmentWrite,
                VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests,
                VkAccessFlags2.DepthStencilAttachmentRead | VkAccessFlags2.DepthStencilAttachmentWrite, VkImageAspectFlags.Depth);
            VkRenderingAttachmentInfo depth = new()
            {
                imageView = viewDepth.View,
                imageLayout = VkImageLayout.DepthAttachmentOptimal,
                loadOp = VkAttachmentLoadOp.Clear,
                storeOp = VkAttachmentStoreOp.DontCare,
                clearValue = new VkClearValue(0f, 0), // reversed depth: 0 is as far as can be
            };
            VkRenderingInfo viewInfo = new()
            {
                renderArea = viewRect,
                layerCount = 1,
                colorAttachmentCount = 1,
                pColorAttachments = &color,
                pDepthAttachment = &depth,
            };
            api.vkCmdBeginRendering(cmd, &viewInfo);
            presenter.SetViewport(cmd, viewRect);
            World.Draw(cmd, f, frame, layout);
            api.vkCmdEndRendering(cmd);
            ToSampled(cmd, view.Handle, VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);

            // the two together over the whole window
            presenter.Barrier(cmd, target, VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.None, VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite);
            presenter.BeginRendering(cmd, targetView, window, VkAttachmentLoadOp.DontCare);
            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, compositePipeline);
            VkDescriptorSet compositeSet = compositeSets[f];
            api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, compositePipelineLayout, 0, 1, &compositeSet, 0, null);
            CompositeParams compositeParams = new()
            {
                FrameX = layout.Frame.X,
                FrameY = layout.Frame.Y,
                FrameWidth = layout.Frame.Width,
                FrameHeight = layout.Frame.Height,
                ViewX = layout.View.X,
                ViewY = layout.View.Y,
                ViewWidth = layout.View.Width,
                ViewHeight = layout.View.Height,
                FramePixelsX = frameWidth,
                FramePixelsY = frameHeight,
                TileX = frame.Backtile.Length > 0 ? tileWidth : 0,
                TileY = frame.Backtile.Length > 0 ? tileHeight : 0,
                Widened = layout.Widened ? 1 : 0,
                InLevel = frame.InLevel ? 1 : 0,
                PixelScaleX = (float)layout.Frame.Width / frameWidth,
                PixelScaleY = (float)layout.Frame.Height / frameHeight,
                WarpPhase = stepped ? QuakeSharp.Core.Numerics.X86.FtoI(frame.Time * 20) & 127 : (float)(frame.Time * 20 % 128),
                Warp = frame.Warp && layout.HasView ? (stepped ? 2 : 1) : 0,
            };
            api.vkCmdPushConstants(cmd, compositePipelineLayout, VkShaderStageFlags.Fragment, 0, (uint)sizeof(CompositeParams), &compositeParams);
            presenter.SetViewport(cmd, window);
            api.vkCmdDraw(cmd, 3, 1, 0, 0);
            api.vkCmdEndRendering(cmd);
            presenter.Barrier(cmd, target, VkImageLayout.ColorAttachmentOptimal, finalLayout,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
                finalLayout == VkImageLayout.PresentSrcKHR ? VkPipelineStageFlags2.None : VkPipelineStageFlags2.Copy,
                finalLayout == VkImageLayout.PresentSrcKHR ? VkAccessFlags2.None : VkAccessFlags2.TransferRead);
        }

        public void Dispose()
        {
            VkDeviceApi api = context.Api;
            World.Dispose();
            frameIndices?.Dispose();
            frameMask?.Dispose();
            backtile?.Dispose();
            overlay?.Dispose();
            weight?.Dispose();
            viewMultisampled?.Dispose();
            viewDepth?.Dispose();
            view?.Dispose();
            api.vkDestroyPipeline(overlayPipeline);
            if (compositePipeline != VkPipeline.Null)
            {
                api.vkDestroyPipeline(compositePipeline);
            }

            api.vkDestroyDescriptorPool(pool);
            api.vkDestroyPipelineLayout(overlayPipelineLayout);
            api.vkDestroyPipelineLayout(compositePipelineLayout);
            api.vkDestroyDescriptorSetLayout(overlaySetLayout);
            api.vkDestroyDescriptorSetLayout(compositeSetLayout);
        }

        private static VkWriteDescriptorSet Write(VkDescriptorSet set, uint binding, VkDescriptorType type,
            VkDescriptorImageInfo* image, VkDescriptorBufferInfo* buffer) => new()
        {
            dstSet = set,
            dstBinding = binding,
            descriptorCount = 1,
            descriptorType = type,
            pImageInfo = image,
            pBufferInfo = buffer,
        };

        private static VkRenderingAttachmentInfo Attachment(VkImageView view, VkAttachmentLoadOp load, VkClearValue clear) => new()
        {
            imageView = view,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = load,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = clear,
        };

        // The most samples the device has, up to what is asked
        private VkSampleCountFlags Samples(int msaa)
        {
            foreach (VkSampleCountFlags s in (ReadOnlySpan<VkSampleCountFlags>)[VkSampleCountFlags.Count8, VkSampleCountFlags.Count4, VkSampleCountFlags.Count2])
            {
                if ((int)s <= msaa && (supportedSamples & s) != 0)
                {
                    return s;
                }
            }

            return VkSampleCountFlags.Count1;
        }

        private VkDescriptorSet Allocate(VkDescriptorSetLayout layout)
        {
            VkDescriptorSetAllocateInfo info = new() { descriptorPool = pool, descriptorSetCount = 1, pSetLayouts = &layout };
            VkDescriptorSet set;
            Check(context.Api.vkAllocateDescriptorSets(&info, &set), "vkAllocateDescriptorSets");
            return set;
        }

        private void Upload(VkCommandBuffer cmd, HostBuffer buffer, ulong offset, Image image, int width, int height)
        {
            presenter.Barrier(cmd, image.Handle, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.None, VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite);
            VkBufferImageCopy region = new()
            {
                bufferOffset = offset,
                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                imageExtent = new VkExtent3D(width, height, 1),
            };
            context.Api.vkCmdCopyBufferToImage(cmd, buffer.Buffer, image.Handle, VkImageLayout.TransferDstOptimal, 1, &region);
            ToSampled(cmd, image.Handle, VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite, VkImageLayout.TransferDstOptimal);
        }

        private void ToAttachment(VkCommandBuffer cmd, VkImage image) =>
            presenter.Barrier(cmd, image, VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.None, VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite);

        private void ToSampled(VkCommandBuffer cmd, VkImage image, VkPipelineStageFlags2 stage, VkAccessFlags2 access, VkImageLayout from) =>
            presenter.Barrier(cmd, image, from, VkImageLayout.ShaderReadOnlyOptimal,
                stage, access, VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead);
    }

    // enhanced_overlay.slang's push constants
    private struct OverlayParams
    {
        public int Scale;
        public int ViewDrawn;
    }

    // enhanced_composite.slang's push constants
    private struct CompositeParams
    {
        public float FrameX, FrameY, FrameWidth, FrameHeight;
        public float ViewX, ViewY, ViewWidth, ViewHeight;
        public int FramePixelsX, FramePixelsY;
        public int TileX, TileY;
        public int Widened;
        public int InLevel;
        public float PixelScaleX, PixelScaleY;
        public float WarpPhase;
        public int Warp;
    }
}
