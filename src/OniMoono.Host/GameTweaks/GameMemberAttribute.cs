#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;

// Compiled into OniMoono.Host and OniMoono.GameTweaks alike: each assembly has its own copy, and
// the host reads both assemblies' declarations from their metadata (TweakChecks), never through
// these types.
namespace OniMoono.Tweaks
{
    /// What a tweak does with a game member (GameMemberAttribute).
    [Flags]
    internal enum GameUse
    {
        /// Reaches it by name (reflection, a delegate, a field it reads): it must be there as declared.
        /// `--check-tweaks` fails on a lookup by a constant name (a literal or `nameof`) that is
        /// not declared (TweakDrift).
        Uses = 0,
        Prefix = 1,
        Postfix = 2,
        Transpiler = 4,
        Finalizer = 8,
        /// A prefix that can skip the method: what runs instead mirrors it, so its body is fingerprinted.
        Replaces = 16,
        /// Reimplemented without being patched (an exact copy of what it does): fingerprinted.
        Mirrors = 32,
    }

    /// A game member a tweak relies on, on the tweak's class (the one whose `Name` constant is
    /// its `--game-tweak` name). `Member` is its full signature as the game's metadata spells it:
    /// `System.Void KAnimBatch::UpdateDirty(System.Int32)` for a method (return type, declaring
    /// type with `/` between nested types, name, parameter types; `` `N`` after a generic method's
    /// name), `System.Int32 Grid::WidthInCells` for a field, `PathFinder/Cell` for a type the
    /// tweak takes by name (`Uses` only). Before the tweak patches anything,
    /// every declared member must be in the game with that signature (TweakChecks).
    ///
    /// `Fingerprints`: for a member it transpiles, replaces or mirrors, the accepted hashes of
    /// the game's own IL for it (TweakMetadata.Fingerprint), one per game build that has a
    /// different body, each followed by the builds it was taken from ("3f2a… 744825"); any
    /// other body turns the tweak off until it is reviewed (`--check-tweaks --review`).
    /// `Rewrites`: for a transpiler, the sites it rewrites in that body (TranspilerCounts).
    /// `Optional`: a member only some game builds have (the tweak checks for it itself); when
    /// it is there, it must match.
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    internal sealed class GameMemberAttribute : Attribute
    {
        public GameMemberAttribute(GameUse use, string member, params string[] fingerprints)
        {
            Use = use;
            Member = member;
            Fingerprints = fingerprints;
        }

        public GameUse Use { get; }
        public string Member { get; }
        public string[] Fingerprints { get; }
        public int Rewrites { get; set; } = -1;
        public bool Optional { get; set; }
    }

    /// On the code that reimplements a game method without patching it: `Member` is the method's
    /// signature as GameMemberAttribute spells it, and the tweak's class must declare it
    /// `GameUse.Mirrors` with its fingerprints (`--check-tweaks`, TweakDrift); a Mirrors
    /// declaration with no code marked so fails too.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property,
        AllowMultiple = true, Inherited = false)]
    internal sealed class MirrorsGameAttribute : Attribute
    {
        public MirrorsGameAttribute(string member) => Member = member;

        public string Member { get; }
    }

    /// On a method (or class) whose lookups of game members by a name or type the IL does not
    /// give as constants (`AccessTools.Field(type, name)` with both passed in) were reviewed:
    /// `--check-tweaks` notes them instead of warning (TweakDrift). Constant lookups of game
    /// members still need their GameMember declarations.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property,
        AllowMultiple = false, Inherited = false)]
    internal sealed class NameLookupReviewedAttribute : Attribute
    {
        public NameLookupReviewedAttribute(string reason) => Reason = reason;

        public string Reason { get; }
    }

    /// Each transpiler's count of the sites it rewrote, for the host's check after a tweak
    /// installs (GameTweaks.Install): more than declared fails, fewer passes only when another
    /// Harmony owner transpiles the method too (its change ran first, issue #41).
    internal static class TranspilerCounts
    {
        static readonly List<(string Tweak, MethodBase Original, int Count)> counts = new();

        /// `original`: the method being transpiled (the transpiler's `MethodBase original` argument).
        public static void Report(string tweak, MethodBase? original, int count)
        {
            if (original == null) return;
            lock (counts) counts.Add((tweak, original, count));
        }

        /// The counts reported since the last call (the host's, through reflection for the
        /// typed assembly's copy): tweak names, methods and counts.
        public static object[] Take()
        {
            lock (counts)
            {
                var result = new object[counts.Count * 3];
                for (int i = 0; i < counts.Count; i++)
                {
                    result[i * 3] = counts[i].Tweak;
                    result[i * 3 + 1] = counts[i].Original;
                    result[i * 3 + 2] = counts[i].Count;
                }
                counts.Clear();
                return result;
            }
        }
    }
}
