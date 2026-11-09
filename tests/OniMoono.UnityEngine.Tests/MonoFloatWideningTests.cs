using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using OniMoono.Host;
using Xunit;
using Cecil = Mono.Cecil;

namespace OniMoono.Tests
{
    /// The subject of MonoFloatWideningTests: float expressions whose float32 and Mono results
    /// differ, as tools/UnityProbe's FloatPrecision measured them in the original. Inputs come
    /// from fields so the compiler cannot fold them.
    public static class WideningSubject
    {
        public static float A07 = 0.7f, A09 = 0.9f, K10 = 10f, K7 = 7f, K255 = 255f, Two = 2f;
        public struct Pair { public float X; }
        static float Root(float f) => (float)Math.Sqrt(f);
        public static int Truncated() => (int)(A07 * K10);
        public static double Rounded() => Math.Round(A09 * K255);
        public static bool Equal() => A07 * K10 == K7;
        public static double Widened() => A07 * K10 - K7;
        public static double StoredLocal() { float d = A07 * K10 - K7; return d; }
        public static double StoredField() { var p = new Pair { X = A07 * K10 - K7 }; return p.X; }
        public static double Product() => Root(Two) * Root(Two);
        public static float Sum() { float ten = 0f; for (int i = 0; i < 10; i++) ten += 0.1f; return ten; }
    }

    public class MonoFloatWideningTests
    {
        static Type Widened()
        {
            var path = typeof(WideningSubject).Assembly.Location;
            using var module = Cecil.ModuleDefinition.ReadModule(path);
            foreach (var type in module.GetTypes().Where(t => t.FullName.StartsWith("OniMoono.Tests.WideningSubject", StringComparison.Ordinal)))
                foreach (var method in type.Methods) MonoFloatWidening.WidenMethod(method);
            using var image = new MemoryStream();
            module.Write(image);
            var context = new AssemblyLoadContext("widened", isCollectible: true);
            return context.LoadFromStream(new MemoryStream(image.ToArray())).GetType(typeof(WideningSubject).FullName!)!;
        }

        static T Call<T>(Type type, string name) => (T)type.GetMethod(name)!.Invoke(null, null)!;

        [Fact]
        public void WidenedArithmeticGivesMonosValues()
        {
            var t = Widened();
            Assert.Equal(6, Call<int>(t, "Truncated"));
            Assert.Equal(229d, Call<double>(t, "Rounded"));
            Assert.False(Call<bool>(t, "Equal"));
            Assert.Equal((double)0.7f * 10 - 7, Call<double>(t, "Widened"));
            Assert.Equal(-1.1920929E-07, Call<double>(t, "StoredLocal"), 1e-14);
            Assert.Equal(-1.1920929E-07, Call<double>(t, "StoredField"), 1e-14);
            Assert.Equal(1.9999999315429164, Call<double>(t, "Product"), 1e-15);
            Assert.Equal(1.0000001f, Call<float>(t, "Sum"));
        }

        [Fact]
        public void UnwidenedArithmeticRoundsEveryStep()
        {
            Assert.Equal(7, WideningSubject.Truncated());
            Assert.True(WideningSubject.Equal());
            Assert.Equal(0d, WideningSubject.StoredLocal());
        }

        [Fact]
        public void TheScopeIsWorldgenOnly()
        {
            Assert.True(MonoFloatWidening.MayTouch("LibNoiseDotNet.dll"));
            Assert.True(MonoFloatWidening.MayTouch("Assembly-CSharp.dll"));
            Assert.False(MonoFloatWidening.MayTouch("UnityEngine.CoreModule.dll"));
            using var module = Cecil.ModuleDefinition.CreateModule("m", Cecil.ModuleKind.Dll);
            Cecil.TypeDefinition Type(string ns, string name) => new(ns, name, Cecil.TypeAttributes.Public, module.TypeSystem.Object);
            var game = MonoFloatWidening.Scope.Single(s => s.Assembly == "Assembly-CSharp");
            var firstpass = MonoFloatWidening.Scope.Single(s => s.Assembly == "Assembly-CSharp-firstpass");
            Assert.True(MonoFloatWidening.InScope(Type("ProcGenGame", "WorldGen"), game.Namespaces, game.Types));
            Assert.False(MonoFloatWidening.InScope(Type("", "Grid"), game.Namespaces, game.Types));
            Assert.False(MonoFloatWidening.InScope(Type("ProcGenGameX", "Other"), game.Namespaces, game.Types));
            Assert.True(MonoFloatWidening.InScope(Type("Delaunay.Geo", "Polygon"), firstpass.Namespaces, firstpass.Types));
            Assert.True(MonoFloatWidening.InScope(Type("", "PointGenerator"), firstpass.Namespaces, firstpass.Types));
            Assert.False(MonoFloatWidening.InScope(Type("", "KAnimBatch"), firstpass.Namespaces, firstpass.Types));
        }
    }
}
