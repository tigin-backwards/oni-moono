using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// Patches of a generic class's shared code after tiered compilation recompiles it (ARCHITECTURE
    /// D18; src/OniMoono.Host/SharedGenerics.cs, compiled in): every instantiation runs the
    /// replacement Harmony detoured last, as on Mono, whose code is never recompiled.
    public class SharedGenericsTests
    {
        public class Holder<T> where T : class
        {
            public T Value;
            public T Get() => Value;
        }

        static void Nothing() { }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void MethodsComeInTheOrderHarmonyDetouredThem()
        {
            SharedGenerics.Install();
            var harmony = new Harmony("OniMoono.Tests.SharedGenerics");
            var postfix = new HarmonyMethod(typeof(SharedGenericsTests), nameof(Nothing));
            MethodBase Get(Type t) => typeof(Holder<>).MakeGenericType(t).GetMethod(nameof(Holder<object>.Get))!;
            var (a, b, c) = (Get(typeof(Uri)), Get(typeof(Version)), Get(typeof(Exception)));
            try
            {
                harmony.Patch(b, postfix: postfix);
                harmony.Patch(c, postfix: postfix);
                harmony.Patch(a, postfix: postfix);
                Assert.Equal([b, c, a], SharedGenerics.InDetourOrder([a, b, c]));
                // Patched again, b's detour is the latest; a method never detoured comes first.
                harmony.Patch(b, prefix: postfix);
                var never = Get(typeof(string));
                Assert.Equal([never, c, a, b], SharedGenerics.InDetourOrder([a, b, never, c]));
            }
            finally
            {
                harmony.UnpatchAll(harmony.Id);
            }
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void TheReplacementDetouredLastRunsAfterTheSharedCodeTiersUp()
        {
            // Box<string>'s replacement casts an item typed as a sealed class that is no Exception,
            // which CoreCLR compiles to null: run for Box<ArgumentException>, it throws.
            var result = SharedGenericsOrderCheck.Run();
            Assert.StartsWith("every call ran the replacement patched last", result);
        }
    }
}
