// What Application.logMessageReceived and logMessageReceivedThreaded get for an exception left
// uncaught in a coroutine (lines "PROBE ... CT ..."): thrown on the first step (inside
// StartCoroutine) and on a later step (resumed by the player loop), an ordinary exception and a
// MissingMethodException (a method naming a member ProbeGone v2 lacks, so it cannot compile;
// Gone/v2 in the mod folder as for missingmembers), from Update and from another coroutine's
// step, nested (yield return StartCoroutine, a yielded IEnumerator), started by name, and for
// comparison an exception from Update, Debug.LogException called from a coroutine's step,
// Debug.unityLogger.LogException, the handler's LogException and LogException from a UnityEvent
// listener; then several of them again behind a pass-through handler (as the game's LogCatcher).
// Each case lies between "CT case NAME begin" and "end" lines, so the Player.log text for it is
// in between, and then lists what each event got: type, condition and stack trace (escaped, in
// full). The managed frames below the log handler (with each method's custom attributes and
// visibility) are listed once for a Debug.LogException and once for a coroutine's exception,
// through a pass-through handler put in front of the game's.
//
// Run alone with PROBE_SCENARIO=cotraces (10 s into the main menu); PROBE_CT_WARMUP=ROUNDS runs
// the cases' paths many times first, for a tiering JIT; a CoroutineTracesProbeDone
// canvas appears at the end.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SemanticsProbe
{
    public class CoroutineTraces : MonoBehaviour
    {
        static void L(string s) => Rec.Log("CT " + s);

        static string Esc(string s) => s == null ? "null" : s.Replace("\r", "\\r").Replace("\n", "\\n");

        static readonly List<string> got = new List<string>();

        static void OnMain(string c, string st, LogType t)
        {
            if (t == LogType.Exception) got.Add("received " + t + " condition \"" + Esc(c) + "\" stack \"" + Esc(st) + "\"");
        }

        static void OnThreaded(string c, string st, LogType t)
        {
            if (t == LogType.Exception) got.Add("threaded " + t + " condition \"" + Esc(c) + "\" stack \"" + Esc(st) + "\"");
        }

        // Runs the next Update when set.
        System.Action fromUpdate;
        bool throwInUpdate;

        void Update()
        {
            var a = fromUpdate;
            fromUpdate = null;
            a?.Invoke();
            if (!throwInUpdate) return;
            throwInUpdate = false;
            Nested();
        }

        // A case: its trigger runs from Update (or from this coroutine's step), then three frames
        // pass so a later step's exception lands in the window too.
        IEnumerator Case(string name, System.Action trigger, bool fromStep = false)
        {
            got.Clear();
            L("case " + name + " begin");
            Application.logMessageReceived += OnMain;
            Application.logMessageReceivedThreaded += OnThreaded;
            if (fromStep) trigger();
            else fromUpdate = trigger;
            for (int i = 0; i < 4; i++) yield return null;
            Application.logMessageReceived -= OnMain;
            Application.logMessageReceivedThreaded -= OnThreaded;
            L("case " + name + " end");
            var copy = got.ToList();
            L("case " + name + " received " + copy.Count);
            foreach (var g in copy) L("case " + name + " got " + g);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Nested() => throw new InvalidOperationException("ct thrown");

        // Cannot compile with Gone/v2: GlobalKeeper.SetCellProperties is gone.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Missing() => GlobalKeeper.SetCellProperties(1, 8);

        IEnumerator ThrowFirst()
        {
            Nested();
            yield return null;
        }

        IEnumerator Empty()
        {
            yield break;
        }

        IEnumerator ThrowLater()
        {
            yield return null;
            Nested();
        }

        // Its MoveNext itself names the gone member: the first step fails to compile.
        IEnumerator MissingFirst()
        {
            GlobalKeeper.SetCellProperties(1, 8);
            yield return null;
        }

        IEnumerator MissingCallFirst()
        {
            Missing();
            yield return null;
        }

        IEnumerator MissingLater()
        {
            yield return null;
            Missing();
        }

        IEnumerator ParentFirst()
        {
            yield return StartCoroutine(ThrowFirst());
        }

        IEnumerator ParentLater()
        {
            yield return StartCoroutine(ThrowLater());
        }

        IEnumerator ParentYieldsEnumerator()
        {
            yield return ThrowFirst();
        }

        IEnumerator ParentYieldsLater()
        {
            yield return null;
            yield return ThrowFirst();
        }

        IEnumerator ThrowFirstByName()
        {
            Nested();
            yield return null;
        }

        IEnumerator LogsInStep()
        {
            yield return null;
            UnityEngine.Debug.LogException(Thrown());
        }

        static Exception Thrown()
        {
            try { Nested(); }
            catch (Exception e) { return e; }
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Kick(IEnumerator routine) => StartCoroutine(routine);

        // Lists the managed frames under the game's handler, then passes the call on.
        class Lister : ILogHandler
        {
            public ILogHandler Next;
            public bool Armed;

            public void LogException(Exception exception, UnityEngine.Object context)
            {
                if (Armed)
                {
                    Armed = false;
                    var frames = new StackTrace(0, false).GetFrames() ?? new StackFrame[0];
                    var lines = new List<string>();
                    foreach (var f in frames)
                    {
                        var m = f.GetMethod();
                        if (m == null) { lines.Add("(no method)"); continue; }
                        string attrs;
                        try { attrs = string.Join(",", m.GetCustomAttributes(false).Select(a => a.GetType().Name)); }
                        catch (Exception e) { attrs = "?" + e.GetType().Name; }
                        string vis = m.IsPublic ? "public" : m.IsAssembly ? "internal" : m.IsPrivate ? "private" : "other";
                        lines.Add((m.DeclaringType?.FullName ?? "?") + ":" + m.Name + " " + vis + (m.IsStatic ? " static" : "") + " [" + attrs + "]");
                    }
                    pending.Add(lines);
                }
                Next.LogException(exception, context);
            }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args) => Next.LogFormat(logType, context, format, args);
        }

        static readonly List<List<string>> pending = new List<List<string>>();

        IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(10f);
            L("begin");
            L("handler " + (UnityEngine.Debug.unityLogger.logHandler?.GetType().FullName ?? "null"));
            // PROBE_CT_WARMUP=ROUNDS: the paths the cases take run many times first, ROUNDS times
            // 2 s apart, so a tiering JIT (ours) has compiled them with its optimizations (inlining,
            // profile-guided) before the cases are measured.
            int.TryParse(Environment.GetEnvironmentVariable("PROBE_CT_WARMUP"), out int rounds);
            for (int round = 0; round < rounds; round++)
            {
                L("warmup round " + round);
                var warmLister = new Lister { Next = UnityEngine.Debug.unityLogger.logHandler };
                for (int i = 0; i < 2000; i++) Kick(Empty());
                for (int i = 0; i < 150; i++)
                {
                    Kick(ThrowFirst());
                    Kick(ParentFirst());
                    Kick(MissingFirst());
                    StartCoroutine(nameof(ThrowFirstByName));
                    UnityEngine.Debug.LogException(Thrown());
                    UnityEngine.Debug.unityLogger.LogException(Thrown());
                    UnityEngine.Debug.unityLogger.logHandler = warmLister;
                    Kick(ThrowFirst());
                    UnityEngine.Debug.LogException(Thrown());
                    UnityEngine.Debug.unityLogger.logHandler = warmLister.Next;
                }
                yield return new WaitForSecondsRealtime(2f);
            }
            yield return Case("update", () => throwInUpdate = true);
            yield return Case("first-step", () => Kick(ThrowFirst()));
            yield return Case("first-step-from-step", () => Kick(ThrowFirst()), fromStep: true);
            yield return Case("later-step", () => Kick(ThrowLater()));
            yield return Case("missing-first", () => Kick(MissingFirst()));
            yield return Case("missing-call-first", () => Kick(MissingCallFirst()));
            yield return Case("missing-later", () => Kick(MissingLater()));
            yield return Case("nested-first", () => Kick(ParentFirst()));
            yield return Case("nested-later", () => Kick(ParentLater()));
            yield return Case("yielded-enumerator", () => Kick(ParentYieldsEnumerator()));
            yield return Case("yielded-enumerator-later", () => Kick(ParentYieldsLater()));
            yield return Case("by-name", () => StartCoroutine(nameof(ThrowFirstByName)));
            yield return Case("logs-in-step", () => Kick(LogsInStep()));
            yield return Case("unity-exception", () => UnityEngine.Debug.LogException(Thrown()));
            yield return Case("logger-exception", () => UnityEngine.Debug.unityLogger.LogException(Thrown()));
            yield return Case("handler-exception", () => UnityEngine.Debug.unityLogger.logHandler.LogException(Thrown(), null));
            var logs = new UnityEngine.Events.UnityEvent();
            logs.AddListener(() => UnityEngine.Debug.LogException(Thrown()));
            yield return Case("unityevent-logs", () => logs.Invoke());
            var throws = new UnityEngine.Events.UnityEvent();
            throws.AddListener(Nested);
            yield return Case("unityevent-throws", () => throws.Invoke());

            var lister = new Lister { Next = UnityEngine.Debug.unityLogger.logHandler };
            UnityEngine.Debug.unityLogger.logHandler = lister;
            lister.Armed = true;
            yield return Case("lister-unity-exception", () => UnityEngine.Debug.LogException(Thrown()));
            lister.Armed = true;
            yield return Case("lister-first-step", () => Kick(ThrowFirst()));
            lister.Armed = true;
            yield return Case("lister-later-step", () => Kick(ThrowLater()));
            yield return Case("lister-update", () => throwInUpdate = true);
            yield return Case("lister-missing-first", () => Kick(MissingFirst()));
            yield return Case("lister-nested-first", () => Kick(ParentFirst()));
            yield return Case("lister-by-name", () => StartCoroutine(nameof(ThrowFirstByName)));
            yield return Case("lister-logger-exception", () => UnityEngine.Debug.unityLogger.LogException(Thrown()));
            yield return Case("lister-unityevent-logs", () => logs.Invoke());
            UnityEngine.Debug.unityLogger.logHandler = lister.Next;
            for (int i = 0; i < pending.Count; i++)
                foreach (var line in pending[i]) L("frames " + i + " " + line);
            L("end");
            var canvasGo = new GameObject("CoroutineTracesProbeDone");
            canvasGo.AddComponent<Canvas>();
            DontDestroyOnLoad(canvasGo);
        }
    }
}
