using System.Runtime.InteropServices;
using QuakeSharp.Core;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vma;
using static Vortice.Vulkan.Vulkan;

namespace QuakeSharp.Render.Vulkan;

/// <summary>Where the Vulkan surface comes from: the platform's window.</summary>
/// <param name="InstanceExtensions">The instance extensions a surface on the window needs.</param>
/// <param name="CreateSurface">Creates the surface (a VkSurfaceKHR) for a VkInstance.</param>
/// <param name="PixelSize">The window's size in pixels now.</param>
public sealed record VulkanSurfaceSource(
    IReadOnlyList<string> InstanceExtensions,
    Func<nint, ulong> CreateSurface,
    Func<(int Width, int Height)> PixelSize);

/// <summary>The Vulkan 1.3 instance, the window's surface, the device and its queue (after Contrail's).</summary>
internal sealed unsafe class VulkanContext : IDisposable
{
    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    private static readonly string[] RequiredDeviceExtensions = ["VK_KHR_swapchain"];

    private readonly VkDebugUtilsMessengerEXT messenger;
    private readonly Action<string> log;

    public VulkanContext(VulkanSurfaceSource surfaceSource, bool enableValidation, Action<string> log)
    {
        this.log = log;

        // Overlay layers (Steam and the like) inject themselves and alter the calls, which gives false validation
        // errors: keep them out of validated runs.
        if (enableValidation)
        {
            Environment.SetEnvironmentVariable("VK_LOADER_LAYERS_DISABLE", "~implicit~");
        }

        if (vkInitialize() != VkResult.Success)
        {
            throw new QuakeException("No Vulkan driver was found.");
        }

        bool validation = enableValidation && HasInstanceLayer(ValidationLayer);
        if (enableValidation && !validation)
        {
            log("Vulkan validation requested, but the layer is not installed (it comes with the Vulkan SDK).");
        }

        var extensions = new List<string>(surfaceSource.InstanceExtensions);
        if (validation)
        {
            extensions.Add("VK_EXT_debug_utils");
        }

        using var extensionNames = new NativeStringArray(extensions);
        using var layerNames = new NativeStringArray(validation ? [ValidationLayer] : []);
        using var appName = new NativeStringArray(["QuakeSharp"]);

        VkApplicationInfo appInfo = new()
        {
            pApplicationName = appName.Pointer[0],
            applicationVersion = new VkVersion(0, 1, 0),
            pEngineName = appName.Pointer[0],
            engineVersion = new VkVersion(0, 1, 0),
            apiVersion = VK_API_VERSION_1_3,
        };
        VkInstanceCreateInfo instanceInfo = new()
        {
            pApplicationInfo = &appInfo,
            enabledExtensionCount = extensionNames.Count,
            ppEnabledExtensionNames = extensionNames.Pointer,
            enabledLayerCount = layerNames.Count,
            ppEnabledLayerNames = layerNames.Pointer,
        };
        VkResult created = vkCreateInstance(&instanceInfo, out Instance);
        if (created == VkResult.ErrorIncompatibleDriver)
        {
            throw new QuakeException("The graphics driver does not support Vulkan 1.3.");
        }

        Check(created, "vkCreateInstance");
        InstanceApi = GetApi(Instance);

        if (validation)
        {
            VkDebugUtilsMessengerCreateInfoEXT messengerInfo = new()
            {
                messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
                messageType = VkDebugUtilsMessageTypeFlagsEXT.General | VkDebugUtilsMessageTypeFlagsEXT.Validation |
                              VkDebugUtilsMessageTypeFlagsEXT.Performance,
                pfnUserCallback = &DebugCallback,
                pUserData = (void*)GCHandle.ToIntPtr(GCHandle.Alloc(log)),
            };
            Check(InstanceApi.vkCreateDebugUtilsMessengerEXT(&messengerInfo, out messenger), "vkCreateDebugUtilsMessengerEXT");
            log("Vulkan validation enabled.");
        }

        Surface = new VkSurfaceKHR(surfaceSource.CreateSurface(Instance.Handle));

        PickPhysicalDevice();
        CreateDevice();

        VmaAllocatorCreateInfo allocatorInfo = new()
        {
            instance = Instance,
            physicalDevice = PhysicalDevice,
            device = Device,
            vulkanApiVersion = VK_API_VERSION_1_3,
        };
        Check(vmaCreateAllocator(&allocatorInfo, out Allocator), "vmaCreateAllocator");
    }

    public readonly VkInstance Instance;

    public VkDevice Device;

    public VkQueue Queue;

    public VmaAllocator Allocator;

    public VkInstanceApi InstanceApi { get; }

    public VkSurfaceKHR Surface { get; }

    public VkPhysicalDevice PhysicalDevice { get; private set; }

    public VkDeviceApi Api { get; private set; } = null!;

    public uint QueueFamily { get; private set; }

    public string DeviceName { get; private set; } = "";

    public static void Check(VkResult result, string what)
    {
        if (result != VkResult.Success)
        {
            throw new QuakeException($"{what} failed: {result}");
        }
    }

    public void WaitIdle() => Api.vkDeviceWaitIdle();

    public void Dispose()
    {
        vmaDestroyAllocator(Allocator);
        Api.vkDestroyDevice();
        InstanceApi.vkDestroySurfaceKHR(Surface);
        if (messenger != VkDebugUtilsMessengerEXT.Null)
        {
            InstanceApi.vkDestroyDebugUtilsMessengerEXT(messenger);
        }

        InstanceApi.vkDestroyInstance();
    }

    private static bool HasInstanceLayer(string name)
    {
        vkEnumerateInstanceLayerProperties(out uint count);
        Span<VkLayerProperties> layers = new VkLayerProperties[count];
        vkEnumerateInstanceLayerProperties(layers);
        foreach (VkLayerProperties layer in layers)
        {
            VkLayerProperties l = layer;
            if (new string((sbyte*)l.layerName) == name)
            {
                return true;
            }
        }

        return false;
    }

    private static string VersionText(uint version) => $"{version >> 22}.{(version >> 12) & 0x3FF}";

    [UnmanagedCallersOnly]
    private static uint DebugCallback(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT types,
        VkDebugUtilsMessengerCallbackDataEXT* data, void* userData)
    {
        var log = (Action<string>)GCHandle.FromIntPtr((nint)userData).Target!;
        log($"[Vulkan {(severity.HasFlag(VkDebugUtilsMessageSeverityFlagsEXT.Error) ? "error" : "warning")}] {new string((sbyte*)data->pMessage)}");
        return 0; // VK_FALSE: don't abort the call
    }

    private HashSet<string> DeviceExtensions(VkPhysicalDevice device)
    {
        InstanceApi.vkEnumerateDeviceExtensionProperties(device, out uint count);
        Span<VkExtensionProperties> properties = new VkExtensionProperties[count];
        InstanceApi.vkEnumerateDeviceExtensionProperties(device, properties);
        var set = new HashSet<string>();
        foreach (VkExtensionProperties p in properties)
        {
            VkExtensionProperties e = p;
            set.Add(new string((sbyte*)e.extensionName));
        }

        return set;
    }

    private void PickPhysicalDevice()
    {
        InstanceApi.vkEnumeratePhysicalDevices(out uint count);
        if (count == 0)
        {
            throw new QuakeException("No graphics card with Vulkan was found.");
        }

        Span<VkPhysicalDevice> devices = new VkPhysicalDevice[count];
        InstanceApi.vkEnumeratePhysicalDevices(devices);

        int bestScore = -1;
        var skipped = new List<string>();
        foreach (VkPhysicalDevice device in devices)
        {
            VkPhysicalDeviceProperties properties = InstanceApi.vkGetPhysicalDeviceProperties(device);
            string name = new((sbyte*)properties.deviceName);
            if (properties.apiVersion < VK_API_VERSION_1_3)
            {
                skipped.Add($"{name}: Vulkan {VersionText(properties.apiVersion)} (needs 1.3)");
                continue;
            }

            if (!RequiredDeviceExtensions.All(DeviceExtensions(device).Contains))
            {
                skipped.Add($"{name}: cannot show images in a window");
                continue;
            }

            if (FindQueueFamily(device) is not uint family)
            {
                skipped.Add($"{name}: no queue that draws and shows");
                continue;
            }

            int score = properties.deviceType == VkPhysicalDeviceType.DiscreteGpu ? 1000 : 100;
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            PhysicalDevice = device;
            QueueFamily = family;
            DeviceName = name;
        }

        if (bestScore < 0)
        {
            throw new QuakeException("No graphics card here can show the game:\n" + string.Join("\n", skipped.Select(r => "  " + r)));
        }

        log($"GPU: {DeviceName}");
    }

    private uint? FindQueueFamily(VkPhysicalDevice device)
    {
        InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, out uint count);
        Span<VkQueueFamilyProperties> families = new VkQueueFamilyProperties[count];
        InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, families);
        for (uint i = 0; i < count; i++)
        {
            if ((families[(int)i].queueFlags & VkQueueFlags.Graphics) == 0)
            {
                continue;
            }

            VkBool32 present;
            InstanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(device, i, Surface, &present);
            if (present)
            {
                return i;
            }
        }

        return null;
    }

    private void CreateDevice()
    {
        float priority = 1f;
        VkDeviceQueueCreateInfo queueInfo = new()
        {
            queueFamilyIndex = QueueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };

        VkPhysicalDeviceVulkan13Features features13 = new()
        {
            dynamicRendering = true,
            synchronization2 = true,
        };
        VkPhysicalDeviceFeatures2 features = new() { pNext = &features13 };

        using var extensionNames = new NativeStringArray(RequiredDeviceExtensions);
        VkDeviceCreateInfo deviceInfo = new()
        {
            pNext = &features,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo,
            enabledExtensionCount = extensionNames.Count,
            ppEnabledExtensionNames = extensionNames.Pointer,
        };
        Check(InstanceApi.vkCreateDevice(PhysicalDevice, &deviceInfo, out Device), "vkCreateDevice");
        Api = GetApi(Instance, Device);
        Api.vkGetDeviceQueue(QueueFamily, 0, out Queue);
    }
}
