using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace OniMoono.Tests
{
    /// The game's save format takes a component's members by KSerialization's
    /// SerializationTemplate (Assembly-CSharp-firstpass, U59 and U60), which walks the type and
    /// its base types up to object: under OptOut, the default for a KMonoBehaviour without
    /// [SerializationConfig], each type's public instance fields and its public instance
    /// properties with a public setter and no index; under OptIn only those marked [Serialize].
    /// So what the facade bases contribute to every component's template must be what Unity's
    /// own bases contribute: an engine field or property made public there would be written into
    /// every save (docs/research/facade-merge.md, category 3).
    public class SaveTemplateTests
    {
        static readonly string[] Bases = ["UnityEngine.Object", "UnityEngine.Component", "UnityEngine.Behaviour", "UnityEngine.MonoBehaviour"];

        /// AddPublicFields and AddPublicProperties of one type, as "Type field|property Name: FieldType".
        static IEnumerable<string> OptOutMembers(Type type)
        {
            const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public;
            foreach (var f in type.GetFields(Declared))
                if (!Has(f, "System.NonSerializedAttribute"))
                    yield return $"{type.FullName} field {f.Name}: {f.FieldType.FullName}";
            foreach (var p in type.GetProperties(Declared))
                if (p.GetIndexParameters().Length == 0 && p.GetSetMethod() != null
                    && !Has(p, "System.NonSerializedAttribute"))
                    yield return $"{type.FullName} property {p.Name}: {p.PropertyType.FullName}";
        }

        /// Whether the member has the attribute. Unity's assemblies carry attributes of assemblies a
        /// reference copy of the game lacks: those cannot be the one asked for.
        static bool Has(MemberInfo m, string attribute)
        {
            foreach (var a in m.CustomAttributes)
            {
                string name;
                try
                {
                    name = a.AttributeType.FullName;
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                if (name == attribute) return true;
            }
            return false;
        }

        /// AddOptInFields and AddOptInProperties: members marked with KSerialization's Serialize.
        static IEnumerable<string> OptInMembers(Type type)
        {
            const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var m in type.GetFields(Declared).Cast<MemberInfo>().Concat(type.GetProperties(Declared)))
                if (Has(m, "KSerialization.Serialize"))
                    yield return $"{type.FullName} {m.Name}";
        }

        [Fact]
        public void TheFacadeBasesGiveATemplateWhatUnitysGive()
        {
            var unityPath = Path.Combine(GameInstall.DataDirUnchecked, "Managed", "UnityEngine.CoreModule.dll");
            if (!File.Exists(unityPath)) Assert.Skip(GameInstall.NeedsAssembly + unityPath);
            var core = Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location), "*.dll");
            var managed = Directory.GetFiles(Path.GetDirectoryName(unityPath), "*.dll");
            using var context = new MetadataLoadContext(new PathAssemblyResolver(core.Concat(managed.Where(p => Path.GetFileName(p) != "mscorlib.dll" && Path.GetFileName(p) != "netstandard.dll"))));
            var unity = context.LoadFromAssemblyPath(unityPath);
            var ours = typeof(UnityEngine.Object).Assembly;

            foreach (var name in Bases)
            {
                var theirs = unity.GetType(name, throwOnError: true);
                var mine = ours.GetType(name, throwOnError: true);
                Assert.Equal(OptOutMembers(theirs).Order(StringComparer.Ordinal), OptOutMembers(mine).Order(StringComparer.Ordinal));
                Assert.Empty(OptInMembers(mine));
            }
            // Unity's own members are there: the template is not empty by accident.
            Assert.Contains("UnityEngine.Object property name: System.String", OptOutMembers(ours.GetType("UnityEngine.Object")));
            Assert.Contains("UnityEngine.Behaviour property enabled: System.Boolean", OptOutMembers(ours.GetType("UnityEngine.Behaviour")));
        }

        /// The base chain a template walks is Unity's: a component's template reaches object
        /// through exactly these classes.
        [Fact]
        public void TheBaseChainIsUnitys()
        {
            var chain = new List<string>();
            for (var t = typeof(UnityEngine.MonoBehaviour); t != typeof(object); t = t.BaseType) chain.Add(t.FullName);
            Assert.Equal(Bases.Reverse(), chain);
        }
    }
}
