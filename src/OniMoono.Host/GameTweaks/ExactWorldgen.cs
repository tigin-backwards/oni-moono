using System.Linq;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `exact-worldgen` (MonoFloatWidening): enabled before any game assembly loads,
    /// as the rewrite runs while they load. Worldgen's float arithmetic runs at the precision of
    /// Unity's Mono (ARCHITECTURE D25), so a seed makes the original's map (issue #66). The
    /// members below are the scope's anchors (MonoFloatWidening.Scope): the global helpers it
    /// names and the entry points of the namespaces it widens, in both supported game updates.
    /// The rewrite does not depend on their bodies, so none is fingerprinted; a game update that
    /// moves worldgen elsewhere fails these checks or the `exact-worldgen` spec.
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<UnityEngine.Vector2> PointGenerator::GetRandomPoints(Delaunay.Geo.Polygon,System.Single,System.Single,System.Collections.Generic.List`1<UnityEngine.Vector2>,PointGenerator/SampleBehaviour,System.Boolean,SeededRandom,System.Boolean,System.Boolean)")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<UnityEngine.Vector2> UniformPoissonDiskSampler::Sample(UnityEngine.Vector2,UnityEngine.Vector2,System.Nullable`1<System.Single>,System.Single,System.Int32)")]
    [GameMember(GameUse.Uses, "System.Single MathUtil::GetClosestPointBetweenPointAndLineSegment(MathUtil/Pair`2<UnityEngine.Vector2,UnityEngine.Vector2>,UnityEngine.Vector2,System.Single&)")]
    [GameMember(GameUse.Uses, "System.Single SeededRandom::RandomValue()")]
    [GameMember(GameUse.Uses, "System.Void VoronoiTree.PowerDiagram::ComputePowerDiagram(System.Int32,System.Single)")]
    [GameMember(GameUse.Uses, "System.Boolean ProcGenGame.WorldGen::RenderOffline(System.Boolean,System.UInt32,System.IO.BinaryWriter,ProcGenGame.WorldgenSimData&,System.Int32,System.Collections.Generic.List`1<ProcGen.WorldTrait>&,System.Boolean)")]
    internal static class ExactWorldgen
    {
        public const string Name = GameTweakNames.ExactWorldgen;

        /// Program, before the game's assemblies load.
        public static void Enable() => MonoFloatWidening.Enabled = true;

        /// GameTweaks.Install: nothing to patch; says whether the game's worldgen was widened
        /// (the load's log names each assembly).
        public static void Install(Harmony harmony)
        {
            var probe = AccessTools.Method("SeededRandom:RandomValue");
            bool widened = probe != null && PatchProcessor.GetOriginalInstructions(probe).Any(i => i.opcode == System.Reflection.Emit.OpCodes.Conv_R8);
            if (!widened)
                EngineLog.Engine($"game tweak {Name}: worldgen keeps .NET's float32 arithmetic: the game's assemblies were not rewritten (the log of their load says why)");
        }

        /// Program's runtime checks: a method of float arithmetic whose results differ between
        /// float32 and Mono's double evaluation (FloatPrecision's measured cases), widened in a
        /// module built here, gives the original's values.
        internal static string SelfCheck() => MonoFloatWidening.SelfCheck();
    }
}
