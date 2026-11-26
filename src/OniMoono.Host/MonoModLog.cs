#nullable enable
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// What Harmony's MonoMod reports only to its own debug log, which no player turns on, in
    /// Player.log: its warnings and errors, and the recompiles whose detour it does not move.
    ///
    /// When tiered compilation recompiles a patched method, MonoMod's JIT hook raises
    /// OnMethodCompiled and PlatformTripleDetourFactory's ManagedDetourBox writes the jump to the
    /// replacement into the new code. Some cases write no jump, or a bad one, and the new code then
    /// runs the method's original body with its patches skipped (issue #127: the game's own
    /// DailyReport.GetEntry ran, without compact-report-entries' prefix). MonoMod logs some of
    /// them: an exception in an OnMethodCompiled handler (which also skips every later handler, the
    /// move among them), a native exception in the JIT, code too short for any jump it knows
    /// ("defaulting to Abs64": 14 bytes written into less, past the method's end). It logs nothing
    /// when the box is being applied or undone while another thread's recompile arrives: the move
    /// returns at once (`IsApplying`, set under another lock than the move's). A prefix of the move
    /// logs that case, and code shorter than the longest jump, with the method's name.
    ///
    /// Not seen here: a compile nested in another on the same thread (the hook reports only the
    /// outer one) and a hook that could not set up its ICorJitInfo wrapper. Cost: MonoMod formats
    /// only messages at the levels subscribed (warnings and errors, rare), and the prefix runs once
    /// per recompile of a patched method (a few hundred a session).
    internal static class MonoModLog
    {
        /// Where lines go (EngineLog.Engine; tests replace it).
        internal static Action<string> Sink = EngineLog.Engine;

        /// The longest jump MonoMod writes on x64 (Abs64: jmp [rip+0] and the address).
        internal const int LongestJump = 14;

        static bool installed;
        static IDisposable? subscription;
        static readonly Limiter limiter = new Limiter(perKind: 5, total: 200);
        static PropertyInfo? isApplied, isApplying;
        [ThreadStatic] static int applyingHere;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            var harmony = typeof(Harmony).Assembly;
            try
            {
                Subscribe(harmony);
            }
            catch (Exception e)
            {
                Sink($"monomod log: its debug log cannot be read here ({e.GetType().Name}: {e.Message})");
            }
            try
            {
                var box = harmony.GetType("MonoMod.Core.Platforms.PlatformTripleDetourFactory+Detour+ManagedDetourBox", true)!;
                var baseBox = box.BaseType ?? throw new MissingMemberException(box.FullName, "DetourBoxBase");
                isApplied = baseBox.GetProperty("IsApplied") ?? throw new MissingMemberException(baseBox.FullName, "IsApplied");
                isApplying = baseBox.GetProperty("IsApplying") ?? throw new MissingMemberException(baseBox.FullName, "IsApplying");
                var detourBase = harmony.GetType("MonoMod.Core.Platforms.PlatformTripleDetourFactory+DetourBase", true)!;
                var h = new Harmony("OniMoono.Host.MonoModLog");
                h.Patch(AccessTools.Method(box, "OnMethodCompiled") ?? throw new MissingMethodException(box.FullName, "OnMethodCompiled"),
                    prefix: new HarmonyMethod(typeof(MonoModLog), nameof(Moving)));
                var own = new HarmonyMethod(typeof(MonoModLog), nameof(Entering));
                var done = new HarmonyMethod(typeof(MonoModLog), nameof(Leaving));
                foreach (var name in new[] { "Apply", "Undo" })
                    h.Patch(AccessTools.Method(detourBase, name) ?? throw new MissingMethodException(detourBase.FullName, name), prefix: own, finalizer: done);
            }
            catch (Exception e)
            {
                Sink($"monomod log: recompiles whose detour is not moved are not logged ({e.GetType().Name}: {e.Message})");
            }
        }

        /// MonoMod's DebugLog.Subscribe(Warning | Error | Assert, OnLogMessage), its delegate made
        /// over its internal LogLevel.
        static void Subscribe(Assembly harmony)
        {
            var log = harmony.GetType("MonoMod.Logs.DebugLog", true)!;
            var filterType = harmony.GetType("MonoMod.Logs.LogLevelFilter", true)!;
            var handlerType = log.GetNestedType("OnLogMessage", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(log.FullName, "OnLogMessage");
            var subscribe = log.GetMethod("Subscribe", BindingFlags.Public | BindingFlags.Static, [filterType, handlerType])
                ?? throw new MissingMethodException(log.FullName, "Subscribe");
            var invoke = handlerType.GetMethod("Invoke")!;
            var ps = Array.ConvertAll(invoke.GetParameters(), p => Expression.Parameter(p.ParameterType, p.Name));
            var call = Expression.Call(typeof(MonoModLog).GetMethod(nameof(Logged), BindingFlags.NonPublic | BindingFlags.Static)!,
                ps[0], Expression.Convert(ps[2], typeof(int)), ps[3]);
            var handler = Expression.Lambda(handlerType, call, ps).Compile();
            // Warning 8, Error 0x10, Assert 0x20 (MonoMod.Logs.LogLevelFilter).
            var filter = Enum.ToObject(filterType, 8 | 0x10 | 0x20);
            subscription = (IDisposable?)subscribe.Invoke(null, [filter, handler]);
        }

        /// A MonoMod message at a subscribed level (LogLevel: 3 Warning, 4 Error, 5 Assert).
        internal static void Logged(string source, int level, string message)
        {
            // MonoMod's uname() probe throws in its library lookup on Linux and falls back; known, harmless.
            if (message.StartsWith("Error trying to detect info on POSIX-like system", StringComparison.Ordinal)) return;
            if (message.Length > 2000) message = message[..2000] + " …";
            string kind = level switch { 3 => "warning", 4 => "error", 5 => "assert", _ => "message" };
            Report($"monomod {kind} ({source}): {message}");
        }

        static void Entering() => applyingHere++;

        static Exception? Leaving(Exception? __exception)
        {
            applyingHere--;
            return __exception;
        }

        /// Prefix of ManagedDetourBox.OnMethodCompiled: what MonoMod's move is about to skip or
        /// overrun.
        static void Moving(object __instance, MethodBase method, ulong codeSize)
        {
            try
            {
                bool applied = (bool)isApplied!.GetValue(__instance)!;
                bool applying = (bool)isApplying!.GetValue(__instance)!;
                var why = Skipped(applied, applying, applyingHere > 0, codeSize);
                if (why != null) Report($"monomod: {Name(method)} was recompiled and {why}");
            }
            catch (Exception)
            {
                // Never stand in the move's way.
            }
        }

        /// Why a recompile's move goes wrong, or null. `ownApply`: this thread is applying or
        /// undoing a detour (MonoMod compiling the method it detours, as it does).
        internal static string? Skipped(bool applied, bool applying, bool ownApply, ulong codeSize)
        {
            if (!applied) return null;
            if (applying && !ownApply)
                return "its detour was being applied or undone on another thread: the new code runs without its patches until it is recompiled again";
            if (!applying && codeSize < LongestJump)
                return $"its new code is {codeSize} byte(s): the jump to its patches may be written past its end (up to {LongestJump} bytes)";
            return null;
        }

        static string Name(MethodBase? m) => m == null ? "a method" : $"{m.DeclaringType?.FullName}.{m.Name}";

        static void Report(string line)
        {
            if (limiter.Take(line, out int repeats) is { } text) Sink(repeats > 1 ? $"{text} ({repeats:N0} times so far)" : text);
        }

        /// Lines by kind (the text with its numbers taken out): the first few of each kind, then every
        /// thousandth with its count, and none past a total, which is logged once.
        internal sealed class Limiter
        {
            static readonly Regex Numbers = new Regex(@"(0x)?[0-9A-Fa-f]{6,}|\d+", RegexOptions.CultureInvariant);
            readonly int perKind, total;
            readonly Dictionary<string, int> seen = new();
            int written;

            public Limiter(int perKind, int total)
            {
                this.perKind = perKind;
                this.total = total;
            }

            /// The line to write, or null; `repeats`: how many of its kind so far.
            public string? Take(string line, out int repeats)
            {
                string kind = Numbers.Replace(line, "#");
                lock (seen)
                {
                    seen.TryGetValue(kind, out repeats);
                    seen[kind] = ++repeats;
                    if (repeats > perKind && repeats % 1000 != 0) return null;
                    if (written > total) return null;
                    if (++written > total) return $"monomod log: {total} lines written; no more this session";
                    return line;
                }
            }
        }
    }
}
