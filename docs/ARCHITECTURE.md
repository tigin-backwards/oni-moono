# Architecture

This document fixes the shape of the system: what we replace, what we keep, how the pieces
fit, and the rules each piece follows. `docs/PLAN.md` sequences the work. The evidence behind
every decision is in `docs/research/` (`assets.md`, `unity-usage.md`, `runtime.md`,
`brutal-api.md`); section references like *usage §1.3* point there.

## 1. Goal and boundary

Run Oxygen Not Included's **original, unmodified** managed code and data on stock .NET 10
CoreCLR, with Unity's engine replaced by our own implementation built on BRUTAL.

**What "the Unity layer" is.** Unity ships a game as three things:

1. the native player (`UnityPlayer.so`: object system, serialization, renderer, input, …);
2. the engine's managed API: `UnityEngine.*Module.dll` (thin C# over the native player);
3. the Mono runtime and its class libraries (`MonoBleedingEdge`, `Managed/mscorlib.dll`, …).

We replace **all three**: (1) and (2) with our engine, (3) with .NET 10. Unity's packages the
game ships in `Managed/` (`UnityEngine.UI.dll`, `Unity.TextMeshPro.dll`, `Unity.Timeline.dll`)
are replaced too, by this project's clean-room versions of the same names (`src/UnityEngine.UI`,
`src/Unity.TextMeshPro`, `src/Unity.Timeline`; docs/cleanroom). The host never loads Unity's.

**What we keep, unmodified, as game code:**

| Assembly | Why it stays |
|---|---|
| `Assembly-CSharp`, `Assembly-CSharp-firstpass` | The game. |
| `FMODUnity*.dll`, `com.rlabrecque.steamworks.net.dll`, `ImGui.NET.dll`, `Newtonsoft.Json`, `VYaml`, `LibNoiseDotNet`, `Ionic.Zip`, `ArabicSupport`, … | Third-party game dependencies. |
| Native plugins (`libSimDLL.so`, `libfmodstudio.so`, `libresonanceaudio.so`, `libsteam_api.so`, `cimgui.so`) | The game's own natives, P/Invoked by game code. |
| All data files (`globalgamemanagers*`, `level*`, `*.assets`, `.resS`, `resources.resource`, `StreamingAssets/**`, `Resources/unity default resources`, `Resources/unity_builtin_extra`) | Loaded read-only by our asset layer. |

Nothing from `UnityPlayer.so`, `UnityEngine.*Module.dll`, Unity's packages or Mono is loaded.

The reference install is the Linux build at `~/projects/oni-linux` (Unity 6000.3.5f2,
`m_GraphicsAPIs = [OpenGLCore]`). The target platform is **linux-x64**. The Windows player
runs the Windows build's own natives and data, whose shaders carry OpenGL programs beside the
Direct3D ones (§6).

## 2. Process and assembly structure

```
OniMoono.Host (exe OniMoono)             entry point; ALC setup; main loop; CLI flags
 ├─ UnityEngine.*Module.dll  (generated)  type-forwarding facades, one per Unity module name
 │     └──forwards to──┐
 ├─ OniMoono.UnityEngine.dll             THE engine API: every public UnityEngine.* / UnityEngine.*.*
 │     │                                  type the game, uGUI, TMP, Timeline and FMODUnity reference
 │     ├─ OniMoono.Assets.dll            Unity file formats: SerializedFile, UnityFS, type trees,
 │     │                                  LZ4/LZMA, object readers, managed-layout builder. No engine types.
 │     ├─ OniMoono.Graphics.dll          GL 4.5 core device: buffers, textures, programs, state,
 │     │                                  framebuffers; Unity shader-blob runtime. No engine types.
 │     ├─ OniMoono.Desktop.dll           GLFW window, GL context, input events, clipboard, cursors,
 │     │                                  display modes, timing.
 │     └─ BRUTAL packages                 Ahwoo.BRUTAL.{Core,Glfw,OpenGl,Numerics,Stb,…}
 └─ game assemblies from Managed/ (unmodified on disk)
```

**Why facades.** Game IL binds to `[UnityEngine.CoreModule]UnityEngine.GameObject`,
`[UnityEngine.UIModule]UnityEngine.Canvas`, and so on. CoreCLR follows `TypeForwardedTo`, so a
facade named `UnityEngine.CoreModule` that forwards to `OniMoono.UnityEngine` binds every
reference exactly as compiled, while the implementation lives in one assembly with ordinary
`internal` access between subsystems (Unity itself relies on `InternalsVisibleTo` between its
modules; we avoid needing that). The facade sources are **generated** by `tools/FacadeGen` from
the original module assemblies (to learn which type lives in which module) intersected with the
types we implement. A facade `UnityEngine.dll` (forwarding everything) is also generated for
completeness.

**Assembly identity.** Unity assemblies are referenced as version `0.0.0.0`, no public key.
Our facades carry the same names and version `0.0.0.0`.

**Load context.** The game runs in the **default** AssemblyLoadContext, so its `Type.GetType`,
`Assembly.Load` and `AppDomain` queries behave as under Unity. `src/OniMoono.Host/AssemblyPolicy.cs`
resolves names the framework does not provide:

1. Names in the .NET shared framework always win, so Mono's BCL copies in `Managed/` are never
   loaded. Mono class libraries the framework lacks (`I18N*`, `Mono.Security`, …) are refused.
2. `UnityEngine` and `UnityEngine.*Module` resolve only to our facades in `engine/`; Unity's
   originals are never loaded (a module without a facade is one nothing references).
3. Unity's own support packages (Burst, Collections, Mathematics, MemoryProfiler, Profiling.Core,
   Multiplayer.Center.Common) are not loaded: they implement engine features we replace and no
   game assembly references them.
4. Unity's UI packages (uGUI, TMP, Timeline) are this project's (PLAN M9), from `packages/`
   next to the host, never the shipped ones from `Managed/`; the host stops with an error when
   one is missing.
5. Everything else loads from `Managed/` (game and third-party libraries). Our Steamworks.NET
   port and the engine bridge ship with the host and therefore take precedence.

The scripting assemblies of `ScriptingAssemblies.json` are preloaded before any game code runs,
as the player does.

**Native libraries.** A per-assembly `SetDllImportResolver` (`GamePlugins`) maps the game's
`DllImport` names (`SimDLL`, `fmodstudio`, `resonanceaudio`, `steam_api`, `cimgui`, …) to
`OxygenNotIncluded_Data/Plugins/lib*.so` (Windows: `Plugins/x86_64/*.dll`) on every assembly
but the framework's and BRUTAL's, so the game's file binds before the runtime's own probing,
as in Unity's player. That probing (the importing assembly's folder, then the system's search:
System32, the working directory, PATH, LD_LIBRARY_PATH) found any other copy of the name first
when only `ResolvingUnmanagedDll` mapped them: #74, an older `steam_api64.dll` elsewhere on the
Windows search bound in place of the game's, and Steam never initialised. `ResolvingUnmanagedDll`
remains for what the resolver leaves (a plugin missing or not loading: D58's messages). BRUTAL
installs its own per-assembly resolvers; we never call `SetDllImportResolver` on BRUTAL
assemblies. The Vulkan loader name quirk in
`brutal-api.md` does not affect us (no Vulkan).

**The Windows executable.** On Windows `OniMoono.exe` is our own native host
(`src/OniMoono.NativeHost/host.c`, about 150 lines of C), not the .NET SDK's apphost, which
can't export data. The drivers of hybrid-graphics laptops (NVIDIA Optimus, AMD PowerXpress) read
two DWORD exports of the process's main executable, `NvOptimusEnablement` and
`AmdPowerXpressRequestHighPerformance`, and pick the discrete GPU when they are nonzero. The
original `OxygenNotIncluded.exe` exports both with the value 1, so ours does too. Otherwise it
does what the apphost did, the documented way (dotnet/runtime's
`docs/design/features/native-hosting.md`): `hostfxr.dll` from its own folder when it's there (the
self-contained player of setups and releases, as the apphost looks first), else the installed
.NET's through nethost's `get_hostfxr_path` (the framework-dependent `scripts/build-windows.sh`
player); then `hostfxr_initialize_for_dotnet_command_line` with `OniMoono.dll` and the process's
arguments as the C runtime split them, `hostfxr_run_app`, `hostfxr_close`, and the app's exit
code. A failure to start goes to stderr when that is redirected, else to a message box and a
line in the game's `Player.log`. It keeps the apphost's resources: the icon as resource 32512
(`GlfwPlatform.SetClassIcon` loads it), the SDK's manifest (`asInvoker` only; GLFW sets the DPI
awareness), the version information and the GUI subsystem. It is built with MSVC and the static C
runtime by `build.cmd`, driven from WSL by `scripts/build-native-host.sh` (`build-windows.sh` and
`release.sh` call it); `setup.ps1` builds it when the Visual Studio C++ build tools are installed
and otherwise keeps the SDK's apphost, with a warning (that player starts the same but gives no
GPU hint). `scripts/check-windows-host.py EXE [--like OTHER]` checks a built exe from its PE file
alone: both exports present, writable and 1, the icon, manifest, version and subsystem. It also
starts a second copy of itself as the crash watcher before the runtime starts (`crash.c`; §7,
"Native crashes").

**The GPU option** (`GpuChoice`; Moono Tweaks' "Graphics card", settings.json `"gpu"`, `--gpu
default|discrete|integrated`) is applied in `Program.Main` before the window opens, so before
anything loads the OpenGL driver. On Windows it changes nothing: the two exports stay at the 1
they're compiled with, as the original has them (the documented form: NVIDIA's Optimus guide and
AMD's GPUOpen say the driver looks them up in the executable), so `default` and `discrete` are the
same, and Windows' Graphics settings page overrides them. `integrated` can't be asked for on
Windows' OpenGL: no driver documents seeing a value written at run time, and 0 would only withdraw
the request, not ask for the integrated GPU. The log says to choose it in Windows' Graphics
settings. Unity's
Linux player sets no GPU hints, and `-force-device-index` covers Direct3D, Metal and Vulkan only,
not ONI's OpenGL build, so on Linux `default` sets nothing. `discrete` asks for PRIME render
offload: `__NV_PRIME_RENDER_OFFLOAD=1` and `__GLX_VENDOR_LIBRARY_NAME=nvidia` when NVIDIA's
proprietary driver is there (`/proc/driver/nvidia/version` and a loadable `libGLX_nvidia.so.0`),
else Mesa's `DRI_PRIME=1`; `integrated` sets `DRI_PRIME=0`. Mesa's default GPU is "the one used
by Wayland/Xorg or the one connected to a display", and `DRI_PRIME=N` selects the Nth other one
(Mesa's envvars documentation, which documents only N > 0): `DRI_PRIME=0` asks for no other GPU,
so it gets that default, which is the integrated one only when the display runs on it. They go into the native environment
with libc's `setenv` (.NET's `Environment.SetEnvironmentVariable` changes only .NET's copy on
Unix, which libGL never reads) and into .NET's, for processes the player starts. A variable the
user set is left alone; those Moono set are listed in `ONI_MOONO_GPU_ENV`, so the run
`App.Restart` starts replaces them with its own choice. On a desktop whose display is on the
discrete card, `DRI_PRIME=1` may pick the integrated GPU. The log has a `gpu:` line with what was
set, then the `OpenGL:` line naming the renderer.

### 2.1 Replacing Mono: runtime compatibility (*runtime §0*)

Binding and JIT are clean: every BCL reference in the game resolves on .NET 10 and all ~82k
game methods JIT. The differences that remain are behaviours Mono's class libraries provided
and .NET 10 ships out-of-band. The host restores them the way .NET documents for
.NET Framework-era code where it can. Game methods are patched at run time only where they
persist or depend on runtime-specific values: two serialization paths (D1 state-machine
parameter names, D3 a local mod's version stamp), mods' loading (D11) and the bubble manager's
one use of a struct's hash as an identity (D26). The game's conversions of floats to integers
are rewritten as its assemblies load (D29), in the same pass as its directory listings (D30),
the double close in 0Harmony's MonoMod (D31) and its thread aborts (D47). The game's Harmony is
adjusted where patches would run differently on CoreCLR (D12–D14, D17–D21, D33, D34, D43), and
mods' assemblies are rewritten as they load where their IL relies on Mono (D11, D22, D23,
D27–D32, D40, D42, D47, D50, D51, D56). No other game IL
is touched.

| Mono provided | Host provides | Needed by |
|---|---|---|
| CAS types in mscorlib | `System.Security.Permissions` package in the host's deps | Newtonsoft.Json 7 (save header, settings, mods.json, key bindings) |
| In-box BinaryFormatter | `System.Runtime.Serialization.Formatters` package + `EnableUnsafeBinaryFormatterSerialization` | `ObjectCloner.SerializingCloner` (every worldgen settings clone) |
| Code pages via `I18N*` | `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` before game code | Ionic.Zip static constructors (IBM437) |
| `System.CodeDom` | `System.CodeDom` package (binding only; never executed) | Ionic.Zip SFX path (dead) |
| Settings classes (`System.Configuration.ApplicationSettingsBase`, in Mono's `System.dll`) | `System.Configuration.ConfigurationManager` package | mods' generated `Properties.Settings` classes (a mod whose types fail to load is dropped by KMod) |
| Reflection writes to `static readonly` fields (and reads that observe them) | Static fields of assemblies from `Managed/` and of mods' assemblies (`ModAccess`; the game's `LocString.CreateLocStringKeys` writes back mods' strings classes) are loaded with `InitOnly` cleared (Field-table flags only; IL untouched; such images load from memory; their `Assembly.Location` still reports the file, as `ModAccess` does for mods, since mods hash it: System Performance Observer) | `Localization.OverloadStrings` (translations of `STRINGS.UI.PERMIT_RARITY`), `LocString.CreateLocStringKeys` |
| Assemblies preloaded by the player | Host eagerly loads every assembly in `ScriptingAssemblies.json` that we serve, before any game type initializes | `App..cctor` / `KSerialization.Manager` assembly snapshots |

**Steamworks.NET.** One explicit-layout union in `com.rlabrecque.steamworks.net` 20.1.0
(`SteamDatagramRelayAuthTicket+ExtraField+OptionValue`, `byte[]` overlapping `long`) is
accepted by Mono and rejected by CoreCLR's type loader; the game's whole-domain
`GetTypes()` scans then crash. This is a third-party library that does not support our runtime,
so we port it: build Steamworks.NET **20.1.0 from upstream source** (MIT) under the same
assembly name and version, with that one union changed to a CoreCLR-valid layout. The port
lives in `src/ThirdParty/Steamworks.NET/` with the upstream commit pinned and the diff
documented. The host serves it instead of the shipped copy.

**Steam itself** is required, as for the original game: `SteamAPI.Init()` failure makes
`SteamManager` call `Application.Quit(2)`, and our `Application.Quit` honours it. Running
past the boot checkpoint needs a Steam client logged in with an account that owns the game.

**Documented deviations**: where Mono and CoreCLR, or Unity's player and ours, differ in ways
game or mod code can notice. Each says how it is resolved, or why it is kept.

- **D1 State-machine parameters in saves.** `StateMachineSerializer` stores and matches
  parameters by their context type's `Type.FullName`, whose generic arguments are
  assembly-qualified (`mscorlib` on Mono, `System.Private.CoreLib` here; Unity modules versus the
  engine assembly). Unpatched, no parameter of a save from the original matched and all loaded
  at their defaults: a large colony (DeCrew, cycle 17628) lost all fifteen Duplicants within
  four cycles, its food and suit docks unreachable, where the original keeps it alive.
  **Resolved by a patch:** the host (`StateMachineSaveNames`) has the serializer use the names
  the original player writes (`OniMoono.Engine.MonoTypeNames`, each type argument's assembly
  taken from the game's own Mono class libraries in `Managed/` or the engine facades), so saves
  load fully in both players in both directions. Saves written by this player before the patch
  carry CoreCLR names; their state-machine parameters load at defaults once. Everything else in
  saves (KSerialization body, Sim blob, header) is runtime-independent.
- **D2 Number formatting.** `double`/`float.ToString()` without a format prints the shortest
  round-trippable string on .NET (Mono printed 15 significant digits), and a negative value
  that rounds to zero keeps its sign ("-0.0" where Mono printed "0.0") — cosmetic, in game
  code's own formatting. Fixed-decimal formats round differently at a half: Mono rounds a
  float's 7-significant-digit form, .NET its exact value, so `67.255f.ToString("F2")` is "67.26"
  on Mono and "67.25" here (`0.005f`: "0.01" and "0.00"). Kept: .NET's is the exact rounding of
  the value the game holds. The engine's own `ToString` methods (vectors, colours, matrices, rects)
  format as Unity's Mono player does.
- **D3 `string.GetHashCode`** is randomized per process (Mono's is stable). Its only persisted
  use is the version stamp of local and dev (unpackaged) mods in `mods.json`
  (`KMod.Local.Subscribe`: `directoryName.GetHashCode()`). Left alone, the stamp differs at
  every launch, so `KMod.Manager` treats the mod as changed, asks for a restart and disables
  it. **Resolved by a patch:** the host (`ModVersionStamp`) uses the game's Harmony to replace
  that one call with Mono's algorithm (`OniMoono.Engine.MonoStringHash`, the .NET Framework
  64-bit hash, checked against the original player with tools/UnityProbe). Stamps are then
  stable and equal to the original player's, so `mods.json` works in both players. Game code's
  other `GetHashCode` calls stay randomized, and none of them is persisted. A `mods.json`
  written before the patch carries random stamps: each local mod asks for a restart once and has
  to be re-enabled once.
- **D4 `Thread.Abort`** throws on CoreCLR. It is reached during play too, not only at shutdown
  as first thought: D47 has how the host aborts as Mono does.
- **D5 `Process.MainModule`** is the host executable; `App.Restart` relaunches the host (we make
  the host the executable that is relaunched, so this works as intended).
- **D6 Runtime tight sprite meshes.** `Sprite.Create(…, SpriteMeshType.Tight)` traces alpha
  outlines in Unity with an unpublished algorithm; the engine creates the full-rect mesh
  instead. Rendered pixels are identical (the omitted area is fully transparent); only
  `sprite.vertices`/`triangles` differ. Sprites loaded from data keep their baked meshes.
- **D7 `GarbageCollector.GCMode`.** CoreCLR's collector cannot be switched off; Disabled and
  Manual hold a no-GC region (`GC.TryStartNoGCRegion`, budget a quarter of available memory),
  re-entered each frame if the runtime had to end it. Entering may collect once, and an
  exhausted budget collects where Unity's heap would grow. Only the game's scripted-profiling
  path changes the mode.
- **D8 TextMesh scale.** Legacy `TextMesh` geometry comes from the same legacy text generator
  as uGUI `Text`, at 0.1 world units per font pixel times `characterSize`. Unity's exact native
  scale and line-box metrics for TextMesh are unpublished; only TMP's example scripts create
  TextMeshes, so no game screen depends on them.
- **D9 UnloadUnusedAssets and ScriptableObject callbacks.** Unloading probes reachability with
  the runtime's collector (the object table holds candidates weakly for one collection), so a
  reclaimed object cannot run code. ScriptableObjects whose type declares `OnDisable` or
  `OnDestroy` (Unity calls both when it unloads one) are therefore never unloaded. No
  ScriptableObject in the game declares either.
- **D10 JsonUtility digits.** Unity's JSON writer is RapidJSON 1.1.0, whose Grisu2 rounding of
  the last digits of long outputs indexes past a ten-entry table and multiplies by whatever
  bytes follow it in `UnityPlayer.so` (identified by matching 35 observed outputs). The engine
  implements the corrected algorithm, so for such values its last digit can be smaller than
  Unity's (0.2f: …224 here, …225 in Unity); both parse back to the same number. The game writes
  JSON with `JsonUtility` only for its scripted performance capture.
- **D11 Mods built against publicized game assemblies.** Many mods are compiled against copies
  of the game's assemblies with every member made public, and use private members directly. Mono
  does not check member access at run time; CoreCLR throws FieldAccessException or
  MethodAccessException when such a method is compiled. **Resolved by a patch** (`ModAccess`):
  the host replaces the `Assembly.LoadFrom` call in `KMod.DLLLoader.LoadDLLs`, and a mod
  assembly that names a game member or type its code could not reach (private, internal) is
  given CoreCLR's `IgnoresAccessChecksTo` attribute for `Assembly-CSharp`,
  `Assembly-CSharp-firstpass`, the game's `0Harmony` (Mono.Cecil; the Multiplayer Mod reads
  Harmony's private fields) and `Unity.TextMeshPro` (Text Input Hotkeys calls
  `TMP_InputField.Backspace()`) and loaded from memory, its symbols with it. The
  game's own assemblies are not changed (making their members public instead would change what
  KSerialization saves: its default opt-out mode saves every public field and property). The mod
  still sees itself as loaded from its file: `Assembly.Location` reports the file (mods find
  their folders from it) and its folder is searched for its references. Other mods load as
  before. A protected member counts as reachable only when one of the mod's classes derives
  from its class, as CoreCLR's check has it: Public Cots and Public Luxury Beds read the
  protected `StateMachine.GenericInstance.smi` from a postfix of `SleepChoreMonitor`'s
  `UpdateBed`, which CoreCLR refused (MethodAccessException, the crash screen as the save
  loaded) while the original runs it. `--check-types` loads a mod calling the protected
  `KMonoBehaviour.OnSpawn` from a class of its own (loaded with access checks off, and run) and
  one calling it from a subclass (loaded as it is).
- **D12 `callvirt` to a static method.** Mono compiles one as a call; CoreCLR's JIT rejects the
  method ("Method not found: '?'"). C# compilers never emit it, but Harmony transpilers that
  swap an instance call for a static helper and keep the opcode do (Mod Updater's patch of
  `KMod.Manager.Subscribe`). **Resolved by a patch** of the game's Harmony (`TranspiledCalls`):
  the instructions its transpilers produce (`CodeTranspiler.GetResult`) have such calls made
  calls, as Mono would run them, and each is logged.
- **D13 Inlined patches of value types' virtual methods and of shared generic code.** Before
  detouring a method, Harmony's MonoMod marks its MethodDesc not to be inlined, so code compiled
  later calls the patched method. For a value type's override (`GetHashCode`, `Equals`,
  `ToString`), the handle reflection gives is the method's unboxing stub; the JIT inlines the
  unboxed MethodDesc after it, which stays unmarked, so code tiered up after the patch ran the
  original (Fast Track's `AxialI.GetHashCode`: dictionary lookups missed,
  `ClusterGrid.RegisterEntity` threw on every starmap entity). Likewise for a generic method
  instantiated over a reference type: the handle is the instantiating stub, and both the code
  callers run and what the JIT inlines are the shared MethodDesc's (`<__Canon>`) the stub wraps.
  That shared code is also tiered up on its own: when the JIT recompiles a detoured method,
  MonoMod moves the detour onto the new code, finding it by the method compiled
  (`PlatformTripleDetourFactory.Detour.OnMethodCompiled`), and the shared MethodDesc is not the
  instantiation it detoured, so the recompiled code ran unpatched. Fast Track replaces the body
  of `InterfaceTool.GetObjectUnderCursor<KSelectable>` with `return null` and calls it from its
  `SelectTool.OnLeftClickDown` prefix, falling back to its own cycling; about 40 clicks into a
  session the shared code tiered up and the original body ran, which, given no previous
  selection, returns the first object under the cursor, so clicks on a stack stopped cycling
  (issue #48). Mono has no unboxing stubs or instantiating stubs, and inlines no method that
  large. **Resolved by patches** of MonoMod's `PlatformTriple.TryDisableInlining` and
  `OnMethodCompiled` (`UnboxedMethods`): the unboxed or the shared MethodDesc is marked too
  (CoreCLR 10's layouts), and a compile of shared code is reported again for each patched
  instantiation it is the code of, so their detours follow it. The host's `--check-types` patches
  an emitted value type's `GetHashCode` and fails if dictionary lookups miss after tier-up, and
  prefixes an emitted generic method over a class and fails if code compiled after the patch, or
  calls after its shared code tiered up, skip the prefix.
- **D14 Mono's enum formatting.** Mods that add elements name their `SimHashes` values by
  patching enum formatting: Mono's private `Enum.InternalFormat(RuntimeType eT, Object value)`, or
  `Enum.ToString()`. In Mono's `system/enum.cs`, `ToString()` is
  `InternalFormat(GetType(), GetValue())` (the enum's type and underlying value);
  `ToString(format)` with "G" or none, the obsolete overloads and the IFormattable route
  `string.Format` takes all call the virtual `ToString()`; `Format(type, value, "G")` calls
  `InternalFormat`. CoreCLR has no InternalFormat, and its `ToString(format)` and span formatting
  do not go through `ToString()` (Rocketry Expanded's ElementUtil: the patch failed, element names
  came out as numbers, lookups by name failed). **Resolved by patches** of `AccessTools.Method`
  and `AccessTools.DeclaredMethod` (the latter is how `[HarmonyPatch]` attributes resolve) and of
  Harmony's `PatchProcessor.Patch` (`MonoEnumFormat`): looked up on `Enum`, the name gives a
  stand-in with Mono's signature, and once a mod patches it or `Enum.ToString()` the formatting is
  routed as Mono routes it; without such a mod nothing is patched. `--check-types` patches both
  as mods do and checks each path.
- **D15 A second version of a loaded assembly.** For an assembly without a strong name, Mono
  answers a load of another file with the assembly already loaded under that simple name,
  whatever the versions (`mono_domain_assembly_search` compares names ignoring version, key and
  case). CoreCLR refuses the second version, and KMod then drops the whole mod (a mod shipping its
  own copy of a library another mod loaded first). Mono's binding of a reference to such an
  assembly searches the same way, so a mod built against PLib 4.22 runs with an older PLib another
  mod loaded; CoreCLR fails the reference ("manifest definition does not match"). **Resolved in**
  `ModAccess`: such a file is given the loaded assembly (`LoadFrom`), and a reference CoreCLR cannot
  bind is given the assembly loaded under its simple name (the default context's Resolving event);
  each substitution is logged. `--check-types` loads two versions of an emitted assembly and binds a
  third that references the second. Strong-named assemblies and the rest of the binding order:
  D45.
- **D16 Exceptions nothing catches on other threads.** They end a CoreCLR process; Unity's
  player keeps running (measured with PerfProbe in the original: a thread's exception is logged,
  by UnityEngine's AppDomain.UnhandledException handler calling Debug.LogException; a thread-pool
  item's and an async void method's are dropped silently, with no UnhandledException; Mono passes
  IsTerminating true). Sgt_Imalas' UtilLibs checks for updates from an async void method that
  throws when offline. **Resolved by** .NET 10's `ExceptionHandling.SetUnhandledExceptionHandler`
  (`UnhandledExceptions`): a thread's exception raises UnhandledException (and is logged as
  Unity's are), a thread pool's is dropped, the main thread's still ends the process;
  `--check-types` throws on both kinds of thread.
- **D17 A try block entered with values on the stack.** ECMA-335 requires an empty evaluation
  stack where a try block starts; CoreCLR's JIT refuses a method that enters one with values on
  it (InvalidProgramException when Harmony applies the patch), and Mono compiles it. Harmony
  transpilers that insert a call returning a value nobody uses produce such methods (Better Info
  Cards' patch of `SelectToolHoverTextCard.UpdateHoverElements` leaves a `KSelectable` behind
  before a `foreach`; the mod failed to load). **Resolved by a patch** of the game's Harmony
  (`TranspiledTryEntries`, beside D12's): in the instructions its transpilers produce, values a
  try block is entered with and never consumes are popped before it. They are dead, since `leave`
  and `throw`, the only ways out of a try, empty the stack. A method whose try block does use them,
  or that the analysis cannot follow, is left as it was, and each change is logged.
  `--check-types` transpiles a method so and runs it.
- **D18 Patches of a generic class's shared code.** A method of a generic class instantiated
  over reference types only runs code that every such instantiation shares, on Mono as on
  CoreCLR (with a value type argument too, on CoreCLR only: D38), so
  a patch of one instantiation's method applies to all (Better Info Cards patches
  `HoverTextDrawer.Pool<MonoBehaviour>.Draw`, which the game never instantiates, to see the
  widgets its `Pool<Image>` and `Pool<LocText>` draw). When tiered compilation recompiles that
  shared code, MonoMod moves its detour to the new code only for a recompiled method equal to the
  patched one; the shared code's is the canonical method (`Pool<__Canon>.Draw`), so the patch
  stopped running once the method tiered up (Better Info Cards: NullReferenceException every
  frame after a few seconds of hovering). **Resolved** in `SharedGenerics`, which subscribes to
  MonoMod's method-compiled event and hands MonoMod's handler every patched method of a generic
  class that has the recompiled method's handle (instantiations sharing code share their method
  handle). Code that inlined such shared code before the patch is not rebuilt (D34).
  With several instantiations patched, each detour handed over points the shared code at its own
  replacement, so the last one wins. On Mono, which never recompiles, that is the replacement
  Harmony detoured last. MonoMod's table enumerates in hash-code order, which changes from run to
  run, so once the code tiered up a random one won. Fast Track patches
  `Components.Cmps<T>.GetWorldItems` for each of the game's ~100 lists. When the replacement for
  `Cmps<GameObject>` won, its `(item as KMonoBehaviour)` on an item typed as the sealed
  GameObject compiled to null on CoreCLR (the JIT folds a cast from a sealed class to an unrelated
  one), and every list threw NullReferenceException from about 10 s after a load (IdleTracker,
  PlanScreen, MeterScreen; the crash screen opened). The original keeps Fast Track's last,
  `Cmps<MinnowImperativePOIStates.Instance>`, for good. **Resolved**: `SharedGenerics` numbers
  Harmony's detours (a postfix of `PatchTools.DetourMethod`) and hands them over in that order,
  the latest last. Not covered: when the replacement detoured last is itself typed over a sealed
  class unrelated to what other instantiations hold, CoreCLR still folds its casts where Mono
  checks them at run time; no mod is known to do so. `--check-types` patches an emitted generic
  class's method for one argument and calls it for another past tier-up. A second check patches
  seven instantiations, the sealed `string` first, and fails unless every call past tier-up
  runs the one patched last.
- **D19 Patch methods' frames.** Harmony calls a mod's prefixes, postfixes and finalizers from
  the replacement it builds for the patched method, a dynamic method owned by 0Harmony. CoreCLR's
  JIT inlines a small patch method into it, so what the patch method calls sees 0Harmony as its
  caller (`Assembly.GetCallingAssembly()`): Traffic Visualizer's SquareLib looks for its icons in
  0Harmony's folder, and the game stopped loading. On Mono the patch method keeps its frame (the
  mod works in the original player). **Resolved in** `PatchFrames`: before Harmony builds a
  replacement, each patch method of a mod is marked not to be inlined (MonoMod's
  `TryDisableInlining`, which Harmony applies to the patched method itself); this project's own
  patches are left as they were. `--check-types` calls `GetCallingAssembly` from a helper an
  emitted mod's postfix calls. The replacement's own frame goes too when its last call is a patch
  method: a postfix of a method returning nothing is called right before the replacement's `ret`,
  and the JIT (replacements are compiled fully optimized) makes that call a tail call. Mono makes
  no implicit tail calls. Cold Wheat Seed Drop's postfix of `SeedProducer.CropPicked` names the
  type `Worker`, gone from the game, and throws when a crop is picked in both players (D40); the
  original's trace starts with the replacement (`CropPicked_Patch1`, read as
  `SeedProducer.CropPicked`), ours with the game's lambda that called it, a log signature the
  original lacks. `PatchFrames` therefore follows each call of a mod's patch method that the
  replacement's `ret` follows (past `nop`s) with `ldc.i4.0; pop` (in `FaultBlockRewriter.Rewrite`,
  the last step before Harmony emits the replacement), which the JIT compiles to nothing and
  which takes the call out of tail position. Calls elsewhere keep their tail calls.
  Stack indices inside a patch depend on it: No Minimum Temperature's only postfix of the
  `ElementConverter.OutputElement` constructor reads `new StackTrace(2).GetFrame(0)` for the
  building config that made the element, and found `BuildingConfigManager.RegisterBuilding` there
  before the fix (no minimum temperature reverted; the original reverts 26, as ours does now).
  UnityProbe's `stackdepth` scenario, a second postfix of that constructor, lists the same frames
  in both players: the postfix, the replacement (`ElementConverter+OutputElement..ctor_PatchN`,
  declared by `DynamicMethodDefinition`, D23), the config's `ConfigureBuildingTemplate`, then
  `RegisterBuilding`.
  `--check-types` patches a method returning nothing with a mod's postfix that throws, and finds
  the replacement's frame under the postfix's; and patches an emitted struct's constructor with a
  postfix that reads `new StackTrace(2)`, called from a `ConfigureBuildingTemplate`.
- **D20 Patching a P/Invoke whose export is missing.** MonoMod prepares a method before
  detouring it; for a P/Invoke, CoreCLR then binds the native export, and a missing one fails the
  patch. Mono binds a P/Invoke at its first call, so mods patch one the platform lacks to keep it
  from being called: Debug Buttons and ONI Together skip `KImGuiUtil.SetKAssertCB`, which the
  game's Linux cimgui does not export, and load in the original player. **Resolved in**
  `PInvokePatches`: a missing export or library does not fail the preparation of a P/Invoke; the
  detour goes on the method's own entry, other P/Invokes bind as before, and calling the original
  still fails as on Mono. CoreCLR's JIT also inlines the transition of a P/Invoke that needs no
  marshalling into optimized callers (all game and mod code, even at tier 0; not a debug build's
  own code), which then call the native target directly, never the detour: the prefix was
  skipped, and a missing export threw. `PInvokePatches` marks a P/Invoke prepared for a patch as
  needing marshalling (CoreCLR 10's cached flag on its MethodDesc), so code compiled from then
  on, including a tier-up of a caller compiled before, calls its entry. A caller's code compiled
  before the patch still calls past it until it is recompiled. `--check-types` patches a P/Invoke
  to a missing export and one to an export libc has, and calls each from a dynamic method
  (compiled optimized in either build); `scripts/check.sh` runs it on the release build too.
- **D21 Patches of methods an earlier replacement calls or inlines.** Harmony compiles a patched
  method's replacement fully optimized, and CoreCLR's JIT inlines methods it calls, and, through
  them, the methods those call; a method patched later is marked not to be inlined from then on,
  but a replacement compiled before keeps its old body inlined and the later patch never runs
  there (True Tiles patches `BlockTileRenderer.GetConnectionBits`, then `MatchesDef`, which it
  calls: no tile connected to its neighbours; Forever Eruption Geyser patches
  `Geyser.GetEmitRate`, then `GetIterationPercent`, which it reaches through `GetOnDuration`: the
  emit rate kept the unpatched share). On Mono the later patch takes effect (both mods work in the
  original player). **Resolved in** `PatchDependents`: the host records the methods each
  replacement's instructions call (from `CodeTranspiler.GetResult`) and reads, once each, the IL of
  every candidate for inlining reachable from them through candidates: a call graph. A candidate
  has IL (not abstract, a P/Invoke or an internal call), is not marked NoInlining or synchronized,
  is not patched yet, and has at most 1024 bytes of IL or is marked AggressiveInlining; a virtual
  one counts, as the JIT inlines one it devirtualizes. 1024 is the JIT's bound
  (`JitExtDefaultPolicyMaxILProf`, its bound with profile data, not the 128 without, and
  measured: a replacement inlines 1024 bytes of straight-line IL two calls down, never 1026). The
  first time a candidate (or a P/Invoke, whose call is inlined too) is patched, the host walks the
  graph back from it through unpatched candidates, 20 calls deep (the JIT's inlining depth), and
  rebuilds the replacements already made that call any method on the way, as Harmony rebuilds one
  when a patch is added. The rule over-approximates (it ignores the JIT's budget and call-site
  heuristics), which costs only time at mod load; replacements run as compiled. Eight
  Harmony-heavy mods together (Fast Save, Ronivans Legacy, Research Manager, Popup Control,
  Duplicant Stat Selector, Sweep By Type, Better Automation Overlay, Favorite Resources) read
  43,600 bodies in 0.6–0.75 s and rebuild 230 times (126 replacements) in 1.2 s; Forever Eruption
  Geyser alone rebuilds 18 times. The graph misses an override the JIT reaches by devirtualizing
  a call to a base method (the call names the base method, whose body is followed instead);
  D34's record of the JIT's own decisions covers it.
  `--check-types` patches a caller, then a method it calls, and calls the caller; and patches A,
  then C, which A reaches through B, with C of 4, 1024 and 1026 bytes of IL, and calls A.
- **D22 Classes missing a method of an interface they list.** A mod built against an older game
  can have a class that no longer implements every method of a game interface (Wireless Power's
  side screen lacks the new `ISliderControl.GetSliderTooltip(int)`). Mono loads the class and fails
  only when it is checked or used through the interface (measured in the original player: "VTable
  setup of type … failed" from `IsAssignableFrom` with an interface; KMod's `GetTypes` and
  `UserMod2` check pass, and Wireless Power runs). CoreCLR refuses the class when it loads, so
  `GetTypes` throws and KMod drops the whole mod. **Resolved in** `MissingInterfaceMethods`:
  before `ModAccess` loads a mod's assembly, it is loaded once in a collectible context that binds
  only to assemblies already loaded, and that context is collected before the real load (until
  then `AppDomain.GetAssemblies` lists its copy, and PLib, which picks its instance from there,
  ran the copy, whose `Location` is empty: Threshold Walls). The methods CoreCLR reports missing
  get an explicit implementation that throws the `TypeLoadException` CoreCLR would have. The class
  loads, and calling the missing method fails. Where Mono fails earlier (an interface check of the class),
  ours goes on. Collecting a probe's context takes two blocking full collections (80 with 40
  mods), so each probe's verdict is kept in the cache folder (`oni-moono/mod-type-probes`) under
  a key naming its inputs: the image's hash, the runtime's and the host's builds, and each loaded
  assembly the image's references reach (outside the runtime's folder) by name, module version
  id and file size and time; a later start with the same mods probes nothing. `--check-types`
  loads a mod built against an older shape of an interface, whose new method takes one of the
  game's own types (Trashcans), and calls the method it lacks; it then completes the image again
  from the cache and checks the bytes and each kept verdict against a fresh probe.
- **D23 Stack frames of patched methods.** Harmony runs a patched method as a dynamic method that
  MonoMod creates with an owner type. On Mono, a frame of it reports a method declared by that
  owner, `MonoMod.Utils.DynamicMethodDefinition` (measured in the original player); CoreCLR's
  `StackFrame.GetMethod` returns the `DynamicMethod`, whose `DeclaringType` and `ReflectedType`
  are null. Customize Plants walks the stack for the class whose `CreatePrefab` made a plant, and
  a null there ended the game's asset setup when another mod had patched a `CreatePrefab` (the
  save never loaded). **Resolved in** `MonoStackFrames`: a mod's calls to `StackFrame.GetMethod`
  (found in its metadata) call the host's instead, which reports a dynamic method with an owner
  as declared by the owner. In a trace's text, Mono names a replacement's frame `(wrapper
  dynamic-method) MonoMod.Utils.DynamicMethodDefinition.T.M_PatchN` and CoreCLR `T.M_PatchN`;
  the test tools' log signatures (`qa-tools.py`) read either as `T.M`, the method patched, since
  the host patches some of Harmony's own methods (D14, D38) that the original runs unpatched:
  Printerceptor Reimagined's refused patches showed `PatchProcessor.Patch_Patch2` in ours alone.
  Writing the rewritten mod resolves its references against the
  assemblies already loaded (an enum default value's type: PLib's mods), and a mod that cannot be
  rewritten loads unchanged. `--check-types` walks the stack from a patched method of a mod
  whose method takes a game enum with a default value.
- **D24 Special folders that do not exist.** On Linux and macOS, Mono's
  `Environment.GetFolderPath` gives a special folder's path (its XDG user directory, or the home's
  `Desktop`, `Documents`…) whether or not it exists; CoreCLR checks unless asked not to and gives
  "" otherwise. Harmony's debug log creates the Desktop folder from that path, so a mod that turns
  the log on (Simple Cheat) failed to load on a machine without `~/Desktop`:
  `Directory.CreateDirectory("")` throws. **Resolved in** `MonoFolderPaths`: the default option
  is answered without the existence check, as Mono answers it; Windows is unchanged.
  `--check-types` asks for the Desktop of a home that has none.
- **D25 Float intermediate precision.** Unity starts Mono with `-O=-float32` (`UnityPlayer.so`;
  Mono's own default is float32 on): float arithmetic is evaluated in double and rounded to
  float32 only where the value is stored (a local, field, array element, parameter or return
  value) or cast. CoreCLR rounds every float operation to float32. So a float expression used
  without being stored first can differ in the last bit: `(int)(0.7f * 10f)` is 6 on Mono and 7
  here, `Math.Round(0.9f * 255)` 229 and 230. In the game this shows as rare off-by-one results
  where a product lands on an integer, such as the germs a ladder passes on (`(int)(count *
  0.005f)`, one fewer on Mono at multiples of 200), and in some UI roundings at a half. Kept in
  play: .NET's results are those the code as written asks for, and emulating Mono everywhere
  (widening every float load and call result in all game and mod IL) would break mods' transpilers
  that match IL patterns. Measured by `tools/UnityProbe` (FloatPrecision.cs,
  `PROBE_SCENARIO=floatprec`); test tools that compare players compute in double, where both
  runtimes agree. **Resolved for worldgen** by the `exact-worldgen` game tweak (on by default;
  `MonoFloatWidening`, in D29's load-time pass): a seed's map depends on the last bit of millions
  of float operations (noise, Poisson-disk sites, the Voronoi relaxation, border widths), so the
  same coordinate drew slightly different biome borders than the original (issue #66). In
  worldgen's code alone (LibNoiseDotNet; firstpass's `ProcGen`, `Delaunay`, `VoronoiTree`,
  `Geometry` and `LibNoiseDotNet` namespaces and the global helpers they call; Assembly-CSharp's
  `ProcGenGame`) every float32 load, constant and call result is followed by `conv.r8`, so the
  arithmetic runs in double and is rounded back where the value is stored, as on Mono. Worldgen
  runs before a game starts, so the doubles cost nothing in play, and worldgen takes no longer (a
  classic-style cluster of eight worlds: 10.5 and 13.0 s with it, 10.6 and 13.7 s without, on
  Linux). Of the mods in the burndown, only Heliconia transpiles a widened method
  (`TerrainCell.ApplyBackground`): it swaps a load of `Element.lowTemp` for a call, which the
  widening's `conv.r8` then follows, so its patch still applies. Checked by the `exact-worldgen`
  spec (a classic-style cluster's eight worlds against the original's) and, while it was built, on
  five coordinates across the base game, classic-style and Spaced Out clusters and Ceres: every
  world's overworld polygons and its cells as they go into the settling simulation are the
  original's bit for bit (without the tweak, nearly every polygon and some 16,000 cells of the
  start world differ). The settling itself does not repeat from run to run in either player (a few
  liquid cells, at times a pocket of frozen gas, differ between two runs of one player), so the
  settled cells are not compared.
  The engine's own versions of Unity's managed math follow Mono where it was measured. `Vector2`,
  `Vector3` and `Mathf` members that round more than once (`magnitude`, `sqrMagnitude`,
  `Distance`, `Dot`, `Lerp`, `LerpUnclamped`, `MoveTowards`, `ClampMagnitude`, `Reflect`, `Cross`,
  `Project`, `Angle`, `InverseLerp` and `==`) are evaluated in double and rounded where Unity's
  would store a value, bit for bit with the original on 2,000 cases each
  (`PROBE_SCENARIO=vecmath`, `vm-check.py`), at no measurable cost. `Matrix4x4`'s products
  (`MultiplyPoint3x4`, `MultiplyPoint`, `MultiplyVector`, matrix × vector and matrix × matrix) sum
  each element in double and round once, as in the original bit for bit (`PROBE_SCENARIO=matmul`,
  `mm-check.py`; ScrollRect's change detection depends on it, `docs/cleanroom/ugui.md`
  "ScrollRect: bounds to the bit"). `Matrix4x4.inverse` is native float code in the original,
  Mesa's Gauss–Jordan inversion; the engine ports it (`MesaInverse`, MIT), bit for bit with the
  original. So are the projection builders (`PROBE_SCENARIO=projection`, `pj-check.py`): `Ortho`
  and `Frustum` are Mesa's (`MesaFrustum`, MIT), `LookAt` is `gluLookAt`'s basis arithmetic in
  Unity's layout (`GluLookAt`, SGI Free Software License B), and `Perspective` and
  `Quaternion.AngleAxis` are float code with the C library's sinf and cosf, which MathF calls too;
  the engine follows all five bit for bit. Neither Mesa's nor GLU's, but measured and followed bit
  for bit too: `Inverse3DAffine` (native float cofactors over a SIMD-shaped determinant), `Rotate`
  (managed: its diagonal is evaluated in double under the Mono, as above, where `TRS` is native
  float), and the camera's rects, `worldToCameraMatrix` and screen conversions (float P·V, a
  whole-pixel viewport, an unprojection at depth 0.95), all but the perspective rays' direction
  (`docs/cleanroom/ugui.md`, "Camera and projection math").
  The transform hierarchy's world matrix, rotation and lossy scale are native float code in the
  original, composed from the leaf up; the engine follows it bit for bit (`HierarchyMath`,
  `docs/cleanroom/ugui.md` "Transform hierarchy to the bit").
- **D26 Hashes of structs without a GetHashCode.** A struct that does not override GetHashCode
  is hashed by `ValueType.GetHashCode`. Mono XORs every field into the hash
  (`ves_icall_System_ValueType_InternalGetHashCode`: an `int` field's value, a string field's
  `mono_string_hash_internal`, any other field's own `GetHashCode`), starting from the address
  of a runtime structure. CoreCLR uses the first field alone when the struct holds floats or
  references, so values that differ only in a later field collide. That is only slower in a
  dictionary, but `BubbleManager.Archetype.GetId` uses the hash as an identity: bubbles are
  filed under the hash of (velocity, element, fade speed), so every bubble rising straight up
  had one id, and the first element registered under it was the one every later bubble released
  (after an Oxy Coral's oxygen bubble, a Thermal Gas Fissure gave oxygen instead of natural
  gas). **Resolved by a patch** (`MonoValueTypeHashes`): that call computes Mono's hash
  (`OniMoono.Engine.MonoValueTypeHash`, starting from 0, so ids equal Mono's up to its
  per-process start).
  **Scanned** (2026-10-23, Mono.Cecil over IL: `constrained.` or `box` before
  `object::GetHashCode`, `EqualityComparer<T>.GetHashCode` and `HashCode` over such a struct;
  a struct as a dictionary key only hashes to a bucket and is left out): every game assembly
  in `Managed/` that is not Unity's or the BCL (Assembly-CSharp, Assembly-CSharp-firstpass,
  ImGui, ImGui.NET, FMODUnity*, Harmony, Newtonsoft.Json, VYaml, Ionic.Zip, LibNoiseDotNet,
  ArabicSupport, Steamworks.NET), and of mods only the Workshop builds in the catalog's
  `out/ws` (archived versions included), 1229 assemblies in all. Mods outside that set are
  not covered. Hits besides `GetId`, neither a collision under CoreCLR: Klei's ImGui object
  drawer (`MemberDrawerCollection.DrawFor`) puts the hash of `MemberDrawContext` (two bools)
  into an ImGui ID, an identity, but CoreCLR hashes a struct of plain values by all its bytes,
  so it tells the four values apart (Mono's XOR does not: (true, false) and (false, true)
  collide there); Antigravity Multiplayer (3635632392) sends the hash of `Random.state` (four
  ints, hashed by all bytes as well) to clients as a random seed, a value, not an identity.
  `--check-types` compares two elements' archetype ids. `tests/e2e/thermal-gas-fissure.yaml`
  checks the fissure's gas.
- **D27 Order of string-keyed concurrent dictionaries.** A `ConcurrentDictionary` enumerates its
  buckets in order, a bucket's newest entry first, in Mono's class library (`Managed/mscorlib.dll`)
  as in .NET's, so the order follows the keys' hashes and the table's size, and both differ for
  string keys: Mono's table has as many buckets as the capacity asked for (at least the
  concurrency level) and hashes with Mono's string hash; .NET rounds the capacity up to a prime
  (32 gives 37) and hashes with its own. PLib's component registry (`PRegistryComponent`, a
  `ConcurrentDictionary<string, …>(2, 32)`) initializes each component while it enumerates the
  registry, and PLib 4.16's `PBuildingManager` registers `PPatchManager` from its `Initialize`:
  Mono's order reaches that key afterwards, ours had passed it, so PLib's stages from
  `Db.Initialize` on never ran (Thermo Sensor Tile's techs were never registered; the original
  logs "Register techs"). **Resolved in** `MonoConcurrentDictionaries`: a mod's
  `new ConcurrentDictionary<string, T>(concurrencyLevel, capacity)` (found in its metadata) calls
  the host's instead, which gives the dictionary a comparer whose hash is the key's bucket in
  Mono's table of that size modulo .NET's table size (the rest of Mono's hash above it), so it
  enumerates in Mono's order until Mono's table would grow (a lock holding more than capacity /
  concurrency level keys). The constructors without a size are left alone: Mono gives them one
  lock per processor and grows their table as soon as a lock holds more than 31 / processors keys,
  so their order in the original depends on the machine. `--check-types` enumerates such a
  dictionary of a mod against Mono's order and adds a key during the enumeration as PLib does.
- **D28 Stack overflows.** Mono turns a stack overflow into a `StackOverflowException` that the
  code below it can catch. Measured in the original player: a test mod whose patch of
  `Telepad.OnSpawn` recursed without end had the game log "Error in
  HeadquartersComplete.Telepad.OnSpawn … System.StackOverflowException: The requested operation
  caused a stack overflow." (`KMonoBehaviour.Spawn` catches every exception), show its error
  dialog and play on. CoreCLR ends the process instead (exit code 134 on Linux, 0xC00000FD on
  Windows) and writes "Stack overflow." and the frames to stderr only, so the player's log just
  stops where the recursion began, as in issue #13's log. **Resolved in** `MonoStackOverflow`: a
  mod's methods on a call cycle among its own methods (from its IL: calls, generic
  instantiations, methods of its generic types, a virtual call reaching every virtual method of
  that name) start with a probe that throws `StackOverflowException`, with Mono's message, once
  `RuntimeHelpers.TryEnsureSufficientExecutionStack` fails; the probe is hidden from stack
  traces. That check wants 128 KB of stack left, so it always fails on a thread whose stack is
  128 KB, as the game's JobManager workers (and fast-jobs') are: issue #15's Stats Unlimited
  threw from its options getter on every worker where Mono ran it. On a stack of 256 KB or less
  (its bounds from `pthread_getattr_np` or `GetCurrentThreadStackLimits`) the probe throws only
  once less than half of it is left; 64 KB is enough to throw, and the handler runs at the
  catching frame. A recursion through game methods or another assembly is not covered, nor one in the
  game's own code. A mod that cannot be rewritten loads unchanged. `--check-types` runs a mod's
  self and mutual recursions without end, and a short recursion and one without end on a 128 KB
  thread.
- **D29 Float-to-integer conversions out of range.** Mono converts a float to an integer with
  the processor's truncating instruction (cvttsd2si), which gives the target's minimum for NaN,
  the infinities and values whose truncation is out of range; CoreCLR saturates since .NET 9
  (NaN gives 0, +∞ the maximum). Attribute Info Screen shows the bionic Duplicant's experience as
  `Mathf.RoundToInt(GetExperienceForNextLevel())`, which is NaN: "Xp:0/-2147483648" in the
  original, "Xp:0/0" here; `Grid.PosToCell` of a NaN position gave cell 0. Measured in the
  original player (`tools/UnityProbe`, `PROBE_SCENARIO=convert`, `cv-check.py rule`: 1596 of
  1596 lines follow these rules), every unchecked IL conversion of a float32 or float64, the
  float32 widened exactly: conv.i4 is the 32-bit conversion, conv.i1/i2/u1/u2 its low bits (300
  as a byte is 44, NaN 0); conv.i8 is the 64-bit conversion, conv.u4 its low 32 bits (-1 gives
  4294967295); conv.u8 and conv.u convert below 2^63 and otherwise convert the value less 2^63
  and flip the top bit (NaN, +∞ give 0, -∞ gives 2^63); conv.i is the 32-bit conversion
  zero-extended (Mono's amd64 back end; -1 gives 4294967295). Unity's `Mathf.RoundToInt`,
  `FloorToInt`, `CeilToInt` and `Vector2Int`/`Vector3Int`'s follow conv.i4. The checked
  conversions (conv.ovf.*), `Convert.ToXxx` and `Color32` from a `Color` agree in both players.
  **Resolved in** the engine's `Mathf` (`OniMoono.Engine.MonoConvert`: the processor's
  truncating instructions through `Sse2`, an equivalent comparison elsewhere) and in
  `MonoConversions`: as a game assembly from `Managed/` or a mod's assembly loads, each
  unchecked conversion whose operand is a float (found by following the IL's stack types along
  every path, generic instantiations included) calls a helper that converts as Mono does; a
  conversion reached with a float on one path and an integer on another, or a method whose IL
  is not followed, stays as it is. The game has 672 such conversions (Assembly-CSharp 501 in 272
  methods, firstpass 95, LibNoiseDotNet 69, Ionic.Zip 4, FMODUnity 1, 0Harmony 2 and 13 not
  followed), the catalog's 1137 mod assemblies 3502; checked against a local rule (a conversion
  right after a float load) with no disagreement. Costs measured: a conversion in a tight loop
  takes 0.75 ns instead of 0.52 (the float32 is widened first); four U57 runs of `qa.sh perf`
  alternating without and with the rewrite gave 32.9 and 43.8 fps without, 37.5 and 39.7 with
  (another session loaded the machine between runs; the spread between runs of one build is
  wider than any difference); rewriting the game's assemblies at load takes 2.4 s, so the
  images are kept in the player's cache folder (`oni-moono/conversions`, by the image's hash
  and the host build) and later starts read them. A Harmony transpiler sees the call where the
  IL had the conversion: no mod assembly in the catalog names an unchecked `OpCodes.Conv_*`
  field (`Conv_I4` and the rest), and none of our transpilers does. Not covered: our own UI packages and the
  engine's other code (compiled for .NET; `Mathf` aside), and an arm64 original (not measured;
  ours converts as the x86-64 one there too). `--check-types` loads a mod with conversions of
  floats, products, a generic result and a join of a float32 and a float64, and an integer's
  conversion, and compares them with the measured values.
- **D30 Order of directory listings.** On Linux and macOS, Unity's Mono hands out a folder's
  entries sorted by the bytes of their UTF-8 names (strcmp: Unicode code point order, so "B"
  before "_" before "a", "ﬀ" U+FB00 before "😀" U+1F600); CoreCLR hands them out in readdir order.
  Both class libraries enumerate with corefx's `FileSystemEnumerator` (Mono's
  `mcs/class/corlib/unix_build_corlib.dll.sources`), but Unity's `libmono-native` is built from
  its own `external/corefx-bugfix/src/Native/Unix/System.Native/pal_io.c`, whose
  `SystemNative_ReadDirR` reads the whole folder on the first call and `qsort`s it with strcmp
  (`CompareByName`); upstream corefx and .NET's `pal_io.c` do not sort, and the Linux and macOS
  `libmono-native` of the game import `qsort` and `strcmp`. A recursive listing walks breadth
  first in both (a queue of subfolders, each queued as its entry is read), so Mono's visits the
  subfolders in sorted order too. Measured in the original player (`tools/UnityProbe`,
  `PROBE_SCENARIO=dirorder`): every `Directory` and `DirectoryInfo` `Get*`/`Enumerate*` call,
  with and without patterns, recursive and with `EnumerationOptions`, sorted a folder created in
  another readdir order; an enumeration reads its folder on its first step (a file added after
  the call and before that step is listed, one added after it is not). On Windows both use
  `FindFirstFileEx` and the same corefx enumerator, so nothing differs there. True Tiles finds
  texture packs with `Directory.GetDirectories` and keeps packs of one Order in the order found:
  its settings list and the texture that won differed (True Tiles Material Pack's Aquatic,
  Prehistoric, Misc Tiles in the original; Misc Tiles first here). **Resolved in**
  `MonoDirectoryListing`, in `MonoConversions`' pass (D29) over the game's assemblies from
  `Managed/` (cached with it) and each mod's: every call of those methods (call, callvirt, and
  ldftn of `Directory`'s; a `DirectoryInfo` method's instance becomes the first parameter) calls
  the host's instead, which lists with .NET's own method (the same entries, paths and exceptions,
  thrown as eagerly) and reorders (`OniMoono.Engine.MonoDirectoryOrder`): a folder's results
  are read in full and sorted when first enumerated; a recursive one lists each folder alone (its
  `FileSystemEnumerable` reports the subfolders it would enter and does not), sorts its results,
  and queues its subfolders sorted, skipping one gone before its turn as .NET's walk does. A
  listing with CoreLib's own `MaxRecursionDepth` stays .NET's. Not covered: reflection calls,
  `FileSystemEnumerable` used directly, our own code, and a transpiler that looks for a listing
  call in game IL (no catalog mod names a listing method in a string). The game has 32 such
  calls (Assembly-CSharp 26, firstpass 2, Ionic.Zip 4). Cost: the sort, and on a recursive
  listing one list per folder: a folder of 200 files lists in 109 µs instead of 81 (GetFiles;
  EnumerateFiles 127 instead of 83), a tree of 21 folders and 600 files in 383 instead of 312.
  `--check-types` lists a mod's folders made in another
  order, recursively and through a delegate.
- **D31 MonoMod's helper file closed twice.** On Linux and macOS, Harmony's MonoMod writes its
  native exception helper to a temporary file on its first patch
  (`PosixNativeLibraryDrop.DropLibrary`: mkstemp, then, on CoreCLR,
  `new FileStream(fd, FileAccess.Write)`, which owns the descriptor and closes it when disposed,
  and then its own `close(fd)` once more). The second close fails harmlessly unless another
  thread opened a file in between and was given the same descriptor number: that file loses its
  descriptor. The host makes its first patches on another thread while the main thread opens the
  window, and a QA run's main thread failed to load Microsoft.Extensions.ObjectPool
  (FileNotFoundException in GLFW's type initializer, through BRUTAL's logging). Three threads
  opening and reading a file in a loop while the patches go in lost a descriptor (EBADF) in 15 of
  56 starts, each within 6 ms of the helper's last write; none did with the fix, in 40. Mono's
  own path closes the descriptor once, before reopening the file by name. **Resolved in** `MonoModHelperDrop`, in `MonoConversions`' pass (D29) over the game's
  0Harmony (cached with it) and any mod's copy of MonoMod: the FileStream is made with
  `ownsHandle: false`, so MonoMod's own close is the only one. `--check-types` drops the helper
  into a folder of its own and checks that no close failed.
- **D32 Nested types of the game a mod names that the game no longer has.** Mono resolves a
  TypeRef whose scope is another TypeRef in `mono_class_from_typeref_checked`
  (`mono/metadata/class.c`, `MONO_RESOLUTION_SCOPE_TYPEREF`; Unity's fork, branch
  `unity-6000.3-mbe`). When the enclosing class's nested types are loaded already
  (`nested_classes_inited`), it scans that list by name; finding none, its loop variable is left
  on the list's last class, and after the warning "TypeRef ResolutionScope not yet handled" it
  returns that class with no error. When they are not loaded yet, it scans the metadata, returns
  nothing, and the reference fails with a TypeLoadException, as on CoreCLR. The list is the
  NestedClass table's order (`mono_class_setup_nested_types`), and it is loaded only by
  enumerating the class's nested types (`Type.GetNestedTypes`/`GetNestedType`, a nested name
  parsed by `Type.GetType`, Mono's own lookup of an `A/B` name), so in general the result
  depends on what ran before the mod's code. The game's strings are the exception:
  `Global.Awake` calls `Localization.RegisterForTranslation(typeof(UI))` before it loads the
  mods, and its `LocString.CreateLocStringKeys` enumerates the nested types (public and not) of
  every class of the STRINGS namespace in Assembly-CSharp, recursively, in every language.
  U&F-Translate Fix names `STRINGS.ROOMS.CRITERIA.DECOR20`, gone from the game: the original logs
  the warning and binds it to CRITERIA's last nested class, POWERBUILDING (its 20-decor
  constraint reads "电力建筑"), and plays; CoreCLR threw, the mod's type initializer failed and
  the game quit while loading. Mosaic Tile, Marble Tile and Insulated Joint Plate name
  `TUNING.DUPLICANTSTATS.MOVEMENT`, whose class nothing has enumerated by then: they fail in the
  original too. **Resolved in**
  `MonoNestedTypeRefs`, before `MissingInterfaceMethods` as `ModAccess` loads a mod: each nested
  TypeRef of the mod into a class of that STRINGS tree (by the game file's metadata) whose name
  the class lacks is renamed to the class's last nested type in the NestedClass table, and
  references within it are looked up there, as Mono looks them up; a class with no nested types
  fails as on Mono. Every other missing nested type is left to fail, which is Mono's result
  unless something had enumerated its class first (not known when the mod loads; in the catalog's
  `out/ws`, `Artable+Stage`, `ElementLoader+ElementEntry`, `Navigator+PathProbeTask` and
  `DUPLICANTSTATS+MOVEMENT`, among others, are such references). Of the catalog's mods, 19
  current builds name a missing nested type of `STRINGS.ROOMS.CRITERIA` (most of them
  `REFRIGERATOR`, some `DECOR20` and `MESSTABLE`); all bind to POWERBUILDING, as on Mono. A mod that cannot be rewritten loads unchanged. `--check-types` loads a mod naming
  `CRITERIA.DECOR20` (bound to the last nested class, its `NAME` read), a type within it, a
  nested type CRITERIA has, and `DUPLICANTSTATS.MOVEMENT`.
- **D33 Patches of two copies of a library naming a generic type over their own types.** Harmony's
  MonoMod builds a patched method's replacement from the original's IL read with Cecil and turns
  each member it names back into reflection's (`ReflectionHelper._ResolveReflection`), through a
  cache keyed by the member's name and the assembly of its declaring type. For a member of a
  generic type, `IEnumerable<IOptionsEntry>.GetEnumerator`, that assembly is the generic
  type's (mscorlib): the type arguments' are left out, on both runtimes. Mods that merge their
  own copy of PLib have same-named types in different assemblies, and a replacement made from
  one copy was given members of another copy's instantiations, resolved before it. The cache
  holds what it resolves weakly: Mono's reflection objects stay alive in the domain's table, so
  there the second copy always gets the first's; CoreCLR's can be collected in between, so here
  it depends on whether a collection ran (a run of `plib-options-copies.yaml` logged the hits).
  Advanced Mopping by Type and Ultimate Power Tweaks each give their copy's
  `OptionsDialog.FillModOptions` a postfix, and a mod's options open through its own copy: the
  second replacement enumerates its own `ICollection<IOptionsEntry>` through the other copy's
  `IEnumerable<IOptionsEntry>`. A mod that patches the dialog of every loaded copy breaks every
  copy's but the first, as in issue #14 (Better Farming Effects and Tweaks', Mod Updater's and
  Advanced Batteries' dialogs, in `FillModOptions_Patch1`; the reporter's patching mod is not
  known). Mono finds an implementation anyway: for
  `IEnumerable<T>`, `ICollection<T>`, `IList<T>` and `IEnumerator<T>` (its "array special
  interfaces", `class-init.c`), `mono_class_interface_offset_with_variance` (`class.c`; Unity's
  fork, branch `unity-6000.3-mbe`) falls back to any instantiation of the same generic interface
  the object implements, so the call reaches `List<IOptionsEntry>`'s own enumerator and the
  dialog opens. CoreCLR's interface dispatch finds no implementation of the other instantiation
  and throws `EntryPointNotFoundException`. **Resolved in**
  `GenericMemberScopes`, a transpiler of `_ResolveReflection`: when a member's declaring type,
  or a type specification's element (an array of `List<IOptionsEntry>`), is built from a generic
  instantiation, the key also names the assembly and module of every type it is built from, so
  each copy's replacement gets its own types; other keys are left as they were. Where Mono's
  fallback ran the other copy's types (a `List<IOptionsEntry>` constructed in the replacement is
  of the other copy on Mono, and a generic interface outside those four fails there, "doesn't
  implement interface"), ours runs the replacement's own. `--check-types` patches two emitted
  copies' methods that enumerate a collection of their own interface, construct a list of it and
  name an array of such lists, and calls the second copy's; `tests/e2e/plib-options-copies.yaml`
  opens the two mods' options dialogs and two others'.
- **D34 Patches of methods code compiled before had inlined.** CoreCLR's JIT inlines small
  methods into the code it compiles at tier 1, and virtual ones it devirtualizes (by the
  receiver's exact type, or behind a check of the type tiered PGO saw). A patch marks its method
  not to be inlined from then on (D13), but code compiled before keeps the old body, so a patch
  made while the game runs never ran from there. A prefix on `Brain.UpdateBrain` added a minute
  into the early-base colony ran 0 times in a minute: the brain groups' loop, and
  `BrainScheduler.RenderEveryTick` around it, had tiered up with it inlined. The original ran it
  10,482 times, 3.00 per brain group update. Mono inlines only methods of under 20 bytes of IL
  without exception clauses, and a virtual one only if it is final
  (`mono_method_check_inlining`, `INLINE_LENGTH_LIMIT`; Unity's fork, branch
  `unity-6000.3-mbe`), so there a late patch reaches every call but those to the smallest
  methods. Mods rarely patch late: of the 2821 Workshop mods in onimaxxing's decompile cache,
  about 11 make a first patch after the game world exists, and the target of two of them is a
  small method a hot caller inlines (Resource Calculator patches
  `KInputController.UpdateScrollStates`, 44 bytes, which `KInputController.Update` calls every
  frame from boot, on the first `Game.OnSpawn`; Interstellar patches
  `DebugHandler.get_NotificationsDisabled` while it builds new worlds). PLib's stages that can
  run after a colony (`InMainMenu`, `OnStartGame`, `OnEndGame`) apply no patch in any of them.
  Test tools patch late as well (E2eDriver's `seed` and `count`, PerfProbe). **Resolved in**
  `PatchInliners` and `PatchDependents`: Harmony's MonoMod hands the JIT a wrapper of its
  `ICorJitInfo` (managed JIT hook), and the host replaces the wrapper's `reportInliningDecision`
  slot, so it records, for each method compiled, the methods its code inlined, from the JIT's own
  decisions (no IL is read). The first time a method is patched, the code recorded as inlining it
  is rebuilt: a Harmony replacement with its patches (as D21 rebuilds one), and a method of the
  game or a mod as a copy, a Harmony replacement without patches built from its own IL, which
  calls the patched method. The copy is marked not to be inlined, as a patched method is, and
  Harmony replaces it when the method itself is patched. A compile that was under way when a patch
  went in is rebuilt once it has finished. Not covered (logged): type initializers, shared generic
  code (its MethodBase is over `__Canon`), Harmony's and the host's own methods; ReadyToRun code,
  the BCL's precompiled methods, which inline one another without the JIT (a patch of a BCL
  method misses those calls at any time); and macOS, where MonoMod hooks the JIT natively and
  hands it no wrapper. Cost: none measured. The record is made at compile time only: U57 reached
  the main menu in 8.6 and 8.4 s with it, 8.5 and 8.3 s without, and four `qa.sh perf` runs
  alternating with and without gave 30.1 and 39.3 fps with it, 27.4 and 36.1 without (other
  sessions loaded the machine; the spread between pairs is wider than any difference). Nothing
  patches late there, so no copy is made. The late prefix on `Brain.UpdateBrain` found 3 of the
  9677 methods recorded in 1.5 ms. A copy runs as any Harmony replacement does (optimized at
  once, without tiered PGO), and only after a late patch of a method it inlined. `--check-types` runs two loops compiled optimized at once, one
  inlining a static method, the other a sealed class's override through a small method compiled
  only inlined, patches both methods, and checks that the prefixes run, and that a later patch of
  a loop replaces its copy; `tests/e2e/late-patch.yaml` counts the late prefix on
  `Brain.UpdateBrain` against the brain groups' updates.
  **Live frames.** A rebuild serves later calls only: a frame already running keeps the code it
  started in. Mods patch the game while the mod loader runs: each mod's `UserMod2.OnLoad` from
  `KMod.DLLLoader.LoadDLLs`, in `KMod.Manager.Load`'s loop over the mods, and `OnAllModsLoaded` in
  its loop after, all under `Global.Awake`. With the default tiering those frames run at tier 0,
  which inlines nothing. With tiering off (`DOTNET_TieredCompilation=0`), or once a loop goes to
  on-stack replacement (a thousand iterations by default: about a thousand mods in one
  `Manager.Load`, or `Global.Awake`'s loop over its forced atlases' sprites), they run optimized
  code compiled before any mod loaded. There `Manager.Load` had inlined `Mod.PostLoad` (37 bytes
  of IL), which PLib patches from Fast Track's `OnLoad`: the copy of `Manager.Load` was made
  ("KMod.Manager.Load copied"), but the running call kept the inlined body, so PLib's start-up hook
  and its later stages never ran, and Fast Track threw "At least one object must implement
  IComparable" (its `DescriptorSorter.Instance` null) and a NullReferenceException in
  `PropertyTextureUpdater`, found in issue #52's hunt with forced on-stack replacement. **Resolved
  in** `ModLoaderInlining`: before any game code is compiled, the host marks not to be inlined
  (MonoMod's `TryDisableInlining`, as for a patched method) the methods those frames call after a
  mod's code may have run: in `Manager.Load`, `Mod.IsEnabledForActiveDlc`, `Mod.Load`,
  `Mod.PostLoad`, `Mod.loaded_content`'s getter, `Mod.available_content`'s, `Mod.title`'s,
  `Mod.SetCrashed` and `Manager.Save`; in `Mod.Load`, `loaded_content`'s setter; in `DLLLoader`'s
  loops, `UserMod2.OnLoad` and `OnAllModsLoaded` (the base methods); and in `Global.Awake` after
  `Manager.Load(Content.DLL)`, `Manager.Load`, `KSerialization.Manager.Initialize`,
  `Global.InitializeGlobalInput`, `InitializeGlobalSound`, `InitializeGlobalAnimation`,
  `Localization.Initialize`, `KMod.Local`'s constructor, `Global.RestoreLegacyMetricsSetting`,
  `TestDataLocations`, `OnGetUserIdKey`, `KleiAccount.AuthenticateUser`,
  `KleiItems.LoadInventoryCache`, `WorldGen.LoadSettings` and
  `ListenForLoadSettingsErrorRoutine` (U59 744825 and U60 761654 alike). Each is called once per
  mod or once per start; the same methods run in the same order, as at tier 0. Not covered: the
  general-purpose methods those frames call too (`Debug`'s logging, the BCL's lists and
  enumerators, Harmony's own methods, `GlobalResources.Instance`, `DistributionPlatform`'s
  accessors), which stay inlinable for the rest of the game; `DLLLoader.LoadDLLs`'s lambda
  filtering the patched methods (called through a delegate); any other frame running when a
  patch goes in (a mod patching from a game callback, as D34's late patches above, reaches the
  frames after it but not the one under way); and mods' own frames. Mono inlines methods of
  under 20 bytes of IL, so in the original a patch made from `OnLoad` of the smallest of these
  (`Global.TestDataLocations`, 1 byte) misses `Global.Awake` too. Cost: none measured. Marking
  them takes about 2 ms, on the host patches' thread while the window opens; U59 with
  Fast Track and Better Info Cards reached the main menu in 17.6, 16.3, 16.9 and 17.5 s with
  the marks, 17.6, 18.1, 19.3 and 18.0 s without (second launches, alternated on a Linux lane;
  the spread is wider than any difference). `--check-types` checks that every one is found and marked, and runs a
  loop compiled optimized at once whose hook patches the small method it calls next: the
  prefix runs from the running loop on every later call when the method is marked, and on none
  when it is not; `tests/e2e/issue-52-fast-track-no-tiering.yaml` loads a colony with Fast Track
  with tiering off and checks that PLib's later stage made Fast Track's `DescriptorSorter`.
- **D35 Strings of fields and events.** Mono prints a field as its type's full name and its name
  (`RuntimeFieldInfo.ToString`, `String.Format("{0} {1}", FieldType, name)`), an event likewise
  (`RuntimeEventInfo.ToString`, `EventHandlerType + " " + Name`; Unity's fork, branch
  `unity-6000.3-mbe`). CoreCLR prints the type's short name when it is a primitive or a nested
  type, or an array or pointer of one (`Type.FormatTypeName`): `Int32 NO_LIGHT` where Mono prints
  `System.Int32 NO_LIGHT`. Harmony transpilers find the instructions they change by comparing
  `operand.ToString()` with Mono's string. Darkness Not Excluded Relit's patch of
  `Workable.GetEfficiencyMultiplier` (Lit Workspace) looks for `System.Int32 NO_LIGHT`: in the
  original it matches; here it matched nothing, the mod logged an error and the game quit at
  start-up. Methods, constructors, properties, parameters and locals print alike on both
  runtimes. Of the Workshop builds in `out/ws`, three hold such a string: that mod and both builds
  of Delivery Temperature Limit (`int` fields in its transpilers of fetch and status-item code).
  **Resolved in** `MonoMemberStrings`: prefixes on CoreLib's `RuntimeFieldInfo.ToString` and
  `RuntimeEventInfo.ToString` print them as Mono does, for the whole process as on Mono; an event
  without an add method still throws as CoreCLR's does. CoreCLR caches a field's string and
  Mono does not; neither does ours. `--check-types` prints fields of a primitive, a nested type,
  an array, a constant and an event of a nested delegate, and transpiles a method by the field's
  Mono string.
- **D36 Log size.** Unity's `Player.log` has no cap, and a mod throwing on every frame filled one
  player's log to 2 GB (issue #15). Kept on purpose: the engine's log (`CappedLogFile`) keeps its
  first 20 MB as written and, past them, only the last 20 MB or so, behind a banner of dashes
  saying how many bytes were cut there; the file stays under about 60 MB. `ONI_LOG_FULL=1`
  turns the cap off.
- **D37 Exception strings with inner exceptions.** Mono's `Exception.ToString` continues the
  outer message on its line: `Outer: message ---> Inner: message`, then the inner frames.
  .NET's starts ` ---> Inner: message` on a line of its own. A mod that logs
  `"…: " + exception` writes one line in the original's log where ours has two (Reversible
  Heater Clones logs so the two patches that fail in both players). Kept: it is log text, and
  the frames below it differ between the runtimes anyway (Mono's `[0x…] in <mvid>:0`). The
  test tools' log signatures (`qa-tools.py`) join such a line onto the one before it, so the
  two players' logs give the same signature; the load sweep had counted the second line as an
  error only ours logged.
- **D38 Patches of a generic class's code shared over a value type argument.** CoreCLR shares a
  generic class's code among the instantiations whose type arguments differ only in reference
  types, also when other arguments are value types: every state machine's
  `StateMachine<…>.Parameter<float>.Set` runs one body, `Parameter<__Canon, __Canon, __Canon,
  __Canon, float>.Set`. Mono's JIT shares code only when every type argument is a reference type
  (`mono_method_is_generic_sharable_full`, `mini-generic-sharing.c`: partial sharing, over
  primitive and enum arguments, is for full AOT or the `partial-sharing` debug option; Unity's
  fork, branch `unity-6000.3-mbe`), so there each such instantiation has code of its own, and a
  Harmony patch of one runs for that one alone. Harmony builds the replacement for the
  instantiation patched, with that instantiation's types where the shared code looks them up in
  the object's own; detoured onto the shared code, it ran for every other instantiation, whose
  objects fail its casts. Critter Condo Save Fix prefixes the critter condo monitor's
  `Parameter<float>.Set`: every state machine's float parameter then threw InvalidCastException
  (`Context[FetchChore+States,…]` to `Context[CritterCondoInteractMontior,…]`), and loading a save
  failed (issue #16, from `Rottable.Instance`'s constructor). The original loads it.
  **Resolved in** `SharedInstantiations`, a prefix of Harmony's `PatchTools.DetourMethod`: the
  shared code of an instance method of a class instantiated over a value type and a type it
  shares code over is detoured to a dispatcher. An object of the patched instantiation (or of a
  class derived from it) runs its replacement; one of another instantiation runs that
  instantiation's replacement when Harmony patched it too, else a copy of its own IL, made the
  first time it is called (as D34 makes a copy, which D34 rebuilds when a method it inlined is
  patched later). The dispatcher takes `this` as an object: typed as the instantiation, the JIT
  takes the type check for granted. A struct's method, a static method and a generic method take
  their instantiation as a hidden argument, which a dispatcher cannot read: they are left as
  they were, logged. Instantiations over reference types only share code on both runtimes (D18)
  and are left as they were. A call through the dispatcher costs a type check and an indirect
  tail call, and for an instantiation other than the patched one a lookup of the object's type.
  Cost at load: none measured. U57 patches no such method (PerfProbe is its only mod), and the
  prefix looks once at each patched method's type arguments; in alternating runs, while other
  sessions ran 2 to 6 players, U57 reached the main menu in 20.1, 16.7, 20.0 and 19.9 s with it,
  16.6, 15.6, 22.1, 21.2 and 17.9 s without, and loaded in 44.9, 35.6, 40.0 and 39.9 s with it,
  37.0, 25.5, 40.1, 44.4 and 46.8 s without. `--check-types` patches an emitted
  `Outer<string>.Param<float>.Set`, which casts its object to `Ctx<A, V>`, calls it for string, a
  class derived from it, Version and Uri past tier-up, then patches Uri's too;
  `tests/e2e/mods/3773818206-critter-condo-save-fix.yaml` tames a hatch that used a condo,
  saves, loads the save through the Load screen and reads how long its monitor waits.
- **D39 Order of fields and properties.** Mono lists a type's fields and properties class by
  class from the type to its bases, each class's in its metadata order
  (`ves_icall_RuntimeType_GetFields_native` and `GetPropertiesByName_native`, `icall.c`, over
  `mono_class_get_fields_lazy` and `mono_class_get_properties`; Unity's fork, branch
  `unity-6000.3-mbe`). CoreCLR walks the classes in the same order, but lists a class's instance
  fields, then its static fields, then its constants (`RuntimeTypeCache.PopulateFields`). And on
  CoreCLR only, Harmony's MonoMod sorts the cached field and property lists of each type that the
  IL of a method it patches names by metadata token (`ReflectionHelper.FixReflectionCache`, from
  `MethodBodyReader` and `DynamicMethodDefinition`; on Mono it finds no such cache and does
  nothing), across classes and assemblies, so a base class's members can come first, depending on
  what has been patched. Measured in both players (tools/UnityProbe,
  `PROBE_SCENARIO=memberorder`, at the main menu with OniMcp and E2eDriver loaded): of the
  game's 20,700 types (Assembly-CSharp and firstpass), GetFields with every flag listed the same
  fields in another order for 789 (732 within a class, 57 across classes), GetFields of instance
  fields for 114 and GetProperties for 50 (both across classes). Methods, events,
  nested types and constructors come in the same order (for the game's classes; System.Object's
  methods are each class library's). The order matters where the game numbers or creates things
  in it. `StateMachine.CreateStates` numbers a state machine's parameters in its fields' order:
  with Critter Condo Save Fix, whose patch of `CritterCondoInteractMontior.FindCondoTarget` names
  the monitor's fields, its parameters were masterTarget and remainingSecondsForEffect here and
  the other way round in the original. Saves match parameters by name, so they load either way;
  the order they are written in, and a mod reading a parameter by position, differed. Without
  mods, the 216 state machines of the early-base colony number theirs as the original does.
  `MyAttributeManager` finds or adds, and then spawns, the components of a component's `MyCmpAdd`,
  `MyCmpGet` and `MyCmpReq` fields in the fields' order, keeping the first field of a name: with
  only the host's and the test mods' patches, Navigator, SuitLocker and HighEnergyParticle had
  their StateMachineController (their base class's field) found and spawned before their own
  components. KSerialization (one class at a time, instance fields), Db's
  `ResourceSet.ResolveReferences` (by name) and GameComps (one class's static fields) come out
  alike. **Resolved in** `MonoReflectionOrder`: postfixes on CoreLib's
  `RuntimeType.GetFields(BindingFlags)` and `GetProperties(BindingFlags)` reorder each result
  (`OniMoono.Engine.MonoMemberOrder`): class by class from the type to its bases, each class's by
  metadata token; a result already in that order is left as it is. That is for the whole
  process, as on Mono, and covers the overloads that call them (`GetFields()`,
  `TypeInfo.DeclaredFields`, `FindMembers`). The members stay CoreCLR's: where the class libraries
  differ (System.Delegate's properties, the engine's classes' private fields), so do the lists.
  MonoMod's sort is left alone. Not covered: `GetMembers` and `GetMember` (no game code calls
  them). Kept: `GetInterfaces` (Mono walks a hash table of interfaces keyed by type token,
  CoreCLR its interface map; 1981 game types differ) and custom attributes (CoreCLR returns
  pseudo-attributes such as `[Serializable]` first, Mono last; 1432 types), which the game looks
  up by type, never by position. Cost: none measured. Starting, loading U57 (56 s while other
  sessions ran four players) and playing 10 s made 276,000 calls, which spent 20 ms in the
  postfixes all told (timed in them). A list in order is checked and left; one MonoMod sorted is
  sorted again on each call: GetFields of 130 instance fields over five classes takes 2.4 µs
  instead of 0.8. `--check-types` patches a method of a class whose fields and
  properties, and its base's, interleave instance, static and constant ones, and lists them;
  `MonoMemberOrderTests` sorts the probe's types as the original lists them.
- **D40 Methods naming a type its assembly no longer has.** A mod built for an older game names
  types the game has since renamed or removed: Endless Science fix's progress-bar prefix calls
  `Worker Workable.get_worker()`, and `Worker` became `WorkerBase`. Both runtimes fail such a
  method as a whole when it is compiled, at every call, before any of it runs, but report it
  differently (measured in both players with `tools/UnityProbe`, `PROBE_SCENARIO=missingtypes`,
  a library built with a type and run without it). Mono throws a TypeLoadException from the
  caller's frame, the failing method having none in the trace. Its message depends on where the
  JIT meets the type (`mini/method-to-ir.c`; Unity's fork, branch `unity-6000.3-mbe`): where it
  keeps the resolution's error, "Could not resolve type with token 01000071 from typeref
  (expected class 'Worker' in assembly 'Assembly-CSharp, Version=0.0.0.0, Culture=neutral,
  PublicKeyToken=null')" (`mono_class_from_typeref_checked`, `metadata/class.c`): exception
  clauses and locals, read first with the method's header, then calls, newobj, ldftn and jmp on
  a member reference or a generic method's arguments, ldtoken and calli; where it drops it
  (`CHECK_TYPELOAD`), "Failure has occurred while loading a type.": the type operands (castclass,
  isinst, newarr, box and the rest), fields, and a call to the mod's own method whose signature
  names the type. The first such place in IL order decides. CoreCLR's trace starts at the
  failing method, and it says "Method not found: 'Worker Workable.get_worker()'.", "Field not
  found: …" or "Could not load type 'Worker' from assembly …"; a call to the mod's own method
  with such a signature fails in the method called, after the caller has run. Endless Science fix
  fails in both players when a Duplicant starts its work, but ours logged a signature the
  original lacks. **Resolved in** `MonoMissingTypes`, after `MonoNestedTypeRefs` as `ModAccess`
  loads a mod: each method that names, in that order, a top-level type missing from an assembly
  already loaded whose types both players share (a mod's, or the game's own from `Managed/`,
  not Mono's class libraries; of Unity's engine and packages, which ours replace, a type that
  both ours and Unity's copy in `Managed/` lack: Delivery Temperature Limit's copy of PLib names
  `TMPro.FaceInfo`, which TextMeshPro no longer has) gets the body Mono's compilation
  gives it, throwing Mono's exception with the token of the mod's file, and is hidden from stack
  traces (`StackTraceHidden`), so the trace starts at its caller as Mono's does. Members
  missing from a type that exists are D42's. Types the original's copy of an engine or class
  library has and ours lacks are D57's. Not covered: missing nested types (D32 binds
  some of them), and assemblies not loaded yet when the mod loads; a method whose own signature
  names a missing type fails in its callers only. A mod that cannot be rewritten loads unchanged.
  `--check-types` loads a mod whose method calls a game method by a missing return type behind a
  branch not taken, one whose isinst names the missing type, and one constructing
  `TMPro.FaceInfo`.
- **D41 The calling assembly of a patch method.** Harmony calls a mod's prefixes, postfixes and
  finalizers from the replacement it builds for the patched method, a dynamic method owned by
  MonoMod's `DynamicMethodDefinition` (in 0Harmony). Mono's `Assembly.GetCallingAssembly` walks
  past wrapper frames, dynamic methods among them, and past CoreLib's `System.Reflection`
  (`get_caller_no_reflection`, `metadata/icall.c`; Unity's fork, branch `unity-6000.3-mbe`), so a
  patch method that asks is told the assembly of the code that called the patched method.
  Measured in the original player (tools/UnityProbe, `PROBE_SCENARIO=callingasm`: patched
  members of the probe called from an emitted assembly): prefixes and postfixes of a method and
  of constructors, one of them called from the other's replacement and one through
  `MethodInfo.Invoke`, name the caller's assembly; through `Activator.CreateInstance`, mscorlib.
  CoreCLR stops at the replacement and names 0Harmony, unless the JIT made the call to the patch
  method a tail call (a postfix last in a replacement that returns nothing), which leaves no
  replacement frame. Mod Origin Info's postfixes on the constructors of `ComplexRecipe`,
  `GeyserType` and `Spice` look up the mod by that assembly: in the early-base colony with Smol
  Generators and Roller Snakes Reborn they recorded no modded recipe, where the original records
  6, and 17 modded things where it records 23. **Resolved in** `MonoCallingAssembly`: a postfix
  on CoreLib's `GetCallingAssembly`, for the whole process as on Mono, walks the stack as Mono
  does whenever CoreCLR's answer is 0Harmony (the owner of every replacement): past the asking
  method, the first frame that is neither a dynamic method nor in CoreLib's `System.Reflection`,
  nor CoreLib's `RuntimeMethodHandle`, whose `InvokeMethod` calls a method invoked by reflection
  where Mono's runtime-invoke wrapper does (the probe's first `MethodInfo.Invoke` in our player).
  The probe then reports as the original does in every case but the one below.
  `GetExecutingAssembly` names the patch method's assembly on both runtimes (D19 keeps its
  frame), and `new StackTrace()` lists the replacement's frame on both (D23). Not covered: a call
  in a patched method's own body, which runs in the replacement: Mono names 0Harmony there (the
  owner's assembly, measured), ours the caller's; dynamic methods owned elsewhere (a mod's own),
  which Mono walks past too; a caller whose call is a tail call. Once a method is optimized
  (tier 1, or with tiered compilation off), CoreCLR's JIT makes a call right before `ret` a
  tail call, so the caller's frame is gone and its own caller is named, with or without Harmony
  (`--check-types` named the host for `call Who; ret` and for `callvirt Invoke; ret` once they
  tiered up, `DOTNET_TieredPGO=0` or `DOTNET_TieredCompilation=0`). Mono keeps that frame (the
  probe's `CallWho` has that shape). Cost: none measured. With the postfix, a call to a method returning
  `GetCallingAssembly()` takes 400 ns, as without it (release build); the walk, made only for
  Harmony's replacements and 0Harmony's own calls, 9 to 15 µs at 25 frames. `--check-types`
  patches an emitted game's two constructors, one calling the other, and a method, and calls
  them from a third assembly, directly and through reflection, past tier-up, from callers that
  use each result after the call, so none of their calls is a tail call;
  `tests/e2e/mods/3757075889-mod-origin-info.yaml` compares the mod's records with the
  original's.
- **D42 Methods naming a member its type no longer has.** A mod built for an older game calls
  methods or reads fields the game has since removed or retyped: Trapdoors & Floor Hatches' OnSpawn
  calls `SimMessages.SetCellProperties(int, byte)`. As for a missing type (D40), both runtimes fail
  the method naming it as a whole when it is compiled, but word it differently. Mono keeps the
  member lookup's error (`CHECK_CFG_ERROR` in `mini/method-to-ir.c`; Unity's fork, branch
  `unity-6000.3-mbe`) and throws it from the caller's frame: "Method not found: void
  .SimMessages.SetCellProperties(int,byte)" (`mono_error_set_method_missing`,
  `metadata/exception.c`): the return type, the class's namespace, '.' (also when the namespace is
  empty, so a nested class is ".Inner"), its name ("Box`1" for a generic one), '.', the method's
  name, "<!0,…>" for a generic method, and the parameters joined by ',', each as
  `mono_type_get_desc` writes it (`metadata/debug-helpers.c`: int, byte, single, string, int&,
  string[,], `System.Collections.Generic.Dictionary`2<string, int[]>`, Outer/Inner, !0 for a type
  parameter, !!0 for a method's). A field: "Field not found: int .GlobalKeeper.Field Due to: Could
  not find field in class" (`field_from_memberref`, `metadata/loader.c`), a field of the same name
  but another type included. Measured in the original player (`tools/UnityProbe`,
  `PROBE_SCENARIO=missingmembers`: 30 cases, among them static, instance and virtual methods, a
  constructor, a property, a changed parameter type, generic types and methods, nested types,
  by-ref and array parameters and fields; the first missing type or member in IL order decides,
  as in D40). CoreCLR says "Method not found: 'Void SimMessages.SetCellProperties(Int32,
  Byte)'." from the failing method's own frame. The game logs the exception as
  `KMonoBehaviour.Spawn` catches it, with `DebugUtil.LogExceptionCallstack(…, e.ToString(), e)`:
  the message and the exception's frames. On Mono the failing method has no frame, so the trace
  is `at KMonoBehaviour.Spawn ()` alone; ours started with `LadderHatch.OnSpawn()`, which made the
  log signature (message and first frame past the runtime's) one the original lacks.
  **Resolved in** `MonoMissingMembers`, in `MonoMissingTypes`' pass as a mod loads: each member
  reference whose type exists in an assembly the same in both players (D40), but has no member of
  that name and signature in it or its bases (a constructor in the type alone; an interface's
  methods in the interfaces it extends, then System.Object), looked up with reflection over the
  types loaded here, gets Mono's message, and each method naming one gets the body Mono's
  compilation gives it: it throws MissingMethodException or MissingFieldException with that
  message, hidden from stack traces. The engine's Unity-format trace of a logged exception
  (`LogTraces`) now leaves hidden frames out too, as `Exception.StackTrace` does. The probe then
  reports as the original does, but for the trace's format (CoreCLR's "   at A.B()", which the
  log signatures already treat alike). A member of Unity's engine and packages or of the class
  libraries that the original's copy has and ours lacks is D57's; one both lack fails as here.
  Not covered: member references whose signature names a type not resolvable when the mod loads
  (the mod's own, another mod's not loaded yet: they fail as CoreCLR fails them), vararg and
  function-pointer signatures. A generic argument tagged `valuetype` for a class is another type
  on Mono (D56): a reference naming one is missing. A
  member the lookup finds
  is one the runtime binds: across the 2,119 Workshop mods in `out/ws` (2,890 assemblies, 2.2
  million member references), none of the 2,089 references the lookup reports missing resolved on
  CoreCLR (`Module.ResolveMember`), and those CoreCLR could not resolve that it passed over name
  Unity's or the class libraries' members or a missing type (D40). Cost: the lookup over those
  assemblies took 3.4 s, 1.2 ms an assembly (release build); it reads metadata, and Cecil reads
  only a mod that names a missing type or member.
  `--check-types` loads a mod calling `SimMessages.SetCellProperties(int, byte)` behind a branch
  not taken, a generic game type's instance method by a by-ref array of its parameter, a generic
  method and a nested type's field the game lacks, and two members it has (one on a base class).
- **D43 A join reached with values on the stack from one side only.** ECMA-335 requires every
  path into an instruction to bring the same stack depth; CoreCLR's JIT refuses a method where
  one does not (InvalidProgramException when Harmony applies the patch). Mono's JIT checks only
  the paths that bring values: a block left with an empty stack hands its successors nothing
  (`handle_stack_args` in `mini/method-to-ir.c` returns at once for no values), so a join that
  one path reaches with values and another with none compiles, and the code after it, valid with
  the empty stack, never takes them. Transpilers that insert a test branching back with the
  tested value still on the stack produce such methods: Fixes and Tweaks' patch of
  `Timelapser.SetPostionAndOrtho` jumps to its `foreach`'s head with a `BuildingComplete` left
  over, and the mod failed to load (it loads in the original). **Resolved by a patch** of the
  game's Harmony (`TranspiledStackJoins`, run before D17's pass on the same
  `CodeTranspiler.GetResult`): where the stack depths of the instructions its transpilers produce
  disagree only so, each path bringing values to a join reached with an empty stack elsewhere
  pops them on its way (before an unconditional branch, after the instruction falling through,
  or in a trampoline for a conditional branch). A method whose depths differ otherwise (two
  non-empty depths, a switch, code after the join taking the values) is left as it was, and
  each change is logged. `--check-types` transpiles a `foreach` so and runs it.
- **D44 Nested types named with '/'.** Mono's lookup of a class by name takes '/' between a
  nested type and its outer type as well as '+' (`metadata/class.c`: the name is split at its
  first '/', and `return_nested_in` follows the rest one '/' at a time), so
  `Assembly.GetType("Outer/Inner")` finds `Outer+Inner`, as ILDasm and Cecil spell it. CoreCLR's
  type name parser takes '+' only and returns null: Delivery Temperature Limit looks up
  `FetchAreaChore/StatesInstance/<>c__DisplayClass17_0` to patch a lambda of the game's, logged
  an error the game reports with its error dialog, and the game quit (it loads in the original).
  **Resolved in** `MonoNestedTypeNames`: a prefix of CoreLib's
  `RuntimeAssembly.GetType(string, bool, bool)`, which every `Assembly.GetType` overload calls,
  spells a '/' as '+' (no CLR type name holds one otherwise). `Type.GetType` is left as it is.
  `--check-types` finds a nested type of a nested type so, ignoring case too, and a missing one
  as before.
- **D45 References to an assembly another mod loaded.** Measured in the original player with
  UnityProbe's binding probe (`AssemblyBinding.cs`, mods built by `tools/UnityProbe/Binding`),
  read with Mono's `metadata/assembly.c` and `appdomain.c` (Unity's fork, branch
  `unity-6000.3-mbe`). A mod's reference binds first to an assembly already loaded, searched in
  load order (`mono_domain_assembly_search`): without a strong name, the first loaded under its
  simple name, ignoring case, whatever the versions (PLib 4.8.1 references bind to another mod's
  PLib 4.22, as D15 has it); with one, the first of the same identity (version and key). Then the
  requesting mod's folder, whose file is taken whatever its version. Then AppDomain's
  AssemblyResolve, whose first handler is one Unity's player registers (UnityEngine's
  `ClassLibraryInitializer`): it loads the simple name, so a strong-named reference to another
  version or key binds to the first assembly loaded under that name too, and failing that the
  event is raised again with the simple name and no requesting assembly, before the other
  handlers see the full name. KMod's `Assembly.LoadFrom` of a strong-named file gives the
  loaded assembly of its identity, and a file of another version of it loads beside it
  (`GetAssemblies` lists both; a reference of that version binds to it); a mod's own
  `Assembly.LoadFrom` behaves as KMod's; `Assembly.Load(byte[])` always loads a new copy, as in
  CoreCLR. CoreCLR fails strong-named references to other versions than the one loaded, refuses
  a second file of a loaded name (KMod then drops the mod, even for a copy of the same version
  but another build), and raises AssemblyResolve once. **Resolved in** `ModAccess`: references
  bind as above (the default context's Resolving event, then an AssemblyResolve handler
  registered before any mod's, which takes the folder, the simple name and then raises the
  event again through `AssemblyLoadContext.OnAssemblyResolve`); a strong-named file of another
  identity loads in a context of its own; a prefix of `Assembly.LoadFrom(string)` gives a mod's
  own loads what KMod's get. Not covered: a strong-named reference whose other version sits in
  the requesting mod's folder while one is loaded binds to the loaded one in ours (Mono loads the
  file); and once a strong-named name has bound to the first loaded version, CoreCLR keeps that
  binding for the default context, so a reference of that name after its own version loads
  beside binds to the first still (Mono binds it to the new one). No mod in the Workshop
  catalogue ships such a file: its strong-named libraries at a mod's
  top level are copies of what the game or another mod ships (Newtonsoft.Json 7.0, YamlDotNet 15).
  The load sweep's Stacking bed difference (`s2000`) was not this: ours's batch failed through
  another mod, and bisecting left Stacking bed in a half without a mod that ships PLib.
  `--check-types` loads a strong-named copy, another version beside it, references to both
  versions and to a third, a mod's `Assembly.LoadFrom` of another version, and a reference to a
  missing assembly, and checks the AssemblyResolve events.
- **D46 NativeArray ranges.** Measured in the original player with UnityProbe's range probe
  (`NativeRanges.cs`): nothing on `NativeArray` checks a range or throws for one. The indexer
  reads and writes the memory before or past the array; `GetSubArray` gives views before or
  past it (a negative length gives a negative `Length`); the two-array `Copy`, `CopyFrom` and
  `CopyTo` copy the source's length whatever the destination's, past a managed array's end
  too, and the five-argument `Copy` the length given, before or past either array. A negative
  length writes garbage over a NativeArray and crashed the player into a managed array. The
  game's `RangeVisualizerEffect.OnPostRender` writes past an occlusion texture's
  `GetPixelData` array when a range outgrows the texture (Zoned Auto Sweeper's zone mode), and
  the original carries on. **Kept safe, without exceptions:** an out-of-range write is dropped
  and a read gives `default(T)`; the copies copy only the part within both arrays (nothing for
  a negative length); `GetSubArray` past the end is cut to the array, and one with a negative
  start or length is empty. The first one in a process is logged with its stack. An array that
  is not created or is disposed still throws. In-range access costs what it did (the same
  compare).
- **D47 Thread.Abort.** Read in Mono's `metadata/threads.c` (Unity's fork, branch
  `unity-6000.3-mbe`): an abort of another thread marks it AbortRequested and wakes it, and
  ThreadAbortException ("Thread was being aborted") is raised at its next safe point, a wait or a
  system call included. It is raised again at the end of every catch that takes it until the
  thread calls ResetAbort, finally blocks run, and a thread it ends ends quietly (no
  UnhandledException). An abort of a thread that has ended or is being aborted already does
  nothing; one of a thread not started marks it, and its Start then does not run it; one of the
  current thread throws at once. CoreCLR has none of it: Abort and ResetAbort throw
  PlatformNotSupportedException on the caller. KleiMetrics' StartSession ends a running session
  with ThreadedHttps.End, which aborts the session's HTTP thread when it has not stopped within
  2 s. With Klei's servers out of reach that thread is waiting to retry its post (16, 32 and
  64 s), and KleiAccount, giving up after the same retries, has Global.Update start a session
  again about two minutes in: CoreCLR threw from Global.Update and the game went to the crash
  reporter (issue #28). `scripts/e2e-klei-unreachable.sh` plays it
  (`tests/e2e/klei-unreachable.yaml`, with proxies that refuse connections for the players alone
  and a Steam double that gives a ticket). In the original, loading the colony started the session
  at 12:43:04, and its thread was in the 64 s wait when KleiAccount gave up at 12:44:42; 2.5 s
  later a new thread posted the new session (and gave up at 12:46:37), the old one was not heard
  from again, and play went on. In ours before the fix, the same timeline ended in the
  PlatformNotSupportedException from Global.Update and the crash reporter's screen; the new
  session never started, and the old thread's "Max Retries" never came: past its wait it takes
  Send's `if (shouldQuit) continue`, which posts again at once, with no line in the log and no
  end. **Resolved in** `MonoThreadAbort`: the calls of Thread.Abort, Abort(object) and ResetAbort
  in the game's assemblies and in mods are rewritten as they load (MonoConversions' pass). An
  abort marks the thread and interrupts it (Thread.Interrupt), so its next wait throws
  ThreadInterruptedException; in an assembly that calls Thread.Abort, every exit of a catch that
  could take it (of object, Exception, SystemException, ThreadAbortException or
  ThreadInterruptedException, or with a filter) calls `CatchEnd` first, which throws
  ThreadAbortException while the thread is being aborted, as Mono's re-raise does
  (ThreadedHttps.Send's catch goes back to retry: without it the thread posts again, forever).
  ResetAbort ends the abort and takes back an interrupt not raised yet. The exception that ends
  the thread is dropped quietly (UnhandledExceptions, D16). Aborting the current thread throws
  ThreadAbortException with Mono's message at the call; one of an ended thread does nothing. The
  game has 155 such catch exits (39 in Assembly-CSharp-firstpass, 116 in Assembly-CSharp), each a
  static read when no thread is being aborted. Only direct calls (call, callvirt) are rewritten,
  and catch exits only in an assembly with such a call of Thread.Abort. Load time, measured on the
  game's assemblies: the metadata filter that skips an assembly without such a reference takes
  0.2–7 ms (0Harmony, Assembly-CSharp); the pass over one that has it 7–25 ms for
  Assembly-CSharp-firstpass and 36–56 ms for Assembly-CSharp, once, as the game's rewritten images
  are cached; a mod with such a call is rewritten at each load, as for D29. `--check-types` aborts
  threads waiting in Sleep, a wait handle, Monitor.Wait, Join, a task's Result and an HTTP request
  to a server that never answers (each ends at once, quietly, and the call returns at once), the
  current thread, an ended one and one not started, and a mod's retry loop like Send's and a catch
  that calls ResetAbort. With the fix the spec passes in both players: the session's thread, in
  its 64 s wait, ends at the abort (the engine log's "Thread.Abort" lines, interrupted and then
  ended by its ThreadInterruptedException, nothing between them), and a new thread posts the new
  session 2.3 s after KleiAccount gave up, and gives up after its own retries, as in the original.
  **Differs from Mono:** the exception at the wait is ThreadInterruptedException, so a catch of
  ThreadAbortException alone does not take it there (only the re-raise at a catch's end), and `e
  is ThreadAbortException` is false for it: ONITwitch's connection thread logs "An unexpected
  exception occurred" rather than "Thread aborted", and Truthful Thermal Conductivity's long-click
  thread ends without its empty catch running (it ends either way). A thread that does not wait is
  not stopped: a loop without waits, or a system call (a socket's Receive is measured to go on
  until the socket closes). A catch in an assembly that does not call Thread.Abort is not followed
  by a re-raise, and the one interrupt can land in a wait in a finally block, which Mono's abort
  leaves alone. ThreadState never shows AbortRequested, ExceptionState is null (Abort(object)'s
  state is lost), a thread aborted before its start runs and its first wait is interrupted, and
  the main thread's own abort throws at the call without being followed. A delegate of
  Thread.Abort, or a call through reflection, still throws PlatformNotSupportedException.
- **D48 Serialized delegates.** Read in Mono's `System/Delegate.cs` and
  `DelegateSerializationHolder.cs` (Unity's fork, branch `unity-6000.3-mbe`): Delegate is
  ISerializable, and its GetObjectData (MulticastDelegate's calls it) writes, for each delegate of
  the invocation list, a DelegateEntry chained to the one before it ("Delegate" holds the first;
  private fields type, assembly, target, targetTypeAssembly, targetTypeName and methodName, public
  `delegateEntry`), the target as "target<n>" when there is one, and the method as "method<n>",
  and sets the info's type to DelegateSerializationHolder, which gives the delegate back. A
  RuntimeMethodInfo is ISerializable too, and writes MemberInfoSerializationHolder's entries (Name,
  AssemblyName, ClassName, Signature, Signature2, MemberType, GenericArguments). Newtonsoft.Json
  writes an ISerializable value as an object of its GetObjectData entries, so on Mono an object
  holding a delegate serializes. Measured in the original player with UnityProbe's delegate probe
  (`DelegateJson.cs`, `PROBE_SCENARIO=delegatejson`) through the game's Newtonsoft.Json 7.0.1: a
  holder of `Func<int,int>` writes `"Fn":{"Delegate":{"delegateEntry":null},"method0":{"Name":"Twice",
  "AssemblyName":"…","ClassName":"…","Signature":"Int32 Twice(Int32)","Signature2":"System.Int32
  Twice(System.Int32)","MemberType":8,"GenericArguments":null}}`; an instance method's and a
  closure's add `"target0"`, the target's own serialization (`{"Add":3,"Label":"target"}`, the
  display class's captured fields, `{}` for a lambda capturing nothing); a multicast one nests
  `delegateEntry` once per delegate and writes `method0`, `target1`, `method1`, … in list order; a
  generic method's GenericArguments are its type arguments' assembly-qualified names (mscorlib's).
  The same with Formatting.Indented and ReferenceLoopHandling.Ignore; none throws. Reading the JSON
  back throws (JsonSerializationException: the delegate type has no serialization constructor), and
  BinaryFormatter round-trips a delegate whose targets are serializable. CoreCLR's GetObjectData
  throws SerializationException ("Serializing delegates is not supported on this platform"), and
  its RuntimeMethodInfo is not ISerializable (Newtonsoft fails on one with
  PlatformNotSupportedException): OniExtract's export of game data, a postfix of
  `LegacyModMain.Load`, threw, the game logged it from `Assets.OnPrefabInit` and quit through its
  crash dialog (issue #22). **Resolved in** `MonoDelegateSerialization`: a prefix of
  `Delegate.GetObjectData` and `MulticastDelegate.GetObjectData` writes Mono's entries, with
  Mono's field names and order, and D1's names for types (mscorlib's for the class libraries'
  types, the Unity modules' for the engine's). "method<n>" holds a MethodInfo standing for the
  delegate's method (it reflects as the method and invokes it) that writes Mono's
  RuntimeMethodInfo entries: Signature is `MethodInfo.ToString()`, the same in both runtimes,
  Signature2 the types' `ToString()` with by-ref ones as `T&` and type arguments as ToString
  prints them (`System.Int32 Same[Int32](System.Int32)`, as measured), and GenericArguments the
  type arguments' Mono names as strings, which Newtonsoft writes as it writes Mono's Types. The
  holder types give the delegate back through BinaryFormatter, the targets serialized as before.
  The probe's JSON is then the original's for every case and setting (static, instance and
  generic methods, multicast, closure, lambda; default, indented, loop-ignoring). Cost: none
  elsewhere: two patches on the host's patch thread (about 20 ms, off the main thread), and a
  delegate's serialization takes about 12 µs (release build). Tests: `MonoDelegateSerializationTests`.
  **Differs from Mono:** names a formatter takes from the runtime: with TypeNameHandling, `$type`
  names this runtime's types (`System.Private.CoreLib` for the delegate type, as for every class
  library type, and `OniMoono.Host.DelegateSerializationHolder+DelegateEntry, OniMoono` and the
  stand-in where Mono names `System.DelegateSerializationHolder+DelegateEntry, mscorlib` and
  `System.Reflection.RuntimeMethodInfo, mscorlib`, GenericArguments `System.String[]` for
  `System.Type[]`), the info's FullTypeName and AssemblyName (the host's holder), and
  BinaryFormatter's bytes. Code reading the entries itself gets the stand-in, not a
  RuntimeMethodInfo (`Delegate.CreateDelegate` refuses it), and GenericArguments as strings. A MethodInfo serialized alone (not in a delegate) still throws, as
  does a delegate to a dynamic method (Mono's throws NullReferenceException, its method having no
  declaring type).
- **D49 Type.GetType by a name without an assembly.** Both runtimes look such a name up in the
  caller's assembly, then in CoreLib (mscorlib), but take the caller differently. Mono
  (`type_from_parsed_name` and `get_caller_no_system_or_reflection`, `metadata/icall.c`; Unity's
  fork, branch `unity-6000.3-mbe`) walks past unmanaged frames, wrappers (a dynamic method is one)
  and mscorlib's `System` and `System.Reflection` namespaces to the first other frame; CoreCLR
  takes the method that called `GetType`. In a Harmony replacement, a dynamic method owned by
  MonoMod's `DynamicMethodDefinition`, CoreCLR therefore searches 0Harmony. The game's
  `SaveLoadRoot.LoadInternal` finds a building's optional components with
  `Type.GetType(name)`; Colony Cuisine and Auto Kitchen patch it, and in ours every rocket
  interior's ports then loaded without them ("AddComponent with a null type", 15 times in the
  2minnow colony), the rockets' doors lost their targets and the crash screen opened as the save
  loaded (`ColonyDiagnosticUtility.GetDiagnostic`: KeyNotFoundException); the original loads
  them. **Resolved in** `MonoTypeByName`: a finalizer of `Type.GetType(string)`,
  `(string, bool)` and `(string, bool, bool)`, for the whole process as on Mono, looks the name up
  in the assembly Mono's walk gives when CoreCLR's lookup found nothing (or threw a
  TypeLoadException) and that assembly is not the caller CoreCLR searched; a stack walk is made
  only for a failed lookup. Not covered: a name that both 0Harmony (or CoreLib) and Mono's
  assembly define is found in the former, as CoreCLR finds it; the overloads taking resolvers.
  `--check-types` patches a game method that calls `Type.GetType` for a type of its own assembly,
  and calls it from that assembly: CoreCLR alone threw "Could not resolve type 'CheckPort' in
  assembly '0Harmony'".
- **D50 Mods built to run in 32-bit processes only.** An IL-only image whose CLI header has
  `32BITREQUIRED` (an x86 build) loads in Mono's 64-bit player: Mono reads the flag only into the
  assembly name's processor architecture (`mono_assembly_fill_assembly_name_full`,
  `metadata/assembly.c`), and its image loader skips the machine check (`metadata/image.c`).
  CoreCLR refuses it in a 64-bit process ("The assembly architecture is not compatible with the
  current process architecture"), and KMod disabled 气体液体管道装载器 (3280526635, whose
  `TestMod.dll` is so built). **Resolved in** `ModAccess`: such a mod image loads from memory
  with the flag cleared, before its other passes, and its `Location` stays its file's; IL runs the
  same either way. An image with native code is left as it is (it cannot run here).
  `--check-types` loads an x86 IL-only image marked so (CoreCLR alone refuses it) and runs it.
- **D51 Types nested in `<Module>`.** Obfuscators nest types in the module's global type (the
  NestedClass table names TypeDef 1 as their enclosing class). Mono loads them as any nested
  type: `<Module>` is a class like the others. CoreCLR's table of the classes an assembly
  declares leaves `<Module>` out, so such a type, and every type nested in it, fails to load
  ("Enclosing type(s) not found for type … in assembly …", BadImageFormatException), and KMod
  disables the mod (Abyssalite's `Katairite.dll` nests 8 of its 15 types in `<Module>` and 3 in
  those, Refrigerator's `Refrigerator.dll` 8 of 16 and 4). **Resolved in** `ModuleNestedTypes`, before `MonoNestedTypeRefs`
  as `ModAccess` loads a mod: the types nested in `<Module>` become top-level types (public ones
  public, the others internal) with their own nested types, and `<Module>`'s private and
  protected members become internal, so the code that could reach them as nested types still
  can. Kept: those types' names lose their `<Module>+` prefix and their `DeclaringType` is null,
  where Mono's is `<Module>`. `--check-types` loads a mod whose type nested in `<Module>` holds a
  type calling a private method of `<Module>` (CoreCLR alone refuses it as it is), lists its
  types and runs it.
- **D52 Mods' serializable types in Instantiate.** Measured in the original player (UnityProbe,
  `PROBE_SCENARIO=emptycomposite`, `EmptyComposite.cs`): Unity's serializer handles a
  `[Serializable]` class or struct only from an assembly the game was built with
  (`ScriptingAssemblies.json`). As a field of a mod's MonoBehaviour, a mod's class (with or
  without serialized fields or a parameterless constructor), a mod's struct and a `List` of a mod's
  class are not copied by Instantiate: the copy keeps what its constructor gave it (null, the
  struct's default; no constructor of the field's type runs) and the source's field is left as it
  is, null included. The same component's ints, strings and `List<int>` are copied, and a field of
  the game's own class (`AttackEffect`) is copied, a null one given an instance in both (the
  engine's rule for the game's types). The engine had applied the game's rule to every type:
  Rephysicalized's `FueledDietController` keeps its diet, a mod class whose state is in private
  fields and get-only properties, in a public field set on the prefab, and takes it from a
  registry in `OnSpawn` only when the field is null; ours gave every Sea Turtle an empty diet, and
  `TryApplyFuelConversion` threw a NullReferenceException each time one ate. **Resolved in**
  `SerializedLayout.Known`: a field whose composite type (or collection element) is from neither
  the game's assemblies nor the engine is not part of a script's serialized layout, for
  Instantiate as for JsonUtility, which had the same rule measured. Not measured: a mod's enum
  (still copied as its integer).
- **D53 Patches of methods with a filter or fault clause.** MonoMod (inside 0Harmony) builds a
  replacement with a filter (`catch … when`) or fault clause as a MethodBuilder in a new dynamic
  assembly, not the DynamicMethod it builds for every other replacement (.NET Framework's
  DynamicMethod could not hold those clauses). That generator's code references
  `AssemblyBuilder.Save`, which CoreCLR lacks, so here such a patch threw MissingMethodException,
  and the background-save tweak's patch of `LoadScreen.MigrateFile` ended the player. **Resolved
  in** `PatchedFilters`: MonoMod's own switch (AppContext data `MonoMod.DMDType`, which a
  `MONOMOD_DMDType` environment variable still overrides) makes it build every replacement as a
  DynamicMethod, whose CoreCLR IL generator holds both clauses. Harmony 2.4.2 still cannot patch a
  method with a filter clause, on either runtime: it copies the clause with MonoMod's Cecil IL
  generator, which never sets the clause's handler start, so the exception block is ended with
  the filter still open and the IL generator throws InvalidOperationException ("Incorrect code
  generation for exception block"; Mono's `ILGenerator.EndExceptionBlock` checks for an open
  filter, and both of MonoMod's generators emit through `_DMDEmit.Generate`). `Harmony.Patch`
  throws it as on Mono, the method stays unpatched and the game goes on; background-save waits in
  `MigrateFile`'s callers instead. A fault clause patches as on Mono: Harmony's
  `FaultBlockRewriter` turns it into a catch and a finally and drops the fault block's code.
  `--check-types` patches an emitted method with a filter, expects that InvalidOperationException
  and calls the method unpatched, and patches one with a fault with a prefix and a postfix.
- **D55 Instance fields injected into patches of static methods.** A patch method's `___name`
  parameter gets the original's field `name`; Harmony emits `ldarg.0; ldfld name` for an instance
  field even when the original is static, so it reads the field's offset in whatever object the
  first argument is. Neither runtime checks the object's type there, and the two lay fields out
  differently. Fast Track's prefix of the static `IdleStates.MoveToNewCell(Instance smi)` (with
  its ReduceCritterIdleMove option) takes `___loop` and, for a critter off screen, sometimes calls
  `smi.GoTo(___loop)`. Measured in the original player with a probe prefix of the same shape on
  the prehistoric1 save: `___loop` is null for every critter kind there, so GoTo stops the idle
  state machine; ours read the instance's transition stack, GoTo pushed it on its
  `Stack<BaseState>`, and CoreCLR's array store check threw ArrayTypeMismatchException (issue
  #31: Klei's error dialog). **Resolved in** `MonoInstanceFieldInjection`: where Harmony injects
  an instance field into a patch of a static method whose first argument is not of the field's
  type, the object is checked at run time; one of the field's type is read as before, anything
  else gives the field type's default. Not matched: another layout's non-null read (none known),
  and a field taken by `ref`, left as Harmony emits it. `--check-types` patches an emitted static
  method taking another type, and one taking the field's own type.
- **D56 A type tagged as another kind than its own.** A signature names a type with a tag:
  `valuetype` or `class`. A mod built for an older game tags as a value type what is now a class:
  LogicValueChanged became a pooled class in U57 (U60 makes Crop.CropVal one too; U59's own
  assemblies keep it a struct). Of the 4,572 assemblies in `out/ws` (106 mods, archived
  versions included), 10 tag LogicValueChanged so (and 159 Crop.CropVal, which matters from
  U60 on), 46 of the 169 in a method's locals; 5 copies of PLib tag
  `Steamworks.PublishedFileId_t`, a struct, `class` (an ILRepack fault). CoreCLR
  refuses every such signature: "Could not load type 'LogicValueChanged' from assembly 'ONI
  Mod1, …' due to value type mismatch." Mono (Unity's fork, branch `unity-6000.3-mbe`) reads the
  tag and the class apart: `do_mono_metadata_parse_type` (`metadata/metadata.c`) resolves the
  token and `compare_type_literals` accepts `valuetype` for a class; the type it builds keeps the
  tag, and `mono_metadata_parse_type_internal` replaces it with the class's own type only where
  it is not transient and has no attributes. So method signatures and member references read the
  class, while a method's locals (parsed transient, `mono_metadata_parse_mh_full`), a field the
  mod declares (parsed with its flags as attributes, `mono_field_resolve_type`) and a generic
  instantiation's arguments keep a value type of the class. Its JIT (`mini/method-to-ir.c`) then
  refuses, when it compiles the method, to store an object into such a local
  (`target_type_is_incompatible`) or to pass such a value where an object is expected, or an
  object where it is expected (`check_call_signature`): the method fails as a whole, every call,
  with an InvalidProgramException from the caller's frame, "Invalid IL code in
  Smart_HEP.SmartHEPStorage:OnLogicValueChanged (object): IL_0006: stloc.0   \n\n"
  (`set_exception_type_from_invalid_il`: `mono_method_full_name`, the instruction as
  `mono_disasm_code_one` writes it, a token as 0x%08x). A member reference whose signature has
  such a generic argument names another instantiation than the game's and is not found (D42):
  "Field not found: System.Collections.Generic.List`1<Crop/CropVal> TUNING.CROPS.CROP_TYPES Due
  to: Could not find field in class". A `class` tag for a value type fails the member reference:
  BadImageFormatException "Expected reference type but got type kind 17". Measured in the
  original player (`tools/UnityProbe`, `PROBE_SCENARIO=kindchange`: a library built with a struct
  and run as a class, and the other way round: 28 cases). Unboxing into a local, storing a call's
  result, a static field's or an array element's, `new` into a temporary, and an instance call
  on a call's result through a temporary all fail at the `stloc`; a default local or an `out`
  local passed on, the mod's own static field passed on (its store passes: `stsfld` is not
  checked) and `List<Flip>.Add` fail at the call; a pass-through, a box, a new `List<Flip>` and
  the mod's own method taking one run. A field reached through the address of a static or
  instance field or an array element reads unrelated memory (0 where it holds 3, 4 and 7), and a
  default local's field and a constructor called on a local's address crash the player (an
  assertion, `store_opcode != OP_STOREV_MEMBASE`, and a signal 11). An assembly with
  SkipVerification (C# emits it with unsafe code allowed: 24 of the 174) skips the store check
  (`mini_assembly_can_skip_verification`), and Mono then fails that assertion and aborts.
  Smart Radbolt Storage's OnLogicValueChanged unboxes the event into a local: the original logs
  Mono's InvalidProgramException each time a radbolt chamber's logic port changes, ours logged
  CoreCLR's TypeLoadException, a log signature the original lacks. **Resolved in**
  `MonoTypeKinds`, after `MonoMissingTypes` as `ModAccess` loads a mod: a walk over the mod's
  signature blobs finds TypeRefs tagged against the kind of their type in an assembly the same
  in both players (D40); only then does Cecil read the mod. Each method whose Mono compilation
  fails so (an abstract run over its IL in Mono's order, its unreachable blocks skipped as Mono's
  verifying JIT skips them, the store checks left out for a SkipVerification assembly) gets the
  body Mono's compilation gives it: it throws Mono's exception and message, hidden from stack
  traces. `MonoMissingMembers` treats a generic argument tagged so as another type. Every tag
  then becomes the type's own, so the rest runs as the class; where a method reaches a member
  through the address of a location of such a type, it reaches it through the location's value,
  and a constructor called on an address becomes `newobj` and a store: where Mono reads unrelated
  memory or crashes, ours reads the class's members (3, 4, 7). A token in the message is the
  mod's file's (an earlier pass's Cecil write renumbers member references). The probe then
  reports in ours as in the original but for those three values. Not covered: a method that
  names a missing member (D42) and also fails so reports the missing member, whichever comes
  first in IL order; the player Mono aborts. Cost: the walk over the 4,572 assemblies took 2.5 s,
  0.55 ms an assembly with its PE reader (release build); Cecil reads only the ones that tag
  against the game's kinds (169 against U60's), 21 ms each on average, 435 ms at most.
  `--check-types` loads a mod built against a library whose struct is
  now a class, with one method per IL pattern above, and the same mod with SkipVerification.
- **D57 References to what the original has and ours lacks.** A mod names a type or member of
  an assembly ours answers for in place of the original's copy in `Managed/` (Unity's engine
  modules and packages, which ours replace; Mono's class libraries, for which the framework
  answers), and the original's copy has it where ours does not. ONI Boost Kit's
  `GCSentinel.Sentinel.RunChecks` reads `JobsUtility.JobWorkerCount` inside a try of its own;
  Artificial Natural Tiles' `TryLoadSprite` builds a `System.Drawing.Bitmap` (Mono's
  System.Drawing, which .NET's facade forwards to System.Drawing.Common, an assembly the framework
  does not carry) inside one. CoreCLR fails each method as a whole when it compiles it, so the
  catch never sees the exception: the sentinel's notification never appeared (a log signature
  the original lacks), and the tiles' build category was never registered, so
  `PlanScreen.RefreshToolTip` threw ArgumentOutOfRangeException and MOD DIFFERENCES stayed up.
  In the original both methods run. Measured there (`tools/UnityProbe`, `PROBE_SCENARIO=atcall`,
  build 761211, Linux player): what fails to resolve fails the method as a whole on Mono too, as
  D40 and D42 measured, a catch inside it never seeing it (none of the method ran, the caller
  caught it): an assembly that cannot be found gives "Could not load file or assembly
  'ProbeAbsent, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null' or one of its
  dependencies." (FileNotFoundException, FileName the display name) where the JIT keeps the
  error (a call, newobj, a local, a value-type local), and "Failure has occurred while loading
  a type." (TypeLoadException) where it drops it (a static field, isinst); a missing member gives
  D42's message. Mono's `new Bitmap(stream)` compiles and throws inside the try: "The type
  initializer for 'System.Drawing.GDIPlus' threw an exception." (TypeInitializationException;
  inner DllNotFoundException "libgdiplus.so.0"; on Windows GDI+ loads the image), and
  `JobsUtility` has `JobWorkerCount` (get and set, 15 with 16 processors and no
  `job-worker-count` in boot.config; values outside 0 to `JobWorkerMaximumCount`, also 15, throw
  ArgumentOutOfRangeException "Invalid JobWorkerCount -1 must be in the range 0 -> 15"),
  `ResetJobWorkerCount()` and the literals `MaxJobThreadCount` (128) and `CacheLineSize` (64),
  which ours now has. **Resolved in** `MonoApiGaps`, in `MonoMissingTypes`' pass as a mod loads:
  a TypeRef (nested ones included) of such an assembly that ours cannot resolve while the
  original's copy (read as metadata, forwarders followed) has it, and a member reference
  `MonoMissingMembers` finds no member for while the original's copy has one (by name and
  signature, bases and interfaces as D42 walks them), is a gap; one both lack fails the method as
  a whole, as D40 and D42. Each method Mono compiles that names a gap gets its instructions
  naming one replaced, each by pops of its operands and a throw, and its locals of a gap type
  become objects (an enum's, its underlying type, from the original's metadata). The rest of the
  method runs as in the original, and a catch around the instruction sees the exception, as it
  sees Mono's from a call that fails. The exception is the one Mono gives for such a reference
  when it does not resolve, by where its JIT meets it: FileNotFoundException naming the
  assembly ours could not load ("System.Drawing.Common, Version=0.0.0.0, …"), else
  TypeLoadException with D40's message, or D42's MissingMethodException or
  MissingFieldException. The tiles' loader then logs its warning and falls back to its
  placeholder icon, as in the original on Linux; the original's own message (GDI+'s) is not
  reproduced. Not covered: a method whose own signature or catch clause names a gap (CoreCLR
  fails it as a whole or its callers' call to it), fields of a gap type, the original's
  behaviour past the gap (System.Drawing on Windows draws the icon; ours falls back), and
  assemblies no player can load (D40's "not loaded yet"). Across the 4,010 mod assemblies in
  `out/ws` (Unity's, the game's and the class libraries' copies left out), 47 name gaps
  (System.Drawing's Image and Bitmap in 20, UnityWebRequest upload handlers, the video and
  particle modules, `Input.inputString`, IMGUI's SelectionGrid, ONI Twitch Integration's copies
  of Unity's internal marshalling helpers); the 307 methods rewritten all compile
  (`RuntimeHelpers.PrepareMethod`). Against U59's assemblies (build 744825) the same scan finds
  the same gaps, by file and reference, as against U60's. `--check-types` loads a mod building a
  Bitmap into a Bitmap local and reading a property of its base, testing isinst Bitmap, calling
  a static method of UnityEngine.CoreModule the original has and ours lacks, storing into a
  local of an enum ours lacks, each inside a try, and reading `JobWorkerCount`.
- **D58 A native library that does not load throws Mono's DllNotFoundException.** U60's crash
  reporter shows its "OS Error" popup ("Your operating system is preventing the sim from
  loading") when an error's text holds "DllNotFoundException: SimDLL"
  (`KCrashReporter.IsOperatingSystemBlockedError`), and WorldGen queues the exception its settle
  step throws for that. CoreCLR's message ("Unable to load shared library 'SimDLL' or one of its
  dependencies…") never matches. Measured in Unity's Linux player (761211, e2e
  `u60/p1-sim-blocked` on the u60 branch): with `libSimDLL.so` absent the message is "SimDLL
  assembly:<unknown assembly> type:<unknown type> member:(null)", the DllImport's name; with
  the file there but unreadable, the dynamic loader's error stands for the name
  ("/…/libSimDLL.so: cannot open shared object file: Permission denied assembly:…"), so no popup.
  U59 has neither the popup nor the queueing: its worldgen thread ends on the exception
  unhandled, and the player logs it (D16) with the same messages, which is what game and mod
  code see there (measured in U59's original, 744825, Linux). **Resolved in**
  `AssemblyPolicy.ResolveNative` with `MonoDllNotFound`: for a P/Invoke of game or mod code (an
  assembly not loaded from the host's folder) whose library no file provides, or whose plugin
  file does not load, the resolver throws DllNotFoundException with Mono's message (dlerror's
  text for a file that failed). A plugin file it cannot read is taken to have the process's
  architecture, and so fails at dlopen as in Unity. Windows is assumed to give the name's form in
  both cases (Klei's matcher expects it for a blocked SimDLL.dll); not measured there yet.
- **D59 French digit-group separators.** ICU separates the digit groups of fr, fr-CD, fr-CH,
  fr-CI, fr-CM, fr-FR, fr-HT, fr-MC, fr-ML, fr-RE and fr-SN with U+202F (narrow no-break space,
  CLDR's since version 34); Mono's culture tables (`mono/culture/culture-info-tables.h`, Unity's
  fork) have U+00A0 (no-break space) for each, as number and currency separators (Mono's percent
  one is the number one). So `1234567.891.ToString("N2", fr-FR)` is "1 234 567,89" with U+202F
  here and U+00A0 in the original (UnityProbe's `ns21` on U60). The other cultures where ICU has
  U+202F differ otherwise: Mono has no data for 32 French cultures, vec and vec-IT, and fr-BE's
  separator is '.' there. **Kept:** the game formats in the invariant culture. `LaunchInitializer`
  sets the main thread's (`LaunchInitializer.cs:43`, `Util.ApplyInvariantCultureToThread`) and
  worldgen its thread's (`ProcGenGame/Cluster.cs:241`); under `LANG=fr_FR.UTF-8` (measured on
  U60) the main thread, a new thread and a pool thread all format in the invariant culture in both
  players, and only `CurrentUICulture` is fr-FR, which no game code formats with. The game's
  fonts draw both characters (NotoSans-Regular and LiberationSans have U+202F), so only a mod
  formatting with a French culture by name shows the other space.
- **D60 GZipStream's compressed bytes.** The same input compresses to other bytes: 4,000 bytes
  ("Oxygen Not Included " 200 times, UnityProbe's `ns21` on U60) give a 70-byte stream in the
  original and 85 here. Mono's `GZipStream` and `DeflateStream` call
  the zlib 1.2.11 inside `libMonoPosixHelper` (`support/zlib-helper.c`, `CreateZStream`:
  `deflateInit2(Z_DEFAULT_COMPRESSION, Z_DEFLATED, 31 or -15, 8, Z_DEFAULT_STRATEGY)`); .NET's
  call its bundled zlib-ng. Both compress at level 6 (`CompressionMode.Compress` is .NET's
  `Optimal`), and the gzip header is the same 10 bytes on Linux (no flags, mtime 0, XFL 0, OS 3):
  the deflate data differs, because zlib-ng's levels 2 to 6 match strings otherwise than
  zlib's. Measured: zlib 1.3 gives 70 bytes at levels 4 to 9 and 85 at 1 to 3; .NET 10 gives
  85 at levels 1 to 6 and 70 at 7 to 9 (`ZLibCompressionOptions`). Each player decompresses the
  other's output. **Kept:** only code comparing compressed bytes or their length notices. The
  game's saves do not use it: `SaveLoader.CompressContents` writes through Ionic.Zlib's
  `ZlibStream` at `BestSpeed` and `DecompressContents` reads with `ZlibStream.UncompressBuffer`
  (`SaveLoader.cs:286`, `:300`), managed code in `Managed/Ionic.Zip.dll` that runs the same in
  both players. The game's one use of `System.IO.Compression` is the crash reporter's upload
  (`KCrashReporter.cs:709`, `:718`, a `ZipArchive` at `Fastest`), which it never reads back.
- **D61 Delegates to instance methods with a null target.** Measured in the U59 and U60
  originals with UnityProbe's null-delegate probe (`NullDelegate.cs`,
  `PROBE_SCENARIO=nulldelegate`): Mono refuses such a delegate as CoreCLR does. `new D(x.M)` with
  `x` null (ldftn and newobj, from C# and from emitted IL, for a non-virtual, a generic, a
  virtual and a struct's method) throws ArgumentException as the delegate is built, so the
  method never runs and no such delegate exists to invoke, compare or combine; an ldvirtftn on
  null throws NullReferenceException. `Delegate.CreateDelegate(type, null, method)` and
  `MethodInfo.CreateDelegate(type, null)` make a delegate closed over null in both (Target null,
  Method the method): invoking it runs the method with `this` null, which returns when the
  method does not read `this` and throws NullReferenceException when it reads a field; a virtual
  method runs its own body. `CreateDelegate(type, null, "name")` throws ArgumentNullException in
  both. Every case gives the same result in both players. Only the exception's text differs:
  Mono's message is ArgumentException's default ("Value does not fall within the expected
  range.") and its trace starts at the method building the delegate; CoreCLR's says "Delegate to
  an instance method cannot have null 'this'." under `MulticastDelegate.CtorClosed`. The game's
  `ManagementMenu.OnPrefabInit` builds `clusterMapScreen.TryHandleCancel` with
  `clusterMapScreen` null when a base-game save is loaded into a Spaced Out session without the
  load screen (PerfProbe's `load.txt`): both players log that ArgumentException from
  `OnPrefabInit`, then a NullReferenceException from `ManagementMenu.OnSpawn`
  (`SetNotificationDisplay`), and open the crash reporter. **Kept:** no rewrite, since one
  storing the null target would make ours differ from the original.
- **D62 Uninitialized delegates.** Read in Mono's `formatterservices.cs` (referencesource) and
  `metadata/icall.c` (Unity's fork, branch `unity-6000.3-mbe`): FormatterServices'
  `GetUninitializedObject` and `GetSafeUninitializedObject` call
  `ActivationServices.AllocateUninitializedClassInstance`, which refuses only interfaces and
  abstract classes, so a delegate type gives a delegate with no method; invoking it throws
  NullReferenceException. CoreCLR's (`RuntimeHelpers.GetUninitializedObject`) refuses delegate
  types with ArgumentException "Type is not supported.". The game's serializer makes a field's
  object so when its type has no parameterless constructor
  (`KSerialization.DeserializationMapping.ReadValue`), and a care package saved before it opens
  holds a `CarePackageInfo` whose public `requirement` is a `Func<bool>`: the load stopped there,
  and the half-loaded world then read the grid before it was made, an access violation in
  `DecorProvider.AddDecor` (issue #69). **Kept:** a prefix of both methods answers a concrete
  delegate type with a new delegate of it over a stub that throws NullReferenceException. Unlike
  Mono's, its `Method` is that stub, not null.
- **D64 Delegates passed to native code.** Read in Mono's `metadata/marshal.c` (Unity's fork,
  `unity-main`): `mono_delegate_to_ftnptr`, behind a P/Invoke's delegate argument and
  `Marshal.GetFunctionPointerForDelegate`, compiles a trampoline for the delegate's method and
  records the delegate in `delegate_hash_table` under it. For a delegate to a static method the
  trampoline is shared by every delegate of its type and method, and the table holds a strong GC
  handle that is never freed: the delegate lives for good. A delegate with a target is held by a
  weak handle, as on CoreCLR. CoreCLR keeps a delegate passed to native code only as long as
  managed code does; a call of its thunk after a collection fails fast ("A callback was made on a
  garbage collected delegate"). The game passes SimDLL its message handler as a fresh delegate
  (`Sim.SIM_Initialize(Sim.DLL_MessageHandler)`, `docs/research/runtime.md` R2), which SimDLL
  calls to report an error and from its exception handler when the process crashes: in issue #75
  an unhandled OutOfMemoryException reached that handler, and the process ended with the
  collected delegate's message instead of the exception's. **Kept:** the calls of P/Invokes with
  delegate parameters, and of `GetFunctionPointerForDelegate`, in the game's assemblies and in
  mods (rewritten as they load, MonoConversions' pass) pass a delegate to a static method through
  `MonoMarshalledDelegates.Keep`: the first delegate of its type and method, kept for good, so
  native code gets one thunk that stays valid, as Mono's shared trampoline.
- **ZEROs (2337612362) patches a method only Mono's class library has.** Its OnLoad patches
  `System.Number.FormatSingle(float, string, NumberFormatInfo)` with a prefix that widens custom
  formats with a '.' ("0.0" becomes "0.0##"), and `StringBuilder.AppendFormat(string, object)`.
  In the original (measured, `PROBE_SCENARIO=atcall`) Mono's mscorlib has that method, every
  `float.ToString` overload calls it (a postfix ran for `ToString()`, "F2", "G" and "N1"; not for
  string interpolation, which formats through the ValueStringBuilder overload), so the mod
  works. CoreLib has no such method: `float.ToString` formats through the generic
  `Number.FormatFloat<TNumber>(TNumber, string, NumberFormatInfo)`, so `AccessTools.Method`
  returns null, Harmony reports "Null method for ZEROs" and the mod is disabled. Structural, not
  shimmed: CoreLib cannot be given a method of Mono's name, so a shim would have to answer
  reflection's lookup of a Mono-internal method with another one (a redirect for this name, not
  an API) and have Harmony patch the float instantiation of a generic, precompiled CoreLib
  method.

## 3. The engine API (`OniMoono.UnityEngine`)

### 3.1 Surface rule

The public surface is **exactly** what the consumers reference, with Unity's names, member
kinds, signatures, `virtual`/`sealed`-ness, and base types, verified by `tools/ApiCheck`
(conformance gate, §9). We do not add public API Unity doesn't have. Internals are free-form.
Mods may call Unity API the game never does; types they are known to need are listed in
`engine-required-types.tsv`, and running `tools/ApiCheck` over a profile's mod assemblies finds
the members they reference that the engine lacks. `scripts/apisurface.sh` (`ApiCheck --surface`)
lists every public type and member of the shipped Unity assemblies that the engine and our
packages lack, and which of them the Workshop builds in `out/ws` reference.

The consumer set is: `Assembly-CSharp`, `Assembly-CSharp-firstpass`, `UnityEngine.UI`,
`Unity.TextMeshPro`, `Unity.Timeline`, `FMODUnity*`, `ImGui.NET`, Steamworks.NET.

Members are implemented **completely or not at all**. A member that is not yet implemented
does not exist; the runtime then fails with `MissingMethodException`, which names it exactly,
and ApiCheck lists it. There are no `throw new NotImplementedException()` placeholders and no
silent no-ops. Where Unity's behaviour is legitimately a no-op on this platform (e.g.
`Gizmos.*` in a player, `Handheld.*`), the member is implemented as that no-op with a comment
citing why.

### 3.2 Object model

- **Engine state lives on the "native side", never in managed fields of public types.** Like
  Unity, `UnityEngine.Object` has only private value-type fields: `IntPtr m_CachedPtr` (here a
  `GCHandle` to the engine's internal `NativeObject` record) and `int m_InstanceID`. Public
  engine classes (`GameObject`, `Component`, `Transform`, `Behaviour`, `MonoBehaviour`, …)
  declare **no other instance fields** unless Unity's own class declares them (e.g. the
  managed fields of `MonoBehaviour`). Reason: game code reflects over the full field set of
  components — `Localization.SetFont` walks `Instance|Public|NonPublic` fields recursively and
  follows `GameObject` fields into `GetComponents()` (*runtime §7.4*), and KSerialization builds
  templates over `Object`/`Component`/`Behaviour` fields with `GetCustomAttributes`. Managed
  back-references there would recurse forever or be serialized.
- The `NativeObject` record holds the alive/destroyed flag, name, hide flags, owning asset (if
  loaded from a file), and for GameObjects/Components the scene-graph data. **Fake-null
  semantics** follow Unity: `==`, `!=`, `op_Implicit(bool)` and `Equals` treat destroyed objects
  (and C#-created `UnityEngine.Object`s with no record) as null; these checks are lock-free
  reads usable from any thread.
- Value types used by saves keep Unity's exact field shape: `Vector2/3/4` (`x,y,z,w` public
  fields), `Color` (`r,g,b,a`), `Color32` (`r,g,b,a` bytes), `Quaternion` (`x,y,z,w`), and **no
  public settable instance properties** on `Color32`/`Vector4` (KSerialization's OptOut rule
  would serialize them). No full type name we define may also exist in a game assembly.
- `GameObject` owns an ordered component list; `Transform`/`RectTransform` is always first.
  `GetComponent*` matches by assignability, including interfaces, in component order.
  Lookups are cached per (GameObject, Type).
- **Activation and messages** follow Unity's player semantics (*usage §3*):
  - Message methods (`Awake`, `OnEnable`, `Start`, `Update`, `LateUpdate`, `FixedUpdate`,
    `OnDisable`, `OnDestroy`, `OnApplicationFocus/Quit`, `OnRenderImage`, `OnPreCull`,
    `OnPostRender`, `OnRectTransformDimensionsChange`, `OnTransformParentChanged`,
    `OnBecameVisible/Invisible`, `OnValidate` (editor-only: never), `OnGUI`, …) are discovered
    per type **by name, most-derived declaration only, any accessibility**, and bound once into
    open delegates. A base implementation runs only if the derived one calls it.
  - `Awake` runs when a component's GameObject first becomes active in hierarchy (or at
    load/instantiate time if already active), then `OnEnable`; `Start` runs before that
    component's first update callback, at the next Start pass or that callback, whichever
    comes first (§3.3). Activation and deactivation
    recurse in Unity's order (Awake/OnEnable for the whole subtree, parent first; OnDisable
    children-first when deactivating). Deactivation, and the same step of a destruction, walks
    the subtree: an object's `activeInHierarchy` turns false when the walk reaches it, then its
    children are walked, then its own `OnDisable`s run. While one object's `OnDisable` runs, its
    later siblings still read active, and deactivating one of them disables it there and then
    (UnityProbe's deactorder; the details panel's storage rows, issue #23).
  - `Destroy` is deferred to the end of the frame; `DestroyImmediate` is synchronous,
    children first, with `OnDisable` then `OnDestroy`. The GameObject passed to either turns
    inactive itself (`activeSelf` false) before its subtree's callbacks, its descendants keeping
    their own flag, and a component destroyed alone leaves its object active (UnityProbe's
    destroyflags): `KMonoBehaviour.OnDestroy` releases the object's `KObject` event system only
    on `!activeSelf`.
  - Exceptions in a message are logged through `Debug.LogException` and do not stop the frame,
    as in Unity.
- **Script execution order** comes from `MonoScript.m_ExecutionOrder` in
  `globalgamemanagers.assets`, and for a type no MonoScript describes (a mod's) from its
  `[DefaultExecutionOrder]`, inherited, as Unity's player applies it (UnityProbe's execorder);
  within one order, update order is the order components' activation
  began (or they were enabled later), matching Unity's behaviour list. Pending `Start` calls are not sorted by it: they run in
  the order the behaviours were scheduled (became active and enabled), as Unity's delayed-call
  queue does.
- `ScriptableObject` and `MonoBehaviour` instances are created with
  `RuntimeHelpers.GetUninitializedObject` + the parameterless constructor, as Unity does
  (field initializers run, then deserialization overwrites serialized fields). A class without
  one is created with no constructor run at all, its base classes' and field initializers
  included, and nothing is logged (UnityProbe's missingtypes; Trash Cans' components have only
  a constructor taking a state).
- **Costs at ONI's scale.** A late-game colony holds hundreds of thousands of objects, most of
  them scene roots (buildings, items, critters), and creates, destroys and re-parents them
  constantly (an item stored is parented to its storage). Scene roots are therefore an ordered
  slot list (`SceneRoots`: constant-time removal, compaction into a new array, enumeration
  safe against it), and per-frame walks cache what they look up per GameObject against its
  copy-on-write component array (`UiParts` for the canvas walk). A transform's child list is
  copy-on-write too, but with spare slots once a parent grows at run time: an append fills the
  next spare slot, which no list read before has, so the game's popup texts (a parent of
  thousands of children, one added per new popup) no longer copy the list each time. Searches
  by type (`FindObjectsByType`) walk the scenes and skip inactive subtrees when inactive objects
  are not asked for.
- **Per-frame allocation.** ONI issues ~1,700 draws a frame (most through `Graphics.DrawMesh`) and
  rebuilds several meshes every frame, so the render path allocates almost nothing per draw:
  draw items, draw lists and canvas batches are reused frame to frame (`FrameRenderer.NewDrawItem`,
  `NewDrawList`; valid until `EndFrame`); queued meshes are values; `DrawMesh` shares the property
  block's sheet with the queued draw until the frame renders, a later change moving the block to a
  copy (`PropertySheet.SharedEpoch`); the device's per-draw inputs come from the recording frame's
  arena (`FrameArena.Next`) with their command delegate made once; mesh uploads hold the mesh's own
  arrays, which are never written once published (`MeshNative`); colours given as bytes stay bytes.
- **Resolved once, not per draw.** Program variants are selected once per material, pass and draw
  keyword list while keywords are unchanged (`MaterialNative.Variants`); what a material and the
  global properties give a program (uniform values, block bytes, sampler textures, pass state) is
  resolved once per their versions (`MaterialValues`), a draw resolving only its per-draw values
  and what its own block overrides; render queues and `_ST` property ids are parsed once.

### 3.3 Player loop

One thread, Unity's order (*usage §1.3, §3.5*), driven by `OniMoono.Host`:

```
Initialization  time update (Time.*), pending scene-load completion
EarlyUpdate     platform events → Input state; Screen size changes
                Start pass
FixedUpdate     0..n steps (accumulator, maximumDeltaTime clamp), each followed by a Start pass;
                coroutines WaitForFixedUpdate; Destroy carried out
Update          Update; Start pass (ScriptRunDelayedDynamicFrameRate); Destroy carried out;
                coroutines (null, WaitForSeconds, custom yields).
PreLateUpdate   Animators (DirectorUpdateAnimationBegin/End); LateUpdate; Start pass; Destroy
                carried out
PostLateUpdate  Canvas.willRenderCanvases (uGUI layout/graphic rebuild) → render all cameras
                → screen-space-overlay canvases → WaitForEndOfFrame coroutines → present
```

Quit (`Application.Quit`, closing the window, or `--frames N` in test runs): a requested quit is
put to `Application.wantsToQuit` at the end of its frame, after WaitForEndOfFrame, each handler in
turn (one that answers false cancels it and the rest are not asked; one that throws is logged and
counts as agreeing); then `OnApplicationQuit` to every active behaviour, `Application.quitting`
(a handler's exception ends it), every scene unloaded (OnDisable/OnDestroy), the graphics device
and window closed,
the log closed, then the process ends with `_exit(code)`: game threads may still be inside
native plugins (SimDLL worldgen), and running the plugins' static destructors under them crashes
or deadlocks. The game registers no `ProcessExit` handlers.

Pending Starts run in the order they were queued, as Unity's delayed-call queue does: a
behaviour is queued when its activation begins, before its Awake (or when it is enabled later),
not by script execution order. Both were measured in the original player: a large save's World
(order 6) starts before Game (order -10), and an object an Awake creates starts after its creator
although it is enabled first. (Loading a save, Game's Awake creates the screens, among them the
new-colony screen; Game starts first and destroys that screen, whose Start would otherwise name
the colony anew and move its saves to a new folder.)

A Start pass runs the Starts queued before it began; those queued meanwhile (objects a Start
creates) wait. FixedUpdate, Update and LateUpdate start a member whose Start is still pending
just before its first call, so it starts ahead of pending behaviours without that message. A
behaviour also takes its place among the update callbacks when its activation begins: a
component that an Awake adds (the game's `MyCmpAdd`) is enabled before its adder but updates
after it. All of this was measured in the original (UnityProbe scenarios 13 to 19). The game
depends on it: SaveLoader loads a save from a Start, so the Duplicants' StandardWorker starts
before their other components and spawns their MinionIdentity before their KPrefabID. A dead
Duplicant thus enters `Components.LiveMinionIdentities` and is removed again once its KPrefabID
restores the Dead tag, and that removal reorders the list.

Destroy is carried out at three points, measured in the original (UnityProbe's destroy phases
and scenario 20): after the fixed steps, after the Start pass that follows Update, and after
LateUpdate's; a call made later in the frame waits for the next frame's Update. The Start pass
comes first, so its Starts still find what Update destroyed, and a pending Start runs on a
descendant that Destroy left enabled before its OnDestroy. Loading a save, Game's Start (just
before its first Update) destroys the new-colony screen, whose CharacterContainers register in
their Start and unregister in OnDestroy.

An Awake that destroys its own GameObject while the object activates leaves nothing of it to
start, measured in the original (UnityProbe's `PROBE_SCENARIO=awakedestroy`): that behaviour and
the ones awake before it get OnDisable at once (the destroying one without an OnEnable), the ones
not awake yet, the object's and its direct children's, still get Awake reporting `enabled =
false`, no OnEnable or Start follows, and all of them get OnDestroy. An object destroyed while
inactive and activated before its destruction gets the same: Awake, disabled, then OnDestroy.
The game depends on it: PlantBranch, from KPrefabID's Awake, destroys a tree branch whose cell
is taken, and a started KPrefabID would start the branch's other state machines without it
(issue #45).

Coroutines are Unity's state machine: an `IEnumerator` is advanced immediately by
`StartCoroutine`; nested `IEnumerator`/`Coroutine` yields wait for the child;
`CustomYieldInstruction` is re-polled each frame via `keepWaiting` (the shared cached
`WaitForSecondsRealtime` instance must behave exactly as Unity's managed implementation does).

### 3.4 Serialization and assets

**Readers (`OniMoono.Assets`)** parse SerializedFile v22 (big-endian header, little-endian
data), UnityFS bundles (LZMA, LZ4/LZ4HC block and blocks-info compression), `.resS`/`.resource`
streaming, and resolve PPtrs (`fileID` → externals, `pathID`). Built-in files map to
`OxygenNotIncluded_Data/Resources/`.

**Native classes** (GameObject, Transform, Material, Shader, Texture2D, Sprite, SpriteAtlas,
Mesh, TextAsset, Font, Canvas, CanvasRenderer, Camera, RectTransform, VideoClip, AnimationClip,
AnimatorController, ParticleSystem, …) are read with the **Unity 6000.3.5f2 release type
trees** (`cache/typetree/InfoJson_6000.3.5f2.json`, vendored into the repo as a compressed
resource), because player files strip type trees. A generic type-tree walker produces a typed
field view; per-class "binders" copy fields into engine objects. Every object must consume
exactly its byte size; a mismatch is an error, never ignored.

**Script classes (MonoBehaviour / ScriptableObject payloads)** have no type tree. We build the
layout from the **actual managed type** following Unity's serialization rules:
serializable fields are instance fields that are public or `[SerializeField]`, not
`[NonSerialized]`, not static/const/readonly, of a serializable type (primitives, enums by
underlying type, `string`, `UnityEngine.Object` subclasses as PPtr, `[Serializable]`
non-abstract classes/structs (generic allowed), arrays and `List<T>` of these (no nested
collections), and Unity's built-in serializable structs: Vector2/3/4(Int), Quaternion,
Matrix4x4, Color, Color32, Rect(Int), Bounds(Int), LayerMask, AnimationCurve, Gradient,
RectOffset, GUIStyle, Hash128, …); depth limit 10; `[FormerlySerializedAs]` irrelevant for
reading (names come from our layout, the data has none). Alignment follows Unity's rules
(4-byte align after bool/byte/char/short runs, after strings and arrays). The exact-size
check over all ~18.8k MonoBehaviours in the build is the proof that the rules are right.

The layout builder is shared by three users: **file deserialization, `Object.Instantiate`
cloning, and `JsonUtility`**. Cloning is serialization-shaped, not a shallow copy: only
serialized fields are copied; references to objects inside the cloned hierarchy are remapped
to the clones; external references are kept; the serializer has no null, so a null string,
array, list or serializable class is `""`, empty or a new instance in the clone (measured in the
original player; issue #17); `ISerializationCallbackReceiver` hooks run
(`OnBeforeSerialize` on the source, `OnAfterDeserialize` on the clone). Non-serialized fields
of the clone keep their constructor/initializer values.
A script's field copy is compiled once per type, as is the copy of each serializable struct
or class and of each array or list element type (`Cloner.CompositeCopier`, `ElementCopier`):
the game's structs (`Tag`, `HashedString`, `CellOffset`) are copied as values, without the
boxes and reflection the clones of resource chunks spent most of their copying on. A struct's
callbacks are called on the struct itself; the copy keeps their changes and the source does
not, as with the box they used to run on.

**Loading model.** Scenes (`level0..2`) are loaded in full on `SceneManager.LoadScene`.
Other files load objects **lazily by PPtr**: an object is materialized the first time it is
referenced, with its dependencies. `Resources.Load` uses the `ResourceManager.m_Container` map;
`AssetBundle.LoadFromFile(Async)` mounts a UnityFS archive as additional files.
`Resources.UnloadUnusedAssets` releases GPU resources of unreferenced loaded assets.

### 3.5 Rendering

**Device: OpenGL 4.5 core via `Brutal.OpenGl` + GLFW.** This build's only graphics API is
OpenGLCore, and every shader ships as GLSL 150 text produced by Unity's HLSLcc, with
per-variant parameter tables (*assets §5*). Executing those programs directly on GL is the most
faithful possible renderer: no shader is hand-ported, so every Klei visual effect keeps its
exact logic. (Vulkan is the alternative BRUTAL emphasises; it would require compiling this
GLSL with relaxed default-uniform-block rules and re-deriving Unity's GL binding model on top.
GL 4.5 is available on Linux drivers and on WSLg hardware via Mesa d3d12 (verified: GL 4.6 core
on an RTX 3090 with `GALLIUM_DRIVER=d3d12`).)

**Shader runtime (`OniMoono.Graphics`).** From `SerializedShader`: properties, subshaders,
passes, tags, render state (blend/ZTest/ZWrite/Cull/ColorMask/Stencil/Offset — each either a
constant or a reference to a material property, e.g. `[_SrcAlpha]`), keyword space, and the
platform-15 program blobs (LZ4). For each pass, variant selection matches Unity's
(local+global enabled keywords → best matching subprogram). Programs are compiled and linked
on first use and cached by (shader, pass, variant). Uniform binding follows the subprogram's
`SerializedProgramParameters` (vector/matrix params by name and array size, textures by name
→ sampler unit, constant buffers with member offsets, built-in `unity_*`/`hlslcc_mtx4x4*`
matrices). Fixed-function GLSL helpers Unity injects (`#ifdef VERTEX`/`FRAGMENT` sections) are
split exactly as Unity's GL device does.

**Engine renderer (`OniMoono.UnityEngine`, Rendering namespace).** Unity's built-in forward
renderer, restricted to what the game uses (*usage §1*):

- Cameras render in ascending `depth`; per camera: `OnPreCull` → cull (layer mask, Renderer
  enabled/visible) → sort (opaque front-to-back by queue, transparent back-to-front along view
  Z for `TransparencySortMode.Orthographic`, ties in submission order) → command buffers at
  their `CameraEvent`s → draw → `OnRenderImage` chain (component order, temporary RTs) →
  `OnPostRender`.
- Culling walks the cull candidates (enabled renderers on active objects, one list per layer)
  of the layers in the camera's mask. Beside each candidate the list keeps its world bounds
  (`CullCache`), made at its first visit and kept until something they were made from changes:
  bounds or local bounds assigned to the renderer, the transform's world matrix (marked in
  `MarkDirty`, so a parent's move counts), the GameObject's components (a filter added or
  removed), the filter's mesh, and that mesh's bounds (to other bits) or destruction; each mesh
  keeps the renderers that read its bounds. Joining the candidates or another layer's list makes
  them again. Renderers whose geometry does not notify (sprites, particles, lines, text meshes)
  have theirs made at every visit, as before. A pass over kept bounds reads the lists' arrays
  alone and looks at a renderer (its flags, then its draws) only when its box is in view; the
  same renderers are drawn in the same order. `ONI_CULL_CACHE=0` makes every visit make them;
  `ONI_VERIFY_CULL_CACHE=1` makes them afresh at each visit too and logs any difference.
- Per-frame buffers are reused only under an owner's rule, each with a verify mode (qa.sh's
  verify stage runs them):
  - A mesh's channel and index arrays are the mesh's own; one it has published is never written
    until the device has run every upload that could hold it, and only then reused. A setter's
    array can be longer than the data (a mesh rebuilt each frame with another vertex count keeps
    its larger arrays, new ones at a size class): `VertexCount` and each submesh's `IndexCount`
    say how much is the mesh's, every reader goes by them, and the getters copy exactly that
    many. `ONI_VERIFY_MESH_REUSE=1` checksums each upload's data when it is recorded and again on
    the GL thread before it is copied. A mesh never drawn (uGUI's shared worker mesh, a
    TextMeshPro text's) keeps up to 32 spares for 120 frames, others 16 for 4: its fills
    alternate between elements of every size. `Clear` keeps the submesh list and its first
    submesh, emptied as a new one is.
  - A canvas renderer's copy of its mesh (`SetMesh`) is the renderer's; its UV, normal,
    tangent and index arrays are also its baked mesh's channels. The copy is let go of when the
    baked mesh moves on to a newer one, and at once when it was never baked (so in no upload);
    never the copy the renderer still has, which a later `SetMesh` of the same mesh unchanged
    takes again. Its arrays, and the baked positions and colours a rebake replaces, go to a
    pool every canvas renderer shares (`CanvasArrays`, by element type and length): a later
    copy of that length, any element's, takes one once every upload of the mesh it was baked
    into, posted by then, has run; one unused for 120 frames is dropped. Most elements rebuild
    less often than the 4 frames a mesh keeps its own spares, or at another length. A channel
    whose values all have the same bits (uGUI's unused UV sets, its default normal and
    tangent, a plain image's colour) is one array that every copy of its length and value
    shares, and never goes back to the pool. The copy is emptied, so a stale reader fails
    rather than read reused arrays, and is the renderer's next copy (only the renderer's
    current and baked copies are compared by reference).
  - A merged canvas batch (a run of draws of the same state, a nested canvas's shared mesh)
    writes into arrays of its mesh's own, from its spares: every value up to the counts is
    written, zeros where a part lacks a UV set the others have.
    `ONI_VERIFY_MESH_REUSE=1` covers both: the uploads' checksums, and the NaN fill of the
    merged mesh's reused arrays.
  - Instantiate's clone map and the lifecycle walks' lists (activation, destruction) are kept
    for the next call; a call started from a callback of another (Awake, OnEnable,
    OnAfterDeserialize) takes its own.
  - A transform's child list (`ChildList`) is an array and a count; a list read keeps the
    count it was read with. Slots below a count once given out are never written again: an
    append writes the next spare slot, and a removal, insertion or replacement makes a new
    array (a parent that had spare slots keeps some). `ONI_VERIFY_CHILD_LISTS=1` checks at each
    append into a spare slot that it and the slots after it are empty.
  - Upload records (mesh and texture), immediate draws' vertex data and the arena's other
    command data belong to the frame they were recorded in and are reused once the GL thread
    has run it; a record carries a generation its command checks.
  - A property block's sheet that a queued draw shares is retired with its render epoch when
    the block moves to another, and a sprite's or a blit's per-frame sheet at once; a sheet
    retired in epoch E is free once epoch E + 2 begins (queued draws are drawn by the end of the
    next epoch, and only draw recording reads sheets). `ONI_VERIFY_PROPERTY_SHEETS=1` holds free
    sheets back longer, marked, and reports a draw recorded with one. A new property block's
    texture store is made with room for an animation batch's textures.
  - A material's values keep a pass state per pass, kept across value changes that resolve it
    to the same bits; `ONI_VERIFY_PASS_STATES=1` resolves each kept one afresh and compares.
  - Passes over a list their callbacks may change (particle systems, cameras, canvases, moved
    rects, pending Starts) copy it into an array the pass keeps (`ListSnapshot`), cleared after
    it; a pass started from inside one gets its own. The phase delegates' invocation lists are
    made once per delegate, the update phases iterate an array of their execution-order lists,
    coroutine waiter lists swap rather than being made anew, and a camera's image-effect list,
    the canvas batcher's run list, the canvas walk's mask renderers and a nested canvas's
    submeshes are kept. `Canvas.willRenderCanvases` and `preWillRenderCanvases` keep their
    handlers in a list (`DelegateList`) with a multicast delegate's rules (append, remove the
    last matching run, call those there were when the call began, stop at an exception):
    every TextMeshPro text joins the first when enabled and leaves it when disabled, which
    copied every other text's handler each time. `GetTemporary` looks its pool up with a
    description it keeps, making one only for a texture it creates. A render texture's
    single-target array is its own (`RenderTargetBinding.Of`), and the GL device records the
    same target object again for the same texture or the back buffer at the same size: the GL
    thread only reads them.
- Draw sources: `MeshRenderer`+`MeshFilter`, `SpriteRenderer`, `ParticleSystemRenderer`,
  `LineRenderer` (mods' lines: its strip as Unity's BakeMesh measures it, corners mitered; rounded
  corners and caps are drawn square), `CanvasRenderer` (world-space and screen-space-camera canvases), and the per-frame
  `Graphics.DrawMesh` queue (cleared after the frame; `camera` argument restricts it).
- Unity's built-in shader constants are maintained per draw/camera (`unity_ObjectToWorld`,
  `unity_MatrixVP`, `_ProjectionParams`, `_ScreenParams`, `_Time`, `_ZBufferParams`, …) with GL
  clip-space conventions as Unity's GLCore device sets them.
- On Windows the original draws with Direct3D 11, whose projection into a render texture has y
  negated (`_ProjectionParams.x` -1) so that the texture's rows come out in OpenGL's order, and
  whose programs are compiled with UNITY_UV_STARTS_AT_TOP. Measured on Windows (U59 and U60,
  `issue-47-missile-trail`'s world shots), the original on Direct3D 11 and on `-force-glcore`
  differ in 17 % of the pixels by more than 40 levels (56 % by more than 8), and ours on OpenGL
  matched the GLCore one exactly. The two causes, from the game's GLCore programs traced from
  their projection products to what their fragment stages read and compared with the Direct3D
  11 programs (all shaders of U59 and U60, the DLC bundles included):
  - *Clip-space coordinates (drawn as Direct3D does).* `Klei/Stars`, the space and nebula
    background drawn into the base camera's render texture, takes its layers' coordinates from
    clip y, so on Direct3D 11 they sit mirrored about the screen's middle row. It is the only
    program that does: ComputeScreenPos (`_ProjectionParams.x`) and ComputeGrabScreenPos
    (UNITY_UV_STARTS_AT_TOP) come out the same on both, and no fragment stage reads its pixel
    position. On Windows the device draws `Klei/Stars` as Direct3D does (`Direct3DClip`): into
    a render texture its program reads the projection with y negated and `_ProjectionParams.x`
    -1, and after its own main its vertex stage turns gl_Position's y back, so only the
    coordinates change. Ours on OpenGL against the original on Direct3D 11 went from 17 % to
    0.5 % (more than 40 levels) on those shots.
  - *The animations' light row (read as Direct3D does).* `Klei/BatchedAnimation` and
    `Klei/BatchedAnimationHuman` turn their pixel's world row into `1 - y / height` in their
    GLCore text only, from a world position their vertex stage gives unturned, so on GLCore
    buildings, plants, critters and Duplicants are shaded by the light at the world position
    mirrored about the world's middle row: lights on screen there (Beakons in a tank above a
    farm) make animations far from any light brighten and darken as they move (issue #76). On
    Windows the device drops that turn (`AnimationLight`): a building's light-grey parts,
    which varied by up to 13 levels on `-force-glcore` and in ours, vary by none, at the
    original's Direct3D 11 levels.
  - *GLCore-only `1 - v` (kept: GLCore parity).* About 16 Klei shaders read the property pages
    `Klei/TexturePage` writes, which the two APIs store turned over, at `1 - v` in their GLCore
    text only. The same coordinates also read textures the CPU uploads, whose rows both APIs
    store alike, so GLCore looks those up mirrored about the world's middle row and Direct3D 11
    does not: `_WorldZoneTex` (SubworldZoneRenderData) in `Klei/GasTest`,
    `Klei/PostFX/LightBufferCompositor`, `Klei/Substance` and `Klei/TiledBlock`, and
    `_ExposedToSunlightTex` (an externally updated PropertyTextures texture) in `Substance` and
    `TiledBlock`. OpenGL keeps the GLCore result, which is what the original draws on OpenGL;
    Vulkan is not in this build. In those shots it leaves space about 10 % brighter than the original on
    Direct3D 11 (27 % of the pixels by more than 8 levels, 0.5 % by more than 40), tiles and UI
    alike. Still to classify (read at the GLCore `1 - v`, source not yet checked): `Klei/Liquid`'s
    `_LiquidDataTex`, `_MaterialDataTex` and `_SolidLiquidGasMassTex`; `LightBufferCompositor`'s
    `_RadHaze1Tex`, `_BlurredTemperature` and `_RadiationTex`; `_LightBufferTex` in
    `Klei/AnimationSimple`, `Klei/Biome/Unlit Transparent`, `Klei/ConduitBall` and
    `Klei/TiledWall`.

  Linux is unchanged: the original there is GLCore. Metal (a macOS port) also renders into
  render textures with the projection flipped and UVs starting at the top, as Direct3D does, so
  both apply there and are to be measured against the macOS original.
- A renderer draws its material's forward passes: those whose LightMode is none, `Always`,
  `ForwardBase` or `Vertex`. A pass keeps its tags in its state (`m_State.m_Tags`, the
  subshader's merged in), and the build stores LightMode upper-case, so values compare without
  case. ShadowCaster, Deferred and ForwardAdd passes are not drawn. The game has no lights, so
  a lit pass (Particles/Standard Surface, the missile smoke trail's) draws dark, as in the
  original (issue #47).
- `RenderTexture` (incl. MRT via `SetTargetBuffers`, temporaries pool, `active`), `Texture2D`
  (CPU copy + `GetRawTextureData<T>` aliasing NativeArray, `Apply`, DXT/BC upload,
  `LoadRawTextureData(IntPtr)`, whose bytes equal to the texture's own make the next `Apply`
  ask for no upload: `RawTextureLoads`), `Texture2DArray`, `Texture3D`, `Mesh` (dynamic buffers,
  submeshes, topologies), `Material`/`MaterialPropertyBlock`/`Shader` globals, `Graphics.Blit`
  (`Hidden/BlitCopy` from `unity_builtin_extra`), `GL` immediate mode, `CommandBuffer`.
- Gamma colour space (`m_ActiveColorSpace=0`): no sRGB conversions.

**Textures for sampling.** Where the driver has `GL_ARB_bindless_texture` (NVIDIA's, for one),
sampler uniforms are set to resident handles of (texture, sampler object) pairs and draws change
no texture bindings: on NVIDIA's OpenGL driver a draw whose vertex stage reads other textures
than the previous draw's waits for it (the game's animation batches each have their own data
textures; in a large colony that was ~33 of 46 GPU ms per frame, 2.5 with handles). Programs are
compiled with `#extension GL_ARB_bindless_texture` and the default `layout(bindless_sampler)`
qualifier ahead of their uniforms; sampling state then lives in sampler objects, and a texture
whose image is specified again is recreated under a new name (a handle fixes its layout).
Elsewhere (Mesa's d3d12 driver under WSL) textures are bound to units, and so they are on AMD's
Windows driver (`GL_VENDOR` "ATI Technologies Inc."), which lists the extension but charges a flat
~0.25 ms of GPU time per draw and per texture upload with handles resident, against
0.001-0.009 ms bound to units (an RX 9070 XT: 270-570 GPU ms per frame on a loaded colony,
26-30 with units; issue #27). Mesa's AMD driver (radeonsi, `GL_VENDOR` "AMD") binds as well:
it adds every resident handle's buffer to each command submission, so the cost grows with the
textures ever sampled, and a save's colony preview, which samples about 4.5 times a normal
frame's textures, left an RX 9070 colony at 20-25 fps from 40-50, paused or not (issue #43).
`--no-bindless` binds on any driver; `--bindless` uses handles on AMD's too. A texture without an
image (zero width or height) is sampled as no texture, (0, 0, 0, 1).

Handle residency (`BindlessResidency`, issue #50): a handle is made resident when a draw first
samples it, and checked (`glGetError`, `glIsTextureHandleResidentARB`); one that fails is never
sampled, and its texture is bound to the sampler's unit with its sampler object instead (a
`bindless_sampler` uniform takes a unit as well as a handle), the same pixels. A failure's
resident count becomes a cap. A texture deleted or given a new name while resident keeps its old
name and resident handles until a fence placed after the frame has signalled, since the
extension leaves open whether making a handle non-resident waits for draws already issued.
Past 8,192 resident textures (`ONI_BINDLESS_MAX_RESIDENT`), the least recently sampled go
non-resident the same way, after a fence, and resident again when next sampled;
`ONI_BINDLESS_IDLE_FRAMES=N` also does that to any texture not sampled for N frames.
`ONI_VERIFY_BINDLESS=1` checks every handle a draw samples and every texture specification
(`bindless handles:` lines, `qa-tools.py verify`); `ONI_BINDLESS_STRESS` measures the driver's
residency limit at start-up. Measurements: [research/bindless-residency.md](research/bindless-residency.md).

`ONI_VERIFY_RAW_LOADS=1` (`RawTextureLoads`, qa.sh's verify stage) checks each
`LoadRawTextureData` against a full copy and each upload it skipped against the texels last
uploaded (`raw texture loads:` summary, every 3,000 loads); `ONI_RAW_LOADS_ALT=1` (measurement
only, with `--frame-stats`) runs every other 60-frame window as before (a full copy and an
upload at every `Apply`), its `raw texture loads` line ending in `variant=0` or `variant=1`;
`ONI_CAPTURE_TWICE=full-uploads` renders each super-sized capture again after every texture
loaded this way is uploaded in full.

**Super-sized captures.** `ScreenCapture.CaptureScreenshot(path, superSize)` renders the frame
again as a screen `superSize` times larger would show it (`FrameRenderer.Capture`), the cameras
only: the original's super-sized captures leave out the overlay UI and IMGUI, even when they fit
one render target. The game renders its world into render textures it makes the screen's size
(the MRT camera's `MRT0`/`MRT1` and their copies, the light buffer, `_LitTex`); for the capture
each such texture gets a device texture of the capture's size (its own is kept aside and put
back afterwards), and the cameras still see the screen's size
(`RenderTextureNative.CaptureLogicalSize`), so the world is drawn at the capture's resolution.
A capture larger than the GPU's textures or than `WindowPixels` renders in windows: bands of the
capture's width a few hundred rows tall, each window's cameras rendered with their projections
narrowed to it (`CaptureWindow.Clip`), the screen-sized textures the window's size, and 64 pixels
around each window rendered and cropped, so that kernels reading neighbours (bloom, the water's
refraction, bilinear reads) see what one target would give them. A band is read into one buffer
and written through `PngWriter` (at level 6: a capture is written in the frame it is taken)
before the next. What the windows need
from the game's shaders is in `CaptureTiles`: `oni_ScreenTile` maps the whole capture's texture
coordinates to the window's. `Klei/Liquid`, which reads `MRT0_Copy`/`MRT1_Copy` where its world
position, `_CameraZoomInfo` and the camera put it on the screen, reads them through it; the
programs that derive screen-space patterns or pixel sizes from their clip position (`Klei/Stars`,
`Klei/Substance`, `Klei/BatchedAnimationShine`, `Klei/PostFX/LightBufferCompositor`'s noise,
TextMeshPro, `UI/Default`) place their vertices in the whole capture and move them into the
window after their own main. The light buffer is rendered once for the whole capture (at most
`LightPixels`), since kanims read it at their anchor, which can lie far outside the window
drawing them, and every `_LightBufferTex` read is moved into it. The game's screen-to-world
vectors of its full-screen passes (`_WsToCs`, `_WsToCcs`) are narrowed to each window.
`ONI_CAPTURE_TWICE=tiles:WxH` renders each super-sized capture again in windows of that size,
for a pixel comparison of the two.

**PNG encoding.** `EncodeToPNG`, `EncodeArrayToPNG` and captures write through `PngWriter`
(`Engine/PngWriter.cs`): each row gets libpng's filter choice (the filter whose output, its bytes
taken signed, sums to the least in absolute value), and the rows are deflated with zlib's
filtered strategy: at level 6 (`PngWriter.FrameLevel`) for captures and for `EncodeTo*` on the
main thread, whose file is written in the frame (U59's Timelapser encodes so), and at level 9
off it (U60's Timelapser encodes on a task). The rows are cut into bands of
about 2 MB, each filtered and deflated on its own pool thread (half the cores, at most 8) and
written as one IDAT ending in a sync flush, so a band's output follows the last one's in the
zlib stream; the Adler-32 is combined from the bands' and written in a last IDAT. Bands fall by
the image's width alone, so the same texels give the same bytes on any machine and thread. An
8-bit RGBA texture or array whose every alpha is 255 is written as RGB (colour type 2): the
original writes RGBA there, but `LoadImage` gives the same texels for both, as does any other
reader. `EncodeArrayToPNG` reads the caller's rows where they are (pinned), with no copy of the
image.

**Render thread, presentation and frame pacing.** Unity's player records rendering on the main
thread and executes it on a render thread (`kGfxThreadingModeClientWorkerJobs` on GLCore), and
presents from there, so GL work and a swap that blocks — vsync, or a display path that reads the
frame back, as Mesa's d3d12 driver does under X11 — overlap the next frame's game work. We do
the same (`GlWorker`): the device's methods run on the main thread and record commands; a GL
thread that owns the rendering context (a hidden window's, shared with the visible one)
executes them. Recording resolves everything a draw reads from engine objects, so the main
thread can change them while the GL thread works: the program variant, the pass state with its
property references, every uniform value (into a per-frame arena), the textures each sampler
gets, and the mesh's layout and submesh range; changed texels and vertex data are copied when
recorded (a non-readable texture hands over its memory, as it keeps no CPU copy). `Present`
hands the frame to the GL thread once the previous frame has been executed, so one frame is in
flight. Work whose result the main thread needs waits for the GL thread: `ReadPixels` and
captures, compiling a program variant the first time it is used, `Shader.WarmupAllShaders`,
diagnostics. `AsyncGPUReadback` of a render texture does not (`GlReadback`): its
`glReadPixels` goes into a pixel buffer with the frame's work, a fence after it; once a frame
the GL thread looks at the fence, and when the GPU has passed it maps the buffer, whose bytes
are the request's data (an 8-bit target read as RGBA32; any other read is read as floats and
converted on a pool thread). A read past 32 MB is first copied on the GPU, the target as it is
then, and the copy read a band of 32 MB a frame: on Mesa's D3D12 driver 400 MB in one piece
held the GL thread about 230 ms. The request is done three frames on, as measured in the
original, or once the texels are there if that takes longer. U59's own code makes no such
request; mods do (Fast Save's timelapse images). `ReadPixels` from an 8-bit target into an
8-bit texture reads bytes, not floats (U59's Timelapser reads its snapshots so). A command
that throws is logged and the frame goes on, as an exception in a draw
used to unwind only that draw. The GL thread renders into an offscreen back buffer (colour
texture + depth-stencil); at present it fences the frame and hands it to a presenter thread
current on the window's context, which waits for the fence on its own thread (neither context's
GPU queue waits on the other's), blits to the window, fences the copy and swaps with
`QualitySettings.vSyncCount` as the interval. Without vSync up to `QualitySettings.maxQueuedFrames`
frames are queued for presentation, at most two (2 by default on PC, as the original reads it,
though Unity documents it for Direct3D 11 and 12 only). With vSync one is, and it counts as queued until a fence placed
after the swap has signalled: the driver returned from `SwapBuffers` at once and queued frames of
its own on top. That fence waiting for the swap is driver behaviour, not a GL guarantee; with it a
key press reached the screen in 50 ms instead of 83, as in the original (19827d58: Windows 11, 60
Hz, windowed, an RTX 3090 on NVIDIA's driver 610.88; other drivers and compositors unmeasured).
With vSync the main thread also waits, after handing a frame over, until the frame
`QualitySettings.maxQueuedFrames - 1` before it (2 by default, as in the original) has been
through the presenter (`QueuedFrames`), as Unity's main thread waits before a frame for frame
*n − maxQueuedFrames* to appear. Without that wait the main thread, the GL thread and the
presenter each held a frame, and a frame's start reached the screen one or two refreshes later
than in the original (docs/research/input-latency.md: 66-85 ms against 48-50 on a small colony
at 60 Hz; 47 with the wait). The wait serializes a frame's stages, so it is on only while they
fit a refresh one after the other, by the pipeline-mode rule of Android's Frame Pacing library
(Swappy): over a sliding two seconds of frames, on once the stages' mean plus 1 ms, and half
again, fits the refresh; off once more than 10% of the frames miss their refresh; the samples
start anew at each change. The stages: the main thread's time to its present, the GL thread's on
the frame and the presenter's until it issues the picture; the refresh is the display's through
DXGI, else the mean time between frames. Always on, it cost a 200-Duplicant colony at about 50
fps 6-25% of its frames in four runs of five. When it turns on, DXGI's next present first waits for one
more picture to retire, once, shedding the picture DXGI queued while it was off.
Three back buffers rotate; the GL thread waits (on the GPU) for a buffer's copy fence before
drawing into it again. Back-buffer reads (`ReadPixels` of the screen,
captures) read the offscreen buffer of the current frame. Pacing follows Unity's desktop
player: with vSync the swap paces frames; otherwise `Application.targetFrameRate` when set;
otherwise none. Without a display (`--headless`, `--batchmode`) frames are paced at
`targetFrameRate` or 60 Hz.

The CPU's fence waits (the presenter's for a frame, the one after a vsynced swap, the stream
buffer's for a ring region) wait 100 ms at a time until the fence signals. A wait still going
after 10 s is logged, and again each minute, as one `[engine] long wait:` line naming the site,
the sync object, the frame where known, the thread and its kernel ID (`LongWaits`); the wait
itself does not change, and one that ends at its first slice costs nothing more. A player once
stopped in a fence wait on WSL's `/dev/dxg` with no word of which. `ONI_TEST_STALL_FENCE=1` makes
the presenter's wait never end, to see the lines. When a SIGTERM's quit misses its deadline
(`QuitSignals`), the log gets each thread's state before the exit (`ThreadDump`, at most 400 ms):
the long waits going on, then each `/proc/self/task` entry's name, state and kernel wait channel.

**Frame deltas with vsync.** Unity (2020.2 on) times a vsynced frame by its presentation, so its
`Time.unscaledDeltaTime` is a whole number of refreshes: in the original on Windows (U59 and U60,
windowed at 59.95 Hz, panning a small colony with the keyboard) 98.7% and 99.3% of 1,199 frames read
within 0.05 ms of one, two or three refreshes. The wall clock between our frames' starts varied by
about ±0.5 ms around the refresh (sd 0.24-0.46 ms against the original's 0.01-0.09), and
`CameraController` moves the view by `unscaledDeltaTime`, so the pan's steps were uneven (sd
0.6-1.1% of a step in steady panning against 0.05-0.3%) over a display that showed every picture for
one refresh. With vsync, `FramePacing` now rounds the wall-clock time since the last frame, plus
what earlier rounding left over, to whole refreshes (at least one), so the deltas still add up to
the wall clock within half a refresh, and `Time.unscaledTime`, `time` and `realtimeSinceStartup`
keep the sums the original's do (its `unscaledTime` is the sum of its deltas; `realtimeSinceStartup`
the wall clock). The refresh period is that of the current mode of the display the swap chain is on
(`DxgiSwapChain`, OpenGL and Vulkan alike: its containing output, matched by GDI name in
`QueryDisplayConfig`'s active paths), looked up again every 300 pictures off the presenter thread,
so a move to another monitor or a mode change is followed within seconds. DXGI's frame statistics
would give the measured period, but Windows updates them only now and then for a windowed swap chain
(in some 20 s runs they stayed on one picture throughout); where they did update, the period they
gave (16.6807 ms) agreed with the mode's 59.951 Hz to 0.003%. The wall clock is used as before
without vsync, without a known period (Linux, OpenGL without DXGI), when nothing has been presented
for a quarter second, for a frame over a second (a hitch or a resume keeps its length and drops the
rounding's remainder; `deltaTime` is capped by `maximumDeltaTime` as before) or under a millisecond
(vsync off whatever the game asked for), both as Godot's DeltaSmoother does, and when eight frames
in a row come in under half a refresh (a faster monitor not looked up yet). Measured after
the change, the same runs (OpenGL on U59, Vulkan on U60): deltas within 0.05 ms of a whole refresh
in 100% of frames, the pan's steady steps even to 0.00%. Below 60 fps the original's deltas are not
a model: on a 200-Duplicant colony (about 21-25 fps in U59's original) they read one refresh for
most frames of 40-47 ms, so the view panned at about 40% of its speed, with deltas of up to 5.9 s
now and then (the step capped at ten times its size); U60's original, at about 15 fps, gave the wall
clock. Ours there (about 38 fps) keeps whole refreshes adding up to the wall clock; how long each
picture stays on screen still depends on when its frame reaches the GPU, which the delta cannot
know: a frame's delta and its picture's time on screen differed by a refresh or more in 24-31% of
the sampled frames before the change and 19-26% after, and the on-screen speed was more than 10% off
in 53-75% of them before and 35-46% after. Variable refresh (G-Sync, FreeSync) gets the same
rounding: measured with G-Sync engaged on a 144 Hz mode (focused, borderless, the 200-Duplicant
colony at 92-103 fps), frames more than 10% off the pan's speed fell from 47-53% with the wall clock
to 32-37% with rounding, and the steady spread from 11-14% to 7%; at a 60 Hz mode the two were within
noise ([research/frame-timing.md](research/frame-timing.md)). Rounding to the refresh's quanta errs
by less than our frame starts jitter, so no variable-refresh guard is needed.

**Presentation on Windows (DXGI).** On Windows the presenter shows the picture through a
Direct3D 11 flip-model swap chain (`DxgiSwapChain`, `DxgiTarget`), on by default (Moono Tweaks'
"Present through Direct3D on Windows", settings.json `"dxgi"`, `--dxgi on|off`). The original
(Unity on D3D11) presents through an 8-bit flip-model DXGI swap chain in the default sRGB colour
space, so Windows knows its frames are SDR. OpenGL presents through WGL with no swap chain
Windows can see; NVIDIA's "Vulkan/OpenGL present method" (Auto by default) may layer it on DXGI,
and Auto HDR or RTX HDR can then take the game for HDR content and push the brightness up (a
user's report: fullscreen forced HDR and maximum brightness). The window is the same in both (a
borderless popup the monitor's size, measured); only the presentation differed. The swap chain:
a D3D11 device on the GL context's adapter (by its LUID: `EXT_external_objects_win32` when DXGI
knows that LUID, else `GL_VENDOR`/`GL_RENDERER` against the DXGI adapters' descriptions; the GPU
option's choice is the GL driver's), `R8G8B8A8_UNORM`, `FLIP_DISCARD`, two buffers, the default
colour space (`RGB_FULL_G22_NONE_P709`), `DXGI_SCALING_STRETCH`, a frame-latency waitable object
at a maximum latency of 2, and `ALLOW_TEARING` when the system supports it, used only without
vsync. The presenter renders the picture into one D3D11 texture, made anew (a quarter larger)
only when the window outgrows it, registered with GL once through `WGL_NV_DX_interop2` for
reading and writing (the splash blends) and locked while the presenter's blit fills it, flipped
(Direct3D's rows run top-down; the splash is drawn flipped too). The swap chain's own buffers are
never registered with GL (re-registering them after a resize is suspected of a use-after-free in
NVIDIA's driver, Godot PR #94503). `CopySubresourceRegion` copies the texture into the back
buffer before `Present` at `QualitySettings.vSyncCount` (0 to 4); with vsync the presenter then
waits on the waitable object until the picture before it retires, two pictures in flight as
Unity's default of two queued frames (with one, a 60 Hz game lost frames on a GPU whose picture
is copied to a monitor on another). Without vsync it does not wait, as SwapBuffers does not:
retiring a picture can take a frame or two (29 ms a picture measured on an AMD iGPU whose picture
is copied to a monitor on the NVIDIA card), which would cap the frame rate; the interop lock
bounds what is in flight.

A size change calls `ResizeBuffers` before the next picture; between the window's resize and that
picture DXGI stretches the last one. A minimized window keeps its framebuffer size (§3.6), so the
buffers keep theirs. Microsoft documents that a flip-model swap chain never returns
`DXGI_STATUS_OCCLUDED`; should one anyway, the picture is taken as not shown and not waited for.
Exclusive fullscreen stays the window's (GLFW sets the display mode, a monitor-sized window), not
DXGI's `SetFullscreenState`, and DXGI is told to leave the window alone
(`DXGI_MWA_NO_WINDOW_CHANGES | NO_ALT_ENTER`). Microsoft's multithreading notes for DXGI list a
DXGI call on a thread other than the window's as a deadlock risk: `Present` may send the window a
message, which the main thread (which may be waiting on the GL thread, which may be waiting on
the presenter) would have to answer; their example is a fullscreen swap chain's mode change,
which never happens here. That `Present` and `ResizeBuffers` send no such message is not
documented; the evidence is measured: the resizes and window switches below (150 each, with 150
minimize/restore cycles) never hung. After start-up every DXGI, D3D11 and interop object is used
and released on the presenter thread alone, and the waits on the waitable object are not
alertable. Start-up makes everything that can fail (the interop extension, the adapter, the
D3D11 device, the registration and a first lock) before it gives the window a swap chain, and a
failure there leaves SwapBuffers in use, with one `present:` log line saying why. A window can
have only one flip-model swap chain at a time (Microsoft's `CreateSwapChainForHwnd` remarks,
which list no error for it); Windows 11 returned `E_ACCESSDENIED` (0x80070005) when it already
had one (`occupied`, measured), and a player's log showed that error at start-up, before any swap
chain of ours, on a laptop with a Ryzen 5600H's iGPU and a GTX 1650, whose built-in screen is
most likely the iGPU's, and in later players' logs on NVIDIA GPUs (#56). The holder was the OpenGL
driver presenting through DXGI itself: with NVIDIA's "Vulkan/OpenGL present method" at "Prefer
layered on DXGI swapchain", set for a test executable alone through an NVAPI driver profile, a
window with our OpenGL context refused our swap chain with that error at start-up on this
machine's RTX 3090 too (measured, 2026-11-05). So on Windows with DXGI on, the visible window is
made without an OpenGL context or pixel format (`GLFW_NO_API`) and only DXGI presents to it; the
presenter thread's context lives on a hidden 1x1 window instead, shared with the GL thread's as
before (the interop registers textures with a context and a D3D11 device, never with a window).
The hidden windows are single-buffered (double-buffered only when the driver has no such pixel
format; nothing presents from them, and OpenGL's SwapBuffers on the visible window stays
double-buffered, the log's `present:` line saying so), and d3d11.dll and dxgi.dll are loaded before them: GLFW clears and swaps a double-buffered
window once as it makes its context, and RivaTuner Statistics Server takes a process that has
called SwapBuffers for an OpenGL one, skips its DXGI presents (its `IgnoreDXGIInterop` default)
and hooks only the Direct3D runtimes loaded when it injects, as windows are made (measured with
RTSS 7.3.7 through its shared memory: before, it attached to OpenGL on a 120x1 hidden window and
showed no overlay, #64; after, to our swap chain at the window's size).
With that setting DXGI then started and presented. The window gets a pixel format, double-buffered,
and a context of its own sharing the presenter's (with the hidden window's pixel format and the
presenter's context itself when that window is double-buffered: WGL takes any window with the
context's pixel format, `Win32GlWindow`), only once DXGI is given up: it could not start, or no
restart is left. Not sooner: a window's pixel format cannot be changed once set, and with that
setting a SwapBuffers on the window gave it the driver's swap chain, after which every restart was
refused with `E_ACCESSDENIED` (measured). While restarts are pending the presenter's pictures go
nowhere (paced by `DwmFlush` with vsync) and the window keeps its last picture. `--dxgi off`,
Linux and macOS keep the context on the visible window, as before. A refused swap chain's log
line names the window's owner thread and process, its styles and size, the adapter showing its
monitor against the device's, and the graphics runtimes and hooks
(overlays, capture, a proxy `dxgi.dll`) loaded before DXGI started. Once the window has a swap
chain, a failure (the chain's own set-up at start-up; while running a lost
device: `DXGI_ERROR_DEVICE_REMOVED`/`RESET`/`HUNG`, a failed lock, resize or present) releases
everything DXGI held and starts it anew. Restarts come in bursts of ten tries, 1, 2, 4 then 8 s apart
(about a minute, outlasting a slow driver reset); a restart that presents for 60 s ends its
burst, so a later failure gets ten tries again. Until a restart succeeds the window keeps its last
picture; once none is left SwapBuffers stands in, which does not reach the screen on a window
whose flip-model swap chain has presented (measured: the window kept DXGI's last picture while
frames went on) unless the driver presents OpenGL through DXGI itself (measured with NVIDIA's
layered present), so a DXGI that cannot restart usually leaves the window still. The `present:`
line at start-up names the adapter and the swap chain's settings; `--dxgi-fault STEP[,STEP]` makes
up a failure at one step, once, for testing
the ways back: at start-up `extension` (no `WGL_NV_DX_interop2`), `adapter`, `device`,
`register`, `swapchain`, `occupied` (not made up: a second D3D11 device gives the window a
flip-model swap chain first) and `association` (the last after the swap chain is made, so DXGI is
restarted); while running `lock:N`, `resize:N` and `removed:N` (the Nth interop lock, resize or
`Present`); `norestart` makes every restart fail too. The host refuses any other step (the
shared `DxgiSwapChain` also lists the Vulkan branch's); with `--frame-stats` the frame counts include the
presenter's time per picture by step, the time from a game frame's start to its picture's
present, and, every 600 frames, the process's video memory on the adapter as DXGI counts it
(only while DXGI presents). Captures and `ReadPixels` read the offscreen back buffers, never the
window, so they are the same either way. With DXGI off, and off Windows, the presenter swaps as
before.

Measured on Windows 11 with an RTX 3090 and an AMD iGPU on U60 (761015, 2026-10-30, the monitor
captured through Desktop Duplication), where this path was built: a paused colony on screen
differed from the original's by 0.29% of pixels through DXGI and 0.31% through SwapBuffers; the
presenter was busy 0.3 to 0.6 ms a picture through DXGI against 0.1 to 0.2 through SwapBuffers; a
light colony without vsync ran at 159 fps through DXGI against 102 through SwapBuffers, and both
held 60 Hz with vsync. With every restart failing (`norestart`), SwapBuffers showed new frames
when the fault came at start-up after the swap chain was made but before it presented
(`association,norestart`), and not when DXGI had presented first (`removed:300,norestart`). On a
530-nit HDR monitor, fullscreen at the menu (tools/HdrCheck), with Windows' HDR on the original
and OpenGL through DXGI left the monitor in HDR, composited as SDR at at most SDR white (240
nits), while OpenGL through WGL switched the monitor out of HDR while fullscreen, a display mode
change the original does not make. With HDR off, Desktop Duplication (which OBS's display
capture uses) lost OpenGL through WGL (`DXGI_ERROR_ACCESS_LOST`) but captured OpenGL through
DXGI. Not measured: Intel GPUs, click-to-photon latency.

Measured again on U59 (744825, 2026-10-31, the same machine, windowed on the second monitor,
captured through Desktop Duplication, which captured every DXGI picture): the paused DeCrew
colony differed from the original U59's by 0.08% of pixels through DXGI and 0.37% through
SwapBuffers (two captures of one run differ by up to 0.07%); the menu differs from the original's
by our own UI only (the Moono Tweaks button, the BRUTAL notice), DXGI's and SwapBuffers' by 2%,
as two captures of the moving menu do. At the menu with vsync the presenter was busy about 0.35
ms a picture through DXGI against 0.05 to 0.1 through SwapBuffers, and a frame's start to its
present took 35 to 36 ms either way. Every `--dxgi-fault` step ended as above: the start-up ones
before the swap chain with SwapBuffers showing frames, `association` and the running ones
(`lock:200`, `resize:1`, `removed:300`) with DXGI restarted on the first try and frames on
screen, `association,norestart` with SwapBuffers showing frames while every restart failed, and
`removed:300,norestart` and `lock:200,norestart` with the window kept on DXGI's last picture.
150 resizes with 150 minimize/restore cycles, and 150 scripted switches between windowed sizes
and a maximized window, showed no failure, no picture the presenter's probe found black and no
growth in the process's video memory. OpenGL stays on the RTX 3090 with `--gpu integrated` here
(both monitors are on it), so the AMD iGPU is not reached through OpenGL on this machine.

Measured with the context off the window (U59 744825 and U60 761654, 2026-11-05, the same
machine, NVIDIA's driver 610.88, a test executable under its own driver profile with "Prefer
layered on DXGI swapchain"): before the change DXGI was refused at start-up (`E_ACCESSDENIED`),
after it DXGI presented (U60's Vulkan, whose window never had a context, presented through
DXGI under the profile too). Under that profile every start-up `--dxgi-fault` step ended with
SwapBuffers showing frames on the window; `association`, `lock:200` and `removed:300` restarted
DXGI on the first try; a restart after two failed ones (a one-off build) started too; with
`norestart` the window kept its last picture until the tenth try, then showed SwapBuffers'
frames. Without the profile, `removed:300,norestart` left the window still, as before. Loading a
colony, panning, fullscreen and back, resizes and minimize/restore cycles showed no failure and
frames after each; `--dxgi off` swapped on the window as before.

**Repeats while no frame comes (DXGI).** A variable-refresh display (G-Sync, FreeSync)
refreshes when a picture is presented, so while the main thread stalls (an autosave, a load
hitch) its refresh rate falls towards nothing, and such panels flicker or dim as it does (#60).
With DXGI presenting, the presenter shows the last frame again while none comes (`StallRepeats`;
Moono Tweaks' "Steady display during saves and loads", settings.json `"stallRepeats"`,
`--stall-repeats on|off`, on by default): the first repeat once no frame has been presented for
three times the usual time between frames (a moving average of the intervals between frames,
each counted up to four times the average, stalls left out), and at least 50 ms; then one every
usual frame time, kept between 1/60 and 1/30 s, until a frame comes, so the display keeps about
the rate it had while the game ran. Three times the usual time, and 50 ms, lie beyond the spread
of frames in play (on a 200-Duplicant colony at about 95 fps 16 of 26,398 frames took longer, each
a hitch already), so frames that come as usual never meet one, a display's own low-framerate
compensation covers shorter gaps, and the presenter's wait for the next frame, untimed
before, is only timed. A repeat goes only when the frame-latency waitable says the last picture
has retired (else it is tried again 2 ms on), so it never queues behind a picture nor blocks in
`Present`; it is the shown back buffer blitted into the shared texture again and presented at sync
interval 0 without `ALLOW_TEARING`, which the flip model discards when a newer picture is queued
before it is shown (`IDXGISwapChain::Present`: "discard this frame if a newer frame is queued"),
so a frame that comes just after one is shown at the vertical blank it would have had. Nothing
waits after a repeat; the next frame's `Present` counts it as in flight while it has not retired,
waiting after its own `Present` (at most a refresh) as frames wait for their predecessor, so as
many pictures are in flight as without repeats. A frame handed over during a repeat is not held
back (a repeat does not count as queued); the GL thread only never moves on to the buffer a repeat
reads, which would take two frames handed over during one. Repeats are kept out of frame pacing,
`QueuedFrames`, the present count, the frame stats' pictures (counted on their own as repeats),
the present trace's probe and the `--dxgi-fault` steps. The waits between them need a 1 ms timer
resolution (Windows' default is 15.6 ms), raised with `timeBeginPeriod` for a run of repeats only.
OpenGL's SwapBuffers has no repeat: a swap there cannot be discarded by the next, so a frame
right after one would wait a refresh.
Measured on Windows (a 200-Duplicant colony at speed 3, vsync on at 60 Hz, five launches with
repeats and five without, 165 s of play each from 35 s after the load): no repeat in frames that
came as usual, one at most a launch, each in a hitch of 70 ms or more; game pictures 56.6 to
59.0 a second with repeats and 53.7 to 59.1 without (one launch in each a slower 53.7), the
slowest 1% of frame intervals 33 to 35 ms against 31 to 34 (42 in both slow launches), and
`Present` 0.045 ms either way. Under five forced 1.5 s stalls and three autosaves, the longest
time without a picture went from 1.5 s to 55 to 93 ms (repeats every 17 to 28 ms after the
first), at vsync 1 and 0 alike, and the first frame after a stall was presented 23 ms after it
on average with repeats and 22 ms without. A garbage collection still leaves a gap: it stops the
presenter thread too: a 0.33 s collection in play left a 0.33 s gap, and the load, whose
collections pause 3.6 s in all, gaps of up to 0.87 s.

**Loading splash (`--splash moo`, on by default; `--splash none` or Moono Tweaks turns it off).** The player's own presentation, not the
game's: the presenter thread draws Gassy Moo comets (the game's `meteor_gassymoo` kanim, its
`fall_loop`, read from `sharedassets0.assets` on a worker thread at start-up) over whatever the
window last showed, redrawing about 60 times a second while no game frame comes, so the window
keeps moving while the main and GL threads load. Game frames are not changed: the comets are
drawn into the window after a frame is copied there. Once 30 frames in a row arrive within
100 ms of each other (the main menu), the rain fades out for good. A thin bar under it shows how
far start-up is (`StartupProgress`): it passes checkpoints, the engine's own (window, first scene,
first frame, each content bundle, the asset unload, the menu) and the game's, recognized from its
log lines (Steam ready, `Global`'s start, the mod system, localization). The checkpoint times of
each launch are kept in the player's cache folder. Between two checkpoints the bar eases towards
the next at the last launch's pace (about 80% of the way by the expected time, then creeping on,
never reaching it before it is passed), and while the first scene loads it moves by the objects
loaded; the drawn bar follows that with a 0.25 s smoothing, so it never stalls or jumps.

**Window messages during long frames.** The frame polls the window's messages at its start, as
Unity does, but a start-up frame takes seconds, and Windows marks a window that has not taken its
messages for 5 s as not responding. Long engine steps (loading an object, instantiating, adding a
component, a log line, recording uploads, waiting for a bundle's decode) therefore pump them at
most every 100 ms on the main thread (`MainThreadPump`, `ONI_PUMP_EVENTS=0` turns it off).
Nothing a pump receives reaches the engine or the game within the frame: GLFW's callbacks (keys,
characters, mouse buttons, movement, scroll, cursor entering, close, framebuffer size, window
position, monitors) are kept in order and replayed at the start of the next frame's poll, just
before the messages that came since, so input state, `Event`s, `Screen` sizes and focus change
only at frame start, as before. The pump calls no game code, and no engine code but GLFW's own.
Native work inside game code (FMOD loading its banks) cannot be interrupted: those stretches stay
unpumped.

Textures loaded from asset files are uploaded as part of loading, as in Unity, not on first
draw: first draws of screens with many textures (the research tree) would otherwise hitch for a
second and throw off the game's time-based UI animation. A non-readable texture loaded on the main
thread is handed to the background uploads (`GlBackgroundUploads`) as it loads: worker threads
read its texels from the asset file, and the GL thread uploads one texture at a time whenever it
has no frame to execute, in load order, with a fence keeping the GPU no more than 34 MB behind.
A frame therefore waits at most for one texture's upload, and loading goes on meanwhile (the first
scene's 2.1 GB of animation textures go up while the game starts up; a DLC bundle's while the next
bundles load). A draw that samples a texture still queued takes it back and records its upload
into the frame ahead of the draw, as every upload was recorded before; so does a change to the
texture, and a release drops it. The texture gives up its texels when queued (a non-readable
texture keeps no CPU copy once uploaded, and the game cannot read it). Readable textures, and
textures loaded on other threads, are uploaded by the main thread before the next frame renders;
when many are pending at once their texels are read on worker threads first and the uploads go
to the GL thread in batches. `ONI_BACKGROUND_UPLOADS=0` turns the background uploads off.

What scripts make is put on the device ahead of its first draw (`GlAhead`): a script's new
texture's storage (`new Texture2D`, up to 16 MB) and the buffers and data of a mesh given data
that no draw has used yet, on the GL thread while it has no frame to execute, or for a
millisecond before each frame when it never runs out. A texture's first upload then fills that
storage (or makes it, if it comes first); a mesh's first draw posts the queued upload into its
frame ahead of the draw when it has not run, so it runs once and before any later upload, and a
released mesh's never runs. A texture made ahead records its first upload when the script applies
it (as Unity uploads at `Apply`), and once 32 MB of uploads are recorded the frame so far goes to
the GL thread if it has nothing left to execute, so a view's first sight of much (a colony
preview after a load) uploads while the main thread goes on. Mesh buffers up to 64 KB take the
dynamic usage hint: Mesa's D3D12 driver took about 0.3 ms to make a small static one.
`ONI_AHEAD_OF_USE=0` and `ONI_UPLOAD_FLUSH=0` turn these off.

Per-draw uniform blocks are streamed through a ring buffer that stays persistently mapped for the
device's lifetime (`GlStreamBuffer`, ARB_buffer_storage): data is copied into the mapping, blocks
are bound by range, and a fence per frame keeps a region from being reused before the GPU has
consumed it; the storage is never respecified (no orphaning). Texture content updates into
storage of unchanged size and format (`Apply` on the game's per-frame property textures) use
`glTexSubImage2D` from client memory. Routing them through a pixel-unpack buffer (orphaned or
persistently mapped) halved their cost but corrupted textures on Mesa's d3d12 driver in long runs
(white sprite-atlas pages, world-sized garbage triangles, device removal), so it is not used.

**UI rendering.** `Canvas` implements Unity's canvas batching: per root canvas, walk
`CanvasRenderer`s in hierarchy order, apply `CanvasGroup` alpha, `RectMask2D` clip rects
(`_ClipRect` + `UNITY_UI_CLIP_RECT`) and stencil masks (materials from uGUI's own
`StencilMaterial`), and merge draws of the same state into one mesh.
An overlay canvas's draws are first reordered as Unity's batcher orders them, where it cannot
change a pixel (`CanvasLayering`): each draw gets a layer above the earlier draws of other
states whose pixel centres it shares, or the layer of an earlier draw of its own state it
overlaps; then a stable sort by layer and by the state's first appearance in it, then the
merge. Two draws that share no pixel can swap, since blending, depth and stencil act per pixel;
two that share one keep their order. A draw's pixels are its baked mesh's bounds on the screen
(pixel centres, an eighth of a pixel of slack); a draw off the screen reaches none and joins the
first draw of its state. Only shaders whose vertex programs place each vertex where the mesh
puts it (UI/Default, Klei's UI/Default Desat and chart lines, and TextMesh Pro's, read in the
game's programs; TextMesh Pro's `_VertexOffsetX/Y` set makes a barrier) may move; a stencil write (a `Mask`'s graphic), a pop
and any other shader are barriers that nothing crosses. Draws in a nested canvas's space join
only each other. Overlap tests go through a grid of 64-pixel cells, and the order is kept on the
canvas while every draw's mesh bounds, material, sheet and transform stand.
Camera and world-space canvases, and super-sized captures, keep the hierarchy order.
`--no-ui-reorder` or `ONI_CANVAS_REORDER=0` turns it off; `ONI_VERIFY_CANVAS_ORDER=1` checks
every pair of draws that share a pixel by their vertices, every 60th order a canvas uses, and
that a kept order matches a new one; `ONI_VERIFY_CANVAS_PIXELS=1` draws the overlay canvases
again into a target of their own, in hierarchy order and reordered, every 120th frame, and
compares every pixel (with merged meshes made for the check: a kept mesh made again for another
order within a frame changes under the draws already queued).
A renderer with `cullTransparentMesh` on (the default for one added at run time) whose mesh has
every vertex alpha below 0.001 is not collected, as measured in the original
(`docs/cleanroom/ugui.md`, "CanvasRenderer.cullTransparentMesh"): it is still numbered and still
takes raycasts, but its geometry is neither baked nor batched. Each geometry `SetMesh` copies
answers the test once. `ONI_CANVAS_CULL=0` turns it off; `ONI_VERIFY_CANVAS_CULL=1` tests every
vertex again at each build and logs any difference.

**UI raycasts.** A `GraphicRaycaster` tests every raycast target of its canvas each frame: its
flags, whether it sits under a closed screen, its rect at the pointer, then its raycast filters.
It keeps the graphics that reach the filters and reuses them while nothing read before the
filters has changed (`RaycastInputs`): the pointer position, the canvas's graphic list, camera
mapping and far plane; the canvas object's hierarchy version, RectTransform moves and resizes at
or below it (counted in `MarkDirty`) and world matrix; each object above it (its own changes and
the walk's closed-screen mark); the numbering of each sort root above it; and a global version
for `raycastTarget`, `raycastPadding`, `cull` and the like. Keys are per canvas because the game
keeps all its UI under one object that changes every frame. A reuse still asks those graphics'
filters, which may read anything, and sorts by depth as before. `ONI_RAYCAST_CACHE=0` turns it
off; `ONI_VERIFY_RAYCAST_CACHE=1` makes each reused pass afresh too and logs any difference.

### 3.6 Platform

`OniMoono.Desktop` wraps `Brutal.Glfw`: one window with a GL 4.5 core context, vsync,
resize, fullscreen/windowed modes (`Screen.*`), monitors/resolutions, DPI, keyboard
(GLFW key → Unity `KeyCode`; character keys through the active layout, as the game's Input
Manager has "Use Physical Keys" off: `KeyboardLayout`, measured on AZERTY), text input (whole code points through our own GLFW character
callback, as BRUTAL's cuts them to 16 bits), mouse buttons/position (bottom-left origin)
and scroll, hardware cursors from `Texture2D`, clipboard, focus events. Legacy Input Manager
axes/buttons come from `globalgamemanagers` `InputManager` settings, with Unity's gravity /
sensitivity / dead-zone smoothing.

On X11 a hardware cursor keeps its texture's size, as in Unity's Linux player: with Klei's 32x32
`cursor_arrow`, XFixes read the shown cursor in both players as 32x32 with its hotspot at (2, 2),
the same pixels, with `XCURSOR_SIZE` unset, 48 or 64 and with `Xcursor.size: 48` in the display's
resources (on Xvfb). On Windows `Win32Cursor` scales it with the pointer size, as the original
does (issue #18); Wayland compositors are unmeasured.

On Windows the window takes IME input only when `Input.imeCompositionMode` asks for it
(`ImeInput`): `On`, which a focused TextMeshPro or uGUI field sets, or `Auto` with an IMGUI text
field focused. Otherwise its input context is taken away, so an IME such as Microsoft Pinyin
leaves the game's hotkeys alone (issue #30). Measured in the original with the Chinese layout on
its window and UnityPlayer.dll's imm32 calls logged: `ImmAssociateContext(hwnd, NULL)` outside
text fields, `ImmAssociateContextEx(hwnd, NULL, IACE_DEFAULT)` in one, applied at the window's
next focus, layout or key message; ours applies a changed mode at the start of the next frame's
event poll, before the messages since are read, and applies it again, changed or not, when the
window gains the focus or its input language changes (`Win32Ime`: `WM_SETFOCUS`,
`WM_INPUTLANGCHANGE`). Applied only as it changed, a player with a Chinese IME reported W, A, S
and D doing nothing until Caps Lock was toggled; the original's re-applying at key messages is
not copied.
Committed IME text arrives as typed characters; ours reports no composition string (the
original's TMP fields show the composition inline, ours leave it to the IME's own window), and
X11 input methods are untouched (none was available to measure).

A minimized window keeps the size it had, as in Unity's player: Windows reports its framebuffer
as 0x0, but `Screen.width`/`height`, `Display.renderingWidth`/`Height` and the cameras' pixel
sizes stay what they were, and frames go on rendering at that size until the window is restored
(measured in the original, windowed and fullscreen; issue #29, where the game's
`LightBufferCompositor` asked for a temporary of `Screen.width / 8` pixels). Minimized, a
Windows window sits at -32000,-32000, on no monitor; it keeps the monitor it was on.

A fullscreen window (`FullScreenWindow`, the game's Fullscreen option) at a resolution smaller
than the monitor's renders at that resolution and stretches it over the window, as Unity's player
does. Measured in the original Linux player on a 1280x720 Xvfb screen (`Screen.SetResolution`,
and the Graphics options' Apply): at 960x720 `Screen.width`/`height` and
`Display.renderingWidth`/`Height` are 960x720, `systemWidth`/`Height` and
`Screen.currentResolution` stay 1280x720, `Screen.resolutions` is unchanged, and so were 1024x576
and 800x600; a size larger than the monitor is cut per axis (1600x600 gave 1280x600). The picture
fills the whole screen, its aspect not kept (no bars), bilinearly filtered (the screen at
1024x576 matched a bilinear stretch of the capture to a mean of 0.04 per channel, nearest-pixel
1.26), and the pointer maps into the rendered frame by the same stretch (`Input.mousePosition`
was (120.75, 718) with the pointer on the screen's pixel 161,1 at 960x720). Captures and
`ReadPixels` of the screen return the rendered size. Ours keeps the window the monitor's size
(`GlfwPlatform.WindowFramebufferWidth`/`Height`, what the presenter fills) and renders at the size
asked for (`FramebufferWidth`/`Height`, which `Screen` and `Display` report); the presenter's
copy to the window (OpenGL's blit, into DXGI's shared texture too)
stretches it with linear filtering, as it already did for a frame drawn before a resize, and
`ToPixels` maps the pointer with `WindowToRendering`. On Windows (a 2560x1440 second monitor,
`SetResolution(1280, 720, FullScreenWindow)`) the original kept its window 2560x1440 and reported
1280x720 for `Screen.width`/`height` and `renderingWidth`/`Height`, 2560x1440 for
`systemWidth`/`Height`, and, unlike on Linux, 1280x720 for `Screen.currentResolution`
(`GlfwPlatform.CurrentResolutionOf`); ours matches, through DXGI and SwapBuffers. A fullscreen
window opened at start-up renders at the monitor's size (the game applies its saved resolution
on its first frames). Not measured: the pointer's mapping on Windows, exclusive fullscreen (on Linux the original reported
`ExclusiveFullScreen` with a centred 960x720 window; ours keeps a fullscreen window there), and
whether Unity scales a hardware cursor with the picture (ours does not).

Known limitations, not handled:
- X11 layouts with several groups (us,ru and a group switch) are unmeasured in the original;
  ours maps keys through the group in effect at each press.
- Wayland has no window positions (`-monitor`, centring and restore placement do nothing there),
  no window icons, and a layout's first level only for key codes; the original's Wayland app ID
  is unmeasured.
- On X11 a fullscreen window is an undecorated monitor-sized window, without
  `_NET_WM_STATE_FULLSCREEN`, so a window manager may keep its panels above it.
- Outside the window the pointer is read each frame (`FollowCursorOutside`); over another window
  that covers ours it is inside our client area, where GLFW reports no movement, so
  `Input.mousePosition` stays where the pointer was last over ours.
- At start a windowed window's client area is centred on its monitor (not its frame, as for a
  restored window: about 11 pixels higher at 100% scaling). The original on Windows starts where
  it saved its window last (its registry key's `Screenmanager Window Position X/Y`: measured with
  the U59 copy, which held 0,0, as a 1024x768 client at 2561,31 on a second monitor at 2560,0);
  ours saves no position, and the original's first start, with none saved, is unmeasured.

### 3.7 Text and fonts

TMP runs as shipped. The engine provides what it needs: `Font`, `TextCore` data types,
`FontEngine` used only for dynamic atlases, and `TextGenerator` for the legacy `UI.Text`
(debug/mod screens).

- **FreeType** is BRUTAL's build (`Ahwoo.BRUTAL.FreeType`: `libfreetype.so` / `freetype.dll`)
  through our own P/Invokes (Unity links FreeType statically; the game ships none); its records'
  C `long` fields are `CLong`, 64-bit on Linux and 32-bit on Windows. OpenType layout data FreeType does not expose (GPOS
  `kern`/`mark`/`mkmk`, GSUB `liga`, GDEF classes, legacy `kern`, OS/2) is read from the sfnt
  tables in managed code.
- **Atlases**: Unity's MaxRects packer over TMP's free/used rect lists (glyph + padding on each
  side); SDF modes rasterize the outline (×8/16/32 supersampled for `SDF8/16/32`) and run
  Gustavson's anti-aliased EDT; the field is 0.5 on the outline with spread = padding + 1
  (TMP's `_GradientScale`), measured against the game's own SDF atlases.
- **Legacy fonts** (class 128) render glyphs on demand into the font's own `Font Texture`
  (Alpha8, from 256², packed on a skyline, grown when a rebuild with the current frame's glyphs
  still overflows) and raise `Font.textureRebuilt` on rebuilds. Glyphs come from a chain of
  faces, each glyph from the first face that has it (`FontNative.Chain`, docs/cleanroom/ugui.md
  "Legacy Text glyph placement"): a font's own data, else (the built-in `LegacyRuntime`, fonts
  from `CreateDynamicFontFromOSFont`) the first installed OS font of `m_FontNames`, then on
  Linux FreeSans, DejaVu Sans and Liberation Sans, as measured; with real bold and italic
  faces. Off Linux (unmeasured) the last link is the built-in Liberation Sans from the game's
  own font asset, which the TextCore font engine also loads for data-less fonts. Glyphs are
  rendered unhinted at whole-pixel sizes, with whole-pixel advances and a one-pixel padded
  box; the generator rounds line metrics, line starts and the block's top to whole pixels, and
  kerns by the font's serialized `m_KerningValues`.
  `Resources.GetBuiltinResource<Font>("Arial.ttf")`, the font's name before Unity renamed it
  (LegacyRuntime's `m_FontNames` is still `["Arial"]`), gives LegacyRuntime: it is the only font
  in `unity default resources`, and the original gives mods that ask for Arial (the only built-in
  name the Workshop builds in `out/ws` ask for) a font without an error. A dynamic font is laid
  out at the text's size times the canvas scale, rounded down to whole pixels (measured in the
  original: a 10-point Text on the game's 0.86 canvas is as wide as at 8 pixels).

### 3.8 Audio, video, other modules

- Audio: FMOD through the game's own `FMODUnity` + native `libfmodstudio.so`; the engine
  supplies what FMODUnity needs (paths, Transform, components, Timeline/Playables types). On
  Windows FMOD allocates through OniMoono.exe's `fmod_heap.c` (game tweak `fmod-quarantine`,
  on by default): size-class slabs in reserved address space, with every block's state, size
  and free list in side tables away from the blocks, and a quarantine for freed blocks, against
  the write to freed memory FMOD's staff acknowledged
  (qa.fmod.com/t/hard-crash-in-fmodstudiol/24589), the heap corruption of issues #72, #73, #58
  and #78. A late write can change only FMOD's data or a freed block's fill, never bookkeeping
  that the allocator or Windows trusts. Native, since FMOD calls it on its own threads; its
  `fmod heap:` lines in Player.log report each late write it catches (docs/TWEAKS.md, "The
  sound memory guard").
  Game tweak `fix-released-sounds` (on by default, managed, every platform) removes
  the cause found behind #78's writes: the game keeps the handles of event instances it has
  released at two places (an ambience layer's `Stop`, `LoopingSoundManager.StopAllSounds`) and
  goes on calling them, FMOD's 11-bit handle serials wrap, and the call lands on whatever object
  has that handle now. Prefixes replacing those two methods clear the handle as it is released
  (docs/TWEAKS.md, "The released sounds fix").
  Game tweak `fmod-steal-fix` patches FMOD's own write to a freed instance (a bus steal of an
  instance still loading) in memory, after `RuntimeManager.Initialize`, only for the FMOD
  version, file hash and bytes it was written for (docs/TWEAKS.md, "The FMOD steal fix").
- Video: `VideoPlayer` decodes VP8 WebM from `resources.resource` with our own EBML demuxer and
  our own VP8 decoder (`Video/Engine/Vp8`, a port of RFC 6386's reference decoder; VP8 decoding is
  exact by specification, and every frame of the game's 18 clips decodes to the same planes as
  libvpx, which the tests use as reference) on a decode thread per player, so no codec library
  is needed on either platform. Frames are converted to RGBA on the CPU with the exact formula of the build's own
  `Hidden/VideoDecode` shader (BT.601 limited range, bilinear chroma) into a CPU `Texture2D`
  that the device uploads; a GPU path running that shader on the planes can replace the
  converter. The game's clips have no audio tracks (and the game sets `audioOutputMode = None`);
  video audio is not played.
- `Animator`: AnimatorController state machine + AnimationClip curve evaluation for the
  properties the game animates (UI). `ParticleSystem`: the modules the prefabs actually enable,
  and for systems mods build by script `Emit(EmitParams, count)`, the velocity limit and z
  rotation over lifetime, as measured (`tools/UnityProbe`, `particleemit`). Features no game data
  uses (in mods' bundles: clamp velocity, rotation, collision, lights, sub emitters, other shapes
  and texture-sheet or trail variants…) are not simulated; the system loads without each and logs
  it once (`[engine] particles: … is not simulated`).
- `UnityWebRequest` → `HttpClient`; `ImageConversion` → stb_image / stb_image_write
  (`Ahwoo.BRUTAL.Stb`); `JsonUtility` → the shared layout builder; `Physics.Raycast` → the
  colliders present in data; IMGUI → clipboard and the event/GUI surface the code paths touch.
- 2D physics is not simulated: no step moves a `Rigidbody2D` and no contact or trigger message
  (`OnTriggerEnter2D`…) is sent. The game creates no 2D bodies; for mods that do, the bodies'
  and colliders' settings are kept as given (`bodyType`, `mass`, `gravityScale`, the dampings,
  `isTrigger`, `CircleCollider2D.radius`, `Physics2D.IgnoreLayerCollision`'s matrix…), so their
  prefabs build (Twitch Integration's glitter pufts, Chaos Events for Twitch Integration's Omega
  Sawblade). `Rigidbody2D.AddForce`, which only a step could honour, is not implemented: Chaos
  Events' sawblade fails with MissingMethodException once its event homes in. Unity's internal
  binding attributes (`UnityEngine.Bindings.NativeHeaderAttribute` and the like, in
  `SharedInternalsModule` and `CoreModule`) exist with Unity's constructors, so a mod that merges
  copies of Unity's modules into its own assembly (Twitch Integration carries
  `InputLegacyModule` and `TextRenderingModule`) has its types' attributes resolve; before, the
  first one stopped Harmony's `PatchAll` (TypeLoadException) and KMod disabled the mod.

## 4. Threading

The game relies on release-player leniency (*runtime §6*): worker, path-prober, worldgen,
HTTP and FMOD threads call into the engine. Our rules:

1. **Free-threaded:** math/value types; `Application` path and platform getters (immutable
   after startup); `Object ==`/`!=`/`bool`/`name`/`GetInstanceID`; `Component.transform` /
   `gameObject`; `GetComponent(s)`; `Transform.position` and other transform **reads**;
   `TextAsset.text`/`bytes`; `Resources.Load` (loading is serialized by a lock and completes
   on the calling thread, as the game's lazy `Db.Get` needs); `Debug.Log*`.
2. **`Debug.Log*` from other threads** invokes `logMessageReceivedThreaded` alone, on the
   caller, and never `logMessageReceived`, later or queued (measured with UnityProbe's
   `logthreads` scenario in the original, for a `Thread`, a thread-pool item and a `Task`; so
   too for an exception nothing catches on a thread, which D16 logs there). A message logged on
   the main thread invokes `logMessageReceived` and then `logMessageReceivedThreaded`, at once.
   The callbacks run before the text is written; a handler's exception ends the call (the
   handlers after it, and after a `logMessageReceived` handler's the threaded event too) and is
   written as a logged exception's text, ahead of the message's, reaching no callback; a message
   logged inside a handler is written and reaches no callback. KCrashReporter listens on
   `logMessageReceived`, so worldgen's errors on its threads open no error dialog.
3. **Main thread only** (asserted in all builds, the failure is an exception with a clear
   message, as Unity's "can only be called from the main thread"): structural mutation
   (`Instantiate`, `AddComponent`, `Destroy`, `SetParent`, transform setters, activation),
   rendering and GPU resource APIs, scene loading.
4. Worker-thread writes into `GetRawTextureData` arrays are allowed (plain CPU memory until
   `Apply`). GL calls happen only on the GL thread, apart from the presenter thread's copy
   and swap on the window's own context (§3.5); the device records them on the main thread.
5. Reverse P/Invoke: SimDLL's error callback is created by game code per call; exceptions
   crossing it are fatal on CoreCLR, which matches Unity's crash behaviour on that path.

## 5. Numerics

`Vector2/3/4`, `Quaternion`, `Matrix4x4`, `Color`, `Mathf`, … are implemented with Unity's
formulas (including `Mathf.Approximately`, `RoundToInt` banker's rounding, `Random.Range`
inclusive/exclusive rules, `Quaternion.Euler` order ZXY). Results follow the original bit for
bit where that was measured and matters (D25 lists them); elsewhere the aim is algorithmic
equivalence. The runtime's float rounding differs from Unity's Mono (D25) and so does its number
formatting (D2).

## 6. Configuration and paths

`Application.dataPath` = `<game>/OxygenNotIncluded_Data`, `streamingAssetsPath` =
`<dataPath>/StreamingAssets`, `isEditor` = false, `isPlaying` = true. On Linux,
`persistentDataPath` = `~/.config/unity3d/Klei/Oxygen Not Included` (Unity's Linux rule:
`$XDG_CONFIG_HOME/unity3d/<company>/<product>`) and `platform` = `LinuxPlayer`. On Windows the
player is the Windows one: `platform` = `WindowsPlayer`, `persistentDataPath` =
`%USERPROFILE%/AppData/LocalLow/<company>/<product>` (the LocalLow known folder),
`temporaryCachePath` under `%TEMP%`, all paths with forward slashes as Unity reports them; the
game itself keeps saves, mods and its settings under `Documents\Klei\OxygenNotIncluded`
(`Environment.GetFolderPath`). The Windows build is the same game build as the Linux one
(ChangeList 744825) and its shaders carry OpenGL programs next to the Direct3D ones, so the same
device runs its data. Natives resolve per platform (`Plugins/x86_64/<name>.dll`, then
`Plugins/<name>.dll`, on Windows; on Linux the first `lib<name>.so` anywhere under `Plugins/` in
ordinal path order, as Unity's Linux player loads and binds them, so a folder Steam left of
another branch's build hides nothing (#49); and for the macOS bundle the executable of
`Contents/PlugIns/<name>.bundle`: `GameLayout`, which tells the layout from the install's files;
macOS is groundwork only, `docs/research/metal-backend-design.md`); Steamworks.NET is built per runtime identifier (library name
and callback packing differ); FreeType comes from BRUTAL's package on both. Company/product/version
come from `globalgamemanagers` PlayerSettings. The host takes `--game <dir>` (default
`~/projects/oni-linux`), `--headless` (no window, no GL: logic-only frames for tests),
`--frames N`, `--capture FRAME:PATH` (§7), and `--log <file>`.

The host directory carries a `Restarter` (`Restarter.exe` on Windows, a .NET program), as Klei's
player directory does: `App.Restart` (language change) starts it with the player's executable
path and quits. Ours waits for the player to exit and starts it again with the arguments it was
started with (recorded by the host in `ONI_MOONO_RESTART_ARGS`, shell-quoted, and
`ONI_MOONO_RESTART_ARGV`, JSON, with `ONI_MOONO_RESTART_PID`). From U60-761654 the Windows
`App.Restart` also passes the player's arguments (`Environment.GetCommandLineArgs()[1..]`, each in
quotes as it is, so one holding a quote or ending in a backslash does not survive); Klei's
761654 `Restarter.exe`, measured, starts the executable with them at once (no wait for the old
process), in the executable's folder, with .NET's quoting (`ArgumentList`). Ours takes
`EXECUTABLE [ARGUMENT...]`: the recorded arguments win, the command line's are used without them
(`RestartCommand`, quoting as Klei's does), and it keeps the player's working folder, so a
relative `--game` still resolves. The Linux `App.Restart` still passes only the executable.
Since the game's `GetCommandLineArgs()` gives the game's arguments (below), the arguments
`App.Restart` passes are those; the recorded ones, the host's whole command line, start the
player again with the same options and the same game arguments.

**The game's command line (#53).** In the original player `Environment.GetCommandLineArgs()`
gives the game's executable (its full path) followed by every argument the player was started
with, in order: Unity's own (`-screen-width`, `-logFile`) and ones it does not know (`-foo bar
--baz=1 -moddev`) alike, so mods read flags of their own there (measured with
`tools/CommandLineTestMod` in the 761654 Linux and Windows players,
`tests/e2e/issue-53-command-line.yaml`). Neither 744825's nor 761654's Assembly-CSharp reads it;
761654's firstpass reads it for `App.Restart` only. Ours used to give the host's: `OniMoono.dll`
and our own options, `-- %command%` included, and stopped at an unknown `--` option after the
game's executable. Now the host sets the runtime's command line (`GameCommandLine`, through
Environment's `s_commandLineArgs`, which `Environment.CommandLine` is built from too) to the
game's before any game code runs (`Options.GameArguments`):
- the executable: the one Steam's command names after `--`, else the game folder's
  (`OxygenNotIncluded.exe`, `OxygenNotIncluded`, or the Mac bundle's);
- in the Steam form, every argument after the game's executable, as it is. Ours and Unity's
  among them are still read as before; an unknown `--` option there no longer stops the player;
- before `--` and in the direct form, Unity's player arguments and stray words with their
  values (anything that does not start with `--`), and each `--game-arg ARG` (any argument,
  `--baz=1` too). Our own `--` options never reach the game, and an unknown one there still
  stops the player with "Unknown option", so a mistyped option of ours is not silently passed on.

The log has a `game arguments:` line with the list.

**Player options (Moono Tweaks).** The game tweaks (`GameTweakNames`), the loading splash, frame
stats and the log's paths come from three places, later ones winning: built-in defaults (every tweak
but `ChangingBehaviour`'s, as `--game-tweak exact`; the moo splash; no frame stats; personal paths
hidden in the log), the settings file `<persistentDataPath>/oni-moono/settings.json`
(`PlayerSettings`: only the choices that differ from the defaults, so a default changed by an update
reaches everyone who has not chosen otherwise; a run home has its own), and the command line
(`EffectiveOptions`). A command line with any `--game-tweak` value sets the whole set of tweaks for
the run (`exact`, `all`, `none`, names, `-NAME`), `--splash moo|none` the splash, `--frame-stats`
the frame stats, `--log-paths hidden|full` the log's paths. They are read before the log opens,
which needs the last; the start of the log names the file and each option with where it came from.
The main menu's **Moono Tweaks** button, directly below Mods, opens a screen
(`src/OniMoono.GameTweaks/MoonoTweaksMenu.cs`) made of the game's own UI: the Mods screen's prefab
instantiated inactive, its `ModsScreen` swapped for `MoonoTweaksScreen` before it wakes, and the
menu's button prefab and style for the button. It lists every option from that list with a toggle
and a line on what it does, the tweaks that change gameplay labelled so, and rows the command line
set dimmed and fixed; "Restart to apply" writes the file and restarts through `App.Restart` (the
Restarter above), and the restarted player reads it with the same command line. `--no-tweaks-menu`
leaves the button out, as in the original's menu (e2e runs compared with it). Test runs pin the
options: `scripts/run.sh` and `scripts/run-windows.sh` pass `--game-tweak none --splash none
--log-paths full` first, so a run has exactly the tweaks, splash and log paths its own arguments
name (`--log-paths full`: the test tools read the run home's paths in log lines).

**How game tweaks install** (`src/OniMoono.Host/GameTweaks/TweakInstall.cs`, `TweakChecks.cs`).
A tweak written for one game version must not crash another's start-up or misbehave in it, so
each is checked against the game before it patches anything, and one that fails is off for the
run: a no-op, the game's own code runs, the log says why in one line (`game tweak NAME: off,
`Type.Method` changed in this game version`), the others install, and its Moono Tweaks row is
greyed out with "Not available on this game version", its setting left as the file has it.
- *Declarations.* Each tweak's class declares the game members it relies on with
  `[GameMember(use, "System.Void KAnimBatch::UpdateDirty(System.Int32)", fingerprints…)]`
  (`GameMemberAttribute.cs`, one source compiled into the host and `OniMoono.GameTweaks`): each
  method it patches (prefix, postfix, transpiler, finalizer; a prefix that can skip the method
  also `Replaces`), each it reimplements (`Mirrors`) and each member it reaches by name,
  `nameof` included (`Uses`), with the full signature as the metadata spells it; a type it
  takes by name is declared by its name alone (`[GameMember(GameUse.Uses, "PathFinder/Cell")]`).
  `Optional = true` marks a member only some builds have. The code that reimplements a game
  method carries `[MirrorsGame("signature")]`, and a site that reaches game members by names the
  IL does not give, once reviewed, `[NameLookupReviewed("why")]` (both in
  `GameMemberAttribute.cs`).
- *Checks, from metadata alone* (`System.Reflection.Metadata` over the game's files as they are
  on disk, before any load-time rewrite, and over the tweak assemblies; nothing is JIT-compiled or
  run): (1) every declared member is there with that signature; (2) every type and member reference
  from the code of a tweak's classes into an assembly the game ships (Assembly-CSharp,
  firstpass, FMODUnity, Harmony…) resolves, attributed per tweak by following the code from the
  class whose `Name` constant is its name through every class it uses, nested ones (lambdas,
  closures, iterators) included: `OniMoono.GameTweaks` is built against the game's types, and a
  changed member would otherwise throw only when its code first runs in play; (3) every member
  it transpiles, replaces or mirrors has a body whose fingerprint is one it declares as reviewed.
  A fingerprint is a hash of the method's IL with every token written as the member or type it
  names (tokens renumber between builds), branches as instruction counts, and its locals and
  exception clauses by type (`Fingerprints.Of`); each build with another body adds its hash, with
  the builds' names after it. The checks run on a worker from start-up (tens of ms) and the
  tweaks' install waits for them; compact-nav-links and paged-path-grids, whose rewrites run as
  the assemblies load, take the verdict at their first grid (`Decide`): off, the session keeps
  the game's flat arrays behind the rewritten accesses, as when a mod's access can't be rewritten.
- *All or nothing* (`TweakInstall.Guarded`): each tweak patches under its own Harmony ID; when
  its install throws, every patch of that ID is taken back. (4) Each transpiler reports the sites
  it rewrote (`TranspilerCounts`) and declares how many (`Rewrites`): more fails, fewer only
  passes when another owner transpiles the method too, its transpiler having run first (#41,
  `CallSiteReroute`); an undeclared transpiler fails. The counts are checked at install, when
  no mod has patched yet; a mod's later patch reruns the transpilers without a check.
- *The Moono Tweaks menu* (`OniMoono.GameTweaks.MoonoTweaksMenu`, `GameTweakNames.Menu`) is
  checked and installed the same way, though it is no tweak (no row, no `--game-tweak` name): its
  class declares its postfix of `MainMenu.OnPrefabInit` and the fields it reads by name
  (MainMenu's button prefab, parent and style, the Mods screen's parts; the Supply Closet's search
  field `Optional`, as the screen leaves its search box out without it), 1 and 2 run on its code
  with the tweaks', and its install is all or nothing under its own Harmony ID. Its reference
  scan stops at the tweaks' classes, which it reaches only to read what each guards
  (`ModOverlaps.Guards`, each in its own try): a tweak that fails turns off that tweak, not the
  menu. What it takes by name at run time is checked when the first main menu is made, before the
  button is (`MoonoTweaksMenu.Problem`): the main menu's parts, the Mods screen's prefab and parts,
  and its rows' references (`BG`, `Title`, `Description`, `EnabledToggle`, `Version`,
  `ManageButton`), each of its type; the sprites and text style it looks up by name are left out
  of that, as a missing one only leaves an icon blank or the body text in the label's style. Off
  either way, the main menu has no Moono Tweaks entry (nor the overlap and update badges beside
  it), one log line says why (`Moono Tweaks menu: off, …`), and the game and the tweaks run as
  without it: the settings file and the tweaks it chose are read before, without the menu.
  `ONI_TWEAK_TEST_MISSING=NAME` (test runs) takes the declared members of a tweak or the menu as
  gone; `ONI_TWEAK_TEST_MENU_MISSING=PART` a part the menu takes at run time
  (`tests/e2e/moono-tweaks-menu-off.yaml`, `moono-tweaks-menu-ui-off.yaml`).
- *The declarations against the code* (`TweakDrift.cs`, offline only: they hold for the code
  whatever the build, so start-up does not pay for them). The code is each tweak's classes and
  what they use, as in 2, stopping at other tweaks' classes. (5) Each game member it reaches by
  name is declared: `AccessTools.Field/Method/Property/PropertyGetter/PropertySetter/Inner/
  TypeByName/FieldRefAccess` (the `"Type:member"` form too), `Traverse`, `Type.GetField/GetMethod/
  GetProperty/GetNestedType/GetMember`, `Type.GetType` and `Assembly.GetType` with a game type's
  name, and `[HarmonyPatch]` attributes. A name is no member reference, so 2 misses it and a
  rename would fail only as the code runs. The IL is followed from each `ldstr` and `typeof`
  through the stack, locals, fields (one holding one value wherever it is stored, closures'
  captures included), constant arrays of names (a `foreach` over them) and `string.Concat`, to
  the lookup's arguments; `Traverse.Create(x)` and `x.GetType()` take `x`'s static type. A
  lookup with a game type and a constant name must find the member in this build and be
  declared: any declaration of it, on the type that has it (a base type's member there), a type
  by any declaration naming it. Its failure names the site (`Type.Method (src/…:LINE)`, from the
  PDB) and the line to paste, with each overload when the name has several. A name spelt
  `nameof(…)` compiles to the same constant and needs the same declaration: the compiler
  checked only that the name exists in the build it compiled with, not the member's signature
  or what it does. A lookup whose type or name the IL does not give, or a walk over a game type's members
  (`GetFields`, `GetDeclaredMethods`), is a warning listing the site, a note once its method or
  class is `NameLookupReviewed`. Not followed: names built any other way, a lambda's parameters,
  tuples, `Expression.Field(x, "name")`. (6) Each `MirrorsGame` marker in that code has a
  `Mirrors` declaration of the member, with its fingerprint for each build (the failure prints the
  line with this build's), and each `Mirrors` declaration a marker.
- Nothing is checked in play: no patch carries a guard or a finalizer for this.
- *Offline* (`OniMoono --game DIR --check-tweaks check|review|declare`, `TweakCheckTool.cs`;
  `scripts/check-tweaks.sh`, run by check.sh over the frozen builds in `/mnt/e/oni-builds`):
  check runs 1–3, 5 and 6 on a build of this branch's game update and then installs every tweak and the
  menu as in play (4, and every patch made must be declared), failing on any tweak or the menu
  off or anything undeclared (5's warnings do not fail it); review runs 1–3, 5 and 6 on
  any build (a newly downloaded one) and prints each body to review with the fingerprint to add
  once it is, and `--tweak-listings DIR` writes the normalised bodies for diffing two builds;
  declare prints each tweak's patches as `GameMember` lines (`ONI_TWEAK_DESCRIBE=Type::name,…`
  adds members it reaches by name). `ONI_TWEAK_TEST_UNREVIEWED=NAME` (test runs) ignores a
  tweak's reviewed fingerprints, as on a build nobody reviewed.

Test runs (not the product): `--run-home DIR` keeps a run's per-user data under DIR (Unity's
paths, and on Windows the Documents and local application data folders the game asks .NET for),
`--audio-wav DIR` sends the game's audio to `DIR/fmodoutput.wav` (FMOD's WAV writer) instead of
the sound device, and the offline Steam double (`tools/SteamStub`, an ELF library or a PE DLL
written directly) stands in for the Steam client. `scripts/run-windows.sh` uses all three.

## 7. Logging and diagnostics

`Debug.Log*` and the logger/handler surface behave as Unity's (the game replaces the handler).
Our engine logs through `Brutal.Logging` into the same Player.log-style file
(`~/.config/unity3d/Klei/Oxygen Not Included/Player.log`), with engine messages tagged
`[engine]`. GL debug output is enabled in debug builds. As the original release player does
(UnityProbe's logging scenario), a message of any type (Log, Warning, Error) is written with no
stack trace, and its callbacks get an empty one, although `Application.GetStackTraceLogType`
reports ScriptOnly for each type. An exception gets its own frames, innermost exception first, in
Mono's format (`  at A.B (System.String s) [0x0001f] in <mvid>:0`), then the managed frames of the
`LogException` call in Unity's (`A:B(String)`), the engine's frames left out where the original's
are native (`Engine/LogTraces.cs`). The call's frames are the original's: Unity's logging frames
(`DebugLogHandler`, the handler's, `Logger:LogException`), and for an exception the player caught
(from Update or a coroutine) `Debug:CallOverridenDebugHandler` when the game replaced the handler,
none with the default one. A coroutine step's exception ends at Unity's stepper
(`UnityEngine.SetupCoroutine.InvokeMoveNext`), and a first step's has StartCoroutine's frames and
its callers after it. The callbacks get the same frames without each UnityEngine frame called by
another (UnityProbe's cotraces scenario). Unlike Unity's, the file is capped at about 60 MB (D36).

Personal paths (`--log-paths hidden`, the default; `LogScrubber`): what goes to Player.log and to
stdout has the profile folder replaced by `%USERPROFILE%` (Windows, any case and slash) or `~`,
the Documents folder outside it by `%DOCUMENTS%`, and the user name as a path segment by
`<user>`. The accounts go too: any individual account's SteamID64 standing as a whole number
(17 digits, 76561197960265729 to 76561202255233023, so no mod or Workshop ID; a save path's
folder, a multiplayer mod's other players) by `<steam id>`; in the game's login line
(`Logged into Steam with ID:…, NAME:…`, Global) the ID and the persona name by `<steam id>`
and `<name>`, the name nowhere else, since it can be any word; in KleiAccount's lines the Klei
user ID (`[Account] Got login for user <klei id>`) and the auth ticket of a failed login
(`for ticket=<ticket>`). The original writes them all as they are. The path rules are built at
start-up from those known values (~2 ms; a Regex of them cost ~10 ms); the account rules hold
no state, so every thread shares them, and need no Steamworks call (the format covers the lines
before `SteamAPI.Init` and other players). A line is scanned only for the rules whose words it
has: a slash and a folder's or the user's name, or `76561`, `Logged into ` or `[Account] `
(vectorized searches; on WSL, release build: ~25 ns a line without a slash, ~55 ns one with a
path that names no one, ~750 ns one with a save path replaced, against ~5, ~40 and ~420 ns
before the account rules). The log callbacks (`Application.logMessageReceived*`) get the text
as logged: mods and the game's crash reporter read it there. The console writer holds a
partial line until its line break (or a flush, or other output, which writes it first, so the
order stays), so a path written in pieces is replaced too. The settings are read before the
log opens, so its first lines are covered.

**Native crashes** (Windows; `src/OniMoono.NativeHost/crash.c`, `NativeCrashes.cs`). A crash in
native code (the GPU driver, Klei's SimDLL, a mod's library, the runtime) has no managed
exception, so before this the log just stopped. Now Player.log ends with what crashed, the
native stack (module+offset frames, runs of managed code counted), the runtime's own text, how
the process ended and a minidump beside the log:

```
[engine] native crash: 0xC0000005 access violation (writing 0x0) in OniMoono.exe+0x41FE (1.0.0.0, %USERPROFILE%\...\OniMoono.exe) on thread 65064 "Test worker"
[engine] native crash stack: OniMoono.exe+0x41FE, 2 frames of generated code, System.Private.CoreLib.dll+0x3C5A1B, 1 frame of generated code, coreclr.dll+0x188BE3, ..., KERNEL32.DLL+0x2DDA7, ntdll.dll+0x7EE4C
[engine] native crash: runtime: Fatal error.
[engine] native crash: runtime: 0xC0000005
[engine] native crash: runtime:    at OniMoono.Host.NativeCrashes.Fault(System.String)
[engine] native crash: the process ended with 0xC0000005 (access violation)
[engine] native crash dump: %USERPROFILE%\AppData\LocalLow\Klei\Oxygen Not Included\crash-20261105-100100.dmp (3795 KB)
```

The thread is `(main)`, or named as .NET names its threads (`"GL"`, `"Presenter"`, the game's
`"KFastWorker3"`); SimDLL's and the drivers' own threads have no name. What sees a crash, measured
with .NET 10 (`--test-native-crash`):

| Crash | In the process | The record |
|---|---|---|
| Fault in native code called from managed code (a P/Invoke: the GL driver on the main or GL thread, SimDLL) | the runtime's vectored handler leaves it; its personality routine prints `Fatal error.` and calls TerminateProcess: no unhandled-exception filter, no Windows Error Reporting | the line and stack (noted at first chance), the runtime's text with the managed stack, the exit code, a dump |
| Fault on a thread with no managed frames (a native thread) | our vectored handler, then the unhandled-exception filter (ours, then the runtime's) | line, stack, exit code, dump |
| Native stack overflow on such a thread | the same | line, stack (`(x95)` for the recursion), dump |
| Heap corruption (0xC0000374) | raised to the vectored handlers just before the heap ends the process | line, stack, dump |
| `__fastfail`, a /GS check, `abort()` (0xC0000409) | nothing; Windows Error Reporting records it (its Application Error event 1000) before the process ends | the line from that record (module, offset and the fast fail's code, as `0xC0000409 fast fail 5 (an invalid parameter to the C runtime) in SimDLL.dll+0x9F1CB`), the exit code; no dump, as the process is gone (`--test-native-crash fastfail-reported`; the other cases turn Windows Error Reporting off) |
| `TerminateProcess` by another module | nothing | the exit code alone |
| `Environment.FailFast`; a stack overflow on a managed thread (in managed code or native code it called) | the runtime's text (`Process terminated.`, `Stack overflow.`), which it handles first | that text with the managed stack, the exit code, a dump |
| A managed null reference, caught or not; the mods' stack probes | the runtime's vectored handler, which is first, handles it | nothing (the managed exception is logged as before) |

The record is written by a watcher: OniMoono.exe starts a second copy of itself (`--crash-watch`,
with inherited handles: no paths on its command line, so `win-run.ps1 -Match RUN_HOME` finds only
the game) before the runtime, sharing a page of memory with the game. The watcher waits for the
game to end, and when it ends without the player's own exit (`Player.ProcessExit`, `Main`'s
return and `AppDomain.ProcessExit` say so, with their exit code) it appends the record. It writes
after the end because the log's own writer writes at its own offset and would write over lines
added while the game runs. In the game, a vectored handler added after the runtime's notes a
fault the runtime left alone (so not one in managed code); native code may still handle it then,
so the note is used only when the game then ends with that fault's code. The unhandled-exception
filter, set over the runtime's once it is up, notes a fault and calls the runtime's after. A
note copies the fault into the shared page and a thread made at start formats it there (the
faulting thread may have no stack left; nothing allocates; a structured exception handler covers
a broken stack). Standard error goes to the watcher through a pipe with no buffer and from there
to where it went before, if anywhere; when the runtime's `Fatal error.` text starts, the watcher
suspends the game, writes the minidump and lets it go on (the runtime's next write waits for the
watcher's next read, so the game can't end first). A player started from a terminal keeps its
console, so a P/Invoke fault there gets no runtime text and no dump. The watcher costs about
5 ms at start and 1.8 MB (7 MB working set); `ONI_MOONO_CRASH_WATCH=off` leaves it out. To
Steam it is a child process that ends with the game; the game is still the process Steam started.

The dump (MiniDumpNormal with thread information, unloaded modules and indirectly referenced
memory: about 0.6 MB at start-up, 4.6 MB at the main menu, 5.7 MB in a 200-Duplicant colony,
written in under a second) holds the threads' registers and stacks, the module list with full
paths and 1 KB of memory around each value on a stack that points into memory, so the heap
blocks and objects the threads were using are in it. For a heap corruption (0xC0000374) it also
holds ntdll's record of the failure, which the exception points at, with 1 KB around the damaged
block it names and the start of its heap. These are small pieces of the game's memory, so the
dump can hold whatever the game had in them: parts of file paths, names, text. The rest of the
heap is left out. It is written only on a crash and never sent anywhere; it is on by
default, as a player sends it only by attaching it to a report, and `--crash-dumps off` leaves it
out. The watcher keeps the two newest dumps, full ones included: before it writes one, it
deletes all but the newest of the older `crash-*.dmp` files beside the log. Module paths in the
crash line are hidden as the log's are (`--log-paths`).

`--crash-dumps full` (or `ONI_CRASH_DUMPS=full`, which takes the place of the option for test
runs) writes a dump with memory, for diagnosing heap corruption (issue #56): the process's private
read-write memory, which holds every heap (the game's, the drivers', the runtime's managed heap),
with the modules' data sections (ntdll's heap globals and its record of the heap failure, which
`!heap` needs to decode the heaps) and the layout of all memory (`!address`). Mapped images and
files are left out (the debugger reads them from disk), which is what makes it smaller than a
full-memory dump; private read-only regions are left out too (two 24 KB heaps at start-up).
`!heap -s` then names the damaged block, the failure's type and its stack, and `!heap -x` the
block's heap and segment (`--test-native-crash heap`: 20 MB at start-up). Its size is about the
game's private memory, several GB in a colony, and it holds everything the game had in memory
(the colony, text, file paths and whatever else the process read), so it is for test runs and for
a player who agrees to send it; the default stays the small dump.

**The crash window** (`crash_dialog.cpp`). Once the watcher has written a crash record it shows
a window, the onimaxxing site's dialog in its light or dark palette: "The game closed
unexpectedly", the error line taken from the record (the fault's code, its name and module, as
`0xC0000005 (access violation) in nvoglv64.dll`, a fast fail's module from Windows Error Reporting
as `0xC0000409 (fast fail) in SimDLL.dll`; an unhandled managed exception's type, method and
message; or the exit code and its name, as `0xC0000409 (fast fail)`), **Show details** (Moono's
release, the game's build, Windows, the GPU and its driver, installed memory, the session's
length, the enabled mods, the files), a box for what the player was doing, a check box to include
the dump (its size; left out when there is none, greyed over 96 MB), **Close** and **Report on
GitHub**. Reporting packs `Player.log` (a log over 32 MB keeps its first 4 and last 28 MB, with a
line between) and the dump if ticked into `OniMoono-crash-YYYYMMDD-hhmm.zip` beside the log (our
own deflate; under 24 MB, else without the dump: GitHub takes no `.dmp` and files up to 25 MB),
opens `issues/new` of this repository in the default browser with the title `Crash: <error>` and
the note, the details and a line asking for the zip as the body (under 7,500 characters; a long
note is cut with "…"), and then shows the zip as a chip to drag into the page (a shell data object,
as Explorer drags a file; clicking it shows it in Explorer). Nothing is sent by the window: the
player submits the issue, or not. The game gives the watcher what only it knows through
`moono_crash_describe` (`NativeCrashes.Describe`): its release, its window (the crash window opens
centred on that monitor), the GPU's renderer string, and from the main menu the game's build, its
language (English, or Simplified Chinese for `zh`) and the mods' count; the watcher adds Windows'
version, the driver version (DXGI) and the memory itself. The window is drawn without the GPU, as
the driver may be what crashed: Direct2D into a WIC bitmap through a software render target, with
DirectWrite text in Gabarito and Red Hat Mono (the site's fonts, SIL OFL 1.1, embedded and loaded
from memory; CJK and other missing characters fall back to the system's fonts). Its controls are
standard ones (buttons, a check box, an edit box, through comctl32 v6 in an activation context of
its own, so the process's manifest is unchanged), each painting its part of the drawing, so Tab,
Enter (Report on GitHub), Esc and screen readers work as in any dialog. The error line and the
details are read-only rich edit controls over their drawn text, in the same fonts, colours and
line spacing, so they can be selected and copied with the mouse or the keyboard and read by
screen readers (rich edit also picks a font for characters ours lack); where rich edit wraps a
line more than DirectWrite, the box grows. Each control's MSAA role and state come from a
Dynamic Annotation server that reads them when asked (button, check box and whether it is
checked, focus, enabled, the default button, and for "Show details" expanded or collapsed, whose
name is "Hide details" when open). Its theme follows Windows' app mode (high contrast: the system's colours), with a dark or
light title bar coloured as the palette's on Windows 11. Every size is in DIPs times the monitor's
scale, the watcher's thread per-monitor DPI aware (v2), relaid out on `WM_DPICHANGED`; lines are
whole pixels. Text and what holds it (lines, buttons, the check box, the icons) also scale by
Windows' text size (Accessibility > Text size, `TextScaleFactor`, 100 to 225%), re-read on
`WM_SETTINGCHANGE`; the window widens by half as much and grows as the text wraps. The executable's
manifest still declares no DPI awareness, so the game's process is as before (GLFW sets its
own). `--crash-dialog off` or `ONI_MOONO_CRASH_DIALOG=off` leaves the window out (the record and
the dump stay), and so does `ONI_MOONO_CRASH_WATCH=off`, which leaves out the watcher; a run with
`--run-home` (every test run) shows none unless `--crash-dialog on`, so a crash in a test never
leaves a window waiting. `ONI_MOONO_CRASH_DIALOG=inactive` shows it without taking the focus (to
look at it in a test run), and an end by Ctrl+C, by Windows shutting down or from outside with
exit code -1 shows none. While it is
open the watcher is still running, so Steam counts the game as running until it closes.
`OniMoono.exe --crash-dialog-render OUT.png [--theme light|dark] [--scale S] [--text-size T]
[--sample gpu|managed|fastfail] [--language zh] [--details-open] [--reported]` draws it to a PNG from sample
facts, with a drawn title bar, showing nothing; `check-native-crash.sh` checks such renders, the zip
(with Python's `zipfile`) and the issue link, and that no crash case leaves a watcher running.
Linux has no crash window (no native host).

On Linux nothing is recorded: the Linux player is the SDK's apphost, with no native code of ours
to install a signal handler, and the runtime reads its own dump settings only from the environment
at start. A player can set them in the launch options, before the command:
`DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_EnableCrashReport=1 "…/OniMoono" --
%command%` writes a dump and a `.crashreport.json` (each thread's frames, with modules) to `/tmp`,
or to `DOTNET_DbgMiniDumpName`. `scripts/check-native-crash.sh` (in `check.sh`) runs every case
above on Windows, as Steam starts the player and with standard error redirected, and on Linux the
caught null reference and that recipe.

Development diagnostics (host options, never needed by players): `--trace-exceptions` logs every
first-chance exception with per-frame IL offsets (the game ships without symbols; the offset
and `ilspycmd -il` locate the throwing instruction); `--capture FRAME:PATH[:SCALE]` writes a
screenshot through `ScreenCapture.CaptureScreenshot`; `--input FRAME:ACTION[:ARGS]` feeds scripted
mouse/keyboard/wheel/text input through the window's input path (held keys for chords such as
Ctrl+F4); `--dump-render-textures FRAME:DIR` writes
the screen and every live render texture as PNG at the end of a frame; `--dump-shader NAME:DIR`
writes a shader's GLCore variants and pass state; `--frame-stats` logs per-phase frame times
and the GPU time per frame (timer queries), any frame over 250 ms with its own breakdown, and every 600 frames the process memory and the managed heap, the game's speed, sim step and update parts (patches of the game's methods made only with frame stats on), the main thread's waits, the runtime's counts and the machine's state (PERFORMANCE.md, "Frame stats in a player's log"); `--max-frame-rate N[:FRAMES]` bounds the frame rate
(for the first FRAMES frames) so scripted schedules that count frames hold on unpaced displays;
`--probe-ui FRAME:X,Y` logs the canvas elements drawn at a window point with their hierarchy
path, material, texture, colour and group alpha; `--dump-hierarchy FRAME:PATH` logs a subtree's
active flags, rects and components; `--dump-component FRAME:PATH|TYPE` logs a component's serialized fields;
`--report-builtin-assets FILE` writes the objects a run loaded
from Unity's built-in resource files (docs/LICENSING.md); `--check-*` run the corpus gates.
`ONI_OBJECT_CENSUS=N[:MARK]` logs every N frames the live render textures (how many, how many
allocated on the device, their estimated bytes), textures, meshes, materials and sprites, each
against the census at frame MARK; with `ONI_OBJECT_SITES=1` it also lists the creation sites
(the game or mod methods) holding most of the objects made since then: what piles up until
Resources.UnloadUnusedAssets (ObjectCensus).

CPU profiles: `perf record -e cpu-clock:u -g` (user-mode sampling needs no privileges at the
default `perf_event_paranoid` 2) with `DOTNET_PerfMapEnabled=1`; the runtime maps JIT code twice
(W^X), so samples fall in a `memfd:doublemapper` mapping and are resolved against
`/tmp/perf-PID.map` by address. `dotnet-trace`'s thread-time sampling stops threads at GC safe
points and charges the time before a safe point to the method that reaches it (it showed lock
and GC-poll time that perf does not), so it is not used for self time.

## 8. Repository layout

```
docs/                     PLAN.md, ARCHITECTURE.md, research/, decisions are recorded inline
src/OniMoono.Assets/     file formats and readers
src/OniMoono.Graphics/   GL device and Unity shader runtime
src/OniMoono.Desktop/    window, input, GL device and presenters
src/OniMoono.UnityEngine/ engine API implementation (namespaces mirror Unity's)
src/OniMoono.Host/       executable
src/Facades/              generated UnityEngine.*Module facade projects (do not edit)
src/Unity.InternalAPIEngineBridge.004/  engine-side bridge that the shipped TMP calls into
src/ThirdParty/Steamworks.NET/          upstream 20.1.0 source port (see §2.1)
tests/                    xunit: OniMoono.Assets.Tests, OniMoono.UnityEngine.Tests, …
tools/Inventory, tools/ApiCheck, tools/FacadeGen
cache/ (ignored)          decompiled code, BRUTAL clone, inventories, type trees
```

Build settings in `Directory.Build.props`: `net10.0`, nullable, `AllowUnsafeBlocks`,
`TreatWarningsAsErrors` for our projects, BRUTAL analyzer configuration (documented
suppressions only), central package versions in `Directory.Packages.props`.

## 9. Verification

1. **Conformance:** `tools/ApiCheck` over the consumer set; the count of missing members only
   goes down. Each milestone states which areas must reach zero.
2. **Asset corpus test:** every object in every data file (and in bundles) deserializes to
   exactly its byte size, natives and MonoBehaviours alike.
3. **Unit tests** for math, serialization rules, cloning, lifecycle ordering, coroutine
   semantics, layout of uGUI (via the shipped DLL), shader variant selection.
4. **Headless boot runs** asserting the scene/bootstrap sequence reaches defined checkpoints
   with zero logged exceptions.
5. **Visual checks:** `--capture` screenshots at checkpoints, reviewed against reference
   screenshots of the real game.
6. **Gameplay soak:** new colony → N cycles → save → reload → continue, no exceptions.
7. **Offline Steam test double** (`tools/SteamStub`, `scripts/run-offline.sh`). The game
   requires a logged-in Steam client (`SteamAPI_Init`); on machines without one, test runs
   load a generated stand-in for `libsteam_api.so` through the host's dev-only
   `--native-override steam_api=PATH`. Every entry point Steamworks.NET binds exists and
   returns zero except a small table describing a logged-in owner of the base game (no DLC,
   no Workshop items, English); no callback is ever delivered. It is test infrastructure,
   not part of the product: it lets M4–M6 engine behaviour be exercised; Steam-specific game
   behaviour (DLC ownership, Workshop mods, achievements, cloud) is verified only with the
   real client.
8. **Original-player comparisons on Windows** (`scripts/original-windows.sh`). The original runs
   from a renamed copy of the install with BepInEx 6 and `tools/OriginalIsolation`, which
   redirects the same two known folders our host's `--run-home` does and sends FMOD's output to
   a WAV file; the copy's product name (PlayerSettings) is changed to one of the same length so
   Unity's registry key and LocalLow folder are the copy's own; the Steam double stands in for
   Steam. Both players load `tools/PerfProbe`, a game mod that keeps the colony at speed 3 and
   logs the frame rate from the real-time clock, and resume the save through the game's own
   `AutoResumeSaveFile` setting (`scripts/windows-run-home.sh`), so the same code measures both.
