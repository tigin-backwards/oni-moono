# Direct3D 11 renderer: goal and plan

Started 2026-11-22. This file is the goal's record: read it first when resuming the work.

## Goal

A Direct3D 11 renderer on Windows that runs the game's shipped D3D11 (DXBC) programs unmodified,
equals the original's D3D11 behaviour wherever that can be measured, and lands on master once, as a
finished unit, after every done criterion below passes and the user says so.

## Rules

- **Branch.** All work on the local `d3d11` branch (worktree `out/d3d11-wt`), never pushed; mirrored
  to `~/projects/oni-brutal-d3d11-backup.git` (`git push ~/projects/oni-brutal-d3d11-backup.git
  d3d11`). Rebased onto master regularly.
- **Prep on master.** Small behaviour-neutral prep refactors may land on master (check.sh green), to
  cut rebase conflicts.
- **Priority.** Below GitHub issues, triage and the user's other requests, player slots included.
  runq jobs use `RUNQ_LEVEL=burndown`.
- **Discipline (as the Vulkan device).** Shipped programs, never our own translations. Unity's
  conventions from public docs and measurement of the original only, never Unity engine source.
  Every COM slot, struct and enum we use checked against the Windows SDK headers by an MSVC
  static-assert check in the build; every return-code and behaviour assumption cited from
  Microsoft's documentation or observed (with what, where, GPU and driver recorded).
- **Reviews.** An adversarial review at the end of every milestone by a fresh agent given the code,
  not conclusions: real bugs, defects, and invented API or cases. Each API use, guard, fallback and
  fault hook is classed documented (cite), observed (evidence) or invented (remove). Open
  UNVERIFIED notes block the milestone.

## Done criteria (all required before merge)

1. Every game shader (121 shaders, every pass and variant) draws through its shipped DXBC program;
   the D3D11 debug layer reports zero errors and warnings over all QA runs.
2. SystemInfo values, projection, `_ProjectionParams`/`_ZBufferParams`, render-texture formats,
   orientation and ReadPixels, depth bias and swap-chain settings equal the original's D3D11 as
   probed on the frozen build.
3. Pixels match the OpenGL device within a stated tolerance over a fixed scene set: main menu, three
   colonies with every overlay, starmap, loading screens, screenshots and timelapse, UI.
4. Everything the Vulkan device does (the device inventory's checklist): textures, render textures,
   readbacks and async readback, its own DXGI swap chain with repeats, splash, loading animation,
   HDR, fullscreen and resize, start-up guard, deny list and fallback.
5. Robustness: a fault hook per start-up step; device removed (`dxcap -forcetdr`) recovers or quits
   cleanly with the error shown; alt-tab, resize, monitor changes; the GPU choice rule.
6. Mods: all 38 mods with shaders draw. In particular the 9 that visibly lose shaders on Windows
   today (Backwalls, Backwalls (Fix), Forcefield Projectors, Chaos Events for Twitch Integration,
   Capsule 2.0, Aki's Extra Twitch Events, the Simplified Chinese font replacement 3561866116, the
   Japanese Font Patch 3135364297, MAGNET!!! under OpenGL) and the 4 lost on every OS (Portal
   Network, PortalNetwork+, Spring Festival Pack, Twitchery's LiquidRefraction) draw on D3D11. That
   needs the 2020.3 and 2018.3 program layouts read as well as U60's.
7. Performance: CPU frame time and fps against Vulkan on the same saves, measured and decided by the
   default/fallback rule below.
8. Correctness gates: the MSVC slot/struct check; no open UNVERIFIED notes; every milestone's review
   resolved; a final full independent review.
9. QA: check.sh, the release e2e list, a D3D11 twin of `vk-gate`, `u60-p2-renderer` with
   `-force-d3d11` against the original, a full Windows dogfood on D3D11.
10. Docs: ARCHITECTURE §3.5 for D3D11; a decision record superseding "No Direct3D renderer, by
    choice" (`docs/research/unity-vs-moono.md`); options `--renderer d3d11` and `-force-d3d11`
    honoured as Unity's player does.

Out of scope: retiring OpenGL on Windows (decided later, on the results).

## Milestones

| # | Milestone | Ends when | Status |
|---|---|---|---|
| M0 | Research: probes of the original, RenderDoc captures, threading/perf measurement, mod API scan, demand count, tooling; this plan | Every convention has a measured or cited answer | done 2026-11-22: reviewed (17 findings fixed, re-measured); remaining unknowns assigned to M2–M7 in `d3d11-captures.md` |
| M1 | Program reader for platform 4 | All 960 game entries and 652 mod entries parse, validate and match their DXBC declarations in unit tests | done 2026-11-22: reviewed (findings fixed), check.sh green (j0008164); prep commit on master (7c5476eec) |
| M2 | Device bring-up, own swap chain | Main menu draws, debug layer clean, pixel-diff tooling against OpenGL | reviewed; review fixes 2026-11-23 ("M2 review fixes"); the BatchedAnimationUI debug-layer warning decided by the user 2026-11-23 (denied in the storage filter, M3): done |
| M3 | Full draw feature set | A colony plays; scene-set pixel parity | part A 2026-11-23: three colonies draw, debug layer clean (the RTV warning denied, the user's decision), scene set and tolerance defined, M3 UNVERIFIED settled or moved. Part B 2026-11-23: criterion 1's proof decided (a validation run makes all 1,070 subprograms but the 68 refused programs; play draws 49 of 121 shaders; the rest accounted for by their references), the research and mid-game starmap scenes fixed, the overlays' light pools fixed as the original draws them (cameras to the screen into a render texture), 75 of 75 scenes within the tolerance. Open: the 68 refused programs (the user's call), 38 game shaders not drawn in play, how the original hands its camera texture to the screen (UNVERIFIED, M4) |
| M4 | Presentation and frame pacing | Repeats, splash, loading animation, HDR, fullscreen/resize matrix | |
| M5 | Robustness | Fault hooks, TDR, guard, deny list and fallback tests | |
| M6 | Performance | Threading design measured and tuned; the rule's table decides default or fallback | |
| M7 | Mods and compatibility | Mod set and SystemInfo parity | |
| M8 | Final | Full review, all QA, docs, rebase, merge on the user's word | |

**M1 (2026-11-22, branch `d3d11-m1`).** Built in `src/OniMoono.Assets/Shaders`:

- `ShaderCompilerPlatform.D3D11` (4) and `GpuProgramTypes` 15–22 (AssetStudio's enums). The
  reader accepts 15–19, the types the data has.
- `D3d11Program`: the entry (shared header and tail, the 38-byte prefix with bytes 1–3 as slot
  counts and the rest raw, one DXBC ending the payload, nothing after the tail). The DXBC's
  program type and major model must be the ones the GPU program type names.
- `Dxbc.cs`: the container and signatures as vkd3d-shader's `dxbc.c` reads them (commit
  698cbfbd), with its component-type ≤ 3 check; the checksum as its `checksum.c` computes it; the
  SHDR/SHEX token stream and the `dcl_constantbuffer`, `dcl_resource`, `dcl_resource_structured`,
  `dcl_sampler` and `dcl_globalFlags` forms of models 4.0 and 5.0, from
  `include/directx/D3D12TokenizedProgramFormat.hpp` (DirectX-Headers 61c77189). Bytes stay
  unmodified; damaged input throws `InvalidDataException`.
- **Refused as unsupported, not decoded untested** (each has a test; each comes back with a
  fixture if a mod needs it): every declaration the 1,612 programs lack (UAVs, raw buffers,
  streams, interfaces and class linkage, hull/domain/compute, `dcl_input_ps_sgv`,
  `dcl_output_sgv`, `dcl_gs_instance_count`), custom-data blocks other than immediate constant
  buffers, OSG5, non-zero stream or minimum precision, GPU types 20–22, models other than 4.0
  and 5.0, resource dimensions other than 2D, 2D MS, 3D, cube, 2D array, 2D MS array and
  structured, sampler modes other than default and comparison, extended opcode tokens on the
  decoded declarations, and instruction lengths with bits 29–30 set.
- `DxbcTokenFormat`: every token-format value the reader uses, under its header name.
  `scripts/gen-dxbc-check.py` turns them into `src/OniMoono.NativeHost/dxbc_check.cpp`, static
  asserts against the committed DirectX-Headers file and the SDK's `d3dcommon.h`; MSVC compiles
  it in the Windows build (`build.cmd`), and `tests/scripts/test_dxbc_check.py` checks that it is
  the generator's output and compiles it with a local C++ compiler when there is one.
- `D3d11Bindings`: Unity's parameters (common plus blob) as t#, s#, b#, checked both ways against
  those declarations (kind, texture dimension, cbuffer size) and the prefix counts. Mods with
  inline sampler states record them in `m_Samplers`, now read by `ShaderParameters`.
- `D3d11Variants`: per-stage lists selected by the existing rule, now `VariantSelection` (moved
  unchanged).

Survey (`ShaderSurvey d3d11` / `d3d11-bundles`), 0 failures in both. `D3d11DataTests` runs the same
check as a unit test when the data is there (`ONI_WIN_DATA`, default the frozen build's folder
under `/mnt/e/oni-builds`; `ONI_WS`), and is skipped, naming the folder, otherwise. An unreadable
bundle, serialized file or shader counts as a failure.

| Data | Shaders | Subprograms | Distinct entries | Vulkan keys mapped |
|---|---|---|---|---|
| U60 764025 Windows data | 121 | 1,070 | 960 | 590 of 590 |
| out/ws mods, 2022.3/6000.3 layout | 82 | 656 | 652 | 5 |

The 91 mod shaders serialized before 2021.2 are counted, not read. New measurements: every
texture's declared dimension matches its TextureDimension (1,722 game, 497 mod); SM 4.0 programs
sit in SHEX chunks too (UIRDefault); SV_Depth's signature register is 0xFFFFFFFF; one
Texture2DMS declaration has sample count 0, which the header calls disallowed (kept raw).

The checksum is also tested against known answers at 13 lengths covering both final-block shapes:
vkd3d-shader's `checksum.c` built and run, equal to an independent Python translation.

**Unknown, kept raw** (read, used by nothing, so no claim rests on them): the cbuffer binding's
second int, prefix byte 0 and bytes 4–37, the entry statistics, the pair's component code.

**Carried to M2, then to M7.** Decode `m_Samplers`' `sampler` value (its encoding is unknown; the
two mod inline samplers, 2990227046's Hidden/Dither, need it to draw). M2 refuses programs with
inline samplers (logged, not drawn: `D3d11Programs.Build`); no game shader has one, so only that
mod needs it, and mods are M7's (done criterion 6).

**Unsettled, noted.**
- A typed `Buffer<T>` (dimension 1): how Unity records its binding is not measured; the reader
  refuses the dimension.
- Extended `dcl_function_body`, `dcl_function_table` and `dcl_interface`, whose length is in the
  next DWORD: refused with every interface declaration.
- The instruction length's width: 7 bits by the header, 5 by vkd3d-shader. No data sets bits
  29–30, and a token that does is refused.
- The selection flattens hardware tiers; only the last is populated in all data read.

User decisions: the merge (M8). The default is decided by the rule below.

**Decided 2026-11-22: the default/fallback rule (M6).** D3D11 replaces Vulkan as the Windows default
only if it is at least as fast; otherwise it is the fallback Vulkan falls back to (before OpenGL).

- **When.** Only after done criteria 1–5 pass (a fast but incomplete device proves nothing), on a
  quiet machine.
- **Where.** The user's desktop on both GPUs: the RTX 3090 (NVIDIA) and the Radeon iGPU
  (`--gpu integrated`, AMD), so two drivers decide, not one.
- **What.** The saves and method of docs/PERFORMANCE.md "How it's measured": speed 3, 1280×720
  windowed, PerfProbe, no autosaves, victory held off, a warm-up launch first, unfocused at High
  priority, no frame stats.
- **How.** Launches alternate D3D11 and Vulkan (ABAB…), at least 5 pairs per save and GPU. Per
  pair: the fps ratio (D3D11/Vulkan, mean of kept windows) and the p99 frame-time ratio; also
  logged, not judged: p50, the 1% low, the device thread's busy time.
- **Pass.** In every (save, GPU) cell, the median fps ratio is ≥ 0.98 and the median p99 ratio is
  ≤ 1.02.
- **Noise.** A cell whose pair ratios spread more than ±5% around the median gets 5 more pairs.
  Still undecided: it counts as a fail (a tie keeps Vulkan).
- **Result.** Every cell passes: D3D11 is the default. Any cell fails: D3D11 is the fallback. The
  table goes in the plan and PERFORMANCE.md either way.

**Decided 2026-11-22: the binding.** No new package ("22MB is way too big"). Extend our hand-written
COM declarations (`src/OniMoono.Desktop/Gl/Dxgi.cs`, or a sibling for D3D11) to what the device
needs, with a generated check in the Windows build: a generator reads every declaration and emits a
C++ file MSVC compiles against the SDK headers, asserting each method's slot (`offsetof` into the
header's vtable struct), its exact signature (`static_assert(std::is_same_v<...>)` on the header's
function-pointer type), and each struct's size and field offsets. It covers the presenter's existing
declarations too.

## M2 part A: the binding and its check (2026-11-22, review fixes the same day)

- **Declarations** (`src/OniMoono.Desktop/D3d11/`): typed interface structs, each `lpVtbl` with
  a nested `Vtbl` whose methods sit at `FieldOffset(slot * 8)`, called as
  `device->lpVtbl->CreateBuffer(device, ...)`; header-named structs, enums and constants;
  `D3D11CreateDevice` and `CreateDXGIFactory2`. Each carries `[Native]` naming its header. Only
  what the device needs (the original's call set, `d3d11-captures.md` §3 and §5, and the
  inventory's checklist): 22 `ID3D11Device` methods, 40 `ID3D11DeviceContext` methods,
  `ID3D11Texture2D::GetDesc`, `ID3D11Debug::ReportLiveDeviceObjects`, 5 `ID3D11InfoQueue`
  methods, 20 DXGI methods on 15 interfaces, IUnknown's 3. No `ID3D11DeviceContext1`, deferred
  contexts or debug object names yet (M6 or when needed). `Gl/Dxgi.cs` keeps its API and calls
  through the same structs; its structs, constants, IIDs and DLL functions are annotated too.
- **The check.** `tools/ComCheckGen` reads every `[Native]` declaration by reflection on the
  built assembly (so it checks the layouts, offsets and function-pointer types the runtime uses,
  not source text) and writes `com_check.cpp`, which MSVC compiles twice, never runs: with
  `CINTERFACE` for the C vtables, then as C++ classes for `__uuidof`. It asserts:
  - each method's `offsetof(XVtbl, M)` and its exact type (`std::is_same_v` on the vtable member;
    the C type is derived from the C# one: `int` → `INT`, an `int` return → `HRESULT`, `uint` →
    `UINT`, pointers, enums and structs by name);
  - each IID, each declared base (`std::is_base_of_v`), IUnknown's `Release` at slot 2 for every
    interface;
  - each struct's size and every field's offset, exact type and size;
  - every enum's size, each enumerator's value and that it is an enumerator of that enum
    (`is_same_v<decltype(NAME), Enum>`), every constant's value;
  - each DLL function's type (`decltype(&F)`), and that the DLL named in its DllImport exports it:
    `com_check.cmd` looks each one up in `%SystemRoot%\System32` with `dumpbin /exports`.
  - C# has no const or typedefs, so `[Const(i)]` adds const to a pointee and `[As(i, "DWORD")]`
    names the native type. An `[As]` (on a parameter or a field) must take the native type's
    place (`abi_ok`): the same value type, an integer of the same size and signedness, or an enum
    for an integer of the same size (never an enum for another enum, never a float for an
    integer), pointers and references pointee by pointee. An untyped pointer or `IntPtr` for a
    typed pointer or a handle needs `Opaque = true`, and an Opaque that is not needed fails. Opaque
    keeps every level of indirection: it stands only for an innermost pointee that is void or a
    struct (`void*` for a `T*`, `IntPtr` for a handle, `IntPtr*` for a `HANDLE*`; never `void*`
    for a `T**`, nor `IntPtr` for a `HANDLE*`). The 19 Opaque uses are listed at the top of
    `com_check.cpp`: handles (HWND, HANDLE, HMODULE, HMONITOR) as IntPtr, and structs only ever
    passed as null or as an opaque buffer (DXGI_SWAP_CHAIN_FULLSCREEN_DESC, SECURITY_ATTRIBUTES,
    DISPLAYCONFIG_MODE_INFO) as void*.
  - Names: a `[Native]` name is an identifier the headers declare (a dotted path of identifiers
    for a struct field); a constant checked against an expression says so with
    `[NativeExpression("sizeof(X)")]`, which must name an SDK identifier, and each is listed at
    the top of `com_check.cpp` (3: the DISPLAYCONFIG struct sizes and `sizeof(BOOL)`).
  - Nothing is skipped silently. The generator sweeps the whole OniMoono.Desktop assembly: every
    DllImport, LibraryImport, unmanaged function-pointer field, `[UnmanagedFunctionPointer]`
    delegate and vtable struct, and every struct those take, is `[Native]` (and checked) or
    `[NotNative("why")]`, else it fails listing them all. A `[Native]` member the generator does
    not emit, a static field that is neither a constant, an IID nor a function pointer, a
    LibraryImport, and a `char` by value in a DllImport (ANSI by default) fail it too. Every
    `[NotNative]` member is listed with its reason at the top of `com_check.cpp`: 75 now, window,
    input and IME calls outside the renderer's binding, Linux libraries, GL extension functions
    (glext.h and wglext.h are not in the SDK: ARB_bindless_texture, WGL_EXT_swap_control,
    WGL_NV_DX_interop), and D3DDDIERR_DEVICEREMOVED (a WDK value). Function-pointer fields (a
    GetProcAddress'd function) are checked like DllImports and name their DLL.
  - Not checked: the casts at the call sites (`(IDXGIFactory*)factory`, `(ID3D11Resource*)tex`).
    The check proves each declaration is the header's and each declared base a real base class
    (`std::is_base_of_v`), not that a C# cast between interface pointers follows a declared base;
    a reviewer reads those against the `Base` the interface declares.
  - Totals now (after M3's storage filter, j0008637): 41 interfaces (94 methods), 49 structs (232
    fields), 35 enums (206 values), 61 constants, 51 IIDs, 18 DLL functions and their exports, 19
    Opaque, 3 expressions, 75 `[NotNative]`. (After M2's review fixes, j0008579: 92 methods, 47
    structs, 224 fields, 60 constants.) (Before the fixes the generator reported 200 values
    and 59 constants, not the 199 and 57 this line said.)
- **The SDK.** The check compiles against the Windows SDK and MSVC that `vcvars64.bat` picks (the
  newest installed), not a pinned one; `com_check.cmd`'s last line names both. Here: Windows SDK
  10.0.26100.0, MSVC 14.44.35207 (j0008218). CI's `windows-2025` runner may have a newer SDK;
  which one it has is UNVERIFIED until CI runs it.
- **Where it runs.** `scripts/com-check.sh` (generate and compile; generate only without a Windows
  side); `scripts/build-native-host.sh` stages it and `build.cmd` compiles it before the host
  (beside M1's `dxbc_check.cpp`), so a difference fails `build-windows.sh` and CI's Windows build;
  `check.sh` runs `com-check.sh`. `setup.ps1`'s from-source host build skips it (no generator).
- **Proof** (j0008335, one job, `tools/ComCheckGen/mutation_proof.py` on a `git archive` copy of
  219d022e0): the unchanged copy passes, first and last; each of 26 mutations, one per run, fails
  the check itself (an MSVC static_assert, the export lookup, or the generator; none breaks the
  C# build):
  - the first review's: slot (`Map` 14 → 15); signature (`DrawIndexed`'s `int` → `uint`); struct
    offset (`D3D11_DEPTH_STENCIL_VIEW_DESC.Texture2D` at 8); IID (`ID3D11Device`'s last byte); a
    field's `[As("LONG")]` over a `float`, and over a `uint`; a pointer `[As("UINT*")]` over
    `D3D11_TEXTURE2D_DESC*`; `void*` for `const SECURITY_ATTRIBUTES*` without Opaque; an Opaque not
    needed; an `[As]` enum over another enum; an enumerator put in another enum; a `char` in a
    DllImport; a static field that is not a declaration; a function-pointer field of the wrong
    type; a `[Native]` DllImport in a class without `[Native]` (now checked, and wrong); a
    LibraryImport; `CreateDXGIFactory2` declared in d3d11.dll;
  - the second review's: an enumerator named by an expression (`(D3D11_USAGE)7`); a constant
    named by a literal (`0x400`); an expression naming no SDK identifier; `ID3D11Debug` without
    `[Native]` and its slot moved; a DllImport in a class the check does not read; a
    `[UnmanagedFunctionPointer]` delegate and a function-pointer field without `[Native]` or
    `[NotNative]`; IntPtr by value for a `HANDLE*`; `void*` for an `ID3D11DeviceContext**`.
  - Earlier runs: j0008222 (three of its mutations only broke the C# build), j0008302.
- **Smoke test** (`tools/D3d11Smoke`, j0008219, light): a device on all 4 adapters, flags
  BGRA_SUPPORT and BGRA_SUPPORT | DEBUG, every value printed. Each equals `d3d11-captures.md` §4:
  both RTX 3090 entries (LUIDs 125ad and 1f659) and the AMD iGPU at FL 11_1;
  DriverConcurrentCreates 1; DriverCommandLists 1 on NVIDIA, 0 on AMD; all 14 D3D11_OPTIONS
  fields read 1 on all 8 devices; CheckFormatSupport 20/19/21/45 =
  0x3310b0/0x1210b0/0x4d217b0/0x3310b0, 10 = 0x12fef3f3 (NVIDIA), 0x32fef3f3 (AMD). The Basic
  Render Driver (software) also gives 11_1, DriverCommandLists 0, format 10 0x2fef3f3.
  - **The debug layer** (Graphics Tools installed): the debug device creates on every adapter
    (GetCreationFlags 0x22) with 0 stored messages. A buffer of ByteWidth 0 makes CreateBuffer
    return E_INVALIDARG with two ERROR messages read back through GetMessage (bounded by
    GetNumStoredMessagesAllowedByRetrievalFilter, as GetMessage's documentation says), ids 66
    and 69, which are D3D11_MESSAGE_ID_CREATEBUFFER_INVALIDDIMENSIONS and
    _CREATEBUFFER_INVALIDARG_RETURN in the header's enum: "ByteWidth cannot be 0".
    ReportLiveDeviceObjects(DETAIL) gives 8 messages (the live device and its internal
    context and default states).
  - **Release counts.** The last Release of each device, adapter and factory returns 0 (each
    printed; ID3D11Debug and ID3D11InfoQueue are interfaces of the device and return its count).
- **WGL_NV_DX_interop errors** (a fix for master, its own commit): DxgiTarget read the interop
  functions' failures with a GetLastError P/Invoke after the call, which the runtime's own calls
  may overwrite (Marshal.GetLastPInvokeError, DllImportAttribute.SetLastError). They are now
  called through delegates with SetLastError, and read with Marshal.GetLastPInvokeError.
  Observed working after the change: `--renderer opengl` through the DXGI presenter on the RTX 3090,
  900 frames, interop opened, registered and locked every frame with no failure logged (j0008281).

## M2 part B: device bring-up

- **B1, done** (`D3d11Context`, `D3d11SwapChain`, `D3d11Device`; `--renderer d3d11`, not in the
  Tweaks row, Windows only; `--d3d11-debug`): the adapter from `--gpu` (EnumAdapters1(0), or
  EnumAdapterByGpuPreference for discrete or integrated; software refused), a device at 11_1 or
  11_0 with BGRA_SUPPORT, and the device's own flip-model swap chain with the original's
  measured settings (`d3d11-captures.md` §1): 2 buffers, FLIP_SEQUENTIAL, R8G8B8A8_UNORM,
  BufferUsage SHADER_INPUT | RENDER_TARGET_OUTPUT (0x30), AlphaMode UNSPECIFIED, scaling STRETCH,
  ALLOW_MODE_SWITCH | FRAME_LATENCY_WAITABLE_OBJECT | ALLOW_TEARING, maximum frame latency 2,
  Present(1, 0) or Present(0, ALLOW_TEARING), resized with the window. It waits for the
  frame-latency object before every frame, the first included (changed by the review fixes
  below, finding 2). Whether the original waits on it is M4's (captures UNVERIFIED 1, moved).
  It clears and presents
  the back buffer, logs the debug layer's messages, and reports SystemInfo as the original's
  (DeviceType Direct3D11, `Direct3D 11.0 [level 11.1]`, the texture-format answers of
  `d3d11-unity-probes.md` §1). Draws, resources and readbacks are counted and dropped. A device
  that cannot start falls back to the auto choice. Observed: `--renderer d3d11 --d3d11-debug` on
  the RTX 3090 (j0008282, 900 frames to the main menu): level 11_1, the swap chain as above
  (2560x1369, tearing allowed), a clean exit, and no debug-layer message in the whole run (with
  nothing drawn yet).
  - `d3d11` is a command-line choice only (settings.json refuses it), and the Tweaks screen's
    renderer row shows locked when the running renderer is one it does not offer.
  - UNVERIFIED, M7's (SystemInfo parity): the version string on an 11_0 device; DeviceVendor on
    AMD and Intel (empty for now); MemorySizeMB (DedicatedVideoMemory in MiB) against the
    original's 24322 on the 3090.
- **B2: the draw path** (2026-11-22, branch `d3d11-m2`). Files: `D3d11Device.cs` (main thread),
  `D3d11Execute.cs` (device thread), `D3d11Resources.cs`, `D3d11Programs.cs`, `D3d11States.cs`,
  `D3d11Formats.cs`.
  - **Threading.** A `GlWorker` ("Direct3D 11") and a `GlDrawRecorder`, as the Vulkan device. The
    device thread alone uses the immediate context, and presents (the swap chain's Present,
    ResizeBuffers and its latency wait moved there): "your application should also use that same
    thread for [DXGI] operations, especially ... Present" ("Introduction to Multithreading in
    Direct3D 11"). The message pump's side is finding 3 of the review fixes. No deferred contexts, no command lists of
    D3D11's (the original uses none: captures §5).
  - **Programs.** `ShaderNative.ProgramsFor(4)` and `ShaderPrograms.D3d11` read M1's entries;
    each pass keeps its `D3d11Variants`. The recorder's variant key is chosen by a new hook,
    `GlDrawRecorder.SelectStages` (`D3d11Programs.Select`): each stage selected from its own list
    by `VariantSelection`, the (pass, vertex, pixel, geometry) choice kept per shader, its index
    the key. `D3d11Programs.Build` checks each stage's `D3d11Bindings` against its DXBC
    (`Check`), refuses hull/domain stages, inline samplers (M1 left their encoding unread; no game
    shader has one), structured buffers and multisample textures (logged once, not drawn), and
    makes the shaders from the shipped DXBC bytes unmodified. Each stage's cbuffers become
    blocks of `DeviceProgram.BlockSizes` (Unity's size rounded to 16) with their members as
    `VkPrograms.Uniform` lays them out (one register per matrix column: the original's cbuffers
    are column-major, probes §1); textures become `DeviceProgram.Samplers` with their t# and s#.
  - **Constant buffers.** One DYNAMIC buffer per (stage, slot, size), of that size, bound whole;
    `Map(WRITE_DISCARD)` only when the draw's bytes differ from what it holds ("the previous
    contents of the resource will be undefined", `D3D11_MAP`).
  - **Input layouts**, per program and mesh layout: an element for each input of the vertex
    stage's signature (the original always declares 14; UNVERIFIED there, 7). A channel the mesh
    has: slot = channel. One it lacks: its own slot, per instance with `InstanceDataStepRate` 0,
    which "takes special meaning, that the instance data should never be stepped at all"
    (Direct3D 11.3 Functional Specification §8.1.3), reading OpenGL's constant (white colour,
    (0, 0, 0, 1) position, zeros) from a 64-byte buffer. Missing components read 0, alpha 1
    (§19.1.3.3). Meshes: every channel in one DYNAMIC vertex buffer, 32-bit indices in a DYNAMIC
    index buffer, rewritten with WRITE_DISCARD; a replaced buffer is released while draws may
    still use it ("Direct3D 11 defers the destruction of objects", `Flush` remarks).
  - **Textures** as the original's formats where measured (probes §5: RGBA32/ARGB32/RGB24/
    RGBA4444 as R8G8B8A8, BGRA32, A8, R8, B5G6R5, the half and float formats, BC1/BC3/BC7),
    else as the OpenGL device reads them (`D3d11Formats.Of`; ARGB32's bytes reordered on upload,
    as D3D11 views cannot swizzle). Made DEFAULT with their texels as initial data; a re-upload
    into the same storage is `UpdateSubresource`. BC textures whose level 0 is no whole number of
    blocks are decoded. Samplers as the original's (MinLOD −FLT_MAX, MaxLOD FLT_MAX, NEVER,
    ANISOTROPIC above 1).
  - **Render textures**: a colour Texture2D (SRV | RTV, GENERATE_MIPS with levels), RTVs per level,
    a multisampled texture resolved before reading; depth R32G8X24_TYPELESS with DSV
    D32_FLOAT_S8X24 above 16 bits, R16_TYPELESS/D16 at 16 or less (probes §3, §5), SRV for a
    depth-only texture; new surfaces cleared (colour 0, depth 0 = far). The back buffer gets a
    D32_FLOAT_S8X24 depth surface (the original's screen depth, probes §2).
  - **States** (`D3d11States`, checked by `D3d11StatesTests` against the captures' §2 tables):
    DepthBias −trunc(units), SlopeScaledDepthBias −factor, clamp 0; comparisons reversed;
    `FrontCounterClockwise` 1 into a render texture, 0 into the back buffer (the colony's UI
    draws; that they target the back buffer is captures UNVERIFIED 6); colour factors in the alpha
    slots as their alpha forms (`D3D11_RENDER_TARGET_BLEND_DESC`; the original's Zero/SrcColor
    has alpha Zero/SrcAlpha).
  - **Targets.** Viewport and scissor turned over in the back buffer (top row first), kept in a
    render texture (rendered upside down); clears ignore the scissor; depth cleared to 1 − v.
    `RenderConventions(Vulkan: true, Flipped)`, as measured (probes §2, §3). The viewport puts
    clip y = 1 at the top (FS §15.6), so the programs carry no y negation; the pixel diff
    shows it. A texture is never sampled while it is a target (the default is read; logged once);
    binding a target first unbinds its sampled views. `Direct3DClip` and `AnimationLight` touch
    only the OpenGL device's GLSL, so nothing of them runs here.
  - **Readback.** `CopySubresourceRegion` into a STAGING texture and `Map(READ)`, which waits for
    the copy (`D3D11_MAP_FLAG_DO_NOT_WAIT`); the back buffer's rows turned over.
  - **Not yet** (M3–M4): the warm-up's prebuilding, AsyncGPUReadback, `CopyTargetRect`, loading
    animation and splash, frame repeats, multi-target draws into fewer targets.
  - **Pixel diff** (`scripts/d3d11-pixel-diff.sh`, `tools/PixelDiff/pixel_diff.py`): one Windows
    job runs ours twice in one run home (`--renderers A,B`, default opengl,d3d11), each capturing
    frame N of the main menu (`--capture`), with the game's clocks advancing 1/60 s a frame
    (`ONI_FIXED_FRAME_SECONDS`, new: `PlayerLoop.FixedFrameSeconds`), and compares: per-channel
    max and mean, the fraction over 0, 1, 2, 4, 8, 16 and 40, PSNR, a diff image.
  - **Results** (RTX 3090, the main menu, frame 600, 2560x1369 windowed, `--d3d11-debug`):
    - **The main menu draws** on D3D11 (j0008437, f19b3da24): every element of the OpenGL frame
      is there, the same at a glance (news tiles, BRUTAL tile, title, buttons, the scene, the DLC
      logos). Unit tests `D3d11StatesTests` and `RendererChoiceTests`: 28 passed (j0008439).
    - **Debug layer**: no error, no corruption; one warning kind, twice a run (j0008437, j0008445,
      j0008449): `D3D11_MESSAGE_ID_DEVICE_DRAW_RENDERTARGETVIEW_NOT_SET` (3146081 =
      `D3D11_MESSAGE_ID_D3D11_1_MESSAGES_START` + 353, d3d11sdklayers.h), "The Pixel Shader expects
      a Render Target View bound to slot 1 [and 2], but none is bound. This is OK, as writes of an
      unbound Render Target View are discarded." The device names the shader (f108dc615):
      `Klei/BatchedAnimationUI` (the menu's UI kanims), whose pixel stage writes SV_Target0-2,
      drawn into the one target the game sets. The original binds the same: in its recorded menu
      frame (d3d11-captures, j0008168 `report-menu.txt`) every one of the 41 draws has `rtv1`
      null. OPEN, for the user to decide: accept it (the original's binding, the API's
      documented discard), deny that one ID in the info queue's storage filter (new [Native]
      declarations), or bind throwaway targets at slots 1-2 for such draws (extra GPU writes the
      original does not make). **Decided by the user 2026-11-23: deny it** in the storage filter
      (M3, "The RTV warning").
    - **B1 re-run** with the measured swap chain (j0008432, 75e8f19f9): level 11_1, the swap chain
      as described, 900 frames, a clean exit, no debug-layer message.
    - **Pixel diff, whole frame**, fraction of pixels whose largest channel difference exceeds
      0 / 2 / 8 / 40, and PSNR (RGB):

      | Job | Pair | > 0 | > 2 | > 8 | > 40 | PSNR |
      |---|---|---|---|---|---|---|
      | j0008437 | opengl, d3d11 | 32.9% | 21.5% | 7.9% | 2.38% | 26.4 dB |
      | j0008445 | opengl, d3d11 | 31.9% | 20.7% | 9.0% | 3.25% | 25.0 dB |
      | j0008446 | opengl, opengl (baseline) | 32.0% | 23.4% | 10.0% | 3.81% | 24.1 dB |

      Two OpenGL runs differ as much as OpenGL and D3D11: the menu's animations are not in step
      between runs even with the fixed clock (the scene's machines, the smoke, a robot that walks
      in), so whole-frame numbers measure run-to-run animation, not the renderer.
    - **Pixel diff, stable pixels** (j0008449, `--baseline`: two OpenGL runs and one D3D11 run;
      the 987,875 pixels where the OpenGL runs differ, 28%, left out): > 0 11.10%, > 1 8.54%,
      > 2 7.28%, > 4 4.16%, > 8 2.75%, > 16 1.55%, > 40 0.62%; mean per channel R 1.03, G 0.84,
      B 0.76; PSNR 32.5 dB. In the diff image what remains is still animated content that happened
      to match in the two OpenGL runs (the machines, the smoke column, the robot), and faint
      outlines of the title and the DLC logos: the title's largest difference is 55 (mean 1.3)
      with the images aligned (a one-pixel shift either way raises the mean to 5-12), which M3
      explains (filtering or BC decoding of the logo textures). The menu buttons: largest 40,
      mean 0.23. A scene-set tolerance and deterministic scenes are M3's (done criterion 3).
  - **UNVERIFIED in B2** (each with the milestone that settles it; M3's outcomes are in "M3"):
    1. `FrontCounterClockwise` 0 into the back buffer: the colony's UI draws had 0, that they
       target the back buffer is captures UNVERIFIED 6 (M3; the menu shows no culling difference).
    2. Whether two textures of one program share a sampler slot (the first's state is taken): not
       counted in the data (M3, a ShaderSurvey count).
    3. B8G8R8A8 render textures made R8G8B8A8: the original's resource format for them is not
       measured (M3).
    4. Bilinear with anisotropy above 1 becomes ANISOTROPIC, which also filters between levels;
       the OpenGL device keeps nearest-level with anisotropy (M3, pixel diff of the world).
    5. The original renders the screen into an R8G8B8A8_TYPELESS texture, not the swap chain's
       buffer (probes §2, captures §1 inference); this device draws into buffer 0 (M4).
    6. Readback of a ScreenRows render texture reads its rows as stored, as VkDevice does (M3).
    7. The debug-layer warning above (BatchedAnimationUI's targets): **settled** by the user's
       decision, the ID denied in the storage filter (M3, "The RTV warning").
  - **Settled in B2**: Unity's DXBC carries no y negation (the item under "Conventions" below):
    with the projection as measured and the viewport's y (FS §15.6), the D3D11 frame comes out
    upright and aligned with OpenGL's (j0008437: the title's difference is least unshifted).


## M2 review fixes (2026-11-23, branch `d3d11-m2`)

The adversarial review of M2 (parts A and B) gave 10 findings and 3 minor ones. Each outcome, and
its class: documented (cited), observed (job, RTX 3090, NVIDIA driver 32.0.16.1088 as in M0's
runs), or moved to a named milestone with the reason.

1. **Process.** d3d11-m2 merged `d3d11` at 61788ffcf (M1 done and reviewed). The open M2
   UNVERIFIED items: captures 1 (does the original wait on the waitable; GetFrameStatistics) and
   2 (the frame's last draw) moved to M4, 3 (the duplicate RTX 3090) to M5, 4 (RenderDoc under
   the isolation) closed as not pursued, each with its reason in `d3d11-captures.md`; B1's wait
   note settled (finding 2) with the original's behaviour moved to M4; B1's SystemInfo items and
   M1's inline `m_Samplers` moved to M7 (SystemInfo parity; mods), above. B2's 1-6 name M3 or
   M4; 7 (BatchedAnimationUI's `DEVICE_DRAW_RENDERTARGETVIEW_NOT_SET`) stayed open for the user,
   who decided it in M3 (denied in the storage filter).
2. **The frame-latency wait** (documented, observed). The no-wait-without-vsync rule came from
   the DXGI presenter, whose reasons (its resync after repeats, the interop lock keeping a
   picture in flight) do not apply here. The device now waits as "Reduce latency with DXGI 1.3
   swap chains" says (step 4: "wait for the swap chain to signal ... before it begins rendering
   every frame. This includes the first frame"): once before the first frame, then after each
   Present, before the next frame renders, vsync or not, at most 1000 ms (the article's sample).
   A timed-out wait's signal is taken when it comes, so the frames in flight stay at 2. Nothing
   of the original's was measured to keep (captures UNVERIFIED 1, now M4's). Observed (j0008581,
   the main menu with `--d3d11-debug` and ONI_PRESENT_TRACE, vsync toggled at run time by
   `--input N:vsync:V`): with vsync the wait averages 15.7-16.1 ms a frame (60 Hz; Present
   itself 0.05 ms), without it 0.05-1.1 ms (largest 16 ms, the switch), no timeout in 1,500
   frames, and switching back and forth needs nothing more.
3. **Present and ResizeBuffers off the message-pump thread** (documented, observed; full screen
   moved to M4). Microsoft's two pages pull opposite ways: "Introduction to Multithreading in
   Direct3D 11" wants DXGI calls, "especially ... Present", on the thread that uses the
   immediate context, and calls using the immediate context concurrently with most DXGI
   functions invalid; "DXGI best practices" and the DXGI overview's "Multithread considerations"
   say never to let the pump wait on a thread whose DXGI calls send it messages. The overview
   names when they do: "Be careful that you never have the message-pump thread wait on the render
   thread when you use full-screen swap chains ... When a mode change occurs, ... Present1 calls
   ::SetWindowPos() or ::SetWindowStyle() and either of these methods call ::SendMessage()". So
   Present stays with the context on the device thread, and the device keeps the DXGI
   presenter's conditions that keep it out of that case: DXGI is told to leave the window alone
   (`DXGI_MWA_NO_WINDOW_CHANGES | NO_ALT_ENTER`), the swap chain is windowed only and never put
   in DXGI's full-screen state (no `SetFullscreenState` or `ResizeTarget`); full screen is the
   window's (GLFW). The main thread waits on the device thread at every frame's hand-over
   (`GlWorker.Submit`), not only in `ReadPixels` and `TakeFrameCounters`, so a Present or
   ResizeBuffers that sent the window a message would hang the run within a frame. Observed: in
   j0008581, 1,500 presents, a minimize and restore and a resize to 1280x720 (ResizeBuffers on the
   device thread while the main thread waited) went through. The Vulkan device's VkDxgiTarget and
   OpenGL's DxgiSwapChain do the same, measured over 300 resizes, minimizes and mode switches
   (ARCHITECTURE §3.5). **M4** owns full screen: any DXGI full-screen state there must either keep
   the pump from waiting on the device thread or move to the pump's thread with the context.
4. **Depth view size at a level above 0** (documented). FS §5.3.6.2 and §17.16: the depth view
   goes with colour views "of the same dimensions". A depth surface of another size than the
   colour views is now left unbound and logged once. The engine never asks for a level above 0
   (every `RenderTargetBinding` passes 0), and the OpenGL and Vulkan devices render level 0.
5. **Caps** (documented, observed). `SupportsTexture` and `SupportsRenderTexture` answer from
   what the device makes: D3d11Formats has the format (not CPU-decoded; encodable or block
   compressed, as VkCaps) and CheckFormatSupport has TEXTURE2D | SHADER_SAMPLE (textures) or
   RENDER_TARGET | SHADER_SAMPLE (render textures). Where that differs from the original's
   measured answers (d3d11-unity-probes.md §1) the device logs it at start-up; j0008581 on the
   RTX 3090: textures YUY2, RGB9e5Float, DXT1Crunched, DXT5Crunched and the 8 `_SIGNED` formats
   no (the original yes); render textures ARGBInt, RGInt, RInt, RGBAUShort and the other formats
   D3d11Formats.Color lacks no (the original yes to all 28). Done criterion 2 wants them equal:
   **M7** (SystemInfo parity) adds those formats or records the gap.
6. **Raw values under the check** (documented: MSVC). `D3D_NAME` (UNDEFINED, TARGET) and
   `D3D_REGISTER_COMPONENT_TYPE` are `[Native]` enums of d3dcommon.h, used for the signature's
   system values and component types; `SizeOfBool` is `[NativeExpression("sizeof(BOOL)")]`; an
   input layout whose constant inputs would need a slot at or past
   `D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT`, or more elements than
   `D3D11_IA_VERTEX_INPUT_STRUCTURE_ELEMENT_COUNT`, is refused and logged. Check green
   (j0008579); totals above.
7. **Messages past the info queue's limit** (documented, observed). `GetNumMessagesDiscardedBy-
   MessageCountLimit` (slot 10, checked) is read at each drain; growth is logged and counted in
   the run's total ("new messages coming in will push old messages out",
   SetMessageCountLimit). Whether ClearStoredMessages resets it is not documented, so a smaller
   value starts a new count. j0008581: 0 dropped.
8. **Minimized** (observed). The platform keeps a minimized window's framebuffer size (Unity's
   player does; GlfwPlatform), so the buffers keep theirs and bindings are not forgotten; a
   window 0x0 from the start is no longer resized every frame. j0008581, minimized with vsync
   (`--input N:minimize`, then `restore`, ShowWindow without activation): no timeout, waits of
   18.3-19.4 ms a frame on average (largest 33 ms), so about 52-55 frames a second, under the
   60 the original keeps minimized (ARCHITECTURE §3.5); the restore came back at full size.
9. **Pixel diff** (a9f01bf52): `--stable` compares RGB, as the fractions and PSNR do. The other
   statistical limits (whole-frame fractions under animation, no per-region tolerance) are M3's
   tolerance work (done criterion 3).
10. **Stale numbers**: the totals above, recounted from the generator (j0008579).

Minor:
- **Sample counts** checked with CheckMultisampleQualityLevels (S_OK and a positive count: its
  remarks), for the colour format and its depth's; else the next lower power of two, logged.
- **GENERATE_MIPS** only where CheckFormatSupport has MIP_AUTOGEN ("Automatic generation of
  mipmaps is supported"); without it the levels above 0 keep their clear, logged once.
- **Channels**: a draw binds a channel by the mesh layout alone, the input layout's test; the
  layout is recorded with its upload (`GlMeshes.AddChannel` sets both), so a layout channel with
  no uploaded data is a failed command rather than a read of an unbound slot's zeros (FS §8.20).

Seen in passing (not M2's, reported): the scripted `resize` (Screen.SetResolution on a window
GLFW reports maximized) restores it with `glfwRestoreWindow`, and j0008581's window took the
focus at that frame; later jobs here leave `resize` out.

Re-run after the fixes (j0008585, `d3d11-pixel-diff.sh --frame 600 --baseline -- --d3d11-debug`,
the frozen build `E:\oni-builds\oni-win-764025`, as j0008437/j0008449 on the Steam install):
the main menu draws on D3D11; the debug layer gives the same two BatchedAnimationUI warnings and
nothing else, none dropped. Stable pixels (the 1,003,208 where the two OpenGL runs differ, 29%,
left out; RGB mask now): > 0 8.98%, > 1 6.28%, > 2 5.10%, > 4 2.69%, > 8 1.73%, > 16 0.94%,
> 40 0.35%; mean per channel R 0.72, G 0.53, B 0.48; PSNR 34.0 dB (j0008449: 32.5 dB with the
RGBA mask, which left more animated pixels in).

## M3: a colony plays (2026-11-23, branch `d3d11-m3`), part A

Part A: a colony draws on D3D11 with the debug layer, the scene set and its tolerance are
defined and measured, and the M3 UNVERIFIED items are settled or moved. Part B (below, "Left
for M3 part B") is the rest of done criterion 1's coverage.

**Design.**
- **AsyncGPUReadback** (`D3d11Readback.cs`): the render texture's level 0 is copied
  (`CopySubresourceRegion`) into a STAGING texture with the frame's work; after each Present the
  device thread tries `Map(READ, D3D11_MAP_FLAG_DO_NOT_WAIT)`, which "should return
  DXGI_ERROR_WAS_STILL_DRAWING when the GPU blocks the CPU from accessing a resource"
  (`D3D11_MAP_FLAG`), and once it maps the rows are copied out (converted on a pool thread when
  the format asked is not the texture's, as VkReadback does). `Wait` maps without the flag. The
  Timelapser's colony preview (each save) and timelapse frames take this path
  (`Timelapser.CapturePng`, U60 764025).
- **CopyTargetRect**: `CopySubresourceRegion` between two render textures' level 0, whose
  resources "Must have compatible DXGI formats (identical or from the same type group)"
  (`ID3D11DeviceContext::CopySubresourceRegion`); the device asks for identical ones and
  logs a failed command otherwise. Rows turned over in a ScreenRows texture, as
  VkDevice.CopyTargetRect. Only the loading animation uses it, which M4 turns on
  (`AnimatesLoading` stays false here).
- **ScreenRows readback** (B2 UNVERIFIED 6, below): read turned over, as the back buffer.
- **Debug-layer attribution**: with `--d3d11-debug` the info queue is drained after every draw
  and its messages counted by (message, shader, target count, target name), each pair logged
  in full once, the counts every 600 frames (`d3d11 debug layer tally`) and at the end.
- **Coverage log**: each program's first draw logs `d3d11 coverage: SHADER sSUBpPASS vs#BLOB
  [keywords] ps#BLOB [keywords]` (`tools/PixelDiff/coverage.py` counts them).
- Nothing else was missing for a colony: render textures (MRT included: the world camera's
  MRT0/MRT1 targets, `Camera.SetTargetBuffers`), 2D arrays, 3D textures, readbacks
  (ScreenCapture, ReadPixels), every overlay's shaders and the starmap drew with M2's paths.

**The scene set** (done criterion 3; `tests/e2e/d3d11-scenes*.yaml`, run by
`scripts/e2e-windows/run.py --player ours` with `E2E_PLAYER_ARGS='--renderer R'`, the frozen
`E:\oni-builds\oni-win-764025`, 1280×720 windowed on monitor 2, RTX 3090): three colonies, each
paused after its load (paused frames are deterministic: `paused-overlays.yaml`):
- a small landing with four gases side by side: the world close and wide, all 17 overlays, the
  UI, the starmap and the save's colony preview;
- a small base with Duplicants, buildings and debris: the world close and wide, all 17
  overlays, the UI, the vitals, skills and database screens, the colony preview and the debug
  8x screenshot (`ScreenCapture.CaptureScreenshot(path, 8)`, 10240×5760, read back in bands);
- a mid-game Spaced Out colony with every DLC: the world close and wide, all 17 overlays, the
  UI, the starmap and the colony preview.
- Overlays are set through OniMcp's `switch_view` (the F-keys did not reach the mid-game
  colony). 70 shots in all. Not compared: the loading screens, drawn on D3D11 during each load but
  animated (M4 owns the loading animation); the research screen and the mid-game colony's
  starmap did not open through `open: {management: …}` in either renderer (the research shot
  equals the UI shot; the mid-game "starmap" shot shows the colony, 1.7% of its pixels unlike
  the UI shot's), so the starmap compared is the landing's.

**The tolerance and how it was chosen.** `tools/PixelDiff/scenes.py` compares each D3D11 shot
with an OpenGL run's, leaving out the pixels where two OpenGL runs differ and those within 3
pixels of them (`--grow 3`: content that changes between runs, the random dig cracks of tiles
and the pulse of status icons, matches in two runs here and there by chance; without the
growth those chance matches put up to 0.35% of pixels over 40 in M3's first comparison).
- **Spread** (OpenGL against OpenGL): two OpenGL runs differ in 0-3.1% of their pixels by more
  than 8 (animations not in step); measured over 4 OpenGL runs (two series), the method's own
  floor, a third OpenGL run against the first with the other's mask, is at most 0.110% over 16
  and 0.045% over 40 (the 8x screenshot; under 0.01% elsewhere), but for the mid-game colony's
  UI shot, which differed between the two spec versions.
- **Tolerance per scene**: at most 0.5% of the compared pixels over 16 in a channel and 0.1%
  over 40, and at least half the image compared. Small differences (2-8 levels) over 1-20% of
  the pixels are left to PSNR: they are the same with anisotropy off (B2 item 4) and are not
  localized.
- A scene over against OpenGL is checked against a Vulkan run, for information only: part A
  counted a scene where D3D11 is within 0.1% over 40 of Vulkan and OpenGL is not as OpenGL's;
  part B dropped that rule, because the original sided with OpenGL in the three such scenes.

**Results** (j0008619, the committed device, `--d3d11-debug`, against OpenGL runs j0008612 and
j0008613, Vulkan j0008617; `scenes.py`; per colony, the largest fraction of the compared pixels
whose largest channel difference exceeds each level, over the scenes within the tolerance):

| Colony | Shots | OpenGL spread > 8 (max) | Compared (min) | > 2 | > 8 | > 16 | > 40 | PSNR (min) | Within |
|---|---|---|---|---|---|---|---|---|---|
| Landing (gases) | 23 | 1.73% | 92% | 20.3% | 0.22% | 0.018% | 0.004% | 44.7 dB | 23 of 23 |
| Small base | 25 | 2.07% | 90% | 13.6% | 0.39% | 0.095% | 0.035% | 43.8 dB | 25 of 25 |
| Mid-game Spaced Out | 22 | 1.78% | 67% | 20.4% | 2.64% | 0.256% | 0.083% | 39.7 dB | 19 of 22 |

- 67 of 70 shots within the tolerance; the UI, management screens, starmap, colony previews
  (AsyncGPUReadback) and the 8x screenshot (ReadPixels in bands) among them. j0008614 gave the
  same verdicts.
- **Over** (3; part A first counted 2 of them as OpenGL's, which part B reversed): the mid-game
  colony's materials overlay (1.535% of pixels over 40 against OpenGL; D3D11 against Vulkan
  0.015%, OpenGL against Vulkan 1.572%), conveyor overlay (0.178%; 0.088% and 0.258%) and power
  overlay (0.883% over 16 and 0.124% over 40; 0.18% and 0.32%). The pixels are round pools of
  room light on buildings: on OpenGL those buildings are unlit (a cat tree purple where D3D11 and
  Vulkan draw it lit, cream). Part A took the agreement of D3D11 and Vulkan as OpenGL's error;
  the original, measured on Windows afterwards, agrees with OpenGL: D3D11 and Vulkan read the
  light buffer mirrored there; part B fixed it ("The light pools", part B).

**Debug layer** (`--d3d11-debug`, every D3D11 run): no error, no corruption, no message
dropped; before the storage filter below, one message, warning
`D3D11_MESSAGE_ID_DEVICE_DRAW_RENDERTARGETVIEW_NOT_SET` (3146081), attributed per draw:
Klei/BatchedAnimationUI, Klei/StatusItem, Klei/BatchedAnimationPstTemperature and
Klei/BatchedAnimation into the back buffer (one target), and Klei/Stipple and Klei/Unlit Transparent into `TempBuffer 1 1280x720` (one
target): 537, 710 and 1,309 messages in the three colonies' runs, nothing else.
The original binds the same single target for these: in its recorded colony frames
(j0008168) Klei/StatusItem and the three Klei/BatchedAnimationPstTemperature passes draw into
one 1024×768 target with `rtv1` null, the world shaders (Substance, TiledBlock, BatchedAnimation
and the rest) into MRT0 and MRT1 as here. The world shaders' single-target draws here go to
temporary render textures (`TempBuffer 1 1280x720`, `TempBuffer 8 1272x808` and
`… 1444x608`), not the world camera's MRT; which cameras' those are, and whether the original
binds one target there too, was not measured (its recordings are of the screen camera's
frames).

**The RTV warning** (B2 item 7; decided by the user 2026-11-23: hide it). `D3d11Context`
adds the ID to the debug layer's storage filter when the layer is on:
`ID3D11InfoQueue::AddStorageFilterEntries` ("Add storage filters to the top of the
storage-filter stack") with a `D3D11_INFO_QUEUE_FILTER` whose `DenyList` ("Types of messages
that you want to deny") names `D3D11_MESSAGE_ID_DEVICE_DRAW_RENDERTARGETVIEW_NOT_SET` alone.
The reason: the original binds the same way (above, and M2 B2), and the warning itself says
"writes of an unbound Render Target View are discarded". The new slots (AddStorageFilterEntries
12, GetNumMessagesDeniedByStorageFilter 7), the two structs and the ID constant are under the
MSVC check (j0008637). The device's own log line for a shader writing more targets than are
bound stays (`d3d11: shader '…' writes N render targets into M (…)`), and each tally line says
how many messages the storage filters denied ("Get the number of messages that were denied
passage through a storage filter", GetNumMessagesDeniedByStorageFilter). Observed (j0008638, the
landing's scene spec with `--d3d11-debug`): 0 debug-layer messages stored, 11,273 denied, the 27
shader-and-target lines logged. That count is not this warning's alone, as part A first wrote:
a `--check-shaders` run, which draws nothing, had 2,582 denied (part B, j0008662), so the
debug layer denies other messages by itself (which ones is not read); the tally line now says
"messages denied by the storage filters".

**Coverage** (done criterion 1; `coverage.py` over the D3D11 runs' logs): 46 of the 121
shaders, 48 of their 176 passes, 56 programs (vertex and pixel combinations), 112 distinct
stage entries of the 1,070 subprograms: every shader the
three colonies, the menus and the loading screens draw. No program failed to build or was
refused, none drew with a geometry stage (the one gs_4_0 is not drawn).

**UNVERIFIED items assigned to M3.**
- B2 1, FrontCounterClockwise 0 into the back buffer, with captures UNVERIFIED 6: **settled**
  (observed, j0008168's recordings): the original's FrontCounterClockwise 0 state is bound only
  with the views the main menu's whole frame and the colony's UI and last draw go to (the
  screen, rendered unflipped), every render-texture draw has 1. As this device does.
- B2 2, two textures sharing a sampler slot: **settled** (ShaderSurvey d3d11, j0008601): no
  subprogram of the game data (1,722 textures) or of the 2022.3+ mods read (497) has two
  textures naming one sampler slot; the first-texture rule never applies.
- B2 3, B8G8R8A8 render textures made R8G8B8A8: **settled for the game, moved to M7 for
  mods**. No game code makes one: the U60 764025 assemblies name only ARGB32, ARGBHalf, RFloat
  and RGFloat render textures (and Depth in a support query), and `new RenderTexture(w, h, d)`
  is ARGB32. Which of the two the resource is cannot change a pixel (both sample and read back
  as RGBA; ReadPixels converts by format); only `GetNativeTexturePtr`, which Moono does not
  offer, would show it. A mod's BGRA32 render texture is M7's to measure. (Measuring the
  original here would need its run's registry key and LocalLow folder, which this brief
  forbids.)
- B2 4, ANISOTROPIC filtering where OpenGL keeps the nearest level: **settled** (documented,
  observed). `D3D11_FILTER_ANISOTROPIC`: "Use anisotropic interpolation for minification,
  magnification, and mip-level sampling", and the original's colony samplers are ANISOTROPIC
  with MaxAnisotropy 4 (captures §2), so the device keeps it. With anisotropy off (a temporary
  build, j0008615) D3D11 was no closer to OpenGL (the landing's world: 21.9% of pixels over 2
  against 20.3% with it), so the small differences above are not this.
- B2 6, readback of a ScreenRows render texture: **settled** (documented). RenderDevice's
  ReadPixels and BeginReadback give rows from the bottom; "The texel coordinate system has its
  origin at the top-left corner of the texture" (Direct3D 11.3 Functional Specification
  §3.3.2), so a texture stored top row first is read turned over, as the back buffer was.
  Only the loading screen's frames are ScreenRows, and nothing reads them back (no game
  readback reaches them). VkDevice reads them as stored, which would give top-first rows: a
  latent difference there, reported, not changed here.
- Captures UNVERIFIED 5 (factor and units) closed for the game, 7, 8 and 9 moved to M7, each
  with its reason in `d3d11-captures.md`.

**Runs** (runq, burndown, owner `d3d11-m3`): builds j0008597, j0008603, j0008606, j0008611,
j0008616; ShaderSurvey j0008601, j0008618; scene runs j0008604 (first, Vulkan: the e2e
lane's renderer variable does not pass runq's environment filter, `E2E_PLAYER_ARGS` does),
j0008605 and j0008607 (first D3D11 colony runs), j0008608-j0008610 (first scene set: OpenGL
twice, D3D11), j0008612-j0008615 (OpenGL twice, D3D11, D3D11 with anisotropy off), j0008617 (Vulkan),
j0008619 (D3D11 with the debug layer, the committed device); check.sh j0008641 (green, on 85e3ae0b6); the storage filter: build and check j0008637, run j0008638. j0008600, the
first submission, waited behind a perf job's reservation and ran the landing's spec on Vulkan
later (the renderer variable did not reach it); its result is not used.

**Left for M3 part B** (part A's list; what became of each is in part B below): coverage (75
shaders not drawn by the scene set, and every pass and variant), and the mid-game overlays' light
pools, which part A took for an OpenGL-device finding.

## M3 part B (2026-11-23, branch `d3d11-m3`)

**How done criterion 1 is proven (decided here).** "Every pass and variant draws" cannot be
shown by play alone: a variant draws only when the game sets its keywords, and many are for
features the game never turns on (procedural instancing, MSAA copies, stereo). The proof has
three parts:

1. **Every subprogram is made on the device** (`--check-shaders --renderer d3d11 --d3d11-debug`,
   `D3d11Check.cs`): each player subprogram of every pass of every shader is selected by its own
   keywords through the draw path's own selection (D3d11Variants.Select must give back that
   subprogram), its program built as a draw builds it (D3d11Programs.Get: the bindings checked
   against the DXBC, `CreateVertexShader`/`CreatePixelShader`/`CreateGeometryShader` on the
   shipped bytes), and its input layouts made for a full mesh layout, no channels (every input a
   constant) and the immediate layout. `CreateInputLayout`'s `pShaderBytecodeWithInputSignature`
   is "A pointer to the compiled shader. The compiled shader code contains a input signature
   which is validated against the array of elements" (ID3D11Device::CreateInputLayout). Nothing
   is drawn. A refusal, a failure, a subprogram its own keywords do not
   select, a layout not made or any debug-layer message fails the run. This is the proof for
   variants play cannot reach: the device accepts each program and its inputs as the draw path
   would bind them; only the per-draw state (constant buffers, textures, targets) goes unproven
   for those.
2. **What the game draws in play, draws** (the scene set and `d3d11-coverage.yaml`, with the
   debug layer, pixels within the scene-set tolerance): the shaders the game's content reaches.
3. **What play does not draw is accounted for** (`ShaderSurvey shader-refs`): each shader no
   run drew, with the objects that reference it. A shader referenced only by GraphicsSettings
   and the ScriptMapper (Unity's always-included list and Shader.Find's name table) is reached
   only by an engine feature; one referenced by the game's materials, MonoBehaviours or
   Resources needs a scene, or a reason it cannot be reached.

**The validation run** (j0008704, the frozen `E:\oni-builds\oni-win-764025`, RTX 3090, debug
layer): 121 shaders, 176 passes, 1,070 subprograms (960 distinct entries), as M1's survey; every
subprogram selected by its own keywords; 504 programs built, 856 entries created, among them the
one geometry stage (Hidden/VR/BlitFromTex2DToTexArraySlice gs#8); 1,512 input layouts made, none
refused; no build failure and no debug-layer message. (j0008662, before ShaderCheck read
globalgamemanagers.assets: 120 shaders, 175 passes, 1,062 subprograms, the same refusals.)
The 104 entries not created are the 68 programs the device refuses (M2's B2 list), by shader:

| Shader | Entries | Why refused | Referenced by (shader-refs) |
|---|---|---|---|
| Particles/Standard Surface | 64 | structured buffers (`PROCEDURAL_INSTANCING_ON` variants) | a material (sharedassets0) |
| Particles/Standard Unlit | 14 | the same | 2 materials |
| Klei/Standard Unlit | 8 | the same | a material |
| Hidden/Physics2D/SDF_* (5 shaders) | 2 each | structured buffers | GraphicsSettings only |
| Hidden/FrameDebuggerRenderTargetDisplay | 6 | multisample textures (`_MSAA_*`) | a built-in material |
| Hidden/BlitToDepth_MSAA | 2 | multisample textures | GraphicsSettings only |

No code of Moono's engine sets `PROCEDURAL_INSTANCING_ON` or an `_MSAA_` keyword or uses
BlitToDepth or the Physics2D debug shaders (`grep` of `src/`), so no draw can select these
variants on any device; making them would need structured-buffer and multisample-texture
bindings the device does not have. **Open, for the user:** implement those bindings so the
check passes whole, or accept the 68 as unreachable and have the check list them apart (it
counts them as failures now, exit 1).

**Coverage in play** (`coverage.py` over the D3D11 runs: part A's j0008619 and part B's
j0008675, j0008694, j0008720):

| | Before (part A) | After (part B) | Of |
|---|---|---|---|
| Shaders | 46 | 49 | 121 |
| Passes | 48 | 51 | 176 |
| Stage entries (subprograms drawn, by blob) | 112 | 120 | 1,070 subprograms (960 entries) |
| Programs (vertex, pixel, geometry combinations) | 56 | 60 | |

New: Klei/Prioritizable (the priority tool over dig orders), Klei/PostFX/Range (a selected space
heater's RangeVisualizer) and Klei/PostFX/SkyVisibility (a selected telescope), from
`d3d11-coverage.yaml` (the small base, debug mode on, a space heater and a telescope built by the
setup; its five shots within the tolerance against two OpenGL runs, j0008693 and j0008703).
Tried and not drawn: Klei/BuildingCell (a selected heater in the power overlay drew no port
icon); the dev tools' ImGui (Ctrl+` is read through Input.GetKeyDown, DevToolManager.
UpdateShouldShowTools, which the Windows runs' synthetic input has no binding for); the
Spaced Out starmap, now open, drew no RocketPath (no rocket in flight in that colony).

**Not drawn in play** (72; `shader-refs`, j0008681, over the data's serialized files and the 15
UnityFS bundles under StreamingAssets):
- **34 referenced only by GraphicsSettings and the ScriptMapper**, Unity's built-ins: the
  Hidden blits (BlitCopyDepth, BlitCopyHDRTonemap, BlitCopyWithDepth, BlitToDepth,
  BlitToDepth_MSAA, ConvertTexture, Compositing), Internal-* (Colored, CombineDepthNormals,
  CubemapToEquirect, DebugPattern, GUIRoundedRect, GUIRoundedRectWithColorPerBorder,
  GUITexture, GUITextureBlit, GUITextureClip, GUITextureClipText, Loading, ODSWorldTexture,
  StencilWrite, UIRAtlasBlitCopy, UIRDefault), InternalClear, InternalErrorShader, the five
  Physics2D SDF shaders, TextCore's Distance Field SSD, the two VR blits, VideoDecode, and
  Sprites/Mask. No game object references them; a draw reaches them only through an engine
  feature. Moono's engine names four of them (Hidden/Internal-GUITexture in Graphics, Sprites/
  Mask, Hidden/InternalErrorShader for a material without a shader, and GUI/Text Shader,
  below), so those want a scene that makes the engine use them; the rest are proven by the
  validation run alone.
- **38 referenced by game objects** (materials, MonoBehaviours, Resources): the Klei shaders of
  features no scene reaches yet (AnimationInvisible, Area Visualizer, Biome/Unlit Transparent,
  BloomedParticleShader, BuildingCell, DLC4 DemoliorBackground and LargeImpactorCometShader,
  LargeImpactorFragments, LiquidSubstance, NorthernLights, the PostFX Desaturate, Fill,
  HueSaturation, LargeImpactorVisualizerShader, RocketLaunchCondition and ScannerNetwork,
  RocketPath, Standard Unlit, TiledWall), Lines/Colored Blended (NavPathDrawer), UI/Graphz,
  Unlit/ImGui, Unlit/VirtualCursorCutoutShader, GUI/Text Shader, Hidden/LineShader, the frame
  debugger's display, the TextMeshPro Surface, 2 Pass, Masking, Overlay and Sprite shaders,
  Legacy Shaders (Diffuse, VertexLit, Particles/Additive), Particles/Standard Surface and
  Unlit, and Standard. Each still wants a scene (rockets in flight, the impactor DLC, debug
  views, a gamepad's virtual cursor, IMGUI) or a reason it cannot be reached; until then the
  validation run is their only proof. Left for M3's close-out or M8's QA.
- Two shaders sit in a StreamingAssets bundle as well as in the data (Klei/LiquidSubstance and
  Klei/Substance in `expansion1_bundle`); the validation run builds the data's copies only.
  Which copy a draw uses, and the bundle copies' programs, are M7's (bundles; captures
  UNVERIFIED 8).

**The scene fixes** (the research screen and the mid-game starmap never opened through
`open: {management: …}` on any renderer):
- **Research.** ManagementMenu.ToggleScreen opens the research screen only when
  ResearchAvailable(), which needs a research station (`Components.ResearchCenters`) unless in
  instant-build or sandbox mode (U60 764025); the landing has none. Its shot moved to the small
  base, which has one, and now shows the tree. The tree moves in real time while the screen is
  open (a tech entry never held still for 30 frames within 1,800, j0008715-j0008717), so the
  comparison masks it and judges the side bar.
- **The mid-game starmap.** The colony reaches a victory imperative as it loads:
  ColonyAchievementTracker.BeginVictorySequence pauses, hides the HUD (ToggleVictoryUI, the
  management menu among it) and opens a StoryMessageScreen whose OnKeyDown consumes every key,
  so the starmap's action reached nothing and part A's UI shot was that message. The spec's
  setup now waits for the message, deactivates it (its `restoreInterfaceOnClose` is false, so
  closing it that way starts nothing; PROCEED would start the victory video) and shows the HUD
  again as the message does when it restores the interface (StoryMessageScreen.HideInterface
  (false)), with the hover text, and gives back the camera. The starmap (the cluster map) and the
  UI with its HUD are shot now.
- **The skills screen** (small base) was within the tolerance in part A and over in part B's
  first run: the Duplicant's portrait animates in real time, paused or not, and had matched in
  both OpenGL runs by chance. Its rect is masked; `scenes.py` now applies a shot's spec masks
  (`NAME.shot.json`) to every comparison of it.

**Scene-set results, part B** (`scenes.py`, after the light-pool fix below: D3D11 with the
debug layer j0008732; OpenGL j0008693 and j0008692, for the coverage spec j0008693 and j0008703,
for the small base j0008718 and j0008719; the tolerance and method of part A, now with each
shot's spec masks applied):

| Colony | Shots | OpenGL spread > 8 (max) | Compared (min) | > 2 | > 8 | > 16 | > 40 | PSNR (min) | Within |
|---|---|---|---|---|---|---|---|---|---|
| Landing (gases) | 22 | 1.74% | 92% | 20.4% | 0.22% | 0.018% | 0.003% | 44.5 dB | 22 of 22 |
| Small base | 26 | 2.10% | 89% | 13.6% | 0.29% | 0.098% | 0.037% | 43.7 dB | 26 of 26 |
| Mid-game Spaced Out | 22 | 2.05% | 86% | 20.6% | 0.94% | 0.397% | 0.022% | 42.8 dB | 22 of 22 |
| Coverage (small base) | 5 | 2.37% | 94% | 11.6% | 0.28% | 0.150% | 0.079% | 43.5 dB | 5 of 5 |

75 of 75 within; the research screen (its side bar), the mid-game UI with its HUD and the
cluster map among them. Before the fix (D3D11 j0008694, j0008720) 72 were: the mid-game
colony's materials (1.541% over 40), conveyor (0.175%) and power (0.122% over 40, 0.851% over
16) overlays were over, the light pools below. No debug-layer message was stored in any D3D11
run (the storage filter's denials apart).

**The light pools in the mid-game colony's overlays** (part A's "OpenGL differs" note, reversed).
The original, measured on Windows (D3D11, j0008657, another agent's run), agrees with OpenGL, not
with Vulkan and D3D11: pixels over 40 against the original in the materials, conveyor and power
overlays: OpenGL 0.093%, 0.198%, 0.411%; Vulkan 1.633%, 0.439%, 0.877%; D3D11 1.597%, 0.365%,
0.554%. So those three scenes counted as over the tolerance on D3D11 (and Vulkan), not as
OpenGL's, until part B fixed them. The cause and the fix:

- **The read.** Klei/BatchedAnimation's shipped pixel program (blob 3, `fxc /dumpbin`, j0008666)
  reads the light buffer at `unity_MatrixVP · unity_ObjectToWorld · (world position)`, then
  `uv = clip.xy · (0.5, −0.5) + 0.5` (`mad r1.zw, r1.zzzw, l(0, 0, 0.5, −0.5), l(0, 0, 0.5, 0.5)`,
  then `sample … t12` = `_LightBufferTex`). No `_ProjectionParams` correction. So the read is
  right only when the draw's `unity_MatrixVP` has the same y sign as the light buffer's own
  render: both flipped (render textures) or both not.
- **The original.** In its recorded colony and temperature-overlay frames (j0008168,
  `report-*.txt`), every world draw goes to a render texture with FrontCounterClockwise 1 (the
  flipped convention): Klei/BatchedAnimation, Conduit, Human and TiledBlock into MRT0 (9220),
  the light buffer (Klei/Lightbuffer) into 9FAA0. The `overlayNoDepth` camera's draws (by
  their layers, inferred: Klei/StatusItem; the KAnim post-processing passes
  Klei/BatchedAnimationPstTemperature, drawn with Graphics.DrawMesh on the Overlay layer from
  KAnimActivePostProcessingEffects.OnRenderImage)
  go into render texture 18A0, 1024×768, FrontCounterClockwise 1, with the depth view the overlay
  camera's last image effect (ClusterCoverPostFX) used; only the UI and the frame's last draw go
  to the screen surfaces (31A0, 2D20, FrontCounterClockwise 0).
- **Ours.** The overlay modes move the buildings they show to the MaskedOverlay layer and add it
  to `Camera.main`'s culling mask (OverlayModes, U60 764025: `Camera.main.cullingMask |=
  cameraLayerMask`); `Camera.main` is
  `overlayNoDepth` (CameraController.OnPrefabInit tags it MainCamera and the overlay camera
  Untagged). Ours renders that camera, which has no image effects (its components:
  NavPathDrawer and the visualizer effects; the overlay camera's chain is ColorCorrectionLookup,
  BloomEffect, SimDebugViewCompositor, KAnimActivePostProcessingEffects, GridCompositor,
  FogOfWarPostFX, CustomActiveScreenPostProcessingEffects, ClusterCoverPostFX,
  CameraReferenceTexture: j0008669), straight into the back buffer: the D3D11 device logs
  Klei/BatchedAnimation and Klei/StatusItem drawing into the back buffer (part A's
  attribution, j0008619). With the back buffer's unflipped `unity_MatrixVP` and the light buffer
  stored flipped, the program reads the light mirrored about the screen's middle row.
- **So** the difference was which target `overlayNoDepth` (a screen camera after a screen
  camera with image effects) renders into: the original renders it, with a render texture's
  conventions, into the render texture the overlay camera's image-effect chain ended in; ours
  rendered it into the back buffer. OpenGL flips nothing, so it read right.
- **The fix** (fec8443bd, after merging origin/master for e84be8586, Vulkan's fix). Vulkan gives
  a camera's back-buffer passes a render texture's projection and turns the viewport over (a
  negative height). Direct3D 11 cannot: "In all cases, Width and Height must be >= 0"
  (D3D11_VIEWPORT, Remarks). So the device takes the original's own route
  (`RenderDevice.CameraPassesOffscreen`, set by the Direct3D 11 device alone): every camera to
  the screen, and the last image effect of each, draws into the frame renderer's screen-sized
  render texture (`FrameRenderer.CameraScreen`, R8G8B8A8, depth 24, remade on a resize), with
  a render texture's conventions, which is drawn into the back buffer (Graphics.Blit, Unity's
  shipped Hidden/BlitCopy) before the overlay canvases. Observed: the device's target lines now
  name `CameraScreen` for Klei/StatusItem, BatchedAnimation, BatchedAnimationConduit,
  BatchedAnimationPstTemperature, BatchedAnimationShine and ConduitBall, as the original's 18A0,
  and the back buffer only for the UI (Klei/BatchedAnimationUI), as its 31A0 (j0008732).
- **Measured** (the other agent's shots of the original were in a worktree since removed, and
  this brief does not run the original on Windows; OpenGL, which matched the original within
  0.1-0.4% over 40 in j0008657, stands in): `gl-lightpool`, pixels over 40 against OpenGL
  (j0008733, the other OpenGL run j0008734 masking what moves): materials 2.009% before
  (D3D11 j0008694's same shot) to 0.001% after (j0008732), conveyor 1.107% to 0.003%, power
  0.872% to 0.003%; the liquid and gas pipe overlays 0.003% after; the normal view 0.014%.
- **UNVERIFIED** (M4, with B2's UNVERIFIED 5, the screen's intermediate texture): how the
  original's 18A0 reaches the screen surface (the recorder lists distinct draws, not copies;
  its draw 1283 into 31A0 reads a screen-sized texture, presumably that blit), and the rule by
  which Unity ends a frame's camera texture (the UI camera's draws went to 31A0). The device's
  hand-over before the overlay canvases is this branch's reading of those recordings.

**Runs** (runq, burndown, owner `d3d11-m3`): check-shaders j0008662 (first), j0008704 (final);
builds j0008661, j0008681 (with `shader-refs`), j0008730 (the light-pool fix); the fix's runs:
D3D11 j0008732 (`gl-lightpool` and the scene set), OpenGL j0008733 and j0008734 (`gl-lightpool`); ShaderSurvey dump j0008665 and `fxc /dumpbin`
j0008666 (Klei/BatchedAnimation and siblings, for the light pools); probe of the overlay
camera's components j0008667-j0008669 (j0008667: run.py not executable, run with python3 since;
j0008668: a Camera member ours lacks); scene and coverage runs: D3D11 j0008675 (the mid-game
setup failed on a null ResourceCategoryScreen, fixed), j0008694, j0008720; OpenGL j0008692
(the coverage spec's selection in the power overlay failed, fixed), j0008693, j0008703
(coverage only), j0008718, j0008719 (base only); base runs j0008711-j0008717 failed waiting for
the research tree to hold still (dropped for a mask; j0008712 and j0008713 cancelled);
check.sh j0008737 (green, on e085648a5, whose code is fa6f8162d's).

## Research so far (M0)

Reports from 2026-11-22 (scratchpad copies; the findings are summarized here so they survive):

- **Programs.** Every game shader carries D3D11 programs (121 shaders; 1,070 player subprograms,
  960 distinct entries; vs/ps 4.0 and 5.0, one gs_4_0). Entries share the Vulkan entry layout
  (magic 0x0C0A75BA, type, 4 statistics ints, keywords, payload, channel mask and pairs); the
  payload is a 38-byte Unity prefix (byte 1 SRV count, byte 2 cbuffer count, byte 3 sampler count)
  and one DXBC container (checksums valid 960/960). RDEF is stripped in 926/960; Unity's parameter
  blob gives D3D registers directly (t#, s#, b# 960/960; cbuffer sizes 2,517/2,517). One entry per
  stage; variants keyed per stage by the keywords it uses, and every Vulkan variant maps to an
  exact D3D11 program for both stages (590/590). No D3D11 `DISABLE_TEMPERATURE_PARTICLES` variant of
  LightBufferCompositor: the toggle does nothing on D3D11, as in the original Windows game.
  Feature level 11_0 needed (SM5 in particle instancing, BlitToDepth*, the VR blit).
- **Mods.** All 38 mods with shaders carry D3D11 programs (2,573 DXBC containers valid). 8 mods are
  D3D11-only in every bundle (drawn by neither device today); 29 ship a D3D11-only Windows bundle.
  Layouts: 2022.3/6000.3 as U60's; 2020.3 a different one (magic 0x0C07513C, bindings inline); 2018
  an older table format.
- **Bindings** (superseded by the decision above). Recommended then: TerraFX.Interop.Windows (MIT, ClangSharp-generated from the SDK
  headers; trimmable, AOT-compatible; 28.4 MB DLL untrimmed, or vendor its generated d3d11/dxgi
  files), plus the MSVC static-assert check. MSVC 14.44 and SDK 10.0.26100 are installed;
  `ID3D11Device` 43 slots, `ID3D11DeviceContext` 115, existing Dxgi.cs D3D11 slots match.
- **Conventions.** As Vulkan's: reversed Z, 0..w clip depth, UV origin at the top, render textures
  rendered flipped, top-left viewport/scissor. Different: the viewport flips y (no shader-side
  negation; that Unity's DXBC has none is observed in B2's pixel diff), constant-buffer packing, inputs
  bound by semantic, depth sampled through typeless formats. No half-texel offset; Gamma, so no sRGB.
- **Device.** Implements `RenderDevice` (`OniMoono.UnityEngine/Engine/Rendering/RenderDevice.cs`)
  and `IResourceWork`; draws arrive as `ProgramInputs` via `GlDrawRecorder`'s `RunDraw`. To change:
  `ShaderCompilerPlatform` (Metal, GLCore, Vulkan only), `ShaderNative.ProgramsFor`,
  `GlDrawRecorder.VariantsOf`, `RenderConventions(bool Vulkan, bool Flipped)`; turn off the OpenGL
  device's D3D11 imitations (`Direct3DClip` for Klei/Stars, `AnimationLight`, #76) on a real D3D11
  device. The flip swap chain is tied to its device, so the D3D11 device owns it (no shared
  texture or keyed mutex). `-force-d3d11` is ignored today (`Options.cs`).
- **Mods' API use** (2,898 mod folders, 3,641 DLLs, IL scan + decompiles; report
  `scratchpad/d3d11-research/mods-and-demand.md`). No mod P/Invokes d3d11/dxgi/opengl32/vulkan or uses
  native pointers, compute, `GetGPUProjectionMatrix`, `usesReversedZBuffer` or
  `graphicsUVStartsAtTop`. Only MAGNET!!! reads `graphicsDeviceType` (it logs it). Device-neutral:
  `AsyncGPUReadback` (Fast Save), `CommandBuffer` (Social Life). Bundles are picked by
  `Application.platform` (`WindowsPlayer` in Moono as in the original), so all 37 D3D11-only-on-Windows
  mods load their Windows bundle; 9 lose something visible today (criterion 6), 22 swap the
  bundle's shaders for the game's own first, 4 are broken for other reasons (FaceInfo, Proper
  Cyrillic Fonts, Mod GUI Pro). Issue #102's log confirms Capsule 2.0 and Portal Network undrawn on
  Vulkan. Of the 9, only Forcefield and the Chinese fonts use U60's entry layout; 7 are 2020.3 or
  2018.3.
- **Demand.** 10 user Player.logs from 8 issues, all Windows: D3D11 would plausibly help #109 (AMD R9
  200, Vulkan 1.2.154 loader, driver 20.11.3) and #101 (a crashed start-up guard, RTX 3050 Laptop),
  maybe #107 (Intel UHD; Vulkan hang with keyed-mutex timeouts, then device removed; unverified).
  No log shows the deny list, a discrete GPU lacking requirements or a DXGI-only GPU.
- **Probes of the original** (2026-11-22, RTX 3090, results in `docs/research/d3d11-unity-probes.md`).
  Reversed Z (`GetGPUProjectionMatrix` z row = Vulkan's `GpuProjection`); render textures flipped
  (`_ProjectionParams.x` −1, VP y negated), screen not; `graphicsUVStartsAtTop` True;
  `graphicsDeviceVersion` `"Direct3D 11.0 [level 11.1]"`; depth 24 → D32_SFloat_S8_UInt (R32G8X24
  typeless); `GetNativeTexturePtr` is the `ID3D11Texture2D`; RGB24/RGBA4444 → R8G8B8A8, Alpha8 →
  A8_UNORM; one DYNAMIC buffer per cbuffer, bound whole, undeclared members 0, matrices
  column-major; 66 of 147 format-support answers differ from GLCore (listed); every orientation a
  script sees is the same as GLCore's. Device FL 11_1, flags BGRA only, DriverCommandLists 1 on
  NVIDIA 610.88.
- **Tooling.** RenderDoc 1.43 installed; Windows SDK 10.0.26100.0 with fxc/dxc; the D3D11 debug layer
  installed 2026-11-22 (Graphics Tools optional feature, `Tools.Graphics.DirectX~~~~0.0.1.0`, by the
  user's UAC approval): `d3d11_3SDKLayers.dll` and `dxgidebug.dll` in System32.
- **Frames of the original** (2026-11-22, RTX 3090, results in `docs/research/d3d11-captures.md`;
  an in-process recorder, as RenderDoc's launch lost the run's isolation). Swap chain: 2 buffers,
  FLIP_SEQUENTIAL, R8G8B8A8_UNORM, flags ALLOW_MODE_SWITCH | FRAME_LATENCY_WAITABLE_OBJECT |
  ALLOW_TEARING, maximum frame latency 2; `Present(1, 0)` with vsync, `Present(0, ALLOW_TEARING)`
  without. Depth bias: DepthBias = −units (fraction dropped), SlopeScaledDepthBias = −factor, clamp
  0, unscaled on D32_S8 and D16. ZTest reversed (LEqual → GREATER_EQUAL), FrontCounterClockwise 1.
  Input layouts always 14 elements, absent channels R8_UNORM at slot 0 offset 0. Shaders created
  from the shipped DXBC (416/420 by checksum). DriverCommandLists 1 on the RTX 3090, **0 on the
  AMD iGPU** (runtime-emulated); both offer constant-buffer offsetting and NO_OVERWRITE. Colony
  frame (2minnow): 1,296 draws, 1,832 constant-buffer `Map(WRITE_DISCARD)` (507 KB), 116 shader
  pairs, 88 depth-stencil and 47 blend changes, all on the immediate context.
- **Open (M0 items):** the UNVERIFIED lists of `d3d11-unity-probes.md` and `d3d11-captures.md`
  (RenderDoc under the isolation, depth-bias rounding, the original on the AMD iGPU).
