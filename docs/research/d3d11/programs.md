# D3D11 (platform 4) programs in U60 764025's Windows data

Research for a Direct3D 11 device that runs the shipped Unity D3D11 programs unmodified. Read-only on
the repo. Every number below comes from one of two places: a measurement on the data, made with a
throwaway reader, or a cited public source. Claims I could not verify are marked **UNVERIFIED** and
collected at the end. I read no Unity engine source.

## Data and tooling

- **Data folder:** `/mnt/e/oni-builds/oni-win-764025/OxygenNotIncluded_Data`.
  - It is the same build as `BrutalNotIncluded-764025/BrutalNotIncluded-764025_Data`. `cmp` shows
    `resources.assets`, `sharedassets1/2.assets`, `globalgamemanagers.assets` and `level0` are
    identical; only `globalgamemanagers` differs, and it holds no shaders.
  - I used the `oni-win` copy because it has the original folder name.
- **Files read:** `level0`–`level2`, `globalgamemanagers`, `globalgamemanagers.assets`,
  `resources.assets`, `sharedassets0`–`sharedassets2.assets`, `Resources/unity default resources` and
  `Resources/unity_builtin_extra`.
- **The throwaway tool** (C#, references `src/OniMoono.Assets`; built into the scratchpad, with nothing
  under `src/` edited) is in
  `scratchpad d3d11-research/tool/`:
  - `Dxbc.cs` is a DXBC container reader: it reads the chunks, ISGN/OSGN/ISG1/OSG1 and SFI0, walks the
    SHDR/SHEX token stream and recomputes the checksum.
  - `Survey.cs` holds the per-subprogram checks.
  - `Bundles.cs` surveys mod bundles.
- **How to run it:** `~/.dotnet/dotnet tool/bin/D3dSurvey.dll survey DATADIR OUTDIR`. The other modes
  are `hex`, `hashes`, `params`, `rdef`, `rdefcmp`, `common`, `classes`, `compute`, `bundles WSDIR` and
  `bundlehex BUNDLE N BYTES`.
- **Outputs** are in `.../d3d11-research/`:
  - `out-game/summary.txt`, `detail.txt`, `features.txt`, `keywords.txt`, `prefix-and-inputs.txt`,
    `mismatches.txt` and `lacking-d3d11.txt` (empty);
  - `params-all.txt`, `mods.tsv` and `mods-summary.txt`.

## 1. Does every game shader carry D3D11 programs? Yes.

**Shader objects (class 48):** 121.

| File | Shaders | Platforms in each shader |
|---|---|---|
| sharedassets0.assets | 73 | 4, 15, 18 |
| unity_builtin_extra | 32 | 4, 15, 18 |
| resources.assets | 6 | 4, 15, 18 |
| unity default resources | 6 | 4, 9, 14, 15, 18, 26 |
| sharedassets2.assets | 3 | 4, 15, 18 |
| globalgamemanagers.assets | 1 | 4, 15, 18 |

`level*`, `globalgamemanagers` and `sharedassets1` hold no shaders.

**Shaders per platform:** 4 = 121, 15 (GLCore) = 121, 18 (Vulkan) = 121, and 9, 14 and 26 = 6 each
(the shaders in `unity default resources`).

**Platform blob-table entries** (programs plus parameter blobs): 4 = 1,396, 18 = 996, 15 = 834.

**Passes:**
- 180 in all; 176 have player programs.
- The 4 without programs have `m_Type` 2, which is GrabPass under AssetStudio's PassType enum (Normal 0,
  Use 1, Grab 2): `Particles/Standard Surface`, `Particles/Standard Unlit`,
  `Klei/BatchedAnimationPstTemperature` and `Klei/Standard Unlit`, each in s0p0.
- Passes with programs per platform: D3D11 176, GLCore 176, Vulkan 175.

**Player subprograms per platform:**

| Platform | Subprograms | Detail |
|---|---|---|
| D3D11 | 1,070 | 960 distinct program entries; 110 subprograms reuse an entry of another pass of the same shader with the same keywords |
| GLCore | 591 | |
| Vulkan | 590 | |
| Metal | 124 | |
| GLES3 | 66 | type 4 |
| type 33 | 63 | in no public enum I found (UNVERIFIED) |

**Hardware tiers:** each stage has 4 `m_PlayerSubPrograms` tier lists, and only the last is populated
(176 of 176 vertex and fragment stages, 1 of 1 geometry). `m_SubPrograms` is empty everywhere.

**Shaders, passes or variants lacking D3D11:** none.
- Every pass with programs has D3D11 programs.
- Every Vulkan keyword set has D3D11 stage programs that cover it (§4).

**The reverse case.** `Hidden/VR/BlitFromTex2DToTexArraySlice` s0p0 has D3D11 programs and no Vulkan
program:
- s0p0 has D3D11 2 and GLCore 1.
- s1p0 has D3D11 3 (vertex, geometry, pixel), GLCore 1 and Vulkan 1.

## 2. Layout of a D3D11 program entry

### Entry layout

Measured on all 960 entries. Each one parses to its exact end, with 0 trailing bytes.

The example is `Klei/PostFX/FullScreen` (sharedassets0), table entry 1, which is the vertex program
(444 bytes):

```
0x00  u32 magic 0x0C0A75BA          (BA750A0C) - same as GLCore/Metal/Vulkan entries
0x04  u32 GPU program type          0x0F = 15
0x08  u32 x4 statistics             0,0,0,0
0x18  u32 keyword count             0   (then len-prefixed strings padded to 4, as Vulkan)
0x1C  u32 payload length            0x182 = 386
0x20  payload:
      +0x00  38-byte Unity prefix   02 00 00 00 + 34 zero bytes
      +0x26  DXBC container         "DXBC" at entry 0x46; container size 0x15C = 348 (38 + 348 = 386)
0x1A4 (payload padded to 4) u32 vertex channel mask 0x11
      u32 pair count 2, then (channel, component) pairs: (0,0) (4,5)
```

- **GPU program type.** The type in the entry header equals the subprogram's `m_GpuProgramType` in
  1,070 of 1,070 subprograms.
- **Keywords.** The entry's keyword list equals the subprogram's keyword set in 1,070 of 1,070.
- **Statistics.** These are four ints whose exact meaning is UNVERIFIED. They correlate with
  instruction counts:
  - stat[1] equals the number of sample/ld instructions in 748 of 960 entries;
  - stat[2] equals the number of flow-control instructions in 800 of 960.
  - Nothing a renderer needs depends on them.
- **The 38-byte prefix** is 38 bytes long in all 960 entries. Its fields:

  | Bytes | Value | Matches |
  |---|---|---|
  | 0 | 2 in every entry | |
  | 1 | number of SRV slots (t#) | 960/960 |
  | 2 | number of constant-buffer slots (b#) | 960/960 |
  | 3 | number of sampler slots (s#) | 960/960 |
  | 4–37 | zero in 959 entries | |

  - For bytes 1–3, the counts equal both the number of declared slots and the highest slot + 1, so
    slots are always dense from 0.
  - The one non-zero entry is the geometry program of `Hidden/VR/BlitFromTex2DToTexArraySlice`, with
    byte 5 = 3. My guess that this is the GS input primitive (triangle = 3) is UNVERIFIED.
- **Pixel programs** end with mask 0 and count 0.

### Comparison with what our Vulkan reader (`VulkanProgram.Read`, platform 18) expects

| | D3D11 | Vulkan (platform 18) |
|---|---|---|
| Programs per variant | one entry per stage | one entry with up to 6 SMOL-V stages |
| Header, keywords, payload length, tail (mask + pairs) | same | same |
| Payload | 38-byte prefix + 1 DXBC | flags word + 6 (offset, size) pairs + SMOL-V streams |
| Where the subprograms are listed | per stage: types 15/16 under progVertex, 17/18 under progFragment, 19 under progGeometry | type 25, only under progVertex |
| Channel mask | channels of every non-SV ISGN element, including declared-but-unused ones (466/466) | the used inputs only |
| Pairs | `(channel, component)` for ISGN elements with a non-zero read mask (466/466) | same channel set |
| Second pair value | fixed per channel: ch0→0, 1→1, 2→2, 3→3, TEXCOORD0–7 (ch4–11)→5–12 | 13, 14, … in declaration order |

- The used-channel set is identical between D3D11 and the Vulkan entry with the same keywords in 465 of
  465 vertex programs that have a Vulkan partner.
- 146 vertex programs declare inputs they never read, for example TMP's TEXCOORD2/3. A D3D11 input
  layout has to supply those too (see the UNVERIFIED list).

### DXBC containers (960 of 960)

- **Magic and header:** magic `DXBC`, version field 1, and a total size equal to the container size
  in every case.
- **Checksum:** my recomputation equals the stored 16 bytes in **960 of 960** containers, with 0 bad.
  - The algorithm is MD5's compression over the container from byte 20, with a custom final block: the
    bit count first, `(bits >> 2) | 1` last.
  - This is the DXBC hash public implementations use, for example DirectXShaderCompiler's `DxilHash`
    (MIT). A wrong algorithm or a damaged container would not match.
- **Chunk sets:**

  | Chunks | Programs |
  |---|---|
  | ISGN, OSGN, SHDR | 760 |
  | ISGN, OSGN, SHEX | 91 |
  | ISG1, OSG1, SHEX, SFI0 | 64 (min-precision signatures, `Hidden/Internal-UIRDefault`) |
  | RDEF, ISGN, OSGN, SHDR, STAT | 34 |
  | ISGN, OSGN, SHEX, SFI0 | 6 |
  | ISGN, OSGN, SHDR, SFI0 | 5 |

- **RDEF and STAT are stripped** in 926 of 960 programs.
  - They survive only in `Klei/PostFX/ClusterCoverPostFX` and `TextMeshPro/Mobile/Distance Field`.
    There the RDEF creator is "Microsoft (R) HLSL Shader Compiler 10.1" and the targets are vs_4_0
    (0xFFFE0400) and ps_4_0 (0xFFFF0400). Why only these two keep it is UNVERIFIED.
  - Unity's own reflection agrees with those RDEFs:
    - 493 of 493 shared variable offsets are equal;
    - 80 of 80 cbuffer sizes are equal;
    - 114 of 114 bindings (cbuffer b#, texture t#, sampler s#) match.
    - The 1,665 RDEF variables missing from Unity's parameters all lack the `D3D_SVF_USED` flag.
- **Shader models:** only 4.0 and 5.0. The type tokens agree with the GPU program types in every case.

  | Program type | Shader model | Programs |
  |---|---|---|
  | 15 | vs_4_0 | 425 |
  | 16 | vs_5_0 | 41 |
  | 17 | ps_4_0 | 442 |
  | 18 | ps_5_0 | 51 |
  | 19 | gs_4_0 | 1 |

  There are no 4.1, 5.1 or level-9 programs.

### Bind info

Textures, samplers and constant buffers are described in Unity's **parameter blob**, plus the pass
stage's `m_CommonParameters`.
- **The blob.**
  - Every D3D11 subprogram has a parameter blob in the platform-4 table (`m_ParameterBlobIndices`).
  - It has the same format as the GLCore, Metal and Vulkan blobs: `ShaderParameters.Read` parses all
    960 with 0 failures.
- **`m_CommonParameters`** is non-empty only for `m_TextureParams`, `m_ConstantBuffers` and
  `m_ConstantBufferBindings`. `m_Samplers`, `m_UAVParams` and `m_BufferParams` are empty everywhere.
- **Slots.** In the merged result (common + blob) the indices are the D3D register numbers.
  Checked against the DXBC declarations (`dcl_resource*`, `dcl_sampler`, `dcl_constantbuffer`) in
  960 of 960 programs:
  - texture `Index` = t#, and structured buffers occupy t# too (`dcl_resource_structured`): 960/960;
  - `SamplerIndex` = s#: 960/960;
  - cbuffer binding `Index` = b#: 960/960;
  - the declared cbuffer size in vec4s × 16 is at most Unity's size rounded up to 16, for 2,517 of
    2,517 bindings.
- **Samplers are not tied to texture slots.** `Klei/PostFX/LightBufferCompositor`'s fragment program
  pairs `_MainTex` t0 with s1, `_LightBufferTex` t1 with s0, and so on: 16 textures and 16 samplers.
  16 is D3D11's sampler slot limit.
- **Vulkan indices are encoded differently.** Vulkan's blob indices are packed values, for example
  134217728 = 0x08000000 for binding 0 and `VGlobals` 0x04010001. D3D11's are plain slots.
- **The second int of a cbuffer binding** is 1 in 612 bindings and 0 in 2,106. It is mostly 1 in pixel
  programs and 0 in vertex programs. It is 0 in every Vulkan binding, and `ShaderParameters` says it is
  0 in every GLCore and Metal blob. Its meaning is UNVERIFIED; binding does not need it.
- **Constant-buffer member offsets** come only from Unity's blob, because RDEF is stripped. They are
  cross-checked only against the two RDEF shaders above (493/493 equal).

## 3. Program types and features

**Program types present:**
- vertex: 15 (SM4.0) and 16 (SM5.0);
- pixel: 17 and 18;
- geometry: 19 (SM4.0), in one program, the Unity built-in VR blit `Hidden/VR/BlitFromTex2DToTexArraySlice`
  s1p0.

**Program types absent:** hull and domain (21, 22) and SM5 geometry (20). The type names come from
AssetStudio's `ShaderGpuProgramType` enum (15–22 = DX11 VertexSM40/50, PixelSM40/50, GeometrySM40/50,
HullSM50, DomainSM50). The data confirms the names for 15–19.

**Absent features:**
- **Tessellation:** none. No hull or domain programs, and no HS or tessellation declarations
  (opcodes 113–116 and 147–154).
- **UAVs:** none. 0 `dcl_uav_*` declarations (156–158), and no store or atomic opcodes.
- **Compute:** no game shader is a compute program. `unity default resources` holds 9 ComputeShader
  objects (class 72), all engine-internal: Internal-Skinning, Internal-BlendShape, 2 × RayTracing
  AddInstances, 2 × VT TranslationTable, 3 × foveated shading-rate. Whether the game ever dispatches
  them is UNVERIFIED (probably not).

**Features used:**

| Feature | Where |
|---|---|
| MSAA reads: `dcl_resource` Texture2DMS / Texture2DMSArray (2, 4, 8 samples), `ld_ms`, `sampleinfo` | only `Hidden/BlitToDepth_MSAA` and `Hidden/FrameDebuggerRenderTargetDisplay` (_MSAA_* keywords) |
| Structured SRVs (`dcl_resource_structured`, 43 programs) | SM5 PROCEDURAL_INSTANCING_ON variants of `Particles/Standard Surface`, `Particles/Standard Unlit`, `Klei/Standard Unlit`, and SM4.0 `Hidden/Physics2D/SDF_*` |
| SM4.0 structured buffers (SFI0 0x2, global flags 0x9) | the Physics2D SDF ones; needs `ComputeShaders_Plus_RawAndStructuredBuffers_Via_Shader_4_x`, always true at FL 11_0 |
| SM5 (needs FL 11_0) | `Hidden/BlitToDepth`, `Hidden/BlitToDepth_MSAA`, the VR blit, and the PROCEDURAL_INSTANCING_ON variants above |
| SFI0 0x10 MinimumPrecision (ISG1/OSG1 signatures) | `Hidden/Internal-UIRDefault`, all 64 variants |
| SFI0 0x2000 ViewportAndRTArrayIndexFromAnyShaderFeedingRasterizer (SV_RenderTargetArrayIndex written by a VS; D3D11.3 options) | only STEREO_INSTANCING_ON vertex variants of 5 built-ins (GUI/Text Shader, Internal-Colored, Internal-Loading, InternalClear, InternalErrorShader) and the VR blit's SM5 VS |
| Comparison samplers / `sample_c(_lz)` | Standard, Legacy Shaders/Diffuse, Particles/Standard Surface (shadow variants) |
| Texture2DArray | Klei/BackWallArray, Klei/Liquid, FrameDebugger, VR/BlitTexArraySlice |
| Cube | 8 shaders |
| 3D | Hidden/ColorCorrection3DLut |
| MRT 3 (SV_Target0–2) | 27 Klei shaders |
| MRT 4 | Standard, Legacy Diffuse, Particles/Standard Surface |
| SV_Depth output | BlitCopyWithDepth, BlitToDepth(_MSAA), Internal-DebugPattern |
| `discard` | 45 shaders |
| Immediate constant buffers (customdata) | 7 Klei/BatchedAnimation* and AnimationSimple shaders |
| Dynamically indexed cbuffers | 12 shaders |
| SV_InstanceID input | 13 shaders |

**Notes on the SFI0 and feature-level rows:**
- The SFI0 bit values are DXC's `ShaderFeatureInfo_*` constants (DxilConstants.h, fetched). Each is
  consistent with the data:
  - 0x2 sits on exactly the SM4.0 programs that use structured buffers;
  - 0x10 sits on exactly the ISG1 programs;
  - 0x2000 sits on exactly the VS programs that output SV_RenderTargetArrayIndex.
- The FL 11_0 requirements are from Microsoft's "Direct3D feature levels" documentation (not fetched in
  this session).

**Minimum device: feature level 11_0** covers every program the game would plausibly use. The
STEREO_INSTANCING (XR) variants also need D3D11.3 `VPAndRTArrayIndexFromAnyShaderFeedingRasterizer`.
The Vulkan device already skips XR stereo variants (ARCHITECTURE §3.5).

## 4. Keyword variants: D3D11 compared with Vulkan

- **Vulkan's keyword sets equal GLCore's** in 175 of 175 passes. Vulkan and GLCore store one program
  per variant, keyed by the union of keywords.
- **D3D11 stores one program per stage, keyed only by the keywords that stage depends on**, as Metal
  does (docs/research/macos-feasibility.md §1.3). Counts:
  - 527 vertex keys, 540 fragment keys, 1 geometry key;
  - against 590 Vulkan keys.
  - Vertex keys equal the Vulkan set in 169 of 175 passes; fragment keys in 167.
- **The mapping check** (`out-game/keywords.txt`) works per Vulkan key K and per stage S. It takes
  U_S, the union of every keyword in S's D3D11 keys, and computes K ∩ U_S.
  - That set is an existing D3D11 stage key for **590 of 590** Vulkan keys, in both vertex and
    fragment.
  - Every D3D11 key is a subset of some Vulkan key (175/175 passes).
  - So per-stage best-match selection (as `ShaderPrograms.SelectVariant` does for Metal) always finds
    an exact stage program.
- **Two passes have Vulkan keywords that no D3D11 stage depends on.**
  - `Klei/PostFX/LightBufferCompositor` s0p0: `DISABLE_TEMPERATURE_PARTICLES`.
    - D3D11 has only the `[]` vertex and fragment programs.
    - The fragment program declares all 16 textures, including `_BlurredTemperature`, `_FrostTex` and
      `_EmberTex`.
    - Vulkan's `[DISABLE_TEMPERATURE_PARTICLES]` program is a different, smaller program (8 textures).
    - So on D3D11 that toggle has no effect: the temperature-particle path is always compiled in, as
      the original Windows game draws it. This matches the Metal set.
    - This is the only keyword game code toggles (macos-feasibility.md).
  - `Klei/BatchedAnimationUI` s0p0: `DISABLE_K_LIGHTING` and `UNITY_UI_ALPHACLIP`. D3D11 has only
    `[]`. The Vulkan entries for those keys differ from `[]` by 24 bytes, which is exactly the
    keyword-list length; whether their code is identical is UNVERIFIED.
- The one D3D11-only pass is `Hidden/VR/BlitFromTex2DToTexArraySlice` s0p0.

## 5. Mods (`out/ws`, 2,898 mod folders at survey time)

**Totals:**
- 413 shader objects in 38 mods.
- **173 objects, in all 38 mods, carry platform 4.** The rest are `15,18` (124), `14` (103) and
  `14,15` (13).
- A format-agnostic scan (decompress the blob segments, find each DXBC, verify its checksum) found
  2,573 DXBC containers, **every one with a valid checksum**.
- Shader models are only vs/ps 4.0 and 5.0. No geometry, hull or domain programs.

**Entry-layout compatibility with U60's D3D11 reader:**

| Unity | Objects | Result |
|---|---|---|
| 2022.3.45f1 | 53 | all 463 entries read as U60's layout (38-byte prefix, checksum OK, no trailing bytes) |
| 6000.3.5f2 | 18 | all 87 entries read as U60's layout |
| 6000.3.8f1 | 10 | all 94 entries read as U60's layout |
| 6000.3.11f1 | 1 | all 8 entries read as U60's layout |
| 2020.3.x (.11f1 2, .25f1 6, .30f1 53, .30f1c1 19) | 80 | do **not** read as U60's layout |
| 2018.3 / 2018.4 | 11 | older table (flat offsets per platform); our `ShaderProgramTable` cannot load it, though the DXBC inside is valid |

- **What differs in 2020.3 entries.** Example: `2787598894/assets/windows/goldenthroneassets`.
  - The magic is 0x0C07513C, not 0x0C0A75BA.
  - The 38-byte prefix (02 00 04 00 …) is still there before the DXBC.
  - The constant-buffer and binding list is inline after the container, not in a parameter blob.
  - A reader for this is plausible; its exact layout is UNVERIFIED.

**Eight mods have shaders with D3D11 programs and no GLCore or Vulkan programs in any of their
bundles.** Today neither device draws these on any OS.

| Mod | Name (from mod-bundle-shaders.md, where listed) | Shaders |
|---|---|---|
| 2815406414 | True Tiles | TMP ×2 |
| 2968184715 | Portal Network | Cartoon FX ×3 |
| 2990227046 | — | Beached/LiquidRefraction |
| 3152316922 | Spring Festival Pack | Standard + Cartoon FX ×2; its linux, mac and windows bundles are all D3D11-only |
| 3290446954 | — | TMP ×2 |
| 3519596146 | Proper Cyrillic Fonts | TMP ×2 |
| 3732422991 | StorageNetwork | TMP ×2; its linux and mac bundles are D3D11-only too |
| 3790434066 | — | Cartoon FX ×3 |

The repo doc's earlier 5 are included; 2990227046, 3290446954 and 3790434066 are new to this list.

**29 more mods ship a Windows-named bundle that is D3D11-only, while another of their bundles has
GLCore or Vulkan.** Moono reports `WindowsPlayer`, so on Windows these mods probably load the D3D11
bundle and their shaders are not drawn by our GL or Vulkan devices. Which bundle each one loads is
UNVERIFIED. The mods:

2119648603, 2660131829, 2787598894, 2837919908, 2856555858, 2869608898, 2921809973, 2947429297,
2966940337, 2977728791, 3046440992, 3135364297, 3179880371, 3281716506, 3371129389, 3427974009,
3433108302, 3468585385, 3478214979, 3519386331, 3557584850, 3561866116, 3565090654, 3568684741,
3630759126, 3747431307, 3753518460, 3782701870, 3826547280.

The full bundle list is in `mods-summary.txt`. With the 8 above, 37 of the 38 shader-carrying mods
have D3D11-only content somewhere.

## Sources

- **Measurements:** the tool and outputs above. The key commands:
  - `survey /mnt/e/oni-builds/oni-win-764025/OxygenNotIncluded_Data out-game`
  - `rdefcmp DATADIR`
  - `common DATADIR`
  - `classes DATADIR`
  - `compute DATADIR`
  - `bundles out/ws > mods.tsv`
- **AssetStudio** `AssetStudio/Classes/Shader.cs`: `ShaderGpuProgramType` and `PassType` enums
  (fetched: https://raw.githubusercontent.com/Perfare/AssetStudio/master/AssetStudio/Classes/Shader.cs).
- **DirectXShaderCompiler** `include/dxc/DXIL/DxilConstants.h`: the `ShaderFeatureInfo_*` values
  (fetched). `include/dxc/DxilContainer/DxilContainer.h` gives the FourCCs (fetched). The `DxilHash`
  DXBC checksum was not fetched; the algorithm is confirmed by 960/960 + 2,573/2,573 matches.
- **Windows SDK `d3d11TokenizedProgramFormat.hpp`:** opcode and operand numbering. Not fetched; the
  values are from memory and consistent with the data:
  - DCL_INPUT 95, DCL_OUTPUT_SIV 103, MOV 54, RET 62, DCL_SAMPLER 90;
  - DCL_RESOURCE 88 with dimension bits 11–15 = 3 for Texture2D, SAMPLE 69;
  - the token streams of 960 programs walk cleanly.
- **Microsoft Learn:** "Direct3D feature levels", `D3D11_FEATURE_DATA_D3D10_X_HARDWARE_OPTIONS`,
  `D3D11_FEATURE_DATA_D3D11_OPTIONS3`, `ID3D11Device::CreateInputLayout`. Not fetched in this session.
- **Repo docs:** docs/research/assets.md, docs/research/mod-bundle-shaders.md,
  docs/research/macos-feasibility.md §1.3–1.4, and ARCHITECTURE §3.5.

## UNVERIFIED

1. The meaning of the four entry statistics ints (they only correlate with instruction counts).
2. Prefix byte 5 = 3 on the geometry program (guessed to be the GS input primitive), and bytes 4–37 in
   general (zero elsewhere).
3. Why `ClusterCoverPostFX` and `TMP/Mobile/Distance Field` keep RDEF and STAT.
4. The meaning of the cbuffer binding's second int (0 or 1).
5. The meaning of the D3D11 pair's second value (0, 1, 2, 3, 5–12 per channel) as a Unity vertex
   component code; component 4 is never used.
6. That a D3D11 input layout must supply declared-but-unused ISGN elements (146 vertex programs have
   them). From memory of `CreateInputLayout` behaviour; not checked against the documentation.
7. GPU program type 33 (63 subprograms in `unity default resources`; probably WebGPU, not in the
   AssetStudio enum). Platform 26's identity.
8. Whether the 9 engine ComputeShaders are ever dispatched by the game.
9. Whether `Klei/BatchedAnimationUI`'s Vulkan keyword variants are code-identical to `[]` (D3D11 has
   only `[]`).
10. The 2020.3 mod entry layout beyond magic, prefix and DXBC position, and the 2018 table layout.
11. Which bundle each mod loads on Windows.
12. The feature-level and D3D11.3-option requirements, taken from Microsoft documentation from memory
    (not fetched here).
13. The exact meaning of D3D11 global-flags bits 0x1 and 0x8 (refactoringAllowed and
    enableRawAndStructuredBuffers by the tokenized-format header, from memory).
