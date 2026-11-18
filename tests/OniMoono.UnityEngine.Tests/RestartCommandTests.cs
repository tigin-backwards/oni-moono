#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text;
using OniMoono.Restarter;
using Xunit;

namespace OniMoono.Tests
{
    /// The Windows Restarter's arguments (src/OniMoono.Restarter/RestartCommand.cs, compiled in
    /// here): what it starts again, and the command line it starts it with.
    public class RestartCommandTests
    {
        const string Exe = @"C:\Moono\OniMoono.exe";

        /// A Windows command line split as the C runtime and CommandLineToArgvW split the
        /// arguments after the program name: white space separates outside quotes; 2n
        /// backslashes before a quote are n and the quote opens or closes, 2n+1 are n and a
        /// literal quote; other backslashes are literal; "" inside quotes is a literal quote.
        static List<string> Split(string line)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, any = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\')
                {
                    int n = 0;
                    while (i < line.Length && line[i] == '\\') { n++; i++; }
                    if (i < line.Length && line[i] == '"')
                    {
                        current.Append('\\', n / 2);
                        if (n % 2 == 1) current.Append('"');
                        else i--;
                    }
                    else { current.Append('\\', n); i--; }
                    any = true;
                }
                else if (c == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else quoted = !quoted;
                    any = true;
                }
                else if ((c == ' ' || c == '\t') && !quoted)
                {
                    if (any) { args.Add(current.ToString()); current.Clear(); any = false; }
                }
                else { current.Append(c); any = true; }
            }
            if (any) args.Add(current.ToString());
            return args;
        }

        [Fact]
        public void TheLineIsTheOneKleisRestarterWrites()
            // Measured: Klei's 761654 Restarter.exe given these arguments started its target with
            // this command line after the program name.
            => Assert.Equal(@"--renderer vulkan ""a b"" """" ""x\""y"" tr\ plain",
                RestartCommand.Join(["--renderer", "vulkan", "a b", "", "x\"y", @"tr\", "plain"]));

        [Theory]
        [InlineData("plain")]
        [InlineData("")]
        [InlineData("a b")]
        [InlineData("tab\there")]
        [InlineData("x\"y")]
        [InlineData("\"")]
        [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\OxygenNotIncluded\OxygenNotIncluded.exe")]
        [InlineData(@"C:\a dir\")]
        [InlineData(@"C:\a dir\\")]
        [InlineData(@"C:\nospace\")]
        [InlineData(@"back\""slash")]
        [InlineData(@"\\server\share\with space")]
        [InlineData(@"two\\""quotes""")]
        public void EachArgumentComesBackAsItWas(string argument)
        {
            string[] arguments = ["--gc", "fast", argument, "--frame-stats"];
            Assert.Equal(arguments, Split(RestartCommand.Join(arguments)));
        }

        [Fact]
        public void TheSteamLaunchOptionsFormComesBackWhole()
        {
            // OniMoono.exe --renderer vulkan --dxgi on -- %command%, with the game's own options.
            string[] arguments = ["--renderer", "vulkan", "--dxgi", "on", "--", @"C:\Program Files (x86)\Steam\steamapps\common\OxygenNotIncluded\OxygenNotIncluded.exe", "-popupwindow"];
            Assert.Equal(arguments, Split(RestartCommand.Join(arguments)));
        }

        [Fact]
        public void U59AndEarlierU60BuildsPassOnlyTheExecutable()
        {
            var plan = RestartCommand.From([Exe], null)!;
            Assert.Equal(Exe, plan.Executable);
            Assert.Empty(plan.Arguments);
            // The host's record gives the arguments.
            Assert.Equal(["--gc", "fast"], RestartCommand.From([Exe], """["--gc","fast"]""")!.Arguments);
        }

        [Fact]
        public void TheRecordedArgumentsWinOverTheCommandLines()
        {
            // 761654's App.Restart puts each argument in quotes as it is: a trailing backslash then
            // escapes the closing quote, and the Restarter's own arguments run together.
            var restarterArgs = Split(@"""C:\Moono\OniMoono.exe"" ""--game"" ""D:\ONI\"" ""--gc"" ""fast""");
            Assert.Equal([Exe, "--game", @"D:\ONI"" --gc fast"], restarterArgs);
            var plan = RestartCommand.From([.. restarterArgs], """["--game","D:\\ONI\\","--gc","fast"]""")!;
            Assert.Equal(Exe, plan.Executable);
            Assert.Equal(["--game", @"D:\ONI\", "--gc", "fast"], plan.Arguments);
        }

        [Fact]
        public void WithoutARecordTheCommandLinesArgumentsAreUsed()
        {
            string[] args = [Exe, "--renderer", "vulkan", "--", @"C:\Games\ONI\OxygenNotIncluded.exe"];
            Assert.Equal(args[1..], RestartCommand.From(args, null)!.Arguments);
            Assert.Equal(args[1..], RestartCommand.From(args, "")!.Arguments);
            Assert.Equal(args[1..], RestartCommand.From(args, "not json")!.Arguments);
            Assert.Equal(args[1..], RestartCommand.From(args, "[null]")!.Arguments);
        }

        [Fact]
        public void TheRecordedEntryPointStartsInPlaceOfTheExecutable()
        {
            // The versioned install's entry point (ONI_MOONO_RESTART_EXE, RestartEntry), with the
            // recorded arguments as before; an empty record is none.
            const string entry = @"C:\Users\a\AppData\Local\oni-moono\versioned\OniMoono.exe";
            string[] args = [@"C:\Users\a\AppData\Local\oni-moono\versioned\player\2026.11.16.04-u60\OniMoono.exe", "--", "x"];
            var plan = RestartCommand.From(args, """["--","C:\\Games\\ONI\\OxygenNotIncluded.exe"]""", entry)!;
            Assert.Equal(entry, plan.Executable);
            Assert.Equal(["--", @"C:\Games\ONI\OxygenNotIncluded.exe"], plan.Arguments);
            Assert.Equal(args[0], RestartCommand.From(args, null, "")!.Executable);
            Assert.Equal(args[0], RestartCommand.From(args, null)!.Executable);
            Assert.Null(RestartCommand.From([], null, entry));
        }

        /// The Linux Restarter (src/OniMoono.Host/Restarter): once its parent (the player) has
        /// exited, the recorded entry point starts in place of the executable it was given, with
        /// the recorded arguments; without the record, that executable.
        [Fact]
        public void TheLinuxScriptStartsTheRecordedEntryPoint()
        {
            if (OperatingSystem.IsWindows()) return;
            var dir = Path.Combine(Path.GetTempPath(), "oni-moono-restarter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var script = Path.Combine(RepoRoot(), "src", "OniMoono.Host", "Restarter");
                var output = Path.Combine(dir, "started.txt");
                string Target(string name)
                {
                    var path = Path.Combine(dir, name);
                    File.WriteAllText(path, $"#!/bin/sh\nprintf '%s\\n' \"$0\" \"$@\" > '{output}'\n");
                    File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
                    return path;
                }
                var player = Target("player");
                var entry = Target("entry");
                string[] Run(string? recordedExe)
                {
                    File.Delete(output);
                    // The player: a shell that starts the Restarter and exits a moment later.
                    var start = new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sh \"$0\" \"$1\" --given & sleep 0.5", script, player } };
                    start.Environment["ONI_MOONO_RESTART_ARGS"] = "'--game' 'a b' '--' '/games/ONI/OxygenNotIncluded'";
                    if (recordedExe != null) start.Environment["ONI_MOONO_RESTART_EXE"] = recordedExe;
                    else start.Environment.Remove("ONI_MOONO_RESTART_EXE");
                    using (var shell = Process.Start(start)!) shell.WaitForExit();
                    for (int i = 0; i < 200 && !File.Exists(output); i++) Thread.Sleep(50);
                    Thread.Sleep(100);
                    return File.Exists(output) ? File.ReadAllLines(output) : ["(not started)"];
                }
                Assert.Equal([entry, "--game", "a b", "--", "/games/ONI/OxygenNotIncluded"], Run(entry));
                Assert.Equal(player, Run(null)[0]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        static string RepoRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

        [Fact]
        public void NoExecutableIsAUsageError()
        {
            Assert.Null(RestartCommand.From([], null));
            Assert.Null(RestartCommand.From([""], "[]"));
        }
    }
}
