using Vortice.Vulkan;
using static QuakeSharp.Render.Vulkan.VulkanContext;

namespace QuakeSharp.Render.Vulkan;

/// <summary>The window's swapchain (after Contrail's).</summary>
internal sealed unsafe class Swapchain : IDisposable
{
    private readonly VulkanContext context;

    /// <param name="vsync">null: mailbox where there is one, else FIFO (the faithful mode's: the game never waits for
    /// the display); true: FIFO (the enhanced mode's vsync); false: immediate, else mailbox, else FIFO.</param>
    public Swapchain(VulkanContext context, uint width, uint height, VkSwapchainKHR oldSwapchain, bool? vsync = null)
    {
        this.context = context;
        Vsync = vsync;

        VkSurfaceCapabilitiesKHR capabilities;
        Check(context.InstanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(context.PhysicalDevice, context.Surface, &capabilities),
            "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        Format = ChooseFormat(out VkColorSpaceKHR colorSpace);
        VkPresentModeKHR presentMode = ChoosePresentMode(vsync);
        Extent = capabilities.currentExtent.width != uint.MaxValue
            ? capabilities.currentExtent
            : new VkExtent2D(
                Math.Clamp(width, capabilities.minImageExtent.width, capabilities.maxImageExtent.width),
                Math.Clamp(height, capabilities.minImageExtent.height, capabilities.maxImageExtent.height));

        uint imageCount = capabilities.minImageCount + 1;
        if (capabilities.maxImageCount > 0)
        {
            imageCount = Math.Min(imageCount, capabilities.maxImageCount);
        }

        VkSwapchainCreateInfoKHR info = new()
        {
            surface = context.Surface,
            minImageCount = imageCount,
            imageFormat = Format,
            imageColorSpace = colorSpace,
            imageExtent = Extent,
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = presentMode,
            clipped = true,
            oldSwapchain = oldSwapchain,
        };
        Check(context.Api.vkCreateSwapchainKHR(&info, out Handle), "vkCreateSwapchainKHR");

        context.Api.vkGetSwapchainImagesKHR(Handle, out uint count);
        Images = new VkImage[count];
        context.Api.vkGetSwapchainImagesKHR(Handle, Images);

        Views = new VkImageView[count];
        for (int i = 0; i < count; i++)
        {
            VkImageViewCreateInfo viewInfo = new()
            {
                image = Images[i],
                viewType = VkImageViewType.Image2D,
                format = Format,
                subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
            };
            Check(context.Api.vkCreateImageView(&viewInfo, out Views[i]), "vkCreateImageView");
        }
    }

    public readonly VkSwapchainKHR Handle;

    public VkFormat Format { get; }

    /// <summary>What the swapchain was made for (see the constructor).</summary>
    public bool? Vsync { get; }

    public VkExtent2D Extent { get; }

    public VkImage[] Images { get; }

    public VkImageView[] Views { get; }

    public void Dispose()
    {
        foreach (VkImageView view in Views)
        {
            context.Api.vkDestroyImageView(view);
        }

        context.Api.vkDestroySwapchainKHR(Handle);
    }

    // A UNORM format: the palette's colours are already what the screen shows, so they pass through unchanged
    private VkFormat ChooseFormat(out VkColorSpaceKHR colorSpace)
    {
        context.InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(context.PhysicalDevice, context.Surface, out uint count);
        Span<VkSurfaceFormatKHR> formats = new VkSurfaceFormatKHR[count];
        context.InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(context.PhysicalDevice, context.Surface, formats);

        foreach (VkSurfaceFormatKHR f in formats)
        {
            if (f.format is VkFormat.B8G8R8A8Unorm or VkFormat.R8G8B8A8Unorm && f.colorSpace == VkColorSpaceKHR.SrgbNonLinear)
            {
                colorSpace = f.colorSpace;
                return f.format;
            }
        }

        colorSpace = formats[0].colorSpace;
        return formats[0].format;
    }

    // Mailbox (no tearing, no waiting for the display) where there is one, else FIFO (vsync); or as asked
    private VkPresentModeKHR ChoosePresentMode(bool? vsync)
    {
        context.InstanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(context.PhysicalDevice, context.Surface, out uint count);
        Span<VkPresentModeKHR> modes = new VkPresentModeKHR[count];
        context.InstanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(context.PhysicalDevice, context.Surface, modes);
        if (vsync == true)
        {
            return VkPresentModeKHR.Fifo;
        }

        if (vsync == false && modes.Contains(VkPresentModeKHR.Immediate))
        {
            return VkPresentModeKHR.Immediate;
        }

        return modes.Contains(VkPresentModeKHR.Mailbox) ? VkPresentModeKHR.Mailbox : VkPresentModeKHR.Fifo;
    }
}
