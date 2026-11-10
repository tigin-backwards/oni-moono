# Plan

Goal: Oxygen Not Included's original managed code and data running on .NET 10 CoreCLR with
Unity's engine replaced by our implementation on BRUTAL, at close to parity with the original
(float bit-exactness excluded). Architecture: `docs/ARCHITECTURE.md`. Evidence:
`docs/research/`.

## Principles

- **Unmodified game.** Game IL and data are never patched. Third-party libraries that cannot
  run on CoreCLR are ported from their upstream source (Steamworks.NET, *ARCHITECTURE §2.1*).
- **Unity's behaviour is the spec.** When unsure how Unity behaves, find out (documentation,
  the shipped uGUI/TMP IL that encodes Unity's contracts, the data), write it down in the code
  comment, then implement. No guessing that "probably works".
- **Complete or absent.** An engine member is implemented fully or not at all; missing members
  surface as `MissingMethodException` and in `tools/ApiCheck`. No placeholder throws, no
  silent no-ops (legitimate player no-ops are documented as such).
- **Every milestone ends verified**: its exit criteria are tests or runs that pass, recorded in
  the commit that closes the milestone.
- **Coherent commits**, each building and passing tests.

## Environment

- .NET SDK 10.0.402 at `~/.dotnet` (`DOTNET_ROOT=$HOME/.dotnet`).
- Game: `~/projects/oni-linux` (Linux build, Unity 6000.3.5f2, base game, no DLC).
- GL: WSLg Mesa d3d12 — `GALLIUM_DRIVER=d3d12 MESA_D3D12_DEFAULT_ADAPTER_NAME=NVIDIA` gives
  GL 4.6 core on the RTX 3090; llvmpipe (GL 4.5) is the software fallback for CI-like runs.
- **Steam:** the game quits with code 2 if `SteamAPI.Init()` fails (as the original does). Runs
  past the launch scene require a Linux Steam client, logged in with an account that owns ONI,
  running in the same environment. Until then, runs are verified up to that checkpoint.
- Dev settings for runs: `kprivacyprefs.yaml` with `disableDataCollection: true`,
  `ignorekcrashreporter.txt` in the CWD, and `settings.yml` automation flags (`devBootSmoke`,
  `devAutoWorldGen`, `autoResumeGame`, `devQuitAfterLoadingSave`) for scripted checkpoints.

## Milestones

### M0 — Foundations

1. Solution skeleton per *ARCHITECTURE §8*: `Directory.Build.props`, `Directory.Packages.props`
   (central versions: `Ahwoo.BRUTAL.*` 2026.10.x, xunit, compat packages), `.editorconfig`,
   analyzer policy, `global.json` pinning SDK 10.0.402.
2. `tools/FacadeGen`: reads Unity's original module assemblies to map type → module, intersects
   with the types implemented in `OniMoono.UnityEngine`, and emits one facade project per
   module (`UnityEngine.CoreModule` … plus `UnityEngine.dll`), version 0.0.0.0, unsigned.
3. `tools/ApiCheck` wired as `scripts/apicheck.sh` over the full consumer set
   (Assembly-CSharp, firstpass, UnityEngine.UI, Unity.TextMeshPro, Unity.Timeline, FMODUnity*,
   ImGui.NET, Steamworks.NET), writing `out/apicheck.json` and a summary by module.
4. `OniMoono.Host`: game ALC with the resolution policy, native library resolver, eager
   preload, compat packages, code pages, runtimeconfig switches, CLI flags
   (`--game`, `--headless`, `--frames`, `--log`, `--screenshot`).
5. Steamworks.NET 20.1.0 port in `src/ThirdParty/Steamworks.NET` (pinned upstream commit,
   minimal documented diff).
6. A first engine slice large enough to load every game type: all engine **types** referenced
   (with base types, interfaces, enums and attribute classes complete) so that every type in
   every game assembly loads.

**Exit:** `dotnet build` clean; host loads all game assemblies and `GetTypes()` succeeds on
every one (B7 path); ApiCheck reports zero missing **types**; unit test project runs.

### M1 — Assets

1. SerializedFile v22 reader (header, types, objects, script types, externals), UnityFS reader
   (LZMA + LZ4/LZ4HC, blocks-info at end), `.resS`/`.resource` streams.
2. Type-tree runtime: load the 6000.3.5f2 release trees (vendored, compressed), typed walker.
3. Managed layout builder from reflection (Unity serialization rules), shared by
   deserialization, cloning and JsonUtility.
4. Corpus test: every object of every file (+ `hires_base_bundle`) consumes exactly its byte
   size; ~18.8k MonoBehaviours across ~700 script classes resolve their layouts.
5. Object binders for the native classes needed by M2 (GameObject, Transform, RectTransform,
   MonoBehaviour/ScriptableObject header, TextAsset, MonoScript, Texture2D metadata, Sprite,
   SpriteAtlas, Material, Shader metadata, Mesh, Font, Camera, Canvas, CanvasRenderer,
   CanvasGroup, VideoClip, globalgamemanagers managers: BuildSettings, ResourceManager,
   TagManager, TimeManager, InputManager, PlayerSettings, QualitySettings, GraphicsSettings,
   MonoManager, ShaderNameRegistry).

**Exit:** corpus test green; binders unit-tested against known values from the asset survey.

### M2 — Object model, player loop, headless boot

1. `UnityEngine.Object` / `GameObject` / `Component` / `Transform` / `Behaviour` /
   `MonoBehaviour` / `ScriptableObject` with native-side state, fake null, lifecycle messages,
   activation semantics, deferred/immediate destroy, `DontDestroyOnLoad`, Find APIs.
2. Instantiate (serialization-shaped clone with remap and callbacks).
3. Player loop, Time, coroutines (all yield types the game uses), execution order.
4. Scenes (`SceneManager.LoadScene` single mode with Unity's next-frame completion), Resources
   (`Load`, `LoadAll`, `FindObjectsOfTypeAll`, `UnloadUnusedAssets`,
   `GetBuiltinResource`), AssetBundles.
5. Application, Debug (logger, handlers, `logMessageReceived`, thread rules), Player.log,
   SystemInfo, Screen (headless values), Input (headless: no events), PlayerLoop hooks used by
   uGUI.
6. Math and value types (Vector*, Quaternion, Matrix4x4, Mathf, Random, Color*, Rect, Bounds,
   AnimationCurve, Gradient, LayerMask, …), Unity.Collections NativeArray surface used by the game.
7. FMOD: whatever FMODUnity needs to initialize and load banks (sound plays through FMOD's own
   output).

**Exit:** `--headless` run: launchscene → LaunchInitializer → (with Steam) global prefabs,
`Global.Awake`, `Assets` async loaders, `InitializeCheck` → frontend loads →
`MainMenu.OnSpawn` with `devBootSmoke` exiting 0; zero exceptions logged. Without Steam: the
run reaches `SteamManager` and exits with code 2, exactly like the original.

### M3 — Graphics

1. `OniMoono.Platform`: GLFW window + GL 4.5 core context, events, vsync, resize.
2. `OniMoono.Graphics`: GL device (buffers, textures incl. DXT/BC upload, RTs/MRT, FBOs,
   samplers, state), Unity shader runtime (SerializedShader parse, LZ4 blobs, GLSL split,
   variant selection, parameter binding, material-driven render state), program cache.
3. Engine rendering: Texture2D/RenderTexture/Texture2DArray/Texture3D, Mesh, Material,
   MaterialPropertyBlock, Shader globals/keywords, Camera rendering (culling, queues, sorting,
   callbacks, `OnRenderImage`, `CommandBuffer`, `SetTargetBuffers`), `Graphics.DrawMesh`/
   `Blit`/`BlitMultiTap`, `GL` immediate mode, MeshRenderer/SpriteRenderer, built-in constants.
4. Debug screenshot capture (`--screenshot`).

**Exit:** boot logo canvas renders (needs M4 canvas basics for the logo: do the minimal canvas
path here or merge with M4); a shader test scene renders every game shader's default variant
without GL errors; screenshots match expectations.

### M4 — UI and input

1. RectTransform (anchors, pivots, driven properties, `DrivenRectTransformTracker`, dimension
   change callbacks), Canvas (render modes, scaler data, sorting, nested canvases,
   `willRenderCanvases`, `ForceUpdateCanvases`), CanvasRenderer (mesh/material/texture, clip
   rects, `cull`, alpha), CanvasGroup, `RectTransformUtility`, canvas batching and drawing.
2. Shipped uGUI and TMP run unchanged: EventSystem, StandaloneInputModule, VirtualInputModule,
   layout groups, masks, scroll rects; TMP static font atlases, sprite assets, styles.
3. Input: Unity `KeyCode` mapping, legacy Input Manager axes from data, mouse/scroll, text
   input, cursor textures, clipboard, focus.
4. `Unity.InternalAPIEngineBridge.004` (engine-side bridge TMP calls into) reimplemented.
5. Animator for UI controllers (AnimatorController + AnimationClip curves).

**Exit:** main menu renders matching a reference screenshot of the original; menus navigate
(options, load screen, new game flow up to the destination screen) by mouse and keyboard.

### M5 — Gameplay

1. New game: worldgen (threaded, SimDLL settle) → backend scene → `SaveLoader` → Sim running.
2. World rendering: property textures, TextureLerper cameras, MRT pass, liquid command buffer,
   lighting composite, overlay camera chain and post-FX, KAnim batches (float data textures +
   VTF), terrain/tiles/backwall/gas/liquid, status items, placers, GL visualizers.
3. Tools, overlays, screens, timelapse capture (`ReadPixels` + `EncodeToPNG`).
4. Save → quit to menu → load → continue.
5. Hi-res texture bundle streaming (`StreamedTextures`).

**Exit:** scripted run: new colony, play 10 cycles at speed 3 with building/digging, save,
reload, continue; zero exceptions; screenshots at fixed checkpoints reviewed against the
original.

### M6 — Remaining modules and parity

1. VideoPlayer (EBML demux + libvpx VP8, `Hidden/VideoDecode`), intro and tutorial videos.
2. ParticleSystem (modules enabled by the game's prefabs), legacy `UI.Text`/`Font`/`TextGenerator`,
   TextCore `FontEngine` (FreeType) for dynamic atlases, `UnityWebRequest` (MOTD, crash report),
   `ImageConversion`, `ScreenCapture`, `JsonUtility`, Physics raycast, IMGUI surface, `Display`,
   `QualitySettings`, fullscreen/resolution switching.
3. Parity sweep: every main-menu option, every overlay, every tool, settings persistence,
   language switching (font swap), performance profiling against the original.

**Exit:** a parity checklist (`docs/PARITY.md`, written at the start of M6 from the game's
screens and features) fully ticked or with documented deviations only.

### M7 — Render thread (done 2026-10-17)

Why: on large colonies the main thread spends about 37% of its time issuing GL work (DeCrew,
cycle 17628: ~1700 draws and ~2.1 MB of texture uploads per frame), in series with game code.
Unity records on the main thread and executes on a render thread; we execute inline, which is
why CoreCLR's faster game code does not show as frame rate (26 fps against the original's 26).
Profile and counters: `--frame-stats` (draws, binds, uniforms, uploads).

Done first (2026-10-16), each measured on DeCrew: bindings cache, uniform value cache, texture
uploads through a pixel-unpack ring, vertex array per mesh layout and `glBindTextureUnit`. They
remove ~45k GL calls per frame but leave render time at ~13 ms: timing the device on Mesa d3d12
(WSL) shows `Draw` at ~12.5 ms per frame, of which draw calls 3.9 ms with single calls stalling
2–3 ms (the driver waiting for the GPU), texture uploads 2.3 ms, uniforms 1.2 ms, texture binds
0.9 ms. The stalls are a property of this driver; the same measurement on a native driver
decides how much a render thread gains there.

Design: `RenderDevice` calls become commands in a per-frame list, executed by a GL thread that
owns the render context (the presenter thread or a new one) while the main thread runs the next
frame. Everything a command reads is captured at record time: resolved uniform values and
texture/mesh references with content versions (texel and vertex data copied into the upload
rings when their version changes, as uploads do now), render state, targets. Sync points wait
for the GL thread: `ReadPixels`/captures, `GetPixels` of render textures, shader warmup, device
caps. One frame of latency at most, as Unity's render thread.

Exit: identical captures and probe output in the parity scenarios; DeCrew frame time bounded by
the slower thread (estimate ~25 ms, from ~38).

Result (`GlWorker`, ARCHITECTURE §3.5): DeCrew 34.6 fps (from 25.8; the original 26), main-thread
render 6.2 ms (recording) and present 0.2 ms (from 13.8 and 3.3); Regrettable Dimension played
28k frames at speed 3: 36.0 fps (from 25.6), no exceptions, no white-out. Paused captures are
identical to before; liquids (Cool Abyss) as before. Texture uploads no longer go through the
persistent pixel-unpack ring: with the GPU a frame or two behind the render thread, staged
uploads corrupted background textures under Mesa's d3d12 driver (white-out probe: three events
per run, with or without explicit flushing; none with a full wait per frame or with uploads from
memory). The uniform ring is unaffected in every run. One diagnostic run that made the main thread
wait for the GL thread every frame, on a build still staging uploads through that ring, ended in
a segmentation fault at frame ~19k; with uploads from memory two such runs passed 24k frames
without one, and no run of the shipping configuration has crashed.

### M8 — Windows (started 2026-10-17)

Findings: the Windows build (`OxygenNotIncluded.exe`, Steam) is the same game build as the
Linux one (KleiVersion.ChangeList 744825); its assemblies differ only in a Windows-only field
(`Global.saveFolderTestResult`) and the Epic Online Services library name. Every shader in its
data carries OpenGLCore programs next to the D3D11 ones (platforms 4 and 15), so the OpenGL
device runs the Windows data as it is; no DXBC translation is needed for the game. (The D3D11
programs keep only signature and code chunks, RDEF stripped; translating them, for mod bundles
built only for D3D11, would take names from Unity's per-program parameter tables.)

Work:
- Engine, Windows semantics: `Application.platform` WindowsPlayer; `persistentDataPath`
  `%USERPROFILE%/AppData/LocalLow/<company>/<product>` and the other paths with forward slashes,
  as Unity reports them; `PlayerPrefs` in the registry (HKCU\Software\<company>\<product>, names
  suffixed with Unity's hash); system fonts from the Windows font folder; `SystemInfo` strings.
- Host: natives as `<name>.dll` from `Plugins/x86_64`; the GLFW and libvpx natives for win-x64;
  the game's `Documents\Klei` root, which the game reads through `Environment.GetFolderPath`.
- Test infrastructure: the offline Steam double as a PE DLL (`steam_api64`), written by the
  generator directly (no native toolchain on either side); a run home that keeps test runs out
  of the user's Documents, LocalLow and registry; audio kept off the speakers; runs started
  from WSL through interop.
- Parity: the Linux original is the reference (same build); saves cross-load between the
  platforms; frame rate on the native driver.

Done (2026-10-17): the Windows player (`scripts/build-windows.sh`, `scripts/run-windows.sh`) runs
the Windows build on the native driver (NVIDIA RTX 3090, OpenGL 4.5): type-load, asset, bind and
shader checks pass (119 shaders, every variant compiles); the menu renders as on Linux; DeCrew
loads and plays without exceptions; Regrettable Dimension played 28k frames at speed 3 with no
exceptions and no white-out, seven autosaves, audio to the run home's WAV file; the user's own
Documents, LocalLow and Steam account untouched. FreeType moved to BRUTAL's package on both
platforms (the system library on Linux before); its `long` fields are `CLong`. Presentation
queues two frames (three buffers) with a copy fence instead of a `glFinish` per frame.

Open:
- Frame rate on NVIDIA's OpenGL driver (done): DeCrew ran at ~26 fps against 35 under WSL (Mesa
  d3d12 on the same GPU). `--gpu-profile` put 33 of 46 GPU ms in `Klei/BatchedAnimation`: binding
  a different texture for the vertex stage between draws (each animation batch has its own data
  textures) serialized the draws (~37 µs each whatever their size). With bindless textures
  (`GL_ARB_bindless_texture`) the frame's GPU time is 6 ms and DeCrew runs at ~42 fps, bounded by
  the main thread (~23.5 ms); paused captures match the binding path within run-to-run variation.
- Videos on Windows (done): there is no system libvpx there and no BRUTAL package for it, so the
  engine decodes VP8 itself, a port of RFC 6386's reference decoder; every frame of the game's
  18 clips decodes to the same planes as libvpx, which is now only the tests' reference (the
  dependency is gone on Linux too).
- Parity runs: save cross-load between the platforms, menus and options, mods, the Windows
  original as reference where it can run without the user's own data and account.

### M9 — Without Unity's packages (started 2026-10-18; stages 1–4 done 2026-10-18)

**Status:** this project's uGUI, TextMeshPro and Timeline are written (`src/UnityEngine.UI`,
`src/Unity.TextMeshPro`, `src/Unity.Timeline`) and are what the host loads by default, from
`packages/` next to it (the `--shipped-ui-packages` switch that loaded Unity's for the
differential runs was removed 2026-10-26; the host only loads ours). Their tests pass against
both the shipped packages and ours (47 uGUI, 71 TextMeshPro and Timeline), ApiCheck finds nothing missing, and the differential runs differ only
where two shipped runs do or by float rounding (`docs/PARITY.md`). Frame rates (Windows, PerfProbe,
speed 3, runs alternated; the machine was busier than for the 48–56 fps measurement, so absolute
figures are lower, and that build measured the same now): DeCrew 35.3–36.3 fps with ours
against 32.6–32.9 with the shipped packages; U57 11.7–12.6 against 12.1–12.3, equal within the
spread.

The goal is that nothing tied to Unity is loaded. Three Unity packages loaded from
`Managed/`:
- **uGUI** (`UnityEngine.UI.dll`);
- **TextMeshPro** (`Unity.TextMeshPro.dll`);
- **Timeline** (`Unity.Timeline.dll`), which is referenced but has no content in the game.

All three are under the Unity Companion License. They are replaced by this project's
assemblies of the same names, built into the host's `packages/` folder, which `AssemblyPolicy`
prefers over `Managed/`.

**What has to be kept:**
- **What the game calls:** 83 uGUI types (281 member references) and 34 TextMeshPro types
  (118), as `tools/Inventory` reports.
- **What it derives from:** Klei's classes derive from 15 of these types, for example `LocText`
  from `TextMeshProUGUI`, `KInputTextField` from `TMP_InputField`, `KScrollRect` from
  `ScrollRect`, and a `PointerInputModule`. Their virtual and protected members, and the order
  in which those are called, are part of the contract.
- **Serialized layout:** prefabs serialize these components by field layout, read through the
  managed type. Field names, types and order must match the shipped assemblies.
- **The shipped packages' calls into the engine:** they currently exercise the engine's
  internal API as well (for example uGUI's UI Toolkit bridge). The replacements may drop
  whatever they do not need.

**Process: clean room** (`docs/cleanroom/README.md`). This project's specification side has
read decompiled uGUI and TextMeshPro, so the replacements are written by separate
implementations, one functional specification per area. The implementations may use:
- **Metadata:** `tools/PackageShapes` generates, from the shipped assemblies' metadata, the
  type headers, enums, interfaces and delegates, the public and protected member signatures
  the game needs, and the exact serialized fields. Like `tools/SurfaceGen`, it never reads
  method bodies.
- **Unity's public documentation:** the uGUI and TextMeshPro manuals and API references.
- **The shipped packages as a black box:** `--shipped-ui-packages` or `--ui-packages DIR` picked
  which assemblies a run loaded (both removed 2026-10-26; compare with the original player
  now). A differential dump records the results of both on the same screens, and the
  implementations are judged on the difference. They never read the shipped code.

**Stages**, each closed by the gate below:
1. **Tooling:**
   - `tools/PackageShapes`;
   - the host switch;
   - the differential dump. `tools/UnityProbe` UiDump is extended to record, per element: the
     rect, the Graphic's generated vertices (count and a hash with a float tolerance), the
     material, the clipping and masking state, the TextMeshPro text info (characters, lines,
     words, bounds, the size auto-sizing chose), and the selectable state;
   - scripted flows covering the main menu, options, new game, the colony HUD, the management
     screens, text input, scrolling and dropdowns.
2. **uGUI**, in four specifications:
   - the event system (event data, raycasters, input modules, `ExecuteEvents`, selection,
     navigation);
   - graphics (`Graphic`, `MaskableGraphic`, `Image` with its simple, sliced, tiled and filled
     modes, `RawImage`, the legacy `Text`, `VertexHelper`, the canvas update registry, `Mask`,
     `RectMask2D`, stencil materials, clipping);
   - layout (layout groups, fitters, `LayoutElement`, `LayoutRebuilder` and `LayoutUtility`,
     `CanvasScaler`);
   - controls (`Selectable`, `Button`, `Toggle` and `ToggleGroup`, `Slider`, `Scrollbar`,
     `ScrollRect`, `InputField`, `Dropdown`).
3. **TextMeshPro**, in three specifications:
   - assets (font assets, including dynamic atlases through the engine's `FontEngine`, sprite
     assets, style sheets, settings, materials and `ShaderUtilities`);
   - text layout and mesh generation (`TMP_Text`, `TextMeshProUGUI`, `TextMeshPro`: rich text,
     wrapping, overflow, auto-size, alignment, kerning, the text-info structures);
   - `TMP_InputField` and `TMP_Dropdown`.
4. **Timeline:** only the types and members the game references. There is no content.

**Gate per stage:**
- `scripts/check.sh`, with ApiCheck extended to the replaced packages as providers: no
  missing members.
- The differential dump on every scripted screen: no differences beyond float rounding (0.01
  px; vertex hashes at that tolerance), or each difference explained and accepted in
  `docs/PARITY.md`.
- The scripted flows without exceptions, and captures compared with those of the shipped
  packages.
- The DeCrew and U57 frame rates no worse than with the shipped packages.

**Exit criterion:** a run loads no assembly from `Managed/` that comes from Unity, and
`docs/LICENSING.md` lists no Unity code loaded at run time. Unity's built-in resource files
(UI shaders, default resources) remain the only Unity data, and they are read from the user's
install.

## Game tweaks (separate from the engine)

Game tweaks are changes to Klei's own code (Harmony patches in `src/OniMoono.Host/GameTweaks`
and `src/OniMoono.GameTweaks`). The engine reproduces Unity; these change how the game does
something, mostly without changing what it produces. Players choose them on the main menu's
Moono Tweaks screen (ARCHITECTURE §6, "Player options"): by default every tweak but those that
change behaviour (`--game-tweak exact`); `--game-tweak NAME|-NAME|all|exact|none` on the command
line sets them for a run, and test runs pass `none` first (`scripts/run.sh`). Measured on U57:
- `fast-save-compression`: saves compressed and decompressed with .NET's zlib instead of
  Ionic.Zlib, the same zlib format (the original game loads these saves, and ours the
  original's); compressing 1.3–1.5 s instead of 5.0–5.5, decompressing 1.1–1.5 instead of
  2.5–3.7, files about 14% larger.
- `background-save`: the compression and file write run on a background thread from the
  serialized snapshot; the file is written beside the target and moved over it, and loading,
  the next save, the load screen's delete and move, and the end of the run wait for it.
- `no-save-gc`: the forced full collection at the end of every save is removed.
- `fast-serialization`: KSerialization templates write plain fields through writers compiled
  per type instead of boxing every value through reflection; alternating saves differ only where
  the sim moved between them.

Loading U57 takes 26–28 s here against 3 min 50 s in the original. Of it, about two thirds is the
game's own work (KSerialization reading the save, each object's setup) and a quarter the engine
creating those objects; UnloadUnusedAssets during the load is 1.2 s, almost all its two full
collections. JIT is not the limit: by the colony 13 s of compiling has happened across all
threads, yet compiling this project's assemblies ahead of time (ReadyToRun) removed 5,000 of
69,000 methods and no load time, and turning tiered PGO off cut the JIT to 8 s with the load
unchanged.

- `fast-particle-collision`: a radbolt's collision check gathers the colliders around it only when
  another radbolt is registered there, and finds radbolts among the live ones (0.94 → 0.44 ms a
  frame in U57; 40,000 checks verified against the game's own result). Each radbolt's colliders
  (its prefab's) are looked up once rather than on every check against it: with many radbolts
  in flight every one checks every other each frame (Gluttony, Linux: 2.9 → 1.5 ms a frame).
  The gather is made only when a nearby radbolt passes the method's own merge test (collideable,
  each holding the other's position), not whenever one is near: Gluttony's radbolts travel close
  together, and 56% of checks gathered against 0.09% now (Linux: 1.5 → 0.9 ms; 1.74 million
  skips verified, none would have merged).

- `off-screen-conveyors` (**changes behaviour**): conveyor items travel along their rails on
  screen as before; off screen their transforms are not moved each frame (85% of the ~950 moves a
  frame in U57), and an item skipped while moving is placed at its cell's centre when it stops
  or leaves the rail (taken off, emptied, or dumped by deconstruction).
  While off screen a travelling item's transform, and so the cell the game files it under for
  partitioner lookups, decor and radiation, lags its place in the conveyor's flow; the flow
  itself is unaffected. U57: about 2.9 ms a frame (conveyor render tick, cell-change monitor,
  animation and collider updates). Built against the game's own types in
  `src/OniMoono.GameTweaks`, whose patches are made only when the tweak is on.

- `fast-save-type-checks`: SaveLoadRoot.SaveWithoutTransform's per-component attribute check
  (Type.IsDefined, which reads metadata each time) and type name (Type.ToString, a new string
  each time) kept per type; the same answers. U57: CoreCLR's share of a save 19% → 8%, saves
  about 12% shorter (4.17 → 3.66 s averaged over five).

- `instant-reachability` (**changes timing**, on by default): ReachabilityMonitor's answers are
  rechecked, with the game's own UpdateReachability, only when an input changed: a cell's count of
  Duplicants reaching it crossing 0 (MinionGroupProber's writers), the object's cell, or its work
  offsets. The answers are the game's, up to a game second sooner than its once-a-second poll.
  U57 (Linux, 2026-11-06): 0.42–0.47 → 0.010–0.014 ms a frame; 27,600 objects covered, none left
  to the poll; at load 42,500 crossed cells, then about one a second and 900 moves a second
  (items on rails and falling). Verify mode: every covered object against a fresh check every
  few frames. It replaced `slow-reachability` (every 4 game seconds, 1.88 → 0.53 ms in the
  U57 inventory, answers up to 3 s late).

- `fast-partitioner-cleanup`: ScenePartitioner.Sim1000ms looks up every entry of every node
  dirtied since its last run (every insertion dirties its nodes too) to drop freed handles.
  Handles are freed only after ScenePartitioner.Remove, so the removed handles are recorded with
  their nodes and only those are looked up, dropped in descending order of their place in the
  node's list (the game's scan, given its swap-removal). Gluttony, Linux: 0.38 → 0.16 ms a frame;
  verified on the original save over 340,000 node scans (2.9 million entries dropped, none left).

- `off-world-fallers` (**changes behaviour**): GravityComponents.FixedUpdate leaves an object in
  place, still falling, when its next position is outside its world, so an item dropped out of
  the bottom of a rocket interior is stepped forever (Gluttony: 17,552 of them, about 8.5 ms a
  frame on Linux). An item (its landing is FallerComponents' item landing) falling straight down
  with no cell under it and no possible later step is landed where it hangs: it stops counting
  as falling, gets no Landed event, and after a load falls again until its first step. Windows,
  Gluttony with every tweak: 18.6 → 25.1 fps; the save with those items destroyed runs at about
  30. See PERFORMANCE_CASE_STUDY_3.md.

- `fast-sublimation`: the update buckets of Sublimates get a batch function (the scheduler's
  batch_update_delegate: same items, order, time steps and sub-ticks) that skips an item's
  Sim200ms only when it would change nothing: not sealed and no tag changed since its last full
  update, same cell, warmer than its element's limit in the sim's chunk, cell still full, left
  blocked on pressure. The skipped update's temperature read side effect (the chunk's heat delta
  copied to its SimTemperatureTransfer) is made. U57: about 76% of updates skipped, 4.7 million
  verified with ONI_TWEAK_VERIFY (the full update run instead changed nothing); sublimation
  1.45 → about 1.0 ms a frame. A skip still reads the item's transform and temperature
  component, which is what remains.

- `fast-anim-events`: AnimEventManager's per-frame loop over every playing animation (about 6,450
  in U57) reads each controller for whether it is destroyed, its frame index (current animation,
  play mode) and play speed. The tweak runs the same loop in the same order with the same
  arithmetic, taking those inputs from a mirror per entry, refreshed when the entry's controller
  changes or the controller is flagged by any method that changes them (StartQueuedAnim, Queue,
  the PlayMode setter, SwapAnims, OnDestroy). 18 million entries verified with ONI_TWEAK_VERIFY
  (mirror against controller, 0 stale); the loop 2.07 → 0.90 ms a frame.

- `fast-symbol-overrides`: a controller re-applies its symbol overrides on each dirty mark, and
  the override block's setters raise its version even when storing the values already held, so
  the batch re-uploads an unchanged block. Setter calls that would store only the current values
  are skipped (exact by construction; the setters are the block's only writers). U57: override
  texture writes 0.42 → 0.01 ms a frame; batch uploads 1.47 → 1.20 ms including the checks.
  Duplicants re-apply on every dirty mark (`applySymbolOverridesEveryFrame`, for FaceGraph's
  face), about 105 applications a frame on c100 (200 Duplicants). The tweak keeps each such
  controller's last calls with their inputs (override list version, batch and its atlases,
  batch group, block, anim data); when they are unchanged it redoes only the writes covering
  entries someone else wrote since (the setters log them), then the face. An application that
  leaves the block's bytes as they were puts its version back, so the face drawn over the
  overrides no longer uploads the block every frame. c100 (Linux, ONI_TWEAK_VERIFY): 0
  mismatches in 120 million checks, about 85% of applications kept, uploads 40–67 → 14–18 a
  frame. Windows (Vulkan, speed 3, interleaved runs, 120 s each): c100 34.9 and 40.7 → 62.4
  and 84.7 fps, the main thread's phases 28.4 and 24.3 → 15.9 and 11.7 ms a frame; DeCrew 107.2
  and 82.3 → 129.8 and 107.8 fps.

- `fast-anim-batches`: KAnimBatchManager.UpdateDirty writes, per dirty controller in order,
  its instance data and symbol instance block, applies its symbol overrides (game code with
  side effects) and writes its override block. The tweak writes the first two for every dirty
  batch on worker threads beforehand (one batch per worker, the main thread waiting), runs the
  game's loop with those calls skipped for controllers already written, and makes the
  override block copies the loop asked for after it, on workers again. A batch's writes touch
  only its own textures and slots; what they read is not changed by another controller's
  override application. UI batches, batches being set up, other controller types and other
  position delegates stay in the loop; another Harmony owner on the write path turns it off.
  Gluttony, Linux, stopwatch: 1.92–2.01 → 1.49–1.65 ms a frame; 3.2 million writes and 55,000
  deferred override blocks verified with ONI_TWEAK_VERIFY (each redone at its original place
  and compared byte for byte), none different. The engine's transform caches were made safe
  for concurrent readers for it.

- `fast-overlays`: the overlays' per-frame visits to objects. Temperature: the game's event
  reaches every temperature visualizer (U57: about 91,000) and each reads its transform; the
  visualizers' handler is replaced by one that keeps, by place in the handler order, the
  positions of buildings that cannot move and passes those out of view without touching them.
  Power: one `GetWattsNeededWhenActive` answer per circuit within a `Power.Update`. The modes
  with targets: their calls to `AddTargetIfVisible`, `RemoveOffscreenTargets` and
  `UpdateHighlightTypeOverlay` run the tweak's equal loops, passing targets already shown and
  keeping each target's transform, controller and fixed position. Paused, stopwatch, U57:
  temperature 10.4 → 6.0 ms a frame, power 1.28 → 0.77–0.95, materials 4.05 → 3.8,
  decor 4.32 → 3.6–4.0; verified with ONI_TWEAK_VERIFY across every overlay (27 million kept positions,
  0 wrong). Details and Colony Performance Fixes' approximate equivalents in
  docs/research/overlays.md.

- `fast-jobs`: the game's JobManager wakes every worker for each Run (a kernel semaphore, 15
  workers here) and waits until all have checked in, however few the items; the main thread
  runs it about 14 times a frame in U57. The tweak runs the same items on its own workers (same
  fixed thread indices): it wakes only as many as there are items beyond the caller's first,
  through a semaphore that spins before blocking, and returns once every item has completed.
  Each item runs exactly once (20,000 runs verified with ONI_TWEAK_VERIFY). Alternating runs,
  every tweak with and without it: 24.1–27.8 fps against 20.9–25.2 in steady windows (about
  25.9 against 23.2, +11%).

- `fast-offset-tables`: each Duplicant's fetch update (once a frame in U57) checks the work
  offsets of every fetchable item (about 27,000) on the job threads and recomputes the stale ones
  on the main thread, in group then list order. Offsets go stale only where the game changes
  them: a tracker's cell update, a solid or navigable cell change in its extents, a new tracker
  (Workable's four assignments), a newly fetchable item. The tweak marks the item at each and
  checks only the marked fetchable ones, recomputing the stale ones in the same order (the job's
  schedule with the main thread taking only the first group). 2,000 updates verified with
  ONI_TWEAK_VERIFY (every item checked as the game does: 0 stale without a mark; about two items
  recomputed an update). UpdatePickups 1.74 → 1.33 ms a frame; alternating runs, every tweak with
  and without it: 25.0–26.9 fps against 22.7–27.3 in steady windows (means 26.0 and 24.7).

- `fast-anim-cleanup`: KBatchedAnimUpdater sweeps 16 chunks of its controller grid each frame for
  destroyed controllers, reading every controller there; the tweak keeps the ids of controllers
  destroyed in the last full round of the sweep and sweeps a chunk only when its dictionary still
  holds one, so the same entries go at the same sweep. U57: 94% of chunk sweeps skipped (37,700
  verified with ONI_TWEAK_VERIFY, no destroyed entry found in a skipped chunk), 0.48 → 0.12 ms a
  frame.

- `fast-conveyor-paths`: every change to the conveyor rails, and the load, rebuilds the whole
  network; SolidConduitFlow keeps each source-to-sink path it finds (TryAdd) after asking every
  path already kept whether it holds the new one, which of them the new one holds, and for each
  rail whether a kept path has it. U57 keeps 447 of 735 paths found, and TryAdd was 97% of a
  rebuild: 50–510 ms on the main thread at each edit, 840–850 ms at the load. The tweak keeps,
  beside the game's list, each rail's paths and places, how many paths hold it and the paths
  starting with it, and asks the game's same questions only of the paths that can answer yes;
  the list ends up the same, in the same order. A rebuild now takes 2–14 ms (the load's 14).
  Verified with ONI_TWEAK_VERIFY (the game's TryAdd run on copies, both lists compared): U57,
  4,410 paths over six rebuilds, and 2minnow, 588, none different.

- `fast-overlay-textures`: MultipleRenderTargetProxy.CreateRenderTarget, run on every coloured
  overlay toggle (temperature, germs, and the temperature legend's filters) and resize, makes new
  copies of the main camera's targets for the liquid pass and drops the old ones undestroyed. In
  Unity, and in ours alike (an object outside the scenes lives until Resources.UnloadUnusedAssets,
  which the game calls only around a load), they stay allocated: U57 at 1024×768, paused, 100
  temperature openings took the live render textures from 45 to 846 and those allocated on the
  device from 74 MB to about 1.9 GB (50 germ openings: +250, +0.9 GB); the original Linux player
  the same, 44 → 445 and 59 → 963 MB over 50 openings (its resident memory stays flat: the
  textures live in video memory under WSL's d3d12). The tweak releases and destroys the copies
  the method displaced two frames later, once the re-recorded command buffer has run, unless the
  proxy, a shader global, a camera or RenderTexture.active still holds one; with it 300 toggles
  leave 47 live render textures and 79 MB. Verified with ONI_TWEAK_VERIFY (every material's
  textures and every component's texture fields searched at each release): U57, 529 textures released over 75 openings, none still referenced.

  The same mechanism elsewhere (2026-10-25): the decompiled game read for objects made again and
  dropped undestroyed, and U57 run with `ONI_OBJECT_CENSUS`/`ONI_OBJECT_SITES` through the
  overlay sweep twice, ten management screens opened and closed four times, and 30,000 frames at
  speed 3. After each thing's first use (lazily made batch groups, tile meshes, a screen's
  widgets) the counts stay flat: the second overlay pass added 46 textures and 36 meshes, the
  later screen rounds 16 meshes and 24 materials, normal play about 75 meshes every 1,200 frames, from
  GroundRenderer's element meshes (one set per chunk and element ever seen there, kept and
  reused: bounded, and not dropped). Read but not seen piling up:

  | Site | Trigger | Left per trigger | Status |
  |---|---|---|---|
  | MultipleRenderTargetProxy.CreateRenderTarget | coloured overlay opened or closed, temperature filter, resize | 8 render textures, 4 allocated, about 18 MB at 1024×768 | fixed (`fast-overlay-textures`) |
  | Diggable, Moppable, MovePickupableChore: `Renderer.material` tints | each dig, mop or move marker | one material instance (no device memory) | recorded: too small to show in the census |
  | RangeVisualizerEffect, LargeImpactorVisualizerEffect.OnPostRender | a range of another size shown | one Alpha8 range texture (KB) | recorded |
  | VideoScreen.PlayVideo, VideoWidget.SetClip | each video played, each codex video | a clip-sized render texture (5.5 MB at 1280×720) | recorded (not exercised) |
  | RetireColonyUtility previews, Slideshow.SetFiles | load screen and retired colonies browsed | a preview texture and sprite each | recorded (not exercised) |
  | LineLayer.InitAreaTexture | each report graph made | a 96×32 texture | recorded |

  The rest of the game's run-time textures, meshes and materials are destroyed when replaced
  (RenderTextureDestroyer, OnResize handlers) or made once per load (PropertyTextures,
  SubworldZoneRenderData, freed by the load's UnloadUnusedAssets). Of 605 decompiled Workshop
  mods none makes render textures on a repeated path (one-shot image savers; Rotate Everything's
  LightBuffer patch destroys through the game's destroyer).

Measured and not kept: the loading side of fast-serialization (a U57 load produced the same
colony, 3 bytes of time of day apart, in 31.3 s against 31.6), an exact early-out for
sublimation and a canvas build cache (docs/research/u57-frame-inventory.md).

With all four, the autosave freezes the game for 4.8 s instead of 11.7 (the remainder is
serializing the colony on the main thread, which reads live state, and the colony preview's GPU
read-back). Not tweaked: the per-item sim systems (Sublimates, ranching, transitions…), which
together make up most of a large colony's frame but are each at most a few percent.

## Defect review (2026-10-20)

A read-only review of every area of our code, one reviewer per area. Each reviewer gave a
failure scenario per finding, and each finding was checked against the code before acting on it.
Fixes come with a regression test that fails before them. Status: **fixed** (commit),
**open** (to do), or **left** (with the reason).

| ID | Area | Finding | Status |
|---|---|---|---|
| A1 | input | A release and a new press of a held key in one frame lost the press (key read as up while held); edges were applied presses-first from two sets. Now applied in order. | fixed (8fb7e5a; InputStateTests) |
| A2 | time | Time.time, unscaledTime and fixedTime were float running sums: several percent off after a few hours, and the fixed-step loop never ends past 2^19 s (a hang). Now double, read as float, as Unity keeps them. | fixed (8fb7e5a; TimeTests) |
| A3 | web requests | A file:// request whose path the runtime rejects stayed in progress forever. | fixed (8fb7e5a; WebRequestTests) |
| A4 | runtime | Float-to-int conversion of NaN or out-of-range values saturates on CoreCLR (Mono gives int.MinValue); the game's own casts differ too (Grid.PosToCell of a NaN position gives a valid cell). | fixed: measured in the original player (UnityProbe convert); the engine's Mathf and the game's and mods' conversions of floats convert as Mono does (ARCHITECTURE D29; MathTests, --check-types). Reached by Attribute Info Screen (bionic Duplicant's NaN experience) |
| L2 | hierarchy | No OnTransformChildrenChanged for Instantiate under a parent or a destroyed child; layout groups miss those child-list changes. | fixed: measured in the original player (PerfProbe measure.txt: Instantiate under a parent, Destroy by the next frame, DestroyImmediate each send it once); LifecycleTests |
| G1 | GL | The multiple-render-target framebuffer cache is never cleared: after a resize the world's MRT pass (MultipleRenderTargetProxy recreates its targets) renders into deleted textures, or leaks them. | fixed (the cached framebuffers are deleted with their textures; the count is logged). Not reached on a resize in this game: the resize callbacks stop at the game's own KCanvasScaler exception before the MRT proxy recreates its targets, in the original player too (measured) |
| G3 | GL | Integer render-texture formats are specified with a float pixel format (GL_INVALID_OPERATION). | fixed: integer formats are no longer offered (SupportsRenderTextureFormat false); no game target uses them |
| G4 | GL | Cube and multisample samplers are given 2D textures. | fixed: cube samplers get 1×1 default cubes of the declared colour (reached: _Cube, unity_SpecCube0, _LightTexture0); multisample samplers are logged, none seen |
| G5 | GL | ivec2/ivec3/uvec4 loose uniforms use the wrong glUniform call; uint block members are written as float bits. | fixed (integer scalars, vectors and arrays use their own glUniform*iv/uiv; uint block members as integer bits) |
| G6 | GL | The multisample resolve blit is clipped by an active scissor. | fixed |
| G7 | GL | A null colour target after the first in a multi-target binding throws. | fixed (an empty attachment) |
| R1 | rendering | Pixel memory of textures reclaimed by UnloadUnusedAssets (without Destroy) is never freed. | fixed (PixelMemory frees itself when collected; PixelMemoryTests) |
| R2 | rendering | A property block's SetTexture also supplies an identity _ST that overrides the material's tiling and offset. | fixed (UniformRecordingTests) |
| R3 | rendering | Shader Integer properties: the shader default in Floats shadows the material's saved int and SetInteger. | fixed (UniformRecordingTests) |
| R4 | rendering | A camera with SetTargetBuffers takes its size from the screen, not its buffers. | fixed (CameraTargetTests) |
| R5 | rendering | A RenderTextureDescriptor with useMipMap and mipCount -1 gets one level. | fixed (RenderingDataTests) |
| C1 | canvas | A graphic with no geometry (Klei's NonDrawingGraphic, the outfit panel's row hit areas) gets no depth, so it is never raycast. | fixed: measured, the original numbers it and raycasts hit it; every live unculled element is numbered (CanvasDepthTests) |
| C2 | canvas | Graphics under a CanvasGroup at alpha 0 get no depth, so they are never raycast (Unity still raycasts them unless blocksRaycasts is off). | fixed: measured, the original numbers it and raycasts hit it; transparent subtrees are numbered unless their groups stop raycasts, which keeps the hidden-subtree skip for those (CanvasDepthTests) |
| C3 | canvas | Depths are renumbered every frame; culled elements and those under alpha-0 groups that let raycasts through read -1; a Mask takes no number of its own; a nested canvas's relativeDepth equals its absoluteDepth. | fixed: measured with UnityProbe (`UIS depth`, 0 differing lines): every renderer on an active object is numbered, a Mask takes one more after its subtree, a nested canvas's relativeDepth starts at 0, and a canvas renumbers only on structural changes, so a deactivated object keeps its number until then (CanvasDepthTests) |
| T1 | TextMeshPro | With wrapping off, a line wider than the rect makes Ellipsis/Truncate text disappear. | fixed (clean room, 596cf66; measured: the cut line ends the text, a first character that does not fit leaves nothing; TmpLayoutTests) |
| T2 | TextMeshPro | Glyphs in a second or later atlas texture are drawn from the first. | fixed (clean room, fa0a612; TmpRichTextTests) |
| T3 | TextMeshPro | Typing a rejected character over a selection deletes the selection. | fixed (clean room, d6cc396; measured: validation sees the text without the selection; TmpInputTests) |
| T4 | TextMeshPro | Hyphens are not line-break points. | fixed (clean room, 48fc6d8; measured: breaks after "-" and the soft hyphen, not after U+2010/U+2011; TmpLayoutTests) |
| T5 | TextMeshPro | The input field uses string positions as character indices (caret off after a typed tag or escape). | fixed (clean room, 32ad6b4; TmpInputTests) |
| S1 | assets | A corrupt or truncated bundle throws out of AssetBundle.LoadFromFile instead of returning null. | fixed: decoders report truncated or damaged blocks as invalid data, the block table is bounds-checked (CompressionTests) |
| S2 | assets | A failed load leaves a half-built object cached. | fixed (ObjectLoaderTests) |
| S3 | assets | Reloading a bundle after Unload(false) reuses instance IDs of live objects. | left: the game only unloads with true |
| S4 | assets | Reloading a scene reuses surviving level objects. | fixed: a level's cached objects are forgotten when it loads again (SceneReloadTests) |
| S5 | assets | TextAsset.text decodes UTF-32 LE as UTF-16. | left: no UTF-32 text asset in the game, and Unity's detection of one cannot be measured without one |
| S6 | assets | Concurrent first reads of a resource file can map it twice (one view leaked). | fixed: one mapping per file under concurrent first reads (ResourceStreamTests; the racing form maps it 32 times) |
| H1 | fast-anim-events | The loop reads the entry count once; the game re-reads it, so an entry an event adds during the loop (a Duplicant's mouth animation started by a speech event) runs one frame behind. Breaks exactness. | fixed: the count is read on every pass; the play speed is read after the events when one restarted the controller; verify mode counts entries added during the loop (0 in a 9,000-frame U57 run) |
| H2 | off-screen-conveyors | An item kept off screen keeps its stale position when the conduit is emptied or deconstructed (it drops there); handles of items taken off the rail stay in the skipped set. | fixed: an item leaving the rail (RemovePickupable) is placed first; the set is per flow |
| H3 | fast-particle-collision | Destroyed radbolts stay in the live list unless a gather runs; the list grows. | fixed: destroyed radbolts leave the list on every check |
| H4 | fast-sublimation | Watched items of a previous colony stay referenced after loading another save. | fixed: watches are reset with each new colony (and fast-anim-events keys its mirrors weakly) |
| H5 | background-save | A save is header-only on disk until the background write ends; a crash in that window loses it. | fixed: a run ending by an unhandled exception waits for the write; a hard kill during it loses that save, as killing the game during its own in-place write does |
| P1 | objects | A destroyed object's access threw MissingReferenceException with the Editor's message; the original player throws NullReferenceException (measured: GetComponent on the game's destroyed KCanvasScaler after a resize, the same exception in both players). | fixed (LifecycleTests) |
| Q1 | TextMeshPro (QA round 1) | The Duplicant selection screen's expectation, interest and trait lines (RobotoCondensed-Regular SDF) render as garbled glyph fragments with this project's TextMeshPro since it became the default (bisected; correct with the shipped package, which the M9 differential run did not catch). | fixed (clean room, 5754969: those lines are saved with NotoSans and RobotoCondensed's material; the shipped package gives a loaded text whose material draws another atlas the font's own material, measured; TmpRichTextTests) |
| Q2 | screen (QA round 1) | Screen.resolutions on a virtual display lacked the twelve standard modes under 720 lines that the original lists (measured, PerfProbe measure.txt). | fixed (DisplayModeTests) |
| Q3 | GL (QA round 1) | A resize in a base-game colony does recreate the world's multi-target textures (no exception there): the cache keeps one framebuffer at the end, the old one deleted with its textures, and the driver was seen reusing a freed texture name for a new target (the collision G1 prevents). | checked (dogfood resize) |
| Q4 | destroyed render textures (QA round 1) | A destroyed render texture releases its device resource (traced, as the game's RenderTextureDestroyer destroys the old MRT targets two frames later). | checked (CameraTargetTests) |
| Q5 | video (QA round 2) | The Spaced Out intro short held its first frames for about 14 s: it starts seconds behind its audio clock (loading a 1080p clip, the menu's first frames), every decoded frame was then late and the worker converted none until it caught up (this managed VP8 decoder runs 1080p at about 45 fps). The original plays it at once. Now a late frame is converted every fourth one, so the picture moves while catching up, as dropping playback does. | fixed (VideoTests; dogfood intro) |
| Q6 | host (QA round 2) | The host carries its own copy of the engine assembly: scripts/build-engine.sh alone does not reach the player. The harness builds engine, packages, host and probe before every stage. | fixed (qa.sh) |

## QA harness (`scripts/qa.sh`)

One script exercises what the engine and the game tweaks do and fails on a regression; it
replaces looking for error logs by hand. Stages (each can be run alone):
- **unit:** the release build, both test suites and `scripts/check.sh` (API conformance with 13
  known UI Toolkit internals missing, type loading, assets, binding, with this project's UI
  packages and Unity's shipped ones).
- **dogfood:** scripted sessions in the game (`scripts/qa-scenarios`, and the M9 scenarios):
  the menu and options, a new game to embark, a large base-game colony's screens (management,
  research, Database) and every overlay, typing into text fields, save and load back through
  the pause menu, window resizes, and clicks that arrive together within one frame. A run fails
  on a log exception or error whose signature (type, message with numbers normalized, first
  frame) is not in the scenario's reference, or on a capture differing from the reference in
  more pixels than the scenario's tolerance.
- **verify:** U57 with every game tweak and every engine verify mode (canvas skips, component
  layouts, texel copies) for at least 10 game minutes; every check must report zero mismatches,
  and each must report.
- **perf:** U57 with every game tweak: peak memory under 16 GB, the steady frame rate no lower
  than the reference's less 15% (this machine's noise), and multi-target framebuffers not piling
  up.

QA rounds (2026-10-20):
- Round 1: the Duplicant cards' garbled text (Q1), the missing standard screen modes (Q2).
- Round 2: the intro short holding its first frames (Q5); the stale engine copy in the host
  (Q6). Its captures became the references; 26.2 fps the baseline.
- Round 3: nothing new in the engine. Calibration: random Duplicants and the research tree's
  real-time zoom need wider capture tolerances; two framebuffers at the end of overlays (a
  second target set) and of save-and-load (the first colony's targets stay referenced) are
  expected. Verify 797 game seconds with zero mismatches, 29.8 fps, peak 13.9 GB.

- Round 4: nothing new. The story-trait panel's preview and the research tree's position vary
  with timing (tolerances widened, those screens checked by eye).

Final review pass (four fresh reviewers over the changes since 810508b):
- Engine core: one medium, measured negligible: the hidden-subtree skip no longer covers the
  Research screen (alpha 0, raycasts still blocked, its raycasters turned off). Measured with
  ONI_CANVAS_STATS=1 on U57: 1,100 objects walked and 0.75 ms a frame against 1,060 and 0.71
  with the old rule. Kept: it is what Unity numbers. Lows fixed: a damaged archive's other
  exceptions are invalid data (fuzz test); Instantiate under a parent its clone's Awake
  destroys; a failed resource mapping is retried; the late-frame video rule waits while the
  player is paused and restarts with each request; focus loss drops queued key edges.
- Rendering and GL: no medium. Lows fixed: a multi-target framebuffer leaves every attached
  texture's list when deleted (a reused GL name is never taken for it); deleting a bound target
  invalidates the binding.
- TextMeshPro: one medium fixed in the clean room (1b23df9: a paste of 3,000 characters laid the
  text out 3,001 times, 7.9 s; now once, as the shipped package; each change laid out once).
  Lows fixed: the caret in a truncated text, soft hyphens and line heights (e4ea0cd); password
  surrogate stepping is the shipped package's too (noted).
- Tweaks and harness: no exactness break. Harness fixes: each check's summary is kept apart
  (the radbolt tweak's two), per-incident lines fail verify, verify and perf check the exit code
  and log signatures, an unreadable capture or reference fails, a missing baseline fails.
  Tweak lows fixed: a skipped conveyor item leaving the rail is placed where the game's last move
  puts it; a background-save failure cannot fault later saves; a check that could not catch
  anything removed from fast-anim-events.

- Round 5 (after those fixes): nothing new; every check passed and the captures were looked at.
- Review of those fixes: two mediums fixed. off-screen-conveyors placed an item leaving the rail
  from the wrong conduit (an item is drawn by the conduit whose last flow points into its cell,
  between that cell and its own); a component-layout mismatch line of the multi-match path did
  not reach verify (it now counts and ends with its number). Lows fixed: a damaged node table
  is invalid data; a failed mapping's retry removes only its own entry and disposes a half-made
  mapping; a bless records nothing from a failed run or without a frame rate, --bless-from fails
  on a missing capture, both radbolt reports are required. TextMeshPro, no medium; lows left
  (a drawn soft hyphen's ascender in the fit check, the truncation record's justify offset, the
  record alone on its line having no height): edge cases of Flush alignment and fallback fonts
  the game's texts were not seen to reach.

- Round 6 (after those fixes): nothing new. The intro's capture at one frame showed the clip's
  red alarm lighting at a different moment (its timing follows its audio clock), so the video
  captures are now checked for motion (each must differ from the one before) instead of against
  a reference. Verify 835 game seconds with zero mismatches, 27.6 fps, peak 14.4 GB.
- Review of that delta: nothing high or medium. Lows fixed: the node bounds check cannot
  overflow; a failed run is never blessed (dogfood, perf baseline, --bless-from), and a
  partial bless keeps the previous reference.

Rounds 5 and 6 found nothing new and the last review pass found nothing high or medium: the QA
stop condition holds (2026-10-20).

References (captures, signatures, the frame-rate baseline) live in `out/qa/reference`, made with
`--bless` once a run's captures and logs have been looked at; they hold game imagery and stay
local. The U57 stages need content that cannot be published (DLC files, a DLC-owning Steam
double), named in `out/local-tools/qa.env`; without it they are skipped and say so.

## Dogfood on a real profile (2026-10-20)

A player's setup end to end on Windows: the repository as a zip, `setup.ps1` with "Run with
PowerShell" (the .NET SDK installed for the user, a self-contained build), the launch options in
Steam, and play on the player's own colony (Rustic Homestead) with their 14 mods. Each finding was
compared with the original player (PerfProbe measure modes, the same save and mods) before it
was fixed, and each fix has a regression test.

| Finding | Cause (measured in the original) | Fix |
|---|---|---|
| Steam could not start the player | the long launch line, copied from the console, picked up its wrapping as spaces | the setups put the line on the clipboard |
| The hover card never showed until the mouse moved | a root screen-space canvas is laid out when it is enabled; the game builds the card in the frame its canvas (saved at scale 0) comes on, so ours built it at scale 0 | canvases laid out on enable; `AddComponent<Canvas>` starts in world space, as Unity's does |
| The pointer was one row high | Unity's `Input.mousePosition.y` is height − 1 − row, `Event.mousePosition.y` row + 1 | `PointerPixels`, for the window and scripted input; the position is read when the window opens |
| The player's log had none of the game's `[INFO]` lines | Klei's logger writes them with `Console.WriteLine`, which Unity's log file receives | the log file takes the console's output |
| Warp conduit receivers failed on load | `Instantiate` gives a null serializable-class field of the *source* an instance of its own (the game then reads its prefab's ports) | the cloner does the same, nested fields included; a null array stays null in the source, as in Unity |
| Blueprints Expanded failed to load, prefab registration aborted, the sandbox menu recursed until the stack overflowed | Unity API the game never calls: `AssetBundle.GetAllLoadedAssetBundles`, `MonoBehaviour.Invoke` and family, `RenderTexture.GetTemporary` with a read/write mode, `Color.darkGray`/`orange`/`violet`, JetBrains annotations | implemented as measured (Invoke: due calls run after Update and after LateUpdate, on disabled and inactive targets too; the colours are CSS's) |
| Blueprints Expanded's converted UI kept its legacy text | the player's `DestroyImmediate` destroys assets without `allowDestroyingAssets` (bundle and game-data prefabs); the refusal is the editor's | the refusal is gone |
| Blueprints Expanded's tools threw FieldAccessException | built against publicized game assemblies; Mono does not check access | ARCHITECTURE D11 |
| On a wide screen the main menu's news tiles sat stacked off its right edge | the tiles take their width from their height on OnRectTransformDimensionsChange; a rect resized or moved during the canvas update gets the message again once the update is over, and that second one lets the row lay out again (traced in both players with PerfProbe layout.txt; Unity's shipped uGUI behaved the same in this engine, so the engine was at fault) | the engine sent the second message; withdrawn on 2026-10-23: the layoutcull probe (ugui.md, "Moved children of layout groups") sees no message after the update, and the re-send made a moved layout child with a ContentSizeFitter re-lay out every frame (DraggableUI); the tiles stay in place without it (dogfood menu, and at 2560 x 1080); after the intro video they did not (dogfood intro, 2026-10-24): the second message is the pixel-perfect canvas's, sent after the update to objects with a CanvasRenderer that moved (ugui.md, "Moves on pixel-perfect canvases"), which the engine now sends; at 2560 x 1440 on Windows (2026-10-24) they stuck again: the engine told no move of an object whose CanvasRenderer was made since the last pass, and the row's first full layout can come in the frame the menu is made; it now tells every move made once the renderer exists and the object is active (ugui.md, "Objects made in the frame") |
| Mod Updater went to safe mode | its transpiler swaps an instance call for a static helper and keeps `callvirt`; Mono runs it as a call, CoreCLR rejects the method (reproduced with Harmony alone) | ARCHITECTURE D12 |

After these, the colony loads and plays with every one of the mods and logs no error, in this
player as in the original. QA round 7 with all of them: unit, dogfood (the resize capture now shows
the hover card under the scripted pointer, as the original would; references re-blessed),
verify (830 game seconds, every tweak and verify mode without a mismatch; 9,000 frames now, since
a less loaded machine runs more frames per game second) and perf (13.9 GB peak). Not covered: three mods of the profile that were not enabled use
API the engine still lacks (ShaderExporter: shader property introspection; MiniBase:
`Mathf.PerlinNoise`, out of scope like every worldgen mod; MakeOldLiquidsGreatAgain:
`Shader.SetGlobalFloatArray`). Open: the game's
error dialog logged that its stack trace's tab character is not in NotoSans-Regular; TextMeshPro
spaces tabs rather than looking them up as glyphs, so this goes to the clean room (a
behaviour description checked against the shipped package, then a fresh implementer).

## Player reports (2026-10-21)

Issue #1: Fast Track crashed on loading a colony, on Windows. Each finding was measured in the
original player (PerfProbe's measure mode) or reproduced with Harmony alone before it was fixed.

| Finding | Cause | Fix |
|---|---|---|
| Fast Track: MissingMethodException in Game.OnPrefabInit | `Renderer.allowOcclusionWhenDynamic`, `motionVectorGenerationMode` and the Mesh setters taking `MeshUpdateFlags` were missing (ApiCheck covered only the game's assemblies) | implemented as measured: defaults true and Object; of the flags, only DontRecalculateBounds changes anything |
| Fast Track: every starmap entity's registration threw (KeyNotFoundException in ClusterGrid.RegisterEntity) | its patch of `AxialI.GetHashCode` was inlined past once callers tiered up: MonoMod marks the unboxing stub of a value type's virtual method, the JIT inlines the unboxed MethodDesc (reproduced with Harmony alone) | ARCHITECTURE D13; the host's `--check-types` patches an emitted value type and fails on a missed lookup |
| Fast Save: types and members missing | `AsyncGPUReadback`, `SystemInfo.supportsAsyncGPUReadback`, `GraphicsFormatUtility.GetGraphicsFormat`, `ImageConversion.EncodeArrayToPNG` | implemented as measured: a read-back is done three frames after the request and its data readable that frame only; the graphics format table as reported in the game's gamma colour space (isSRGB changes nothing); PNGs from raw rows with a stride, BGRA swizzled, R8 as grey |
| Mesh bounds changed where Unity's do not | found while measuring the flags: Unity recomputes a mesh's bounds when its number of vertices changes and on RecalculateBounds, not for new positions of as many vertices, not for triangles (their submesh's bounds only), not on Clear | the engine follows those rules |
| MiniBase: `Mathf.PerlinNoise` missing | worldgen, out of scope for compatibility, but cheap | Unity's noise as measured (lattice values and slopes, period, symmetry): Ken Perlin's improved noise at z = 0 of the coordinates' absolute values, (n + 0.69) / 1.483; equal to the player's to four decimals |
| Rocketry Expanded: its elements had numbers for names, so lookups by name failed (KeyNotFoundException, "must have material associated with it", ground rendering) | its shared ElementUtil names SimHashes values by patching Mono's private `Enum.InternalFormat`, which CoreCLR does not have (checked against Mono's `system/enum.cs`) | ARCHITECTURE D14; with it, U57 and Rocketry Expanded log no error in either player (ours 19 fps, the original 5 to 6) |
| Rocketry Expanded: types and a member missing | JetBrains annotations (`ContractAnnotation`, `ItemNotNull`, `MustUseReturnValue`, `NotNull`, `Pure`), `GradientAlphaKey`, `Gradient.SetKeys` | implemented; the gradient's alpha keys are public now, as Unity's are |
| A review of every Mono emulation against Mono's source (Unity's fork, MIT) | an exception on a background thread ended the process where Unity's player goes on; enum formatting patched through `Enum.ToString()` alone, or found by `[HarmonyPatch]` attributes, did not apply everywhere; a mod's copy of a library already loaded in another version dropped the mod | ARCHITECTURE D14 to D16, each with a host check |
| The BRUTAL tile stayed bright over the new game's screens, Options and the other modal screens | drawn with IMGUI, it sits above every canvas; the game's modal screens dim and cover the menu | hidden while a modal screen is open (KScreenManager's stack) |
| The Windows setup's window closed on a red error | Run with PowerShell closes the window when the script ends; from inside the zip Explorer runs the script alone, and a folder name with an apostrophe breaks the menu's quoting (both reproduced with Explorer's command line) | `setup.cmd` to double-click (runs the script with `-File`, pauses); the script waits when started from the menu and says to extract the zip |

`tools/ApiCheck` over 126 mod assemblies (Fast Track, Fast Save, Peter Han's other mods,
Rephysicalized and the dogfood profile's) now finds nothing missing but ShaderExporter's shader introspection and
`Shader.SetGlobalFloatArray`. Rephysicalized's public build loads and plays with the same errors
as in the original. Open: `RenderTexture.active` does not make the texture the target of GL
immediate calls (`GL.Clear` after setting it clears the previous target; Unity's clears the
texture); the game only reads pixels through it, which works.

Open from the review, by likelihood: standard number formats with a precision ("F1", "N2")
round differently (Mono rounds a float to 7 significant digits, a double to 15, before applying
them; temperatures shown to a tenth can differ by one in the last digit); float-to-int casts of
NaN and out-of-range values saturate on .NET 9 and later; a HashSet emptied by removals refills
in another order; culture-aware comparisons use ICU rather than Mono's collator; state machine
save names of mod generics with framework type arguments name another assembly. True Tiles
1.3.1 (its last GitHub release) fails in both players on the current game; the Workshop build is
untested.

## Issue #2 and the popular-mods sweep (2026-10-21)

Issue #2 listed mods turning red on the mods screen, with logs. The top 100 Workshop mods (from
onimaxxing's catalog, the ones not flagged as malware) were then run in our player on U57.
Each finding was measured in the original player or checked against its log before it was fixed.

| Finding | Cause | Fix |
|---|---|---|
| True Tiles 1.4.1 stopped the game before the menu; the mods it ran with were blamed for the crash | its bundle (Unity 2022.3) was read with this Unity's layouts, not the file's own type trees | native objects in a file that carries type trees are read with them, as Unity reads a file from another version |
| Backwalls, Capsule 2.0, Cluster Generation Manager: bundles from Unity 2020.3 failed to load | older layouts of script data, Material keywords, placeholder textures (0 images), ParticleSystem (`useRigidbodyForVelocity`), and shaders (keywords per pass, two keyword lists per program) | each read in its old form; Unity 6 loads all of them (measured with Backwalls) |
| Cluster Generation Manager, Backwalls: "script type not found" | bundle prefabs referencing classes the mod does not ship | a missing script holds its place and is null to component queries, with Unity's two warnings (measured: slot order, GetComponent, Instantiate, FindObjectsByType) |
| Better Info Cards failed to load (InvalidProgramException) | its transpiler leaves a value on the stack before a try block, which Mono compiles | ARCHITECTURE D17 |
| Better Info Cards: NullReferenceException every frame once hovering had gone on a few seconds | its patch of `HoverTextDrawer.Pool<MonoBehaviour>.Draw` (shared generic code) was lost when tiering recompiled that code | ARCHITECTURE D18 |
| Popular mods' API missing | `Canvas.pixelRect`, `ColorUtility.TryParseHtmlString`, `Texture.allowThreadedTextureCreation`, `SceneManager.sceneLoaded`/`sceneUnloaded`/`activeSceneChanged`, `LineRenderer` | implemented as measured |
| Six mods on Ony's shared library (DGSM, WGSM, NoDrop, SmartMop, FavoriteResources, Mod Manager) | uGUI `DefaultControls` missing | clean-room specification from measurement, implemented from it |
| About 19 mods embedding an old PLib | `TMP_Text.ignoreRectMaskCulling` missing (Klei's own no-op in the shipped TMP) | clean-room specification, implemented from it |
| Capsule 2.0's effects | particle features the game never uses (clamp velocity, collision, lights, …) were refused at load | loaded without them, each logged; the Sphere shape is implemented |

Not ours (the same in the original player): cimgui's `SetKAssertCB` (absent from the game's Linux
cimgui.so too), TMP `FaceInfo`/`characterDictionary`/`TMP_TextElement.width` (absent from the
shipped TMP; old PLib's code that uses them is not reached), `InputField.onEndEdit`'s old
`SubmitEvent` signature.

## Open items

Known behaviours not reproduced, all confirmed unreachable by the game (M6):

- Playables evaluation of animation/audio clip playables throws NotSupportedException; only
  Timeline content reaches it, and no game assembly references Timeline or PlayableDirector.
- CPU texel reads (`GetPixel*`) cover uncompressed formats, BC1 and BC3–BC7, ETC1, ETC2, EAC,
  ASTC LDR at every block size and PVRTC, each checked against independent decoders (ASTC exactly
  against ARM's astcenc, ASTC HDR to the bit: `BlockDecodingTests`). No readable texture in the build
  uses a compressed format other than BC1 and BC3 (all 2682 textures checked by `--check-bind`).
  Compressed formats the GPU cannot sample (ETC, EAC, ASTC and PVRTC on desktop OpenGL; S3TC or
  BPTC on a context without them) are created with Unity's "Decompressing texture" warning and
  uploaded decoded; a GPU round trip (blit and read back) matched the CPU reads exactly for each.
- Animator: data-driven features absent from the build throw at load (blend trees, layer masks,
  synchronized layers, StateMachineBehaviours, non-legacy clip events); every animation asset of
  the game loads (`AnimationTests.EveryAnimationAssetInTheGameLoads`).
- UI Toolkit panel internals (13 members) stay absent: uGUI reaches them only through UI Toolkit
  panels, and no game assembly references UIDocument.

Environment notes:

- Rare corrupt geometry and one device removal were seen in long runs on Mesa d3d12 under WSL;
  llvmpipe runs show none, and the original player crashes and glitches on d3d12 here too (a
  double fault inside the driver during a probe run, black world frames). To confirm on native
  GPU drivers.
- Parity runs use the offline Steam test double (ARCHITECTURE §9.7) and need `libvpx` for video.

## Tracking

- `docs/PLAN.md` is updated when a milestone closes (status line under its heading).
- ApiCheck missing-member counts per module are recorded at each milestone close.
- Known deviations live in *ARCHITECTURE §2.1*.

## Status

- Planning: complete (this document, `ARCHITECTURE.md`, `research/*`).
- **M0: complete** (2026-10-15). `scripts/check.sh`: solution builds clean; 24 engine unit
  tests pass; ApiCheck: 0 missing types, 757 missing members (743 type refs / 3098 member
  refs); host `--check-types`: all 36 loaded assemblies type-load on CoreCLR, Steamworks.NET
  port verified against the shipped binary (tools/AssemblyDiff), Newtonsoft.Json /
  BinaryFormatter / IBM437 verified through the game's own code. The engine already contains the
  core of M2 (object model, lifecycle, coroutines, cloning) because type completeness required it.
- M1: corpus gate passing (`--check-assets`), rendering data binds (`--check-bind`: 7064
  objects).
- M2 (2026-10-15): headless boot runs launchscene → LaunchInitializer → SteamManager and exits
  the way the original player does outside Steam (`SteamAPI.RestartAppIfNecessary` → `App.Quit`,
  exit 0, clean shutdown with scene unload). Running further (frontend, main menu, devBootSmoke)
  needs a logged-in Steam client on the machine, as for the original game. Test runs use
  `scripts/run.sh` (isolated persistent-data root).
- M3 in progress (2026-10-15): the OpenGL 4.5 device runs the game's own GLSL (all 583 GLCore
  variants decode; `--check-shaders` compiles and links all 119 shaders on Mesa d3d12 / RTX 3090,
  skipping VR multiview variants whose extension the context lacks, as Unity does) and the launch scene's uGUI canvas
  renders correctly (`--capture 2:out.png`). Engine-side renderer complete: cameras, renderers,
  sprites (incl. 9-slice), particles, canvases (overlay/camera/world), video planes, Graphics/GL/
  CommandBuffer; IMGUI (OnGUI dispatch, built-in skin, layout, windows, text editing) verified
  on the GPU; shipped uGUI and TMP (SDF atlas, rich text, dynamic glyphs) render through the GL
  device. ApiCheck: 13 missing members (the UI Toolkit panel internals; see open items).
- M4/M5 in progress (2026-10-15), driven by scripted runs (`--input`, offline Steam test
  double): main menu, options, the new-game flow (game mode, asteroid style, destination,
  worldgen with SimDLL, Duplicant selection, embark) and the colony run without exceptions; the
  colony renders as the original (world composite with light buffer and property textures,
  KAnim batches for Duplicants and buildings, gas, liquids, tiles, HUD, details panels with
  nested canvases and RectMask2D).
- **M5: exit met** (2026-10-16). Scripted run (release build, offline Steam test double, debug-mode
  scratch copy of the game for the Dev Life Support building): new colony, dig orders, a building
  placed, ten cycles at speed 3 with an autosave each cycle and zero exceptions; Load Game →
  colony → Cycle 8 autosave → unpause → play on to the Cycle 9 autosave, zero exceptions.
  Without life support or food production the colony is lost as in the original (no orders:
  suffocation at cycle 3–4 in both; Dev Life Support only: starvation at cycle 9–10).
  Construction with materials, other tools and overlays continue under M6 (docs/PARITY.md).
- Performance (2026-10-15), colony at speed 3, 1024×768, Xvfb + Mesa d3d12 / RTX 3090, frame
  rate from GALLIUM_HUD over the same scripted new game: original Unity player 58 fps mean
  (min 47, max 64); this engine (release build) 57–62 fps mean (min 38–46, max 65–69), with the
  frame presented from a presenter thread as Unity presents from its render thread
  (ARCHITECTURE §3.5). Per-frame CPU: update 1.6 ms, LateUpdate 4.2, canvases 4.8, render 3.7;
  GPU 4–5 ms.
  Re-measured 2026-10-16 after M6 work: 58.6 fps mean (min 38, max 70) in the same scenario.
  Windows, 2026-10-18 (RTX 3090, native driver; the original from an isolated renamed copy,
  scripts/original-windows.sh; both players resuming the same save at speed 3 with
  tools/PerfProbe measuring): DeCrew (base game, cycle 17628) 22–24 fps, the original 12–13.5,
  real time per game cycle 227 s against 299–305 s; U57 (Spaced Out and every DLC, cycle 17077)
  ~12 fps against ~8, 261–282 s per cycle against 405–407 s.
  Re-measured after the engine costs a CPU profile found (scene roots, searches, destroy; the probe
  no longer searches the scene while the game runs, which had slowed both players): DeCrew 27–34
  fps against 15–18.5, a cycle every 213 s against 237 s (speed 3 allows no less than 200 s);
  U57 12.5–15.5 fps against ~8.5, 232–247 s against 378–393 s.
  Re-measured after the allocation and per-draw work (pooled draw records, materials' uniform
  values resolved once per version, DrawMesh sharing its block; allocations down about two
  thirds, collection pauses likewise), back to back: DeCrew 48–56 fps against 17–20, a cycle every
  210 s against 229–232 s (the simulation, not the frame, now sets the pace); U57 17–18.3 fps
  against 8.3–9.5, a cycle every 219–222 s against 363–368 s.
  Where U57's frame goes (2026-10-19, Windows, EventPipe CPU sampling of the main thread, 81 ms
  frames at the time): about two thirds is the game's own logic (sim step, state machines,
  conveyor rendering), so even a free engine would leave U57 near 21 fps. Ruled out as limits:
  GC (about 1.4% of the frame; server GC cuts pauses fourfold and the autosave hitch threefold
  for about 2 GB more memory, without raising the frame rate), lock contention (about ten waits
  per 600 frames; the Monitor time EventPipe shows is its safe-point sampling bias, absent from
  perf on Linux), CPU contention (the process uses under two of sixteen threads) and the draw
  count (about 1,500 draws a frame from the game's own batching, about 2.5 ms of recording).
  Engine costs it found and removed: page faults in the texel copies of the textures the game
  re-applies every frame (about 20 MB a frame; 3.8 ms, now 1.8, with reused buffers), canvas
  elements' property sheets refilled and compared every frame (over 700 elements), and
  reflection in Instantiate's field copies. U57 measured back to back against the build before
  them: 12.4–12.9 fps against 12.2–12.5 in steady windows; the machine's other load moved the
  same build between 12.5 and 16 fps.
  With the engine API hot paths (null checks inlined, objects holding their native record,
  generic component queries without reflection) and server GC, three alternating pairs against
  the build before this work: 13.71 fps against 12.05 in steady windows (+14%), 10.7–11.1
  against 9.0–9.4 in the autosave window; every run of the new build above every run of the old.
  Then (2026-10-19, measured on Linux, where U57 runs with the Windows install's DLC content and
  perf profiles without the Windows sampler's bias, docs/research/u57-frame-inventory.md): the
  device's texture snapshots copied on worker threads (2.2 ms a frame off the main thread),
  components holding their transform and GameObject, and roots' positions read without their
  world matrix. Three alternating pairs against the build before them: 16.01 fps against 14.74 in
  steady windows (+8.6%).
  U57 against the original Unity player on Linux (2026-10-19, same machine, save and probe, back
  to back): original 6.5–6.8 fps in steady windows, this engine 17.0–18.5 without game tweaks and
  19–20 with them (about 2.7× and 3×); in the autosave window 4.1 against 11.7.
- Clean-room rewrites (2026-10-18): code found to be modelled on Unity's managed source was
  rewritten from functional specifications by separate implementations (docs/cleanroom,
  docs/LICENSING.md "Provenance"):
  - the IMGUI module;
  - UnityEvents;
  - the UI Toolkit events and runtime;
  - TextGenerator's front;
  - the object pools;
  - HSV conversion;
  - SmoothDamp;
  - small routines.

  How the result was checked:
  - the implementations' tests were run against the old code, and the differences settled in
    the specifications;
  - `scripts/check.sh` passes: 281 tests, ApiCheck at 13 missing members;
  - the main menu's Options opens identically;
  - DeCrew plays 6,200 frames without exceptions;
  - typing into the in-game Database search filters it (text input reaches TextMeshPro through
    `Event.PopEvent`).

  IMGUI members that no game assembly references (`GUI.tooltip`, Toolbar, SelectionGrid,
  PasswordField) are not implemented.
- M6 in progress (2026-10-16): docs/PARITY.md ticked (Mop, Disinfect, Harvest, Attack and Wrangle orders carried out in both players). `tools/UnityProbe` (a mod run
  in both players) compares lifecycle and timing semantics, engine values, the main-menu and
  in-game UI layout (about 3400 elements), victory sequence, event popup, 18 building side
  screens and a Mop order with the original; its findings are fixed or documented (ARCHITECTURE
  D2, D3, D10). ApiCheck (ImGui assembly now included): 13 missing members, the UI Toolkit
  panel internals.
- **M6: exit met** (2026-10-16): docs/PARITY.md fully ticked or with documented deviations
  (ARCHITECTURE D1–D11).
