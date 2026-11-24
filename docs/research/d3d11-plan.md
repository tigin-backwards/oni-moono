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
- **The player's own presentation** (decided by the user 2026-11-24). The player may draw its
  own presentation with programs of its own (the splash, the loading animation, the bars, the
  stretch quad), as VkSplash and GlSplash do on the other devices; the game's draws use only the
  shipped programs.
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
| M3 | Full draw feature set | A colony plays; scene-set pixel parity | part A 2026-11-23: three colonies draw, debug layer clean (the RTV warning denied, the user's decision), scene set and tolerance defined, M3 UNVERIFIED settled or moved. Part B 2026-11-23: criterion 1's proof decided (a validation run makes all 1,070 subprograms but the 68 refused programs; play draws 49 of 121 shaders; the rest accounted for by their references), the research and mid-game starmap scenes fixed, the overlays' light pools fixed as the original draws them (cameras to the screen into a render texture), 75 of 75 scenes within the tolerance. Part C 2026-11-23: the 68 refused programs accepted as unreachable (the user's decision; the check counts them apart and exits 0), the bundle's two shader copies checked, the 38 settled: 13 drawn (62 of 121 shaders in play), 20 shown unreachable, 4 reachable and not drawn, 1 not traced; 8 new scenes, 5 within (the sky judged against Vulkan, which equals the original there: the original's own D3D11 mirrors the northern lights against GL), 1 over (nav lines two rows thick) and 2 unjudged. Open: that one over (UNVERIFIED, M4: what the original draws), 5 shaders not drawn (4 reachable, Standard not traced), how the original hands its camera texture to the screen (UNVERIFIED, M4). Review fixes 2026-11-24 ("M3 review fixes"): 9 findings fixed or settled; draws to the screen during cameras land in the camera screen (the Timelapser's freeze frame), the debug layer stores everything but the RTV warning (nothing above INFO was hidden), the check exempts only the accepted 68, both UNVERIFIED items closed (the original's own lines are two rows thick), the 5 shaders drawn (67 of 121 in play), 83 of 85 scenes within. Open for the user: the impactor's 2 coverage shots unjudged (under half stable) |
| M4 | Presentation and frame pacing | Repeats, splash, loading animation, HDR, fullscreen/resize matrix | 2026-11-24 ("M4"): the original observed (its screen in a typeless texture drawn into buffer 0 last; its main thread waits on the waitable once a frame; nothing presented during a load); the device presents from two screen textures of its own, with repeats, the splash, the loading animation and refresh-paced frame deltas on the device thread; menu, loading animation and the landing's 22 scenes pass, debug layer clean. Open: captures UNVERIFIED 10 and the full-screen matrix (runs on the user's desktop, listed), the HDR check and Auto HDR (#112; no monitor in HDR), the mod-kanim loading spec (mod gate). Review fixes 2026-11-24 ("M4 review fixes"): 8 items fixed or recorded; the repeat's waitable pairing observed balanced over 1,001 repeats (the discarded case still UNVERIFIED); the presenter's own program accepted by the user. Desktop runs 2026-11-24 ("M4 desktop runs"): the original's full screen observed (captures 10 settled but for its chain inside exclusive), Loading Pip passes (0 pixels differ), ours through windowed/borderless/stretch/exclusive clean (j0009137). Focused exclusive observed (j0009185: the original's is DXGI's own, a blt DISCARD chain in DXGI's full-screen state; it goes windowed on losing the focus; ours minimizes). HDR on the HDR monitor (j0009190, j0009200): the original and ours composed as SDR at the 240-nit SDR white level, no Auto HDR boost (Auto HDR most likely off). Open, holding M4: whether to match the original's exclusive, our alt-tab back in, Auto HDR engaged (#112) |
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
  below, finding 2). Whether the original waits on it is M4's (captures UNVERIFIED 1, moved;
  settled in "M4": its main thread waits once a frame).
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
    animation and splash, frame repeats, multi-target draws into fewer targets. (M4 added the
    loading animation, splash and repeats: "M4".)
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
       **Settled in M4** (observed, j0009056, j0009060): it does, and draws that texture into
       buffer 0 last; the device now draws into typeless screen textures of its own.
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
bindings the device does not have. (Decided in part C: accepted as unreachable.) **Open, for the user:** implement those bindings so the
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
- **Closed in the M3 review fixes** (finding 1; was UNVERIFIED, M4): how the
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

## M3 part C (2026-11-23, branch `d3d11-m3`)

Part C: the 38 game shaders part B left undrawn, the bundle's two shader copies, and the 68
refused programs.

**The 68 refused programs: accepted as unreachable** (the user's decision, 2026-11-23). The
evidence: no code of Moono's engine sets `PROCEDURAL_INSTANCING_ON` or an `_MSAA_` keyword, or
uses BlitToDepth_MSAA, the frame debugger's display or the Physics2D debug shaders (part B's
`grep` of `src/`); no device of Moono's binds either kind (the engine has no `SetBuffer`, the
Vulkan device binds a zeroed 256-byte placeholder to storage-buffer slots, `emptyStorage`, and
refuses multisample-texture variants, `VkPrograms`; the OpenGL device binds no storage buffers
and has no multisample textures, `GlPrograms`). Revisit in M7 only if a mod needs them.
`--check-shaders` now logs each as `d3d11 check: refused, unreachable: SHADER sSpP vs#… ps#…,
program N (why)`, counts them apart and exits 0 when nothing else fails (`D3d11Check.cs`;
j0008854: 68 refused, unreachable; 0 failed, 0 refused, 0 debug-layer messages).

**The bundle's copies** (part B: "M7's"). Adding them was cheap: `ShaderCheck` loads
`StreamingAssets/expansion1_bundle` as the game does (`AssetBundle.LoadFromFile`) and makes each
of its shaders an object, so the device checks Klei/LiquidSubstance's and Klei/Substance's bundle
copies too (j0008865: 123 shaders, 178 passes, 1,082 subprograms, 510 programs built, 1,530 input
layouts, the same 68 refused and unreachable, nothing else). Which copy a draw uses stays M7's.

**How each of the 38 was settled.** Each shader's referencing objects were named
(`ShaderSurvey shader-refs DATADIR NAME…`, two levels: a material by name, a MonoBehaviour by its
script and GameObject; j0008826), every material's shader listed (`ShaderSurvey materials`,
j0008843), the components that would use them looked for in the data (`ShaderSurvey
script-uses`, j0008841), and the U60 764025 code read for every `Shader.Find`, `Resources.Load`,
`Assets.GetMaterial` and material field. Byte matches of small path IDs in the second level are
mostly not PPtrs (texture, text and UI component bytes); only named components count as
evidence. `Assets.ShaderAssets`/`Assets.Shaders` (the GameAssets list holding most Klei shaders)
and the ResourceManager paths keep shaders in the build; no code reads `Assets.Shaders` or loads
a shader by its Resources path.

New scenes (ours only, Windows, `E2E_PLAYER_ARGS`, the frozen build), each spec's setup making
the state the game makes, as the coverage spec does:
- `d3d11-coverage-particles.yaml` (the landing): the effect prefabs the game instantiates
  (`EffectPrefabs`: HappySingerFX, HugFrenzyFX, SparkleStreakFX, MissileSmokeTrailFX,
  LongRangeMissileSmokeTrailFX, PlantPollinated, MeteorBackground), as HappySinger, HugMonitor,
  SparkleStreaker, MissileProjectile, MissileLongRangeProjectile, PollinationVFXMonitor and
  MeteorShowerEvent do, one second at speed 1.
- `d3d11-coverage-debug.yaml` (the small base, debug mode): the dev tools (Ctrl+` reads
  Input.GetKeyDown, which the synthetic input does not reach, so setup sets `showImGui` and
  `UserAcceptedWarning` with the driver's new `setup.set`), a Duplicant's navigation grid
  (NavPathDrawer.SetNavigator, what the Draw paths button calls) and a selected Space Scanner.
- `d3d11-coverage-rockets.yaml` (the Spaced Out rockets fixture): the small habitat selected.
- `d3d11-coverage-impactor.yaml` (the Prehistoric pack's colony): the research portal's event,
  as `demolior-pointer.yaml` raises it, 20 seconds at speed 1.
- `d3d11-coverage-sky.yaml` (the same colony): the world's `largeImpactorFragments` (1,
  ALLOWED) and `northernlights` (1) and the tracker's `largeImpactorState` (Defeated) set, the
  sky above the surface revealed.
- Dropped: a gamepad spec. Setting `KInputManager.currentControllerIsGamepad` (what a gamepad's
  input sets) drew Unlit/VirtualCursorCutoutShader but threw a NullReferenceException in
  `VirtualInputModule.ActivateModule` (ours; the original not run) and the shot was the crash
  screen (j0008855). No comparable shot.

Results (`scenes.py`, part A's tolerance and method; D3D11 with the debug layer j0008866, OpenGL
j0008867 and j0008868, Vulkan j0008871, the sky's reference; no debug-layer message stored):

| Scene | OpenGL spread > 8 | Stable | > 16 | > 40 | PSNR | Verdict |
|---|---|---|---|---|---|---|
| particles | 0.77% | 97.4% | 0.078% | 0.043% | 40.5 dB | within |
| debug/devtools | 2.00% | 93.8% | 0.122% | 0.092% | 33.7 dB | within |
| debug/navgrid | 2.29% | 94.3% | 0.137% | 0.128% | 31.8 dB | **over** (> 40) |
| debug/scanner | 2.25% | 94.2% | 0.010% | 0.001% | 46.8 dB | within |
| rockets/launch-conditions | 0.13% | 99.7% | 0.006% | 0.000% | 58.7 dB | within |
| impactor/after-reveal | 7.05% | 34.6% | 0.331% | 0.269% | 38.1 dB | unjudged |
| impactor/impactor-world | 3.69% | 45.4% | 0.017% | 0.002% | 53.6 dB | unjudged |
| sky (against Vulkan, below) | 0.00% | 99.8% | 0.007% | 0.000% | | within |

- **navgrid** (over by 0.028 points). The differing pixels are the grid's horizontal lines
  (GL.Begin lines through Lines/Colored Blended): OpenGL draws them one row thick, Vulkan one
  row (the row above OpenGL's), D3D11 two rows (both). The device's rasterizer has
  MultisampleEnable 1, as the original's measured state (captures §2). "Quadrilateral lines take
  2 endpoints and turn them into a simple rectangle with width 1.4f, drawn with triangles"
  (D3D11.3 Functional Specification §3.4.5), and D3D11_RASTERIZER_DESC's table gives
  quadrilateral lines for MultisampleEnable TRUE; its Remarks also say both settings "apply only
  to multisample antialiasing (MSAA) render targets", and this target has one sample, so the
  documentation does not settle which algorithm a single-sample target gets. Observed (RTX
  3090): two-row lines, as a 1.4-wide rectangle would give. Whether the original's lines are two
  rows thick too was UNVERIFIED here; **settled in the M3 review fixes** (finding 5: they are,
  and the scene is judged against the original).
- **sky** (within, judged against Vulkan). Klei/NorthernLights' shipped vertex program writes the clip position's x and
  y from the mesh unchanged (`mov o0.xy, v0.xyxx`, `fxc /dumpbin`, j0008876), a quad already in
  clip space with no projection, so its image is turned over in a target whose rows run the
  other way: D3D11 draws the world camera into render textures with the flipped convention (as
  the original, captures §2). D3D11 and Vulkan agree exactly (0.000% over 40), OpenGL differs
  (5.75%): the aurora is mirrored top to bottom between them. **Settled against the original**
  (the coordinator's run, j0008881, `tests/e2e/sky-northern-lights.yaml` on master): the
  original's own D3D11 against ours on Vulkan 0.000% of pixels over 40, its GLCore against ours
  on OpenGL 0.001%, its two renderers 20.8% apart. The mirroring is the original's own
  behaviour, so D3D11 is right; `scenes.py` judges this scene against the Vulkan run
  (`VULKAN_REFERENCE`), which equals D3D11 there: 0.007% over 16, 0.000% over 40. Klei/
  LargeImpactorFragments and DemoliorBackground draw in the same shot.
- **impactor** (unjudged). The planet's material gets `_Random` from Random.value and
  `_UnscaledTime` every frame (ParallaxBackgroundObject.LateUpdate) and the reveal animates in
  real time, so two OpenGL runs agree on under half the image. Drawn, not judged.

Coverage in play (`coverage.py` over the D3D11 runs: part B's and j0008866, j0008858, j0008855):

| | Before (part B) | After (part C) | Of |
|---|---|---|---|
| Shaders | 49 | 62 (61 in the committed specs) | 121 |
| Passes | 51 | 64 | 176 |
| Stage entries | 120 | 146 | 1,070 subprograms |
| Programs | 60 | 73 | |

**The 38** (each with what references it in the data and the code that would draw it):

| Shader | Outcome | Evidence |
|---|---|---|
| Klei/BloomedParticleShader | drawn, within | particles (hearts_fx, music_notes, music_notes_2, Sparkle) |
| Particles/Standard Surface | drawn, within | particles (smoke_trail) |
| Particles/Standard Unlit | drawn, within | particles (missile_sparkle, pollinated_fx) |
| Legacy Shaders/Particles/Additive | drawn, within | particles (meteorshowerparticle) |
| Unlit/ImGui | drawn, within | debug/devtools (the ImGui material, ImGuiRenderer) |
| Klei/PostFX/ScannerNetwork | drawn, within | debug/scanner |
| Klei/PostFX/RocketLaunchCondition | drawn, within | rockets |
| Lines/Colored Blended | drawn, over | debug/navgrid, above |
| Klei/NorthernLights | drawn, within (against Vulkan, which equals the original) | sky, above |
| Klei/LargeImpactorFragments | drawn, within (the same shot) | sky |
| Klei/DLC4/DemoliorBackground | drawn, unjudged; also in sky | impactor (BGPlanet, ParallaxBackgroundObject) |
| Klei/PostFX/LargeImpactorVisualizerShader | drawn, unjudged | impactor |
| Unlit/VirtualCursorCutoutShader | drawn once, no comparable shot | the dropped gamepad spec, above |
| Klei/Standard Unlit | reachable, not drawn | long_smoke_trail on LongRangeMissileSmokeTrailFX's TrailFX2; instantiated in particles, nothing drew (why not traced: open) |
| Klei/AnimationInvisible | reachable, not drawn | only LargeComet sets MaterialType.Invisible: the Demolior's crash, at the event's end |
| Klei/DLC4/LargeImpactorCometShader | reachable, not drawn | LargeComet (Shader.Find), the same |
| TextMeshPro/Sprite | reachable, not drawn | 13 TMP sprite-asset materials; `<sprite>` tags in storage rows (germs, food states: SimpleInfoScreen), bionic battery and Bonbon branch text, controller glyphs (SteamInputInterpreter) |
| Standard | not traced | Default-Material, held by a MeshRenderer on a WaterExplode object; its use not traced |
| Klei/BuildingCell | unreachable | BuildingCellVisualizerResources.Initialize makes its 10 materials; nothing reads them (the port icons are UI images: EntityCellVisualizer.DrawUtilityIcon) |
| Klei/RocketPath | unreachable | no material, no Shader.Find of it; only GameAssets and Resources keep it |
| Klei/TiledWall | unreachable | its one material, walls_insulation, is never asked for (Assets.GetMaterial names only tiles_solid and BGPlanet) |
| Klei/Area Visualizer, Klei/PostFX/Desaturate, Fill, HueSaturation | unreachable | no material, no Shader.Find, no Resources.Load of their paths |
| Klei/Biome/Unlit Transparent | unreachable | gasFlowVis and liquidFlowVis sit in Game's private gasFlowMaterial and liquidFlowMaterial, which no code reads |
| Klei/LiquidSubstance | unreachable | the liquids' materials (matLiquid, matWater, …, both copies) are read only by LiquidTileOverlayRenderer, which no scene, prefab or code adds (script-uses: none) |
| UI/Graphz | unreachable | its material Graphz is only a Resources path; nothing loads it |
| GUI/Text Shader | unreachable | the fonts' Font Materials; only TextMesh draws with them, used only by TextMesh Pro's example scripts, none of which is in the data |
| Hidden/LineShader | unreachable | RTEditorGUI's line material, for the NodeEditor's RuntimeNodeEditor, which the data does not hold |
| Hidden/FrameDebuggerRenderTargetDisplay | unreachable | the editor's frame debugger; its variants are among the 68 |
| TextMeshPro/Distance Field (Surface), Mobile/Distance Field (Surface), - 2 Pass, - Masking, Overlay | unreachable | each only in TextMesh Pro's sample materials, loaded by name only by its example scripts (none in the data); no `material=` tag in the game's text |
| Legacy Shaders/Diffuse, VertexLit | unreachable | no material uses them; other shaders name them as Fallback, which applies only to a shader the device cannot run, and every game shader's program builds |

**Driver.** `setup.set {target, member, value}` (E2eDriver, setup only, as setup.call): a field
or property set by reflection, for state no method sets.

**Left open** (each taken up in "M3 review fixes", below). The navgrid UNVERIFIED item above;
Klei/Standard Unlit, AnimationInvisible, LargeImpactorCometShader, TextMeshPro/Sprite and
Standard not drawn; the two impactor shots unjudged; VirtualCursorCutoutShader without a
comparable shot.

**Runs** (runq, burndown, owner `d3d11-m3`): ShaderSurvey j0008823 (a script without its
interpreter line), j0008824 and j0008826 (refs), j0008841 (script-uses), j0008843 (materials);
builds j0008848, j0008862; check-shaders j0008854 (unreachable counted apart), j0008865 (with
the bundle); first D3D11 runs j0008855 (particles and gamepad drawn; the others' setup
expressions fixed), j0008858; the comparison: D3D11 j0008866, OpenGL j0008867 and j0008868,
Vulkan j0008871; NorthernLights' DXBC dumps j0008870, j0008872, j0008874 and `fxc /dumpbin` j0008873, j0008875,
j0008876 (the first ones' scripts were wrong); check.sh j0008877 (failed: ruff's format of
scenes.py), j0008896 (green, on b587caf69).

## M3 review fixes (2026-11-24, branch `d3d11-m3`)

The adversarial review of M3 (parts A to C) gave 9 findings and a caveat. d3d11-m3 merged
origin/master first (329aefa77; master's d92cd6220 is 31c4fe0f3's cherry-pick). Each finding's
premise was checked before any change. Each outcome is classed documented (cited), observed (job;
RTX 3090, the machine and driver of M2's runs) or open (with the reason).

1. **Draws to the screen while cameras render** (premise observed; fixed, b3c2012ef). While the
   cameras render on this device, RenderTexture.active null, Graphics.Blit to null and a command
   buffer's SetRenderTarget to null all bound the back buffer. The hand-over then copied the
   camera screen over it. The game's case is the Timelapser's freeze. Timelapser.Render
   (U60 764025) enables the timelapseFreezeCamera, a copy of the UI camera three above it in
   depth, for the frame in which it moves the camera for a timelapse or a save's colony preview.
   That camera's FillRenderTargetEffect.OnRenderImage is `Graphics.Blit(fillTexture, null)`.
   `d3d11-timelapse-freeze.yaml` holds that frame with a white fill:
   - before the fix, D3D11 showed the moved world and OpenGL the fill (j0009008);
   - after it, D3D11 shows the fill (j0009015, j0009023).

   **The fix:** while RenderScreen's cameras render, a null target means the camera screen
   (FrameRenderer.ScreenBinding and ScreenTexture). ReadPixels from null reads it too.

   **Measured against the original** (j0009025, the same spec on its Direct3D 11), pixels over
   40 with the OpenGL pair's unstable pixels left out:

   | Run | Over 40 against the original |
   |---|---|
   | D3D11 after the fix | 1.184% |
   | OpenGL | 1.180% |
   | D3D11 before the fix | 91.4% |

   The 1.18% the fixed D3D11 and OpenGL share is the players' own UI differences. The scene is
   in the scene set, within the tolerance against OpenGL (0.085% over 40).

   **The hand-over** (part B's UNVERIFIED, which the Rules require M3 to close). Closed as
   observed. In the original's colony frame (j0008168, `report-colony.txt`) the screen
   camera's texture reaches the screen surface 31A0 in draw 1283:
   - draw 1283 is the first draw into 31A0 after the last into 18A0 (1238);
   - its program pair is the one draw 1163 uses (made before the hook: Unity's built-in blit);
   - it uses the full-screen quad's vertex buffer (stride 20) and reads one 1024x768 R8G8B8A8
     texture;
   - the UI's draws follow it into 31A0.

   The recorder logs views, not resources, so that texture is not proven to be 18A0's.

   Ours copies after every camera, the UI camera's draws included, not before them. No pixel
   can tell these apart: a draw blended over the copy or over the texture before the copy
   meets the same colours, and the back buffer's alpha is not shown. Measured on the screens
   where routing matters, against the original:
   - the light pools (part B);
   - the freeze frame (above);
   - the navigation grid (finding 5: 0.000% over 40).

   Routing only the cameras the original routes was not taken: nothing measured separates the
   two.
2. **What the debug layer denied** (premise observed; fixed, 066157a9b and a40e78367).
   - **The default filter.** The layer starts with a storage filter of its own:
     `allow {none}, deny {severity INFO}`, one filter on the stack, an empty retrieval filter
     (logged at start, j0009015). The device now pushes an empty storage filter and an empty
     retrieval filter ("An empty storage filter allows all messages to pass through",
     PushEmptyStorageFilter; PushEmptyRetrievalFilter likewise). It then denies only
     DEVICE_DRAW_RENDERTARGETVIEW_NOT_SET. So GetNumMessagesDeniedByStorageFilter counts that
     warning alone.
   - **What had been denied.** In the draw-free check (j0009015) that is 9 messages, from the
     menu's frames. The 2,623 others are all INFO: object creation and destruction (Create and
     Destroy of textures, views, buffers, shaders, layouts, samplers and states, IDs 2097228 to
     2097276 and 3146250). The play runs (j0009022, j0009023) add INFO 107 (CreateTexture2D:
     "would use more than 128 MB of application usable memory. This is fine") and INFO 49
     (OMSetRenderTargets forcing slot 0 to NULL at a FLIP_SEQUENTIAL Present). Nothing above
     INFO was hidden. INFO and MESSAGE are now stored, counted and logged by ID apart, and only
     the other severities count.
   - **The message count limit.** With INFO stored, the queue's limit (1,024 at start) filled
     between drains while a colony loaded: 5,557 messages dropped (j0009020). The limit is now
     lifted ("-1 means no limit", SetMessageCountLimit), and the queue is cleared at each drain.
   - **Result.** The scene-set runs (j0009022, j0009023): no message above INFO, none dropped.
     Denied 528, 1,162 and 1,269 in the three colonies, the RTV warning alone (part A: 537,
     710, 1,309).
   - **The DirectX Control Panel.** It plays no part. `reg query HKCU\Software\Microsoft\Direct3D
     /s` (read only, j0009015) found no key: the control panel has set nothing for this user.
     The layer's INFO filter is its own default.
   - **The MSVC check.** The eight new info-queue slots are under it: SetMessageCountLimit,
     GetMessageCountLimit, GetStorageFilter, PushEmptyStorageFilter, GetStorageFilterStackSize,
     GetRetrievalFilter, PushEmptyRetrievalFilter and GetRetrievalFilterStackSize (check.sh j0009029).
3. **The check's exemption** (premise verified in the code; fixed, e1701faf5). Before, any
   refusal for structured buffers or multisample textures counted as refused and unreachable.
   - Now D3d11Programs.AcceptedUnreachable names the accepted set, by shader and keyword:
     - the PROCEDURAL_INSTANCING_ON variants of Particles/Standard Surface, Particles/Standard
       Unlit and Klei/Standard Unlit;
     - the five Hidden/Physics2D/SDF_* shaders;
     - Hidden/FrameDebuggerRenderTargetDisplay's `_MSAA_*` variants;
     - Hidden/BlitToDepth_MSAA.
   - Any other refusal fails the check.
   - A stage entry no program made is a failure unless it belongs to an accepted program.
   - j0009015: the same 68 programs exempted; all 104 entries not created belong to them
     ("0 entries not created otherwise"); 0 failed; exit 0.
4. **scenes.py** (premises verified in the code; fixed, b0aa4b8d5).
   - **(a) Missing shots.** The shots are those the spec names (`shot`, `world_shot`,
     `file_shot`). A spec folder or shot missing from any run is a failure.
   - **(b) Scenes judged against another renderer** (now `REFERENCE`: Vulkan or the original):
     - the other run's size note is a verdict;
     - the share compared must meet MIN_STABLE.
   - **(c) The share is of the whole image.** The spec's masks count against it. One scene
     masks more than half: the research screen, 71%, whose tree moves in real time (part B).
     Its claim is lowered to the side bar (`SMALLER_SHARE`, at least 20%; it compares 29%).
   - **(d) The sky.** Judging the sky against Vulkan is the user's decision (2026-11-23, "Yeah
     can check Vulkan", on the northern lights' mirroring). The evidence is the original's own
     (j0008881, part C).
   - **What 0.1% over 40 allows.** At 1280x720 that is 922 pixels of a whole image; with 92%
     compared (the landing's least), 848.
5. **Parity, and the shaders not drawn** (premises verified; settled as below).
   - **The navigation grid's two-row lines are the original's** (observed, j0009025, the same
     spec on the original's Direct3D 11). Down one column of the grid's horizontal line:

     | Player | Rows drawn |
     |---|---|
     | The original | 449 and 450 |
     | Ours on D3D11 | 449 and 450 |
     | Ours on OpenGL | 450 |
     | Ours on Vulkan | 449 |

     Over the image, with the OpenGL pair's unstable pixels left out:

     | Against the original | Over 16 | Over 40 |
     |---|---|---|
     | D3D11 | 0.001% | 0.000% |
     | OpenGL | 0.136% | 0.127% |
     | Vulkan | 0.340% | 0.134% |

     The rasterizer state stays. `scenes.py` judges the shot against the original (`--original`,
     `REFERENCE`), which closes part C's UNVERIFIED. OpenGL's and Vulkan's one-row lines are
     theirs, not this device's (reported, not changed here).
   - **The five undrawn shaders** are drawn (j0009020, j0009023; their first-draw coverage
     lines, debug layer clean). Each, with how the spec reaches it:
     - **Klei/Standard Unlit.** `long_smoke_trail` is TrailFX2's particle material (slot 0), not
       a trail. That system emits by distance only: rateOverDistance 10, rateOverTime 0, the
       trail module off (sharedassets0). So part C's still instance drew nothing.
       `d3d11-coverage-play` launches a long-range missile as
       MissileLongRangeProjectile.StatesInstance.PrepareLaunch does.
     - **TextMeshPro/Sprite.** The same spec selects a storage locker holding one germy Mush
       Bar. Its contents row gets AppendIcons' `<sprite>` germ icon
       (SimpleInfoScreen.RefreshStoragePanel); the details panel is scrolled to it.
     - **Standard.** WaterExplode is a GameAssets prefab whose MeshRenderer uses
       Default-Material. No game code or data instantiates it, so it is unreachable in play. The
       spec places it so the program draws.
     - **Klei/AnimationInvisible and Klei/DLC4/LargeImpactorCometShader.** `d3d11-coverage-comet`
       triggers ClusterDestinationReached on the impactor. LargeImpactorStatus then lands it,
       and the event makes the falling LargeImpactorComet. Its LargeComet is the only code that
       sets MaterialType.Invisible; no assignment of `materialType` elsewhere can give it
       (KAnimLayering's table, BuildingFacadeAnimateIn's copy). A prefab's serialized
       `materialType` was not read (ShaderSurvey cannot read script fields); no other draw of
       the shader appeared in any run.
     - **Unlit/VirtualCursorCutoutShader.** `d3d11-coverage-gamepad` adds a gamepad controller
       first (`Global.GetInputManager().AddGamepadController(1)`, KInputManager.devToolFocus),
       as another investigation found. In the scene set, within: 0.001% over 40.

     Coverage in the committed specs (`coverage.py` over j0009022 and j0009023): 67 of 121
     shaders, 69 of 176 passes, 158 stage entries, 79 programs. Of part C's 38, 17 draw in
     play, Standard draws only where the spec places it, and 20 are unreachable as part C
     found. Every game shader part C found reachable now draws through its shipped program.
   - **The impactor's two shots: drawn, still unjudged** (open). FrozenImpactorTestMod holds the
     materials' time and random inputs at constants in both players (4554b64b4, b0aa4b8d5): the time
     1000 s, the entry's start 1 s, random 0.5. The first try held the entry's start at 0,
     which the grid's program takes the sign of, a value at a discontinuity, and was dropped.
     With the inputs held (j0009028):
     - **impactor-world.** D3D11 against OpenGL: 0.008% over 40, D3D11 against the original
       0.092%, OpenGL against the original 0.085%, of the pixels compared.
     - **after-reveal.** D3D11 against OpenGL: 0.050%.

     Only 48% and 45% of each image is stable between two OpenGL runs. What still moves:
     - the surface's edge and the sky's particles after 20 s of play;
     - the landing-zone grid's dots: they changed between the two OpenGL runs despite the held
       inputs (not traced);
     - the reveal card in after-reveal.

     So both stay under MIN_STABLE and unjudged. They are coverage scenes (criterion 1), not
     criterion 3's listed scene set. Whether to judge them on less than half the image, or to
     keep tracing what moves, is left to the user.
   - **d3d11-coverage.yaml**: its header no longer says the power overlay's ports draw through
     Klei/BuildingCell (part C: they are UI images).
6. **CopyTargetRect** (premise verified: AnimatesLoading is not overridden, and nothing else
   calls it; fixed, 08e1fa3b9). A rectangle outside either texture is refused and logged as a
   failed command: "If you try and copy outside the destination resource or specify a source
   box that is larger than the source resource, the behavior of CopySubresourceRegion is
   undefined" (CopySubresourceRegion, Remarks). The bounds test, CopyFits, is unit-tested
   (D3d11CopyTests). It does not count toward done criterion 4 until M4 turns the loading
   animation on and exercises it.
7. **The readback release race** (premise verified in the code; fixed, 08e1fa3b9). The race:
   ReleaseReadback freed the texels on the main thread while the device thread could be in
   Copy, setting `owned`, `Data` and the non-volatile `conversion` (PollReadbacks after a
   Present). The texels are now freed inside the posted release, on the device thread, after
   the request leaves the pending list; a conversion it started is waited for first. Completion
   is polled only after a Present (and on Wait), as part A's design says.
8. **The target's name on every bind** (premise verified; fixed, 066157a9b). BoundTarget keeps
   the texture's name. Its label is made only when a log line needs it.
9. **setup.set** (premises verified: FieldInfo.SetValue on a static readonly field and
   GetProperty on overloaded indexers; fixed, 9936a71e7). A constant, a static readonly field
   or an indexer is refused with an error that names which. Properties are found by name
   without index parameters. The same Set is on master (d92cd6220): this fix can be
   cherry-picked there as behaviour-neutral prep (not done here).

**The caveat** (the 68 include Particles/Standard Surface and Unlit's PROCEDURAL_INSTANCING_ON
variants; measured in the data). Unity's manual gives particle GPU instancing for Mesh render
mode only ("Set your Particle System's renderer mode to Mesh", Particle System GPU instancing,
2020.3). Every ParticleSystemRenderer in the game's data was read (UnityPy, the frozen build's
sharedassets, level, resources and globalgamemanagers files): 11 Billboard with GPU instancing
on, 1 Stretched, none in Mesh mode. So no game particle system can select those variants in the
original. Whether a mod's could is M7's.

**Scene-set results** (`scenes.py`, part A's tolerance, the changes above). The runs:
- D3D11 with the debug layer: j0009022 (the three colonies and coverage) and j0009023 (the
  rest);
- OpenGL: part B's and C's pairs, j0009024 for the specs new or changed here, j0009028 for the
  impactor (with its D3D11 run);
- Vulkan: j0008871;
- the original: j0009025.

| Spec | Shots | OpenGL spread > 8 (max) | Compared (min) | > 16 | > 40 | PSNR (min) | Within |
|---|---|---|---|---|---|---|---|
| Landing (gases) | 22 | 1.74% | 92% | 0.031% | 0.017% | 43.2 dB | 22 of 22 |
| Small base | 26 | 2.10% | 29% (research, side bar) | 0.108% | 0.040% | 43.5 dB | 26 of 26 |
| Mid-game Spaced Out | 22 | 2.05% | 86% | 0.399% | 0.022% | 41.4 dB | 22 of 22 |
| Coverage specs (A-C) | 11 | 2.37% | 93% | 0.122% | 0.092% | 33.7 dB | 11 of 11 (sky against Vulkan, navgrid against the original) |
| Freeze frame | 1 | 0.00% | 100% | 0.235% | 0.085% | 42.2 dB | 1 of 1 |
| Gamepad | 1 | 1.71% | 94% | 0.010% | 0.001% | 48.9 dB | 1 of 1 |
| Impactor | 2 | | 45-48% | | | | unjudged |

83 of 85 scenes are within the tolerance; the 2 impactor shots are unjudged. Every scene of done
criterion 3's set is within.

**State of M3 under the Rules.** No UNVERIFIED note of M3 is open: the hand-over (finding 1)
and the navigation grid (finding 5) are closed. What remains for the user's word before M3 is
marked done:
- the impactor's two coverage shots, drawn and debug-layer clean, but unjudged (under half the
  image stable);
- the loading screens of criterion 3's set, which stay M4's (animated; M4 owns the loading
  animation), as part A recorded.

**Runs** (runq, burndown, owner `d3d11-m3`): builds j0009013, j0009019, j0009021; the freeze
before the fix j0009008; check-shaders, filters and freeze j0009015; the new specs' first D3D11
run j0009020 (5,557 dropped, which led to the limit); the scene set on D3D11 j0009022 and
j0009023; OpenGL j0009024; the original j0009025 (navgrid, freeze); the impactor on Vulkan and
the original j0009026, its grid program's dump j0009027, the impactor held again (entry start 1 s)
j0009028; check.sh j0009029 (green, on d391fcd70).

## M4: presentation and frame pacing (2026-11-24, branch `d3d11-m4`)

M4 gives the device the presentation the Vulkan device has (done criterion 4: repeats, splash,
loading animation, HDR, full screen and resize), settles the UNVERIFIED notes assigned to it by
observing the original, and lists what only the user's desktop can show. Base: `d3d11-m3`
(20cc567df). Every run: the RTX 3090, NVIDIA driver 32.0.16.1088 (as M0-M3), Windows 11, monitor 2,
windowed, runq burndown, owner `d3d11-m4`.

**The original, observed** (j0009056 and j0009060: the frozen copy
`E:\oni-builds\BrutalNotIncluded-764025`, its Direct3D 11 at level 11_1, 1024×768 windowed; the
recorder of `tools/D3d11Native` extended, `PROBE_PACING=1` in UnityProbe's `rdcapture`
scenario; logs in `out/d3d11-captures/m4-pacing*/`):
- **The screen's texture.** At every Present (300 of 300 in each window, menu and colony) the
  bound render target is not buffer 0: it is a texture of the window's size,
  `R8G8B8A8_TYPELESS` (27), bind flags SRV | RTV (0x28), its view `R8G8B8A8_UNORM` (28). Buffer
  0 (format 28, bind 0x28) gets one draw a frame, the frame's last (stride 88, no depth, full
  viewport), which samples that texture at t0; no clear, CopyResource, CopySubresourceRegion or
  ResolveSubresource writes buffer 0. The menu frame: 1 of 41 draws into buffer 0, 40 into the
  texture; the colony frame: 1 of 1,375.
- **The frame-latency waitable.** Unity's main thread (not the thread that presents) waits on it
  once a frame: `WaitForMultipleObjectsEx` with 2 handles, the waitable first, wait-any,
  INFINITE, every call signalled; each wait starts as the presenting thread enters Present
  (−0.02 to −0.05 ms from it). The handle was matched with `CompareObjectHandles` ("Compares two
  object handles to determine if they refer to the same underlying kernel object", Windows 10+).
  Only the six Wait functions UnityPlayer.dll and the exe import were hooked.

  | vSyncCount | Present | Waits a Present | Waited, mean / largest | Presents |
  |---|---|---|---|---|
  | 1 | `Present(1, 0)` | 1 (300 of 300) | 15.9-16.2 / 17.6 ms | 60.0 a second |
  | 0 | `Present(0, ALLOW_TEARING)` | 0, 1 or 2 (50, 200, 50 of 300) | 0.50-0.68 / 5.7-6.5 ms | 0.8-1.0 ms apart |
  | 2 | `Present(2, 0)` | 1 (300 of 300) | 32.5-32.7 / 49 ms | 33.3-33.4 ms apart |

  `IDXGISwapChain2::GetMaximumFrameLatency` 2, `IDXGIDevice1::GetMaximumFrameLatency` 3 (the
  device's default). `GetFrameStatistics` is called on the main thread for about the first 130
  presents after the hook (S_OK, PresentCount and PresentRefreshCount 0, windowed), then never;
  `GetLastPresentCount` never.
- **During a save load the original presents nothing**: one gap of 21.7 s (j0009056) and 20.9 s
  (j0009060) before the colony's first frame, then 0.6-0.9 s gaps, then frames as usual. Its
  window keeps the last picture. The repeats and the loading animation are Moono's, as on the
  other devices.

**Design** (`D3d11Present.cs`, `D3d11LoadAnimation.cs`; 00cfe095b, 357904a36):
- **The screen.** The game's back buffer is one of two screen textures of the device's own, made
  at the rendering size (`Screen.width/height`, smaller than the window in a fullscreen window
  the game asked a lower resolution of), `R8G8B8A8_TYPELESS` with `R8G8B8A8_UNORM` views as the
  original's. At its present the frame's texture goes into buffer 0 by CopyResource ("Must be the
  same type", "Must have identical dimensions", "Must have compatible DXGI formats, which means
  the formats must be identical or at least from the same type group": its Remarks), or, when the
  sizes differ, by a bilinear stretch over the whole buffer (as the GL and Vulkan presenters
  stretch). The original draws its texture into buffer 0 instead; a copy of the same size keeps
  every texel. The next frame draws into the other texture, so the last frame shown stays whole
  while the next is drawn (a frame drawn in parts, flushed in the middle, never shows). A frame
  that drew nothing into the screen shows the last one again (`GlPresenter.Choose`). ReadPixels
  and captures of the screen read the frame's texture.
- **Between frames, on the device thread.** The device thread alone uses the immediate context
  and presents (M2's rule), so the pictures shown while no frame comes are its idle work:
  `GlWorker.IdleWaitMs` (new, null for the other devices) wakes it when one is due. In
  VkPresenter's order: the splash while it runs (`SplashRain`, redrawn 16 ms after the last
  picture), else the loading animation (30 a second, `GlLoadAnimation.Wanted`/`WatchdogDue`
  arming, the bar's `Advance`, `LoadProgress`), else a refresh of the last frame when the window's
  contents were damaged (`ContentsDamaged`), else a repeat (`StallRepeats`, `--stall-repeats`).
  A repeat goes only when the waitable is signalled (`LastPictureRetired`, wait 0), so it does
  not queue behind a picture. It is presented with `Present(0, 0)`: sync interval 0 without
  ALLOW_TEARING in the flip model is what lets a newer picture replace it ("discard this frame
  if a newer frame is queued", IDXGISwapChain::Present, SyncInterval); the waitable plays no part
  in that. Never after a resize, not counted as a present nor by frame pacing; the waits
  between repeats raise the timer resolution to 1 ms for the run of repeats (and of the loading
  animation). Every picture is meant to keep M2's pairing, one waitable wait for each Present (a
  frame's and a redraw's after it, a repeat's before it); for repeats that rests on an
  assumption no page documents (UNVERIFIED, "M4 review fixes" item 2).
- **The presenter's program.** The splash's comets, the loading kanim's tile and the bars are
  window-space quads drawn with a program of the presenter's own (texel × mul + add, premultiplied
  over), as VkSplash and GlSplash draw with theirs: they are the player's presentation, not the
  game's, which the Rules allow (the user's decision of 2026-11-24). HLSL in
  `D3d11Present.cs`, compiled once with the SDK's fxc 10.1 (`vs_4_0`/`ps_4_0`, j0009057), shader
  model 4.0 being feature level 10_0's ("Direct3D feature levels"). After one is drawn the device
  forgets its tracked bindings and sets its target, viewport and scissor again. The target is
  bound before the view it may read (OMSetRenderTargets, Remarks: a subresource bound for reading
  and writing has its bind points "set to NULL"), and both are unbound before Present (with buffer
  0 bound, the debug layer reported INFO 49, "Forcing OM Render Target slot 0 to NULL", in
  j0009072; gone in j0009082).
  The splash atlas has mips only where R8G8B8A8_UNORM has MIP_AUTOGEN, GENERATE_MIPS needing
  "the bind flags that specify that the resource is a render target and a shader resource"
  (D3D11_RESOURCE_MISC_FLAG).
- **The loading animation** (`AnimatesLoading` true, `HandOverLoadingFrames`): the kanim frames'
  strip is handed over by a command after its rendering, on the one context, and given back
  without waiting. Its tile is drawn unfiltered when it is not scaled, turned over when the strip
  stores its rows bottom first (VkLoadAnimation's geometry, `D3d11LoadAnimation.TileOf`), and
  `ONI_TWEAK_VERIFY=1` compares the first replayed tile with the frame shown, as Vulkan's does.
  With it on, LoadingScreen renders kanim frames, and `CopyTargetRect` (M3's, bounds-checked)
  runs for them.
- **Frame pacing.** A picture presented with vsync tells `FramePacing` the display's refresh
  period (the containing output's mode, `DxgiSwapChain.LookUpRefreshPeriod`, now shared), one
  without that it is unpaced, so frame deltas are whole refreshes as on the other devices (they
  were the wall clock's on this device). The waitable wait stays the device thread's, before each
  frame renders (M2, "Reduce latency with DXGI 1.3 swap chains"), where the original's main
  thread waits: here the main thread waits for the device thread at each frame's hand-over.
  Inference, not measured: the frames in flight are then the same as the original's (neither
  side's pipeline depth was counted).
- **HDR**: as the DXGI path: an 8-bit R8G8B8A8_UNORM flip-model swap chain whose colour space is
  never set. Microsoft documents 8-bit swap chains as "implicit sRGB" ("Use DirectX with Advanced
  Color on high/standard dynamic range displays"); no page names
  `DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709` as the default by name (ARCHITECTURE §3.5 uses that
  name for the DXGI presenter). Not run on an HDR monitor here:
  every monitor was SDR (j0009063, `dupcap`); the HDR check (`tools/HdrCheck`, now with
  `ours-d3d11-own`) is on the user's list below.
- **Full screen** stays the window's (GLFW), as the DXGI presenter's: the swap chain is never put
  in DXGI's full-screen state and DXGI leaves the window alone (`NO_WINDOW_CHANGES | NO_ALT_ENTER`),
  so the overview's deadlock case ("When a mode change occurs, ... Present1 calls
  ::SetWindowPos() ...", M2 review fixes finding 3) does not arise; the buffers follow the
  window's framebuffer and the screen textures the rendering size.
- `--input FRAME:stall:MS` (new) holds the main thread up, for repeat tests.

**Results.**
- **Unit tests** (`D3d11PresentTests`: the tile's geometry bottom and top first and scaled, the
  bar's rectangles, GlWorker's timed idle work; 56 of the filtered tests pass, j0009069).
- **The main menu** (j0009072, `--d3d11-debug`, `ONI_PRESENT_TRACE=1`, `--splash moo`): the menu
  draws upright (frame 800's capture); the splash rains from start-up and fades after 8.0 s; the
  first frames' window change from 1280×720 to 2560×1369 (the game's own, unscripted) resized the
  buffers and remade both screen textures; under 1.5 s stalls at vsync 1 and 0 the last frame
  was presented again 86 and 85 times, every 16.7 ms; frame-latency waits a frame: 16.1 ms at
  vsync 1, 0.04-1.4 ms at 0, 32.1-32.7 ms at 2 (the original's: 16, 0.5-0.7, 32.6), Present
  0.02-0.05 ms, no timeout; minimized at frame 1800 and restored at 2000 (`ShowWindow`
  SW_SHOWMINNOACTIVE and SW_SHOWNOACTIVATE, "the window is not activated"), frames and waits as
  before; `\\.\DISPLAY2 refreshes every 16.6802 ms`. Debug layer: 0 messages above INFO, 11 of
  the RTV warning denied (M3's filter). Again on the final build (j0009082, typeless screen
  textures, the back buffer unbound before Present): splash faded after 7.4 s, 86 and 86 repeats
  under the stalls, 0 messages above INFO and no INFO 49.
- **The loading animation** (`loading-animation-d3d11`, j0009079, ok): the colony's load was
  animated with 200 redraws over 7.7 s, the longest gap 180 ms, the bar at 0.992 when the colony
  came. `loading-animation-pip-d3d11` (a mod's kanim on the loading screen, the replay check and
  `CopyTargetRect`) was skipped by the mod security gate: neither mod's current upload is
  analysed by onimaxxing.
- **The scene set** after the change (`d3d11-scenes`, j0009081, against part B's OpenGL pair):
  22 of 22 within the tolerance (largest > 40: 0.016%, the world; PSNR 44.0-56.8 dB), as before.

**UNVERIFIED notes settled** (each was assigned to M4):
- captures 1 (the waitable, GetFrameStatistics): observed above. The device keeps its documented
  wait (M2); its waits equal the original's within 0.5 ms at vsync 1 and 2.
- captures 2 and B2 5 (the frame's last draw, buffer 0): observed above; the device draws into
  its own typeless texture as the original does.
- B1's note on the original's waiting: the same observation.
- **Open**: captures 10 (the original's full-screen swap chain in `FullScreenWindow` and
  exclusive, and its `ResizeBuffers` arguments) needs a mode change on the user's desktop (below).
  So does the fullscreen stretch (rendering size under the window's), never shown here.

**Runs that need the user's desktop** (not run: each changes the display or may take the
focus):
1. The original, `-screen-fullscreen 1` at the monitor's size (FullScreenWindow), then exclusive
   (`-window-mode exclusive`), each with the recorder (`PROBE_PACING=1`) and one in-game
   resolution change: its swap chain's descriptions and ResizeBuffers arguments (captures 10).
   About 4 min each, on monitor 2.
2. Ours, `--renderer d3d11 --d3d11-debug`, through `--input` resizes: windowed 1280×720 →
   borderless at the monitor's size → borderless at 1280×720 (the stretch) → exclusive at the
   monitor's mode → windowed, a few hundred frames each with a stall in each: no hang, repeats,
   debug layer clean. About 5 min on monitor 2; exclusive changes that monitor's mode.
3. Alt-tab out of and back into exclusive full screen, and minimize/restore by the user (focus
   changes only a person can make): about 2 min.
4. The HDR check with HDR on (`tools/HdrCheck/hdr-check.sh`, `ONI_HDR_ONLY="original
   ours-d3d11-own"`, the frozen build): fullscreen on the HDR monitor (monitor 1), about 4 min;
   with Windows' Auto HDR on, the same two players windowed, watching for the Game Bar's Auto HDR
   notification and the dupcap luminance (issue #112, below).

**Auto HDR (issue #112).** Documented: Auto HDR "automatically upgrades the rendering of color in
existing DirectX 10 thru DirectX 12 games", per-app toggles "will only apply on eligible games"
(DirectX blog, "Navigating the redesigned graphics settings page"), and Microsoft enables it by
title ("we haven't yet enabled Auto HDR on all top DX11/DX12 titles since some are hard to identify
as Auto HDR eligible", "Auto HDR preview for PC available today"). Signals: the Game Bar's "Adjust
HDR Intensity" slider "only applies to games with Auto HDR", and its notification; no API reports
it, but composed luminance above SDR white in an FP16 Desktop Duplication capture shows it.
Not observed: no monitor was in HDR (j0009063). Inference, not measured: our swap chain is a
flip-model D3D11 one like the original's, so if the original engages and ours does not, the
likeliest cause is Microsoft's per-title eligibility (the executable). No behaviour changed for it.

**Prep for master** (behaviour-neutral, would cut conflicts): 639de4c4b (`GlWorker.IdleWaitMs`,
`DxgiSwapChain`'s shared refresh lookup) and 159dbbcca (`--input stall`) can be cherry-picked.

**Runs** (runq, burndown, owner `d3d11-m4`): fxc j0009057; probe builds j0009052-j0009055,
j0009059; the original j0009056, j0009060; monitor colour spaces j0009058, j0009063; builds
j0009064 (cancelled), j0009066-j0009068 (dotnet off the job's PATH, then build errors fixed),
j0009069 (unit tests); Windows builds j0009070, j0009074, j0009076 and runs: menu j0009072 and j0009082, loading j0009075 (no qa.env in the
worktree), j0009077 (no E2eDriver), j0009079, scenes j0009081; check.sh j0009083 (green, on dc2703ded; com-check matches the SDK headers).

## M4 review fixes (2026-11-24, branch `d3d11-m4`)

The adversarial review of M4 gave 8 items. Each was fixed in its own commit (dee52ad2f to
9dd84e5ef; the plan's text here). RTX 3090, NVIDIA driver 32.0.16.1088, monitor 2, as M4.

1. **A zero idle wait went untimed** (dee52ad2f). `GlWorker.IdleWaitMs` returning 0 (the work
   fell due after HasIdleWork said none) fell to the untimed `Monitor.Wait`. A wait of 0 or more
   is now timed (0 gives the gate up and takes it back at once); only a negative one is untimed.
   `D3d11PresentTests.IdleWorkFallingDueBetweenTheTwoQuestionsIsNotMissed` makes the work fall
   due between the two calls.
2. **The repeat's waitable pairing** (de3c7e76b). The repeat takes the waitable's signal before
   its Present and counts it as the repeat's own wait, so the pairing (one wait per Present)
   assumes each Present, a repeat discarded for a newer picture included, signals the waitable
   once. No Microsoft page says so; DxgiSwapChain does not assume it (it drains and re-counts the
   waitable after repeats and bounds the wait after one). The comment that said the repeat works
   "as DxgiSwapChain.LastPictureRetired" now says what it assumes, and ONI_PRESENT_TRACE logs the
   first wait after a run of repeats and the repeats so far.
   **Observed** (j0009097, the menu at vsync 1, ten 1.5 s stalls: three on consecutive frames,
   three 3 frames apart, four consecutive): 1,001 repeats, no WaitForFrame timeout, nothing owed;
   the first wait after each run of repeats returned at once, signalled (the repeat before it had
   long retired), and the frames after it waited 16.0-16.1 ms a frame on average, as before the
   stalls (a count one short would show as timeouts or 33 ms waits, one over as frames that keep
   not waiting). So the count stayed balanced. **UNVERIFIED, narrowed:** whether any repeat was in
   fact discarded for a newer picture in that run is not known (nothing counts discards), so the
   discarded case's signal is still unshown; adopting DxgiSwapChain's resync is the fallback if a
   run shows a miscount.
3. **The presenter forgot bindings still in place** (c12bc9a3f). After a presenter draw,
   ForgetBindings cleared every tracked view, sampler and constant buffer, though only the pixel
   stage's slot 0 had changed, so SetTarget's hazard pass no longer knew of views still bound in
   other slots and stages. Now only what DrawQuads sets is marked unbound
   (`ForgetPresenterBindings`). The stretch path (rendering size unlike the window's) has no cheap
   run: it needs a fullscreen window at a lower resolution (the user's desktop, "M4" run 2) or a
   resize; its debug-layer run came with the desktop runs (j0009137, 0 messages above INFO).
4. **"The frames in flight are the same"** is now labelled an inference (neither pipeline's depth
   was counted).
5. **What discards a repeat**: sync interval 0 in the flip model ("discard this frame if a newer
   frame is queued"), not the waitable; the waitable only keeps a repeat from queueing behind a
   picture. The "M4" text says so.
6. **The presenter's program** (9dd84e5ef). `scripts/gen-d3d11-present.py` compiles the HLSL in
   D3d11Present.cs's comment with the SDK's fxc 10.1 and compares the committed blobs (`--write`
   sets them); `tests/scripts/test_d3d11_present.py` does the same where fxc is reachable and
   checks the blobs' shape everywhere (j0009095: equal, 3 tests ok). **Accepted** by the user's
   decision of 2026-11-24 (Rules, "The player's own presentation"): the player's own programs
   may draw its own presentation; the game's draws stay on shipped programs.
7. **A refresh waited for the loading watchdog** (629173377). IdleDue now asks for a refresh
   first, as VkPresenter's wait ends on it first, and draws it as a redraw (the splash or the
   loading animation over the frame where they run, as VkPresenter's refresh).
8. **A picture's failed call did not stop the device** (44e07768d). Show's presenter draws can
   throw D3d11UnavailableException; from a frame's present it was counted as a failed command.
   It now sets `lost`, as a failure in IdleWork's pictures does.

**Runs** (runq, burndown, owner `d3d11-m4`): blob check j0009095; Windows build j0009096; stalls
j0009097; check.sh j0009100 (failed: ruff on gen-d3d11-present.py, fixed), j0009106 (green).

## M4 desktop runs (2026-11-24, branch `d3d11-m4`; the user's OK for the four runs, the Loading Pip exception)

- **Run 1, the original's full screen** (j0009128; the frozen copy, monitor 2, the recorder with
  `PROBE_MODES=1`, which now also hooks `SetFullscreenState` and `ResizeTarget` and describes the
  chain after each `ResizeBuffers`; `out/d3d11-captures/m4-modes/`). Through `Screen.SetResolution`:
  - **Borderless at a lower resolution** (1920×1080, FullScreenWindow, on a 2560×1440 monitor):
    the same flip-model chain, `ResizeBuffers(2, 2560x1440, R8G8B8A8_UNORM, flags 0x842)`,
    windowed 1, not in DXGI's full-screen state: buffers at the window's size and the frame
    stretched into them, as this device does (screen texture at the rendering size, stretched over
    the buffer).
  - **Exclusive**: a second swap chain, the legacy blt model (`DXGI_SWAP_EFFECT_DISCARD`), 1
    buffer, flags 0x2 (ALLOW_MODE_SWITCH), refresh 59951/1000; `ResizeTarget` with the mode, then
    `SetFullscreenState(TRUE)`, which returned 0x887A0022 (DXGI_ERROR_NOT_CURRENTLY_AVAILABLE) at
    2560×1440 and 1920×1080; Unity then called `SetFullscreenState(FALSE)` and went back to the
    flip chain as FullScreenWindow. That the failure is the window lacking the focus (a test window
    never takes it) is an inference. So the original's exclusive full screen is DXGI's own (its
    swap chain enters DXGI's full-screen state), unlike this device's and the DXGI presenter's
    (the window's, through GLFW; ARCHITECTURE §3.5); its chain inside exclusive was not observed.
  - **Back to windowed** 1280×720: `ResizeTarget(1280x720)` and `ResizeBuffers(2, 1280x720, 0x842)`
    on the flip chain.
  - Captures UNVERIFIED 10 is settled for FullScreenWindow and for the calls the original makes
    for exclusive; its swap chain while in exclusive full screen needs the window focused (the
    user's hand).
- **Run 2, ours through the modes** (j0009137, the user's approval in this session; monitor 2,
  `--renderer d3d11 --d3d11-debug`, `ONI_PRESENT_TRACE=1`, `--input` resizes with a 1.5 s stall in
  each): windowed 1280×720 → borderless 2560×1440 → borderless at 1280×720 (the screen textures
  made 1280×720 and "stretched over the 2560x1440 buffer") → exclusive 2560×1440 → windowed
  1280×720. Each change remade both screen textures at the rendering size and resized the
  buffers to the window's; each stall was presented again 76-86 times, 16.7-18.0 ms apart; no
  frame-latency timeout, nothing owed; no hang at any change (the swap chain never enters DXGI's
  full-screen state). Debug layer: 0 messages above INFO, the storage filter denying only
  DEVICE_DRAW_RENDERTARGETVIEW_NOT_SET. This is review item 3's stretch-path debug-layer run.
  The window was never focused (a test run), so exclusive here is GLFW's monitor-sized window
  with its mode set, unfocused.
- **Run 3, ours focused in exclusive, and an alt-tab without keys** (j0009167; the user's word
  "You can take over my desktop" and their approval of runs 3 and 4; `--input N:focus` /
  `focusother`, SetForegroundWindow from the player's own process, no key sent, the pointer not
  moved): SetForegroundWindow returned 1 and the window was the foreground; exclusive 2560×1440
  (GLFW's monitor-sized window with the mode set) presented, the screen textures remade, a 1.5 s
  stall repeated 86 times every 16.7 ms. Handing the foreground to a popup of the player's own
  returned 0 (Windows refused it), but the game window lost the focus all the same and GLFW
  iconified it (`window iconify Iconified`, `focus LostFocus`), as an alt-tab out of exclusive
  does; minimized, a stall still repeated 76 times; asking for the foreground back returned 0
  (a process not in the foreground may not take it, so the alt-tab back in was not made); the
  restore (SW_SHOWNOACTIVATE) and the change to windowed 1280×720 went through. No hang, no
  frame-latency timeout, nothing owed; debug layer 0 messages above INFO.
- **Run 4, HDR** (j0009165, `tools/HdrCheck/hdrtoggle` and `dupcap`; monitor 2, `\\.\DISPLAY2`):
  `DisplayConfigSetDeviceInfo` (SET_ADVANCED_COLOR_STATE) switched its advanced colour on
  (returned 0, enabled 1 read back) and off again after the run (enabled 0 read back), but DXGI
  reported the output SDR throughout (colour space 0, 8 bits), every capture at most 80 nits: the
  original and ours, borderless and windowed, all composed as SDR (brightest 0.5% 77.6-80 nits).
  Why: that display does not support HDR. Windows 11 24H2's advanced colour info
  (`DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2`, j0009181) says DISPLAY2
  `hdrSupported 0`, `wideColorSupported 1`; DISPLAY1 and DISPLAY10 `hdrSupported 1`. So the
  older SET_ADVANCED_COLOR_STATE switched on wide colour on an SDR panel, and dupcap read the
  output rightly. hdrtoggle now switches HDR itself (`DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE`),
  refuses a display without HDR, and reads the active colour mode back until it is HDR (10 s at
  most) before anything runs. The DirectX user settings value (Auto HDR) is unset (read only).
  The rerun on monitor 1 (j0009182) picked DISPLAY2 again (its script indexed the second screen)
  and skipped, changing nothing. **A valid run on the HDR monitor cannot go through runq as it
  stands**: a Windows job's games must be on monitor 2 (runq's `windows-rules`), and HDR and Auto
  HDR act on the display the game is on; it needs that rule relaxed for the job (the user's
  call). So nothing about HDR or #112 was observed.
- **Run 3, the original focused in exclusive** (j0009185; `ORIG_FOREGROUND_AT=12`, d9c121937:
  original-windows.sh gives no focus back and, 12 s in, calls AllowSetForegroundWindow for the
  game's process and SetForegroundWindow on its window: both returned True, the game the
  foreground. SetForegroundWindow's Remarks allow it to "the foreground process" among others, and
  AllowSetForegroundWindow lets "a process that can set the foreground window ... enable another
  process"; why the launcher could is not established). Observed:
  - the probe's SetForegroundWindow returned 1; `SetResolution(2560, 1440, ExclusiveFullScreen)`
    made the second chain (legacy blt `DISCARD`, 1 buffer, `R8G8B8A8_UNORM`, usage 0x30, flags 0x2
    ALLOW_MODE_SWITCH) and `SetFullscreenState(TRUE)` returned S_OK: `GetFullscreenState`
    fullscreen 1, `GetFullscreenDesc` windowed 0, refresh 59951/1000, `ResizeBuffers(1, 2560x1440,
    R8G8B8A8_UNORM, 0x2)`; `IDXGISwapChain2::GetMaximumFrameLatency` 0x887A0001
    (DXGI_ERROR_INVALID_CALL: a blt-model chain has no waitable). In exclusive it presented
    `Present(1, 0)` on that chain (239 of the window's 300 presents), Screen.fullScreenMode
    ExclusiveFullScreen.
  - The foreground handed to a popup of its own (SetForegroundWindow 1): Unity called
    `SetFullscreenState(FALSE)` on the blt chain, went back to the flip chain
    (`ResizeTarget(2560x1440)`, `ResizeBuffers(2, 2560x1440, 0x842)`) and to **Windowed**
    2560×1440, not minimized; the foreground given back (SetForegroundWindow 1) left it windowed
    (it did not go exclusive again).
  - So captures UNVERIFIED 10 is settled: the original's exclusive full screen is DXGI's (a blt
    DISCARD chain in DXGI's full-screen state, no frame-latency waitable); its other modes keep the
    flip chain. **Differs from this device** (and from the DXGI presenter of the other devices):
    their exclusive is the window's (GLFW sets the mode, the flip chain presents), and losing the
    focus minimizes the window (GLFW's auto-iconify, j0009167) where the original goes windowed.
    Whether to match the original there is open (done criterion 2, swap-chain settings; the
    deadlock note of the M2 review fixes, finding 3, applies to DXGI's full-screen state).
- **Open, holding M4 (UNVERIFIED)**:
  - (closed above, j0009185) the original's swap chain inside exclusive full screen. j0009178 failed to
    give it the foreground (the probe's SetForegroundWindow 0; j0009166 had found the probe's DLL
    gone with a rebuild of out/win);
  - an alt-tab back into exclusive full screen (Windows refuses a background process the
    foreground; a person's alt-tab, or a key, would make it);
  - Auto HDR (#112) engaged: seen for neither player, with Windows' Auto HDR most likely off
    (above); observing it engaged needs Auto HDR switched on, a settings change left to the user;
  - whether this device's exclusive full screen should be DXGI's, as the original's is (a
    decision; the original goes windowed on losing the focus, ours minimizes).
- **Run 4b, HDR on the HDR monitor** (j0009190; the user's approval, runq's monitor rule set to
  monitor 1 for this job by the coordinator; `hdr-display1.sh`: `\\.\DISPLAY1`, the primary,
  `hdrSupported 1`, `-monitor 1` for both players): SET_HDR_STATE switched HDR on (returned 0,
  active mode HDR read back; dupcap: colour space 12, G2084/P2020, 10 bits, 530 nits peak) and
  off again after (active SDR read back). FP16 captures of the monitor (each checked by eye, scaled
  down; the PFMs stay in the job's folder, not in git):
  - **Ours (D3D11), borderless at 2560×1440**: the main menu fills the monitor; brightest 0.5%
    and maximum 240 nits, the SDR white level ("SDR content brightness", ARCHITECTURE §3.5);
    median 7.4 nits.
  - **Ours, windowed 1280×720**: the menu's window over the desktop; inside the window's area
    brightest 0.5% and maximum 240 nits, median 9.4.
  - **The original, both modes: not observed.** At the capture, 140 s after its start, no game
    window was on the monitor (the capture shows the desktop's other windows), in both modes; its
    output went to /dev/null in the script, so why is not known (it may not have reached the
    menu, may have ended, or may have opened behind another window). Its 240-nit figures in the
    job's summary are the desktop's, not the game's.

  **The original, rerun** (j0009200, `hdr-orig-display1.sh`: the launcher's output kept, the
  game made the foreground 30 s in, `ORIG_FOREGROUND_AT`: Allow True, Set True; its window found
  by its process and the capture cropped to it; HDR on and restored as above): borderless, its
  window 2560×1440 at 0,0, the capture shows the game's own picture (its start screen, the planet
  and rockets, with its white close mark; not the menu), brightest 0.5% 81.9 nits, maximum 240
  nits, median 2.4. Windowed was not captured: its window rect was −7, 0, 1296×759 (the invisible
  resize border 7 px left of the display), which the script's test that the rect lies entirely
  on the display refused for its 170 s; not rerun, borderless answering the question.

  So, observed: under Windows HDR the original and our D3D11 player are both composed as SDR
  content, their white at the SDR white level (240 nits); Windows took neither for HDR content and
  Auto HDR boosted neither. The DirectX user settings value where Windows keeps its graphics
  settings is unset (read only); that Auto HDR is off on this machine is an inference. For #112:
  with Auto HDR off, HDR mode alone does not change our D3D11 output relative to the original;
  whether Auto HDR, switched on, engages for the original and not for ours is still unobserved.
- **Loading Pip** (`loading-animation-pip-d3d11`, j0009135, ok): the kanim replayed, 193 redraws
  over 7.2 s, and the replay check found 0 of 118,680 pixels differing from the frame shown (the
  tile copied from the screen by CopyTargetRect, drawn unfiltered). Under the user's exception
  for mods a spec needs, pinned in `scripts/mod-gate.py` (`USER_EXCEPTIONS`, 4f564c87d):
  Loading Pip 1.4.0 (3408518726, upload 15844966683619590494, LoadingPip.dll SHA-256
  62ab2d565dac5e8425d7e166acde5d3885ede76f1e67414e5cd3e1ea41c3a471) and Pip Licks Butt 1.0.0.0
  (3802090892, upload 13679339843057888814, PipLicksButt.dll SHA-256
  5097a9efba2a237a72a5e8bc39a814e94acd4001266d4a3bc8fc67f37859e493), SteamCMD's latest uploads.
  Both were decompiled and read before: PLib with Sgt_Imalas's UtilLibs (a Steam version check,
  its own settings files) and aki's FUtility (links to its own pages); nothing suspicious. Two
  fixes on the way: `scripts/e2e-windows/run.py` now passes a spec's `env` to our player
  (0ab86f40a; without it the check never ran, j0009130), and the D3D11 replay check compares the
  start tile as Vulkan's does (it compared the first redraw's own tile, a frame on: 13,417
  pixels, j0009133, while its closest-tile search found the start tile exact).

**Runs** (runq, burndown, owner `d3d11-m4`): probe builds j0009126, j0009127; the original
j0009128; Windows builds j0009129, j0009132, j0009134; Loading Pip j0009130, j0009131, j0009133,
j0009135; ours through the modes j0009137; check.sh j0009136 (green, on 8b098a27f), j0009144 (cancelled for later code); HDR state j0009149, j0009181; HDR on DISPLAY2 j0009165, the monitor-1 attempt j0009182; focused exclusive, ours j0009167, the original j0009166 (no probe DLL), j0009178, j0009185; HDR on DISPLAY1 j0009190, the original again j0009200; builds j0009150, j0009162 (ComCheckGen: a RECT without [NotNative]), j0009164, j0009176; check.sh CHECKJOB4.

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
