using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OniMoono.GameTweaks;
using OniMoono.Host.GameTweaks;
using OniMoono.Tweaks;
using Xunit;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Tests
{
    /// A game tweak written for one game version is off on another, and the rest install
    /// (src/OniMoono.Host/GameTweaks/TweakChecks.cs, TweakInstall.cs, compiled in): the checks
    /// against the game's files, on a stand-in Assembly-CSharp and tweak assembly written here as
    /// a game update would change them, and the install's all-or-nothing and transpiler counts,
    /// with Harmony on methods of this assembly.
    public class TweakChecksTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), $"tweak-checks-{Guid.NewGuid():N}");

        public TweakChecksTests()
        {
            Directory.CreateDirectory(dir);
            TweakInstall.Reset();
        }

        public void Dispose()
        {
            TweakInstall.Reset();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ---- The game's files ----

        /// How a game update changed the stand-in game.
        public enum Update { None, BodyChanged, MethodGone, SignatureChanged, FieldRetyped, CalleeGone, MenuTargetGone }

        /// Assembly-CSharp in `folder`: class Machine with Work(int) (returns its argument plus
        /// one; plus two once its body changed), Signed(int) (long once its signature changed),
        /// a field count (an int, a long once retyped), and Helper(), which the tweak's lambda
        /// calls; class MainMenu with OnPrefabInit(), which the Moono Tweaks menu patches.
        static string Game(string folder, Update update)
        {
            Directory.CreateDirectory(folder);
            var name = new Cecil.AssemblyNameDefinition("Assembly-CSharp", new Version(0, 0, 0, 0));
            using var assembly = Cecil.AssemblyDefinition.CreateAssembly(name, "Assembly-CSharp", Cecil.ModuleKind.Dll);
            var module = assembly.MainModule;
            var machine = new Cecil.TypeDefinition("", "Machine", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(machine);
            var ctor = new Cecil.MethodDefinition(".ctor", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.HideBySig | Cecil.MethodAttributes.SpecialName | Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            ctor.Body.GetILProcessor().Emit(Cil.OpCodes.Ret);
            machine.Methods.Add(ctor);
            var work = new Cecil.MethodDefinition("Work", Cecil.MethodAttributes.Public, module.TypeSystem.Int32);
            work.Parameters.Add(new Cecil.ParameterDefinition("x", Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            var il = work.Body.GetILProcessor();
            il.Emit(Cil.OpCodes.Ldarg_1);
            il.Emit(update == Update.BodyChanged ? Cil.OpCodes.Ldc_I4_2 : Cil.OpCodes.Ldc_I4_1);
            il.Emit(Cil.OpCodes.Add);
            il.Emit(Cil.OpCodes.Ret);
            if (update != Update.MethodGone) machine.Methods.Add(work);
            var signed = new Cecil.MethodDefinition("Signed", Cecil.MethodAttributes.Public, module.TypeSystem.Void);
            signed.Parameters.Add(new Cecil.ParameterDefinition("x", Cecil.ParameterAttributes.None, update == Update.SignatureChanged ? module.TypeSystem.Int64 : module.TypeSystem.Int32));
            signed.Body.GetILProcessor().Emit(Cil.OpCodes.Ret);
            machine.Methods.Add(signed);
            machine.Fields.Add(new Cecil.FieldDefinition("count", Cecil.FieldAttributes.Public, update == Update.FieldRetyped ? module.TypeSystem.Int64 : module.TypeSystem.Int32));
            var helper = new Cecil.MethodDefinition("Helper", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            helper.Body.GetILProcessor().Emit(Cil.OpCodes.Ret);
            if (update != Update.CalleeGone) machine.Methods.Add(helper);
            var mainMenu = new Cecil.TypeDefinition("", "MainMenu", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(mainMenu);
            var prefabInit = new Cecil.MethodDefinition("OnPrefabInit", Cecil.MethodAttributes.Family | Cecil.MethodAttributes.Virtual | Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
            prefabInit.Body.GetILProcessor().Emit(Cil.OpCodes.Ret);
            if (update != Update.MenuTargetGone) mainMenu.Methods.Add(prefabInit);
            string path = Path.Combine(folder, "Assembly-CSharp.dll");
            assembly.Write(path);
            return path;
        }

        /// A tweak assembly in `folder`, built against the unchanged game: OniMoono.Tweaks'
        /// GameUse and GameMemberAttribute (each tweak assembly has its own), tweak `uses-work`
        /// (class UsesWork: replaces Machine.Work, declared with `fingerprint`, and reads
        /// Machine.count), tweak `calls-helper` (class CallsHelper: calls Machine.Helper from a
        /// lambda's closure class nested in it, declaring nothing), tweak `signed` (declares
        /// Machine.Signed(int)) and the Moono Tweaks menu (class Menu: declares its postfix of
        /// MainMenu.OnPrefabInit, and calls CallsHelper's Guarded, which calls Machine.Helper, as
        /// ModOverlaps reads what each tweak guards).
        static string Tweaks(string folder, string gameFile, string fingerprint)
        {
            Directory.CreateDirectory(folder);
            var name = new Cecil.AssemblyNameDefinition("Tweaks", new Version(1, 0, 0, 0));
            using var assembly = Cecil.AssemblyDefinition.CreateAssembly(name, "Tweaks", Cecil.ModuleKind.Dll);
            var module = assembly.MainModule;
            using var game = Cecil.ModuleDefinition.ReadModule(gameFile);
            var machine = game.Types.Single(t => t.Name == "Machine");

            var use = new Cecil.TypeDefinition("OniMoono.Tweaks", "GameUse", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Sealed,
                module.ImportReference(typeof(Enum)));
            use.Fields.Add(new Cecil.FieldDefinition("value__", Cecil.FieldAttributes.Public | Cecil.FieldAttributes.SpecialName | Cecil.FieldAttributes.RTSpecialName, module.TypeSystem.Int32));
            module.Types.Add(use);
            var attribute = new Cecil.TypeDefinition("OniMoono.Tweaks", "GameMemberAttribute", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Sealed,
                module.ImportReference(typeof(Attribute)));
            var stringArray = new Cecil.ArrayType(module.TypeSystem.String);
            var attributeCtor = new Cecil.MethodDefinition(".ctor", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.HideBySig | Cecil.MethodAttributes.SpecialName | Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            attributeCtor.Parameters.Add(new Cecil.ParameterDefinition(use));
            attributeCtor.Parameters.Add(new Cecil.ParameterDefinition(module.TypeSystem.String));
            attributeCtor.Parameters.Add(new Cecil.ParameterDefinition(stringArray));
            attributeCtor.Body.GetILProcessor().Emit(Cil.OpCodes.Ret);
            attribute.Methods.Add(attributeCtor);
            module.Types.Add(attribute);

            Cecil.TypeDefinition Tweak(string type, string tweakName)
            {
                var t = new Cecil.TypeDefinition("", type, Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
                t.Fields.Add(new Cecil.FieldDefinition("Name", Cecil.FieldAttributes.Public | Cecil.FieldAttributes.Static | Cecil.FieldAttributes.Literal, module.TypeSystem.String) { Constant = tweakName });
                module.Types.Add(t);
                return t;
            }
            void Declare(Cecil.TypeDefinition t, GameUse kind, string member, params string[] prints)
            {
                var a = new Cecil.CustomAttribute(attributeCtor);
                a.ConstructorArguments.Add(new Cecil.CustomAttributeArgument(use, (int)kind));
                a.ConstructorArguments.Add(new Cecil.CustomAttributeArgument(module.TypeSystem.String, member));
                a.ConstructorArguments.Add(new Cecil.CustomAttributeArgument(stringArray,
                    prints.Select(p => new Cecil.CustomAttributeArgument(module.TypeSystem.String, p)).ToArray()));
                t.CustomAttributes.Add(a);
            }

            var usesWork = Tweak("UsesWork", "uses-work");
            Declare(usesWork, GameUse.Prefix | GameUse.Replaces, "System.Int32 Machine::Work(System.Int32)", fingerprint + " test-build");
            var read = new Cecil.MethodDefinition("Read", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
            read.Parameters.Add(new Cecil.ParameterDefinition(module.ImportReference(machine)));
            var il = read.Body.GetILProcessor();
            il.Emit(Cil.OpCodes.Ldarg_0);
            il.Emit(Cil.OpCodes.Ldfld, module.ImportReference(machine.Fields.Single(f => f.Name == "count")));
            il.Emit(Cil.OpCodes.Ret);
            usesWork.Methods.Add(read);

            var callsHelper = Tweak("CallsHelper", "calls-helper");
            var closure = new Cecil.TypeDefinition("", "<>c", Cecil.TypeAttributes.NestedPrivate | Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
            callsHelper.NestedTypes.Add(closure);
            var lambda = new Cecil.MethodDefinition("<Install>b__0_0", Cecil.MethodAttributes.Assembly, module.TypeSystem.Void);
            il = lambda.Body.GetILProcessor();
            il.Emit(Cil.OpCodes.Call, module.ImportReference(machine.Methods.Single(m => m.Name == "Helper")));
            il.Emit(Cil.OpCodes.Ret);
            closure.Methods.Add(lambda);

            var signed = Tweak("SignedTweak", "signed");
            Declare(signed, GameUse.Postfix, "System.Void Machine::Signed(System.Int32)");

            var guarded = new Cecil.MethodDefinition("Guarded", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            il = guarded.Body.GetILProcessor();
            il.Emit(Cil.OpCodes.Call, module.ImportReference(machine.Methods.Single(m => m.Name == "Helper")));
            il.Emit(Cil.OpCodes.Ret);
            callsHelper.Methods.Add(guarded);
            var menu = Tweak("Menu", GameTweakNames.Menu);
            Declare(menu, GameUse.Postfix, "System.Void MainMenu::OnPrefabInit()");
            var refresh = new Cecil.MethodDefinition("Refresh", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            il = refresh.Body.GetILProcessor();
            il.Emit(Cil.OpCodes.Call, guarded);
            il.Emit(Cil.OpCodes.Ret);
            menu.Methods.Add(refresh);

            string path = Path.Combine(folder, "Tweaks.dll");
            assembly.Write(path);
            return path;
        }

        /// The tweaks' checks against the game after `update`: each tweak's reason to be off (null: on).
        Dictionary<string, string> Checked(Update update)
        {
            string built = Game(Path.Combine(dir, "built"), Update.None);
            string hash;
            using (var file = MetadataFile.Open(built)!)
            {
                var type = file.Type("Machine");
                var work = file.Reader.GetTypeDefinition(type).GetMethods().Single(m => file.Reader.GetString(file.Reader.GetMethodDefinition(m).Name) == "Work");
                hash = Fingerprints.Of(file, work).Hash;
            }
            string tweaks = Tweaks(Path.Combine(dir, "tweaks"), built, hash);
            string managed = Path.Combine(dir, "game-" + update);
            Game(managed, update);
            TweakInstall.Reset();
            string[] all = ["uses-work", "calls-helper", "signed", GameTweakNames.Menu];
            TweakInstall.Check(managed, tweaks, typeof(TweakChecksTests).Assembly, all);
            return all.ToDictionary(n => n, n => TweakInstall.Unavailable.TryGetValue(n, out var r) ? r : null);
        }

        [Fact]
        public void TheGameItWasWrittenForTurnsNoneOff()
        {
            var off = Checked(Update.None);
            Assert.All(off.Values, Assert.Null);
        }

        [Fact]
        public void AChangedBodyTurnsOffTheTweakThatReplacesIt()
        {
            var off = Checked(Update.BodyChanged);
            Assert.Equal("`Machine.Work` changed in this game version", off["uses-work"]);
            Assert.Null(off["calls-helper"]);
            Assert.Null(off["signed"]);
        }

        [Fact]
        public void AMissingTargetTurnsOffItsTweakAlone()
        {
            var off = Checked(Update.MethodGone);
            Assert.Equal("`Machine.Work` is missing in this game version", off["uses-work"]);
            Assert.Null(off["calls-helper"]);
            Assert.Null(off["signed"]);
        }

        [Fact]
        public void AChangedSignatureTurnsOffItsTweakAlone()
        {
            var off = Checked(Update.SignatureChanged);
            Assert.Equal("`Machine.Signed` changed in this game version", off["signed"]);
            Assert.Null(off["uses-work"]);
            Assert.Null(off["calls-helper"]);
        }

        /// The reference scan: a field the tweak's code reads, retyped, and a method a lambda in
        /// a closure class nested in the tweak's class calls, gone.
        [Fact]
        public void TheCodesReferencesAreCheckedPerTweakLambdasToo()
        {
            var off = Checked(Update.FieldRetyped);
            Assert.Equal("`Machine.count` changed in this game version", off["uses-work"]);
            Assert.Null(off["calls-helper"]);
            off = Checked(Update.CalleeGone);
            Assert.Equal("`Machine.Helper` is missing in this game version", off["calls-helper"]);
            Assert.Null(off["uses-work"]);
        }

        /// A game update without what the Moono Tweaks menu patches: start-up goes on as Program
        /// runs it (the tweaks' installs, then the menu's), the tweaks install and the menu does
        /// not, its install never run and nothing of it patched; the tweaks the settings file
        /// chose are as it says, read without the menu.
        [Fact]
        public void WithoutTheMenusTargetTheTweaksInstallAndThereIsNoMenu()
        {
            var off = Checked(Update.MenuTargetGone);
            Assert.Equal("`MainMenu.OnPrefabInit` is missing in this game version", off[GameTweakNames.Menu]);
            Assert.Null(off["uses-work"]);
            Assert.Null(off["calls-helper"]);
            Assert.Null(off["signed"]);
            var settings = OniMoono.Host.PlayerSettings.Parse("{\"gameTweaks\": {\"fast-jobs\": false}}");
            var effective = OniMoono.Host.EffectiveOptions.Resolve(GameTweakNames.All, GameTweakNames.ChangingBehaviour, [], null, false, settings,
                offByDefault: GameTweakNames.OffByDefault);
            Assert.DoesNotContain("fast-jobs", effective.EnabledTweaks);
            Assert.Contains("fast-sublimation", effective.EnabledTweaks);
            bool menuRan = false;
            try
            {
                Assert.True(TweakInstall.Guarded("signed", h =>
                    h.Patch(AccessTools.Method(typeof(Target), nameof(Target.B)), prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.Ten))), () => null));
                Assert.False(TweakInstall.Guarded(GameTweakNames.Menu, h =>
                {
                    menuRan = true;
                    h.Patch(AccessTools.Method(typeof(Target), nameof(Target.A)), prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.Ten)));
                }, () => null, log: false, harmonyId: "OniMoono.Host.MoonoTweaksMenu"));
                Assert.False(menuRan);
                Assert.Equal(1, Target.A());
                Assert.Equal(10, Target.B());
                Assert.DoesNotContain(Harmony.GetPatchInfo(AccessTools.Method(typeof(Target), nameof(Target.A)))?.Prefixes ?? [], p => p.owner == "OniMoono.Host.MoonoTweaksMenu");
            }
            finally
            {
                Unpatch("OniMoono.Host.GameTweaks.signed", "OniMoono.Host.MoonoTweaksMenu");
            }
        }

        /// The menu's reference scan stops at the tweaks' classes: a tweak whose code reaches a
        /// member the game lost is off, the menu that reads what that tweak guards is not.
        [Fact]
        public void ATweaksBrokenReferenceLeavesTheMenuOn()
        {
            var off = Checked(Update.CalleeGone);
            Assert.Equal("`Machine.Helper` is missing in this game version", off["calls-helper"]);
            Assert.Null(off[GameTweakNames.Menu]);
        }

        [Fact]
        public void FingerprintsIgnoreTokenNumbers()
        {
            // The same body after an unrelated type is added before Machine (its tokens renumber).
            string a = Game(Path.Combine(dir, "a"), Update.None);
            var b = Path.Combine(dir, "b", "Assembly-CSharp.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(b)!);
            using (var module = Cecil.ModuleDefinition.ReadModule(a))
            {
                module.Types.Insert(1, new Cecil.TypeDefinition("", "Earlier", Cecil.TypeAttributes.Public, module.TypeSystem.Object));
                var helper = module.Types.Single(t => t.Name == "Machine").Methods.Single(m => m.Name == "Helper");
                module.Types.Single(t => t.Name == "Machine").Methods.Remove(helper);
                module.Types.Single(t => t.Name == "Machine").Methods.Insert(0, helper);
                module.Write(b);
            }
            string Hash(string path)
            {
                using var file = MetadataFile.Open(path)!;
                var work = file.Reader.GetTypeDefinition(file.Type("Machine")).GetMethods().Single(m => file.Reader.GetString(file.Reader.GetMethodDefinition(m).Name) == "Work");
                return Fingerprints.Of(file, work).Hash;
            }
            Assert.Equal(Hash(a), Hash(b));
        }

        // ---- The install ----

        public class Target
        {
            public static readonly List<string> Calls = new();

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static int A() => 1;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static int B() => 2;

            /// Two calls of Site, which the tweak's transpiler sends to Ours.
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Twice()
            {
                Site("first");
                Site("second");
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Site(string which) => Calls.Add("game " + which);
        }

        public static class Patches
        {
            public static bool Ten(ref int __result)
            {
                __result = 10;
                return false;
            }

            public static void Ours(string which) => Target.Calls.Add("tweak " + which);
            public static void Mods(string which) => Target.Calls.Add("mod " + which);

            static readonly MethodInfo site = AccessTools.Method(typeof(Target), nameof(Target.Site));

            public static IEnumerable<CodeInstruction> Reroute(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                int n = 0;
                foreach (var i in instructions)
                {
                    if (i.Calls(site)) { i.operand = AccessTools.Method(typeof(Patches), nameof(Ours)); n++; }
                    yield return i;
                }
                TranspilerCounts.Report("reroute", original, n);
            }

            /// A mod's transpiler that takes the first call itself (as Fast Track's CullConduits
            /// takes the overlays' calls, issue #41).
            public static IEnumerable<CodeInstruction> ModTakesFirst(IEnumerable<CodeInstruction> instructions)
            {
                bool taken = false;
                foreach (var i in instructions)
                {
                    if (!taken && i.Calls(site)) { i.operand = AccessTools.Method(typeof(Patches), nameof(Mods)); taken = true; }
                    yield return i;
                }
            }
        }

        static readonly MethodInfo twice = AccessTools.Method(typeof(Target), nameof(Target.Twice));

        static void DeclareTranspiler(string name, int rewrites) =>
            TweakInstall.Declare(name, [new GameMemberDeclaration(name, GameUse.Transpiler, TweakChecks.Signature(twice), [], rewrites)]);

        static void Unpatch(params string[] owners)
        {
            foreach (var owner in owners) new Harmony(owner).UnpatchAll(owner);
        }

        static List<string> RunTwice()
        {
            Target.Calls.Clear();
            Target.Twice();
            return Target.Calls.ToList();
        }

        [Fact]
        public void AFailingInstallTakesBackWhatItPatchedAndTheNextInstalls()
        {
            try
            {
                bool first = TweakInstall.Guarded("half", h =>
                {
                    h.Patch(AccessTools.Method(typeof(Target), nameof(Target.A)), prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.Ten)));
                    throw new MissingMethodException("Machine", "Gone");
                }, () => null);
                bool second = TweakInstall.Guarded("whole", h =>
                    h.Patch(AccessTools.Method(typeof(Target), nameof(Target.B)), prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.Ten))), () => null);
                Assert.False(first);
                Assert.True(second);
                Assert.Equal(1, Target.A());
                Assert.Equal(10, Target.B());
                Assert.StartsWith("its install failed: MissingMethodException", TweakInstall.Unavailable["half"]);
                Assert.False(TweakInstall.Unavailable.ContainsKey("whole"));
            }
            finally
            {
                Unpatch("OniMoono.Host.GameTweaks.half", "OniMoono.Host.GameTweaks.whole");
            }
        }

        [Fact]
        public void ATranspilerRewritingTheSitesItDeclaresInstalls()
        {
            DeclareTranspiler("reroute", 2);
            try
            {
                Assert.True(TweakInstall.Guarded("reroute", h => h.Patch(twice, transpiler: new HarmonyMethod(typeof(Patches), nameof(Patches.Reroute))), () => null));
                Assert.Equal(new[] { "tweak first", "tweak second" }, RunTwice());
            }
            finally
            {
                Unpatch("OniMoono.Host.GameTweaks.reroute");
            }
        }

        [Theory]
        [InlineData(3, "rewrote 2 of 3 sites")] // fewer, and no other owner transpiles it
        [InlineData(1, "rewrote 2 of 1 sites")] // more
        public void ATranspilerOffItsCountIsTakenBack(int declared, string why)
        {
            DeclareTranspiler("reroute", declared);
            try
            {
                Assert.False(TweakInstall.Guarded("reroute", h => h.Patch(twice, transpiler: new HarmonyMethod(typeof(Patches), nameof(Patches.Reroute))), () => null));
                Assert.Contains(why, TweakInstall.Unavailable["reroute"]);
                Assert.StartsWith("`OniMoono.Tests.TweakChecksTests.Target.Twice` changed in this game version", TweakInstall.Unavailable["reroute"]);
                Assert.Equal(new[] { "game first", "game second" }, RunTwice());
            }
            finally
            {
                Unpatch("OniMoono.Host.GameTweaks.reroute");
            }
        }

        [Fact]
        public void AnUndeclaredTranspilerIsTakenBack()
        {
            try
            {
                Assert.False(TweakInstall.Guarded("reroute", h => h.Patch(twice, transpiler: new HarmonyMethod(typeof(Patches), nameof(Patches.Reroute))), () => null));
                Assert.Contains("its transpiler is not declared", TweakInstall.Unavailable["reroute"]);
                Assert.Equal(new[] { "game first", "game second" }, RunTwice());
            }
            finally
            {
                Unpatch("OniMoono.Host.GameTweaks.reroute");
            }
        }

        /// Issue #41: a mod's transpiler took one of the two sites first; the tweak's, run after
        /// it (CallSiteReroute's priority), rewrites the other, one of the two it declares, and
        /// installs.
        [Fact]
        public void FewerSitesWithAnotherOwnersTranspilerInstalls()
        {
            DeclareTranspiler("reroute", 2);
            var mod = new Harmony("test.tweakchecks.mod");
            try
            {
                mod.Patch(twice, transpiler: new HarmonyMethod(typeof(Patches), nameof(Patches.ModTakesFirst)));
                Assert.True(TweakInstall.Guarded("reroute", h => h.Patch(twice,
                    transpiler: CallSiteReroute.Transpiler(typeof(Patches), nameof(Patches.Reroute))), () => null));
                Assert.False(TweakInstall.Unavailable.ContainsKey("reroute"));
                Assert.Equal(new[] { "mod first", "tweak second" }, RunTwice());
            }
            finally
            {
                Unpatch("OniMoono.Host.GameTweaks.reroute", mod.Id);
            }
        }

        [Fact]
        public void SignaturesOfLoadedMethodsAreSpelledAsTheMetadatas()
        {
            Assert.Equal("System.Void OniMoono.Tests.TweakChecksTests/Target::Twice()", TweakChecks.Signature(twice));
            var generic = typeof(Dictionary<int, string>).GetMethod("TryGetValue")!;
            Assert.Equal("System.Boolean System.Collections.Generic.Dictionary`2::TryGetValue(!0,!1&)", TweakChecks.Signature(generic));
        }
    }
}
