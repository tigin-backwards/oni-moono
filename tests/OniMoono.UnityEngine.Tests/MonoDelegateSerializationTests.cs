using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using OniMoono.Host;
using Xunit;

// Formatter-based serialization, as the game's Newtonsoft.Json and BinaryFormatter use it.
#pragma warning disable SYSLIB0050, SYSLIB0051

namespace OniMoono.Tests
{
    /// Delegates serialized as on Mono (ARCHITECTURE D48; src/OniMoono.Host/MonoDelegateSerialization.cs,
    /// compiled in): through the game's Newtonsoft.Json 7, which writes an ISerializable value's
    /// GetObjectData entries, and through GetObjectData directly. The expected texts are those
    /// tools/UnityProbe's delegatejson probe logged in the original player (2026-10-29), with this
    /// assembly's names for the probe's.
    public class MonoDelegateSerializationTests
    {
        public class Target
        {
            public int Add = 3;
            public string Label = "target";
            public int Plus(int x) => x + Add;
        }

        [Serializable]
        public class SerializableTarget
        {
            public int Add = 3;
            public int Plus(int x) => x + Add;
        }

        public class Holder
        {
            public string Name = "holder";
            public Func<int, int> Fn;
        }

        public static int Twice(int x) => x * 2;
        public static T Same<T>(T x) => x;
        public class Inner { public static int Nested(Inner x, float y) => 0; }
        public class Box<T> { public T Get(T x, List<T> y) => x; }
        public static List<string> Mixed(List<int> a, ref int b, int[] c, string d, Dictionary<string, int[]> e, int[,] f) => null;

        static readonly string Asm = typeof(MonoDelegateSerializationTests).Assembly.FullName;
        const string Me = "OniMoono.Tests.MonoDelegateSerializationTests";
        const string Mscorlib = "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

        public MonoDelegateSerializationTests()
        {
            GameInstall.RequireAssembly("0Harmony");
            GameInstall.RequireAssembly("Newtonsoft.Json");
            // As the host installs it, with D1's names for the class libraries' types.
            static string AssemblyOf(Type t) => t.Assembly == typeof(object).Assembly ? Mscorlib : t.Assembly.FullName;
            MonoDelegateSerialization.Install(t => OniMoono.Engine.MonoTypeNames.FullName(t, AssemblyOf), AssemblyOf);
        }

        static string Method(string name, string className, string signature, string signature2, string generic = "null") =>
            $"{{\"Name\":\"{name}\",\"AssemblyName\":\"{Asm}\",\"ClassName\":\"{className}\",\"Signature\":\"{signature}\",\"Signature2\":\"{signature2}\",\"MemberType\":8,\"GenericArguments\":{generic}}}";

        static readonly string TwiceJson = Method("Twice", Me, "Int32 Twice(Int32)", "System.Int32 Twice(System.Int32)");
        static readonly string PlusJson = Method("Plus", Me + "+Target", "Int32 Plus(Int32)", "System.Int32 Plus(System.Int32)");

        [Fact]
        public void StaticMethodDelegate()
        {
            Assert.Equal("{\"Name\":\"holder\",\"Fn\":{\"Delegate\":{\"delegateEntry\":null},\"method0\":" + TwiceJson + "}}",
                JsonConvert.SerializeObject(new Holder { Fn = Twice }));
        }

        [Fact]
        public void InstanceMethodDelegateWritesItsTarget()
        {
            var target = new Target();
            Assert.Equal("{\"Delegate\":{\"delegateEntry\":null},\"target0\":{\"Add\":3,\"Label\":\"target\"},\"method0\":" + PlusJson + "}",
                JsonConvert.SerializeObject(new Func<int, int>(target.Plus)));
        }

        [Fact]
        public void MulticastDelegateChainsItsEntries()
        {
            var target = new Target();
            var d = (Func<int, int>)Delegate.Combine(new Func<int, int>(Twice), new Func<int, int>(target.Plus), new Func<int, int>(Twice));
            Assert.Equal("{\"Delegate\":{\"delegateEntry\":{\"delegateEntry\":{\"delegateEntry\":null}}},\"method0\":" + TwiceJson
                + ",\"target1\":{\"Add\":3,\"Label\":\"target\"},\"method1\":" + PlusJson + ",\"method2\":" + TwiceJson + "}",
                JsonConvert.SerializeObject(d));
        }

        [Fact]
        public void ClosureWritesItsDisplayClass()
        {
            int captured = 7;
            Func<int, int> d = x => x + captured;
            var json = JsonConvert.SerializeObject(d);
            Assert.StartsWith("{\"Delegate\":{\"delegateEntry\":null},\"target0\":{\"captured\":7},\"method0\":{\"Name\":\"" + d.Method.Name + "\"", json);
            Assert.Contains("\"ClassName\":\"" + d.Method.DeclaringType.FullName + "\"", json);
            Func<int, int> lambda = x => x - 1;
            Assert.StartsWith("{\"Delegate\":{\"delegateEntry\":null},\"target0\":{},\"method0\":", JsonConvert.SerializeObject(lambda));
        }

        [Fact]
        public void SettingsAsOnMono()
        {
            var holder = new Holder { Fn = Twice };
            Assert.Equal(JsonConvert.SerializeObject(holder), JsonConvert.SerializeObject(holder, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore }));
            Assert.Contains("\"Fn\": {\n    \"Delegate\": {\n      \"delegateEntry\": null\n    },\n    \"method0\": {\n      \"Name\": \"Twice\",",
                JsonConvert.SerializeObject(holder, Formatting.Indented).Replace("\r", ""));
            // $type names this runtime's types (System.Private.CoreLib, the host's holder), where Mono's name mscorlib's.
            var typed = JsonConvert.SerializeObject(holder, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All });
            Assert.Contains("\"Delegate\":{\"$type\":\"OniMoono.Host.DelegateSerializationHolder+DelegateEntry, ", typed);
        }

        sealed class PrivateFields : DefaultContractResolver
        {
            protected override List<MemberInfo> GetSerializableMembers(Type objectType) =>
                objectType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Cast<MemberInfo>().ToList();

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var p = base.CreateProperty(member, memberSerialization);
                p.Readable = p.Writable = true;
                return p;
            }
        }

        [Fact]
        public void EntryFieldsInMonosOrderWithAResolverTakingPrivateFields()
        {
            var json = JsonConvert.SerializeObject(new Func<int, int>(Twice), new JsonSerializerSettings { ContractResolver = new PrivateFields() });
            Assert.StartsWith("{\"Delegate\":{\"type\":\"System.Func`2[[System.Int32, " + Mscorlib + "],[System.Int32, " + Mscorlib + "]]\",\"assembly\":\"" + Mscorlib
                + "\",\"target\":null,\"targetTypeAssembly\":\"" + Asm + "\",\"targetTypeName\":\"" + Me + "\",\"methodName\":\"Twice\",\"delegateEntry\":null},\"method0\":", json);
        }

        static List<SerializationEntry> List(SerializationInfo info)
        {
            var list = new List<SerializationEntry>();
            foreach (SerializationEntry e in info) list.Add(e);
            return list;
        }

        [Fact]
        public void GetObjectDataWritesMonosEntries()
        {
            var target = new Target();
            var d = (Func<int, int>)Delegate.Combine(new Func<int, int>(Twice), new Func<int, int>(target.Plus));
            var info = new SerializationInfo(d.GetType(), new FormatterConverter());
            d.GetObjectData(info, new StreamingContext(StreamingContextStates.All));
            var entries = List(info);
            Assert.Equal(["Delegate", "method0", "target1", "method1"], entries.Select(e => e.Name));
            Assert.Same(target, entries[2].Value);
            var method = Assert.IsAssignableFrom<MethodInfo>(entries[1].Value);
            // A MethodInfo standing for the method (CoreCLR's RuntimeMethodInfo is not ISerializable).
            Assert.True(method.Equals(typeof(MonoDelegateSerializationTests).GetMethod(nameof(Twice))));
            Assert.Equal(8, method.Invoke(null, [4]));
            Assert.Equal("Int32 Twice(Int32)", method.ToString());
            Assert.Equal("OniMoono.Host.DelegateSerializationHolder", info.FullTypeName);
        }

        [Fact]
        public void MethodSignaturesAsMonoWritesThem()
        {
            static IDictionary<string, object> Entries(MethodInfo m)
            {
                var info = new SerializationInfo(typeof(object), new FormatterConverter());
                ((ISerializable)new SerializedMethod(m)).GetObjectData(info, default);
                return List(info).ToDictionary(e => e.Name, e => e.Value);
            }
            var mixed = Entries(typeof(MonoDelegateSerializationTests).GetMethod(nameof(Mixed)));
            Assert.Equal("System.Collections.Generic.List`1[System.String] Mixed(System.Collections.Generic.List`1[System.Int32], Int32 ByRef, Int32[], System.String, "
                + "System.Collections.Generic.Dictionary`2[System.String,System.Int32[]], Int32[,])", mixed["Signature"]);
            Assert.Equal("System.Collections.Generic.List`1[System.String] Mixed(System.Collections.Generic.List`1[System.Int32], System.Int32&, System.Int32[], System.String, "
                + "System.Collections.Generic.Dictionary`2[System.String,System.Int32[]], System.Int32[,])", mixed["Signature2"]);
            Assert.Null(mixed["GenericArguments"]);
            var same = Entries(typeof(MonoDelegateSerializationTests).GetMethod(nameof(Same)).MakeGenericMethod(typeof(int)));
            Assert.Equal("Int32 Same[Int32](Int32)", same["Signature"]);
            Assert.Equal("System.Int32 Same[Int32](System.Int32)", same["Signature2"]);
            Assert.Equal(["System.Int32, " + Mscorlib], (string[])same["GenericArguments"]);
            var nested = Entries(typeof(Inner).GetMethod(nameof(Inner.Nested)));
            Assert.Equal("Int32 Nested(Inner, Single)", nested["Signature"]);
            Assert.Equal("System.Int32 Nested(" + Me + "+Inner, System.Single)", nested["Signature2"]);
            Assert.Equal(Me + "+Inner", nested["ClassName"]);
            var boxGet = Entries(typeof(Box<int>).GetMethod(nameof(Box<int>.Get)));
            Assert.Equal(Me + "+Box`1[[System.Int32, " + Mscorlib + "]]", boxGet["ClassName"]);
            Assert.Equal("Int32 Get(Int32, System.Collections.Generic.List`1[System.Int32])", boxGet["Signature"]);
            Assert.Equal("System.Int32 Get(System.Int32, System.Collections.Generic.List`1[System.Int32])", boxGet["Signature2"]);
            var definition = Entries(typeof(MonoDelegateSerializationTests).GetMethod(nameof(Same)));
            Assert.Equal("T Same[T](T)", definition["Signature"]);
            Assert.Equal("T Same[T](T)", definition["Signature2"]);
            Assert.Null(definition["GenericArguments"]);
        }

        [Fact]
        public void GenericMethodDelegate()
        {
            Assert.Equal("{\"Delegate\":{\"delegateEntry\":null},\"method0\":" + Method("Same", Me, "Int32 Same[Int32](Int32)", "System.Int32 Same[Int32](System.Int32)",
                "[\"System.Int32, " + Mscorlib + "\"]") + "}", JsonConvert.SerializeObject(new Func<int, int>(Same)));
        }

#pragma warning disable SYSLIB0011 // BinaryFormatter: the game's and mods' clones use it
        static T RoundTrip<T>(T value)
        {
            var stream = new MemoryStream();
            new BinaryFormatter().Serialize(stream, value);
            stream.Position = 0;
            return (T)new BinaryFormatter().Deserialize(stream);
        }

        [Fact]
        public void BinaryFormatterGivesTheDelegateBack()
        {
            Assert.Equal(10, RoundTrip(new Func<int, int>(Twice))(5));
            var target = new SerializableTarget { Add = 4 };
            var d = RoundTrip((Func<int, int>)Delegate.Combine(new Func<int, int>(target.Plus), new Func<int, int>(Twice)));
            Assert.Equal(2, d.GetInvocationList().Length);
            Assert.Equal(9, ((Func<int, int>)d.GetInvocationList()[0])(5));
            Assert.Equal(10, d(5));
            Assert.Equal(5, RoundTrip(new Func<int, int>(Same))(5));
            // As on Mono, a target that is not serializable fails the formatter.
            Assert.Throws<SerializationException>(() => RoundTrip(new Func<int, int>(new Target().Plus)));
        }
#pragma warning restore SYSLIB0011

        [Fact]
        public void NewtonsoftCannotReadADelegateBack()
        {
            // Both runtimes: a delegate type has no (SerializationInfo, StreamingContext) constructor.
            var json = JsonConvert.SerializeObject(new Func<int, int>(Twice));
            Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<Func<int, int>>(json));
        }
    }
}
