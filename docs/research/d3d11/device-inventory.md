# Render device inventory, for a planned Direct3D 11 device

Repo: the repo, master at 7b0f9e292. **The working tree has uncommitted changes** to
`Gl/Dxgi.cs` (adds `Dxgi.Adapter` and `Dxgi.Adapters()`, lines ~300-327), `Vulkan/VkContext.cs`
(adds `VkCandidate.Luid/VendorId/DeviceId` and the GPU-choice rule "auto never moves off a discrete GPU"),
`docs/ARCHITECTURE.md` and `RendererChoiceTests.cs`. Line numbers below are from that working tree.
All paths are relative to the repo root; `Desktop/` means `src/OniMoono.Desktop/`.

---

## 0. Headline facts for the plan

- **There is one device abstraction:** `RenderDevice` (an abstract class) plus `GraphicsCaps`. Both devices use the
  same main-thread side: `GlDrawRecorder`, `GlTextures.Prepare` and `GlMeshes.Prepare` record the work, and
  `GlWorker` hands frames to the device thread. **What differs per device** is the executor. For Vulkan that is
  `IResourceWork` plus a `RunDraw(ProgramInputs)` callback plus `StateCommand`s posted as `Action`s.
  - So a D3D11 device would be `D3d11Device : RenderDevice, IResourceWork`, like
    `VkDevice` (`Desktop/Vulkan/VkDevice.cs:80`).
- **Shader data:**
  - The Windows game data carries D3D11 programs: platform 4 (DXBC) next to GLCore (platform 15)
    (`docs/PLAN.md:202-207`). U60 also ships Vulkan programs (platform 18).
  - PLAN.md:205-207 notes that the D3D11 programs keep only the signature and code chunks, with **RDEF
    stripped**. Bindings would therefore have to come from Unity's per-program parameter tables, as the
    Vulkan device takes them from the platform-18 parameter blobs.
  - **No reader for platform 4 exists yet.** `ShaderNative.ProgramsFor` throws for any platform other than
    15, 14 and 18 (`src/OniMoono.UnityEngine/Engine/Rendering/ShaderNative.cs:155-175`). The pass's variant
    lists exist only for GLCore (`Variants`), Metal and Vulkan (`VulkanVariants`) (`ShaderNative.cs:66-77`).
    The parser to copy is `src/OniMoono.Assets/Shaders/VulkanProgram.cs` (100 lines; entry header magic
    0x0C0A75BA) with `ShaderParameters.cs` (228 lines).
- **`docs/research/unity-vs-moono.md:120` records an earlier decision:** "No Direct3D renderer, by choice". It
  notes that the Windows shaders carry D3D11 programs, so one could be added. The plan should supersede that
  line explicitly (and add a D-number, as D54 did for Vulkan, `docs/ARCHITECTURE.md:1514-1523`).
- **Conventions:** the shared code knows only `RenderConventions(bool Vulkan, bool Flipped)`
  (`Desktop/Gl/GlPrograms.cs:453-472`). Unity's D3D11 conventions are reversed Z and UV-starts-at-top, as
  Vulkan's are, but with a different clip-space y (no `gl_Position.y` negation epilogue in DXBC). The record
  will need a third mode or a rename (for example an enum). The current `Vulkan` flag drives:
  - `GpuProjection`;
  - `_ZBufferParams` (`GlPrograms.cs:886-892`);
  - `_ProjectionParams.x` (`:878-883`);
  - `oni_ClipY` (`:908-913`).
- **A D3D11 Windows build already exists in part:** `Direct3DClip` (`GlPrograms.cs:474-...`) gives the OpenGL
  device Direct3D's render-texture convention for `Klei/Stars` on Windows. A D3D11 device would get that
  behaviour natively, and `Direct3DClip` must not also apply to it.
- **Presentation:** the D3D11 swap chain machinery already exists (`Desktop/Gl/DxgiSwapChain.cs`, 529 lines),
  but each device presents through its own interop:
  - OpenGL through `WGL_NV_DX_interop2` (`DxgiTarget`);
  - Vulkan through an imported keyed-mutex texture (`VkDxgiTarget`).

  A D3D11 device could render into its own texture and `CopySubresourceRegion` it on the same device, or
  render to the back buffer directly. Either way it needs no interop, keyed mutex or `VkDxgiSupport` checks.
  `DxgiSwapChain.Open(luid, out reason)` (`:138`) makes its own `D3D11CreateDevice`, so it would have to accept
  an existing device or share one.

---

## 1. The device abstraction

### 1.1 `GraphicsCaps` (`src/OniMoono.UnityEngine/Engine/Rendering/RenderDevice.cs:10-28`)

These are all abstract. The Vulkan implementation is `VkCaps` (`Desktop/Vulkan/VkDevice.cs:15-71`) and the
OpenGL one is `GlCaps` (`Desktop/Gl/GlDevice.cs:16-66`).

| Member | RenderDevice.cs | VkCaps value |
|---|---|---|
| `DeviceType` | :12 | `GraphicsDeviceType.Vulkan` (:26). GL: `OpenGLCore` (GlDevice.cs:22). D3D11 would be `Direct3D11`. |
| `DeviceName / DeviceVendor / DeviceVersion` | :13-15 | From VkContext. Vendor mapped by PCI id (VkDevice.cs:154). Version = `vk.DriverInfo`. |
| `DeviceId / VendorId` | :16-17 | PCI ids. GL reports 0 for both (GlDevice.cs:26-27). |
| `MemorySizeMB` | :18 | The largest DEVICE_LOCAL heap (VkDevice.cs:167-171). |
| `ShaderLevel` | :19 | 50 |
| `MaxTextureSize / MaxRenderTargets` | :20-21 | `maxImageDimension2D`; min(8, maxColorAttachments) |
| `Supports2DArrayTextures / Supports3DTextures` | :22-23 | true / true |
| `SupportsInstancing` | :24 | true. No instanced drawing exists anywhere in the engine (no DrawMeshInstanced; checked by grep). |
| `SupportsComputeShaders` | :25 | false |
| `SupportsTexture(TextureFormat)` | :26 | Format sampled and (BC, or the engine can encode it). A format that needs CPU decoding = false (VkDevice.cs:41-56). |
| `SupportsRenderTexture(RenderTextureFormat)` | :27 | COLOR_ATTACHMENT feature on the mapped format (VkDevice.cs:58-70). |

### 1.2 `RenderDevice` (`RenderDevice.cs:55-227`): every member, with what the Vulkan device does

| Member | Line | Kind | VkDevice |
|---|---|---|---|
| `Caps` | :57 | abstract | VkDevice.cs:180 |
| `Supports(ShaderNative)` | :61 | virtual (default: has GLCore programs) | `VkPrograms.PlatformOf(shader) != 0` (VkDevice.cs:182). The route is chosen per shader. |
| `RendersFrames` | :65 | abstract | true (:181) |
| `FailedCommands` | :69 | virtual | Failed commands plus 1 if the device is lost (:198) |
| `CaptureTileShaders` | :74 | virtual (true) | **false** (:187). The shipped programs cannot take the CaptureTiles rewrite. Read by FrameRenderer.cs:616. |
| `ReadPixelsRgba8(...)` | :79 | virtual | Overridden (VkDevice.cs:476) |
| `BeginReadback(...)` | :99 | virtual (null) | `VkDevice.Readback.cs:17-90`, `VkReadback` :95-172 |
| `ReadPixels(...)` | :102 | abstract | VkDevice.cs:415-474, banded at about 1 M pixels |
| `ReleaseResource(object gpu)` | :105 | abstract | :508 (texture, mesh or program set, retired to the frame) |
| `PrepareAhead(TextureNative)` | :109 | virtual | **Not overridden** (GL only: GlDevice.cs:1227) |
| `PrepareAhead(MeshNative)` | :113 | virtual | **Not overridden** (GL: GlDevice.cs:1243) |
| `Applied(TextureNative)` | :117 | virtual | **Not overridden** (GL: :1255) |
| `AheadQueued()` | :120 | virtual | **Not overridden** (GL: :1248) |
| `Upload(TextureNative)` | :123 | virtual | `GlTextures.Prepare(worker, texture)` (:506) |
| `SetPass(Material,int)` | :127 | abstract | :323 (stores it). `RecordPass` :331-354 records through the recorder. |
| `WarmupShaders()` | :130 | abstract | :527-549. Builds every variant on the device thread, waiting. With `CheckPipelines` it also builds a pipeline per pass state. |
| `SetRenderTarget(in RenderTargetBinding)` | :133 | abstract | :279-302. Sets `recorder.Conventions = (Vulkan, Flipped: RT && !ScreenRows)` (:293). |
| `SetViewport(Rect)` / `SetScissor(Rect?)` | :136 / :140 | abstract | :304 / :306, posted as StateCommands. Executed in Execute.cs:178-218. |
| `Clear(bool,bool,Color,float)` | :142 | abstract | :308 → `ClearNow` Execute.cs:220 (depth cleared to 1 − value) |
| `SetCamera` (sealed) → `ApplyCamera(in CameraConstants)` | :148 / :158 | abstract | :311 |
| `Draw(in DrawRequest)` | :162 | abstract | `recorder.Draw` (:319) |
| `DrawQueue(List<DrawItem>)` | :167 | virtual | `recorder.DrawQueue` (:321), parallel recording |
| `TakeQueueCounters(int)` | :174 | virtual | `GlDrawRecorder.TakeCounters` (:645) |
| `DrawImmediate(...)` | :177 | abstract | :356-394. Builds a vertex array into the per-frame stream; `ExecuteImmediate` Execute.cs:593. |
| `DrawMeshNow(...)` | :181 | abstract | :396-413 |
| `Present()` | :184 | abstract | :618-639. Lost-device quit message; posts `PresentNow` (Execute.cs:759); Submit; vsync `GlDevice.WaitForQueuedFrames`. |
| `StopForQuit()` | :189 | virtual | :696-716. Worker and presenter stop, WaitIdle, pipeline cache saved, guard cleared. |
| `FlushRecorded()` | :193 | virtual | `worker.Submit()` (:551) |
| `HandOverLoadingFrames(KanimFrames)` | :197 | virtual | :592-616 |
| `CopiesTargetRects` / `CopyTargetRect(...)` | :200 / :205 | virtual | true / :557-587 (vkCmdCopyImage, rows turned over for ScreenRows) |
| `AnimatesLoading` | :209 | virtual (false) | true (:553) |
| `UploadInBackground(TextureNative)` | :214 | virtual (false) | **Not overridden.** Background uploads are GL only (`GlBackgroundUploads`, GlDevice.cs:1345). |
| `TakeGpuFrameMilliseconds()` | :218 | virtual | :661 (timestamp queries in VkFrames) |
| `TakeFrameCounters(int)` | :222 | virtual | :647-659 |
| `MemoryCounters()` | :226 | virtual | :669-692 (VMA budgets, DXGI process video memory, VideoMemoryLedger) |

Related types in the same file:
- `RenderTargetBinding` :230
- `CameraConstants` :237, with `ScreenTile/TileClip/VirtualProjection` for tiled captures
- `DrawRequest` :285
- `ImmediatePrimitive` :288 (Lines, LineStrip, Triangles, TriangleStrip, Quads)
- `ImmediateVertex` :297
- `NullRenderDevice` :307
- `GraphicsDevice` (Install/Adopt/Release, `QueueUpload`, `UploadPending` with the 64 MB `FlushRecorded`
  cadence, `ReleaseOrphans`) :334-462
- `DeviceReadback` :466-482 (Ready/Failed/Data/Size, Poll/Wait/Release)

### 1.3 The device-thread interface: `IResourceWork` (`Desktop/Gl/GlResourceWork.cs:9-24`)

| Method | VkDevice implementation |
|---|---|
| `CreateRenderTexture(DeviceTexture, RenderShape, string name)` | VkDevice.Resources.cs:160-208 |
| `UploadTexture(DeviceTexture, TexelUpload)` | :274 (→ `Upload` :297-384, `UploadChanged` :405-443) |
| `ApplySampler(DeviceTexture, GlTextures.SamplerSettings)` | :445-479 |
| `UploadMesh(DeviceMesh, MeshUpload)` | :481-590 (`CopyMesh` :494) |
| `DeviceProgram Program(ShaderNative, int blobIndex)` | VkPrograms.Get (VkPrograms.cs:202) |

Supporting types:
- **`ResourceCommand`** (GlResourceWork.cs:30-93): pooled commands, with a generation check against stale arena
  records.
- **`StateCommand<T>`** (:95-116).
- **The shared records, with a per-device slot:**
  - `DeviceTexture` (GlTextures.cs:14), whose `public Vulkan.VkTexture Vk` is at :78;
  - `DeviceMesh` (GlMeshes.cs:47), `Vk` at :69;
  - `DeviceProgram` (GlPrograms.cs:63), `Vk` at :83.

  A D3D11 device needs its own slot on each (or a generic `object Device`).
- **The record payloads:** `RenderShape` (GlTextures.cs:150), `TexelUpload` (:162), `MeshUpload` (GlMeshes.cs:120).

### 1.4 What the recorder hands the device

`GlDrawRecorder(GlWorker, Func<string,uint,DeviceTexture> defaultTexture, Action<ProgramInputs> runDraw)`
(GlDrawRecorder.cs:75-130, ctor at :124). Its device-specific hooks are:
- `PlatformOf` (:93), which picks the shader platform per shader;
- `VariantsOf` (:98), hard-coded to `VulkanVariants` when the platform is 18. **This needs a D3D11 branch.**
- `Conventions` (:87).
- `RenderTexture` (:89), the OpenGL device's flag for Direct3DClip.

**A draw** reaches the device as `ProgramInputs` (GlDrawRecorder.cs:18-56), with these fields:
- Program, Label;
- `ResolvedState` (blend, depth, stencil, cull, colour mask, offset, resolved from material properties);
- `Values` and `Blocks`, the loose floats and uniform-block bytes laid out in the GLCore programs' std140-like
  layout (`GlUniforms`);
- the `SamplerTexture[]` textures;
- `DeviceMesh` with `MeshLayout` and `SubMesh` (offset, count, GL mode, base vertex);
- `RunDraw`.

**Everything else** reaches the device as posted `Action`s in the frame (`GlWorker.Post`, GlWorker.cs:126). These
are viewport, scissor, clear, target, present, readbacks and copies. A `worker.Call` is a synchronous round trip
(:201-245).

### 1.5 The threading model

- **Main thread.** The game, `RenderDevice` calls, the `GlDrawRecorder`. Draw queues are recorded in parallel on
  `ForkJoin` helpers (GlDrawRecorder.cs:100-120; `ONI_PARALLEL_DRAWS=0` turns it off).
  `GlTextures.Prepare`/`GlMeshes.Prepare` copy changed data into the frame arena.
- **Device thread.** `GlWorker("Vulkan", enter, exit, CommandFailed)` (VkDevice.cs:138) runs each submitted
  frame's `Commands`:
  - It records Vulkan command buffers (`VkFrames`, two frames in flight) and submits in batches: at present,
    when the main thread needs a result, and on `FlushRecorded`.
  - Only one frame is handed over at a time (ARCHITECTURE.md:2469).
  - A command that throws calls `CommandFailed` (VkDevice.cs:202-231). It is logged once per kind, the open
    pass ends, and the frame goes on. A device loss is handled in §2.15.
- **Presenter thread.** `VkPresenter.Run` (VkPresenter.cs:330). It has three offscreen back buffers, at most two
  frames queued (one with vsync), and does the repeats, the splash and the loading animation. Both threads
  submit to the one graphics queue in order, so no semaphores are needed (ARCHITECTURE.md:3285-3287).
- **The main thread's vsync wait.** `GlDevice.WaitForQueuedFrames(presenter.Presented, frame, interval)`
  (VkDevice.cs:638), backed by `Desktop/Gl/QueuedFrames.cs` (189 lines, a Swappy-style on/off rule;
  ARCHITECTURE.md:2502-2521).

---

## 2. Feature checklist of the Vulkan device

Line counts per file: VkDevice.cs 790, VkDevice.Execute.cs 793, VkDevice.Resources.cs 649, VkDevice.Readback.cs
172, VkPresenter.cs 1063, VkPrograms.cs 593, VkContext.cs 561, VkFrames.cs 493, VkLoadAnimation.cs 442,
VkDxgiTarget.cs 362, VkGlsl.cs 343, VkPipelines.cs 337, VkDescriptorCache.cs 330, VkSplash.cs 253,
SpirvReflection.cs 249, VkFormats.cs 230, VkImages.cs 225, VkStartGuard.cs 145, VkTextureSet.cs 114,
VkDxgiSupport.cs 106, VkPipelineCacheFile.cs 87, VkShaderCompiler.cs 75, VkFault.cs 68, VkMesh.cs 62,
VkDrawAlternation.cs 50, VkDrawVerify.cs 48, VkFenceWait.cs 38, VkCheck.cs 13. **That is 8,691 in Vulkan/, plus
Renderers.cs at 111.** Outside Vulkan/ there are also `src/OniMoono.Assets/Shaders/VulkanProgram.cs` (100) and
`SmolV.cs` (249). Packages: `Ahwoo.BRUTAL.Vulkan`, `.Vulkan.Abstractions`, `.Vulkan.Vma` and `.ShaderC`
(`src/OniMoono.Desktop/*.csproj:17-21`).

The OpenGL equivalents are listed so the D3D11 device can be compared with both. ✓ = implemented on Vulkan.

### 2.1 Context and start-up
- ✓ **Instance, GPU choice and logical device** (`VkContext`, VkContext.cs:30-561, ctor :162, Create :177-320):
  - Loader check (`LoaderPresent` :62).
  - Requirements: Vulkan 1.3, dynamic rendering, synchronization2, swapchain extension, a graphics queue
    (`MissingRequirements` :381-399).
  - The GPU option (`Choose` :90-135; "default/discrete/integrated").
  - The Windows LUID (`LuidOf` :356). `UnlistedByVulkan` (:372) reports DXGI adapters Vulkan does not list.
  - Driver description (:323).
  - The `admit` callback for the deny list and start guard.
  - `VulkanUnavailableException` (:14).
- ✓ **The renderer choice** (`Desktop/Renderers.cs:10-110`): `auto|vulkan|opengl`. `TryVulkan` releases
  everything on any failure and opens a GL window (`RenderApi.OpenGl`) instead. **A D3D11 device must be added
  here, along with its fallback order.**
- ✓ **The start-up guard** (`VkStartGuard`, VkStartGuard.cs:22-117):
  - It writes `starting KEY` to `vulkan-start-guard.txt`.
  - It is cleared after 60 presented frames (`FramesToClear`) or a clean quit.
  - A leftover line becomes `crashed KEY`, and auto then starts on OpenGL.
  - Key = vendor/device ids + driver version + build (`KeyOf` :38). The build comes from `Renderers.BuildId`
    (Renderers.cs:100-110).
- ✓ **The deny list** (`VkDenyList`, VkStartGuard.cs:119-145): Intel Windows drivers below 101.5333 (decoded
  `major<<14|build`) and Mesa Dozen. Auto only.
- ✓ **Software device refusal** under auto (lavapipe; ARCHITECTURE.md:2198-2208).

### 2.2 Textures
- ✓ **Formats** (`VkFormats.Of`, VkFormats.cs:47-90):
  - Alpha8 (swizzle), R8, RG16, RGB24→RGBA8 (converted), RGBA32, ARGB32 (swizzle), BGRA32,
    ARGB4444/RGBA4444→RGBA8, RGB565, R16, RG32, RGB48→RGBA64, RGBA64, R/RG/RGBA Half, R/RG/RGBA Float.
  - BC1(DXT1)/BC3(DXT5)/BC4/BC5/BC6H/BC7 when the device samples BC. Otherwise CPU decode to RGBA8 (BC6H to
    half floats).
  - Conversions are in `VkFormats.Convert` (:117-210) and the `TexelConversion` enum (:11-27). The texel size
    table is at :221.
  - D3D11 gets BC natively (feature level 11). Swizzles have no D3D11 equivalent: ARGB32 and Alpha8 need a
    CPU-side swizzle or a different format.
- ✓ **Render texture colour formats** (`VkFormats.Color`, :92-114): RGBA8/SRGB/BGRA → RGBA8, RGBAHalf,
  RGBAFloat, RG32F, RG16F, R32F, R16F, R8, RG8, R16, RG16, RGBA16, 565, 4444, 5551, A2B10G10R10, B10G11R11. No
  integer formats.
- ✓ **Uploads** (VkDevice.Resources.cs:274-443):
  - Staging is in the host-memory staging stream (VkFrames.cs:336-366). `ONI_VULKAN_STAGING=device` stages
    in the per-frame stream instead.
  - Per-level and per-layer `Stage` (:386).
  - Mip skipping (`TexelUpload.Base/SliceStride`, the global mipmap limit).
  - 2D, 2D-array, 3D and cube views (default textures made per view type: `MakeDefault` :86-105).
- ✓ **Partial re-uploads** (`UploadChanged` :405-443): only the changed rows go into unchanged storage
  (`DeviceTexture.UploadedTexels`), as on GL.
- ✓ **Upload placement**: each batch has an upload command buffer submitted ahead of rendering. A texture the
  batch has already sampled is uploaded in-line, ending the pass (ARCHITECTURE.md:3253-3256; counted as
  "passes broken by uploads").
- ✗ **Background uploads** (`UploadInBackground`) and `PrepareAhead`/`Applied`/`AheadQueued`: GL only
  (`GlBackgroundUploads` 281 lines, `GlAhead` 98). Vulkan relies on batching instead. On D3D11 the immediate
  context is single-threaded, but a deferred context or `ID3D11Device` resource creation is free-threaded, so
  background uploads are possible. That is an option, not current scope.
- ✓ **Mipmaps**: `VkTextureWork.GenerateMips` (Resources.cs:17-49, blit chain). Before a render texture is
  sampled, `PrepareForSampling` (:230-259) resolves MSAA and regenerates mips. D3D11 has
  `GenerateMips`/`ResolveSubresource`.
- ✓ **Texture storage replacement** is safe: new storage is made before the old is retired (ARCHITECTURE.md:3240).
- ✓ **Samplers** (`ApplySampler` :445-479): wrap modes (:471), filter, anisotropy clamped to the device limit,
  LOD bias, and a sampler cache (`samplers` dictionary, destroyed in ReleaseAll :774).

### 2.3 Render textures
- ✓ **Colour plus optional depth/stencil** (CreateRenderTexture Resources.cs:160-208):
  - Depth bits >24 → D32S8; >16 → the device depth format (D24S8, else D32S8, VkDevice.cs:174); else D16.
  - Depth-only targets are D32F.
  - New surfaces are cleared: colour 0, depth at the far plane (`ClearColor/ClearDepth` :211-228).
- ✓ **MSAA**: a separate multisampled image resolved into the single-sample one (`vkCmdResolveImage`,
  Resources.cs:238-245). Multisampled depth is D24S8.
- ✓ **Multiple render targets**, up to 8 colours (`VkTargetFormats`, VkPipelines.cs:11-35).
- ✓ **Image layout and barrier tracking** (`VkImageResource.Use`, VkImages.cs:40-90). Not applicable to D3D11,
  but its replacement is SRV/RTV unbind hazards: a texture bound as both RTV and SRV is nulled by the runtime.
- ✓ **`CopyTargetRect`** (VkDevice.cs:557-587) is used by the loading screen's kanim strip.

### 2.4 Readbacks
- ✓ **`ReadPixels`** (VkDevice.cs:415-474) and **`ReadPixelsRgba8`** (:476-504):
  - Waits for the queue, in bands of about 1 M pixels (bytes path: 4 M).
  - `ReadBytesNow` (Execute.cs:640-757) turns rows over for the back buffer and ScreenRows targets.
  - An 8-bit fast path.
- ✓ **`AsyncGPUReadback`** (`BeginReadback`, Readback.cs:17-90; `VkReadback` :95-172):
  - Copies into a host buffer with the frame's work, then is ready when the frame slot recycles (two frames on).
  - Non-RGBA8 reads are converted (`FreeConverted` :160).
  - D3D11 needs staging textures plus `Map(D3D11_MAP_READ, DO_NOT_WAIT)` polling.
- ✓ **Back-buffer reads** read the current frame's offscreen back buffer (`VkPresenter.ReadBuffer`, :193).

### 2.5 Render state
- ✓ **Pipelines are keyed by** (program, `ResolvedState`, vertex layout, topology, target formats)
  (`VkPipelineKey` VkPipelines.cs:37-52; `Get` :81, `Create` :137-241).
  - Lookups: hashed once per draw, last key tried first.
  - Blend factors and ops (:262-290), compare (:292), stencil ops and faces (:250, :304).
  - Depth compares are swapped for reversed Z, depth offsets negated.
  - Cull: front faces flip per target (ARCHITECTURE.md:3166-3171).
  - A refused pipeline is remembered (`VkPipelines.Refused`).
- On D3D11 this maps to cached `ID3D11BlendState`/`DepthStencilState`/`RasterizerState` objects and input
  layouts. There is no pipeline cache file.

### 2.6 Programs, variants and uniform binding
- ✓ **The route per shader** (`VkPrograms.PlatformOf` :92, `Route` :108-147): shipped platform-18 if every pass
  with programs has U60-layout Vulkan variants; else GLCore compiled; else not drawn. It is logged once per
  shader (:100-102) and stored in `ShaderNative.DevicePlatform`.
- ✓ **Shipped programs** (`BuildShipped` :259-374):
  - SMOL-V decoded (`src/OniMoono.Assets/Shaders/SmolV.cs`).
  - Bindings from the parameter table (`stages<<24|set<<16|binding`; set 0 textures and samplers, set 1
    constant buffers).
  - `SpirvReflection` (249 lines) for inputs and blocks.
  - `CreateObjects` (:554), `Module` (:587).
- ✓ **Values**: `GlUniforms` writes into the constant buffers at the table's offsets. Matrices are four 16-byte
  columns and arrays use a 16-byte stride (ARCHITECTURE.md:3101-3104). **D3D11 cbuffers use HLSL packing.**
  Unity's D3D11 parameter tables give the offsets, but whether the GLCore-shaped `Blocks` bytes fit unchanged is
  unverified.
- ✓ **Variants not drawn** are logged once (`NotDrawn` :242): shadow-compare and MSAA textures, stereo, and
  stages other than vertex and fragment.
- ✓ **Failures**: a program that cannot be built draws nothing. An out-of-memory build is retried
  (`program-every:N` test fault).
- ✓ **Set 0 binding**: push descriptors with update templates (`VkTextureSet` 114), or the
  `VkDescriptorCache` (330) for programs with more than 32 bindings. Set 1 uses dynamic uniform offsets into a
  4.2 MB uniform stream (`UniformSet` :149, `VkFrames.UniformStream` :394). D3D11 equivalents are
  `PSSetShaderResources`/`PSSetSamplers`/`*SetConstantBuffers1` with offsets (11.1) or `Map(WRITE_DISCARD)`.
- ✓ **Ready-texture stamps** (`VkReadyStamps`, VkImages.cs:172-200).
- ✓ **`WarmupShaders` / `--check-shaders`** build every variant and pipeline (VkDevice.cs:527-549,
  `CheckFailures` :523).

### 2.7 Mods' shaders without shipped programs (GLCore→SPIR-V)
- ✓ `VkGlsl.Translate` (VkGlsl.cs:83) rewrites GLCore text into a Vulkan layout. `VkShaderCompiler` (75 lines,
  shaderc) compiles it. `BuildCompiled` is at VkPrograms.cs:400-484.
- ✓ An OpenGL-clip epilogue (`UniformBinding.OpenGlClip`, GlPrograms.cs:47): depth to reversed, y via
  `oni_ClipY` (`UniformSource.ClipY` :908-913). Known disagreements are at ARCHITECTURE.md:3135-3146.
- ✓ `ONI_VULKAN_ROUTE=glcore` sends every shader this way (diagnostic, and the QA scenario `colony-glcore`).
- **For D3D11:** a mod bundle may lack D3D11 programs (docs/research/mod-bundle-shaders.md:12). A GLSL→HLSL or
  GLSL→SPIR-V→DXBC path (for example via SPIRV-Cross + D3DCompile) would be the analogue. Nothing like that
  exists today.

### 2.8 Meshes and dynamic vertex buffers
- ✓ `UploadMesh/CopyMesh` (Resources.cs:481-590), `VkMesh` (62 lines):
  - One buffer holds vertices and uint32 indices (Execute.cs:283).
  - The CPU never writes a buffer that recorded draws read: up to two spare buffers, growth by an eighth once
    a mesh changes.
  - On discrete GPUs, buffers are device memory staged by copy. Integrated GPUs map them
    (`ONI_VULKAN_MESHES=staged|mapped`).
  - Old buffers are retired, not destroyed (`RetireMeshBuffer` :618).
- ✓ **Absent channels** read GL's constant attributes from a stride-0 constants buffer (`ConstantOffset`,
  Execute.cs:290). On D3D11 use a constants vertex buffer (stride 0 is legal in D3D11 IASetVertexBuffers).
- ✓ **Vertex bindings**: one bind call, binding i feeds input i (`BindVertexBuffers` Execute.cs:300).
  Topologies are at Execute.cs:308 (quads are converted for immediate mode).
- ✓ **Immediate geometry** goes through the per-frame host-visible stream (VkFrames.cs:324, 420).
- **Instancing:** none in the engine (see 1.1).

### 2.9 Execution, frames and memory
- ✓ **Dynamic-rendering passes**: begun lazily, ended on a target change, copy or frame end
  (`BeginRendering/EndRendering`, Execute.cs:47-165).
- ✓ **`VkFrames`** (493 lines):
  - Two frame slots, each with a command pool, fence, streams, descriptor pools and a retire list (`Retire`).
  - Batched submits (`Submit` :221).
  - Chunk recycling (8.4 MB chunks, the first two kept).
  - Timestamp queries (`CollectTimestamps` :292).
- ✓ **`VkFenceWait`** (38 lines): 100 ms slices, with the `LongWaits` log lines after 10 s
  (ARCHITECTURE.md:2537-2547).
- ✓ **VMA memory**: the first buffer of each kind logs its memory type. `VideoMemoryLedger` keeps accounting by
  kind.

### 2.10 The queued-frames wait and frame pacing
- ✓ The shared `QueuedFrames` (Gl/QueuedFrames.cs), called through `GlDevice.WaitForQueuedFrames`
  (VkDevice.cs:638).
- ✓ Presenter queue limit: one with vsync, else `maxQueuedFrames` (at most 2) (VkPresenter.cs:41-43).
- ✓ `vSyncCount` 2-4 under FIFO presents each frame N times (ARCHITECTURE.md:3290-3292). `--vsync-count`
  overrides it (`SwapIntervalOverride`, VkDevice.cs:83).
- ✓ A minimized window keeps its size and paces itself (`PaceMinimized` :580, `MinimizedWait` :594; issue #29).
- `FramePace`/`FramePacing` (frame deltas rounded to refreshes) are engine-side and shared. The refresh comes
  from `DxgiSwapChain` (ARCHITECTURE.md:2567-2571).

### 2.11 Presentation
- ✓ **Its own swapchain** (`ShowSwapchain` VkPresenter.cs:603-685):
  - `Recreate/TryRecreate` (:869-997), with retries from 0.1 s doubling to 2 s, giving up after five failures
    in ten seconds.
  - `ModeFor` (:855): FIFO with vsync, IMMEDIATE or else MAILBOX without.
  - BGRA8/RGBA8 UNORM.
- ✓ **DXGI on Windows** (`ShowDxgi` :687-757, `VkDxgiTarget` 362 lines, `VkDxgiSupport` 106 lines):
  - The shared NT-handle texture with keyed mutex is imported (`Import` :190), keys 0/1, with external
    queue-family barriers (:824).
  - `HandoverReport` (:759) on a timeout.
  - `RestartDxgi` (:793) and `DxgiRestarts` (Gl/DxgiRestarts.cs): Vulkan's swapchain stands in while DXGI
    restarts.
  - The interop checks come first (`Importable` :123). Options: `--dxgi on|off`, the Tweaks "dxgi" row,
    `DxgiSwapChain.Enabled` (Gl/DxgiSwapChain.cs:27), and `--dxgi-fault` (`DxgiSwapChain.Fault` :43-66).
- **For D3D11:** the D3D11 swap chain is already in `DxgiSwapChain`: flip discard, two buffers, waitable object
  at latency 2, tearing without vsync, `Resize`, `Present`/`PresentCopied`, `PresentRepeat`,
  `LastPictureRetired`, `CopyToBackBuffer`, `DeviceState`/`GetDeviceRemovedReason`, HoldBack (:68-500).
  - A D3D11 device should share its device and context, not make its own (`Open` :138). Keyed mutex and
    interop code would not apply.
  - The "own swapchain" fallback (Vulkan's `--dxgi off`) has no D3D11 meaning. `--dxgi off` on D3D11 would be
    a no-op or a refusal.
- ✓ **Damaged contents / refresh**: `platform.ContentsDamaged += presenter.Refresh` (VkDevice.cs:135).
  `LoadingScreen.Changed = presenter.Poke` (:136).

### 2.12 DXGI repeats (StallRepeats)
- ✓ `VkPresenter.Repeat` (:457) and `VkDxgiTarget.ShowRepeat` (:342) re-present the last picture while no frame
  comes. With DXGI, D3D11 copies the shared texture again with no Vulkan work (ARCHITECTURE.md:2824-2898,
  3029-3037 region).
  - The policy is the shared `StallRepeats` (Gl/StallRepeats.cs, 98 lines). Options: the Tweaks "stall-repeats"
    row, `--stall-repeats`, `DxgiSwapChain.Repeats` (:31).
  - On D3D11 a repeat is `PresentRepeat` on the same device.

### 2.13 The loading splash and the loading-screen animation
- ✓ **The splash** (`VkSplash` 253 lines) uses its own pipeline per format (:141) and an art upload (:80). It is
  drawn over the window on the presenter thread. Art and comets are shared (`SplashRain`, `GlDevice.Splash`
  static, VkDevice.cs:126). Options: `--splash moo|none`.
- ✓ **The loading animation** (`VkLoadAnimation` 442 lines):
  - The presenter copies the last frame, blits the due kanim tile from the strip, and clears the bar rects.
  - Arming and pacing are shared with `GlLoadAnimation` (`Wanted`, `WatchdogDue`, `Advance`).
  - The strip handover is `VkDevice.HandOverLoadingFrames` (:592). The engine side is `LoadingScreen.cs:114,
    140` (gated on `AnimatesLoading`, `CopiesTargetRects`).
  - A verification replay (`VerifyQueued/VerifyCompare` :292-425, under `ONI_TWEAK_VERIFY`; see the history
    report).

### 2.14 Screenshots and capture
- Captures go through `ReadPixels`/`ReadPixelsRgba8` and `FailedCommands` (a capture that saw a failure writes
  nothing).
- `CaptureTileShaders => false` on Vulkan changes how super-sized captures tile (FrameRenderer.cs:616;
  ARCHITECTURE.md:3352-3393). docs/PARITY.md:196 notes that on Vulkan oversized captures keep seams, as in the
  original.
- `ONI_CAPTURE_TWICE=full-uploads|full-draws` is a verify tool.

### 2.15 Device loss
- `IsDeviceLost` (VkDevice.cs:189).
- `CommandFailed` sets `deviceLost`; no more work is recorded.
- `PresenterFailed` (:241) covers presenting that failed for good.
- `Present` (:620-626) logs the error naming Moono Tweaks' Renderer row and `--renderer opengl`, then
  `PlayerLoop.RequestQuit(1)`.
- No guard mark is left for a later failure.
- On D3D11: `DXGI_ERROR_DEVICE_REMOVED/RESET/HUNG` (`Dxgi.DeviceLost`, Gl/Dxgi.cs:246) and
  `GetDeviceRemovedReason` (:234).

### 2.16 The pipeline cache
- `vulkan-pipelines.bin` with its own header, length and SHA-256. It is checked against vendor, device and
  `pipelineCacheUUID`, and written atomically at shutdown (`VkPipelineCacheFile` 87 lines; VkContext.cs:463-539).
  `ONI_VK_PIPELINE_CACHE=0` skips it.
- On D3D11 there is no driver pipeline cache API; DXBC is loaded as is. Not needed.

### 2.17 Diagnostics and log lines
Log lines:
- `renderer: Vulkan (choice, source)` / `renderer: OpenGL (reason)` (Renderers.cs:35, 40)
- `vulkan: device ready (name, depth, swapchain)` (VkDevice.cs:146)
- `vulkan: shader 'X': ...` route lines (VkPrograms.cs:100-102)
- `vulkan: driver ID … conformance …; VK_KHR_external_memory_win32 yes|no …` (VkContext.cs:~310-315)
- `present:` lines (DXGI)
- `vulkan: the device was lost` (VkDevice.cs:209)
- `vulkan: presenting failed` (:246)
- start-guard lines (:271)
- `vulkan draws:` (VkDrawVerify)

Counters: `--frame-stats`, through `TakeFrameCounters` (VkDevice.cs:647-659) and `VkTimings` (:776-790):
- draws, pipeline binds, render passes, passes broken by uploads, submits;
- programs built, pipelines made;
- texture uploads, unchanged uploads, staging waits;
- descriptor sets made, found, kept;
- `PresenterTimings`.

GPU time comes from timestamp queries (`TakeGpuFrameMilliseconds`). `MemoryCounters` reports VMA budgets and the
process's video memory via DXGI (`Dxgi.ProcessVideoMemory`, Gl/Dxgi.cs:329).

Measurement and verify tools:
- `ONI_VK_ALT` (VkDrawAlternation, A/B windows)
- `ONI_VERIFY_VK_DRAWS=1` (VkDrawVerify; fails `qa-tools.py verify`)
- `ONI_RENDER_TIMING`
- `ONI_VERIFY_PARALLEL_DRAWS`
- `ONI_VERIFY_RAW_LOADS`

### 2.18 Test hooks
- **`ONI_VULKAN_FAIL`** (`VkFault`, VkFault.cs:1-68; `VkContext.FailAt` :150):
  - `context`, `device`, `presenter`, `presenter-create`
  - `present-after:N`, `present-oom-after:N`
  - `alloc-every:N`, `pipeline-every:N`, `program-every:N`
  - `crash-at:N` (VkDevice.cs:255-261 exits with 70)
  - `guard-build:X`
- **`--dxgi-fault`** (DxgiSwapChain.cs:33-66):
  - shared: adapter, device, swapchain, association, occupied, lock:N, resize:N, removed:N, norestart;
  - Vulkan only: import, importview:N.
- **`ONI_TEST_STALL_FENCE=1`.**

### 2.19 Validation layer
- `--vulkan-validation` / `ONI_VULKAN_VALIDATION=1|sync|info`.
- A local layer through `VK_ADD_LAYER_PATH` (`AddLocalValidationLayer` VkContext.cs:405, `LayerAvailable` :429).
- `scripts/get-vulkan-validation.sh` (pinned 1.4.363.0, SHA-256), staged by `scripts/run-windows.sh`.
- The D3D11 analogue is the `D3D11_CREATE_DEVICE_DEBUG` + `ID3D11InfoQueue` (SDK layers, from the Windows
  "Graphics Tools" optional feature). That is an analogy; nothing in the repo does it.

### 2.20 Shutdown
- `StopForQuit` (VkDevice.cs:696) and `Shutdown` on window close (:718).
- `ReleaseAll` (:741-773) also runs after a failed start, from whatever was made by then.

### 2.21 Shared, device-neutral code a new device reuses as is
`GlDrawRecorder`, `GlUniforms`/`GlPrograms` value resolution, `GlTextures.Prepare`, `GlMeshes.Prepare`,
`GlWorker`/`FrameArena`, `QueuedFrames`, `StallRepeats`, `PresentTrace`, `PresenterTimings`, `SplashRain`,
`GlLoadAnimation` (arming and pacing), `DxgiSwapChain`, `DxgiRestarts`, `DxgiWindowReport`,
`VideoMemoryLedger`, `UploadBuffers`, `DefaultTextures`, `GlDevice.StereoOnly`, `GlDevice.Splash`.

Despite the `Gl` prefix, these are shared. Some are named after GL but used by Vulkan
(`GlDevice.WaitForQueuedFrames`, `GlDeviceStoppedException` says "OpenGL device", GlWorker.cs:347).

---

## 3. Places outside Gl/ and Vulkan/ that are device- or API-specific

Gathered by a grep sweep (a sub-agent). I spot-checked `Options.cs:486-492`, `ShaderProgramTable.cs:9-14`,
the absence of `usesReversedZBuffer` and `graphicsUVStartsAtTop`, and `Desktop/Metal/`. All of them held up.
Line numbers inside Gl/ in this section come from my own reads.

### 3.1 SystemInfo and capabilities
- `src/OniMoono.UnityEngine/Core/SystemInfo.cs:22-27`: `graphicsDeviceType`, `Name`, `Vendor`, `Version`,
  `ID` and `VendorID` all come from `GraphicsDevice.Caps`.
- `SystemInfo.cs:38,42`: `supportsAsyncGPUReadback` and `copyTextureSupport` branch only on Null.
- `SystemInfo.cs:47-57`: the default formats copy the original's GLCore player. Whether D3D11's values differ
  is unverified; the original on Windows is D3D11, so measure it with UnityProbe.
- **`SystemInfo.usesReversedZBuffer`, `graphicsUVStartsAtTop` and `GL.GetGPUProjectionMatrix` are not
  implemented anywhere** (grep: no hits). On Vulkan the game already sees GLCore-like answers for them. Check
  whether the game or any mod reads them before deciding what D3D11 reports.
- `FrameTiming.cs:28` is a comment about gpuFrameTime on OpenGL. `tools/UnityProbe/TextureFallback.cs:127` logs
  graphicsDeviceType.
- Consumers of the device capability flags:
  - `LoadingScreen.cs:114,140,144`: `AnimatesLoading`, `CopiesTargetRects`, `ScreenRows`.
  - `FrameRenderer.cs:616`: `CaptureTileShaders`.

### 3.2 Unity conventions
- The logic lives in `Desktop/Gl/GlPrograms.cs`:
  - `RenderConventions` at :453-472.
  - `Direct3DClip` at :474-~519. This is the D3D11 render-texture y flip for `Klei/Stars`, Windows only.
  - `AnimationLight` after it. It reproduces D3D11's light-buffer read, issue #76.
  - `UniformSource.ProjectionParams` / `ZBufferParams` / `ClipY` at :878-913.
- **A D3D11 device must not get the GL emulations.** `Direct3DClip` and `AnimationLight` imitate on GL what
  D3D11 does natively, so leaving them on would apply the effect twice.
- `TextureNatives.cs:513-516`: `RenderTextureNative.ScreenRows` (on Vulkan stored top first). `:129`: readback
  is per frame on Vulkan and by background readers on OpenGL.
- `FrameRenderer.cs:452,509-511,1109-1110`: the capture clip matrix. `:840-847`: the Vulkan draw shortcuts are
  turned off during a capture (`VkDrawAlternation`).
- GL-convention matrices: `Graphics.cs:159-176` (`GL.LoadOrtho`, `LoadPixelMatrix`, bottom-left pixels),
  `Camera.cs:126`. The `RenderDevice.cs:144` contract is "Unity convention; the device applies its clip-space
  rules".
- Bottom-left contracts: `RenderDevice.cs:77` (ReadPixelsRgba8, bottom row first) and `:138` (scissor).
- `SplashArt.cs:23` and `Desktop/SplashRain.cs:132,175`: v-up atlas coordinates and bottom-left window
  coordinates, shared by both splashes.
- Comments: `AsyncGPUReadback.cs:14`, `LongWaits.cs:40`, `FramePacing.cs:17,20`.

### 3.3 Shader platform selection
- `src/OniMoono.Assets/Shaders/ShaderProgramTable.cs`:
  - :9-14 `ShaderCompilerPlatform { Metal=14, OpenGLCore=15, Vulkan=18 }`, with **no D3D11 (4)**.
  - :18-33 `GpuProgramTypes`: GLCore is 6/7/8, Metal 23/24, SPIR-V 25. `PlatformOf()` returns 0 for any other
    type, so D3D11 program types are ignored today.
- `ShaderNative.cs`:
  - :66-77 the per-pass variant lists.
  - :101-107 the platform constants.
  - :111-115 `DevicePlatform`.
  - :151-175 `HasProgramsFor`/`ProgramsFor`, which throw for any platform other than 15, 14 or 18.
- `ShaderPrograms.cs:26-36, 63, 71, 89, 133-148, 198-236, 280-286`: per-platform formats, loading, the GLCore
  filter (`IsGlCore`) and stage selection.
- `GlDrawRecorder.cs:93-99`: `PlatformOf` and `VariantsOf`, hard-wired to Vulkan's variant list.
- Host:
  - `BindCheck.cs:71`: GLCore only.
  - `ShaderCheck.cs:10-12,27-28,45-50`: `--check-shaders` branches on `device is GlDevice` versus Vulkan
    (`VkDevice.CheckPipelines` / `CheckFailures`). **This needs a D3D11 branch.**
  - `ShaderDump.cs:11`: GLCore only.
- Tools: `tools/ShaderSurvey` (`Program.cs:8,17,41`, `VulkanSurvey.cs`, `SpirvValidator.cs`, `MetalCompare.cs`).
  A D3D11 survey mode (decode all platform-4 programs; a DXBC container check) would mirror `ShaderSurvey vulkan`.
- `src/OniMoono.Desktop/Metal/MetalConstantBuffers.cs` exists. Metal programs (14) are read too, as research or
  a partial device. It is a precedent for a third program reader.

### 3.4 Device selection and windowing
- `Desktop/Renderers.cs:15-17` (choices), `:25-50` (`Open`), `:52-97` (`TryVulkan`), `:100-108` (`BuildId`).
- `Desktop/GlfwPlatform.cs`:
  - :26-37 `WindowSettings.Api` and `enum RenderApi { OpenGl, Vulkan }`. D3D11 would be a no-API GLFW window,
    like Vulkan's.
  - :32-33, 76-78 `ContextFreeWindow` (DXGI with GL).
  - :103 re-init after a failed Vulkan start.
  - :125-140 per-API GLFW hints.
  - :208-226 preloads d3d11.dll and dxgi.dll (GL on Windows; this ordering matters for RTSS, ARCHITECTURE.md
    DXGI section).
  - :417-426 the hidden GL share window.
  - :961 the GL-only dispose.
- `src/OniMoono.Host/Player.cs`:
  - :27-43 static device configuration: `GlDevice.Splash`, `DebugOutput`, `SwapIntervalOverride`;
    `VkDevice.SwapIntervalOverride`; `VkContext.Validation`; `GlGpuProfile`; `GlBindless`;
    `DxgiSwapChain.Enabled` / `Fault` / `Repeats`.
  - :44-45 `OpenDevice` and `GraphicsDevice.Install`.
  - :161-167 `Renderers.Open(...)`.
  - :269 `ContextFreeWindow` when DXGI is on.
- `Host/GpuChoice.cs:11,94` and `src/OniMoono.NativeHost/host.c:7`: the GPU-choice text and the
  NvOptimus/AMD exports are GL-oriented. A D3D11 device would choose its adapter by LUID instead (VkContext
  does this in the uncommitted working tree).
- `Host/NativeCheck.cs:35-36`: the Vulkan loader is treated as a system library. d3d11.dll and dxgi.dll are
  already loaded by DXGI presentation.

### 3.5 The Moono Tweaks renderer row and translations
- `src/OniMoono.Host/GameTweaks/GameTweakNames.cs`:
  - :42 the `--renderer auto|vulkan|opengl` hint.
  - :43 `--dxgi`.
  - :57 `RendererRow` and `DxgiRow`.
  - :127 the GPU row text, which mentions OpenGL.
  - :130-133 the Renderer row's `Choices`.
  - :134-141 the Dxgi and StallRepeats rows ("Direct3D 11 swap chain").
- `Host/MoonoTweaksMenu.cs:53-55,80-95`: the row to and from `EffectiveOptions`.
- `Host/GameTweaks/TweakStrings.cs:66-67`: the choice key is `STRINGS.MOONO.TWEAKS.CHOICES.RENDERER.<CHOICE>`.
- Translations: `translations/moono.pot:248-289` and `translations/zh.po:617-649`. A new choice such as
  `D3D11` needs pot and po entries.
- `src/OniMoono.GameTweaks/MoonoTweaksMenu.cs`: the generic UI, which names no renderer.

### 3.6 Settings
- `src/OniMoono.Host/PlayerSettings.cs`:
  - :22-23 the documentation example.
  - :37-42 the fields.
  - :117-123 validation through `Renderers.IsChoice`.
  - :148-149 serialisation.
  - :196-206 the defaults (renderer auto, dxgi true).
  - :244, 264-267 the precedence.
  - :282-301 `Apply`.
  - :333-341 `Describe` / `RendererFrom`.
- `Host/Program.cs:68,116-119`: the effective renderer and dxgi.

### 3.7 Command line and environment variables
- `src/OniMoono.Host/Options.cs`:
  - :83-90 `Renderer`, `RendererSource`, `VulkanValidation`.
  - :91-102 `Dxgi`, `StallRepeats`, `DxgiFault`.
  - :317-325 `--renderer` and `--vulkan-validation`.
  - :326-334 `--dxgi`.
  - :356-360 `--dxgi-fault`.
  - :277 `--gl-debug`; :298-301 `--gpu-profile`, `--no-bindless`, `--bindless` (GL only).
  - :486-492: the Unity `-force-*` options map to the renderer, and **`-force-d3d11` is ignored explicitly**.
- `Host/UnityArguments.cs:25-31`: `-force-glcore`/`-force-opengl` mean opengl, `-force-vulkan` means vulkan.
  `-force-d3d11` would map here.
- `Host/DxgiFaultOption.cs:7-12`: the fault names (Vulkan-only `import` and `importview`).
- Environment variables named outside the device folders: `ONI_VULKAN_FAIL` (Renderers.cs:101),
  `ONI_VULKAN_VALIDATION` (Options.cs:88), `ONI_RENDER_TIMING`, `ONI_VULKAN_ROUTE` (qa scenario) and
  `ONI_GLSL_DUMP` (tests). There are no `ONI_GL*`, `ONI_RENDERER` or `ONI_D3D*` variables.

### 3.8 Crash reporting and logs
- `Host/Player.cs:47-49`: `NativeCrashes.Describe("gpu", device.Caps.DeviceName)`.
- NativeHost:
  - `crash.c:597-608`: the gpu field is documented as GL_RENDERER.
  - `crash.c:1080-1154`: the "NVIDIA OpenGL Driver" event-log read (nvoglv64).
  - `crash_dialog.h:19`, `crash_window.h:28`: the gpu field.
  - `crash_dialog.cpp:42,108,271-310`: `gpu_line()` matches the renderer string against the DXGI adapters.
  - `crash_window_linux.c:911-916`.
  - The sample crashes in `crash_window.c:1022` and `scripts/check-native-crash.sh:293`.

  On D3D11, look for D3D11/DXGI driver DLLs (nvwgf2umx, amdxx64, igd10iumd64) in addition to nvoglv64. That is
  a suggestion, not existing code.
- Log lines: `renderer:` (Renderers.cs:35,40), and `PlayerSettings.Describe` (:333-334) logs the choice and its
  source.

### 3.9 Launcher
- MoonoLoader, Restarter, Host/Launch and Host/Install name no renderer.
- Restart passes the arguments through (`RestartCommandTests.cs:65-66,91-92,121`).

### 3.10 Scripts, QA and CI
- `scripts/qa.sh`:
  - :174 the scenarios.
  - :189-192, 227-228 a `VULKAN=1` scenario is skipped when the log says `renderer: OpenGL (Vulkan unavailable`.
  - :266 one REQUIRE line per renderer.
  - :362 `QA_EXTRA_ARGS`.
  - :454-457 verify skips partial uploads on Vulkan and "vulkan draws" on OpenGL.
- `scripts/qa-tools.py:233` ("vulkan draws").
- `scripts/qa-scenarios/colony-glcore.sh:1-9`; `resize.sh:9-10` (alternative lines for GL and Vulkan).
- `scripts/e2e.py`:
  - :8-10, 5761-5783 `player-args`.
  - :3436-3437 `E2E_ORIGINAL_ARGS`.
  - :3477-3480 `E2E_PLAYER_ARGS`.
  - :87, 1057, 3410, 3707-3710 Mesa d3d12.
- **`scripts/e2e-windows/run.py:6-9,60,542`: `WIN_RENDERER` defaults to vulkan.** The original runs on D3D11.
- Other scripts:
  - `scripts/e2e-bench.py:13-14` (the `vk+arg=-force-vulkan` variant).
  - `scripts/get-vulkan-validation.sh`, `scripts/run-windows.sh:30-32`.
  - `scripts/check.sh:81` (Metal versus GLCore checks).
  - `scripts/check-windows-host.py:30` (dxgi.dll in the loaded-later list).
- `tools/HdrCheck/hdr-check.sh:4-13,30,71-78`: a matrix of ours on vulkan or opengl × dxgi or its own
  presentation. D3D11 would be a new row.
- **CI** never names a renderer:
  - `.github/actions/game-tests/action.yml:43` and `perf-spread.yml:147` install only Mesa GL.
  - `ci/game-tests/push.txt` runs `--headless`.
  - A D3D11 device would get no CI coverage (CI is Linux), so all of its testing is on the Windows lanes.

---

## 4. Tests per device (counts = `[Fact]` + `[Theory]` attributes per file)

### 4.1 Vulkan unit tests (`tests/OniMoono.UnityEngine.Tests/`)
| File | Tests | What it covers |
|---|---|---|
| `VkStartGuardTests.cs` | 5 | key, never-cleared start keeps auto on GL, guard file, Intel version decode, deny list |
| `VkTextureSetTests.cs` | 8 | descriptor set writes and identities, 60-frame sweep, templates |
| `VkGlslTests.cs` | 4 | GLCore→Vulkan rewrite bindings and locations, clip factor, stereo refused, dumped programs compile |
| `VkRobustnessTests.cs` | 2 | self-referencing SPIR-V array refused; fault settings parse |
| `VkReadyStampsTests.cs` | 4 | ready-texture stamps |
| `VkPipelineCacheFileTests.cs` | 5 | header, hash, foreign or damaged files |
| `VkMeshSpareTests.cs` | 3 | spare mesh buffers |
| `VkFormatsTests.cs` | 4 | incl. SwizzlesMatchOpenGLs, ReadbacksAreOpenGLsFloats |
| `VkDescriptorCacheTests.cs` | 5 | the descriptor cache |

Assets: `tests/OniMoono.Assets.Tests/VulkanProgramTests.cs` (5, platform-18 entry parsing and damage) and
`ShaderDataTests.cs` (9; `PlatformOf` at :133-135, EveryVulkanProgramDecodes).

### 4.2 Shared or both-device unit tests
- `RendererChoiceTests.cs` (6, more uncommitted): settings and command line choose it, GPU option picks by type,
  Linux default takes the driver's order, auto refuses software and devices missing requirements, auto keeps an
  old discrete GPU on GL, GPUs without a Vulkan driver are named.
- `DxgiPresentTests.cs` (20): adapter matching, Vulkan presents through DXGI only when the import works,
  memory type choice, every fault accepted, and more.
- `UniformRecordingTests.cs` (4): `CompiledGlCoreProgramsReadOpenGlsProjection` exercises
  `RenderConventions(Vulkan: true, ...)` (:114-142).
- `QueuedFramesTests.cs` (6), `StallRepeatsTests.cs` (7), `PresentChoiceTests.cs` (2),
  `LoadAnimationGateTests.cs` (5), `LoadProgressTests.cs` (8), `ParallelDrawRecordingTests.cs` (4),
  `ResourceCommandTests.cs` (2), `FrameRecordTests.cs` (1), `CaptureTilesTests.cs` (9),
  `StereoVariantTests.cs` (1), `DefaultTextureTests.cs` (3), `VideoMemoryLedgerTests.cs` (3).
- Metal: `MetalConstantBufferTests.cs` (3), `MetalShaderProgramTests.cs` (3).

### 4.3 OpenGL-only unit tests
`Direct3DClipTests.cs` (4), `BindlessResidencyTests.cs` (11), `AheadOfUseTests.cs` (3), `GlBindlessTests.cs` (1),
`GlFormatTests.cs` (1), `GlOutputLocationTests.cs` (2), `GlSamplerTypeTests.cs` (2), `ProgramBuildLogTests.cs` (7),
`PassStateCacheTests.cs` (1), `TextureQualityTests.cs` (10), `UploadBuffersTests.cs` (1), `ForkJoinTests.cs` (2).

### 4.4 Host option tests
- `UnityArgumentsTests.cs:52-60`: TheForceArgumentsPickARenderer, 7 cases, with `-force-d3d11` → null today.
- `GameCommandLineTests.cs:76-79,111`.
- `PlayerSettingsTests.cs:249-254` (renderer) and :261-289 (dxgi).
- `RestartCommandTests.cs:65-66,91-92,121`.
- Python: `tests/scripts/test_e2e_spec.py:272,355-359`, `test_qa_verify.py:180-191` (test_vulkan_draws).

### 4.5 e2e specs and lists
- **`tests/e2e/u60/vk-gate.yaml`, the release gate** (`tests/e2e/release.txt:9-10`,
  `player-args='--renderer vulkan'`, ours only). It requires the log line `renderer: Vulkan \(vulkan, ` and
  `SystemInfo::graphicsDeviceType == 'Vulkan'` (:14,17), with no error signatures and no blank shots. **A D3D11
  device needs a twin gate.**
- `tests/e2e/u60/vk-smoke.yaml` (`periodic.txt:97`): ours on Vulkan against the original.
- `tests/e2e/loading-animation-vulkan.yaml` and `loading-animation-pip-vulkan.yaml` (`expect_log renderer:
  Vulkan`; in neither list).
- `tests/e2e/u60-p2-renderer.yaml`: graphicsDeviceType equals the original's under matching `-force-*` options
  (commented out at `periodic.txt:137`). With `-force-d3d11` in the original and a D3D11 device in ours, this
  becomes a direct parity check, since the original's Windows default is D3D11.
- Comment mentions only: `issue-71-space-overlay.yaml:7-11` (Vulkan attachment semantics),
  `game-update-smoke-late.yaml:2`, `u60-start-forest-grid.yaml:3`.
- The game-update checklist (`docs/GAME-UPDATES.md:176-188`) runs `u60/vk-gate ours player-args='--renderer
  vulkan'` and a "Windows Vulkan smoke" with `game-update-smoke-late.yaml`.

### 4.6 QA scenarios
- Vulkan only: `colony-glcore.sh`.
- Both: `resize.sh`, with one REQUIRE alternative per renderer. **A D3D11 device must log an equivalent line**
  (`vulkan: multi-target render texture released`) or add a third alternative.
- Renderer-neutral: burst, intro, newgame, overlays, research, saveload. They run on whatever `QA_EXTRA_ARGS`
  picks.
- The verify stage: `ONI_VERIFY_VK_DRAWS` and the partial-upload check, split by renderer at `qa.sh:454-457`.

---

## 5. How the Vulkan device was brought up

Source: `git log --reverse -i --grep=vulkan` plus commit bodies, summarised by a sub-agent. I did not re-verify
each hash.

**No incremental bring-up appears in master's history.** There is no "first clear" or "first triangle" commit:
- `cae46a64c` (2026-10-30) is the GLCore→SPIR-V compiler, about 660 lines.
- `ae1294645` (2026-10-30), within 30 minutes of it, is the whole device: about 4,170 lines in 18 files, with
  context, frames, pipelines and cache, VMA, presenter with its own swapchain, splash and the OpenGL fallback.
  The commit cites no tests or QA.
- The same subjects exist under other hashes on other branches (`git log --all`). This suggests the device was
  written elsewhere and squashed or cherry-picked. That is an inference.

| # | Stage | Commits (date) | Verification recorded |
|---|---|---|---|
| 1 | GLCore→SPIR-V compiler (shaderc, VkGlsl, SpirvReflection) | cae46a64c (10-30) | VkGlslTests; "all 559 non-stereo variants of U60 compile" |
| 2 | The whole device behind GlDrawRecorder/GlWorker | ae1294645 (10-30) | none in the commit |
| 3 | `--renderer` and Tweaks row, `--vulkan-validation` | 65b95f09b (10-30) | RendererChoiceTests |
| 4 | `ONI_VULKAN_FAIL` faults, frame counters | 487f52928, 1818aab0e, 4159310c4, b43ce1b3f (10-30) | the fallback testable on any machine |
| 5 | ARCHITECTURE §3.5 and D54 | 2396b87be (10-30) | docs |
| 6 | Switch to Unity's shipped platform-18 SPIR-V (SMOL-V, Unity's Vulkan conventions, reports Vulkan) | fa1c04257, 8d312b120, 3a48bd1c6, c03fa6807, d5a82e4dc, c06b8375f, b332ad6c1 (10-30) | VkFormatsTests, ShaderDataTests; all 539 programs (1,079 stages) decode and pass SPIRV-Tools validation; `ShaderSurvey vulkan` |
| 7 | Hardening against damaged input and out-of-memory | f4e711002, 0db822627, 5d4359d40, 66c7c25b0 (10-30) | VulkanProgramTests (every prefix, bit flips), VkRobustnessTests; the validation layer caught an out-of-range draw |
| 8 | Route for mods' shaders (`ONI_VULKAN_ROUTE=glcore`) | 2a98d3f38, 8d00a2cec, 42eb2740e, 3804759e8 (10-30); fix 086589b24 (11-01) | the black-world fix measured: 0.66 → 0.010 pixels differing against GL; qa scenario colony-glcore (e1d51a022) |
| 9 | QA harness made Vulkan-aware | 41d8ee66d, d2375d2cf, cc2ae7edf, 2b3b0b106 (10-30) | `QA_EXTRA_ARGS` / `E2E_PLAYER_ARGS` |
| 10 | Start guard and deny list | 47ecf3fa7, b21a6a27a (10-30); e20480ad5 (10-31) | VkStartGuardTests; faults crash-at and guard-build |
| 11 | DXGI interop (keyed-mutex shared texture) | DXGI base 26e934776 (10-29), abeb4eadc, 4ecafb559, 542a6854b; Vulkan side 94fc97191 … 96b31f82a (10-30); 9cf5ef822, 6f3e761ef, 9035ee2be, 24fc38ef4 (10-31) | "review finding" fixes in b928ab98c, 73fa9c18b, 96b31f82a, 542a6854b; `--dxgi-fault`; DxgiPresentTests; measured fps and latency; docs c344176c6, 486c9db5f, 0c9d41b26 |
| 12 | Spec audit: claims checked against vk.xml and the spec | ed8e26400 … cec703ce2 (10-31, 11 commits) | false premises removed (VK_TIMEOUT; the "made-up" keyed-mutex timeout); "measured, not documented" labels |
| 13 | Validation tooling (pinned layer, sync validation) | ea6fc65f5, 83afe58af, a3daeea3d (10-31) | no layer errors across the menu, a colony, window changes, every DXGI fault and the fallback |
| 14 | Perf and QA on U60, Windows | c10b79ba4, f48a26244 (10-31) | perf numbers |
| 15 | Unity `-force-*` options | be78d2933, ac894b695 (11-01) | UnityArgumentsTests; `u60-p2-renderer.yaml`; the original's behaviour measured first |
| 16 | Draw-path CPU perf (descriptor cache, host staging, buffer reuse, templates) | 72d41c87d … cea5e581f (11-01 to 11-03) | docs/research/vulkan-draw-binding.md; `ONI_VERIFY_VK_DRAWS` (fails qa verify); `ONI_CAPTURE_TWICE=full-draws` byte-identical; `ONI_VK_ALT` A/B; VkDescriptorCache, ReadyStamps, MeshSpare and TextureSet tests |
| 17 | Full U60 QA spec on Windows: ours on Vulkan against the original on D3D11 | 6f335576e (Windows e2e runner), 7ec4ea8f7, 9c8fc9feb, f9feacd31 (vk-smoke), bacc951aa (11-02) | R/U/A/I/P/S items pass; shot differences in docs/research/u60-qa-spec.md |
| 18 | Release gate | e071b9502 (11-05), bb4eff8f4 (11-06) | vk-gate in release.txt |
| 19 | Presentation follow-ups (repeats, vsync wait) | cda65d825, 748bf1343, 5ce5ef06b, bbf2c439e, 31a11ac46 (11-05 to 11-09) | launches and fps unchanged |
| 20 | AsyncGPUReadback without a stall; 8-bit ReadPixels | e575118bd (11-11), 6b687cdf6, 608336ded | bytes equal to a synchronous read; save-frame times |
| 21 | Loading animation on Vulkan | ab07f78f9 (tweak held back until it ran on Vulkan), 7a8778af9, 581e7af95, 741ae3c52, b48320540, 6704c6889 (11-11/12) | e2e loading-animation-vulkan(-pip); `ONI_TWEAK_VERIFY` 0 px; 21 loads in a row on both OSes |
| 22 | Pipeline cache file hardening | 4ee58f151 (11-12) | VkPipelineCacheFileTests; 67 → 10 ms |
| 23 | Default-on and docs | default from ae1294645; 1381bb376, e31a5c750, 181f19f70, README 68ce5df86 | the gates above |
| 24 | Post-release fix, issue #107 (Intel UHD keyed-mutex timeout) | cb4b0bdcc, a7cdbd897 (11-21) | DxgiPresentTests extended, verdict log line; found by a user |

**Docs:**
- docs/PLAN.md has no Vulkan milestone or acceptance criteria (only :448, perf numbers). M8 Windows is at
  :199-225.
- ARCHITECTURE.md: D54 at :1514-1523; §3.5 at :2182-3468.
- docs/PARITY.md:149,196.
- docs/TWEAKS.md:23,228-237.
- docs/research/u60-qa-spec.md:56,112,1887-1931 (P2), :1898. Linux dogfood was not run on Vulkan because of the
  user's rule against Vulkan runs on Linux.
- docs/research/u60-inventory.md:162-166.

Commits with "review" in the subject: none touch Vulkan. Reviews show up only as "(review finding)" in commit
bodies, and `vulkan-draw-binding.md:301-334` answers a review item by item.

**The process actually followed (inferred from the commits; no document names it):**
1. Reuse the shared recorder and main-thread side; the device is an executor only.
2. Make every start step fail on demand and fall back cleanly.
3. Guard the start with a crash guard and deny list; a failure in play quits with a clear message.
4. Check API claims against the spec or registry, and label claims as measured or documented.
5. Make the validation layer a gate, run across the menu, a colony, window changes and every fault.
6. Add a verify mode for each shortcut, failing `qa-tools.py verify`.
7. Measure perf with A/B windows in one run, plus a research doc.
8. Run the full U60 QA spec on Windows against the original. For D3D11 the original's own default is D3D11, so
   parity there is closer.
9. Add an ours-only release gate that proves the device started rather than the fallback.
10. Hold tweaks back until they run on the default device.
11. Write the docs commit (§3.5 plus a D-number) right after each batch.

**Gaps:**
- The core device commit shipped without its own tests.
- Linux Vulkan was tested on llvmpipe only, never real GPU hardware.
- Issue #107 was found by a user.

---

## 6. Checklist: what a D3D11 device touches (derived from the above)

1. `D3d11Device : RenderDevice, IResourceWork` and `D3d11Caps : GraphicsCaps`, with
   `DeviceType = Direct3D11`.
2. Per-device slots on `DeviceTexture`, `DeviceMesh` and `DeviceProgram` (GlTextures.cs:78, GlMeshes.cs:69,
   GlPrograms.cs:83).
3. Shader data:
   - add `ShaderCompilerPlatform.D3D11 = 4` and the D3D11 GPU program types to `ShaderProgramTable.cs`;
   - a platform-4 entry reader in Assets (`VulkanProgram.cs` analogue; RDEF stripped, so bindings come from the
     parameter tables);
   - `ShaderNative.ProgramsFor` / variant lists (`D3D11Variants`);
   - `GlDrawRecorder.VariantsOf` / `PlatformOf`;
   - a `ShaderSurvey` mode.
4. `RenderConventions`: a D3D11 mode (reversed Z, render-texture y flip, no gl_Position epilogue), with care in
   `GlPrograms.cs:878-913`. **Turn `Direct3DClip` and `AnimationLight` off for this device.**
5. Constant-buffer layout: check the `GlUniforms` std140-style bytes against D3D11 cbuffer packing from the
   parameter tables.
6. A route for mods' shaders without D3D11 programs (none exists; see 2.7), or "not drawn", or another
   renderer for them.
7. Formats: there is no swizzle on D3D11 (ARGB32 and Alpha8 handling); BC formats are native.
8. Presentation: share or extend `DxgiSwapChain` (its device, `Open`); the repeats, splash, loading animation
   and queued-frames wait work through the shared classes; there is no interop. Decide what `--dxgi off` means.
9. Readbacks with staging textures; AsyncGPUReadback by polling.
10. Device removed: quit with the message (rename the "Vulkan stopped working" text).
11. Start-up:
    - `Renderers` gets a new choice and fallback order;
    - `RenderApi`;
    - a start guard keyed per device (`VkStartGuard` is Vulkan-named and its file is `vulkan-start-guard.txt`);
    - a deny list;
    - fault injection (an `ONI_D3D11_FAIL` analogue);
    - a debug-layer option (an analogue of `--vulkan-validation`).
12. Host:
    - `Options.cs` (`--renderer`, map `-force-d3d11` at :491 and `UnityArguments.cs:25-31`);
    - `PlayerSettings`;
    - `GameTweakNames` Renderer row and translations (moono.pot, zh.po);
    - `ShaderCheck` branch;
    - `Player.cs:27-43` static configuration;
    - crash `gpu` and driver event-log lookups.
13. Tests and QA:
    - unit-test twins of VkFormats and VkStartGuard;
    - a DxgiPresent subset;
    - UnityArguments (`-force-d3d11` no longer null);
    - a `d3d11-gate.yaml` in release.txt;
    - a `u60-p2-renderer` case with `-force-d3d11`;
    - `qa.sh` verify split;
    - the `resize.sh` REQUIRE alternative;
    - `scripts/e2e-windows/run.py` `WIN_RENDERER=d3d11`;
    - the HdrCheck matrix;
    - loading-animation spec twins;
    - Windows-only coverage, since CI is Linux.
14. Docs:
    - ARCHITECTURE §3.5 and a new D-number;
    - supersede docs/research/unity-vs-moono.md:120 ("No Direct3D renderer, by choice");
    - TWEAKS.md renderer row.
