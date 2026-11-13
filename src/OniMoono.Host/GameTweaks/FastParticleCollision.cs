using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `fast-particle-collision` (opt-in). Every frame, each radbolt
    /// (HighEnergyParticle.CheckCollision) gathers every collider in the 3×3 cells around it and
    /// calls GetComponent&lt;HighEnergyParticle&gt;() on each one's GameObject to find other
    /// radbolts. A colony has tens of thousands of colliders (every stored item) and only a handful
    /// of radbolts, so nearly all of those lookups scan an item's components and find nothing
    /// (U57: 0.9 ms a frame for 4 radbolts).
    ///
    /// Two changes, each giving the method the same outcome:
    /// - The gather is skipped unless another radbolt has a collider registered in those cells (the
    ///   colliders' partitioner registrations, KCollider2D.cachedExtents while its entry is valid,
    ///   are checked against the 3×3 area) that the method would merge with: the radbolt is
    ///   collideable, this one's circle collider holds its position and its collider holds this
    ///   one's (the method's own test, which changes nothing). The gathered entries only matter
    ///   through such a collider, so an empty list gives the same result; when there is one, the
    ///   real gather is made, so the first in the gather's order is the one merged.
    /// - When it gathers, GetComponent&lt;HighEnergyParticle&gt;() on each collider's GameObject is a
    ///   lookup among the live radbolt components (registered as they are constructed): a radbolt
    ///   carries one HighEnergyParticle (its prefab), so the lookup returns what GetComponent does.
    /// A radbolt's colliders are its prefab's (HighEnergyParticleConfig adds one), so each radbolt's
    /// are looked up once, the first time another radbolt checks against it, rather than on every
    /// check: with many radbolts in flight every one of them checks every other each frame.
    /// With ONI_TWEAK_VERIFY=1 (test runs) the skipped gathers are still made and checked for a
    /// radbolt the method would have merged with, and both lookups are compared; differences are
    /// logged.
    [GameMember(GameUse.Uses, "System.Void GameScenePartitioner::GatherEntries(System.Int32,System.Int32,System.Int32,System.Int32,ScenePartitionerLayer,System.Collections.Generic.List`1<ScenePartitionerEntry>)")]
    [GameMember(GameUse.Postfix, "System.Void HighEnergyParticle::.ctor()")]
    [GameMember(GameUse.Transpiler, "System.Void HighEnergyParticle::CheckCollision()", "4322d622f5b47a0c linux-744825 win-744825", "4aa8354cd16662f1 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023", Rewrites = 2)]
    [GameMember(GameUse.Uses, "System.Boolean HighEnergyParticle::isCollideable")]
    [GameMember(GameUse.Uses, "System.Boolean KCollider2D::Intersects(UnityEngine.Vector2)")]
    [GameMember(GameUse.Uses, "Extents KCollider2D::cachedExtents")]
    [GameMember(GameUse.Uses, "HandleVector`1/Handle<System.Int32> KCollider2D::partitionerEntry")]
    [GameMember(GameUse.Uses, "System.Object ScenePartitionerEntry::obj")]
    [GameMember(GameUse.Uses, "KCircleCollider2D")]
    internal static class FastParticleCollision
    {
        public const string Name = GameTweakNames.FastParticleCollision;

        /// The live radbolts, each with its colliders once looked up.
        static readonly List<Live> live = new();
        sealed class Live(UnityEngine.Component particle)
        {
            public readonly UnityEngine.Component Particle = particle;
            public UnityEngine.Component[]? Colliders;
        }
        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static long checks, mismatches, skipped, gathered, wrongSkips;
        static MethodInfo getComponent = null!, gatherEntries = null!;
        static Action<object, int, int, int, int, object, object> gather = null!;
        /// A collider's partitioner registration: valid, and the cells it covers.
        static Func<object, (bool Valid, int X, int Y, int W, int H)> registration = null!;
        static Type colliderType = null!, circleType = null!;
        static Func<object, bool> collideable = null!;
        /// KCollider2D.Intersects(Vector2), virtual.
        static Func<object, UnityEngine.Vector2, bool> intersects = null!;

        public static void Install(Harmony harmony)
        {
            var particle = AccessTools.TypeByName("HighEnergyParticle");
            getComponent = AccessTools.Method(typeof(UnityEngine.GameObject), nameof(UnityEngine.GameObject.GetComponent), Type.EmptyTypes).MakeGenericMethod(particle);
            colliderType = AccessTools.TypeByName("KCollider2D");
            circleType = AccessTools.TypeByName("KCircleCollider2D");
            collideable = CompileCollideable(particle);
            intersects = CompileIntersects();
            var partitioner = AccessTools.TypeByName("GameScenePartitioner");
            gatherEntries = AccessTools.Method(partitioner, "GatherEntries", [typeof(int), typeof(int), typeof(int), typeof(int), AccessTools.TypeByName("ScenePartitionerLayer"), typeof(List<>).MakeGenericType(AccessTools.TypeByName("ScenePartitionerEntry"))]);
            gather = CompileGather();
            registration = CompileRegistration();
            harmony.Patch(AccessTools.Constructor(particle), postfix: new HarmonyMethod(typeof(FastParticleCollision), nameof(Constructed)));
            harmony.Patch(AccessTools.Method(particle, "CheckCollision"), transpiler: new HarmonyMethod(typeof(FastParticleCollision), nameof(Transpiler)));
        }

        static void Constructed(UnityEngine.Component __instance)
        {
            lock (live) live.Add(new Live(__instance));
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var replacement = AccessTools.Method(typeof(FastParticleCollision), nameof(ParticleOf));
            var gatherIfNeeded = AccessTools.Method(typeof(FastParticleCollision), nameof(GatherIfNeeded));
            int replaced = 0, gathers = 0;
            foreach (var i in instructions)
            {
                if (i.Calls(getComponent))
                {
                    replaced++;
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, replacement) { labels = i.labels, blocks = i.blocks };
                }
                else if (i.Calls(gatherEntries))
                {
                    gathers++;
                    // The particle doing the check, as a last argument.
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0) { labels = i.labels, blocks = i.blocks };
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, gatherIfNeeded);
                }
                else yield return i;
            }
            // One GetComponent<HighEnergyParticle>() and one GatherEntries in CheckCollision
            // (TweakInstall checks the count).
            OniMoono.Tweaks.TranspilerCounts.Report(Name, original, replaced + gathers);
        }

        /// GameScenePartitioner.GatherEntries(x, y, width, height, layer, list), made only when another
        /// radbolt has a collider registered in the area.
        [NameLookupReviewed("ScenePartitionerEntry.obj, declared")]
        static void GatherIfNeeded(object partitioner, int x, int y, int width, int height, object layer, object list, UnityEngine.Component self)
        {
            if (MergeableParticleIn(self, x, y, width, height))
            {
                gathered++;
                gather(partitioner, x, y, width, height, layer, list);
                return;
            }
            skipped++;
            if (!verify) return;
            gather(partitioner, x, y, width, height, layer, list);
            foreach (var entry in (System.Collections.IEnumerable)list)
            {
                var obj = AccessTools.Field(entry.GetType(), "obj").GetValue(entry) as UnityEngine.Component;
                if (obj == null) continue;
                var other = getComponent.Invoke(obj.gameObject, null) as UnityEngine.Component;
                // The lookup ParticleOf would give, checked here too: real gathers are rare now.
                CheckLookup(obj.gameObject, other);
                if (other != null && !ReferenceEquals(other, self) && collideable(other)
                    && intersects(self.GetComponent(circleType), (UnityEngine.Vector2)other.transform.position) && intersects(obj, (UnityEngine.Vector2)self.transform.position))
                    EngineLog.Engine($"{Name}: a skipped gather held radbolt {other.name}, which would have merged ({++wrongSkips})");
            }
            ((System.Collections.IList)list).Clear();
            if ((skipped + gathered) % 20000 == 0) EngineLog.Engine($"{Name}: {skipped} gathers skipped, {gathered} made, {wrongSkips} wrong skips, {live.Count} radbolts registered");
        }

        /// Whether another live radbolt has a collider registered in the area that CheckCollision would
        /// merge with (its test, in its order of operands).
        static bool MergeableParticleIn(UnityEngine.Component self, int x, int y, int width, int height)
        {
            UnityEngine.Component? circle = null;
            UnityEngine.Vector2 position = default;
            bool selfRead = false;
            lock (live)
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    var l = live[i];
                    var c = l.Particle;
                    // Destroyed radbolts leave here, every frame a radbolt checks, not only when a
                    // gather is made (most checks make none).
                    if (c == null) { live.RemoveAt(i); continue; }
                    if (ReferenceEquals(c, self)) continue;
                    foreach (var collider in l.Colliders ??= c.gameObject.GetComponents(colliderType))
                    {
                        var (valid, cx, cy, cw, ch) = registration(collider);
                        if (!(valid && cx < x + width && x < cx + cw && cy < y + height && y < cy + ch)) continue;
                        if (!collideable(c)) break;
                        if (!selfRead)
                        {
                            circle = self.GetComponent(circleType);
                            position = self.transform.position;
                            selfRead = true;
                        }
                        // Without a circle collider the method fails on its first candidate: let it.
                        if (circle == null) return true;
                        if (intersects(circle, (UnityEngine.Vector2)c.transform.position) && intersects(collider, position)) return true;
                    }
                }
            return false;
        }

        [NameLookupReviewed("HighEnergyParticle.isCollideable, declared")]
        static Func<object, bool> CompileCollideable(Type particle)
        {
            var arg = System.Linq.Expressions.Expression.Parameter(typeof(object), "particle");
            var field = System.Linq.Expressions.Expression.Field(System.Linq.Expressions.Expression.Convert(arg, particle), AccessTools.Field(particle, "isCollideable"));
            return System.Linq.Expressions.Expression.Lambda<Func<object, bool>>(field, arg).Compile();
        }

        static Func<object, UnityEngine.Vector2, bool> CompileIntersects()
        {
            var arg = System.Linq.Expressions.Expression.Parameter(typeof(object), "collider");
            var pos = System.Linq.Expressions.Expression.Parameter(typeof(UnityEngine.Vector2), "pos");
            var call = System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Convert(arg, colliderType), AccessTools.Method(colliderType, "Intersects", [typeof(UnityEngine.Vector2)]), pos);
            return System.Linq.Expressions.Expression.Lambda<Func<object, UnityEngine.Vector2, bool>>(call, arg, pos).Compile();
        }

        static Action<object, int, int, int, int, object, object> CompileGather()
        {
            var ps = new[] { typeof(object), typeof(int), typeof(int), typeof(int), typeof(int), typeof(object), typeof(object) }
                .Select((t, i) => System.Linq.Expressions.Expression.Parameter(t, "p" + i)).ToArray();
            var args = gatherEntries.GetParameters();
            var call = System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Convert(ps[0], gatherEntries.DeclaringType!), gatherEntries,
                ps[1], ps[2], ps[3], ps[4], System.Linq.Expressions.Expression.Convert(ps[5], args[4].ParameterType), System.Linq.Expressions.Expression.Convert(ps[6], args[5].ParameterType));
            return System.Linq.Expressions.Expression.Lambda<Action<object, int, int, int, int, object, object>>(call, ps).Compile();
        }

        [NameLookupReviewed("KCollider2D's fields, declared, and HandleVector's IsValid on the declared field's type")]
        static Func<object, (bool, int, int, int, int)> CompileRegistration()
        {
            var arg = System.Linq.Expressions.Expression.Parameter(typeof(object), "collider");
            var collider = System.Linq.Expressions.Expression.Convert(arg, colliderType);
            var entry = System.Linq.Expressions.Expression.Field(collider, AccessTools.Field(colliderType, "partitionerEntry"));
            var valid = System.Linq.Expressions.Expression.Call(entry, entry.Type.GetMethod("IsValid", Type.EmptyTypes)!);
            var extents = System.Linq.Expressions.Expression.Field(collider, AccessTools.Field(colliderType, "cachedExtents"));
            var tuple = typeof(ValueTuple<bool, int, int, int, int>).GetConstructor([typeof(bool), typeof(int), typeof(int), typeof(int), typeof(int)])!;
            var body = System.Linq.Expressions.Expression.New(tuple, valid,
                System.Linq.Expressions.Expression.Field(extents, "x"), System.Linq.Expressions.Expression.Field(extents, "y"),
                System.Linq.Expressions.Expression.Field(extents, "width"), System.Linq.Expressions.Expression.Field(extents, "height"));
            return System.Linq.Expressions.Expression.Lambda<Func<object, (bool, int, int, int, int)>>(body, arg).Compile();
        }

        /// GameObject.GetComponent&lt;HighEnergyParticle&gt;() for a collider's GameObject.
        static UnityEngine.Component? ParticleOf(UnityEngine.GameObject go)
        {
            var found = Lookup(go);
            if (verify) CheckLookup(go, (UnityEngine.Component?)getComponent.Invoke(go, null), found);
            return found;
        }

        /// The live radbolt on a GameObject, or null.
        static UnityEngine.Component? Lookup(UnityEngine.GameObject go)
        {
            UnityEngine.Component? found = null;
            lock (live)
            {
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    var c = live[i].Particle;
                    if (c == null) { live.RemoveAt(i); continue; }   // destroyed
                    if (found == null && ReferenceEquals(c.gameObject, go)) found = c;
                }
            }
            return found;
        }

        /// Verify mode: ParticleOf's answer against GetComponent's.
        static void CheckLookup(UnityEngine.GameObject go, UnityEngine.Component? expected) => CheckLookup(go, expected, Lookup(go));

        static void CheckLookup(UnityEngine.GameObject go, UnityEngine.Component? expected, UnityEngine.Component? found)
        {
            checks++;
            if (!ReferenceEquals(expected, found) && !(expected == null && found == null))
                EngineLog.Engine($"{Name}: mismatch {++mismatches} of {checks} on {go.name}");
            if (checks % 100000 == 0) EngineLog.Engine($"{Name}: {checks} checks, {mismatches} mismatches");
        }
    }
}
