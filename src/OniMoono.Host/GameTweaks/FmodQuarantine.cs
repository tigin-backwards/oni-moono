#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `fmod-quarantine` (on by default): FMOD allocates from a heap of its own with a
    /// quarantine for freed blocks, src/OniMoono.NativeHost/fmod_heap.c (docs/TWEAKS.md, "Why
    /// fmod-quarantine"). It works around, and reports, the write to freed memory FMOD's staff
    /// acknowledged in its stop/steal path (2.01.18 and later; the game ships 2.03.12), which on
    /// Windows corrupts the process heap that FMOD shares with the runtime and the drivers: the
    /// 0xC0000374 crashes on the FMOD Studio update thread of issues #72 and #58. A late write
    /// lands in a quarantined block instead, and when the block leaves the quarantine its fill is
    /// checked: each changed block is one `fmod heap:` line in Player.log.
    ///
    /// FMOD_Memory_Initialize must come before any FMOD system exists. The game's first system is
    /// FMOD.Studio.System.create in FMODUnity's RuntimeManager.Initialize, the same method in
    /// U59 and U60 (before it only RuntimeUtils.EnforceLibraryOrder's Memory.GetStats and
    /// Util.parseID, which create none): this prefix calls it first, through the game's own
    /// wrapper (so through its own library, fmodstudio or fmodstudioL), with delegates over the
    /// native functions, which marshal back to the native pointers themselves. FMOD calls them on
    /// its own threads, never into managed code. Off: nothing is patched or called, and FMOD
    /// allocates as it always has. A prefix that runs the method as it is: no fingerprint.
    ///
    /// The functions are OniMoono.exe's exports (Windows; built by build.cmd), or a
    /// libOniMoonoNative.so beside the host (Linux; none is built yet). Without them the tweak is
    /// off with a line saying so. ONI_FMOD_QUARANTINE_MB sets the quarantine's size (default 32;
    /// 0 keeps the private heap and the checks, without a quarantine).
    [GameMember(GameUse.Prefix, "FMOD.RESULT FMODUnity.RuntimeManager::Initialize()")]
    [GameMember(GameUse.Uses, "FMOD.RESULT FMOD.Memory::Initialize(System.IntPtr,System.Int32,FMOD.MEMORY_ALLOC_CALLBACK,FMOD.MEMORY_REALLOC_CALLBACK,FMOD.MEMORY_FREE_CALLBACK,FMOD.MEMORY_TYPE)")]
    internal static unsafe class FmodQuarantine
    {
        public const string Name = GameTweakNames.FmodQuarantine;
        public const int DefaultMegabytes = 32;
        const string LinuxLibrary = "libOniMoonoNative.so";

        /// fmod_heap.h's moono_fmod_heap_event.
        [StructLayout(LayoutKind.Sequential)]
        internal struct HeapEvent
        {
            public uint Kind, Type, Size, Changed;
            public long FirstOffset, LastOffset, TimeMs, AgeMs;
            public ulong Address;
            public uint Count, Reserved;
            public fixed int Offsets[8];
            public fixed byte Values[8];
            public fixed byte Expected[8];
        }

        /// fmod_heap.h's moono_fmod_heap_counters.
        [StructLayout(LayoutKind.Sequential)]
        internal struct Counters
        {
            public ulong LiveBytes, LiveBlocks, PeakLiveBytes;
            public ulong QuarantinedBytes, QuarantinedBlocks, PeakHeldBytes;
            public ulong Allocs, Frees, Reallocs, BytesAllocated, BytesFreed;
            public ulong CheckedBytes;
            public ulong Events, EventsDropped;
            public ulong QuarantineLimit;
        }

        static IntPtr allocAt, reallocAt, freeAt;
        static delegate* unmanaged<ulong, int> init;
        static delegate* unmanaged<Counters*, void> stats;
        static delegate* unmanaged<HeapEvent*, int, int> takeEvents;
        static Counters window;
        static bool initializing;
        // Kept with the allocator: FMOD holds their native pointers for the rest of the run.
        static FMOD.MEMORY_ALLOC_CALLBACK? alloc;
        static FMOD.MEMORY_REALLOC_CALLBACK? realloc;
        static FMOD.MEMORY_FREE_CALLBACK? free;

        /// For specs (`type:OniMoono.Host.GameTweaks.FmodQuarantine::Active`): FMOD allocates
        /// here; the problems logged so far.
        public static bool Active { get; private set; }
        public static int Logged { get; private set; }
        public static ulong Allocs => Read().Allocs;
        public static ulong Frees => Read().Frees;
        public static ulong LiveBytes => Read().LiveBytes;
        public static ulong QuarantinedBytes => Read().QuarantinedBytes;
        /// FMOD's channels playing now (sound still plays): 0 before FMOD starts.
        public static int ChannelsPlaying
        {
            get
            {
                if (!FMODUnity.RuntimeManager.IsInitialized) return 0;
                FMODUnity.RuntimeManager.CoreSystem.getChannelsPlaying(out int channels, out _);
                return channels;
            }
        }

        /// The prefix goes in whether or not the native functions are here (so the tweak checks
        /// see it as in play); without them it does nothing, as the log line says.
        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method("FMODUnity.RuntimeManager:Initialize") ?? throw new MissingMethodException("FMODUnity.RuntimeManager", "Initialize"),
                prefix: new HarmonyMethod(typeof(FmodQuarantine), nameof(Initializing)));
            var library = NativeLibrary.GetMainProgramHandle();
            if (!NativeLibrary.TryGetExport(library, "moono_fmod_alloc", out _)
                && !(OperatingSystem.IsLinux() && NativeLibrary.TryLoad(System.IO.Path.Combine(AppContext.BaseDirectory, LinuxLibrary), out library)))
            {
                EngineLog.Engine($"{Name}: not active, " + (OperatingSystem.IsWindows()
                    ? "this OniMoono.exe is the .NET SDK's apphost, without Moono's allocator; FMOD allocates as it always has"
                    : $"no {LinuxLibrary} beside the host (none is built for Linux yet); FMOD allocates as it always has"));
                return;
            }
            init = (delegate* unmanaged<ulong, int>)Export(library, "moono_fmod_heap_init");
            stats = (delegate* unmanaged<Counters*, void>)Export(library, "moono_fmod_heap_stats");
            takeEvents = (delegate* unmanaged<HeapEvent*, int, int>)Export(library, "moono_fmod_heap_events");
            allocAt = Export(library, "moono_fmod_alloc");
            reallocAt = Export(library, "moono_fmod_realloc");
            freeAt = Export(library, "moono_fmod_free");
        }

        static IntPtr Export(IntPtr library, string name) =>
            NativeLibrary.TryGetExport(library, name, out var at) ? at : throw new EntryPointNotFoundException(name);

        /// The quarantine's size: ONI_FMOD_QUARANTINE_MB, else DefaultMegabytes.
        internal static ulong QuarantineBytes(string? megabytes) =>
            (ulong)(int.TryParse(megabytes, out var mb) && mb >= 0 && mb <= 4096 ? mb : DefaultMegabytes) << 20;

        /// RuntimeManager.Initialize's prefix: once, before its FMOD.Studio.System.create.
        static void Initializing()
        {
            if (initializing || init == null) return;
            initializing = true;
            ulong bytes = QuarantineBytes(Environment.GetEnvironmentVariable("ONI_FMOD_QUARANTINE_MB"));
            if (init(bytes) == 0)
            {
                EngineLog.Engine($"{Name}: its heap could not be made; FMOD allocates as it always has");
                return;
            }
            alloc = Marshal.GetDelegateForFunctionPointer<FMOD.MEMORY_ALLOC_CALLBACK>(allocAt);
            realloc = Marshal.GetDelegateForFunctionPointer<FMOD.MEMORY_REALLOC_CALLBACK>(reallocAt);
            free = Marshal.GetDelegateForFunctionPointer<FMOD.MEMORY_FREE_CALLBACK>(freeAt);
            var result = FMOD.Memory.Initialize(IntPtr.Zero, 0, alloc, realloc, free, FMOD.MEMORY_TYPE.ALL);
            if (result != FMOD.RESULT.OK)
            {
                EngineLog.Engine($"{Name}: FMOD refused the allocator ({result}); FMOD allocates as it always has");
                return;
            }
            Active = true;
            PlayerLoop.EarlyUpdate += Drain;
            FrameStatsWindow.Reports.Add(Report);
            EngineLog.Engine($"{Name}: FMOD allocates from Moono's heap{(OperatingSystem.IsWindows() ? " (a private one)" : "")}, freed blocks quarantined up to {bytes >> 20} MiB and checked");
        }

        static Counters Read()
        {
            Counters c = default;
            if (stats != null) stats(&c);
            return c;
        }

        /// Each frame: the problems found since the last, one line each.
        static void Drain()
        {
            HeapEvent* found = stackalloc HeapEvent[8];
            int n;
            while ((n = takeEvents(found, 8)) > 0)
                for (int i = 0; i < n; i++)
                {
                    Logged++;
                    EngineLog.Engine(Describe(found[i]));
                }
        }

        static readonly string[] kinds = ["", "write after free", "double free", "overrun", "free of a pointer it never returned", "damaged block header"];

        /// One problem as its log line: what, the block (size, FMOD's memory type, address),
        /// the changed bytes (offset from the pointer FMOD was given: value, and what it should
        /// be), how long the block had been free, and when.
        internal static string Describe(in HeapEvent e)
        {
            var line = new StringBuilder("fmod heap: ");
            line.Append(e.Kind < kinds.Length ? kinds[e.Kind] : $"problem {e.Kind}");
            if (e.Kind != 4) line.Append($", a {e.Size}-byte block (type 0x{e.Type:x})");
            line.Append($" at 0x{e.Address:x}");
            if (e.Changed > 0)
            {
                line.Append($": {e.Changed} byte{(e.Changed == 1 ? "" : "s")} changed at offsets {e.FirstOffset}..{e.LastOffset} (");
                for (int i = 0; i < e.Count && i < 8; i++)
                    line.Append(i > 0 ? ", " : "").Append($"{e.Offsets[i]:+0;-0}: {e.Values[i]:x2} for {e.Expected[i]:x2}");
                line.Append(e.Changed > e.Count ? ", …)" : ")");
            }
            if (e.Kind == 1) line.Append($", {e.AgeMs} ms after it was freed");
            line.Append($"; found {DateTimeOffset.FromUnixTimeMilliseconds(e.TimeMs).UtcDateTime:yyyy-MM-dd HH:mm:ss.fff}Z");
            return line.ToString();
        }

        /// The frame-stats window's line: what FMOD holds and how fast it allocates.
        static string[] Report(double seconds, int frames)
        {
            var now = Read();
            var was = window;
            window = now;
            double perSecond(ulong a, ulong b) => seconds > 0 ? (a - b) / seconds : 0;
            return [$"fmod heap: live {Mib(now.LiveBytes)} MiB in {now.LiveBlocks:N0} blocks (peak {Mib(now.PeakLiveBytes)}), quarantined {Mib(now.QuarantinedBytes)} of {Mib(now.QuarantineLimit)} MiB in {now.QuarantinedBlocks:N0}, held at most {Mib(now.PeakHeldBytes)} MiB; "
                + $"{perSecond(now.Allocs, was.Allocs):N0} allocs/s ({Mib((ulong)perSecond(now.BytesAllocated, was.BytesAllocated))} MiB/s), {perSecond(now.Frees, was.Frees):N0} frees/s, {perSecond(now.Reallocs, was.Reallocs):N0} reallocs/s; "
                + $"{now.Events} problem{(now.Events == 1 ? "" : "s")} found{(now.EventsDropped > 0 ? $" ({now.EventsDropped} not logged)" : "")}"];
        }

        static string Mib(ulong bytes) => (bytes / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        /// The end of the run (GameTweaks.Shutdown): the problems not yet logged, and the totals.
        public static void Shutdown()
        {
            if (!Active) return;
            Drain();
            var c = Read();
            EngineLog.Engine($"{Name}: {c.Allocs:N0} allocs, {c.Frees:N0} frees, {c.Reallocs:N0} reallocs ({Mib(c.BytesAllocated)} MiB allocated), live at most {Mib(c.PeakLiveBytes)} MiB, held at most {Mib(c.PeakHeldBytes)} MiB; {Mib(c.CheckedBytes)} MiB checked on leaving the quarantine; {c.Events} problem{(c.Events == 1 ? "" : "s")} found");
        }
    }
}
