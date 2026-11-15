using UnityEngine;

namespace PerfProbe
{
    /// `quit.txt` "SECONDS [save]": SECONDS of real time into a loaded colony the game quits to
    /// the desktop as the pause menu's Quit does (PauseScreen.OnDesktopQuitConfirm; `save`: its
    /// Save and Quit), for measuring and checking the quit. Before that it sets a Unity
    /// PlayerPrefs key and a KPlayerPrefs key (QuitProbeMark, the time's ticks) without saving
    /// them, and each quit callback writes a file beside the probe (quit-CALLBACK.txt, holding
    /// the mark), as a mod that saves its state at quit does: OnApplicationQuit, then OnDisable
    /// and OnDestroy of an object kept across scenes and of one in the colony's scene.
    public class QuitProbe : MonoBehaviour
    {
        public string Path;
        public string Label;
        float since = -1;
        bool asked;
        static string mark = "";

        public static void Install(GameObject keeper, string path)
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(path, "quit.txt"))) return;
            foreach (var old in System.IO.Directory.GetFiles(path, "quit-*.txt")) System.IO.File.Delete(old);
            var probe = keeper.AddComponent<QuitProbe>();
            probe.Path = path;
            probe.Label = "kept";
        }

        void Update()
        {
            if (Label != "kept" || asked || Game.Instance == null || SaveLoader.Instance == null) return;
            if (since < 0)
            {
                since = Time.realtimeSinceStartup;
                var scene = new GameObject("QuitProbeScene").AddComponent<QuitProbe>();
                scene.Path = Path;
                scene.Label = "scene";
                return;
            }
            var words = System.IO.File.ReadAllText(System.IO.Path.Combine(Path, "quit.txt")).Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            if (Time.realtimeSinceStartup - since < float.Parse(words[0], System.Globalization.CultureInfo.InvariantCulture)) return;
            asked = true;
            bool save = System.Array.IndexOf(words, "save") >= 0;
            mark = System.DateTime.UtcNow.Ticks.ToString();
            PlayerPrefs.SetString("QuitProbeMark", mark);
            KPlayerPrefs.SetString("QuitProbeMark", mark);
            Debug.Log($"PERF quit: asking at frame {Time.frameCount}, mark {mark}, save {save}, pause screen {(PauseScreen.Instance != null ? "present" : "absent")}");
            if (PauseScreen.Instance != null)
                HarmonyLib.AccessTools.Method(typeof(PauseScreen), "OnDesktopQuitConfirm").Invoke(PauseScreen.Instance, new object[] { save });
            else
            {
                if (save) SaveLoader.Instance.Save(SaveLoader.GetActiveSaveFilePath());
                App.Quit();
            }
        }

        void Write(string callback) => System.IO.File.WriteAllText(System.IO.Path.Combine(Path, $"quit-{Label}-{callback}.txt"), mark);

        void OnApplicationQuit() => Write("OnApplicationQuit");
        void OnDisable() { if (asked || Label == "scene" && mark.Length > 0) Write("OnDisable"); }
        void OnDestroy() { if (asked || Label == "scene" && mark.Length > 0) Write("OnDestroy"); }
    }
}
