using System.Runtime.CompilerServices;
using Brutal.VulkanApi;
using OniMoono.Engine;

namespace OniMoono.Desktop.Vulkan
{
    /// A CPU wait for a fence, as vkWaitForFences with no timeout, made of 100 ms slices so that a
    /// wait still going after 10 s is logged and tagged (LongWait, as the GL fence waits are).
    internal static unsafe class VkFenceWait
    {
        const nint SliceNanoseconds = 100_000_000;

        /// Waits for FENCE (SITE names the wait; SLOT, when not -1, the frame slot or presenter
        /// slot it belongs to) and returns the result that ended the wait. A STALLABLE wait never
        /// ends under ONI_TEST_STALL_FENCE=1 (LongWaits.StallFence).
        public static VkResult Wait(VkContext vk, VkFence fence, string site, int slot = -1, bool stallable = false)
        {
            var wait = new LongWait();
            if (stallable && LongWaits.StallFence)
                while (true)
                {
                    System.Threading.Thread.Sleep(100);
                    wait.Slice(site, Describe(fence, slot) + ", stalled by ONI_TEST_STALL_FENCE");
                }
            VkResult r;
            while ((r = vk.Device.PInvoke.vkWaitForFences.Invoke(vk.Device.Handle, 1, &fence, true, SliceNanoseconds)) == VkResult.Timeout)
                wait.Slice(site, Describe(fence, slot));
            wait.End();
            return r;
        }

        static string Describe(VkFence fence, int slot)
        {
            ulong handle = Unsafe.SizeOf<VkFence>() == sizeof(ulong) ? Unsafe.As<VkFence, ulong>(ref fence) : 0;
            return slot < 0 ? $"fence 0x{handle:x}" : $"fence 0x{handle:x}, slot {slot}";
        }
    }
}
