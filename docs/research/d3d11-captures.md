# The original's Direct3D 11 frames: swap chain, state, depth bias, threading, cost

Milestone M0, part C of the Direct3D 11 plan ([d3d11-plan.md](d3d11-plan.md)), after part A
([d3d11-unity-probes.md](d3d11-unity-probes.md)). Every value below was measured on the original
game or read from the Windows SDK 10.0.26100.0 headers, except the lines that cite a public
document. No Unity engine source was read. Anything not measured or cited is marked
**UNVERIFIED** and listed at the end with the milestone that needs it. Reviewed by an
adversarial M0 review; its findings are folded in.

## How it was measured

- **Build.** The frozen U60 build 764025 (`E:\oni-builds\BrutalNotIncluded-764025`), Unity
  6000.3.17f1, by `scripts/original-windows.sh`: isolated run home, monitor 2, volume 0, a
  DLC-owning offline Steam double. Each run's `original-isolation.txt` confirms the
  redirections. Windowed 1024×768, default renderer (Direct3D 11), RTX 3090, driver
  32.0.16.1088 (Unity's `Driver:` log line).
- **The probe.** `tools/UnityProbe` with `PROBE_SCENARIO=rdcapture` (`RdCapture.cs`) and its native
  helper `tools/D3d11Native/probe_native.dll` (`probe_native.c`, `ctx_hook.c`), built with MSVC
  14.44 against the SDK headers.
  - Method slots come from the headers' C vtables (`offsetof`).
  - Every hook's type, and every forwarding type, is checked against the header's member type at
    compile time.
- **What the helper does in the game's process:**
  - **Swap chain.** It makes swap chains on a hidden, never-shown window with the game's own DXGI
    factory. All three kinds (flip and blt `CreateSwapChainForHwnd`, legacy `CreateSwapChain`) had
    the same vtable. It hooks that vtable's `Present`, `Present1` and `ResizeBuffers` slots, so
    the game's swap chain reports itself on its first `Present`. Every Present is tallied by swap
    chain, sync interval, flags, entry point and thread.
  - **Device and context.**
    - It hooks the device's `CreateVertexShader`, `CreatePixelShader`, `CreateGeometryShader`,
      `CreateInputLayout` and `CreateDeferredContext`.
    - It hooks 46 slots of the immediate context: draws, shader, buffer, view, sampler and state
      settings, `Map`/`Unmap`, updates, copies and clears. Forward targets are kept per context
      vtable.
    - It records one whole frame, from one `Present` to the next.
  - **Depth bias.** The probe draws `Hidden/Internal-Colored` into render textures with a range of
    material values. After each draw a rendering-event callback (`CommandBuffer.IssuePluginEvent`)
    reads the context's rasterizer, depth-stencil and blend state back with `RSGetState`,
    `OMGetDepthStencilState` and `OMGetBlendState`.
- **Colony.** `tests/e2e/fixtures/2minnow.sav` (a mid-game Spaced Out colony), resumed by the
  probe from the main menu, unpaused, then the temperature overlay.
- **Runs** (runq, `windows` slot, burndown). Raw logs are under `out/d3d11-captures/JOB/`
  (`Player.log`, `rd.txt`, `report-*.txt`), not in git.

  | Job | What it gave | Stored |
  |---|---|---|
  | j0008139 | swap chain, first depth-bias table; its colony loaded without its DLCs (not used) | no |
  | j0008144 | the RenderDoc launch that lost the isolation (below) | `j0008144/` (its RenderDoc log) |
  | j0008151 | first frame recording: no draws counted (see "The context's vtable") | `j0008151/` |
  | j0008159 | frame recordings, depth-bias table | `j0008159/` |
  | j0008168 | after the review fixes: per-vtable forwarding, Present threads, depth-bias rounding cases; frame recordings again | `j0008168/` |
  | j0008169 | part A's `d3d11` scenario under `-force-glcore` with the full GLCore matrix read | `docs/research/d3d11/unity-probes-glcore-2.txt` |

- **Threading.** `tools/D3d11Native/caps.exe`, a standalone program, run on the desktop through
  runq (`light`): `out/d3d11-captures/caps.txt`.
- **Shader names.** `ShaderSurvey dxbc DATADIR` lists the DXBC container checksum (bytes 4..19)
  of every shipped Direct3D 11 program. `tools/D3d11Native/frame-report.py` matches each shader the
  game creates by its checksum.

### RenderDoc: not used, as its launch bypassed the isolation

**RenderDoc 1.43's documentation.**
- `renderdoccmd capture` has no option to capture given frames (`renderdoccmd capture --help`).
- The in-application API is the documented route: "use `GetModuleHandle` on Windows" when
  renderdoc.dll is already injected, then `RENDERDOC_GetAPI`. `TriggerCapture` "will trigger a
  capture as if the user had pressed one of the capture hotkeys". `StartFrameCapture` /
  `EndFrameCapture` take `NULL` "to wildcard match" (renderdoc.org/docs/in_application_api.html,
  and the shipped `renderdoc_app.h`).

**j0008144.**
- The game was started under `renderdoccmd capture` through a temporary `ORIG_RENDERDOC` mode of
  `original-windows.sh`.
- RenderDoc's injection bypassed the Doorstop isolation: `OriginalIsolation` never confirmed its
  redirections, and the watchdog ended the player 3 s after its log appeared.
- In those seconds the game logged `Applying resolution 2560x1440 @59hz (fullscreen:
  FullScreenWindow)`. That is `GraphicsOptionsScreen.SetResolutionFromPrefs` with no resolution
  in KPlayerPrefs.
- RenderDoc's own log shows it read `%APPDATA%\renderdoc\renderdoc.conf`.

**No user data changed.** The coordinator checked this afterwards, read-only:
- the user's `Documents\Klei` and `LocalLow\Klei` files show no change on 2026-11-22;
- `HKCU\Software\Klei\Oxygen Not Included` was last written 2026-11-21 13:51.

**The rule since then.**
- RenderDoc launches of the original are not used. The `ORIG_RENDERDOC` mode was removed
  (`scripts/` is unchanged from master).
- The probe's in-application API code remains for a future launch that keeps the isolation. It
  runs only with `PROBE_RD_CAPTURE=1` and a `PROBE_RD_TEMPLATE` inside `ONI_RUN_HOME`.
- With renderdoc.dll in the process otherwise, the scenario ends at once.
- Every capture question was answered by the in-process recorder instead.

## 1. Swap chain and Present (measured, j0008139, j0008159, j0008168)

`IDXGISwapChain1::GetDesc1` of the swap chain the game presents with (windowed 1024×768):

| Field | Value | SDK name |
|---|---|---|
| Width × Height | 1024 × 768 | |
| Format | 28 | DXGI_FORMAT_R8G8B8A8_UNORM |
| Stereo, SampleDesc | 0, 1/0 | |
| BufferUsage | 0x30 | SHADER_INPUT \| RENDER_TARGET_OUTPUT |
| BufferCount | **2** | |
| Scaling | 0 | DXGI_SCALING_STRETCH |
| SwapEffect | **3** | **DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL** |
| AlphaMode | 0 | DXGI_ALPHA_MODE_UNSPECIFIED |
| Flags | **0x842** | ALLOW_MODE_SWITCH (2) \| FRAME_LATENCY_WAITABLE_OBJECT (64) \| ALLOW_TEARING (2048) |

- **`GetDesc`.**
  - RefreshRate 0/1, scanline and scaling 0, Windowed 1.
  - `GetFullscreenDesc`: RefreshRate 0/1, Windowed 1.
  - `GetFullscreenState`: FALSE.
- **Interfaces.** It answers `IDXGISwapChain2`, `3` and `4`.
- **Frame latency.**
  - `IDXGISwapChain2::GetMaximumFrameLatency` = **2**.
  - The device's `IDXGIDevice1::GetMaximumFrameLatency` = 3, the same as a fresh device's on both
    GPUs (caps.exe).
- **Buffer 0.** An `ID3D11Texture2D`, 1024×768, format 28, usage DEFAULT, bind 0x28 (SRV | RTV),
  no CPU access, misc 0.
- **Tearing.** The factory's `CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING)` = TRUE.
- **Present calls.**
  - Only `IDXGISwapChain::Present`, never `Present1`.
  - **Every Present came from one OS thread.** In j0008168, all 2,439 calls were on thread 8304,
    tallied per call.
  - **Vsync on** (the game's setting, `QualitySettings.vSyncCount` 1): **`Present(1, 0)`**, every
    call in the menu, the load and the colony (1,899 calls).
  - **Vsync off** (`vSyncCount` set to 0): **`Present(0, DXGI_PRESENT_ALLOW_TEARING)`** (flags
    0x200; 540 calls). The counts are the same in j0008159 and j0008168.
- **`ResizeBuffers`.** Not called in these runs (no resize).
- **Frame statistics.** Unity's log says: "Direct3D: detected that
  IDXGISwapChain::GetFrameStatistics is broken (it does not return proper timestamps). Delta time
  will now be calculated using cpu-side time stampling until the game window is moved." This is
  Unity's own log line; `GetFrameStatistics` was not hooked.
- **The camera's target.** It is not buffer 0. Part A's screen camera drew to a
  R8G8B8A8_TYPELESS (27) texture; buffer 0 is R8G8B8A8_UNORM (28), and one texture cannot report
  two formats. This is a cross-run inference; the pointers were not compared.

## 2. Depth bias, cull, depth test and blend (measured, j0008159 and j0008168, RTX 3090)

**The shader.** `Hidden/Internal-Colored` (`unity default resources`) has `Offset [_ZBias],
[_ZBias]`, `Cull [_Cull]`, `ZTest [_ZTest]`, `ZWrite [_ZWrite]` and `Blend SrcAlpha
OneMinusSrcAlpha`. This was read from its serialized state by `ShaderSurvey states`.
- Of the data's 179 passes, only two others have an Offset: TextMeshPro Surface s0p1 and s0p2
  (`Offset 1, 1`).
- No pass has a factor different from its units.

**The draws.** The probe drew one quad per case into a 64×64 `RenderTexture` with 24-bit depth,
which is D32_SFloat_S8_UInt (DSV format 20, `DXGI_FORMAT_D32_FLOAT_S8X24_UINT`). It repeated
every case with 16-bit depth (DSV format 55, D16_UNORM). The rows from 1.5 on are j0008168's.

| `_ZBias` (factor = units) | DepthBias (INT) | SlopeScaledDepthBias | DepthBiasClamp |
|---|---|---|---|
| 0 | 0 | −0 | 0 |
| 1 | −1 | −1 | 0 |
| −1 | 1 | 1 | 0 |
| 2 | −2 | −2 | 0 |
| 0.5 | 0 | −0.5 | 0 |
| −3.25 | 3 | 3.25 | 0 |
| 10 | −10 | −10 | 0 |
| 100 | −100 | −100 | 0 |
| **1.5** | **−1** | −1.5 | 0 |
| **−1.5** | **1** | 1.5 | 0 |
| **2.5** | **−2** | −2.5 | 0 |
| **−2.5** | **2** | 2.5 | 0 |
| 0.75 | 0 | −0.75 | 0 |
| −0.75 | 0 | 0.75 | 0 |

The D32_S8 and D16 targets give identical values.

**The mapping, as measured.**
- `SlopeScaledDepthBias = −factor`.
- `DepthBias = −trunc(units)`, **truncated toward zero**. ±1.5 → ∓1 and ±2.5 → ∓2 rule out
  round-half-to-even (±2 for 1.5), round-half-up (2 for 1.5) and round-half-away (2 for 1.5);
  ±0.75 → 0 rules out round-to-nearest.
- `DepthBiasClamp = 0`.
- No scaling by depth format.
- The negation is the reversed-Z one, the same as our Vulkan device's `−OffsetUnits` /
  `−OffsetFactor` (`VkPipelines.cs`).

**Which goes where.**
- In every measured case factor and units are equal, so the measurement cannot tell which D3D11
  field each one feeds.
- Unity's documentation says factor "Scales the maximum Z slope" and units "Scales the minimum
  resolvable depth buffer value, to produce a constant depth offset" (docs.unity.com, 6000.0
  manual, ShaderLab command Offset).
- Microsoft's Depth Bias page gives `SlopeScaledDepthBias * MaxDepthSlope`, plus `DepthBias * r`
  (UNORM, r the format's smallest step) or `DepthBias * 2**(exponent(max z in primitive) - r)`
  (float, r = 23 for float32).
- So factor → SlopeScaledDepthBias and units → DepthBias, as cited.
- **On a D32_S8 target** one DepthBias unit is `2^(e − 23)`, where e is the exponent of the
  primitive's largest z (same page). Unity passes the truncated integer through unscaled.
- How the original's GLCore renderer sets polygon offset was not measured in M0.

**Rasterizer, depth-stencil and blend state of the same draws:**

| Unity state | D3D11 state |
|---|---|
| `Cull Off` (0) | CullMode 1 (D3D11_CULL_NONE) |
| `Cull Front` (1) | 2 (D3D11_CULL_FRONT) |
| `Cull Back` (2) | 3 (D3D11_CULL_BACK) |
| every probe draw (render textures) | FillMode 3 (SOLID), **FrontCounterClockwise 1**, DepthClipEnable 1, ScissorEnable 0, MultisampleEnable **1** (single-sample target), AntialiasedLineEnable 0 |
| `ZTest Less` (2) | DepthFunc 5 (GREATER) |
| `ZTest Equal` (3) | 3 (EQUAL) |
| `ZTest LEqual` (4) | 7 (GREATER_EQUAL) |
| `ZTest Greater` (5) | 2 (LESS) |
| `ZTest NotEqual` (6) | 6 (NOT_EQUAL) |
| `ZTest GEqual` (7) | 4 (LESS_EQUAL) |
| `ZTest Always` (8) | 8 (ALWAYS), **DepthEnable stays 1** |
| `ZWrite Off` / `On` | DepthWriteMask 0 / 1 |
| no stencil block | StencilEnable 0, read/write masks 0xFF, ops KEEP/KEEP/KEEP, func ALWAYS |
| `Blend SrcAlpha OneMinusSrcAlpha` | BlendEnable 1, SRC_ALPHA (5) / INV_SRC_ALPHA (6), op ADD, the same for alpha, write mask 0xF |

Viewport (0, 0, 64, 64), depth 0..1; RTV format 28 (R8G8B8A8_UNORM).

**Depth test.** The comparisons are reversed for reversed Z (Less ↔ Greater, LEqual ↔ GEqual).
That agrees with part A's measurement that a clear to v stores 1 − v. In the colony frames
`ClearDepthStencilView` is always flags 3 (depth | stencil), depth **0**, stencil 0.

**The colony's other states** (j0008159, `report-colony.txt`; the same kinds in j0008168):
- **Rasterizers.** Three kinds:
  - CULL_NONE with FrontCounterClockwise 1 (most world draws);
  - CULL_BACK with FrontCounterClockwise 1 (light buffer, blur, compositor, fog-of-war passes);
  - **CULL_NONE with FrontCounterClockwise 0**, used by the UI draws (vertex stride 76).
  - Why the UI's state has the other winding is **UNVERIFIED**.
- **Stencil.** It is used: states with StencilEnable 1, ref 1 or 3, funcs EQUAL (3), LESS_EQUAL
  (4), NOT_EQUAL (6) and ALWAYS (8), pass ops KEEP (1), ZERO (2) and REPLACE (3). The masked UI
  uses read mask 0x1 with write mask 0.
- **Blend states.** These appear:
  - One / Zero (off);
  - One + One with alpha op MAX (5);
  - SrcAlpha / One with mask 0x7;
  - One / InvSrcAlpha, including mask 0;
  - SrcAlpha / InvSrcAlpha with mask 0xF or 0x7;
  - Zero / SrcColor (src 1, dst 3) with alpha Zero / SrcAlpha.
- **Samplers.** Five descriptions:
  - filters 0 (MIN_MAG_MIP_POINT), 0x14 (MIN_MAG_LINEAR_MIP_POINT) and 0x55 (ANISOTROPIC,
    MaxAnisotropy 4);
  - addressing WRAP (1) or CLAMP (3);
  - MipLODBias 0, MinLOD −FLT_MAX, MaxLOD FLT_MAX, ComparisonFunc 1 (NEVER), border 0.

## 3. Shaders, input layouts, buffers, views (measured, j0008159 and j0008168)

- **Shaders are the shipped DXBC.**
  - The game made **732 shader objects** after the hook (j0008159: 354 `CreateVertexShader`, 378
    `CreatePixelShader`; j0008168: 354 and 377). They hold **420 distinct bytecodes** (DXBC
    checksums), the same set in both runs.
  - 416 of the 420 are byte-identical to shipped Direct3D 11 programs, matched by checksum.
  - The other 4 (vertex shaders of 852 and 1,064 bytes, pixel shaders of 416 and 568 bytes) match
    no program in the scanned files. ShaderSurvey's list excludes StreamingAssets bundles, so they
    are the player's built-ins or bundle shaders (**UNVERIFIED**).
  - No geometry shader was created in these runs, through the hooked `CreateGeometryShader`.
    `CreateGeometryShaderWithStreamOutput` and the hull, domain and compute creators were not
    hooked.
- **Input layouts.** **55 were created** in each run (53–54 before the colony recording). They
  hold **15 distinct element lists** and **34 distinct signature blobs**.
  - Each layout has **14 elements**, in this order: POSITION0, NORMAL0, TANGENT0, COLOR0,
    TEXCOORD0–7, BLENDWEIGHTS0, BLENDINDICES0 (`BLENDWEIGHTS` with the S). All are per-vertex
    (class 0, step 0).
  - Real-looking entries carry real formats at increasing offsets in slot 0, for example
    POSITION R32G32B32_FLOAT (6) at 0, NORMAL (6) at 12, TANGENT R32G32B32A32_FLOAT (2) at 24,
    TEXCOORD R32G32_FLOAT (16) or R32G32B32A32_FLOAT (2).
  - The other entries are **R8_UNORM (61), slot 0, offset 0**.
  - 17 of the 55 layouts use **slot 1**. 15 put COLOR0 R8G8B8A8_UNORM at slot 1 offset 4. Two also
    put NORMAL0 at slot 1 offset 8 and TANGENT0 at slot 1 offset 12 (all R8G8B8A8_UNORM).
  - What the R8_UNORM-at-0 entries and the slot-1 entries stand for (channels the mesh lacks,
    default values) is **UNVERIFIED**. The meshes' channels were not logged, and no recorded draw
    used a slot-1 layout: every logged draw had vb1 null.
  - The signature bytecode passed to `CreateInputLayout` is 88–284 bytes and matches no shipped
    program. It is presumably a signature-only container Unity builds (**UNVERIFIED**).
- **Draws.**
  - Every draw is `DrawIndexed`, topology 4 (TRIANGLELIST), with a 16-bit index buffer
    (R16_UINT, 57).
  - Vertex strides seen: 20, 24, 32, 36, 48, 56, 76 (UI), 88 bytes.
  - `IASetVertexBuffers` and `IASetIndexBuffer` are called about once per draw (1,294 calls for
    1,296 draws).
- **Constant buffers, per stage.**
  - Each slot holds its own buffer of the cbuffer's exact size, DYNAMIC with CPU write, bound
    whole (`first` 0, `num` 4096), as part A found.
  - In the colony the VS has b0 the material's buffer (48 to 4,848 bytes, e.g. BatchedAnimation
    368, TiledBlock 400, BackWallArray 4,848), then 144 or 176, 176 or 368, 368, 368.
  - The PS has b0 the material's, b1 144 or 176, b2–b4 368, and **b5 128** on every logged draw.
  - b0 is mapped with `WRITE_DISCARD` before the draws that use it. b1–b5 are rebound rarely
    (VS/PS `SetConstantBuffers` 164 and 106 calls in the colony frame).
- **Shader resources and samplers.**
  - They are bound per draw and rebound often: `VSSetShaderResources` 5,407 and
    `PSSetShaderResources` 3,123 calls for 1,296 draws.
  - The VS reads textures too: BatchedAnimation's build and frame data, R32G32B32A32_FLOAT
    textures such as 64×32, 16×16 and 4×16.
  - View dimensions among the colony frame's logged bindings (j0008168): TEXTURE2D (4) 1,683,
    **TEXTURE2DARRAY (5)** 10 (BC1 and BC3, 1024×1024), **TEXTURE3D (8)** 7 (R8G8B8A8; the size
    was not read).
  - Formats seen include A8_UNORM (65), BC1 (71), BC3 (77), R8_UNORM (61), R8G8B8A8 (28),
    R16G16B16A16_FLOAT (10), R32G32_FLOAT (16) and R32G32B32A32_FLOAT (2).
- **Targets.**
  - The world draws go to 1024×768 R8G8B8A8_UNORM with DSV D32_FLOAT_S8X24_UINT (20).
  - The light buffer goes to 1024×768 R16G16B16A16_FLOAT (10), with no depth.
  - Off-screen passes use 722×304, 512×384 (R16G16B16A16_FLOAT, Flow), 256×192 and 128×96 (blur,
    bloom).
  - Viewports always cover the target, depth 0..1.
  - A scissor rectangle (0, 0, 1024, 768) stays set while ScissorEnable is 0.
  - The frame's last draw has no DSV. Whether it targets buffer 0 is **UNVERIFIED** (the RTV's
    resource was not compared with buffer 0).

## 4. Threading support of both GPUs (measured, caps.exe, 2026-11-22)

`D3D11CreateDevice` on each adapter from `IDXGIFactory1::EnumAdapters1`, `D3D_DRIVER_TYPE_UNKNOWN`,
levels 11_1 to 10_0. Two runs per adapter: flags `D3D11_CREATE_DEVICE_BGRA_SUPPORT` (the
original's, part A §5) and flags 0. Both runs answer the same.

| | NVIDIA GeForce RTX 3090 | AMD Radeon(TM) Graphics (iGPU) |
|---|---|---|
| Vendor / device | 0x10de / 0x2204 | 0x1002 / 0x13c0 |
| UMD version (`CheckInterfaceSupport`) | 32.0.16.1088 | 32.0.21042.62 |
| Feature level | 11_1 (0xb100) | 11_1 (0xb100) |
| THREADING DriverConcurrentCreates | 1 | 1 |
| THREADING **DriverCommandLists** | **1** | **0** |
| OPTIONS ConstantBufferOffsetting | 1 | 1 |
| OPTIONS ConstantBufferPartialUpdate | 1 | 1 |
| OPTIONS MapNoOverwriteOnDynamicConstantBuffer | 1 | 1 |
| OPTIONS MapNoOverwriteOnDynamicBufferSRV | 1 | 1 |
| OPTIONS DiscardAPIsSeenByDriver / FlagsForUpdateAndCopySeenByDriver | 1 / 1 | 1 / 1 |
| OPTIONS ClearView / CopyWithOverlap / OutputMergerLogicOp | 1 / 1 / 1 | 1 / 1 / 1 |
| OPTIONS1 TiledResourcesTier / MinMaxFiltering / MapOnDefaultBuffers | 2 / 1 / 1 | 2 / 1 / 1 |
| OPTIONS2 PSSpecifiedStencilRef / ROVs / ConservativeRaster tier | 0 / 1 / 3 | 1 / 1 / 3 |
| OPTIONS2 TiledResourcesTier / MapOnDefaultTextures / UnifiedMemoryArchitecture | 3 / 1 / 0 | 2 / 1 / **1** |
| OPTIONS3 VPAndRTArrayIndexFromAnyShaderFeedingRasterizer | 1 | 1 |
| OPTIONS4 ExtendedNV12SharedTexture / OPTIONS5 SharedResourceTier | 1 / 3 | 1 / 3 |
| ARCHITECTURE_INFO TileBasedDeferredRenderer | 0 | 0 |
| DOUBLES | 1 | 1 |
| IDXGIDevice1 maximum frame latency (default) | 3 | 3 |
| ID3D11Multithread::GetMultithreadProtected (default) | 0 | 0 |
| CheckFormatSupport D32_FLOAT_S8X24_UINT (20) / R32G8X24_TYPELESS (19) / R32_FLOAT_X8X24_TYPELESS (21) | 0x3310b0 / 0x1210b0 / 0x4d217b0 | same |
| CheckFormatSupport D24_UNORM_S8_UINT (45) | 0x3310b0 | 0x3310b0 |
| CheckFormatSupport R16G16B16A16_FLOAT (10) | 0x12fef3f3 | 0x32fef3f3 |

**What the threading fields mean** (Microsoft, `D3D11_FEATURE_DATA_THREADING`):
- `DriverCommandLists` FALSE "means that the API will emulate deferred contexts and command lists
  with software".
- `DriverConcurrentCreates` TRUE "means resources can be created concurrently on multiple threads
  while drawing".
- So on the AMD iGPU, deferred contexts work but are emulated by the runtime.

**Other results.**
- The factory reports PRESENT_ALLOW_TEARING TRUE.
- The RTX 3090 is listed twice (adapters 0 and 2, LUIDs 000125ad and 0001f659, identical
  answers); why is **UNVERIFIED**.
- Adapter 3 is the Microsoft Basic Render Driver (software, not queried).

## 5. The original's per-frame call counts on Direct3D 11 (measured, RTX 3090)

**How it was counted.**
- One frame, `Present` to `Present`, with the helper's hooks counting each call.
- "Changes" counts a setting only when the object differs from the previous one set. Unity issues
  no repeated shader or input-layout settings itself: every such call was a change. Changes of
  views, samplers and buffers were not tracked.
- Only the immediate context made calls in the recorded frames, and no `ExecuteCommandList` was
  called. Any deferred context would have to be executed on the immediate one, so the original
  records nothing on deferred contexts.
- `CreateDeferredContext` was not called after the hook (the menu on). The engine log says
  `GfxDevice: creating device client; kGfxThreadingModeClientWorkerJobs`.
- Each scene is one frame per run, two runs: a sample, not a distribution.

| Per frame (j0008159; j0008168 in brackets) | Main menu | Colony (2minnow) | Colony, temperature overlay |
|---|---|---|---|
| Time between the two Presents (hooks on) | 16.8 ms [16.9] (vsync) | 37.9 ms [26.2] | 45.5 ms [37.7] |
| Draws (all `DrawIndexed`) | 41 [41] | **1,296** [1,298] | **1,494** [1,525] |
| Distinct (VS, PS, layout, RS, BS, DS, topology) combinations | 12 [12] | 48 [49] | 54 [54] |
| VS / PS `SetShader` (all changes) | 23 / 23 | 116 / 116 [110] | 448 / 448 [459] |
| `IASetInputLayout` (all changes) | 16 | 59 [57] | 61 [60] |
| `IASetVertexBuffers` / `IASetIndexBuffer` | 41 / 41 | 1,294 / 1,294 [1,297] | 1,493 / 1,493 [1,522] |
| `RSSetState` calls / changes | 1 / 1 | 35 / 12 [36 / 12] | 40 / 14 [40 / 14] |
| `OMSetBlendState` calls / changes | 17 / 17 | 74 / 47 [67 / 40] | 288 / 258 [293 / 263] |
| `OMSetDepthStencilState` calls / changes | 18 / 18 | 90 / 88 [82 / 81] | 315 / 314 [322 / 321] |
| `OMSetRenderTargets` | 8 | 43 [43] | 48 [48] |
| `RSSetViewports` / `RSSetScissorRects` | 11 / 2 | 77 / 7 | 84 / 8 |
| VS / PS `SetConstantBuffers` | 30 / 14 | 164 / 106 [158 / 103] | 180 / 120 [175 / 120] |
| VS / PS `SetShaderResources` | 22 / 82 [22 / 89] | 5,407 / 3,123 [5,373 / 2,977] | 7,521 / 5,924 [7,709 / 6,145] |
| VS / PS `SetSamplers` | 4 / 59 [4 / 65] | 3,682 / 2,818 [3,654 / 2,669] | 5,170 / 5,286 [5,306 / 5,494] |
| **Constant-buffer updates**: `Map(WRITE_DISCARD)` | 30 [30] | **1,832** [1,821] | **2,188** [2,237] |
| Constant-buffer bytes mapped | 7,168 | 507,440 [504,928] | 636,688 [650,224] |
| Constant buffers mapped by size: ≤128 / ≤256 / ≤512 / ≤1K / larger | 18 / 1 / 8 / 3 / 0 | 34 / 890 / 904 / 3 / 1 | 39 / 921 / 1,221 / 6 / 1 |
| Other buffers: `Map(WRITE_DISCARD)` / `Map(WRITE_NO_OVERWRITE)` | 2 / 4 | 6 / 68 | 6 / 76 |
| Textures: `Map(WRITE)` / `CopySubresourceRegion` | 0 / 0 | 298 / 298 [248 / 248] | 268 / 269 [290 / 291] |
| `UpdateSubresource` | 0 | 0 | 0 |
| Clears: RTV / DSV | 2 / 2 | 6 / 6 | 8 / 6 |
| Context calls hooked, in total | 529 [542] | 24,519 [23,961] | 35,780 [36,814] |

**Notes for the threading design.**
- **Constant buffers.**
  - About 1.4 constant-buffer maps per draw in these frames, nearly all 129–512 bytes.
  - Always `WRITE_DISCARD` on a whole small buffer: never `NO_OVERWRITE`, never
    `UpdateSubresource`, never `*SetConstantBuffers1` offsets.
  - Both GPUs offer ConstantBufferOffsetting and MapNoOverwriteOnDynamicConstantBuffer, which
    would allow one large ring buffer instead.
- **Per-draw rebinding.** About 4 VS and 2.4 PS shader-resource settings and about 5 sampler
  settings per draw.
- **Texture writes.** About 250–300 textures per frame are written by `Map(WRITE)`, with the same
  number of `CopySubresourceRegion` calls. That these are staging uploads is **UNVERIFIED**: the
  source's usage was not logged.
- **Main-thread time** (`Time.unscaledDeltaTime` over the 300 frames before each recording, hooks
  idle):

  | Run | Colony | Temperature overlay | Overlay without its one 5,064 ms frame | Vsync off |
  |---|---|---|---|---|
  | j0008159 | 43.1 ms mean, max 86.1 | 60.8 ms mean, max 5,064.3 | about 44.1 ms | 44.1 ms mean, max 65.1 |
  | j0008168 | 28.9 ms mean, max 49.9 | 41.4 ms mean, max 72.8 | (no such frame) | 38.3 ms mean, max 54.2 |

  - The 5,064 ms frame in j0008159 fell inside the measurement window, which starts 240 frames
    after the overlay switch. Its cause was not measured.
  - The machine was shared with other agents' jobs, so these are not performance figures
    (docs/PERFORMANCE.md has those).
  - They show the frame is longer than the 16.7 ms vsync interval: the game is not vsync-limited
    in this colony.
  - Whether the CPU or the GPU limits it was not measured.

### What the recorder sees and what it misses

**The context's vtable.**
- The immediate context's vtable pointer points into the object itself (vtable = object + 8;
  `VirtualQuery`: PAGE_READWRITE, MEM_PRIVATE). That makes it a per-object copy.
- The runtime rewrites a group of its slots while the game runs: the eight draw and dispatch
  slots, `UpdateSubresource`(1), `CopyResource`, `CopySubresourceRegion`, both clears,
  `GenerateMips` and `ResolveSubresource`.
  - In j0008168, with the counts kept apart, each slot of the group was found rewritten once when
    each recording started.
  - During the frame, each was rewritten 1 time in the menu and 9 times in each colony frame.
- j0008151's one-time patch therefore kept every Set and Map hook but lost all draw hooks. The
  recorder now re-checks every hooked slot of the calling context's vtable after each hooked
  call, and forwards to whatever function the runtime last put there.

**Draws missed.**
- A draw directly after an unhooked call that rewrote the slots would not be counted.
- Draws (1,296; 1,298) and vertex-buffer settings (1,294; 1,297) are nearly equal, which suggests
  none were. The draw counts are lower bounds, exact as far as that check shows.
- Draws by other routes (NVAPI) were not looked for.

**Entry points not hooked.** `DispatchIndirect`; the compute, hull and domain stage setters;
`CopySubresourceRegion1`; `ClearView`; `DiscardResource` / `DiscardView`;
`ClearUnorderedAccessView*`; `CreateGeometryShaderWithStreamOutput` and the hull, domain and
compute shader creators. The clear, copy and shader counts above cover the hooked entry points
only.

## Tools added

- `tools/D3d11Native/` (`build.sh`, `build.cmd`):
  - `caps.exe`: adapters, feature levels, threading, options, format support;
  - `probe_native.dll` (`probe_native.c`, `ctx_hook.c`): the RenderDoc in-application API, opt-in
    only; the swap-chain hook; the frame recorder; the state read-back event;
  - `frame-report.py`: names a recorded frame's shaders.
- `tools/UnityProbe/RdCapture.cs` (`PROBE_SCENARIO=rdcapture`).
- `ShaderSurvey states` (each pass's cull, ZTest, ZWrite and Offset) and `ShaderSurvey dxbc`
  (the shipped Direct3D 11 programs' checksums).

## UNVERIFIED, by the milestone that needs it

**M2 (device bring-up, own swap chain)**, each settled or moved by M2's review fixes
(d3d11-plan.md, "M2 review fixes"):
1. **The frame-latency waitable.** Whether and when the game waits on it; Unity's
   `GetFrameStatistics` use (only its log line is known). **Moved to M4** (presentation and frame
   pacing): the device no longer copies any rule from the original here. It waits as Microsoft
   documents ("Reduce latency with DXGI 1.3 swap chains", step 4: before rendering every frame,
   the first included), so nothing rests on the original's choice until M4 compares pacing.
   Test: a hook of `WaitForSingleObjectEx` on the swap chain's waitable handle in the recorder.
2. **The frame's last draw.** Whether it targets swap-chain buffer 0. Compare the RTV resource
   with buffer 0 in the recorder. **Moved to M4**, with d3d11-plan.md's B2 UNVERIFIED 5 (the
   screen drawn into an R8G8B8A8_TYPELESS texture): the device draws into buffer 0 until then.
3. **The duplicate RTX 3090** in DXGI's adapter enumeration. **Moved to M5**: it matters only
   for the GPU choice rule (done criterion 5), which M5 settles; M2 takes EnumAdapters1(0) or
   EnumAdapterByGpuPreference and never walks the list.
4. **RenderDoc under the isolation.** Why Doorstop does not run under RenderDoc's injection.
   **Closed, not pursued**: every capture question of M0 to M2 was answered by the in-process
   recorder (tools/D3d11Native), which keeps the isolation; no later milestone lists a question
   only RenderDoc answers. Reopened only if one does.

**M3 (full draw feature set)**, each settled or moved in M3 (d3d11-plan.md, "M3"):
5. **Factor and units.** That factor feeds SlopeScaledDepthBias and units DepthBias is cited, not
   measured: no shipped shader has factor ≠ units. The GLCore renderer's polygon-offset values
   are not measured. **Closed for the game**: with factor = units in every shipped pass (§2,
   ShaderSurvey `states`) the two assignments give the same state, so no game draw can tell
   them apart; the cited one stays. **Moved to M7** for mods (a mod pass with factor ≠ units
   would tell them apart). GLCore's values are not needed for D3D11 parity.
6. **The UI's winding.** **Settled** (observed, j0008168's recordings): the state with
   FrontCounterClockwise 0 is bound only with the two 1024×768 views the main menu's frame draws
   everything into (the menu scene, its UI at stride 76, and the frame's last draw, stride 88,
   without depth), the same views in the colony frames: the screen's surfaces, which Unity
   renders unflipped (d3d11-unity-probes.md §2; that these views are the screen's is inferred
   from what they take, the pointers were not compared with buffer 0); every draw into a render texture (MRT0 and MRT1 with
   depth, the light buffer, the blur, bloom and fog-of-war targets) has FrontCounterClockwise
   1. So the winding follows the target: unflipped screen 0, flipped render texture 1, as the
   device binds them (D3d11States).
7. **Input layouts.** What the R8_UNORM-at-slot-0 entries and the slot-1 entries stand for.
   **Moved to M7**: the device's layouts name only the inputs a program reads (d3d11-plan.md,
   B2), and the scene set's pixels show no input differing (M3); the original's extra elements
   matter only for parity of what a mod's program could read, which M7 owns.
8. **The 4 unmatched shaders** (built-ins or StreamingAssets bundle shaders). **Moved to M7**:
   the player's built-ins and bundles are what M7's mod and compatibility work reads. M3 part C
   added the Spaced Out bundle's copies of Klei/LiquidSubstance and Klei/Substance to
   `--check-shaders` (j0008865: 123 shaders, 1,082 subprograms, all built); which copy a draw
   uses stays M7's.
9. **TMP Surface s0p1/s0p2.** That they are shadow-caster passes. **Moved to M7**: no scene of
   the scene set draws them (M3's coverage list), so only a mod's use would show it.

**M4 (presentation and frame pacing)**
10. **The full-screen swap chain.** Its description (`FullScreenWindow` and exclusive), and
    `ResizeBuffers` arguments on a resize.

**M6 (performance)**
11. **Texture writes.** That the 250–300 `Map(WRITE)` textures per frame are staging uploads.
12. **The CPU split.** Main thread, render thread and driver; CPU against GPU limits.
13. **Typical frames.** Frames other than the one recorded per scene and run.
14. **Missed draws.** Draws missed after unhooked calls, or made by other routes (NVAPI).

**M7 (mods and compatibility)**
15. **The original on the AMD iGPU.** Its swap chain, state and frame counts there.
