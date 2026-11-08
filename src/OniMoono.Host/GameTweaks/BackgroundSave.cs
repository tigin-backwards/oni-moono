using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `background-save` (opt-in): the game serializes the colony into memory and then
    /// compresses it into the file on the main thread (5 s for a large colony with Ionic.Zlib,
    /// 1.3 s with fast-save-compression). The serialized bytes are a finished snapshot, so the
    /// compression and the file write run on a background thread instead, and only serializing
    /// stays on the main thread.
    ///
    /// The saved file is the same. It is written beside the target and moved over it when
    /// complete; until then the target holds the header the game wrote, which is what the save
    /// lists read. Whatever reads, replaces or deletes saves waits for the writes in flight first:
    /// loading a save, the next save, the load screen's delete and its cloud/local copies and moves, and the end of the run.
    /// So overlapping saves (a pause-menu save or Save and Quit during an autosave's write) are
    /// written whole and in order, as the game writes them one after another (BackgroundSaveTests,
    /// tests/e2e/overlap-manual.yaml).
    /// A failed write is logged; the game's own failure dialog does not appear for it. A run that
    /// ends by an unhandled exception waits for the write too.
    [GameMember(GameUse.Prefix, "System.Void LoadScreen::CheckCloudLocalOverlap()")]
    [GameMember(GameUse.Prefix, "System.Boolean LoadScreen::ConvertToLocalOrCloud(System.String,System.String,System.String)")]
    [GameMember(GameUse.Prefix, "System.Void LoadScreen::DeleteFileAndEmptyFolder(System.String)")]
    [GameMember(GameUse.Prefix, "System.Boolean LoadScreen::FileMatch(System.String,System.String,Tuple`2<System.Boolean,System.Boolean>&)")]
    [GameMember(GameUse.Prefix, "System.Boolean LoadScreen::MigrateSave(System.String,System.String,System.Boolean,System.String&)")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void SaveLoader::CompressContents(System.IO.BinaryWriter,System.Byte[],System.Int32)", "7027ce9ea9fd4d7b linux-744825 win-744825", "772347a4c67b2ad9 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix, "System.Boolean SaveLoader::Load(System.String)")]
    [GameMember(GameUse.Prefix, "System.String SaveLoader::Save(System.String,System.Boolean,System.Boolean)")]
    internal static class BackgroundSave
    {
        public const string Name = GameTweakNames.BackgroundSave;

        static readonly object gate = new();
        static Task pending = Task.CompletedTask;
        /// Compress with .NET's zlib (fast-save-compression) rather than Ionic.Zlib, as the game
        /// does. Internal, as Compress is, for BackgroundSaveTests.
        internal static bool fast;

        public static void Install(Harmony harmony, bool fastCompression)
        {
            fast = fastCompression;
            // A run ending by an unhandled exception still finishes the save in flight (the game
            // has already truncated its file to the header). A hard kill during the write loses
            // it, as killing the game during its own in-place write does.
            AppDomain.CurrentDomain.UnhandledException += (_, _) =>
            {
                Task t;
                lock (gate) t = pending;
                t.Wait(TimeSpan.FromSeconds(30));
            };
            // Mods' prefixes run first (Better Save takes the body itself).
            harmony.Patch(AccessTools.Method("SaveLoader:CompressContents"), prefix: new HarmonyMethod(typeof(BackgroundSave), nameof(Compress)) { priority = Priority.Last });
            var wait = new HarmonyMethod(typeof(BackgroundSave), nameof(WaitPrefix));
            harmony.Patch(AccessTools.Method("SaveLoader:Save", [typeof(string), typeof(bool), typeof(bool)]), prefix: wait);
            harmony.Patch(AccessTools.Method("SaveLoader:Load", [typeof(string)]), prefix: wait);
            // The load screen's file operations on saves: deleting, the cloud/local migration's
            // hash comparison (FileMatch), its copies (MigrateFile, waited for in its callers
            // CheckCloudLocalOverlap and MigrateSave: Harmony cannot patch MigrateFile, whose
            // `catch … when` clauses fail on Mono too, ARCHITECTURE D53) and moving a colony's
            // folder (ConvertToLocalOrCloud). Each must exist, so a renamed one stops the start-up.
            foreach (var name in (string[])["DeleteFileAndEmptyFolder", "FileMatch", "CheckCloudLocalOverlap", "MigrateSave", "ConvertToLocalOrCloud"])
                harmony.Patch(AccessTools.Method("LoadScreen:" + name) ?? throw new MissingMethodException("LoadScreen", name), prefix: wait);
        }

        static void WaitPrefix() => WaitAll();

        /// Blocks until every save in flight is on disk.
        public static void WaitAll()
        {
            Task t;
            lock (gate) t = pending;
            if (!t.IsCompleted) EngineLog.Engine("waiting for a save being written");
            t.Wait();
        }

        /// SaveLoader.CompressContents(BinaryWriter, byte[], int): the header is in the file; the
        /// body is compressed and the whole file written in the background. A writer that is not
        /// the game's file (Better Save writes the save into memory and to disk itself) is left to
        /// the rest of the method's patches and the game.
        internal static bool Compress(BinaryWriter fileWriter, byte[] uncompressed, int length)
        {
            if (fileWriter.BaseStream is not FileStream file) return true;
            fileWriter.Flush();
            string path = file.Name;
            var header = new byte[file.Position];
            file.Position = 0;
            file.ReadExactly(header);
            file.Position = header.Length;
            // Each write follows the previous one whatever its outcome, and never faults the chain
            // (a fault would make every later save and load throw).
            lock (gate) pending = pending.ContinueWith(_ =>
            {
                try { Write(path, header, uncompressed, length); }
                catch (Exception e) { EngineLog.Engine($"background save of {path}: {e}"); }
            }, TaskScheduler.Default);
            return false;
        }

        static void Write(string path, byte[] header, byte[] body, int length)
        {
            string temporary = path + ".writing";
            try
            {
                using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                output.Write(header);
                if (fast)
                {
                    using var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true);
                    zlib.Write(body, 0, length);
                }
                else
                {
                    using var zlib = new Ionic.Zlib.ZlibStream(output, Ionic.Zlib.CompressionMode.Compress, Ionic.Zlib.CompressionLevel.BestSpeed, leaveOpen: true);
                    zlib.Write(body, 0, length);
                    zlib.Flush();
                }
            }
            catch (Exception e)
            {
                // Unwritten (a full disk, say): the partial file is no save.
                UnityEngine.Debug.LogError($"Background save of {path} failed: {e}");
                try { File.Delete(temporary); } catch (Exception) { } // the write's failure is what matters
                return;
            }
            // The whole save is in the temporary file from here on, and it is never deleted unless it
            // took the save's place: another process holding the save (an antivirus scan, a cloud
            // client, the game closing its handle on the header) is waited out, then copied over.
            if (Replace(temporary, path)) EngineLog.Engine($"background save written: {path}");
            else UnityEngine.Debug.LogError($"Background save of {path} could not replace the save file, which another program keeps open; the complete save is in {temporary}");
        }

        /// Moves the finished temporary file onto the save: retried for about 30 s (20 ms at first,
        /// backing off to 500 ms), then a copy over it is tried. False when neither went through; the
        /// temporary file is then left as it is.
        internal static bool Replace(string temporary, string path, int waitMilliseconds = 30_000)
        {
            int waited = 0;
            for (int pause = 20; ; pause = Math.Min(pause * 2, 500))
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    return true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (waited >= waitMilliseconds) break;
                    Thread.Sleep(pause);
                    waited += pause;
                }
            }
            try
            {
                File.Copy(temporary, path, overwrite: true);
                File.Delete(temporary);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
