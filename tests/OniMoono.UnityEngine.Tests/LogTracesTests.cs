using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OniMoono.Engine;
using UnityEngine;
using Xunit;

// Outside the OniMoono namespaces: the engine's own frames are left out of logged stack traces.
namespace LogTraceSubjects
{
    public static class Thrower
    {
        public static Exception Thrown()
        {
            try { Throw(); }
            catch (Exception e) { return e; }
            return null;
        }

        static void Throw() => throw new InvalidOperationException("thrown");

        public static Exception WithInner()
        {
            try
            {
                try { Throw(); }
                catch (Exception e) { throw new ArgumentException("outer", e); }
            }
            catch (Exception e) { return e; }
            return null;
        }

        // As the host gives a mod's method that Mono's compilation fails (MonoMissingTypes): its
        // frame is hidden, so the trace starts at its caller as on Mono.
        [System.Diagnostics.StackTraceHidden]
        static void Hidden() => throw new MissingMethodException("Method not found: void .SimMessages.SetCellProperties(int,byte)");

        public static Exception FromHidden()
        {
            try { Hidden(); }
            catch (Exception e) { return e; }
            return null;
        }

        public static void Report(Exception e) => UnityEngine.Debug.LogException(e);

        public static void ReportError(string message) => UnityEngine.Debug.LogError(message);
    }

    public class Outer<TA>
    {
        public class Inner
        {
            public static Exception Thrown(TA value, int[] counts)
            {
                try { throw new InvalidOperationException("generic " + value + counts.Length); }
                catch (Exception e) { return e; }
            }
        }
    }

    public class ThrowsInUpdate : MonoBehaviour
    {
        void Update() => throw new InvalidOperationException("from update");
    }
}

namespace OniMoono.Tests
{
    using LogTraceSubjects;

    /// What the original (release) player writes for each log type, measured with UnityProbe's
    /// logging scenario: messages carry no stack trace, in the log or for the callbacks;
    /// exceptions carry their own frames in Mono's format, then the logging call's frames in
    /// Unity's. Signatures of scripts/qa-tools.py read the frame after an error line, so a trace
    /// the original lacks named Debug.LogError where the original's log names the next message.
    [Collection("Console")]
    public class LogTracesTests : EngineTest
    {
        static List<(string condition, string stack, LogType type)> Received(Action action, string marker)
        {
            var got = new List<(string, string, LogType)>();
            Application.LogCallback on = (c, s, t) => { if (c.Contains(marker)) got.Add((c, s, t)); };
            Application.logMessageReceived += on;
            try { action(); }
            finally { Application.logMessageReceived -= on; }
            return got;
        }

        static string LogText(Action action)
        {
            var path = Path.Combine(Path.GetTempPath(), $"logtraces-{Guid.NewGuid():N}", "Player.log");
            var stdout = Console.Out;
            try
            {
                EngineLog.Open(path);
                action();
            }
            finally
            {
                EngineLog.Close();
                Console.SetOut(stdout);
            }
            var text = File.ReadAllText(path);
            Directory.Delete(Path.GetDirectoryName(path)!, true);
            return text;
        }

        [Fact]
        public void MessagesOfEveryTypeHaveNoStackTrace()
        {
            var got = Received(() =>
            {
                UnityEngine.Debug.Log("lt-msg log");
                UnityEngine.Debug.LogWarning("lt-msg warning");
                Thrower.ReportError("lt-msg error");
                UnityEngine.Debug.LogErrorFormat("lt-msg {0}", "errorformat");
                UnityEngine.Debug.unityLogger.Log(LogType.Exception, "lt-msg exception type");
            }, "lt-msg");
            Assert.Equal(5, got.Count);
            Assert.All(got, g => Assert.Equal("", g.stack));
            Assert.Equal(new[] { LogType.Log, LogType.Warning, LogType.Error, LogType.Error, LogType.Exception }, got.Select(g => g.type));
        }

        [Fact]
        public void AnErrorIsALineOfItsOwnInTheLog()
        {
            var text = LogText(() =>
            {
                Thrower.ReportError("lt-line error");
                UnityEngine.Debug.Log("lt-line next");
            });
            Assert.Contains("lt-line error\nlt-line next\n", text.Replace("\r\n", "\n"));
        }

        [Fact]
        public void AnExceptionHasItsFramesInMonosFormatThenTheCall()
        {
            var e = Thrower.Thrown();
            string text = null;
            var got = Received(() => text = LogText(() => Thrower.Report(e)), "thrown");
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int at = Array.IndexOf(lines, "InvalidOperationException: thrown");
            Assert.True(at >= 0, text);
            Assert.Matches(@"^  at LogTraceSubjects\.Thrower\.Throw \(\) \[0x[0-9a-f]{5}\] in .+:\d+ $", lines[at + 1]);
            Assert.Matches(@"^  at LogTraceSubjects\.Thrower\.Thrown \(\) \[0x[0-9a-f]{5}\] in .+:\d+ $", lines[at + 2]);
            // Unity's frames of the call, as the original's log has them.
            Assert.Equal("UnityEngine.DebugLogHandler:Internal_LogException_Injected(Exception, IntPtr)", lines[at + 3]);
            Assert.Equal("UnityEngine.DebugLogHandler:Internal_LogException(Exception, Object)", lines[at + 4]);
            Assert.Equal("UnityEngine.DebugLogHandler:LogException(Exception, Object)", lines[at + 5]);
            Assert.Equal("UnityEngine.Logger:LogException(Exception, Object)", lines[at + 6]);
            Assert.Equal("UnityEngine.Debug:LogException(Exception)", lines[at + 7]);
            Assert.Equal("LogTraceSubjects.Thrower:Report(Exception)", lines[at + 8]);
            // The engine's frames (this test's, too) are left out; the trace ends with an empty line.
            Assert.DoesNotContain(lines.Skip(at).TakeWhile(l => l != ""), l => l.StartsWith("OniMoono") || l.StartsWith("  at OniMoono"));
            Assert.Contains("", lines.Skip(at));

            var (condition, stack, type) = Assert.Single(got);
            Assert.Equal(LogType.Exception, type);
            Assert.Equal("InvalidOperationException: thrown", condition);
            var frames = stack.Split('\n');
            Assert.Matches(@"^LogTraceSubjects\.Thrower\.Throw \(\) \(at .+:\d+\)$", frames[0]);
            Assert.Matches(@"^LogTraceSubjects\.Thrower\.Thrown \(\) \(at .+:\d+\)$", frames[1]);
            Assert.Equal("UnityEngine.Debug:LogException(Exception)", frames[2]);
            Assert.Equal("LogTraceSubjects.Thrower:Report(Exception)", frames[3]);
        }

        [Fact]
        public void TheInnermostExceptionComesFirst()
        {
            var e = Thrower.WithInner();
            var (condition, stack, _) = Assert.Single(Received(() => Thrower.Report(e), "thrown"));
            Assert.Equal("InvalidOperationException: thrown", condition);
            var frames = stack.Split('\n');
            Assert.StartsWith("LogTraceSubjects.Thrower.Throw () (at ", frames[0]);
            Assert.StartsWith("LogTraceSubjects.Thrower.WithInner () (at ", frames[1]);
            Assert.Equal("Rethrow as ArgumentException: outer", frames[2]);
            Assert.StartsWith("LogTraceSubjects.Thrower.WithInner () (at ", frames[3]);
            Assert.Equal("UnityEngine.Debug:LogException(Exception)", frames[4]);
        }

        static void ThrowInEngine(Exception e) => throw e;

        static Exception ThrownInEngine(Exception e)
        {
            try { ThrowInEngine(e); }
            catch (Exception caught) { return caught; }
            return null;
        }

        // Issue #79: an OutOfMemoryException from the engine's own code (all its frames left out
        // of the original's format) said neither where it was thrown nor how much memory was in use.
        [Fact]
        public void AnEngineFaultFollowsWithItsFramesAndMemory()
        {
            var e = ThrownInEngine(new OutOfMemoryException());
            var text = LogText(() => UnityEngine.Debug.LogException(e)).Replace("\r\n", "\n");
            var lines = text.Split('\n');
            int at = Array.IndexOf(lines, "OutOfMemoryException: Insufficient memory to continue the execution of the program.");
            Assert.True(at >= 0, text);
            // The original's format first, the engine's frames still left out of it.
            Assert.DoesNotContain(lines.Skip(at).TakeWhile(l => l != ""), l => l.Contains("OniMoono"));
            Assert.Matches(new Regex(@"\n\[engine\] memory at an OutOfMemoryException: working set [\d,]+ MB \(peak [\d,]+ MB\), private [\d,]+ MB, managed heap [\d,]+ MB \(committed [\d,]+ MB, fragmented [\d,]+ MB\), collections \d+/\d+/\d+"), text);
            Assert.Matches(new Regex(@"\n\[engine\] engine fault: System\.OutOfMemoryException: Insufficient memory to continue the execution of the program\.\n\s+at OniMoono\.Tests\.LogTracesTests\.ThrowInEngine"), text);
        }

        [Fact]
        public void AScriptsExceptionHasNoEngineLines()
        {
            var e = Thrower.Thrown();
            var text = LogText(() => Thrower.Report(e));
            Assert.Contains("InvalidOperationException: thrown", text);
            Assert.DoesNotContain("engine fault", text);
            Assert.DoesNotContain("memory at an OutOfMemoryException", text);
        }

        [Fact]
        public void AnExceptionFromUpdateHasItsOwnFramesAlone()
        {
            var go = new GameObject("lt-update");
            go.AddComponent<ThrowsInUpdate>();
            var got = Received(() => Frame(), "from update");
            UnityEngine.Object.DestroyImmediate(go);
            var (condition, stack, type) = Assert.Single(got);
            Assert.Equal(LogType.Exception, type);
            Assert.Equal("InvalidOperationException: from update", condition);
            Assert.Matches(new Regex(@"^LogTraceSubjects\.ThrowsInUpdate\.Update \(\) \(at .+:\d+\)\n$"), stack);
        }

        [Fact]
        public void AGenericFrameIsNamedByItsDefinitionAsMonoNamesIt()
        {
            // As the original's logs name them: "StateMachine`4+GenericInstance[StateMachineType,…]".
            var e = Outer<int>.Inner.Thrown(1, new int[2]);
            var (_, stack, _) = Assert.Single(Received(() => Thrower.Report(e), "generic 12"));
            Assert.StartsWith("LogTraceSubjects.Outer`1+Inner[TA].Thrown (TA value, System.Int32[] counts) (at ", stack);
        }

        [Fact]
        public void AHiddenFrameIsLeftOut()
        {
            var e = Thrower.FromHidden();
            string text = null;
            var got = Received(() => text = LogText(() => Thrower.Report(e)), "SetCellProperties");
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int at = Array.IndexOf(lines, "MissingMethodException: Method not found: void .SimMessages.SetCellProperties(int,byte)");
            Assert.True(at >= 0, text);
            Assert.Matches(@"^  at LogTraceSubjects\.Thrower\.FromHidden \(\) \[0x[0-9a-f]{5}\] in .+:\d+ $", lines[at + 1]);
            Assert.Equal("UnityEngine.DebugLogHandler:Internal_LogException_Injected(Exception, IntPtr)", lines[at + 2]);
            var (_, stack, _) = Assert.Single(got);
            Assert.StartsWith("LogTraceSubjects.Thrower.FromHidden () (at ", stack);
        }

        [Fact]
        public void AnUnthrownExceptionHasTheCallAlone()
        {
            var e = new InvalidOperationException("lt-unthrown");
            var (_, stack, _) = Assert.Single(Received(() => Thrower.Report(e), "lt-unthrown"));
            Assert.StartsWith("UnityEngine.Debug:LogException(Exception)\nLogTraceSubjects.Thrower:Report(Exception)\n", stack);
        }
    }
}
