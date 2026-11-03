using System.Collections.Generic;
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
        public void NoExecutableIsAUsageError()
        {
            Assert.Null(RestartCommand.From([], null));
            Assert.Null(RestartCommand.From([""], "[]"));
        }
    }
}
