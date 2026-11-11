using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using OniMoono.Host.GameTweaks;
using OniMoono.Tweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// `--check-tweaks`' drift checks (src/OniMoono.Host/GameTweaks/TweakDrift.cs, compiled in):
    /// the fixture tweaks below, in this assembly, against TweakChecksTests' stand-in game (class
    /// Machine with Work(int) and a field `count`). A game member reached by name must be declared;
    /// one whose name the IL does not give is a warning unless reviewed; a MirrorsGame marker and a
    /// Mirrors declaration need each other.
    public class TweakDriftTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), $"tweak-drift-{Guid.NewGuid():N}");

        public TweakDriftTests() => Directory.CreateDirectory(dir);

        public void Dispose()
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        Dictionary<string, DriftCheck> Drift(params string[] tweaks)
        {
            string managed = Path.Combine(dir, "game");
            TweakChecksTests.Game(managed, TweakChecksTests.Update.None);
            using var game = new GameFiles(managed);
            using var file = MetadataFile.Open(typeof(TweakDriftTests).Assembly.Location)!;
            var assemblies = new List<TweakChecks.Assembly> { new(file, typed: false) };
            var checks = TweakChecks.Run(game, assemblies, tweaks);
            return TweakDrift.Run(game, assemblies, checks, "test-build");
        }

        [Fact]
        public void AnUndeclaredLookupFailsWithTheLineToAdd()
        {
            var drift = Drift(DriftFixtures.Undeclared.Name)[DriftFixtures.Undeclared.Name];
            var failure = Assert.Single(drift.Failures, f => f.Contains("Machine.count", StringComparison.Ordinal));
            Assert.Contains("DriftFixtures.Undeclared.Read", failure);
            Assert.Contains("TweakDriftTests.cs:", failure);
            Assert.Contains("[GameMember(GameUse.Uses, \"System.Int32 Machine::count\")]", failure);
            // The type it took by name too, which nothing declared names.
            Assert.Contains(drift.Failures, f => f.Contains("[GameMember(GameUse.Uses, \"Machine\")]", StringComparison.Ordinal));
        }

        [Fact]
        public void ADeclaredLookupPasses()
        {
            var drift = Drift(DriftFixtures.Declared.Name)[DriftFixtures.Declared.Name];
            Assert.Empty(drift.Failures);
            Assert.Empty(drift.Warnings);
            Assert.Equal(3, drift.Checked); // the type, the field through a field and through a closure
        }

        [Fact]
        public void AMethodNamedFromAStringIsFoundOnItsType()
        {
            var drift = Drift(DriftFixtures.ColonForm.Name)[DriftFixtures.ColonForm.Name];
            var failure = Assert.Single(drift.Failures);
            Assert.Contains("[GameMember(GameUse.Uses, \"System.Int32 Machine::Work(System.Int32)\")]", failure);
        }

        [Fact]
        public void ALookupOfAMemberTheGameLacksFails()
        {
            var drift = Drift(DriftFixtures.Gone.Name)[DriftFixtures.Gone.Name];
            Assert.Contains(drift.Failures, f => f.Contains("Machine.Missing", StringComparison.Ordinal) && f.Contains("not in this game version", StringComparison.Ordinal));
        }

        [Fact]
        public void ANameofLookupNeedsNoDeclaration()
        {
            var drift = Drift(DriftFixtures.ByNameof.Name)[DriftFixtures.ByNameof.Name];
            Assert.Empty(drift.Failures);
            Assert.Equal(1, drift.Nameof);
        }

        [Fact]
        public void ADynamicLookupWarnsAndAReviewedOneIsANote()
        {
            var drift = Drift(DriftFixtures.Dynamic.Name, DriftFixtures.Reviewed.Name);
            Assert.Empty(drift[DriftFixtures.Dynamic.Name].Failures);
            Assert.Contains("not checked", Assert.Single(drift[DriftFixtures.Dynamic.Name].Warnings));
            var reviewed = drift[DriftFixtures.Reviewed.Name];
            Assert.Empty(reviewed.Failures);
            Assert.Empty(reviewed.Warnings);
            Assert.Contains("reviewed: the fixture's own reason", Assert.Single(reviewed.Notes));
        }

        [Fact]
        public void AMarkedAndDeclaredMirrorPasses()
        {
            var drift = Drift(DriftFixtures.Mirror.Name)[DriftFixtures.Mirror.Name];
            Assert.Empty(drift.Failures);
        }

        [Fact]
        public void AMirrorDeclaredButNotMarkedFails()
        {
            var failure = Assert.Single(Drift(DriftFixtures.MirrorUnmarked.Name)[DriftFixtures.MirrorUnmarked.Name].Failures);
            Assert.Contains("no code of the tweak is marked [MirrorsGame(\"System.Int32 Machine::Work(System.Int32)\")]", failure);
        }

        [Fact]
        public void AMirrorMarkedButNotDeclaredFailsWithItsFingerprint()
        {
            var failure = Assert.Single(Drift(DriftFixtures.MirrorUndeclared.Name)[DriftFixtures.MirrorUndeclared.Name].Failures);
            Assert.Contains("DriftFixtures.MirrorUndeclared.Work", failure);
            Assert.Matches("add \\[GameMember\\(GameUse.Mirrors, \"System.Int32 Machine::Work\\(System.Int32\\)\", \"[0-9a-f]{16} test-build\"\\)\\]", failure);
        }

        [Fact]
        public void ATypeDeclaredByNameIsCheckedAgainstTheGame()
        {
            string managed = Path.Combine(dir, "game");
            TweakChecksTests.Game(managed, TweakChecksTests.Update.None);
            using var game = new GameFiles(managed);
            var there = new GameMemberDeclaration("t", GameUse.Uses, "Machine", [], -1);
            var gone = new GameMemberDeclaration("t", GameUse.Uses, "Machine/Gone", [], -1);
            Assert.True(there.IsType);
            Assert.Equal("Machine.Gone", gone.Display);
            using var file = MetadataFile.Open(typeof(TweakDriftTests).Assembly.Location)!;
            var checks = TweakChecks.Run(game, [new TweakChecks.Assembly(file, typed: false)], [DriftFixtures.TypeDeclarations.Name]);
            Assert.Equal(["`Machine.Gone` is missing in this game version"], checks[DriftFixtures.TypeDeclarations.Name].Problems);
        }
    }
}

// The fixture tweaks: each class's `Name` is its tweak name, its methods the code scanned (never run).
namespace OniMoono.Tests.DriftFixtures
{
    internal static class Undeclared
    {
        public const string Name = "drift-undeclared";

        internal static object Read() => AccessTools.Field(AccessTools.TypeByName("Machine"), "count");
    }

    [GameMember(GameUse.Uses, "System.Int32 Machine::count")]
    internal static class Declared
    {
        public const string Name = "drift-declared";

        static Type machine;

        internal static void Install() => machine = AccessTools.TypeByName("Machine");

        internal static object Read() => AccessTools.Field(machine, "count");

        internal static Func<object> Later()
        {
            string field = "count";
            return () => AccessTools.Field(machine, field);
        }
    }

    internal static class ColonForm
    {
        public const string Name = "drift-colon";

        internal static object Read() => AccessTools.Method("Machine:Work");
    }

    [GameMember(GameUse.Uses, "Machine")]
    internal static class Gone
    {
        public const string Name = "drift-gone";

        internal static object Read() => AccessTools.Method(AccessTools.TypeByName("Machine"), "Missing");
    }

    [GameMember(GameUse.Uses, "Machine")]
    internal static class ByNameof
    {
        public const string Name = "drift-nameof";

        /// Stands in for the game's Machine (a typed tweak's nameof names the game's member).
        internal sealed class Machine
        {
            public int count = 1;
        }

        internal static object Read() => AccessTools.Field(AccessTools.TypeByName("Machine"), nameof(Machine.count));
    }

    [GameMember(GameUse.Uses, "Machine")]
    internal static class Dynamic
    {
        public const string Name = "drift-dynamic";

        internal static object Read(string name) => AccessTools.Field(AccessTools.TypeByName("Machine"), name);
    }

    [GameMember(GameUse.Uses, "Machine")]
    internal static class Reviewed
    {
        public const string Name = "drift-reviewed";

        [NameLookupReviewed("the fixture's own reason")]
        internal static object Read(string name) => AccessTools.Field(AccessTools.TypeByName("Machine"), name);
    }

    [GameMember(GameUse.Mirrors, "System.Int32 Machine::Work(System.Int32)", "0000000000000000 test-build")]
    internal static class Mirror
    {
        public const string Name = "drift-mirror";

        [MirrorsGame("System.Int32 Machine::Work(System.Int32)")]
        internal static int Work(int x) => x + 1;
    }

    [GameMember(GameUse.Mirrors, "System.Int32 Machine::Work(System.Int32)", "0000000000000000 test-build")]
    internal static class MirrorUnmarked
    {
        public const string Name = "drift-mirror-unmarked";

        internal static int Work(int x) => x + 1;
    }

    internal static class MirrorUndeclared
    {
        public const string Name = "drift-mirror-undeclared";

        [MirrorsGame("System.Int32 Machine::Work(System.Int32)")]
        internal static int Work(int x) => x + 1;
    }

    [GameMember(GameUse.Uses, "Machine")]
    [GameMember(GameUse.Uses, "Machine/Gone")]
    internal static class TypeDeclarations
    {
        public const string Name = "drift-type-declarations";
    }
}
