#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OniMoono.Restarter
{
    /// What the Restarter starts again, and its command line (RestartCommandTests).
    internal static class RestartCommand
    {
        internal sealed record Plan(string Executable, string[] Arguments);

        /// The Restarter's arguments are EXECUTABLE [ARGUMENT...]: up to U60-761211 App.Restart
        /// passes only the executable; from 761654 on it adds the player's arguments too
        /// (Environment.GetCommandLineArgs()[1..]), each put in quotes as it is, so one holding a
        /// quote or ending in a backslash does not come through as it was. In ours those are the
        /// game's arguments (GameCommandLine), without the host's own options. The arguments the
        /// host recorded (ONI_MOONO_RESTART_ARGV, a JSON array: all of the host's, the game's
        /// among them) are exact and win, so a restart starts the player as it was started; the
        /// ones on the command line are used without them. Null without an executable.
        internal static Plan? From(string[] args, string? recordedArgv)
        {
            if (args.Length == 0 || args[0].Length == 0) return null;
            string[]? recorded = null;
            if (!string.IsNullOrEmpty(recordedArgv))
            {
                try { recorded = JsonSerializer.Deserialize<string[]>(recordedArgv); }
                catch (JsonException) { }
            }
            if (recorded != null && recorded.Any(a => a == null)) recorded = null;
            return new Plan(args[0], recorded ?? args[1..]);
        }

        /// The arguments as one Windows command line, read back into the same arguments by
        /// CommandLineToArgvW and the C runtime: an argument without white space or quotes as it
        /// is, any other in quotes, with a backslash before each quote in it and the backslashes
        /// before a quote (or the closing one) doubled. Klei's Restarter (761654) writes the same
        /// line (.NET's ProcessStartInfo.ArgumentList).
        internal static string Join(IEnumerable<string> arguments)
        {
            var line = new StringBuilder();
            foreach (var argument in arguments)
            {
                if (line.Length > 0) line.Append(' ');
                Append(line, argument);
            }
            return line.ToString();
        }

        static void Append(StringBuilder line, string argument)
        {
            if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
            {
                line.Append(argument);
                return;
            }
            line.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') line.Append('\\', backslashes * 2 + 1);
                else line.Append('\\', backslashes);
                backslashes = 0;
                line.Append(c);
            }
            line.Append('\\', backslashes * 2).Append('"');
        }
    }
}
