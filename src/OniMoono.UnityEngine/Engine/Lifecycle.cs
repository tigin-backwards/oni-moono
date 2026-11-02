using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Engine
{
    /// GameObject/component creation, activation and destruction with Unity's player semantics.
    ///
    /// - Awake runs once per behaviour, when its GameObject first becomes active in the
    ///   hierarchy (also for disabled behaviours). Objects outside scenes (prefab assets) never
    ///   become active in the hierarchy and are never Awakened.
    /// - OnEnable/OnDisable bracket the "active and enabled" state: GameObject active in the
    ///   hierarchy and Behaviour.enabled.
    /// - Start runs once, before the behaviour's first Update, in the next Start phase.
    /// - Activation sets the active flags of the whole subtree first, then runs Awake/OnEnable
    ///   per component in hierarchy pre-order; deactivation clears the flags, then runs OnDisable
    ///   in the same order.
    /// - Destroy is deferred to the player loop's next destruction point (after the fixed steps,
    ///   after the Update pass, after LateUpdate); DestroyImmediate is synchronous.
    ///   OnDestroy runs only for behaviours whose Awake ran.
    internal static class Lifecycle
    {
        // ---- Creation ----

        public static void CreateGameObject(GameObject managed, string name, Type[] components)
        {
            var native = new GameObjectNative { Name = name ?? "" };
            managed.Bind(native);
            AttachTransform(native, typeof(Transform));
            var scene = Scenes.Active;
            if (scene != null)
            {
                native.Scene = scene;
                Scenes.AddRoot(scene, native);
                native.ActiveInHierarchy = true;
            }
            if (components != null)
                foreach (var t in components) AddComponent(native, t);
        }

        /// Creates the GameObject's Transform (or RectTransform) as component 0.
        static Transform AttachTransform(GameObjectNative go, Type transformType)
        {
            var native = (TransformNative)ComponentNatives.Create(transformType);
            native.GameObject = go;
            var t = Object.ConstructBound<Transform>(transformType, native);
            go.Transform = native;
            go.Components = [t];
            return t;
        }

        public static Component AddComponent(GameObjectNative go, Type type)
        {
            Platform.MainThreadPump.Maybe();
            if (type == null)
            {
                Debug.LogError("AddComponent with a null type");
                return null;
            }
            if (!typeof(Component).IsAssignableFrom(type))
            {
                Debug.LogError($"AddComponent asking for invalid type \"{type.FullName}\"");
                return null;
            }
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            {
                Debug.LogError($"Cannot add component of type '{type.Name}' because it is abstract.");
                return null;
            }
            // The refusals below are the player's own messages: LogType.Log, no stack trace
            // (measured: UnityProbe's "add component refusals").
            if (typeof(Transform).IsAssignableFrom(type))
            {
                // A RectTransform replaces a plain Transform in place and a Transform replaces a
                // RectTransform, even under components that require one; the same type again fails.
                if (go.Components[0].GetType() != type) return ConvertTransform(go, type);
                RefuseSameType(go, type);
                return null;
            }
            if (SingleInstance.Contains(type) && Components.First(go, type) != null)
            {
                RefuseSameType(go, type);
                return null;
            }
            if (DisallowedBy(go, type) is var (declaring, existing))
            {
                var message = $"Can't add '{type.Name}' to {go.Name} because a '{existing.GetType().Name}' is already added to the game object!";
                if (existing.GetType() != declaring) message += $"\nA GameObject can only contain one '{declaring.Name}' component.";
                EngineLog.Message(LogType.Log, message, "");
                return null;
            }

            // Required components first, recursively.
            foreach (RequireComponent rc in type.GetCustomAttributes(typeof(RequireComponent), true))
                foreach (var req in new[] { rc.m_Type0, rc.m_Type1, rc.m_Type2 })
                    if (req != null && Components.First(go, req) == null)
                    {
                        if (typeof(Transform).IsAssignableFrom(req)) AddComponent(go, req);
                        else if (AddComponent(go, req) == null) return null;
                    }

            var component = CreateComponent(go, type);
            component.EngineReset();
            if (go.ActiveInHierarchy) AwakeAndEnable(component);
            return component;
        }

        /// Engine components a GameObject holds at most one of, measured one by one (an
        /// AudioSource or a collider may repeat). The player also refuses components that conflict
        /// with others (a SpriteRenderer beside a MeshRenderer, 2D physics beside 3D); not here.
        static readonly HashSet<Type> SingleInstance =
        [
            typeof(Camera), typeof(Canvas), typeof(CanvasGroup), typeof(CanvasRenderer), typeof(MeshFilter),
            typeof(MeshRenderer), typeof(SpriteRenderer), typeof(LineRenderer), typeof(Light), typeof(Rigidbody),
            typeof(Rigidbody2D), typeof(Animator), typeof(Animation), typeof(ParticleSystem),
            typeof(ParticleSystemRenderer),
        ];

        static void RefuseSameType(GameObjectNative go, Type type) =>
            EngineLog.Message(LogType.Log, $"Can't add component '{type.Name}' to {go.Name} because such a component is already added to the game object!", "");

        /// The class whose [DisallowMultipleComponent] refuses TYPE here, and the component in the
        /// way. The attribute covers the class declaring it and its subclasses, so the most basic
        /// declaring class decides; the second line of the message names it.
        static (Type declaring, Component existing)? DisallowedBy(GameObjectNative go, Type type)
        {
            Type declaring = null;
            for (var t = type; t != null && t != typeof(Component); t = t.BaseType)
                if (t.IsDefined(typeof(DisallowMultipleComponent), false)) declaring = t;
            if (declaring == null) return null;
            foreach (var c in go.Components)
                if (declaring.IsInstanceOfType(c)) return (declaring, c);
            return null;
        }

        /// Constructs a component and appends it to the GameObject (no lifecycle calls).
        public static Component CreateComponent(GameObjectNative go, Type type)
        {
            var native = ComponentNatives.Create(type);
            native.GameObject = go;
            if (native is BehaviourNative bn && typeof(MonoBehaviour).IsAssignableFrom(type))
            {
                bn.Script = ScriptInfo.Get(type);
                bn.ExecutionOrder = bn.Script.ExecutionOrder;
            }
            var component = Object.ConstructBound<Component>(type, native);
            var comps = go.Components;
            var grown = new Component[comps.Length + 1];
            Array.Copy(comps, grown, comps.Length);
            grown[^1] = component;
            go.Components = grown;
            if (go.Transform != null) Hierarchy.Touch(go.Transform);
            return component;
        }

        /// Replaces the GameObject's Transform with one of TYPE (Transform or RectTransform), keeping
        /// its local placement and its place in the hierarchy.
        static Component ConvertTransform(GameObjectNative go, Type type)
        {
            var old = (Transform)go.Components[0];
            var oldNative = (TransformNative)old.NativeOrThrow;
            var native = (TransformNative)ComponentNatives.Create(type);
            native.GameObject = go;
            native.LocalPosition = oldNative.LocalPosition;
            native.LocalRotation = oldNative.LocalRotation;
            native.LocalScale = oldNative.LocalScale;
            native.Parent = oldNative.Parent;
            native.Children = oldNative.Children;
            foreach (var child in native.Children) child.Parent = native;
            if (native.Parent != null)
            {
                var siblings = (TransformNative[])native.Parent.Children.Clone();
                siblings[Array.IndexOf(siblings, oldNative)] = native;
                native.Parent.Children = siblings;
            }
            var transform = Object.ConstructBound<Transform>(type, native);
            var comps = (Component[])go.Components.Clone();
            comps[0] = transform;
            go.Transform = native;
            go.Components = comps;
            Hierarchy.Touch(native);
            foreach (var c in comps) c.ForgetTransform();
            old.Unbind();
            native.MarkDirty(false);
            return transform;
        }

        // ---- Activation ----

        public static bool ParentActive(GameObjectNative go)
        {
            var parent = go.Transform.Parent;
            return parent != null ? parent.GameObject.ActiveInHierarchy : go.Scene != null;
        }

        public static void SetActive(GameObjectNative go, bool value)
        {
            if (go.ActiveSelf == value) return;
            go.ActiveSelf = value;
            UpdateActiveInHierarchy(go);
        }

        /// Recomputes activeInHierarchy for a subtree after its activeSelf, parent or scene
        /// changed, and runs the resulting lifecycle callbacks.
        public static void UpdateActiveInHierarchy(GameObjectNative go)
        {
            Hierarchy.Touch(go.Transform);
            ApplyActive(go, go.ActiveSelf && ParentActive(go));
        }

        static void ApplyActive(GameObjectNative go, bool now)
        {
            if (now == go.ActiveInHierarchy) return;
            var changed = new List<GameObjectNative>();
            if (now)
            {
                SetFlags(go, true, changed);
                ReapplyDrivenOnActivation(go.Transform, changed);
                AwakeAndEnableAll(changed);
            }
            else
            {
                CollectActive(go, changed);
                ReapplyDrivenOnActivation(go.Transform, changed);
                DeactivateTree(go);
            }
        }

        static void CollectActive(GameObjectNative go, List<GameObjectNative> active)
        {
            if (!go.ActiveInHierarchy) return;
            active.Add(go);
            foreach (var child in go.Transform.Children) CollectActive(child.GameObject, active);
        }

        /// Leaves the active state, as Unity's player does on SetActive(false) and on destruction
        /// (measured, UnityProbe PROBE_SCENARIO=deactorder): an object's activeInHierarchy turns
        /// false when the walk reaches it, its children are walked, and only then do its own
        /// OnDisables run (children first). So while one object's OnDisable runs, its later
        /// siblings and their subtrees still read activeInHierarchy true, and an OnDisable that
        /// deactivates one of them disables it there and then (the walk then skips it).
        /// DetailCollapsableLabel.OnDisable hides its content rows, later siblings, only while
        /// they still read active (issue #23).
        static void DeactivateTree(GameObjectNative go)
        {
            if (!go.ActiveInHierarchy) return;
            go.ActiveInHierarchy = false;
            foreach (var child in go.Transform.Children)
                if (child.GameObject.ActiveSelf && child.Parent == go.Transform) DeactivateTree(child.GameObject);
            foreach (var c in go.Components)
                if (c.Native != null) Disable(c);
            StopCoroutinesOn(go);
        }

        /// RectTransform.reapplyDrivenProperties for every RectTransform whose activeInHierarchy
        /// just changed, children before their parent, before any OnEnable or OnDisable (measured
        /// in the original, UnityProbe PROBE_SCENARIO=childactive). No dimension message is sent.
        /// uGUI's LayoutRebuilder listens and marks the layout around each one, which is how a
        /// layout group re-lays out when a child without a Graphic or layout element appears or
        /// disappears (the build menu's search results).
        static void ReapplyDrivenOnActivation(TransformNative t, List<GameObjectNative> changed)
        {
            if (changed.Count == 0) return;
            var set = changed.Count == 1 ? null : new HashSet<GameObjectNative>(changed);
            void PostOrder(TransformNative n)
            {
                foreach (var child in n.Children)
                    if (set != null && set.Contains(child.GameObject)) PostOrder(child);
                if (n is RectTransformNative && n.Managed is UnityEngine.RectTransform rt && !n.GameObject.Destroyed)
                {
                    try { UnityEngine.RectTransform.SendReapplyDrivenProperties(rt); }
                    catch (Exception e) { Debug.LogException(e); }
                }
            }
            PostOrder(t);
        }

        /// Awake/OnEnable for objects that just became active (scene load, Instantiate,
        /// SetActive): engine components first, then scripts in script execution order, hierarchy
        /// order within an order. A component whose GameObject an earlier callback deactivated
        /// is skipped.
        public static void AwakeAndEnableAll(List<GameObjectNative> activated)
        {
            var scripts = new List<(Component c, int order, int seq)>();
            int seq = 0;
            foreach (var g in activated)
                foreach (var c in g.Components)
                {
                    if (c is MonoBehaviour mb) scripts.Add((mb, (mb.Native as BehaviourNative)?.ExecutionOrder ?? 0, seq++));
                    else if (g.ActiveInHierarchy && c.Native != null) AwakeAndEnable(c);
                }
            scripts.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : a.seq.CompareTo(b.seq));
            foreach (var (c, _, _) in scripts)
                if (c.Native is ComponentNative cn && cn.GameObject.ActiveInHierarchy) AwakeAndEnable(c);
        }

        static void SetFlags(GameObjectNative go, bool active, List<GameObjectNative> changed)
        {
            bool now = active && go.ActiveSelf;
            if (now == go.ActiveInHierarchy) return;
            go.ActiveInHierarchy = now;
            changed.Add(go);
            foreach (var child in go.Transform.Children) SetFlags(child.GameObject, now, changed);
        }

        /// Awake (once) and OnEnable (if enabled) for a component whose GameObject is active.
        ///
        /// When an Awake destroys its GameObject while the object activates (the game's
        /// PlantBranch does, from KPrefabID's Awake, for a branch whose cell is taken: issue #45),
        /// that behaviour is not enabled after its Awake, and the behaviours not awake yet, the
        /// object's and its direct children's, still get Awake, reporting enabled = false, and
        /// nothing else: none of them gets OnEnable or Start, and each gets OnDestroy at the
        /// destruction (measured in Unity's player, UnityProbe's PROBE_SCENARIO=awakedestroy). So
        /// do the behaviours of an object destroyed while inactive and activated before its
        /// destruction (the same probe).
        public static void AwakeAndEnable(Component c)
        {
            var native = c.Native as ComponentNative;
            if (native == null) return;
            if (native is BehaviourNative bn)
            {
                if (!bn.AwakeCalled)
                {
                    bn.AwakeCalled = true;
                    // Start is queued when activation begins, before Awake (measured in Unity's
                    // player): objects an Awake creates start after their creator, although they
                    // are enabled first. A behaviour that Awake leaves disabled is skipped when the
                    // queue runs and queued again when enabled.
                    // Its place among the update callbacks is taken then too: a component that an
                    // Awake adds (the game's MyCmpAdd) is enabled before the component adding it,
                    // and updates after it (UnityProbe scenario 19).
                    bool reserved = bn.Script != null && bn.Enabled && !bn.DestroyPending;
                    if (reserved)
                    {
                        if (!bn.StartCalled && !bn.StartPending) BehaviourManager.ScheduleStart(bn);
                        BehaviourManager.Register(bn);
                    }
                    c.EngineAwake();
                    bn.InAwake = true;
                    bool threw;
                    try
                    {
                        threw = bn.Script?.Awake != null && !TryInvoke(bn.Script.Awake, (MonoBehaviour)c);
                    }
                    finally
                    {
                        bn.InAwake = false;
                    }
                    // An Awake that throws leaves its behaviour disabled, with an OnDisable although
                    // it had no OnEnable; enabling it later runs OnEnable and Start as for any
                    // (measured in Unity's player, UnityProbe's PROBE_SCENARIO=u60, AW lines).
                    if (threw && bn.Enabled && !bn.DestroyPending && c.Native != null)
                    {
                        bn.Enabled = false;
                        if (bn.Script?.OnDisable != null) Invoke(bn.Script.OnDisable, (MonoBehaviour)c);
                    }
                    if (c.Native == null || !native.GameObject.ActiveInHierarchy)
                    {
                        if (reserved && !bn.IsActiveAndEnabledState) BehaviourManager.Unregister(bn);
                        return;
                    }
                    if (bn.Enabled && !bn.DestroyPending) Enable(c, bn);
                    if (reserved && !bn.IsActiveAndEnabledState) BehaviourManager.Unregister(bn); // disabled or destroyed by Awake
                    return;
                }
                if (bn.Enabled && !bn.DestroyPending) Enable(c, bn);
            }
            else
            {
                if (native.DestroyPending) return;
                if (!native.AwakeCalledForComponent)
                {
                    native.AwakeCalledForComponent = true;
                    c.EngineAwake();
                }
                native.EngineEnabled = true;
                c.EngineOnEnable(); // components that are not Behaviours: each activation
            }
        }

        public static void Enable(Component c, BehaviourNative bn)
        {
            if (bn.IsActiveAndEnabledState) return;
            bn.IsActiveAndEnabledState = true;
            Hierarchy.Touch(bn.GameObject.Transform);
            c.EngineOnEnable();
            var script = bn.Script;
            if (script != null)
            {
                if (script.OnEnable != null) Invoke(script.OnEnable, (MonoBehaviour)c);
                if (!bn.IsActiveAndEnabledState) return; // disabled again by OnEnable
                BehaviourManager.Register(bn);
                if (!bn.StartCalled && !bn.StartPending) BehaviourManager.ScheduleStart(bn);
            }
        }

        public static void Disable(Component c)
        {
            if (c.Native is ComponentNative { EngineEnabled: true } cn and not BehaviourNative)
            {
                cn.EngineEnabled = false;
                c.EngineOnDisable(); // non-Behaviours: deactivation
            }
            if (c.Native is not BehaviourNative bn || !bn.IsActiveAndEnabledState) return;
            bn.IsActiveAndEnabledState = false;
            Hierarchy.Touch(bn.GameObject.Transform);
            if (bn.Script != null) BehaviourManager.Unregister(bn);
            c.EngineOnDisable();
            if (bn.Script?.OnDisable != null) Invoke(bn.Script.OnDisable, (MonoBehaviour)c);
        }

        /// Coroutines stop when their GameObject is deactivated (not when the behaviour is disabled).
        static void StopCoroutinesOn(GameObjectNative go)
        {
            foreach (var c in go.Components)
                if (c is MonoBehaviour mb && c.Native is BehaviourNative bn && bn.Coroutines is { Count: > 0 })
                    Coroutines.StopAll(mb, bn);
        }

        public static void Invoke(Action<MonoBehaviour> message, MonoBehaviour target)
        {
            try
            {
                message(target);
            }
            catch (Exception e)
            {
                Debug.LogException(e, target);
            }
        }

        /// Invoke for a message with two arguments (OnRenderImage), without a closure around them.
        public static void Invoke<TA, TB>(Action<MonoBehaviour, TA, TB> message, MonoBehaviour target, TA a, TB b)
        {
            try
            {
                message(target, a, b);
            }
            catch (Exception e)
            {
                Debug.LogException(e, target);
            }
        }

        /// Invoke, telling whether the message returned without an exception.
        static bool TryInvoke(Action<MonoBehaviour> message, MonoBehaviour target)
        {
            try
            {
                message(target);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e, target);
                return false;
            }
        }

        // ---- Destruction ----

        static readonly List<Object> pendingDestroy = new();
        static readonly List<(Object obj, float time)> timedDestroy = new();

        public static void ScheduleDestroy(Object obj, float delay)
        {
            var native = obj.Native;
            if (native == null) return;
            if (delay > 0f)
            {
                timedDestroy.Add((obj, Time.time + delay));
                return;
            }
            if (native.DestroyRequested) return;
            native.DestroyRequested = true;
            pendingDestroy.Add(obj);
            DisableForDestroy(obj);
        }

        /// Destroy disables part of what it will destroy at once (OnDisable now, OnDestroy at the
        /// next destruction point), as observed in Unity's player: the behaviours of the GameObject and
        /// of its direct children, or the one component. Until destruction they report
        /// enabled = false and activation does not enable them again, while the GameObjects keep
        /// their active flags. Deeper descendants stay enabled until the destruction itself.
        static void DisableForDestroy(Object obj)
        {
            switch (obj)
            {
                case GameObject go:
                    var native = (GameObjectNative)go.Native;
                    DisableComponentsForDestroy(native);
                    foreach (var child in native.Transform.Children) DisableComponentsForDestroy(child.GameObject);
                    break;
                case Transform:
                    break; // refused at destruction
                case Component c when c.Native is ComponentNative cn:
                    cn.DestroyPending = true;
                    if (cn is BehaviourNative) Disable(c);
                    break;
            }
        }

        static void DisableComponentsForDestroy(GameObjectNative g)
        {
            foreach (var c in g.Components)
                if (c.Native is ComponentNative cn)
                {
                    cn.DestroyPending = true;
                    if (cn is BehaviourNative bn)
                    {
                        if (bn.InAwake) DisableInAwake(c, bn);
                        else Disable(c);
                    }
                }
        }

        /// A behaviour whose Awake destroys its GameObject gets OnDisable then, although it had
        /// no OnEnable (measured in Unity's player, UnityProbe's PROBE_SCENARIO=awakedestroy). That
        /// a disabled one gets none is only a guard, not measured.
        static void DisableInAwake(Component c, BehaviourNative bn)
        {
            if (!bn.Enabled || bn.IsActiveAndEnabledState) { Disable(c); return; }
            if (bn.Script?.OnDisable != null) Invoke(bn.Script.OnDisable, (MonoBehaviour)c);
        }

        /// Carries out the Destroy requests made so far: the player loop calls it after the fixed
        /// steps, after the Update pass and after LateUpdate (PlayerLoop.RunFrame).
        public static void ProcessDestroyQueue()
        {
            float now = Time.time;
            for (int i = timedDestroy.Count - 1; i >= 0; i--)
            {
                if (timedDestroy[i].time > now) continue;
                var obj = timedDestroy[i].obj;
                timedDestroy.RemoveAt(i);
                ScheduleDestroy(obj, 0f);
            }
            // Destroy callbacks may request more destruction; drain until empty.
            while (pendingDestroy.Count > 0)
            {
                var batch = pendingDestroy.ToArray();
                pendingDestroy.Clear();
                foreach (var obj in batch) DestroyNow(obj);
            }
        }

        /// Destroys OBJ now. A scene's unload passes `unloading`: its roots keep their active flag
        /// (not measured there), where Destroy and DestroyImmediate clear it.
        public static void DestroyNow(Object obj, bool unloading = false)
        {
            var native = obj.Native;
            if (native == null || native.Destroyed) return;
            switch (obj)
            {
                case GameObject go:
                    DestroyGameObject((GameObjectNative)native, unloading);
                    break;
                case Transform:
                    Debug.LogError("Destroying the transform component is not permitted. Destroy the GameObject instead.");
                    native.DestroyRequested = false;
                    break;
                case Component c:
                    DestroyComponent(c, (ComponentNative)native, detach: true);
                    break;
                case ScriptableObject so:
                    native.Destroyed = true;
                    ScriptableObjectLifecycle.Destroy(so);
                    obj.EngineOnDestroy();
                    obj.Unbind();
                    break;
                default:
                    native.Destroyed = true;
                    obj.EngineOnDestroy();
                    obj.Unbind();
                    break;
            }
        }

        static void DestroyGameObject(GameObjectNative go, bool unloading)
        {
            var t = go.Transform;
            // 0. The destroyed object itself turns inactive (activeSelf false) before its
            //    subtree's OnDisable and OnDestroy; its descendants keep their own flags (measured
            //    in the original player, UnityProbe's destroyflags: Destroy and DestroyImmediate,
            //    of an active object, an inactive one and one under an inactive parent).
            //    KMonoBehaviour.OnDestroy releases its KObject's event system only when it sees
            //    activeSelf false.
            if (!unloading) go.ActiveSelf = false;
            // 1. Leave the active state: OnDisable for what is still enabled, deepest objects
            //    first (as Unity's player orders it when destroying).
            if (go.ActiveInHierarchy) DeactivateForDestroy(go);
            // 2. Detach from parent or scene; the parent's child list changed (measured in the
            //    original player, for Destroy and DestroyImmediate).
            var parent = t.Parent;
            if (parent != null)
            {
                Hierarchy.RemoveChild(parent, t);
                Messages.SendToComponents(parent.GameObject, s => s.OnTransformChildrenChanged);
            }
            else if (go.Scene != null) Scenes.RemoveRoot(go.Scene, go);
            // 3. Destroy the subtree.
            DestroySubtree(go);
        }

        static void DeactivateForDestroy(GameObjectNative go) => DeactivateTree(go);

        /// OnDestroy for every component of the subtree first, then the objects are released:
        /// while the callbacks run, every object of the subtree is still alive (OnDestroy code
        /// looks up sibling components, as in Unity).
        static void DestroySubtree(GameObjectNative root)
        {
            var released = new List<Object>();
            void Callbacks(GameObjectNative go)
            {
                if (go.Destroyed) return;
                go.Destroyed = true;
                foreach (var c in go.Components)
                    if (c.Native is ComponentNative cn && !cn.Destroyed)
                    {
                        DestroyComponent(c, cn, detach: false, unbind: false);
                        released.Add(c);
                    }
                foreach (var child in go.Transform.Children) Callbacks(child.GameObject);
                released.Add(go.Managed);
            }
            Callbacks(root);
            foreach (var o in released) o.Unbind();
        }

        static void DestroyComponent(Component c, ComponentNative native, bool detach, bool unbind = true)
        {
            native.Destroyed = true;
            if (native is BehaviourNative bn)
            {
                Disable(c);
                if (c is MonoBehaviour mb)
                {
                    if (bn.Coroutines is { Count: > 0 }) Coroutines.StopAll(mb, bn);
                    BehaviourManager.CancelStart(bn);
                    if (bn.AwakeCalled && bn.Script?.OnDestroy != null) Invoke(bn.Script.OnDestroy, mb);
                }
            }
            c.EngineOnDestroy();
            if (detach)
            {
                var go = native.GameObject;
                var comps = go.Components;
                int index = Array.IndexOf(comps, c);
                if (index >= 0)
                {
                    var shrunk = new Component[comps.Length - 1];
                    Array.Copy(comps, 0, shrunk, 0, index);
                    Array.Copy(comps, index + 1, shrunk, index, comps.Length - index - 1);
                    go.Components = shrunk;
                    Hierarchy.Touch(go.Transform);
                }
            }
            if (unbind) c.Unbind();
        }
    }
}
