#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace OniMoono.Host
{
    internal sealed class Options
    {
        public string GameDir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "OxygenNotIncluded")
            : OperatingSystem.IsMacOS()
            ? OniMoono.Engine.Platform.GameLayout.DefaultMacSteamInstall(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux");
        /// Test runs (`--run-home DIR`): the player's per-user data (Unity's persistent and cache
        /// paths, and on Windows the Documents and local application data folders the game reads)
        /// under DIR instead of the user's (RunHome).
        public string? RunHome;
        /// Test runs (`--audio-wav DIR`): the game's audio written to DIR/fmodoutput.wav instead of
        /// the sound device (AudioCapture).
        public string? AudioWav;
        public string? LogPath;
        public bool Headless;
        public bool BatchMode;
        public bool CheckTypes;
        /// `--check-tweaks check|review|declare`: the game tweaks against the build at --game,
        /// without starting it (TweakCheckTool); `--tweak-listings DIR`: review's bodies to DIR.
        public string? CheckTweaks;
        public string? TweakListings;
        /// `--game-tweak` values, in order (GameTweaks, EffectiveOptions): when any is given, they
        /// alone choose the game tweaks, over the Moono Tweaks settings.
        public readonly List<string> GameTweaks = new();
        public bool CheckAssets;
        /// With --check-assets: where to list the script classes the data uses.
        public string? ListDataScripts;
        public bool CheckBind;
        public bool CheckShaders;
        /// --check-natives: the P/Invokes against the install's plugins (NativeCheck).
        public bool CheckNatives;
        /// With --check-natives: the architecture checked (`--native-arch x86_64|arm64`; the
        /// process's by default) and a host folder whose natives are checked (`--native-dir`).
        public int? NativeArch;
        public string? NativeDir;
        public string? DumpShader;
        public string? DumpScript;
        public int Frames = -1;
        /// --max-frame-rate N[:FRAMES]: an upper bound on the frame rate whatever the game asks
        /// for (scripted runs whose schedules count frames), for the first FRAMES frames when
        /// given; 0 for none.
        public int MaxFrameRate, MaxFrameRateFrames = int.MaxValue;
        /// Diagnostics: log every first-chance exception with IL offsets per frame (game code
        /// has no symbols, so offsets locate the throwing instruction).
        public bool TraceExceptions;
        /// Diagnostics (--gl-debug): the driver's GL debug output in the log (debug context).
        public bool GlDebug;
        /// --background: the window takes no focus and the desktop's mouse and keyboard reach
        /// nothing (test runs on a desktop someone is using). The player counts as focused, with
        /// the pointer over the window at (0, 0), so the game hovers there every frame: in U57 at
        /// 1280x720 that cost EventSystem.Update about 1.3 ms a frame and the frame rate about a
        /// quarter. Frame-rate runs use --no-focus instead.
        public bool Background;
        /// --no-focus: the window opens without taking the focus, and takes input once focused
        /// (Windows test runs on the user's second monitor: scripts/run-windows.sh).
        public bool NoFocus;
        /// --splash moo|none: the player's own loading splash (Gassy Moos falling while it loads),
        /// over the Moono Tweaks settings; null when not given (then the effective value).
        public string? Splash;
        /// --frame-stats: average frame phase times in the log (PlayerLoop.FrameStats), over the
        /// Moono Tweaks settings.
        public bool FrameStats;
        /// --log-paths hidden|full: personal paths in Player.log and the console replaced, or as
        /// logged (LogScrubber), over the Moono Tweaks settings; null when not given.
        public string? LogPaths;
        /// --gpu default|discrete|integrated: the GPU of a laptop with two (GpuChoice), over the
        /// Moono Tweaks settings; null when not given.
        public string? Gpu;
        /// --renderer auto|vulkan|opengl: the graphics API (OniMoono.Desktop.Renderers), over the
        /// Moono Tweaks settings; null when not given. After the options resolve, the effective
        /// choice and where it came from (RendererSource).
        public string? Renderer;
        public string RendererSource = "";
        /// Diagnostics (--vulkan-validation, or ONI_VULKAN_VALIDATION=1): the Khronos validation
        /// layer on the Vulkan device, its messages in the log.
        public bool VulkanValidation;
        /// --dxgi on|off: Windows, presenting through DXGI (OniMoono.Desktop.Gl.DxgiTarget), over
        /// the Moono Tweaks settings; null when not given (then the effective value).
        public bool? Dxgi;
        /// --stall-repeats on|off: Windows, DXGI: the last picture presented again while no
        /// frame comes (OniMoono.Desktop.Gl.StallRepeats), over the Moono Tweaks settings; null
        /// when not given (then the effective value).
        public bool? StallRepeats;
        /// --gc fast|low-memory: the garbage collection (GcMode), over the Moono Tweaks settings;
        /// null when not given. OniMoono.exe reads it too, before the runtime starts.
        public string? Gc;
        /// Diagnostics (--dxgi-fault WHAT): a made-up DXGI failure (DxgiSwapChain.Fault).
        public string? DxgiFault;
        /// --crash-dumps on|off|full: a minidump beside Player.log when the player crashes in
        /// native code (Windows; NativeCrashes), the two newest kept. On by default; `full` adds
        /// the process's memory (diagnostics: as large as the game's private memory).
        /// ONI_CRASH_DUMPS=on|off|full, for test runs, takes the place of the option.
        public string CrashDumps = "on";
        /// --crash-dialog on|off: the crash window after a crash (Windows; NativeCrashes,
        /// crash_dialog.cpp). On by default, off for a run with --run-home (test runs, which no
        /// window may wait on); ONI_MOONO_CRASH_DIALOG=on|off|inactive takes the place of both.
        public string? CrashDialog;
        /// Diagnostics (--test-native-crash WHERE[:FRAME]): a crash on purpose once the log is
        /// open, or at the start of frame FRAME on the main thread (NativeCrashes.Test).
        public string? TestNativeCrash;
        public int TestNativeCrashFrame = -1;
        /// --no-tweaks-menu: the main menu without our Moono Tweaks button, as the original's (test
        /// runs compared with the original game: scripts/e2e.py).
        public bool NoTweaksMenu;
        /// Diagnostics (--log-pointer-hits): the UI element under the mouse, logged when it changes.
        public bool LogPointerHits;
        /// Diagnostics (--vsync-count N): the swap interval regardless of the quality settings.
        public int VSyncCount = -1;
        /// Diagnostics (--gpu-profile): GPU time by operation with the frame counters.
        public bool GpuProfile;
        /// Diagnostics (--no-bindless): textures bound to units even where bindless textures exist.
        public bool NoBindless;
        /// --bindless: bindless textures even on AMD's drivers, which bind to units by default.
        public bool Bindless;
        public readonly ScriptedInput Input = new();
        // Unity's standalone player arguments.
        public int ScreenWidth = -1, ScreenHeight = -1;
        public int ScreenFullscreen = -1;
        public int Monitor;
        public string? WindowMode;
        /// Unity's -logFile PATH: the log's file when --log names none.
        public string? UnityLogFile;
        /// Unity arguments ignored or malformed, for the log once it is open.
        public readonly List<string> ArgumentNotes = new();
        /// Development: "frame:path.png[:superSize]" calls ScreenCapture.CaptureScreenshot in
        /// that frame.
        public readonly List<(int Frame, string Path, int Scale)> Captures = new();
        /// --probe-ui FRAME:X,Y: logs the canvas elements drawn at a window point (top-left
        /// origin, as --input) as of that frame.
        public readonly List<(int Frame, float X, float Y)> Probes = new();
        /// --dump-hierarchy FRAME:PATH: logs the subtree at a GameObject path (GameObject.Find
        /// syntax) as of that frame: active flags, rect transforms, components.
        public readonly List<(int Frame, string Path)> HierarchyDumps = new();
        /// --report-builtin-assets FILE: at exit, the objects the run loaded from Unity's built-in
        /// resource files (tab-separated).
        public string? BuiltinReport;
        /// --dump-component FRAME:PATH|TYPE: logs the serialized fields of a component (by type
        /// name) on the GameObject at PATH as of that frame.
        public readonly List<(int Frame, string Spec)> ComponentDumps = new();
        /// --dump-ui FRAME:PATH: writes every active UI element (rects, canvas renderer output,
        /// package component state) as of that frame (PLAN M9's differential oracle).
        public readonly List<(int Frame, string Path)> UiDumps = new();
        /// --native-override NAME=PATH: a native library loaded from PATH (AssemblyPolicy).
        public readonly Dictionary<string, string> NativeOverrides = new(StringComparer.Ordinal);
        /// The game's command line (GameCommandLine, #53): what the original player's
        /// Environment.GetCommandLineArgs() gives after the executable, every argument it was
        /// started with. In the Steam form every argument after the game's executable, which is
        /// GameExecutable; before it, and in the direct form, Unity's player arguments and stray
        /// words among ours (with the values Unity documents for them) and each --game-arg value,
        /// never our own `--` options. In command-line order.
        public readonly List<string> GameArguments = new();
        /// The game's executable Steam's command names (`-- %command%`); null in the direct form.
        public string? GameExecutable;

        public static Options? Parse(string[] args)
        {
            var o = new Options();
            int i = 0;
            try
            {
                if (!ParseInto(o, args, ref i)) return null;
            }
            catch (Exception e) when (e is FormatException or OverflowException or IndexOutOfRangeException or ArgumentException)
            {
                // One of ours with a malformed or missing value: said, as an unknown one is.
                int at = Math.Min(i, args.Length - 1);
                while (at > 0 && !args[at].StartsWith("--", StringComparison.Ordinal)) at--;
                Console.Error.WriteLine(e is IndexOutOfRangeException ? $"Option {args[at]} has no value" : $"Option {args[at]} {args[Math.Min(i, args.Length - 1)]}: {e.Message}");
                return null;
            }
            // --log wins over Unity's -logFile.
            o.LogPath ??= o.UnityLogFile;
            return o;
        }

        static bool ParseInto(Options o, string[] args, ref int i)
        {
            // In the Steam form, the first argument after the game's executable: from there every
            // argument is the game's.
            int gameFrom = args.Length;
            for (; i < args.Length; i++)
            {
                int at = i;
                switch (args[i])
                {
                    case "--game": o.GameDir = args[++i]; break;
                    case "--" when at < gameFrom:
                    {
                        // Steam launch options `OniMoono [options] -- %command%`: Steam's command
                        // for the game, any wrappers of Steam's (launch wrapper, reaper) followed by
                        // the game's executable and its arguments. The game directory is the
                        // executable's. The arguments after it are the game's, every one, as the
                        // original player passes them on; they are read as the player's too
                        // (Unity's -screen-width and the like, and ours but --game-arg).
                        int exe = Array.FindIndex(args, i + 1, a => OniMoono.Engine.Platform.GameLayout.GameDirOfExecutable(a) != null);
                        if (exe < 0)
                        {
                            Console.Error.WriteLine("Moono: no OxygenNotIncluded executable after --; the Steam launch options should end with -- %command%");
                            return false;
                        }
                        o.GameDir = OniMoono.Engine.Platform.GameLayout.GameDirOfExecutable(args[exe])!;
                        o.GameExecutable = args[exe];
                        gameFrom = exe + 1;
                        o.GameArguments.AddRange(args[gameFrom..]);
                        i = exe;
                        break;
                    }
                    case "--log": o.LogPath = args[++i]; break;
                    // --game-arg ARG: an argument for the game itself (GameArguments), any argument
                    // (one of ours too, or a `--` option the game or a mod reads).
                    case "--game-arg" when at < gameFrom: o.GameArguments.Add(args[++i]); break;
                    case "--run-home": o.RunHome = Path.GetFullPath(args[++i]); break;
                    case "--audio-wav": o.AudioWav = Path.GetFullPath(args[++i]); break;
                    case "--headless": o.Headless = true; break;
                    case "--batchmode": o.BatchMode = true; break;
                    case "--check-types": o.CheckTypes = true; break;
                    case "--check-tweaks":
                        o.CheckTweaks = args[++i];
                        if (o.CheckTweaks is not ("check" or "review" or "declare"))
                        {
                            Console.Error.WriteLine($"--check-tweaks takes check, review or declare, not {o.CheckTweaks}");
                            return false;
                        }
                        break;
                    case "--tweak-listings": o.TweakListings = Path.GetFullPath(args[++i]); break;
                    case "--check-assets": o.CheckAssets = true; break;
                    case "--list-data-scripts": o.ListDataScripts = args[++i]; break;
                    case "--game-tweak":
                    {
                        var name = args[++i];
                        if (OniMoono.Host.GameTweaks.GameTweakNames.IsRetired(name, out var replacement))
                        {
                            if (replacement == null)
                            {
                                Console.Error.WriteLine($"Game tweak {name} is no longer part of Moono: the game fixes it now; ignored");
                                break;
                            }
                            Console.Error.WriteLine($"Game tweak {name} is replaced by {replacement}; using that");
                            name = replacement;
                        }
                        if (!OniMoono.Host.GameTweaks.GameTweakNames.IsKnown(name))
                        {
                            Console.Error.WriteLine($"Unknown game tweak {name}; known: all, none, exact (all but {string.Join(", ", OniMoono.Host.GameTweaks.GameTweakNames.ChangingBehaviour)}), {string.Join(", ", OniMoono.Host.GameTweaks.GameTweakNames.All)}");
                            return false;
                        }
                        o.GameTweaks.Add(name);
                        break;
                    }
                    case "--check-bind": o.CheckBind = true; break;
                    case "--check-shaders": o.CheckShaders = true; break;
                    case "--check-natives": o.CheckNatives = true; break;
                    case "--native-arch": o.NativeArch = OniMoono.Engine.Platform.NativeExports.Cpu(args[++i]) ?? throw new ArgumentException("--native-arch expects x86_64 or arm64"); break;
                    case "--native-dir": o.NativeDir = Path.GetFullPath(args[++i]); break;
                    case "--dump-shader": o.DumpShader = args[++i]; break;
                    case "--dump-script": o.DumpScript = args[++i]; break;
                    case "--frames": o.Frames = int.Parse(args[++i]); break;
                    case "--trace-exceptions": o.TraceExceptions = true; break;
                    case "--gl-debug": o.GlDebug = true; break;
                    case "--background": o.Background = true; break;
                    case "--no-focus": o.NoFocus = true; break;
                    case "--splash":
                        o.Splash = args[++i];
                        if (!PlayerSettings.IsSplash(o.Splash))
                        {
                            Console.Error.WriteLine($"Unknown splash {o.Splash}; known: moo, none");
                            return false;
                        }
                        break;
                    case "--log-paths":
                        o.LogPaths = args[++i];
                        if (!PlayerSettings.IsLogPaths(o.LogPaths))
                        {
                            Console.Error.WriteLine($"Unknown log paths {o.LogPaths}; known: hidden, full");
                            return false;
                        }
                        break;
                    case "--log-pointer-hits": o.LogPointerHits = true; break;
                    case "--vsync-count": o.VSyncCount = int.Parse(args[++i]); break;
                    case "--gpu-profile": o.GpuProfile = true; break;
                    case "--no-bindless": o.NoBindless = true; break;
                    case "--bindless": o.Bindless = true; break;
                    case "--input": o.Input.Add(args[++i]); break;
                    // --input-file PATH: --input steps, one a line (scripts too long for a command line).
                    case "--input-file":
                        foreach (var line in File.ReadAllLines(args[++i]))
                            if (line.Trim() is { Length: > 0 } step && !step.StartsWith('#')) o.Input.Add(step);
                        break;
                    case "--frame-stats": o.FrameStats = true; break;
                    case "--gpu":
                        o.Gpu = args[++i];
                        if (!GpuChoice.IsChoice(o.Gpu))
                        {
                            Console.Error.WriteLine($"Unknown gpu {o.Gpu}; known: {string.Join(", ", GpuChoice.Choices)}");
                            return false;
                        }
                        break;
                    case "--renderer":
                        o.Renderer = args[++i];
                        if (!OniMoono.Desktop.Renderers.IsChoice(o.Renderer))
                        {
                            Console.Error.WriteLine($"Unknown renderer {o.Renderer}; known: {string.Join(", ", OniMoono.Desktop.Renderers.Choices)}");
                            return false;
                        }
                        break;
                    case "--vulkan-validation": o.VulkanValidation = true; break;
                    case "--dxgi":
                        {
                            var value = args[++i];
                            if (value is not ("on" or "off"))
                            {
                                Console.Error.WriteLine($"Unknown dxgi {value}; known: on, off");
                                return false;
                            }
                            o.Dxgi = value == "on";
                            break;
                        }
                    case "--stall-repeats":
                        {
                            var value = args[++i];
                            if (value is not ("on" or "off"))
                            {
                                Console.Error.WriteLine($"Unknown stall-repeats {value}; known: on, off");
                                return false;
                            }
                            o.StallRepeats = value == "on";
                            break;
                        }
                    case "--gc":
                        o.Gc = args[++i];
                        if (!GcMode.IsChoice(o.Gc))
                        {
                            Console.Error.WriteLine($"Unknown gc {o.Gc}; known: {string.Join(", ", GcMode.Choices)}");
                            return false;
                        }
                        break;
                    case "--dxgi-fault":
                        o.DxgiFault = args[++i];
                        if (DxgiFaultOption.Unknown(o.DxgiFault) is { } fault)
                        {
                            Console.Error.WriteLine($"Unknown dxgi fault {fault}; known: {DxgiFaultOption.Known}");
                            return false;
                        }
                        break;
                    case "--crash-dumps":
                        {
                            var value = args[++i];
                            if (value is not ("on" or "off" or "full"))
                            {
                                Console.Error.WriteLine($"Unknown crash dumps {value}; known: on, off, full");
                                return false;
                            }
                            o.CrashDumps = value;
                            break;
                        }
                    case "--crash-dialog":
                        {
                            var value = args[++i];
                            if (value is not ("on" or "off"))
                            {
                                Console.Error.WriteLine($"Unknown crash dialog {value}; known: on, off");
                                return false;
                            }
                            o.CrashDialog = value;
                            break;
                        }
                    case "--test-native-crash":
                        o.TestNativeCrash = args[++i];
                        if (o.TestNativeCrash.IndexOf(':') is int frameAt and > 0)
                        {
                            o.TestNativeCrashFrame = int.Parse(o.TestNativeCrash[(frameAt + 1)..]);
                            o.TestNativeCrash = o.TestNativeCrash[..frameAt];
                        }
                        if (!NativeCrashTestOption.IsChoice(o.TestNativeCrash))
                        {
                            Console.Error.WriteLine($"Unknown test native crash {o.TestNativeCrash}; known: {NativeCrashTestOption.Known}");
                            return false;
                        }
                        break;
                    case "--no-tweaks-menu": o.NoTweaksMenu = true; break;
                    case "--max-frame-rate":
                        {
                            var spec = args[++i].Split(':');
                            o.MaxFrameRate = int.Parse(spec[0]);
                            if (spec.Length > 1) o.MaxFrameRateFrames = int.Parse(spec[1]);
                            break;
                        }
                    case "--dump-render-textures":
                        {
                            var spec = args[++i];
                            int colon = spec.IndexOf(':');
                            OniMoono.Engine.Rendering.FrameRenderer.DumpFrame = int.Parse(spec[..colon]);
                            OniMoono.Engine.Rendering.FrameRenderer.DumpDirectory = Path.GetFullPath(spec[(colon + 1)..]);
                            break;
                        }
                    case "--native-override":
                        {
                            var spec = args[++i];
                            int eq = spec.IndexOf('=');
                            if (eq <= 0) throw new ArgumentException("--native-override expects NAME=PATH");
                            o.NativeOverrides[spec[..eq]] = Path.GetFullPath(spec[(eq + 1)..]);
                            break;
                        }
                    // Unity's player arguments (UnityArguments): what is wrong with one is noted in
                    // the log, and the setting keeps its default.
                    case "-screen-width": o.ScreenWidth = UnityArguments.Int(args, ref i, o.ArgumentNotes, min: 1) ?? o.ScreenWidth; break;
                    case "-screen-height": o.ScreenHeight = UnityArguments.Int(args, ref i, o.ArgumentNotes, min: 1) ?? o.ScreenHeight; break;
                    case "-screen-fullscreen": o.ScreenFullscreen = UnityArguments.Int(args, ref i, o.ArgumentNotes, min: 0, max: 1) ?? o.ScreenFullscreen; break;
                    case "-monitor": o.Monitor = UnityArguments.Int(args, ref i, o.ArgumentNotes, min: 0) ?? o.Monitor; break;
                    case "-window-mode":
                    {
                        // Unity's are exclusive and borderless (Windows only); windowed is ours.
                        if (UnityArguments.Value(args, ref i, o.ArgumentNotes) is not { } value) break;
                        if (value is "exclusive" or "borderless" or "windowed") o.WindowMode = value;
                        else o.ArgumentNotes.Add($"-window-mode {value}: not exclusive, borderless or windowed; ignored");
                        break;
                    }
                    case "-logFile":
                    {
                        // Unity's "-" (the console) is not a file: the log stays where it was.
                        if (UnityArguments.Value(args, ref i, o.ArgumentNotes) is not { } value) break;
                        if (value == "-") o.ArgumentNotes.Add("-logFile -: the log goes to its file, not the console");
                        else o.UnityLogFile = value;
                        break;
                    }
                    case "--dump-component":
                    {
                        var spec = args[++i];
                        int colon = spec.IndexOf(':');
                        o.ComponentDumps.Add((int.Parse(spec[..colon]), spec[(colon + 1)..]));
                        break;
                    }
                    case "--report-builtin-assets": o.BuiltinReport = args[++i]; break;
                    case "--dump-hierarchy":
                    {
                        var spec = args[++i];
                        int colon = spec.IndexOf(':');
                        o.HierarchyDumps.Add((int.Parse(spec[..colon]), spec[(colon + 1)..]));
                        break;
                    }
                    case "--dump-ui-vertices": OniMoono.Engine.UI.UiDump.VerticesFilter = args[++i]; break;
                    case "--dump-ui":
                    {
                        var spec = args[++i];
                        int colon = spec.IndexOf(':');
                        o.UiDumps.Add((int.Parse(spec[..colon]), spec[(colon + 1)..]));
                        break;
                    }
                    case "--probe-ui":
                    {
                        var parts = args[++i].Split(':', ',');
                        o.Probes.Add((int.Parse(parts[0]), float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                    }
                    case "--capture":
                    {
                        // FRAME:PATH[:SCALE]; the path may hold a colon itself (a Windows drive).
                        var spec = args[++i];
                        int colon = spec.IndexOf(':');
                        var path = spec[(colon + 1)..];
                        int scale = 1;
                        int last = path.LastIndexOf(':');
                        if (last > 0 && int.TryParse(path[(last + 1)..], out int s)) { scale = s; path = path[..last]; }
                        o.Captures.Add((int.Parse(spec[..colon]), path, scale));
                        break;
                    }
                    // Unity's renderer choice (-force-glcore, -force-opengl, -force-vulkan): as
                    // --renderer; the later of the two wins.
                    case var force when UnityArguments.Renderer(force) is { } renderer:
                        o.Renderer = renderer;
                        break;
                    // Another of Unity's (-popupwindow, -force-d3d11, -screen-quality…) or a stray
                    // word: ignored, as Unity's player ignores what it does not know. Ours are strict.
                    case var unitys when UnityArguments.IsUnitys(unitys):
                        UnityArguments.Ignore(args, ref i, o.ArgumentNotes);
                        break;
                    // An unknown `--` option after the game's executable: the game's or a mod's.
                    case var _ when at >= gameFrom:
                        o.ArgumentNotes.Add($"{args[i]}: not read by this player; passed to the game");
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown option {args[i]}");
                        Console.Error.WriteLine("usage: OniMoono [--game DIR] [--log FILE] [--run-home DIR] [--audio-wav DIR] [--headless] [--batchmode] [--frames N] [--native-override NAME=PATH] [--trace-exceptions] [--gl-debug] [--background] [--no-focus] [--splash moo|none] [--log-pointer-hits] [--vsync-count N] [--gpu-profile] [--no-bindless] [--bindless] [--input FRAME:ACTION[:ARGS]] [--input-file PATH] [--frame-stats] [--log-paths hidden|full] [--gpu default|discrete|integrated] [--renderer auto|vulkan|opengl] [--vulkan-validation] [--dxgi on|off] [--stall-repeats on|off] [--gc fast|low-memory] [--dxgi-fault WHAT] [--crash-dumps on|off|full] [--crash-dialog on|off] [--test-native-crash WHERE[:FRAME]] [--no-tweaks-menu] [--max-frame-rate N[:FRAMES]] [--dump-render-textures FRAME:DIR] [-screen-width W] [-screen-height H] [-screen-fullscreen 0|1] [-window-mode exclusive|borderless|windowed] [-monitor N] [-logFile PATH] [other Unity player arguments, passed to the game] [--check-types] [--check-tweaks check|review|declare [--tweak-listings DIR]] [--game-tweak NAME|-NAME|all|exact|none] [--check-assets [--list-data-scripts FILE]] [--check-bind] [--check-shaders] [--check-natives [--native-arch x86_64|arm64] [--native-dir DIR]] [--dump-shader NAME:DIR] [--capture FRAME:PATH[:SCALE]] [--probe-ui FRAME:X,Y] [--dump-ui FRAME:PATH] [--dump-ui-vertices PATH-SUBSTRING] [--dump-hierarchy FRAME:PATH] [--report-builtin-assets FILE] [--dump-component FRAME:PATH|TYPE] [--game-arg ARG] [-- STEAM-COMMAND [ARG...]]");
                        return false;
                }
                // Unity's player arguments and stray words among ours, with their values: the
                // game's too, as the original player passes them on.
                if (at < gameFrom && UnityArguments.IsUnitys(args[at])) o.GameArguments.AddRange(args[at..(i + 1)]);
            }
            return true;
        }
    }
}
