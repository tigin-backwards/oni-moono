# Running ONI's managed code on stock .NET 10 CoreCLR

**Scope.** This document asks whether the *unmodified* game assemblies in
`oni-linux/OxygenNotIncluded_Data/Managed` can run on .NET 10.0.13 CoreCLR when our own
`UnityEngine.*` assemblies replace Unity's. It covers seven areas:

- BCL compatibility
- assembly binding
- native interop
- bootstrap sequence
- platform services
- threading
- reflection and serialization

**How each claim is backed.** Every claim rests on one of these:

- a metadata scan
- a runtime probe on CoreCLR
- a decompiled source location under `cache/decomp/<Assembly>/…`

Source references take the form `file.cs:line` and are relative to `cache/decomp/`. "AC" means
Assembly-CSharp and "FP" means Assembly-CSharp-firstpass.

**Scratch tooling.** None of it is in the repo; it lives in `/tmp/claude-1000/runtime-research/`:

| Tool | What it does |
|---|---|
| `Analyze/` (`Program.cs`, `Reach.cs`) | Uses System.Reflection.Metadata. Modes: `refs` (assembly identities), `bcl` (resolves every BCL TypeRef/MemberRef against `~/.dotnet/shared/Microsoft.NETCore.App/10.0.13`, follows type forwards, attributes IL callers), `calls` (call sites of APIs that match a regex), `pinvoke` (DllImport inventory) and `reach` (IL call-graph reachability from thread entry points to UnityEngine members). |
| `Probe/` | Custom `AssemblyLoadContext` prototype. It loads the game, resolves every TypeRef and MemberRef token, and JITs (`RuntimeHelpers.PrepareMethod`) every non-generic method. |
| `Behave/` | Runs risky BCL APIs and game and library code paths on CoreCLR. It runs once with no fixes and once with the proposed fixes (`… fix`). |
| `Graph/` | Computes the type closure of the object graphs that the game clones with BinaryFormatter. |
| `agentA/` | UnityPy scripts that dumped scene and prefab contents for §4. |

Raw outputs are in that directory: `refs.txt`, `bcl.txt`, `risk.txt`, `pinvoke.txt`, `probe1.txt` and `reach_*.txt`.

---

## 0. Summary of blockers

| # | Issue | Severity | Fix in host (no game changes) |
|---|---|---|---|
| B1 | **Newtonsoft.Json 7.0 (net20 build) cannot serialize anything.** `JsonTypeReflector.get_DynamicCodeGeneration`/`get_FullyTrusted` reference CAS types (`SecurityPermission`, `ReflectionPermission`, `CodeAccessPermission.Demand`). mscorlib forwards these to `System.Security.Permissions`, which is not in the shared framework. As a result `FileNotFoundException` is thrown from `JsonConvert.SerializeObject` and `DeserializeObject` (verified). | **Blocker** | Ship the `System.Security.Permissions` NuGet package (10.0.13) with the host, so it appears in deps.json or the TPA. Verified fix: Newtonsoft round-trips. |
| B2 | **BinaryFormatter on the new-game and worldgen path.** `FP/ObjectCloner/SerializingCloner.cs` `Copy<T>` serializes and then deserializes with BinaryFormatter. It is called from `SettingsCache.CloneInToNewWorld` (`FP/ProcGen/SettingsCache.cs:156-162`), which runs from `MutatedWorldData..ctor` (`FP/ProcGen/MutatedWorldData.cs:41`) on every `WorldGenSettings` construction (`FP/ProcGen/WorldGenSettings.cs:56,87`). It is also called from `MutatedClusterLayout..ctor` (`FP/ProcGen/MutatedClusterLayout.cs:13`), `WorldLayout.cs:728` and `WorldgenMixing.cs:296`. On .NET 9+ the in-box BinaryFormatter throws `PlatformNotSupportedException` (verified). | **Blocker** | Reference the `System.Runtime.Serialization.Formatters` 10.0.13 compat package and set `<EnableUnsafeBinaryFormatterSerialization>true</EnableUnsafeBinaryFormatterSerialization>` (runtimeconfig switch `System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization`). Verified fix: `SerializingCloner.Copy` works. The cloned graph contains only `[Serializable]` game and BCL types, with no UnityEngine types (`Graph/` output). |
| B3 | **Ionic.Zip (DotNetZip 1.9) static ctors call `Encoding.GetEncoding("IBM437")`.** This affects `ZipFile`, `ZipEntry` and `SharedUtilities`. The call throws `ArgumentException` on CoreCLR (verified). It is used by Klei's zip FileSystem (`FP/Klei/ZipFileDirectory.cs`), mods (`AC/KMod/ZipFile.cs`) and Steam UGC. **Ionic.Zlib.ZlibStream**, which save compression uses (`AC/SaveLoader.cs:286,300`), is *not* affected (verified round-trip). | High | Call `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` in the host before any game code runs. Verified fix. |
| B4 | **`SteamManager.Awake` quits the process with code 2 when `SteamAPI.Init()` fails.** `DistributionPlatform.Initialize` always constructs `SteamDistributionPlatform`, and no standalone or dev flag exists (§5). | Blocker *without Steam* | Our `Application.Quit` can refuse that call; the rest of the code mostly tolerates `Initialized == false`. The alternative is a stub Steamworks assembly. |
| B5 | **Worker threads touch UnityEngine objects.** Examples: `Component.transform`, `GameObject.GetComponent`, `Object.op_Equality`/`op_Implicit`, `TextAsset.text`, `Resources.Load` (lazy `Db.Get`), `Debug.LogError` and `Application.streamingAssetsPath`/`dataPath`/`isPlaying`/`platform` (§6). Release Unity players tolerate these calls. | Design constraint | Our UnityEngine must allow these reads from any thread. |
| B6 | **Assembly preloading.** Unity loads every assembly listed in `ScriptingAssemblies.json` before scripts run. The game then snapshots `AppDomain.GetAssemblies()` in `App..cctor` (`FP/App.cs:69-93`) and `KSerialization.Manager.Initialize` (`FP/KSerialization/Manager.cs:25-28`), and uses the snapshot to resolve save-file type names (`Manager.GetType`, `:30-50`). CoreCLR loads assemblies lazily. | High (silent save-load breakage) | The host must eagerly load every game assembly into the ALC before any game type initializes. |
| R1 | `Thread.Abort` throws `PlatformNotSupportedException`. The call sites are `ThreadedHttps<T>.End()` (`FP/ThreadedHttps.cs:124-135`, only if the HTTP thread does not join within 2 s, which happens at quit or session end) and `Cluster.AbortGeneration` (`AC/ProcGenGame/Cluster.cs:500-508`, which has **no callers**). | Low | Disable metrics or tolerate it. It is reached only at shutdown. |
| R2 | `Sim.SIM_Initialize(Sim.DLL_MessageHandler)` passes a **temporary delegate** that nothing keeps alive (`AC/SaveLoader.cs:245,412,1029`, `AC/ProcGenGame/WorldGenSimUtil.cs:18`). On CoreCLR, a GC can collect it, and a later SimDLL error callback then jumps into a freed thunk. Separately, a managed exception thrown inside that callback is fatal on CoreCLR. | Medium (only on SimDLL error paths) | Fixed: delegates to static methods passed to native code are kept alive as on Mono (ARCHITECTURE D64). |
| R3 | 0Harmony/MonoMod: 8 MemberRefs are missing (`AppDomain.DefineDynamicAssembly(…)`, `AssemblyBuilder.Save`, `DefineDynamicModule(string,string,bool)`, CAS `PermissionSetAttribute`). All of them are in fallback or Cecil paths. Harmony 2.4 supports CoreCLR. | Low (modding only) | Ship `System.Security.Permissions` (B1 fix) and the `System.CodeDom` package if wanted. |
| B7 | **`com.rlabrecque.steamworks.net` `Assembly.GetTypes()` throws `ReflectionTypeLoadException`.** `Steamworks.SteamDatagramRelayAuthTicket+ExtraField+OptionValue` is an explicit-layout union that overlaps `byte[] m_szStringValue` with `long` at offset 0, and CoreCLR rejects it at type load. Mono accepted it. `App..cctor` swallows the exception, but **`AsyncLoadManager<T>.Run()` (`FP/AsyncLoadManager.cs:27-48`, called from `Assets.OnPrefabInit`) and `LegacyModMain.Load()` (`AC/LegacyModMain.cs:26-45`, from `Assets.CreatePrefabs`) call `GetTypes()` on every loaded assembly with no try/catch**. Startup therefore crashes as soon as Steamworks is loaded, and it is loaded before `Assets` (SteamManager runs in LaunchInitializer). | **Blocker** | Ship our own build of Steamworks.NET 20.1.0 under the same assembly name (MIT, open source) with that union fixed, or a Steam stub (B4). Alternatively, keep Steamworks out of `GetAssemblies()` by not loading it, which only works with a stub platform. |
| B8 | **State-machine parameters from saves are silently dropped.** `StateMachineSerializer` writes `context.GetType().FullName` (`AC/StateMachineSerializer.cs:45`) and on load matches it by **string equality** (`:117`), normalising only `Version=2.0.0.0` to `4.0.0.0` (`:112`). The FullNames are generic instantiations that embed assembly-qualified type arguments. Real saves contain `[System.Object, mscorlib, Version=4.0.0.0, …]` (7 127 occurrences in a sample save, typically the default `DefType=object`, `AC/GameStateMachine.cs:2933,2936`). On CoreCLR the same FullName says `System.Private.CoreLib, Version=10.0.0.0, … PublicKeyToken=7cec85d7bea7798e`. | **High** (existing saves load with all SM float, bool and int parameters reset; saves we write would not round-trip into real ONI) | Requires behaviour the host cannot provide transparently. Options: (a) a runtime patch of `StateMachineSerializer.Deserialize`/`Serialize` that normalises `System.Private.CoreLib, Version=10…` to `mscorlib, Version=4.0.0.0…` (the shipped 0Harmony could do it, or a load-time IL rewrite in the ALC); (b) accept the loss. For new games within our runtime only, the round trip is self-consistent. |
| R4 | Unity engine assemblies appearing as generic arguments in persisted FullNames (B8 mechanism) must keep `Version=0.0.0.0, Culture=neutral, PublicKeyToken=null`. | Constraint | Build our UnityEngine modules unsigned with version 0.0.0.0. |

Two further constraints come from KSerialization (§7):

- `Color32` must expose exactly the public fields `r,g,b,a` and no public settable properties.
- `Vector4` must expose exactly `x,y,z,w`.

Everything else checks out:

- **The game's own code resolves 100% against .NET 10.** For Assembly-CSharp this is 238 BCL TypeRefs and 11 022 MemberRefs; for firstpass it is 300 TypeRefs and 3 543 MemberRefs.
- **All game methods JIT on CoreCLR.** Assembly-CSharp has 66 979 and firstpass 15 118, with 0 failures.
- **Every SimDLL entry point is exported by `libSimDLL.so`.** It depends only on libc and libstdc++.

---

## 1. BCL compatibility

### 1.1 Method

`Analyze bcl` takes every TypeRef whose resolution scope is a BCL assembly. That set covers:

- `mscorlib`, `netstandard`, `System`, `System.*`
- `Mono.*`, `I18N*`, `Microsoft.CSharp`

It also takes every MemberRef whose parent is such a type, or a generic instantiation of one.

It resolves each one against the .NET 10.0.13 shared framework by name. For type references this means:

- it follows `ExportedType` forwarders across assemblies (mscorlib → System.Private.CoreLib, and so on)
- it checks that the type is public

For member references this means:

- it compares name plus decoded signature (modreq and modopt are ignored)
- it walks base types
- it checks accessibility

Unresolved references are attributed to the methods that reference them, found by scanning the IL operand tokens.

The consumer set is every non-engine managed assembly in `Managed/`:

- Assembly-CSharp(-firstpass), FMODUnity(+Haptics/Resonance), com.rlabrecque.steamworks.net
- Newtonsoft.Json, VYaml(+Annotations), ImGui.NET, ImGui, LibNoiseDotNet, Ionic.Zip, ArabicSupport, 0Harmony
- Unity.TextMeshPro, UnityEngine.UI, Unity.Timeline, Unity.Burst(+Unsafe), Unity.Collections(+LowLevel.ILSupport), Unity.Mathematics, Unity.MemoryProfiler, Unity.Profiling.Core, Unity.InternalAPIEngineBridge.004, Unity.Multiplayer.Center.Common
- System.IO.Hashing

### 1.2 Results (static)

| Assembly | BCL TypeRefs (unresolved) | BCL MemberRefs (unresolved) |
|---|---|---|
| Assembly-CSharp | 238 (0) | 11 022 (0) |
| Assembly-CSharp-firstpass | 300 (0) | 3 543 (0) |
| FMODUnity / Haptics / Resonance | 84 / 14 / 22 (0) | 232 / 9 / 26 (0) |
| com.rlabrecque.steamworks.net | 70 (0) | 128 (0) |
| **Newtonsoft.Json** (7.0.0.0, net20) | 295 (**4**) | 1 416 (**4**) |
| VYaml, VYaml.Annotations | 172, 19 (0) | 535, 14 (0) |
| ImGui.NET, ImGui, LibNoiseDotNet, ArabicSupport | 0 unresolved | 0 unresolved |
| **Ionic.Zip** (1.9.1.8, net20) | 165 (**7**) | 400 (**20**) |
| **0Harmony** (2.4.2) | 372 (**4**) + 11 malformed typerefs | 2 537 (**8**) |
| TMP, UI, Timeline, Burst, Collections, Mathematics, … | 0 unresolved | 0 unresolved |

The unresolved references, grouped by root cause, are:

1. **CAS (`System.Security.Permissions` is not in the shared framework).** Every reference is via `[mscorlib]`:
   - **Newtonsoft.Json**:
     - `ReflectionPermission::.ctor(ReflectionPermissionFlag)`, `SecurityPermission::.ctor(SecurityPermissionFlag)`, `SecurityPermission::.ctor(PermissionState)` and `CodeAccessPermission::Demand()`.
     - They are referenced from `Newtonsoft.Json.Serialization.JsonTypeReflector::get_DynamicCodeGeneration` and `::get_FullyTrusted`.
     - Every serializer or contract-resolver path reaches these getters, so this is **critical** (B1).
   - **Ionic.Zip**:
     - `SecurityPermission::.ctor(SecurityPermissionFlag)` and `::IsUnrestricted()`.
     - They are referenced from `Ionic.Zip.SharedUtilities::ReadWithRetry`.
     - That method runs on a zip read retry after an IOException. It is rare, but would then fail with `FileNotFoundException`.
   - **0Harmony**: `PermissionSetAttribute` (4 members) in `Mono.Cecil.Rocks.SecurityDeclarationRocks::TryProcessPermissionSetAttribute`. This path is not used at runtime.
2. **CodeDom (`System.CodeDom` is not in the shared framework).**
   - Ionic.Zip `ZipFile::_SaveSfxStub` references `CSharpCodeProvider` and `CompilerParameters`/`CompilerResults`/`CompilerError`, 20 members in all.
   - It only runs when building self-extracting archives, which the game never does.
   - Not critical. The `System.CodeDom` 10.0.13 package would satisfy the binding, and `CompileAssemblyFromSource` would then throw PNSE.
3. **Reflection.Emit APIs that only exist in .NET Framework or Mono** (0Harmony/MonoMod):
   - `AppDomain::DefineDynamicAssembly(AssemblyName, AssemblyBuilderAccess)`, referenced from `HarmonyLib.PatchTools::DefineDynamicAssembly`.
   - `AppDomain::DefineDynamicAssembly(AssemblyName, AssemblyBuilderAccess, String)`.
   - `AssemblyBuilder::DefineDynamicModule(String, String, Boolean)`.
   - `AssemblyBuilder::Save(String)`.
   - The last three are referenced from `MonoMod.Utils.DMDEmitMethodBuilderGenerator`, a debug or dump generator that is not the default.
   - MonoMod picks its CoreCLR backend at runtime (types `MonoMod.Core.Interop.CoreCLR/V60…` exist).
4. **0Harmony's malformed TypeRefs.** These are MonoMod.Backports shims that point at `[System]System.Runtime.CompilerServices.Unsafe`, `[System]MonoMod.Backports.MethodImplOptionsEx` and `ILHelpers.UnsafeRaw`. They are ILPack/merge artefacts and are only bound if MonoMod's backport path is taken.

The CoreCLR probe (§2.3) confirms every one of these dynamically. It is the only class of JIT failure across all assemblies.

**No game reference to any of these appeared.** The scan covered the following, and every one resolved or is absent:

- Remoting (`System.Runtime.Remoting.*`)
- `AppDomain.CreateDomain`/`Unload`
- `Assembly.ReflectionOnlyLoad*`
- System.Drawing, System.Data (outside Newtonsoft's `DataSet`/`DataTable` converters), System.Web
- `Mono.*` and `I18N*`
- CAS in game code

### 1.3 APIs that resolve but behave differently on CoreCLR

Call sites come from `Analyze calls` (output in `risk.txt`). Behaviour was verified by `Behave`.

| API | CoreCLR behaviour (verified) | Game call sites | On the normal path? |
|---|---|---|---|
| `BinaryFormatter.Serialize/Deserialize` | `PlatformNotSupportedException` | `FP ObjectCloner.SerializingCloner::Copy` | **Yes:** every world settings construction (B2) |
| `Encoding.GetEncoding("IBM437")`/`(1252)` | `ArgumentException`/`NotSupportedException` unless `CodePagesEncodingProvider` is registered | Ionic.Zip `ZipFile`/`ZipEntry`/`SharedUtilities` `.cctor`, `ZipInputStream::_Init`, `ZipOutputStream` | Yes, for zip content and mods (B3). `GZipStream` uses `iso-8859-1`, which is built in, so OK. |
| `Encoding.Default` | UTF-8 (Mono on Linux also gives UTF-8) | Ionic.Zip `ZipFile::AddEntry`/`UpdateEntry` | Same as Mono |
| `Thread.Abort()` | `PlatformNotSupportedException` | `FP ThreadedHttps\`1::End` (only after a 2 s join timeout), `AC ProcGenGame.Cluster::AbortGeneration` (dead code) | Shutdown only (R1) |
| `Delegate.BeginInvoke/EndInvoke` | PNSE | **none** in any game assembly | n/a |
| `Assembly.LoadFrom`/`LoadFile` | Work; they load into the **default** ALC, or an anonymous ALC for `LoadFile` | `AC KMod.DLLLoader::LoadDLLs`, `::LoadUserModLoaderDLL` | Mods only. Under a custom ALC, mod DLLs that reference `Assembly-CSharp` would bind to a *second* copy. If mods ever matter, the host must hook `AssemblyLoadContext.Default.Resolving` and forward to the game ALC. |
| `Assembly.LoadWithPartialName` | Exists (obsolete), resolves through the default ALC | Newtonsoft `DefaultSerializationBinder::GetTypeFromTypeNameKey` (TypeNameHandling only) | No |
| `AppDomain.CurrentDomain.GetAssemblies()` | Returns assemblies of **all** ALCs, but only those **already loaded** | `FP App::.cctor`, `KSerialization.Manager::Initialize`, `AsyncLoadManager\`1::Run`, `TuningSystem::Load`, NodeEditorFramework; `AC LegacyModMain::Load`, `DevToolSimDebug`; ImGui `ReflectionUtil` | **Yes** (B6) |
| `Type.GetType(string)` | Resolves short names only in the *calling* assembly and CoreLib. Assembly-qualified names bind through the caller's ALC. | `FP KSerialization.Manager::GetType` (falls back to scanning `Manager.assemblies`), `AC SaveLoadRoot::LoadInternal`, `StateMachineSerializer`, `SerializedList\`1::OnDeserialized`, `ColonyAchievementStatus::Deserialize`, `DetailsScreen::GetComponent` | Yes (save load). It works if B6 is satisfied. See §7. |
| `FormatterServices.GetUninitializedObject` | Works (obsolete warning only) | `FP KSerialization.DeserializationMapping::ReadValue`, `AC ColonyAchievementStatus::Deserialize`, `ConduitTemperatureManager::Sim200ms` | Yes, OK |
| `string.GetHashCode()` | **Randomized per process** (Mono is deterministic) | `FP StringKey` ctor (`StringKey.cs:13`) and `StringTable.cs:24` (in-process only, OK); `AC KMod/Local.cs:24` (`version = directoryName.GetHashCode()`), which is persisted in mods.json, so local mods look "changed" on every launch | Minor |
| `double`/`float.ToString()` | Shortest round-trip (`0.30000000000000004`). Mono prints `0.3`. | Any UI or number formatting via `ToString()` without a format | Cosmetic. Saves are binary, so they are unaffected. |
| `Environment.GetFolderPath(Personal)` | Returns `$HOME` (empty in the probe sandbox). `Util::GetKleiRootPath`, `SuperluminalPerf::Initialize` | Linux save root comes from `Application.persistentDataPath` instead (§5) | OK |
| `Thread(start, maxStackSize: 131072)` | Honoured (128 KiB stacks) | `FP JobManager/WorkerThread::.ctor` (`JobManager.cs:23`), "KWorker*" threads | Yes. CoreCLR frames are larger than Mono's, so watch for stack overflow in deep work items (pathfinding). |
| `System.Net.Http.HttpClient` with `ServerCertificateCustomValidationCallback` and `X509Chain` | Works; uses OpenSSL on Linux | `FP ThreadedHttps\`1` | Telemetry only |
| `FileSystemWatcher` | Works (inotify) | `FP TuningSystem::ListenForFileChanges` | Dev only |
| `Process.GetCurrentProcess().MainModule`, `Process.Start` | `MainModule` is `dotnet` or the host executable, not the game binary | `FP App::Restart` | On restart (e.g. language change), the game would relaunch the wrong executable. Our `App`-level host must provide the right `MainModule` path, or accept this. |
| `Thread.CurrentCulture` setter | Works | `FP Util::ApplyInvariantCultureToThread` | OK. Requires ICU (libicu) or `InvariantGlobalization`. |
| `GC.Collect()` | Works | App/SaveLoader | OK |

### 1.4 Mono BCL copies shipped in `Managed/`: what must and must not load

The CoreCLR rule: anything in the TPA (shared framework) **must** come from the framework. Our ALC returns `null` for those names, so the default context binds them. **Never load Mono's copies.** They implement icalls into the Mono runtime and would fail or corrupt the process.

| Shipped file (version, PKT) | Policy | Notes |
|---|---|---|
| mscorlib (4.0.0.0, b77a5c…) | **Do not load.** Use the framework facade. | Ionic.Zip, Newtonsoft and ArabicSupport reference mscorlib **2.0.0.0**. The framework's 4.0.0.0 facade satisfies that (higher version wins). Verified in the probe. |
| netstandard (2.1.0.0), System, System.Core, System.Xml, System.Xml.Linq, System.Numerics, System.Net.Http, System.IO.Compression, System.IO.Compression.FileSystem, System.Runtime, System.Runtime.Serialization, System.Data, System.Data.DataSetExtensions, System.Drawing, System.Configuration, System.Security, System.Transactions, System.ComponentModel.Composition (\*), System.ServiceModel.Internals (\*), System.EnterpriseServices (\*), System.Memory (4.0.99.0), System.Runtime.CompilerServices.Unsafe (6.0.0.0) | **Do not load.** Use the framework. | These are Mono BCL builds. Items marked (\*) are not in the framework but are referenced by **no** game assembly, only by Mono's own `netstandard`/`System.Data`/`System.Runtime`. The game references `System.Net.Http` 4.2.0.0 and `System.IO.Compression` 4.2.0.0, which the framework satisfies. |
| Mono.Security (4.0.0.0, 0738eb…) | **Do not load.** | Only Mono's `System.dll` and `System.Net.Http.dll` reference it. |
| I18N, I18N.West (2.0.5.0) | **Do not load.** | Mono's code-page providers, loaded by Mono's Encoding through reflection. They are replaced by `CodePagesEncodingProvider` (B3). |
| System.IO.Hashing (9.0.0.0, cc7b13…, netstandard2.0) | **Fine to load from Managed**, or ship the NuGet package | Not in the shared framework. **No game assembly references it** statically, so it is probably a Unity package dependency. Its references (netstandard, System.Memory, Unsafe) bind to the framework. |
| **Not shipped, but needed:** `System.Security.Permissions` 10.0.13, `System.Runtime.Serialization.Formatters` 10.0.13 (full implementation), optionally `System.CodeDom` 10.0.13 | **Add to the host's deps.json** | B1, B2 and R3. Verified working with Newtonsoft, Ionic and ObjectCloner. |

---

## 2. Assembly loading strategy

### 2.1 Identities referenced

All engine references are unsigned (PKT null):

| Referenced name | Version | Referenced by (game side) |
|---|---|---|
| UnityEngine.CoreModule | 0.0.0.0 | every Unity-facing assembly (16) |
| UnityEngine.UI | **1.0.0.0** | AC, FP, FMODUnity, ImGui, TMP |
| UnityEngine.InputLegacyModule, IMGUIModule | 0.0.0.0 | AC, FP, TMP, UI, ImGui |
| UnityEngine.UIModule, TextRenderingModule, AnimationModule | 0.0.0.0 | AC, FP, TMP, UI, Timeline |
| UnityEngine.PhysicsModule, Physics2DModule | 0.0.0.0 | AC, FMODUnity, UI |
| UnityEngine.TextCoreFontEngineModule | 0.0.0.0 | AC, TMP |
| UnityEngine.ParticleSystemModule, JSONSerializeModule, AssetBundleModule, DirectorModule, UIElementsModule | 0.0.0.0 | AC/FP/FMOD/UI/Timeline |
| UnityEngine.VideoModule, UnityWebRequestModule, ImageConversionModule, ScreenCaptureModule | 0.0.0.0 | AC only |
| UnityEngine.TilemapModule, SpriteShapeModule | 0.0.0.0 | UnityEngine.UI only |
| UnityEngine.AudioModule, SharedInternalsModule | 0.0.0.0 | Unity.Timeline |
| UnityEngine.PropertiesModule | 0.0.0.0 | Unity.Collections |
| Unity.TextMeshPro | 0.0.0.0 | AC, FP |
| Unity.Timeline | **1.0.0.0** | FMODUnity |
| Unity.InternalAPIEngineBridge.004 | 0.0.0.0 | Unity.TextMeshPro |
| Unity.Burst / Burst.Unsafe (**4.0.5.0**) / Mathematics / Collections.LowLevel.ILSupport | – | Unity.Collections and Burst only (not the game) |

Assembly-CSharp's full reference list is:

- BCL: mscorlib, System, System.Core, System.Numerics, System.IO.Compression 4.2, System.Runtime.CompilerServices.Unsafe 6.0
- Klei and third-party: firstpass, Newtonsoft.Json 7.0.0.0 (PKT 30ad4fe6b2a6aeed), VYaml(+Annotations) 1.0.0.0, FMODUnity, ImGui(.NET), com.rlabrecque.steamworks.net, Ionic.Zip 1.9.1.8, LibNoiseDotNet, **0Harmony 2.4.2.0**, ArabicSupport 1.0.5608.27766
- The UnityEngine modules listed above

Firstpass additionally references System.Net.Http 4.2, System.Xml and System.Xml.Linq. The full dump is in `refs.txt`.

### 2.2 Recommended ALC resolution policy

Use a single non-collectible `GameLoadContext`. Its `Load(AssemblyName)` checks these rules in order:

1. **Framework.** If the name is in `TRUSTED_PLATFORM_ASSEMBLIES` (mscorlib, netstandard, System, System.\*, Microsoft.\*, plus the added `System.Security.Permissions`, `System.Runtime.Serialization.Formatters` and `System.CodeDom`), return `null` so the default ALC binds it. Ignore the requested version: CoreCLR accepts a higher framework version, as with mscorlib 2.0.0.0, which the probe verified.
2. **Our engine.** `UnityEngine`, `UnityEngine.*Module`, and any engine assembly we choose to reimplement (see 4) load from our build output. Keep the **same simple names**. Use versions at least as high as referenced: `0.0.0.0` for modules, `1.0.0.0` for `UnityEngine.UI` and `Unity.Timeline` if we replace those. Leave them unsigned. Forwarding facades are an option: a single real implementation with type forwards from each module name works, because CoreCLR follows `ExportedType` forwards exactly as the probe did for mscorlib.
3. **Shipped game code** loads from `Managed/` as is:
   - Assembly-CSharp(-firstpass)
   - FMODUnity(+Haptics/Resonance)
   - com.rlabrecque.steamworks.net
   - Newtonsoft.Json, VYaml(+Annotations), ImGui(.NET), LibNoiseDotNet, Ionic.Zip, ArabicSupport, 0Harmony
   - System.IO.Hashing
4. **Unity package assemblies.** These are compiled C# rather than engine icalls, so we decide per assembly whether to run Unity's IL against our UnityEngine:
   - UnityEngine.UI, Unity.TextMeshPro, Unity.Timeline
   - Unity.Mathematics, Unity.Collections, Unity.Burst
   - Unity.InternalAPIEngineBridge.004
   - Unity.Profiling.Core, Unity.MemoryProfiler, Unity.Multiplayer.Center.Common

   If we run Unity's copies, their references define extra API surface we must provide (`linux_unity.json`). `Unity.InternalAPIEngineBridge.004` calls **internal** CoreModule members through `InternalsVisibleTo`, and CoreCLR enforces that visibility. Our CoreModule would therefore need `[InternalsVisibleTo("Unity.InternalAPIEngineBridge.004")]` with matching internal members, or we reimplement the bridge.
5. **Never load** the Mono BCL copies, Mono.Security or I18N (§1.4).
6. **Native libraries.** Override `LoadUnmanagedDll` (or call `NativeLibrary.SetDllImportResolver` per assembly) to map `SimDLL`, `fmodstudio`, `steam_api`, `sdkencryptedappticket` and `cimgui` to `OxygenNotIncluded_Data/Plugins/`. CoreCLR's default probing only searches the assembly directory (`Managed/`) and the system paths (§3).
7. **Eagerly load** everything listed in `ScriptingAssemblies.json` that maps to rules 2 to 4 *before* touching any game type. This is B6.
8. **Mods.** If they are ever wanted, `AssemblyLoadContext.Default.Resolving` should forward to the game ALC, and `KMod.DLLLoader`'s `Assembly.LoadFrom` must also be steered into the game ALC.

Running the game inside a custom ALC has two side effects:

- **`Type.GetType("X, Assembly-CSharp")` binds through the *calling* assembly's ALC.** A game assembly calling it from inside the ALC is fine. Host code calling it from the default context returns `null`, which `Behave` verified. Host code that needs it should use `AssemblyLoadContext.EnterContextualReflection` or `alc.LoadFromAssemblyName`.
- **`AppDomain.GetAssemblies()` also returns the host's own assemblies and System.Private.CoreLib.** `App..cctor`, `AsyncLoadManager.Run` and `LegacyModMain.Load` call `GetTypes()` on all of them, and the last two do so without try/catch. **Every loaded assembly, host included, must be fully type-loadable** (B7).

### 2.3 Prototype result

The probe is `Probe/Program.cs`. It uses Unity's **original** `UnityEngine.*` from `Managed/` for this probe only, and covers 23 assemblies. Results:

```
Assembly-CSharp-firstpass: types=3035  typeLoadFailures=0 typeRefs ok=576  fail=0 memberRefs ok=4527  fail=0 jit ok=15118 fail=0
Assembly-CSharp:           types=17673 typeLoadFailures=0 typeRefs ok=1105 fail=0 memberRefs ok=27514 fail=0 jit ok=66979 fail=0
FMODUnity 1211/0, steamworks 1929/0 (2 type-load failures), Newtonsoft.Json 2848/2, Ionic.Zip 1264/2, 0Harmony 10544/4,
TMP 1749/0, UnityEngine.UI 1824/0, Timeline 857/0, Burst 3608/0, Collections 1424/0, Mathematics 9427/0, VYaml 528/0,
ImGui.NET 1563/0, LibNoise 573/0, UnityEngine.CoreModule (original) 10588/0
```

"needGenericCtx" MemberRefs are omitted from these counts. They are members on open generic instantiations, which `Module.ResolveMember` cannot resolve without generic context, and they are not errors.

Every failure, listed exhaustively:

- `TypeLoadException`: `Could not load type 'OptionValue' … contains an object field at offset 0 that is incorrectly aligned or overlapped by a non-object field`. This is `Steamworks.SteamDatagramRelayAuthTicket+ExtraField+OptionValue` (**B7**: fatal through `AsyncLoadManager.Run`).
- `FileNotFoundException System.Security.Permissions`: JIT of `Newtonsoft…JsonTypeReflector::get_DynamicCodeGeneration` and `::get_FullyTrusted`, `Ionic.Zip.SharedUtilities::ReadWithRetry`, and `0Harmony Mono.Cecil.Rocks.SecurityDeclarationRocks::TryProcessPermissionSetAttribute`.
- `FileNotFoundException System.CodeDom`: `Ionic.Zip.ZipFile::_SaveSfxStub`.
- `MissingMethodException`: the three 0Harmony Reflection.Emit methods from §1.2 (3).
- 0Harmony TypeRefs that point at `[System]MonoMod.Backports.*` and `[System]System.Runtime.CompilerServices.Unsafe`.

Assemblies that the probe bound from `Managed/` because they were referenced:

- 0Harmony, ArabicSupport, steamworks, FMODUnity, ImGui(.NET), Ionic.Zip, LibNoiseDotNet, Newtonsoft.Json, Unity.TextMeshPro
- UnityEngine.{Animation, AssetBundle, Core, ImageConversion, IMGUI, InputLegacy, JSONSerialize, ParticleSystem, Physics2D, Physics, ScreenCapture, TextCoreFontEngine, TextRendering, UI, UIModule, UnityWebRequest, Video}Module
- VYaml(+Annotations)

Assemblies that bound to the framework: mscorlib 2.0/4.0, netstandard 2.1, System 2.0/4.0, System.Core, System.IO.Compression 4.2, System.Net.Http 4.2, System.Numerics, System.Runtime.CompilerServices.Unsafe 6.0, System.Xml and System.Xml.Linq.

**Conclusion:** binding and JIT are not obstacles. The obstacles are B1 to B6, which are behavioural.

---

## 3. Native dependencies (DllImport)

### 3.1 Inventory

The inventory comes from `Analyze pinvoke` (output in `pinvoke.txt`).

| Library name (as written) | Assembly | # entry points | Calling convention | Marshalling features | Present on Linux? |
|---|---|---|---|---|---|
| `SimDLL` | Assembly-CSharp | 16 | Winapi (= cdecl on Unix) | blittable; `byte*`, `int*`, `char*` return; **1 delegate** param (`Sim/GAME_MessageHandler`); 1 `bool` | `libSimDLL.so` ✔ |
| `SimDLL` | firstpass (`Klei.KProfilerPlugin`) | 16 | Winapi | 2 `string` params (default ANSI→UTF-8 on Unix) | ✔ exported; the only RuntimeInitialize hook (`InitModule`) is empty |
| `fmodstudio` (`FMOD.VERSION.dll`) | FMODUnity | 585 | Winapi | 486 byref, 333 `[Out]`, 63 arrays, `bool` (4-byte, FMOD_BOOL), strings as `byte[]` UTF-8, delegates via `Marshal.GetFunctionPointerForDelegate` in structs (`CREATESOUNDEXINFO`) | `libfmodstudio.so` ✔; `libresonanceaudio.so` is an **FMOD plugin** (exports `FMODGetPluginDescriptionList`) loaded by FMOD via `System.loadPlugin` (`FMODUnity/Platform.cs:384-410`) |
| `steam_api` | steamworks.net | 930 | Cdecl | `MarshalAs(I1)` bools ×435, 154 `[Out]`, arrays, byref, 1 delegate (`SteamInputActionEventCallbackPointer`) | `libsteam_api.so` ✔ (Steamworks.NET 20.1.0, SDK 1.53a) |
| `sdkencryptedappticket` | steamworks.net | 11 | Cdecl | `I1`, `LPArray` | ✔ (unused by game) |
| `cimgui` | ImGui.NET (610), ImGui (1) | 611 | Cdecl | pointers; delegates `ImGuiInputTextCallback`, `ImGuiSizeCallback` | `cimgui.so` (no `lib` prefix; CoreCLR also probes `<name>.so`) ✔ |
| `EOSSDK-Win64-Shipping.dll` | firstpass (Epic) | 360 | Winapi | StringBuilder ×14, `LPStr`, 97 callback delegates | ✘, never bound (no Epic platform on Linux) |
| `rail_api64` | firstpass (WeGame/Rail) | 4 979 | Winapi | `CustomMarshaler` ×400 (`UTF8Marshaler`, supported by CoreCLR), `LPArray`, SWIG exception and string callbacks | ✘, never bound |
| `kernel32` | firstpass `SuperluminalPerf/NativeLibrary` | 2 | Winapi, SetLastError, Unicode/Ansi | – | ✘, Windows only |
| `dl`, `libdl.so.2`, `libc`, `libSystem`, `kernel32`, `kernelbase`, `ole32.dll` | 0Harmony (MonoMod) | 51 | mixed | runtime-selected per OS | Linux ones ✔ |

No `__Internal` imports exist. No game DllImport uses `PreserveSig=false`, `BestFitMapping`, `ThrowOnUnmappableChar`, or `SetLastError` on a Linux-relevant library.

None of the Linux plugins (`ldd`, `nm -D --undefined-only`) link against Mono, Unity or il2cpp. They only need libc, libm, libstdc++, libgcc_s and libdl, so they are runtime-agnostic.

### 3.2 SimDLL

All 32 imported entry points are exported by `libSimDLL.so` (1 212 exports in total).

- **AC `Sim` (`AC/Sim.cs:1001-1118`):**
  - `void SIM_Initialize(GAME_MessageHandler)`
  - `void SIM_Shutdown()`
  - `IntPtr SIM_HandleMessage(int, int, byte*)`
  - `IntPtr SIM_HandleMessages(int, int, int, byte*)`
  - `byte* SIM_BeginSave(int*, int, int)`
  - `void SIM_EndSave()`
  - `void SIM_DebugCrash()`
  - `char* SYSINFO_Acquire()`
  - `void SYSINFO_Release()`
- **AC `ConduitTemperatureManager`:**
  - `void ConduitTemperatureManager_Initialize()`, `_Shutdown()` and `_Clear()`
  - `int ConduitTemperatureManager_Add(float, float, int, int, float, float, bool)`
  - `int ConduitTemperatureManager_Set(int, float, float, int)`
  - `void ConduitTemperatureManager_Remove(int)`
  - `IntPtr ConduitTemperatureManager_Update(float, IntPtr)`
- **FP `Klei.KProfilerPlugin`:** the `kprofiler_*` exports and `kprofile_record_string(string)`.

How the Sim protocol works:

- All Sim traffic is a byte-buffer message protocol (`SimMessages.*` builds structs with `Marshal.SizeOf`) through `SIM_HandleMessage(s)`.
- Results come back as native pointers that the game reads unsafely, for example `GameDataUpdate*` in `Sim.cs:1088`.
- This is all blittable, so CoreCLR marshalling is a no-op and compatible.

**Callback.** The single managed callback has these properties:

- **Signature:** `public delegate int GAME_MessageHandler(int message_id, IntPtr data)` (`Sim.cs:9`), with no `UnmanagedFunctionPointer`, so Winapi, which is cdecl on Unix.
- **Registration:** it is passed as `Sim.SIM_Initialize(Sim.DLL_MessageHandler)`, which creates a **fresh, unrooted delegate** at each call site (`SaveLoader.cs:245,412,1029`, `WorldGenSimUtil.cs:18`).
- **What it does:** the handler (`Sim.cs:1120-1157`) only handles `ReportMessage` and `ExceptionHandler`. It calls `Marshal.PtrToStringAnsi` and then `KCrashReporter.ReportSimDLLCrash`.
- **Mono vs CoreCLR:** on Mono this was effectively safe. On CoreCLR the thunk can be collected after a GC (R2), and an exception escaping the reverse P/Invoke terminates the process.
- **When it fires:** only on SimDLL errors. Normal simulation never calls back.
- **Thread affinity:** SimDLL is driven from the main thread and, during worldgen, from the worldgen background thread (`WorldGenSimUtil.DoSettleSim`).

### 3.3 Other callbacks

- **FMOD:**
  - `FMODUnity.RuntimeManager` stores `debugCallback` and `errorCallback` in instance fields (`RuntimeManager.cs:61-63,301-302`). They are rooted, and marked with `[AOT.MonoPInvokeCallback]`, which is a no-op attribute. The attribute type must exist in our CoreModule (`AOT.MonoPInvokeCallbackAttribute`).
  - They are invoked on FMOD's threads and call `UnityEngine.Debug.Log*`, which therefore needs to be thread-safe.
  - The game itself registers no FMOD event callbacks: there are no `setCallback` or `EVENT_CALLBACK` uses in AC or FP.
- **Steam:**
  - `SteamManager` installs `SteamClient.SetWarningMessageHook(m_SteamAPIWarningMessageHook)`, stored in a field (`FP/SteamManager.cs:111`), and pumps `SteamAPI.RunCallbacks()` from `Update` (`:136`).
  - Steamworks.NET 20 uses manual dispatch, so callbacks run on the main thread inside `RunCallbacks`.
  - The 9 `ThisCall` delegates (`ISteamMatchmaking*Response`) are never used by ONI.
- **ImGui.NET:** the input-text and size callbacks are only used by dev tools.

### 3.4 Mono vs CoreCLR marshalling differences to watch

1. **Library search path:** see §2.2 rule 6. Unity's player searched `Plugins/`; CoreCLR does not.
2. **Delegate lifetime:** see R2.
3. **Exceptions across reverse P/Invoke frames** are fatal on CoreCLR. Mono sometimes unwound them.
4. **`bool` without `MarshalAs`** is a 4-byte BOOL on both runtimes; the FMOD and SimDLL ABIs match that. Steamworks uses `I1` explicitly.
5. **String default** (`CharSet` None means ANSI) means UTF-8 on Unix for both runtimes.
6. **Explicit-layout structs mixing references and values** are rejected at type load on CoreCLR (B7). There are no such structs in AC, FP or FMOD; the probe loaded all their types.
7. Runtime marshalling is enabled by default; there is no `DisableRuntimeMarshalling` in any assembly. `ICustomMarshaler` (`UTF8Marshaler`) is supported.

---

## 4. Bootstrap

This section draws on the decompiled sources and on UnityPy dumps of `level0`–`level2`, `sharedassets0-2` and `globalgamemanagers`. The scripts are in `agentA/`.

### 4.1 Runtime-initialize hooks

`RuntimeInitializeOnLoads.json` lists six entries.

The game's two entries:

- **`KleiItemsStatusRefresher.Initialize`** (AC, `loadTypes:0` = AfterSceneLoad, `AC/KleiItemsStatusRefresher.cs:37-41`).
  - It calls `KleiItems.AddInventoryRefreshCallback(...)`, which constructs `ThreadedHttps<KleiItems>.Instance`.
  - That constructor only sets fields; there is no I/O.
- **`Klei.KProfilerPlugin.InitModule`** (FP, `SubsystemRegistration`, `FP/Klei/KProfilerPlugin.cs:12-15`). Its body is empty.

The remaining four are Unity package internals that we can skip:

- Unity.Burst `$BurstDirectCallInitializer.Initialize`
- Unity.Collections `__JobReflectionRegistrationOutput__….EarlyInit` and `$BurstDirectCallInitializer`
- Unity.MemoryProfiler `MetadataInjector.PlayerInitMetadata`

`ScriptingAssemblies.json` lists every managed DLL in `Managed/`, engine modules included. Unity loads all of them up front (B6).

### 4.2 Scenes

The build settings in `globalgamemanagers` define three scenes:

| Index | File | Name |
|---|---|---|
| 0 | `level0` | `launchscene` |
| 1 | `level1` | `frontend` |
| 2 | `level2` | `backend` |

There is exactly **one** `SceneManager.LoadScene` call: `FP/App.cs:141`, single mode, by name. It is driven from `App.LateUpdate` (`App.cs:124-149`) after `App.LoadScene(name)` sets `loadingSceneName` (`:110-116`). The sequence is:

1. `KObjectManager.Cleanup`
2. `SimAndRenderScheduler.Reset`
3. `Resources.UnloadUnusedAssets`
4. `GC.Collect`
5. `OnPreLoadScene`
6. `SceneManager.LoadScene`
7. `OnPostLoadScene`

There are no `LoadSceneAsync`, additive or `Application.LoadLevel` calls.

`App.LoadScene` callers:

- `"frontend"`:
  - `InitializeCheck.Awake` (`AC/InitializeCheck.cs:36`)
  - `OfflineWorldGen.OnConfirmExit` (`:319`)
  - `MinionSelectScreen` back (`:36`)
  - `SaveLoader.OnSpawn` on failure (`:258,279`)
  - `PauseScreen` (`:270,333`)
- `"backend"`:
  - `OfflineWorldGen.Update` (`:274`)
  - `SaveLoader.LoadScene()` (`AC/SaveLoader.cs:1418-1421`), called from LoadScreen, InspectSaveScreen, ScenariosMenu and MainMenu resume

### 4.3 launchscene → frontend

`level0` contains:

- `BootCanvas` (Canvas, CanvasScaler, GraphicRaycaster)
- `BootLogo` and `BootBG` (Image)
- `Canvas`
- `EventSystem` (StandaloneInputModule)
- **`Launch Initializer`** (`LaunchInitializer`, `KProfilerBegin`, `KProfilerEnd`)

**`LaunchInitializer.Update`** (`AC/LaunchInitializer.cs:29-75`) runs these steps:

1. Waits for `Time.renderedFrameCount >= numWaitFrames (2)`.
2. **While `!DistributionPlatform.Initialized`**, which repeats every frame without Steam, it runs:
   - `GraphicsOptionsScreen.SetSettingsFromPrefs()`
   - `DontDestroyOnLoad(gameObject)`
   - `KPlayerPrefs.instance.Load()`
   - `DistributionPlatform.Initialize()`
   - a `SystemInfo.SupportsTextureFormat(RGBAFloat)` check
3. Waits for `IsDLCStatusReady()`, which is always true on Steam.
4. Calls `KFMOD.Initialize()`.
5. Calls `Util.KInstantiate` on each `SpawnPrefabs` entry, parented under the DontDestroyOnLoad object, in this order:
   1. KObjectManager
   2. BundledAssetsLoader
   3. KScreenManager
   4. ScreenPrefabs
   5. **Global** (also carries KCrashReporter, **App**, KComponentsInitializer, …)
   6. MusicManager
   7. InputInit
   8. Audio
   9. EffectPrefabs
   10. EntityPrefabs
   11. **GlobalAssets** (also carries **InitializeCheck**)
   12. **GameAssets** (Assets, BuildingLoader, EntityConfigManager, …)
   13. CustomGameSettings
   14. ImGui
   15. VirtualCursorController
6. Sets `enabled=false`.

`KMonoBehaviour.Awake` → `OnPrefabInit` runs synchronously inside Instantiate (`FP/KMonoBehaviour.cs:30-64`), so the order above is the Awake order.

**`Global.Awake`** (`AC/Global.cs:291-375`) then runs:

1. KCrashReporter hook
2. `GameObject.Find("Canvas")` + DontDestroyOnLoad
3. `OutputSystemInfo`
4. `MyCmp`/`MySmi`/`DevToolManager.Init`
5. SpriteAtlas
6. `FileSystem.Initialize`
7. StateMachine singletons
8. `Localization.RegisterForTranslation`
9. **`KMod.Manager.LoadModDBAndInitialize` + `Load(DLL)` + `Load(Strings)`**
10. **`KSerialization.Manager.Initialize()`** (assembly snapshot, B6)
11. `GameInputManager`
12. `Audio.Get`
13. KAnim managers
14. `Localization.Initialize` and the Translation mods
15. Local/Dev mod platforms
16. Metrics opt-out migration
17. `DistributionPlatform` event subscriptions
18. If Steam is initialized: KleiAccount login. Otherwise: `LogWarning` and `OnGetUserIdKey()`
19. `KleiItems.LoadInventoryCache`
20. LayerableFiles mods
21. **`WorldGen.LoadSettings(in_async_thread:true)`**, a `Task.Run` (`AC/ProcGenGame/WorldGen.cs:272-286`), plus the coroutine `ListenForLoadSettingsErrorRoutine` that rethrows the task's exception
22. `Resources.Load<GlobalResources>`

The startup continues with three more components:

- **`InitializeCheck.Awake`** (`AC/InitializeCheck.cs:30-52`):
  - Verifies the save directory is writable, a 15 MB file, and `WorldGenDataSave.worldgen`.
  - If those pass, calls `AudioMixer.Create()` and `App.LoadScene("frontend")`.
  - Otherwise shows a dialog and `QuitCode(1)`.
- **`Assets.OnPrefabInit`** (`AC/Assets.cs:179-218`):
  - Calls `LoadAnims` (`Resources.Load("animgrouptags")`).
  - Calls **`AsyncLoadManager<IGlobalAsyncLoader>.Run()`**, whose loaders run on the KWorker job threads and read `TextAsset.text` (§6).
  - Loads `GameAudioSheets`.
  - Calls `CreatePrefabs`, which runs `Db.Get()` (`Resources.Load<Db>("Db")`) and `LegacyModMain.Load()`.
- **`BundledAssetsLoader`** (`AC/BundledAssetsLoader.cs:13-38`) calls `AssetBundle.LoadFromFile(StreamingAssets/<dlc>)` per DLC. This install ships no DLC bundles.

**frontend (`level1`).** The steps:

- **`SceneInitializerLoader.Awake`** (`AC/SceneInitializerLoader.cs:22-37`):
  - Disables cameras via `FindObjectsByType<Camera>`.
  - Clears `isLoadingScene`.
  - Instantiates `SceneInitializerFE`, which pre-instantiates `FrontEndCamera`, `FrontEndManager` and `Lighting`.
- **`FrontEndManager.OnPrefabInit`** (`AC/FrontEndManager.cs:13-73`):
  - Instantiates `MainMenuForVanilla`/`ForSpacedOut`.
  - On first run it also instantiates the intro, the health message, the Locker canvases (DontDestroyOnLoad) and a `CoroutineRunner`.
- **`MainMenu.OnSpawn`** (`AC/MainMenu.cs:359-412`).
- **`MainMenu_Motd`**, an async UnityWebRequest (§5).

### 4.4 New game → worldgen → game

1. **`MainMenu.NewGame` → `NewGameFlow.BeginFlow`** (`AC/NewGameFlow.cs:42-55`). This instantiates `ModeSelectScreen` → `ClusterCategorySelectionScreen` → `ColonyDestinationSelectScreen` → `WorldGenScreen` in turn.
   - Building `ColonyDestinationAsteroidBeltData` already calls `new MutatedClusterLayout` (`AC/ColonyDestinationAsteroidBeltData.cs:126`) → **BinaryFormatter** (B2).
2. **`WorldGenScreen.OnSpawn` → `OfflineWorldGen.Generate`.**
   - `DoWorldGenInitialize` (`AC/OfflineWorldGen.cs:336-350`) calls `new Cluster(...)` and then `Cluster.Generate`.
   - `Cluster.Generate` starts `new Thread(ThreadMain)` (`AC/ProcGenGame/Cluster.cs:229-243`).
   - The thread generates each world and settles it in **SimDLL on that background thread** (`WorldGenSimUtil.DoSettleSim`).
   - It writes `<RootFolder>/WorldGenDataSave.worldgen`.
   - `OfflineWorldGen.Update` polls `HasGenerationStopped`, calls `Join()`, then `App.LoadScene("backend")`.
3. **backend (`level2`)** contains `SceneInitializerLoader`, `camera`, `EventSystem` and `Canvas`.
   - The `SceneInitializer` preloads `cameraMain`, `PreUpdateScheduler` and **`SaveLoader`**.
   - **`SaveLoader.OnSpawn`** (`AC/SaveLoader.cs:240-282`) either loads a save or calls **`LoadFromWorldGen()`** (`:977-1071`), which runs:
     1. `Sim.SIM_Initialize`
     2. element table
     3. `AllocateCells`
     4. `Sim.Load` per world
     5. `Sim.Start`
     6. **`SceneInitializer.PostLoadPrefabs()`**, which instantiates Lighting, **WorldRenderer**, **Game**, PathProber and the rest
     7. `NewSaveGamePrefab`
4. **`Game.OnPrefabInit`** (`AC/Game.cs:780-845`):
   - Replaces `Debug.unityLogger.logHandler`.
   - Calls `SceneManager.GetActiveScene().name`, `Shader.WarmupAllShaders` and `LayerMask` APIs.
   - Runs `StepTheSim(0)`.
   - Instantiates `GameScreenManager`.
   - Calls `AsyncPathProber.CreateInstance`, which starts the path-probe threads.
5. **`Game.OnSpawn`** (`:923-985`) sets `Time.timeScale=0` and calls `Resources.FindObjectsOfTypeAll(typeof(MeshRenderer))`.
6. **MinionSelectScreen** comes from a `SpawnScreen` component in the GameScreenManager prefab and runs a coroutine. Then `NewBaseScreen.Init`.
7. The first save comes from the coroutine `Game.DelayedInitialSave` → `SaveLoader.InitialSave` (`:1808-1838`).

This path depends on the following Unity facilities:

- Object lifecycle:
  - synchronous Awake/OnEnable during Instantiate and AddComponent
  - Start on the next frame
  - Update/LateUpdate
  - `OnApplicationQuit`/`OnApplicationFocus`
- DontDestroyOnLoad.
- Coroutines (`yield return null`).
- Single-mode scene load by name and `GetActiveScene`.
- Deserialized scene and prefab object graphs, including cross-file references between sharedassets0/1/2.
- `GameObject.Find`, `FindObjectsByType`, `FindFirstObjectByType` and `Resources.FindObjectsOfTypeAll`.
- `Resources.Load<T>` (Db, GlobalResources, GameAudioSheets, animgrouptags, FMOD settings and others) and `UnloadUnusedAssets`.
- `AssetBundle.LoadFromFile(Async)` (`FP/StreamedTextures.cs:110-140`).
- `Application.streamingAssetsPath`, `dataPath`, `persistentDataPath` and `consoleLogPath`.
- `Application.logMessageReceived` and `Debug.unityLogger.logHandler`.
- `Time.renderedFrameCount`, `timeScale` and `unscaledDeltaTime`.
- `SystemInfo.*` and `SpriteAtlas.GetSprites`.
- `Texture2D.LoadImage`/`EncodeToPNG`.
- `UnityWebRequest`.

### 4.5 Useful automation hooks

These come from `settings.yml` in the game root (`FP/Klei/GenericGameSettings.cs:104`):

- **`devBootSmoke`** quits after reaching the main menu, with exit code 0 or 1 (`MainMenu.cs:394-397`). With it, any error dialog quits with code 1 (`KCrashReporter.cs:394-399`).
- **`devAutoWorldGen`** starts worldgen automatically (`MainMenu.cs:403-409`).
- **`autoResumeGame`**, **`scriptedProfile.saveGame`** and **`devQuitAfterLoadingSave`** (`PerformanceCaptureMonitor.cs:145`).

These are ideal smoke tests for our runtime. Separately, an `ignorekcrashreporter.txt` file in the CWD disables crash reporting.

---

## 5. Platform services

### 5.1 Steam

- **`DistributionPlatform.Initialize()`** (`FP/DistributionPlatform.cs:75-85`) always creates `SteamDistributionPlatform`. There is no Epic, Rail or standalone implementation wired in; the Epic and Rail bindings exist only as dead code. There is no command-line flag either; `GetCommandLineArgs` is used nowhere.
- **`SteamManager`** is a lazily created GameObject (`FP/SteamManager.cs`). Its `Awake` does three things:
  1. Runs `SteamAPI.RestartAppIfNecessary(457140)`. If that returns true it calls `App.Quit()`; a `DllNotFoundException` leads to `App.QuitCode(2)`. `steam_appid.txt` ships, so this returns false.
  2. Runs **`SteamAPI.Init()`**. If it fails, it logs `LogWarning` and calls **`App.QuitCode(2)` → `Application.Quit(2)`** (`FP/App.cs:100-103`).
  3. If initialized, `Update` calls `SteamAPI.RunCallbacks()`.
- **So stock behaviour without Steam is a silent exit(2).**

**If the quit is suppressed**, by our `Application.Quit` ignoring that call or by a policy flag, the rest tolerates `Initialized == false`:

- `IsDLCStatusReady()` is true, so LaunchInitializer proceeds. It repeats its init block every frame, which is harmless.
- `IsDLCPurchased`/`Subscribed` return false unless `Application.isEditor` (`SteamDistributionPlatform.cs:127-165`), so the game runs vanilla only.
- `Global` skips the KleiAccount login and KleiItems. UGC, achievements and Steam mods are only added when initialized (`Global.cs:500-508,519-522`).
- MainMenu, KCrashReporter, LoadScreen, GameOptionsScreen and SteamInputInterpreter all gate on `Initialized`.

Remaining unguarded Steam calls, which would throw Steamworks.NET's "Steamworks is not initialized." `InvalidOperationException`:

- `AC/FeedbackScreen.cs:50` `SteamUtils.IsSteamRunningOnSteamDeck()`, which runs when the feedback screen opens.
- `AC/ScenariosMenu.cs:119` `SteamUGC.GetItemInstallInfo`, which needs UGC items.

A stub `com.rlabrecque.steamworks.net` with `Init()==true` is the alternative. It is more work, because more Steam paths become live.

### 5.2 Telemetry and web

- **`ThreadedHttps<T>`** (`FP/ThreadedHttps.cs`) uses `HttpClient` with a custom certificate callback, not UnityWebRequest.
  - It runs on a dedicated thread, and calls are blocking `.Result` on that thread.
  - Exceptions are caught. It retries 3 times (0 for KleiItems) with exponential backoff, then logs `LogWarning` and quits the sender. It never raises the crash dialog.
  - **Hazards:**
    - `End()` → `Thread.Abort` (R1).
    - In `Send`, an exception while `shouldQuit` is set does `continue` without incrementing the retry counter (`:168-171`). A persistent network error can therefore spin.
- **`KleiMetrics`** sends to `oni.metrics.klei.com/write`.
  - It is enabled unless `KPrivacyPrefs.disableDataCollection` is set.
  - Its session starts **even without Steam**.
  - It runs a `System.Timers.Timer` heartbeat, and `EndSession` runs on quit.
- **`KleiAccount`** only logs in if the platform is initialized and data collection is on (`FP/KleiAccount.cs:96-119`).
- **UnityWebRequest** is used in exactly two places:
  - **`KCrashReporter`** (`AC/KCrashReporter.cs:630-670`) multipart-POSTs to `games-feedback.klei.com/submit`, polling `isDone` in a coroutine. Failure only fires a callback.
  - **`MotdDataFetchRequest`** (`:82,144`) does `UnityWebRequest.Get(motd.klei.com…)` with `timeout=3` and a `completed` callback, followed by `Texture2D.LoadImage`. Errors log `LogWarning` and leave the MOTD empty.

  Both tolerate failure. A UnityWebRequest stub that completes with an error is sufficient.
- **KCrashReporter self-disables** if `hashes.json` MD5s of Assembly-CSharp(-firstpass) mismatch, or if `ignorekcrashreporter.txt` exists. It hooks `Application.logMessageReceived`; any Error or Exception log shows `ReportErrorDialog`, or is deferred while `App.isLoading`. **Our `Debug.LogError`/`LogException` must route through `logMessageReceived` exactly like Unity does.**
- **Opt-out:** write `disableDataCollection: true` to `<KleiRoot>/Agreements/OxygenNotIncluded/kprivacyprefs.yaml` (`FP/KPrivacyPrefs.cs`). This disables metrics, account login and crash upload, and is recommended for development.

---

## 6. Threading

### 6.1 Threads the game creates

| Thread | Created at | What runs there |
|---|---|---|
| `KWorker0..N-2` (`SystemInfo.processorCount-1`, AboveNormal priority, **128 KiB stack**) | `FP/JobManager.cs:20-31,106-112` (`GlobalJobManager`) | `IWorkItemCollection`: `WorkItemCollection<T,S>.Run` / `WorkItemCollection<S>.RunItem`, `DivisibleTask<S>`, `AsyncLoadManager.RunLoader`. Used by NavGridUpdater, GlobalChoreProvider (FindTopPriorityTask), FetchManager, ConduitFlow, SolidTransferArm, SimDebugView, ConduitFlowVisualizer and the startup asset loaders. The main thread also participates. Note: `new Semaphore(0, coreCount-1)` throws if `processorCount == 1`. |
| AsyncPathProber agents | `AC/AsyncPathProber.cs:355` | `PathProber.Run` over Navigator abilities |
| Worldgen | `AC/ProcGenGame/Cluster.cs:240` | Entire world generation, **including SimDLL settle** |
| `Task.Run` (ThreadPool) | `AC/ProcGenGame/WorldGen.cs:277` | `WorldGen.LoadSettings`, YAML settings load at startup |
| HTTP | `FP/ThreadedHttps.cs:119` | Metrics, account and items |
| `System.Timers.Timer` (ThreadPool) | KleiMetrics heartbeat | Metrics |
| Debug | `AC/DebugHandler.cs:520` | Dev key only |
| Native-owned | FMOD mixer and stream threads | `RuntimeManager.DEBUG_CALLBACK`/`ERROR_CALLBACK` → `Debug.Log*` |
| FileSystemWatcher | `FP/TuningSystem.cs` | Dev only |

### 6.2 UnityEngine APIs reachable off the main thread

These results come from `Analyze reach`, an IL call-graph walk with class-hierarchy virtual dispatch. It starts from the thread entry points above and stops at lazy initializers (`Db.Initialize`, `.cctor`). A full transitive walk over-approximates because of virtual state-machine dispatch (≈20k methods), so the table uses a **depth-limited (≤4–5 calls) view**. That view is the high-confidence set:

| Entry | UnityEngine members reached (excluding value-type math: Mathf, Vector2/3, Color, Color32, Rect) |
|---|---|
| Job workers (20 entry methods) | `Component.get_transform`, `Component.get_gameObject`, `Component.GetComponent`, `GameObject.GetComponent`, `GameObject.get_transform`, `Transform.get_position`, `Object.op_Equality`/`op_Inequality`/`op_Implicit`, `Object.get_name`, `TextAsset.get_text` (AsyncLoadManager loaders), `Resources.Load` (lazy `Db.Get`), `Debug.LogError` |
| Path prober (full closure, 1 533 methods) | the above plus `Application.isEditor`/`isPlaying`/`platform`/`streamingAssetsPath`, `GameObject..ctor`/`AddComponent`/`GetComponents`, `Object.Destroy`, `Object.set_name`, `Object.GetInstanceID`, `UnityEngine.Pool.ObjectPool<T>.Get/Release` (most of these sit behind rarely-taken lazy paths) |
| Worldgen thread | `Application.dataPath`, `isEditor`, `platform`, `Debug.isDebugBuild`, `Debug.Log`/`LogError`, `Object.get_name`/`op_Inequality` |
| `WorldGen.LoadSettings` task | `Application.isEditor`/`isPlaying`/`platform`/`streamingAssetsPath`, `Object.get_name`/`op_Inequality` |
| HTTP thread | `Object.get_name`, `Object.op_Inequality` |

**Thread rules our UnityEngine must implement:**

1. Value types and math are free-threaded.
2. **These must be callable from any thread:**
   - `Application.*` path and platform getters: cache them at startup as immutable values.
   - `Debug.Log*`: thread-safe, and must fire `logMessageReceivedThreaded` on the calling thread. `logMessageReceived` gets only messages logged on the main thread: Unity neither calls it for another thread's messages nor queues them to the main thread (measured, UnityProbe `logthreads`; ARCHITECTURE §4), so KCrashReporter, which subscribes there, never sees them.
   - `Object.==`/`!=`/`bool`, `name` and `GetInstanceID`: lock-free reads of a native-alive flag.
   - `Component.transform`/`gameObject`, `GetComponent`/`GetComponents` and `Transform.position` *reads*: the game relies on release-player leniency here.
   - `TextAsset.text`/`bytes` and `Resources.Load` of already-loaded assets.
3. Structural mutation is main-thread only and may assert: `Instantiate`, `AddComponent`, `Destroy`, `SetParent`, and the position/rotation setters. Worker code paths to these are rare and lazy.
4. SimDLL must tolerate calls from both the main thread and the worldgen thread, as it already does under Mono.

### 6.3 Platform checks

These are taken from the decompiled sources.

- The game queries `Application.platform`, `isEditor`, `isPlaying`, `isDebugBuild` and `SystemInfo.*` on hot paths.
- On this Linux build, `RuntimePlatform.LinuxPlayer` must be reported.
- `isEditor` must be **false**. When `true` it changes behaviour, for example:
  - DLC ownership is granted (`SteamDistributionPlatform.cs:127-165`)
  - `KCrashReporter.terminateOnError=false` (`:324-327`)
- `isPlaying` must be **true**.

See §7.5 for the full list of platform branches.

---

## 7. Reflection and serialization over Unity types

Sources for this section:

- decompiled code
- a dump of a real save's KSerialization type directory (Cycle 100, build 701625) made by `agentB/scripts/dir.py`, with output in `agentB/dir.txt`

### 7.1 Save format (KSerialization)

**File layout.** `AC/SaveLoader.cs:772-879` writes it and `:902-975` reads it. In order:

1. A header: `uint buildVersion, int headerSize, uint headerVersion, int compression`.
2. **Newtonsoft JSON** `GameInfo` (`AC/SaveGame.cs:233,309`). This makes B1 a save blocker too.
3. The `KSerialization.Manager.SerializeDirectory` type-template directory (`FP/KSerialization/Manager.cs:111`).
4. The body, compressed with **Ionic.Zlib `ZlibStream`** (`BestSpeed`, `SaveLoader.cs:284-301`). This works on CoreCLR without code pages (verified). The body holds `Serializer.Serialize(SaveFileRoot)`, `Game.SaveSettings`, `Sim.Save` (SimDLL blob), `SaveManager.Save` and `Game.Save`.

**No BinaryFormatter and no Unity `JsonUtility` is involved in saves.**

**Type names.** The save stores `Type.FullName` (`FP/KSerialization/Helper.cs:668-671`), which is not assembly-qualified except for generic arguments. On load it resolves names with `Manager.GetType` (`Manager.cs:30-50`):

1. `Type.GetType(name)`, which searches firstpass and CoreLib.
2. Otherwise, the first match among the `AppDomain.GetAssemblies()` snapshot taken in `Manager.Initialize()` (called from `AC/Global.cs:340`).

Consequences:

- B6: preload every assembly before `Global.Awake`.
- **Never define the same full type name in two loaded assemblies.** For example, our UnityEngine must not duplicate a type that a game assembly also defines. The first match wins, and a TypeInfo mismatch silently drops members (`DeserializationMapping.cs:26-80`).

**Member selection rules** (`FP/KSerialization/SerializationTemplate.cs`):

- Types without `[SerializationConfig]` default to **OptOut**: all public instance fields plus public settable non-indexer properties, at every hierarchy level (`:90-98,111-118,149-188`).
- **OptIn** types (for example `KMonoBehaviour`, `FP/KMonoBehaviour.cs:5`) take `[Serialize]` members, public or not (`:120-134,158-172`).
- `[NonSerialized]` excludes a member.
- `[OnSerializing]`, `[OnSerialized]`, `[OnDeserializing]` and `[OnDeserialized]` (the System.Runtime.Serialization attributes), plus Klei's `[CustomSerialize]`/`[CustomDeserialize]`, are found by `Helper.GetSerializationMethods` (`Helper.cs:623-648`) and invoked with no arguments.
- Objects are created with `Activator.CreateInstance`, or with `FormatterServices.GetUninitializedObject` when there is no parameterless constructor (`DeserializationMapping.cs:361`).

**Reflection over our UnityEngine classes.** Template construction for KMonoBehaviour subclasses walks into **`UnityEngine.MonoBehaviour`, `Behaviour`, `Component` and `Object`**:

- It calls `GetFields`/`GetProperties` with NonPublic.
- It calls `GetCustomAttributes(false)` on every member.

So:

- Any attribute we put on members of those classes must be constructible.
- Public fields or settable properties we add there would not be serialized for OptIn types. They would still be visited.

### 7.2 Unity types inside saves: layout constraints on our UnityEngine

- **Special-cased by `type == typeof(...)`** (`Helper.cs:85-100,176-198`; `IOHelper.cs:89-118`; `DeserializationTemplate.cs:204-215`):
  - `Vector2` is written as `x,y` floats.
  - `Vector3` is written as `x,y,z` floats.
  - `Color` is written as 4 bytes, `r,g,b,a × 255`.
  - The code accesses the public **fields** `x/y/z` and `r/g/b/a` directly in IL, so they must stay fields.
- **Reflective OptOut path:**
  - **`UnityEngine.Color32`** is present in real saves as the template `r:byte; g:byte; b:byte; a:byte`, with no properties (`AC/FallingWater.cs:40-50`).
  - **`UnityEngine.Vector4`** is used as `[Serialize] Vector4[] m_biomesSize` (`AC/WorldContainer.cs:247-248`).
  - Our versions must expose **exactly** these public instance fields and **no public settable instance property**. Unity's indexers are allowed, because indexers are excluded.
- **`Quaternion`** for every saved transform is written manually as `x,y,z,w` (`FP/Util.cs:583-591,835`; `AC/SaveLoadRoot.cs:100,170-172`).
- `SaveLoadRoot` matches components by `GetType().ToString()` (`:136,248-258`). Optional components use `Type.GetType(name)` + `AddComponent(Type)` (`:271-277`). These names are always game types.
- `StateMachineSerializer` is covered by **B8**. `SerializedList<T>` uses FullName and `Type.GetType` for Assembly-CSharp types only (`AC/SerializedList.cs:57,86`).

### 7.3 Other serializers

- **Newtonsoft.Json 7.0** (needs B1) is used for:
  - the save header
  - `mods.json` (`AC/KMod/Manager.cs:92,1009,1097`)
  - key bindings (`FP/GameInputMapping.cs:118,151`)
  - Unlocks (`AC/Unlocks.cs:333,390`)
  - retired colonies (`AC/RetireColonyUtility.cs:40`)
  - outfits (`AC/SerializableOutfitData.cs:80`)
  - `TuningSystem` (`StreamingAssets/Tuning.json`)
  - KleiItems, Account and Metrics payloads

  **No Unity types are serialized with Newtonsoft.**
- **Unity `JsonUtility`** is used once, in `AC/PerformanceCaptureMonitor.cs:107`, and only when `scriptedProfile` is set. Low priority.
- **VYaml** is used only for element data: `AC/KYaml.cs:29-78`, with custom formatters for Klei's `Vector2f`, Tag, SimHashes and ElementState, called from `AC/ElementLoader.cs:46`. It runs fine on CoreCLR (all methods JIT).
- **YamlDotNet** is embedded in firstpass and accessed via `FP/Klei/YamlIO.cs`. It handles worldgen, templates, the codex, `mod.yaml`, `KPlayerPrefs` (`kplayerprefs.yaml`), `KPrivacyPrefs` and `settings.yml`.
  - It inspects properties only (`ReadablePropertiesTypeInspector`).
  - Some Unity-typed YAML-visible properties exist: `CodexImage.sprite`/`color` and `SubEntry.icon`. The shipped YAML does not use them.
- **BinaryFormatter** is covered by B2. It is used only by `ObjectCloner.SerializingCloner`, but on these normal paths:
  - `MutatedWorldData..ctor` (every world)
  - `MutatedClusterLayout..ctor` (destination screen and worldgen mixing)
  - `SettingsCache.cs:364`
  - `WorldLayout.cs:728`
  - `WorldgenMixing.cs:296`

  The cloned graph (`Graph/` output) is entirely `[Serializable]` game and BCL types. It includes Klei's `Vector2I`, `MinMax`, `Tag` and `TagSet`, and **no UnityEngine types**.

### 7.4 Reflection the game does on (or across) Unity types

- **Whole-domain type scans:**
  - `App..cctor` (try/catch)
  - **`AsyncLoadManager.Run` and `LegacyModMain.Load` (no try/catch, B7)**
  - `KSerialization.Manager`
  - `TuningSystem`
  - `DevToolSimDebug`
  - ImGui `ReflectionUtil`

  Consumers of `App.GetCurrentDomainTypes()` include `LoopingSoundManager.cs:102` and `KFMOD.cs:284`, which instantiate parameter updaters with `Activator.CreateInstance`.

  **Every type in every loaded assembly, ours included, must load on CoreCLR.** Our UnityEngine assemblies must therefore contain no explicit-layout unions that mix object references.
- **`typeof(X).IsAssignableFrom` filters:** `AsyncLoaderType`, `UserMod2` and the KMonoBehaviour MyCmp machinery. `FP/MyAttributeManager.cs:58-90` stops its walk at `KMonoBehaviour`, and uses `MakeGenericMethod` plus `Delegate.CreateDelegate`.
- **Unity component calls taking a Type or string:**
  - `GetComponent(Type)`: `AC/RocketModule.cs:340`, `RequireAttachedComponent.cs:41`, `SelectToolHoverTextCard.cs:668`.
  - `AddComponent(Type)`: `FP/Util.cs:428`, `AC/BuildingLoader.cs:139`, `SaveLoadRoot.cs:277`.
  - `GetComponent(string)`: `FP/Util.cs:160,199` (no callers) and `AC/DetailsScreen.cs:472-480` (unused).
- **`Type.GetType` strings naming Unity types:** only `FP/NodeEditorFramework.Utilities/GUIScaleUtility.cs:46` (`"UnityEngine.GUIClip"`, throwOnError), which belongs to an editor-only framework and is unreferenced. **There are no `"UnityEngine.X, UnityEngine…"` strings in game code.**
- **Recursion hazard** in `AC/Localization.cs:831-866` (`SetFont`, reached from the language-change dialog):
  - It recursively reflects over all fields (`Instance|Public|NonPublic|FlattenHierarchy`) of every component, and follows `GameObject` fields into `GetComponents<Component>()`.
  - Real Unity components hold only native pointers. **If our `Component`/`Transform`/`GameObject` keep managed back-reference fields, this can recurse until it overflows the stack.**
  - Mitigation options: mark such fields so the walk skips them (it does not check attributes, so this needs a different storage shape), or store engine state outside the managed object, for example in a side table keyed by instance id.
- **Unity serialization dependence:**
  - About 2 146 `[SerializeField]` uses and about 31 `ScriptableObject` subclasses.
  - Many fields are only ever set by scene or prefab deserialization, for example `Global.forcedAtlasInitializationList` (`Global.cs:311`) and `LaunchInitializer.SpawnPrefabs`.
  - `Resources.Load` targets include `Db`, `GlobalResources`, `GameAudioSheets`, `Audio`, `ProgressBarsConfig`, `BuildingCellVisualizerResources`, `AudioMixerSnapshots`, fonts and materials.
  - Our asset loader must reproduce Unity's field-serialization rules: public, or `[SerializeField]`, non-static, non-readonly and serializable type, with `[Serializable]` on nested plain classes. It must do so for game classes when instantiating scene and prefab data.

### 7.5 Application and platform branches

| Member | Use | Value we must report |
|---|---|---|
| `isPlaying` | 33 uses (e.g. `SaveLoader.cs:400`) | `true` |
| `isEditor` | 12 uses: DLC checks (`FP/DlcManager.cs:672`), EditorOnly worldgen skips (`FP/ProcGen/Worlds.cs:93,117`, `ClusterLayouts.cs:79`), `KCrashReporter.cs:324` | `false` |
| `platform` | 35 uses. Game code compares only `WindowsPlayer`/`WindowsEditor` (`FP/Util.cs:762,786,939`). FMODUnity maps `LinuxPlayer` to `PlatformLinux` (`FMODUnity/Settings.cs:356`). UI and TMP compare mobile and console values. KleiMetrics sends `platform.ToString()`. | `RuntimePlatform.LinuxPlayer` |
| `persistentDataPath` | `FP/Util.cs:27` static initializer. On Linux `GetKleiRootPath()` and `RootFolder()` both return it (`Util.cs:760-791`): saves go to `RootFolder/save_files`, worldgen to `RootFolder/WorldGenDataSave.worldgen`, the cache to `persistentDataPath/cache` | `~/.config/unity3d/Klei/Oxygen Not Included` (from `app.info`: "Klei", "Oxygen Not Included") |
| `dataPath` | `DLLLoader.cs:19` (`Managed/ModLoader.dll` probe), `GenericGameSettings.cs:104` (`../settings.yml`), `TuningSystem.cs:99`, FMOD plugin dir `dataPath/Plugins/lib*.so` (`FMODUnity/PlatformLinux.cs:39-41`) | `<game>/OxygenNotIncluded_Data` |
| `streamingAssetsPath` | 27 uses | `<dataPath>/StreamingAssets` |
| `consoleLogPath`, `systemLanguage`, `isBatchMode`, `OpenURL`, `Quit(int)`, `targetFrameRate` | single uses | – |
| `logMessageReceived` | `KCrashReporter.cs:273,332`: any Error or Exception shows the crash dialog | Must be raised for uncaught exceptions in Unity callbacks and coroutines, as Unity does |
| `Debug.unityLogger.logHandler` | replaced by `LogCatcher` (`AC/Game.cs:782`) | Must be a settable `ILogHandler` that all `Debug.Log*` calls route through |

These members are not used: `version`, `unityVersion`, `runInBackground` and `logMessageReceivedThreaded`.

### 7.6 Harmony and dynamic loading

- `0Harmony` 2.4.2.0 (with MonoMod.Core) is referenced by Assembly-CSharp, but **is used only for mods**:
  - `AC/KMod/DLLLoader.cs:118-128` (`new Harmony(id)`, `UserMod2.OnLoad` → `PatchAll`)
  - `AC/KMod/UserMod2.cs:15-18`

  Those run only from `Mod.Load` for enabled mods with DLLs (`AC/KMod/Manager.cs:580-585`). **No unconditional Harmony use exists at startup**, so 0Harmony is only loaded lazily (and by the whole-domain scans if preloaded).
- **Code that does run on every startup:**
  - `Global.Awake` → `modManager.Load(Content.DLL)` → `DLLLoader.LoadUserModLoaderDLL()` (`DLLLoader.cs:15-48`).
  - That method probes `File.Exists(dataPath/Managed/ModLoader.dll)`. The file is absent, so nothing happens.
  - If the file existed, the method would call `Assembly.LoadFile` and reflection-invoke `ModLoader.ModLoader.Start`.
- Mod DLLs use `Assembly.LoadFrom` and then `GetTypes()` to find `UserMod2` (`DLLLoader.cs:75,92-104`). See §2.2 rule 8. Mods are out of scope.
