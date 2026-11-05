using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OniMoono.Engine.Rendering;
using UnityEngine.Rendering;

namespace UnityEngine
{
    public partial class SystemInfo
    {
        static GraphicsCaps Gfx => GraphicsDevice.Caps;

        public static int processorCount => Environment.ProcessorCount;
        public static string processorType => cpuModel.Value;
        public static int systemMemorySize => memoryMB.Value;
        public static string operatingSystem => osName.Value;
        public static OperatingSystemFamily operatingSystemFamily => OperatingSystem.IsWindows() ? OperatingSystemFamily.Windows : OperatingSystemFamily.Linux;
        public static DeviceType deviceType => DeviceType.Desktop;
        public static string deviceModel => deviceModelName.Value;

        public static GraphicsDeviceType graphicsDeviceType => Gfx.DeviceType;
        public static string graphicsDeviceName => Gfx.DeviceName;
        public static string graphicsDeviceVendor => Gfx.DeviceVendor;
        public static string graphicsDeviceVersion => Gfx.DeviceVersion;
        public static int graphicsDeviceID => Gfx.DeviceId;
        public static int graphicsDeviceVendorID => Gfx.VendorId;
        public static int graphicsMemorySize => Gfx.MemorySizeMB;
        public static int graphicsShaderLevel => Gfx.ShaderLevel;
        public static bool graphicsMultiThreaded => false;
        public static int maxTextureSize => Gfx.MaxTextureSize;
        public static int supportedRenderTargetCount => Gfx.MaxRenderTargets;
        public static bool supports2DArrayTextures => Gfx.Supports2DArrayTextures;
        public static bool supports3DTextures => Gfx.Supports3DTextures;
        public static bool supportsInstancing => Gfx.SupportsInstancing;
        public static bool supportsComputeShaders => Gfx.SupportsComputeShaders;
        /// Texture read-backs (AsyncGPUReadback) on any device that renders.
        public static bool supportsAsyncGPUReadback => Gfx.DeviceType != GraphicsDeviceType.Null;
        public static bool supportsShadows => true;
        public static bool supportsSparseTextures => false;
        public static NPOTSupport npotSupport => NPOTSupport.Full;
        public static CopyTextureSupport copyTextureSupport => Gfx.DeviceType == GraphicsDeviceType.Null ? CopyTextureSupport.None : CopyTextureSupport.Basic | CopyTextureSupport.Copy3D | CopyTextureSupport.DifferentTypes | CopyTextureSupport.TextureToRT | CopyTextureSupport.RTToTexture;

        public static bool SupportsTextureFormat(TextureFormat format) => Gfx.SupportsTexture(format);
        public static bool SupportsRenderTextureFormat(RenderTextureFormat format) => Gfx.SupportsRenderTexture(format);

        /// The platform's default formats, as the original's GLCore player gives them in gamma
        /// colour space (UnityProbe's apigaps "rtctor").
        public static UnityEngine.Experimental.Rendering.GraphicsFormat GetGraphicsFormat(UnityEngine.Experimental.Rendering.DefaultFormat format) => format switch
        {
            UnityEngine.Experimental.Rendering.DefaultFormat.LDR => UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
            UnityEngine.Experimental.Rendering.DefaultFormat.HDR => UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
            UnityEngine.Experimental.Rendering.DefaultFormat.DepthStencil => UnityEngine.Experimental.Rendering.GraphicsFormat.D24_UNorm_S8_UInt,
            UnityEngine.Experimental.Rendering.DefaultFormat.Shadow => UnityEngine.Experimental.Rendering.GraphicsFormat.D16_UNorm,
            UnityEngine.Experimental.Rendering.DefaultFormat.Video => UnityEngine.Experimental.Rendering.GraphicsFormat.YUV2,
            _ => UnityEngine.Experimental.Rendering.GraphicsFormat.None,
        };

        /// A value under HKEY_LOCAL_MACHINE (Windows), or null.
        static string MachineRegistryString(string key, string name)
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            return (k?.GetValue(name) as string)?.Trim();
        }

        static readonly Lazy<string> cpuModel = new(() =>
        {
            if (OperatingSystem.IsWindows())
                return MachineRegistryString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? RuntimeInformation.ProcessArchitecture.ToString();
            try
            {
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                return line != null ? line[(line.IndexOf(':') + 1)..].Trim() : RuntimeInformation.ProcessArchitecture.ToString();
            }
            catch (IOException)
            {
                return RuntimeInformation.ProcessArchitecture.ToString();
            }
        });

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        /// Physical memory in MB (Linux: MemTotal; Windows: GlobalMemoryStatusEx).
        static readonly Lazy<int> memoryMB = new(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status)) return (int)(status.TotalPhys / (1024 * 1024));
                return (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
            }
            try
            {
                var line = File.ReadLines("/proc/meminfo").First(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
                long kb = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                return (int)(kb / 1024);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or FormatException)
            {
                return (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
            }
        });

        /// Unity on Linux: "<distribution> <version> <architecture>" with the kernel version; on
        /// Windows: "Windows 10  (10.0.19045) 64bit", Windows 11 from build 22000.
        static readonly Lazy<string> osName = new(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                var v = Environment.OSVersion.Version;
                string name = v.Major == 10 && v.Build >= 22000 ? "Windows 11" : $"Windows {(v.Major == 10 ? "10" : v.Major + "." + v.Minor)}";
                return $"{name}  ({v.Major}.{v.Minor}.{v.Build}) {(Environment.Is64BitOperatingSystem ? "64bit" : "32bit")}";
            }
            string distro = "Linux";
            try
            {
                var pretty = File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
                if (pretty != null) distro = pretty["PRETTY_NAME=".Length..].Trim('"');
            }
            catch (IOException) { }
            return $"Linux {Environment.OSVersion.Version} {distro} {(Environment.Is64BitOperatingSystem ? "64bit" : "32bit")}";
        });

        /// Linux: the DMI product name; Windows: "<product> (<manufacturer>)" from the BIOS
        /// description, as Unity reports them.
        static readonly Lazy<string> deviceModelName = new(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                var product = MachineRegistryString(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName");
                var maker = MachineRegistryString(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer");
                if (string.IsNullOrEmpty(product)) return "PC";
                return string.IsNullOrEmpty(maker) ? product : $"{product} ({maker})";
            }
            try
            {
                var name = File.ReadAllText("/sys/devices/virtual/dmi/id/product_name").Trim();
                return name.Length > 0 ? name : "PC";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return "PC";
            }
        });
    }

    public partial class QualitySettings
    {
        static int s_Current = -1;
        static int s_VSync = -1;

        static OniMoono.Assets.TypeTrees.Fields Levels => OniMoono.Engine.Assets.ProjectSettings.QualitySettings;

        public static int GetQualityLevel()
        {
            if (s_Current < 0) s_Current = Levels.Int("m_CurrentQuality");
            return s_Current;
        }

        public static void SetQualityLevel(int index) => SetQualityLevel(index, true);

        public static void SetQualityLevel(int index, bool applyExpensiveChanges)
        {
            var levels = Levels.Array("m_QualitySettings");
            s_Current = Mathf.Clamp(index, 0, levels.Length - 1);
            s_VSync = -1;
        }

        public static string[] names => Levels.Array("m_QualitySettings").Cast<OniMoono.Assets.TypeTrees.Fields>().Select(q => q.String("name")).ToArray();

        /// The current level's vSync count unless set since the level was applied.
        public static int vSyncCount
        {
            get
            {
                if (s_VSync >= 0) return s_VSync;
                var level = (OniMoono.Assets.TypeTrees.Fields)Levels.Array("m_QualitySettings")[GetQualityLevel()];
                return level.Int("vSyncCount");
            }
            set => s_VSync = Mathf.Clamp(value, 0, 4);
        }

        /// Gamma or linear, from the project's player settings.
        public static ColorSpace activeColorSpace
            => OniMoono.Engine.Assets.ProjectSettings.PlayerSettings.Int("m_ActiveColorSpace") == 1 ? ColorSpace.Linear : ColorSpace.Gamma;
    }
}
