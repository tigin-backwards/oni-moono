#nullable enable
using System;
using System.Linq;

namespace OniMoono.Host
{
    /// `--dxgi-fault WHAT[,WHAT]` (OniMoono.Desktop.Gl.DxgiSwapChain.Fault): the made-up DXGI
    /// failures this build has: OpenGL's and Vulkan's (import, importview:N and
    /// fencetimeout:N are Vulkan's). The tests compile it in.
    internal static class DxgiFaultOption
    {
        /// At start-up, and once DXGI runs (with N, the Nth time), and "norestart".
        public static readonly string[] Steps = ["extension", "adapter", "device", "register", "swapchain", "association", "import", "norestart"];
        public static readonly string[] Counted = ["lock", "resize", "removed", "importview", "fencetimeout"];

        public static string Known => string.Join(", ", Steps.Concat(Counted.Select(c => c + ":N")));

        /// Null when every comma-separated token of `value` is a fault of this build, else the
        /// first one that is not.
        public static string? Unknown(string value)
        {
            foreach (var token in value.Split(','))
            {
                if (Steps.Contains(token)) continue;
                int colon = token.IndexOf(':');
                if (colon > 0 && Counted.Contains(token[..colon]) && int.TryParse(token[(colon + 1)..], System.Globalization.NumberStyles.None, null, out int n) && n > 0)
                    continue;
                return token;
            }
            return null;
        }
    }
}
