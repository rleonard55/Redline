using System.Runtime.InteropServices;
using System.Text;

namespace Redline.Analysis.Grmr;

/// <summary>VkPhysicalDeviceType.</summary>
public enum VulkanDeviceType
{
    Other = 0,
    IntegratedGpu = 1,
    DiscreteGpu = 2,
    VirtualGpu = 3,
    Cpu = 4,
}

/// <summary>
/// Which GPUs Vulkan sees. The model only goes to a discrete GPU: on an integrated one it measured no faster than
/// the CPU (Iris Xe: 650-770 vs 370-630 ms/sentence) and its "video memory" is system RAM (~1.2 GB vs ~130 MB).
/// llama.cpp's Vulkan backend also prefers a discrete GPU when there is one, so "any discrete GPU" means it gets used.
/// Called in the model's own process: loading the Vulkan loader and drivers costs memory until the process exits.
/// </summary>
public static class VulkanDevices
{
    /// <summary>The GPUs Vulkan reports; empty when there's no Vulkan loader or driver.</summary>
    public static IReadOnlyList<(string Name, VulkanDeviceType Type)> List()
    {
        try
        {
            return Enumerate();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return [];
        }
    }

    /// <summary>
    /// Use the GPU only when there is a discrete one. Otherwise <paramref name="integrated"/> names the integrated
    /// GPU that was passed over (null when there's no GPU at all).
    /// </summary>
    public static bool ShouldUseGpu(IReadOnlyList<(string Name, VulkanDeviceType Type)> devices, out string? integrated)
    {
        integrated = devices.FirstOrDefault(d => d.Type == VulkanDeviceType.IntegratedGpu).Name;
        return devices.Any(d => d.Type == VulkanDeviceType.DiscreteGpu);
    }

    private static List<(string, VulkanDeviceType)> Enumerate()
    {
        var info = new InstanceCreateInfo { SType = 1 }; // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
        if (vkCreateInstance(ref info, IntPtr.Zero, out var instance) != 0) return [];
        var result = new List<(string, VulkanDeviceType)>();
        try
        {
            uint count = 0;
            if (vkEnumeratePhysicalDevices(instance, ref count, null) != 0 || count == 0) return result;
            var devices = new IntPtr[count];
            if (vkEnumeratePhysicalDevices(instance, ref count, devices) < 0) return result;

            // VkPhysicalDeviceProperties: apiVersion, driverVersion, vendorID, deviceID, deviceType (offset 16),
            // deviceName[256] (offset 20), ... ~824 bytes in all.
            var properties = Marshal.AllocHGlobal(4096);
            try
            {
                foreach (var device in devices.Take((int)count))
                {
                    vkGetPhysicalDeviceProperties(device, properties);
                    var type = (VulkanDeviceType)Marshal.ReadInt32(properties, 16);
                    var nameBytes = new byte[256];
                    Marshal.Copy(properties + 20, nameBytes, 0, nameBytes.Length);
                    int end = Array.IndexOf(nameBytes, (byte)0);
                    result.Add((Encoding.UTF8.GetString(nameBytes, 0, end < 0 ? nameBytes.Length : end), type));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(properties);
            }
        }
        finally
        {
            vkDestroyInstance(instance, IntPtr.Zero);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public int SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr PApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll")]
    private static extern int vkCreateInstance(ref InstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

    [DllImport("vulkan-1.dll")]
    private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr[]? devices);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

    [DllImport("vulkan-1.dll")]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);
}
