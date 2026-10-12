using Vortice.Vulkan;
using static QuakeSharp.Render.Vulkan.VulkanContext;
using static Vortice.Vulkan.Vma;

namespace QuakeSharp.Render.Vulkan;

/// <summary>A persistently mapped buffer the CPU writes and the GPU reads (staging, per-frame data).</summary>
internal sealed unsafe class HostBuffer : IDisposable
{
    private readonly VulkanContext context;
    private readonly VmaAllocation allocation;

    public HostBuffer(VulkanContext context, ulong size, VkBufferUsageFlags usage)
    {
        this.context = context;
        Size = size;

        VkBufferCreateInfo bufferInfo = new() { size = size, usage = usage, sharingMode = VkSharingMode.Exclusive };
        VmaAllocationCreateInfo allocationInfo = new()
        {
            usage = VmaMemoryUsage.Auto,
            flags = VmaAllocationCreateFlags.HostAccessSequentialWrite | VmaAllocationCreateFlags.Mapped,
        };
        VmaAllocationInfo info;
        VkBuffer buffer;
        VmaAllocation created;
        Check(vmaCreateBuffer(context.Allocator, &bufferInfo, &allocationInfo, &buffer, &created, &info), "vmaCreateBuffer");
        Buffer = buffer;
        allocation = created;
        Mapped = (byte*)info.pMappedData;
    }

    public VkBuffer Buffer { get; }

    public ulong Size { get; }

    public byte* Mapped { get; }

    /// <summary>Makes the CPU's writes visible to the GPU (nothing to do on host-coherent memory).</summary>
    public void Flush() => vmaFlushAllocation(context.Allocator, allocation, 0, Vortice.Vulkan.Vulkan.VK_WHOLE_SIZE);

    public void Dispose() => vmaDestroyBuffer(context.Allocator, Buffer, allocation);
}
