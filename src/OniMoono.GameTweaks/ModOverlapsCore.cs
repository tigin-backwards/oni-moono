using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OniMoono.GameTweaks
{
    /// A patch's kind as Harmony lists them, and `Rewrite` for the host's load-time IL rewrite.
    public enum PatchKind { Prefix, Postfix, Transpiler, Finalizer, Rewrite }

    /// How much a mod's patch can get in the way of a tweak on the same method. `StepsAside`: the
    /// tweak turns itself off for that patch, so it is no conflict.
    public enum OverlapGrade { Low, High, StepsAside }

    /// How one tweak touches one method: its own Harmony patches (`SkipPrefix`: a prefix that
    /// returns bool, so it can skip the original), the host's IL rewrite of it, and `Guard`: the
    /// tweak steps aside when another owner patches it. `StepsAsideOnTranspiler`: a rewrite whose
    /// tweak runs flat when an unreviewed transpiler changes the method (compact-nav-links).
    public sealed class TweakUse
    {
        public string Tweak;
        public bool Prefix, SkipPrefix, Postfix, Transpiler, Finalizer, Rewrite, Guard, StepsAsideOnTranspiler;

        /// The tweak changes what the method's body does: a mod skipping the body skips it too.
        public bool Replaces => SkipPrefix || Transpiler || Rewrite;
    }

    /// Another owner's patch of a method a tweak touches: its Harmony ID, the mod's title, its
    /// kind (`CanSkip`: a prefix returning bool) and the class that holds it.
    public sealed class ForeignPatch
    {
        public string Owner, Mod, PatchClass;
        public PatchKind Kind;
        public bool CanSkip;
    }

    /// One patched method: its name as the log and tooltips show it, the tweaks that touch it and
    /// the other owners' patches.
    public sealed class PatchedMethod
    {
        public string Method;
        public List<TweakUse> Uses = new List<TweakUse>();
        public List<ForeignPatch> Patches = new List<ForeignPatch>();
    }

    /// One overlap: a tweak, a method it touches, a mod's patch there and how it is graded.
    public sealed class Overlap
    {
        public string Tweak, Method, Mod, Owner;
        public PatchKind Kind;
        public bool CanSkip;
        public OverlapGrade Grade;
    }

    /// The overlaps of the mods' Harmony patches with the game tweaks (ModOverlaps gathers them
    /// in the game): grading, the log's block, the main menu's count and the rows' tooltips.
    /// Plain .NET with no game types (the tests compile it in).
    public static class ModOverlapsCore
    {
        /// Each tweak patches under its own Harmony ID, "OniMoono.Host.GameTweaks.NAME"
        /// (OniMoono.Host.GameTweaks.GameTweaks makes them).
        public const string TweakOwnerPrefix = "OniMoono.Host.GameTweaks";

        public static string OwnerOf(string tweak) => TweakOwnerPrefix + "." + tweak;

        /// The tweak a Harmony ID is, or null for any other owner.
        public static string TweakOf(string owner) =>
            owner != null && owner.Length > TweakOwnerPrefix.Length + 1 && owner.StartsWith(TweakOwnerPrefix + ".", StringComparison.Ordinal)
                ? owner.Substring(TweakOwnerPrefix.Length + 1) : null;

        /// A tweak's own patch (the tweaks' checks for other owners' patches use this).
        public static bool IsTweakOwner(string owner) => owner == TweakOwnerPrefix || TweakOf(owner) != null;

        /// Ours, never an overlap: the host's patches (OniMoono.*) and the perf tool's
        /// stopwatches (PerfProbe, as path-probe-repair ignores them).
        public static bool IsOurs(string owner) =>
            owner == null || owner.StartsWith("OniMoono.", StringComparison.Ordinal) || owner == "PerfProbe" || owner.StartsWith("PerfProbe.", StringComparison.Ordinal);

        /// How a mod's patch on a method meets a tweak's use of it:
        /// - steps aside: the tweak guards the method (it turns itself off for such a patch), or a
        ///   transpiler that was not reviewed changes a method compact-nav-links rewrites;
        /// - high: a transpiler on a method the tweak patches or rewrites (it sees other IL than
        ///   the game's), or a prefix that can skip the original on a method the tweak replaces;
        /// - low: anything else (postfixes, finalizers, prefixes that return nothing, a
        ///   reviewed transpiler).
        public static OverlapGrade Grade(TweakUse use, ForeignPatch patch, bool reviewed)
        {
            if (use.Guard) return OverlapGrade.StepsAside;
            if (patch.Kind == PatchKind.Transpiler)
            {
                if (use.Rewrite && use.StepsAsideOnTranspiler) return reviewed ? OverlapGrade.Low : OverlapGrade.StepsAside;
                return OverlapGrade.High;
            }
            if (patch.Kind == PatchKind.Prefix && patch.CanSkip && use.Replaces) return OverlapGrade.High;
            return OverlapGrade.Low;
        }

        /// Every overlap of the methods, in the order of the tweaks (`order`, then by name), the
        /// methods and the patches. `reviewed(tweak, method, patch class)`: a transpiler known to
        /// work with that tweak's rewrite.
        public static List<Overlap> Find(IEnumerable<PatchedMethod> methods, Func<string, string, string, bool> reviewed, IList<string> order = null)
        {
            var found = new List<Overlap>();
            foreach (var m in methods)
                foreach (var use in m.Uses)
                    foreach (var p in m.Patches)
                    {
                        if (IsOurs(p.Owner)) continue;
                        bool known = p.Kind == PatchKind.Transpiler && reviewed != null && reviewed(use.Tweak, m.Method, p.PatchClass);
                        found.Add(new Overlap
                        {
                            Tweak = use.Tweak, Method = m.Method, Mod = string.IsNullOrEmpty(p.Mod) ? p.Owner : p.Mod, Owner = p.Owner,
                            Kind = p.Kind, CanSkip = p.CanSkip, Grade = Grade(use, p, known),
                        });
                    }
            int Rank(string tweak)
            {
                int i = order == null ? -1 : order.IndexOf(tweak);
                return i < 0 ? int.MaxValue : i;
            }
            // A mod's two patches of the same kind on one method are one overlap. (Plain loops and
            // a comparison: this runs at the first main menu, where every generic instantiation
            // over a struct key would be compiled first.)
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<Overlap>();
            foreach (var o in found)
                if (seen.Add($"{o.Tweak}\n{o.Method}\n{o.Mod}\n{(int)o.Kind}\n{o.CanSkip}\n{(int)o.Grade}")) unique.Add(o);
            unique.Sort((a, b) =>
            {
                int c = Rank(a.Tweak).CompareTo(Rank(b.Tweak));
                if (c == 0) c = string.CompareOrdinal(a.Tweak, b.Tweak);
                if (c == 0) c = string.CompareOrdinal(a.Method, b.Method);
                if (c == 0) c = string.CompareOrdinal(a.Mod, b.Mod);
                if (c == 0) c = ((int)a.Kind).CompareTo((int)b.Kind);
                return c;
            });
            return unique;
        }

        /// The main menu's count: each tweak and mod with a high-grade overlap, once.
        public static int HighCount(IEnumerable<Overlap> overlaps) =>
            overlaps.Where(o => o.Grade == OverlapGrade.High).Select(o => o.Tweak + "\n" + o.Mod).Distinct().Count();

        /// A patch's kind as the log names it ("prefix (can skip)" for a bool prefix).
        public static string KindName(PatchKind kind, bool canSkip) => kind switch
        {
            PatchKind.Prefix => canSkip ? "prefix (can skip)" : "prefix",
            PatchKind.Postfix => "postfix",
            PatchKind.Transpiler => "transpiler",
            PatchKind.Finalizer => "finalizer",
            _ => "rewrite",
        };

        public static string GradeName(OverlapGrade grade) => grade switch
        {
            OverlapGrade.High => "high",
            OverlapGrade.StepsAside => "steps aside",
            _ => "low",
        };

        /// Player.log's block: a line of totals and the cost, then a line per overlap.
        public static string LogBlock(IList<Overlap> overlaps, int patchedMethods, double ms)
        {
            var log = new StringBuilder();
            int high = overlaps.Count(o => o.Grade == OverlapGrade.High), aside = overlaps.Count(o => o.Grade == OverlapGrade.StepsAside);
            log.Append($"[Moono] mod overlaps: {(overlaps.Count == 0 ? "none" : overlaps.Count.ToString())}");
            if (overlaps.Count > 0) log.Append($" ({high} high, {overlaps.Count - high - aside} low, {aside} where the tweak steps aside)");
            log.Append($"; {patchedMethods} patched method(s) read in {ms:0.00} ms");
            foreach (var o in overlaps)
                log.Append($"\n  {o.Tweak} | {o.Method} | {StripTags(o.Mod)} ({o.Owner}) | {KindName(o.Kind, o.CanSkip)} | {GradeName(o.Grade)}");
            return log.ToString();
        }

        /// The tweak's overlaps for its row's tooltip, one line per mod and what its patches can
        /// do (`ui`: the screen's labels, STRINGS.MOONO.TWEAKS.UI.NAME): where the tweak steps
        /// aside first, then high, then low, and the advice once when there is a conflict; ""
        /// for none. `warning`: the high lines' colour (hex, no #).
        public static string Tooltip(IEnumerable<Overlap> overlaps, Func<string, string> ui, string warning)
        {
            var lines = new List<string>();
            bool conflict = false;
            var mine = overlaps.ToList();
            foreach (var (grade, reason) in new[] { (OverlapGrade.StepsAside, "OVERLAP_STEPS_ASIDE"), (OverlapGrade.High, "OVERLAP_REWRITES"),
                         (OverlapGrade.High, "OVERLAP_SKIPS"), (OverlapGrade.Low, "OVERLAP_PATCHES") })
            {
                var group = mine.Where(o => o.Grade == grade && Reason(o) == reason).ToList();
                foreach (var mod in group.Select(o => o.Mod).Distinct())
                {
                    if (grade != OverlapGrade.StepsAside) conflict = true;
                    // A mod's high line names its methods once, under its strongest reason.
                    if (reason == "OVERLAP_SKIPS" && mine.Any(o => o.Mod == mod && Reason(o) == "OVERLAP_REWRITES")) continue;
                    if (reason == "OVERLAP_PATCHES" && mine.Any(o => o.Mod == mod && o.Grade == OverlapGrade.High)) continue;
                    var methods = group.Where(o => o.Mod == mod).Select(o => o.Method).Distinct().ToList();
                    var line = string.Format(ui(reason), StripTags(mod), Methods(methods, ui));
                    lines.Add(grade == OverlapGrade.High ? $"<color=#{warning}>{line}</color>" : line);
                }
            }
            if (lines.Count == 0) return "";
            if (conflict) lines.Add(ui("OVERLAP_ADVICE"));
            return string.Join("\n", lines);
        }

        static string Reason(Overlap o) =>
            o.Grade == OverlapGrade.StepsAside ? "OVERLAP_STEPS_ASIDE"
            : o.Grade == OverlapGrade.High ? (o.Kind == PatchKind.Transpiler ? "OVERLAP_REWRITES" : "OVERLAP_SKIPS")
            : "OVERLAP_PATCHES";

        /// Up to three methods, then how many more.
        static string Methods(List<string> methods, Func<string, string> ui)
        {
            var shown = string.Join(ui("LIST_SEPARATOR"), methods.Take(3));
            return methods.Count <= 3 ? shown : string.Format(ui("OVERLAP_MORE_METHODS"), shown, methods.Count - 3);
        }

        /// The worst grade of a tweak's overlaps that count (high over low), or null when it has
        /// none; StepsAside when all of them are steps aside.
        public static OverlapGrade? Worst(IEnumerable<Overlap> overlaps)
        {
            OverlapGrade? worst = null;
            foreach (var o in overlaps)
            {
                if (o.Grade == OverlapGrade.High) return OverlapGrade.High;
                if (o.Grade == OverlapGrade.Low || worst == null) worst = o.Grade;
            }
            return worst;
        }

        /// A mod title without its rich-text tags (some titles are coloured).
        public static string StripTags(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
            var plain = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                int end = text[i] == '<' ? text.IndexOf('>', i + 1) : -1;
                // A tag: up to its '>', with no '<' inside.
                if (end > 0 && text.IndexOf('<', i + 1, end - i - 1) < 0) i = end;
                else plain.Append(text[i]);
            }
            return plain.ToString();
        }
    }
}
