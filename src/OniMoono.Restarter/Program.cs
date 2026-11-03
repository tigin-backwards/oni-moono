using System;
using System.Diagnostics;

namespace OniMoono.Restarter
{
    /// Restarter EXECUTABLE [ARGUMENT...] (Windows): started by the game's App.Restart before the
    /// player quits (e.g. after a language change). Once the player has exited, the executable is
    /// started again with the arguments the player was started with (RestartCommand), which the
    /// host records in ONI_MOONO_RESTART_ARGV with its process id in ONI_MOONO_RESTART_PID.
    /// Klei's Restarter starts the game at once, without waiting, in the executable's folder;
    /// this one waits for the player and keeps the folder it was started in (the player's), so a
    /// relative path among the arguments (--game) still finds the same files.
    internal static class Program
    {
        static int Main(string[] args)
        {
            var plan = RestartCommand.From(args, Environment.GetEnvironmentVariable("ONI_MOONO_RESTART_ARGV"));
            if (plan == null)
            {
                Console.Error.WriteLine("usage: Restarter EXECUTABLE [ARGUMENT...]");
                return 2;
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("ONI_MOONO_RESTART_PID"), out int pid))
            {
                try
                {
                    using var player = Process.GetProcessById(pid);
                    player.WaitForExit();
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
            }
            Process.Start(new ProcessStartInfo(plan.Executable, RestartCommand.Join(plan.Arguments)) { UseShellExecute = false });
            return 0;
        }
    }
}
