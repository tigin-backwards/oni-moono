using System;
using OniMoono.Engine;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// A component's own state (its GameObject, its lifecycle flags, a behaviour's enabled state
    /// and script) is on the component itself, merged from what was its record
    /// (docs/research/facade-merge.md, step 2): scripts and plain components have no record,
    /// engine components keep their subsystem's state in theirs. The values the game sees are
    /// the records' were.
    public class MergedComponentStateTests : EngineTest
    {
        public class Script : MonoBehaviour
        {
            // Not serialized: Instantiate would copy them from the source.
            [NonSerialized] public bool EnabledInConstructor, HadGameObjectInConstructor;
            [NonSerialized] public HideFlags FlagsInConstructor = (HideFlags)(-1);
            [NonSerialized] public string NameInConstructor;
            [NonSerialized] public int Awakes;

            public Script()
            {
                // Unity forbids engine calls here; the engine answers them as its records did,
                // with the component's starting state in place before the constructor body.
                EnabledInConstructor = enabled;
                NameInConstructor = name;
                FlagsInConstructor = hideFlags;
                HadGameObjectInConstructor = gameObject;
            }

            void Awake() => Awakes++;
        }

        /// Created with no constructor run at all (no parameterless constructor).
        public class NoDefaultConstructor : MonoBehaviour
        {
            public NoDefaultConstructor(int unused) { }
        }

        public class Plain : Component { }

        [Fact]
        public void AScriptStartsAsItsRecordDid()
        {
            var go = new GameObject("holder");
            var s = go.AddComponent<Script>();
            Assert.True(s.EnabledInConstructor);
            Assert.Equal("holder", s.NameInConstructor);
            Assert.Equal(HideFlags.None, s.FlagsInConstructor);
            Assert.True(s.HadGameObjectInConstructor);
            Assert.True(s.enabled);
            Assert.True(s.useGUILayout);
            Assert.True(s.didAwake);
            Assert.False(s.didStart);
            Assert.True(s.isActiveAndEnabled);
            Assert.Equal(1, s.Awakes);
            Assert.True(s.GetInstanceID() < 0);
            Assert.Same(go, s.gameObject);
            Assert.Same(go.transform, s.transform);
            Assert.Null(s.EngineState);
            Assert.NotNull(s.EngineScript);
            Assert.Equal(s.EngineScript.ExecutionOrder, s.EngineExecutionOrder);
            Frame();
            Assert.True(s.didStart);
        }

        [Fact]
        public void AScriptMadeWithoutAConstructorStartsEnabled()
        {
            var go = new GameObject("holder");
            var s = go.AddComponent<NoDefaultConstructor>();
            Assert.True(s.enabled);
            Assert.True(s.useGUILayout);
            Assert.True(s.isActiveAndEnabled);
            Assert.Same(go, s.gameObject);
        }

        [Fact]
        public void EngineComponentsKeepTheirSubsystemsState()
        {
            var go = new GameObject("holder");
            var camera = go.AddComponent<Camera>();
            var plain = go.AddComponent<Plain>();
            Assert.IsType<Engine.Rendering.CameraNative>(camera.EngineState);
            Assert.Same(camera, camera.EngineState.Managed);
            Assert.Same(go, camera.EngineState.GameObject);
            Assert.True(camera.enabled);
            Assert.True(((BehaviourNative)camera.EngineState).IsActiveAndEnabledState);
            Assert.Null(go.transform.EngineState);
            Assert.Same(go.transform, go.Transform);
            Assert.Null(plain.EngineState);
            Assert.Same(go, plain.gameObject);
        }

        [Fact]
        public void AnInactiveObjectsScriptIsNotAwake()
        {
            var go = new GameObject("holder");
            go.SetActive(false);
            var s = go.AddComponent<Script>();
            Assert.Equal(0, s.Awakes);
            Assert.False(s.didAwake);
            Assert.True(s.enabled);
            Assert.False(s.isActiveAndEnabled);
            go.SetActive(true);
            Assert.Equal(1, s.Awakes);
            Assert.True(s.isActiveAndEnabled);
        }

        [Fact]
        public void ADestroyedScriptIsFakeNullAndThrowsAsBefore()
        {
            var go = new GameObject("holder");
            var s = go.AddComponent<Script>();
            int id = s.GetInstanceID();
            Assert.NotEqual(0, id);
            Object.DestroyImmediate(s);
            Assert.True(s == null);
            Assert.False(s);
            Assert.Equal(0, s.GetInstanceID());
            Assert.Equal("null", s.ToString());
            Assert.False(s.isActiveAndEnabled);
            Assert.Throws<NullReferenceException>(() => s.enabled);
            Assert.Throws<NullReferenceException>(() => s.enabled = false);
            Assert.Throws<NullReferenceException>(() => s.name);
            Assert.Throws<NullReferenceException>(() => s.hideFlags);
            Assert.Throws<NullReferenceException>(() => s.gameObject);
            Assert.Throws<NullReferenceException>(() => s.didAwake);
            Assert.Throws<NullReferenceException>(() => s.StopAllCoroutines());
            Assert.True(s.EngineDestroyed);
            Assert.Null(s.EngineGameObject);
            Assert.True(go);
            Assert.Null(go.GetComponent<Script>());
        }

        [Fact]
        public void DestroyDisablesAtOnceAndDestroysAtTheEndOfTheFrame()
        {
            var go = new GameObject("holder");
            var s = go.AddComponent<Script>();
            Object.Destroy(s);
            Assert.True(s);
            Assert.False(s.enabled);
            Assert.False(s.isActiveAndEnabled);
            Assert.True(s.EngineDestroyPending);
            Assert.True(s.EngineDestroyRequested);
            s.enabled = true;
            Assert.False(s.enabled);
            Frame();
            Assert.False(s);
        }

        [Fact]
        public void InstantiateCopiesTheStateAsTheRecordsDid()
        {
            var go = new GameObject("source");
            var enabled = go.AddComponent<Script>();
            var disabled = go.AddComponent<Script>();
            disabled.enabled = false;
            disabled.useGUILayout = false;
            disabled.hideFlags = HideFlags.DontSave;
            var pending = go.AddComponent<Script>();
            Object.Destroy(pending);

            var clone = Object.Instantiate(go);
            var scripts = clone.GetComponents<Script>();
            Assert.Equal(3, scripts.Length);
            Assert.True(scripts[0].enabled);
            Assert.True(scripts[0].useGUILayout);
            Assert.True(scripts[0].EnabledInConstructor);
            Assert.False(scripts[1].enabled);
            Assert.False(scripts[1].EnabledInConstructor);
            Assert.False(scripts[1].useGUILayout);
            Assert.Equal(HideFlags.DontSave, scripts[1].hideFlags);
            Assert.Equal(HideFlags.DontSave, scripts[1].FlagsInConstructor);
            // Copied disabled while its source awaits destruction, and outlives it.
            Assert.False(scripts[2].enabled);
            Frame();
            Assert.False(pending);
            Assert.True(scripts[2]);
            Assert.False(scripts[2].enabled);
            Assert.Same(clone, scripts[0].gameObject);
            Assert.Null(scripts[0].EngineState);
            Assert.NotEqual(enabled.GetInstanceID(), scripts[0].GetInstanceID());
        }

        [Fact]
        public void ARectTransformTakesATransformsPlaceAndTheOldOneIsDestroyed()
        {
            var go = new GameObject("holder");
            var s = go.AddComponent<Script>();
            var old = go.transform;
            Assert.Same(old, s.transform);
            var rect = go.AddComponent<RectTransform>();
            Assert.False(old);
            Assert.Same(rect, go.transform);
            Assert.Same(rect, s.transform);
            Assert.Null(rect.EngineState);
            Assert.Same(rect, go.Transform);
            Assert.Same(go, rect.EngineGameObject);
        }

        [Fact]
        public void TheTableHoldsComponentsWithoutRecords()
        {
            var go = new GameObject("holder");
            var s = go.AddComponent<Script>();
            Assert.Same(s, ObjectTable.FindByInstanceId(s.GetInstanceID()));
            Assert.Same(s, ObjectTable.Resolve(s.Handle));
            Assert.Contains(s, Resources.FindObjectsOfTypeAll<Script>());
            int id = s.GetInstanceID();
            var handle = s.Handle;
            Object.DestroyImmediate(go);
            Assert.Null(ObjectTable.FindByInstanceId(id));
            Assert.Null(ObjectTable.Resolve(handle));
        }
    }
}
