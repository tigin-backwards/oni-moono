#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using OniMoono.Host.Install;

namespace OniMoono.Host.Launch
{
    /// Program.Main's first step after the options: how this start came about (StandaloneLaunch)
    /// and, for a versioned install, which version plays (VersionedStart). The log is not open
    /// yet, so the lines wait in Log until it is.
    internal static class LaunchStep
    {
        public static readonly List<string> Log = new();

        /// Null to go on, else the process's exit code (a hand-off, a failure, a version switch).
        public static int? Run(Options options)
        {
            var machine = new SteamLibrary.Machine();
            var library = new SteamLibrary(machine);
            var clientLog = new List<string>();
            var client = new SteamClient(library, machine, clientLog.Add);
            var plan = StandaloneLaunch.Decide(options.GameExecutable != null, options.GameDirGiven, library, client,
                StandaloneLaunch.MemoryPath(options.RunHome, machine), installs => StandaloneLaunch.Choose(installs, machine, library.IsTest),
                TimeSpan.FromSeconds(90));
            Log.AddRange(plan.Log);
            Log.AddRange(clientLog);
            switch (plan.Mode)
            {
                case StandaloneLaunch.Mode.Standalone:
                    options.GameDir = plan.GameDir!;
                    StandaloneLaunch.SetAppId();
                    break;
                case StandaloneLaunch.Mode.HandOff:
                    client.Open(plan.Url!, plan.Root);
                    foreach (var line in Log) Console.Error.WriteLine(line);
                    if (plan.Message != null) StandaloneLaunch.Tell(plan.Message, error: false, library.IsTest);
                    return 0;
                case StandaloneLaunch.Mode.Failed:
                    foreach (var line in Log) Console.Error.WriteLine(line);
                    StandaloneLaunch.Tell(plan.Message!, error: true, library.IsTest);
                    return 2;
            }

            var installRoot = Environment.GetEnvironmentVariable(VersionedInstall.RootVariable) is { Length: > 0 } root ? root : null;
            if (installRoot != null && VersionedStart.Run(installRoot, options.GameDir, options.UseVersion, Log) is int exit)
            {
                foreach (var line in Log) Console.Error.WriteLine(line);
                return exit;
            }
            return null;
        }
    }
}
