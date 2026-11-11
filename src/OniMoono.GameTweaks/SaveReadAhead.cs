using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `save-read-ahead`: a save is read and decompressed on a thread of its own from
    /// the moment it is picked, and the load takes the result instead of doing that work itself.
    ///
    /// A load reads the whole file (`File.ReadAllBytes` in SaveLoader.Load(string)), parses its
    /// header and type directory, copies the compressed body out and decompresses it
    /// (SaveLoader.DecompressContents), all on the main thread in the frame that loads the colony:
    /// 0.5 s of a mid-game colony's load and 2.6 s of a late-game one's on Linux
    /// (docs/research/save-load-and-loading-screen.md). The work starts:
    ///
    /// - when a save is selected on the Load screen (LoadScreen.UpdateSelected, also when a
    ///   colony's view opens on its newest save), 0.2 s after the selection settles;
    /// - at once when it is loaded: the Load button (LoadScreen.Load), LoadScreen.DoLoad (the
    ///   Load screen's path, and the game's own reloads), the main menu's Resume
    ///   (MainMenu.ResumeGame) and SaveLoader.LoadScene, whichever comes first. Started at the
    ///   click, it runs beside the overlay's frames, the teardown and the scene switch.
    ///
    /// The thread reads the file (its size and write time before and after, unchanged), finds
    /// where the compressed body starts by skipping the header and the type directory, and
    /// decompresses a copy of the body with the game's own SaveLoader.DecompressContents, so
    /// whatever decompresses the game's loads (`fast-save-compression`'s zlib, or the game's
    /// Ionic.Zlib) decompresses this too. The load then takes it:
    ///
    /// - its `File.ReadAllBytes` (a transpiler on SaveLoader.Load(string)) gets the bytes read
    ///   when the path is the same and the file's size and write time still are; otherwise, or
    ///   while the thread has not read it yet and failed, the file is read as before;
    /// - its DecompressContents (a prefix) gets the decompressed body when the compressed bytes
    ///   it is given are the bytes after the offset found, compared whole; otherwise the game's
    ///   decompression runs as before. A decompression that threw on the thread leaves the
    ///   game's to throw as it would.
    ///
    /// Memory: one save at most is kept (a new selection drops the last), dropped 30 s after it
    /// is ready unless the load has started, when the Load screen closes without a load, and
    /// once the load has taken it or 120 s after the load started. A selection superseded while
    /// its decompression runs is dropped when that ends.
    ///
    /// Mods: steps aside (the game's own read and decompression) while another Harmony owner
    /// patches DecompressContents in any way (the thread would run its patch off the main thread,
    /// or its postfix twice), or transpiles SaveLoader.Load(string) or patches it with a prefix
    /// that can skip it (either can replace the read). Postfixes and prefixes that return nothing
    /// on Load(string) are left alone: many mods note the load there.
    ///
    /// With ONI_TWEAK_VERIFY=1 the load also reads the file and runs the game's decompression
    /// itself, and compares both with the result taken, byte for byte.
    [GameMember(GameUse.Postfix, "System.Void LoadScreen::UpdateSelected(KButton,System.String,System.Collections.Generic.List`1<System.String>)")]
    [GameMember(GameUse.Prefix, "System.Void LoadScreen::Load()")]
    [GameMember(GameUse.Prefix, "System.Void LoadScreen::DoLoad(System.String)")]
    [GameMember(GameUse.Postfix, "System.Void LoadScreen::OnDeactivate()")]
    [GameMember(GameUse.Uses, "LoadScreen/SelectedSave")]
    [GameMember(GameUse.Uses, "LoadScreen/SelectedSave LoadScreen::selectedSave")]
    [GameMember(GameUse.Uses, "System.String LoadScreen/SelectedSave::filename")]
    [GameMember(GameUse.Postfix, "System.Void MainMenu::ResumeGame()")]
    [GameMember(GameUse.Uses, "LoadingOverlay LoadingOverlay::instance")]
    [GameMember(GameUse.Uses, "System.Boolean LoadingOverlay::showLoad")]
    [GameMember(GameUse.Prefix, "System.Void SaveLoader::LoadScene()")]
    [GameMember(GameUse.Uses, "System.String SaveLoader::GetActiveSaveFilePath()")]
    [GameMember(GameUse.Transpiler | GameUse.Postfix, "System.Boolean SaveLoader::Load(System.String)", "1ca0954283b467ea linux-744825 win-744825", Rewrites = 1)]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Byte[] SaveLoader::DecompressContents(System.Byte[])", "7f27a2ec6323e63c linux-744825 win-744825")]
    public static class SaveReadAhead
    {
        public const string Name = "save-read-ahead";

        /// A selection starts reading once no other selection has come for this long.
        const int SettleMs = 200;
        /// A save read ahead and not loaded is dropped this long after it is ready.
        const int UnusedMs = 30_000;
        /// A save whose load has started is dropped this long after, if the load never takes it.
        const int CommittedMs = 120_000;

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        sealed class Job
        {
            public string Path, Full;
            public long Size;
            public System.DateTime WriteTime;
            public byte[] File;
            public int BodyOffset = -1;
            public bool Compressed;
            public byte[] Body;
            public Exception ReadError, DecompressError;
            public volatile bool Dropped, Committed;
            public readonly ManualResetEventSlim Read = new ManualResetEventSlim(false), Done = new ManualResetEventSlim(false);
            /// Set when the load starts: a selection's thread waits no longer for it to settle.
            public readonly ManualResetEventSlim Go = new ManualResetEventSlim(false);
            public long Requested, ReadAt, DoneAt;
            public double ReadMs, DecompressMs;
            public System.Threading.Timer Expiry;
        }

        static readonly object gate = new object();
        static Job current;
        /// The job whose bytes this load's read took, until its DecompressContents.
        static Job armed;
        [ThreadStatic] static bool bypass;

        static MethodBase load, decompress;
        static readonly HashSet<string> loggedAside = new HashSet<string>(StringComparer.Ordinal);
        static long mismatches;

        /// For tests (E2eDriver's state reads, `type:OniMoono.GameTweaks.SaveReadAhead::Taken`):
        /// loads that took the bytes read ahead, the body decompressed ahead, the times it
        /// stepped aside, and verify's mismatches.
        public static int Taken, Decompressed, SteppedAside;
        public static long Mismatches => mismatches;

        public static void Install(Harmony harmony)
        {
            var screen = typeof(LoadScreen);
            load = AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.Load), new[] { typeof(string) });
            decompress = AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.DecompressContents));
            harmony.Patch(AccessTools.Method(screen, nameof(LoadScreen.UpdateSelected)), postfix: new HarmonyMethod(typeof(SaveReadAhead), nameof(Selected)));
            harmony.Patch(AccessTools.Method(screen, nameof(LoadScreen.Load)), prefix: new HarmonyMethod(typeof(SaveReadAhead), nameof(LoadClicked)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(screen, nameof(LoadScreen.DoLoad), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(SaveReadAhead), nameof(DoLoad)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(screen, "OnDeactivate"), postfix: new HarmonyMethod(typeof(SaveReadAhead), nameof(ScreenClosed)));
            harmony.Patch(AccessTools.Method(typeof(MainMenu), nameof(MainMenu.ResumeGame)), postfix: new HarmonyMethod(typeof(SaveReadAhead), nameof(Resumed)));
            harmony.Patch(AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.LoadScene)), prefix: new HarmonyMethod(typeof(SaveReadAhead), nameof(LoadScene)) { priority = Priority.First });
            harmony.Patch(load, transpiler: new HarmonyMethod(typeof(SaveReadAhead), nameof(Transpile)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(SaveReadAhead), nameof(Loaded)) { priority = Priority.Last });
            // First: ahead of fast-save-compression's prefix, which then leaves the result alone.
            harmony.Patch(decompress, prefix: new HarmonyMethod(typeof(SaveReadAhead), nameof(Decompress)) { priority = Priority.First });
        }

        /// The methods whose patching by another owner makes this tweak step aside (ModOverlaps),
        /// with the kinds of patch that count on each.
        internal static IEnumerable<MethodBase> Guarded() => new[] { load, decompress };

        internal static ProbeRepairPolicy.GuardedKinds KindsOf(MethodBase m) =>
            m == load ? ProbeRepairPolicy.GuardedKinds.SkipsAndTranspilers : ProbeRepairPolicy.GuardedKinds.All;

        // ----- requests -----

        static void Selected(string filename) => Request(filename, commit: false);

        static void LoadClicked(LoadScreen __instance) => Request(__instance.selectedSave?.filename, commit: true);

        static void DoLoad(string filename) => Request(filename, commit: true);

        static void Resumed()
        {
            if (LoadingOverlay.instance != null && LoadingOverlay.instance.showLoad) Request(SaveLoader.GetActiveSaveFilePath(), commit: true);
        }

        static void LoadScene() => Request(SaveLoader.GetActiveSaveFilePath(), commit: true);

        /// The Load screen closed: a save read ahead whose load has not started is dropped.
        static void ScreenClosed()
        {
            lock (gate)
                if (current != null && !current.Committed) Drop(current);
        }

        /// Main thread: reads `filename` ahead, unless it is being read already (a load then keeps
        /// it past the screen and the 30 s).
        static void Request(string filename, bool commit)
        {
            if (string.IsNullOrEmpty(filename)) return;
            string full;
            try { full = System.IO.Path.GetFullPath(filename); }
            catch { return; }
            Job job;
            lock (gate)
            {
                if (current != null && current.Full == full)
                {
                    if (commit && !current.Committed)
                    {
                        current.Committed = true;
                        current.Go.Set();
                        current.Expiry?.Change(CommittedMs, Timeout.Infinite);
                    }
                    return;
                }
                if (Aside(out var who, out var where))
                {
                    SteppedAside++;
                    LogAside(who, where);
                    if (current != null) Drop(current);
                    return;
                }
                if (current != null) Drop(current);
                job = current = new Job { Path = filename, Full = full, Committed = commit, Requested = Stopwatch.GetTimestamp() };
                job.Expiry = new System.Threading.Timer(Expired, job, commit ? CommittedMs : Timeout.Infinite, Timeout.Infinite);
            }
            if (commit) job.Go.Set();
            var thread = new Thread(() => Run(job, SettleMs)) { Name = "Save read ahead", IsBackground = true };
            thread.Start();
        }

        static void Expired(object state)
        {
            lock (gate)
            {
                var job = (Job)state;
                if (current == job && armed != job) Drop(job);
            }
        }

        /// Under `gate`: lets go of a job (its thread lets go of what it makes after).
        static void Drop(Job job)
        {
            job.Dropped = true;
            job.File = null;
            job.Body = null;
            job.Expiry?.Dispose();
            if (current == job) current = null;
        }

        // ----- the thread -----

        static void Run(Job job, int settleMs)
        {
            try
            {
                if (settleMs > 0)
                {
                    job.Go.Wait(settleMs);
                    if (job.Dropped) return;
                }
                long start = Stopwatch.GetTimestamp();
                try
                {
                    var before = new FileInfo(job.Path);
                    long size = before.Length;
                    var time = before.LastWriteTimeUtc;
                    var bytes = System.IO.File.ReadAllBytes(job.Path);
                    var after = new FileInfo(job.Path);
                    if (bytes.Length != size || after.Length != size || after.LastWriteTimeUtc != time) throw new IOException("the file changed while it was read");
                    job.Size = size;
                    job.WriteTime = time;
                    job.BodyOffset = SaveBody.Offset(bytes, out job.Compressed);
                    lock (gate)
                        if (!job.Dropped) job.File = bytes;
                }
                catch (Exception e)
                {
                    job.ReadError = e;
                }
                job.ReadMs = Ms(start);
                job.ReadAt = Stopwatch.GetTimestamp();
                job.Read.Set();
                var file = job.File;
                if (file == null || job.Dropped || !job.Compressed || job.BodyOffset < 0) return;
                start = Stopwatch.GetTimestamp();
                try
                {
                    var compressed = new byte[file.Length - job.BodyOffset];
                    Array.Copy(file, job.BodyOffset, compressed, 0, compressed.Length);
                    file = null;
                    byte[] body;
                    bypass = true;
                    try { body = SaveLoader.DecompressContents(compressed); }
                    finally { bypass = false; }
                    lock (gate)
                        if (!job.Dropped) job.Body = body;
                }
                catch (Exception e)
                {
                    job.DecompressError = e;
                }
                job.DecompressMs = Ms(start);
            }
            finally
            {
                job.DoneAt = Stopwatch.GetTimestamp();
                job.Read.Set();
                job.Done.Set();
                lock (gate)
                    if (current == job && !job.Committed && !job.Dropped) job.Expiry?.Change(UnusedMs, Timeout.Infinite);
            }
        }

        static double Ms(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;

        // ----- the load -----

        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var read = AccessTools.Method(typeof(File), nameof(File.ReadAllBytes), new[] { typeof(string) });
            var ours = AccessTools.Method(typeof(SaveReadAhead), nameof(ReadAllBytes));
            int rewritten = 0;
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo m && m == read)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = ours;
                    rewritten++;
                }
                yield return instruction;
            }
            TranspilerCounts.Report(Name, original, rewritten);
        }

        /// SaveLoader.Load(string)'s read of the save: the bytes read ahead when they are this
        /// file's as it is now.
        static byte[] ReadAllBytes(string path)
        {
            Job job;
            lock (gate)
            {
                job = current;
                armed = null;
            }
            if (job == null || Aside(out _, out _)) return File.ReadAllBytes(path);
            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch { return File.ReadAllBytes(path); }
            if (job.Full != full) return File.ReadAllBytes(path);
            long waitStart = Stopwatch.GetTimestamp();
            job.Read.Wait();
            double waited = Ms(waitStart);
            byte[] bytes;
            lock (gate) bytes = job.Dropped ? null : job.File;
            FileInfo now = null;
            try { now = new FileInfo(path); }
            catch { }
            if (bytes == null || now == null || !now.Exists || now.Length != job.Size || now.LastWriteTimeUtc != job.WriteTime)
            {
                global::Debug.Log($"[Moono] {Name}: the save read ahead is not used ({(bytes == null ? job.ReadError?.GetType().Name ?? "dropped" : "the file changed since")}); read as usual");
                lock (gate) Drop(job);
                return File.ReadAllBytes(path);
            }
            lock (gate) armed = job;
            job.Committed = true;
            Taken++;
            global::Debug.Log($"[Moono] {Name}: {bytes.Length / 1e6:F1} MB read ahead in {job.ReadMs:F0} ms, "
                + (waited >= 1 ? $"the load waited {waited:F0} ms for it" : $"ready {(waitStart - job.ReadAt) * 1000.0 / Stopwatch.Frequency:F0} ms before the load"));
            if (verify)
            {
                var own = File.ReadAllBytes(path);
                if (!own.AsSpan().SequenceEqual(bytes)) Mismatch("the bytes read ahead differ from the file's");
                else global::Debug.Log($"[Moono] {Name}: verify: the bytes read ahead are the file's ({own.Length} bytes)");
            }
            return bytes;
        }

        /// SaveLoader.DecompressContents: the body decompressed ahead, when `compressed` is the
        /// body of the bytes the load's read took.
        static bool Decompress(byte[] compressed, ref byte[] __result, bool __runOriginal)
        {
            if (bypass || !__runOriginal) return true;
            Job job;
            lock (gate)
            {
                job = armed;
                armed = null;
            }
            if (job == null) return true;
            try
            {
                if (Aside(out _, out _) || compressed == null) return true;
                byte[] file;
                lock (gate) file = job.Dropped ? null : job.File;
                if (file == null || job.BodyOffset < 0 || compressed.Length != file.Length - job.BodyOffset || !compressed.AsSpan().SequenceEqual(file.AsSpan(job.BodyOffset)))
                {
                    global::Debug.Log($"[Moono] {Name}: the body the load decompresses is not the one read ahead; decompressed as usual");
                    return true;
                }
                long waitStart = Stopwatch.GetTimestamp();
                job.Done.Wait();
                double waited = Ms(waitStart);
                byte[] body;
                lock (gate) body = job.Dropped ? null : job.Body;
                if (body == null)
                {
                    // Threw on the thread: the game's own decompression throws as it would.
                    global::Debug.Log($"[Moono] {Name}: decompressing ahead failed ({job.DecompressError?.GetType().Name ?? "dropped"}); decompressed as usual");
                    return true;
                }
                global::Debug.Log($"[Moono] {Name}: {body.Length / 1e6:F1} MB decompressed ahead in {job.DecompressMs:F0} ms, "
                    + (waited >= 1 ? $"the load waited {waited:F0} ms for it" : $"ready {(waitStart - job.DoneAt) * 1000.0 / Stopwatch.Frequency:F0} ms before the load"));
                if (verify)
                {
                    byte[] own;
                    bypass = true;
                    try { own = SaveLoader.DecompressContents(compressed); }
                    finally { bypass = false; }
                    if (!own.AsSpan().SequenceEqual(body)) Mismatch($"the body decompressed ahead differs from the game's ({body.Length} and {own.Length} bytes)");
                    else global::Debug.Log($"[Moono] {Name}: verify: the body decompressed ahead is the game's ({own.Length} bytes); {mismatches} mismatches so far");
                }
                __result = body;
                Decompressed++;
                return false;
            }
            finally
            {
                lock (gate)
                    if (current == job) Drop(job);
            }
        }

        static void Mismatch(string what)
        {
            mismatches++;
            global::Debug.LogWarning($"[Moono] {Name}: verify: {what} ({mismatches} so far)");
        }

        /// After the load: nothing is kept.
        static void Loaded()
        {
            lock (gate)
            {
                armed = null;
                if (current != null && current.Committed) Drop(current);
            }
        }

        // ----- other owners -----

        /// Another owner's patch that makes this tweak step aside: any on DecompressContents, a
        /// transpiler or a prefix that can skip it on Load(string).
        static bool Aside(out string owner, out string method)
        {
            owner = Foreign(decompress, all: true);
            method = "SaveLoader.DecompressContents";
            if (owner != null) return true;
            owner = Foreign(load, all: false);
            method = "SaveLoader.Load";
            return owner != null;
        }

        static string Foreign(MethodBase m, bool all)
        {
            var info = Harmony.GetPatchInfo(m);
            if (info == null) return null;
            foreach (var p in info.Transpilers)
                if (!ModOverlapsCore.IsOurs(p.owner)) return p.owner;
            foreach (var p in info.Prefixes)
                if (!ModOverlapsCore.IsOurs(p.owner) && (all || p.PatchMethod?.ReturnType == typeof(bool))) return p.owner;
            if (!all) return null;
            foreach (var p in info.Postfixes)
                if (!ModOverlapsCore.IsOurs(p.owner)) return p.owner;
            foreach (var p in info.Finalizers)
                if (!ModOverlapsCore.IsOurs(p.owner)) return p.owner;
            return null;
        }

        static void LogAside(string owner, string method)
        {
            if (loggedAside.Add(owner + "\n" + method))
                global::Debug.Log($"[Moono] {Name}: steps aside while {owner} patches {method}; saves are read and decompressed as usual");
        }
    }
}
