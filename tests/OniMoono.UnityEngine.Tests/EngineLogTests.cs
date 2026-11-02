using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using OniMoono.Engine;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // The game's own logger (Klei's Debug.Log) writes its [INFO] lines with Console.WriteLine; the
    // original player's log file holds them, so the player's log takes the console's output too.
    [Collection("Console")]
    public class EngineLogTests
    {
        [Fact]
        public void ConsoleOutputReachesTheLogFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"enginelog-{Guid.NewGuid():N}", "Player.log");
            var stdout = Console.Out;
            try
            {
                EngineLog.Open(path);
                Console.WriteLine("[INFO] from the game's logger");
                EngineLog.Engine("an engine line");
            }
            finally
            {
                EngineLog.Close();
                Console.SetOut(stdout);
            }
            var lines = File.ReadAllLines(path);
            Assert.Contains("[INFO] from the game's logger", lines);
            Assert.Single(lines, l => l == "[engine] an engine line");
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }

        // As Unity's player: the default log's last session is kept as Player-prev.log, replacing
        // the one before; a log named on the command line is simply overwritten.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ThePreviousSessionsLogIsKeptForTheDefaultLog(bool keepPrevious)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"enginelog-{Guid.NewGuid():N}");
            var path = Path.Combine(dir, "Player.log");
            var previous = Path.Combine(dir, "Player-prev.log");
            Directory.CreateDirectory(dir);
            File.WriteAllText(previous, "older session\n");
            File.WriteAllText(path, "last session\n");
            var stdout = Console.Out;
            try
            {
                EngineLog.Open(path, keepPrevious);
                EngineLog.Engine("this session");
            }
            finally
            {
                EngineLog.Close();
                Console.SetOut(stdout);
            }
            Assert.Equal(["[engine] this session"], File.ReadAllLines(path));
            Assert.Equal(keepPrevious ? "last session\n" : "older session\n", File.ReadAllText(previous));
            Directory.Delete(dir, true);
        }

        static readonly LogScrubber Scrubber = LogScrubber.Create(windows: true, @"C:\Users\Admin", @"E:\Documents", "Admin");

        /// The file's and the console's text after `action`, the log open with the scrubber.
        static (string File, string Console) Scrubbed(Action action)
        {
            var path = Path.Combine(Path.GetTempPath(), $"enginelog-{Guid.NewGuid():N}", "Player.log");
            var stdout = Console.Out;
            var console = new StringWriter();
            try
            {
                Console.SetOut(console);
                EngineLog.Open(path, scrubber: Scrubber);
                action();
            }
            finally
            {
                EngineLog.Close();
                Console.SetOut(stdout);
            }
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            Directory.Delete(Path.GetDirectoryName(path)!, true);
            return (text, console.ToString().Replace("\r\n", "\n"));
        }

        // Personal paths are replaced in the file and on the console; the log callbacks get the
        // message as logged (mods and the game's crash reporter read it there).
        [Fact]
        public void TheFileAndConsoleAreScrubbedButCallbacksAreNot()
        {
            const string message = @"Loading save file: E:\Documents\Klei\cloud_save_files\76561197960265729\Colony.sav";
            var got = new List<string>();
            var threaded = new List<string>();
            Application.LogCallback on = (c, s, t) => { if (c.StartsWith("Loading save file")) got.Add(c); };
            Application.LogCallback onThreaded = (c, s, t) => { if (c.StartsWith("Loading save file")) threaded.Add(c); };
            Application.logMessageReceived += on;
            Application.logMessageReceivedThreaded += onThreaded;
            (string File, string Console) text;
            try
            {
                text = Scrubbed(() =>
                {
                    UnityEngine.Debug.Log(message);
                    EngineLog.Engine(@"exe=C:\Users\Admin\OniMoono.exe");
                    Console.WriteLine("[INFO] C:/users/admin/x");
                });
            }
            finally
            {
                Application.logMessageReceived -= on;
                Application.logMessageReceivedThreaded -= onThreaded;
            }
            // logMessageReceived runs only on the engine's main thread, which a test's may not be;
            // both get the same text.
            if (Threads.IsMainThread) Assert.Equal([message], got);
            Assert.Equal([message], threaded);
            const string expected = @"Loading save file: %DOCUMENTS%\Klei\cloud_save_files\<steam id>\Colony.sav" + "\n"
                                    + @"[engine] exe=%USERPROFILE%\OniMoono.exe" + "\n[INFO] %USERPROFILE%/x\n";
            Assert.Equal(expected, text.File);
            Assert.Equal(expected, text.Console);
        }

        // The game's login line and KleiAccount's come from different threads (the main thread,
        // the HTTP reply's); every line reaches the file and the console whole and scrubbed, and
        // the callbacks get the name and IDs as logged.
        [Fact]
        public void AccountLinesFromManyThreadsAreScrubbed()
        {
            const string login = "[INFO] Logged into Steam with ID:76561199076905660, NAME:charmich0802";
            const string klei = "[INFO] [Account] Got login for user KU_Odm4vVjy";
            int raw = 0;
            Application.LogCallback onThreaded = (c, s, t) => { if (c == login) Interlocked.Increment(ref raw); };
            Application.logMessageReceivedThreaded += onThreaded;
            (string File, string Console) text;
            const int Threads = 8, Lines = 200;
            try
            {
                text = Scrubbed(() =>
                {
                    var threads = new Thread[Threads];
                    for (int t = 0; t < Threads; t++)
                    {
                        int n = t;
                        threads[t] = new Thread(() =>
                        {
                            for (int i = 0; i < Lines; i++)
                            {
                                if (n == 0) UnityEngine.Debug.Log(login);
                                else Console.WriteLine(n % 2 == 0 ? login : klei);
                            }
                        });
                        threads[t].Start();
                    }
                    foreach (var thread in threads) thread.Join();
                });
            }
            finally { Application.logMessageReceivedThreaded -= onThreaded; }
            Assert.Equal(Lines, raw);
            foreach (var output in new[] { text.File, text.Console })
            {
                var lines = output.TrimEnd('\n').Split('\n');
                Assert.Equal(Threads * Lines, lines.Length);
                Assert.All(lines, line => Assert.True(
                    line is "[INFO] Logged into Steam with ID:<steam id>, NAME:<name>" or "[INFO] [Account] Got login for user <klei id>", line));
            }
        }

        // A path written in pieces is replaced once its line is whole; a partial line still goes
        // out, in order, before the next line of other output, and on Close.
        [Fact]
        public void APathSplitAcrossWritesIsScrubbed()
        {
            var text = Scrubbed(() =>
            {
                Console.Write(@"path C:\Us");
                Console.Write('e');
                Console.Write(@"rs\Adm");
                Console.Write("in\\x\nnext ");
                Console.Out.Write(@"C:\Users".ToCharArray());
                Console.WriteLine(@"\Admin\y");
                Console.Write("partial ");
                EngineLog.Engine("an engine line");
                Console.Write(@"tail C:\Users\Admin");
            });
            const string expected = "path %USERPROFILE%\\x\nnext %USERPROFILE%\\y\npartial [engine] an engine line\ntail %USERPROFILE%";
            Assert.Equal(expected, text.File);
            Assert.Equal(expected, text.Console);
        }

        [Fact]
        public void WithoutAScrubberPathsAreWrittenAsLogged()
        {
            var path = Path.Combine(Path.GetTempPath(), $"enginelog-{Guid.NewGuid():N}", "Player.log");
            var stdout = Console.Out;
            try
            {
                EngineLog.Open(path);
                Console.Write(@"C:\Users\Ad");
                Console.WriteLine(@"min\x");
            }
            finally
            {
                EngineLog.Close();
                Console.SetOut(stdout);
            }
            Assert.Equal([@"C:\Users\Admin\x"], File.ReadAllLines(path));
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }

        /// A console writer that takes Console.Out's lock on every write, as .NET's console stream
        /// does on Linux and macOS (ConsolePal.Unix.cs, WriteFromConsoleStream: `lock
        /// (Console.Out)` around each write, whatever stdout is). It waits for the lock at most
        /// Patience and then throws, counting it, so a lock-order inversion fails the test
        /// instead of hanging it.
        sealed class UnixConsole : TextWriter
        {
            static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);
            readonly StringBuilder text = new();
            public int GaveUp;

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {
                var console = Console.Out;
                if (!Monitor.TryEnter(console, Patience))
                {
                    Interlocked.Increment(ref GaveUp);
                    throw new TimeoutException("the console's lock was held by a thread waiting for this one");
                }
                try { lock (text) text.Append(value); }
                finally { Monitor.Exit(console); }
            }

            public override string ToString() { lock (text) return text.ToString().Replace("\r\n", "\n"); }
        }

        /// Runs `other` on another thread while this one holds Console.Out's lock, as the game's
        /// Console.WriteLine does, and once that thread waits, runs `mine` (still holding it).
        /// What `other` threw, if anything.
        static Exception WhileTheConsoleIsLocked(Action other, Action mine)
        {
            Exception thrown = null;
            var thread = new Thread(() =>
            {
                try { other(); }
                catch (Exception e) { thrown = e; }
            });
            lock (Console.Out)
            {
                thread.Start();
                var until = DateTime.UtcNow.AddSeconds(10);
                while ((thread.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) == 0 && DateTime.UtcNow < until)
                    Thread.Sleep(1);
                mine();
            }
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the other thread did not finish");
            return thrown;
        }

        // An engine line from another thread (the presenter's splash lines at start-up) while the
        // game's Console.WriteLine holds the console: the engine line waits for the console first,
        // as that write does, and does not take the log's lock that the write needs next. Taking
        // them the other way round hung macOS start-ups (#44).
        [Fact]
        public void AnEngineLineFromAnotherThreadWaitsForTheConsoleBeforeTheLog()
        {
            var path = Path.Combine(Path.GetTempPath(), $"enginelog-{Guid.NewGuid():N}", "Player.log");
            var stdout = Console.Out;
            var console = new UnixConsole();
            Exception thrown;
            try
            {
                Console.SetOut(console);
                EngineLog.Open(path);
                EngineLog.Engine("first");
                thrown = WhileTheConsoleIsLocked(
                    () => EngineLog.Engine("from another thread"),
                    () => Console.WriteLine("[INFO] from the game's logger"));
            }
            finally
            {
                EngineLog.Close();
                Console.SetOut(stdout);
            }
            Assert.Null(thrown);
            Assert.Equal(0, console.GaveUp);
            const string expected = "[engine] first\n[INFO] from the game's logger\n[engine] from another thread\n";
            Assert.Equal(expected, File.ReadAllText(path).Replace("\r\n", "\n"));
            Assert.Equal(expected, console.ToString());
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }

        // Close writes the pending text to the console, so it too waits for the console first:
        // here another thread holds it and writes an engine line.
        [Fact]
        public void CloseWaitsForTheConsoleBeforeTheLog()
        {
            var path = Path.Combine(Path.GetTempPath(), $"enginelog-{Guid.NewGuid():N}", "Player.log");
            var stdout = Console.Out;
            var console = new UnixConsole();
            Exception thrown;
            try
            {
                Console.SetOut(console);
                EngineLog.Open(path, scrubber: Scrubber);
                Console.Write("partial ");
                thrown = WhileTheConsoleIsLocked(EngineLog.Close, () => EngineLog.Engine("an engine line"));
            }
            finally
            {
                EngineLog.Close();
                Console.SetOut(stdout);
            }
            Assert.Null(thrown);
            Assert.Equal(0, console.GaveUp);
            const string expected = "partial [engine] an engine line\n";
            Assert.Equal(expected, File.ReadAllText(path).Replace("\r\n", "\n"));
            Assert.Equal(expected, console.ToString());
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
