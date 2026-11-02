using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace OniMoono.Desktop.Gl
{
    /// Windows: what the log says about the window when CreateSwapChainForHwnd refuses it. The
    /// call documents S_OK, E_OUTOFMEMORY and DXGI_ERROR_INVALID_CALL only; it also says a window
    /// can have only one flip-model swap chain at a time. E_ACCESSDENIED is what Windows 11
    /// returned here when the window already had one (`--dxgi-fault occupied`, measured). Who
    /// else may hold one: OpenGL's driver presenting through DXGI itself (NVIDIA's "Vulkan/OpenGL
    /// present method"; how a laptop's GPU reaches a screen on the integrated GPU is up to its
    /// driver), an overlay or capture hook, a proxy dxgi.dll. The
    /// report names the window's owner and styles, which adapter shows its monitor against the
    /// device's, and the graphics and hook modules loaded before DXGI started.
    internal static unsafe class DxgiWindowReport
    {
        /// The facts the report is made of (Facts gathers them; Format is pure).
        internal readonly record struct WindowFacts(
            bool IsWindow, uint OwnerProcess, uint OwnerThread, uint Process, uint Thread,
            long Style, long ExStyle, int ClientWidth, int ClientHeight,
            string Monitor, string MonitorAdapter, long MonitorAdapterLuid, string DeviceAdapter, long DeviceLuid);

        /// Windows' graphics runtimes (one loaded from elsewhere is a proxy: ReShade, Special K),
        /// then overlays and capture hooks known to hook presentation in a game's process.
        internal static readonly string[] Runtimes = ["dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "dcomp.dll", "vulkan-1.dll"];
        internal static readonly string[] Hooks =
        [
            "GameOverlayRenderer64.dll", "RTSSHooks64.dll", "DiscordHook64.dll", "nvspcap64.dll",
            "graphics-hook64.dll", "ReShade64.dll", "SpecialK64.dll", "OWClient.dll", "overlay64.dll",
        ];

        static string modulesBefore;

        /// The watched modules loaded now, once: called as DXGI first starts, before its own DLLs
        /// are loaded.
        public static void NoteModulesBeforeDxgi() => modulesBefore ??= LoadedModules();

        static string LoadedModules()
        {
            var found = new List<string>();
            string system = Environment.SystemDirectory;
            char* path = stackalloc char[1024];
            foreach (var name in (string[])[.. Runtimes, .. Hooks])
            {
                IntPtr module = GetModuleHandleW(name);
                if (module == IntPtr.Zero) continue;
                uint length = GetModuleFileNameW(module, path, 1024);
                string file = length > 0 ? new string(path, 0, (int)length) : "";
                bool proxy = Array.IndexOf(Runtimes, name) >= 0 && !file.StartsWith(system, StringComparison.OrdinalIgnoreCase);
                found.Add(proxy ? $"{name} (not Windows' own: from {Path.GetFileName(Path.GetDirectoryName(file))})" : name);
            }
            return found.Count == 0 ? "none" : string.Join(", ", found);
        }

        /// The report for `hwnd`, which a D3D11 device on adapter `deviceLuid` (`deviceAdapter`)
        /// could not have a swap chain for. Never throws: what cannot be read is left out.
        public static string Describe(void* factory, IntPtr hwnd, long deviceLuid, string deviceAdapter)
        {
            try
            {
                return Format(hwnd, Facts(factory, hwnd, deviceLuid, deviceAdapter), modulesBefore);
            }
            catch (Exception e)
            {
                return $"window 0x{hwnd:X}: not described ({e.GetType().Name})";
            }
        }

        static WindowFacts Facts(void* factory, IntPtr hwnd, long deviceLuid, string deviceAdapter)
        {
            bool isWindow = IsWindow(hwnd) != 0;
            uint ownerProcess = 0, ownerThread = isWindow ? GetWindowThreadProcessId(hwnd, &ownerProcess) : 0;
            Rect client = default;
            if (isWindow) GetClientRect(hwnd, &client);
            IntPtr monitor = isWindow ? MonitorFromWindow(hwnd, MonitorDefaultToNearest) : IntPtr.Zero;
            var (monitorName, adapterName, adapterLuid) = OutputOf(factory, monitor);
            return new WindowFacts(isWindow, ownerProcess, ownerThread, GetCurrentProcessId(), GetCurrentThreadId(),
                isWindow ? (long)GetWindowLongPtrW(hwnd, GwlStyle) : 0, isWindow ? (long)GetWindowLongPtrW(hwnd, GwlExStyle) : 0,
                client.Right - client.Left, client.Bottom - client.Top, monitorName, adapterName, adapterLuid, deviceAdapter, deviceLuid);
        }

        /// The output showing `monitor` and its adapter, among the adapters `factory` lists.
        static (string Monitor, string Adapter, long Luid) OutputOf(void* factory, IntPtr monitor)
        {
            if (factory == null || monitor == IntPtr.Zero) return ("", "", 0);
            for (uint i = 0; ; i++)
            {
                void* adapter;
                if (Dxgi.Failed(Dxgi.EnumAdapters1(factory, i, &adapter))) break;
                try
                {
                    for (uint j = 0; ; j++)
                    {
                        void* output;
                        if (Dxgi.Failed(Dxgi.EnumOutputs(adapter, j, &output))) break;
                        Dxgi.OutputDesc desc;
                        bool ok = !Dxgi.Failed(Dxgi.GetDesc(output, &desc));
                        Dxgi.Release(ref output);
                        if (!ok || desc.Monitor != monitor) continue;
                        Dxgi.AdapterDesc1 a;
                        bool named = !Dxgi.Failed(Dxgi.GetDesc1(adapter, &a));
                        return (desc.Name, named ? a.Name : "", named ? a.Luid : 0);
                    }
                }
                finally
                {
                    Dxgi.Release(ref adapter);
                }
            }
            return ("", "", 0);
        }

        internal const long WsExLayered = 0x80000, WsExNoRedirectionBitmap = 0x200000;

        /// The report's text (pure): one clause each for the window's owner, its styles and size,
        /// its monitor's adapter against the device's, and the modules loaded before DXGI.
        internal static string Format(IntPtr hwnd, WindowFacts f, string modules)
        {
            if (!f.IsWindow) return $"window 0x{hwnd:X}: not a window";
            string owner = f.OwnerProcess != f.Process
                ? $"of another process ({f.OwnerProcess})"
                : $"made on thread {f.OwnerThread}, DXGI called on thread {f.Thread}";
            string styles = $"style 0x{f.Style:X8}, extended 0x{f.ExStyle:X8}"
                + ((f.ExStyle & WsExNoRedirectionBitmap) != 0 ? " (no redirection bitmap)" : "")
                + ((f.ExStyle & WsExLayered) != 0 ? " (layered)" : "");
            string monitor = f.Monitor.Length == 0
                ? "its monitor is on no adapter DXGI lists"
                : f.MonitorAdapterLuid == f.DeviceLuid
                    ? $"on {f.Monitor}, an output of the device's adapter"
                    : $"on {f.Monitor}, an output of {f.MonitorAdapter}, not of the device's {f.DeviceAdapter}: the picture crosses adapters";
            return $"window 0x{hwnd:X} {owner}; {styles}; client {f.ClientWidth}x{f.ClientHeight}; {monitor}; loaded before DXGI started: {modules ?? "not noted"}";
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        const int GwlStyle = -16, GwlExStyle = -20;
        const uint MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")]
        static extern int IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, uint* processId);

        [DllImport("user32.dll")]
        static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        static extern int GetClientRect(IntPtr hwnd, Rect* rect);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32.dll")]
        static extern uint GetModuleFileNameW(IntPtr module, char* path, uint size);
    }
}
