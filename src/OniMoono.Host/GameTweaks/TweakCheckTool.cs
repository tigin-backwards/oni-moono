#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// `--check-tweaks check|review|declare` against a game build on disk (`--game DIR`), without
    /// starting the game (scripts/check-tweaks.sh, check.sh): which tweaks would be off on it, and why.
    ///
    /// - check: every tweak's checks against the build's files (TweakChecks: declared members,
    ///   references, fingerprints); then, when this host is for the build's game update, the
    ///   assemblies are loaded and every tweak installs as in play (TweakInstall.Guarded: its
    ///   transpilers' rewrite counts), and what each patched must be what it declares. Exit 1 when
    ///   any tweak would be off or patches something undeclared.
    /// - review: the same, exit 0, and for each fingerprinted member whose body is not a reviewed
    ///   one, the fingerprint to add once it is reviewed (`--tweak-listings DIR`: every
    ///   fingerprinted body's normalised IL, for diffing two builds).
    ///   The Moono Tweaks menu (GameTweakNames.Menu) is checked and installed the same way.
    /// - declare: every tweak installed without its checks, and each patched method printed as a
    ///   GameMember line with its fingerprint and rewrites (a tweak's first declarations).
    internal static class TweakCheckTool
    {
        /// The build's label: its folder's name without `oni-` (`linux-744825`).
        public static string Label(string gameDir)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir)));
            return name.StartsWith("oni-", StringComparison.Ordinal) ? name.Substring(4) : name;
        }

        /// The checks from the files alone (any game update). Returns the tweaks off.
        public static int Files(string gameDir, string dataDir, string mode, string? listings, out bool failed)
        {
            string label = Label(gameDir);
            string managed = Path.Combine(dataDir, "Managed");
            using var game = new GameFiles(managed);
            using var typedFile = MetadataFile.Open(Path.Combine(AppContext.BaseDirectory, "OniMoono.GameTweaks.dll"));
            using var hostFile = MetadataFile.Loaded(typeof(TweakCheckTool).Assembly);
            var assemblies = new List<TweakChecks.Assembly> { new(hostFile!, typed: false) };
            if (typedFile != null) assemblies.Add(new TweakChecks.Assembly(typedFile, typed: true));
            else Console.WriteLine("check-tweaks: no OniMoono.GameTweaks.dll beside the host");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var checks = TweakChecks.Run(game, assemblies, GameTweakNames.Checked, bodies: true);
            double ms = clock.Elapsed.TotalMilliseconds;
            if (mode == "declare")
            {
                Describe(game, label);
                // Each declared member's body on this build, as its fingerprint line ends.
                foreach (var check in checks.Values)
                    foreach (var (member, hash, _) in check.Bodies)
                        Console.WriteLine($"  fingerprint {check.Tweak} {hash} {member.Member}");
            }
            failed = typedFile == null;
            int off = 0;
            var undeclared = GameTweakNames.All.Where(n => checks[n].Members.Count == 0).ToList();
            foreach (var name in GameTweakNames.All)
            {
                var check = checks[name];
                if (check.Problems.Count == 0) continue;
                off++;
                Console.WriteLine($"  off {name}: {string.Join("; ", check.Problems)}");
                if (mode == "check") continue;
                foreach (var (member, hash, _) in check.Bodies)
                    if (!member.Fingerprints.Any(p => p.StartsWith(hash, StringComparison.Ordinal)))
                        Console.WriteLine($"      to review: {member.Display}: body {hash} (reviewed: {(member.Fingerprints.Length == 0 ? "none" : string.Join(", ", member.Fingerprints))}); once reviewed, add \"{hash} {label}\"");
            }
            if (listings != null)
            {
                var dir = Path.Combine(listings, label);
                Directory.CreateDirectory(dir);
                foreach (var check in checks.Values)
                    foreach (var (member, hash, listing) in check.Bodies)
                        File.WriteAllText(Path.Combine(dir, $"{check.Tweak}.{member.Display}.il"), listing);
                Console.WriteLine($"  listings: {dir}");
            }
            if (undeclared.Count > 0) Console.WriteLine($"  declare nothing: {string.Join(", ", undeclared)}");
            if (undeclared.Count > 0 && mode == "check") failed = true;
            Console.WriteLine($"check-tweaks {label}: {GameTweakNames.All.Length - off} of {GameTweakNames.All.Length} tweaks pass the file checks, {off} off ({ms:F1} ms)");
            if (off > 0 && mode == "check") failed = true;
            // The Moono Tweaks menu, checked as a tweak is.
            var menu = checks[GameTweakNames.Menu];
            if (menu.Members.Count == 0) Console.WriteLine("  the Moono Tweaks menu declares nothing");
            foreach (var problem in menu.Problems) Console.WriteLine($"  off {GameTweakNames.Menu}: {problem}");
            Console.WriteLine($"check-tweaks {label}: the Moono Tweaks menu {(menu.Problems.Count == 0 ? "passes" : "fails")} the file checks");
            if ((menu.Problems.Count > 0 || menu.Members.Count == 0) && mode == "check") failed = true;
            return off;
        }

        /// With the build's assemblies loaded: every tweak the file checks left on installs as
        /// in play, and patches only what it declares.
        public static void Installs(string gameDir, string dataDir, string mode, bool failedBefore, out bool failed)
        {
            string label = Label(gameDir);
            failed = failedBefore;
            if (mode == "declare")
            {
                Declare(Path.Combine(dataDir, "Managed"), label);
                return;
            }
            TweakInstall.Reset();
            TweakInstall.Check(Path.Combine(dataDir, "Managed"), Path.Combine(AppContext.BaseDirectory, "OniMoono.GameTweaks.dll"), typeof(TweakCheckTool).Assembly, GameTweakNames.Checked);
            var installed = new HashSet<string>(StringComparer.Ordinal);
            int off = 0;
            var problems = new List<string>();
            foreach (var (name, install, _) in GameTweaks.Installers(TweakInstall.Available))
            {
                var harmony = new Harmony("OniMoono.Host.GameTweaks." + name);
                if (!TweakInstall.Guarded(name, install, () => GameTweaks.typed, log: false))
                {
                    off++;
                    Console.WriteLine($"  off {name}: {TweakInstall.Unavailable[name]}");
                    continue;
                }
                installed.Add(name);
                AsDeclared(name, harmony, problems);
            }
            // The Moono Tweaks menu as in play, with the default options and no settings file.
            var options = EffectiveOptions.Resolve(GameTweakNames.All, GameTweakNames.ChangingBehaviour, [], null, false, new PlayerSettings(),
                offByDefault: GameTweakNames.OffByDefault);
            bool menu = MoonoTweaksMenu.InstallScreen(options, new PlayerSettings(), Path.Combine(Path.GetTempPath(), "check-tweaks-settings.json"));
            if (menu) AsDeclared(GameTweakNames.Menu, new Harmony(MoonoTweaksMenu.HarmonyId), problems);
            else Console.WriteLine($"  off {GameTweakNames.Menu}: {TweakInstall.Unavailable[GameTweakNames.Menu]}");
            foreach (var problem in problems) Console.WriteLine("  " + problem);
            Console.WriteLine($"check-tweaks {label} installs: {installed.Count} installed, {off} off, the Moono Tweaks menu {(menu ? "installed" : "off")}; {(problems.Count == 0 ? "every patch declared" : $"{problems.Count} patches not as declared")}");
            if ((off > 0 || !menu || problems.Count > 0) && mode == "check") failed = true;
        }

        /// Each patch `harmony` made must be one `name` declares, of that kind.
        static void AsDeclared(string name, Harmony harmony, List<string> problems)
        {
            var declared = TweakInstall.Declared(name);
            foreach (var method in harmony.GetPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                var uses = Uses(info, harmony.Id);
                if (uses == GameUse.Uses) continue;
                string signature = TweakChecks.Signature(method);
                var member = declared.FirstOrDefault(m => m.Member == signature);
                if (member == null) problems.Add($"{name} patches {signature} ({uses}), which it does not declare");
                else if ((member.Use & uses) != uses) problems.Add($"{name} declares {signature} as {member.Use}, but its patches are {uses}");
            }
        }

        /// The kinds of patch `owner` has on a method.
        static GameUse Uses(Patches info, string owner)
        {
            var uses = GameUse.Uses;
            foreach (var p in info.Prefixes)
                if (p.owner == owner) uses |= p.PatchMethod.ReturnType == typeof(bool) ? GameUse.Prefix | GameUse.Replaces : GameUse.Prefix;
            if (info.Postfixes.Any(p => p.owner == owner)) uses |= GameUse.Postfix;
            if (info.Transpilers.Any(p => p.owner == owner)) uses |= GameUse.Transpiler;
            if (info.Finalizers.Any(p => p.owner == owner)) uses |= GameUse.Finalizer;
            return uses;
        }

        /// declare's ONI_TWEAK_DESCRIBE=Type::name,…: those members as GameMember lines (every
        /// overload), for the members a tweak reaches by name (any build, from its files).
        static void Describe(GameFiles game, string label)
        {
            foreach (var wanted in (Environment.GetEnvironmentVariable("ONI_TWEAK_DESCRIBE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var (typeName, memberName) = GameMemberDeclaration.Split(wanted);
                if (game.Find(typeName) is not { } owner) { Console.WriteLine($"    {wanted}: no such type"); continue; }
                var type = owner.File.Reader.GetTypeDefinition(owner.Type);
                foreach (var handle in type.GetMethods())
                    if (owner.File.Reader.StringComparer.Equals(owner.File.Reader.GetMethodDefinition(handle).Name, memberName))
                        Console.WriteLine($"    [GameMember(GameUse.Mirrors, \"{owner.File.Names.Method(handle)}\", \"{Fingerprints.Of(owner.File, handle).Hash} {label}\")]");
                foreach (var handle in type.GetFields())
                    if (owner.File.Reader.StringComparer.Equals(owner.File.Reader.GetFieldDefinition(handle).Name, memberName))
                        Console.WriteLine($"    [GameMember(GameUse.Uses, \"{owner.File.Names.Field(handle)}\")]");
            }
        }

        /// Every tweak installed without its checks; each patched method as a GameMember line.
        static void Declare(string managed, string label)
        {
            using var game = new GameFiles(managed);
            foreach (var (name, install, _) in GameTweaks.Installers(_ => true))
            {
                var harmony = new Harmony("OniMoono.Host.GameTweaks." + name);
                string? failure = null;
                try { install(harmony); }
                catch (Exception e) { failure = (e is TargetInvocationException { InnerException: { } inner } ? inner : e).Message; }
                var counts = new Dictionary<MethodBase, int>();
                foreach (var flat in new[] { TranspilerCounts.Take(), GameTweaks.typed?.GetType("OniMoono.Tweaks.TranspilerCounts")?.GetMethod("Take")?.Invoke(null, null) as object[] ?? [] })
                    for (int i = 0; i + 2 < flat.Length; i += 3)
                        if ((string)flat[i] == name) counts[(MethodBase)flat[i + 1]] = (int)flat[i + 2];
                Console.WriteLine($"{name}:{(failure != null ? " (install failed: " + failure + ")" : "")}");
                foreach (var method in harmony.GetPatchedMethods().OrderBy(m => m.DeclaringType?.FullName).ThenBy(m => m.Name).ToList())
                {
                    var info = Harmony.GetPatchInfo(method);
                    if (info == null) continue;
                    var uses = Uses(info, harmony.Id);
                    string signature = TweakChecks.Signature(method);
                    string use = string.Join(" | ", Enum.GetValues<GameUse>().Where(u => u != GameUse.Uses && (uses & u) != 0).Select(u => "GameUse." + u));
                    string print = "";
                    if ((uses & (GameUse.Transpiler | GameUse.Replaces)) != 0 && game.Find(GameMemberDeclaration.Split(signature).Type) is { } owner)
                        foreach (var handle in owner.File.Reader.GetTypeDefinition(owner.Type).GetMethods())
                            if (owner.File.Names.Method(handle) == signature) print = $", \"{Fingerprints.Of(owner.File, handle).Hash} {label}\"";
                    string rewrites = counts.TryGetValue(method, out var n) ? $", Rewrites = {n}" : (uses & GameUse.Transpiler) != 0 ? ", Rewrites = ?" : "";
                    Console.WriteLine($"    [GameMember({use}, \"{signature}\"{print}{rewrites})]");
                }
                harmony.UnpatchAll(harmony.Id);
            }
        }
    }
}
