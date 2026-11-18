using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using OniMoono.Assets.TypeTrees;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Engine
{
    /// Engine-side record of a UnityEngine.Object (what Unity keeps in native memory). The
    /// object's identity and lifetime (instance id, hide flags, where it was loaded from, its
    /// destruction flags) are kept on the object itself once the record is bound to it
    /// (Object.Bind); the record reads them there. A record without an object (the engine's own
    /// meshes and textures, or one not bound yet) keeps them itself, and binding hands them over.
    internal class NativeObject
    {
        public Object Managed;
        public string Name = "";

        int instanceId;
        HideFlags hideFlags;
        bool isPersistent, destroyRequested, destroyed;
        AssetSource source;

        public int InstanceId
        {
            get => Managed is { } m ? m.EngineInstanceId : instanceId;
            set
            {
                if (Managed is { } m) m.EngineInstanceId = value;
                else instanceId = value;
            }
        }

        public HideFlags HideFlags
        {
            get => Managed is { } m ? m.EngineHideFlags : hideFlags;
            set
            {
                if (Managed is { } m) m.EngineHideFlags = value;
                else hideFlags = value;
            }
        }

        /// Loaded from an asset file (scene objects excluded). Persistent objects are never
        /// Awakened and are released only by Resources.UnloadUnusedAssets / bundle unload.
        public bool IsPersistent
        {
            get => Managed is { } m ? m.EngineIsPersistent : isPersistent;
            set
            {
                if (Managed is { } m) m.EngineIsPersistent = value;
                else isPersistent = value;
            }
        }

        /// Where the object was loaded from, when it was.
        public AssetSource Source
        {
            get => Managed is { } m ? m.EngineSource : source;
            set
            {
                if (Managed is { } m) m.EngineSource = value;
                else source = value;
            }
        }

        /// Destroy has been requested (deferred destruction pending) or performed.
        public bool DestroyRequested
        {
            get => Managed is { } m ? m.EngineDestroyRequested : destroyRequested;
            set
            {
                if (Managed is { } m) m.EngineDestroyRequested = value;
                else destroyRequested = value;
            }
        }

        public bool Destroyed
        {
            get => Managed is { } m ? m.EngineDestroyed : destroyed;
            set
            {
                if (Managed is { } m) m.EngineDestroyed = value;
                else destroyed = value;
            }
        }

        /// Object.Bind: what the record kept before it had its object.
        public void HandOver(Object to)
        {
            to.EngineInstanceId = instanceId;
            to.EngineHideFlags = hideFlags;
            to.EngineIsPersistent = isPersistent;
            to.EngineSource = source;
            to.EngineDestroyRequested = destroyRequested;
            to.EngineDestroyed = destroyed;
        }

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

    /// What a component starts with, set before its constructor body runs (Object.ConstructComponent):
    /// its GameObject's record, its hide flags, and for a behaviour whether it starts disabled
    /// or without GUILayout and its script. Defaults are a component's: enabled, using
    /// GUILayout.
    internal struct ComponentInit
    {
        public GameObject GameObject;
        public HideFlags HideFlags;
        public bool Disabled;
        public bool NoGUILayout;
        public ScriptInfo Script;
        public int ExecutionOrder;

        public ComponentInit(GameObject gameObject) : this() => GameObject = gameObject;
    }

    /// The record of an engine component (transforms, renderers, colliders, cameras…): the state
    /// of its subsystem. The component's own state (its GameObject, its lifecycle flags) is on
    /// the component (UnityEngine.Component); these read it there. Scripts and plain components
    /// have no record.
    internal class ComponentNative : NativeObject
    {
        /// The component's GameObject record, as the component's (set when it is bound, never
        /// changed): read here without going through the component.
        public GameObject GameObject;

        public Component Component => Unsafe.As<Component>(Managed);

        public bool AwakeCalledForComponent
        {
            get => Component.EngineAwakeCalledForComponent;
            set => Component.EngineAwakeCalledForComponent = value;
        }

        public bool EngineEnabled
        {
            get => Component.EngineEnableRan;
            set => Component.EngineEnableRan = value;
        }

        public bool DestroyPending
        {
            get => Component.EngineDestroyPending;
            set => Component.EngineDestroyPending = value;
        }
    }

    /// The record of an engine Behaviour (cameras, canvases, animators…): the behaviour's own
    /// state is on the behaviour (UnityEngine.Behaviour); these read it there.
    internal class BehaviourNative : ComponentNative
    {
        public Behaviour Behaviour => Unsafe.As<Behaviour>(Managed);

        public bool Enabled
        {
            get => Behaviour.EngineEnabledSelf;
            set => Behaviour.EngineEnabledSelf = value;
        }

        public bool IsActiveAndEnabledState => Behaviour.EngineActiveAndEnabled;
    }
}
