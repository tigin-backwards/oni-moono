using System;
using System.Diagnostics;
using System.Globalization;
using OniMoono.Engine;

namespace UnityEngine
{
    public partial class Application
    {
        public static string dataPath => EngineConfig.DataPath;
        public static string streamingAssetsPath => EngineConfig.StreamingAssetsPath;
        public static string persistentDataPath => EngineConfig.PersistentDataPath;
        public static string temporaryCachePath => EngineConfig.TemporaryCachePath;
        public static string consoleLogPath => EngineConfig.ConsoleLogPath;
        public static string companyName => EngineConfig.CompanyName;
        public static string productName => EngineConfig.ProductName;
        public static string version => EngineConfig.Version;
        public static string unityVersion => EngineConfig.UnityVersion;

        /// The player of the operating system the engine runs on.
        public static RuntimePlatform platform => OperatingSystem.IsWindows() ? RuntimePlatform.WindowsPlayer : RuntimePlatform.LinuxPlayer;
        public static bool isEditor => false;
        public static bool isPlaying => true;
        public static bool isBatchMode => EngineConfig.IsBatchMode;
        public static bool isMobilePlatform => false;
        public static bool isConsolePlatform => false;
        public static bool isFocused => PlayerLoop.HasFocus;
        public static bool runInBackground { get; set; } = true;

        public static int targetFrameRate
        {
            get => PlayerLoop.TargetFrameRate;
            set => PlayerLoop.TargetFrameRate = value;
        }

        /// Unity's players derive the language from the user's UI language (Windows) or the
        /// locale environment (Linux), which the current UI culture reflects.
        public static SystemLanguage systemLanguage => Languages.FromCulture(CultureInfo.CurrentUICulture);

        /// Quits at the end of the frame, after OnApplicationQuit, with this exit code.
        public static void Quit(int exitCode)
        {
            // The caller, as the game quits from several places (crash reports, smoke tests, menus).
            EngineLog.Engine($"Application.Quit({exitCode}) requested by\n{EngineLog.CaptureStackTrace()}");
            PlayerLoop.RequestQuit(exitCode);
        }

        public static void Quit() => Quit(0);

        /// Asked at the end of the frame a quit was requested in, after WaitForEndOfFrame, each
        /// handler in turn: one that answers false cancels the quit, and the handlers after it are
        /// not asked; one that throws is logged and counts as agreeing (UnityProbe's quitevents).
        public static event Func<bool> wantsToQuit;

        /// Raised once the quit goes ahead, after OnApplicationQuit and before objects are
        /// destroyed. A handler that throws is logged, and the handlers after it do not run
        /// (UnityProbe's quitevents).
        public static event Action quitting;

        /// Unity raises it when a player embedded in another application (Unity as a Library)
        /// unloads; a standalone player never does (UnityProbe's quitevents: not at its quit).
        public static event Action unloading
        {
            add => s_Unloading += value;
            remove => s_Unloading -= value;
        }
        static Action s_Unloading;

        internal static bool WantsToQuit()
        {
            if (wantsToQuit == null) return true;
            foreach (Func<bool> handler in wantsToQuit.GetInvocationList())
            {
                try
                {
                    if (!handler()) return false;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            return true;
        }

        internal static void RaiseQuitting()
        {
            try
            {
                quitting?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// URLs (web pages, and file:// folders such as a mod's) go to the desktop's opener: the
        /// shell on Windows (ShellExecute, which opens a folder in Explorer), `open` on macOS and
        /// xdg-open on Linux.
        public static void OpenURL(string url)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("URL must not be null or empty.", nameof(url));
            try
            {
                if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url) { UseShellExecute = false });
            }
            catch (Exception e)
            {
                Debug.LogWarning("Failed to open URL " + url + ": " + e.Message);
            }
        }

        public static event LogCallback logMessageReceived;
        public static event LogCallback logMessageReceivedThreaded;

        /// The log callbacks for one message, as the original player calls them (UnityProbe's
        /// logthreads scenario): logged on the main thread, logMessageReceived and then
        /// logMessageReceivedThreaded; logged on any other thread, logMessageReceivedThreaded
        /// alone, on that thread (logMessageReceived never sees the message, later or queued). A
        /// handler's exception ends the call: the handlers after it, and the threaded event after
        /// a logMessageReceived handler's, do not run. It is returned for the log to write.
        internal static Exception CallLogCallback(string condition, string stackTrace, LogType type, bool onMainThread)
        {
            try
            {
                if (onMainThread) logMessageReceived?.Invoke(condition, stackTrace, type);
                logMessageReceivedThreaded?.Invoke(condition, stackTrace, type);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
    }

    public partial class AsyncOperation
    {
        bool m_IsDone;
        float m_Progress;
        Action<AsyncOperation> m_completeCallback;

        public AsyncOperation() { }

        /// True once the work is done, read from any thread: an operation whose work runs on
        /// another thread (a web request) turns done when that work ends, before the main thread
        /// completes it, so a main thread that blocks on isDone sees it turn as in Unity
        /// (UnityProbe's apigaps "webspin": Mod Manager spins on a request's operation).
        public bool isDone => m_IsDone || WorkDone;
        public float progress => isDone ? 1f : m_Progress;

        /// Whether the operation's work has ended though the main thread has not completed it yet.
        internal virtual bool WorkDone => false;

        /// Completed on the main thread (its callbacks have run); what a coroutine waits for.
        internal bool IsCompleted => m_IsDone;
        public int priority { get; set; }
        public bool allowSceneActivation { get; set; } = true;

        /// Subscribing to an operation that is already done invokes the callback at once, also
        /// when its work ended but the main thread has not completed it yet (measured: UnityProbe's
        /// apigaps "webspin"); those subscribed before run at the completion.
        public event Action<AsyncOperation> completed
        {
            add
            {
                if (isDone) value(this);
                else m_completeCallback += value;
            }
            remove => m_completeCallback -= value;
        }

        internal void SetProgress(float value) => m_Progress = value;

        /// Marks the operation done and invokes completion callbacks (main thread).
        internal void Complete()
        {
            if (m_IsDone) return;
            m_IsDone = true;
            var callback = m_completeCallback;
            m_completeCallback = null;
            if (callback == null) return;
            try
            {
                callback(this);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}

namespace OniMoono.Engine
{
    /// Process-wide settings the host establishes before any game code runs.
    internal static class EngineConfig
    {
        public static string DataPath = "";
        public static string StreamingAssetsPath = "";
        public static string PersistentDataPath = "";
        public static string TemporaryCachePath = "";
        public static string ConsoleLogPath = "";
        public static string CompanyName = "";
        public static string ProductName = "";
        public static string Version = "";
        public static string UnityVersion = "";
        public static bool IsBatchMode;
        /// The assemblies the game was built with (ScriptingAssemblies.json), by simple name; null
        /// in tests. JsonUtility handles composite types from these only (JsonLayout).
        public static System.Collections.Generic.HashSet<string> BuildAssemblies;
        public static bool Headless;
    }

    internal static class Languages
    {
        public static UnityEngine.SystemLanguage FromCulture(CultureInfo culture)
        {
            var name = culture.Name;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return name.Contains("TW", StringComparison.OrdinalIgnoreCase) || name.Contains("HK", StringComparison.OrdinalIgnoreCase) || name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    ? UnityEngine.SystemLanguage.ChineseTraditional
                    : UnityEngine.SystemLanguage.ChineseSimplified;
            return culture.TwoLetterISOLanguageName switch
            {
                "af" => UnityEngine.SystemLanguage.Afrikaans,
                "ar" => UnityEngine.SystemLanguage.Arabic,
                "eu" => UnityEngine.SystemLanguage.Basque,
                "be" => UnityEngine.SystemLanguage.Belarusian,
                "bg" => UnityEngine.SystemLanguage.Bulgarian,
                "ca" => UnityEngine.SystemLanguage.Catalan,
                "cs" => UnityEngine.SystemLanguage.Czech,
                "da" => UnityEngine.SystemLanguage.Danish,
                "nl" => UnityEngine.SystemLanguage.Dutch,
                "en" => UnityEngine.SystemLanguage.English,
                "et" => UnityEngine.SystemLanguage.Estonian,
                "fo" => UnityEngine.SystemLanguage.Faroese,
                "fi" => UnityEngine.SystemLanguage.Finnish,
                "fr" => UnityEngine.SystemLanguage.French,
                "de" => UnityEngine.SystemLanguage.German,
                "el" => UnityEngine.SystemLanguage.Greek,
                "he" => UnityEngine.SystemLanguage.Hebrew,
                "hu" => UnityEngine.SystemLanguage.Hungarian,
                "is" => UnityEngine.SystemLanguage.Icelandic,
                "id" => UnityEngine.SystemLanguage.Indonesian,
                "it" => UnityEngine.SystemLanguage.Italian,
                "ja" => UnityEngine.SystemLanguage.Japanese,
                "ko" => UnityEngine.SystemLanguage.Korean,
                "lv" => UnityEngine.SystemLanguage.Latvian,
                "lt" => UnityEngine.SystemLanguage.Lithuanian,
                "nb" or "nn" or "no" => UnityEngine.SystemLanguage.Norwegian,
                "pl" => UnityEngine.SystemLanguage.Polish,
                "pt" => UnityEngine.SystemLanguage.Portuguese,
                "ro" => UnityEngine.SystemLanguage.Romanian,
                "ru" => UnityEngine.SystemLanguage.Russian,
                "sr" or "hr" or "bs" => UnityEngine.SystemLanguage.SerboCroatian,
                "sk" => UnityEngine.SystemLanguage.Slovak,
                "sl" => UnityEngine.SystemLanguage.Slovenian,
                "es" => UnityEngine.SystemLanguage.Spanish,
                "sv" => UnityEngine.SystemLanguage.Swedish,
                "th" => UnityEngine.SystemLanguage.Thai,
                "tr" => UnityEngine.SystemLanguage.Turkish,
                "uk" => UnityEngine.SystemLanguage.Ukrainian,
                "vi" => UnityEngine.SystemLanguage.Vietnamese,
                "hi" => UnityEngine.SystemLanguage.Hindi,
                _ => UnityEngine.SystemLanguage.Unknown,
            };
        }
    }
}
