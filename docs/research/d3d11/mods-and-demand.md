# D3D11 research M0, part B: mods that depend on the graphics API, and demand

Read-only on the repo. All mod files and issue attachments were read as data only: no mod code ran, Python
ran with `-I`, and zips were opened with `zipfile` into separate directories. Anything I could not prove
is marked **UNVERIFIED**.

Scratch outputs are in `scratchpad d3d11-research/`:

- **`modscan/`:**
  - `Program.cs` is a throwaway System.Reflection.Metadata IL scanner.
  - `scan.tsv` holds every hit; `modhits.tsv` holds the hits with game and engine copies filtered out;
    `plat.tsv` holds the `Application.platform` call sites.
  - `dec/` holds `ilspycmd` decompiles of 48 mod DLLs.
- **`demand/zips/`** holds the text logs from the issue zips. Dumps and saves were not extracted.

## Summary

1. **No mod calls into the graphics API directly.**
   - No P/Invoke into d3d11, dxgi, opengl32 or vulkan, in 3,641 unique DLLs (4,581 files, 940 duplicates)
     across 2,898 mod folders.
   - No `GetNativeTexturePtr`, `CreateExternalTexture`/`UpdateExternalTexture`, `GetNativeDepthBufferPtr`,
     `ComputeShader`/`Dispatch`, `GL.GetGPUProjectionMatrix`, `usesReversedZBuffer` or
     `graphicsUVStartsAtTop` in mod-authored code.
   - Exactly one mod reads `SystemInfo.graphicsDeviceType`, and only to log it (Magnet, below).
2. **Bundle choice depends on `Application.platform`, not the graphics API.**
   - Every one of the 37 mods selects its bundle from `Application.platform`, or loads one bundle on every
     OS.
   - Moono reports `RuntimePlatform.WindowsPlayer` on Windows (`src/OniMoono.UnityEngine/Core/Application.cs:21`).
   - All 29 "D3D11-only Windows bundle" mods therefore load that bundle on Windows today, as does the
     original. This closes UNVERIFIED item 11 of `programs.md`.
3. **Whether a mod loses something visible depends on what it does with the bundle's shaders.**
   - **No visible loss: 22 of the 37.** These mods ship D3D11-only TextMeshPro or particle shaders but swap
     them for the game's own before drawing. They use UtilLibs/FUtility `TMPConverter.ReplaceAllText`, or
     replace particle materials with `Shader.Find("Klei/BloomedParticleShader")`.
   - **Visible loss on Windows today: 9 mods.** Shaders that are not drawn under Moono's OpenGL or Vulkan
     device on Windows: Backwalls ×2 (shiny/rainbow/custom walls), Forcefield Projectors (the shield),
     Chaos Events (fog/fire), Capsule 2.0 (glows), Aki's Extra Twitch Events (pixel/dither/egg effects),
     Simplified Chinese font replacement (its replaced labels), Japanese Font Patch 3135364297 (under a
     Japanese translation), and Magnet under OpenGL only.
   - **Lost on every OS: 4 mods.** Portal Network, PortalNetwork+, Spring Festival Pack and Twitchery's
     LiquidRefraction have D3D11 programs and nothing else.
   - **Broken anyway: 4 mods.** Two TMP `FaceInfo` font loaders and two mods that fail in the original too.
   - **One log already shows it.** Issue #102's Windows/Vulkan Player.log has 9 `vulkan: shader … is not
     drawn (no Vulkan programs)` lines. They are for exactly the Capsule 2.0 and Portal Network shaders.
4. **Demand.** We hold 10 distinct user Player.logs from 8 issue reports, all on Windows:
   - 3 logs from 2 reports are from OniBrutal builds that came before the renderer choice existed, so they
     have no `renderer:` line.
   - Of the 7 logs with a `renderer:` line (6 reports):

     | Renderer | Logs | Detail |
     |---|---|---|
     | Vulkan | 3 | |
     | OpenGL | 4 | |

   - Why those 4 logs (3 reports) are on OpenGL:

     | Reason | Logs | Issue |
     |---|---|---|
     | Crashed start-up guard | 1 | #101 |
     | Vulkan loader too old (1.2.154) on an AMD R9 200 Series | 1 report (3 identical copies) | #109 |
     | User chose OpenGL on the command line | 1 | #107 |

   - Other counts:
     - 0 deny-list refusals;
     - 0 `vulkan: device …: <missing>` suffixes;
     - 0 integrated GPUs in use while a discrete one exists (#107b runs Vulkan on an Intel UHD, but no
       discrete GPU is listed).
   - #107b's Vulkan run on Intel UHD ended as a hang, with DXGI keyed-mutex timeouts and
     `DXGI_ERROR_DEVICE_REMOVED`.
   - **2 of 8 reports would plausibly be served by a D3D11 device:** #109 has no Vulkan 1.3, and #101 is
     guarded off Vulkan. #107 is a third candidate, an Intel iGPU whose Vulkan run hung, but that is
     **UNVERIFIED** as a driver fault.

## 1. Graphics-API-dependent mod code

### Method

- `modscan` walks every `*.dll` under `out/ws` (archived versions included) and decodes each method body's
  IL. For every call, field, token or type operand it resolves the member name; it also collects `ldstr`
  strings and int constants, and every ImplMap (P/Invoke) module.
- Hits from shipped copies of game and engine assemblies are filtered out (`UnityEngine.*`, `Unity.*`,
  `Assembly-CSharp*`, FMODUnity, MoonSharp, 0Harmony, System.*, Mono.*, Newtonsoft, ImGui.NET). That
  leaves 401 hit methods, most of them `Application.get_platform`.
- The regex is in `Program.cs`. Name matches on `Dispatch` from mods' own classes (event dispatchers) are
  false positives; none of them is `ComputeShader.Dispatch`.

### P/Invoke modules (all DLLs)

- **No graphics-API DLLs:** no d3d11, dxgi, opengl32, vulkan-1 or any GL library.
- **What there is:**
  - platform SDKs: rail_api64, steam_api64, EOSSDK, sdkencryptedappticket64;
  - fmodstudio;
  - cimgui (ImGui.NET copies);
  - SimDLL (copies of the game's Assembly-CSharp);
  - gdi32/gdiplus/user32 (System.Drawing copies, DebugConsole, StorageNetwork window handling,
    ForcedBuild, ModListPresets, 大一统);
  - Tolk/prism (screen readers, Vision Not Included);
  - a few mods' own natives (Lique.Fy, Enth.Anni, mx4.r9, SimDLL_RS.sim, SimDLL.sim).
- **Bundled engine modules:** `NVUnityPlugin`/`AMDUnityPlugin` appear only in bundled copies of
  `UnityEngine.NVIDIAModule`/`AMDModule`, the engine's DLSS/FSR modules, in 3509802691, 3528100424 and
  3750329747. These are engine copies, not mod code.

### Graphics members used by mod code (current versions)

| Mod | Name | Member | What the code does |
|---|---|---|---|
| 3637202410 | MAGNET!!! | `SystemInfo.graphicsDeviceType` (boxed into log strings only), `Application.platform`, `Shader.isSupported` | `Magnet.FieldEffect.LoadShaders` loads `assets/{mac\|linux\|windows}/magnet`, using "windows" for anything that is not 1 (OSX) or 13 (Linux). It loads two shaders, and if either `!isSupported` it logs "Effects shaders are missing or not supported here ({graphicsDeviceType})" and turns the effect off. The windows bundle has D3D11 + Vulkan programs (`mods.tsv`), so it works on Moono/Vulkan on Windows and is cleanly off on Moono/OpenGL on Windows. Moono's `isSupported` asks the device (`Material.cs:21`, `RenderDevice.Supports` → GLCore programs, `VkDevice.Supports` → `VkPrograms.PlatformOf`). |
| 1867707267 | Fast Save | `SystemInfo.supportsAsyncGPUReadback`, `AsyncGPUReadback.Request`, `.done/.hasError/.GetData` | Timelapse coroutine reads back the screenshot RT asynchronously. Device-neutral. |
| 2669076399 | Configurable Build Menus | `Graphics.CopyTexture` | `SpriteHelper.CopyGameTexture`, only in `archived_versions/*` (6 old DLLs); the current DLL has no hit. Device-neutral. |
| 3775109476 | Social Life | `CommandBuffer` (.ctor, set_name, DrawMesh, Dispose) | `DialoguePortraitRenderer.Capture` draws a portrait mesh into an RT. Device-neutral. |
| 3420064522, 3750329747 (`bin/Debug/ImGui.dll`), 3509802691 (`src/bin/Release/…/ImGui.dll`) | — | `CommandBuffer` (SetRenderTarget, ClearRenderTarget, SetViewProjectionMatrices, SetViewport, Enable/DisableScissorRect, DrawMesh) | An ImGui renderer. Not loaded by the game: these sit in subfolders, and KMod's `DLLLoader` lists only the mod folder's own files (`directoryInfo.GetFiles()`, oni-archives U60-764025 `KMod.DLLLoader.cs:69`). |

**Not found in any mod-authored code:**
- `graphicsDeviceVersion`, `graphicsDeviceName`, `usesReversedZBuffer`, `graphicsUVStartsAtTop`, and
  any `GraphicsDeviceType` comparison;
- `GetNativeTexturePtr`, `CreateExternalTexture`, `UpdateExternalTexture`, `GetNativeDepthBufferPtr`;
- `ComputeShader`, `GL.GetGPUProjectionMatrix`, `GL.IssuePluginEvent`.

**Conclusion:** a D3D11 device changes nothing for mod code. The one reader of `graphicsDeviceType` only
logs it.

### `Application.platform` users

- **Call sites:** 217 in 156 mods (current versions, `plat.tsv`).
- **Bundle loaders:** 129 mods have a platform-selecting bundle loader.
- **The shared helper:** 121 of them use the helper copied between UtilLibs (Sgt_Imalas), FUtility (Aki)
  and others: `LoadAssetBundle(name, path, platformSpecific)`, with strings `assets/windows/linux/mac`.
- **How the shared helper decides** (decompiled `FUtility/FAssets.cs`, `UtilLibs/AssetUtils.cs`,
  PortalNetwork `LoadAssets.cs`, `RsLib.Unity/RsAssetBundle.cs`, `ONI_Together.UI.lib/AssetUtils.cs`):
  - `RuntimePlatform` 1 (OSXPlayer) → `mac`;
  - 2 (WindowsPlayer) → `windows`;
  - 13 (LinuxPlayer) → `linux`;
  - anything else: no subfolder.

The rest of the call sites are file-browser openers, IsMac checks and console toggles.

## 2. The 37 D3D11-only mods: which bundle loads on Windows, and what is lost

How to read this table:

- "Win bundle" is what the code loads when `Application.platform == WindowsPlayer`, which is what Moono
  reports on Windows.
- Shader platforms come from `mods.tsv` (current versions only). 4 = D3D11, 14 = Metal, 15 = GLCore,
  18 = Vulkan.
- "Lost under Moono on Windows" means the Windows bundle's shader has no GLCore or Vulkan programs and the
  mod draws with it. Our devices then skip the draw: `RecordPass` returns null with no program, and Vulkan
  logs `is not drawn (no Vulkan programs)`, `VkPrograms.cs:102`.
- "Neutralised" means the mod replaces the bundle's shader or material with a game shader before anything
  draws.

### 2a. Visible loss under Moono on Windows today, which D3D11 would fix

| Mod | Name | Win bundle (code) | D3D11-only shaders actually drawn |
|---|---|---|---|
| 2921809973 | Backwalls | `assets/windows/backwalls_tileshader` and `backwallassets` (FUtility `platformSpecific: true`, `ModAssets.LoadAssets`) | `Backwalls/TileShader` (`TileMaterial.mat` → `BackwallRenderer.shinyMaterial`), `Backwalls/SGTRainbow` (rainbow variant's `uniqueMaterial`), `Unlit/TileShader` (custom-wall blit material in `CustomTileLoader`, `Graphics.Blit(…, material)`). Plain backwalls use `Shader.Find("TextMeshPro/Sprite")` and are fine; the shiny walls, the rainbow wall and user custom walls are lost. Linux bundle has 15/18. |
| 3753518460 | Backwalls (Fix) | same code and bundles | same |
| 3568684741 | Forcefield Projectors | `assets/windows/forcefieldtile_assets` (UtilLibs) | `Shader Graphs/ForceField`: `ForceField.mat` is the shield mesh's material (`ShieldGrid.cs:91`). **The shield itself is invisible on Windows.** Linux 15/18. |
| 3433108302 | Chaos Events for Twitch Integration | `assets/windows/chaos_twitch_assets` (UtilLibs) | `Mobile/Particles/Additive`, `Alpha Blended`: the fog and fire particle prefabs keep their bundle materials (only `renderQueue` is changed). Linux 15/18. |
| 3179880371 | Capsule 2.0 | `assets/windows/capsule` (own `LoadAB`, same platform switch) | Hovl `Add_CenterGlow`/`Blend_CenterGlow`, Cartoon FX ×2, `Particles/Standard Unlit`, `Legacy … Alpha Blended Premultiply`, `Standard`: assigned to its effect materials (`LoadAssets.cs:142–151`). **Confirmed in a user log:** issue #102 (Windows, Vulkan) logs `Hovl/Particles/Add_CenterGlow`, `Blend_CenterGlow`, `Particles/Standard Unlit` and `Legacy Shaders/Particles/Alpha Blended Premultiply` "is not drawn (no Vulkan programs)". Linux 15/18. |
| 2990227046 | Aki's Extra Twitch Events (Twitchery) | `assets/windows/akis_twitch_events`, `aetn_fireoverlay` (FUtility) | Bundle shaders are used directly: `PixelShaderMat.mat`, `Dither.shader` → `ditherMaterial`, `EggShader` → `eggMaterial`. `Beached/LiquidRefraction` is D3D11-only on every OS. The rest have 15/18 in the Linux bundle. |
| 3561866116 | 简体中文字体替换 (Font Replacement in Simplified Chinese) | `assets/fonts_bundle_win` (`FontConfig.GetPlatformBundleName`: platform 2 → win, else other) | TMP Distance Field (6000.3.5f2, U60 layout): the code sets `fontSharedMaterial = fontAsset.material` (`OniFontPatchZhCN.cs:156`), so the replaced labels use the bundle's D3D11-only shader. **On Windows the replaced text would not be drawn** (inferred from the code and bundle contents; not run). The spec `tests/e2e/mods/3561866116-…` played on Linux (`fonts_bundle_other`, 15/18). |
| 3135364297 | Japanese Font Patch | `assets/win/fonts.bundle` (platform 1 → mac, 2 → win, else linux) | TMP ×2 (Unity 2018.3, an old table format). No shader swap in the code, so the bundle shader is used. The swap only happens under a Japanese translation (MOD_COMPATIBILITY row 638). Lost on Windows (inferred). A D3D11 device would also need the 2018 table reader (`programs.md` §5). |
| 3637202410 | MAGNET!!! (the 38th shader mod) | `assets/windows/magnet` | Windows bundle has 4 + 18: works on Vulkan; under OpenGL on Windows the effect is off by design (`isSupported`). D3D11 would bring it back for OpenGL fallbacks. |

### 2b. Neutralised: the bundle's D3D11-only shaders are loaded but never drawn

These all load the Windows bundle (the shared helper with `platformSpecific: true`, unless noted). Their
D3D11-only shaders are TextMeshPro's or particle shaders that the mod replaces before drawing.

**TMP shaders inside UI prefabs, converted by `TMPConverter.ReplaceAllText(prefab)`.** This replaces
every TMP text with a `LocText` using the game's NotoSans or GRAYSTROKE font
(`UtilLibs/TMPConverter.cs:23–44`), so the bundle's TMP shader is not referenced by anything drawn:

| Mod | Name | Bundle |
|---|---|---|
| 2837919908 | Rocketry Expanded | `rocketryexpanded_ui_assets` |
| 2856555858 | Duplicant Stat Selector | `dss_uiassets` |
| 2947429297 | Cluster Generation Manager | `…_menuassets` |
| 2977728791 | Preset Manager | `dupe_prio_preset_window` |
| 3281716506 | Mod Preset Manager Light | `mpm_ui` |
| 3427974009 | Synchronized Building Groups | `buildingsettingsync_*`; also ships a copy of `rocketryexpanded_ui_assets` |
| 3468585385 | Blueprints Expanded | `blueprints_ui` |
| 3478214979 | Fabricator Recipe Controller | `complexfabricatorribbon_assets` |
| 3557584850 | Ronivans Legacy – Industrial Revolution | `ronivan_aio` |
| 3565090654 | Attribute Info Screen | `attributescreen_assets` |
| 3630759126 | Oxygen Not Included Together | `oni_mp_ui_assets`; also embedded `_win` resources picked by `GetBundleBasedOnPlatform` |
| 3782701870 | Automatic Industry | `ai_building_editor`; own switch: 1 → mac, 13 → linux, else windows |
| 2815406414 | True Tiles | `truetilesassets`, one bundle on every OS, used only for its SettingsDialog prefab |

**Indirect evidence for the TMP case.** Issue #102 has SetStartDupes (2856555858) and
ClusterTraitGenerationManager (2947429297) loaded on Windows/Vulkan. Moono logs each shader's route on
first use, and the log has no "TextMeshPro/… is not drawn" line. All 3 `TextMeshPro/Distance Field` route
lines say "Unity's Vulkan programs", so the D3D11-only copies were never drawn. Whether *every* TMP text in
every prefab is converted was not checked per prefab: **UNVERIFIED**.

**TMP shaders with the font set to the game's default by the mod's own code:**

| Mod | Name | How |
|---|---|---|
| 2966940337 | 传送端口 (Transfer Port) | `RsLib.Adapter.LocTextAdapter` sets `font = TMP_FontAsset.defaultFontAsset` |
| 3371129389 | 传送端口修复 (Transfer Port fix) | same as 2966940337 |
| 3519386331 | 传送端口 (Transfer Port) | same, with `TMP_Settings.defaultFontAsset` |
| 3732422991 | StorageNetwork | sets `font = defaultFontAsset; fontSharedMaterial = defaultFontAsset.material` (`StorageNetworkOrderProductionCenterSideScreen.cs:261`); every bundle, Linux included, is D3D11-only |

That TMP's font setter also replaces the material for the TransferPort mods is **UNVERIFIED**.

**Particle shaders replaced with `new Material(Shader.Find("Klei/BloomedParticleShader"))` on the
particle renderer**, plus TMP through TMPConverter. The bundle's `Particles/Standard Unlit` and
`Legacy … Additive (Soft)` go unused:

| Mod | Name | Where |
|---|---|---|
| 2787598894 | The Golden Throne | `ModAssets.cs:64`; only with its UseParticles setting |
| 2660131829 | Decor Pack I | `ModAssets.cs:256` |
| 2869608898 | Bio-Inks: Rechargeable Printing Pods | `ModAssets.cs:104` |
| 3747431307 | WannaPrint: Bio-Inks | same code |

**A font mod that swaps the shader itself:** 3826547280, Japanese Font Patch [Unofficial Patch].
`LegacyFontAssetUpgrader.Upgrade` sets `font.material.shader` to the game default font's shader. Fine on
Windows.

### 2c. Lost on every OS: D3D11 is the only platform they carry

| Mod | Name | Bundle (code) | Effect |
|---|---|---|---|
| 2968184715 | Portal Network | `assets/windows/temperatureeffects` (`platformSpecific: true`; only a windows folder exists, so the load fails on Linux and Mac) | Cartoon FX ×3 assigned to its effect materials (`LoadAssets.cs:54–62`). Issue #102 logs `Cartoon FX/Remaster/Particle Procedural Ring/Glow/Ubershader` "is not drawn". Lost on Windows today. |
| 3790434066 | PortalNetwork+ | same code and bundle | same |
| 3152316922 | Spring Festival Pack | `assets/{windows,linux,mac}/snoweffects`, all D3D11-only | Cartoon FX/Standard assigned to the snow and petal materials. Lost on every OS. Only a D3D11 device draws it, and only on Windows. |
| 2990227046 | Twitchery | (see 2a) | `Beached/LiquidRefraction` |

### 2d. Broken regardless

| Mod | Name | Why |
|---|---|---|
| 2119648603 | 字型模組 | `win`/`other` folder. It swaps to the game's TMP shader **only on Linux** (`FontUtil.cs:31`, platform 13), so on Windows it would keep the D3D11-only shader. But MOD_COMPATIBILITY marks it broken upstream (`FaceInfo` missing). |
| 3046440992 | 字型模組。新 | `win`; "other" platforms are refused; no shader swap. Broken upstream (`FaceInfo`). |
| 3519596146 | Proper Cyrillic Fonts | `Assets/font`, one bundle, D3D11-only. Fails in the original too (MOD_COMPATIBILITY row 1174). |
| 3290446954 | Mod GUI Pro | `oni_default`, one bundle, D3D11-only. Broken on U60 (MissingMethod, row 2679). |

### Caveats for a D3D11 device

- The reader formats differ by the Unity version a bundle was built with:

  | Built with | Mods | Reads as U60's D3D11 entry? |
  |---|---|---|
  | 2022.3 / 6000.x | Forcefield (6000.3.8f1), Simplified Chinese fonts (6000.3.5f2) | yes |
  | 2020.3.x | Backwalls ×2, Chaos, Capsule, Twitchery, Portal Network ×2, Spring Festival | no (magic 0x0C07513C, inline bindings) |
  | 2018.3 | Japanese Font Patch 3135364297 | no: old table that `ShaderProgramTable` cannot load at all |

- So a D3D11 device that runs only U60-layout programs brings back **2 of the 9** visible losses. The
  other 7 need the 2020.3 entry reader (and the 2018 table for the Japanese Font Patch). See
  `programs.md` §5 and its UNVERIFIED item 10.
- An alternative that avoids D3D11 for these mods is to have Moono report `LinuxPlayer`-style bundle
  selection on Windows. That is **not** recommended: it changes `Application.platform` for every mod.
  Mentioned only for completeness.

## 3. Demand: Player.logs we hold from issues

### Sources

- **Logs held:** `out/issues/1/Player.log`; `out/issues/2/Player.log` and `Player2.log`;
  `out/issue50/Player.log`; `out/i101/x_Player.log`; `out/i102/x/Player.log`; `out/i104/Player.log`;
  `out/i107/x/Player.log`; `out/i107b/x/Player.log`; `out/i109/Player.log`.
- **Zips:** `out/i101/crash.zip`, `i102/crash.zip`, `i107/crash.zip`, `i107b/crash.zip` and
  `i109/OniMoono-crash-20261121-2221.zip` each hold one Player.log (plus a .dmp, not extracted).
  - The i101 and i102 zip logs are the same size as the `x/` copies. i109's zip log, `i109/Player.log`
    and `i109/z/Player.log` are byte-identical (SHA-1 `d2bbfa1d0f…`).
  - `i105/perf-logs.zip` holds only PerfProbe logs, with no renderer lines.
- **Excluded:** other `i*` and `issue*` folders under `out/` are our own runs or worktrees.

**Total: 10 distinct user logs from 8 issue reports**, all Windows. #2 has 2 logs; #107 has 2 runs, the
same machine and build with different renderer settings.

### Per log

| Log | Build | Renderer line | Vulkan devices listed | GPU in use (driver) |
|---|---|---|---|---|
| issues/1 | OniBrutal (U59, before the renderer choice) | none: OpenGL only | — | GeForce GTX 1660 (NVIDIA 560.94) |
| issues/2 ×2 | OniBrutal (before the renderer choice) | none: OpenGL only | — | GeForce RTX 4070 Laptop (NVIDIA 616.56) |
| issue50 | Moono 1994800e | none: OpenGL only | — | GeForce RTX 5070 Ti Laptop (NVIDIA 610.47) |
| i101 | Moono 50519887 | `renderer: OpenGL (Vulkan unavailable: Vulkan did not finish starting in an earlier run with this GPU, driver and Moono build (gpu 10de:25a2 driver 9a170000 (NVIDIA) …)` | RTX 3050 Laptop (discrete, 1.4.351); Iris Xe (integrated, 1.4.323), listed twice | RTX 3050 Laptop (NVIDIA 616.92, OpenGL), `gpu: discrete` |
| i102 | Moono 50519887 | `renderer: Vulkan (auto, default)` | RTX 5070 Laptop (discrete, 1.4.341); Radeon 610M (integrated, 1.3.302) | RTX 5070 Laptop (NVIDIA 610.88) |
| i104 | Moono 1043305e | `renderer: Vulkan (auto, default)` | RTX 5070 Ti (discrete, 1.4.329); Radeon Graphics (integrated, 1.4.315) | RTX 5070 Ti (NVIDIA 596.49), `gpu: discrete` |
| i107 | Moono cf6548b1 | `renderer: OpenGL (OpenGL chosen (the command line))` | not enumerated | Intel UHD Graphics (32.0.101.7092) |
| i107b | Moono cf6548b1 | `renderer: Vulkan (vulkan, the settings file)` | Intel UHD (integrated, 1.4.323) only | Intel UHD (101.7092), `gpu: integrated`. DXGI `AcquireSync(1)` timeouts, `D3D11CreateDevice … DXGI_ERROR_DEVICE_REMOVED`, process ended 0xCFFFFFFF (hang) |
| i109 | Moono 1043305e | `renderer: OpenGL (Vulkan unavailable: the Vulkan loader is version 1.2.154; 1.3 is needed)` | — | AMD Radeon R9 200 Series (Adrenalin 20.11.3, OpenGL 4.5.14757) |

### Counts asked for

| Count | Logs | Reports |
|---|---|---|
| `renderer: OpenGL` with a reason | 4 of 7 | 3 |
| Vulkan deny-list refusals | 0 | |
| Crashed start-up guard | 1 | #101 |
| `vulkan: device …: <missing requirement>` | 0 | |
| `vulkan: Windows lists the GPU …` | 0 | |
| Integrated GPU in use while a discrete one exists | 0 | |

- **OpenGL reasons:**
  - #101, the start-up guard (a crashed earlier Vulkan start on the RTX 3050 Laptop);
  - #109, a Vulkan 1.2 loader on a GCN 1-era R9 200 with a legacy 2020 driver;
  - #107, the user's `--renderer opengl`.
- **The 3 pre-Vulkan logs** (issues/1, issues/2 ×2) and issue50 are OpenGL by construction.
- **Integrated GPU:** every log that lists an integrated and a discrete GPU uses the discrete one: i101
  (on OpenGL), i102 and i104. i107b uses an integrated Intel UHD with no discrete GPU listed.
- **The #107 machine** is an Intel UHD on Vulkan, and it hung. That is a D3D11 candidate if the hang is the
  Vulkan/DXGI interop on that driver, which is **UNVERIFIED**.

**GPU models and drivers seen** (8 machines):

| Vendor | GPUs | Drivers |
|---|---|---|
| NVIDIA | GTX 1660; RTX 3050 Laptop; RTX 4070 Laptop; RTX 5070 Laptop; RTX 5070 Ti Laptop; RTX 5070 Ti | 560.94, 596.49, 610.47, 610.88, 616.56, 616.92 |
| AMD | R9 200 Series | 20.11.3 |
| Intel | UHD Graphics | 101.7092 |

### What this says about demand (small sample)

- **Reports a D3D11 device would plausibly help: 2 of 8, maybe 3.**
  - **#109:** no Vulkan 1.3. The R9 200 supports D3D11 feature level 11_1 by its generation
    (**UNVERIFIED**, from memory).
  - **#101:** guarded off Vulkan after a start-up crash on NVIDIA.
  - **Maybe #107:** Intel iGPU, Vulkan run hung.
- **Most reporters (5 of 8) are on recent NVIDIA**, where Vulkan works (#102, #104) or OpenGL predates
  the choice.
- **Mod-side demand is concrete.** Issue #102 is a Windows user whose log shows mod effects not drawn for
  lack of Vulkan programs (Capsule 2.0, Portal Network). A D3D11 device with a 2020.3-entry reader would
  draw them, as the original does.

## UNVERIFIED

1. That every TMP text in the 13 TMPConverter mods' prefabs is converted. Supported only by the code
   pattern and by the absence of "not drawn" TMP lines in #102.
2. That TMP's `font` setter replaces the shared material, for the 3 TransferPort mods.
3. That 3561866116's and 3135364297's replaced labels are invisible on Windows under Moono. Inferred from
   the code and bundle contents; not run on Windows.
4. The cause of #107b's hang (driver vs. interop).
5. D3D11 feature-level support of the R9 200 Series.
6. Whether ImGui DLLs in subfolders are loaded by the mods themselves (reflection or `Assembly.LoadFrom`).
   The game's loader does not load them (`KMod.DLLLoader.cs:69`).
