#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace OniMoono.Host.Launch
{
    /// What the standalone launch asks of the Steam client: is it running, start it, open a
    /// steam:// link. Test runs (SteamLibrary.RootsVariable set) never reach the real client:
    /// ONI_MOONO_STEAM_START names a command to run instead of starting Steam (nothing runs
    /// without one), and a link is only logged, to ONI_MOONO_STEAM_OPENED's file when given.
    internal sealed class SteamClient
    {
        public const string StartVariable = "ONI_MOONO_STEAM_START", OpenedVariable = "ONI_MOONO_STEAM_OPENED";

        readonly SteamLibrary library;
        readonly SteamLibrary.Machine machine;
        readonly Action<string> log;
        /// Tests: the clock and the pause between looks, in place of the real ones.
        internal Func<TimeSpan> Elapsed;
        internal Action<TimeSpan> Sleep = Thread.Sleep;
        internal Func<int, bool> Alive = ProcessAlive;

        public SteamClient(SteamLibrary library, SteamLibrary.Machine machine, Action<string> log)
        {
            this.library = library;
            this.machine = machine;
            this.log = log;
            var watch = Stopwatch.StartNew();
            Elapsed = () => watch.Elapsed;
        }

        static bool ProcessAlive(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }

        public (bool Running, bool LoggedIn) State() => library.ClientState(Alive);

        /// Steam running with an account logged in: as found, or after starting it and waiting
        /// up to `timeout`. False when it never got there (the caller hands off or gives up).
        public bool EnsureRunning(SteamLibrary.Root? root, TimeSpan timeout)
        {
            var state = State();
            if (state.LoggedIn) return true;
            if (!state.Running)
            {
                log("launch: Steam is not running; starting it");
                if (!Start(root)) return false;
            }
            else log("launch: Steam is running but no account is logged in yet; waiting");
            var until = Elapsed() + timeout;
            while (Elapsed() < until)
            {
                Sleep(TimeSpan.FromMilliseconds(500));
                if (State().LoggedIn)
                {
                    log("launch: Steam is up");
                    return true;
                }
            }
            log($"launch: Steam did not come up within {timeout.TotalSeconds:F0} s");
            return false;
        }

        bool Start(SteamLibrary.Root? root)
        {
            if (library.IsTest)
            {
                var command = machine.Env(StartVariable);
                if (string.IsNullOrEmpty(command))
                {
                    log("launch: (test run: no ONI_MOONO_STEAM_START, so nothing is started)");
                    return false;
                }
                return Run(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", OperatingSystem.IsWindows() ? ["/c", command] : ["-c", command]);
            }
            if (machine.Windows)
            {
                var exe = machine.Registry(@"Software\Valve\Steam", "SteamExe") as string;
                if (string.IsNullOrEmpty(exe) && root != null) exe = Path.Combine(root.Dir, "steam.exe");
                return exe != null && Run(exe, ["-silent"]);
            }
            return root?.Sandbox switch
            {
                SteamLibrary.Sandbox.Flatpak => Run("flatpak", ["run", "com.valvesoftware.Steam", "-silent"]),
                SteamLibrary.Sandbox.Snap => Run("snap", ["run", "steam", "-silent"]),
                _ => Run("steam", ["-silent"]),
            };
        }

        bool Run(string file, string[] args)
        {
            try
            {
                var start = new ProcessStartInfo(file) { UseShellExecute = false };
                foreach (var a in args) start.ArgumentList.Add(a);
                using var p = Process.Start(start);
                return p != null;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                log($"launch: could not start {file}: {e.Message}");
                return false;
            }
        }

        /// Opens a steam:// link with the Steam client (`steam://rungameid/457140`).
        public bool Open(string url, SteamLibrary.Root? root)
        {
            if (library.IsTest)
            {
                log($"launch: (test run) would open {url}");
                if (machine.Env(OpenedVariable) is { Length: > 0 } file) File.AppendAllText(file, url + "\n");
                return true;
            }
            if (machine.Windows)
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return true;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    log($"launch: could not open {url}: {e.Message}");
                    return false;
                }
            }
            return root?.Sandbox switch
            {
                SteamLibrary.Sandbox.Flatpak => Run("flatpak", ["run", "com.valvesoftware.Steam", url]),
                SteamLibrary.Sandbox.Snap => Run("snap", ["run", "steam", url]),
                _ => Run("steam", [url]) || Run("xdg-open", [url]),
            };
        }
    }
}
