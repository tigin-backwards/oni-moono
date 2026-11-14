using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `background-save`'s colony summary (exact): every save rewrites the colony's
    /// summary file (RetiredColonies/NAME/NAME.json, what the main menu's Colony Summaries
    /// shows) before it writes the save, on the main thread: RetireColonyUtility
    /// .SaveColonySummaryData gathers the data (GetCurrentColonyRetiredColonyData: the
    /// Duplicants, the buildings, 16 statistics with a point for every cycle), turns it into JSON
    /// and writes the file, 100-126 ms on a late-game colony (270,000 points).
    ///
    /// Here, for the call a save makes, the data is gathered on the main thread as before and the
    /// JSON and the file are made on background-save's writer, ahead of the save's own write.
    /// The gathered RetiredColonyData is a snapshot: strings, numbers, and arrays, lists and
    /// dictionaries its constructor made new, with no reference into the game; nothing reads it
    /// but the writer. The file's bytes are the game's (the same JsonConvert.SerializeObject of
    /// the same data, the same write with its retries). The migration of old screenshot folders
    /// in between stays on the main thread (it reads the worlds).
    ///
    /// What reads the file waits for the writer first: the game's LoadRetiredColonies (the
    /// Colony Summaries screen), the next save, a load and the end of the run (BackgroundSave).
    /// Every other caller of SaveColonySummaryData (an achievement, retiring the colony, the
    /// pause screen) gets the game's method, after the writes in flight. So does a save's, when
    /// another mod patches the method, or JsonConvert.DefaultSettings is set (a converter could
    /// run mod code on the writer).
    ///
    /// What differs: a save's call returns true before the file is written (SaveLoader.Save does
    /// not read it), and a failure to write is logged from the writer.
    /// ONI_TWEAK_VERIFY=1 makes the JSON on the main thread as well and compares the two.
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Boolean RetireColonyUtility::SaveColonySummaryData()", "12152414c207f347 linux-744825 win-744825")]
    [GameMember(GameUse.Prefix, "RetiredColonyData[] RetireColonyUtility::LoadRetiredColonies(System.Boolean)")]
    public static class ColonySummaryWrite
    {
        public const string Name = "background-save";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Test infrastructure (PerfProbe's savetest.txt): true leaves a save's call to the game.
        public static bool Bypass;

        static System.Action<System.Action> enqueue;
        static System.Action wait;
        static MethodBase target;
        static int saving;
        static string loggedOff;
        static long verified, mismatches;

        /// From the host's BackgroundSave: its writer's queue (work runs after the writes in
        /// flight, in order) and its wait for all of them.
        public static void Install(Harmony harmony, System.Action<System.Action> enqueueWork, System.Action waitForWrites)
        {
            enqueue = enqueueWork;
            wait = waitForWrites;
            target = AccessTools.Method(typeof(RetireColonyUtility), nameof(RetireColonyUtility.SaveColonySummaryData));
            harmony.Patch(AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.Save), new[] { typeof(string), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(ColonySummaryWrite), nameof(SaveStarting)), finalizer: new HarmonyMethod(typeof(ColonySummaryWrite), nameof(SaveEnded)));
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ColonySummaryWrite), nameof(Saving)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(RetireColonyUtility), nameof(RetireColonyUtility.LoadRetiredColonies)),
                prefix: new HarmonyMethod(typeof(ColonySummaryWrite), nameof(Reading)));
        }

        static void SaveStarting() => saving++;

        static void SaveEnded() => saving = Math.Max(0, saving - 1);

        static void Reading() => wait();

        /// Why a save's call is the game's own now, or null.
        static string Off()
        {
            if (Bypass) return "bypassed";
            if (JsonConvert.DefaultSettings != null) return "JsonConvert.DefaultSettings is set";
            var info = Harmony.GetPatchInfo(target);
            if (info != null)
                foreach (var owner in info.Owners)
                    if (!ModOverlapsCore.IsOurs(owner)) return $"{owner} patches RetireColonyUtility.SaveColonySummaryData";
            return null;
        }

        /// RetireColonyUtility.SaveColonySummaryData, for a save: its JSON and its file on the writer.
        static bool Saving(ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            string off = saving == 0 ? "" : Off();
            if (off != null)
            {
                if (off.Length > 0 && off != loggedOff && !Bypass)
                {
                    loggedOff = off;
                    global::Debug.Log($"{Name}: the colony summary is written on the main thread ({off})");
                }
                // The game's method writes the file itself: after any write of it still queued.
                wait();
                return true;
            }
            if (!Directory.Exists(Util.RootFolder()))
            {
                Directory.CreateDirectory(Util.RootFolder());
            }
            string text = Path.Combine(Util.RootFolder(), Util.GetRetiredColoniesFolderName());
            if (!Directory.Exists(text))
            {
                Directory.CreateDirectory(text);
            }
            string text2 = RetireColonyUtility.StripInvalidCharacters(SaveGame.Instance.BaseName);
            string text3 = Path.Combine(text, text2);
            if (!Directory.Exists(text3))
            {
                Directory.CreateDirectory(text3);
            }
            string path = Path.Combine(text3, text2 + ".json");
            RetiredColonyData currentColonyRetiredColonyData = RetireColonyUtility.GetCurrentColonyRetiredColonyData();
            string onMain = verify ? JsonConvert.SerializeObject(currentColonyRetiredColonyData) : null;
            if (DlcManager.IsExpansion1Active())
            {
                foreach (WorldContainer worldContainer in ClusterManager.Instance.WorldContainers)
                {
                    if (!worldContainer.IsDiscovered || worldContainer.IsModuleInterior)
                    {
                        continue;
                    }
                    string name = worldContainer.GetComponent<ClusterGridEntity>().Name;
                    string text4 = Path.Combine(text3, name);
                    string text5 = Path.Combine(text3, worldContainer.id.ToString("D5"));
                    if (!Directory.Exists(text4))
                    {
                        continue;
                    }
                    bool flag = Directory.GetFiles(text4).Length != 0;
                    if (!Directory.Exists(text5))
                    {
                        Directory.CreateDirectory(text5);
                    }
                    string[] files = Directory.GetFiles(text4);
                    foreach (string text6 in files)
                    {
                        try
                        {
                            File.Copy(text6, text6.Replace(text4, text5), overwrite: true);
                            File.Delete(text6);
                        }
                        catch (Exception obj)
                        {
                            flag = false;
                            global::Debug.LogWarning("Error occurred trying to migrate screenshot: " + text6);
                            global::Debug.LogWarning(obj);
                        }
                    }
                    if (flag)
                    {
                        Directory.Delete(text4);
                    }
                }
            }
            enqueue(() => Write(path, currentColonyRetiredColonyData, onMain));
            __result = true;
            return false;
        }

        /// The writer's part: the method's JSON and its write, with its retries.
        static void Write(string path, RetiredColonyData data, string onMain)
        {
            string s = JsonConvert.SerializeObject(data);
            if (onMain != null)
            {
                bool same = s == onMain;
                verified++;
                if (!same) mismatches++;
                string line = $"{Name}: verify: the colony summary's JSON {(same ? "identical" : "DIFFERS from the main thread's")} ({s.Length:N0} characters; {verified} saves, {mismatches} mismatches)";
                if (same) global::Debug.Log(line);
                else global::Debug.LogWarning(line);
            }
            bool flag2 = false;
            int num = 0;
            while (!flag2 && num < 5)
            {
                try
                {
                    Thread.Sleep(num * 100);
                    using FileStream fileStream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    flag2 = true;
                    Encoding uTF = Encoding.UTF8;
                    byte[] bytes = uTF.GetBytes(s);
                    fileStream.Write(bytes, 0, bytes.Length);
                }
                catch (Exception ex)
                {
                    global::Debug.LogWarningFormat("SaveColonySummaryData failed attempt {0}: {1}", num + 1, ex.ToString());
                }
                num++;
            }
        }
    }
}
