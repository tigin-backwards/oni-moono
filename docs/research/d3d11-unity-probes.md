# The original's Direct3D 11 player compared with its GLCore player

Milestone M0, part A of the Direct3D 11 plan ([d3d11-plan.md](d3d11-plan.md)). Every value
below was measured on the original game, except the lines marked as cited from a public source.
No Unity engine source was read. Anything not measured or cited is marked **UNVERIFIED**.

## How it was measured

- **Build.** The frozen U60 Windows build 764025 (`E:\oni-builds\BrutalNotIncluded-764025`),
  Unity 6000.3.17f1, run by `scripts/original-windows.sh`. Each run had its own run home, product
  name, registry key and LocalLow folder, all moved into the run home afterwards. Window
  1024×768 on monitor 2, volume 0, offline Steam double.
- **Renderers.** One run each through runq (`windows` slot, level burndown):
  - default (Direct3D 11): job j0008120;
  - `-force-glcore`: job j0008121.
- **Machine.** NVIDIA GeForce RTX 3090; driver 610.88 (from the GL version string).
- **The probe.** `tools/UnityProbe` with `PROBE_SCENARIO=d3d11` (`D3d11Probe.cs`,
  `D3d11Native.cs`). It starts 10 s into the main menu and takes under a second. Both runs
  reached `DX done` with no exception, timeout or missing API.
- **Raw output.** The `DX` lines are in [d3d11/unity-probes-d3d11.txt](d3d11/unity-probes-d3d11.txt)
  and [d3d11/unity-probes-glcore.txt](d3d11/unity-probes-glcore.txt). Every value in this
  document can be found there by its tag (`sys`, `gpm`, `rtgf`, `rows`, `nat capture` and so on).

### How the built-in shader values were read (item 2)

The game ships no shader that writes `_ProjectionParams`, `_ZBufferParams`, `_ScreenParams` or
`unity_OrthoParams` to its output. Building a probe shader into an asset bundle needs the Unity
Editor. So the probe reads the values from the program's own inputs on the render thread,
straight after a draw:

1. A camera (perspective, then orthographic; near 0.5, far 200, fov 50, ortho size 3, at
   (1, 2, −10)) draws a quad with four game shaders. One run draws into a 64×32 render texture
   (`Camera.Render`); the other draws to the screen for one frame. Each draw is a
   `CommandBuffer.DrawMesh` at `CameraEvent.AfterEverything`, followed by
   `CommandBuffer.IssuePluginEvent` with a managed callback.
   - The callback runs on the render thread. Unity documents IssuePluginEvent's callback as
     called from the render thread (Unity 6000.0 `GL.IssuePluginEvent`).
2. **On Direct3D 11.** The callback gets the device from a texture's `GetNativeTexturePtr`, which
   is documented as an `ID3D11Resource` (Unity 6000.0 `Texture.GetNativeTexturePtr`). It then:
   - gets the immediate context;
   - reads `VSGetConstantBuffers1` / `PSGetConstantBuffers1` for slots 0–13;
   - copies each bound buffer into a staging buffer that starts filled with 0xCD, then maps and
     reads it.
3. **On GLCore.** The callback lists the active uniforms of `GL_CURRENT_PROGRAM` and reads each
   with `glGetUniformfv`.
4. **Which bytes to read.** Each value comes from the slot and offset that the shader's D3D11
   parameter blob gives it in the survey's `params-all.txt`:

   | Shader | Value | Slot and offset |
   |---|---|---|
   | `Legacy Shaders/Particles/Additive [SOFTPARTICLES_ON]` | `_ProjectionParams` | VS b1 @80 |
   | | `_ZBufferParams` | PS b1 @112 |
   | | `unity_MatrixV` | VS b3 @144 |
   | | `unity_MatrixVP` | VS b3 @272 |
   | `Hidden/Internal-CombineDepthNormals` | `_ZBufferParams` | PS b0 @112 |
   | `Klei/Liquid [_SAMPLEFILTERING_NONE]` | `_ScreenParams` | PS b1 @96 |
   | | `unity_OrthoParams` | PS b1 @128 |
   | `Hidden/Internal-Colored` | `unity_MatrixVP` | VS b2 @272 |

   - Slots a program does not declare hold an earlier draw's buffers. For example, the
     Internal-Colored capture shows a b1 with the menu camera's `_ScreenParams` 1024×768. Those
     slots are ignored.
- **Vtable slots and GUIDs** are from the Windows SDK 10.0.26100.0 headers `d3d11.h` and
  `d3d11_1.h`. DXGI format numbers are from `dxgiformat.h`. GL enum values are from the Khronos
  registry's `gl.xml`.

## 1. SystemInfo

| Property | Direct3D 11 | GLCore |
|---|---|---|
| graphicsDeviceType | Direct3D11 | OpenGLCore |
| graphicsDeviceVersion (exact) | `Direct3D 11.0 [level 11.1]` | `OpenGL 4.5.0 NVIDIA 610.88` |
| graphicsDeviceName | `NVIDIA GeForce RTX 3090` | `NVIDIA GeForce RTX 3090/PCIe/SSE2` |
| graphicsDeviceVendor | `NVIDIA` | `NVIDIA Corporation` |
| graphicsDeviceID / VendorID | 0x2204 / 0x10de | 0x2204 / 0x10de |
| graphicsShaderLevel | 50 | 50 |
| graphicsMemorySize | 24322 | 24322 |
| graphicsMultiThreaded | False | False |
| renderingThreadingMode | LegacyJobified | LegacyJobified |
| usesReversedZBuffer | **True** | **False** |
| graphicsUVStartsAtTop | **True** | **False** |
| maxTextureSize / maxCubemapSize | 16384 / 16384 | 32768 / 32768 |
| maxTexture3DSize / maxTextureArraySlices | 2048 / 2048 | 16384 / 2048 |
| copyTextureSupport | Basic, Copy3D, DifferentTypes, TextureToRT, RTToTexture (31) | same (31) |
| supportsComputeShaders / npotSupport | True / Full | True / Full |
| supportedRenderTargetCount | 8 | 8 |
| maxComputeBufferInputs (compute / other stages) | 32 / 128 | 16 / 16 |
| supportsCompressed3DTextures | True | False |
| supportsGpuRecorder | True | False |
| supportsMultisampleResolveDepth | False | True |
| hdrDisplaySupportFlags | Supported, RuntimeSwitchable, AutomaticTonemapping | None |
| GetGraphicsFormat(DefaultFormat.LDR / HDR) | R8G8B8A8_UNorm / R16G16B16A16_SFloat | same |
| GetGraphicsFormat(DefaultFormat.DepthStencil) | **D32_SFloat_S8_UInt** | **D24_UNorm_S8_UInt** |
| GetGraphicsFormat(DefaultFormat.Shadow / Video) | D16_UNorm / YUV2 | same |

- **Other SystemInfo properties.** The probe lists every static SystemInfo property (`sysall`
  lines). All the ones not in the table are equal on both renderers.
- **Not SystemInfo.** `gfx-threading-mode=6` comes from the game's `boot.config`.

**SupportsTextureFormat.** 64 values were queried.
- **Direct3D 11 only:** YUY2.
- **GLCore only:** ETC_RGB4, EAC_R, EAC_R_SIGNED, EAC_RG, EAC_RG_SIGNED, ETC2_RGB, ETC2_RGBA1,
  ETC2_RGBA8, ETC_RGB4Crunched and ETC2_RGBA8Crunched.
- **Neither:** all PVRTC and ASTC formats.

**SupportsRenderTextureFormat.** All 28 values are True on both.

**IsFormatSupported(GraphicsFormat, GraphicsFormatUsage).** 147 formats were queried, each with
every usage flag and with `GetCompatibleFormat(Sample/Render)`. 66 of them differ. The main
groups:

| Formats | Direct3D 11 | GLCore |
|---|---|---|
| 3-component R8G8B8_*, B8G8R8_* | no usage at all; the compatible format is the 4-component one (R8G8B8_UNorm → R8G8B8A8_UNorm) | Sample/Render/ReadPixels (UNorm, SRGB, SNorm) |
| 3-component 16- and 32-bit (R16G16B16_*, R32G32B32_*) | none, except R32G32B32_SFloat, which has Sample only; Render → the 4-component format | Render, and Sample for the float/norm ones |
| B8G8R8A8_UNorm / _SRGB | Render, Blend, MSAA, LoadStore (UNorm), Sparse | Sample and pixel access only; Render → R8G8B8A8 |
| R4G4B4A4, R5G6B5, R5G5B5A1, A1R5G5B5 | none (compatible: B-ordered or RGBA8) | Render, MSAA (R4G4B4A4 and R5G6B5 also Sample) |
| B4G4R4A4 | Sample only; Render → R8G8B8A8_UNorm | Render only; Sample → R4G4B4A4 |
| D24_UNorm, S8_UInt | none (D24_UNorm → D32_SFloat; S8 → D24_UNorm_S8_UInt) | Render, Blend, MSAA |
| D16, D24S8, D32, D32S8 | Render, MSAA | Render, Blend, MSAA |
| BC1–BC7 | Sample, Linear, GetPixels, **Sparse** | the same without Sparse |
| ETC / ETC2 / EAC | none | Sample, Linear |
| R8_UInt | adds StencilSampling | no StencilSampling |
| YUV2 | Sample, Linear | none |

The full lists are the `gfmt` lines of the two raw files.

## 2. Projection and built-in shader values

### GL.GetGPUProjectionMatrix

**Direct3D 11.**
- `renderIntoTexture` false: row 2 becomes `0.5·row3 − 0.5·row2`. This maps clip z to near = 1,
  far = 0.
- `renderIntoTexture` true: row 1 is negated as well. Its zeros become −0.

Matrices are `[row0] [row1] [row2] [row3]`:

| Input | false | true |
|---|---|---|
| Perspective(60, 1.5, 0.3, 1000) | `[1.1547,0,0,0] [0,1.73205,0,0] [0,0,0.00030005,0.30009] [0,0,-1,0]` | `[1.1547,0,0,0] [0,-1.73205,0,0] [0,0,0.00030005,0.30009] [0,0,-1,0]` |
| Ortho(−8, 8, −6, 6, 0.3, 1000) | `[0.125,0,0,0] [0,0.16667,0,0] [0,0,0.0010003,1.0003] [0,0,0,1]` | row 1 `[0,-0.16667,0,0]` |
| Ortho(0, 1024, 0, 768, −1, 1) | `[0.00195,0,0,-1] [0,0.0026,0,-1] [0,0,0.5,0.5] [0,0,0,1]` | row 1 `[0,-0.0026,0,1]` |
| identity | `[1,0,0,0] [0,1,0,0] [0,0,-0.5,0.5] [0,0,0,1]` | row 1 `[0,-1,0,0]` |

- **Bit check.** All six cases match both `0.5f·p3 − 0.5f·p2` and `0.5f·(p3 − p2)` in float32,
  bit for bit apart from the sign of zeros. These inputs cannot tell the two forms apart.
- **Comparison with our devices.** It is the formula our `RenderConventions.GpuProjection`
  already uses for Vulkan.

**GLCore.** Both flags return the input unchanged.

### What the programs see

All 16 captures, for each renderer:

| Value | D3D11, render texture | D3D11, screen | GLCore, render texture | GLCore, screen |
|---|---|---|---|---|
| `_ProjectionParams` | **(−1, 0.5, 200, 0.005)** | (1, 0.5, 200, 0.005) | (1, 0.5, 200, 0.005) | (1, 0.5, 200, 0.005) |
| `_ZBufferParams` | (399, 1, 1.995, 0.005) | (399, 1, 1.995, 0.005) | (−399, 400, −1.995, 2) | (−399, 400, −1.995, 2) |
| `_ScreenParams` | (64, 32, 1.015625, 1.03125) | (1024, 768, 1.000977, 1.001302) | (64, 32, 1.015625, 1.03125) | (1024, 768, 1.000977, 1.001302) |
| `unity_OrthoParams`, perspective | (6, 3, 0, 0) | (4, 3, 0, 0) | (6, 3, 0, 0) | (4, 3, 0, 0) |
| `unity_OrthoParams`, ortho | (6, 3, 0, 1) | (4, 3, 0, 1) | (6, 3, 0, 1) | (4, 3, 0, 1) |
| `unity_MatrixVP` y scale (perspective) | −2.1445 | +2.1445 | +2.1445 (column 0 only read, see UNVERIFIED) | |

- **`_ZBufferParams`.**
  - Direct3D 11 has the reversed form: x = −1 + far/near, y = 1, z = x/far, w = 1/far.
  - GLCore has x = 1 − far/near, y = far/near, z = x/far, w = y/far.
  - Both are the same in perspective and ortho, and in render textures and on screen.
- **`unity_OrthoParams`.** x and y are the ortho half-width (size × aspect) and half-height
  (size) even for a perspective camera; w is 1 for ortho. They are the same on both renderers.
- **`_ProjectionParams.x`.**
  - Direct3D 11: −1 in a render texture and +1 on screen.
  - GLCore: +1 in both.

**Matrix layout on Direct3D 11.**
- **Storage.** The matrices are stored one column per float4, column-major. For example,
  `unity_MatrixVP` at @272 in the render-texture case is c0 (1.072, 0, 0, 0), c1 (0, −2.1445, 0, 0),
  c2 (0, 0, −0.0025063, 1), c3 (−1.072, 4.289, 0.47619, 10).
- **Contents.** That equals `GetGPUProjectionMatrix(proj, true) × worldToCameraMatrix`. On screen
  it equals `GetGPUProjectionMatrix(proj, false) × worldToCameraMatrix`.
- **`unity_MatrixV`** equals worldToCameraMatrix.

**Constant buffers on Direct3D 11.**
- Every bound buffer is its own `ID3D11Buffer`. Each has the exact cbuffer size (48, 144, 176,
  368, …), usage DYNAMIC, bind CONSTANT_BUFFER and CPU write.
- Each is bound whole: `firstConstant` 0, `numConstants` 4096, the default range.
- Only the members a program declares hold values. The rest are 0. For example, the Additive VS
  b3 holds `unity_MatrixV` and `unity_MatrixVP` and zeros elsewhere.

**Uniforms on GLCore.**
- The program is a linked program (`GL_CURRENT_PROGRAM` set, no pipeline object).
- Every built-in is a loose uniform (block −1). There are no uniform blocks.
- Matrices are `vec4[4]` arrays named `hlslcc_mtx4x4<name>`.

**`Shader.GetGlobalVector`.** For any of these names it returns the main menu camera's values
(near 0.3, far 1000), not the probe camera's. It is not a view of what a program sees.
- D3D11 `_ZBufferParams` = (3332.33, 1, 3.332, 0.001).
- GLCore = (−3332.33, 3333.33, −3.332, 3.333).

### Targets and viewport during a camera's draw (Direct3D 11)

| | Render texture 64×32 (depth 24) | Screen |
|---|---|---|
| RTV | R8G8B8A8_UNORM (28), TEXTURE2D | R8G8B8A8_UNORM (28), TEXTURE2D |
| Colour resource | 64×32 R8G8B8A8_TYPELESS (27), bind SRV\|RTV (0x28) | 1024×768 R8G8B8A8_TYPELESS (27), bind SRV\|RTV (0x28) |
| DSV | D32_FLOAT_S8X24_UINT (20) | D32_FLOAT_S8X24_UINT (20) |
| Depth resource | R32G8X24_TYPELESS (19), bind SRV\|DSV (0x48) | R32G8X24_TYPELESS (19), bind DSV only (0x40) |
| Viewport | (0, 0, 64, 32), depth 0..1 | (0, 0, 1024, 768), depth 0..1 |

**GLCore:**
- a render texture draws to FBO 4, viewport (0, 0, 64, 32);
- the screen draws to framebuffer 0, viewport (0, 0, 1024, 768).

## 3. Render textures

**`RenderTexture(4, 4, depth, ARGB32)`.** The colour format is R8G8B8A8_UNorm in every case.

| Requested depth | D3D11: depthStencilFormat (reported depth) | GLCore: depthStencilFormat (reported depth) |
|---|---|---|
| 0 | None (0) | None (0) |
| 16 | D16_UNorm (16) | D16_UNorm (16) |
| 24 | **D32_SFloat_S8_UInt (32)** | **D24_UNorm_S8_UInt (24)** |
| 32 | D32_SFloat_S8_UInt (32) | D32_SFloat_S8_UInt (32) |

`RenderTextureDescriptor` gives the same formats.

**`graphicsFormat` of each RenderTextureFormat.** It is the same on both renderers except:

| RenderTextureFormat | D3D11 | GLCore |
|---|---|---|
| ARGB4444 | R8G8B8A8_UNorm | B4G4R4A4_UNormPack16 |
| BGRA32 | B8G8R8A8_UNorm | R8G8B8A8_UNorm |

The rest are the same on both:
- ARGB32 / Default → R8G8B8A8_UNorm.
- ARGBHalf / DefaultHDR → R16G16B16A16_SFloat.
- Depth / Shadowmap → None, with D16_UNorm when the depth requested is 0.
- RGB565 → B5G6R5_UNormPack16.
- ARGB1555 → B5G5R5A1_UNormPack16.
- ARGB2101010 → A2B10G10R10_UNormPack32.
- ARGB64 → R16G16B16A16_UNorm.
- ARGBFloat → R32G32B32A32_SFloat.
- RGFloat → R32G32_SFloat.
- RGHalf → R16G16_SFloat.
- RFloat → R32_SFloat.
- RHalf → R16_SFloat.
- R8 → R8_UNorm.
- ARGBInt / RGInt / RInt → R32G32B32A32_SInt / R32G32_SInt / R32_SInt.
- RGB111110Float → B10G11R11_UFloatPack32.
- RG32 → R16G16_UNorm.
- RGBAUShort → R16G16B16A16_UInt.
- RG16 → R8G8_UNorm.
- BGRA10101010_XR and BGR101010_XR → R16G16B16A16_UNorm. `GraphicsFormatUtility` gives the XR
  formats for them.
- R16 → R16_UNorm.
- The sRGB and Linear read-write variants are equal to the default (the game is in Gamma).

**`Texture2D.graphicsFormat`.** It is the same on both renderers:
- RGB24 → R8G8B8_UNorm;
- R8 → R8_UNorm;
- RGBA32 → R8G8B8A8_UNorm;
- BGRA32 → B8G8R8A8_UNorm;
- RGBA4444 → R4G4B4A4_UNormPack16;
- ARGB4444 → B4G4R4A4_UNormPack16;
- RGB565 → B5G6R5_UNormPack16;
- DXT1 / DXT5 / BC7 → RGBA_DXT1_UNorm / RGBA_DXT5_UNorm / RGBA_BC7_UNorm;
- Alpha8 → GraphicsFormat 54 and ARGB32 → 88. These are numeric because the build's managed
  GraphicsFormat enum has no name for those values.

### Orientation

These results are identical on the two renderers. The pattern texel (x, y) is
(x·60+10, y·60+10, 77). Reads are listed as `x.y` in index order y·4+x, so row 0 is Unity's
bottom row.

| Operation | Result (both renderers) |
|---|---|
| GetPixels32 of the source | identity |
| Blit tex→RT, ReadPixels(0,0,4,4) | identity (row 0 read = texel row 0) |
| ReadPixels rect (1, 2, 2, 2) | texels 1.2 2.2 / 1.3 2.3: the rect origin is bottom-left |
| Blit RT→RT, CopyTexture tex→RT, CopyTexture RT→RT | identity |
| CopyTexture region src (0, 0, 2×1) → dst (1, 2) | texels 0.0 and 1.0 land at pixels (1, 2) and (2, 2): bottom-left origin |
| GL quad over pixel (0, 0) (LoadPixelMatrix), into an RT, ReadPixels | red at index 0 (bottom-left) |
| The same quad on the back buffer at WaitForEndOfFrame, ReadPixels(0,0,4,4) | red at index 0; ReadPixels(0, h−4, 4, 4) is black |
| ScreenCapture.CaptureScreenshotAsTexture | pixel 0 red, so row 0 is the bottom |
| Blit with scale (1, −1), offset (0, 1) | rows reversed |
| AsyncGPUReadback of the RT | identity: the data starts at the bottom row |
| texelSize of a Texture2D and of an RT | (0.25, 0.25): no negative y |

**Culling.** It is identical on both renderers, in an RT and on the back buffer. The test
triangles are wound in Unity's y-up pixel space.
- `Cull Back` (2) keeps clockwise and culls counter-clockwise.
- `Cull Front` (1) does the reverse.

**Depth.** `Hidden/Internal-Colored` with ZTest LEqual: red at z 0.5, then green at z −0.5
(left half) and blue at z 0.9 (right half). The read gives red on the left and blue on the right
on both renderers: z is eye-space depth through LoadPixelMatrix's ortho.

**Stored depth after `GL.Clear(depth = v)`.** Read by sampling a `RenderTextureFormat.Depth`
target (24 bits requested) through Blit into ARGBFloat:

| v | D3D11 (D32_SFloat_S8_UInt) | GLCore (D24_UNorm_S8_UInt) |
|---|---|---|
| 1 | 0 | 1 |
| 0.25 | 0.75 | 0.25000003 |
| 0 | 1 | 0 |

So Direct3D 11 stores 1 − v (reversed Z), and the sampled value is the raw stored depth.

## 4. Texture uploads

These results are identical on the two renderers.

**Apply(updateMipmaps).** An 8×8 RGBA32 texture with mips (mipmapCount 4); mip 1's CPU data was
set to 9,9,9,9 before Apply. GPU mip 1 was read through `CopyTexture(tex, 0, 1, rt, 0, 0)`.
- **`Apply(true)`.** CPU and GPU mip 1 are the 2×2 box average of mip 0 (16, 16, 100, 255 …).
  This mip-0 checkerboard averages exactly, so the filter's rounding is UNVERIFIED.
- **`Apply(false)`.** CPU and GPU mip 1 keep 9,9,9,9: the CPU mip is uploaded as set.

**LoadRawTextureData, 4×4.** Sampled through Blit into ARGB32 and ReadPixels, and decoded by
GetPixels32:

| Format | Data | Sampled (Blit) | GetPixels32 |
|---|---|---|---|
| RGB24 | byte i·3.. = (i·16, 255−i·16, i·3+1) | (r, g, b, 255), data byte 0 at pixel 0 (bottom-left) | same |
| Alpha8 | byte i = i·16+5 | (0, 0, 0, a) | (0, 0, 0, a) |
| R8 | byte i = i·16+5 | **(r, 0, 0, 255)** | **(r, 255, 255, 255)** |
| DXT1 | c0 red, c1 blue, memory row k = index k | memory row 0 → Unity row y = 0 (bottom). Row 2 is (170, 0, 85), row 3 (85, 0, 170) | same |
| DXT5 | alpha a0 = 255, a1 = 0, alpha row k = index k | alpha rows 255, 0, 219, 183 | alpha rows 255, 0, 219, **182** |
| BC4 | as the DXT5 alpha | red rows 255, 0, 219, 184 | 255, 0, 219, **182** |
| BC5 | red as BC4, green g0 = 0, g1 = 255 | green rows 0, 255, 48, 95 | 0, 255, **51, 102** |
| BC7 | mode 6, black→white, texel i = index i | 1, 17, 37, 53, 68, … 255, in memory order from pixel 0 | same |

- **Row order.** Raw data row 0 is Unity's bottom row, and BC block rows run upward in the same
  way.
- **Swizzle.** R8 and Alpha8 sample the same on both renderers. On GLCore an Alpha8 texture is
  stored as GL_R8 (section 5), so that renderer swizzles it.
- **CPU decode.** GetPixels32's BC4/BC5/DXT5 decode is the CPU's. It differs from the GPU's
  interpolation (182 against 183/184, 51 against 48).

**GetPixels after rendering.**
- **`ReadPixels` without `Apply`.** `GetPixels32` returns the read pixels: the CPU copy is
  updated at once.
- **`CopyTexture(rt → tex)`.** It changes only the GPU copy. `GetPixels32` still returns the old
  CPU data (1, 2, 3, 4), while a Blit of the texture shows the copied pattern.

## 5. GetNativeTexturePtr

### Direct3D 11

The pointer is the `ID3D11Texture2D` itself.
- QueryInterface for ID3D11Resource and ID3D11Texture2D returns the same pointer.
- QueryInterface for SRV, RTV, DSV and Buffer returns E_NOINTERFACE (0x80004002).
- IUnknown returns a different address. COM's identity pointer is allowed to differ.

`D3D11_TEXTURE2D_DESC` of each:

| Texture | DXGI format | Bind | Notes |
|---|---|---|---|
| Texture2D RGBA32, ARGB32, RGB24, RGBA4444 | R8G8B8A8_UNORM (28) | SRV (0x8) | RGB24 and RGBA4444 are expanded to RGBA8 |
| Texture2D BGRA32 | B8G8R8A8_UNORM (87) | SRV | |
| Texture2D Alpha8 | A8_UNORM (65) | SRV | |
| Texture2D R8 | R8_UNORM (61) | SRV | |
| Texture2D RGB565 | B5G6R5_UNORM (85) | SRV | |
| Texture2D RHalf / RGBAHalf / RFloat | R16_FLOAT (54) / R16G16B16A16_FLOAT (10) / R32_FLOAT (41) | SRV | |
| Texture2D DXT1 / DXT5 / BC7 | BC1_UNORM (71) / BC3_UNORM (77) / BC7_UNORM (98) | SRV | |
| RT ARGB32 (any depth), RGB565 | R8G8B8A8_TYPELESS (27); RGB565 is B5G6R5_UNORM (85) | SRV\|RTV (0x28) | the depth is a separate resource |
| RT RHalf / ARGBHalf / ARGBFloat / RFloat / R8 | R16_TYPELESS (53) / R16G16B16A16_TYPELESS (9) / R32G32B32A32_TYPELESS (1) / R32_TYPELESS (39) / R8_TYPELESS (60) | SRV\|RTV | |
| RT Depth, depth 24 | R32G8X24_TYPELESS (19) | SRV\|DSV (0x48) | |
| RT Shadowmap, depth 24 | R32_TYPELESS (39) | SRV\|DSV | no stencil |
| RT ARGB32, MSAA 4 | R8G8B8A8_TYPELESS, **1 sample** | SRV\|RTV | the pointer is the resolve texture |

**Flags.** All have usage DEFAULT (0), CPU access 0 and misc 0. The 8×8 texture with mips has
MipLevels 4.

**`GetNativeDepthBufferPtr` and the RenderBuffer pointers.**
- The probe logged these but did not call into them, because their native types are not
  documented. Unity's page says only "pointer to an underlying graphics API depth buffer
  resource".
- For a colour RT without depth, `GetNativeDepthBufferPtr` equals `GetNativeTexturePtr`.
- For an RT with depth it is a different pointer. What type it is stays UNVERIFIED.

**Device, from the texture's `GetDevice`:**
- feature level **0xb100 (11_1)**;
- creation flags **0x20 (BGRA_SUPPORT)** only: no DEBUG and no SINGLETHREADED;
- `D3D11_FEATURE_THREADING`: DriverConcurrentCreates 1 and DriverCommandLists 1 (NVIDIA);
- the immediate context answers QueryInterface for ID3D11DeviceContext1.

### GLCore

The pointer is the GL texture name (small integers: 2716 and up), and `glIsTexture` is true. The
internal format of each, from `glGetTextureLevelParameteriv`:
- RGBA32, ARGB32, BGRA32 and RT ARGB32 → GL_RGBA8;
- RGB24 → **GL_RGB8**;
- Alpha8 and R8 → **GL_R8**;
- RGB565 → GL_RGB565;
- RGBA4444 → GL_RGBA4;
- RHalf → GL_R16F;
- RGBAHalf → GL_RGBA16F;
- RFloat → GL_R32F;
- ARGBFloat → GL_RGBA32F;
- DXT1 → COMPRESSED_RGBA_S3TC_DXT1;
- DXT5 → COMPRESSED_RGBA_S3TC_DXT5;
- BC7 → COMPRESSED_RGBA_BPTC_UNORM;
- RT Depth (24) → GL_DEPTH24_STENCIL8;
- RT Shadowmap (24) → GL_DEPTH_COMPONENT24;
- RT MSAA 4 → a single-sample GL_RGBA8 texture, again the resolve target.

## Key differences, Direct3D 11 against GLCore

1. **Depth.** Direct3D 11 uses reversed Z.
   - `usesReversedZBuffer` is True.
   - `GetGPUProjectionMatrix` maps near to 1 and far to 0.
   - `_ZBufferParams` is (−1 + f/n, 1, …).
   - A clear to v stores 1 − v.
   - GLCore keeps OpenGL's −1..1 clip z and `_ZBufferParams` (1 − f/n, f/n, …).
2. **Render textures are flipped only on Direct3D 11.** `GetGPUProjectionMatrix(…, true)` and
   the VP a program gets negate y, and `_ProjectionParams.x` is −1. On screen, both renderers
   use +1 and no flip.
3. **UV origin.** `graphicsUVStartsAtTop` is True on Direct3D 11 and False on GLCore.
4. **Depth formats.** 24 bits becomes **D32_SFloat_S8_UInt** on Direct3D 11 (DXGI
   R32G8X24_TYPELESS) and D24_UNorm_S8_UInt on GLCore. `DefaultFormat.DepthStencil` follows.
   D24_UNorm and S8 alone are unsupported on Direct3D 11.
5. **Texture and RT storage.**
   - RT colour textures are TYPELESS resources with SRV|RTV bind; depth RTs are TYPELESS with
     SRV|DSV.
   - RGB24 and RGBA4444 are expanded to RGBA8.
   - Alpha8 is DXGI A8_UNORM; GLCore uses R8.
   - ARGB4444 RTs become RGBA8.
   - BGRA32 RTs stay BGRA8 (GLCore makes them RGBA8).
   - Three-component formats are unsupported.
6. **Capability limits** differ: texture sizes, compute buffer inputs, compressed 3D,
   MSAA depth resolve and HDR display flags (section 1 table).
7. **Equal on both renderers:**
   - every row order and orientation that scripts see (ReadPixels, Blit, CopyTexture,
     AsyncGPUReadback, screenshots, raw and BC data rows);
   - culling as Unity defines it;
   - `_ScreenParams` and `unity_OrthoParams`;
   - shader level 50, copyTextureSupport 31 and 8 render targets.

## Tooling on the Windows machine (read-only checks)

- **D3D11 debug layer: not available.**
  - `C:\Windows\System32` has no `d3d11_3SDKLayers.dll`, `D3D11SDKLayers.dll`,
    `D3D11_1SDKLayers.dll` or `dxgidebug.dll`. It does have `d3d10sdklayers.dll` and
    `d3dref9.dll`.
  - Microsoft documents that, on Windows 10 and later, the debug layer comes with the "Graphics
    Tools" optional feature (Learn, "Software layers", cited in
    [d3d11/bindings-conventions.md](d3d11/bindings-conventions.md) C.9).
  - The missing DLLs suggest it is not installed. `Get-WindowsCapability` needs elevation, so the
    feature's state itself is UNVERIFIED.
- **RenderDoc 1.43** is installed at `C:\Program Files\RenderDoc` (`renderdoc.dll` version
  1.43.0.0, `qrenderdoc.exe`, `renderdoccmd.exe`). It is registered under the installed programs
  as "RenderDoc".
- **Windows SDK 10.0.26100.0** is installed with `fxc.exe`, `dxc.exe` and the D3D11 headers. It
  has no SDK layers DLL.
- Nothing was installed or changed.

## UNVERIFIED

1. **What `GetNativeDepthBufferPtr` and `RenderBuffer.GetNativeRenderBufferPtr` point to on
   Direct3D 11.** They were not dereferenced.
2. **Whether the "screen" camera's target is the swap chain's buffer or an intermediate
   texture.** It is a 1024×768 R8G8B8A8_TYPELESS texture with SRV|RTV bind. No DXGI query was
   made on it.
3. **GLCore's `unity_MatrixVP` y sign in a render texture.** Only element 0 of each
   `hlslcc_mtx4x4` array was read. `_ProjectionParams.x` = +1 and GetGPUProjectionMatrix being
   the identity point to no flip.
4. **The exact float form of GetGPUProjectionMatrix's z row** (`0.5·p3 − 0.5·p2` against
   `0.5·(p3 − p2)`), and the sign of the zeros it produces in row 2. The six inputs agree with
   both forms.
5. **The mipmap filter's rounding.** The test pattern averaged exactly.
6. **GPU-vendor dependence.** Everything here was measured on one NVIDIA GPU. The threading caps
   in particular are the driver's, and other vendors (the AMD iGPU) are not measured.
7. **That the values hold outside the main menu** (in a colony, with the game's own cameras). Only
   the probe's cameras were measured.
8. **The state of the Graphics Tools optional feature.** The query needs elevation.
