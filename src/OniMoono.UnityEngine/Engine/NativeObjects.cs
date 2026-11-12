using System.Collections.Generic;
using System.Threading;
using OniMoono.Assets.TypeTrees;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Engine
{
    /// Engine-side record of a UnityEngine.Object (what Unity keeps in native memory).
    internal class NativeObject
    {
        public int Slot;
        public int Generation;
        public int InstanceId;
        public Object Managed;
        public string Name = "";
        public HideFlags HideFlags;
        /// Loaded from an asset file (scene objects excluded). Persistent objects are never
        /// Awakened and are released only by Resources.UnloadUnusedAssets / bundle unload.
        public bool IsPersistent;
        /// Where the object was loaded from, when it was.
        public AssetSource Source;
        /// Destroy has been requested (deferred destruction pending) or performed.
        public bool DestroyRequested;
        public bool Destroyed;

        /// Copies engine-side state for Instantiate (each record type adds its own state).
        public virtual void CopyStateFrom(NativeObject source) { }

        /// After Instantiate copied the state: references to objects of the cloned hierarchy
        /// are redirected to their clones (`map` returns its argument for anything else), as
        /// Unity does for every reference a component holds.
        public virtual void RemapReferences(System.Func<UnityEngine.Object, UnityEngine.Object> map) { }

        /// Serialized data of an engine object loaded from an asset file, kept until the owning
        /// subsystem's Bind consumes it.
        public OniMoono.Assets.TypeTrees.Fields Serialized;

        /// Builds engine state from serialized data once references can be resolved. Record types
        /// of subsystems that bind their data override it and clear Serialized.
        public virtual void Bind(OniMoono.Assets.TypeTrees.Fields data, OniMoono.Engine.Serialization.IReferenceResolver resolver) { }
    }

    /// Identity of an object loaded from a file: the file and the object's pathID in it.
    internal readonly record struct AssetSource(string File, long PathId);

    internal sealed class GameObjectNative : NativeObject
    {
        /// Component order as in Unity; [0] is always the Transform. Copy-on-write so that
        /// worker threads can read it while the main thread mutates.
        public Component[] Components
        {
            get => components;
            set
            {
                components = value;
                if (CullBoundsWatched) Rendering.FrameRenderer.BoundsInputsChanged(this);
            }
        }
        Component[] components = System.Array.Empty<Component>();
        /// A renderer of it keeps culling bounds that depend on its components (its mesh
        /// filter's mesh): a change of them recomputes those bounds (FrameRenderer). Never
        /// cleared.
        public bool CullBoundsWatched;
        public int Layer;
        public string Tag = "Untagged";
        public bool ActiveSelf = true;
        /// Cached activeInHierarchy, maintained by activation changes.
        public bool ActiveInHierarchy
        {
            get => activeInHierarchy;
            set
            {
                if (value && !activeInHierarchy) ActivatedAt = ++activations;
                activeInHierarchy = value;
            }
        }
        bool activeInHierarchy;
        /// When it last became active in its hierarchy, counting activations (main thread):
        /// GameObject.Find returns the match activated first.
        public long ActivatedAt;
        static long activations;
        /// Owning scene; null for prefab assets and other objects that are not in a scene.
        public SceneRecord Scene;
        public bool IsStatic;

        /// The native side of Components[0].
        public TransformNative Transform;
        /// Slot in the owning scene's root list while a root, else -1 (SceneRoots).
        public int RootSlot = -1;
        /// Its canvas components, cached by CanvasSystem for the current Components array.
        public UI.UiParts UiParts;
        /// Its component types' shared layout, for the component list it was made for (Components).
        public ComponentLayout.Cached Layout;
        /// Changes below and at this object (Hierarchy.Touch).
        public long SubtreeVersion;
        /// Changes at this object itself (Hierarchy.Touch on its transform).
        public long SelfVersion;
        /// The SubtreeVersion at which CanvasSystem last walked this object as the top of a
        /// subtree hidden by its CanvasGroups, finding no group that ignores its parents; -1 when
        /// not known to be so.
        public long CanvasHiddenAt = -1;
        /// Likewise for the top of a hidden subtree whose groups let raycasts through, where the
        /// walk found no enabled raycaster in it or above it to its nearest live canvas.
        public long CanvasNoRaycastersAt = -1;
        /// CanvasSystem.Unreachable's answer for this object, and the pass it was found in.
        public long UnreachablePass;
        public bool Unreachable;
        /// CanvasSystem.NearestCanvas's answer for the object, and the Hierarchy.Touches it was
        /// found at (0: none kept).
        public long NearestCanvasAt;
        public UI.CanvasNative NearestCanvas;
    }

    internal class ComponentNative : NativeObject
    {
        public GameObjectNative GameObject;
        /// EngineAwake has run (components that are not Behaviours).
        public bool AwakeCalledForComponent;
        /// EngineOnEnable has run and EngineOnDisable has not run since (components that are not
        /// Behaviours).
        public bool EngineEnabled;
        /// Destroy was called on the component or its GameObject (or an ancestor) and it awaits
        /// destruction: as in Unity it is disabled at once and is not enabled again.
        public bool DestroyPending;
    }

    internal class BehaviourNative : ComponentNative
    {
        public bool Enabled = true;
        /// OnEnable has run and OnDisable has not run since (Unity's "is in enabled state").
        public bool IsActiveAndEnabledState;
        public bool AwakeCalled;
        /// Its Awake is running, while its GameObject activates (Lifecycle.AwakeAndEnable).
        public bool InAwake;
        public bool StartCalled;
        /// Scheduled for Start in the next Start phase.
        public bool StartPending;
        public ScriptInfo Script;
        public int ExecutionOrder;
        /// MonoBehaviour.useGUILayout: OnGUI gets Layout events and may use GUILayout.
        public bool UseGUILayout = true;

        public override void CopyStateFrom(NativeObject source)
        {
            var b = (BehaviourNative)source;
            // Instantiate copies a behaviour awaiting destruction, and copies it disabled, as
            // observed in Unity's player: the clone keeps the enabled = false its source reports
            // and lives on after the source's destruction.
            Enabled = b.Enabled && !b.DestroyPending;
            UseGUILayout = b.UseGUILayout;
        }
        /// Per-behaviour coroutines (MonoBehaviour only).
        public List<CoroutineState> Coroutines;
    }

    internal partial class TransformNative : ComponentNative
    {
        TransformNative parent;

        /// The parent transform. A change of it starts a new rect epoch (RectTransformNative.ComputeRect).
        public TransformNative Parent
        {
            get => parent;
            set
            {
                parent = value;
                RectTransformNative.RectEpoch++;
            }
        }

        /// Children in sibling order. A list read before a change keeps the children it had:
        /// a slot below the count is never written again once published. An append fills the
        /// next spare slot of the array, any other change makes a new array (copy-on-write).
        /// Parents that grow at run time get spare slots (ChildList.Capacity), so a parent with
        /// thousands of children (the game's popup texts) is not copied whole for each one.
        TransformNative[] childArray = System.Array.Empty<TransformNative>();
        int childCount;

        public ChildList Children => new(childArray, childCount);

        /// Sets the children to exactly `children`, which the transform keeps.
        public void SetChildren(TransformNative[] children)
        {
            childArray = children;
            childCount = children.Length;
        }

        /// Inserts `child` at `index` (0 to Children.Length); Hierarchy keeps the parent links.
        public void InsertChildAt(TransformNative child, int index)
        {
            int n = childCount;
            var items = childArray;
            if (index == n && n < items.Length)
            {
                if (VerifyingChildLists) VerifyAppend(items, n);
                items[n] = child;
                childCount = n + 1;
                return;
            }
            if (VerifyingChildLists) childListCopies++;
            var grown = new TransformNative[ChildList.Capacity(n + 1)];
            System.Array.Copy(items, 0, grown, 0, index);
            grown[index] = child;
            System.Array.Copy(items, index, grown, index + 1, n - index);
            childArray = grown;
            childCount = n + 1;
        }

        /// ONI_VERIFY_CHILD_LISTS=1: each append into a spare slot checks that it and the slots
        /// after it hold nothing (no list was ever given them), logs "child lists wrong (N)"
        /// otherwise, and a summary every 4,096 such appends. Main thread only, as the changes.
        public static readonly bool VerifyingChildLists = System.Environment.GetEnvironmentVariable("ONI_VERIFY_CHILD_LISTS") == "1";
        static long childListAppends, childListCopies, childListsWrong;

        static void VerifyAppend(TransformNative[] items, int count)
        {
            for (int i = count; i < items.Length; i++)
            {
                if (items[i] == null) continue;
                EngineLog.Engine($"child lists wrong ({++childListsWrong}): spare slot {i} of {items.Length} past {count} children holds {items[i].GameObject?.Name}");
                break;
            }
            if (++childListAppends % 4096 == 1)
                EngineLog.Engine($"child lists: {childListAppends} appends into spare slots, {childListCopies} copies, {childListsWrong} wrong");
        }

        /// Removes the child at `index`. A parent that had spare slots keeps some.
        public void RemoveChildAt(int index)
        {
            int n = childCount;
            var items = childArray;
            if (VerifyingChildLists) childListCopies++;
            var shrunk = new TransformNative[items.Length > n ? ChildList.Capacity(n - 1) : n - 1];
            System.Array.Copy(items, 0, shrunk, 0, index);
            System.Array.Copy(items, index + 1, shrunk, index, n - index - 1);
            childArray = shrunk;
            childCount = n - 1;
        }

        /// Puts `replacement` in `child`'s place.
        public void ReplaceChild(TransformNative child, TransformNative replacement)
        {
            int index = Children.IndexOf(child);
            if (index < 0) return;
            var copy = (TransformNative[])childArray.Clone();
            copy[index] = replacement;
            childArray = copy;
        }
        public Vector3 LocalPosition;
        public Quaternion LocalRotation = Quaternion.identity;
        public Vector3 LocalScale = Vector3.one;
        public bool HasChanged = true;
        /// A renderer of its GameObject keeps culling bounds made from its world matrix: a
        /// change of that matrix recomputes them (FrameRenderer). Never cleared.
        internal bool CullBoundsWatched;
        /// Someone outside the engine follows this transform's moves (TransformMoveWatch): set on
        /// a few thousand game objects at most; every other transform pays the one test of it.
        internal bool MoveWatched;

        // World data, computed when first read after a change (MarkDirty). Reads may come from
        // several threads at once while nothing changes the hierarchy (game tweaks that read
        // animation controllers on worker threads, fast-anim-batches): each computes the same
        // value, and the flag is cleared with release semantics after the value is stored and
        // tested with acquire semantics before it is read, so a reader that finds it clear sees
        // the stored value. Plain moves on x64.
        Matrix4x4 localToWorld;
        HierarchyMath.Affine localToWorldAffine;
        HierarchyMath.Affine localTrs;
        Quaternion worldRotation;
        Vector3 lossyScale;
        bool worldDirty = true, rotationDirty = true, scaleDirty = true, localTrsDirty = true;

        /// The inverse matrices, made on the first worldToLocalMatrix read: most transforms
        /// never have one read, and the big colonies have hundreds of thousands of them.
        sealed class InverseCache
        {
            public HierarchyMath.Affine Local, World;
            public bool LocalDirty = true, WorldDirty = true;
        }

        InverseCache inverse;

        public override void CopyStateFrom(NativeObject source)
        {
            var t = (TransformNative)source;
            LocalPosition = t.LocalPosition;
            LocalRotation = t.LocalRotation;
            LocalScale = t.LocalScale;
        }

        public Matrix4x4 LocalToWorld
        {
            get
            {
                if (Volatile.Read(ref worldDirty)) ComputeLocalToWorld();
                return localToWorld;
            }
        }

        /// The original player composes the levels from the leaf up, (P0 (P1 (… L))), so a
        /// transform's world matrix is not its parent's times its local matrix: each read after
        /// a change walks to the root (HierarchyMath; the local matrices are cached).
        void ComputeLocalToWorld()
        {
            var m = LocalTrs();
            for (var p = Parent; p != null; p = p.Parent)
            {
                var pm = p.LocalTrs();
                m = HierarchyMath.Mul(in pm, in m);
            }
            localToWorldAffine = m;
            localToWorld = m.ToMatrix();
            Volatile.Write(ref worldStamp, Interlocked.Increment(ref worldStamps));
            Volatile.Write(ref worldDirty, false);
        }

        /// A number for the world matrix as computed: each computation takes a new one, from 1
        /// up, so while it stands so does LocalToWorld, and so does anything computed from it
        /// alone (CanvasSystem keeps the canvas elements' products with their canvas's inverse
        /// while both numbers stand).
        public long WorldStamp
        {
            get
            {
                if (Volatile.Read(ref worldDirty)) ComputeLocalToWorld();
                return Volatile.Read(ref worldStamp);
            }
        }

        long worldStamp;
        static long worldStamps;

        HierarchyMath.Affine LocalTrs()
        {
            if (Volatile.Read(ref localTrsDirty))
            {
                localTrs = HierarchyMath.Trs(LocalPosition, LocalRotation, LocalScale);
                Volatile.Write(ref localTrsDirty, false);
            }
            return localTrs;
        }

        /// World to local, as the original player computes it (tools/UnityProbe `invscale`, IS3
        /// and IS4: 3,000 chains of 1 to 6 levels, every element bit for bit): each level's
        /// inverse (HierarchyMath.InverseTrs) multiplied from the leaf up, ((L⁻¹ P1⁻¹) … P0⁻¹),
        /// never the world matrix inverted. A zero scale axis inverts to 0 (Rocketry Expanded
        /// scales a UI image by (1, 1), z 0), so a singular transform has one too.
        public Matrix4x4 WorldToLocal
        {
            get
            {
                var cache = Inverses();
                if (Volatile.Read(ref cache.WorldDirty))
                {
                    var m = LocalInverse();
                    for (var p = Parent; p != null; p = p.Parent)
                    {
                        var pm = p.LocalInverse();
                        m = HierarchyMath.Mul(in m, in pm);
                    }
                    cache.World = m;
                    Volatile.Write(ref cache.WorldDirty, false);
                }
                return cache.World.ToMatrix();
            }
        }

        InverseCache Inverses() => inverse ?? Interlocked.CompareExchange(ref inverse, new InverseCache(), null) ?? inverse;

        /// The inverse scale of each axis (InverseScale.Of), the diagonal of the cached local
        /// inverse: exactly that while the local rotation is the identity, which is the only
        /// case Hierarchy.TryInverseTransformPoint reads it in.
        public Vector3 UnrotatedInverseScale()
        {
            var m = LocalInverse();
            return new Vector3(m.M00, m.M11, m.M22);
        }

        HierarchyMath.Affine LocalInverse()
        {
            var cache = Inverses();
            if (Volatile.Read(ref cache.LocalDirty))
            {
                cache.Local = HierarchyMath.InverseTrs(LocalPosition, LocalRotation, LocalScale);
                Volatile.Write(ref cache.LocalDirty, false);
            }
            return cache.Local;
        }

        public Quaternion WorldRotation
        {
            get
            {
                if (Volatile.Read(ref rotationDirty))
                {
                    // From the leaf up too: P0 · m0(P1 · m1(… L)), each level's product seen
                    // through that ancestor's scale signs (HierarchyMath.Mirror).
                    var r = LocalRotation;
                    for (var p = Parent; p != null; p = p.Parent)
                        r = HierarchyMath.Mul(p.LocalRotation, HierarchyMath.Mirror(r, p.LocalScale));
                    worldRotation = r;
                    Volatile.Write(ref rotationDirty, false);
                }
                return worldRotation;
            }
        }

        /// The world matrix with the world rotation taken out: its diagonal (Transform.lossyScale).
        /// TextMeshPro reads it for every text each frame.
        public Vector3 LossyScale
        {
            get
            {
                if (Volatile.Read(ref scaleDirty))
                {
                    var rotation = WorldRotation;
                    if (Volatile.Read(ref worldDirty)) ComputeLocalToWorld();
                    lossyScale = HierarchyMath.LossyScale(in localToWorldAffine, rotation);
                    Volatile.Write(ref scaleDirty, false);
                }
                return lossyScale;
            }
        }

        /// Invalidates cached world data for this transform and its descendants, and marks
        /// them changed (Transform.hasChanged), for a change of their place in the world: a
        /// position, rotation, scale or parent set.
        public void MarkDirty() => MarkDirty(true);

        /// moved false: only cached data changed (an object made, cloned or loaded, a rect
        /// resized in place), not the transform's place. The local matrices are this
        /// transform's alone: its descendants keep theirs and lose only their world data.
        /// changed false: not even marked changed (hasChanged), for a value stored in place of
        /// an equal one with other bits (KeepsEqualPosition).
        public void MarkDirty(bool moved, bool changed = true)
        {
            localTrsDirty = true;
            var cache = inverse;
            if (cache != null) cache.LocalDirty = true;
            WorldChanged(moved, changed);
            bool rect = this is RectTransformNative;
            foreach (var c in Children) rect |= c.MarkWorldDirty(moved, changed);
            // A RectTransform at or below moved or resized: counted here and on every object
            // above (UI.RaycastInputs).
            if (rect)
                for (var t = this; t != null; t = t.Parent) t.SubtreeMoves++;
        }

        /// Whether a RectTransform was among those marked.
        bool MarkWorldDirty(bool moved, bool changed)
        {
            WorldChanged(moved, changed);
            bool rect = this is RectTransformNative;
            foreach (var c in Children) rect |= c.MarkWorldDirty(moved, changed);
            return rect;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        void WorldChanged(bool moved, bool changed)
        {
            worldDirty = rotationDirty = scaleDirty = true;
            var cache = inverse;
            if (cache != null) cache.WorldDirty = true;
            if (changed) HasChanged = true;
            if (CullBoundsWatched) Rendering.FrameRenderer.BoundsInputsChanged(GameObject);
            if (moved)
            {
                OnMoved();
                if (MoveWatched) TransformMoveWatch.Note(this);
            }
        }

        /// Moves and resizes of the RectTransforms at or below the transform (MarkDirty).
        public long SubtreeMoves;

        /// The transform's place in the world changed, through its own change or an ancestor's.
        protected virtual void OnMoved() { }

        /// Called after the parent changed (RectTransforms re-resolve their anchoring).
        public virtual void ParentChangedInternal(bool worldPositionStays) { }

        /// Called after the sibling index changed.
        public virtual void SiblingOrderChangedInternal() { }
    }
}
