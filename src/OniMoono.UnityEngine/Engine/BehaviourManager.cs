using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OniMoono.Engine
{
    /// Creates the record of an engine component type, the state of its subsystem. Engine
    /// components with their own state register a factory; transforms, scripts and plain
    /// components get none (null).
    internal static class ComponentNatives
    {
        static readonly Dictionary<Type, Func<ComponentNative>> factories = new()
        {
            // Engine components with their own native state. One list, so registration never
            // depends on type initialization order.
            [typeof(CanvasGroup)] = () => new CanvasGroupNative(),
            [typeof(Camera)] = () => new Rendering.CameraNative(),
            [typeof(Canvas)] = () => new UI.CanvasNative(),
            [typeof(CanvasRenderer)] = () => new UI.CanvasRendererNative(),
            [typeof(Renderer)] = () => new Rendering.RendererNative(),
            [typeof(MeshRenderer)] = () => new Rendering.MeshRendererNative(),
            [typeof(SpriteRenderer)] = () => new Rendering.SpriteRendererNative(),
            [typeof(MeshFilter)] = () => new Rendering.MeshFilterNative(),
            [typeof(Light)] = () => new Rendering.LightNative(),
            [typeof(UnityEngine.Rendering.SortingGroup)] = () => new Rendering.SortingGroupNative(),
            [typeof(SphereCollider)] = () => new SphereColliderNative(),
            [typeof(BoxCollider)] = () => new BoxColliderNative(),
            [typeof(CapsuleCollider)] = () => new CapsuleColliderNative(),
            [typeof(MeshCollider)] = () => new MeshColliderNative(),
            [typeof(Rigidbody)] = () => new RigidbodyNative(),
            [typeof(BoxCollider2D)] = () => new BoxCollider2DNative(),
            [typeof(PolygonCollider2D)] = () => new PolygonCollider2DNative(),
            [typeof(EdgeCollider2D)] = () => new EdgeCollider2DNative(),
            [typeof(CircleCollider2D)] = () => new CircleCollider2DNative(),
            [typeof(Rigidbody2D)] = () => new Rigidbody2DNative(),
            [typeof(Animator)] = () => new Mecanim.AnimatorNative(),
            [typeof(Animation)] = () => new Mecanim.AnimationNative(),
            [typeof(ParticleSystem)] = () => new Particles.ParticleSystemNative(),
            [typeof(ParticleSystemRenderer)] = () => new Particles.ParticleSystemRendererNative(),
            [typeof(LineRenderer)] = () => new Rendering.LineRendererNative(),
            [typeof(UnityEngine.Video.VideoPlayer)] = () => new Video.VideoPlayerNative(),
            [typeof(UnityEngine.Playables.PlayableDirector)] = () => new Playables.PlayableDirectorNative(),
            [typeof(TextMesh)] = () => new Text.TextMeshNative(),
        };

        /// The factory each component type resolved to (every Instantiate creates a record per
        /// component); cleared when a factory is registered.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<ComponentNative>> resolved = new();

        public static void Register(Type componentType, Func<ComponentNative> factory)
        {
            lock (factories) factories[componentType] = factory;
            resolved.Clear();
        }

        public static ComponentNative Create(Type type) => resolved.GetOrAdd(type, Resolve)();

        static Func<ComponentNative> Resolve(Type type)
        {
            lock (factories)
                for (var t = type; t != null && t != typeof(Component); t = t.BaseType)
                    if (factories.TryGetValue(t, out var f)) return f;
            // Transforms, scripts and plain components keep all their state on themselves: no
            // record.
            return static () => null;
        }
    }

    /// The per-phase lists of active-and-enabled MonoBehaviours (Unity's behaviour managers),
    /// ordered by script execution order and, within an order, by the order in which they were
    /// enabled. Changes made while a phase is running take effect from the next phase.
    internal static class BehaviourManager
    {
        enum Phase { Update, LateUpdate, FixedUpdate }

        sealed class PhaseList(string name, Phase phase)
        {
            // The phase's message, read from the script directly (a delegate call per behaviour
            // per phase before).
            Action<MonoBehaviour> MessageOf(ScriptInfo s) => phase switch
            {
                Phase.Update => s.Update,
                Phase.LateUpdate => s.LateUpdate,
                _ => s.FixedUpdate,
            };

            public readonly string Name = name;
            /// With frame stats on: each script's wall time in this phase since the last report.
            public readonly Dictionary<ScriptInfo, long> Times = new();
            readonly SortedDictionary<int, List<Behaviour>> byOrder = new();
            /// byOrder's lists in key order, remade when an order is added (iterating the
            /// sorted dictionary allocated its enumerator's stack every phase).
            List<Behaviour>[] ordered = [];
            Predicate<Behaviour> removing;
            /// The behaviours added since the last Integrate, in order; a null is one removed
            /// again before it (a table screen's rows enable and then disable thousands of
            /// widgets' parts in the frame that builds them, and a search of this list for each
            /// took 113 ms of that frame on a colony of 200 Duplicants).
            readonly List<Behaviour> pendingAdd = new();
            /// Each pending behaviour's place in pendingAdd.
            readonly Dictionary<Behaviour, int> pendingAddAt = new(ReferenceEqualityComparer.Instance);
            readonly HashSet<Behaviour> members = new(ReferenceEqualityComparer.Instance);
            readonly HashSet<Behaviour> pendingRemove = new(ReferenceEqualityComparer.Instance);
            readonly List<List<Behaviour>> touched = new();

            public void Add(Behaviour b)
            {
                if (MessageOf(b.EngineScript) == null) return;
                pendingRemove.Remove(b);
                if (members.Contains(b) || !pendingAddAt.TryAdd(b, pendingAdd.Count)) return;
                pendingAdd.Add(b);
            }

            public void Remove(Behaviour b)
            {
                if (pendingAddAt.Remove(b, out int at))
                {
                    pendingAdd[at] = null;
                    return;
                }
                if (members.Contains(b)) pendingRemove.Add(b);
            }

            void Integrate()
            {
                if (pendingRemove.Count > 0)
                {
                    // A behaviour's execution order is set when it is created, before it is
                    // added: only the lists of the removed behaviours' orders hold them.
                    foreach (var b in pendingRemove)
                        if (byOrder.TryGetValue(b.EngineExecutionOrder, out var list) && !touched.Contains(list))
                        {
                            touched.Add(list);
                            list.RemoveAll(removing ??= pendingRemove.Contains);
                        }
                    touched.Clear();
                    foreach (var b in pendingRemove) members.Remove(b);
                    pendingRemove.Clear();
                }
                foreach (var b in pendingAdd)
                {
                    if (b is null) continue;
                    if (!byOrder.TryGetValue(b.EngineExecutionOrder, out var list))
                    {
                        byOrder[b.EngineExecutionOrder] = list = new();
                        ordered = [.. byOrder.Values];
                    }
                    list.Add(b);
                    members.Add(b);
                }
                pendingAdd.Clear();
                pendingAddAt.Clear();
            }

            int depth;

            /// The lists change only in Integrate, before the phase's callbacks run, so they are
            /// iterated in place; a run started from inside a callback (the player loop never does
            /// this) leaves the integration to the outer one.
            public void Run()
            {
                if (depth == 0) Integrate();
                depth++;
                try
                {
                    foreach (var list in ordered)
                        for (int i = 0; i < list.Count; i++)
                        {
                            var b = list[i];
                            // Disabled or destroyed earlier in this phase: skip.
                            if (!b.EngineActiveAndEnabled || (pendingRemove.Count != 0 && pendingRemove.Contains(b))) continue;
                            if (!b.EngineStartCalled)
                            {
                                // A member whose Start is still pending starts here, just before
                                // its first callback of this phase, ahead of the pending Starts of
                                // behaviours without this callback (measured in the original,
                                // UnityProbe scenarios 13, 17, 18).
                                if (!b.EngineStartPending) continue;
                                RunStart(b);
                                if (!b.EngineActiveAndEnabled || b.EngineDestroyed) continue;
                            }
                            if (!PlayerLoop.FrameStats)
                            {
                                Lifecycle.Invoke(MessageOf(b.EngineScript), (MonoBehaviour)b);
                                continue;
                            }
                            long start = System.Diagnostics.Stopwatch.GetTimestamp();
                            Lifecycle.Invoke(MessageOf(b.EngineScript), (MonoBehaviour)b);
                            Times[b.EngineScript] = Times.GetValueOrDefault(b.EngineScript) + System.Diagnostics.Stopwatch.GetTimestamp() - start;
                        }
                }
                finally
                {
                    depth--;
                }
            }
        }

        static readonly PhaseList update = new("Update", Phase.Update);
        static readonly PhaseList lateUpdate = new("LateUpdate", Phase.LateUpdate);
        static readonly PhaseList fixedUpdate = new("FixedUpdate", Phase.FixedUpdate);
        static readonly List<Behaviour> startQueue = new();
        static readonly ListSnapshot<Behaviour> startSnapshot = new();

        public static void Register(Behaviour b)
        {
            update.Add(b);
            lateUpdate.Add(b);
            fixedUpdate.Add(b);
            IMGUI.GUIManager.Register(b); // OnGUI behaviours
        }

        public static void Unregister(Behaviour b)
        {
            update.Remove(b);
            lateUpdate.Remove(b);
            fixedUpdate.Remove(b);
            IMGUI.GUIManager.Unregister(b);
        }

        public static void ScheduleStart(Behaviour b)
        {
            b.EngineStartPending = true;
            startQueue.Add(b);
        }

        public static void CancelStart(Behaviour b)
        {
            if (!b.EngineStartPending) return;
            b.EngineStartPending = false;
            ByReference.Remove(startQueue, b);
        }

        /// Runs Start for behaviours scheduled before this call, in the order they were scheduled
        /// (when their activation began, before Awake; or when enabled later), as Unity's
        /// delayed-call queue does: script
        /// execution order does not reorder pending Starts (measured in the original player while
        /// a large save loads: World, order 6, starts before Game, order -10, because it was
        /// enabled first). Execution order still shapes Start order indirectly, through the Awake/OnEnable
        /// order of a batch activated together. A behaviour that is not active-and-enabled at
        /// that point keeps its Start for when it is enabled.
        ///
        /// Starts scheduled while the pass runs (objects a Start creates) wait for a later pass,
        /// or for the first update callback of their own (PhaseList.Run), as measured in the
        /// original (UnityProbe scenarios 14 and 17): the game's SaveLoader loads a save from a
        /// Start, and the Duplicants' behaviours with Update start before the others, all
        /// Duplicants' together; their MinionIdentity spawns before their KPrefabID, so a
        /// dead Duplicant enters Components.LiveMinionIdentities and leaves it again, which
        /// reorders the list.
        public static void RunStarts()
        {
            if (startQueue.Count == 0) return;
            using var snapshot = startSnapshot.Take(startQueue);
            startQueue.Clear();
            var items = snapshot.Items;
            // A save load's bar (LoadProgress) counts the pass that starts its objects.
            bool counted = LoadProgress.Phase >= LoadProgress.AfterObjects;
            if (counted) LoadProgress.StartPass(items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                var b = items[i];
                if (counted) LoadProgress.Started(i);
                // Started since it was queued (by its first update callback): nothing to do.
                if (b.EngineStartCalled || b.EngineDestroyed) continue;
                b.EngineStartPending = false;
                if (!b.EngineActiveAndEnabled) continue; // rescheduled on the next OnEnable
                RunStart(b);
            }
        }

        /// Start for one behaviour, active and enabled, whose Start has not run. A queue entry it
        /// leaves behind is skipped by the pass that reaches it.
        static void RunStart(Behaviour b)
        {
            b.EngineStartPending = false;
            b.EngineStartCalled = true;
            var mb = (MonoBehaviour)b;
            if (b.EngineScript.Start != null) Lifecycle.Invoke(b.EngineScript.Start, mb);
            else if (b.EngineScript.StartCoroutine != null)
            {
                try
                {
                    var routine = b.EngineScript.StartCoroutine(mb);
                    if (routine != null) Coroutines.Start(mb, routine);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, mb);
                }
            }
        }

        /// Frame stats: the scripts' update callbacks that took the most wall time since the last
        /// call, as "Type.Phase ms" per frame over the given frames; the totals restart.
        public static string TakeScriptTimes(int frames, int count)
        {
            var all = new List<(string Name, double Ms)>();
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames;
            foreach (var phase in new[] { update, lateUpdate, fixedUpdate })
            {
                foreach (var (script, ticks) in phase.Times) all.Add(($"{script.Type.Name}.{phase.Name}", ticks * toMs));
                phase.Times.Clear();
            }
            all.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            return string.Join(", ", all.Take(count).Select(t => $"{t.Name} {t.Ms:F2}"));
        }

        public static void RunUpdate() => update.Run();
        public static void RunLateUpdate() => lateUpdate.Run();
        public static void RunFixedUpdate() => fixedUpdate.Run();
    }
}
