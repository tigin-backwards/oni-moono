using System;
using System.Collections.Generic;
using OniMoono.Desktop.Vulkan;

namespace OniMoono.Desktop.D3d11
{
    /// Fault injection for tests (ONI_D3D11_FAIL, comma-separated), as ONI_VULKAN_FAIL (VkFault)
    /// for the Vulkan device. Each is made up in place of a call, never by changing the driver:
    /// - start-up steps, which fail after the step's own call, as a driver failing there would,
    ///   and fall back (Renderers.Open: the others as auto): `factory` (CreateDXGIFactory2),
    ///   `adapter` (the adapter choice), `device` (D3D11CreateDevice), `debug` (the debug
    ///   layer's info queue, with `--d3d11-debug` only), `swapchain` (CreateSwapChainForHwnd and
    ///   its waitable), `presenter` (the presenter's program and states), `screen` (the first
    ///   screen texture, the device's last start-up step);
    /// - `removed-after:N`: the Nth Present returns DXGI_ERROR_DEVICE_REMOVED in place of
    ///   presenting (`removed-after:1`: the first present), as a removed device's Present does
    ///   (IDXGISwapChain::Present, Return value); the game quits with the error;
    /// - `mode-changed-after:N`: the Nth Present presents and returns DXGI_STATUS_MODE_CHANGED
    ///   in place of S_OK (DXGI_STATUS; D3d11Present);
    /// - `exclusive-refused`: SetFullscreenState(TRUE) is not called and returns
    ///   DXGI_ERROR_NOT_CURRENTLY_AVAILABLE, as the original's did while its window lacked the
    ///   focus (j0009128, j0009178);
    /// - `enter-hold:MS`: the device thread waits MS after SetFullscreenState(TRUE) returns,
    ///   inside Enter, while the main thread answers sent messages (a focus change during Enter);
    /// - `crash-at:N`: the process ends at presented frame N without shutting down, as a crash
    ///   would (the start-up guard's test);
    /// - `guard-build:X`: the start-up guard keys this run's build as X.
    internal static class D3d11Fault
    {
        public const string Variable = "ONI_D3D11_FAIL";

        /// Every fault this build has (the names; `:N`, `:MS` and `:X` take a value).
        public static readonly string[] Steps = ["factory", "adapter", "device", "debug", "swapchain", "presenter", "screen", "exclusive-refused"];
        public static readonly string[] Valued = ["removed-after", "mode-changed-after", "enter-hold", "crash-at", "guard-build"];

        static readonly Dictionary<string, string> faults = VkFault.Parse(Environment.GetEnvironmentVariable(Variable));

        public static bool Has(string name) => faults.ContainsKey(name);

        /// The text a `name:X` fault carries, or null.
        public static string Text(string name) => faults.GetValueOrDefault(name);

        /// The number a `name:N` fault carries, or -1.
        public static int Number(string name) => faults.TryGetValue(name, out var v) && int.TryParse(v, out int n) ? n : -1;

        /// The names in `text` that are no fault of this build (logged at start-up, so a misspelt
        /// test fault is not taken for a pass).
        public static List<string> Unknown(string text)
        {
            var unknown = new List<string>();
            foreach (var (name, value) in VkFault.Parse(text))
            {
                bool known = Array.IndexOf(Steps, name) >= 0 && value.Length == 0
                             || Array.IndexOf(Valued, name) >= 0 && (name == "guard-build" ? value.Length > 0 : int.TryParse(value, out int n) && n > 0);
                if (!known) unknown.Add(value.Length == 0 ? name : $"{name}:{value}");
            }
            return unknown;
        }

        public static List<string> Unknown() => Unknown(Environment.GetEnvironmentVariable(Variable));

        /// Start-up fails at `step` (after it has made what it makes): the fallback's test.
        public static void FailAt(string step)
        {
            if (Has(step)) throw new D3d11UnavailableException($"{Variable}={step} (a test of the fallback)");
        }
    }
}
