using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace OniMoono.Engine
{
    /// The stack traces the original (release, Mono) player writes for a logged exception, measured
    /// with UnityProbe's logging and cotraces scenarios. Messages (Log, Warning, Error, Assert) get
    /// none.
    ///
    /// The condition is the innermost exception's "Type: message". The log's text continues with
    /// each exception's own frames, innermost first, in Mono's format ("  at A.B (System.String
    /// name) [0x0001f] in &lt;mvid&gt;:0 "), each outer one introduced by "Rethrow as Type: message",
    /// then the managed frames of the logging call in Unity's format ("A:B(String, Int32)"), and an
    /// empty line. The callbacks get the exception frames as "A.B (…) (at &lt;mvid&gt;:0)" and the
    /// call's frames without each UnityEngine frame whose caller is a UnityEngine frame too.
    ///
    /// The call's frames are Unity's: Debug.LogException reaches the native log through
    /// Logger.LogException, the handler (the game's LogCatcher, then the default one) and
    /// DebugLogHandler.LogException. An exception the engine catches (from Update, a coroutine's
    /// step) is reported from the original's native code: through
    /// Debug.CallOverridenDebugHandler and the handler when the handler was replaced, else with no
    /// logging frames at all. The engine's frames stand for native code and are left out, but for
    /// those that stand for Unity's managed ones: the coroutine stepper (Coroutines.Advance, Unity's
    /// SetupCoroutine.InvokeMoveNext, also at the bottom of a step's exception frames) and the
    /// start of a coroutine from StartCoroutine (Coroutines.Start, StartCoroutineManaged2 and its
    /// injected binding).
    internal static class LogTraces
    {
        internal readonly record struct ExceptionReport(string Condition, string LogTrace, string CallbackTrace);

        const string StepperMono = "UnityEngine.SetupCoroutine.InvokeMoveNext (System.Collections.IEnumerator enumerator, System.IntPtr returnValueAddress)";
        const string StepperOffset = "0x00026";
        const string Stepper = "UnityEngine.SetupCoroutine:InvokeMoveNext(IEnumerator, IntPtr)";
        static readonly string[] HandlerTop =
        {
            "UnityEngine.DebugLogHandler:Internal_LogException_Injected(Exception, IntPtr)",
            "UnityEngine.DebugLogHandler:Internal_LogException(Exception, Object)",
            "UnityEngine.DebugLogHandler:LogException(Exception, Object)",
        };
        const string Overridden = "UnityEngine.Debug:CallOverridenDebugHandler(Exception, Object)";
        static readonly string[] StartManaged = { "UnityEngine.MonoBehaviour:StartCoroutineManaged2_Injected(IntPtr, IEnumerator)", "UnityEngine.MonoBehaviour:StartCoroutineManaged2(IEnumerator)" };
        static readonly string[] StartManagedByName = { "UnityEngine.MonoBehaviour:StartCoroutineManaged_Injected(IntPtr, ManagedSpanWrapper&, Object)", "UnityEngine.MonoBehaviour:StartCoroutineManaged(String, Object)" };

        public static ExceptionReport Exception(Exception exception, StackTrace calls)
        {
            var chain = new List<Exception>();
            for (var e = exception; e != null; e = e.InnerException) chain.Add(e);
            chain.Reverse();
            var log = new StringBuilder();
            var callback = new StringBuilder();
            for (int i = 0; i < chain.Count; i++)
            {
                var e = chain[i];
                if (i > 0)
                {
                    string rethrow = "Rethrow as " + Condition(e) + "\n";
                    log.Append(rethrow);
                    callback.Append(rethrow);
                }
                var frames = new StackTrace(e, true).GetFrames();
                int end = frames.Length;
                while (end > 0 && IsEngine(frames[end - 1].GetMethod())) end--;
                for (int f = 0; f < end; f++)
                {
                    var method = frames[f].GetMethod();
                    // Left out as Exception.StackTrace leaves it out: a mod's method that throws as
                    // Mono's compilation of it would (the host's MonoMissingTypes) has no frame on Mono.
                    if (method == null || Hidden(method)) continue;
                    string name = MonoMethod(method);
                    string file = frames[f].GetFileName();
                    string location = file != null ? file + ":" + frames[f].GetFileLineNumber() : "<" + ModuleId(method) + ">:0";
                    int il = Math.Max(0, frames[f].GetILOffset());
                    log.Append("  at ").Append(name).Append(" [0x").Append(il.ToString("x5")).Append("] in ").Append(location).Append(" \n");
                    callback.Append(name).Append(" (at ").Append(location).Append(")\n");
                }
                // Caught by the coroutine stepper: the step's exception left Unity's InvokeMoveNext.
                if (end < frames.Length && IsStepper(frames[end].GetMethod()))
                {
                    string location = "<" + typeof(UnityEngine.MonoBehaviour).Module.ModuleVersionId.ToString("N") + ">:0";
                    log.Append("  at ").Append(StepperMono).Append(" [").Append(StepperOffset).Append("] in ").Append(location).Append(" \n");
                    callback.Append(StepperMono).Append(" (at ").Append(location).Append(")\n");
                }
            }
            var lines = UnityCalls(calls);
            for (int i = 0; i < lines.Count; i++)
            {
                log.Append(lines[i]).Append('\n');
                // As Unity's callbacks: a UnityEngine frame called by another goes.
                if (i + 1 < lines.Count && IsUnity(lines[i]) && IsUnity(lines[i + 1])) continue;
                callback.Append(lines[i]).Append('\n');
            }
            return new ExceptionReport(Condition(chain[0]), log.ToString(), callback.ToString());
        }

        /// The original's managed frames, in Unity's format, of a LogException call whose frames
        /// (from the log handler's caller on) are CALLS.
        static List<string> UnityCalls(StackTrace calls)
        {
            var frames = calls.GetFrames();
            var lines = new List<string>();
            int logger = Array.FindIndex(frames, f => f.GetMethod()?.DeclaringType == typeof(Logging.UnityLogger));
            int after = logger + 1;
            while (after > 0 && after < frames.Length && (frames[after].GetMethod() is not { } m || IsLogging(m))) after++;
            var next = after > 0 && after < frames.Length ? frames[after].GetMethod() : null;
            // Reported by the engine (its catch of a script's exception): the original's native code
            // reports it, through the handler only when the game replaced it.
            bool engine = logger >= 0 && (next == null || IsEngine(next) || (next.DeclaringType == typeof(UnityEngine.Debug) && CalledByEngine(frames, after)));
            bool replaced = false;
            for (int i = 0; i < Math.Max(logger, 0); i++)
                if (frames[i].GetMethod() is { } h && !IsLogging(h) && !IsEngine(h)) replaced = true;
            if (!engine || replaced)
            {
                lines.AddRange(HandlerTop);
                for (int i = 0; i < Math.Max(logger, 0); i++)
                    if (frames[i].GetMethod() is { } h && !IsLogging(h) && !IsEngine(h)) lines.Add(UnityFrame(h));
                if (logger >= 0) lines.Add(LoggerFrame(frames, logger));
                if (engine) lines.Add(Overridden);
            }
            int from = logger >= 0 ? after : 0;
            if (engine && next != null && next.DeclaringType == typeof(UnityEngine.Debug)) from++;
            for (int i = from; i < frames.Length; i++)
            {
                var method = frames[i].GetMethod();
                if (method == null || IsLogging(method)) continue;
                // The player loop's callers (the host, a test) stand for none of the original's.
                if (method.DeclaringType == typeof(PlayerLoop)) break;
                if (!IsEngine(method))
                {
                    if (method.DeclaringType == typeof(UnityEngine.Debug) && CalledByEngine(frames, i)) continue;
                    lines.Add(UnityFrame(method));
                }
                // The stepper while it runs a step (not where it reports one's exception).
                else if (IsStepper(method) && Callee(frames, i) is { } callee && !IsEngine(callee) && !IsLogging(callee)
                         && callee.DeclaringType != typeof(UnityEngine.Debug))
                    lines.Add(Stepper);
                else if (method.DeclaringType == typeof(Coroutines) && method.Name == nameof(Coroutines.Start) && Caller(frames, i) is { } starter
                         && starter.DeclaringType == typeof(UnityEngine.MonoBehaviour) && starter.Name == nameof(UnityEngine.MonoBehaviour.StartCoroutine))
                    lines.AddRange(starter.GetParameters()[0].ParameterType == typeof(string) ? StartManagedByName : StartManaged);
            }
            return lines;
        }

        /// The frames of CALLS in Unity's format, one per line, the engine's frames with their source
        /// lines (engine diagnostics).
        public static string Calls(StackTrace calls)
        {
            var sb = new StringBuilder();
            foreach (var frame in calls.GetFrames())
            {
                var method = frame.GetMethod();
                if (method == null || IsLogging(method)) continue;
                sb.Append(UnityFrame(method));
                var file = frame.GetFileName();
                if (file != null) sb.Append(" (at ").Append(file).Append(':').Append(frame.GetFileLineNumber()).Append(')');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // Unity's Logger.LogException overload the call went through: LogException(Exception) calls
        // the handler itself.
        static string LoggerFrame(StackFrame[] frames, int logger)
        {
            int outer = logger;
            while (outer + 1 < frames.Length && frames[outer + 1].GetMethod()?.DeclaringType == typeof(Logging.UnityLogger)) outer++;
            return frames[outer].GetMethod().GetParameters().Length == 1
                ? "UnityEngine.Logger:LogException(Exception)"
                : "UnityEngine.Logger:LogException(Exception, Object)";
        }

        static bool IsUnity(string frame) => frame.StartsWith("UnityEngine.", StringComparison.Ordinal);

        static bool IsStepper(MethodBase m) => m?.DeclaringType == typeof(Coroutines) && m.Name == "Advance";

        // The method frame I calls (the previous frame with a method).
        static MethodBase Callee(StackFrame[] frames, int i)
        {
            for (int j = i - 1; j >= 0; j--)
                if (frames[j].GetMethod() is { } m) return m;
            return null;
        }

        static MethodBase Caller(StackFrame[] frames, int i)
        {
            for (int j = i + 1; j < frames.Length; j++)
                if (frames[j].GetMethod() is { } m) return m;
            return null;
        }

        static bool Hidden(MethodBase m) =>
            m.IsDefined(typeof(StackTraceHiddenAttribute), false) || (m.DeclaringType?.IsDefined(typeof(StackTraceHiddenAttribute), false) ?? false);

        static string Condition(Exception e) => e.GetType().Name + ": " + e.Message;

        // The caller of frame I (the next frame with a method) is the engine's, or there is none.
        static bool CalledByEngine(StackFrame[] frames, int i)
        {
            for (int j = i + 1; j < frames.Length; j++)
            {
                var caller = frames[j].GetMethod();
                if (caller != null) return IsEngine(caller);
            }
            return true;
        }

        static bool IsLogging(MethodBase m)
        {
            var t = m.DeclaringType;
            return t == typeof(EngineLog) || t == typeof(LogTraces) || t?.Namespace == "OniMoono.Engine.Logging";
        }

        static bool IsEngine(MethodBase m)
        {
            var ns = m?.DeclaringType?.Namespace;
            return ns != null && (ns == "OniMoono" || ns.StartsWith("OniMoono.", StringComparison.Ordinal));
        }

        static string ModuleId(MethodBase m)
        {
            try { return m.Module.ModuleVersionId.ToString("N"); }
            catch (InvalidOperationException) { return "00000000000000000000000000000000"; }
        }

        // Unity's frame: "Namespace.Type:Method(ArgType, ArgType)", a nested type named by its own
        // name after the namespace ("SemanticsProbe.<>c:<All>b__7_4()").
        static string UnityFrame(MethodBase m)
        {
            var sb = new StringBuilder();
            var t = m.DeclaringType;
            if (t == null) sb.Append("(wrapper dynamic-method) ");
            else
            {
                if (!string.IsNullOrEmpty(t.Namespace)) sb.Append(t.Namespace).Append('.');
                sb.Append(t.Name).Append(':');
            }
            sb.Append(m.Name).Append('(');
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ps[i].ParameterType.Name);
            }
            return sb.Append(')').ToString();
        }

        // Mono's frame: "Namespace.Outer+Nested.Method (System.String name, …)", a generic type or
        // method named by its definition, its arguments (the outer types' too) on the innermost
        // type: "StateMachine`4+GenericInstance[StateMachineType,…].ExecuteActions ()".
        static string MonoMethod(MethodBase m)
        {
            if (m.DeclaringType is { IsGenericType: true } || m.IsGenericMethod)
            {
                try { m = m.Module.ResolveMethod(m.MetadataToken) ?? m; }
                catch (ArgumentException) { }
            }
            var sb = new StringBuilder();
            if (m.DeclaringType == null) sb.Append("(wrapper dynamic-method) ");
            else sb.Append(MonoType(m.DeclaringType)).Append('.');
            sb.Append(m.Name);
            if (m.IsGenericMethod) AppendArguments(sb, m.GetGenericArguments());
            sb.Append(" (");
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(MonoType(ps[i].ParameterType)).Append(' ').Append(ps[i].Name);
            }
            return sb.Append(')').ToString();
        }

        static string MonoType(Type t)
        {
            if (t.IsByRef) return MonoType(t.GetElementType()) + "&";
            if (t.IsPointer) return MonoType(t.GetElementType()) + "*";
            if (t.IsArray) return MonoType(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsGenericParameter) return t.Name;
            var sb = new StringBuilder();
            AppendPath(sb, t);
            if (t.IsGenericType) AppendArguments(sb, t.GetGenericArguments());
            return sb.ToString();
        }

        static void AppendPath(StringBuilder sb, Type t)
        {
            if (t.IsNested)
            {
                AppendPath(sb, t.DeclaringType);
                sb.Append('+');
            }
            else if (!string.IsNullOrEmpty(t.Namespace)) sb.Append(t.Namespace).Append('.');
            sb.Append(t.Name);
        }

        static void AppendArguments(StringBuilder sb, Type[] arguments)
        {
            sb.Append('[');
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(MonoType(arguments[i]));
            }
            sb.Append(']');
        }
    }
}
