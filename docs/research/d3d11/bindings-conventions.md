# Direct3D 11 render device: bindings, Unity's D3D11 conventions, D3D11 design facts

Research for a Windows D3D11 render device that runs Unity's shipped D3D11 (DXBC) programs.
Date: 2026-11-22. Every claim carries a source: a URL, a file path, or a command run here.
**UNVERIFIED** marks what no source here confirms. Unity engine source was not read. Only
Unity's public manual and scripting reference were used.

---

## Part A: binding source

### A.0 What the repo does today

- `src/OniMoono.Desktop/Gl/Dxgi.cs` (350 lines) makes about 30 DXGI and D3D11 calls as
  `delegate* unmanaged<...>` calls through hand-numbered vtable slots, for example
  `Vtbl(context)[110]` for `ClearState` and `Vtbl(device)[39]` for `GetDeviceRemovedReason`.
  Its header comment says the slots, GUIDs, constants and struct layouts are "those of the
  Windows SDK 10.0.26100 headers (checked with offsetof on the vtables and structs, compiled
  with MSVC)".
- **No slot-checking tool is committed.** `git log -S offsetof -- src/OniMoono.Desktop/Gl/Dxgi.cs`
  finds only the commit that added the file, 26e934776 ("engine: present through a DXGI swap
  chain on Windows"). Its message says "Minimal COM calls through vtable slots, checked against
  the SDK headers; no package." The check was a one-off and nothing reruns it.
  - `tools/HdrCheck` is not a slot checker. It is an HDR luminance capture tool (`dupcap.c`,
    Desktop Duplication), built by `tools/HdrCheck/build.cmd` with `cl` (README.md there).
  - The only committed header check is `src/OniMoono.NativeHost/sdl_min_check.c`. It uses
    `_Static_assert(offsetof(ours, f) == offsetof(theirs, f))` against SDL's headers,
    "Compiled, never run, by scripts/build-native-host-linux.sh; a difference fails the build".
    That pattern would carry over to D3D11.
- **MSVC is available, and the build already uses it.**
  - `scripts/build-windows.sh` calls `scripts/build-native-host.sh`. That script stages sources
    in Windows `%TEMP%` and runs `src/OniMoono.NativeHost/build.cmd` through WSL's `cmd.exe`,
    with `vcvars64.bat` found by `vswhere`.
  - This machine has VS 2022 BuildTools with MSVC 14.44.35207
    (`/mnt/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Tools/MSVC`) and the
    Windows SDK 10.0.26100.0 headers
    (`/mnt/c/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/d3d11.h`, `d3d11_1.h`,
    `d3d11sdklayers.h`; `shared/dxgi1_6.h`).
- **Probe run here (Windows %TEMP%, deleted afterwards).** A C file with `#define CINTERFACE`
  that prints `offsetof(XVtbl, m)/sizeof(void*)` and some `sizeof`s, built with that `cl`:

  | Probe | Result |
  |---|---|
  | `ID3D11Device` slots | `CreateTexture2D` 5, `CheckFeatureSupport` 33, `GetDeviceRemovedReason` 39 |
  | `ID3D11DeviceContext` slots | `DrawIndexed` 12, `Map` 14, `CopySubresourceRegion` 46, `ClearState` 110, `Flush` 111, `FinishCommandList` 114 |
  | Vtable sizes | `ID3D11DeviceVtbl` 43 slots, `ID3D11DeviceContextVtbl` 115 slots |
  | Struct sizes | `D3D11_TEXTURE2D_DESC` 44, `D3D11_BUFFER_DESC` 24, `D3D11_SAMPLER_DESC` 52, `D3D11_BLEND_DESC` 264 |

  So Dxgi.cs's existing D3D11 slots (5, 39, 46, 110, 111) match the SDK, and a generated check
  for every slot and struct the new device uses is cheap.
- **Calling convention.** Counting return types of the `virtual ... STDMETHODCALLTYPE` methods
  in `d3d11.h` gives 82 HRESULT, 177 void, 7 UINT, 5 APP_DEPRECATED_HRESULT, and one each of
  D3D_FEATURE_LEVEL, D3D11_DEVICE_CONTEXT_TYPE and FLOAT. No D3D11 method returns a struct by
  value, so plain `delegate* unmanaged` (the x64 default convention) is enough for d3d11.h.
  - The member-function struct-return ABI is a D3D12 problem only, for example
    `ID3D12Resource::GetDesc`. That D3D12 example is UNVERIFIED here: d3d12.h was not grepped.
- **Licensing rules.** `docs/LICENSING.md` has no rule against third-party packages. Its pattern
  for MIT code is a notice in `NOTICE.txt`, which ships beside the player, plus an entry under
  "Third-party code in this repository" (examples: Mesa, GLU, smol-v, nethost). An MIT binding
  package would need the same two entries.
- **Not trimmed.** `scripts/build-windows.sh` builds framework-dependent
  (`--self-contained false`), and no project sets `PublishTrimmed` or `PublishAot` (grep over
  `*.props`/`*.csproj`/`*.targets`). A package's DLL therefore ships whole.
  - That trimming needs a self-contained publish is UNVERIFIED here.

### A.1 Options compared

Versions, dates, licences and sizes come from the nuget.org v3 API
(`https://api.nuget.org/v3-flatcontainer/<id>/index.json` and
`https://api.nuget.org/v3/registration5-gz-semver2/<id>/<ver>.json`, catalog entries). The
packages were downloaded and their assembly metadata read with System.Reflection.Metadata, and
slot indices were read by decompiling them with `ilspycmd`. All of this was done in the
scratchpad.

| | 1. Hand-written slots (today) | 2. TerraFX.Interop.Windows | 3. Vortice.Windows (Direct3D11 + DXGI) | 4. Silk.NET.Direct3D11 |
|---|---|---|---|---|
| Licence | the project's own | MIT (nuspec `licenseExpression`; repo LICENSE.md) | MIT (all four packages) | MIT |
| Generated from headers? | No. Typed by hand from MSVC output; nothing re-checks it | Yes: ClangSharp over the Windows SDK headers. Repo `generation/DirectX/um/d3d11/generate.rsp` and `generation/settings.rsp` (`--config ... vtbls=trimmable`, `--generate ... callconv-member-function, vtbl-index-attribute, disable-runtime-marshalling`). Generated files are headed "Ported from um/d3d11.h in the Windows SDK" | Partly. SharpGenTools maps the headers (README: "SharpDX bindings were used ... to understand how mapping works using SharpGenTools"; building needs the VC++ toolset and Windows 11 SDK 10.0.26100.0), and hand-written partial classes add convenience overloads (e.g. `VSSetConstantBuffers(slot, ...)` helpers in the decompiled `ID3D11DeviceContext`) | Yes: Silk's generator over `$windowsSdkDir/Include/$windowsSdkVersion/um/d3d11.h` and others (`generator.json`, profile "D3D11", raw.githubusercontent.com/dotnet/Silk.NET/main/generator.json lines 2312–2361) |
| Slots checked against MSVC here | 5/39/46/110/111 match | `Map` → `lpVtbl[14]`, `ClearState` → `lpVtbl[110]`, `FinishCommandList` → `lpVtbl[114]` (ilspycmd of 10.0.26100.6): match | `this[14]`, `this[110]`, `this[114]` (ilspycmd of 3.8.3): match | `LpVtbl[14]`, `LpVtbl[110]` (ilspycmd of 2.23.0): match |
| Shape | raw `void*` + function pointers | blittable structs with `lpVtbl`; methods are `delegate* unmanaged[MemberFunction]` calls; `ID3D11DeviceContext*` etc. No allocation, no marshalling | managed classes (`public class ID3D11DeviceContext : ID3D11DeviceChild`, CppObject/ComObject): one GC object per COM object; methods return `Result`, some throw through `CheckError()` | structs with `LpVtbl` and `delegate* unmanaged[Stdcall]`; also ref/span overloads |
| Latest versions | n/a | 10.0.28000 (2026-11-16, nupkg 33.7 MB, net10.0 + net11.0); 10.0.26100.6 (2025-12-12, nupkg 15.0 MB, net10.0 only). Both stamp `AssemblyInformationalVersion` "...-rc1+commit" although nuget lists them as stable | 3.8.3 (2026-03-04; net8/9/10); 3.8.1 (2025-12-02) | 2.23.0 (2026-01-23); 2.22.0 (2024-11-05) |
| Size shipped | 0 | `TerraFX.Interop.Windows.dll` 28.4 MB (26100.6) / 29.1 MB (28000); 35,113 types, 164,749 methods; one DLL, no dependencies | Direct3D11.dll 353 KB + DXGI.dll 92 KB + DirectX.dll 365 KB + SharpGen.Runtime + SharpGen.Runtime.COM + Vortice.Mathematics | Direct3D11.dll 16.9 MB + DXGI + Silk.NET.Core 310 KB + Silk.NET.Maths; Core pulls Microsoft.Extensions.DependencyModel, Microsoft.DotNet.PlatformAbstractions, Microsoft.CSharp, System.Memory… |
| .NET 10 / trim / AOT | n/a | TFM net10.0; `AssemblyMetadata IsTrimmable=True`, `IsAotCompatible=True` (read from metadata) | TFM net10.0; IsTrimmable/IsAotCompatible True. **But** it depends on SharpGen.Runtime 2.4.2-beta (IsTrimmable only, no IsAotCompatible) | **No net6+ TFM for Direct3D11**: lib/netstandard2.0, netcoreapp3.1, net5.0. No IsTrimmable/IsAotCompatible on Silk.NET.Direct3D11 (Core: IsTrimmable only) |
| Maintenance | n/a | active: last push 2026-11-16 (PR #425 "update-sdk-bindings"); .NET Foundation project; 324 stars, 13 open issues (gh api) | single maintainer; last push 2026-10-05 (DirectML ops); "any feature request would require a sponsor" (README). SharpGenTools last push **2025-02-04**, and every SharpGen.Runtime release is a beta (`2.3.8-beta … 2.4.2-beta`) | active repo (push 2026-11-22), but recent commits are tagged "[3.0] [2.X]". That 2.x is now maintenance-only while 3.0 is developed is UNVERIFIED (commit titles only) |
| Fit with Dxgi.cs | n/a | cast `void*` ↔ `IDXGISwapChain1*`/`ID3D11Device*`: same raw pointers. Dxgi.cs can stay or migrate method by method | wrapping needs `new ID3D11Device(nint)` / `NativePointer`; ownership and refcounts sit in the wrapper, which clashes with Dxgi.cs's `Release(ref void*)` | casts work as with TerraFX; extra layers (`D3D11.GetApi()`, Silk native loader) |

### A.2 Recommendation: TerraFX.Interop.Windows, pinned, with an MSVC cross-check

Recommended: **TerraFX.Interop.Windows 10.0.26100.6**. It matches the installed SDK
(10.0.26100.0) and the SDK named in Dxgi.cs. Add an MSVC slot/struct assertion over the
interfaces the device uses.

1. **It removes the "invented API" failure mode at compile time.** Every method, overload,
   enum value and struct is generated by ClangSharp from Microsoft's headers. A call to
   something the SDK lacks does not compile, and a slot can't be mistyped. The four slots
   compared here (14, 110, 114 and Dxgi.cs's) match MSVC.
   - It does not stop wrong *semantics*, such as a return code a method cannot produce. Those
     still need the Microsoft docs (Part C).
2. **Same model as today.** It uses raw COM pointers and function-pointer calls, with no
   managed wrappers, no allocation, no exceptions, and `[MemberFunction]` calling convention
   that is correct even for struct returns. Dxgi.cs and DxgiTarget can keep working unchanged
   while the new device uses TerraFX types, because the pointers are interchangeable.
3. **No dependencies.** It is MIT, net10.0, and declares IsTrimmable and IsAotCompatible.
4. **Cost.** A 28.4 MB DLL ships untrimmed, since the build is framework-dependent; the nupkg is
   15 MB. If that size matters (see the Workshop-size note in memory), the fallback with the
   same provenance is to vendor TerraFX's generated `.cs` files for `d3d11`, `d3d11_1`,
   `d3dcommon`, `dxgi*` and their type closure into `src/ThirdParty/TerraFX/` under MIT.
   - How large that type closure is (IUnknown, HRESULT, BOOL, LUID, RECT…) is UNVERIFIED.
5. **Belt and braces.** Add a `d3d11_check.c` beside `sdl_min_check.c` that
   `_Static_assert`s each used slot index and each used struct's `sizeof`/`offsetof`
   against the SDK. Compile it in `build-native-host.sh`'s MSVC step; a mismatch then fails
   the Windows build. Tests can feed the expected values from TerraFX's `VtblIndex`/`sizeof`.
   - TerraFX's `VtblIndexAttribute` is not present in the release build's metadata. My
     metadata scan found no `VtblIndex` on `ID3D11DeviceContext.Map`, so it is presumably
     `[Conditional]`. That presumption is UNVERIFIED, so read the indices from the IL or the
     source instead.

Why not the others:

- **Vortice** is part hand-written and wraps every interface in a managed class. It also rests
  on a beta-only SharpGen runtime that has not been released since 2025-02.
- **Silk.NET 2.x** has no modern TFM and no trim/AOT metadata on Direct3D11, and its 2.x
  future is unclear.
- **Extending the hand-written slots** to about 100 methods and about 40 structs is the exact
  surface where invented or mistyped entries crept in before. It is only acceptable if the
  MSVC `_Static_assert` file is generated for every entry, and at that point it amounts to a
  smaller TerraFX written by us.

---

## Part B: Unity's D3D11 conventions compared with our OpenGL and Vulkan devices

### B.0 How our devices implement Unity's conventions today

- `RenderConventions(bool Vulkan, bool Flipped)` in `src/OniMoono.Desktop/Gl/GlPrograms.cs`
  (~line 453) handles depth and the projection; commit d5a82e4dc ("gl: draws can follow
  Unity's Vulkan conventions").
  - `GpuProjection` maps depth to [1, 0] (`p[2,c] = 0.5*p[3,c] - 0.5*p[2,c]`), and into a render
    texture (`Flipped`) it negates y.
  - `_ProjectionParams.x` is -1 when Flipped.
  - `_ZBufferParams` takes the reversed form (`x = -1 + far/near`, `y = 1`).
  - `unity_CameraProjection` keeps the camera's matrix.
- docs/ARCHITECTURE.md §3.5, Vulkan *Conventions* (around lines 3163–3192):
  - Every shipped Vulkan vertex stage ends by negating `gl_Position.y`, read from the SPIR-V.
  - The back buffer is stored top row first, and render textures keep OpenGL's row order.
  - Depth tests compare the other way, depth offsets are negated, clears write 1 minus the
    value asked for, and new depth surfaces start at 0.
  - Viewport and scissor keep OpenGL's bottom-left origin, turned over in the back buffer.
  - Front faces are clockwise in the back buffer and counter-clockwise in a flipped render
    texture (`VkPipelines.cs` `VkTargetFormats.Flipped`).
- `src/OniMoono.Desktop/Vulkan/VkDevice.cs:289-293` sets
  `Flipped = colors != null && !screenRows`.
- `Direct3DClip` (GlPrograms.cs) already draws `Klei/Stars` "as Unity's Direct3D 11 player
  draws them". The reason: Unity's D3D projection into a render texture has y negated, so a
  program that derives UVs from clip y reads them turned over. ARCHITECTURE §3.5 "Engine
  renderer" (~line 2355) records that this was measured on Windows. It also states that Vulkan
  "matches Direct3D 11" for the GLCore-only `1 - v` reads, and that the original shows the same
  `Klei/Stars` result on Direct3D 11 and Vulkan.

### B.1 Unity public-doc sources used

- **U-PD63**: "Write HLSL for different graphics APIs", Unity 6.3 LTS manual (6000.3),
  https://docs.unity.com/en-us/engine/6000.3/manual/materials-and-shaders/shaders/writing-custom/shader-writing/sl/writing-shader-writing-shader-programs-hlsl/sl-platform-differences
  (docs.unity3d.com/Manual/SL-PlatformDifferences.html redirects here).
- **U-PD22**: "Writing shaders for different graphics APIs", 2022.3,
  https://docs.unity3d.com/2022.3/Documentation/Manual/SL-PlatformDifferences.html
- **U-PD55**: the same page for 5.5, https://docs.unity3d.com/550/Documentation/Manual/SL-PlatformDifferences.html
- **U-MAC**: "Built-in macros", 2022.3, https://docs.unity3d.com/2022.3/Documentation/Manual/SL-BuiltinMacros.html
- **U-VAR**: "Built-in shader variables", 2022.3, https://docs.unity3d.com/2022.3/Documentation/Manual/SL-UnityShaderVariables.html
- **U-GPM**: `GL.GetGPUProjectionMatrix`, https://docs.unity.com/en-us/engine/current/script-reference/unityengine/gl/getgpuprojectionmatrix
- **U-RZ**: `SystemInfo.usesReversedZBuffer`, https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SystemInfo-usesReversedZBuffer.html
- **U-UV**: `SystemInfo.graphicsUVStartsAtTop`, https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SystemInfo-graphicsUVStartsAtTop.html
- **U-SC**: "Shader compilation", 2022.3, https://docs.unity3d.com/2022.3/Documentation/Manual/shader-compilation.html
- **U-RTD**: `RenderTexture.depth`, 2022.3, https://docs.unity3d.com/2022.3/Documentation/ScriptReference/RenderTexture-depth.html
- **U-TGT**: "Shader compilation target levels", 2022.3, https://docs.unity3d.com/2022.3/Documentation/Manual/SL-ShaderCompileTargets.html
- **U-SYS**: "System requirements", 6000.3, https://docs.unity.com/en-us/engine/6000.3/manual/get-started/install-and-upgrade/getting-started-installing-unity/system-requirements

Microsoft and Khronos sources for the API-side facts:

- **MS-COORD**: https://learn.microsoft.com/en-us/windows/win32/direct3d10/d3d10-graphics-programming-guide-resources-coordinates
- **MS-VP**: https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d10-graphics-programming-guide-rasterizer-stage-getting-started
- **MS-RAST**: https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_rasterizer_desc
- **VK-POST**: Vulkan spec `chapters/vertexpostproc.adoc` (KhronosGroup/Vulkan-Docs main)
- **VK-PRIM**: Vulkan spec `chapters/primsrast.adoc`

A key public fact: Unity compiles **every** API's programs with FXC. "Platforms that use DirectX
use Microsoft's FXC HLSL compiler"; OpenGL, Metal and Vulkan also use FXC "followed by
translation ... with HLSLcc" (U-SC). Our Vulkan device therefore already runs HLSLcc
translations of the same FXC output whose DXBC the D3D11 device would run. The differences
below come from the API, not from different source.

### B.2 Convention table

| Convention | Unity on D3D11 (public docs) | D3D11 API fact | Same as | Notes for the device |
|---|---|---|---|---|
| **UNITY_REVERSED_Z / depth direction** | "DirectX 11, DirectX 12, Metal: Reversed direction"; depth buffer "1.0 at the near plane, decreasing to 0.0 at the far plane" (U-PD22). U-PD55 lists DirectX 11 too. U-PD63's table: "DirectX, Metal, and Vulkan: 1.0 near to 0.0 far". UNITY_REVERSED_Z is "defined on plaftorms using reverse Z buffer" (U-MAC) | n/a (an API-agnostic choice) | **Vulkan** | Reuse `RenderConventions` depth: reversed projection, swapped compares, `1 - clear`, depth surfaces start at 0. `SystemInfo.usesReversedZBuffer` true (U-RZ) |
| **Clip-space Z range** | Direct3D-like: "+1.0 at the near plane to 0.0 at the far plane"; OpenGL-like: "–1.0 ... to +1.0" (U-PD22). U-PD63 clip-space row: "DirectX, Metal, and Vulkan: 1.0 near to 0.0 far" | clip z test `0 <= z <= w` (MS-RAST, DepthClipEnable); viewport `Z = MinDepth + Z*(MaxDepth-MinDepth)` (MS-VP) | **Vulkan** (`z_m <= z_c <= w_c` with `z_m = 0` unless `negativeOneToOne`, VK-POST lines 905–908, 1077) | `GpuProjection`'s depth row is right for D3D11 as it is |
| **GL.GetGPUProjectionMatrix** | "projection matrices follow OpenGL convention. However on some platforms they have to be transformed ... The value will match what comes as UNITY_MATRIX_P"; `renderIntoTexture` "On some platforms it affects how the final matrix will look like" (U-GPM). "returns a z-reverted matrix if you are on a platform where the depth (Z) is reversed" (U-PD22) | n/a | **Vulkan** (same z reversal; y negated into a render texture) | Exact D3D11 matrix values were not checked here against the original's `GetGPUProjectionMatrix`; that comparison is UNVERIFIED. Measure on the original's D3D11 player (`-force-d3d11`) via OniMcp before coding |
| **UNITY_UV_STARTS_AT_TOP / texture V origin** | "Direct3D-like platforms use value of 1" (U-MAC). "If you sample a render texture at (0,0), DirectX, Metal, and Vulkan return the top-left" (U-PD63). Matches `SystemInfo.graphicsUVStartsAtTop` (U-UV) | "The texel coordinate system has its origin at the top-left corner of the texture" (MS-COORD) | **Vulkan** | The shipped DXBC is compiled with UNITY_UV_STARTS_AT_TOP=1, as the Vulkan SPIR-V is |
| **Render-texture flipping / `_ProjectionParams.x`** | "When rendering into a Texture on a Direct3D-like platform, Unity internally flips rendering upside down" (U-PD22). `_ProjectionParams.x` "-1 indicates a projection has been flipped upside down to match OpenGL-like projection coordinates" (U-PD22, U-VAR). Image-effect sources with AA are not flipped; MRTs may "come out at different vertical orientations"; GrabPass "might not actually be turned upside down" (U-PD22). Code sample: `if (_MainTex_TexelSize.y < 0) uv.y = 1-uv.y` (U-PD63) | the render target's Y axis points down, and the viewport flips NDC y: `Y = (1 - Y) * Height * 0.5 + TopLeftY` (MS-VP) | **Vulkan in effect, but by a different mechanism** | See B.3. Into a render texture: projection y negated and `_ProjectionParams.x = -1`, as `Flipped` does now. The `_TexelSize.y < 0` case for a flipped source is not handled by our devices as far as this research saw (UNVERIFIED either way) |
| **Back-buffer orientation** | (implied by the above: no flip when not rendering into a texture) | NDC y +1 → row 0 (MS-VP); pixel origin "upper-left corner" (MS-COORD) | **Vulkan's storage** (top row first) | Projection unflipped, `_ProjectionParams.x = 1`; the presenter copies with no flip, as Vulkan's does |
| **Vertex-stage epilogue** | not documented | NDC y is up, so no negation is needed | **Neither, exactly**. Vulkan's NDC y points down (`y_f = (p_y/2) y_d + o_y`, `o_y = y + height/2`, VK-POST lines 1564, 1941), hence the shipped SPIR-V's `gl_Position.y` negation (ARCHITECTURE §3.5). GL has no negation and a bottom-left origin | Whether Unity's DXBC carries any y epilogue is UNVERIFIED. The API needs none; check by disassembling a few shipped DXBC vertex programs (ShaderSurvey) |
| **Front faces / winding** | not documented for D3D11 | `FrontCounterClockwise` FALSE (the default) means clockwise on the render target is front (MS-RAST) | **Vulkan** (front face defined in framebuffer space by area sign, VK-PRIM ~3371) | Clockwise front into the back buffer; counter-clockwise into a flipped render texture (or swap the cull mode), as `VkTargetFormats.Flipped` does. Derived, needs a measurement |
| **Viewport / scissor origin** | Unity's rects are bottom-left (Unity API, OpenGL convention) | `D3D11_VIEWPORT.TopLeftX/TopLeftY`, `D3D11_RECT` top/left (MS-VP) | **Vulkan** | Turn rects over in the back buffer, leave them in flipped render textures; the same for `ReadPixels` of the screen |
| **Fragment position (SV_Position)** | not documented | top-left origin, "Pixel centers are offset by (0.5f,0.5f)" (MS-COORD) | **Vulkan** ("A fragment is located by its upper left corner", VK-PRIM line 20). GL's `gl_FragCoord` is bottom-left | |
| **Half-texel / half-pixel offset** | not mentioned in U-PD22, U-PD55 or U-PD63. That Unity dropped D3D9 is UNVERIFIED here | D3D10+ puts the render-target origin at the top-left corner of the top-left pixel; "Direct3D 9 defines the center of the upper-left pixel as the origin" (MS-COORD) | **Both** (no offset on GL, Vulkan or D3D11) | Nothing to do |
| **`_ZBufferParams`** | x = "-1+far/near if UNITY_REVERSED_Z", y = "1" (U-VAR) | n/a | **Vulkan** | Reuse |
| **Depth bias** | "Unity automatically deals with depth (Z) bias"; a native plugin must "negate (reverse) depth (Z) bias" (U-PD22) | `D3D11_RASTERIZER_DESC.DepthBias` INT, `SlopeScaledDepthBias` (MS-RAST) | **Vulkan** (negated offsets) | D3D11's bias units differ from GL/Vulkan's floats (DepthBias is an INT scaled by the format's unit, Depth Bias doc). Units mapping UNVERIFIED |
| **sRGB** | the game is Gamma (`m_ActiveColorSpace=0`, ARCHITECTURE §3.5 "Gamma colour space ... no sRGB conversions") | flip-model swap chains take only `R16G16B16A16_FLOAT`, `B8G8R8A8_UNORM`, `R8G8B8A8_UNORM` (or 10-bit) formats (DXGI_SWAP_CHAIN_DESC1 remarks; MS writes "R10G10B10A10_UNORM", a doc typo for R10G10B10A2) | **Both** (UNORM swapchain; no sRGB encode) | `_SRGB` views are possible on UNORM back buffers (MS "Converting data for the color space", search result) but not needed in Gamma |
| **Depth formats** | `RenderTexture.depth` 0/16/24/32; "The selected format depends on the available formats on the platform" (U-RTD) | FL 11.0 requires depth-stencil support for D24_UNORM_S8_UINT, D32_FLOAT, D32_FLOAT_S8X24_UINT and D16_UNORM; sampling goes through R24_UNORM_X8_TYPELESS / R32_FLOAT (/ R32_FLOAT_X8X24_TYPELESS) views (MS format-support FL 11.0 page, below) | **Neither exactly**: D3D needs TYPELESS resources + typed DSV/SRV to sample depth | Which D3D11 depth format Unity picks for 24 and 32 bits is UNVERIFIED |
| **Constant-buffer layout** | float3 "might become a float4"; use float4 and decreasing size order (U-PD63) | HLSL cbuffer packing (16-byte registers). The packing rules page was not fetched: UNVERIFIED | **Neither**: our GL path uses loose uniforms; Vulkan uses std140-like blocks from the SPIR-V | Take offsets from Unity's serialized binding info / DXBC RDEF, never from GL/Vulkan layouts |
| **Vertex input binding** | n/a | by semantic, through an input layout built against the VS input signature | **Neither** (GL/Vulkan bind by location) | Build input layouts from DXBC ISGN / Unity channel data |
| **Feature level of the original** | Windows player: "DX10, DX11, DX12 or Vulkan capable GPUs" (U-SYS). `#pragma target` 3.0–4.0 → "DirectX 11 feature level 10+"; 4.5–5.0 → "feature level 11+" (U-TGT) | see C.1 | n/a | Which targets the game's shaders were compiled at is UNVERIFIED: survey the DXBC versions (SM 4.0 vs 5.0) |
| **`SystemInfo.graphicsDeviceType`** | (Direct3D11 on the original) | n/a | n/a | D54 has the Vulkan device report Vulkan "so code that branches on it sees the conventions it renders with" (ARCHITECTURE ~1514). The D3D11 device should report Direct3D11 for the same reason |

### B.3 Why D3D11 matches Vulkan's stored rows by a different route

From the viewport formulas only. This is a derivation, to be confirmed by measurement.

- **Vulkan, back buffer.**
  1. The projection is unflipped, then the SPIR-V epilogue negates y.
  2. The scene's top reaches NDC y = -1.
  3. Vulkan maps y_d = -1 to the top row (`y_f = (p_y/2) y_d + o_y`).
  4. Result: top row first.
- **D3D11, back buffer.**
  1. The projection is unflipped and there is no epilogue (UNVERIFIED for Unity's DXBC).
  2. The scene's top stays at NDC y = +1.
  3. D3D maps y = +1 to the top row (`Y = (1 - Y) * H/2`, MS-VP).
  4. Result: top row first. **Same storage.**
- **Vulkan, render texture.**
  1. The projection's y is negated and the epilogue negates again, so the scene's top is at
     NDC +1.
  2. That lands on the bottom row.
  3. Result: OpenGL's row order.
- **D3D11, render texture.**
  1. The projection's y is negated and there is no epilogue, so the scene's top is at NDC -1.
  2. D3D maps -1 to the bottom row.
  3. Result: OpenGL's row order. **Same storage.** This matches Unity's "Unity internally flips
     rendering upside down" (U-PD22) and ARCHITECTURE's measured `Klei/Stars` result (identical
     on the original's D3D11 and Vulkan).

Consequences:

- `RenderConventions` (depth, `Flipped`, `_ProjectionParams`, `_ZBufferParams`), the
  rect turning, the front-face swap, and the presenter's no-flip copy all carry over to D3D11
  as they are.
- The one difference is that the D3D11 device must not add the y negation that Vulkan's
  shipped programs carry, and it doesn't need to, because the DXBC has none (to be checked).
- GLCore-route mods' programs would need their own D3D11 story; mods are out of scope here.

---

## Part C: D3D11 facts that drive the design

### C.1 Feature levels and shader models

- From the Direct3D 11 feature table at
  https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-devices-downlevel-intro:
  - Shader model by feature level: 11_1 and 11_0 "5.0", 10_1 "4.x", 10_0 "4.0", 9_x "2.0
    (4_0_level_9_x)".
  - Footnote 2: "DirectX 11 only supports up to shader model 5.0."
  - Compute shader is Optional at 10_x and Yes at 11_0.
  - Texture limits: Max Texture Dimension is 16384 at 11_0 and 8192 at 10_x; Max Input Slots
    32 at 10_1+ and 16 at 10_0; 8 simultaneous render targets at 10_0+.
  - BC4/BC5 need 10_0+; BC6H/BC7 need 11_0+.
- **So SM 4.0 DXBC needs FL 10_0, and SM 5.0 DXBC needs FL 11_0.** Request
  `{11_1, 11_0, 10_1, 10_0}` and reject any program whose DXBC version exceeds the level
  obtained. Requesting 11_1 needs the 11.1 runtime (footnote 1).
- Our existing code passes `D3D11_SDK_VERSION` = 7, which matches `d3d11.h:15014`.

### C.2 Deferred contexts and multithreaded recording

- **Driver support is a query; the runtime emulates what the driver lacks.**
  - `D3D11_FEATURE_DATA_THREADING.DriverCommandLists`: "TRUE means command lists are supported
    by the current driver; FALSE means that the API will emulate deferred contexts and command
    lists with software." `DriverConcurrentCreates`: "TRUE means resources can be created
    concurrently on multiple threads while drawing"
    (https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_feature_data_threading).
  - "The runtime supports multithreading and command lists regardless of driver and hardware
    support; if there is no driver and hardware support ... the runtime will emulate the
    functionality" (https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-render-multi-thread-support).
- **Performance depends on the driver.** "When using a command list, performance depends on the
  amount of support implemented in a driver"
  (https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-render-multi-thread-command-list).
  "Multithreading performance depends on the driver support"
  (https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-render-multi-thread-intro).
  - Which vendors set DriverCommandLists today is **UNVERIFIED**; measure with
    CheckFeatureSupport on the test machines.
- **Thread safety.** "the use of a device context (ID3D11DeviceContext) is not thread-safe, the
  use of a Direct3D 11 device (ID3D11Device) is thread-safe" (multi-thread-intro).
  - "Only one thread at a time should use the immediate context. However, your application
    should also use that same thread for ... DXGI operations, especially ... Present"; it is
    "invalid to use an immediate context concurrently with most of the DXGI interface
    functions" (multi-thread-intro). **Present must stay on the device thread.**
  - The device must not be created with `D3D11_CREATE_DEVICE_SINGLETHREADED` if deferred
    contexts are wanted: `CreateDeferredContext` then "returns DXGI_ERROR_INVALID_CALL"
    (https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11device-createdeferredcontext).
- **Command lists are recorded on deferred contexts and played only on the immediate context.**
  - "A command list must be recorded by a deferred context, but it can only be played back on an
    immediate context" (command-list page).
  - "You cannot play back two or more command lists simultaneously on the immediate context"
    (https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-render-multi-thread-render).
- **Queries on a deferred context.** You "cannot call GetData on a deferred context"; query
  data is available only after ExecuteCommandList (multi-thread-render).

### C.3 Map, WRITE_DISCARD and WRITE_NO_OVERWRITE

From https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_map and
https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-map:

- `WRITE_DISCARD` (4): "previous contents of the resource will be undefined"; it requires
  write access and `D3D11_USAGE_DYNAMIC`.
- `WRITE_NO_OVERWRITE` (5): "only valid on vertex and index buffers" and not on
  `D3D11_BIND_CONSTANT_BUFFER`, **except** that the 11.1 runtime (Windows 8+) allows dynamic
  constant buffers and SRVs of dynamic buffers. Check
  `D3D11_FEATURE_DATA_D3D11_OPTIONS.MapNoOverwriteOnDynamicConstantBuffer` /
  `MapNoOverwriteOnDynamicBufferSRV`. NO_OVERWRITE "cannot be used with dynamic textures".
- **Deferred contexts.** Map accepts only WRITE_DISCARD and/or WRITE_NO_OVERWRITE (Map remarks).
  "Map ... is only supported in a deferred context for dynamic resources ... where the first
  call within the command list is D3D11_MAP_WRITE_DISCARD. D3D11_MAP_WRITE_NO_OVERWRITE is
  supported on subsequent calls" (multi-thread-render).
- **Return values.**
  - Map "returns one of the Direct3D 11 Return Codes".
  - It returns `DXGI_ERROR_WAS_STILL_DRAWING` only with `D3D11_MAP_FLAG_DO_NOT_WAIT`.
  - It returns `DXGI_ERROR_DEVICE_REMOVED` "if MapType allows any CPU read access and the video
    card has been removed".
  - Code must not assume Map can return anything else it lists. Write-only maps on a removed
    device are not documented to fail.
- **Write-combined memory.** Never read mapped write memory, since it may be `PAGE_WRITECOMBINE`
  (Map remarks).
- **Discard is whole-buffer.** "the runtime always discards the entire buffer" on DISCARD.
  For DYNAMIC textures, map only the top mip with DISCARD.
  (https://learn.microsoft.com/en-us/windows/win32/direct3d11/how-to--use-dynamic-resources)

### C.4 Limits (from the SDK header, `d3d11.h` 10.0.26100.0)

| Limit | Value |
|---|---|
| `D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT` (line 370) | 14 (HW slot count 15, line 376) |
| `D3D11_REQ_CONSTANT_BUFFER_ELEMENT_COUNT` (line 890) | 4096 (16-byte vectors) |
| `D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT` (line 408) | 128 SRVs per stage |
| `D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT` (line 418) | 16 samplers per stage |
| `D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT` (line 795) | 32 vertex buffers |
| `D3D11_IA_VERTEX_INPUT_STRUCTURE_ELEMENT_COUNT` (line 799) | 32 input elements |
| `D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT` (line 953) | 8 |
| `D3D11_PS_CS_UAV_REGISTER_COUNT` (line 842) | 8; `D3D11_1_UAV_SLOT_COUNT` 64 (line 1054) |
| `D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION` (line 929) | 16384 |
| `D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE` (line 1023) | 16 |

These are header constants. The feature-level table (C.1) lowers some of them at 10_x: input
slots 16 at 10_0, texture size 8192 at 10_x, UAVs 1 at 10_x and 8 at 11_0.

### C.5 UpdateSubresource or staging/dynamic

From https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-updatesubresource:

- It cannot target immutable or dynamic resources, depth-stencil resources, or multisampled
  resources.
- For a constant buffer, `pDstBox` must be NULL: "It is not possible to use this method to
  partially update a shader-constant buffer". `UpdateSubresource1` (11.1) relaxes this; the
  page that says so was not fetched here, so that detail is UNVERIFIED.
- With contention (the GPU still using the destination), it makes **two copies**: the CPU copies
  into command-buffer space, then the GPU copies at flush. Without contention it picks
  whichever is faster.
- The source memory can be freed on return.
- **Deferred-context bug.** With a non-(0,0,0) `pDstBox` on a deferred context and
  `DriverCommandLists == FALSE`, the runtime wrongly applies the box offset to `pSrcData`. The
  page gives the workaround code. This interacts with C.2's emulation.
- Feature level 9_x only: staging→default copies with a NULL source box can corrupt the
  destination.
- **Design consequence.** Use UpdateSubresource for whole-resource texture uploads (the game's
  `Texture2D.Apply`), DYNAMIC + DISCARD/NO_OVERWRITE for per-frame vertex, index and constant
  data, and STAGING resources + CopyResource for readbacks.

### C.6 Block-compressed formats

From https://learn.microsoft.com/en-us/windows/win32/direct3d11/texture-block-compression-in-direct3d-11:

- "Support for these three formats [BC1, BC2, BC3] is required by all feature levels"
  (9_1 through 11_0). Their `_SRGB` variants exist.
- BC4 and BC5 need "Direct3D 10" (FL 10_0+ per the C.1 table). BC6H and BC7 need "Direct3D 11"
  (FL 11_0+).
- So FL 10_x cannot sample BC6H/BC7. Whether the game ships any BC6H/BC7 textures is UNVERIFIED.
- Block sizes: BC1/BC4 8 bytes per 4×4 block; BC2/3/5/6H/7 16 bytes.
- Depth formats at FL 11.0, from
  https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/format-support-for-direct3d-11-0-feature-level-hardware:
  - D24_UNORM_S8_UINT, D32_FLOAT, D32_FLOAT_S8X24_UINT and D16_UNORM: "Depth/Stencil Target:
    Hardware support required"; shader sample is disallowed on the D-typed formats.
  - R24_UNORM_X8_TYPELESS and R32_FLOAT: sample and sample_c required.

### C.7 DXGI flip model and the existing swap chain

- **Requirements**, from https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_swap_chain_desc1
  remarks and https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model:
  - Format is FP16, B8G8R8A8_UNORM, R8G8B8A8_UNORM or 10-bit.
  - SampleDesc is 1/0: "MSAA ... is not supported", so resolve before Present.
  - BufferCount is 2–16.
- **After Present**, from https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model:
  - "the back buffer needs to explicitly be re-bound to the D3D11 immediate context before it
    can be used again."
  - After SetFullscreenState, call ResizeBuffers before Present.
- **The swap chain belongs to one device.** `CreateSwapChainForHwnd`'s `pDevice`: "For
  Direct3D 11 ... this is a pointer to the Direct3D device for the swap chain"
  (https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforhwnd).
  - The D3D11 render device should create the swap chain itself on its own device. The
    current `DxgiTarget`/`VkDxgiTarget` path (a second D3D11 device, a shared texture, a keyed
    mutex or WGL_NV_DX_interop2: ARCHITECTURE ~2602–2635) is not needed. Rendering happens on
    the swap chain's device, and the final copy into the back buffer becomes an ordinary
    CopySubresourceRegion or draw.
- **One flip swap chain per HWND.** "you can associate only one flip presentation model swap
  chain at a time with an HWND". Because D3D11 defers object destruction, replacing one needs
  the "Deferred Destruction Issues with Flip Presentation Swap Chains" steps in `Flush`'s docs:
  ClearState then Flush before the new swap chain (CreateSwapChainForHwnd remarks).
  - "Use one flip model swap chain per HWND"; once a flip swap chain has presented, "GDI no
    longer works with the HWND ... even after the destruction of the swap chain" (flip-model
    page; DXGI_SWAP_EFFECT remarks).
  - Dxgi.cs already records that a second flip swap chain on the same window gave
    `E_ACCESSDENIED` (measured, `Dxgi.AccessDenied`). Switching between the GL+DXGI presenter
    and the D3D11 device at run time must release the old swap chain first.
- **FLIP_DISCARD** "is supported starting with Windows 10" for D3D11
  (https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ne-dxgi-dxgi_swap_effect).

### C.8 Device removed

- **Where it shows up.** Check Present's and ResizeBuffers' results for
  `DXGI_ERROR_DEVICE_REMOVED` and `DXGI_ERROR_DEVICE_RESET`. On either, "release any references
  to graphics device resources ... all references to the swap chain must be released before a
  new one can be created", then recreate the device and all resources
  (https://learn.microsoft.com/en-us/windows/uwp/gaming/handling-device-lost-scenarios).
- **`GetDeviceRemovedReason`** should be called "before releasing the Direct3D device". It
  returns DEVICE_HUNG, DEVICE_REMOVED, DEVICE_RESET, DRIVER_INTERNAL_ERROR, INVALID_CALL
  ("your code caused the device removed condition"), or S_OK (same page).
- **Other entry points.** `CreateDeferredContext` returns DEVICE_REMOVED "if the video card has
  been physically removed ... or a driver upgrade" (C.2 link). Map returns it only for
  CPU-read maps (C.3).
- **Already in the repo.** Dxgi.cs's `DeviceLost` (removed, reset, hung, driver-internal,
  `D3DDDIERR_DEVICEREMOVED`) and `GetDeviceRemovedReason` slot 39 (verified above) can be
  reused.
- **Testing.** `dxcap -forcetdr` forces a TDR; it ships in the Graphics Tools Feature on Demand
  (same page). Fault-injection hooks alone prove nothing (repo memory "Verify API premises").

### C.9 Debug layer

- `D3D11_CREATE_DEVICE_DEBUG` gives "extensive additional parameter and consistency
  validation". "For Windows 10, to create a device that supports the debug layer, enable the
  'Graphics Tools' optional feature" (Settings → Apps → Optional features). Older systems used
  `D3D11SDKLayers.dll` / `D3D11_1SDKLayers.dll` from the SDK. "Core functions will have the same
  return values independent of the debug layer"
  (https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-devices-layers).
- The header `d3d11sdklayers.h` (221 KB) is in the installed SDK (`ID3D11Debug`,
  `ID3D11InfoQueue`).
- What device creation returns when the flag is set but Graphics Tools is missing is not
  stated on that page. It is commonly reported as `DXGI_ERROR_SDK_COMPONENT_MISSING`, which is
  UNVERIFIED. Retry without the flag and log.
- Whether the Graphics Tools optional feature is installed on the user's Windows machine is
  UNVERIFIED.

---

## Open items to verify before design sign-off

1. Disassemble a few shipped DXBC vertex programs to confirm there is no y epilogue (B.3).
   Survey the DXBC shader models (SM 4.0 vs 5.0) and any BC6H/BC7 textures, which decides FL
   10_0 vs 11_0.
2. On the original's D3D11 player, read `GL.GetGPUProjectionMatrix(p, true/false)`,
   `_ProjectionParams`, `SystemInfo.usesReversedZBuffer` and `graphicsUVStartsAtTop` through
   OniMcp.
3. Query `D3D11_FEATURE_THREADING` and `D3D11_FEATURE_D3D11_OPTIONS` on the test GPUs (NVIDIA,
   AMD iGPU) before choosing deferred contexts over a single recording thread.
4. Confirm the D3D11 depth-bias units mapping and the D3D11 depth format Unity picks for 24 and
   32 bits.
5. Decide between the TerraFX package (28 MB) and a vendored generated subset. Either way, add
   `d3d11_check.c` static asserts to the MSVC build.
