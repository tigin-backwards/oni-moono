using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OniMoono.Engine;
using UnityEngine;
using Xunit;

// Outside the OniMoono namespaces: the engine's own frames are left out of logged stack traces.
namespace CoroutineTraceSubjects
{
    /// Runs its Case from Update, as UnityProbe's cotraces scenario runs each case.
    public class Subject : MonoBehaviour
    {
        public string Case;

        void Update()
        {
            var c = Case;
            Case = null;
            switch (c)
            {
                case "first-step": FirstStep(); break;
                case "later-step": LaterStep(); break;
                case "missing-first": MissingFirst(); break;
                case "nested-first": NestedFirst(); break;
                case "yielded": Yielded(); break;
                case "by-name": ByName(); break;
                case "logs-in-step": LogsInStep(); break;
                case "logger": Logger(); break;
                case "update": Throw(); break;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throw() => throw new InvalidOperationException("ct thrown");

        static Exception Thrown()
        {
            try { Throw(); }
            catch (Exception e) { return e; }
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] void Kick(IEnumerator routine) => StartCoroutine(routine);
        [MethodImpl(MethodImplOptions.NoInlining)] void FirstStep() => Kick(ThrowFirst());
        [MethodImpl(MethodImplOptions.NoInlining)] void LaterStep() => Kick(ThrowLater());
        [MethodImpl(MethodImplOptions.NoInlining)] void MissingFirst() => Kick(new Missing());
        [MethodImpl(MethodImplOptions.NoInlining)] void NestedFirst() => Kick(ParentFirst());
        [MethodImpl(MethodImplOptions.NoInlining)] void Yielded() => Kick(ParentYields());
        [MethodImpl(MethodImplOptions.NoInlining)] void ByName() => StartCoroutine(nameof(ThrowFirstByName));
        [MethodImpl(MethodImplOptions.NoInlining)] void LogsInStep() => Kick(LogsInStepRoutine());
        [MethodImpl(MethodImplOptions.NoInlining)] void Logger() => UnityEngine.Debug.unityLogger.LogException(Thrown());

        IEnumerator ThrowFirst()
        {
            Throw();
            yield return null;
        }

        IEnumerator ThrowLater()
        {
            yield return null;
            Throw();
        }

        IEnumerator ThrowFirstByName()
        {
            Throw();
            yield return null;
        }

        IEnumerator ParentFirst()
        {
            yield return StartCoroutine(ThrowFirst());
        }

        IEnumerator ParentYields()
        {
            yield return ThrowFirst();
        }

        IEnumerator LogsInStepRoutine()
        {
            yield return null;
            UnityEngine.Debug.LogException(Thrown());
        }

        /// As the host gives a mod's MoveNext that Mono cannot compile (MonoMissingMembers): it
        /// throws MissingMethodException from a frame hidden from stack traces.
        sealed class Missing : IEnumerator
        {
            public object Current => null;
            [System.Diagnostics.StackTraceHidden]
            public bool MoveNext() => throw new MissingMethodException("Method not found: void .GlobalKeeper.SetCellProperties(int,byte)");
            public void Reset() { }
        }
    }

    /// A log handler in front of the default one, as the game's LogCatcher is.
    public sealed class PassThrough : ILogHandler
    {
        public ILogHandler Next;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void LogException(Exception exception, UnityEngine.Object context) => Next.LogException(exception, context);

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args) => Next.LogFormat(logType, context, format, args);
    }
}

namespace OniMoono.Tests
{
    using CoroutineTraceSubjects;

    /// The stack traces of exceptions from coroutines, as UnityProbe's cotraces scenario measured
    /// them in the original (U57, 2026-11-09): the exception's frames end at Unity's coroutine
    /// stepper, SetupCoroutine.InvokeMoveNext; then the managed frames the original's native code
    /// was called from (StartCoroutine and its callers on a first step, none on a later one), with
    /// the log handler's frames when the game replaced the handler. The log has every frame; the
    /// callbacks leave out each UnityEngine frame whose caller is a UnityEngine frame too.
    [Collection("Console")]
    public class CoroutineTracesTests : EngineTest
    {
        static readonly string Mvid = typeof(MonoBehaviour).Module.ModuleVersionId.ToString("N");
        static string Stepper => "UnityEngine.SetupCoroutine.InvokeMoveNext (System.Collections.IEnumerator enumerator, System.IntPtr returnValueAddress) (at <" + Mvid + ">:0)";
        static string StepperLog => "  at UnityEngine.SetupCoroutine.InvokeMoveNext (System.Collections.IEnumerator enumerator, System.IntPtr returnValueAddress) [0x00026] in <" + Mvid + ">:0 ";

        // The compiler's name of an iterator's class: "<Name>d__N".
        static string Iterator(string name) => typeof(Subject).GetNestedTypes(System.Reflection.BindingFlags.NonPublic).Single(t => t.Name.StartsWith("<" + name + ">d__")).Name;

        // Our exception frames carry source files where the original's name <mvid>:0.
        static string Frames(string stack) => Regex.Replace(stack, @" \(at /[^)]+:\d+\)", " (at FILE)");

        (string condition, string callback, string log) Run(string c, bool replaced = false)
        {
            var go = new GameObject("ct");
            var subject = go.AddComponent<Subject>();
            Frame();
            var got = new List<(string, string)>();
            var threaded = new List<string>();
            Application.LogCallback on = (cond, st, t) => { if (t == LogType.Exception) got.Add((cond, st)); };
            Application.LogCallback onThreaded = (cond, st, t) => { if (t == LogType.Exception) threaded.Add(st); };
            var path = Path.Combine(Path.GetTempPath(), $"cotraces-{Guid.NewGuid():N}", "Player.log");
            var stdout = Console.Out;
            var handler = UnityEngine.Debug.unityLogger.logHandler;
            Application.logMessageReceived += on;
            Application.logMessageReceivedThreaded += onThreaded;
            try
            {
                if (replaced) UnityEngine.Debug.unityLogger.logHandler = new PassThrough { Next = handler };
                EngineLog.Open(path);
                subject.Case = c;
                for (int i = 0; i < 3; i++) Frame();
            }
            finally
            {
                UnityEngine.Debug.unityLogger.logHandler = handler;
                Application.logMessageReceived -= on;
                Application.logMessageReceivedThreaded -= onThreaded;
                EngineLog.Close();
                Console.SetOut(stdout);
                UnityEngine.Object.DestroyImmediate(go);
            }
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            Directory.Delete(Path.GetDirectoryName(path)!, true);
            var (condition, stack) = Assert.Single(got);
            Assert.Equal(stack, Assert.Single(threaded));
            int at = text.IndexOf(condition + "\n", StringComparison.Ordinal);
            Assert.True(at >= 0, text);
            int end = text.IndexOf("\n\n", at, StringComparison.Ordinal);
            return (condition, stack, text.Substring(at + condition.Length + 1, end - at - condition.Length));
        }

        static string Lines(params string[] lines) => string.Join("", lines.Select(l => l + "\n"));

        const string Throw = "CoroutineTraceSubjects.Subject.Throw () (at FILE)";
        static string MoveNext(string iterator) => "CoroutineTraceSubjects.Subject+" + Iterator(iterator) + ".MoveNext () (at FILE)";

        [Fact]
        public void AFirstStepsExceptionEndsAtTheStepperThenStartCoroutinesCallers()
        {
            var (condition, callback, log) = Run("first-step");
            Assert.Equal("InvalidOperationException: ct thrown", condition);
            Assert.Equal(Lines(Throw, MoveNext("ThrowFirst"), Stepper,
                "UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)",
                "CoroutineTraceSubjects.Subject:Kick(IEnumerator)",
                "CoroutineTraceSubjects.Subject:FirstStep()",
                "CoroutineTraceSubjects.Subject:Update()"), Frames(callback));
            var lines = log.Split('\n');
            Assert.Matches(@"^  at CoroutineTraceSubjects\.Subject\.Throw \(\) \[0x[0-9a-f]{5}\] in /.+:\d+ $", lines[0]);
            Assert.Equal(StepperLog, lines[2]);
            Assert.Equal(Lines(
                "UnityEngine.MonoBehaviour:StartCoroutineManaged2_Injected(IntPtr, IEnumerator)",
                "UnityEngine.MonoBehaviour:StartCoroutineManaged2(IEnumerator)",
                "UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)",
                "CoroutineTraceSubjects.Subject:Kick(IEnumerator)",
                "CoroutineTraceSubjects.Subject:FirstStep()",
                "CoroutineTraceSubjects.Subject:Update()"), string.Join("\n", lines.Skip(3)));
        }

        [Fact]
        public void ALaterStepsExceptionHasItsOwnFramesAlone()
        {
            var (_, callback, log) = Run("later-step");
            Assert.Equal(Lines(Throw, MoveNext("ThrowLater"), Stepper), Frames(callback));
            Assert.Equal(StepperLog + "\n", log.Substring(log.IndexOf("  at UnityEngine.SetupCoroutine", StringComparison.Ordinal)));
        }

        [Fact]
        public void AStepThatCannotCompileHasTheStepperAlone()
        {
            // As the original's #63 report: the method Mono cannot compile has no frame.
            var (condition, callback, log) = Run("missing-first");
            Assert.Equal("MissingMethodException: Method not found: void .GlobalKeeper.SetCellProperties(int,byte)", condition);
            Assert.Equal(Lines(Stepper,
                "UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)",
                "CoroutineTraceSubjects.Subject:Kick(IEnumerator)",
                "CoroutineTraceSubjects.Subject:MissingFirst()",
                "CoroutineTraceSubjects.Subject:Update()"), callback);
            Assert.StartsWith(StepperLog + "\nUnityEngine.MonoBehaviour:StartCoroutineManaged2_Injected(IntPtr, IEnumerator)\n", log);
        }

        [Fact]
        public void ANestedCoroutinesStepperIsLeftOutWhereStartCoroutineCalledIt()
        {
            var (_, callback, log) = Run("nested-first");
            string parent = "CoroutineTraceSubjects." + Iterator("ParentFirst") + ":MoveNext()";
            Assert.Equal(Lines(Throw, MoveNext("ThrowFirst"), Stepper,
                "UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)",
                parent,
                "UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)",
                "CoroutineTraceSubjects.Subject:Kick(IEnumerator)",
                "CoroutineTraceSubjects.Subject:NestedFirst()",
                "CoroutineTraceSubjects.Subject:Update()"), Frames(callback));
            Assert.EndsWith(Lines(
                "UnityEngine.MonoBehaviour:StartCoroutineManaged2_Injected(IntPtr, IEnumerator)",
                "UnityEngine.MonoBehaviour:StartCoroutineManaged2(IEnumerator)",
                "UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)",
                parent,
                "UnityEngine.SetupCoroutine:InvokeMoveNext(IEnumerator, IntPtr)",
                "UnityEngine.MonoBehaviour:StartCoroutineManaged2_Injected(IntPtr, IEnumerator)",
                "UnityEngine.MonoBehaviour:StartCoroutineManaged2(IEnumerator)",
                "UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)",
                "CoroutineTraceSubjects.Subject:Kick(IEnumerator)",
                "CoroutineTraceSubjects.Subject:NestedFirst()",
                "CoroutineTraceSubjects.Subject:Update()"), log);
        }

        [Fact]
        public void AYieldedEnumeratorRunsInsideItsParentsStartCoroutine()
        {
            var (_, callback, _) = Run("yielded");
            Assert.Equal(Lines(Throw, MoveNext("ThrowFirst"), Stepper,
                "UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)",
                "CoroutineTraceSubjects.Subject:Kick(IEnumerator)",
                "CoroutineTraceSubjects.Subject:Yielded()",
                "CoroutineTraceSubjects.Subject:Update()"), Frames(callback));
        }

        [Fact]
        public void ACoroutineStartedByNameHasUnitysOverloads()
        {
            var (_, callback, log) = Run("by-name");
            Assert.Equal(Lines(Throw, MoveNext("ThrowFirstByName"), Stepper,
                "UnityEngine.MonoBehaviour:StartCoroutine(String)",
                "CoroutineTraceSubjects.Subject:ByName()",
                "CoroutineTraceSubjects.Subject:Update()"), Frames(callback));
            Assert.EndsWith(Lines(
                "UnityEngine.MonoBehaviour:StartCoroutineManaged_Injected(IntPtr, ManagedSpanWrapper&, Object)",
                "UnityEngine.MonoBehaviour:StartCoroutineManaged(String, Object)",
                "UnityEngine.MonoBehaviour:StartCoroutine(String, Object)",
                "UnityEngine.MonoBehaviour:StartCoroutine(String)",
                "CoroutineTraceSubjects.Subject:ByName()",
                "CoroutineTraceSubjects.Subject:Update()"), log);
        }

        [Fact]
        public void ALogExceptionInALaterStepEndsAtTheStepper()
        {
            var (_, callback, log) = Run("logs-in-step");
            string step = "CoroutineTraceSubjects." + Iterator("LogsInStepRoutine") + ":MoveNext()";
            Assert.EndsWith(Lines("UnityEngine.Debug:LogException(Exception)", step, "UnityEngine.SetupCoroutine:InvokeMoveNext(IEnumerator, IntPtr)"), callback);
            Assert.EndsWith(Lines(
                "UnityEngine.DebugLogHandler:Internal_LogException_Injected(Exception, IntPtr)",
                "UnityEngine.DebugLogHandler:Internal_LogException(Exception, Object)",
                "UnityEngine.DebugLogHandler:LogException(Exception, Object)",
                "UnityEngine.Logger:LogException(Exception, Object)",
                "UnityEngine.Debug:LogException(Exception)",
                step,
                "UnityEngine.SetupCoroutine:InvokeMoveNext(IEnumerator, IntPtr)"), log);
        }

        [Fact]
        public void TheLoggersOwnOverloadCallsTheHandler()
        {
            var (_, callback, log) = Run("logger");
            Assert.EndsWith(Lines("UnityEngine.Logger:LogException(Exception)", "CoroutineTraceSubjects.Subject:Logger()", "CoroutineTraceSubjects.Subject:Update()"), callback);
            Assert.EndsWith(Lines(
                "UnityEngine.DebugLogHandler:LogException(Exception, Object)",
                "UnityEngine.Logger:LogException(Exception)",
                "CoroutineTraceSubjects.Subject:Logger()",
                "CoroutineTraceSubjects.Subject:Update()"), log);
        }

        [Fact]
        public void WithAReplacedHandlerAFirstStepHasTheHandlersFrames()
        {
            var (_, callback, log) = Run("first-step", replaced: true);
            Assert.Equal(Lines(Throw, MoveNext("ThrowFirst"), Stepper,
                "UnityEngine.DebugLogHandler:LogException(Exception, Object)",
                "CoroutineTraceSubjects.PassThrough:LogException(Exception, Object)",
                "UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)",
                "CoroutineTraceSubjects.Subject:Kick(IEnumerator)",
                "CoroutineTraceSubjects.Subject:FirstStep()",
                "CoroutineTraceSubjects.Subject:Update()"), Frames(callback));
            Assert.EndsWith(Lines(StepperLog,
                "UnityEngine.DebugLogHandler:Internal_LogException_Injected(Exception, IntPtr)",
                "UnityEngine.DebugLogHandler:Internal_LogException(Exception, Object)",
                "UnityEngine.DebugLogHandler:LogException(Exception, Object)",
                "CoroutineTraceSubjects.PassThrough:LogException(Exception, Object)",
                "UnityEngine.Logger:LogException(Exception, Object)",
                "UnityEngine.Debug:CallOverridenDebugHandler(Exception, Object)",
                "UnityEngine.MonoBehaviour:StartCoroutineManaged2_Injected(IntPtr, IEnumerator)",
                "UnityEngine.MonoBehaviour:StartCoroutineManaged2(IEnumerator)",
                "UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)",
                "CoroutineTraceSubjects.Subject:Kick(IEnumerator)",
                "CoroutineTraceSubjects.Subject:FirstStep()",
                "CoroutineTraceSubjects.Subject:Update()"), log);
        }

        [Fact]
        public void WithAReplacedHandlerALaterStepEndsAtCallOverridenDebugHandler()
        {
            var (_, callback, log) = Run("later-step", replaced: true);
            Assert.Equal(Lines(Throw, MoveNext("ThrowLater"), Stepper,
                "UnityEngine.DebugLogHandler:LogException(Exception, Object)",
                "CoroutineTraceSubjects.PassThrough:LogException(Exception, Object)",
                "UnityEngine.Debug:CallOverridenDebugHandler(Exception, Object)"), Frames(callback));
            Assert.EndsWith(Lines(StepperLog,
                "UnityEngine.DebugLogHandler:Internal_LogException_Injected(Exception, IntPtr)",
                "UnityEngine.DebugLogHandler:Internal_LogException(Exception, Object)",
                "UnityEngine.DebugLogHandler:LogException(Exception, Object)",
                "CoroutineTraceSubjects.PassThrough:LogException(Exception, Object)",
                "UnityEngine.Logger:LogException(Exception, Object)",
                "UnityEngine.Debug:CallOverridenDebugHandler(Exception, Object)"), log);
        }

        [Fact]
        public void AnUpdatesExceptionHasItsOwnFramesAloneOrTheReplacedHandlers()
        {
            Assert.Equal(Lines(Throw, "CoroutineTraceSubjects.Subject.Update () (at FILE)"), Frames(Run("update").callback));
            Assert.Equal(Lines(Throw, "CoroutineTraceSubjects.Subject.Update () (at FILE)",
                "UnityEngine.DebugLogHandler:LogException(Exception, Object)",
                "CoroutineTraceSubjects.PassThrough:LogException(Exception, Object)",
                "UnityEngine.Debug:CallOverridenDebugHandler(Exception, Object)"), Frames(Run("update", replaced: true).callback));
        }

        [Fact]
        public void WithAReplacedHandlerLogExceptionHasTheHandlersFrames()
        {
            var e = LogTraceSubjects.Thrower.Thrown();
            var handler = UnityEngine.Debug.unityLogger.logHandler;
            var got = new List<string>();
            Application.LogCallback on = (c, s, t) => { if (c.EndsWith(": thrown")) got.Add(s); };
            Application.logMessageReceived += on;
            try
            {
                UnityEngine.Debug.unityLogger.logHandler = new PassThrough { Next = handler };
                LogTraceSubjects.Thrower.Report(e);
            }
            finally
            {
                UnityEngine.Debug.unityLogger.logHandler = handler;
                Application.logMessageReceived -= on;
            }
            var frames = Assert.Single(got).Split('\n');
            Assert.Equal("UnityEngine.DebugLogHandler:LogException(Exception, Object)", frames[2]);
            Assert.Equal("CoroutineTraceSubjects.PassThrough:LogException(Exception, Object)", frames[3]);
            Assert.Equal("UnityEngine.Debug:LogException(Exception)", frames[4]);
            Assert.Equal("LogTraceSubjects.Thrower:Report(Exception)", frames[5]);
        }
    }
}
