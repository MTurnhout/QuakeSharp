using System.Runtime.InteropServices;
using QuakeSharp.Core.Platform;
using Vortice.Vulkan;
using static QuakeSharp.Render.Vulkan.VulkanContext;
using static Vortice.Vulkan.Vma;

namespace QuakeSharp.Render.Vulkan;

/// <summary>
/// Shows the engine's 8-bit frames in the window (design.md §6.4). Each frame and its palette are copied to the GPU;
/// the first pass looks the pixels up in the palette and repeats each one k x k times (k the smallest whole number that
/// covers the frame's rectangle of the window), the second filters that linearly into the rectangle: the frame's
/// display aspect (4:3, as on the monitors Quake was made for; or square pixels), letterboxed in black. So pixels stay
/// sharp and rows even, which one nearest-neighbour pass to a size that is not a multiple cannot give.
/// </summary>
public sealed unsafe partial class VulkanPresenter : IVideoOutput, IDisposable
{
    private const int FramesInFlight = 2;
    private const int PaletteBytes = 256 * 4;
    private const VkFormat UpscaledFormat = VkFormat.R8G8B8A8Unorm;

    private static readonly byte* EntryVertex = EntryName("vertexMain");
    private static readonly byte* EntryFragment = EntryName("fragmentMain");

    private readonly VulkanContext context;
    private readonly VulkanSurfaceSource? surfaceSource;
    private readonly VkCommandPool commandPool;
    private readonly VkCommandBuffer[] commandBuffers = new VkCommandBuffer[FramesInFlight];
    private readonly VkFence[] inFlight = new VkFence[FramesInFlight];
    private readonly VkSemaphore[] imageAcquired = new VkSemaphore[FramesInFlight];
    private readonly HostBuffer?[] staging = new HostBuffer?[FramesInFlight];
    private readonly VkDescriptorSetLayout paletteSetLayout;
    private readonly VkDescriptorSetLayout scaleSetLayout;
    private readonly VkPipelineLayout palettePipelineLayout;
    private readonly VkPipelineLayout scalePipelineLayout;
    private readonly VkDescriptorPool descriptorPool;
    private readonly VkDescriptorSet[] paletteSets = new VkDescriptorSet[FramesInFlight];
    private readonly VkDescriptorSet scaleSet;
    private readonly VkSampler sampler;
    private readonly VkPipeline palettePipeline;

    private readonly Lock presenting = new();

    private Swapchain? swapchain;
    private VkSemaphore[] renderFinished = [];
    private VkPipeline scalePipeline;
    private VkFormat scalePipelineFormat;
    private int frame;

    // The frame's image (palette indices) and the integer-scaled one, at their current sizes
    private Image? frameImage;
    private Image? upscaledImage;
    private int frameWidth, frameHeight, scale;

    /// <param name="surfaceSource">The window (null: headless, drawing only offscreen).</param>
    /// <param name="enableValidation">Turn on the Vulkan validation layer, where it is installed.</param>
    /// <param name="log">Where messages go.</param>
    public VulkanPresenter(VulkanSurfaceSource? surfaceSource, bool enableValidation, Action<string> log)
    {
        this.surfaceSource = surfaceSource;
        context = new VulkanContext(surfaceSource, enableValidation, log);
        VkDeviceApi api = context.Api;

        VkCommandPoolCreateInfo poolInfo = new() { flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = context.QueueFamily };
        Check(api.vkCreateCommandPool(&poolInfo, out commandPool), "vkCreateCommandPool");
        for (int i = 0; i < FramesInFlight; i++)
        {
            VkCommandBufferAllocateInfo allocateInfo = new() { commandPool = commandPool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
            VkCommandBuffer cmd;
            Check(api.vkAllocateCommandBuffers(&allocateInfo, &cmd), "vkAllocateCommandBuffers");
            commandBuffers[i] = cmd;
            VkFenceCreateInfo fenceInfo = new() { flags = VkFenceCreateFlags.Signaled };
            Check(api.vkCreateFence(&fenceInfo, null, out inFlight[i]), "vkCreateFence");
            imageAcquired[i] = CreateSemaphore();
        }

        // the palette pass: the frame's indices and the palette; the scale pass: the upscaled frame
        paletteSetLayout = CreateSetLayout(
            (VkDescriptorType.SampledImage, 0),
            (VkDescriptorType.StorageBuffer, 1));
        scaleSetLayout = CreateSetLayout((VkDescriptorType.CombinedImageSampler, 0));
        palettePipelineLayout = CreatePipelineLayout(paletteSetLayout, sizeof(int));
        scalePipelineLayout = CreatePipelineLayout(scaleSetLayout, 0);

        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[3];
        sizes[0] = new() { type = VkDescriptorType.SampledImage, descriptorCount = FramesInFlight };
        sizes[1] = new() { type = VkDescriptorType.StorageBuffer, descriptorCount = FramesInFlight };
        sizes[2] = new() { type = VkDescriptorType.CombinedImageSampler, descriptorCount = 1 };
        VkDescriptorPoolCreateInfo descriptorPoolInfo = new() { maxSets = FramesInFlight + 1, poolSizeCount = 3, pPoolSizes = sizes };
        Check(api.vkCreateDescriptorPool(&descriptorPoolInfo, null, out descriptorPool), "vkCreateDescriptorPool");
        for (int i = 0; i < FramesInFlight; i++)
        {
            paletteSets[i] = AllocateSet(paletteSetLayout);
        }

        scaleSet = AllocateSet(scaleSetLayout);

        VkSamplerCreateInfo samplerInfo = new()
        {
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            mipmapMode = VkSamplerMipmapMode.Nearest,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
        };
        Check(api.vkCreateSampler(&samplerInfo, null, out sampler), "vkCreateSampler");

        palettePipeline = CreatePipeline("palette", palettePipelineLayout, UpscaledFormat);
    }

    // the frame offscreen drawing uses (the first; a headless presenter presents nothing)
    private const int Frame0 = 0;

    /// <summary>A presenter without a window: it only draws offscreen (<see cref="RenderEnhanced"/>).</summary>
    public static VulkanPresenter Headless(bool enableValidation, Action<string> log) => new(null, enableValidation, log);

    /// <summary>Show square pixels (the frame's own shape) instead of a 4:3 picture.</summary>
    public bool SquarePixels { get; set; }

    public string DeviceName => context.DeviceName;

    /// <summary>
    /// Shows a frame. The game thread (the faithful mode) and the enhanced renderer's thread both call it, one at a
    /// time.
    /// </summary>
    public void Present(ReadOnlySpan<byte> pixels, int width, int height, int rowBytes, ReadOnlySpan<byte> palette)
    {
        lock (presenting)
        {
            PresentLocked(pixels, width, height, rowBytes, palette);
        }
    }

    private void PresentLocked(ReadOnlySpan<byte> pixels, int width, int height, int rowBytes, ReadOnlySpan<byte> palette)
    {
        if (!BeginFrame(out int f, out uint imageIndex))
        {
            return;
        }

        // The frame's rectangle of the window and the integer scale that covers it
        (int rectX, int rectY, int rectWidth, int rectHeight) =
            PresentationLayout.FrameRect(width, height, (int)swapchain!.Extent.width, (int)swapchain.Extent.height, SquarePixels);
        var rect = new VkRect2D(rectX, rectY, (uint)rectWidth, (uint)rectHeight);
        int k = PresentationLayout.IntegerScale(width, height, rectWidth, rectHeight);
        EnsureImages(width, height, k);

        // The palette (as 0x00BBGGRR) and the pixels, row after row
        HostBuffer buffer = EnsureStaging(f, PaletteBytes + (width * height));
        WritePalette(buffer.Mapped, palette);
        var pixelsOut = new Span<byte>(buffer.Mapped + PaletteBytes, width * height);
        for (int y = 0; y < height; y++)
        {
            pixels.Slice(y * rowBytes, width).CopyTo(pixelsOut.Slice(y * width, width));
        }

        buffer.Flush();
        UpdatePaletteSet(f, buffer);

        VkCommandBuffer cmd = StartCommands(f);
        Record(cmd, buffer, swapchain.Images[imageIndex], swapchain.Views[imageIndex], rect, k);
        EndFrame(f, imageIndex, cmd);
    }

    // A frame begun: the swapchain fits the window, frame f's fence has passed (its staging buffer is free), an image
    // is acquired. False when there is nothing to draw on (minimised, or the swapchain was out of date).
    private bool BeginFrame(out int f, out uint imageIndex)
    {
        f = frame;
        imageIndex = 0;
        if (surfaceSource == null)
        {
            return false; // headless
        }

        (int windowWidth, int windowHeight) = surfaceSource.PixelSize();
        if (windowWidth <= 0 || windowHeight <= 0)
        {
            return false; // minimised
        }

        if (swapchain == null || swapchain.Extent.width != windowWidth || swapchain.Extent.height != windowHeight)
        {
            RecreateSwapchain(windowWidth, windowHeight);
        }

        VkDeviceApi api = context.Api;
        VkFence fence = inFlight[f];
        api.vkWaitForFences(1, &fence, true, ulong.MaxValue);

        uint index;
        VkResult acquired = api.vkAcquireNextImageKHR(swapchain!.Handle, ulong.MaxValue, imageAcquired[f], VkFence.Null, &index);
        if (acquired == VkResult.ErrorOutOfDateKHR)
        {
            RecreateSwapchain(windowWidth, windowHeight);
            return false;
        }

        if (acquired != VkResult.SuboptimalKHR)
        {
            Check(acquired, "vkAcquireNextImageKHR");
        }

        imageIndex = index;
        return true;
    }

    // Frame f's command buffer, reset and begun (its fence reset: it signals again when the frame is done)
    private VkCommandBuffer StartCommands(int f)
    {
        VkDeviceApi api = context.Api;
        VkFence fence = inFlight[f];
        api.vkResetFences(1, &fence);
        VkCommandBuffer cmd = commandBuffers[f];
        api.vkResetCommandBuffer(cmd, 0);
        VkCommandBufferBeginInfo beginInfo = new() { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        api.vkBeginCommandBuffer(cmd, &beginInfo);
        return cmd;
    }

    // The frame's commands submitted and its image presented
    private void EndFrame(int f, uint imageIndex, VkCommandBuffer cmd)
    {
        VkDeviceApi api = context.Api;
        api.vkEndCommandBuffer(cmd);

        VkSemaphore acquiredSemaphore = imageAcquired[f];
        VkSemaphore finishedSemaphore = renderFinished[imageIndex];
        VkCommandBufferSubmitInfo cmdInfo = new() { commandBuffer = cmd };
        VkSemaphoreSubmitInfo waitInfo = new() { semaphore = acquiredSemaphore, stageMask = VkPipelineStageFlags2.ColorAttachmentOutput };
        VkSemaphoreSubmitInfo signalInfo = new() { semaphore = finishedSemaphore, stageMask = VkPipelineStageFlags2.AllGraphics };
        VkSubmitInfo2 submit = new()
        {
            waitSemaphoreInfoCount = 1,
            pWaitSemaphoreInfos = &waitInfo,
            commandBufferInfoCount = 1,
            pCommandBufferInfos = &cmdInfo,
            signalSemaphoreInfoCount = 1,
            pSignalSemaphoreInfos = &signalInfo,
        };
        Check(api.vkQueueSubmit2(context.Queue, 1, &submit, inFlight[f]), "vkQueueSubmit2");

        VkSwapchainKHR handle = swapchain!.Handle;
        VkPresentInfoKHR present = new()
        {
            waitSemaphoreCount = 1,
            pWaitSemaphores = &finishedSemaphore,
            swapchainCount = 1,
            pSwapchains = &handle,
            pImageIndices = &imageIndex,
        };
        VkResult presented = api.vkQueuePresentKHR(context.Queue, &present);
        if (presented is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
        {
            RecreateSwapchain((int)swapchain.Extent.width, (int)swapchain.Extent.height);
        }
        else
        {
            Check(presented, "vkQueuePresentKHR");
        }

        frame = (frame + 1) % FramesInFlight;
    }

    // The palette as 0x00BBGGRR words
    private static void WritePalette(byte* destination, ReadOnlySpan<byte> palette)
    {
        uint* paletteOut = (uint*)destination;
        for (int i = 0; i < 256; i++)
        {
            paletteOut[i] = palette[i * 3] | ((uint)palette[(i * 3) + 1] << 8) | ((uint)palette[(i * 3) + 2] << 16);
        }
    }

    public void Dispose()
    {
        context.WaitIdle();
        VkDeviceApi api = context.Api;
        DisposeEnhanced();
        frameImage?.Dispose();
        upscaledImage?.Dispose();
        foreach (HostBuffer? b in staging)
        {
            b?.Dispose();
        }

        DestroySwapchain();
        api.vkDestroyPipeline(palettePipeline);
        if (scalePipeline != VkPipeline.Null)
        {
            api.vkDestroyPipeline(scalePipeline);
        }

        api.vkDestroySampler(sampler);
        api.vkDestroyDescriptorPool(descriptorPool);
        api.vkDestroyPipelineLayout(palettePipelineLayout);
        api.vkDestroyPipelineLayout(scalePipelineLayout);
        api.vkDestroyDescriptorSetLayout(paletteSetLayout);
        api.vkDestroyDescriptorSetLayout(scaleSetLayout);
        for (int i = 0; i < FramesInFlight; i++)
        {
            api.vkDestroyFence(inFlight[i]);
            api.vkDestroySemaphore(imageAcquired[i]);
        }

        api.vkDestroyCommandPool(commandPool);
        context.Dispose();
    }

    private static byte* EntryName(string name)
    {
        byte* p = (byte*)NativeMemory.Alloc((nuint)name.Length + 1);
        for (int i = 0; i < name.Length; i++)
        {
            p[i] = (byte)name[i];
        }

        p[name.Length] = 0;
        return p;
    }

    private static byte[] LoadShader(string name)
    {
        using Stream stream = typeof(VulkanPresenter).Assembly.GetManifestResourceStream($"shaders.{name}.spv")
            ?? throw new InvalidOperationException($"the shader {name} is not embedded");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private void Record(VkCommandBuffer cmd, HostBuffer buffer, VkImage target, VkImageView targetView, VkRect2D rect, int k)
    {
        VkDeviceApi api = context.Api;

        // the pixels into the frame's image
        Barrier(cmd, frameImage!.Handle, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.None, VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite);
        VkBufferImageCopy region = new()
        {
            bufferOffset = PaletteBytes,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageExtent = new VkExtent3D(frameWidth, frameHeight, 1),
        };
        api.vkCmdCopyBufferToImage(cmd, buffer.Buffer, frameImage.Handle, VkImageLayout.TransferDstOptimal, 1, &region);
        Barrier(cmd, frameImage.Handle, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite, VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead);

        // the palette pass, into the upscaled image
        Barrier(cmd, upscaledImage!.Handle, VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.None, VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite);
        var upscaledRect = new VkRect2D(0, 0, (uint)(frameWidth * k), (uint)(frameHeight * k));
        BeginRendering(cmd, upscaledImage.View, upscaledRect, VkAttachmentLoadOp.DontCare);
        api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, palettePipeline);
        VkDescriptorSet paletteSet = paletteSets[frame];
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, palettePipelineLayout, 0, 1, &paletteSet, 0, null);
        api.vkCmdPushConstants(cmd, palettePipelineLayout, VkShaderStageFlags.Fragment, 0, sizeof(int), &k);
        SetViewport(cmd, upscaledRect);
        api.vkCmdDraw(cmd, 3, 1, 0, 0);
        api.vkCmdEndRendering(cmd);
        Barrier(cmd, upscaledImage.Handle, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead);

        // the scale pass, into the window, black around
        Barrier(cmd, target, VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.None, VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite);
        BeginRendering(cmd, targetView, new VkRect2D(0, 0, swapchain!.Extent.width, swapchain.Extent.height), VkAttachmentLoadOp.Clear);
        api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, scalePipeline);
        VkDescriptorSet set = scaleSet;
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, scalePipelineLayout, 0, 1, &set, 0, null);
        SetViewport(cmd, rect);
        api.vkCmdDraw(cmd, 3, 1, 0, 0);
        api.vkCmdEndRendering(cmd);
        Barrier(cmd, target, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.PresentSrcKHR,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkPipelineStageFlags2.None, VkAccessFlags2.None);
    }

    private void BeginRendering(VkCommandBuffer cmd, VkImageView view, VkRect2D area, VkAttachmentLoadOp load)
    {
        VkRenderingAttachmentInfo attachment = new()
        {
            imageView = view,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = load,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue(0f, 0f, 0f, 1f),
        };
        VkRenderingInfo info = new()
        {
            renderArea = area,
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &attachment,
        };
        context.Api.vkCmdBeginRendering(cmd, &info);
    }

    private void SetViewport(VkCommandBuffer cmd, VkRect2D rect)
    {
        VkViewport viewport = new(rect.offset.x, rect.offset.y, rect.extent.width, rect.extent.height, 0f, 1f);
        context.Api.vkCmdSetViewport(cmd, 0, 1, &viewport);
        context.Api.vkCmdSetScissor(cmd, 0, 1, &rect);
    }

    private void Barrier(VkCommandBuffer cmd, VkImage image, VkImageLayout from, VkImageLayout to,
        VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess, VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess,
        VkImageAspectFlags aspect = VkImageAspectFlags.Color, uint levels = 1)
    {
        VkImageMemoryBarrier2 barrier = new()
        {
            srcStageMask = srcStage,
            srcAccessMask = srcAccess,
            dstStageMask = dstStage,
            dstAccessMask = dstAccess,
            oldLayout = from,
            newLayout = to,
            srcQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = new VkImageSubresourceRange(aspect, 0, levels, 0, 1),
        };
        VkDependencyInfo dependency = new() { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &barrier };
        context.Api.vkCmdPipelineBarrier2(cmd, &dependency);
    }

    // The frame's image and the upscaled one, made again when the frame's size or the scale changes
    private void EnsureImages(int width, int height, int k)
    {
        if (frameImage != null && width == frameWidth && height == frameHeight && k == scale)
        {
            return;
        }

        context.WaitIdle();
        if (width != frameWidth || height != frameHeight || frameImage == null)
        {
            frameImage?.Dispose();
            frameImage = new Image(context, (uint)width, (uint)height, VkFormat.R8Uint,
                VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled);
        }

        upscaledImage?.Dispose();
        upscaledImage = new Image(context, (uint)(width * k), (uint)(height * k), UpscaledFormat,
            VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled);
        frameWidth = width;
        frameHeight = height;
        scale = k;

        VkDescriptorImageInfo upscaledInfo = new() { sampler = sampler, imageView = upscaledImage.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        VkWriteDescriptorSet write = new()
        {
            dstSet = scaleSet,
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            pImageInfo = &upscaledInfo,
        };
        context.Api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private HostBuffer EnsureStaging(int f, int size)
    {
        if (staging[f] is HostBuffer existing && existing.Size >= (ulong)size)
        {
            return existing;
        }

        context.WaitIdle();
        staging[f]?.Dispose();
        staging[f] = new HostBuffer(context, (ulong)size, VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.StorageBuffer);
        return staging[f]!;
    }

    private void UpdatePaletteSet(int f, HostBuffer buffer)
    {
        VkDescriptorImageInfo frameInfo = new() { imageView = frameImage!.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        VkDescriptorBufferInfo paletteInfo = new() { buffer = buffer.Buffer, offset = 0, range = PaletteBytes };
        VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[2];
        writes[0] = new()
        {
            dstSet = paletteSets[f],
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.SampledImage,
            pImageInfo = &frameInfo,
        };
        writes[1] = new()
        {
            dstSet = paletteSets[f],
            dstBinding = 1,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageBuffer,
            pBufferInfo = &paletteInfo,
        };
        context.Api.vkUpdateDescriptorSets(2, writes, 0, null);
    }

    private void RecreateSwapchain(int width, int height)
    {
        context.WaitIdle();
        Swapchain? old = swapchain;
        swapchain = new Swapchain(context, (uint)width, (uint)height, old?.Handle ?? VkSwapchainKHR.Null);
        if (old != null)
        {
            old.Dispose();
        }

        foreach (VkSemaphore s in renderFinished)
        {
            context.Api.vkDestroySemaphore(s);
        }

        renderFinished = new VkSemaphore[swapchain.Images.Length];
        for (int i = 0; i < renderFinished.Length; i++)
        {
            renderFinished[i] = CreateSemaphore();
        }

        if (scalePipelineFormat != swapchain.Format)
        {
            if (scalePipeline != VkPipeline.Null)
            {
                context.Api.vkDestroyPipeline(scalePipeline);
            }

            scalePipeline = CreatePipeline("scale", scalePipelineLayout, swapchain.Format);
            scalePipelineFormat = swapchain.Format;
        }
    }

    private void DestroySwapchain()
    {
        foreach (VkSemaphore s in renderFinished)
        {
            context.Api.vkDestroySemaphore(s);
        }

        renderFinished = [];
        swapchain?.Dispose();
        swapchain = null;
    }

    private VkSemaphore CreateSemaphore()
    {
        VkSemaphoreCreateInfo info = new();
        Check(context.Api.vkCreateSemaphore(&info, null, out VkSemaphore semaphore), "vkCreateSemaphore");
        return semaphore;
    }

    private VkDescriptorSetLayout CreateSetLayout(params (VkDescriptorType Type, uint Binding)[] bindings) =>
        CreateSetLayout(VkShaderStageFlags.Fragment, bindings);

    private VkDescriptorSetLayout CreateSetLayout(VkShaderStageFlags stages, params (VkDescriptorType Type, uint Binding)[] bindings)
    {
        VkDescriptorSetLayoutBinding* b = stackalloc VkDescriptorSetLayoutBinding[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            b[i] = new()
            {
                binding = bindings[i].Binding,
                descriptorType = bindings[i].Type,
                descriptorCount = 1,
                stageFlags = stages,
            };
        }

        VkDescriptorSetLayoutCreateInfo info = new() { bindingCount = (uint)bindings.Length, pBindings = b };
        Check(context.Api.vkCreateDescriptorSetLayout(&info, null, out VkDescriptorSetLayout layout), "vkCreateDescriptorSetLayout");
        return layout;
    }

    private VkPipelineLayout CreatePipelineLayout(VkDescriptorSetLayout setLayout, int pushConstantBytes)
    {
        VkPushConstantRange range = new() { stageFlags = VkShaderStageFlags.Fragment, offset = 0, size = (uint)pushConstantBytes };
        VkPipelineLayoutCreateInfo info = new()
        {
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = pushConstantBytes > 0 ? 1u : 0u,
            pPushConstantRanges = &range,
        };
        Check(context.Api.vkCreatePipelineLayout(&info, null, out VkPipelineLayout layout), "vkCreatePipelineLayout");
        return layout;
    }

    private VkDescriptorSet AllocateSet(VkDescriptorSetLayout layout)
    {
        VkDescriptorSetAllocateInfo info = new() { descriptorPool = descriptorPool, descriptorSetCount = 1, pSetLayouts = &layout };
        VkDescriptorSet set;
        Check(context.Api.vkAllocateDescriptorSets(&info, &set), "vkAllocateDescriptorSets");
        return set;
    }

    // A fullscreen-triangle pipeline of a shader (its vertexMain and fragmentMain) drawing into one colour attachment
    private VkPipeline CreatePipeline(string shader, VkPipelineLayout layout, VkFormat colorFormat) =>
        CreatePipeline(shader, layout, [colorFormat]);

    // The same, drawing into several colour attachments (the fragment shader's SV_Target0, 1...)
    private VkPipeline CreatePipeline(string shader, VkPipelineLayout layout, ReadOnlySpan<VkFormat> colorFormats)
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
        VkPipelineVertexInputStateCreateInfo vertexInput = new();
        VkPipelineInputAssemblyStateCreateInfo inputAssembly = new() { topology = VkPrimitiveTopology.TriangleList };
        VkPipelineViewportStateCreateInfo viewport = new() { viewportCount = 1, scissorCount = 1 };
        VkPipelineRasterizationStateCreateInfo raster = new()
        {
            polygonMode = VkPolygonMode.Fill,
            cullMode = VkCullModeFlags.None,
            frontFace = VkFrontFace.CounterClockwise,
            lineWidth = 1f,
        };
        VkPipelineMultisampleStateCreateInfo multisample = new() { rasterizationSamples = VkSampleCountFlags.Count1 };
        VkPipelineColorBlendAttachmentState* blendAttachments = stackalloc VkPipelineColorBlendAttachmentState[colorFormats.Length];
        for (int i = 0; i < colorFormats.Length; i++)
        {
            blendAttachments[i] = new() { colorWriteMask = VkColorComponentFlags.All };
        }

        VkPipelineColorBlendStateCreateInfo blend = new() { attachmentCount = (uint)colorFormats.Length, pAttachments = blendAttachments };
        VkDynamicState* dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        VkPipelineDynamicStateCreateInfo dynamic = new() { dynamicStateCount = 2, pDynamicStates = dynamicStates };
        VkFormat* formats = stackalloc VkFormat[colorFormats.Length];
        colorFormats.CopyTo(new Span<VkFormat>(formats, colorFormats.Length));
        VkPipelineRenderingCreateInfo rendering = new() { colorAttachmentCount = (uint)colorFormats.Length, pColorAttachmentFormats = formats };

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
            pColorBlendState = &blend,
            pDynamicState = &dynamic,
            layout = layout,
        };
        VkPipeline pipeline;
        Check(api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &info, &pipeline), "vkCreateGraphicsPipelines");
        api.vkDestroyShaderModule(module);
        return pipeline;
    }

    /// <summary>A 2D image on the GPU with its view.</summary>
    private sealed class Image : IDisposable
    {
        private readonly VulkanContext context;
        private readonly VmaAllocation allocation;

        public Image(VulkanContext context, uint width, uint height, VkFormat format, VkImageUsageFlags usage,
            VkSampleCountFlags samples = VkSampleCountFlags.Count1, VkImageAspectFlags aspect = VkImageAspectFlags.Color,
            uint mipLevels = 1)
        {
            this.context = context;
            VkImageCreateInfo imageInfo = new()
            {
                imageType = VkImageType.Image2D,
                format = format,
                extent = new VkExtent3D(width, height, 1),
                mipLevels = mipLevels,
                arrayLayers = 1,
                samples = samples,
                tiling = VkImageTiling.Optimal,
                usage = usage,
                sharingMode = VkSharingMode.Exclusive,
                initialLayout = VkImageLayout.Undefined,
            };
            VmaAllocationCreateInfo allocationInfo = new() { usage = VmaMemoryUsage.AutoPreferDevice };
            VkImage image;
            VmaAllocation created;
            Check(vmaCreateImage(context.Allocator, &imageInfo, &allocationInfo, &image, &created, null), "vmaCreateImage");
            Handle = image;
            allocation = created;

            VkImageViewCreateInfo viewInfo = new()
            {
                image = image,
                viewType = VkImageViewType.Image2D,
                format = format,
                subresourceRange = new VkImageSubresourceRange(aspect, 0, mipLevels, 0, 1),
            };
            Check(context.Api.vkCreateImageView(&viewInfo, null, out VkImageView view), "vkCreateImageView");
            View = view;
        }

        public VkImage Handle { get; }

        public VkImageView View { get; }

        public void Dispose()
        {
            context.Api.vkDestroyImageView(View);
            vmaDestroyImage(context.Allocator, Handle, allocation);
        }
    }
}
