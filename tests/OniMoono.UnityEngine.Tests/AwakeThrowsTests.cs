using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// Throws from Awake and records its callbacks (as U60's LocText throws without a key).
    public class AwakeThrower : MonoBehaviour
    {
        public static readonly List<string> Log = new();
        bool updated;
        void Awake() { Log.Add("Awake"); throw new InvalidOperationException("probe"); }
        void OnEnable() => Log.Add("OnEnable");
        void Start() => Log.Add("Start");
        void Update() { if (!updated) Log.Add("Update"); updated = true; }
        void OnDisable() => Log.Add("OnDisable");
        void OnDestroy() => Log.Add("OnDestroy");
    }

    /// A behaviour whose Awake throws is left disabled, with an OnDisable although it had no
    /// OnEnable; enabled again, it runs as any other. Measured in the U60 761211 player
    /// (tools/UnityProbe, PROBE_SCENARIO=u60, lines `U60 AW`).
    public class AwakeThrowsTests : EngineTest
    {
        public AwakeThrowsTests() => AwakeThrower.Log.Clear();

        string State(MonoBehaviour b) => $"enabled={b.enabled} isActiveAndEnabled={b.isActiveAndEnabled}";

        [Fact]
        public void OnAnInactiveObjectActivatedLater()
        {
            var go = new GameObject("inactive");
            go.SetActive(false);
            var b = go.AddComponent<AwakeThrower>();
            Assert.Equal("enabled=True isActiveAndEnabled=False", State(b));
            go.SetActive(true);
            Assert.Equal(["Awake", "OnDisable"], AwakeThrower.Log);
            Assert.Equal("enabled=False isActiveAndEnabled=False", State(b));
            Frame();
            Frame();
            go.SetActive(false);
            go.SetActive(true);
            Assert.Equal(["Awake", "OnDisable"], AwakeThrower.Log);
            Assert.Equal("enabled=False isActiveAndEnabled=False", State(b));
            b.enabled = false;
            b.enabled = true;
            Assert.Equal("enabled=True isActiveAndEnabled=True", State(b));
            Frame();
            Assert.Equal(["Awake", "OnDisable", "OnEnable", "Start", "Update"], AwakeThrower.Log);
        }

        [Fact]
        public void AddedToAnActiveObject()
        {
            var go = new GameObject("active");
            var b = go.AddComponent<AwakeThrower>();
            Assert.Equal(["Awake", "OnDisable"], AwakeThrower.Log);
            Assert.Equal("enabled=False isActiveAndEnabled=False", State(b));
            Frame();
            Frame();
            Assert.Equal(["Awake", "OnDisable"], AwakeThrower.Log);
            UnityEngine.Object.Destroy(go);
            Frame();
            Assert.Equal(["Awake", "OnDisable", "OnDestroy"], AwakeThrower.Log);
        }
    }
}
