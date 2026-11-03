using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using OniMoono.Engine;
using Cecil = Mono.Cecil;

namespace OniMoono.Host
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            WindowsConsole.AttachToParent();
            var options = Options.Parse(args);
            if (options == null) return 2;
            // For the Restarter next to the host (App.Restart): the arguments to start again with
            // (shell-quoted for the Linux script; as a JSON array with the process id for the
            // Windows one) and the process to wait for.
            Environment.SetEnvironmentVariable("ONI_MOONO_RESTART_ARGS", string.Join(" ", args.Select(a => "'" + a.Replace("'", "'\\''") + "'")));
            Environment.SetEnvironmentVariable("ONI_MOONO_RESTART_ARGV", System.Text.Json.JsonSerializer.Serialize(args));
            Environment.SetEnvironmentVariable("ONI_MOONO_RESTART_PID", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // The install's layout: OxygenNotIncluded_Data beside the game (Linux, Windows) or the
            // macOS bundle OxygenNotIncluded.app, told from the files (GameLayout).
            var layout = OniMoono.Engine.Platform.GameLayout.Find(options.GameDir);
            if (layout == null)
            {
                Console.Error.WriteLine($"Game data not found: {Path.Combine(options.GameDir, OniMoono.Engine.Platform.GameLayout.DataFolderName)} (or an OxygenNotIncluded.app there)");
                return 2;
            }
            var dataDir = layout.DataDir;

            // Code pages (IBM437 etc.), which Mono provided through I18N.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            Paths.Configure(layout, options);
            // The options the main menu's Moono Tweaks screen sets: defaults, then its settings
            // file in the per-user folder, then the command line. Read before the log opens, which
            // needs to know whether to hide personal paths from its first line; logged after.
            var settingsPath = PlayerSettings.PathIn(EngineConfig.PersistentDataPath);
            var migrated = PlayerSettings.MigrateIn(EngineConfig.PersistentDataPath, out var migrateProblem);
            var settings = PlayerSettings.Read(settingsPath, out var settingsProblem);
            var effective = EffectiveOptions.Resolve(GameTweaks.GameTweakNames.All, GameTweaks.GameTweakNames.ChangingBehaviour,
                options.GameTweaks, options.Splash, options.FrameStats, settings, options.LogPaths, options.Gpu, options.Dxgi);
            EngineLog.Open(options.LogPath ?? EngineConfig.ConsoleLogPath, keepPrevious: options.LogPath == null,
                effective.HideLogPaths ? LogScrubber.ForThisMachine() : null);
            // Which build this is (the commit the SDK stamps into the informational version) and
            // where it runs from, so a log tells which release, or which stray copy, wrote it.
            var built = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string commit = built?.IndexOf('+') is int plus and >= 0 ? built[(plus + 1)..] : "unknown";
            EngineLog.Engine($"Moono starting: commit={commit} exe={Environment.ProcessPath} game={options.GameDir} .NET={Environment.Version}");
            StartupStats.Mark("host starts (runtime up)");
            UnhandledExceptions.Install();
            if (migrated != null) EngineLog.Engine($"settings: copied {migrated} (from before the Moono name) to {settingsPath}");
            if (migrateProblem != null) EngineLog.Engine($"settings: could not copy {PlayerSettings.OldPathIn(EngineConfig.PersistentDataPath)} to {settingsPath}: {migrateProblem}");
            EngineLog.Engine(settingsProblem != null ? $"settings: {settingsPath} could not be read, so defaults apply: {settingsProblem}"
                : File.Exists(settingsPath) ? $"settings: {settingsPath}" : $"settings: {settingsPath} (none; defaults apply)");
            foreach (var line in effective.Describe()) EngineLog.Engine("option " + line);
            foreach (var note in options.ArgumentNotes) EngineLog.Engine("arguments: " + note);
            if (effective.UnknownTweaks.Count > 0) EngineLog.Engine($"settings: not tweaks of this player, ignored: {string.Join(", ", effective.UnknownTweaks)}");
            options.Splash = effective.Splash;
            options.Dxgi = effective.Dxgi;
            OniMoono.Engine.PlayerLoop.FrameStats = effective.FrameStats;
            // The GPU for laptops with two: before anything loads the OpenGL driver (the window,
            // in Player.Run), whose GPU drivers read the choice as it loads.
            foreach (var line in GpuChoice.Apply(effective.Gpu)) EngineLog.Engine(line);

            // This project's UI packages, never Unity's: without all of them the install is broken.
            var missingPackages = AssemblyPolicy.MissingPackages(AppContext.BaseDirectory);
            if (missingPackages.Count > 0)
            {
                EngineLog.Engine($"Moono's UI packages are missing from {Path.Combine(AppContext.BaseDirectory, "packages")}: {string.Join(", ", missingPackages.Select(p => p + ".dll"))}. Reinstall Moono (or rebuild the host).");
                return 2;
            }
            if (GameVersionCheck.Problem(dataDir) is string versionProblem)
            {
                GameVersionCheck.Report(versionProblem);
                return 2;
            }
            AssemblyPolicy.Install(layout, AppContext.BaseDirectory);
            if (options.CheckNatives) return NativeCheck.Run(layout, AssemblyPolicy.RuntimeAssemblyPaths(), options.NativeArch ?? AssemblyPolicy.ProcessCpu, options.NativeDir);
            var assemblies = AssemblyPolicy.PreloadScriptingAssemblies(dataDir);
            EngineConfig.BuildAssemblies = AssemblyPolicy.BuildAssemblyNames(dataDir);
            EngineLog.Engine($"Preloaded {assemblies.Count} scripting assemblies");
            StartupStats.Mark("assemblies preloaded");

            if (options.CheckTypes) return CheckTypes(assemblies);
            if (options.CheckAssets) return AssetCheck.Run(dataDir, options.ListDataScripts);
            if (options.CheckBind) return BindCheck.Run(dataDir);
            if (options.CheckShaders) return ShaderCheck.Run(dataDir);
            if (options.DumpShader != null) return ShaderDump.Run(dataDir, options.DumpShader);
            if (options.DumpScript != null) return AssetCheck.Dump(dataDir, options.DumpScript);

            if (options.TraceExceptions) ExceptionTrace.Install();
            if (options.RunHome != null) RunHome.Install(options.RunHome);
            if (options.AudioWav != null) AudioCapture.Install(options.AudioWav);
            // The patches of the game's and Harmony's own methods are made on another thread while
            // the player opens its window (no game code runs before they are all in: Player.Run
            // waits for them before the first scene). Those of the runtime's own methods, which the
            // engine itself calls meanwhile, are made after, on this thread, in the same order.
            Exception? patchFailure = null;
            var gamePatches = new Thread(() =>
            {
                try
                {
                    PatchedFilters.Install();
                    ModVersionStamp.Install();
                    MonoValueTypeHashes.Install();
                    ModAccess.Install();
                    TranspiledCalls.Install();
                    TranspiledTryEntries.Install();
                    MonoInstanceFieldInjection.Install();
                    UnboxedMethods.Install();
                    SharedGenerics.Install();
                    SharedInstantiations.Install();
                    PatchFrames.Install();
                    MonoCallingAssembly.Install();
                    PInvokePatches.Install();
                    PatchDependents.Install();
                    GenericMemberScopes.Install();
                    // Delegates' GetObjectData writes what Mono's does, which Newtonsoft.Json
                    // writes (D48). A runtime method, but one the engine never calls: patched here,
                    // off the main thread (two patches, about 20 ms).
                    MonoDelegateSerialization.Install(MonoNames.FullName, StateMachineSaveNames.AssemblyOf);
                }
                catch (Exception e)
                {
                    patchFailure = e;
                }
            }) { Name = "Host patches" };
            gamePatches.Start();
            return Player.Run(options, dataDir, () =>
            {
                gamePatches.Join();
                if (patchFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(patchFailure);
                MonoEnumFormat.Install();
                // Special folders' paths are given whether or not they exist, as Mono gives them (D24).
                MonoFolderPaths.Install();
                // Fields and events print as Mono prints them, which mods' transpilers compare (D35).
                MonoMemberStrings.Install();
                // Fields and properties are listed in Mono's order, which the game numbers state
                // machines' parameters and adds components in (D39).
                MonoReflectionOrder.Install();
                // Assembly.GetType takes '/' between nested types, as Mono's does (D44).
                MonoNestedTypeNames.Install();
                // Type.GetType in a patched method looks where Mono's does (D49).
                MonoTypeByName.Install();
                GameTweaks.GameTweaks.Install(effective.EnabledTweaks.ToHashSet(StringComparer.Ordinal));
                if (effective.FrameStats) GameTweaks.GameTweaks.InstallFrameStats();
                if (!options.NoTweaksMenu) MoonoTweaksMenu.Install(effective, settings, settingsPath);
                StateMachineSaveNames.Install(dataDir, AppContext.BaseDirectory);
                StartupStats.Mark("host patches installed");
            });
        }

        /// M0 gate: every type of every loaded assembly must load, as the game's whole-domain
        /// GetTypes() scans require.
        static int CheckTypes(List<Assembly> assemblies)
        {
            int failures = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var types = asm.GetTypes();
                    Console.WriteLine($"ok   {asm.GetName().Name,-45} {types.Length,6} types");
                }
                catch (ReflectionTypeLoadException e)
                {
                    failures++;
                    Console.WriteLine($"FAIL {asm.GetName().Name}");
                    foreach (var le in e.LoaderExceptions)
                        if (le != null) Console.WriteLine("       " + le.Message);
                }
            }
            Console.WriteLine(failures == 0 ? "All assemblies type-load." : $"{failures} assemblies failed to type-load.");

            // Where the replaced assemblies came from.
            foreach (var name in new[] { "UnityEngine.CoreModule", "OniMoono.UnityEngine", "com.rlabrecque.steamworks.net", "mscorlib" })
                Console.WriteLine($"from {name,-35} {Assembly.Load(name).Location}");

            failures += CheckRuntimeServices();
            return failures == 0 ? 0 : 1;
        }

        /// CheckRuntimeServices' patch of a value type's GetHashCode.
        static void PatchedHash(ref int __result) => __result = -1;

        /// The services Mono provided in-box, exercised through the game's own code paths
        /// (ARCHITECTURE §2.1): Newtonsoft.Json (save header), BinaryFormatter (worldgen settings
        /// cloning), code pages (Ionic.Zip).
        static int CheckRuntimeServices()
        {
            int failures = 0;
            void Check(string what, Func<string> test)
            {
                try
                {
                    Console.WriteLine($"ok   {what}: {test()}");
                }
                catch (Exception e)
                {
                    failures++;
                    Console.WriteLine($"FAIL {what}: {(e is TargetInvocationException t ? t.InnerException : e)}");
                }
            }

            Check("Newtonsoft.Json round trip", () =>
            {
                var convert = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!;
                var json = (string)convert.GetMethod("SerializeObject", [typeof(object)])!.Invoke(null, [new Dictionary<string, int> { ["cycle"] = 42 }])!;
                var back = convert.GetMethods().First(m => m.Name == "DeserializeObject" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    .MakeGenericMethod(typeof(Dictionary<string, int>)).Invoke(null, [json]);
                return ((Dictionary<string, int>)back!)["cycle"] == 42 ? json : throw new InvalidOperationException("value lost");
            });
            Check("BinaryFormatter via ObjectCloner.SerializingCloner.Copy", () =>
            {
                var cloner = Assembly.Load("Assembly-CSharp-firstpass").GetType("ObjectCloner.SerializingCloner", true)!;
                var copy = cloner.GetMethod("Copy")!.MakeGenericMethod(typeof(List<string>)).Invoke(null, [new List<string> { "a", "b" }]);
                return string.Join(",", (List<string>)copy!);
            });
            Check("Code page IBM437 (Ionic.Zip)", () => Encoding.GetEncoding("IBM437").EncodingName);
            Check("MonoMod's native helper written through a descriptor closed once (MonoModHelperDrop)", () =>
            {
                if (!OperatingSystem.IsLinux()) return "not Linux";
                // Harmony's own drop of its exception helper, into a folder of the check's: the
                // shipped one closes the descriptor twice, and the second close fails (EBADF).
                // DropLibrary takes the span type Harmony carries itself: a stub makes one of an array.
                var harmony = typeof(HarmonyLib.Harmony).Assembly;
                var drop = harmony.GetType("MonoMod.Core.Platforms.Systems.LinuxSystem+LinuxNativeLibDrop", true)!;
                var span = harmony.GetType("System.ReadOnlySpan`1", true)!.MakeGenericType(typeof(byte));
                var stub = new System.Reflection.Emit.DynamicMethod("DropLibrary", typeof(string), [typeof(Stream), typeof(byte[])], drop, skipVisibility: true);
                var il = stub.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, drop.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Newobj, span.GetConstructor([typeof(byte[])])!);
                il.Emit(System.Reflection.Emit.OpCodes.Callvirt, drop.GetMethod("DropLibrary", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var write = stub.CreateDelegate<Func<Stream, byte[], string>>();
                var folder = Directory.CreateTempSubdirectory("oni-moono-check-");
                try
                {
                    byte[] content = [0x7F, (byte)'E', (byte)'L', (byte)'F'];
                    System.Runtime.InteropServices.Marshal.SetLastSystemError(0);
                    var path = write(new MemoryStream(content), Encoding.UTF8.GetBytes(Path.Combine(folder.FullName, "helper.XXXXXX")));
                    int errno = System.Runtime.InteropServices.Marshal.GetLastSystemError();
                    if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) throw new InvalidOperationException("the file was not written");
                    return errno == 0 ? "written, its descriptor closed once" : throw new InvalidOperationException($"a close failed (errno {errno}): the descriptor was closed twice");
                }
                finally
                {
                    folder.Delete(recursive: true);
                }
            });
            Check("System.Configuration's settings classes (mods' generated Properties.Settings)", () =>
                (Type.GetType("System.Configuration.ApplicationSettingsBase, System", throwOnError: true)!).Assembly.GetName().Name!);
            Check("A Harmony patch of a value type's GetHashCode after tier-up (UnboxedMethods)", () =>
            {
                UnboxedMethods.Install();
                // The value type is emitted (dynamic code is optimized whatever this build is): an
                // int field and GetHashCode returning it, patched to return -1 for every key.
                var module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Host.Check"),
                    System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("Check");
                var builder = module.DefineType("PatchedHash", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
                var value = builder.DefineField("Value", typeof(int), FieldAttributes.Public);
                var hash = builder.DefineMethod(nameof(GetHashCode), MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
                var il = hash.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldfld, value);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                builder.DefineMethodOverride(hash, typeof(object).GetMethod(nameof(GetHashCode))!);
                var type = builder.CreateType();
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(type.GetMethod(nameof(GetHashCode)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(PatchedHash)));
                // Keys hashed by the patch at tier 0, then looked up for long enough that the
                // dictionary's methods (CoreLib's, which inline the key's GetHashCode) reach tier 1:
                // a lookup inlining the original body misses.
                var keys = new object[100];
                var dictionary = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(type, typeof(int)))!;
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i] = Activator.CreateInstance(type)!;
                    type.GetField("Value")!.SetValue(keys[i], i);
                    dictionary[keys[i]] = i;
                }
                int misses = 0;
                for (int round = 0; round < 100; round++)
                {
                    for (int i = 0; i < 10000; i++)
                        if (!dictionary.Contains(keys[i % keys.Length])) misses++;
                    System.Threading.Thread.Sleep(20);
                }
                return misses == 0 ? "no lookup missed" : throw new InvalidOperationException($"{misses} lookups missed");
            });
            Check("A transpiler leaving a value on the stack before a try block (TranspiledTryEntries)", () =>
            {
                TranspiledTryEntries.Install();
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(typeof(Program).GetMethod(nameof(Guarded), BindingFlags.NonPublic | BindingFlags.Static),
                    transpiler: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(LeaveValueBehind)));
                int doubled = Guarded(21);
                return doubled == 42 ? "the method compiles and runs as in Mono" : throw new InvalidOperationException($"returned {doubled}");
            });
            Check("A transpiler branching to a loop's head with a value left on the stack (TranspiledStackJoins)", () =>
            {
                TranspiledTryEntries.Install();
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(typeof(Program).GetMethod(nameof(SumAll), BindingFlags.NonPublic | BindingFlags.Static),
                    transpiler: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(SkipEven)));
                int sum = SumAll([1, 2, 3, 4, 5]);
                return sum == 9 ? "the method compiles and skips as in Mono" : throw new InvalidOperationException($"returned {sum}");
            });
            Check("A Harmony patch of a generic class's method for one reference type argument (SharedGenerics)", () =>
            {
                SharedGenerics.Install();
                // Emitted (dynamic code is optimized whatever this build is): Pool<T>.Draw, and a
                // caller of Pool<string>.Draw; the patch is made on Pool<object>.Draw, whose code
                // Pool<string> shares, as Better Info Cards patches HoverTextDrawer.Pool<MonoBehaviour>.
                var module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Host.SharedCheck"),
                    System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("SharedCheck");
                var pool = module.DefineType("Pool", TypeAttributes.Public | TypeAttributes.Sealed);
                var t = pool.DefineGenericParameters("T")[0];
                t.SetGenericParameterAttributes(GenericParameterAttributes.ReferenceTypeConstraint);
                var last = pool.DefineField("last", t, FieldAttributes.Public);
                pool.DefineDefaultConstructor(MethodAttributes.Public);
                var draw = pool.DefineMethod("Draw", MethodAttributes.Public | MethodAttributes.HideBySig, t, [t]);
                var il = draw.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Stfld, last);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var poolType = pool.CreateType();
                var caller = module.DefineType("Caller", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);
                var stringPool = poolType.MakeGenericType(typeof(string));
                var run = caller.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [stringPool]);
                il = run.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "x");
                il.Emit(System.Reflection.Emit.OpCodes.Call, stringPool.GetMethod("Draw")!);
                il.Emit(System.Reflection.Emit.OpCodes.Pop);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var callerType = caller.CreateType();
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(poolType.MakeGenericType(typeof(object)).GetMethod("Draw"),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountDraw)));
                var instance = Activator.CreateInstance(stringPool)!;
                var runMethod = callerType.GetMethod("Run")!;
                int missed = 0;
                for (int round = 0; round < 100; round++)
                {
                    int before = drawn;
                    for (int i = 0; i < 1000; i++) runMethod.Invoke(null, [instance]);
                    missed += 1000 - (drawn - before);
                    System.Threading.Thread.Sleep(20);
                }
                return missed == 0 ? "every call ran the patch" : throw new InvalidOperationException($"{missed} calls ran the original");
            });
            Check("Harmony patches of one instantiation of a generic class's method over reference and value types (SharedInstantiations)", () =>
            {
                SharedGenerics.Install();
                SharedInstantiations.Install();
                PatchDependents.Install(); // the copies of the instantiations not patched
                // Emitted, so that its code is optimized whatever this build is (a dispatcher typing
                // `this` as the patched class had its type check taken for granted only so):
                // Outer<A>.Param<V>.Set(V, object), which casts its object to Ctx<A, V>, as
                // StateMachine<…>.Parameter<float>.Set casts its context; Sub<A>, a Param<float>.
                // CoreCLR shares Param<string, float>'s code with Param<Version, float>'s; Mono does
                // not (a value type argument), so a patch of the first is not run for the second.
                var module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Host.InstantiationCheck"),
                    System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("InstantiationCheck");
                var ctx = module.DefineType("Ctx", TypeAttributes.Public | TypeAttributes.Sealed);
                var ctxArgs = ctx.DefineGenericParameters("A", "V");
                var ctxValue = ctx.DefineField("value", ctxArgs[1], FieldAttributes.Public);
                ctx.DefineDefaultConstructor(MethodAttributes.Public);
                var outer = module.DefineType("Outer", TypeAttributes.Public);
                outer.DefineGenericParameters("A");
                var param = outer.DefineNestedType("Param", TypeAttributes.NestedPublic);
                var args = param.DefineGenericParameters("A", "V");
                var last = param.DefineField("last", args[1], FieldAttributes.Public);
                param.DefineDefaultConstructor(MethodAttributes.Public);
                var set = param.DefineMethod("Set", MethodAttributes.Public | MethodAttributes.HideBySig, args[1], [args[1], typeof(object)]);
                var il = set.GetILGenerator();
                var ctxOfParam = ctx.MakeGenericType(args[0], args[1]);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
                il.Emit(System.Reflection.Emit.OpCodes.Castclass, ctxOfParam);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Stfld, System.Reflection.Emit.TypeBuilder.GetField(ctxOfParam, ctxValue));
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Stfld, System.Reflection.Emit.TypeBuilder.GetField(param.MakeGenericType(args[0], args[1]), last));
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var ctxType = ctx.CreateType();
                outer.CreateType();
                var paramType = param.CreateType();
                var sub = module.DefineType("Sub", TypeAttributes.Public | TypeAttributes.Sealed);
                var subBase = paramType.MakeGenericType(sub.DefineGenericParameters("A")[0], typeof(float));
                sub.SetParent(subBase);
                il = sub.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes).GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Call, System.Reflection.Emit.TypeBuilder.GetConstructor(subBase, paramType.GetConstructor(Type.EmptyTypes)!));
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var subType = sub.CreateType();
                Type Param(Type a) => paramType.MakeGenericType(a, typeof(float));
                // A compiled caller of each instantiation, called past tier-up, as D18's check calls
                // Pool<string>.Draw.
                var caller = module.DefineType("Caller", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);
                foreach (var a in new[] { typeof(string), typeof(Version), typeof(Uri) })
                {
                    il = caller.DefineMethod("Run" + a.Name, MethodAttributes.Public | MethodAttributes.Static, typeof(float), [typeof(object), typeof(object), typeof(float)]).GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Castclass, Param(a));
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                    il.Emit(System.Reflection.Emit.OpCodes.Call, Param(a).GetMethod("Set")!);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                }
                var callerType = caller.CreateType();
                Func<object, object, float, float> Run(Type a) => callerType.GetMethod("Run" + a.Name)!.CreateDelegate<Func<object, object, float, float>>();
                var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                harmony.Patch(Param(typeof(string)).GetMethod("Set"), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountStringParam)));
                object New(Type type) => Activator.CreateInstance(type)!;
                var (runString, runVersion, runUri) = (Run(typeof(string)), Run(typeof(Version)), Run(typeof(Uri)));
                var (stringParam, versionParam, uriParam, stringSub) = (New(Param(typeof(string))), New(Param(typeof(Version))), New(Param(typeof(Uri))), New(subType.MakeGenericType(typeof(string))));
                var (stringCtx, versionCtx, uriCtx) = (New(ctxType.MakeGenericType(typeof(string), typeof(float))), New(ctxType.MakeGenericType(typeof(Version), typeof(float))), New(ctxType.MakeGenericType(typeof(Uri), typeof(float))));
                int wrong = 0;
                for (int round = 0; round < 100; round++)
                {
                    if (round == 50)
                        harmony.Patch(Param(typeof(Uri)).GetMethod("Set"), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountUriParam)));
                    var (strings, uris) = (stringPatched, uriPatched);
                    for (int i = 0; i < 100; i++)
                    {
                        runString(stringParam, stringCtx, 1f);
                        runString(stringSub, stringCtx, 1f);
                        runVersion(versionParam, versionCtx, i);
                        runUri(uriParam, uriCtx, 1f);
                    }
                    if (stringPatched - strings != 200 || uriPatched - uris != (round >= 50 ? 100 : 0)) wrong++;
                    System.Threading.Thread.Sleep(20);
                }
                if (wrong > 0) throw new InvalidOperationException($"{wrong} rounds ran another instantiation's patch, or not their own");
                return (float)versionCtx.GetType().GetField("value")!.GetValue(versionCtx)! == 99f && (float)versionParam.GetType().GetField("last")!.GetValue(versionParam)! == 99f
                    ? "each instantiation runs its own patches, the others their own code" : throw new InvalidOperationException("Param<Version>.Set stored nothing");
            });
            Check("GetCallingAssembly in a helper a Harmony postfix calls (PatchFrames)", () =>
            {
                PatchFrames.Install();
                // Assembly A: the patched method. B: a helper returning GetCallingAssembly(). C: the
                // mod, whose postfix calls the helper and keeps what it returned.
                System.Reflection.Emit.ModuleBuilder Module(string name) => System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule(name);
                var a = Module("CheckTarget").DefineType("Target", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var run = a.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                var il = run.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); il.Emit(System.Reflection.Emit.OpCodes.Add); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var target = a.CreateType();
                var b = Module("CheckHelper").DefineType("Helper", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var who = b.DefineMethod("Who", MethodAttributes.Public | MethodAttributes.Static, typeof(Assembly), Type.EmptyTypes);
                il = who.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(Assembly).GetMethod(nameof(Assembly.GetCallingAssembly))!); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var helper = b.CreateType();
                var cModule = Module("CheckMod");
                var c = cModule.DefineType("Mod", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var seen = c.DefineField("Seen", typeof(Assembly), FieldAttributes.Public | FieldAttributes.Static);
                var postfix = c.DefineMethod("Postfix", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
                il = postfix.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Call, helper.GetMethod("Who")!); il.Emit(System.Reflection.Emit.OpCodes.Stsfld, seen); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var mod = c.CreateType();
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(target.GetMethod("Run"), postfix: new HarmonyLib.HarmonyMethod(mod.GetMethod("Postfix")));
                var results = new HashSet<string>();
                var runMethod = target.GetMethod("Run")!;
                for (int round = 0; round < 60; round++)
                {
                    for (int i = 0; i < 200; i++) runMethod.Invoke(null, [i]);
                    results.Add(((Assembly)mod.GetField("Seen")!.GetValue(null)!).GetName().Name!);
                    System.Threading.Thread.Sleep(20);
                }
                return results.Count == 1 && results.Contains("CheckMod") ? "the postfix's assembly calls, as in Mono" : throw new InvalidOperationException($"the caller was {string.Join(", ", results)}");
            });
            Check("The frame of a replacement whose last call is a mod's postfix (PatchFrames)", () =>
            {
                PatchFrames.Install();
                // As Cold Wheat Seed Drop's postfix of SeedProducer.CropPicked, which returns nothing:
                // the postfix throws, and the replacement's frame is in the trace, as on Mono.
                System.Reflection.Emit.ModuleBuilder Module(string name) => System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule(name);
                var a = Module("CheckTailTarget").DefineType("TailTarget", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var picked = a.DefineMethod("Picked", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(int)]);
                var il = picked.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var target = a.CreateType();
                var c = Module("CheckTailMod").DefineType("TailMod", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var postfix = c.DefineMethod("Postfix", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(int)]);
                postfix.DefineParameter(1, ParameterAttributes.None, "__0");
                il = postfix.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "check"); il.Emit(System.Reflection.Emit.OpCodes.Newobj, typeof(TypeLoadException).GetConstructor([typeof(string)])!); il.Emit(System.Reflection.Emit.OpCodes.Throw);
                var mod = c.CreateType();
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(target.GetMethod("Picked"), postfix: new HarmonyLib.HarmonyMethod(mod.GetMethod("Postfix")));
                var call = target.GetMethod("Picked")!.CreateDelegate<Action<int>>();
                try
                {
                    call(1);
                    throw new InvalidOperationException("the postfix did not throw");
                }
                catch (TypeLoadException e)
                {
                    var frames = new System.Diagnostics.StackTrace(e, false).GetFrames().Select(f => f.GetMethod()).ToList();
                    return frames.Count >= 2 && frames[0]?.Name == "Postfix" && frames[1] is System.Reflection.Emit.DynamicMethod { Name: var name } && name.Contains("Picked")
                        ? "the replacement's frame is under the postfix's, as on Mono" : throw new InvalidOperationException($"frames: {string.Join(", ", frames.Select(m => m?.Name ?? "?"))}");
                }
            });
            Check("Frames a mod's postfix of a struct's constructor sees (PatchFrames)", () =>
            {
                PatchFrames.Install();
                // As No Minimum Temperature's postfix of ElementConverter.OutputElement's constructor,
                // which reads new StackTrace(2).GetFrame(0) for the building config that made it.
                System.Reflection.Emit.ModuleBuilder Module(string name) => System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule(name);
                var targetModule = Module("CheckDepthTarget");
                var elem = targetModule.DefineType("Elem", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
                var rate = elem.DefineField("Rate", typeof(float), FieldAttributes.Public);
                var ctor = elem.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(float), typeof(int)]);
                var il = ctor.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Stfld, rate); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var elemType = elem.CreateType();
                var config = targetModule.DefineType("CheckConfig", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);
                var configure = config.DefineMethod("ConfigureBuildingTemplate", MethodAttributes.Public | MethodAttributes.Static, elemType.MakeArrayType(), Type.EmptyTypes);
                il = configure.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); il.Emit(System.Reflection.Emit.OpCodes.Newarr, elemType);
                il.Emit(System.Reflection.Emit.OpCodes.Dup); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldc_R4, 0.5f); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_3);
                il.Emit(System.Reflection.Emit.OpCodes.Newobj, elemType.GetConstructor([typeof(float), typeof(int)])!);
                il.Emit(System.Reflection.Emit.OpCodes.Stelem, elemType); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var configType = config.CreateType();
                var modType = Module("CheckDepthMod").DefineType("DepthMod", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var seen = modType.DefineField("Seen", typeof(string), FieldAttributes.Public | FieldAttributes.Static);
                var postfix = modType.DefineMethod("Postfix", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [elemType.MakeByRefType()]);
                postfix.DefineParameter(1, ParameterAttributes.None, "__instance");
                il = postfix.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_2); il.Emit(System.Reflection.Emit.OpCodes.Newobj, typeof(System.Diagnostics.StackTrace).GetConstructor([typeof(int)])!);
                il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0); il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(System.Diagnostics.StackTrace).GetMethod(nameof(System.Diagnostics.StackTrace.GetFrame))!);
                il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(MonoStackFrames).GetMethod(nameof(MonoStackFrames.GetMethod))!);
                il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(MemberInfo).GetProperty(nameof(MemberInfo.Name))!.GetMethod!);
                il.Emit(System.Reflection.Emit.OpCodes.Stsfld, seen); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var mod = modType.CreateType();
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(elemType.GetConstructor([typeof(float), typeof(int)]), postfix: new HarmonyLib.HarmonyMethod(mod.GetMethod("Postfix")));
                var configureMethod = configType.GetMethod("ConfigureBuildingTemplate")!;
                var results = new HashSet<string>();
                for (int round = 0; round < 40; round++)
                {
                    for (int i = 0; i < 100; i++) configureMethod.Invoke(null, null);
                    results.Add((string?)mod.GetField("Seen")!.GetValue(null) ?? "null");
                    System.Threading.Thread.Sleep(20);
                }
                return results.Count == 1 && results.Contains("ConfigureBuildingTemplate") ? "two frames up is the constructor's caller, as on Mono" : throw new InvalidOperationException($"two frames up: {string.Join(", ", results)}");
            });
            Check("A transpiler's closure call in a method a mod prefixes (PatchFrames)", () =>
            {
                PatchFrames.Install();
                // As issue #32: Blueprints Expanded prefixes SelectToolHoverTextCard.UpdateHoverElements,
                // then Better Info Cards' transpiler of it adds a CodeInstruction.CallClosure call,
                // of a dynamic method. The patch failed with InvalidOperationException.
                System.Reflection.Emit.ModuleBuilder Module(string name) => System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule(name);
                var t = Module("CheckClosureTarget").DefineType("ClosureTarget", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var value = t.DefineMethod("Value", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                var il = value.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var target = t.CreateType();
                var m = Module("CheckClosureMod").DefineType("ClosureMod", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                il = m.DefineMethod("Prefix", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes).GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var mod = m.CreateType();
                var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                harmony.Patch(target.GetMethod("Value"), prefix: new HarmonyLib.HarmonyMethod(mod.GetMethod("Prefix")));
                harmony.Patch(target.GetMethod("Value"), transpiler: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(ClosureTranspiler)));
                int result = target.GetMethod("Value")!.CreateDelegate<Func<int, int>>()(1);
                return result == 42 ? "the closure's call is patched in, as on Mono" : throw new InvalidOperationException($"Value(1) returned {result}");
            });
            Check("A prefix of a static method injecting an instance field (MonoInstanceFieldInjection)", () =>
            {
                MonoInstanceFieldInjection.Install();
                // As issue #31: Fast Track's prefix of the static IdleStates.MoveToNewCell(Instance
                // smi) takes ___loop, IdleStates' own field; Harmony reads it from smi. The original
                // player got null; here it got another field of smi.
                System.Reflection.Emit.ModuleBuilder Module(string name) => System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule(name);
                var module = Module("CheckFieldTarget");
                var smi = module.DefineType("Smi", TypeAttributes.Public);
                smi.DefineField("Stack", typeof(object), FieldAttributes.Public);
                smi.DefineField("Other", typeof(object), FieldAttributes.Public);
                smi.DefineDefaultConstructor(MethodAttributes.Public);
                var smiType = smi.CreateType();
                var states = module.DefineType("States", TypeAttributes.Public);
                states.DefineField("Loop", typeof(string), FieldAttributes.Public);
                states.DefineField("Count", typeof(int), FieldAttributes.Public);
                var move = states.DefineMethod("Move", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [smiType]);
                move.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
                var own = states.DefineMethod("Own", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [states]);
                own.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
                states.DefineDefaultConstructor(MethodAttributes.Public);
                var statesType = states.CreateType();
                var m = Module("CheckFieldMod").DefineType("FieldMod", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var seenLoop = m.DefineField("SeenLoop", typeof(string), FieldAttributes.Public | FieldAttributes.Static);
                var seenCount = m.DefineField("SeenCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
                var prefix = m.DefineMethod("Prefix", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(string), typeof(int)]);
                prefix.DefineParameter(1, ParameterAttributes.None, "___Loop");
                prefix.DefineParameter(2, ParameterAttributes.None, "___Count");
                var il = prefix.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Stsfld, seenLoop);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Stsfld, seenCount);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var mod = m.CreateType();
                var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                harmony.Patch(statesType.GetMethod("Move"), prefix: new HarmonyLib.HarmonyMethod(mod.GetMethod("Prefix")));
                harmony.Patch(statesType.GetMethod("Own"), prefix: new HarmonyLib.HarmonyMethod(mod.GetMethod("Prefix")));
                var instance = Activator.CreateInstance(smiType)!;
                smiType.GetField("Stack")!.SetValue(instance, new Stack<int>());
                smiType.GetField("Other")!.SetValue(instance, 7);
                mod.GetField("SeenLoop")!.SetValue(null, "unset");
                statesType.GetMethod("Move")!.Invoke(null, [instance]);
                var fromOther = ((string?)mod.GetField("SeenLoop")!.GetValue(null), (int)mod.GetField("SeenCount")!.GetValue(null)!);
                var states2 = Activator.CreateInstance(statesType)!;
                statesType.GetField("Loop")!.SetValue(states2, "loop");
                statesType.GetField("Count")!.SetValue(states2, 3);
                statesType.GetMethod("Own")!.Invoke(null, [states2]);
                var fromOwn = ((string?)mod.GetField("SeenLoop")!.GetValue(null), (int)mod.GetField("SeenCount")!.GetValue(null)!);
                return fromOther == (null, 0) && fromOwn == ("loop", 3)
                    ? "another type's argument gives the defaults, as the original player's read gave null; the field's own type is read"
                    : throw new InvalidOperationException($"from Smi: {fromOther}, from States: {fromOwn}");
            });
            Check("Harmony prefixes on P/Invokes, one whose export is missing (PInvokePatches)", () =>
            {
                PInvokePatches.Install();
                var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                harmony.Patch(typeof(Program).GetMethod(nameof(MissingExport), BindingFlags.NonPublic | BindingFlags.Static),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(SkipMissing)));
                // A caller compiled after the patch and optimized, as game and mod code is (a debug
                // build's own methods are not): the JIT would inline the P/Invoke's transition past
                // the detour. This lambda was compiled before the patch: it does not call them.
                var callMissing = Caller<Action>(nameof(MissingExport));
                for (int i = 0; i < 1000; i++) callMissing();
                if (skipped != 1000) throw new InvalidOperationException($"skipped {skipped} of 1000 calls");
                if (!OperatingSystem.IsLinux()) return "the prefix runs from optimized callers";
                // An export that exists (libc's): optimized callers would call it past the detour.
                harmony.Patch(typeof(Program).GetMethod(nameof(SkippedExport), BindingFlags.NonPublic | BindingFlags.Static),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(SkipExport)));
                var callSkipped = Caller<Action>(nameof(SkippedExport));
                for (int i = 0; i < 1000; i++) callSkipped();
                // A P/Invoke never called before still binds to its own export.
                int pid = OtherExport();
                return skippedExport == 1000 && pid == Environment.ProcessId ? "the prefixes run from optimized callers; other P/Invokes bind as before" : throw new InvalidOperationException($"skipped {skippedExport} of 1000 calls to an export, getpid {pid}");
            });
            Check("Harmony patches of methods with a filter and a fault (PatchedFilters)", () =>
            {
                PatchedFilters.Install();
                PatchFrames.Install();
                // As LoadScreen.MigrateFile's two `catch … when`. MonoMod built such a replacement
                // with a MethodBuilder, whose code calls AssemblyBuilder.Save, missing from CoreCLR:
                // the patch threw MissingMethodException. Harmony 2.4.2 cannot copy a filter clause
                // on either runtime, so the patch throws InvalidOperationException, as on Mono.
                var type = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CheckFilters"), System.Reflection.Emit.AssemblyBuilderAccess.Run)
                    .DefineDynamicModule("CheckFilters").DefineType("Filtered", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var filters = type.DefineField("Filters", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
                // static int Guarded(Exception e) { try { throw e; } catch when (++Filters > 0 && e is InvalidOperationException) { return 7; } }
                var guarded = type.DefineMethod("Guarded", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(Exception)]);
                var il = guarded.GetILGenerator();
                var result = il.DeclareLocal(typeof(int));
                il.BeginExceptionBlock();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Throw);
                il.BeginExceptFilterBlock();
                il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, filters); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); il.Emit(System.Reflection.Emit.OpCodes.Add); il.Emit(System.Reflection.Emit.OpCodes.Stsfld, filters);
                il.Emit(System.Reflection.Emit.OpCodes.Isinst, typeof(InvalidOperationException)); il.Emit(System.Reflection.Emit.OpCodes.Ldnull); il.Emit(System.Reflection.Emit.OpCodes.Cgt_Un);
                il.BeginCatchBlock(null);
                il.Emit(System.Reflection.Emit.OpCodes.Pop); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_7); il.Emit(System.Reflection.Emit.OpCodes.Stloc, result);
                il.EndExceptionBlock();
                il.Emit(System.Reflection.Emit.OpCodes.Ldloc, result); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                // static void Faulted(int[] a) { try { throw new InvalidOperationException(); } fault { a[0] = 5; } }
                var faulted = type.DefineMethod("Faulted", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(int[])]);
                il = faulted.GetILGenerator();
                il.BeginExceptionBlock();
                il.Emit(System.Reflection.Emit.OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!); il.Emit(System.Reflection.Emit.OpCodes.Throw);
                il.BeginFaultBlock();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_5); il.Emit(System.Reflection.Emit.OpCodes.Stelem_I4);
                il.EndExceptionBlock();
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var filtered = type.CreateType();
                var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                try
                {
                    harmony.Patch(filtered.GetMethod("Guarded"), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountFilterPrefix)), postfix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(AddFilterPostfix)));
                    throw new ArgumentException("the filter's method was patched");
                }
                catch (InvalidOperationException e) when (e.Message == "Incorrect code generation for exception block.")
                {
                }
                // The method still runs unpatched: its filter accepts and rejects, no prefix or postfix.
                var guard = filtered.GetMethod("Guarded")!.CreateDelegate<Func<Exception, int>>();
                int Filters() => (int)filtered.GetField("Filters")!.GetValue(null)!;
                int accepted = guard(new InvalidOperationException());
                bool rejected = false;
                try { guard(new ArgumentException("check")); }
                catch (ArgumentException e) when (e.Message == "check") { rejected = true; }
                if (accepted != 7 || !rejected || Filters() != 2 || filterPrefixed != 0 || filterPostfixed != 0)
                    throw new InvalidOperationException($"after the failed patch: returned {accepted}, rejected {rejected}, filter ran {Filters()}, prefix {filterPrefixed}, postfix {filterPostfixed}");
                // A fault: Harmony's FaultBlockRewriter, run on every replacement on both runtimes,
                // turns it into a catch and a finally and drops the fault block's code. The prefix
                // runs, and the exception leaves the replacement past the postfix.
                harmony.Patch(filtered.GetMethod("Faulted"), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountFilterPrefix)), postfix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountFaultPostfix)));
                var fault = filtered.GetMethod("Faulted")!.CreateDelegate<Action<int[]>>();
                var array = new int[1];
                try
                {
                    fault(array);
                    throw new ArgumentException("the fault's exception was caught");
                }
                catch (InvalidOperationException)
                {
                    if (filterPrefixed != 1 || filterPostfixed != 0 || array[0] != 0)
                        throw new InvalidOperationException($"fault: prefix {filterPrefixed}, postfix {filterPostfixed}, a[0] {array[0]}");
                }
                return "the filter's patch throws InvalidOperationException and the method runs unpatched; the fault's patch runs its prefix, its block dropped; as on Mono";
            });
            Check("A Harmony prefix on a method an earlier patch's replacement calls (PatchDependents)", () =>
            {
                PatchDependents.Install();
                // B: a small static method; A calls it. A is patched first (its replacement is compiled
                // optimized), then B gets a prefix, as True Tiles patches GetConnectionBits and then
                // MatchesDef.
                var module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Host.LaterCheck"),
                    System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("LaterCheck");
                var type = module.DefineType("Later", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var b = type.DefineMethod("B", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                var il = b.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_2); il.Emit(System.Reflection.Emit.OpCodes.Mul); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var a = type.DefineMethod("A", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                il = a.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Call, b); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); il.Emit(System.Reflection.Emit.OpCodes.Add); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var later = type.CreateType();
                var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                harmony.Patch(later.GetMethod("A"), postfix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountA)));
                harmony.Patch(later.GetMethod("B"), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountB)));
                var run = later.GetMethod("A")!;
                int before = prefixedB;
                for (int i = 0; i < 100; i++) run.Invoke(null, [i]);
                return prefixedB - before == 100 ? "the later prefix runs from the earlier replacement" : throw new InvalidOperationException($"B's prefix ran {prefixedB - before} of 100 calls");
            });
            Check("A Harmony prefix on a method an earlier replacement reaches through an inlined one (PatchDependents)", () =>
            {
                PatchDependents.Install();
                // A calls B, which calls C: static, and B small, so A's replacement inlines B and,
                // through it, C. A is patched first, then C gets a prefix, as Forever Eruption Geyser
                // patches Geyser.GetEmitRate and then GetIterationPercent, which GetOnDuration calls.
                // C has 4 bytes of IL, then the most the JIT inlines (1024), then 2 more, which the
                // JIT must call (no replacement is rebuilt for it): the bound PatchDependents assumes.
                var ran = new List<string>();
                foreach (var size in new[] { 4, 1024, 1026 })
                {
                    var module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"OniMoono.Host.NestedCheck{size}"),
                        System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("NestedCheck");
                    var type = module.DefineType("Nested", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                    var c = type.DefineMethod("C", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                    var il = c.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    for (int i = 0; i < (size - 2) / 2; i++)
                    {
                        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_3);
                        il.Emit(i % 2 == 0 ? System.Reflection.Emit.OpCodes.Mul : System.Reflection.Emit.OpCodes.Xor);
                    }
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var b = type.DefineMethod("B", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                    il = b.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Call, c); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_2); il.Emit(System.Reflection.Emit.OpCodes.Add); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var a = type.DefineMethod("A", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                    il = a.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Call, b); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); il.Emit(System.Reflection.Emit.OpCodes.Add); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var nested = type.CreateType();
                    int length = nested.GetMethod("C")!.GetMethodBody()!.GetILAsByteArray()!.Length;
                    if (length != size) throw new InvalidOperationException($"C has {length} bytes of IL, not {size}");
                    var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                    harmony.Patch(nested.GetMethod("A"), postfix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountA)));
                    harmony.Patch(nested.GetMethod("C"), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountC)));
                    var run = nested.GetMethod("A")!;
                    int before = prefixedC;
                    for (int i = 0; i < 100; i++) run.Invoke(null, [i]);
                    if (prefixedC - before != 100) throw new InvalidOperationException($"with {size} bytes of IL in C, its prefix ran {prefixedC - before} of 100 calls");
                    ran.Add(size.ToString());
                }
                return $"the later prefix runs from the earlier replacement (C of {string.Join(", ", ran)} bytes of IL)";
            });
            Check("Harmony prefixes on methods code compiled before had inlined (PatchInliners)", () =>
            {
                PatchDependents.Install();
                if (!PatchInliners.Available) return "not available with this JIT hook";
                // Twice is a small static method, Critter.Update a sealed class's override of a virtual
                // method. Loop calls Twice; Herd calls Update on a Critter, which the JIT devirtualizes,
                // through Visit, a small method compiled only inlined. Both loops are compiled optimized
                // at once (as tier 1 compiles a hot loop) and run before the prefixes are added, as a
                // prefix on Brain.UpdateBrain went on in a colony whose brain loop had tiered up.
                var module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Host.InlinedCheck"),
                    System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("InlinedCheck");
                var brain = module.DefineType("Brain", TypeAttributes.Public | TypeAttributes.Class);
                var update = brain.DefineMethod("Update", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot, typeof(int), [typeof(int)]);
                var il = update.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                brain.DefineDefaultConstructor(MethodAttributes.Public);
                var brainType = brain.CreateType();
                var critter = module.DefineType("Critter", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, brainType);
                var critterUpdate = critter.DefineMethod("Update", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, typeof(int), [typeof(int)]);
                il = critterUpdate.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_2); il.Emit(System.Reflection.Emit.OpCodes.Add); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                critter.DefineDefaultConstructor(MethodAttributes.Public);
                var critterType = critter.CreateType();
                var type = module.DefineType("Inlined", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var twice = type.DefineMethod("Twice", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                il = twice.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_2); il.Emit(System.Reflection.Emit.OpCodes.Mul); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var visit = type.DefineMethod("Visit", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [critterType, typeof(int)]);
                il = visit.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Callvirt, critterType.GetMethod("Update")!); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                // static int Loop(int n) { int s = 0; for (int i = 0; i < n; i++) s += Twice(i); return s; }
                // static int Herd(Critter c, int n) { int s = 0; for (int i = 0; i < n; i++) s += Visit(c, i); return s; }
                foreach (var (name, callee, critterArgument) in new[] { ("Loop", (MethodInfo)twice, false), ("Herd", visit, true) })
                {
                    var loop = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(int), critterArgument ? [critterType, typeof(int)] : [typeof(int)]);
                    loop.SetImplementationFlags(MethodImplAttributes.AggressiveOptimization);
                    il = loop.GetILGenerator();
                    var sum = il.DeclareLocal(typeof(int));
                    var i = il.DeclareLocal(typeof(int));
                    var test = il.DefineLabel();
                    var body = il.DefineLabel();
                    il.Emit(System.Reflection.Emit.OpCodes.Br, test);
                    il.MarkLabel(body);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldloc, sum);
                    if (critterArgument) il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldloc, i);
                    il.Emit(System.Reflection.Emit.OpCodes.Call, callee);
                    il.Emit(System.Reflection.Emit.OpCodes.Add); il.Emit(System.Reflection.Emit.OpCodes.Stloc, sum);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldloc, i); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); il.Emit(System.Reflection.Emit.OpCodes.Add); il.Emit(System.Reflection.Emit.OpCodes.Stloc, i);
                    il.MarkLabel(test);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldloc, i);
                    il.Emit(critterArgument ? System.Reflection.Emit.OpCodes.Ldarg_1 : System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Blt, body);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldloc, sum); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                }
                var inlined = type.CreateType();
                var herd = inlined.GetMethod("Herd")!;
                var loopMethod = inlined.GetMethod("Loop")!;
                var aCritter = Activator.CreateInstance(critterType)!;
                int Run() => (int)loopMethod.Invoke(null, [100])! + (int)herd.Invoke(null, [aCritter, 100])!;
                int expected = Run();
                var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                harmony.Patch(inlined.GetMethod("Twice"), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountB)));
                harmony.Patch(critterType.GetMethod("Update"), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountC)));
                int b = prefixedB, c = prefixedC;
                if (Run() != expected) throw new InvalidOperationException("the copies compute another result");
                if (prefixedB - b != 100 || prefixedC - c != 100)
                    throw new InvalidOperationException($"Twice's prefix ran {prefixedB - b} of 100 calls, Critter.Update's {prefixedC - c} of 100");
                // The copy of Loop is replaced when Loop is patched itself, and calls Twice's patch.
                harmony.Patch(loopMethod, prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountA)));
                int a = countedA;
                b = prefixedB;
                Run();
                return countedA - a == 1 && prefixedB - b == 100 ? "the prefixes run from both loops, compiled before with the methods inlined; a later patch of a loop runs too"
                    : throw new InvalidOperationException($"after Loop's own patch: its prefix ran {countedA - a} of 1 calls, Twice's {prefixedB - b} of 100");
            });
            Check("A Harmony prefix on a generic method over a reference type, from code compiled after it (UnboxedMethods)", () =>
            {
                UnboxedMethods.Install();
                PatchDependents.Install();
                // Pick<T>, a small static generic method constrained to a class, is patched for one
                // class, as Fast Track patches InterfaceTool.GetObjectUnderCursor<KSelectable>
                // (T : MonoBehaviour). Caller calls Pick<Selectable> and is compiled optimized only
                // after the patch, as tier 1 recompiled Fast Track's click prefix that calls it
                // (issue #48). Without the shared code marked, the prefix never runs from Caller.
                var module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Host.GenericInlineCheck"),
                    System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("GenericInlineCheck");
                var selectable = module.DefineType("Selectable", TypeAttributes.Public | TypeAttributes.Class);
                selectable.DefineDefaultConstructor(MethodAttributes.Public);
                var selectableType = selectable.CreateType();
                var type = module.DefineType("Generic", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var pick = type.DefineMethod("Pick", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                pick.DefineGenericParameters("T")[0].SetBaseTypeConstraint(selectableType);
                var il = pick.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_3); il.Emit(System.Reflection.Emit.OpCodes.Mul); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var caller = type.DefineMethod("Caller", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                caller.SetImplementationFlags(MethodImplAttributes.AggressiveOptimization);
                il = caller.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Call, pick.MakeGenericMethod(selectableType));
                il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); il.Emit(System.Reflection.Emit.OpCodes.Add); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var generic = type.CreateType();
                var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                harmony.Patch(generic.GetMethod("Pick")!.MakeGenericMethod(selectableType), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountG)));
                var run = generic.GetMethod("Caller")!;
                int before = prefixedG;
                for (int i = 0; i < 100; i++) run.Invoke(null, [i]);
                return prefixedG - before == 100 ? "the prefix runs from code compiled after the patch"
                    : throw new InvalidOperationException($"Pick<Selectable>'s prefix ran {prefixedG - before} of 100 calls");
            });
            Check("A Harmony prefix on a generic method over a reference type, after its shared code tiers up (UnboxedMethods)", () =>
            {
                UnboxedMethods.Install();
                // Pick<T> is patched for one class, as Fast Track patches
                // InterfaceTool.GetObjectUnderCursor<KSelectable>, then called from tier-0 code until
                // tiered compilation recompiles its shared code (Pick<__Canon>), as Fast Track's click
                // prefix did some 40 clicks into a session (issue #48). MonoMod moves a detour onto a
                // method's new code by the method compiled, and without the host the shared code's
                // compile found none: the prefix stopped running.
                var module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Host.GenericTierCheck"),
                    System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("GenericTierCheck");
                var selectable = module.DefineType("Selectable", TypeAttributes.Public | TypeAttributes.Class);
                selectable.DefineDefaultConstructor(MethodAttributes.Public);
                var selectableType = selectable.CreateType();
                var type = module.DefineType("Generic", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var pick = type.DefineMethod("Pick", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                pick.DefineGenericParameters("T")[0].SetBaseTypeConstraint(selectableType);
                var il = pick.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_3); il.Emit(System.Reflection.Emit.OpCodes.Mul); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var caller = type.DefineMethod("Caller", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                il = caller.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Call, pick.MakeGenericMethod(selectableType));
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var generic = type.CreateType();
                var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                harmony.Patch(generic.GetMethod("Pick")!.MakeGenericMethod(selectableType), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountG)));
                var run = generic.GetMethod("Caller")!;
                // Tier 1 compiles a method after 30 calls once a 100 ms delay without new tier-0
                // compiles has passed, in the background: 2 s of calls covers it on a loaded machine.
                int recompiles = UnboxedMethods.recompiles;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < 2)
                {
                    for (int i = 0; i < 50; i++) run.Invoke(null, [i]);
                    Thread.Sleep(20);
                }
                int before = prefixedG;
                for (int i = 0; i < 100; i++) run.Invoke(null, [i]);
                return prefixedG - before == 100 ? $"the prefix runs after the shared code was recompiled ({UnboxedMethods.recompiles - recompiles} time(s))"
                    : throw new InvalidOperationException($"after the shared code was recompiled, Pick<Selectable>'s prefix ran {prefixedG - before} of 100 calls");
            });
            Check("Harmony patches of two copies of a library naming a generic type over their own types (GenericMemberScopes)", () =>
            {
                GenericMemberScopes.Install();
                // Two assemblies, each with a copy of the same library, as mods merge PLib: an
                // interface Library.IEntry, and Library.Dialog, whose Fill enumerates an
                // ICollection<IEntry>, Make constructs a List<IEntry> and Rows names a List<IEntry>[].
                // Both copies' methods get a postfix, as Advanced Mopping by Type and Ultimate Power
                // Tweaks patch their PLib's OptionsDialog.FillModOptions.
                var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                var entries = new Type[2];
                var dialogs = new Type[2];
                var held = new List<object>();
                for (int copy = 0; copy < 2; copy++)
                {
                    var module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Host.ScopeCheck" + copy),
                        System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("ScopeCheck" + copy);
                    var entry = module.DefineType("Library.IEntry", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract).CreateType();
                    var collection = typeof(ICollection<>).MakeGenericType(entry);
                    var enumerable = typeof(IEnumerable<>).MakeGenericType(entry);
                    var list = typeof(List<>).MakeGenericType(entry);
                    var getEnumerator = enumerable.GetMethod("GetEnumerator")!;
                    var construct = list.GetConstructor(Type.EmptyTypes)!;
                    // MonoMod's cache holds what it resolves weakly: kept alive, as a game holds them.
                    held.Add(getEnumerator);
                    held.Add(construct);
                    var dialog = module.DefineType("Library.Dialog", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                    var fill = dialog.DefineMethod("Fill", MethodAttributes.Public | MethodAttributes.Static, typeof(bool), [collection]);
                    var il = fill.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Callvirt, getEnumerator);
                    il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var make = dialog.DefineMethod("Make", MethodAttributes.Public | MethodAttributes.Static, typeof(object), Type.EmptyTypes);
                    il = make.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Newobj, construct);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var rows = dialog.DefineMethod("Rows", MethodAttributes.Public | MethodAttributes.Static, typeof(object), Type.EmptyTypes);
                    il = rows.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldtoken, list.MakeArrayType());
                    il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    entries[copy] = entry;
                    dialogs[copy] = dialog.CreateType();
                    foreach (var name in new[] { "Fill", "Make", "Rows" })
                        harmony.Patch(dialogs[copy].GetMethod(name), postfix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(NoOp)));
                }
                var wrong = new List<string>();
                for (int copy = 0; copy < 2; copy++)
                {
                    var list = typeof(List<>).MakeGenericType(entries[copy]);
                    try
                    {
                        dialogs[copy].GetMethod("Fill")!.Invoke(null, [Activator.CreateInstance(list)]);
                    }
                    catch (TargetInvocationException e)
                    {
                        wrong.Add($"copy {copy}'s Fill: {e.InnerException?.GetType().Name}: {e.InnerException?.Message}");
                    }
                    string Of(Type type) => type.GetGenericArguments()[0].Assembly.GetName().Name!;
                    var made = dialogs[copy].GetMethod("Make")!.Invoke(null, null)!.GetType();
                    if (made != list) wrong.Add($"copy {copy}'s Make made a List<IEntry> of {Of(made)}");
                    var array = (Type)dialogs[copy].GetMethod("Rows")!.Invoke(null, null)!;
                    if (array != list.MakeArrayType()) wrong.Add($"copy {copy}'s Rows named a List<IEntry>[] of {Of(array.GetElementType()!)}");
                }
                if (wrong.Count > 0) throw new InvalidOperationException(string.Join("; ", wrong));
                GC.KeepAlive(held);
                return "each copy's replacements enumerate, construct and name its own List<IEntry>";
            });
            Check("Exceptions nothing catches on other threads (UnhandledExceptions)", () =>
            {
                UnhandledExceptions.Install();
                int raised = 0;
                UnhandledExceptionEventHandler count = (_, args) =>
                {
                    if (args.ExceptionObject is InvalidOperationException { Message: "check" }) Interlocked.Increment(ref raised);
                };
                AppDomain.CurrentDomain.UnhandledException += count;
                try
                {
                    var thread = new Thread(() => throw new InvalidOperationException("check"));
                    thread.Start();
                    thread.Join();
                    ThreadPool.QueueUserWorkItem(_ => throw new InvalidOperationException("check"));
                    Thread.Sleep(500);
                }
                finally
                {
                    AppDomain.CurrentDomain.UnhandledException -= count;
                }
                return raised == 1 ? "the process goes on; the thread's raised UnhandledException, the pool's did not" : throw new InvalidOperationException($"UnhandledException raised {raised} times");
            });
            // Waits until the thread blocks (or 5 s): an abort before it would land in its next wait.
            static void Blocked(Thread thread)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while ((thread.ThreadState & ThreadState.WaitSleepJoin) == 0 && watch.ElapsedMilliseconds < 5000) Thread.Sleep(5);
                Thread.Sleep(50);
            }
            Check("Thread.Abort of a thread waiting in Sleep, a wait handle, Monitor.Wait, Join, a task and an HTTP request (MonoThreadAbort)", () =>
            {
                UnhandledExceptions.Install();
                int raised = 0;
                UnhandledExceptionEventHandler count = (_, _) => Interlocked.Increment(ref raised);
                AppDomain.CurrentDomain.UnhandledException += count;
                var never = new ManualResetEvent(false);
                var gate = new object();
                // A server that takes connections and never answers, as a proxy that has stopped.
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                var accepted = new List<System.Net.Sockets.Socket>();
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    while (true) { var socket = await listener.AcceptSocketAsync(); lock (accepted) accepted.Add(socket); }
                });
                var http = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler { UseProxy = false });
                var blocker = new Thread(() => never.WaitOne()) { IsBackground = true };
                blocker.Start();
                var waits = new (string, ThreadStart)[]
                {
                    ("Sleep", () => Thread.Sleep(Timeout.Infinite)),
                    ("WaitOne", () => never.WaitOne()),
                    ("Monitor.Wait", () => { lock (gate) Monitor.Wait(gate); }),
                    ("Join", () => blocker.Join()),
                    ("Task.Result", () => _ = new System.Threading.Tasks.TaskCompletionSource<int>().Task.Result),
                    ("an HTTP request's Result", () => _ = http.GetAsync($"http://127.0.0.1:{port}/").Result),
                };
                var times = new List<string>();
                try
                {
                    foreach (var (name, wait) in waits)
                    {
                        var thread = new Thread(wait) { IsBackground = true };
                        thread.Start();
                        Blocked(thread);
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        MonoThreadAbort.Abort(thread);
                        long returned = watch.ElapsedMilliseconds;
                        if (!thread.Join(5000)) throw new InvalidOperationException($"the thread waiting in {name} still runs 5 s after the abort");
                        times.Add($"{name} {watch.ElapsedMilliseconds} ms (the call {returned} ms)");
                    }
                    // A system call is not a wait CoreCLR interrupts: a socket's read goes on until
                    // the socket closes (Mono's abort interrupts the call). Measured, not required.
                    var client = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                    client.Connect(System.Net.IPAddress.Loopback, port);
                    int read = -1;
                    var reader = new Thread(() =>
                    {
                        try { read = client.Receive(new byte[16]); }
                        catch (System.Net.Sockets.SocketException) { read = -2; }
                    }) { IsBackground = true };
                    reader.Start();
                    Blocked(reader);
                    MonoThreadAbort.Abort(reader);
                    bool stillReading = !reader.Join(1000);
                    lock (accepted) foreach (var socket in accepted) socket.Close();
                    if (!reader.Join(5000)) throw new InvalidOperationException("the socket's reader did not end when its socket closed");
                    times.Add(stillReading ? $"a socket's Receive goes on until the socket closes (then {read})" : "a socket's Receive ended at the abort");
                    client.Close();
                }
                finally
                {
                    never.Set();
                    listener.Stop();
                    http.Dispose();
                    AppDomain.CurrentDomain.UnhandledException -= count;
                }
                if (raised != 0) throw new InvalidOperationException($"UnhandledException raised {raised} times");
                return "the caller goes on and each thread ends quietly: " + string.Join("; ", times);
            });
            Check("Thread.Abort of the current thread, of one that has ended and of one not started (MonoThreadAbort)", () =>
            {
                string? self = null;
                bool reset = false;
                var thread = new Thread(() =>
                {
                    try
                    {
                        MonoThreadAbort.Abort(Thread.CurrentThread);
                        self = "the call returned";
                    }
                    catch (ThreadAbortException e)
                    {
                        self = e.Message;
                        MonoThreadAbort.ResetAbort();
                        reset = true;
                    }
                });
                thread.Start();
                thread.Join();
                if (self != "Thread was being aborted" || !reset) throw new InvalidOperationException($"the current thread's abort: {self}, reset {reset}");
                MonoThreadAbort.Abort(thread);
                // Mono marks a thread not started as aborted, and its Start then does not run it;
                // here its first wait is interrupted.
                string? first = null;
                var unstarted = new Thread(() =>
                {
                    try { Thread.Sleep(10); first = "slept"; }
                    catch (ThreadInterruptedException) { first = "interrupted"; }
                });
                MonoThreadAbort.Abort(unstarted);
                unstarted.Start();
                if (!unstarted.Join(5000)) throw new InvalidOperationException("the thread aborted before it started did not end");
                try
                {
                    MonoThreadAbort.ResetAbort();
                    throw new InvalidOperationException("ResetAbort without an abort did not throw");
                }
                catch (ThreadStateException)
                {
                }
                return $"the current thread's throws at the call (\"{self}\", ResetAbort takes it back), an ended thread's does nothing, a thread aborted before its start: its first wait {first}";
            });
            Check("A mod's retry loop and ResetAbort when its thread is aborted (MonoThreadAbort)", () =>
            {
                UnhandledExceptions.Install();
                ModAccess.Install();
                int raised = 0;
                UnhandledExceptionEventHandler count = (_, _) => Interlocked.Increment(ref raised);
                AppDomain.CurrentDomain.UnhandledException += count;
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    // ThreadedHttps.Send's loop: a catch of Exception that goes back to try again.
                    var mod = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckAborts"), typeof(object).Assembly);
                    var type = mod.DefineDynamicModule("OniMoonoCheckAborts").DefineType("Aborts", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                    var gateField = type.DefineField("Gate", typeof(ManualResetEventSlim), FieldAttributes.Public | FieldAttributes.Static);
                    var catches = type.DefineField("Catches", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
                    var after = type.DefineField("After", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
                    var wait = typeof(ManualResetEventSlim).GetMethod(nameof(ManualResetEventSlim.Wait), Type.EmptyTypes)!;
                    void Count(System.Reflection.Emit.ILGenerator il, System.Reflection.Emit.FieldBuilder field, int by)
                    {
                        il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, field);
                        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, by);
                        il.Emit(System.Reflection.Emit.OpCodes.Add);
                        il.Emit(System.Reflection.Emit.OpCodes.Stsfld, field);
                    }
                    var retry = type.DefineMethod("Retry", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes).GetILGenerator();
                    var loop = retry.DefineLabel();
                    var done = retry.DefineLabel();
                    retry.MarkLabel(loop);
                    retry.BeginExceptionBlock();
                    retry.Emit(System.Reflection.Emit.OpCodes.Ldsfld, gateField);
                    retry.Emit(System.Reflection.Emit.OpCodes.Callvirt, wait);
                    retry.Emit(System.Reflection.Emit.OpCodes.Leave, done);
                    retry.BeginCatchBlock(typeof(Exception));
                    retry.Emit(System.Reflection.Emit.OpCodes.Pop);
                    Count(retry, catches, 1);
                    retry.Emit(System.Reflection.Emit.OpCodes.Leave, loop);
                    retry.EndExceptionBlock();
                    retry.MarkLabel(done);
                    retry.Emit(System.Reflection.Emit.OpCodes.Ret);
                    // A catch that resets the abort: the thread goes on, and waits again.
                    var keep = type.DefineMethod("Keep", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes).GetILGenerator();
                    keep.BeginExceptionBlock();
                    keep.Emit(System.Reflection.Emit.OpCodes.Ldsfld, gateField);
                    keep.Emit(System.Reflection.Emit.OpCodes.Callvirt, wait);
                    keep.BeginCatchBlock(typeof(Exception));
                    keep.Emit(System.Reflection.Emit.OpCodes.Pop);
                    keep.Emit(System.Reflection.Emit.OpCodes.Call, typeof(Thread).GetMethod("ResetAbort", Type.EmptyTypes)!);
                    keep.EndExceptionBlock();
                    keep.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 20);
                    keep.Emit(System.Reflection.Emit.OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.Sleep), [typeof(int)])!);
                    Count(keep, after, 1);
                    keep.Emit(System.Reflection.Emit.OpCodes.Ret);
#pragma warning disable SYSLIB0006 // Thread.Abort: the call the rewrite replaces
                    var stop = type.DefineMethod("Stop", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(Thread)]).GetILGenerator();
                    stop.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    stop.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(Thread).GetMethod(nameof(Thread.Abort), Type.EmptyTypes)!);
                    stop.Emit(System.Reflection.Emit.OpCodes.Ret);
#pragma warning restore SYSLIB0006
                    type.CreateType();
                    var modPath = Path.Combine(dir, "OniMoonoCheckAborts.dll");
                    mod.Save(modPath);
                    var aborts = ModAccess.LoadFrom(modPath).GetType("Aborts")!;
                    var gate = new ManualResetEventSlim(false);
                    aborts.GetField("Gate")!.SetValue(null, gate);
                    string Run(string method)
                    {
                        var thread = new Thread(aborts.GetMethod(method)!.CreateDelegate<ThreadStart>()) { IsBackground = true };
                        thread.Start();
                        Blocked(thread);
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        aborts.GetMethod("Stop")!.Invoke(null, [thread]);
                        if (!thread.Join(5000)) throw new InvalidOperationException($"{method}'s thread still runs 5 s after the abort");
                        return $"{watch.ElapsedMilliseconds} ms";
                    }
                    string retried = Run("Retry");
                    int caught = (int)aborts.GetField("Catches")!.GetValue(null)!;
                    if (caught != 1) throw new InvalidOperationException($"the retry loop's catch ran {caught} times");
                    // Keep's thread resets the abort in its catch, then sleeps and returns.
                    var keeper = new Thread(aborts.GetMethod("Keep")!.CreateDelegate<ThreadStart>()) { IsBackground = true };
                    keeper.Start();
                    Blocked(keeper);
                    aborts.GetMethod("Stop")!.Invoke(null, [keeper]);
                    if (!keeper.Join(5000)) throw new InvalidOperationException("Keep's thread did not end after ResetAbort");
                    if ((int)aborts.GetField("After")!.GetValue(null)! != 1) throw new InvalidOperationException("Keep's thread did not go on past its catch");
                    if (raised != 0) throw new InvalidOperationException($"UnhandledException raised {raised} times");
                    return $"the retry loop's catch ran once and its thread ended quietly ({retried}); after ResetAbort the thread went on and slept";
                }
                finally
                {
                    AppDomain.CurrentDomain.UnhandledException -= count;
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod's copy of a library, and references to it, when another version is loaded (ModAccess)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    Type? marker2 = null;
                    string Emit(string version)
                    {
                        var builder = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckLibrary") { Version = new Version(version) }, typeof(object).Assembly);
                        var marker = builder.DefineDynamicModule("OniMoonoCheckLibrary").DefineType("Marker", TypeAttributes.Public);
                        marker.CreateType();
                        if (version == "2.0.0.0") marker2 = marker;
                        var path = Path.Combine(dir, version, "OniMoonoCheckLibrary.dll");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        builder.Save(path);
                        return path;
                    }
                    var first = ModAccess.LoadFrom(Emit("1.0.0.0"));
                    var second = ModAccess.LoadFrom(Emit("2.0.0.0"));
                    if (!ReferenceEquals(first, second)) throw new InvalidOperationException("two versions loaded");
                    // A mod built against version 2, with its copy beside it, while version 1 is the
                    // one loaded: its reference binds to version 1.
                    var user = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckUser"), typeof(object).Assembly);
                    var use = user.DefineDynamicModule("OniMoonoCheckUser").DefineType("Use", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                    var get = use.DefineMethod("Get", MethodAttributes.Public | MethodAttributes.Static, typeof(Type), Type.EmptyTypes);
                    var il = get.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldtoken, marker2!);
                    il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    use.CreateType();
                    var userPath = Path.Combine(dir, "2.0.0.0", "OniMoonoCheckUser.dll");
                    user.Save(userPath);
                    var bound = (Type)Assembly.LoadFrom(userPath).GetType("Use")!.GetMethod("Get")!.Invoke(null, null)!;
                    return ReferenceEquals(bound.Assembly, first) ? "the loaded version stands in for files and references, as in Mono" : throw new InvalidOperationException($"the reference bound to {bound.Assembly.GetName().Version}");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("Strong-named copies, references to versions not loaded and the nested AssemblyResolve, as in Unity's player (ModAccess)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    // A public key (not signed: neither runtime checks a signature).
                    byte[] key;
                    using (var rsa = new System.Security.Cryptography.RSACryptoServiceProvider(1024))
                    {
                        var blob = rsa.ExportCspBlob(false);
                        key = [.. BitConverter.GetBytes(0x2400), .. BitConverter.GetBytes(0x8004), .. BitConverter.GetBytes(blob.Length), .. blob];
                    }
                    // A library NAME VERSION in FOLDER whose Who() says "VERSION FOLDER", or a user whose
                    // Call() returns REF's Who(), REF named with VERSION and TOKEN.
                    string Write(string folder, string name, Action<Cecil.ModuleDefinition, Cecil.Cil.ILProcessor> body, string version = "1.0.0.0", byte[]? publicKey = null)
                    {
                        var assembly = Cecil.AssemblyDefinition.CreateAssembly(new Cecil.AssemblyNameDefinition(name, Version.Parse(version)) { PublicKey = publicKey }, name, Cecil.ModuleKind.Dll);
                        var module = assembly.MainModule;
                        var type = new Cecil.TypeDefinition("", name, Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
                        module.Types.Add(type);
                        var method = new Cecil.MethodDefinition("Run", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, module.TypeSystem.String);
                        method.ImplAttributes |= Cecil.MethodImplAttributes.NoInlining;
                        var il = method.Body.GetILProcessor();
                        body(module, il);
                        il.Emit(Cecil.Cil.OpCodes.Ret);
                        type.Methods.Add(method);
                        var path = Path.Combine(dir, folder, name + ".dll");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        assembly.Write(path);
                        return path;
                    }
                    string Library(string folder, string name, string version, byte[]? publicKey = null) =>
                        Write(folder, name, (_, il) => il.Emit(Cecil.Cil.OpCodes.Ldstr, $"{version} {folder}"), version, publicKey);
                    string User(string folder, string name, string library, string version, byte[]? token = null) => Write(folder, name, (module, il) =>
                    {
                        var reference = new Cecil.AssemblyNameReference(library, Version.Parse(version)) { PublicKeyToken = token };
                        module.AssemblyReferences.Add(reference);
                        il.Emit(Cecil.Cil.OpCodes.Call, new Cecil.MethodReference("Run", module.TypeSystem.String, new Cecil.TypeReference("", library, module, reference)));
                    });
                    string Run(string path) => (string)ModAccess.LoadFrom(path).GetType(Path.GetFileNameWithoutExtension(path))!.GetMethod("Run")!.Invoke(null, null)!;

                    const string strong = "OniMoonoCheckStrong", plain = "OniMoonoCheckPlain", missing = "OniMoonoCheckMissing";
                    var first = ModAccess.LoadFrom(Library("s1", strong, "1.0.0.0", key));
                    var token = first.GetName().GetPublicKeyToken();
                    // A copy of the same identity is the loaded one; another version loads beside it.
                    if (!ReferenceEquals(ModAccess.LoadFrom(Library("s1b", strong, "1.0.0.0", key)), first)) throw new InvalidOperationException("a copy of the loaded strong-named assembly loaded again");
                    var second = ModAccess.LoadFrom(Library("s2", strong, "2.0.0.0", key));
                    if (ReferenceEquals(second, first) || second.GetName().Version != new Version(2, 0, 0, 0) || Path.GetFileName(Path.GetDirectoryName(second.Location)) != "s2")
                        throw new InvalidOperationException($"version 2 gave {second.GetName().Version} from {second.Location}");
                    // A reference of that identity binds to it; one to a version not loaded, to the first loaded.
                    if (Run(User("u2", "OniMoonoCheckUser2", strong, "2.0.0.0", token)) is var two && two != "2.0.0.0 s2") throw new InvalidOperationException($"a reference to version 2 bound to {two}");
                    if (Run(User("u3", "OniMoonoCheckUser3", strong, "3.0.0.0", token)) is var three && three != "1.0.0.0 s1") throw new InvalidOperationException($"a reference to version 3 bound to {three}");
                    // A mod's own Assembly.LoadFrom of another version of a loaded assembly without a strong name.
                    var plainFirst = ModAccess.LoadFrom(Library("p1", plain, "1.0.0.0"));
                    if (!ReferenceEquals(Assembly.LoadFrom(Library("p2", plain, "2.0.0.0")), plainFirst)) throw new InvalidOperationException("Assembly.LoadFrom loaded a second version");
                    // A reference nothing binds: the handlers see its simple name (no requesting assembly), then its full name.
                    var seen = new List<string>();
                    ResolveEventHandler record = (_, e) =>
                    {
                        if (e.Name.StartsWith(missing, StringComparison.Ordinal)) seen.Add(e.Name + " from " + (e.RequestingAssembly?.GetName().Name ?? "null"));
                        return null;
                    };
                    AppDomain.CurrentDomain.AssemblyResolve += record;
                    try
                    {
                        Run(User("un", "OniMoonoCheckUserN", missing, "1.0.0.0"));
                        throw new InvalidOperationException("a reference to a missing assembly bound");
                    }
                    catch (TargetInvocationException e) when (e.InnerException is FileNotFoundException)
                    {
                    }
                    finally
                    {
                        AppDomain.CurrentDomain.AssemblyResolve -= record;
                    }
                    string[] expected = [missing + " from null", missing + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=null from OniMoonoCheckUserN"];
                    if (!seen.SequenceEqual(expected)) throw new InvalidOperationException("AssemblyResolve saw " + string.Join("; ", seen));
                    return "copies, versions beside, references and AssemblyResolve as in Unity's player";
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("The game's assemblies report their files as Location (ModAccess)", () =>
            {
                ModAccess.Install();
                foreach (var name in new[] { "Assembly-CSharp", "Assembly-CSharp-firstpass" })
                {
                    var location = Assembly.Load(name).Location;
                    var expected = Path.Combine(AssemblyPolicy.ReferenceDirectories[0], name + ".dll");
                    if (Path.GetFullPath(location) != Path.GetFullPath(expected)) throw new InvalidOperationException($"{name}'s location is '{location}'");
                }
                return "as on Mono, whether or not their images were rewritten";
            });
            Check("A mod built to run in 32-bit processes only (ModAccess)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    // As 3280526635's TestMod.dll: x86, IL only, 32BITREQUIRED.
                    var name = "OniMoonoCheck32Bit";
                    using (var assembly = Cecil.AssemblyDefinition.CreateAssembly(new Cecil.AssemblyNameDefinition(name, new Version(1, 0)), name, Cecil.ModuleKind.Dll))
                    {
                        var module = assembly.MainModule;
                        module.Architecture = Cecil.TargetArchitecture.I386;
                        module.Attributes = Cecil.ModuleAttributes.ILOnly | Cecil.ModuleAttributes.Required32Bit;
                        var probe = new Cecil.TypeDefinition("", "Probe", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
                        module.Types.Add(probe);
                        var get = new Cecil.MethodDefinition("Get", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
                        probe.Methods.Add(get);
                        get.Body.GetILProcessor().Emit(Cecil.Cil.OpCodes.Ldc_I4_7);
                        get.Body.GetILProcessor().Emit(Cecil.Cil.OpCodes.Ret);
                        assembly.Write(Path.Combine(dir, name + ".dll"));
                    }
                    var path = Path.Combine(dir, name + ".dll");
                    try
                    {
                        new System.Runtime.Loader.AssemblyLoadContext("OniMoono check 32-bit", isCollectible: true).LoadFromAssemblyPath(path);
                        throw new InvalidOperationException("CoreCLR loaded the 32-bit image as it is");
                    }
                    catch (Exception e) when (e is FileLoadException or BadImageFormatException)
                    {
                    }
                    var loaded = ModAccess.LoadFrom(path);
                    if (loaded.Location != path) throw new InvalidOperationException($"its location is {loaded.Location}");
                    var result = (int)loaded.GetType("Probe")!.GetMethod("Get")!.Invoke(null, null)!;
                    return result == 7 ? "it loads and runs, as on Mono" : throw new InvalidOperationException($"returned {result}");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod with types nested in <Module> (ModuleNestedTypes)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    // As Abyssalite's obfuscated Katairite.dll: Hidden, nested in <Module>, with a type
                    // nested in it, calls a private method of <Module>.
                    var name = "OniMoonoCheckGlobalNested";
                    using (var assembly = Cecil.AssemblyDefinition.CreateAssembly(new Cecil.AssemblyNameDefinition(name, new Version(1, 0)), name, Cecil.ModuleKind.Dll))
                    {
                        var module = assembly.MainModule;
                        var global = module.Types.Single(t => t.Name == "<Module>");
                        var secret = new Cecil.MethodDefinition("Secret", Cecil.MethodAttributes.Private | Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
                        secret.Body.GetILProcessor().Emit(Cecil.Cil.OpCodes.Ldc_I4_7);
                        secret.Body.GetILProcessor().Emit(Cecil.Cil.OpCodes.Ret);
                        global.Methods.Add(secret);
                        var hidden = new Cecil.TypeDefinition("", "Hidden", Cecil.TypeAttributes.NestedAssembly | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
                        global.NestedTypes.Add(hidden);
                        var inner = new Cecil.TypeDefinition("", "Inner", Cecil.TypeAttributes.NestedAssembly | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
                        hidden.NestedTypes.Add(inner);
                        var get = new Cecil.MethodDefinition("Get", Cecil.MethodAttributes.Assembly | Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
                        get.Body.GetILProcessor().Emit(Cecil.Cil.OpCodes.Call, secret);
                        get.Body.GetILProcessor().Emit(Cecil.Cil.OpCodes.Ret);
                        inner.Methods.Add(get);
                        var probe = new Cecil.TypeDefinition("", "Probe", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
                        module.Types.Add(probe);
                        var run = new Cecil.MethodDefinition("Run", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
                        run.Body.GetILProcessor().Emit(Cecil.Cil.OpCodes.Call, get);
                        run.Body.GetILProcessor().Emit(Cecil.Cil.OpCodes.Ret);
                        probe.Methods.Add(run);
                        assembly.Write(Path.Combine(dir, name + ".dll"));
                    }
                    var path = Path.Combine(dir, name + ".dll");
                    try
                    {
                        new System.Runtime.Loader.AssemblyLoadContext("OniMoono check global nested", isCollectible: true).LoadFromAssemblyPath(path).GetTypes();
                        throw new InvalidOperationException("CoreCLR loaded the types nested in <Module> as they are");
                    }
                    catch (BadImageFormatException)
                    {
                    }
                    var loaded = ModAccess.LoadFrom(path);
                    var types = loaded.GetTypes();
                    var result = (int)loaded.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null)!;
                    return result == 7 && types.Length == 3 ? "its types load and run, as on Mono" : throw new InvalidOperationException($"returned {result}, {types.Length} types");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod calling a protected game method of a class it does not derive from (ModAccess)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    var kmb = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name is "Assembly-CSharp" or "Assembly-CSharp-firstpass" ? a.GetType("KMonoBehaviour") : null).Single(t => t != null)!;
                    var onSpawn = kmb.GetMethod("OnSpawn", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes) ?? throw new InvalidOperationException("KMonoBehaviour.OnSpawn is not in the game");
                    if (!onSpawn.IsFamily) throw new InvalidOperationException("KMonoBehaviour.OnSpawn is not protected");
                    // As Public Cots' postfix reads the protected StateMachine.GenericInstance.smi:
                    // Outsider calls it from a class that derives from nothing of the game's;
                    // Subclass from a KMonoBehaviour, which CoreCLR allows as it is.
                    Assembly Mod(string name, bool subclass)
                    {
                        using var assembly = Cecil.AssemblyDefinition.CreateAssembly(new Cecil.AssemblyNameDefinition(name, new Version(1, 0)), name, Cecil.ModuleKind.Dll);
                        var module = assembly.MainModule;
                        var probe = new Cecil.TypeDefinition("", "Probe", Cecil.TypeAttributes.Public, subclass ? module.ImportReference(kmb) : module.TypeSystem.Object);
                        module.Types.Add(probe);
                        var call = new Cecil.MethodDefinition("Call", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
                        call.Parameters.Add(new Cecil.ParameterDefinition(module.TypeSystem.Boolean));
                        call.Parameters.Add(new Cecil.ParameterDefinition(subclass ? probe : module.ImportReference(kmb)));
                        probe.Methods.Add(call);
                        var il = call.Body.GetILProcessor();
                        var skip = il.Create(Cecil.Cil.OpCodes.Ldc_I4_7);
                        il.Emit(Cecil.Cil.OpCodes.Ldarg_0);
                        il.Emit(Cecil.Cil.OpCodes.Brfalse, skip);
                        il.Emit(Cecil.Cil.OpCodes.Ldarg_1);
                        il.Emit(Cecil.Cil.OpCodes.Callvirt, module.ImportReference(onSpawn));
                        il.Append(skip);
                        il.Emit(Cecil.Cil.OpCodes.Ret);
                        var path = Path.Combine(dir, name + ".dll");
                        assembly.Write(path);
                        return ModAccess.LoadFrom(path);
                    }
                    bool Opened(Assembly a) => a.GetCustomAttributesData().Any(d => d.AttributeType.Name == "IgnoresAccessChecksToAttribute");
                    var outsider = Mod("OniMoonoCheckProtectedOutsider", false);
                    var subclass = Mod("OniMoonoCheckProtectedSubclass", true);
                    if (!Opened(outsider)) throw new InvalidOperationException("the outsider was loaded with access checks on");
                    if (Opened(subclass)) throw new InvalidOperationException("the subclass was loaded with access checks off");
                    var result = (int)outsider.GetType("Probe")!.GetMethod("Call")!.Invoke(null, [false, null])!;
                    return result == 7 ? "the outsider runs, as on Mono; the subclass loads as it is" : throw new InvalidOperationException($"returned {result}");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("Reflection writing a mod's static readonly field after its type is initialized (ModAccess)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    var builder = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckStrings"), typeof(object).Assembly);
                    var strings = builder.DefineDynamicModule("OniMoonoCheckStrings").DefineType("Strings", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                    var field = strings.DefineField("NAME", typeof(string), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
                    var cctor = strings.DefineTypeInitializer().GetILGenerator();
                    cctor.Emit(System.Reflection.Emit.OpCodes.Ldstr, "before");
                    cctor.Emit(System.Reflection.Emit.OpCodes.Stsfld, field);
                    cctor.Emit(System.Reflection.Emit.OpCodes.Ret);
                    strings.CreateType();
                    var path = Path.Combine(dir, "OniMoonoCheckStrings.dll");
                    builder.Save(path);
                    var name = ModAccess.LoadFrom(path).GetType("Strings")!.GetField("NAME")!;
                    if ((string)name.GetValue(null)! != "before") throw new InvalidOperationException("type initializer did not run");
                    name.SetValue(null, "after");
                    return (string)name.GetValue(null)! == "after" ? "the write holds, as in Mono" : throw new InvalidOperationException("the write was lost");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod's class missing a method of a game interface it lists (MissingInterfaceMethods)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    // The game's interface as the mod was built against (Old) and as it is now (New).
                    System.Reflection.Emit.TypeBuilder Api(string method, string folder)
                    {
                        var builder = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckApi"), typeof(object).Assembly);
                        var module = builder.DefineDynamicModule("OniMoonoCheckApi");
                        // The new method takes one of the game's own types (Trashcans' missing
                        // method names the game's ButtonMenuTextOverride).
                        var arg = module.DefineType("Arg", TypeAttributes.Public);
                        arg.CreateType();
                        var thing = module.DefineType("IThing", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
                        thing.DefineMethod(method, MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig, typeof(int), method == "New" ? [arg] : Type.EmptyTypes);
                        // A second new method: CoreCLR names one missing method of a class at a time.
                        if (method == "New")
                            thing.DefineMethod("Newer", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
                        thing.CreateType();
                        Directory.CreateDirectory(Path.Combine(dir, folder));
                        builder.Save(Path.Combine(dir, folder, "OniMoonoCheckApi.dll"));
                        return thing;
                    }
                    var oldThing = Api("Old", "old");
                    Api("New", "new");
                    var current = Assembly.LoadFrom(Path.Combine(dir, "new", "OniMoonoCheckApi.dll"));
                    var mod = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckOldMod"), typeof(object).Assembly);
                    var impl = mod.DefineDynamicModule("OniMoonoCheckOldMod").DefineType("Impl", TypeAttributes.Public, typeof(object), [oldThing]);
                    var old = impl.DefineMethod("Old", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
                    var il = old.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_7); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    impl.DefineDefaultConstructor(MethodAttributes.Public);
                    impl.CreateType();
                    MissingInterfaceMethods.CacheFolder = Path.Combine(dir, "probes");
                    var modPath = Path.Combine(dir, "mod", "OniMoonoCheckOldMod.dll");
                    Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
                    mod.Save(modPath);
                    var type = ModAccess.LoadFrom(modPath).GetTypes().Single(t => t.Name == "Impl");
                    // The probe's copy is gone: a library that picks its instance from the
                    // process's assemblies (PLib) must find the loaded one only.
                    int copies = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == "OniMoonoCheckOldMod");
                    if (copies != 1) throw new InvalidOperationException($"{copies} copies of the mod are listed");
                    var instance = Activator.CreateInstance(type)!;
                    if ((int)type.GetMethod("Old")!.Invoke(instance, null)! != 7) throw new InvalidOperationException("the mod's own method changed");
                    foreach (var name in new[] { "New", "Newer" })
                    {
                        var newMethod = current.GetType("IThing")!.GetMethod(name)!;
                        try
                        {
                            newMethod.Invoke(instance, newMethod.GetParameters().Length == 0 ? null : [null]);
                            throw new InvalidOperationException($"the missing method {name} did not throw");
                        }
                        catch (TargetInvocationException e) when (e.InnerException is TypeLoadException)
                        {
                        }
                    }
                    // The probes' verdicts kept in the cache: a second completion of the image
                    // probes nothing and gives the same bytes, and each verdict kept is the one a
                    // fresh probe finds.
                    var image = File.ReadAllBytes(modPath);
                    byte[]? noSymbols = null;
                    // An empty cache: the load above kept its verdicts under the same keys.
                    MissingInterfaceMethods.CacheFolder = Path.Combine(dir, "probes-again");
                    int probes = MissingInterfaceMethods.ProbesRun, hits = MissingInterfaceMethods.CacheHits;
                    var first = MissingInterfaceMethods.Adjust(image, ref noSymbols, modPath) ?? throw new InvalidOperationException("nothing was completed");
                    int probed = MissingInterfaceMethods.ProbesRun - probes;
                    if (probed < 2 || MissingInterfaceMethods.CacheHits != hits) throw new InvalidOperationException($"the first completion probed {probed} times and read {MissingInterfaceMethods.CacheHits - hits} verdicts from the cache");
                    var second = MissingInterfaceMethods.Adjust(image, ref noSymbols, modPath) ?? throw new InvalidOperationException("nothing was completed the second time");
                    if (MissingInterfaceMethods.ProbesRun - probes != probed || MissingInterfaceMethods.CacheHits - hits != probed)
                        throw new InvalidOperationException($"the second completion probed {MissingInterfaceMethods.ProbesRun - probes - probed} times and read {MissingInterfaceMethods.CacheHits - hits} of {probed} verdicts from the cache");
                    if (!first.AsSpan().SequenceEqual(second)) throw new InvalidOperationException("the image completed from the cache differs");
                    foreach (var checkedImage in new[] { image, first })
                    {
                        var (cached, fresh) = MissingInterfaceMethods.CompareWithProbe(checkedImage);
                        if (cached != fresh) throw new InvalidOperationException($"the cached verdict ({cached ?? "none"}) is not the probe's ({fresh})");
                    }
                    return $"the class loads; calling either missing method throws TypeLoadException; {probed} probes' verdicts read back from the cache, the same image";
                }
                finally
                {
                    MissingInterfaceMethods.CacheFolder = null;
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod's nested type of the game that the game no longer has (MonoNestedTypeRefs)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
                    var criteria = game.GetType("STRINGS.ROOMS+CRITERIA", throwOnError: true)!;
                    const BindingFlags anyNested = BindingFlags.Public | BindingFlags.NonPublic;
                    // Mono binds the class's last nested type (by row); measured in the original
                    // (U59-744825): DECOR20 reads POWERBUILDING's strings.
                    var nested = criteria.GetNestedTypes(anyNested).OrderBy(t => t.MetadataToken).ToArray();
                    var last = nested[^1];
                    var first = nested[0];
                    // A mod naming STRINGS.ROOMS.CRITERIA.DECOR20 (U&F-Translate Fix), a nested type
                    // CRITERIA has (left as it is), a nested type within the missing one, and
                    // TUNING.DUPLICANTSTATS.MOVEMENT (Mosaic Tile), outside the game's strings.
                    var name = "OniMoonoCheckNestedMod";
                    using var assembly = Cecil.AssemblyDefinition.CreateAssembly(new Cecil.AssemblyNameDefinition(name, new Version(1, 0)), name, Cecil.ModuleKind.Dll);
                    var module = assembly.MainModule;
                    var gameRef = new Cecil.AssemblyNameReference("Assembly-CSharp", game.GetName().Version);
                    module.AssemblyReferences.Add(gameRef);
                    Cecil.TypeReference Nested(Cecil.TypeReference outer, string inner) => new("", inner, module, gameRef) { DeclaringType = outer };
                    var criteriaRef = Nested(new Cecil.TypeReference("STRINGS", "ROOMS", module, gameRef), "CRITERIA");
                    var decor20 = Nested(criteriaRef, "DECOR20");
                    var withinMissing = Nested(decor20, "INNER");
                    var present = Nested(criteriaRef, first.Name);
                    var movement = Nested(new Cecil.TypeReference("TUNING", "DUPLICANTSTATS", module, gameRef), "MOVEMENT");
                    var locString = module.ImportReference(last.GetField("NAME")!.FieldType);
                    var probe = new Cecil.TypeDefinition("", "Probe", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
                    module.Types.Add(probe);
                    var fromHandle = module.ImportReference(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                    void Method(string methodName, Action<Cecil.Cil.ILProcessor> body, Cecil.TypeReference returns)
                    {
                        var method = new Cecil.MethodDefinition(methodName, Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, returns);
                        probe.Methods.Add(method);
                        var il = method.Body.GetILProcessor();
                        body(il);
                        il.Emit(Cecil.Cil.OpCodes.Ret);
                    }
                    foreach (var (methodName, target) in new[] { ("Decor20", decor20), ("WithinMissing", withinMissing), ("Present", present), ("Movement", movement) })
                        Method(methodName, il => { il.Emit(Cecil.Cil.OpCodes.Ldtoken, target); il.Emit(Cecil.Cil.OpCodes.Call, fromHandle); }, module.ImportReference(typeof(Type)));
                    Method("Decor20Name", il => il.Emit(Cecil.Cil.OpCodes.Ldsfld, new Cecil.FieldReference("NAME", locString, decor20)), module.TypeSystem.Object);
                    var modPath = Path.Combine(dir, name + ".dll");
                    assembly.Write(modPath);
                    using var modPe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(modPath));
                    using var gamePe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(Path.Combine(AssemblyPolicy.ReferenceDirectories[0], "Assembly-CSharp.dll")));
                    var bindings = MonoNestedTypeRefs.Bindings(
                        System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(modPe), System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(gamePe));
                    if (bindings.Count != 1 || bindings.Values.Single() is not { Name: var boundName, Missing: "DECOR20" } || boundName != last.Name)
                        throw new InvalidOperationException("bindings: " + string.Join(", ", bindings.Values.Select(b => $"{b.Missing} -> {b.Name}")));
                    var type = ModAccess.LoadFrom(modPath).GetType("Probe")!;
                    object? Call(string methodName) => type.GetMethod(methodName)!.Invoke(null, null);
                    if (Call("Decor20") as Type != last) throw new InvalidOperationException($"DECOR20 binds to {Call("Decor20")}, not {last}");
                    if (Call("Present") as Type != first) throw new InvalidOperationException($"{first.Name} binds to {Call("Present")}");
                    if (!ReferenceEquals(Call("Decor20Name"), last.GetField("NAME")!.GetValue(null))) throw new InvalidOperationException("DECOR20.NAME is not " + last.Name + ".NAME");
                    string Fails(string methodName)
                    {
                        try
                        {
                            Call(methodName);
                        }
                        catch (Exception e) when ((e as TargetInvocationException)?.InnerException is TypeLoadException || e is TypeLoadException)
                        {
                            return "TypeLoadException";
                        }
                        throw new InvalidOperationException(methodName + " did not fail");
                    }
                    // POWERBUILDING has no nested INNER (Mono fails it too); MOVEMENT's class has not
                    // had its nested types listed when a mod's code runs, so Mono fails it as well.
                    Fails("WithinMissing");
                    Fails("Movement");
                    return $"CRITERIA.DECOR20 binds to {last.Name}, as on Mono; DUPLICANTSTATS.MOVEMENT and a type within DECOR20 throw TypeLoadException";
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod's methods naming a type the game no longer has (MonoMissingTypes)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
                    const string gone = "OniMoonoCheckGone";
                    if (game.GetType(gone) != null) throw new InvalidOperationException(gone + " is in the game");
                    // As Endless Science fix's prefix: `callvirt Worker Workable::get_worker()` behind a
                    // branch not taken, Worker gone from the game. Inner fails as a whole, on Mono with
                    // its message from the caller's frame; IsInst drops the error (Mono's generic
                    // message); Fine names no missing type.
                    var name = "OniMoonoCheckGoneMod";
                    using var assembly = Cecil.AssemblyDefinition.CreateAssembly(new Cecil.AssemblyNameDefinition(name, new Version(1, 0)), name, Cecil.ModuleKind.Dll);
                    var module = assembly.MainModule;
                    var gameRef = new Cecil.AssemblyNameReference("Assembly-CSharp", game.GetName().Version);
                    module.AssemblyReferences.Add(gameRef);
                    var goneRef = new Cecil.TypeReference("", gone, module, gameRef);
                    var workable = module.ImportReference(game.GetType("Workable", throwOnError: true)!);
                    var getWorker = new Cecil.MethodReference("get_worker", goneRef, workable) { HasThis = true };
                    var probe = new Cecil.TypeDefinition("", "Probe", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
                    module.Types.Add(probe);
                    Cecil.MethodDefinition Method(string methodName, Cecil.TypeReference returns, Action<Cecil.Cil.ILProcessor> body, params Cecil.TypeReference[] parameters)
                    {
                        var method = new Cecil.MethodDefinition(methodName, Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, returns);
                        foreach (var parameter in parameters) method.Parameters.Add(new Cecil.ParameterDefinition(parameter));
                        probe.Methods.Add(method);
                        body(method.Body.GetILProcessor());
                        return method;
                    }
                    var inner = Method("Inner", module.TypeSystem.Int32, il =>
                    {
                        var skip = il.Create(Cecil.Cil.OpCodes.Ldc_I4_7);
                        il.Emit(Cecil.Cil.OpCodes.Ldarg_0);
                        il.Emit(Cecil.Cil.OpCodes.Brfalse, skip);
                        il.Emit(Cecil.Cil.OpCodes.Ldnull);
                        il.Emit(Cecil.Cil.OpCodes.Callvirt, getWorker);
                        il.Emit(Cecil.Cil.OpCodes.Pop);
                        il.Append(skip);
                        il.Emit(Cecil.Cil.OpCodes.Ret);
                    }, module.TypeSystem.Boolean);
                    Method("Outer", module.TypeSystem.Int32, il =>
                    {
                        il.Emit(Cecil.Cil.OpCodes.Ldc_I4_0);
                        il.Emit(Cecil.Cil.OpCodes.Call, inner);
                        il.Emit(Cecil.Cil.OpCodes.Ret);
                    });
                    Method("IsInst", module.TypeSystem.Boolean, il =>
                    {
                        il.Emit(Cecil.Cil.OpCodes.Ldarg_0);
                        il.Emit(Cecil.Cil.OpCodes.Isinst, goneRef);
                        il.Emit(Cecil.Cil.OpCodes.Ldnull);
                        il.Emit(Cecil.Cil.OpCodes.Cgt_Un);
                        il.Emit(Cecil.Cil.OpCodes.Ret);
                    }, module.TypeSystem.Object);
                    Method("Fine", module.TypeSystem.Int32, il => { il.Emit(Cecil.Cil.OpCodes.Ldc_I4_5); il.Emit(Cecil.Cil.OpCodes.Ret); });
                    // As Delivery Temperature Limit's copy of PLib (PUIUtils.GetEmWidth): a type that
                    // both Unity's TextMeshPro (Managed/) and ours lack, TMPro.FaceInfo.
                    var tmp = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Unity.TextMeshPro");
                    if (tmp.GetType("TMPro.FaceInfo") != null) throw new InvalidOperationException("TMPro.FaceInfo is in our TextMeshPro");
                    var tmpRef = new Cecil.AssemblyNameReference("Unity.TextMeshPro", new Version(0, 0, 0, 0));
                    module.AssemblyReferences.Add(tmpRef);
                    var faceInfoRef = new Cecil.TypeReference("TMPro", "FaceInfo", module, tmpRef, valueType: false);
                    var faceInfoCtor = new Cecil.MethodReference(".ctor", module.TypeSystem.Void, faceInfoRef) { HasThis = true };
                    Method("Package", module.TypeSystem.Int32, il =>
                    {
                        var skip = il.Create(Cecil.Cil.OpCodes.Ldc_I4_3);
                        il.Emit(Cecil.Cil.OpCodes.Ldarg_0);
                        il.Emit(Cecil.Cil.OpCodes.Brfalse, skip);
                        il.Emit(Cecil.Cil.OpCodes.Newobj, faceInfoCtor);
                        il.Emit(Cecil.Cil.OpCodes.Pop);
                        il.Append(skip);
                        il.Emit(Cecil.Cil.OpCodes.Ret);
                    }, module.TypeSystem.Boolean);
                    var modPath = Path.Combine(dir, name + ".dll");
                    assembly.Write(modPath);
                    int token;
                    using (var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(modPath)))
                    {
                        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                        token = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(md.TypeReferences.Single(h => md.GetString(md.GetTypeReference(h).Name) == gone));
                    }
                    var type = ModAccess.LoadFrom(modPath).GetType("Probe")!;
                    TypeLoadException Fails(string methodName, object?[]? args)
                    {
                        try
                        {
                            type.GetMethod(methodName)!.Invoke(null, BindingFlags.DoNotWrapExceptions, null, args, null);
                        }
                        catch (TypeLoadException e)
                        {
                            return e;
                        }
                        throw new InvalidOperationException(methodName + " did not throw TypeLoadException");
                    }
                    var expected = $"Could not resolve type with token {token:x8} from typeref (expected class '{gone}' in assembly 'Assembly-CSharp, Version={game.GetName().Version}, Culture=neutral, PublicKeyToken=null')";
                    var outer = Fails("Outer", null);
                    if (outer.Message != expected) throw new InvalidOperationException($"Outer: \"{outer.Message}\", not \"{expected}\"");
                    var first = outer.StackTrace!.Split('\n')[0].Trim();
                    if (!first.StartsWith("at Probe.Outer(", StringComparison.Ordinal)) throw new InvalidOperationException("Outer's first frame: " + first);
                    var isInst = Fails("IsInst", [null]);
                    if (isInst.Message != "Failure has occurred while loading a type.") throw new InvalidOperationException("IsInst: " + isInst.Message);
                    if ((int)type.GetMethod("Fine")!.Invoke(null, null)! != 5) throw new InvalidOperationException("Fine changed");
                    int faceInfoToken;
                    using (var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(modPath)))
                    {
                        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                        faceInfoToken = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(md.TypeReferences.Single(h => md.GetString(md.GetTypeReference(h).Name) == "FaceInfo"));
                    }
                    var package = Fails("Package", [false]);
                    var packageExpected = $"Could not resolve type with token {faceInfoToken:x8} from typeref (expected class 'TMPro.FaceInfo' in assembly 'Unity.TextMeshPro, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null')";
                    if (package.Message != packageExpected) throw new InvalidOperationException($"Package: \"{package.Message}\", not \"{packageExpected}\"");
                    return $"the method throws Mono's TypeLoadException from its caller's frame ({first}), also for a type Unity's package and ours both lack; isinst gives Mono's message without the type";
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod's methods naming a member the game no longer has (MonoMissingMembers)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
                    var firstpass = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp-firstpass");
                    const string gone = "OniMoonoCheckGone";
                    // As Trapdoors & Floor Hatches' OnSpawn: `call void SimMessages::SetCellProperties(int32,
                    // uint8)` behind a branch not taken, the game having no such overload. Each method
                    // below names one missing member: Mono fails it as a whole, from its caller's frame,
                    // with the lookup's message.
                    var simMessages = game.GetType("SimMessages", throwOnError: true)!;
                    if (simMessages.GetMethod("SetCellProperties", [typeof(int), typeof(byte)]) != null) throw new InvalidOperationException("SimMessages.SetCellProperties(int, byte) is in the game");
                    var generic = game.GetExportedTypes().First(t => t.IsGenericTypeDefinition && t.GetGenericArguments().Length == 1 && t.Namespace == null && !t.IsNested && !t.IsInterface);
                    var nested = game.GetExportedTypes().First(t => t.IsNested && !t.IsGenericType && t.DeclaringType!.Namespace == null && !t.DeclaringType.IsGenericType && !t.IsInterface);
                    var name = "OniMoonoCheckGoneMembersMod";
                    using var assembly = Cecil.AssemblyDefinition.CreateAssembly(new Cecil.AssemblyNameDefinition(name, new Version(1, 0)), name, Cecil.ModuleKind.Dll);
                    var module = assembly.MainModule;
                    var ts = module.TypeSystem;
                    var probe = new Cecil.TypeDefinition("", "Probe", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, ts.Object);
                    module.Types.Add(probe);
                    Cecil.MethodDefinition Method(string methodName, Cecil.TypeReference returns, Action<Cecil.Cil.ILProcessor> body, params Cecil.TypeReference[] parameters)
                    {
                        var method = new Cecil.MethodDefinition(methodName, Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, returns);
                        foreach (var parameter in parameters) method.Parameters.Add(new Cecil.ParameterDefinition(parameter));
                        probe.Methods.Add(method);
                        body(method.Body.GetILProcessor());
                        return method;
                    }
                    // `if (flag) <emit>`, then return 7.
                    Cecil.MethodDefinition Behind(string methodName, Action<Cecil.Cil.ILProcessor> emit) => Method(methodName, ts.Int32, il =>
                    {
                        var skip = il.Create(Cecil.Cil.OpCodes.Ldc_I4_7);
                        il.Emit(Cecil.Cil.OpCodes.Ldarg_0);
                        il.Emit(Cecil.Cil.OpCodes.Brfalse, skip);
                        emit(il);
                        il.Append(skip);
                        il.Emit(Cecil.Cil.OpCodes.Ret);
                    }, ts.Boolean);
                    var setCell = new Cecil.MethodReference("SetCellProperties", ts.Void, module.ImportReference(simMessages));
                    setCell.Parameters.Add(new Cecil.ParameterDefinition(ts.Int32));
                    setCell.Parameters.Add(new Cecil.ParameterDefinition(ts.Byte));
                    var inner = Behind("Inner", il => { il.Emit(Cecil.Cil.OpCodes.Ldc_I4_1); il.Emit(Cecil.Cil.OpCodes.Ldc_I4_8); il.Emit(Cecil.Cil.OpCodes.Call, setCell); });
                    Method("Outer", ts.Int32, il => { il.Emit(Cecil.Cil.OpCodes.Ldc_I4_0); il.Emit(Cecil.Cil.OpCodes.Call, inner); il.Emit(Cecil.Cil.OpCodes.Ret); });
                    // A generic type's instance method by a by-ref array of its parameter.
                    var open = module.ImportReference(generic);
                    var instance = new Cecil.GenericInstanceType(open);
                    instance.GenericArguments.Add(ts.Int32);
                    var onGeneric = new Cecil.MethodReference(gone, ts.Void, instance) { HasThis = true };
                    onGeneric.Parameters.Add(new Cecil.ParameterDefinition(new Cecil.ByReferenceType(new Cecil.ArrayType(open.GenericParameters[0]))));
                    Behind("OnGeneric", il => { il.Emit(Cecil.Cil.OpCodes.Ldnull); il.Emit(Cecil.Cil.OpCodes.Ldnull); il.Emit(Cecil.Cil.OpCodes.Callvirt, onGeneric); });
                    // A generic method of SimMessages, and a nested type's static field.
                    var genericMethod = new Cecil.MethodReference(gone, ts.Void, module.ImportReference(simMessages));
                    var t = new Cecil.GenericParameter("T", genericMethod);
                    genericMethod.GenericParameters.Add(t);
                    genericMethod.Parameters.Add(new Cecil.ParameterDefinition(t));
                    var call = new Cecil.GenericInstanceMethod(genericMethod);
                    call.GenericArguments.Add(ts.String);
                    Behind("GenericMethod", il => { il.Emit(Cecil.Cil.OpCodes.Ldnull); il.Emit(Cecil.Cil.OpCodes.Call, call); });
                    var field = new Cecil.FieldReference(gone, ts.Int32, module.ImportReference(nested));
                    Behind("NestedField", il => { il.Emit(Cecil.Cil.OpCodes.Ldsfld, field); il.Emit(Cecil.Cil.OpCodes.Pop); });
                    // Found: a method of the game by its own signature, and one of KMonoBehaviour named
                    // on Workable.
                    var xyToCell = new Cecil.MethodReference("XYToCell", ts.Int32, module.ImportReference(game.GetType("Grid", throwOnError: true)!));
                    xyToCell.Parameters.Add(new Cecil.ParameterDefinition(ts.Int32));
                    xyToCell.Parameters.Add(new Cecil.ParameterDefinition(ts.Int32));
                    var trigger = new Cecil.MethodReference("Trigger", ts.Void, module.ImportReference(game.GetType("Workable", throwOnError: true)!)) { HasThis = true };
                    trigger.Parameters.Add(new Cecil.ParameterDefinition(ts.Int32));
                    trigger.Parameters.Add(new Cecil.ParameterDefinition(ts.Object));
                    if (firstpass.GetType("KMonoBehaviour", throwOnError: true)!.GetMethod("Trigger", [typeof(int), typeof(object)]) == null) throw new InvalidOperationException("KMonoBehaviour.Trigger(int, object) changed");
                    Method("Fine", ts.Int32, il =>
                    {
                        var skip = il.Create(Cecil.Cil.OpCodes.Ldc_I4_2);
                        il.Emit(Cecil.Cil.OpCodes.Ldarg_0);
                        il.Emit(Cecil.Cil.OpCodes.Brfalse, skip);
                        il.Emit(Cecil.Cil.OpCodes.Ldnull);
                        il.Emit(Cecil.Cil.OpCodes.Ldc_I4_0);
                        il.Emit(Cecil.Cil.OpCodes.Ldnull);
                        il.Emit(Cecil.Cil.OpCodes.Callvirt, trigger);
                        il.Append(skip);
                        il.Emit(Cecil.Cil.OpCodes.Ldc_I4_3);
                        il.Emit(Cecil.Cil.OpCodes.Call, xyToCell);
                        il.Emit(Cecil.Cil.OpCodes.Ret);
                    }, ts.Boolean);
                    var modPath = Path.Combine(dir, name + ".dll");
                    assembly.Write(modPath);
                    var type = ModAccess.LoadFrom(modPath).GetType("Probe")!;
                    Exception Fails(string methodName, object?[]? args)
                    {
                        try
                        {
                            type.GetMethod(methodName)!.Invoke(null, BindingFlags.DoNotWrapExceptions, null, args, null);
                        }
                        catch (Exception e) when (e is MissingMethodException or MissingFieldException)
                        {
                            return e;
                        }
                        throw new InvalidOperationException(methodName + " did not throw MissingMethodException or MissingFieldException");
                    }
                    void Expect(Exception e, Type exception, string message)
                    {
                        if (e.GetType() != exception || e.Message != message) throw new InvalidOperationException($"{e.GetType().Name} \"{e.Message}\", not {exception.Name} \"{message}\"");
                    }
                    var outer = Fails("Outer", null);
                    Expect(outer, typeof(MissingMethodException), "Method not found: void .SimMessages.SetCellProperties(int,byte)");
                    var first = outer.StackTrace!.Split('\n')[0].Trim();
                    if (!first.StartsWith("at Probe.Outer(", StringComparison.Ordinal)) throw new InvalidOperationException("Outer's first frame: " + first);
                    Expect(Fails("OnGeneric", [false]), typeof(MissingMethodException), $"Method not found: void .{generic.Name}.{gone}(!0[]&)");
                    Expect(Fails("GenericMethod", [false]), typeof(MissingMethodException), $"Method not found: void .SimMessages.{gone}<!0>(!!0)");
                    Expect(Fails("NestedField", [false]), typeof(MissingFieldException), $"Field not found: int .{nested.Name}.{gone} Due to: Could not find field in class");
                    var cell = type.GetMethod("Fine")!.Invoke(null, [false])!;
                    if ((int)cell != (int)game.GetType("Grid")!.GetMethod("XYToCell")!.Invoke(null, [2, 3])!) throw new InvalidOperationException("Fine changed");
                    return $"the methods throw Mono's MissingMethodException and MissingFieldException from their caller's frame ({first}); members found in the type or its bases are left";
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod's references to what the original has and ours lacks (MonoApiGaps)", ApiGapsCheck.Run);
            Check("A mod's signatures tagging a type as another kind than its own (MonoTypeKinds)", TypeKindsCheck.Run);
            Check("A mod's stack walk through a patched method (MonoStackFrames)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    // A game enum in another folder: writing the rewritten mod back resolves it, as
                    // the type of a parameter's default value (PLib's mods).
                    var api = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckStackApi"), typeof(object).Assembly);
                    var kindBuilder = api.DefineDynamicModule("OniMoonoCheckStackApi").DefineEnum("Kind", TypeAttributes.Public, typeof(int));
                    kindBuilder.DefineLiteral("A", 0);
                    kindBuilder.DefineLiteral("B", 1);
                    kindBuilder.CreateType();
                    Directory.CreateDirectory(Path.Combine(dir, "api"));
                    api.Save(Path.Combine(dir, "api", "OniMoonoCheckStackApi.dll"));
                    var kind = Assembly.LoadFrom(Path.Combine(dir, "api", "OniMoonoCheckStackApi.dll")).GetType("Kind")!;
                    // Walk() names the type that declares its caller's frame; Caller() calls it.
                    var mod = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckStackMod"), typeof(object).Assembly);
                    var type = mod.DefineDynamicModule("OniMoonoCheckStackMod").DefineType("Plants", TypeAttributes.Public);
                    var pick = type.DefineMethod("Pick", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [kind]);
                    pick.DefineParameter(1, ParameterAttributes.Optional | ParameterAttributes.HasDefault, "kind").SetConstant(Enum.ToObject(kind, 1));
                    pick.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
                    var walk = type.DefineMethod("Walk", MethodAttributes.Public | MethodAttributes.Static, typeof(string), Type.EmptyTypes);
                    walk.SetImplementationFlags(MethodImplAttributes.NoInlining);
                    var il = walk.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Newobj, typeof(System.Diagnostics.StackTrace).GetConstructor(Type.EmptyTypes)!);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
                    il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(System.Diagnostics.StackTrace).GetMethod("GetFrame")!);
                    il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(System.Diagnostics.StackFrame).GetMethod("GetMethod")!);
                    il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(MemberInfo).GetProperty("ReflectedType")!.GetMethod!);
                    il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(Type).GetProperty("FullName")!.GetMethod!);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var caller = type.DefineMethod("Caller", MethodAttributes.Public | MethodAttributes.Static, typeof(string), Type.EmptyTypes);
                    caller.SetImplementationFlags(MethodImplAttributes.NoInlining);
                    il = caller.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Call, walk);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    type.CreateType();
                    var modPath = Path.Combine(dir, "OniMoonoCheckStackMod.dll");
                    mod.Save(modPath);
                    var plants = ModAccess.LoadFrom(modPath).GetType("Plants")!;
                    new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(plants.GetMethod("Caller"), prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(NoOp)));
                    // Measured in the original player: a patched method's frame is declared by
                    // MonoMod's DynamicMethodDefinition.
                    var owner = (string)plants.GetMethod("Caller")!.Invoke(null, null)!;
                    return owner == "MonoMod.Utils.DynamicMethodDefinition" ? "the patched frame is declared by " + owner : throw new InvalidOperationException("the patched frame is declared by " + owner);
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("GetCallingAssembly in a mod's patches of constructors and a method (MonoCallingAssembly)", () =>
            {
                ModAccess.Install();
                PatchFrames.Install();
                MonoCallingAssembly.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    System.Reflection.Emit.ModuleBuilder Module(string name) => System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule(name);
                    // The game: Recipe(), Recipe(int), which calls Recipe(), and Run(int).
                    var recipe = Module("CheckCallingGame").DefineType("Recipe", TypeAttributes.Public);
                    var x = recipe.DefineField("X", typeof(int), FieldAttributes.Public);
                    var ctor0 = recipe.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
                    var il = ctor0.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); il.Emit(System.Reflection.Emit.OpCodes.Stfld, x);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var ctor1 = recipe.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(int)]);
                    il = ctor1.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Call, ctor0);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Stfld, x);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var runBuilder = recipe.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                    il = runBuilder.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); il.Emit(System.Reflection.Emit.OpCodes.Add); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var game = recipe.CreateType();
                    // The mod (Mod Origin Info's shape): postfixes and a prefix keep what
                    // GetCallingAssembly returns; Who returns it.
                    var modBuilder = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckCallingMod"), typeof(object).Assembly);
                    var type = modBuilder.DefineDynamicModule("OniMoonoCheckCallingMod").DefineType("Origins", TypeAttributes.Public);
                    var getCalling = typeof(Assembly).GetMethod(nameof(Assembly.GetCallingAssembly))!;
                    foreach (var name in new[] { "Ctor0", "Ctor1", "Run" })
                    {
                        var seen = type.DefineField(name, typeof(Assembly), FieldAttributes.Public | FieldAttributes.Static);
                        il = type.DefineMethod(name == "Run" ? "RunPrefix" : name + "Postfix", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes).GetILGenerator();
                        il.Emit(System.Reflection.Emit.OpCodes.Call, getCalling); il.Emit(System.Reflection.Emit.OpCodes.Stsfld, seen); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    }
                    il = type.DefineMethod("Who", MethodAttributes.Public | MethodAttributes.Static, typeof(Assembly), Type.EmptyTypes).GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Call, getCalling); il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    type.CreateType();
                    var modPath = Path.Combine(dir, "OniMoonoCheckCallingMod.dll");
                    modBuilder.Save(modPath);
                    var origins = ModAccess.LoadFrom(modPath).GetType("Origins")!;
                    var harmony = new HarmonyLib.Harmony("OniMoono.Host.Check");
                    harmony.Patch(game.GetConstructor(Type.EmptyTypes), postfix: new HarmonyLib.HarmonyMethod(origins.GetMethod("Ctor0Postfix")));
                    harmony.Patch(game.GetConstructor([typeof(int)]), postfix: new HarmonyLib.HarmonyMethod(origins.GetMethod("Ctor1Postfix")));
                    var run = game.GetMethod("Run")!;
                    harmony.Patch(run, prefix: new HarmonyLib.HarmonyMethod(origins.GetMethod("RunPrefix")));
                    // A method of a loaded (not emitted) assembly, called first through reflection.
                    var run2 = typeof(Program).GetMethod(nameof(ReflectedTarget), BindingFlags.NonPublic | BindingFlags.Static)!;
                    harmony.Patch(run2, prefix: new HarmonyLib.HarmonyMethod(origins.GetMethod("RunPrefix")));
                    // The caller, a third assembly: the game's constructors, Run directly and
                    // through reflection, and the mod's Who. Each keeps its frame: once optimized,
                    // CoreCLR makes a call right before `ret` a tail call, and Who's caller or
                    // Invoke's would be the host's lambda (Mono keeps the frame; D41's Not
                    // covered). The result is passed to GC.KeepAlive, which compiles to nothing.
                    var c = Module("CheckCallingCaller").DefineType("Caller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                    void Define(string name, Action<System.Reflection.Emit.ILGenerator> body)
                    {
                        var m = c.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(object), Type.EmptyTypes);
                        var emit = m.GetILGenerator();
                        body(emit);
                        emit.Emit(System.Reflection.Emit.OpCodes.Dup);
                        emit.Emit(System.Reflection.Emit.OpCodes.Call, typeof(GC).GetMethod(nameof(GC.KeepAlive))!);
                        emit.Emit(System.Reflection.Emit.OpCodes.Ret);
                    }
                    Define("MakeA", g => g.Emit(System.Reflection.Emit.OpCodes.Newobj, game.GetConstructor(Type.EmptyTypes)!));
                    Define("MakeB", g => { g.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_4); g.Emit(System.Reflection.Emit.OpCodes.Newobj, game.GetConstructor([typeof(int)])!); });
                    Define("CallRun", g => { g.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_2); g.Emit(System.Reflection.Emit.OpCodes.Call, run); g.Emit(System.Reflection.Emit.OpCodes.Box, typeof(int)); });
                    void DefineInvoke(string name, MethodInfo target) => Define(name, g =>
                    {
                        g.Emit(System.Reflection.Emit.OpCodes.Ldtoken, target);
                        g.Emit(System.Reflection.Emit.OpCodes.Call, typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
                        g.Emit(System.Reflection.Emit.OpCodes.Ldnull);
                        g.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); g.Emit(System.Reflection.Emit.OpCodes.Newarr, typeof(object));
                        g.Emit(System.Reflection.Emit.OpCodes.Dup); g.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
                        g.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_7); g.Emit(System.Reflection.Emit.OpCodes.Box, typeof(int)); g.Emit(System.Reflection.Emit.OpCodes.Stelem_Ref);
                        g.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
                    });
                    DefineInvoke("InvokeRun", run);
                    DefineInvoke("InvokeRun2", run2);
                    Define("CallWho", g => g.Emit(System.Reflection.Emit.OpCodes.Call, origins.GetMethod("Who")!));
                    var caller = c.CreateType();
                    Func<object> Call(string name) => caller.GetMethod(name)!.CreateDelegate<Func<object>>();
                    Assembly? Seen(string field) => (Assembly?)origins.GetField(field)!.GetValue(null);
                    void Clear() { foreach (var f in new[] { "Ctor0", "Ctor1", "Run" }) origins.GetField(f)!.SetValue(null, null); }
                    var wrong = new List<string>();
                    void Expect(string what, Assembly? seen) { if (seen?.GetName().Name != "CheckCallingCaller") wrong.Add($"{what}: {seen?.GetName().Name ?? "null"}"); }
                    var (makeA, makeB, callRun, invokeRun, callWho) = (Call("MakeA"), Call("MakeB"), Call("CallRun"), Call("InvokeRun"), Call("CallWho"));
                    // A first call through reflection goes through RuntimeMethodHandle.InvokeMethod,
                    // later ones through an emitted stub.
                    Clear(); Call("InvokeRun2")(); Expect("a prefix of a method first called through reflection", Seen("Run"));
                    // Past the callers' tier-up. Recipe()'s replacement tail-calls its postfix, so
                    // its frame is gone; Recipe(int)'s calls Recipe() and keeps its frame.
                    for (int round = 0; round < 30 && wrong.Count == 0; round++)
                    {
                        for (int i = 0; i < 100; i++)
                        {
                            Clear(); makeA(); Expect("Recipe()'s postfix", Seen("Ctor0"));
                            Clear(); makeB(); Expect("Recipe(int)'s postfix", Seen("Ctor1")); Expect("Recipe()'s postfix from Recipe(int)", Seen("Ctor0"));
                            Clear(); callRun(); Expect("Run's prefix", Seen("Run"));
                            Expect("Who", (Assembly)callWho());
                        }
                        Clear(); invokeRun(); Expect("Run's prefix through reflection", Seen("Run"));
                        System.Threading.Thread.Sleep(10);
                    }
                    return wrong.Count == 0 ? "the patches name the caller's assembly, as on Mono" : throw new InvalidOperationException(string.Join("; ", wrong.Distinct()));
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod's recursion without end (MonoStackOverflow)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    // Recurse calls itself, Ping and Pong call each other, Plain calls neither; each
                    // adds one after its call, so no call is a tail call.
                    var mod = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckOverflowMod"), typeof(object).Assembly);
                    var type = mod.DefineDynamicModule("OniMoonoCheckOverflowMod").DefineType("Deep", TypeAttributes.Public);
                    System.Reflection.Emit.MethodBuilder Define(string name) => type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
                    var recurse = Define("Recurse");
                    var ping = Define("Ping");
                    var pong = Define("Pong");
                    var plain = Define("Plain");
                    foreach (var (method, callee) in new[] { (recurse, recurse), (ping, pong), (pong, ping) })
                    {
                        var il = method.GetILGenerator();
                        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
                        il.Emit(System.Reflection.Emit.OpCodes.Add);
                        il.Emit(System.Reflection.Emit.OpCodes.Call, callee);
                        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
                        il.Emit(System.Reflection.Emit.OpCodes.Add);
                        il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    }
                    var plainIl = plain.GetILGenerator();
                    plainIl.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    plainIl.Emit(System.Reflection.Emit.OpCodes.Ret);
                    // Down(n) recurses n times and returns n.
                    var down = Define("Down");
                    var downIl = down.GetILGenerator();
                    var more = downIl.DefineLabel();
                    downIl.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    downIl.Emit(System.Reflection.Emit.OpCodes.Brtrue, more);
                    downIl.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
                    downIl.Emit(System.Reflection.Emit.OpCodes.Ret);
                    downIl.MarkLabel(more);
                    downIl.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    downIl.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
                    downIl.Emit(System.Reflection.Emit.OpCodes.Sub);
                    downIl.Emit(System.Reflection.Emit.OpCodes.Call, down);
                    downIl.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
                    downIl.Emit(System.Reflection.Emit.OpCodes.Add);
                    downIl.Emit(System.Reflection.Emit.OpCodes.Ret);
                    type.CreateType();
                    var modPath = Path.Combine(dir, "OniMoonoCheckOverflowMod.dll");
                    mod.Save(modPath);
                    int probed = MonoStackOverflow.RecursiveMethods(File.ReadAllBytes(modPath)).Count;
                    if (probed != 4) throw new InvalidOperationException($"{probed} methods found that can recurse, not 4");
                    var deep = ModAccess.LoadFrom(modPath).GetType("Deep")!;
                    if ((int)deep.GetMethod("Plain")!.Invoke(null, [7])! != 7) throw new InvalidOperationException("Plain changed");
                    string Overflow(string name)
                    {
                        try
                        {
                            deep.GetMethod(name)!.Invoke(null, [0]);
                        }
                        catch (TargetInvocationException e) when (e.InnerException is StackOverflowException s)
                        {
                            return s.Message;
                        }
                        throw new InvalidOperationException($"{name} returned");
                    }
                    // Measured in the original player: Mono's message.
                    const string mono = "The requested operation caused a stack overflow.";
                    foreach (var name in new[] { "Recurse", "Ping" })
                        if (Overflow(name) is var message && message != mono) throw new InvalidOperationException($"{name}: {message}");
                    // The game's JobManager workers have 128 KB stacks (issue #15): a short
                    // recursion runs there, one without end still throws.
                    string? onWorker = null;
                    var worker = new Thread(() =>
                    {
                        try
                        {
                            if ((int)deep.GetMethod("Down")!.Invoke(null, [20])! != 20) onWorker = "Down(20) changed";
                            else if (Overflow("Recurse") is var message && message != mono) onWorker = "Recurse: " + message;
                        }
                        catch (Exception e)
                        {
                            onWorker = e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message;
                        }
                    }, 131072);
                    worker.Start();
                    worker.Join();
                    if (onWorker != null) throw new InvalidOperationException("on a 128 KB thread, " + onWorker);
                    return "both recursions throw StackOverflowException with Mono's message, on a 128 KB thread too, where a short one runs; the process goes on";
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A special folder that does not exist (MonoFolderPaths)", () =>
            {
                MonoFolderPaths.Install();
                if (OperatingSystem.IsWindows()) return "Windows: unchanged";
                var home = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                var saved = (Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.GetEnvironmentVariable("XDG_DESKTOP_DIR"));
                try
                {
                    Environment.SetEnvironmentVariable("HOME", home);
                    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
                    Environment.SetEnvironmentVariable("XDG_DESKTOP_DIR", null);
                    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    // Harmony's debug log does this with it (FileLog.LogPath).
                    Directory.CreateDirectory(desktop);
                    return desktop == Path.Combine(home, "Desktop") ? "~/Desktop is given although it did not exist, as on Mono" : throw new InvalidOperationException($"got '{desktop}'");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("HOME", saved.Item1);
                    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", saved.Item2);
                    Environment.SetEnvironmentVariable("XDG_DESKTOP_DIR", saved.Item3);
                    try { Directory.Delete(home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A transpiler that finds a field by Mono's string of it (MonoMemberStrings)", () =>
            {
                MonoMemberStrings.Install();
                var texts = new[] { typeof(LitCell).GetField(nameof(LitCell.lux))!.ToString(), typeof(LitCell).GetField(nameof(LitCell.shade))!.ToString(),
                    typeof(LitCell).GetField(nameof(LitCell.Limit))!.ToString(), typeof(LitCell).GetField(nameof(LitCell.history))!.ToString(),
                    typeof(LitCell).GetEvent(nameof(LitCell.Changed))!.ToString(), typeof(LitCell).GetMethod(nameof(LitCell.Lit))!.ToString() };
                var expected = new[] { "System.Int32 lux", "OniMoono.Host.Program+LitCell+Shade shade", "System.Int32 Limit", "System.Single[] history",
                    "OniMoono.Host.Program+LitCell+Handler Changed", "Boolean Lit()" };
                if (!texts.SequenceEqual(expected)) throw new InvalidOperationException($"printed {string.Join(", ", texts)}");
                // As Darkness Not Excluded Relit's patch of Workable.GetEfficiencyMultiplier does.
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(typeof(LitCell).GetMethod(nameof(LitCell.Lit)),
                    transpiler: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(BrightLux)));
                return new LitCell().Lit() ? "fields and events print their type's full name, as on Mono, and the transpiler finds its field" : throw new InvalidOperationException("the transpiler found no field");
            });
            Check("A nested type named with '/' by Assembly.GetType (MonoNestedTypeNames)", () =>
            {
                MonoNestedTypeNames.Install();
                // As Delivery Temperature Limit finds FetchAreaChore/StatesInstance/<>c__DisplayClass17_0.
                var assembly = typeof(Program).Assembly;
                var found = new[] { assembly.GetType("OniMoono.Host.Program/LitCell/Shade"), assembly.GetType("OniMoono.Host.Program/LitCell+Shade", false),
                    assembly.GetType("oniMoono.host.program/litcell/shade", false, true) };
                if (found.Any(t => t != typeof(LitCell.Shade))) throw new InvalidOperationException($"found {string.Join(", ", found.Select(t => t?.FullName ?? "null"))}");
                if (assembly.GetType("OniMoono.Host.Program/Missing") != null) throw new InvalidOperationException("found a type that does not exist");
                try
                {
                    assembly.GetType("OniMoono.Host.Program/Missing", throwOnError: true);
                    throw new InvalidOperationException("a missing type did not throw");
                }
                catch (TypeLoadException)
                {
                }
                return "found as on Mono";
            });
            Check("Type.GetType by a bare name in a patched method (MonoTypeByName)", () =>
            {
                MonoTypeByName.Install();
                // As SaveLoadRoot.LoadInternal, once a mod patches it, finds an optional component.
                var module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CheckTypeByNameGame"), System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("CheckTypeByNameGame");
                module.DefineType("CheckPort", TypeAttributes.Public).CreateType();
                var root = module.DefineType("CheckRoot", TypeAttributes.Public);
                var find = root.DefineMethod("Find", MethodAttributes.Public | MethodAttributes.Static, typeof(Type), [typeof(string), typeof(bool)]);
                var il = find.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetType), [typeof(string), typeof(bool)])!);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                // Load calls Find, as SaveLoadRoot.Load calls LoadInternal.
                var load = root.DefineMethod("Load", MethodAttributes.Public | MethodAttributes.Static, typeof(Type), [typeof(string), typeof(bool)]);
                il = load.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Call, find);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                var rootType = root.CreateType();
                var method = rootType.GetMethod("Find")!;
                if (method.CreateDelegate<Func<string, bool, Type?>>()("CheckPort", false)?.Assembly != rootType.Assembly) throw new InvalidOperationException("the unpatched method did not find its own type");
                var Find = rootType.GetMethod("Load")!.CreateDelegate<Func<string, bool, Type?>>();
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(method, postfix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(NoOpPostfix)));
                var found = new[] { Find("CheckPort", false), Find("CheckPort", true), Find("System.String", false) };
                if (found[0]?.Assembly != rootType.Assembly || found[1] != found[0] || found[2] != typeof(string))
                    throw new InvalidOperationException($"found {string.Join(", ", found.Select(t => t?.AssemblyQualifiedName ?? "null"))}");
                if (Find("CheckMissing", false) != null) throw new InvalidOperationException("found a type that does not exist");
                try
                {
                    Find("CheckMissing", true);
                    throw new InvalidOperationException("a missing type did not throw");
                }
                catch (TypeLoadException)
                {
                }
                return "the patched method finds its assembly's type, as on Mono";
            });
            Check("Fields and properties of a class whose method a mod patches, in Mono's order (MonoReflectionOrder)", () =>
            {
                MonoReflectionOrder.Install();
                // As Critter Condo Save Fix patches CritterCondoInteractMontior.FindCondoTarget:
                // MonoMod re-sorts the cached members of the types the method's IL names.
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(typeof(OrderLeaf).GetMethod(nameof(OrderLeaf.Sum)),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(CountOrder)));
                const BindingFlags every = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
                string Names(IEnumerable<MemberInfo> members) => string.Join(" ", members.Select(m => m.Name));
                var lists = new[]
                {
                    Names(typeof(OrderLeaf).GetFields(every)),
                    Names(typeof(OrderLeaf).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)),
                    Names(typeof(OrderLeaf).GetFields()),
                    Names(typeof(OrderLeaf).GetProperties(every)),
                };
                // The original's order: the class's own members in metadata order, then its base's.
                var expected = new[]
                {
                    "leafA leafStatic LeafConst leafB <LeafP>k__BackingField <LeafS>k__BackingField rootA rootStatic RootConst rootB",
                    "leafA leafB <LeafP>k__BackingField rootA rootB",
                    "leafA leafStatic LeafConst leafB rootA rootB",
                    "LeafP LeafS RootP",
                };
                if (!lists.SequenceEqual(expected)) throw new InvalidOperationException($"listed {string.Join("; ", lists)}");
                return new OrderLeaf().Sum() == 0 && orderPatched > 0 ? "a class's own fields and properties, then its base's, each in metadata order, as on Mono" : throw new InvalidOperationException("the prefix did not run");
            });
            Check("A mod's string-keyed ConcurrentDictionary enumerated while it grows (MonoConcurrentDictionaries)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    // A mod making a dictionary as PLib's registry does: (concurrencyLevel, capacity).
                    var cd = typeof(System.Collections.Concurrent.ConcurrentDictionary<string, object>);
                    var mod = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckDictionaries"), typeof(object).Assembly);
                    var type = mod.DefineDynamicModule("OniMoonoCheckDictionaries").DefineType("Registry", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                    var sized = type.DefineMethod("Sized", MethodAttributes.Public | MethodAttributes.Static, cd, [typeof(int), typeof(int)]).GetILGenerator();
                    sized.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    sized.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                    sized.Emit(System.Reflection.Emit.OpCodes.Newobj, cd.GetConstructor([typeof(int), typeof(int)])!);
                    sized.Emit(System.Reflection.Emit.OpCodes.Ret);
                    type.CreateType();
                    var modPath = Path.Combine(dir, "OniMoonoCheckDictionaries.dll");
                    mod.Save(modPath);
                    var registry = ModAccess.LoadFrom(modPath).GetType("Registry")!;
                    System.Collections.Concurrent.ConcurrentDictionary<string, object> Make(int concurrencyLevel, int capacity) =>
                        (System.Collections.Concurrent.ConcurrentDictionary<string, object>)registry.GetMethod("Sized")!.Invoke(null, [concurrencyLevel, capacity])!;
                    // Mono's order, from its ConcurrentDictionary (Managed/mscorlib.dll): buckets
                    // ascending, a bucket's newest entry first; bucket = (hash & 0x7fffffff) % size.
                    static string MonoOrder(IEnumerable<string> added, int size) => string.Join(" ", added
                        .Select((key, i) => (key, i, bucket: (MonoStringHash.Compute(key) & 0x7fffffff) % size))
                        .OrderBy(e => e.bucket).ThenByDescending(e => e.i).Select(e => e.key));
                    var keys = new[] { "PeterHan.PLib.Core.PLibCorePatches", "PeterHan.PLib.Database.PLocalization", "PeterHan.PLib.Buildings.PBuildingManager",
                        "PeterHan.PLib.AVC.PVersionCheck", "PeterHan.PLib.Options.POptions", "PeterHan.PLib.Lighting.PLightManager", "PeterHan.PLib.Actions.PActionManager",
                        "PeterHan.PLib.Detours.PDetours", "PeterHan.PLib.UI.PUIElements", "PeterHan.PLib.Codex.PCodexManager", "Alpha", "Beta", "Gamma" };
                    // PLib's sizes; 13 keys stay within Mono's lock budget (capacity / concurrencyLevel).
                    foreach (var (concurrencyLevel, capacity) in new[] { (2, 32), (2, 64), (1, 17) })
                    {
                        var dictionary = Make(concurrencyLevel, capacity);
                        foreach (var key in keys) dictionary.TryAdd(key, key);
                        var order = string.Join(" ", dictionary.Select(e => e.Key));
                        var mono = MonoOrder(keys, Math.Max(capacity, concurrencyLevel));
                        if (order != mono) throw new InvalidOperationException($"({concurrencyLevel}, {capacity}): order {order}; Mono's is {mono}");
                        if (order != string.Join(" ", dictionary.Keys)) throw new InvalidOperationException("Keys differ from the enumeration");
                    }
                    // PLib 4.16's registry: PBuildingManager's Initialize registers PPatchManager
                    // while the registry is enumerated; Mono's order reaches it afterwards.
                    var plib = Make(2, 32);
                    foreach (var key in keys.Take(5)) plib.TryAdd(key, key);
                    var visited = new List<string>();
                    foreach (var entry in plib)
                    {
                        visited.Add(entry.Key);
                        if (entry.Key.EndsWith(".PBuildingManager", StringComparison.Ordinal)) plib.TryAdd("PeterHan.PLib.PatchManager.PPatchManager", "");
                    }
                    if (!visited.Contains("PeterHan.PLib.PatchManager.PPatchManager")) throw new InvalidOperationException("PPatchManager, added during the enumeration, was not reached: " + string.Join(" ", visited));
                    // Many keys: the table grows and every key stays reachable.
                    var big = Make(2, 32);
                    for (int i = 0; i < 5000; i++) big[$"key{i}"] = i;
                    for (int i = 0; i < 5000; i++) if (!big.TryGetValue($"key{i}", out var v) || (int)v != i) throw new InvalidOperationException($"key{i} lost");
                    return "enumerated in Mono's bucket order; PPatchManager, added during PLib's registry walk, is reached";
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod's conversions of NaN and out-of-range floats to integers (MonoConversions)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    var mod = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckConversions"), typeof(object).Assembly);
                    var type = mod.DefineDynamicModule("OniMoonoCheckConversions").DefineType("Convert", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                    void Define(string name, Type result, Type[] parameters, System.Reflection.Emit.OpCode conversion)
                    {
                        var il = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, result, parameters).GetILGenerator();
                        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                        il.Emit(conversion);
                        il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    }
                    Define("I4F", typeof(int), [typeof(float)], System.Reflection.Emit.OpCodes.Conv_I4);
                    Define("I4D", typeof(int), [typeof(double)], System.Reflection.Emit.OpCodes.Conv_I4);
                    Define("U1F", typeof(int), [typeof(float)], System.Reflection.Emit.OpCodes.Conv_U1);
                    Define("U4D", typeof(uint), [typeof(double)], System.Reflection.Emit.OpCodes.Conv_U4);
                    Define("U8D", typeof(ulong), [typeof(double)], System.Reflection.Emit.OpCodes.Conv_U8);
                    Define("I8F", typeof(long), [typeof(float)], System.Reflection.Emit.OpCodes.Conv_I8);
                    Define("IF", typeof(nint), [typeof(float)], System.Reflection.Emit.OpCodes.Conv_I);
                    // An integer's conversion stays as it is.
                    Define("Long", typeof(int), [typeof(long)], System.Reflection.Emit.OpCodes.Conv_I4);
                    // A product, a generic type's float result, and floats of two types meeting at
                    // a join.
                    var product = type.DefineMethod("Product", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(float), typeof(float)]).GetILGenerator();
                    product.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    product.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                    product.Emit(System.Reflection.Emit.OpCodes.Mul);
                    product.Emit(System.Reflection.Emit.OpCodes.Conv_I4);
                    product.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var listed = type.DefineMethod("Listed", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(List<float>)]).GetILGenerator();
                    listed.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    listed.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
                    listed.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(List<float>).GetMethod("get_Item")!);
                    listed.Emit(System.Reflection.Emit.OpCodes.Conv_I4);
                    listed.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var joined = type.DefineMethod("Joined", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(bool), typeof(float), typeof(double)]).GetILGenerator();
                    var second = joined.DefineLabel();
                    var join = joined.DefineLabel();
                    joined.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    joined.Emit(System.Reflection.Emit.OpCodes.Brtrue_S, second);
                    joined.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                    joined.Emit(System.Reflection.Emit.OpCodes.Br_S, join);
                    joined.MarkLabel(second);
                    joined.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
                    joined.MarkLabel(join);
                    joined.Emit(System.Reflection.Emit.OpCodes.Conv_I4);
                    joined.Emit(System.Reflection.Emit.OpCodes.Ret);
                    type.CreateType();
                    var modPath = Path.Combine(dir, "OniMoonoCheckConversions.dll");
                    mod.Save(modPath);
                    using (var module = Mono.Cecil.ModuleDefinition.ReadModule(modPath))
                    {
                        int found = MonoConversions.RewriteModule(module, out _, out int unsure);
                        if (found != 10 || unsure != 0) throw new InvalidOperationException($"{found} conversions of floats found ({unsure} not followed), not 10");
                    }
                    var convert = ModAccess.LoadFrom(modPath).GetType("Convert")!;
                    object Call(string name, object[] args) => convert.GetMethod(name)!.Invoke(null, args)!;
                    // The original player's values (UnityProbe convert scenario).
                    var cases = new (string Name, object[] Args, object Mono)[]
                    {
                        ("I4F", [float.NaN], int.MinValue), ("I4F", [float.PositiveInfinity], int.MinValue), ("I4F", [-1.5f], -1), ("I4F", [3e9f], int.MinValue),
                        ("I4D", [double.NegativeInfinity], int.MinValue), ("I4D", [2147483647.9], int.MaxValue), ("I4D", [-2147483648.9], int.MinValue),
                        ("U1F", [300f], 44), ("U1F", [float.NaN], 0), ("U4D", [-1.0], 4294967295u), ("U4D", [5e9], 705032704u),
                        ("U8D", [double.NegativeInfinity], 9223372036854775808ul), ("U8D", [double.NaN], 0ul), ("U8D", [1e19], 10000000000000000000ul),
                        ("I8F", [float.NaN], long.MinValue), ("IF", [-1f], unchecked((nint)4294967295L)), ("IF", [float.NaN], unchecked((nint)2147483648L)),
                        ("Long", [0x100000005L], 5), ("Product", [1e30f, 1e10f], int.MinValue), ("Product", [1.5f, 3f], 4),
                        ("Listed", [new List<float> { float.NaN }], int.MinValue), ("Joined", [false, 3e9f, 0.0], int.MinValue),
                        ("Joined", [true, 0f, double.NaN], int.MinValue), ("Joined", [true, 0f, -7.5], -7),
                    };
                    foreach (var (name, args, mono) in cases)
                        if (Call(name, args) is var got && !Equals(got, mono)) throw new InvalidOperationException($"{name}({string.Join(", ", args)}) gave {got}; Mono gives {mono}");
                    return $"{cases.Length} conversions give the original's values; an integer's conversion is untouched";
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("A mod's directory listings (MonoDirectoryListing)", () =>
            {
                ModAccess.Install();
                var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
                try
                {
                    // A mod listing folders as True Tiles does, a DirectoryInfo's recursive listing,
                    // and a delegate of Directory.GetFiles.
                    var mod = new System.Reflection.Emit.PersistedAssemblyBuilder(new AssemblyName("OniMoonoCheckListings"), typeof(object).Assembly);
                    var type = mod.DefineDynamicModule("OniMoonoCheckListings").DefineType("Listings", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                    var folders = type.DefineMethod("Folders", MethodAttributes.Public | MethodAttributes.Static, typeof(string[]), [typeof(string)]).GetILGenerator();
                    folders.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    folders.Emit(System.Reflection.Emit.OpCodes.Call, typeof(Directory).GetMethod(nameof(Directory.GetDirectories), [typeof(string)])!);
                    folders.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var files = type.DefineMethod("Files", MethodAttributes.Public | MethodAttributes.Static, typeof(IEnumerable<FileInfo>), [typeof(DirectoryInfo)]).GetILGenerator();
                    files.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    files.Emit(System.Reflection.Emit.OpCodes.Ldstr, "*");
                    files.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
                    files.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(DirectoryInfo).GetMethod(nameof(DirectoryInfo.EnumerateFiles), [typeof(string), typeof(SearchOption)])!);
                    files.Emit(System.Reflection.Emit.OpCodes.Ret);
                    var lister = type.DefineMethod("Lister", MethodAttributes.Public | MethodAttributes.Static, typeof(Func<string, string[]>), Type.EmptyTypes).GetILGenerator();
                    lister.Emit(System.Reflection.Emit.OpCodes.Ldnull);
                    lister.Emit(System.Reflection.Emit.OpCodes.Ldftn, typeof(Directory).GetMethod(nameof(Directory.GetFiles), [typeof(string)])!);
                    lister.Emit(System.Reflection.Emit.OpCodes.Newobj, typeof(Func<string, string[]>).GetConstructors()[0]);
                    lister.Emit(System.Reflection.Emit.OpCodes.Ret);
                    type.CreateType();
                    var modPath = Path.Combine(dir, "OniMoonoCheckListings.dll");
                    mod.Save(modPath);
                    using (var module = Mono.Cecil.ModuleDefinition.ReadModule(modPath))
                    {
                        int found = MonoDirectoryListing.RewriteModule(module);
                        int expected = OperatingSystem.IsWindows() ? 0 : 3;
                        if (found != expected) throw new InvalidOperationException($"{found} listings found, not {expected}");
                    }
                    // True Tiles' packs, made in an order that is not sorted; a folder of each.
                    var packs = Path.Combine(dir, "true_tiles_addon");
                    foreach (var pack in new[] { "Specialty", "Aquatic", "Prehistoric", "aquatic", "_Shared" })
                    {
                        Directory.CreateDirectory(Path.Combine(packs, pack, "textures"));
                        File.WriteAllText(Path.Combine(packs, pack, "tiles.json"), "{}");
                    }
                    var listings = ModAccess.LoadFrom(modPath).GetType("Listings")!;
                    string Names(IEnumerable<string> paths) => string.Join(" ", paths.Select(p => Path.GetRelativePath(packs, p)));
                    string Expect(string order) => OperatingSystem.IsWindows() ? null! : order;
                    var listed = Names((string[])listings.GetMethod("Folders")!.Invoke(null, [packs])!);
                    // Mono's order: each folder's entries by their UTF-8 bytes, breadth first.
                    if (Expect("Aquatic Prehistoric Specialty _Shared aquatic") is { } folderOrder && listed != folderOrder)
                        throw new InvalidOperationException($"GetDirectories gave {listed}; Mono gives {folderOrder}");
                    var recursive = Names(((IEnumerable<FileInfo>)listings.GetMethod("Files")!.Invoke(null, [new DirectoryInfo(packs)])!).Select(f => f.FullName));
                    if (Expect("Aquatic/tiles.json Prehistoric/tiles.json Specialty/tiles.json _Shared/tiles.json aquatic/tiles.json") is { } fileOrder && recursive != fileOrder)
                        throw new InvalidOperationException($"EnumerateFiles(*, AllDirectories) gave {recursive}; Mono gives {fileOrder}");
                    File.WriteAllText(Path.Combine(packs, "b"), "");
                    File.WriteAllText(Path.Combine(packs, "B"), "");
                    File.WriteAllText(Path.Combine(packs, "a"), "");
                    var delegated = Names(((Func<string, string[]>)listings.GetMethod("Lister")!.Invoke(null, null)!)(packs));
                    if (Expect("B a b") is { } delegateOrder && delegated != delegateOrder)
                        throw new InvalidOperationException($"a delegate of GetFiles gave {delegated}; Mono gives {delegateOrder}");
                    return OperatingSystem.IsWindows() ? "left as they are on Windows" : $"folders {listed}; files {recursive}; through a delegate {delegated}";
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
            Check("Bubble archetype ids of two elements (MonoValueTypeHashes)", () =>
            {
                MonoValueTypeHashes.Install();
                var game = Assembly.Load("Assembly-CSharp");
                var archetype = game.GetType("BubbleManager+Archetype", true)!;
                var simHashes = game.GetType("SimHashes", true)!;
                var getId = archetype.GetMethod("GetId")!;
                int Id(string element)
                {
                    var value = Activator.CreateInstance(archetype, BindingFlags.Public | BindingFlags.Instance, null, [new UnityEngine.Vector2(0f, 1f), Enum.Parse(simHashes, element)], null)!;
                    var id = getId.Invoke(value, null)!;
                    return (int)id.GetType().GetField("hashCode")!.GetValue(id)!;
                }
                int oxygen = Id("Oxygen"), methane = Id("Methane");
                // The fields' XOR, as Mono's ValueType.GetHashCode computes it (velocity, element,
                // alphaFadeSpeed = |velocity|).
                int expected = new UnityEngine.Vector2(0f, 1f).GetHashCode() ^ (int)Enum.Parse(simHashes, "Methane") ^ 1f.GetHashCode();
                if (methane != expected) throw new InvalidOperationException($"Methane's id is {methane}, Mono's fields give {expected}");
                return oxygen != methane ? $"Oxygen {oxygen}, Methane {methane}" : throw new InvalidOperationException($"both are {oxygen}");
            });
            // Last: once a mod patches enum formatting, enum names go through it for the process.
            Check("A Harmony patch of Enum.ToString() alone (MonoEnumFormat)", () =>
            {
                MonoEnumFormat.Install();
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(HarmonyLib.AccessTools.Method(typeof(Enum), nameof(Enum.ToString), Type.EmptyTypes),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(NamedDay)));
                var value = (DayOfWeek)100;
                var texts = new[] { value.ToString(), value.ToString("G"), value.ToString((string?)null), string.Format("{0}", value), string.Format("{0:G}", value), new StringBuilder().AppendFormat("{0}", value).ToString() };
                foreach (var text in texts)
                    if (text != "Named") throw new InvalidOperationException($"formatted as {string.Join(", ", texts)}");
                return value.ToString("D") == "100" ? "every \"G\" formatting path reaches it" : throw new InvalidOperationException("\"D\" renamed");
            });
            Check("A Harmony patch of Mono's Enum.InternalFormat (MonoEnumFormat)", () =>
            {
                // As [HarmonyPatch(typeof(Enum), "InternalFormat")] finds it.
                var internalFormat = HarmonyLib.AccessTools.DeclaredMethod(typeof(Enum), "InternalFormat")
                    ?? throw new InvalidOperationException("no InternalFormat to patch");
                new HarmonyLib.Harmony("OniMoono.Host.Check").Patch(internalFormat,
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(NamedValue)));
                var value = (AttributeTargets)0x40000000;
                // The game's and mods' code targets .NET Framework, where an interpolated string is
                // string.Format (this host's would use .NET's interpolation handler instead).
                var texts = new[] { value.ToString(), value.ToString("G"), string.Format("{0}", value), string.Concat("", value), Enum.Format(typeof(AttributeTargets), value, "G"), Enum.Format(typeof(AttributeTargets), 0x40000000, "G") };
                foreach (var text in texts)
                    if (text != "Named") throw new InvalidOperationException($"formatted as {string.Join(", ", texts)}");
                try
                {
                    Enum.Format(typeof(AttributeTargets), value, "");
                    throw new InvalidOperationException("Enum.Format accepted an empty format");
                }
                catch (FormatException) { }
                return AttributeTargets.Class.ToString() == "Class" ? "every \"G\" formatting path names the value" : throw new InvalidOperationException("other values renamed");
            });
            return failures;
        }

        /// CheckRuntimeServices' P/Invokes: one whose export no library has, ones whose exports exist.
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "oni_moono_missing_export")]
        static extern void MissingExport();

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getpid")]
        static extern int OtherExport();

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "endpwent")]
        static extern void SkippedExport();

        static int skipped, skippedExport;
        static bool SkipMissing() { skipped++; return false; }
        static bool SkipExport() { skippedExport++; return false; }

        /// CheckRuntimeServices' transpiler: adds 41 before the ret through a closure's dynamic
        /// method, as CodeInstruction.CallClosure makes one for a lambda with captured state.
        static IEnumerable<HarmonyLib.CodeInstruction> ClosureTranspiler(IEnumerable<HarmonyLib.CodeInstruction> instructions)
        {
            int offset = 41;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == System.Reflection.Emit.OpCodes.Ret)
                    yield return HarmonyLib.CodeInstruction.CallClosure<Func<int, int>>(x => x + offset);
                yield return instruction;
            }
        }

        /// A dynamic method calling one of Program's P/Invokes, compiled optimized in any build.
        static T Caller<T>(string pinvoke) where T : Delegate
        {
            var target = typeof(Program).GetMethod(pinvoke, BindingFlags.NonPublic | BindingFlags.Static)!;
            // Anonymously hosted: one owned by a type of this assembly is compiled as debug code
            // in a debug build.
            var caller = new System.Reflection.Emit.DynamicMethod("Call" + pinvoke, target.ReturnType, Type.EmptyTypes, restrictedSkipVisibility: true);
            var il = caller.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Call, target); il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return caller.CreateDelegate<T>();
        }

        sealed class SharedPool<T> where T : class
        {
            public T? last;
            public T Draw(T item) { last = item; return item; }
        }

        /// CheckRuntimeServices' MonoMemberStrings check: a field of each kind CoreCLR prints by
        /// its type's short name, and a method a transpiler finds one in.
        sealed class LitCell
        {
            public sealed class Shade { }
            public delegate void Handler();
            public const int Limit = 10;
            public int lux = 1;
            public Shade? shade = null;
            public float[]? history = null;
            public event Handler? Changed { add { } remove { } }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public bool Lit() => lux > Limit;
        }

        /// CheckRuntimeServices' MonoReflectionOrder check: instance, static and constant fields
        /// interleaved, in a class and its base, and a method a mod patches that names them.
        class OrderRoot
        {
            public int rootA = 0;
            public static int rootStatic = 0;
            public const int RootConst = 3;
            public int rootB = 0;
            public int RootP { get; set; }
        }

        sealed class OrderLeaf : OrderRoot
        {
            public int leafA = 0;
            public static int leafStatic = 0;
            public const int LeafConst = 2;
            public int leafB = 0;
            public int LeafP { get; set; }
            public static int LeafS { get; set; }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public int Sum() => leafA + leafB + rootA + rootB + LeafP + RootP + leafStatic + rootStatic + LeafS;
        }

        static IEnumerable<HarmonyLib.CodeInstruction> BrightLux(IEnumerable<HarmonyLib.CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
                yield return instruction.opcode == System.Reflection.Emit.OpCodes.Ldfld && instruction.operand.ToString() == "System.Int32 lux"
                    ? new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Call, typeof(Program).GetMethod(nameof(Bright), BindingFlags.NonPublic | BindingFlags.Static))
                    : instruction;
        }

        static int Bright(LitCell cell) => 1000;

        static int orderPatched;
        static void CountOrder() => orderPatched++;

        static int filterPrefixed, filterPostfixed;
        static void CountFilterPrefix() => filterPrefixed++;
        static void AddFilterPostfix(ref int __result) { filterPostfixed++; __result += 100; }
        static void CountFaultPostfix() => filterPostfixed++;

        static int prefixedB, prefixedC, countedA;
        static void CountA() => countedA++;
        static void CountB() => prefixedB++;
        static void CountC() => prefixedC++;
        static int prefixedG;
        static void CountG() => prefixedG++;

        static int drawn;
        static void CountDraw() => drawn++;

        static int stringPatched, uriPatched;
        static void CountStringParam() => stringPatched++;
        static void CountUriParam() => uriPatched++;

        /// CheckRuntimeServices' method whose transpiled copy enters its try block with a value on the stack.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int Guarded(int value)
        {
            try
            {
                value = checked(value * 2);
            }
            finally
            {
                guardedRuns++;
            }
            return value;
        }

        static int guardedRuns;

        /// The transpiler: a value pushed first and never used, as Better Info Cards leaves one.
        static IEnumerable<HarmonyLib.CodeInstruction> LeaveValueBehind(IEnumerable<HarmonyLib.CodeInstruction> instructions) =>
            instructions.Prepend(new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ldc_I4_7));

        static int SumAll(List<int> values)
        {
            int sum = 0;
            foreach (var value in values) sum += value;
            return sum;
        }

        static bool IsOdd(int value) => (value & 1) != 0;

        /// The transpiler: as Fixes and Tweaks' patch of Timelapser.SetPostionAndOrtho, a test of
        /// the current item (kept on the stack by a dup) that branches back to the loop's head
        /// when it fails, leaving the item behind.
        static IEnumerable<HarmonyLib.CodeInstruction> SkipEven(IEnumerable<HarmonyLib.CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            int head = codes.FindIndex(c => c.opcode == System.Reflection.Emit.OpCodes.Br || c.opcode == System.Reflection.Emit.OpCodes.Br_S);
            int current = codes.FindIndex(c => c.operand is MethodInfo { Name: "get_Current" });
            if (head < 0 || current < 0) throw new InvalidOperationException("the loop was not found");
            codes.InsertRange(current + 1, [
                new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Dup),
                new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Call, typeof(Program).GetMethod(nameof(IsOdd), BindingFlags.NonPublic | BindingFlags.Static)),
                new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Brfalse_S, codes[head].operand),
            ]);
            return codes;
        }

        /// CheckRuntimeServices' patch of Enum.ToString(), as element mods write theirs.
        static void NoOp() { }

        static void NoOpPostfix() { }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int ReflectedTarget(int v) => v + 2;

        static void NamedDay(Enum __instance, ref string __result)
        {
            if (__instance is DayOfWeek day && (int)day == 100) __result = "Named";
        }

        /// CheckRuntimeServices' patch of Enum.InternalFormat, as element mods write theirs.
        static void NamedValue(Type eT, object value, ref string __result)
        {
            if (eT == typeof(AttributeTargets) && (AttributeTargets)value == (AttributeTargets)0x40000000) __result = "Named";
        }
    }

    internal sealed class Options
    {
        public string GameDir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "OxygenNotIncluded")
            : OperatingSystem.IsMacOS()
            ? OniMoono.Engine.Platform.GameLayout.DefaultMacSteamInstall(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux");
        /// Test runs (`--run-home DIR`): the player's per-user data (Unity's persistent and cache
        /// paths, and on Windows the Documents and local application data folders the game reads)
        /// under DIR instead of the user's (RunHome).
        public string? RunHome;
        /// Test runs (`--audio-wav DIR`): the game's audio written to DIR/fmodoutput.wav instead of
        /// the sound device (AudioCapture).
        public string? AudioWav;
        public string? LogPath;
        public bool Headless;
        public bool BatchMode;
        public bool CheckTypes;
        /// `--game-tweak` values, in order (GameTweaks, EffectiveOptions): when any is given, they
        /// alone choose the game tweaks, over the Moono Tweaks settings.
        public readonly List<string> GameTweaks = new();
        public bool CheckAssets;
        /// With --check-assets: where to list the script classes the data uses.
        public string? ListDataScripts;
        public bool CheckBind;
        public bool CheckShaders;
        /// --check-natives: the P/Invokes against the install's plugins (NativeCheck).
        public bool CheckNatives;
        /// With --check-natives: the architecture checked (`--native-arch x86_64|arm64`; the
        /// process's by default) and a host folder whose natives are checked (`--native-dir`).
        public int? NativeArch;
        public string? NativeDir;
        public string? DumpShader;
        public string? DumpScript;
        public int Frames = -1;
        /// --max-frame-rate N[:FRAMES]: an upper bound on the frame rate whatever the game asks
        /// for (scripted runs whose schedules count frames), for the first FRAMES frames when
        /// given; 0 for none.
        public int MaxFrameRate, MaxFrameRateFrames = int.MaxValue;
        /// Diagnostics: log every first-chance exception with IL offsets per frame (game code
        /// has no symbols, so offsets locate the throwing instruction).
        public bool TraceExceptions;
        /// Diagnostics (--gl-debug): the driver's GL debug output in the log (debug context).
        public bool GlDebug;
        /// --background: the window takes no focus and the desktop's mouse and keyboard reach
        /// nothing (test runs on a desktop someone is using). The player counts as focused, with
        /// the pointer over the window at (0, 0), so the game hovers there every frame: in U57 at
        /// 1280x720 that cost EventSystem.Update about 1.3 ms a frame and the frame rate about a
        /// quarter. Frame-rate runs use --no-focus instead.
        public bool Background;
        /// --no-focus: the window opens without taking the focus, and takes input once focused
        /// (Windows test runs on the user's second monitor: scripts/run-windows.sh).
        public bool NoFocus;
        /// --splash moo|none: the player's own loading splash (Gassy Moos falling while it loads),
        /// over the Moono Tweaks settings; null when not given (then the effective value).
        public string? Splash;
        /// --frame-stats: average frame phase times in the log (PlayerLoop.FrameStats), over the
        /// Moono Tweaks settings.
        public bool FrameStats;
        /// --log-paths hidden|full: personal paths in Player.log and the console replaced, or as
        /// logged (LogScrubber), over the Moono Tweaks settings; null when not given.
        public string? LogPaths;
        /// --gpu default|discrete|integrated: the GPU of a laptop with two (GpuChoice), over the
        /// Moono Tweaks settings; null when not given.
        public string? Gpu;
        /// --dxgi on|off: Windows, presenting through DXGI (OniMoono.Desktop.Gl.DxgiTarget), over
        /// the Moono Tweaks settings; null when not given (then the effective value).
        public bool? Dxgi;
        /// Diagnostics (--dxgi-fault WHAT): a made-up DXGI failure (DxgiSwapChain.Fault).
        public string? DxgiFault;
        /// --no-tweaks-menu: the main menu without our Moono Tweaks button, as the original's (test
        /// runs compared with the original game: scripts/e2e.py).
        public bool NoTweaksMenu;
        /// Diagnostics (--log-pointer-hits): the UI element under the mouse, logged when it changes.
        public bool LogPointerHits;
        /// Diagnostics (--vsync-count N): the swap interval regardless of the quality settings.
        public int VSyncCount = -1;
        /// Diagnostics (--gpu-profile): GPU time by operation with the frame counters.
        public bool GpuProfile;
        /// Diagnostics (--no-bindless): textures bound to units even where bindless textures exist.
        public bool NoBindless;
        /// --bindless: bindless textures even on AMD's drivers, which bind to units by default.
        public bool Bindless;
        public readonly ScriptedInput Input = new();
        // Unity's standalone player arguments.
        public int ScreenWidth = -1, ScreenHeight = -1;
        public int ScreenFullscreen = -1;
        public int Monitor;
        public string? WindowMode;
        /// Unity's -logFile PATH: the log's file when --log names none.
        public string? UnityLogFile;
        /// Unity arguments ignored or malformed, for the log once it is open.
        public readonly List<string> ArgumentNotes = new();
        /// Development: "frame:path.png[:superSize]" calls ScreenCapture.CaptureScreenshot in
        /// that frame.
        public readonly List<(int Frame, string Path, int Scale)> Captures = new();
        /// --probe-ui FRAME:X,Y: logs the canvas elements drawn at a window point (top-left
        /// origin, as --input) as of that frame.
        public readonly List<(int Frame, float X, float Y)> Probes = new();
        /// --dump-hierarchy FRAME:PATH: logs the subtree at a GameObject path (GameObject.Find
        /// syntax) as of that frame: active flags, rect transforms, components.
        public readonly List<(int Frame, string Path)> HierarchyDumps = new();
        /// --report-builtin-assets FILE: at exit, the objects the run loaded from Unity's built-in
        /// resource files (tab-separated).
        public string? BuiltinReport;
        /// --dump-component FRAME:PATH|TYPE: logs the serialized fields of a component (by type
        /// name) on the GameObject at PATH as of that frame.
        public readonly List<(int Frame, string Spec)> ComponentDumps = new();
        /// --dump-ui FRAME:PATH: writes every active UI element (rects, canvas renderer output,
        /// package component state) as of that frame (PLAN M9's differential oracle).
        public readonly List<(int Frame, string Path)> UiDumps = new();

        public static Options? Parse(string[] args)
        {
            var o = new Options();
            int i = 0;
            try
            {
                if (!ParseInto(o, args, ref i)) return null;
            }
            catch (Exception e) when (e is FormatException or OverflowException or IndexOutOfRangeException or ArgumentException)
            {
                // One of ours with a malformed or missing value: said, as an unknown one is.
                int at = Math.Min(i, args.Length - 1);
                while (at > 0 && !args[at].StartsWith("--", StringComparison.Ordinal)) at--;
                Console.Error.WriteLine(e is IndexOutOfRangeException ? $"Option {args[at]} has no value" : $"Option {args[at]} {args[Math.Min(i, args.Length - 1)]}: {e.Message}");
                return null;
            }
            // --log wins over Unity's -logFile.
            o.LogPath ??= o.UnityLogFile;
            return o;
        }

        static bool ParseInto(Options o, string[] args, ref int i)
        {
            for (; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--game": o.GameDir = args[++i]; break;
                    case "--":
                    {
                        // Steam launch options `OniMoono [options] -- %command%`: Steam's command
                        // for the game, any wrappers of Steam's (launch wrapper, reaper) followed by
                        // the game's executable and its arguments. The game directory is the
                        // executable's; the arguments after it are read as the player's (Unity's
                        // -screen-width and the like).
                        int exe = Array.FindIndex(args, i + 1, a => OniMoono.Engine.Platform.GameLayout.GameDirOfExecutable(a) != null);
                        if (exe < 0)
                        {
                            Console.Error.WriteLine("Moono: no OxygenNotIncluded executable after --; the Steam launch options should end with -- %command%");
                            return false;
                        }
                        o.GameDir = OniMoono.Engine.Platform.GameLayout.GameDirOfExecutable(args[exe])!;
                        i = exe;
                        break;
                    }
                    case "--log": o.LogPath = args[++i]; break;
                    case "--run-home": o.RunHome = Path.GetFullPath(args[++i]); break;
                    case "--audio-wav": o.AudioWav = Path.GetFullPath(args[++i]); break;
                    case "--headless": o.Headless = true; break;
                    case "--batchmode": o.BatchMode = true; break;
                    case "--check-types": o.CheckTypes = true; break;
                    case "--check-assets": o.CheckAssets = true; break;
                    case "--list-data-scripts": o.ListDataScripts = args[++i]; break;
                    case "--game-tweak":
                    {
                        var name = args[++i];
                        if (!OniMoono.Host.GameTweaks.GameTweakNames.IsKnown(name))
                        {
                            Console.Error.WriteLine($"Unknown game tweak {name}; known: all, none, exact (all but {string.Join(", ", OniMoono.Host.GameTweaks.GameTweakNames.ChangingBehaviour)}), {string.Join(", ", OniMoono.Host.GameTweaks.GameTweakNames.All)}");
                            return false;
                        }
                        o.GameTweaks.Add(name);
                        break;
                    }
                    case "--check-bind": o.CheckBind = true; break;
                    case "--check-shaders": o.CheckShaders = true; break;
                    case "--check-natives": o.CheckNatives = true; break;
                    case "--native-arch": o.NativeArch = OniMoono.Engine.Platform.NativeExports.Cpu(args[++i]) ?? throw new ArgumentException("--native-arch expects x86_64 or arm64"); break;
                    case "--native-dir": o.NativeDir = Path.GetFullPath(args[++i]); break;
                    case "--dump-shader": o.DumpShader = args[++i]; break;
                    case "--dump-script": o.DumpScript = args[++i]; break;
                    case "--frames": o.Frames = int.Parse(args[++i]); break;
                    case "--trace-exceptions": o.TraceExceptions = true; break;
                    case "--gl-debug": o.GlDebug = true; break;
                    case "--background": o.Background = true; break;
                    case "--no-focus": o.NoFocus = true; break;
                    case "--splash":
                        o.Splash = args[++i];
                        if (!PlayerSettings.IsSplash(o.Splash))
                        {
                            Console.Error.WriteLine($"Unknown splash {o.Splash}; known: moo, none");
                            return false;
                        }
                        break;
                    case "--log-paths":
                        o.LogPaths = args[++i];
                        if (!PlayerSettings.IsLogPaths(o.LogPaths))
                        {
                            Console.Error.WriteLine($"Unknown log paths {o.LogPaths}; known: hidden, full");
                            return false;
                        }
                        break;
                    case "--log-pointer-hits": o.LogPointerHits = true; break;
                    case "--vsync-count": o.VSyncCount = int.Parse(args[++i]); break;
                    case "--gpu-profile": o.GpuProfile = true; break;
                    case "--no-bindless": o.NoBindless = true; break;
                    case "--bindless": o.Bindless = true; break;
                    case "--input": o.Input.Add(args[++i]); break;
                    // --input-file PATH: --input steps, one a line (scripts too long for a command line).
                    case "--input-file":
                        foreach (var line in File.ReadAllLines(args[++i]))
                            if (line.Trim() is { Length: > 0 } step && !step.StartsWith('#')) o.Input.Add(step);
                        break;
                    case "--frame-stats": o.FrameStats = true; break;
                    case "--gpu":
                        o.Gpu = args[++i];
                        if (!GpuChoice.IsChoice(o.Gpu))
                        {
                            Console.Error.WriteLine($"Unknown gpu {o.Gpu}; known: {string.Join(", ", GpuChoice.Choices)}");
                            return false;
                        }
                        break;
                    case "--dxgi":
                        {
                            var value = args[++i];
                            if (value is not ("on" or "off"))
                            {
                                Console.Error.WriteLine($"Unknown dxgi {value}; known: on, off");
                                return false;
                            }
                            o.Dxgi = value == "on";
                            break;
                        }
                    case "--dxgi-fault":
                        o.DxgiFault = args[++i];
                        if (DxgiFaultOption.Unknown(o.DxgiFault) is { } fault)
                        {
                            Console.Error.WriteLine($"Unknown dxgi fault {fault}; known: {DxgiFaultOption.Known}");
                            return false;
                        }
                        break;
                    case "--no-tweaks-menu": o.NoTweaksMenu = true; break;
                    case "--max-frame-rate":
                        {
                            var spec = args[++i].Split(':');
                            o.MaxFrameRate = int.Parse(spec[0]);
                            if (spec.Length > 1) o.MaxFrameRateFrames = int.Parse(spec[1]);
                            break;
                        }
                    case "--dump-render-textures":
                        {
                            var spec = args[++i];
                            int colon = spec.IndexOf(':');
                            OniMoono.Engine.Rendering.FrameRenderer.DumpFrame = int.Parse(spec[..colon]);
                            OniMoono.Engine.Rendering.FrameRenderer.DumpDirectory = Path.GetFullPath(spec[(colon + 1)..]);
                            break;
                        }
                    case "--native-override":
                        {
                            var spec = args[++i];
                            int eq = spec.IndexOf('=');
                            if (eq <= 0) throw new ArgumentException("--native-override expects NAME=PATH");
                            AssemblyPolicy.NativeOverrides[spec[..eq]] = Path.GetFullPath(spec[(eq + 1)..]);
                            break;
                        }
                    // Unity's player arguments (UnityArguments): what is wrong with one is noted in
                    // the log, and the setting keeps its default.
                    case "-screen-width": o.ScreenWidth = UnityArguments.Int(args, ref i, o.ArgumentNotes, min: 1) ?? o.ScreenWidth; break;
                    case "-screen-height": o.ScreenHeight = UnityArguments.Int(args, ref i, o.ArgumentNotes, min: 1) ?? o.ScreenHeight; break;
                    case "-screen-fullscreen": o.ScreenFullscreen = UnityArguments.Int(args, ref i, o.ArgumentNotes, min: 0, max: 1) ?? o.ScreenFullscreen; break;
                    case "-monitor": o.Monitor = UnityArguments.Int(args, ref i, o.ArgumentNotes, min: 0) ?? o.Monitor; break;
                    case "-window-mode":
                    {
                        // Unity's are exclusive and borderless (Windows only); windowed is ours.
                        if (UnityArguments.Value(args, ref i, o.ArgumentNotes) is not { } value) break;
                        if (value is "exclusive" or "borderless" or "windowed") o.WindowMode = value;
                        else o.ArgumentNotes.Add($"-window-mode {value}: not exclusive, borderless or windowed; ignored");
                        break;
                    }
                    case "-logFile":
                    {
                        // Unity's "-" (the console) is not a file: the log stays where it was.
                        if (UnityArguments.Value(args, ref i, o.ArgumentNotes) is not { } value) break;
                        if (value == "-") o.ArgumentNotes.Add("-logFile -: the log goes to its file, not the console");
                        else o.UnityLogFile = value;
                        break;
                    }
                    case "--dump-component":
                    {
                        var spec = args[++i];
                        int colon = spec.IndexOf(':');
                        o.ComponentDumps.Add((int.Parse(spec[..colon]), spec[(colon + 1)..]));
                        break;
                    }
                    case "--report-builtin-assets": o.BuiltinReport = args[++i]; break;
                    case "--dump-hierarchy":
                    {
                        var spec = args[++i];
                        int colon = spec.IndexOf(':');
                        o.HierarchyDumps.Add((int.Parse(spec[..colon]), spec[(colon + 1)..]));
                        break;
                    }
                    case "--dump-ui-vertices": OniMoono.Engine.UI.UiDump.VerticesFilter = args[++i]; break;
                    case "--dump-ui":
                    {
                        var spec = args[++i];
                        int colon = spec.IndexOf(':');
                        o.UiDumps.Add((int.Parse(spec[..colon]), spec[(colon + 1)..]));
                        break;
                    }
                    case "--probe-ui":
                    {
                        var parts = args[++i].Split(':', ',');
                        o.Probes.Add((int.Parse(parts[0]), float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                    }
                    case "--capture":
                    {
                        // FRAME:PATH[:SCALE]; the path may hold a colon itself (a Windows drive).
                        var spec = args[++i];
                        int colon = spec.IndexOf(':');
                        var path = spec[(colon + 1)..];
                        int scale = 1;
                        int last = path.LastIndexOf(':');
                        if (last > 0 && int.TryParse(path[(last + 1)..], out int s)) { scale = s; path = path[..last]; }
                        o.Captures.Add((int.Parse(spec[..colon]), path, scale));
                        break;
                    }
                    // Unity's renderer choice (-force-glcore, -force-opengl, -force-vulkan): ours has
                    // OpenGL only, so it reads them for the log alone.
                    case var force when UnityArguments.Renderer(force, o.ArgumentNotes):
                        break;
                    // Another of Unity's (-popupwindow, -force-d3d11, -screen-quality…) or a stray
                    // word: ignored, as Unity's player ignores what it does not know. Ours are strict.
                    case var unitys when UnityArguments.IsUnitys(unitys):
                        UnityArguments.Ignore(args, ref i, o.ArgumentNotes);
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown option {args[i]}");
                        Console.Error.WriteLine("usage: OniMoono [--game DIR] [--log FILE] [--run-home DIR] [--audio-wav DIR] [--headless] [--batchmode] [--frames N] [--native-override NAME=PATH] [--trace-exceptions] [--gl-debug] [--background] [--no-focus] [--splash moo|none] [--log-pointer-hits] [--vsync-count N] [--gpu-profile] [--no-bindless] [--bindless] [--input FRAME:ACTION[:ARGS]] [--input-file PATH] [--frame-stats] [--log-paths hidden|full] [--gpu default|discrete|integrated] [--dxgi on|off] [--dxgi-fault WHAT] [--no-tweaks-menu] [--max-frame-rate N[:FRAMES]] [--dump-render-textures FRAME:DIR] [-screen-width W] [-screen-height H] [-screen-fullscreen 0|1] [-window-mode exclusive|borderless|windowed] [-monitor N] [-logFile PATH] [other Unity player arguments, ignored] [--check-types] [--game-tweak NAME|-NAME|all|exact|none] [--check-assets [--list-data-scripts FILE]] [--check-bind] [--check-shaders] [--check-natives [--native-arch x86_64|arm64] [--native-dir DIR]] [--dump-shader NAME:DIR] [--capture FRAME:PATH[:SCALE]] [--probe-ui FRAME:X,Y] [--dump-ui FRAME:PATH] [--dump-ui-vertices PATH-SUBSTRING] [--dump-hierarchy FRAME:PATH] [--report-builtin-assets FILE] [--dump-component FRAME:PATH|TYPE] [-- STEAM-COMMAND]");
                        return false;
                }
            }
            return true;
        }
    }

    /// Unity's player paths for this game: the Linux player's (XDG directories) or the Windows
    /// player's (LocalLow, the temporary folder; with forward slashes, as Unity reports them).
    internal static class Paths
    {
        public static void Configure(OniMoono.Engine.Platform.GameLayout layout, Options options)
        {
            var dataDir = layout.DataDir;
            // app.info: company name, then product name.
            var info = File.ReadAllLines(Path.Combine(dataDir, "app.info"));
            EngineConfig.CompanyName = info.Length > 0 ? info[0] : "";
            EngineConfig.ProductName = info.Length > 1 ? info[1] : "";
            if (OperatingSystem.IsWindows()) ConfigureWindows(dataDir, options.RunHome);
            else if (OperatingSystem.IsMacOS() && layout.IsMacBundle) ConfigureMac(layout, options.RunHome);
            else ConfigureLinux(dataDir, options.RunHome);
            if (!OperatingSystem.IsMacOS() || !layout.IsMacBundle) EngineConfig.ConsoleLogPath = EngineConfig.PersistentDataPath + "/Player.log";
            // A bundle's Application.dataPath is its Contents (a Mac bundle read on Linux too, by
            // the check modes); the data it reads stays in Contents/Resources/Data.
            if (layout.IsMacBundle && !OperatingSystem.IsMacOS())
            {
                EngineConfig.DataPath = layout.DataPath;
                EngineConfig.StreamingAssetsPath = layout.StreamingAssetsPath;
            }
            EngineConfig.IsBatchMode = options.BatchMode;
            EngineConfig.Headless = options.Headless;
        }

        /// Unity's macOS player (OniMoono.Engine.Platform.MacUserFolders): dataPath the bundle's
        /// Contents, persistent data and caches under ~/Library by the bundle identifier, the log
        /// in ~/Library/Logs.
        static void ConfigureMac(OniMoono.Engine.Platform.GameLayout layout, string? runHome)
        {
            EngineConfig.DataPath = layout.DataPath;
            EngineConfig.StreamingAssetsPath = layout.StreamingAssetsPath;
            var home = runHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folders = OniMoono.Engine.Platform.MacUserFolders.For(home, layout.BundleIdentifier ?? $"unity.{EngineConfig.CompanyName}.{EngineConfig.ProductName}",
                EngineConfig.CompanyName, EngineConfig.ProductName);
            EngineConfig.PersistentDataPath = folders.PersistentDataPath;
            EngineConfig.TemporaryCachePath = folders.TemporaryCachePath;
            EngineConfig.ConsoleLogPath = folders.ConsoleLogPath;
        }

        static void ConfigureLinux(string dataDir, string? runHome)
        {
            EngineConfig.DataPath = dataDir;
            EngineConfig.StreamingAssetsPath = Path.Combine(dataDir, "StreamingAssets");
            // Unity's Linux player: $XDG_CONFIG_HOME/unity3d/<company>/<product>, defaulting to ~/.config.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var config = runHome != null ? Path.Combine(runHome, "config") : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config)) config = Path.Combine(home, ".config");
            EngineConfig.PersistentDataPath = Path.Combine(config, "unity3d", EngineConfig.CompanyName, EngineConfig.ProductName);
            var cache = runHome != null ? Path.Combine(runHome, "cache") : Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(cache)) cache = Path.Combine(home, ".cache");
            EngineConfig.TemporaryCachePath = Path.Combine(cache, EngineConfig.CompanyName, EngineConfig.ProductName);
        }

        static void ConfigureWindows(string dataDir, string? runHome)
        {
            static string Unity(string path) => path.Replace('\\', '/');
            EngineConfig.DataPath = Unity(Path.GetFullPath(dataDir));
            EngineConfig.StreamingAssetsPath = EngineConfig.DataPath + "/StreamingAssets";
            var localLow = runHome != null ? Path.Combine(runHome, "AppData", "LocalLow") : WindowsFolders.LocalAppDataLow();
            EngineConfig.PersistentDataPath = Unity(Path.Combine(localLow, EngineConfig.CompanyName, EngineConfig.ProductName));
            var temp = runHome != null ? Path.Combine(runHome, "AppData", "Local", "Temp") : Path.GetTempPath();
            EngineConfig.TemporaryCachePath = Unity(Path.Combine(temp, EngineConfig.CompanyName, EngineConfig.ProductName));
        }
    }

    /// Windows known folders .NET's SpecialFolder does not name.
    internal static class WindowsFolders
    {
        static readonly Guid LocalAppDataLowId = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

        [System.Runtime.InteropServices.DllImport("ole32.dll")]
        static extern void CoTaskMemFree(IntPtr p);

        /// %USERPROFILE%\AppData\LocalLow, where Unity's Windows player keeps persistent data.
        public static string LocalAppDataLow()
        {
            int hr = SHGetKnownFolderPath(LocalAppDataLowId, 0, IntPtr.Zero, out var p);
            if (hr != 0) throw new InvalidOperationException($"The LocalLow folder could not be found (0x{hr:X8}).");
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(p)!; }
            finally { CoTaskMemFree(p); }
        }
    }
}
