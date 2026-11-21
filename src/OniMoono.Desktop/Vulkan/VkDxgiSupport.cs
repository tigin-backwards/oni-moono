using System;
using System.Collections.Generic;
using Brutal.VulkanApi;

namespace OniMoono.Desktop.Vulkan
{
    /// What VkDxgiTarget decides from Vulkan's answers before DXGI presents, and how it words a
    /// handover timeout: the parts that run anywhere (ARCHITECTURE §3.5, "Presentation on
    /// Windows"). Vulkan can tell whether the driver imports a D3D11 texture (the extensions, the
    /// external image-format query, the handle's memory types); that its keyed mutex works is
    /// expressed only by VK_KHR_win32_keyed_mutex being there, which no query goes past.
    internal static class VkDxgiSupport
    {
        /// The handle type the shared texture is imported by: an NT handle from
        /// IDXGIResource1::CreateSharedHandle to a D3D11 texture.
        public const VkExternalMemoryHandleTypeFlags HandleType = VkExternalMemoryHandleTypeFlags.D3D11TextureBit;

        /// Why DXGI cannot present Vulkan's pictures for lack of a device extension (null: both
        /// are there).
        public static string MissingExtensions(bool externalMemoryWin32, bool win32KeyedMutex) => (externalMemoryWin32, win32KeyedMutex) switch
        {
            (true, true) => null,
            (false, false) => "the Vulkan driver has no VK_KHR_external_memory_win32 and no VK_KHR_win32_keyed_mutex",
            (false, _) => "the Vulkan driver has no VK_KHR_external_memory_win32",
            _ => "the Vulkan driver has no VK_KHR_win32_keyed_mutex",
        };

        /// Why the driver cannot import the shared texture as our image, from
        /// vkGetPhysicalDeviceImageFormatProperties2's result and VkExternalImageFormatProperties
        /// for HandleType (null: it can). DEDICATED_ONLY needs nothing: the import is always a
        /// dedicated allocation.
        public static string NotImportable(VkResult result, VkExternalMemoryProperties p)
        {
            if (result != VkResult.Success) return $"the Vulkan driver cannot import a D3D11 texture as an image ({result})";
            if ((p.ExternalMemoryFeatures & VkExternalMemoryFeatureFlags.ImportableBit) == 0) return "the Vulkan driver does not import D3D11 textures";
            // The image may be made with the handle type only when it is in compatibleHandleTypes
            // (VUID-VkImageCreateInfo-pNext-00990).
            if ((p.CompatibleHandleTypes & HandleType) == 0)
                return $"the Vulkan driver's D3D11 texture import is inconsistent (compatible handle types {Names(p.CompatibleHandleTypes)} lack D3D11_TEXTURE)";
            return null;
        }

        /// The memory type to import into: in both the image's and the handle's memoryTypeBits,
        /// device-local first; -1 when none is in both.
        public static int MemoryType(uint imageTypes, uint handleTypes, ReadOnlySpan<VkMemoryPropertyFlags> typeFlags)
        {
            uint types = imageTypes & handleTypes;
            int type = -1;
            for (int i = 0; i < typeFlags.Length && i < 32; i++)
            {
                if ((types & (1u << i)) == 0) continue;
                if (type < 0) type = i;
                if ((typeFlags[i] & VkMemoryPropertyFlags.DeviceLocalBit) != 0) return i;
            }
            return type;
        }

        /// What the driver said about the import, for the log (once per session).
        public static string DescribeImport(VkExternalMemoryProperties p, uint imageTypes, uint handleTypes, int type, VkMemoryPropertyFlags typeFlags,
            bool requiresDedicated, bool prefersDedicated)
            => $"present: Vulkan imports the shared texture by D3D11_TEXTURE (NT handle), R8G8B8A8_UNORM optimal: features {Names(p.ExternalMemoryFeatures)}, "
               + $"compatible handle types {Names(p.CompatibleHandleTypes)}, exportable from an import {Names(p.ExportFromImportedHandleTypes)}; "
               + $"memory types image 0x{imageTypes:X} handle 0x{handleTypes:X}, chosen {type} ({Names(typeFlags)}); "
               + $"dedicated allocation (the image requires one: {(requiresDedicated ? "yes" : "no")}, prefers: {(prefersDedicated ? "yes" : "no")})";

        /// D3D11's AcquireSync(1) timed out: what the Vulkan batch that should release key 1 and
        /// the mutex were doing. `atTimeout` is the batch fence's status then; `later` its status
        /// after a bounded wait (the same when it was done); `key1`/`key0` what AcquireSync with a
        /// zero timeout returned for each key afterwards (S_OK: free and handed back at once).
        public static string Verdict(VkResult atTimeout, VkResult later, int key1, int key0)
        {
            if (atTimeout == VkResult.ErrorDeviceLost || later == VkResult.ErrorDeviceLost) return "the Vulkan device is lost";
            string keys = key1 == 0 ? "key 1 has since reached D3D11" : key0 == 0 ? "key 1 never reached D3D11 and key 0 is free" : "neither key is free";
            if (atTimeout == VkResult.Success)
                return key1 == 0 ? $"Vulkan's batch had completed; {keys}: the release came late"
                    : key0 == 0 ? $"Vulkan's batch had completed, yet {keys}: the driver did not apply the batch's keyed-mutex acquire and release"
                    : $"Vulkan's batch had completed, yet {keys}: the driver took key 0 and did not release key 1";
            if (later == VkResult.Success) return $"Vulkan's batch was still running at the timeout and completed later; {keys}";
            return $"Vulkan's batch had not completed {LateWaitMilliseconds} ms after the timeout ({later}): the GPU work itself is stuck; {keys}";
        }

        /// How long the timeout report waits for a batch still running.
        public const int LateWaitMilliseconds = 2000;

        /// The set bits of a flags enum by name (none: "none").
        public static string Names<T>(T flags) where T : struct, Enum
        {
            ulong v = Bits(flags);
            if (v == 0) return "none";
            var names = new List<string>();
            foreach (T f in Enum.GetValues<T>())
            {
                ulong b = Bits(f);
                if (b == 0 || (b & (b - 1)) != 0 || (v & b) == 0) continue;
                names.Add(Short(f.ToString()));
                v &= ~b;
            }
            if (v != 0) names.Add($"0x{v:X}");
            return string.Join("|", names);
        }

        static ulong Bits<T>(T f) where T : struct, Enum => unchecked((ulong)Convert.ToInt64(f)) & 0xFFFFFFFF;

        static string Short(string name) => name.EndsWith("BitKHR", StringComparison.Ordinal) ? name[..^6] : name.EndsWith("Bit", StringComparison.Ordinal) ? name[..^3] : name;
    }
}
