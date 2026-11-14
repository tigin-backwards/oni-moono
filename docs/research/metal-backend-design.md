# A Metal backend: design and scoping

**Scope.** How Moono would render on macOS through Metal, building on the macOS study
(`docs/research/macos-feasibility.md`, "the study" below; §-references without a document name
point there). OpenGL on the Mac (GL 4.1) is not a target and is not designed for here (study
§2.4). This document has two halves:

1. what is done and verified on Linux, with the evidence (§1, §2);
2. the design of what needs a Mac: the device interface both backends share, Objective-C interop,
   pipelines, targets, conventions, uploads, presentation, and a test plan for when a Mac exists
   (§3 to §11), with the open questions and how each would be settled (§12).

No Mac and no macOS CI were used. Everything marked *verified* ran on this Linux machine against
the macOS depot (`OxygenNotIncluded.app`, Steam depot 457142, ChangeList 744825, the same build as
the Linux install). Everything else is design, marked **(to verify on a Mac)** where a fact rests
only on Apple's documentation or on community reports.

**Clean room.** As in the study: no Unity C# or native code was read or disassembled. Formats were
inferred from the shipped data, the dylibs' symbol tables were read as data.

---

## 1. What is done (Linux-side groundwork)

| Piece | Where | Verified by |
|---|---|---|
| Shader blob readers: platform blob table, Metal program entries (keywords, entry point, MSL, channel mask, vertex attribute table), parameter blobs merged with a pass stage's common parameters | `src/OniMoono.Assets/Shaders/` (`ShaderProgramTable`, `MetalProgram`, `ShaderParameters`, `PlayerSubprograms`) | `tests/OniMoono.Assets.Tests/ShaderDataTests.cs`: hand-written fixtures in the shipped layout; all 1057 Metal programs and parameter blobs of the Mac data decode (522 vertex, 535 fragment, 120 shaders) |
| Program loading per platform: `ShaderPrograms.Load(shader, 14 or 15)`, Metal variants per stage (`ShaderPass.MetalVertexVariants`, `MetalFragmentVariants`), `ShaderPrograms.SelectStages`, `ShaderNative.ProgramsFor`, `RenderDevice.ShaderPlatform` | `src/OniMoono.UnityEngine/Engine/Rendering/ShaderPrograms.cs`, `ShaderNative.cs`, `RenderDevice.cs` | `tests/OniMoono.UnityEngine.Tests/MetalShaderProgramTests.cs`; the GLCore path is unchanged: every GLCore variant of the Linux and Mac data (583 and 596) decodes to the same keywords, stage sources and channel masks before and after (a scratch comparison, not kept) |
| Metal parameters against GLCore's | `tools/ShaderSurvey` `compare-metal` (`MetalCompare.cs`), `check-metal` | §2; `scripts/check.sh` runs both when the Mac data is present |
| Constant-buffer packer | `src/OniMoono.Desktop/Metal/MetalConstantBuffers.cs` | `tests/OniMoono.UnityEngine.Tests/MetalConstantBufferTests.cs`: §2.3 |
| osx-arm64 cross-publish (the primary build) and osx-x64 (the fallback) | `scripts/check.sh` | self-contained publishes, arm64 and x86_64 Mach-O apphosts, every gate run; the arm64 one carries BRUTAL's dylibs and, when at hand, onimaxxing-sim in `plugins/` |
| Native interface, both architectures | `src/OniMoono.Host/NativeCheck.cs` (`--check-natives`, `--native-arch`, `--native-dir`), `src/OniMoono.UnityEngine/Engine/Platform/NativeExports.cs` (slices, ELF machine, Mach-O dependencies) | §2.4; `NativeExportsTests` on hand-built Mach-O and ELF files |
| SimDLL replacement for arm64 | `GameLayout.Native`, `AssemblyPolicy.ResolveNative`: a plugin without the process's architecture gives way to `plugins/lib<name>.dylib` | `GameLayoutTests`; the port bound on Linux with `--native-override` (study §3.4) |
| Mac host paths | `src/OniMoono.UnityEngine/Engine/Platform/GameLayout.cs` (`GameLayout`, `MacUserFolders`), `Program.cs` (`Paths.ConfigureMac`), `AssemblyPolicy.ResolveNative` | `GameLayoutTests` on fake `.app` trees; `--check-assets` and `--check-natives` read the depot's bundle on Linux |

---

## 2. What the shipped data says

### 2.1 Metal programs against GLCore: every difference, by class

`ShaderSurvey compare-metal DATADIR [all|CLASS]` takes each GLCore variant of each pass, selects the
Metal vertex and fragment programs Unity's rule picks for its keywords, and compares each Metal
stage's parameters with the GLSL section's declarations (what GL reflection reports: names, types,
array sizes, default block or uniform block, samplers, storage blocks) and the union of both with
the GLCore parameter blob. On the Mac data:

```
173 passes, 591 GLCore variants, 1057 Metal programs (1057 selected by a GLCore variant),
582 variants with exact Metal keywords
 10700  agree  constants with the same name, type and array size
  1860  agree  textures with the same name and dimension
    69  agree  structured buffers matching a GLSL storage block
every difference is explained
```

(The 5 GLCore programs of passes without Metal programs are `Hidden/VideoDecodeOSX`'s 4 and the
geometry-shader subshader of `Hidden/VR/BlitFromTex2DToTexArraySlice`, study §1.3.)

The differences, each a consequence of how the platforms' programs were built:

| Class | Count | What it is | What the Metal device does |
|---|---|---|---|
| Unity built-in cbuffer on Metal, loose uniform on GLCore | 3895 | HLSLcc's GLCore output turns `UnityPerDraw`, `UnityPerFrame`, `UnityPerCamera`, `UnityLighting`… into default-block uniforms; Metal keeps them as `constant T&` buffers. | Packs and binds those buffers like `VGlobals`/`FGlobals` (the packer treats all bound buffers alike, §2.3). |
| GLCore comparison sampler `hlslcc_zcmp_X` of Metal's depth texture X | 57 | A shadow map sampled both plainly and with comparison: GLSL declares two samplers for one texture, Metal one `depth2d` with `sample_compare` (35 programs). Only `Standard`, `Legacy Shaders/Diffuse` and the particle surface shaders, in shadow variants the game does not use. | A sampler state with a compare function for the comparison sampler slot. |
| No sampler on Metal (`texture.read`) | 87 | `Klei/BatchedAnimation*` instance data and the like: Metal reads texels with no sampler (sampler slot −1), GLSL uses `texelFetch` on its combined sampler. | Binds the texture only. |
| GLCore blob drops members whose offsets coincide across stages | 1314 | The GLCore parameter blob merges the two stages' `$Globals` layouts by offset: where both stages have a member at an offset, one is recorded (`Klei/BatchedAnimation`'s vertex `_ClusterWorldSizeInfo` @128 is lost under the fragment's `_DiseaseOverlayTexInfo` @128). Every drop is checked against both stages' packing, inferred from the data: 16-byte registers, no value straddling one, arrays and matrices starting a register, a float array tight. | Nothing: the GL device binds by reflection and never reads the GLCore blob. A neutral parameter model (§3) must not be filled from GLCore blobs. |
| GLCore blob records int as type 5 | 120 | The GLCore blob's ShaderParamType for `int` is 5; Metal's is 1, `uint` 5. | Read the type per platform. |
| Fallback: GLCore variant without exact Metal counterpart | 9 (+94 consequent differences) | The Mac build's GLCore extras (DISABLE_K_LIGHTING of five BatchedAnimation shaders, UNITY_UI_ALPHACLIP of BatchedAnimationUI, DISABLE_TEMPERATURE_PARTICLES of LightBufferCompositor). The Metal set lacks them, as does Linux's GLCore set. | Selection falls back to the keyword-less programs, as our Linux player does today and as Klei's Mac player must. |
| Multiview | 12 variants (35 differences) | `STEREO_MULTIVIEW_ON` (VR): the GLSL indexes `unity_StereoMatrixVP` by `gl_ViewID`; Metal has no multiview extension and its variant reads `unity_MatrixVP`. | Never selected (the GL device skips them without `GL_OVR_multiview2`). |

No variant with exact Metal keywords differs in a constant's type or array size, a texture's
dimension or multisampling, buffer membership other than the classes above, or structured buffers.

### 2.2 Metal reflection against the MSL

`ShaderSurvey check-metal` (study §1.4), extended: besides texture, sampler and buffer slots, it now
decodes each entry with `MetalProgram.Read` and checks the vertex attribute table against the MSL's
`[[ attribute(n) ]]` declarations: a pair (channel, component) is `[[ attribute(component − 13) ]]`
for every input of all 522 vertex programs, and every channel of the table is in the channel mask.
`1057 Metal subprograms checked, 0 inconsistent`.

Other facts from the data (scratch census over all 1057 programs):

- **Slots.** Constant and structured buffers use `[[buffer(0..5)]]` in both stages; textures and
  samplers 0..15; vertex attributes 0..9. The largest constant buffer is 1088 bytes.
- **Colour outputs.** 413 fragment programs write 1 target, 1 writes 2, 80 write 3 and 40 write 4.
- **Function constants.** Two only: `rp_output_remap_mask` (index 1, `uint`, 534 fragment
  programs; output *i* goes to `color((mask >> 4i) & 0xF)`) and `has_base_vertex_instance` (index
  4, `bool`, 55 vertex programs; gates a `[[base_instance]]` argument added to `instance_id`).
- **Payload header.** Twelve 4-byte fields before the entry point: magic `0xF00DCAFE`, the entry
  point's offset (48), 7, the source's offset, five zeros, a field that is 39 in exactly the 13
  vertex programs writing `render_target_array_index` and 37 in all others (a flags word, bit 1 =
  layered rendering), −1, 0. `MetalProgram.PayloadHeader` keeps them uninterpreted.
- **Common parameters and blob sizes.** A pass stage's common parameters record its largest
  variant's buffer size; in 9 programs that is larger than the program's MSL struct. The variant
  blob's size is never larger than the MSL struct's, so `ShaderParameters.Merge` takes the blob's.

### 2.3 The constant-buffer packer

`MetalConstantBuffers.Layout(ShaderParameters)` gives a stage's bound buffers (those with a
constant-buffer binding: name, `[[buffer(n)]]` slot, size, members). For each member it builds the
`UniformBinding` the GL device would build for the uniform of that name (`GlPrograms.Classify`:
HLSLcc names, `hlslcc_mtx4x4X` for matrix X) and the MSL stride (scalars of an array 4 bytes, a
`float3` 16). `Pack(buffer, DrawInputs, bytes)` resolves each member with `GlUniforms.Resolve`, the
GL device's own resolution (renderer block, then material, then globals; built-in matrices, time,
screen and texture-derived vectors), and writes it at the blob's offset (ints and uints as
integers, as `WriteBlock` does).

Verified on the Mac data:

- **Layout.** In all 1057 programs, the 2688 bound buffers' 9576 members are at the offsets, with
  the MSL types, element counts and strides, that the program's MSL struct gives them by Metal's
  layout rules (`MslStructs`, a reader of the structs HLSLcc emits), with no overlap, within the
  buffer size, and read back as resolved.
- **Values against GL.** For the 570 GLCore variants whose keywords Metal has exactly (multiview
  aside), the GL program's default-block uniforms (from the GLSL declarations, as reflection gives
  them) are recorded with `GlUniforms.Record` for one draw with distinct values for every input;
  all 10172 are in a Metal buffer of the selected programs, with the same 53624 values, bit for
  bit.

The packer does not apply Metal's clip-space conventions; the device hands it camera constants
that already have them (§6).

### 2.4 Native interface

`OniMoono --game <dir or .app> --check-natives` reads every P/Invoke of the assemblies the host
runs with (its own, the UI packages, and those of `Managed/` the policy would load), resolves each
library as the host does for the install's layout, and reads the exports of the plugin file
without loading it. It reads the slice of `--native-arch` (x86_64 or arm64) and lists the
dependencies each Mach-O slice links, which must be found. On the Mac depot:

| Library | x86_64 (fallback) | arm64 (primary, `--native-dir out/publish/osx-arm64`) |
|---|---|---|
| SimDLL | Klei's `SimDLL.bundle/Contents/MacOS/SimDLL` (thin x86_64): 32 of 32 | Klei's has no arm64 code; onimaxxing-sim from `plugins/libSimDLL.dylib`: 16 of 32, the other 16 the `kprofiler_*` functions no game code calls (study §3.4) |
| fmodstudio (universal) | 571 of 571 | 571 of 571 |
| steam_api (`libsteam_api.dylib`, universal) | 924 of 930; the 6 missing (`SteamGameServerClient`, `SteamGameServer_ReleaseCurrentThreadMemory`, four `ISteamNetworkingConnectionSignaling`/`SignalingRecvContext` functions) are missing from Linux's `libsteam_api.so` too: Steamworks.NET 20.1.0 binds a newer SDK than the game's; never called, and a P/Invoke binds at its first call | the same |
| cimgui, libEOSSDK-Mac-Shipping, rail_api64, sdkencryptedappticket | not shipped in the bundle: Klei's dev tools are compiled out on Mac; no EOS platform is created; Rail is WeGame's; no game assembly names the encrypted-ticket API | the same |
| glfw, freetype, stb, glad | BRUTAL's: its packages ship no osx-x64 natives (study §3.2), the gap to close for the fallback | BRUTAL's osx-arm64 dylibs in the publish: glfw 138 of 154 (the 16 absent are GLX, WGL, Wayland, Win32 and X11 accessors), freetype 15 of 15 with `libpng18.18` and `libzlib.1` found beside it, stb 17 of 17, glad 2 of 2 |
| libc, dl, libSystem, kernel32… | system libraries (0Harmony and MonoMod probe all platforms') | the same |

Each of the port's four dylibs (arm64 and x86_64, sim-thread and default) gives the same 16 of
32, with `libSystem.B.dylib` its only dependency.

On the Linux install the same check finds SimDLL, fmodstudio and steam_api complete but for the
same six, and Linux's `cimgui.so` lacking `SetKAssertCB` (ARCHITECTURE D20), which validates the
check. `resonanceaudio` and `fmod_haptics` are not P/Invoked: FMOD loads them itself (§12).

### 2.5 Mac host paths

`GameLayout.Find(--game)` takes a folder with `OxygenNotIncluded_Data`, an `.app`, or a folder
holding one `.app` (the Steam install). For the bundle:

- Data is in `Contents/Resources/Data`, and `Application.dataPath` = `Contents`.
- Plugins are `PlugIns/<name>.bundle/Contents/MacOS/<CFBundleExecutable>`, then
  `PlugIns/lib<name>.dylib`.

`MacUserFolders` (Unity's documented Mac behaviour, **to verify on a Mac**):

- Persistent data: `~/Library/Application Support/<CFBundleIdentifier>`
  (`unity.Klei.Oxygen Not Included`).
- Caches: `~/Library/Caches/<id>`.
- Log: `~/Library/Logs/Klei/Oxygen Not Included/Player.log`.

Steam's `%command%` may name the `.app` or its executable `Contents/MacOS/Oxygen Not Included`.
The default install is `~/Library/Application Support/Steam/steamapps/common/OxygenNotIncluded`
(**to verify**).

---

## 3. The device interface both backends share

`RenderDevice` (17 members, study §2.1) stays the seam; a `MetalDevice : RenderDevice` implements
it, and `ShaderPlatform` (now on `RenderDevice`) returns 14. What has to move first is the
backend-neutral logic that lives in `src/OniMoono.Desktop/Gl/` (study §2.3). This refactor is
**designed here, not done**: it touches the per-frame render path, so it should be done as its own
series, each step keeping the GL device's output and frame time.

### 3.1 Target shape

New engine-side home: `src/OniMoono.UnityEngine/Engine/Rendering/Device/` (namespace
`OniMoono.Engine.Rendering.Device`), internal, visible to `OniMoono.Desktop`.

| Neutral piece | From | Contents |
|---|---|---|
| `UniformSource`, `UniformSlot` | `GlPrograms.cs` `UniformSource`, `UniformBinding` | What a constant is (name, source, property id, scale-offset id, GL-style type, size, matrix). `UniformBinding`'s GL placement fields (`Location`, `Block`, `Offset`, `ArrayStride`) move to a GL-side `GlUniformPlacement` (or stay on a GL subclass). |
| `DrawInputs`, `UniformValues.Resolve`, `Length` | `GlPrograms.cs` `DrawInputs`, `GlUniforms.Resolve`/`ResolveProperty`/`WriteMatrix`/`Length` | Value resolution, unchanged. Gains a `ClipConventions` input for the projection-bearing sources (§6). |
| `ConstantRecorder` | `GlUniforms.Record`/`RecordFrom`/`ValuesOf`/`Resolve`/`SharedValuesOf`/`RecordAll`/`OwnValue`, `MaterialValues` | The per-material cache and the per-draw overlay, over an abstract layout: `IConstantLayout { int Length; ReadOnlySpan<UniformSlot> Slots; void Write(int slot, ReadOnlySpan<float> values, Span<byte> target); }`. GL implements it as today's loose floats plus std140 blocks; Metal as its packed buffers (`MetalConstantBuffers.Write`). |
| `PassSelection` | `GlDrawRecorder.IsForwardPass`/`Passes`/`ResolveUsePass`, the variant cache (`MaterialNative.Variants`, `GlDrawRecorder.SelectVariant`) | Pass list for a draw; `ShaderPrograms.SelectStages(pass, platform, …)` (done) for the programs. |
| `PassState`, `ResolvedState`, `ResolvedFor` | `GlRenderState.cs` | ShaderLab state parsed and resolved from material values. `GlStateCache` (GL calls) stays in `Gl/`. |
| `DefaultTextures` | `GlDevice.CreateDefaults`/`DefaultTexture`, `GlDrawRecorder`'s unbound-sampler rule | The names and colours (white, black, gray, bump, red, unbound = (0,0,0,1)) and dimensions; each device makes its objects. |
| `ProgramInputs`, `FrameArena`, `DrawRecorder` | `GlDrawRecorder.cs`, `GlWorker.FrameArena` | Main-thread recording, parallel queue recording and verify mode, over a backend: `PrepareTexture`, `PrepareMesh`, `Program(shader, pass, StagePrograms)` (compiles on the render thread, waits), `Post(ProgramInputs)`. |
| `RenderWorker` | `GlWorker` | Frame hand-over, one frame in flight, `Call` for results; GL's subclass owns the context, Metal's an autorelease pool per frame. |
| `MeshLayout`, `Channel` | `GlMeshes.cs` | Channels a mesh provides; index conversion. |
| `TexelUpload`, `UploadBuffers` | `GlTextures.cs` | Texel snapshots and the reused copy buffers. |

Generic recording code should be generic over a struct backend
(`DrawRecorder<TBackend> where TBackend : struct, IDrawBackend`) so the JIT devirtualises the
per-draw calls: the GL recorder records ~1700 draws a frame (ARCHITECTURE §3.2) and its timing is
measured (`ONI_RENDER_TIMING`).

### 3.2 Order and safety net

1. `UniformSource`/`UniformSlot`/`DrawInputs`/`Resolve` to the engine (pure move);
   `MetalConstantBuffers` switches to them. Net: `UniformRecordingTests`, `MetalConstantBufferTests`.
2. `PassState`/`ResolvedState`; then `PassSelection` and `DefaultTextures`.
3. `ConstantRecorder` over `IConstantLayout`, GL first (its loose-and-block layout).
4. `RenderWorker` and `FrameArena`; then `DrawRecorder<TBackend>` (`ParallelDrawRecordingTests`
   compare serial and parallel command streams).
5. `MeshLayout`, `TexelUpload`.

After each: unit tests, `--check-shaders`, the e2e screenshot suites, and a frame-time comparison
on the large colony (`docs/research/u57-frame-inventory.md` method) to keep the GL path's cost.

---

## 4. Objective-C and Metal from .NET

**Choice: raw `objc_msgSend` through typed function pointers, hand-written for the subset used.**

- `src/OniMoono.Desktop/Metal/ObjC.cs`: `libobjc` (`/usr/lib/libobjc.A.dylib`) through
  `NativeLibrary`; `objc_getClass`, `sel_registerName`, `objc_msgSend`,
  `objc_autoreleasePoolPush`/`Pop`, `objc_retain`/`objc_release`. Selectors resolved once into
  `static readonly IntPtr` fields. Each call site casts `objc_msgSend` to its exact signature,
  e.g. `((delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint, IntPtr>)msgSend)(device, newBufferWithLength, length, options)`;
  no marshalling stubs, no `DllImport` overload families.
- `src/OniMoono.Desktop/Metal/MetalApi.cs`: thin typed wrappers (`MTLDevice`, `MTLCommandQueue`,
  `MTLLibrary`… as `readonly struct` handles) for about 150 selectors, plus `MTLCreateSystemDefaultDevice`
  from `Metal.framework`. Structs by value as Apple declares them: `MTLRegion` (48 bytes),
  `MTLViewport` (6 doubles), `MTLScissorRect`, `MTLClearColor`, `MTLSize`, `MTLOrigin`.
- **arm64** (the primary build) has no `objc_msgSend_stret`: every call goes through
  `objc_msgSend`, struct returns included.
- **x86_64 under Rosetta (the fallback):** a struct return larger than 16 bytes goes through
  `objc_msgSend_stret` (arm64 has none). The device needs few (`MTLSize`-returning getters such as
  `maxThreadsPerThreadgroup`, avoidable). Struct *arguments* by value follow the SysV ABI through a
  typed function pointer. Double returns (`GPUStartTime`) use plain `objc_msgSend` on x86_64.
- **Ownership:** `new…`/`copy` results are +1 and released by the device's `ReleaseResource`;
  `commandBuffer`, `renderCommandEncoderWithDescriptor:`, `nextDrawable`, descriptors from class
  factories are autoreleased: the render thread wraps each frame in an autorelease pool, and the
  main thread's resource creation in one per call.
- **No blocks.** Completion handlers need Objective-C block literals; the design avoids them:
  frames are retired with `waitUntilCompleted` on the command buffer of frame N−3 before its ring
  region is reused (it returns at once unless the GPU is behind), and libraries and pipelines are
  built synchronously on a compile thread (§5).
- **Errors:** `NSError**` out-parameters read with `localizedDescription`; Objective-C exceptions
  are fatal, so debug runs enable Metal API validation (`MTL_DEBUG_LAYER=1`,
  `METAL_DEVICE_WRAPPER_TYPE=1`) to catch misuse before it throws.

Alternatives (study §3.3): SharpMetal (MIT, generated, low activity) and ppy's
`Veldrid.MetalBindings` (MIT, hand-written, shipped by osu!) are good references for selector
spellings and struct layouts; `dotnet/macios` needs the macOS workload and Xcode to build;
Silk.NET has no Metal. Apple's metal-cpp headers (Apache-2.0) are the selector catalogue.

What Linux can check: struct sizes and field offsets against Apple's headers (unit tests with
`Marshal.SizeOf`/`Unsafe.OffsetOf`), selector strings against a list taken from metal-cpp's
`_MTL_PRIVATE_DEF_SEL` lines, and that every wrapper's function-pointer signature has the
argument count its selector's colons imply. All runtime behaviour needs a Mac.

---

## 5. Programs and pipeline states from MSL

- **Libraries.** One `MTLLibrary` per Metal program entry: `newLibraryWithSource:options:error:`
  on `MetalProgram.Source`, cached in the shader's `Gpu` slot by blob index (`MetalProgramSet`,
  the counterpart of `DeviceProgramSet`). `MTLCompileOptions.languageVersion` to be fixed by
  compiling all 945 sources (**to verify**: they need MSL 2.1+ for `invariant`; Unity's choice is
  not recorded in the payload as far as the data shows).
- **Functions.** `newFunctionWithName:constantValues:error:` for `xlatMtlMain` with
  `MTLFunctionConstantValues`: `rp_output_remap_mask` (index 1) = identity `0x76543210` unless an
  MRT binding leaves a gap (§7); `has_base_vertex_instance` (index 4) = true when draws pass a base
  instance (instanced particle draws), false otherwise. Cached per (library, constants).
- **Vertex descriptor.** From the mesh's `MeshLayout` (one buffer per channel, as `DeviceMesh`
  keeps them) and `MetalProgram.Inputs` (channel → attribute). Channel *c*'s buffer at
  `[[buffer(30 − c)]]` (19..30), clear of the constant buffers' 0..5. Absent channels the program
  reads (white colour and the like) come from a 16-byte constant buffer bound with
  `MTLVertexStepFunctionConstant`.
- **Pipeline key** (`MetalPipelineKey`): vertex function, fragment function, vertex descriptor
  id, colour attachment pixel formats (up to 4 used, 8 possible), depth and stencil formats,
  sample count, per-attachment blend state and write mask (`ResolvedState`), alpha-to-coverage, and
  for the 13 layered programs `inputPrimitiveTopology`. `MTLRenderPipelineState` by
  `newRenderPipelineStateWithDescriptor:error:`.
- **Depth-stencil key**: ZTest, ZWrite, stencil compare/ops/masks per face →
  `MTLDepthStencilState`. Cull mode, front-face winding, depth bias (`Offset`), stencil reference,
  viewport and scissor are encoder state, set per draw when changed (as `GlStateCache` does).
- **Where and when.** Libraries and functions compile on a compile thread (`MTLDevice` is
  thread-safe); a draw whose pipeline is not ready waits for it, as the GL recorder waits for a
  program (`GlDrawRecorder.Program`). `WarmupShaders` compiles every library and the pipelines of
  the formats seen so far. An `MTLBinaryArchive` in `Application.temporaryCachePath`, not in the
  bundle, can keep pipelines across runs.
- **A Metal `--check-shaders`**: every library, function (with both constant values) and one
  pipeline per pass with default formats; compile errors and time per program.

---

## 6. Clip space, the y flip and projections

Our engine follows Unity's GLCore conventions (study §2.2): GL clip z in [−1, 1],
`_ProjectionParams.x = 1`, viewport and scissor from the bottom left, texture rows bottom up. The
Metal programs were compiled for Unity's "UV starts at top" conventions (study §1.5:
`Klei/PostFX/FullScreen`'s vertex stage flips `TEXCOORD0` on GLCore and passes it through on Metal).

The device applies Unity's Metal conventions in one place:

1. **Projection.** `MetalDevice.ApplyCamera` (and `DrawImmediate`'s projection) turns the GL
   projection *P* into the GPU projection: z remapped to [0, 1]
   (`P' = [1 0 0 0; 0 1 0 0; 0 0 ½ ½; 0 0 0 1] · P`), and when the target is a render texture also y
   negated. The sources `MatrixP`, `MatrixVP`, `MatrixInvVP`, `MatrixMVP` and `ZBufferParams` of
   `UniformValues.Resolve` read the GPU projection; `CameraProjection`/`CameraInvProjection`
   (`unity_CameraProjection`) keep the GL one, as Unity's do. `ProjectionParams.x` is −1 when
   rendering into a texture. These enter as a `ClipConventions` input (§3.1), so the packer
   (§2.3) needs no change.
2. **Render-texture content.** With y negated into textures, a render texture's rows end up in the
   same memory order as on GL, so textures loaded from data (stored bottom-up by Unity for every
   platform) and rendered ones sample alike, and `ReadPixels` of a render texture copies rows as
   GL does. The back buffer is drawn unflipped; reading it back reverses its rows.
3. **Winding.** Negating y reverses the winding of every triangle: front-facing is
   counter-clockwise when not flipped (GL's default) and clockwise when flipped
   (`setFrontFacingWinding:`).
4. **Viewport and scissor** convert from bottom-left to top-left: `y' = targetHeight − (y + h)`.
5. **Hard-coded orthographic matrices** (`Graphics.cs`, `GUIRenderer.cs`, `CanvasSystem.cs`,
   `VideoPlanes.cs`, study §2.2) all reach the device through `SetCamera` or `DrawImmediate`, so
   they get the same rewrite.

Which passes then sample with flipped UVs is decided by the MSL itself (`UNITY_UV_STARTS_AT_TOP`
was true when it was compiled). Whether every Klei pass composes upright under these rules
(post-processing chains of `Klei/PostFX/*`, the light buffer, MRT copies) is the main convention
risk (study R3): settled pass by pass on a Mac with `--dump-render-textures` against the Linux GL
player and against Klei's Mac player (§11).

---

## 7. Render targets and MRT

- `SetRenderTarget` records a target; the render pass starts lazily at the first draw or clear
  after it. A pass is an `MTLRenderPassDescriptor`: colour attachments (`RenderTargetBinding.Colors`,
  up to 4 used), depth-stencil attachment (`Depth32Float_Stencil8`, which every Mac GPU has;
  `Depth24Unorm_Stencil8` only where `depth24Stencil8PixelFormatSupported`), load action `Load`,
  store action `Store`
  (`StoreAndMultisampleResolve` with a resolve texture for MSAA targets).
- `Clear` before the pass's first draw becomes load actions (`Clear` with the colour and depth);
  a clear after draws ends the encoder and starts the pass again with clear load actions (or draws
  a full-target quad if measurement shows passes restarting often).
- **MRT with gaps**: a null colour after the first (a destroyed buffer) leaves its attachment
  empty on GL. On Metal the pipeline's pixel format for that slot is `Invalid`; whether writes to
  it are dropped or must be remapped with `rp_output_remap_mask` (Unity's reason for the constant)
  is to be settled with the validation layer (§12).
- **Back buffer**: an offscreen `BGRA8Unorm` texture per frame in flight, as GL's presenter
  rotates three; at present it is blitted to the `CAMetalLayer` drawable (`framebufferOnly = NO`)
  or drawn to it with a full-screen pass.
- **Mips and resolves**: auto-generated mips by a blit encoder (`generateMipmapsForTexture:`) after
  the pass that wrote the texture, as GL's `Dirty` flag drives them today.
- **Readback** (`ReadPixels`, `AsyncGPUReadback`, captures): a blit into a shared `MTLBuffer`,
  `waitUntilCompleted`, then `TextureFormats.Encode` as GL does after `glReadPixels`.

---

## 8. Texture upload and streaming buffers

GL leans on the driver: `glTexSubImage2D` and `glBufferSubData` may be called while earlier draws
reading the same object are in flight. Metal does not version resources: a CPU write to a texture
or buffer the GPU is still reading races with it. The design keeps GL's semantics explicitly.

- **Ring buffer** (`MetalStreamBuffer`, the counterpart of `GlStreamBuffer`): one `MTLBuffer` in
  `MTLStorageModeShared`, split per frame; three regions (frames in flight plus the one recording).
  Constant buffers are packed **straight into the ring at record time** (shared memory: no copy on
  the render thread, unlike GL's `FrameArena` → `GlStreamBuffer` copy); parallel recording workers
  take sub-ranges with an interlocked add. Offsets aligned to 256 bytes (macOS's constant-buffer
  offset rule for Intel and AMD GPUs; Apple GPUs allow less, **to verify**). Bound with
  `setVertexBuffer:offset:atIndex:` / `setFragmentBuffer:…`, or `setVertexBufferOffset:` when only
  the offset changes. `setVertexBytes` (≤ 4 KB) is the fallback for one-off immediate draws.
  Before a region is reused, the command buffer that last read it is waited for
  (`waitUntilCompleted`, §4).
- **Dynamic meshes** (rebuilt every frame by the game): vertex data copied into the same ring (or
  a second one), bound by offset; static meshes in their own buffers.
- **Texture creation**: `MTLStorageModePrivate` textures filled by a blit from the ring at frame
  start (BC1/BC3/BC7 directly where `supportsBCTextureCompression`, decoded by our CPU decoders
  otherwise; ARGB32 and Alpha8 through `MTLTextureSwizzleChannels`).
- **Per-frame content updates** (`Texture2D.Apply` on the game's property textures, about 20 MB a
  frame in a large colony, `UploadBuffers`): the recorded upload is a copy into the ring plus a blit
  in the frame's upload encoder, which runs before the frame's first render pass. A texture applied
  again after a draw of the same frame has read it gets a new version (a fresh `MTLTexture` from a
  per-size pool) for the later draws, the renaming GL drivers do; the old version returns to the
  pool when its frame completes. `GlTextures.UploadChanged`'s row diffing carries over.
- **Shared, managed, private.** The primary build is native arm64 on Apple Silicon; the fallback
  is an x86_64 process under Rosetta.
  - On Apple Silicon all storage is unified memory. `Shared` buffers are CPU-visible without
    copies and `Managed` behaves as `Shared` (its `didModifyRange` costs nothing).
  - On Intel Macs with discrete GPUs, `Shared` textures are not allowed and `Managed` costs a
    transfer.
  - So: `Shared` for the ring and readback buffers on both; `Private` + blit for textures on
    both (no per-architecture paths).
  - Under Rosetta (the fallback), the x86_64 slices of Apple's frameworks are translated too, so
    each Metal call costs more than natively. The design minimises calls per draw (offset
    rebinding, state caching, one encoder per pass), and the frame counters
    (`TakeFrameCounters`) count them.

---

## 9. Threading, presentation and the window

- **Threads** as today (ARCHITECTURE §3.5): the main thread records, a render thread (`MetalWorker`,
  from `RenderWorker`) encodes one `MTLCommandBuffer` per frame on one `MTLCommandQueue`. The
  presenter thread and the shared-context hidden window disappear: present is `nextDrawable` (on
  the render thread), the back-buffer blit, `presentDrawable:`, `commit`.
- **Pacing**: `CAMetalLayer.displaySyncEnabled` = `vSyncCount > 0`; `maximumDrawableCount` 2 or 3
  (Unity's two queued frames); no vsync → `targetFrameRate` pacing as on Linux.
- **GPU timing**: `MTLCommandBuffer.GPUStartTime`/`GPUEndTime` after completion, for
  `TakeGpuFrameMilliseconds`.
- **Window** (`GlfwPlatform`, study §4): a variant with `ClientApi = NoApi`, no GL hints, no hidden
  shared window, no X11 preference; a `CAMetalLayer` set on `glfwGetCocoaView` (Brutal.Glfw's
  `CocoaExtensions`) on the main thread, `contentsScale` and `drawableSize` updated from the
  content-scale and framebuffer-size callbacks. Split as `GlfwPlatform` (neutral: input, cursors,
  monitors, clipboard) plus a surface (`GlSurface`: context, swap; `MetalSurface`: layer).
- **Host wiring**: `Player.cs` and `ShaderCheck.cs` pick the device (`MetalDevice` on macOS, GL
  elsewhere; `--device gl|metal` for tests).

---

## 10. Files

| File | Role |
|---|---|
| `src/OniMoono.Desktop/Metal/ObjC.cs`, `MetalApi.cs` | Interop (§4) |
| `src/OniMoono.Desktop/Metal/MetalDevice.cs`, `MetalCaps.cs` | `RenderDevice`, `GraphicsCaps` (`GraphicsDeviceType.Metal`, formats) |
| `src/OniMoono.Desktop/Metal/MetalWorker.cs` | Render thread (§9) |
| `src/OniMoono.Desktop/Metal/MetalPrograms.cs`, `MetalPipelines.cs` | Libraries, functions, pipeline and depth-stencil caches (§5) |
| `src/OniMoono.Desktop/Metal/MetalConstantBuffers.cs` | Packer (done, §2.3) |
| `src/OniMoono.Desktop/Metal/MetalStreamBuffer.cs`, `MetalTextures.cs`, `MetalMeshes.cs` | Ring, textures, render targets, meshes (§7, §8) |
| `src/OniMoono.Desktop/Metal/MetalSurface.cs` | Layer, drawables, present (§9) |
| `src/OniMoono.UnityEngine/Engine/Rendering/Device/*` | The neutral pieces (§3) |

Size: as the study estimates, about 4000–5000 lines for the device, 1000–2000 of interop, and
about 1000 moved out of `Gl/`.

---

## 11. Test plan for when a Mac exists

An Apple Silicon Mac (the primary arm64 build runs there natively, and the fallback under
Rosetta), with the game installed by Steam; an Intel Mac as a second check of the fallback.

1. **Platform port** (no Metal), arm64 first: `scripts/check.sh` on the Mac (unit tests natively,
   `--check-natives` loading nothing, `--check-assets`). Then `NativeLibrary.Load` of the port from
   `plugins/` and of fmodstudio and libsteam_api from the bundle (a host `--check-natives --load`
   variant), and a Harmony patch applied and called (study R11). Then the headless player
   (`--headless --frames N`) loading a save with onimaxxing-sim, its log naming the port's
   SYSINFO banner. Then the x86_64 build with Klei's SimDLL under Rosetta, which shakes out the
   CoreCLR-under-Rosetta hangs (study §3.1): 200 launches.
2. **Interop smoke** (`MetalSmokeTests`, skipped off macOS): device, queue, a trivial MSL library,
   an offscreen triangle read back; struct sizes against the real framework.
3. **Shaders**: the Metal `--check-shaders` over all 945 sources and both constant values; then
   the **GPU oracle for the packer**: build each program's pipeline with reflection
   (`MTLPipelineOptionArgumentInfo`/`BufferTypeInfo`) and compare every buffer member's offset and
   type with the blob and `MslStructs` (replacing the Linux-side MSL struct reader as oracle).
4. **Conventions**: a fixed scene of quads with a known pattern drawn through `Hidden/BlitCopy`,
   `Klei/PostFX/FullScreen` and an MRT pass into textures and the back buffer, read back and
   compared with the Linux GL device's output (golden PNGs from Linux).
5. **Game frames**: main menu and a colony captured (`--capture`) and compared loosely with the
   Linux player; `--dump-render-textures` pass by pass where they differ; Klei's own Mac player as
   the authority for anything the Linux player cannot answer.
6. **e2e**: the driver's lock and lane scheme on the Mac host; the screenshot suites and UI dumps
   against Linux runs.
7. **Performance**: frame time on the large colony against Klei's Mac player and the Linux
   player (`--frame-stats`, Metal call counters, GPU time); compile and pipeline times at load
   (study R4).

---

## 12. Open questions, and how each would be settled

| # | Question | How to settle |
|---|---|---|
| Q1 | Which `MTLLanguageVersion` the sources need; what the payload header's other fields (7, the flags word's bit 0 and 2) mean | Compile all 945 sources at each version on a Mac (`--check-shaders`); compare warnings. The fields may stay unread if every source compiles. |
| Q2 | MRT with a null colour slot: does Metal drop writes to an `Invalid` attachment, or must `rp_output_remap_mask` remap them? | Validation layer run of an MRT draw with a gap; read back each attachment. |
| Q3 | Does every Klei pass compose upright under §6's rules (FullScreen's UV, light buffer, MRT copies)? | Convention tests (§11.4), then pass-by-pass render-texture dumps against Linux and Klei's Mac player. |
| Q4 | Constant-buffer offset alignment on the target GPUs (256 assumed) | Validation layer with 16-byte alignment on Apple Silicon; keep 256 if Intel Macs are supported. |
| Q5 | `has_base_vertex_instance`: which draws pass a base instance | Trace instanced draws (`Graphics.DrawMeshInstanced`, procedural particles) in the GL device; set per draw. |
| Q6 | Metal call overhead under Rosetta, and whether the per-draw call count needs argument buffers | Frame counters and Instruments on the large colony. |
| Q7 | Unity's Mac `persistentDataPath` form for this game (bundle identifier vs company/product) and the log path | Where Klei's Mac player writes `save_files` and `Player.log` on a Mac with saves. |
| Q8 | What Steam's `%command%` passes on macOS (the `.app` or its executable) | A launch option running a script that logs its arguments. |
| Q9 | Whether FMOD finds `resonanceaudio`/`fmod_haptics` from FMODUnity's Mac plugin path with `dataPath = Contents` | Run with FMOD's logging on; FMODUnity's `Platform` code (game code we may read) names the folder. |
| Q10 | Klei-signed dylibs loaded by an ad-hoc-signed arm64 process (and an unsigned or ad-hoc-signed x86_64 one), and the zig-linked port once ad-hoc signed (library validation off without the hardened runtime) | The headless runs of §11.1. |
| Q11 | `CAMetalLayer` on a GLFW `NO_API` window: does GLFW's Cocoa view keep our layer across full-screen toggles and monitor changes? | Toggle full screen and move between Retina and non-Retina monitors with the layer's scale logged. |
| Q12 | `MTLBinaryArchive` under Rosetta: are archives keyed so x86_64 processes reuse them? | Second-run load times with and without the archive. |
| Q13 | Mod shaders in pre-2021.2 bundles carrying Metal programs (legacy keyword lists) | Survey the Workshop catalog's bundles (`out/ws`) for platform 14 programs; the legacy reader handles GLCore only today. |
| Q14 | BRUTAL's osx-x64 natives (glfw, freetype/png/zlib, stb) for the fallback: built by us or upstream (the arm64 build has them) | Study §3.2 options; `--check-natives --native-arch x86_64 --native-dir` will list them as found once a package carries them. |
| Q15 | Harmony 2.4.2 on osx-arm64 CoreCLR (arm64 detours, W^X): the host's patches, the game tweaks, mods | §11.1's Harmony step, then the headless player with `--game-tweak all`. |
| Q16 | onimaxxing-sim on macOS: does it run a colony as on Linux, where does its `oni-ffi.log` go (it reads `$TEMP`/`$TMP`, not `$TMPDIR`), and how fast is it on Apple Silicon against Klei's under Rosetta | The headless run of §11.1; §11.7's frame times. |
