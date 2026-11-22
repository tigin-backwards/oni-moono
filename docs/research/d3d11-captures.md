# The original's Direct3D 11 frames: swap chain, state, depth bias, threading, cost

Milestone M0, part C of the Direct3D 11 plan ([d3d11-plan.md](d3d11-plan.md)), after part A
([d3d11-unity-probes.md](d3d11-unity-probes.md)). Every value below was measured on the original
game or read from the Windows SDK 10.0.26100.0 headers, except the lines that cite a public
document. No Unity engine source was read. Anything not measured or cited is marked
**UNVERIFIED**.

## How it was measured

- **Build.** The frozen U60 build 764025 (`E:\oni-builds\BrutalNotIncluded-764025`), Unity
  6000.3.17f1, by `scripts/original-windows.sh` (isolated run home, monitor 2, volume 0, a
  DLC-owning offline Steam double), windowed 1024×768, default renderer (Direct3D 11), RTX 3090,
  driver 32.0.16.1088 (Unity's `Driver:` log line).
- **The probe.** `tools/UnityProbe` with `PROBE_SCENARIO=rdcapture` (`RdCapture.cs`) and its native
  helper `tools/D3d11Native/probe_native.dll` (`probe_native.c`, `ctx_hook.c`), built with MSVC
  14.44 against the SDK headers. Method slots come from the headers' C vtables (`offsetof`), and
  every hook's type is checked against the header's member type at compile time.
- **What the helper does in the game's process:**
  - **Swap chain.** It makes swap chains on a hidden, never-shown window with the game's own DXGI
    factory. All three kinds (flip and blt `CreateSwapChainForHwnd`, legacy `CreateSwapChain`) had
    the same vtable, whose `Present`, `Present1` and `ResizeBuffers` slots it hooks. The game's
    swap chain then reports itself on its first `Present`.
  - **Device and context.** It hooks the device's `CreateVertexShader`, `CreatePixelShader`,
    `CreateGeometryShader`, `CreateInputLayout` and `CreateDeferredContext` and 46 slots of the
    immediate context: draws, shader, buffer, view, sampler and state settings, `Map`/`Unmap`,
    updates, copies and clears. It records one whole frame, from one `Present` to the next.
  - **Depth bias.** The probe draws `Hidden/Internal-Colored` into render textures with a range of
    material values. After each draw a rendering-event callback (`CommandBuffer.IssuePluginEvent`)
    reads the context's rasterizer, depth-stencil and blend state back with `RSGetState`,
    `OMGetDepthStencilState` and `OMGetBlendState`.
- **Colony.** `tests/e2e/fixtures/2minnow.sav` (a mid-game Spaced Out colony), resumed by the
  probe from the main menu, unpaused, then the temperature overlay.
- **Runs** (runq, `windows` slot, burndown): j0008139 (swap chain, depth bias; that colony loaded
  without its DLCs, so its colony lines are not used), j0008151 (first frame recording; no draws
  counted, see "The context's vtable" below), **j0008159 (the frame recordings used here)**.
  Raw logs: `out/d3d11-captures/hook/` (`Player.log`, `rd.txt`, `report-*.txt`,
  `distinct-colony.txt`) and `out/d3d11-captures/hook-run2/`; not in git.
- **Threading.** `tools/D3d11Native/caps.exe`, a standalone program, run on the desktop through
  runq (`light`): `out/d3d11-captures/caps.txt`.
- **Shader names.** `ShaderSurvey dxbc DATADIR` lists the DXBC container checksum (bytes 4..19)
  of every shipped Direct3D 11 program. `tools/D3d11Native/frame-report.py` matches each shader the
  game creates by its checksum.

### RenderDoc: not used (an isolation failure)

**RenderDoc 1.43's documentation.**
- `renderdoccmd capture` has no option to capture given frames (`renderdoccmd capture --help`).
- The in-application API is the documented route: "use `GetModuleHandle` on Windows" when
  renderdoc.dll is already injected, then `RENDERDOC_GetAPI`. `TriggerCapture` "will trigger a
  capture as if the user had pressed one of the capture hotkeys". `StartFrameCapture` /
  `EndFrameCapture` take `NULL` "to wildcard match" (renderdoc.org/docs/in_application_api.html,
  and the shipped `renderdoc_app.h`).
- The probe and the helper implement this (`pn_rd_init`, `pn_rd_trigger`, render events 1 and 2).

**What went wrong** (run j0008144):
- The game was started under `renderdoccmd capture` through a temporary `ORIG_RENDERDOC` mode of
  `original-windows.sh`.
- Unity Doorstop's `OriginalIsolation` never confirmed its redirections, and the watchdog ended
  the player 3 s after its log appeared.
- Before that the game logged `Applying resolution 2560x1440 @59hz (fullscreen:
  FullScreenWindow)`. That is the path `GraphicsOptionsScreen.SetResolutionFromPrefs` takes when
  KPlayerPrefs holds no resolution, so the run home's `kplayerprefs.yaml` (1024×768) was not read.
- Whether the user's own `Documents\Klei\OxygenNotIncluded` was read or written in those seconds
  is **UNVERIFIED** (not inspected, by the rules).
- RenderDoc's own log shows it read `C:\Users\…\AppData\Roaming\renderdoc\renderdoc.conf`.

**What was done instead.** The `ORIG_RENDERDOC` mode was removed. Every capture question was
answered by the in-process recorder above instead, which runs under the normal, confirmed
isolation.

**To use RenderDoc on the original later:**
- First find why Doorstop does not run under RenderDoc's injection. That is **UNVERIFIED**; one
  guess is that both hook the loader.
- Add a guard that ends the game before any user data is read when the isolation is not
  confirmed.

## 1. Swap chain and Present (measured, j0008139 and j0008159)

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
  - Only `IDXGISwapChain::Present`, never `Present1`, all from one thread.
  - **Vsync on** (the game's setting, `QualitySettings.vSyncCount` 1): **`Present(1, 0)`**, every
    call in the menu, the load and the colony (1,899 calls).
  - **Vsync off** (`vSyncCount` set to 0): **`Present(0, DXGI_PRESENT_ALLOW_TEARING)`** (flags
    0x200; 540 calls).
- **`ResizeBuffers`.** Not called in these runs (no resize).
- **Frame statistics.** Unity calls `GetFrameStatistics`. Its log says: "Direct3D: detected that
  IDXGISwapChain::GetFrameStatistics is broken (it does not return proper timestamps). Delta time
  will now be calculated using cpu-side time stampling until the game window is moved."
- **UNVERIFIED:**
  - whether and how the game waits on the frame-latency waitable object;
  - the full-screen (`FullScreenWindow`) and exclusive-mode descriptions;
  - the other GPU (AMD); only the RTX 3090 was measured.

## 2. Depth bias, cull, depth test and blend (measured, j0008139, RTX 3090)

`Hidden/Internal-Colored` (`unity default resources`) has `Offset [_ZBias], [_ZBias]`,
`Cull [_Cull]`, `ZTest [_ZTest]`, `ZWrite [_ZWrite]` and `Blend SrcAlpha OneMinusSrcAlpha`. This
was read from its serialized state by `ShaderSurvey states`, which this milestone adds.
- Of the game's 179 passes, only two others have an Offset: the TextMeshPro surface shaders'
  shadow-caster passes (`Offset 1, 1`). No game pass has a factor different from its units.
- The probe drew one quad per case into a 64×64 `RenderTexture` with 24-bit depth, which is
  D32_SFloat_S8_UInt (DSV format 20, `DXGI_FORMAT_D32_FLOAT_S8X24_UINT`). It repeated each case
  with 16-bit depth (DSV format 55, D16_UNORM).

**Depth bias:**

| `_ZBias` (factor = units) | DepthBias (INT) | SlopeScaledDepthBias | DepthBiasClamp |
|---|---|---|---|
| 0 | 0 | −0 | 0 |
| 1 | −1 | −1 | 0 |
| −1 | 1 | 1 | 0 |
| 2 | −2 | −2 | 0 |
| 0.5 | **0** | −0.5 | 0 |
| −3.25 | **3** | 3.25 | 0 |
| 10 | −10 | −10 | 0 |
| 100 | −100 | −100 | 0 |

The D32_S8 and D16 targets give identical values.

**The mapping, as measured.**
- `SlopeScaledDepthBias = −factor`.
- `DepthBias = −units`, made an integer with the fraction dropped: 0.5 → 0, −3.25 → 3.
  Truncation toward zero and round-half-to-even both give these; the rounding rule is
  **UNVERIFIED**.
- `DepthBiasClamp = 0`.
- No scaling by depth format.
- The negation is the reversed-Z one, the same as our Vulkan device's `−OffsetUnits` /
  `−OffsetFactor` (`VkPipelines.cs`).

**Which goes where.**
- In every measured case factor and units are equal, so the measurement cannot tell which D3D11
  field each one feeds.
- Unity's documentation says factor "Scales the maximum Z slope" and units "Scales the minimum
  resolvable depth buffer value, to produce a constant depth offset"
  (docs.unity.com, 6000.0 manual, ShaderLab command Offset). Microsoft's Depth Bias page gives
  `SlopeScaledDepthBias * MaxDepthSlope` and `DepthBias * r` (UNORM), or
  `DepthBias * 2**(exponent(max z in primitive) - r)` with r = 23 (float32).
- So factor → SlopeScaledDepthBias and units → DepthBias, as cited. A shader with factor ≠ units
  would be needed to measure it; none ships.

**What the D32_S8 target means.**
- DepthBias is in units of `2^(e − 23)`, where e is the exponent of the primitive's largest z
  (Microsoft, "Depth Bias").
- Unity passes the integer through unscaled, so the size of the offset differs from OpenGL's
  `glPolygonOffset` on D24 (GLCore, where 24-bit depth is D24_UNorm_S8_UInt).
- That pixel-level difference is part of what the original's D3D11 draws. A D3D11 device that
  passes the same integers matches it.

**Rasterizer, depth-stencil and blend state of the same draws:**

| Unity state | D3D11 state |
|---|---|
| `Cull Off` (0) | CullMode 1 (D3D11_CULL_NONE) |
| `Cull Front` (1) | 2 (D3D11_CULL_FRONT) |
| `Cull Back` (2) | 3 (D3D11_CULL_BACK) |
| every probe draw | FillMode 3 (SOLID), **FrontCounterClockwise 1**, DepthClipEnable 1, ScissorEnable 0, MultisampleEnable **1** (single-sample target), AntialiasedLineEnable 0 |
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
That agrees with part A's measurement that a clear to v stores 1 − v. In the colony frame
`ClearDepthStencilView` is always flags 3 (depth | stencil), depth **0**, stencil 0.

**The colony's other states** (j0008159, `report-colony.txt`):
- **Rasterizers.** Three kinds:
  - CULL_NONE with FrontCounterClockwise 1 (most world draws);
  - CULL_BACK with FrontCounterClockwise 1 (light buffer, blur, compositor and fog-of-war
    passes);
  - **CULL_NONE with FrontCounterClockwise 0**, used by the UI draws (the canvas shaders, vertex
    stride 76).
  - Why the UI's state has the other winding is **UNVERIFIED**; it does not change a CULL_NONE
    draw.
- **Stencil.** It is used: DepthStencil states with StencilEnable 1, ref 1 or 3, funcs EQUAL (3),
  LESS_EQUAL (4), NOT_EQUAL (6) and ALWAYS (8), pass ops KEEP (1), ZERO (2) and REPLACE (3), and
  read mask 0x1 with write mask 0 for the masked UI.
- **Blend states.** These appear:
  - One / Zero (off);
  - One + One with alpha op MAX (5);
  - SrcAlpha / One with mask 0x7;
  - One / InvSrcAlpha, including mask 0 (a colour-mask-0 pass);
  - SrcAlpha / InvSrcAlpha with mask 0xF or 0x7;
  - Zero / SrcColor (src 1, dst 3) with alpha Zero / SrcAlpha.
- **Samplers.** Five descriptions:
  - filters 0 (MIN_MAG_MIP_POINT), 0x14 (MIN_MAG_LINEAR_MIP_POINT) and 0x55 (ANISOTROPIC,
    MaxAnisotropy 4);
  - addressing WRAP (1) or CLAMP (3);
  - MipLODBias 0, MinLOD −FLT_MAX, MaxLOD FLT_MAX, ComparisonFunc 1 (NEVER), border 0.

## 3. Input layouts, buffers, constant buffers, views (measured, j0008159)

- **Shaders are the shipped DXBC.** 416 of the 420 vertex and pixel shaders the game created after
  the hook (the colony's) have the DXBC checksum of a shipped Direct3D 11 program of the game's
  data.
  - The other 4 (a vs of 852 and one of 1,064 bytes, a ps of 416 and one of 568 bytes) match no
    program in the scanned files. They are probably the player's own built-in shaders
    (**UNVERIFIED**).
  - No geometry shader was created in these frames.
- **Input layouts: always 14 elements**, in this order: POSITION0, NORMAL0, TANGENT0, COLOR0,
  TEXCOORD0–7, BLENDWEIGHTS0, BLENDINDICES0 (`BLENDWEIGHTS` with the S).
  - A channel the mesh has gets its real format, slot and offset, for example POSITION R32G32B32_FLOAT
    (6) at 0, NORMAL (6) at 12, TANGENT R32G32B32A32_FLOAT (2) at 24, COLOR R8G8B8A8_UNORM (28),
    TEXCOORD R32G32_FLOAT (16) or R32G32B32A32_FLOAT (2).
  - A channel the mesh lacks is **R8_UNORM (61), slot 0, offset 0**.
  - Some layouts put COLOR0 in slot 1 at offset 4; the rest are single-stream.
  - All elements are per-vertex (class 0, step 0).
  - The bytecode passed to `CreateInputLayout` is not the program but 88–284 bytes whose checksum
    matches no shipped program. It is probably a signature-only container Unity builds
    (**UNVERIFIED**).
  - 34 distinct layouts were created by the time of the colony.
- **Draws.**
  - Every draw is `DrawIndexed`, topology 4 (TRIANGLELIST), with a 16-bit index buffer
    (R16_UINT, 57).
  - Vertex strides seen: 20, 24, 32, 36, 48, 56, 76 (UI), 88 bytes.
  - `IASetVertexBuffers` and `IASetIndexBuffer` are called about once per draw (1,294 calls for
    1,296 draws).
- **Constant buffers, per stage.**
  - Each slot holds its own buffer of the cbuffer's exact size, DYNAMIC with CPU write, bound
    whole (`first` 0, `num` 4096), as part A found.
  - In the colony, the VS has b0 the material's buffer (48 to 4,848 bytes, e.g. BatchedAnimation
    368, TiledBlock 400, BackWallArray 4,848), then 144 or 176, 176 or 368, 368, 368.
  - The PS has b0 the material's, b1 144 or 176, b2–b4 368, **b5 128** (on every draw).
  - Per frame, b0 is mapped with `WRITE_DISCARD` before the draws that use it; b1–b5 are rebound
    rarely (VS/PS `SetConstantBuffers` 164 and 106 calls in the colony frame).
- **Shader resources and samplers.**
  - Bound per draw, and rebound often: `VSSetShaderResources` 5,407 and `PSSetShaderResources`
    3,123 calls for 1,296 draws.
  - The VS reads textures too (BatchedAnimation's build and frame data: R32G32B32A32_FLOAT
    textures such as 64×32, 16×16, 4×16).
  - Formats seen include A8_UNORM (65), BC1 (71), BC3 (77), R8G8B8A8 (28), R16G16B16A16_FLOAT
    (10) and R32G32_FLOAT (16), all TEXTURE2D views.
- **Targets.**
  - The world draws go to 1024×768 R8G8B8A8_UNORM with DSV D32_FLOAT_S8X24_UINT (20).
  - The light buffer goes to 1024×768 R16G16B16A16_FLOAT (10), with no depth.
  - Off-screen passes use 722×304, 512×384 (R16G16B16A16_FLOAT, Flow), 256×192 and 128×96
    (blur, bloom).
  - Viewports always cover the target, depth 0..1.
  - A scissor rectangle (0, 0, 1024, 768) stays set while ScissorEnable is 0.
  - The last draw of the frame has no DSV. Whether its target is the swap chain's buffer is
    **UNVERIFIED** (the RTV's resource was not compared with buffer 0).

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
- **UNVERIFIED:** what the original's player itself does on the AMD iGPU (it was run only on the
  RTX 3090).

## 5. The original's CPU frame cost on Direct3D 11 (measured, j0008159, RTX 3090)

**How it was counted.**
- One frame, `Present` to `Present`, with the helper's hooks counting each call.
- "Changes" counts a setting only when the object differs from the previous one set. Unity issues
  no repeated shader or input-layout settings itself: every such call was a change.
- Only the immediate context made calls in the recorded frames, and no `ExecuteCommandList` was
  called. So the original records nothing on deferred contexts; any deferred context would have
  to be executed on the immediate one.
- `CreateDeferredContext` was not called after the hook (the menu on). The engine log says
  `GfxDevice: creating device client; kGfxThreadingModeClientWorkerJobs`.

| Per frame | Main menu | Colony (2minnow) | Colony, temperature overlay |
|---|---|---|---|
| Time between the two Presents (hooks on) | 16.8 ms (vsync) | 37.9 ms | 45.5 ms |
| Draws (all `DrawIndexed`) | 41 | **1,296** | **1,494** |
| Distinct (VS, PS, layout, RS, BS, DS, topology) combinations | 12 | 48 | 54 |
| VS / PS `SetShader` (all changes) | 23 / 23 | 116 / 116 | 448 / 448 |
| `IASetInputLayout` (all changes) | 16 | 59 | 61 |
| `IASetVertexBuffers` / `IASetIndexBuffer` | 41 / 41 | 1,294 / 1,294 | 1,493 / 1,493 |
| `RSSetState` calls / changes | 1 / 1 | 35 / 12 | 40 / 14 |
| `OMSetBlendState` calls / changes | 17 / 17 | 74 / 47 | 288 / 258 |
| `OMSetDepthStencilState` calls / changes | 18 / 18 | 90 / 88 | 315 / 314 |
| `OMSetRenderTargets` | 8 | 43 | 48 |
| `RSSetViewports` / `RSSetScissorRects` | 11 / 2 | 77 / 7 | 84 / 8 |
| VS / PS `SetConstantBuffers` | 30 / 14 | 164 / 106 | 180 / 120 |
| VS / PS `SetShaderResources` | 22 / 82 | 5,407 / 3,123 | 7,521 / 5,924 |
| VS / PS `SetSamplers` | 4 / 59 | 3,682 / 2,818 | 5,170 / 5,286 |
| **Constant-buffer updates**: `Map(WRITE_DISCARD)` | 30 | **1,832** | **2,188** |
| Constant-buffer bytes mapped | 7,168 | 507,440 | 636,688 |
| Constant buffers mapped by size: ≤128 / ≤256 / ≤512 / ≤1K / larger | 18 / 1 / 8 / 3 / 0 | 34 / 890 / 904 / 3 / 1 | 39 / 915 / 1,221 / 6 / 1 |
| Other buffers: `Map(WRITE_DISCARD)` / `Map(WRITE_NO_OVERWRITE)` | 2 / 4 | 6 / 68 | 6 / 76 |
| Textures: `Map(WRITE)`, each with a `CopySubresourceRegion` | 0 | 298 | 268–269 |
| `UpdateSubresource` | 0 | 0 | 0 |
| Clears: RTV / DSV | 2 / 2 | 6 / 6 | 8 / 6 |
| Context calls hooked, in total | 529 | 24,519 | 35,780 |

**Notes for the threading design.**
- **Constant buffers.**
  - About 1.4 constant-buffer maps per draw, nearly all 129–512 bytes.
  - Always `WRITE_DISCARD` on a whole small buffer: never `NO_OVERWRITE`, never
    `UpdateSubresource`, never `*SetConstantBuffers1` offsets.
  - Both GPUs offer ConstantBufferOffsetting and MapNoOverwriteOnDynamicConstantBuffer, which
    would allow one large ring buffer instead.
- **Per-draw rebinding.** About 4 VS and 2.4 PS shader-resource settings and about 5 sampler
  settings per draw. The original does not filter redundant view and sampler bindings (the
  changes of these were not tracked).
- **Texture uploads.** Each frame copies from about 270–300 CPU-written (`Map(WRITE)`) textures
  into others, by `CopySubresourceRegion`. These are presumably staging uploads: the source's
  usage was not logged, **UNVERIFIED**.
- **Main-thread time** (`Time.unscaledDeltaTime`, 300 frames, hooks idle):
  - colony 43.1 ms mean;
  - temperature overlay 60.8 ms mean, with one 5 s frame from the overlay switch;
  - vsync off 44.1 ms.
  - The machine was shared with other agents' jobs: these are not performance figures
    (docs/PERFORMANCE.md has those). They show the frame is CPU-bound in the original at about
    23 fps in this colony, so the 16.7 ms vsync interval is never reached.
- **Not measured.** The split of that time between Unity's main thread, its render thread and the
  driver (**UNVERIFIED**; it needs a CPU profiler or ETW trace).

### The context's vtable (why the first recording counted no draws)

- The immediate context's vtable pointer points into the object itself (vtable = object + 8;
  `VirtualQuery`: PAGE_READWRITE, MEM_PRIVATE). That makes it a per-object copy, not the module's
  table.
- The runtime rewrites a group of its slots while the game runs: in the colony frame, 10 times
  each for the eight draw and dispatch slots, `UpdateSubresource`(1), `CopyResource`,
  `CopySubresourceRegion`, both clears, `GenerateMips` and `ResolveSubresource`.
- So j0008151's single patch kept every Set and Map hook but lost all draw hooks. The recorder
  now re-checks every hooked slot after each hooked call while it records, and forwards to
  whatever function the runtime last put there.
- **Remaining risk.** A draw directly after an unhooked call that rewrote the slots would be
  missed. The near equality of draws (1,296) and vertex-buffer settings (1,294) suggests none
  were. The draw counts are therefore lower bounds, exact as far as this check shows
  (**UNVERIFIED** beyond it).
- That Unity's draws reach D3D11 only through these slots is also **UNVERIFIED** (no NVAPI
  multi-draw was looked for).

## Tools added

- `tools/D3d11Native/` (`build.sh`, `build.cmd`):
  - `caps.exe`: adapters, feature levels, threading, options, format support;
  - `probe_native.dll` (`probe_native.c`, `ctx_hook.c`): the RenderDoc in-application API, the
    swap chain hook, the frame recorder and the state read-back event;
  - `frame-report.py`: names a recorded frame's shaders.
- `tools/UnityProbe/RdCapture.cs` (`PROBE_SCENARIO=rdcapture`).
- `ShaderSurvey states` (each pass's cull, ZTest, ZWrite and Offset) and `ShaderSurvey dxbc`
  (the shipped Direct3D 11 programs' checksums).

## UNVERIFIED

1. **RenderDoc.**
   - No RenderDoc capture was made: the one launch lost the isolation (see above).
   - Whether that launch read or wrote the user's Documents is unknown.
   - Why Doorstop did not run under RenderDoc is unknown.
2. **Depth-bias rounding.** Truncation or round-half-to-even of non-integer units.
3. **Factor and units fields.** That factor feeds SlopeScaledDepthBias and units DepthBias is
   cited (Unity's Offset page, Microsoft's Depth Bias page), not measured: no shader ships with
   factor ≠ units.
4. **The UI's winding.** Why the UI's rasterizer state has FrontCounterClockwise 0 while every
   other state has 1.
5. **The 4 unmatched shaders** and the input layouts' small signature bytecode: what they are.
6. **The last draw's target.** Whether the frame's last draw (no DSV) targets the swap chain's
   buffer.
7. **The frame-latency waitable.** Whether and when the game waits on it; the full-screen swap
   chain description.
8. **The original on the AMD iGPU.** Its swap chain and frame counts there.
9. **The texture uploads.** That the 270–300 `Map(WRITE)` textures per frame are staging
   textures.
10. **Missed draws.** Draws missed by the recorder after unhooked calls; draws by other routes
    (NVAPI).
11. **The CPU split.** Main thread, render thread and driver.
12. **The duplicate RTX 3090 adapter** in DXGI's enumeration.
