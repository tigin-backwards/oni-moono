#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// How a game tweak installs (ARCHITECTURE "How game tweaks install"): checked against the
    /// game's files before anything is patched (Check, TweakChecks), then installed all or
    /// nothing (Guarded). A tweak that fails either is off for the run: a no-op, the game's own
    /// code runs, one log line says why, and the Moono Tweaks screen shows its row greyed out.
    /// Nothing is checked while the game plays: no guard runs in a patched method.
    internal static class TweakInstall
    {
        static readonly Dictionary<string, string> unavailable = new(StringComparer.Ordinal);
        static readonly Dictionary<string, List<GameMemberDeclaration>> declared = new(StringComparer.Ordinal);

        /// The tweaks off for this run, with why.
        public static IReadOnlyDictionary<string, string> Unavailable
        {
            get
            {
                Join();
                return unavailable;
            }
        }

        public static bool Available(string name)
        {
            Join();
            return !unavailable.ContainsKey(name);
        }

        /// The checks running in the background (Start), applied by Join.
        static System.Threading.Tasks.Task<(Dictionary<string, TweakCheck>? Checks, string? Failure, double Ms)>? pending;

        /// Every tweak's checks against the game's assemblies in `managed`, from the host's and
        /// the typed assembly's declarations, on a worker: start-up goes on meanwhile, and the
        /// first to ask (Available, before the tweaks install) waits for them.
        public static void Start(string managed, string typedPath, Assembly host, IReadOnlyList<string> tweaks)
        {
            pending = System.Threading.Tasks.Task.Run<(Dictionary<string, TweakCheck>? Checks, string? Failure, double Ms)>(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var game = new GameFiles(managed);
                    using var typedFile = MetadataFile.Open(typedPath);
                    using var hostFile = MetadataFile.Loaded(host);
                    var assemblies = new List<TweakChecks.Assembly>();
                    if (hostFile != null) assemblies.Add(new TweakChecks.Assembly(hostFile, typed: false));
                    if (typedFile != null) assemblies.Add(new TweakChecks.Assembly(typedFile, typed: true));
                    var checks = TweakChecks.Run(game, assemblies, tweaks);
                    return (checks, null, clock.Elapsed.TotalMilliseconds);
                }
                catch (Exception e) when (e is System.IO.IOException or BadImageFormatException or InvalidOperationException or UnauthorizedAccessException)
                {
                    return (null, $"{e.GetType().Name}: {e.Message}", clock.Elapsed.TotalMilliseconds);
                }
            });
        }

        /// Start, and wait for it.
        public static void Check(string managed, string typedPath, Assembly host, IReadOnlyList<string> tweaks)
        {
            Start(managed, typedPath, host, tweaks);
            Join();
        }

        /// The background checks' results, once: each tweak off is logged.
        static readonly object joining = new();

        static void Join()
        {
            if (pending == null) return;
            lock (joining) JoinLocked();
        }

        static void JoinLocked()
        {
            if (pending == null) return;
            var wait = System.Diagnostics.Stopwatch.StartNew();
            var (checks, failure, ms) = pending.Result;
            double waited = wait.Elapsed.TotalMilliseconds;
            pending = null;
            // Without the checks, the tweaks install as they did before there were any.
            if (failure != null) EngineLog.Engine($"game tweaks: not checked against the game: {failure}");
            if (checks != null)
                foreach (var (name, check) in checks)
                {
                    declared[name] = check.Members;
                    if (check.Reason is { } reason) TurnOff(name, reason);
                }
            EngineLog.Engine($"game tweaks: checked against the game in {ms:F1} ms on a worker ({waited:F1} ms waited for), {unavailable.Count} off");
        }

        /// A tweak's declarations, as Check read them (none before it).
        public static IReadOnlyList<GameMemberDeclaration> Declared(string name)
        {
            Join();
            return declared.TryGetValue(name, out var members) ? members : [];
        }

        /// For tests: a tweak's declarations as Check would have read them.
        internal static void Declare(string name, List<GameMemberDeclaration> members) => declared[name] = members;

        /// For tests: forget every check and install.
        internal static void Reset()
        {
            pending = null;
            unavailable.Clear();
            declared.Clear();
        }

        public static void TurnOff(string name, string reason)
        {
            if (!unavailable.TryAdd(name, reason)) return;
            EngineLog.Engine($"game tweak {name}: off, {reason}");
        }

        /// One tweak's install, all or nothing: when it throws, or a transpiler of it rewrote
        /// other than the sites it declares (Rewrites), what it patched is unpatched and it is off
        /// for the run. `typed`: the typed assembly once loaded (its TranspilerCounts too). True
        /// when it is on.
        public static bool Guarded(string name, Action<Harmony> install, Func<Assembly?> typed, bool log = true)
        {
            if (!Available(name)) return false;
            var harmony = new Harmony("OniMoono.Host.GameTweaks." + name);
            string? problem;
            try
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                Counts(typed()); // a previous install's leftovers (another tweak's transpilers run again)
                long installing = System.Diagnostics.Stopwatch.GetTimestamp();
                install(harmony);
                long installed = System.Diagnostics.Stopwatch.GetTimestamp();
                problem = Rewrites(name, harmony, Counts(typed()));
                CheckTicks += installing - start + System.Diagnostics.Stopwatch.GetTimestamp() - installed;
            }
            catch (Exception e)
            {
                var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                problem = $"its install failed: {cause.GetType().Name}: {cause.Message}";
            }
            if (problem == null)
            {
                if (log) EngineLog.Engine($"game tweak {name}");
                return true;
            }
            try { harmony.UnpatchAll(harmony.Id); }
            catch (Exception e) { EngineLog.Engine($"game tweak {name}: its patches could not all be taken back: {e.GetType().Name}: {e.Message}"); }
            TurnOff(name, problem);
            return false;
        }

        /// The time Guarded spent on its own bookkeeping (counts and their check), for the log.
        public static long CheckTicks;

        static MethodInfo? typedTake;

        /// The transpilers' counts reported since the last call, from the host's and the typed
        /// assembly's TranspilerCounts.
        static List<(string Tweak, MethodBase Original, int Count)> Counts(Assembly? typed)
        {
            var result = new List<(string, MethodBase, int)>();
            void Add(object[] flat)
            {
                for (int i = 0; i + 2 < flat.Length; i += 3) result.Add(((string)flat[i], (MethodBase)flat[i + 1], (int)flat[i + 2]));
            }
            Add(TranspilerCounts.Take());
            if (typed != null && (typedTake ??= typed.GetType("OniMoono.Tweaks.TranspilerCounts")?.GetMethod("Take")) is { } take) Add((object[])take.Invoke(null, null)!);
            return result;
        }

        /// 4: each of the tweak's transpilers against its declaration, by its last count: more
        /// sites than declared fails; fewer passes only when another owner transpiles the method
        /// too (that transpiler ran first and took those sites, issue #41). A transpiler that is
        /// not declared, or reported nothing, fails.
        internal static string? Rewrites(string name, Harmony harmony, List<(string Tweak, MethodBase Original, int Count)> counts)
        {
            Join();
            declared.TryGetValue(name, out var members);
            // A tweak that declares no transpiler and reported no count has nothing to compare
            // (an undeclared transpiler reports one).
            if ((members == null || !members.Any(m => (m.Use & GameUse.Transpiler) != 0)) && !counts.Any(c => c.Tweak == name)) return null;
            foreach (var method in harmony.GetPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null || !info.Transpilers.Any(t => t.owner == harmony.Id)) continue;
                string signature = TweakChecks.Signature(method);
                var member = members?.FirstOrDefault(m => (m.Use & GameUse.Transpiler) != 0 && m.Member == signature);
                if (member == null) return $"`{signature}`: its transpiler is not declared";
                int count = 0;
                foreach (var c in counts)
                    if (c.Tweak == name && c.Original == method) count = c.Count;
                if (count == member.Rewrites) continue;
                bool shared = count < member.Rewrites && info.Transpilers.Any(t => t.owner != harmony.Id);
                if (!shared) return $"`{member.Display}` changed in this game version (its transpiler rewrote {count} of {member.Rewrites} sites)";
            }
            return null;
        }
    }
}
