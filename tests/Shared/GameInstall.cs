#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.v3;

namespace OniMoono.Tests
{
    /// The game install the unit tests read (ONI_GAME_DIR, by default ~/projects/oni-linux) and
    /// what they need of it. A test that needs the game's data or one of its runnable assemblies
    /// asks here before it touches them, and is skipped without them, for a reason that starts
    /// with NeedsData or NeedsAssembly. The CI build has only the game's reference assemblies and
    /// skips those tests (scripts/ci-unit-tests.sh); scripts/check.sh fails on any such skip
    /// (scripts/test-skips.py), so a machine with the game runs them all.
    static class GameInstall
    {
        public const string NeedsData = "needs game data: ";
        public const string NeedsAssembly = "needs game assembly: ";

        /// ONI_TESTS_NO_GAME=1: the run has no game install, only its reference assemblies (CI),
        /// and also skips a test that fails reading the install or loading a reference assembly
        /// without having asked here first (NoGameSkips).
        public static bool NoGameMode => Environment.GetEnvironmentVariable("ONI_TESTS_NO_GAME") == "1";

        /// The install's folder, whether or not the game is in it.
        public static string Dir =>
            Environment.GetEnvironmentVariable("ONI_GAME_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux");

        /// OxygenNotIncluded_Data, whether or not the game's data is in it (its Managed folder may
        /// hold reference assemblies only).
        public static string DataDirUnchecked => Path.Combine(Dir, "OxygenNotIncluded_Data");

        /// The game's data folder; the test is skipped when the game's data is not there.
        public static string DataDir
        {
            get
            {
                var dir = DataDirUnchecked;
                RequireData(dir);
                return dir;
            }
        }

        /// Skips the test unless the game's data is in DIR (an OxygenNotIncluded_Data folder):
        /// its globalgamemanagers, which every build has.
        public static void RequireData(string dir)
        {
            if (!File.Exists(Path.Combine(dir, "globalgamemanagers"))) Assert.Skip(NeedsData + dir);
        }

        static readonly ConcurrentDictionary<string, string?> s_Assemblies = new();

        /// Skips the test unless the game's assembly NAME (0Harmony, Ionic.Zip, Newtonsoft.Json)
        /// that the tests were built with can run: the build copies it beside them from the game
        /// install, and a reference assembly (as CI builds against) has no code. It must come
        /// before the first method using the assembly is compiled, which loads it: a test method
        /// that uses it directly has NeedsGameAssemblyAttribute instead.
        public static void RequireAssembly(string name)
        {
            var missing = s_Assemblies.GetOrAdd(name, static name =>
            {
                var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
                if (!File.Exists(path)) return name + " (missing)";
                return IsReferenceAssembly(path) ? name + " (a reference assembly)" : null;
            });
            if (missing != null) Assert.Skip(NeedsAssembly + missing);
        }

        /// Whether PATH is a reference assembly (System.Runtime.CompilerServices.ReferenceAssemblyAttribute),
        /// which the runtime refuses to load for running.
        public static bool IsReferenceAssembly(string path)
        {
            using var pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata) return false;
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var ctor = reader.GetCustomAttribute(handle).Constructor;
                var type = ctor.Kind switch
                {
                    HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)ctor).Parent,
                    HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType(),
                    _ => default(EntityHandle),
                };
                var name = type.Kind switch
                {
                    HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)type).Name),
                    HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)type).Name),
                    _ => null,
                };
                if (name == "ReferenceAssemblyAttribute") return true;
            }
            return false;
        }
    }

    /// The test (or every test of the class) needs the game's runnable assembly NAME: it is
    /// skipped without it (GameInstall.RequireAssembly), before its method is compiled.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    sealed class NeedsGameAssemblyAttribute(string name) : BeforeAfterTestAttribute
    {
        public override void Before(MethodInfo methodUnderTest, IXunitTest test) => GameInstall.RequireAssembly(name);
    }
}
