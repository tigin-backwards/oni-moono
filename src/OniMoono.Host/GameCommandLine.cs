#nullable enable
using System;
using System.IO;
using System.Reflection;

namespace OniMoono.Host
{
    /// The command line the game's code and its mods read (#53): Environment.GetCommandLineArgs()
    /// and Environment.CommandLine. In the original player they are the game's executable and
    /// every argument it was started with, Unity's own and unknown ones alike (measured with a
    /// test mod in the Linux and Windows players: tests/e2e/issue-53-command-line.yaml). Ours
    /// would otherwise give the host's: OniMoono.dll and our own options, `-- %command%` included.
    /// So the game gets the executable Steam named (or the game folder's, in the direct form)
    /// followed by Options.GameArguments.
    internal static class GameCommandLine
    {
        /// Environment.GetCommandLineArgs() for the game: the executable, then its arguments.
        public static string[] For(Options options, OniMoono.Engine.Platform.GameLayout layout) =>
            [options.GameExecutable ?? Executable(options.GameDir, layout), .. options.GameArguments];

        /// The game's executable in its folder, as a full path: OxygenNotIncluded.exe on Windows,
        /// OxygenNotIncluded on Linux, the bundle's executable on macOS.
        public static string Executable(string gameDir, OniMoono.Engine.Platform.GameLayout layout)
        {
            if (layout.IsMacBundle) return Path.Combine(layout.App, "Contents", "MacOS", OniMoono.Engine.Platform.GameLayout.MacExecutable);
            return Path.Combine(Path.GetFullPath(gameDir), OperatingSystem.IsWindows() ? "OxygenNotIncluded.exe" : "OxygenNotIncluded");
        }

        /// Makes Environment.GetCommandLineArgs() (and Environment.CommandLine, which .NET builds
        /// from it) answer `commandLine` from now on. The runtime keeps the process's arguments
        /// in Environment's s_commandLineArgs, which it sets before Main and GetCommandLineArgs
        /// copies (CoreLib, .NET 10); GameCommandLineTests checks that it still does. False when
        /// the field is gone (the host's own arguments stay).
        public static bool Install(string[] commandLine)
        {
            var field = typeof(Environment).GetField("s_commandLineArgs", BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(string[])) return false;
            field.SetValue(null, (string[])commandLine.Clone());
            return true;
        }
    }
}
