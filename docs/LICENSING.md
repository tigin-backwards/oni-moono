# Unity-originated code and data

An inventory of what this engine still takes from Unity Technologies at run time, for deciding
what has to be replaced before the result can be distributed without Unity's player. It is a
technical inventory, not legal advice: licence terms must be checked against the exact package
versions shipped with the game.

## Replaced by this project (never loaded)

| Unity component | Replacement |
|---|---|
| `UnityPlayer.so` (the native engine) | `OniMoono.Host`, `OniMoono.Desktop` (BRUTAL GLFW/OpenGL) |
| `UnityEngine.dll`, the 77 `UnityEngine.*Module.dll` | `OniMoono.UnityEngine` and generated forwarding facades |
| `Unity.InternalAPIEngineBridge.004.dll` | `src/Unity.InternalAPIEngineBridge.004` |
| `UnityEngine.UI.dll`, `Unity.TextMeshPro.dll`, `Unity.Timeline.dll` | `src/UnityEngine.UI`, `src/Unity.TextMeshPro`, `src/Unity.Timeline` (clean-room, PLAN M9; below) |
| Mono runtime and class libraries | stock .NET 10 CoreCLR |
| `Resources/UnityPlayer.png` | not used |

## Unity packages in the game's `Managed/` folder

These are Unity *packages* compiled into the game build; the game's own code references them.

| Assembly | Role in the game | Loaded |
|---|---|---|
| `UnityEngine.UI.dll` (uGUI) | every menu, button, panel, layout, scroll view, mask | no: replaced by `src/UnityEngine.UI` (PLAN M9) |
| `Unity.TextMeshPro.dll` | all text layout and rendering | no: replaced by `src/Unity.TextMeshPro` |
| `Unity.Timeline.dll` | referenced; the game ships no Timeline content | no: replaced by `src/Unity.Timeline` |

The replacements were written clean-room from functional specifications
(`docs/cleanroom/`); their implementers did not read the shipped code. Their type headers and
serialized fields are generated from the shipped assemblies' metadata, which the game's data
binds to. The shipped packages are never loaded; the build tools read their metadata only.

| Assembly | Role | Loaded |
|---|---|---|
| `Unity.Collections.dll`, `Unity.Collections.LowLevel.ILSupport.dll`, `Unity.Burst.dll`, `Unity.Burst.Unsafe.dll`, `Unity.Mathematics.dll`, `Unity.Profiling.Core.dll`, `Unity.MemoryProfiler.dll`, `Unity.Multiplayer.Center.Common.dll` | not referenced by any game assembly (only by each other) | never; can be left out. The game's `NativeArray` use is `UnityEngine.CoreModule`'s, which this engine implements |

Current versions of uGUI, TextMeshPro and Timeline are, to our knowledge, distributed under the
Unity Companion License (use only with the Unity engine), which is why they are replaced.

## Unity data the game reads

### Built-in resource files (`Resources/`)

Objects a full scripted session loaded from them (menus, new game, colony, every overlay, the
research screens), recorded with `--report-builtin-assets FILE`:

| File | Object |
|---|---|
| `unity default resources` | Font `LegacyRuntime` (with `Font Material`, `Font Texture`) |
| `unity default resources` | Shaders `GUI/Text Shader`, `Hidden/InternalErrorShader` |
| `unity default resources` | Meshes `Plane`, `Sphere` |
| `unity_builtin_extra` | Shader `Hidden/BlitCopy` |

The engine creates its own built-in textures (`UnityWhite` and the other default textures), so
none are read from these files.

### Unity shaders compiled into the game's asset files

Unity's standard shaders the game uses live in its own asset files (`sharedassets0.assets`,
`sharedassets2.assets`, `resources.assets`, `globalgamemanagers.assets`): `UI/Default`,
`UI/Default Desat`, `Sprites/Default`, `Sprites/Mask`, `Unlit/Transparent`, `Standard`,
`Legacy Shaders/*`, `Particles/*`, the `Hidden/*` internal shaders (Blit, GUI, video decode and
composite), and the TextMeshPro shaders (`TextMeshPro/Distance Field` and variants,
`TextMeshPro/Sprite`). Unity has published the source of its built-in shaders, under MIT as far
as we know, so they can be rebuilt from source; the TextMeshPro shaders follow the TextMeshPro
package's licence.

## Not Unity's

Klei's game code and assets; `libSimDLL.so`; FMOD (`libfmodstudio.so`, FMOD's own licence);
Steamworks (`libsteam_api.so`, Steamworks.NET under MIT); `cimgui.so` (Dear ImGui, MIT).

## Third-party code in this repository

- `src/OniMoono.UnityEngine/Video/Engine/Vp8/`: a port of the VP8 reference decoder ("dixie")
  published in RFC 6386, Section 20, © 2010–2011 Google Inc., BSD-style licence with an
  additional patent grant (`src/ThirdParty/rfc6386/LICENSE`, `PATENTS`).
- `src/ThirdParty/Steamworks.NET`: Steamworks.NET 20.1.0, MIT.
- `src/OniMoono.UnityEngine/Math/MesaInverse.cs` (`Matrix4x4.inverse`): a port of
  `invert_matrix_general` from Mesa's `src/mesa/math/m_matrix.c` (Mesa 20.3.5, from the
  release tarball at archive.mesa3d.org; Mesa 21 replaced it with `util_invert_mat4x4`),
  © 1999–2005 Brian Paul, MIT. The notice is in the file's header and in `NOTICE.txt`, which
  ships beside the player. The original player's inverse was measured to equal it bit for bit
  (`docs/cleanroom/ugui.md`, "Transform hierarchy to the bit").
- `src/OniMoono.UnityEngine/Math/MesaFrustum.cs` (`Matrix4x4.Frustum`): a port of
  `_math_matrix_frustum` from the same Mesa 20.3.5 file, same copyright and MIT notice.
- `src/OniMoono.UnityEngine/Math/GluLookAt.cs` (`Matrix4x4.LookAt`): the basis arithmetic of
  `gluLookAt` and its `normalize` and `cross` helpers from GLU's `src/libutil/project.c` (GLU
  9.0.3, release tarball at archive.mesa3d.org), © 1991–2000 Silicon Graphics, Inc., SGI Free
  Software License B 2.0 (X11-equivalent). The notice is in the file's header and in
  `NOTICE.txt`. Both were measured to equal the original's bit for bit
  (`docs/cleanroom/ugui.md`, "Camera and projection math").
- `src/OniMoono.NativeHost` (the Windows `OniMoono.exe`) links `libnethost.lib` and includes
  `nethost.h` and `hostfxr.h` from the .NET SDK's `Microsoft.NETCore.App.Host.win-x64` pack
  (dotnet/runtime, © .NET Foundation and Contributors, MIT). The notice is in `NOTICE.txt`.
- `src/OniMoono.NativeHost/fonts/`: Gabarito (© 2023 The Gabarito Project Authors) and Red Hat
  Mono (© 2024 The Red Hat Project Authors), SIL OFL 1.1, as onimaxxing.com subsets them
  (Latin-1 and the punctuation it uses), cut to static weights for the crash window
  (`crash_dialog.cpp`). The crash window also draws the GitHub mark from Octicons (© GitHub Inc.,
  MIT). Both notices are in `NOTICE.txt`.
- `src/OniMoono.Assets/Shaders/SmolV.cs` (the Vulkan device reads Unity's SMOL-V-compressed
  SPIR-V): a port of `smolv::Decode` and its opcode table from Aras Pranckevičius' smol-v
  (`source/smolv.cpp`, github.com/aras-p/smol-v, master as of 2026-10-30, file SHA-256
  99430333…6e017e2), © 2016–2024 Aras Pranckevicius, dual-licensed MIT or public domain (used
  under MIT). The notice is in the file's header and in `NOTICE.txt`.
- Native libraries from BRUTAL's packages (GLFW, glad, stb, FreeType with libpng and zlib, Vulkan
  Memory Allocator, and shaderc with glslang, SPIRV-Tools and SPIRV-Headers, which the Vulkan
  device uses to compile mods' GLCore shaders that carry no Vulkan programs U60 reads), under
  their own licences as shipped in those packages (shaderc's: Apache-2.0, BSD-3-Clause and
  others, listed in the package's `LICENSES/BRUTAL-SHADERC-THIRD-PARTY-NOTICES.txt`).

## Provenance of the bundled type trees

`src/OniMoono.Assets/Data/typetrees-6000.3.17f1.json.gz` describes how Unity 6000.3.17f1 lays
out each native class's fields in a release build. The game's data files don't include this
information, so the asset reader needs it. It is a compacted copy (field names, types, sizes and
flags only) of the `InfoJson/6000.3.17f1.json` dump in AssetRipper's public TypeTreeDumps
repository (github.com/AssetRipper/TypeTreeDumps). That repository produces its dumps with
the TypeTreeDumper tool, running the Windows Unity Editor from Unity's download archive.

Neither TypeTreeDumps nor the dumper's output states a licence (AssetRipper's Tpk packer, which
redistributes the same data, is MIT for its code only). The data is machine-generated from
Unity's editor, so any rights in it would be Unity's rather than AssetRipper's. It is a factual
description of a file format, used here only to read the game's files (interoperability).
Neither of the obvious workarounds changes that:
- A dump made by this project with the same dumper is the same data, generated from the same
  editor.
- The game's own data cannot replace it. Its asset bundles carry type trees for only 8 of the 57
  native classes the data uses (GameObject, Transform, Material, Texture2D, Shader, TextAsset,
  MonoScript, AssetBundle; measured 2026-10-18 on the Windows install with every DLC). The
  `.assets` files carry none.

**Decision (2026-10-18):** the data ships. Under US law, facts and file-format structure are
not copyrightable, and a mechanical dump has no creative authorship. The project ships only the
compact subset needed to read the game's files. EU database right is out of scope for this
project.

## Provenance of the engine's code

The engine reimplements Unity's API. Public type and member names, signatures and serialized
field names follow Unity's, because the game's assemblies bind to them and its data names them.
They are generated from the shipped assemblies' metadata by `tools/SurfaceGen`, with no method
bodies.

How the engine's behaviour was found:
- **Unity's documentation.**
- **Measurement** of the original player: `tools/UnityProbe` and the side-by-side comparisons in
  `docs/PARITY.md`.
- **The game's own data and code** (Klei's assemblies).

Unity's native engine was never read.

A 2026-10-18 audit found regions written too closely after Unity's own managed code:
- the IMGUI module;
- UnityEvents;
- the UI Toolkit event classes and their runtime state;
- TextGenerator's caching front;
- the object pools;
- a few maths and colour helpers;
- some small routines.

They are rewritten clean-room. Specifications are written from behaviour, and a separate
implementation works from the specifications alone, without the old code
(`docs/cleanroom/README.md`, one specification per region).

Rules for new engine code:
- No code is taken from Unity's C# reference source (reference-only licence) or from decompiled
  Unity assemblies.
- Private structure and names are the project's own.
- When behaviour has to match Unity, the source is the documentation or a measurement, and the
  code says which.

## BRUTAL's terms

The BRUTAL License asks every distributed copy for BRUTAL's copyright notice in the credits or
equivalent (§2.1: the README), its logo before gameplay (§2.2: drawn on the game's main menu by
`BrutalCredit`, unaltered, as large as the menu's news tiles), and `NOTICE.txt` alongside (§2.3:
copied next to the player by the build, so every setup and release carries it). The logo and
notice are Ahwoo's, from ahwootech/brutal-branding. `NOTICE.txt` also carries Mesa's MIT notice
and GLU's SGI Free Software License B notice after BRUTAL's (above).

## This project's license
All Rights Reserved.
