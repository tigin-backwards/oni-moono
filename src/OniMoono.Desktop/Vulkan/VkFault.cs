using System;
using System.Collections.Generic;
using System.Threading;
using Brutal.VulkanApi;

namespace OniMoono.Desktop.Vulkan
{
    /// Fault injection for tests (ONI_VULKAN_FAIL, comma-separated):
    /// - `context`, `device`, `presenter`: start-up fails at that step (the OpenGL fallback);
    /// - `presenter-create`: the presenter's constructor fails after making its objects;
    /// - `present-after:N`: at frame N presenting fails for good (a lost surface);
    /// - `present-oom-after:N`: from frame N the next three swapchain creations run out of
    ///   memory (retried with backoff, then presenting goes on);
    /// - `alloc-every:N`, `pipeline-every:N`, `program-every:N`: every Nth texture or mesh
    ///   allocation, pipeline, or program build runs out of device memory.
    /// - `crash-at:N`: the process ends at presented frame N without shutting down, as a
    ///   crash would (the start-up guard's test);
    /// - `guard-build:X`: the start-up guard keys this run's build as X.
    internal static class VkFault
    {
        static readonly Dictionary<string, string> faults = Parse(Environment.GetEnvironmentVariable("ONI_VULKAN_FAIL"));
        static readonly Dictionary<string, int> counts = new();

        internal static Dictionary<string, string> Parse(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var token in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = token.IndexOf(':');
                if (colon < 0) result[token] = "";
                else result[token[..colon]] = token[(colon + 1)..];
            }
            return result;
        }

        public static bool Has(string name) => faults.ContainsKey(name);

        /// The text a `name:X` fault carries, or null.
        public static string Text(string name) => faults.GetValueOrDefault(name);

        /// The number a `name:N` fault carries, or -1.
        public static int Number(string name) => faults.TryGetValue(name, out var v) && int.TryParse(v, out int n) ? n : -1;

        /// True on every Nth call for a `name:N` fault.
        public static bool Every(string name)
        {
            int n = Number(name);
            if (n <= 0) return false;
            lock (counts)
            {
                int c = counts.GetValueOrDefault(name) + 1;
                counts[name] = c;
                return c % n == 0;
            }
        }

        /// Throws an out-of-device-memory error on every Nth call for a `name:N` fault.
        public static void OutOfMemoryEvery(string name)
        {
            if (Every(name)) throw new VkResultError(VkResult.ErrorOutOfDeviceMemory, $"ONI_VULKAN_FAIL={name} (a test of running out of memory)");
        }

        /// Out of host or device memory: worth trying again later, never remembered as a refusal.
        public static bool IsOutOfMemory(Exception e)
            => e is VkResultError { Result: VkResult.ErrorOutOfHostMemory or VkResult.ErrorOutOfDeviceMemory or VkResult.ErrorOutOfPoolMemory }
               || e.InnerException is VkResultError { Result: VkResult.ErrorOutOfHostMemory or VkResult.ErrorOutOfDeviceMemory or VkResult.ErrorOutOfPoolMemory };
    }
}
