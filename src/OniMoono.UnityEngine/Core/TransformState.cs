using System.Threading;
using OniMoono.Engine;

namespace UnityEngine
{
    /// A transform's engine state and hierarchy, kept on the transform itself
    /// (docs/research/facade-merge.md, step 4; it was its record, TransformNative). The engine
    /// reads a Transform as it read the record. None of these fields is GameObject-typed; the
    /// parent is of the class's own type and the children an array, which Localization.SetFont's
    /// walk does not follow.
    public partial class Transform
    {
        Transform m_EngineParent;

        /// The parent transform. A change of it starts a new rect epoch (RectTransform.ComputeRect).
        internal Transform Parent
        {
            get => m_EngineParent;
            set
            {
                if (ReferenceEquals(m_EngineParent, value)) return;
                m_EngineParent = value;
                RectTransform.RectEpoch++;
            }
        }

        /// Children in sibling order. A list read before a change keeps the children it had:
        /// a slot below the count is never written again once published. An append fills the
        /// next spare slot of the array, any other change makes a new array (copy-on-write).
        /// Parents that grow at run time get spare slots (ChildList.Capacity), so a parent with
        /// thousands of children (the game's popup texts) is not copied whole for each one.
        Transform[] childArray;
        int m_EngineChildCount;

        internal ChildList Children => new(childArray, m_EngineChildCount);

        /// Sets the children to exactly `children`, which the transform keeps.
        internal void SetChildren(Transform[] children)
        {
            childArray = children;
            m_EngineChildCount = children.Length;
        }

        /// Inserts `child` at `index` (0 to Children.Length); Hierarchy keeps the parent links.
        internal void InsertChildAt(Transform child, int index)
        {
            int n = m_EngineChildCount;
            var items = childArray;
            if (index == n && n < items.Length)
            {
                if (VerifyingChildLists) VerifyAppend(items, n);
                items[n] = child;
                m_EngineChildCount = n + 1;
                return;
            }
            if (VerifyingChildLists) childListCopies++;
            var grown = new Transform[ChildList.Capacity(n + 1)];
            System.Array.Copy(items, 0, grown, 0, index);
            grown[index] = child;
            System.Array.Copy(items, index, grown, index + 1, n - index);
            childArray = grown;
            m_EngineChildCount = n + 1;
        }

        /// ONI_VERIFY_CHILD_LISTS=1: each append into a spare slot checks that it and the slots
        /// after it hold nothing (no list was ever given them), logs "child lists wrong (N)"
        /// otherwise, and a summary every 4,096 such appends. Main thread only, as the changes.
        internal static readonly bool VerifyingChildLists = System.Environment.GetEnvironmentVariable("ONI_VERIFY_CHILD_LISTS") == "1";
        static long childListAppends, childListCopies, childListsWrong;

        static void VerifyAppend(Transform[] items, int count)
        {
            for (int i = count; i < items.Length; i++)
            {
                if (items[i] is null) continue;
                EngineLog.Engine($"child lists wrong ({++childListsWrong}): spare slot {i} of {items.Length} past {count} children holds {items[i].GameObject?.Name}");
                break;
            }
            if (++childListAppends % 4096 == 1)
                EngineLog.Engine($"child lists: {childListAppends} appends into spare slots, {childListCopies} copies, {childListsWrong} wrong");
        }

        /// Removes the child at `index`. A parent that had spare slots keeps some.
        internal void RemoveChildAt(int index)
        {
            int n = m_EngineChildCount;
            var items = childArray;
            if (VerifyingChildLists) childListCopies++;
            var shrunk = new Transform[items.Length > n ? ChildList.Capacity(n - 1) : n - 1];
            System.Array.Copy(items, 0, shrunk, 0, index);
            System.Array.Copy(items, index + 1, shrunk, index, n - index - 1);
            childArray = shrunk;
            m_EngineChildCount = n - 1;
        }

        /// Moves the child at `from` to `to` (both 0 to Children.Length - 1) in one new array:
        /// what RemoveChildAt and InsertChildAt make of it, with one copy instead of two.
        internal void MoveChild(int from, int to)
        {
            int n = m_EngineChildCount;
            var items = childArray;
            if (VerifyingChildLists) childListCopies++;
            var moved = new Transform[items.Length];
            var child = items[from];
            if (from < to)
            {
                System.Array.Copy(items, 0, moved, 0, from);
                System.Array.Copy(items, from + 1, moved, from, to - from);
                System.Array.Copy(items, to + 1, moved, to + 1, n - to - 1);
            }
            else
            {
                System.Array.Copy(items, 0, moved, 0, to);
                System.Array.Copy(items, to, moved, to + 1, from - to);
                System.Array.Copy(items, from + 1, moved, from + 1, n - from - 1);
            }
            moved[to] = child;
            childArray = moved;
        }

        /// Puts `replacement` in `child`'s place.
        internal void ReplaceChild(Transform child, Transform replacement)
        {
            int index = Children.IndexOf(child);
            if (index < 0) return;
            var copy = (Transform[])childArray.Clone();
            copy[index] = replacement;
            childArray = copy;
        }
        internal Vector3 LocalPosition;
        internal Quaternion LocalRotation;
        internal Vector3 LocalScale;
        internal bool HasChanged;
        /// A renderer of its GameObject keeps culling bounds made from its world matrix: a
        /// change of that matrix recomputes them (FrameRenderer). Never cleared.
        internal bool CullBoundsWatched;
        /// Someone outside the engine follows this transform's moves (TransformMoveWatch): set on
        /// a few thousand game objects at most; every other transform pays the one test of it.
        internal bool MoveWatched;
        /// TextMeshPro's scale check follows this transform's scale (OniMoono.Engine.UI.TextScaleWatch): its
        /// slot, 0 for none. Every other transform pays the one test of it.
        internal int ScaleWatchSlot;

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
        Vector3 m_EngineLossyScale;
        bool worldDirty, rotationDirty, scaleDirty, localTrsDirty;

        /// The inverse matrices, made on the first worldToLocalMatrix read: most transforms
        /// never have one read, and the big colonies have hundreds of thousands of them.
        sealed class InverseCache
        {
            public HierarchyMath.Affine Local, World;
            public bool LocalDirty = true, WorldDirty = true;
        }

        InverseCache inverse;

        /// Instantiate's copy of the source's local placement (a RectTransform adds its anchoring).
        internal virtual void CopyStateFrom(Transform t)
        {
            LocalPosition = t.LocalPosition;
            LocalRotation = t.LocalRotation;
            LocalScale = t.LocalScale;
        }

        internal Matrix4x4 LocalToWorld
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
            for (var p = Parent; p is not null; p = p.Parent)
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
        internal long WorldStamp
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
        internal Matrix4x4 WorldToLocal
        {
            get
            {
                var cache = Inverses();
                if (Volatile.Read(ref cache.WorldDirty))
                {
                    var m = LocalInverse();
                    for (var p = Parent; p is not null; p = p.Parent)
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
        internal Vector3 UnrotatedInverseScale()
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

        internal Quaternion WorldRotation
        {
            get
            {
                if (Volatile.Read(ref rotationDirty))
                {
                    // From the leaf up too: P0 · m0(P1 · m1(… L)), each level's product seen
                    // through that ancestor's scale signs (HierarchyMath.Mirror).
                    var r = LocalRotation;
                    for (var p = Parent; p is not null; p = p.Parent)
                        r = HierarchyMath.Mul(p.LocalRotation, HierarchyMath.Mirror(r, p.LocalScale));
                    worldRotation = r;
                    Volatile.Write(ref rotationDirty, false);
                }
                return worldRotation;
            }
        }

        /// The world matrix with the world rotation taken out: its diagonal (Transform.lossyScale).
        /// TextMeshPro reads it for every text each frame.
        internal Vector3 LossyScale
        {
            get
            {
                if (Volatile.Read(ref scaleDirty))
                {
                    var rotation = WorldRotation;
                    if (Volatile.Read(ref worldDirty)) ComputeLocalToWorld();
                    m_EngineLossyScale = HierarchyMath.LossyScale(in localToWorldAffine, rotation);
                    Volatile.Write(ref scaleDirty, false);
                }
                return m_EngineLossyScale;
            }
        }

        /// Invalidates cached world data for this transform and its descendants, and marks
        /// them changed (Transform.hasChanged), for a change of their place in the world: a
        /// position, rotation, scale or parent set.
        internal void MarkDirty() => MarkDirty(true);

        /// moved false: only cached data changed (an object made, cloned or loaded, a rect
        /// resized in place), not the transform's place. The local matrices are this
        /// transform's alone: its descendants keep theirs and lose only their world data.
        /// changed false: not even marked changed (hasChanged), for a value stored in place of
        /// an equal one with other bits (KeepsEqualPosition).
        internal void MarkDirty(bool moved, bool changed = true)
        {
            localTrsDirty = true;
            var cache = inverse;
            if (cache != null) cache.LocalDirty = true;
            WorldChanged(moved, changed);
            bool rect = this is RectTransform;
            foreach (var c in Children) rect |= c.MarkWorldDirty(moved, changed);
            // A RectTransform at or below moved or resized: counted here and on every object
            // above (UI.RaycastInputs).
            if (rect)
                for (var t = this; t is not null; t = t.Parent) t.SubtreeMoves++;
        }

        /// Whether a RectTransform was among those marked.
        bool MarkWorldDirty(bool moved, bool changed)
        {
            WorldChanged(moved, changed);
            bool rect = this is RectTransform;
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
            if (CullBoundsWatched) OniMoono.Engine.Rendering.FrameRenderer.BoundsInputsChanged(GameObject);
            int slot = ScaleWatchSlot;
            if (slot != 0) OniMoono.Engine.UI.TextScaleWatch.Armed[slot] = 1;
            if (moved)
            {
                OnMoved();
                if (MoveWatched) TransformMoveWatch.Note(this);
            }
        }

        /// Moves and resizes of the RectTransforms at or below the transform (MarkDirty).
        internal long SubtreeMoves;

        /// The transform's place in the world changed, through its own change or an ancestor's.
        internal virtual void OnMoved() { }

        /// Called after the parent changed (RectTransforms re-resolve their anchoring).
        internal virtual void ParentChangedInternal(bool worldPositionStays) { }

        /// Called after the sibling index changed.
        internal virtual void SiblingOrderChangedInternal() { }

        /// The starting state, as the record started: set when the transform is bound, before
        /// its constructor body runs (no field initializers, which a transform made without its
        /// constructor, as the loaders make them, lacks).
        internal virtual void InitTransformState()
        {
            childArray = System.Array.Empty<Transform>();
            LocalRotation = Quaternion.identity;
            LocalScale = Vector3.one;
            HasChanged = true;
            worldDirty = rotationDirty = scaleDirty = localTrsDirty = true;
        }

        /// The transform itself, which must be alive; throws Unity's error otherwise. (It was the
        /// record.)
        internal Transform TransformNative
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get
            {
                if (!Alive) ThrowTransformDestroyed();
                return this;
            }
        }

        static void ThrowTransformDestroyed() => throw new System.NullReferenceException();

        /// Serialized data of a transform loaded from an asset file, kept until it is filled.
        OniMoono.Assets.TypeTrees.Fields m_EngineSerialized;
        internal OniMoono.Assets.TypeTrees.Fields Serialized { get => m_EngineSerialized; set => m_EngineSerialized = value; }

        /// What the record's GameObject and Managed were.
        internal GameObject GameObject => EngineGameObject;
        internal Transform Managed => this;
        internal bool Destroyed => EngineDestroyed;

        /// Destroyed: it drops its place in the hierarchy (its children and its parent), so one
        /// the game keeps holds nothing of the hierarchy. Its local placement stays.
        internal override void Unbind()
        {
            base.Unbind();
            childArray = System.Array.Empty<Transform>();
            m_EngineChildCount = 0;
            m_EngineParent = null;
        }
    }
}
