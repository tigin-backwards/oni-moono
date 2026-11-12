using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace Doorstop
{
    /// TEST INFRASTRUCTURE for comparisons with the original Windows player, never shipped: Unity
    /// Doorstop starts it in a renamed copy of the game (scripts/original-windows.sh) before the
    /// game's own assemblies load, and it gives the original the same isolation our host gives
    /// itself (OniMoono.Host RunHome and AudioCapture). Documents and the application data
    /// folders answer folders under ONI_RUN_HOME, so saves, settings, mods and the write test stay
    /// out of the user's own; the working folder is the run home; FMOD writes the game's audio to
    /// fmodoutput.wav there instead of the sound device. Without ONI_RUN_HOME, or when a
    /// redirection fails, the player is ended before the game reads any user data.
    ///
    /// No BepInEx: its HarmonyX would replace the game's 0Harmony, and mods built against the
    /// game's Harmony then fail (Harmony.PatchCategory, CodeInstruction.LoadsConstant). This
    /// patches with the game's own 0Harmony, which Mono finds in the Managed folder.
    ///
    /// It confirms each step in RUN_HOME\original-isolation.txt: "redirected" once the folders
    /// answer the run home's (the launcher ends the player when that line is late), "fmod" once
    /// FMOD's output is the WAV writer.
    public static class Entrypoint
    {
        static string home = "", documents = "", localAppData = "", appData = "";
        static Harmony harmony;
        static bool fmodDone;

        // Doorstop 4 calls Doorstop.Entrypoint.Start().
        public static void Start()
        {
            try
            {
                Isolate();
            }
            catch (Exception e)
            {
                Fail("isolation failed: " + e);
            }
        }

        static void Isolate()
        {
            home = Environment.GetEnvironmentVariable("ONI_RUN_HOME") ?? "";
            if (home.Length == 0 || !Path.IsPathRooted(home))
                Fail("ONI_RUN_HOME (an absolute path) is not set");
            home = Path.GetFullPath(home).TrimEnd('\\');
            documents = Path.Combine(home, "Documents");
            localAppData = Path.Combine(home, "AppData", "Local");
            appData = Path.Combine(home, "AppData", "Roaming");
            Directory.CreateDirectory(documents);
            Directory.CreateDirectory(localAppData);
            Directory.CreateDirectory(appData);
            Environment.CurrentDirectory = home;
            harmony = new Harmony("oni-moono.original-isolation");
            var withOption = typeof(Environment).GetMethod(nameof(Environment.GetFolderPath), new[] { typeof(Environment.SpecialFolder), typeof(Environment.SpecialFolderOption) });
            var plain = typeof(Environment).GetMethod(nameof(Environment.GetFolderPath), new[] { typeof(Environment.SpecialFolder) });
            harmony.Patch(withOption, prefix: new HarmonyMethod(typeof(Entrypoint), nameof(FolderPrefix)));
            harmony.Patch(plain, prefix: new HarmonyMethod(typeof(Entrypoint), nameof(FolderPrefix)));
            var folders = new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData };
            var wanted = new[] { documents, localAppData, appData };
            for (int i = 0; i < folders.Length; i++)
            {
                var got = Environment.GetFolderPath(folders[i]);
                if (got != wanted[i])
                    Fail($"{folders[i]} still answers {got}");
            }
            if (!string.Equals(Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\'), home, StringComparison.OrdinalIgnoreCase))
                Fail($"the working folder is still {Environment.CurrentDirectory}");
            Confirm($"redirected: Documents {documents}, local application data {localAppData}, application data {appData}, working folder {home}; Harmony {typeof(Harmony).Assembly.Location}");
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryFmod(e.LoadedAssembly);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                TryFmod(a);
        }

        static void TryFmod(Assembly a)
        {
            if (fmodDone || a.GetName().Name != "FMODUnity")
                return;
            fmodDone = true;
            try
            {
                var m = a.GetType("FMODUnity.Platform")?.GetMethod("GetOutputType", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null)
                    Fail("FMODUnity.Platform.GetOutputType not found: the game's audio would reach the speakers");
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(Entrypoint), nameof(OutputPrefix)));
                Confirm($"fmod: output is the WAV writer, {Path.Combine(home, "fmodoutput.wav")}");
            }
            catch (Exception e)
            {
                Fail("FMOD's output not redirected: " + e);
            }
        }

        static bool FolderPrefix(Environment.SpecialFolder folder, ref string __result)
        {
            switch (folder)
            {
                case Environment.SpecialFolder.MyDocuments:
                    __result = documents;
                    return false;
                case Environment.SpecialFolder.LocalApplicationData:
                    __result = localAppData;
                    return false;
                case Environment.SpecialFolder.ApplicationData:
                    __result = appData;
                    return false;
                default:
                    return true;
            }
        }

        static bool OutputPrefix(ref FMOD.OUTPUTTYPE __result)
        {
            __result = FMOD.OUTPUTTYPE.WAVWRITER;
            return false;
        }

        // The launcher reads the file while it waits: a write it holds up is tried again.
        static void Confirm(string line)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    File.AppendAllText(Path.Combine(home, "original-isolation.txt"), line + "\r\n");
                    return;
                }
                catch (IOException) when (i < 40)
                {
                    System.Threading.Thread.Sleep(25);
                }
            }
        }

        static void Fail(string why)
        {
            try
            {
                if (home.Length > 0 && Path.IsPathRooted(home))
                    File.AppendAllText(Path.Combine(home, "original-isolation.txt"), "FAILED: " + why + ": ending the player\r\n");
            }
            catch
            {
            }
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            throw new InvalidOperationException(why);
        }
    }
}
