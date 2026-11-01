using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    [DefaultExecutionOrder(-1000)]
    public class OrderEarly : Recorder { }

    /// No attribute of its own: it inherits its base class's order.
    public class OrderEarlyChild : OrderEarly { }

    [DefaultExecutionOrder(1000)]
    public class OrderLate : Recorder { }

    // [DefaultExecutionOrder] on a mod's MonoBehaviours, which no MonoScript describes. Radbolt
    // Demolisher (Workshop 3768016876) and Cooperative Colonies (3824584976) mark theirs; without
    // the type their assemblies failed to load. Measured in the original U60 player (UnityProbe's
    // execorder): the player orders such types' FixedUpdate, Update and LateUpdate by it, and an
    // object's Awake and OnEnable as it activates, the attribute inherited; Starts of separate
    // objects run in the order they were scheduled.
    public class DefaultExecutionOrderTests : EngineTest
    {
        public DefaultExecutionOrderTests() => Recorder.Log.Clear();

        [Fact]
        public void TheAttributeHasUnitysShape()
        {
            var t = typeof(DefaultExecutionOrder);
            Assert.Equal("UnityEngine", t.Namespace);
            Assert.False(t.IsSealed); // as U59's and U60's CoreModule declare it
            var usage = t.GetCustomAttribute<AttributeUsageAttribute>()!;
            Assert.Equal(AttributeTargets.Class, usage.ValidOn);
            Assert.False(usage.AllowMultiple);
            Assert.True(usage.Inherited);
            Assert.NotNull(t.GetConstructor([typeof(int)]));
            var order = t.GetProperty("order")!;
            Assert.Equal(typeof(int), order.PropertyType);
            Assert.Null(order.SetMethod);
            Assert.Equal(-1000, new DefaultExecutionOrder(-1000).order);
        }

        static GameObject Make<T>(string label) where T : Recorder
        {
            var go = new GameObject(label);
            go.SetActive(false);
            go.AddComponent<T>().label = label;
            go.SetActive(true);
            return go;
        }

        [Fact]
        public void UpdatesRunInTheAttributesOrder()
        {
            Make<OrderLate>("pos1"); Make<Recorder>("plain1"); Make<OrderEarly>("neg1"); Make<OrderEarlyChild>("child1");
            Make<OrderEarly>("neg2"); Make<Recorder>("plain2"); Make<OrderLate>("pos2"); Make<OrderEarlyChild>("child2");
            Recorder.Log.Clear();
            Frame();
            Assert.Equal(["pos1", "plain1", "neg1", "child1", "neg2", "plain2", "pos2", "child2"],
                Recorder.Log.Where(s => s.EndsWith(":Start")).Select(s => s.Split(':')[0]));
            Assert.Equal(["neg1", "child1", "neg2", "child2", "plain1", "plain2", "pos1", "pos2"],
                Recorder.Log.Where(s => s.EndsWith(":Update")).Select(s => s.Split(':')[0]));
        }

        [Fact]
        public void AnObjectAwakesItsScriptsInTheAttributesOrder()
        {
            var go = new GameObject("one");
            go.SetActive(false);
            go.AddComponent<OrderLate>().label = "pos";
            go.AddComponent<Recorder>().label = "plain";
            go.AddComponent<OrderEarly>().label = "neg";
            go.AddComponent<OrderEarlyChild>().label = "child";
            go.SetActive(true);
            Assert.Equal(["neg:Awake", "neg:OnEnable", "child:Awake", "child:OnEnable", "plain:Awake", "plain:OnEnable", "pos:Awake", "pos:OnEnable"], Recorder.Log);
            Recorder.Log.Clear();
            Frame();
            Assert.Equal(["neg:Start", "child:Start", "plain:Start", "pos:Start", "neg:Update", "child:Update", "plain:Update", "pos:Update"], Recorder.Log);
        }
    }
}
