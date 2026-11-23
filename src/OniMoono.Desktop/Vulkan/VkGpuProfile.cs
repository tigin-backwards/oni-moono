using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;

namespace OniMoono.Desktop.Vulkan
{
    /// Diagnostics (--gpu-profile, as GlGpuProfile on OpenGL): the GPU time of each rendering pass
    /// of a frame, from timestamps written at the start and end of every pass (stage ALL_COMMANDS:
    /// each one is written once the work recorded before it is done), read back once the frame's
    /// fence has signalled. A pass is named by its target (the back buffer, or a render texture's
    /// size and format) and the program of its first draw; the work between passes (barriers,
    /// copies, resolves, mips, uploads into textures the batch already used) is "outside passes".
    /// A batch's upload command buffer runs ahead of its rendering, so a later batch's uploads land
    /// in the segment before its first mark. Reported with the frame counters (--frame-stats),
    /// largest first. Adds two timestamps per pass; for measurement runs only.
    internal sealed unsafe class VkGpuProfile : IDisposable
    {
        /// Timestamps a frame may write; a frame with more leaves the rest unmeasured (counted).
        const int Capacity = 1024;

        readonly VkContext vk;
        readonly double periodNs;
        readonly ulong validMask;
        readonly VkQueryPool[] pools;
        readonly List<string>[] labels;
        readonly Dictionary<string, (double Ms, long Count)> totals = new();
        double measured;
        int frames;
        long overflows;

        public VkGpuProfile(VkContext vk, int slots, double periodNs, int validBits)
        {
            this.vk = vk;
            this.periodNs = periodNs;
            validMask = validBits >= 64 ? ulong.MaxValue : (1UL << validBits) - 1;
            pools = new VkQueryPool[slots];
            labels = new List<string>[slots];
            for (int i = 0; i < slots; i++)
            {
                pools[i] = vk.Device.CreateQueryPool(new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = Capacity }, null);
                labels[i] = new List<string>(128);
            }
        }

        /// The frame's first command (outside any pass): its queries reset and its start written.
        public void BeginFrame(CommandBuffer cb, int slot)
        {
            vk.Device.PInvoke.vkCmdResetQueryPool.Invoke(cb.Handle, pools[slot], 0, Capacity);
            labels[slot].Clear();
            Mark(cb, slot, null);
        }

        /// Ends the segment since the last mark as `label` (outside any pass).
        public void Mark(CommandBuffer cb, int slot, string label)
        {
            var l = labels[slot];
            if (l.Count >= Capacity)
            {
                overflows++;
                return;
            }
            vk.Device.PInvoke.vkCmdWriteTimestamp2.Invoke(cb.Handle, VkPipelineStageFlags2.AllCommandsBit, pools[slot], l.Count);
            l.Add(label);
        }

        /// After the frame's fence: its segments added to the totals.
        public void Collect(int slot)
        {
            var l = labels[slot];
            int n = l.Count;
            if (n < 2) return;
            var values = new ulong[n];
            fixed (ulong* v = values)
            {
                var r = vk.Device.PInvoke.vkGetQueryPoolResults.Invoke(vk.Device.Handle, pools[slot], 0, n, (nuint)(n * 8), v, (Brutal.ByteSize64)8, VkQueryResultFlags._64Bit);
                if (r != VkResult.Success) return;
            }
            for (int i = 1; i < n; i++)
            {
                double ms = ((values[i] - values[i - 1]) & validMask) * periodNs / 1e6;
                var (sum, count) = totals.GetValueOrDefault(l[i]);
                totals[l[i]] = (sum + ms, count + 1);
                measured += ms;
            }
            frames++;
            l.Clear();
        }

        /// GPU ms a frame by pass since the last take, largest first (null before a frame).
        public string Take()
        {
            if (frames == 0) return null;
            int f = frames;
            var top = totals.OrderByDescending(kv => kv.Value.Ms).Take(20)
                .Select(kv => FormattableString.Invariant($"{kv.Key} {kv.Value.Ms / f:F2} ms ({kv.Value.Count / (double)f:F1}/frame)")).ToList();
            double rest = totals.OrderByDescending(kv => kv.Value.Ms).Skip(20).Sum(kv => kv.Value.Ms) / f;
            if (rest > 0) top.Add(FormattableString.Invariant($"the other {totals.Count - 20} {rest:F2} ms"));
            string text = FormattableString.Invariant($"gpu by pass (ms a frame over {f}, {measured / f:F2} measured{(overflows > 0 ? $", {overflows} timestamps past the {Capacity} a frame holds" : "")}): ")
                          + string.Join("; ", top);
            totals.Clear();
            measured = 0;
            frames = 0;
            overflows = 0;
            return text;
        }

        public void Dispose()
        {
            foreach (var p in pools)
                if (!p.IsNull()) vk.Device.DestroyQueryPool(p, null);
        }
    }
}
