# Unity API gaps

What Unity's public API has and ours lacks, which of it the game and Workshop mods use, and what
was done about it (2026-11-05). Behaviour of every member added was measured in the original
player with UnityProbe's `apigaps` scenario (`tools/UnityProbe/ApiGaps.cs`,
`tests/e2e/probe-apigaps.yaml`, `tools/UnityProbe/ag-check.py` to compare the two players' lines).

## How the gaps were found

`scripts/apisurface.sh` (`tools/ApiCheck --surface`) compares, by name and signature from metadata
only, every public type and member of the Unity assemblies the game ships (the frozen U59 install,
`~/projects/oni-linux`; U60's, `~/projects/oni-linux-761211`, gives the same mod-referenced list)
with the engine and our UI packages, then reads the IL of the Workshop corpus in `out/ws` (2,898
items, static metadata reading only; nothing is loaded or run). A mod's DLLs are those
`KMod.DLLLoader` could load: the top of the mod's folder and of each `archived_versions/*`
folder, without copies of Unity's or the game's assemblies. Scanning every DLL under a mod
instead finds 456 gaps (268 types), almost all in copies of `UnityEngine.*Module.dll` left in
mods' `bin/` folders, which the game never loads.

Unity's surface: 3,490 public types and 37,061 members in 75 assemblies. Ours lacked 2,749 types
(22,201 members) and 5,704 members of types it has (515 of them `[Obsolete]`). The game's own
assemblies use none of them (`scripts/apicheck.sh`: 0 missing). Workshop mods referenced 62 of
them (19 types) from 24 mods; 21 more references name members the shipped assemblies do not have
either (built against older game versions: TMP's `FaceInfo`, `TMP_FontAsset.fontInfo`), which
fail in the original too.

String literals naming a missing member of a type the same mod references (a hint of reflection
by name) matched 145 members; the ones looked at are common words (`Scale`, `Invoke`, `Update`,
`identifier`, `Assert`) in Harmony patches and logging, not reflection on Unity members. The list
is in the surface report's `MOD STRINGS` section and its JSON's `modReflection`.

## The three known gaps

- `RenderTexture.active = rt` now binds rt as `Graphics.SetRenderTarget` does. Measured: GL.Clear,
  GL immediate drawing and ReadPixels after it go to rt, also when another target was bound with
  SetRenderTarget; the viewport covers the whole target (also when rt was active already, after a
  2 × 2 `GL.Viewport`); a texture not created is created; null is the back buffer. `active` is
  the target after `Graphics.SetRenderTarget` and after `Graphics.Blit` (with or without a
  material); inside a camera's `onPostRender` it is the camera's target texture; after
  `Camera.Render()` it is what it was before (null or a texture), and GL.Clear goes there; the
  next frame it is null. Ours differed only inside `onPostRender` (it kept the previous target)
  and in binding nothing on a set. The binding is the engine's, so GL, Metal and u60's Vulkan
  device all follow it.
- Shader property introspection: `FindPropertyIndex`, `GetPropertyNameId`, `GetPropertyFlags`,
  `GetPropertyDescription`, `GetPropertyAttributes`, `GetPropertyDefaultFloatValue`,
  `GetPropertyDefaultVectorValue`, `GetPropertyDefaultIntValue`, `GetPropertyRangeLimits`,
  `GetPropertyTextureDimension` and `GetPropertyTextureDefaultName`, from the parsed property
  block. Measured on the 82 shaders the original has loaded at the main menu: every getter on
  every property of the 77 both players load gives the same value or the same exception (the
  typed getters throw ArgumentException "Property type is not Float or Range." / "Color or
  Vector." / "Range." / "TexEnv." / "Texture." / "Int."; an index out of range
  ArgumentOutOfRangeException for `propertyIndex`); `FindPropertyIndex` is case-sensitive and -1
  for unknown names, "" and null; the default texture name is as declared ("grey" stays "grey").
- RenderTexture constructors: `(w, h, depth, RenderTextureFormat, RenderTextureReadWrite)`,
  `(w, h, depth, RenderTextureFormat, int mipCount)`, `(w, h, depth, DefaultFormat)`,
  `(w, h, depth, GraphicsFormat)`, `(w, h, depth, GraphicsFormat, int mipCount)`,
  `(w, h, GraphicsFormat, GraphicsFormat)` and `(w, h, GraphicsFormat, GraphicsFormat, int)`, with
  `depthStencilFormat`, `stencilFormat`, `dimension` (and `Texture.dimension`) and
  `SystemInfo.GetGraphicsFormat(DefaultFormat)`. Measured (gamma colour space): none of them
  checks its arguments; readWrite sRGB gives sRGB false; BGRA32 falls back to ARGB32 with a
  warning; a depth format as the colour is an error and R8G8B8A8_UNorm; B8G8R8A8, compressed
  formats and YUV2 (DefaultFormat.Video) are an error and a destroyed object; an invalid depth
  format is an error and D24_UNorm_S8_UInt (D16_UNorm_S8_UInt silently so); DepthStencil and
  Shadow use 24 and 16 bits whatever the depth; mipCount is the descriptor's (-1 for 0 or less)
  and `useMipMap` keeps it; Create fails, with the original's errors, for a size of 0, a mip
  count over the full chain and no format at all; an sRGB GraphicsFormat warns at Create. Found
  on the way and fixed: runtime render textures (constructed, from a descriptor, temporary) clamp
  (ours repeated), read `isReadable` true, temporaries have anisoLevel 0, and the descriptor's
  mipCount is -1 without a request (ours: 1).

## Mod-referenced gaps

Count is the mods referencing the member from a loadable DLL (the game itself uses none). After
this work 42 of the 62 references (10 types) remain, from 13 of the 24 mods; Mod Manager, bottle
emptier extend, telescope auto research, generic fabricator, starship, Support Package+,
UpgradeConduits, Lightning fast Menus, Futur's Timelapser, Clean Memory & Performance Capture and
God Tools reference none.

| Member | Mods | Mods (rank in the catalog) | Done |
|---|---|---|---|
| `UploadHandler`, `UploadHandlerRaw`, `UnityWebRequest.uploadHandler` | 4 | Mod Manager (#26), bottle emptier extend (#63), telescope auto research (#301), generic fabricator | fixed |
| `UnityWebRequest.Post(string, string)` | 3 | bottle emptier extend, telescope auto research, generic fabricator | fixed |
| `Input.inputString` | 3 | Refreshpicks (#1479), Superduplicant (#1499), Custom Material Costs | open: typed characters reach the engine in `GlfwPlatform.cs`'s input feed, which other work is changing |
| `VideoPlayer` source, url, renderMode, timeUpdateMode, sendFrameReadyEvents, errorReceived, frameReady, width, height | 2-3 | Rookie 100-Day Quest Guide (#943), Modular Power (#1865), Extra Twitch Events | types fixed (Modular Power's fields of ErrorEventHandler kept its types from loading); members open: playing from a URL needs a file source in the video engine (only VideoClips play); measured so far: a missing file raises errorReceived "VideoPlayer cannot play url : URL\nCannot read file.", an .mp4 "VideoPlayer cannot play url : URL", url reads "" with a VideoClip source |
| `ParticleSystem.Burst`, `EmissionModule.SetBurst`/`burstCount`/`rateOverTimeMultiplier`, `MainModule.startSize`, `velocityOverLifetime` | 2-3 | Metal Bird Volcano (#2758), Metal Hatch Volcano (#2580), Extra Twitch Events | open: velocity over lifetime needs the simulation; measured in the probe's "ps" part (burst clamps and errors, burstCount 0 to 8, defaults, local and world space velocity) |
| `GUILayout.SelectionGrid` (2 overloads), `GUILayout.PasswordField` | 2 | ModTools (#1676), AI Advisor | open: measured rects in the probe's "gui" part (grid rows 42 high for two rows, 0 for xCount 0 with a warning) |
| `DownloadHandlerBuffer`, `DownloadHandlerFile` | 1 | Mod Manager (#26) | fixed |
| `UnityWebRequestTexture`, `DownloadHandlerTexture` | 1 | starship (#1643) | fixed |
| `NativeArray<T>(int, Allocator, NativeArrayOptions)`, `Allocator`, `NativeArrayOptions` | 1 | Support Package+ (#717) | fixed |
| `SpriteRenderer.drawMode`, `SpriteDrawMode` | 1 | UpgradeConduits (#2103) | fixed (Tiled is kept and drawn as Sliced) |
| `AnimationCurve.RemoveKey` | 1 | Lightning fast Menus (#492) | fixed |
| `AnimationCurve.SmoothTangents` | 1 | Extra Twitch Events | fixed |
| `Camera.ResetCullingMatrix` | 1 | Futur's Timelapser | fixed, with `cullingMatrix` |
| `GarbageCollector.CollectIncremental`, `incrementalTimeSliceNanoseconds` | 1 | Clean Memory & Performance Capture | fixed |
| `Collider.bounds`, `Collider2D.bounds` | 1 | God Tools (#1789) | fixed, with BoxCollider's and SphereCollider's centre and size |
| `Graphics.CopyTexture(Texture, Texture)` | 1 | Configurable Build Menus (#1402) | open: needs a device copy in the GL and Vulkan devices; measured: a readable source copies CPU and GPU data, a non-readable one only the GPU's (Apply then uploads the old CPU data), mismatched sizes, mip counts or nulls are errors and copy nothing |
| `AudioSource` playback, `AudioClip.Create` | 1 | Alert Voice | open: Unity's own audio output does not exist here (the game plays through FMOD) |
| `Rigidbody2D.AddForce`, `AddTorque`, `angularVelocity` | 2 | Extra Twitch Events, Chaos Events for Twitch Integration (#1817) | open: no 2D physics simulation |
| `TrailRenderer`, `ParticleSystem.Particle` triggers | 1 | Extra Twitch Events | open: no trail rendering, no particle collision |

Smaller differences the probe showed and that were left: PhysX widens 3D collider bounds by a few
ulps (1.3660257 against 1.3660254); a render texture asked for D16_UNorm_S8_UInt gets
D24_UNorm_S8_UInt but its descriptor reports the request (ours reports what it got); a sprite
renderer's bounds have a z extent of 0.1 in the
original (0 here); the original logs "ReadPixels was called to read pixels from system frame
buffer, while not inside drawing frame." and "Releasing render texture that is set to be
RenderTexture.active!", and warns that a tight-mesh sprite may not tile well; Mono prints `R`
floats with nine digits (the same floats).

## Unreferenced gaps

Nothing in the game or the corpus references the rest; it is not implemented. By assembly,
before this work (types missing, their members, members missing from present types; after it,
2,736 types with 22,123 members and 5,649 members of present types remain, the open rows above
included):

| Assembly | Missing types | Their members | Missing members |
|---|---|---|---|
| UnityEngine.CoreModule | 891 | 6,024 | 2,565 |
| UnityEngine.UIElementsModule | 584 | 4,091 | 326 |
| UnityEngine.Physics2DModule | 184 | 2,866 | 538 |
| UnityEngine.ParticleSystemModule | 67 | 678 | 522 |
| UnityEngine.PhysicsModule | 83 | 855 | 342 |
| UnityEngine.AnimationModule | 67 | 744 | 176 |
| Unity.TextMeshPro | 50 | 286 | 379 |
| UnityEngine.AudioModule | 84 | 478 | 111 |
| Unity.Timeline | 45 | 306 | 83 |
| UnityEngine.TextCoreTextEngineModule | 11 | 151 | 191 |
| UnityEngine.IMGUIModule | 15 | 82 | 96 |
| UnityEngine.TilemapModule | 20 | 148 | 92 |
| UnityEngine.VideoModule | 11 | 54 | 57 |
| UnityEngine.UI | 13 | 75 | 31 |
| 37 modules of which ours has no type (AI, AndroidJNI, Terrain, VFX, XR, analytics, the web request asset-bundle, audio and WWW modules, …; the texture one before this work) | 588 | 5,139 | 0 |
| 11 more (AssetBundle, Grid, InputLegacy, SpriteShape, UnityWebRequest, UI, …) | 36 | 224 | 195 |

The present types missing the most members: CommandBuffer (335), Physics2D (297), TMP_Text (197),
Physics (195), Mesh (183), Graphics (121), Animator (117), QualitySettings (115), Rigidbody2D
(114), VisualElement (112), Camera (110), Rigidbody (103), SystemInfo (101), Light (96),
ParticleSystem.ShapeModule (92), AudioSource (91). The full list, member by member, is
`out/apisurface.txt` after `scripts/apisurface.sh`.
