using System;
using System.Runtime.Serialization;
using OniMoono.Host;
using Xunit;

// FormatterServices, as the game's serializer calls it.
#pragma warning disable SYSLIB0050

namespace OniMoono.Tests
{
    /// FormatterServices.GetUninitializedObject makes delegates as Mono's does (ARCHITECTURE D62;
    /// src/OniMoono.Host/MonoUninitializedObjects.cs, compiled in): a care package's
    /// Func&lt;bool&gt; requirement in a save (issue #69).
    public class MonoUninitializedObjectsTests
    {
        delegate string Private(int x, ref float y);

        class Plain
        {
            public int Value = 5;
        }

        public MonoUninitializedObjectsTests()
        {
            GameInstall.RequireAssembly("0Harmony");
            MonoUninitializedObjects.Install();
        }

        [Fact]
        public void DelegateIsMadeAndThrowsWhenInvoked()
        {
            var made = Assert.IsType<Func<bool>>(FormatterServices.GetUninitializedObject(typeof(Func<bool>)));
            Assert.Null(made.Target);
            Assert.Throws<NullReferenceException>(() => made());
            Assert.NotSame(made, FormatterServices.GetUninitializedObject(typeof(Func<bool>)));
        }

        [Fact]
        public void SafeVariantAndNonPublicDelegatesToo()
        {
            var made = Assert.IsType<Private>(FormatterServices.GetSafeUninitializedObject(typeof(Private)));
            float y = 1;
            Assert.Throws<NullReferenceException>(() => made(1, ref y));
        }

        [Fact]
        public void AbstractDelegateTypesStillThrow()
        {
            Assert.ThrowsAny<Exception>(() => FormatterServices.GetUninitializedObject(typeof(MulticastDelegate)));
            Assert.ThrowsAny<Exception>(() => FormatterServices.GetUninitializedObject(typeof(Func<>)));
            Assert.Throws<ArgumentNullException>(() => FormatterServices.GetUninitializedObject(null!));
        }

        [Fact]
        public void OtherClassesAreLeftUninitialized()
        {
            Assert.Equal(0, Assert.IsType<Plain>(FormatterServices.GetUninitializedObject(typeof(Plain))).Value);
        }
    }
}
