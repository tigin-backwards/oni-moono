using System;
using System.Runtime.CompilerServices;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;

namespace UnityEngine
{
    /// A GameObject's engine state, kept on the object itself (docs/research/facade-merge.md, step
    /// 3; it was its record, GameObjectNative). The fields are private, named apart from Unity's
    /// members; the engine reads them through internal properties of the record's old names, so
    /// its code reads a GameObject as it read the record. No field here is GameObject-typed.
    public sealed partial class GameObject
    {
        Component[] m_EngineComponents;
        bool m_EngineCullBoundsWatched;
        int m_EngineLayer;
        string m_EngineTag;
        bool m_EngineActiveSelf;
        bool m_EngineActiveInHierarchy;
        long m_EngineActivatedAt;
        SceneRecord m_EngineScene;
        bool m_EngineIsStatic;
        Transform m_EngineTransform;
        int m_EngineRootSlot;
        OniMoono.Engine.UI.UiParts m_EngineUiParts;
        ComponentLayout.Cached m_EngineLayout;
        long m_EngineSubtreeVersion;
        long m_EngineSelfVersion;
        long m_EngineCanvasHiddenAt;
        long m_EngineCanvasNoRaycastersAt;
        long m_EngineUnreachablePass;
        bool m_EngineUnreachable;
        long m_EngineNearestCanvasAt;
        OniMoono.Engine.UI.CanvasNative m_EngineNearestCanvas;
        string m_EngineName;
        Fields m_EngineSerialized;
        static long activations;

        /// The starting state, as the record started: every constructor sets it before the object
        /// is bound (no field initializers, which an object made without its constructor lacks).
        void InitEngineState()
        {
            m_EngineComponents = Array.Empty<Component>();
            m_EngineTag = "Untagged";
            m_EngineActiveSelf = true;
            m_EngineRootSlot = -1;
            m_EngineCanvasHiddenAt = -1;
            m_EngineCanvasNoRaycastersAt = -1;
            m_EngineName = "";
        }

        /// Engine constructor (deserialization, cloning): its starting state, bound; the caller
        /// sets the rest.
        internal GameObject(EngineMade made) : base()
        {
            InitEngineState();
            BindAsGameObject();
        }

        internal readonly struct EngineMade { }

        /// Destroyed: it drops what links it to its components and hierarchy, so one the game keeps
        /// holds nothing of them (DestroyedFacadeRetentionTests). Its flags stay.
        internal override void Unbind()
        {
            base.Unbind();
            m_EngineComponents = Array.Empty<Component>();
            m_EngineTransform = null;
            m_EngineUiParts = null;
            m_EngineLayout = null;
            m_EngineNearestCanvas = null;
            m_EngineSerialized = null;
        }

        /// A GameObject has no record of its own; these say so at compile time.
        [Obsolete("A GameObject's state is on the GameObject: Alive, or the object itself.", true)]
        internal new NativeObject Native => throw new InvalidOperationException();
        [Obsolete("A GameObject's state is on the GameObject: GameObjectNative (the object, alive or Unity's error).", true)]
        internal new NativeObject NativeOrThrow => throw new InvalidOperationException();

        /// The object itself, which must be alive; throws Unity's error otherwise. (It was the record.)
        internal GameObject GameObjectNative
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (!Alive) ThrowDestroyed();
                return this;
            }
        }

        static void ThrowDestroyed() => throw new NullReferenceException();

        /// What the record's Managed was.
        internal GameObject Managed => this;

        internal string Name { get => m_EngineName; set => m_EngineName = value; }
        internal Fields Serialized { get => m_EngineSerialized; set => m_EngineSerialized = value; }
        internal int InstanceId { get => EngineInstanceId; set => EngineInstanceId = value; }
        internal HideFlags HideFlags { get => EngineHideFlags; set => EngineHideFlags = value; }
        internal bool IsPersistent { get => EngineIsPersistent; set => EngineIsPersistent = value; }
        internal AssetSource Source { get => EngineSource; set => EngineSource = value; }
        internal bool DestroyRequested { get => EngineDestroyRequested; set => EngineDestroyRequested = value; }
        internal bool Destroyed { get => EngineDestroyed; set => EngineDestroyed = value; }

        /// Component order as in Unity; [0] is always the Transform. Copy-on-write so that
        /// worker threads can read it while the main thread mutates.
        internal Component[] Components
        {
            get => m_EngineComponents;
            set
            {
                m_EngineComponents = value;
                if (m_EngineCullBoundsWatched) OniMoono.Engine.Rendering.FrameRenderer.BoundsInputsChanged(this);
            }
        }

        /// A renderer of it keeps culling bounds that depend on its components (its mesh
        /// filter's mesh): a change of them recomputes those bounds (FrameRenderer). Never
        /// cleared.
        internal bool CullBoundsWatched { get => m_EngineCullBoundsWatched; set => m_EngineCullBoundsWatched = value; }
        internal int Layer { get => m_EngineLayer; set => m_EngineLayer = value; }
        internal string Tag { get => m_EngineTag; set => m_EngineTag = value; }
        internal bool ActiveSelf { get => m_EngineActiveSelf; set => m_EngineActiveSelf = value; }

        /// Cached activeInHierarchy, maintained by activation changes.
        internal bool ActiveInHierarchy
        {
            get => m_EngineActiveInHierarchy;
            set
            {
                if (value && !m_EngineActiveInHierarchy) m_EngineActivatedAt = ++activations;
                m_EngineActiveInHierarchy = value;
            }
        }

        /// When it last became active in its hierarchy, counting activations (main thread):
        /// GameObject.Find returns the match activated first.
        internal long ActivatedAt { get => m_EngineActivatedAt; set => m_EngineActivatedAt = value; }
        /// Owning scene; null for prefab assets and other objects that are not in a scene.
        internal SceneRecord Scene { get => m_EngineScene; set => m_EngineScene = value; }
        internal bool IsStatic { get => m_EngineIsStatic; set => m_EngineIsStatic = value; }
        /// The record of Components[0].
        internal Transform Transform { get => m_EngineTransform; set => m_EngineTransform = value; }
        /// Slot in the owning scene's root list while a root, else -1 (SceneRoots).
        internal int RootSlot { get => m_EngineRootSlot; set => m_EngineRootSlot = value; }
        /// Its canvas components, cached by CanvasSystem for the current Components array.
        internal OniMoono.Engine.UI.UiParts UiParts { get => m_EngineUiParts; set => m_EngineUiParts = value; }
        /// Its component types' shared layout, for the component list it was made for (Components).
        internal ComponentLayout.Cached Layout { get => m_EngineLayout; set => m_EngineLayout = value; }
        /// Changes below and at this object (Hierarchy.Touch).
        internal long SubtreeVersion { get => m_EngineSubtreeVersion; set => m_EngineSubtreeVersion = value; }
        /// Changes at this object itself (Hierarchy.Touch on its transform).
        internal long SelfVersion { get => m_EngineSelfVersion; set => m_EngineSelfVersion = value; }
        /// The SubtreeVersion at which CanvasSystem last walked this object as the top of a
        /// subtree hidden by its CanvasGroups, finding no group that ignores its parents; -1 when
        /// not known to be so.
        internal long CanvasHiddenAt { get => m_EngineCanvasHiddenAt; set => m_EngineCanvasHiddenAt = value; }
        /// Likewise for the top of a hidden subtree whose groups let raycasts through, where the
        /// walk found no enabled raycaster in it or above it to its nearest live canvas.
        internal long CanvasNoRaycastersAt { get => m_EngineCanvasNoRaycastersAt; set => m_EngineCanvasNoRaycastersAt = value; }
        /// CanvasSystem.Unreachable's answer for this object, and the pass it was found in.
        internal long UnreachablePass { get => m_EngineUnreachablePass; set => m_EngineUnreachablePass = value; }
        internal bool Unreachable { get => m_EngineUnreachable; set => m_EngineUnreachable = value; }
        /// CanvasSystem.NearestCanvas's answer for the object, and the Hierarchy.Touches it was
        /// found at (0: none kept).
        internal long NearestCanvasAt { get => m_EngineNearestCanvasAt; set => m_EngineNearestCanvasAt = value; }
        internal OniMoono.Engine.UI.CanvasNative NearestCanvas { get => m_EngineNearestCanvas; set => m_EngineNearestCanvas = value; }
    }
}
