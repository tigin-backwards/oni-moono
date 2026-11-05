using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using UnityEngine;

namespace OniMoono.Host
{
    /// The player: start-up in Unity's order, then the frame loop.
    internal static class Player
    {
        /// `patches`: the host's patches, put in before the first scene loads (game code runs
        /// from there on).
        public static int Run(Options options, string dataDir, Action patches)
        {
            StartupStats.Mark("player starts");
            QuitSignals.Install();
            AssetSystem.Initialize(dataDir);
            ProjectSettings.Load();
            BundlePrefetch.Start(EngineConfig.StreamingAssetsPath);
            if (options.Splash == "moo" && !options.Headless && !options.BatchMode)
                OniMoono.Desktop.Gl.GlDevice.Splash = System.Threading.Tasks.Task.Run(OniMoono.Engine.Rendering.SplashArt.Load);
            PlayerLoop.Initialize();
            OniMoono.Desktop.GlfwPlatform? platform = null;
            if (options.Headless || options.BatchMode) OniMoono.Engine.Platform.PlatformLoop.Install(null);
            else
            {
                OniMoono.Desktop.Gl.GlDevice.DebugOutput = options.GlDebug;
                OniMoono.Desktop.Gl.GlDevice.SwapIntervalOverride = options.VSyncCount;
                OniMoono.Desktop.Vulkan.VkDevice.SwapIntervalOverride = options.VSyncCount;
                OniMoono.Desktop.Vulkan.VkContext.Validation = options.VulkanValidation;
                OniMoono.Desktop.Gl.GlGpuProfile.Enabled = options.GpuProfile;
                OniMoono.Desktop.Gl.GlBindless.Disabled = options.NoBindless;
                OniMoono.Desktop.Gl.GlBindless.Forced = options.Bindless;
                OniMoono.Desktop.Gl.DxgiSwapChain.Enabled = options.Dxgi != false;
                OniMoono.Desktop.Gl.DxgiSwapChain.Fault = options.DxgiFault;
                (platform, var device) = OpenDevice(options);
                OniMoono.Engine.Platform.PlatformLoop.Install(platform);
                OniMoono.Engine.Rendering.GraphicsDevice.Install(device);
            }
            StartupStats.Mark("window and device ready");
            StartupProgress.Checkpoint("window");
            OniMoono.Engine.Rendering.FrameRenderer.Install();
            OniMoono.Engine.UI.CanvasSystem.Install();
            OniMoono.Engine.IMGUI.GUIManager.Install();
            EngineLog.Engine($"Project: {EngineConfig.CompanyName} / {EngineConfig.ProductName} {EngineConfig.Version} (Unity {EngineConfig.UnityVersion})");

            patches();
            var hooks = RuntimeInitializeHooks.Load(dataDir);
            hooks.Run(RuntimeInitializeLoadType.SubsystemRegistration);
            hooks.Run(RuntimeInitializeLoadType.AfterAssembliesLoaded);
            hooks.Run(RuntimeInitializeLoadType.BeforeSplashScreen);
            hooks.Run(RuntimeInitializeLoadType.BeforeSceneLoad);
            SceneLoader.LoadNow(0);
            StartupProgress.Checkpoint("scene0");
            hooks.Run(RuntimeInitializeLoadType.AfterSceneLoad);
            BrutalCredit.Install();

            var clock = Stopwatch.StartNew();
            double last = 0;
            int frame = 0;
            while (!PlayerLoop.QuitRequested && (options.Frames < 0 || frame < options.Frames))
            {
                double now = clock.Elapsed.TotalSeconds;
                // With vsync, whole refreshes (FramePacing), as Unity times a frame by its presentation.
                float dt = frame == 0 ? 0f : FramePacing.Delta(now - last);
                last = now;
                foreach (var (captureFrame, path, scale) in options.Captures)
                    if (captureFrame == frame) ScreenCapture.CaptureScreenshot(path, scale);
                foreach (var (probeFrame, x, y) in options.Probes)
                    if (probeFrame == frame && platform != null)
                        EngineLog.Engine(OniMoono.Engine.UI.CanvasSystem.Probe(new Vector2(x, platform.FramebufferHeight - y)));
                foreach (var (dumpFrame, path) in options.UiDumps)
                    if (dumpFrame == frame) OniMoono.Engine.UI.UiDump.Write(path);
                foreach (var (dumpFrame, spec) in options.ComponentDumps)
                    if (dumpFrame == frame) EngineLog.Engine(ComponentDump(spec));
                foreach (var (dumpFrame, path) in options.HierarchyDumps)
                    if (dumpFrame == frame) EngineLog.Engine($"mouse {Input.mousePosition} dt {Time.unscaledDeltaTime}\n" + HierarchyDump(path));
                options.Input.Apply(frame);
                if (frame == options.TestNativeCrashFrame) NativeCrashes.Test(options.TestNativeCrash!);
                PlayerLoop.RunFrame(dt);
                StartupStats.FrameEnd(frame);
                StartupProgress.FrameEnded(frame);
                if (options.LogPointerHits) PointerHitLog.Frame(frame);
                TestHang.Frame(frame);
                frame++;
                Throttle(clock, now, platform != null, frame <= options.MaxFrameRateFrames ? options.MaxFrameRate : 0);
            }

            // Unity's quit: OnApplicationQuit, then every object is destroyed, then the process
            // ends (threads the game started do not keep it alive).
            if (options.BuiltinReport != null)
                File.WriteAllLines(options.BuiltinReport, OniMoono.Engine.Assets.ObjectLoader.BuiltinLoads()
                    .Select(b => $"{b.File}\t{b.PathId}\t{b.ClassId}\t{b.Type}\t{b.Name}"));
            var quitClock = Stopwatch.StartNew();
            PlayerLoop.SendApplicationQuit();
            double quitEvents = quitClock.Elapsed.TotalMilliseconds;
            SceneLoader.UnloadAll();
            Lifecycle.ProcessDestroyQueue(drain: true);
            double unload = quitClock.Elapsed.TotalMilliseconds;
            // The graphics device and window go before the process exits, as in Unity's player
            // (a GL context left current into process exit can hang driver exit handlers).
            if (platform != null)
            {
                OniMoono.Engine.Rendering.GraphicsDevice.Install(new OniMoono.Engine.Rendering.NullRenderDevice());
                platform.Dispose();
            }
            GameTweaks.GameTweaks.Shutdown();
            double total = quitClock.Elapsed.TotalMilliseconds;
            EngineLog.Engine($"Quit took {total:0} ms: OnApplicationQuit {quitEvents:0}, unloading {unload - quitEvents:0}, device and window {total - unload:0}");
            OniMoono.Engine.UI.CanvasSystem.LogSkipTotals();
            EngineLog.Engine($"Exiting after {frame} frames with code {PlayerLoop.ExitCode}");
            EngineLog.Close();
            Console.Out.Flush();
            Console.Error.Flush();
            // The process ends here without running native libraries' exit-time teardown: game
            // threads may still be inside native plugins (worldgen settling in SimDLL), and the
            // plugins' static destructors would free state under them. Unity's player likewise
            // ends without waiting for or tearing down under foreign threads. The game registers
            // no ProcessExit handlers.
            ProcessExit(PlayerLoop.ExitCode);
            return PlayerLoop.ExitCode;
        }

        /// The window and its graphics device for the renderer option (Renderers.Open: Vulkan
        /// unless it cannot start or OpenGL was chosen).
        internal static (OniMoono.Desktop.GlfwPlatform, OniMoono.Engine.Rendering.RenderDevice) OpenDevice(Options options, OniMoono.Desktop.WindowSettings? settings = null)
        {
            var cache = string.IsNullOrEmpty(EngineConfig.TemporaryCachePath) ? null : Path.Combine(EngineConfig.TemporaryCachePath, "oni-moono");
            return OniMoono.Desktop.Renderers.Open(settings ?? WindowSettingsFor(options), options.Renderer ?? OniMoono.Desktop.Renderers.Auto,
                options.RendererSource is { Length: > 0 } s ? s : "default", options.Gpu ?? GpuChoice.Default, cache);
        }

        /// Diagnostics (`--dump-hierarchy`): the subtree at a path, one line per GameObject.
        static string HierarchyDump(string path)
        {
            var root = FindIncludingInactive(path);
            if (root == null) return $"hierarchy {path}: not found";
            var text = new System.Text.StringBuilder($"hierarchy {path}:");
            void Walk(Transform t, int depth)
            {
                var go = t.gameObject;
                var rect = (t is RectTransform rt ? $" rect {rt.rect} anchored {rt.anchoredPosition}" : "") + (t.localScale != Vector3.one ? $" scale {t.localScale}" : "") + $" world {t.position}";
                var comps = string.Join(",", go.GetComponents<Component>().Where(c => c != null && c is not Transform).Select(c => c is Behaviour b && !b.enabled ? c.GetType().Name + "(off)" : c.GetType().Name));
                text.Append($"\n{new string(' ', depth * 2)}{go.name} self={go.activeSelf} hier={go.activeInHierarchy}{rect} [{comps}]");
                for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
            }
            Walk(root.transform, 1);
            return text.ToString();
        }

        /// A GameObject by path, inactive objects included (GameObject.Find sees active ones
        /// only): the deepest active prefix by GameObject.Find, the rest by walking children. A
        /// `*` segment matches the first child that leads to a match.
        static GameObject? FindIncludingInactive(string path)
        {
            if (!path.Contains('*') && GameObject.Find(path) is { } direct) return direct;
            var parts = path.TrimStart('/').Split('/');
            static Transform? Descend(Transform t, ReadOnlySpan<string> rest)
            {
                if (rest.Length == 0) return t;
                for (int i = 0; i < t.childCount; i++)
                {
                    var child = t.GetChild(i);
                    if (rest[0] != "*" && child.name != rest[0]) continue;
                    var found = Descend(child, rest[1..]);
                    if (found != null) return found;
                }
                return null;
            }
            for (int n = parts.Length; n > 0; n--)
            {
                if (parts[..n].Contains("*")) continue;
                var found = GameObject.Find("/" + string.Join('/', parts[..n]));
                if (found != null) return Descend(found.transform, parts[n..])?.gameObject;
            }
            return null;
        }

        /// Diagnostics (`--dump-component`): a component's serialized fields (public or
        /// [SerializeField], not [NonSerialized]) and their current values.
        static string ComponentDump(string spec)
        {
            int bar = spec.LastIndexOf('|');
            var go = FindIncludingInactive(spec[..bar]);
            if (go == null) return $"component {spec}: GameObject not found";
            var component = go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == spec[(bar + 1)..]);
            if (component == null) return $"component {spec}: not found";
            var text = new System.Text.StringBuilder($"component {spec}:");
            for (var t = component.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(Component); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    bool serialized = (f.IsPublic || f.IsDefined(typeof(SerializeField), false)) && !f.IsDefined(typeof(NonSerializedAttribute), false);
                    if (!serialized) continue;
                    var v = f.GetValue(component);
                    var shown = v is LayerMask m ? $"LayerMask {m.value} (0x{m.value:X})" : v?.ToString() ?? "null";
                    text.Append($"\n  {t.Name}.{f.Name} = {shown}");
                }
            return text.ToString();
        }

        /// Ends the process at once, without static destructors or other exit handlers: _exit on
        /// Linux, TerminateProcess of the current process on Windows (ExitProcess would run the
        /// plugins' DLL detach code, under their own threads, as static destructors would).
        static void ProcessExit(int status)
        {
            NativeCrashes.Exiting(status);
            if (OperatingSystem.IsWindows()) TerminateProcess(GetCurrentProcess(), (uint)status);
            else LinuxExit(status);
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "_exit")]
        static extern void LinuxExit(int status);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool TerminateProcess(IntPtr process, uint exitCode);

        /// The player window from PlayerSettings (native resolution when so configured),
        /// overridden by Unity's -screen-* / -window-mode arguments.
        static OniMoono.Desktop.WindowSettings WindowSettingsFor(Options options)
        {
            var ps = ProjectSettings.PlayerSettings;
            bool native = ps.Bool("defaultIsNativeResolution");
            int width = native ? -1 : ps.Int("defaultScreenWidth");
            int height = native ? -1 : ps.Int("defaultScreenHeight");
            var mode = (FullScreenMode)ps.Int("fullscreenMode");
            if (options.ScreenWidth > 0) width = options.ScreenWidth;
            if (options.ScreenHeight > 0) height = options.ScreenHeight;
            if (options.ScreenFullscreen == 0) mode = FullScreenMode.Windowed;
            else if (options.ScreenFullscreen == 1 && mode == FullScreenMode.Windowed) mode = FullScreenMode.FullScreenWindow;
            mode = options.WindowMode switch
            {
                "exclusive" => FullScreenMode.ExclusiveFullScreen,
                "borderless" => FullScreenMode.FullScreenWindow,
                "windowed" => FullScreenMode.Windowed,
                _ => mode,
            };
            return new OniMoono.Desktop.WindowSettings(EngineConfig.ProductName, width, height, mode) { IgnoreUserInput = options.Input.Any || options.Background, DebugContext = options.GlDebug, Monitor = options.Monitor, NoFocus = options.NoFocus };
        }

        /// Frame pacing as in Unity's desktop player: with vSync the swap paces frames (the
        /// presenter's wait for the previous frame), otherwise Application.targetFrameRate when
        /// set, otherwise none. Without a display there is no swap; frames are paced at
        /// targetFrameRate or 60 Hz. --max-frame-rate bounds the rate in any case.
        static void Throttle(Stopwatch clock, double frameStart, bool display, int maxFrameRate)
        {
            int target = PlayerLoop.TargetFrameRate;
            if (display && QualitySettings.vSyncCount > 0) target = 0;
            else if (!display && target <= 0) target = 60;
            if (maxFrameRate > 0 && (target <= 0 || target > maxFrameRate)) target = maxFrameRate;
            if (target <= 0) return;
            double remaining = 1.0 / target - (clock.Elapsed.TotalSeconds - frameStart);
            if (remaining > 0.001) Thread.Sleep(TimeSpan.FromSeconds(remaining));
        }
    }

    /// [RuntimeInitializeOnLoadMethod] methods, as recorded at build time in
    /// RuntimeInitializeOnLoads.json, run by load phase.
    internal sealed class RuntimeInitializeHooks
    {
        readonly (int phase, string assembly, string type, string method)[] entries;

        RuntimeInitializeHooks((int, string, string, string)[] e) => entries = e;

        public static RuntimeInitializeHooks Load(string dataDir)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "RuntimeInitializeOnLoads.json")));
            var list = doc.RootElement.GetProperty("root"u8).EnumerateArray().Select(e =>
            {
                var ns = e.GetProperty("nameSpace"u8).GetString()!;
                var cls = e.GetProperty("className"u8).GetString()!;
                return (e.GetProperty("loadTypes"u8).GetInt32(), e.GetProperty("assemblyName"u8).GetString()!, ns.Length > 0 ? ns + "." + cls : cls, e.GetProperty("methodName"u8).GetString()!);
            }).ToArray();
            return new RuntimeInitializeHooks(list);
        }

        public void Run(RuntimeInitializeLoadType phase)
        {
            foreach (var (p, asmName, typeName, methodName) in entries)
            {
                if (p != (int)phase) continue;
                // Hooks of assemblies the player does not load (Unity's support packages) don't run.
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == asmName);
                if (asm == null) continue;
                var method = asm.GetType(typeName, true)!.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? throw new MissingMethodException(typeName, methodName);
                try
                {
                    method.Invoke(null, null);
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    UnityEngine.Debug.LogException(e.InnerException);
                }
            }
        }
    }
}
