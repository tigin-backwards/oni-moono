# Master commits for the U60 cut-over

`git cherry origin/u60 origin/master` on 2026-11-19 (master 808ac53e9, u60 cf6548b11) lists 354
master commits with no exact patch on u60. Each is classed:

- **a** on u60 in another form: the u60 commit named (a conflict-resolved pick with the same
  subject, the original it came from, or what superseded it);
- **b** U59-only, not wanted on U60: why;
- **c** missing from u60: cherry-picked (oldest first, u60's code kept where U60 differs);
- **?** left for the owner.

Counts: a 318, b 11, c 23, ? 2.
A same-subject pair was also checked for files master's commit touched and u60's did not; what
that found is noted on the row. Two commits with no master counterpart carry over what those
checks found (the Workshop preview and description notes, the census spec).

| Master | Subject | Class | u60 / why |
|---|---|---|---|
| `cfb7adcf9` | tweaks: Moono Tweaks screen in the game's language from .po files | a | 9b2169dd7 |
| `61b960ac9` | qa: a memory stage beside other runs; players end with qa.sh | a | 35543cb76 |
| `d5068cb80` | windows: test windows open on monitor 2 without taking the focus | a | 7c953e6f1 |
| `8156e1c64` | e2e: E2E_ORIGINAL_ENV, the original player's own environment | a | ff90112c1 |
| `888f27712` | platform: windowed requests too big for the screen are maximized | a | 2bc1ca00f |
| `db8ef08e2` | engine: present through a DXGI swap chain on Windows | a | 26e934776; u60 has its own DXGI files and FrameStartedAt |
| `99f9ea7ac` | host: dxgi option in Moono Tweaks and --dxgi on\|off | a | 6b2cc18ef; u60 has the strings |
| `12601f83b` | tools: hdr check with an fp16 desktop capture | a | c424fb4e8 |
| `5bf01f477` | docs: presentation on Windows through DXGI, and the HDR note | a | c344176c6; its TWEAKS.md HDR troubleshooting note is not on u60 (left for the owner: it names SwapBuffers, u60 falls back to Vulkan's swapchain) |
| `c3461c2a7` | engine: keep the shared DXGI files as the dxgi branch has them | a | u60's 94fc97191 and ed8e26400 own the shared DXGI files (u60 dropped the queuelock fault that master's copy still names) |
| `44d228308` | host: --dxgi-fault takes only this build's OpenGL faults | a | 81667b371 (u60's --dxgi-fault with its OpenGL and Vulkan faults) |
| `dc6bee11d` | tools: hdr check runs the original fullscreen, on any monitor count | c | picked (default monitor 2, as on master) |
| `472dd8312` | docs: DXGI on U59, measured | b | OpenGL DXGI measured on U59 744825; u60's ARCHITECTURE has its own presentation figures |
| `28978621c` | tweaks: overlay call reroutes run after mods' transpilers | b | fast-overlays retired on U60 (c967677ea); the reroutes served only it |
| `80e375f1c` | e2e: issue 41 specs, wires and pipes through tiles | b | issue-41 Fast Track spec of fast-overlays, retired on U60 (c967677ea) |
| `375e9219d` | engine: canvas keeps its matrix products while their inputs stand | a | 3cc825c65 |
| `d86e2e696` | host: the GPU option no longer writes the Windows exports | a | 5aa4784f5 |
| `96fa7f287` | docs: DXGI's occluded status and window messages, as documented | a | f1c63fb86 |
| `58f10e94b` | docs: DXGI's lack of window messages is measured, not documented | a | 28e2f0363 |
| `4d72b7e96` | gl: every GLSL sampler type is reflected as a sampler | a | f6936d5cd |
| `c011f5bff` | input: typed characters above U+FFFF keep their code point | a | d799056af |
| `14a08e83e` | host: unity's player arguments are read or ignored, never fatal | a | 002d21c38 |
| `6989733bb` | platform: comments say what was observed and what is only a guard | a | b0511a14c |
| `174ae019d` | platform: the window is focused once, by showing it | a | 1301c4dd5 |
| `5a8c79bc7` | docs: architecture names OniMoono.Desktop and lists window limits | a | 068a425a1 |
| `2b60d655f` | gl: Mesa's AMD driver binds textures to units, as AMD's Windows one | a | 2136332ef |
| `b87f22c43` | mods: the U60 burndown's table, specs and results from u60 | a | copied from u60's burndown (c5bea5cb5, 76e25cd3c, d4ff9df30 and the rounds); u60 newer |
| `6716fbc81` | engine: an Awake that destroys its object leaves nothing to start | a | a79fbb3eb |
| `3bf291b7c` | mods: the U60 burndown's later rounds from u60 | a | copied from u60's burndown (9875869bb and the later rounds); u60 newer |
| `455a6a4d8` | mods: every U60 catalog mod has a row, from u60 | a | copied from u60 (c5bea5cb5, d19444147); u60 newer |
| `de86247d5` | mods: the burndown's last U60 verdicts, from u60 | a | copied from u60 (2945241f5 and the last verdicts); u60 newer |
| `48d83616d` | e2e: research tree pans as in the original at the same frame rate | c | picked |
| `cb4faf5f1` | docs: a fullscreen window ignores the resolution asked for | a | 4ae2bc5cb (u60 stretches the asked resolution; no limitation to note) |
| `eca513f95` | e2e: build menu counts only reachable materials, as the original | a | 12608d807 |
| `83cc319c0` | input: apply the IME state again on focus and input language | a | ad601111f |
| `3f2e3fc07` | docs: a fullscreen window stretches the resolution asked for | a | 4ae2bc5cb |
| `75622f609` | desktop: currentResolution is a fullscreen window's size on Windows | a | c724f3127 |
| `9a64a50a4` | probe: measure DefaultExecutionOrder and frame timings in mods' use | a | d39e26b5c |
| `46048ef9a` | engine: frame timings, Profiler memory counters and GetSceneAt for mods | a | 91cf2bdf8 |
| `0a7a78920` | host: the game's rewritten assemblies keep their files as Location | a | 66e7c9433 |
| `7ffc4e8d9` | probe: measure signatures tagging a struct that is now a class | a | 0d0a7ff9c |
| `6327d9c78` | mods: read a type tagged as another kind as Mono reads it | a | 7abaee29d |
| `7ca3ea7b6` | tmp: an indent opened part-way along a line moves the pen | a | 014dd5af2 |
| `b7827bd40` | probe: measure references that do not resolve, inside a try | a | 429335cc8 |
| `9e1b20204` | host: what the original has and ours lacks throws where named | a | f89484be3 |
| `e0278f63c` | docs: the known differences run to D57 | c | picked, adapted to u60's numbering (D1–D58; D57 and D58) |
| `a47b0976a` | e2e: display_dpi runs a spec's Xvfb with -dpi | a | 347d961db |
| `2f3fabd53` | platform: Screen.dpi reports the original's values | a | 44e978a72 |
| `f436c0f64` | present: a refused DXGI swap chain says what holds the window | a | fd5b0bf6a |
| `4d17605a5` | host: hide Steam IDs, the Steam name and the Klei ID in the log | a | c40a417ed |
| `4b233e980` | e2e: blocked plugins, data collection off and profile file shots | a | bb1b3e7f1 |
| `272e8389f` | host: game P/Invokes throw Mono's DllNotFoundException | a | 372453118; u60's DEVELOPMENT.md has its own D-range |
| `35423cfb3` | e2e: extra_saves, more colonies in a run's profile | a | 9b5603c1a |
| `006d6258c` | e2e: data collection off by default, management screens via E2eDriver | a | d7f388a6b |
| `90a1ec4a4` | e2e: A1, every template read through VYaml as in the original | a | 3e63bcff9 |
| `b763545ba` | probe: U60's BCL calls, unchanged TMP text and Awake exceptions | a | 0211f6a20; u60's spec is u60-bcl-probe |
| `07288c1de` | tmp: the same text set again is a change, but for the text property | a | 29363207c |
| `872eb40d5` | e2e: a Workshop item as a Steam subscription | a | f11cb56d4 |
| `321aac97e` | host: Unity's -force-glcore, -force-opengl and -force-vulkan | a | be78d2933; u60's own renderer text and u60-p2-renderer |
| `2a231d078` | e2e: the mouse's side buttons in steps and in the key log | a | 2cbf7eb70; u60's specs are u60-i1/w4-side-buttons |
| `b7a80c486` | e2e: side buttons up to X button 10, as Xvfb's XTEST pointer has | a | 19524b65d; as above |
| `6d027d9b0` | e2e: X button 11 as an event sent to the window under the pointer | a | 2adb8cafe; as above |
| `8bcc87ce9` | input: X side buttons 10 and 11 numbered as Unity's X11 player | a | dbbccba21 |
| `92ace741c` | e2e: census reads of a world's pickupables, chunks and start grid | a | 0728cca71; its census spec came over in "e2e: census spec, the census reads in both players" |
| `08ed561ba` | tweaks: fast-jobs wakes each worker through its own semaphore | a | 3ac5d19f7 |
| `0741df06d` | render: cull only renderers enabled on active objects | a | 3e396fbea |
| `0552c7e7c` | engine: bundle nodes are windows on their archive's data | a | ff9467807; u60 tests it in TexelSourceTests |
| `c0baa4dad` | tweaks: symbol overrides reapply only what changed for Duplicants | a | d48c0c9e5 |
| `17e1681b6` | docs: Windows numbers for the kept symbol override applications | a | 6d6e7b939 |
| `5f945509c` | menu: a Gassy Moo badge when a newer Moono is out | a | 601c905a0 |
| `b40983977` | host: Linux plugins are found anywhere under Plugins | a | 96ccb1a23 |
| `a63f323fd` | gl: uploads, targets and program lookups are recorded without closures | a | 461f5a1a0, 3c26e5d63 (u60's originals of the change) |
| `1994800e0` | engine: frame stats split the sim step and log waits and the machine | a | b6e6ffb18 |
| `b2025c4f1` | rendering: mesh setters copy once and reuse arrays uploads let go | a | 992a381dd |
| `8bfa58be2` | tools: PerfProbe logs allocation and alternates a switch in one run | a | 6a1302458 |
| `939b96631` | host: SIGTERM and SIGINT quit at a frame boundary | a | fa869e986 |
| `abb3ef56a` | e2e: teardowns quit players first, then kill what is left | a | 6012627d5 |
| `e60959792` | e2e: specs name game tweaks and an environment for ours | a | 507dbfb71 |
| `7d807f140` | tweaks: path-probe-repair repairs Duplicants' path probes (opt-in) | a | 0dcdddee7 |
| `174c16ff5` | docs: path-probe-repair floods when a repair would redo half | a | u60's TWEAKS.md already says it (path-probe-repair section) |
| `2cc4a29f5` | tweaks: Moono Tweaks rows show their tooltip from anywhere on the row | a | 9b2f252c9 |
| `371932822` | tweaks: path-probe-repair is on by default (the player's choice) | a | a81918cd0 |
| `851151b5d` | host: a compact store for NavGrid's links | a | 5ecd618b3 |
| `b1b7942c0` | tweaks: compact-nav-links keeps nav grid links in compact stores | a | 509367be2 |
| `677e03567` | docs: compact-nav-links' verification and Windows numbers | a | 9ef124222 |
| `806299092` | e2e: time the Load screen with thousands of saves | a | b99b45456 |
| `383ee16a5` | tweaks: fast-load-screen reads each save once a refresh | a | d7b16565d |
| `b6aba33f4` | tweaks: fix-load-screen-clicks opens a colony once (opt-in) | a | 443c3e5ca |
| `ca03407e1` | docs: the Load screen tweaks' results and the original's draws | a | 1b154f64f |
| `cdc29fd56` | tweaks: fix-load-screen-clicks is on by default (the player's choice) | a | 5964fc9e7 |
| `b555ee397` | engine: LoadImage's texel hand-over without U60's unload state | b | U59-only: U59's LoadImage has no U60 unload state |
| `3c646eea3` | tweaks: a Bug fixes group for the fixes of Klei's bugs | a | 8af6e0ed2 |
| `f84f99c6f` | tweaks: off-world-fallers moves to Bug fixes | a | cf9eaf913 |
| `7d610f08c` | tweaks: no launch-options banner; general group notes | a | b77e224d0 |
| `30248850c` | tweaks: fast-smi-lookups keeps state machine lookups per controller | a | 26fd9328c |
| `b07cc4540` | gl: frame stats count video memory by kind and upload staging | a | 2697a4fb3 |
| `10b57871b` | docs: video memory at start-up, four levers measured | a | 59bf62562 |
| `498467e3f` | e2e: our player runs with path-probe-repair by default | a | ee2959210 |
| `0e2e09fdb` | e2e: moono-tweaks specs scroll over rows, not the list's gaps | a | 8b052900b |
| `490555ae1` | gl: check bindless residency and fence non-residency and deletes | a | df27111c9 |
| `d6f271b5b` | tweaks: a search box on the Moono Tweaks screen | a | a51a116ae |
| `5cb06cf2c` | engine: frame stats name the longest GC pause and peak memory | a | 36c08d036 |
| `f58e07395` | host: a garbage collection option, fast or low memory | a | 38ce49d46 |
| `8416ac5be` | docs: describe test colonies by kind, not by their save names | a | 4a817ad1e |
| `ccbaed511` | host: PathGrid's arrays in page tables, made on first write | a | c2bfa28c9 |
| `7c0eac178` | tweaks: paged-path-grids keeps path grids in pages | a | 0b958e8d2 |
| `c79cb9ebe` | docs: paged-path-grids built, verified and measured | a | 19071e276 |
| `e8033835c` | docs: paged-path-grids' soggy lab 2 pair on one build | a | a75d2f8d5 |
| `3ed78ba87` | e2e: moono-tweaks specs reach the gain row through the search box | a | bfb7534b3 |
| `c6731dc82` | qa: verify fails on every verify mode's mismatches | a | 6db1d50f4 |
| `02437dc49` | tweaks: show mods that patch what the game tweaks change | a | 5741a5530; minus the retired fast-overlays files |
| `7bcf43b77` | e2e: mod-overlap specs with a test mod of ours | a | d100cb677 |
| `224b35a36` | host: French cultures group digits with Mono's no-break space | a | fdf06eadd |
| `6d9692884` | docs: D60, GZipStream's compressed bytes | a | 1423a8527 (u60's D61) |
| `b7f96dc4a` | Revert "host: French cultures group digits with Mono's no-break space" | a | 658850db6 |
| `1952ed736` | docs: D59, French digit-group separators | a | b0fcf93d3 (u60's D60) |
| `b1e019dec` | probe: TMP's fallback chain, in three languages | a | b058a34b4 |
| `66b5f3675` | engine: raw texture loads that change nothing skip the re-upload | a | fd1497946 |
| `556f2738c` | e2e: click cycling in the automation overlay and under patched shapes | a | 19c2f05b4 |
| `8d2e8a428` | e2e: Fast Track's GitHub builds pass the mod gate by their hashes | b | the U59-only Fast Track gate exception (Fast Track is broken on U60) |
| `27b73cc6d` | tweaks: stop endless loop sounds and one-cell sound piles | a | eb951efe2 |
| `9d96530e0` | host: pass the game its own command line, as the original | a | 1b964dcaa |
| `1119c335d` | e2e: game arguments for both players and a command-line test mod | a | feed15daa |
| `07cbcfcb8` | e2e: click cycling with Better Info Cards in many selection states | ? | issue-52 BIC regression specs: not picked, left for the owner (half use Fast Track, broken on U60) |
| `6f6612e35` | time: vsynced frames last whole refreshes, as Unity times them | a | 19b187379 |
| `f0bde6ef3` | time: revert whole-refresh frame deltas | a | 9fb4d4ecf |
| `84fb846bd` | tweaks: name the self-destructed rocket's debris, not its steam | a | ec848e78e |
| `4ff6c0016` | time: vsynced frames last whole refreshes again, also with G-Sync | a | bb88a0120 |
| `6767488e6` | time: frames over a second or under a millisecond keep their wall clock | a | d821e4e2f |
| `b4d650d1f` | render: missing textures read what the original's player gives them | a | 41e179d57 |
| `021f6688f` | render: default texture checks find the game as the other tests do | a | 7553f9170, 41e179d57 (u60's DefaultTextureTests already reads TmpTestSupport.GameDir) |
| `729f9bbbf` | host: record native crashes in Player.log, with a minidump | a | 23d51ea86 |
| `279c793ff` | present: keep OpenGL's context off the window DXGI presents to | a | cda65d825 |
| `6ff4c7a52` | docs: a delegate to an instance method on null throws in mono too | a | 0cadb80b6 |
| `511cdefc3` | ui: overlay canvases draw over a cleared depth buffer | a | 798d2ec55 |
| `2da816c8a` | e2e: Colony Summary charts compared with the original | a | 5b8714fd0 |
| `772f09e1d` | host: --crash-dumps full writes a dump with the process's heaps | a | 6ece6fd46 |
| `47bed0282` | present: wait for queued frames with vsync, as Unity's main thread | a | 748bf1343 |
| `2272247bb` | e2e: research tree's y after the drag against the original's steady fps | c | picked |
| `fbb846e09` | gl: draw Klei/Stars on Windows as Direct3D 11 does | a | fa7860674 |
| `d9f4fc7a6` | e2e: per-spec player-args in --list, docs name the release list | a | 9092f474b |
| `971935547` | fast-overlay-textures: verify reads only the shader's texture properties | b | fast-overlay-textures retired on U60 (c967677ea) |
| `ec84b28af` | qa: count errors logged as plain lines among the log signatures | a | 7430ff6a7 |
| `ba8a7cf9b` | tweaks: instant-reachability answers reachability by events | a | 68c78803e |
| `46eb15f05` | tweaks: retire slow-reachability, instant-reachability replaces it | a | bfd4855b2 |
| `00e2391cd` | docs: instant-reachability's cost on U57 | a | 080b62c4c |
| `4f383caa8` | e2e: klei-unreachable's original is the folder its executable sits in | a | 12608d807 |
| `40cd1bdf1` | probe: autosave.txt can wait first and holds off the game's own | c | picked |
| `c9cec8b1a` | docs: performance figures measured again on Windows and Linux | b | U59 744825 performance figures for master's docs; u60's PERFORMANCE.md keeps U60's (re-measure after the switch) |
| `6fcf9e99b` | docs: tweak table dates its earlier figures | a | 21f0eb4e6 |
| `3c0618902` | docs: tweak table drops its Earlier column | a | f6f9be8d9 |
| `8840904be` | e2e: overlap-manual waits for the autosave before Save and Quit | a | d1f98b0da |
| `93b25d316` | e2e: #52 after-load specs check each click against the original | ? | issue-52 after-load specs: not picked, left for the owner (fixture blessed on U59, needs U60 originals) |
| `740fb5450` | tweaks: instant-reachability hands back stray offsets and old tables | a | dd52d4988 |
| `13f85ee87` | docs: frame inventory with every tweak on, U57 and 2minnow | c | picked |
| `aa49190ca` | docs: frame inventory gets a main-thread share per system | c | picked |
| `4c284a7f7` | docs: how Moono's engine differs from Unity's player, and why | c | picked |
| `528e60ecf` | docs: unity-vs-moono gets per-call costs, original vs Moono | c | picked |
| `3210dd42d` | tweaks: fast-anim-events stands down for foreign event-loop patches | a | 52c87cf67 |
| `a9a10cc9f` | docs: unity-vs-moono's position-set cost, measured warmed up | c | picked |
| `cbc7c2ecc` | tweaks: post-load-gc moves the first big collection into the load | a | ac65e68a6 |
| `f73a6dc56` | tweaks: post-load-gc's row says what it does | a | 44dab9989 |
| `bee1795fb` | host: a crash window to report crashes on GitHub | a | 7a02e5b94 |
| `7a421f151` | present: show the last frame again while no frame comes | a | 5ce5ef06b |
| `9e6d56a00` | ui: raycast results reused while nothing they read changed | a | a0d174f54 |
| `a1caed1b2` | render: culling keeps renderer bounds until what they read changes | a | b1e21af21 |
| `7df51d0ad` | tweaks: each tweak checks the game's code before it patches anything | a | 85739c327; minus files of tweaks retired on U60 |
| `384ab7c31` | tweaks: fetch-by-cell checks each pile of identical items once | a | 158b030b3 |
| `4a5d3fbb6` | tweaks: fetch-by-cell costs mixed cells through the game's first item | a | c910bfeb4 |
| `8073e0778` | tweaks: backport U60's geothermal vent fix as fix-geothermal-vents | b | backport of U60's own geothermal vent fix to U59 |
| `1ef3b0756` | ui: canvas renderers honour cullTransparentMesh as Unity does | a | 0a81beae6 |
| `3b729ab33` | tweaks: Moono's own Workshop item stays out of the game's mod manager | a | d2133fbc6 |
| `48d381088` | mesh: setters reuse larger channel arrays as the vertex count changes | a | f45d90a74 |
| `6074bd9ee` | gl: upload records come from the recording frame's arena | a | 2221ed5c1 |
| `3cf814897` | render: property sheets make their stores lazily and are recycled | a | 832adb8b3 |
| `e681d5758` | gl: pass states stay cached per pass while they resolve the same | a | 8b55c043d |
| `6edb5d77c` | qa: the verify stage checks the reuse of meshes, sheets and states | a | 50866a6fe |
| `8e7cf48e7` | ci: tests skip themselves without the game; no hand-kept list | a | 7553f9170; minus CallSiteReroute's tests |
| `8a5261186` | e2e: research-pan checks hold for the original's uneven frames | c | picked |
| `afddd7aa0` | e2e: Moono Tweaks specs follow post-load-gc's row text | a | 3314d7ce9, 44dab9989 |
| `aaf089d3e` | scripts: fix shellcheck's findings | a | 56c2ee972 |
| `8d9b863eb` | scripts: ruff format the Python tools | a | 89ac5103a |
| `d0986a69b` | scripts: fix ruff's findings | a | 28757c168 |
| `81554c239` | src: dotnet format whitespace | a | b2163e4f4 |
| `4da83bb99` | ci: the game refs keep Assembly-CSharp's private members | a | 2763b9cae |
| `10cfb5784` | tweaks: fetch and reachability tweaks read game fields directly | a | 4c497323a |
| `edeecec1b` | tweaks: anim tweaks read game fields directly | a | 4a099aec5 |
| `7aeb47802` | tweaks: building and sim tweaks read game fields directly | a | ec25483d1; minus the U59-only geothermal backport |
| `3cb02a7ef` | tweaks: fast-overlays reads and sets game fields directly | b | fast-overlays retired on U60 (c967677ea) |
| `1f1ef2f7d` | engine: coroutine exceptions log the original's stack traces | a | 50c29c40b |
| `559e1d178` | present: hidden GL windows single-buffered so RTSS sees DXGI | a | 7445b0588 |
| `90d4ea02d` | worldgen: a seed makes the original's map (exact-worldgen) | a | 84eb559fd |
| `f9fcf9fa2` | host: FormatterServices makes delegates as Mono's does | a | 49a5f0f91 |
| `80231cef4` | e2e: fixture saves come by hash from the refs repository's releases | a | 5b2a4d90b |
| `4a9483f36` | tweaks: shared-duplicates keeps one copy of identical loaded data | a | 231164003 |
| `22728c7ef` | ci: GitHub and onimaxxing marks for the Workshop description | c | picked |
| `32927f88f` | ci: the Workshop description's onimaxxing mark is transparent | c | picked |
| `8d9a5d27b` | ci: the Workshop description is kept in the repository | c | picked (RELEASES.md conflict: both sections kept) |
| `7e2e37c25` | ci: the Workshop description drops a line the uninstall step covers | c | picked |
| `c45505b46` | ci: the Workshop description lists its platforms; Linux uninstall path | c | picked |
| `a0b1071c5` | workshop: a loader mod offers Moono from the original game | a | e3671ba69; u60 keeps its own ci/game-refs.json |
| `513b4e0f7` | tweaks: compact-report-history keeps past days' notes in flat arrays | a | ce8cc6c38 |
| `cd3ea1452` | install: no signature check of Workshop updates | a | a16ed6e51 |
| `16ea230aa` | docs: the installer checks Workshop files for integrity only | a | 0a35e8e92 |
| `376585b0d` | gl: mask draw buffers a fragment program does not write | a | c76ca1df4 |
| `a35289497` | install: an installed version missing files doesn't start | a | 8b987f4c1 |
| `e542f1fb1` | engine: no-content mode for stripped copies of the game | a | 9d32a897a |
| `736d4c5c3` | e2e: E2E_NO_SHOTS takes no screenshots | a | 51a4af6a7 |
| `e6342cd7d` | ci: game tests on a stripped copy of the game | a | e7c6c4af8 |
| `8099c01b2` | ci: a GitHub App token reads the refs; the game from release assets | a | 69c53dffd |
| `cda07b511` | ci: the nightly verify run under Xvfb, with the checks it reports | a | 7cf58c1bc (u60's lists name its own tweaks) |
| `efe76060f` | ci: the Workshop job reads the refs with the GitHub App token | a | 69c53dffd |
| `536ad63e0` | hierarchy: child lists grow with spare slots instead of a copy per add | a | 2c63ba246 |
| `50b716e10` | ci: the game job keeps DejaVu's core faces only | a | 4d04efc3e |
| `40bbdbca9` | e2e: the tweaks search spec expects compact-report-history's row | a | 123476aa1 |
| `8392ed3b5` | ci: by-hand nightly runs take a spec list; build-menu-search checks input | a | 6eaddd3a9 |
| `53971eef7` | audio: FMOD allocates from a private heap with a quarantine | a | 98ddb3242 |
| `94005b8bb` | e2e: a spec skips ours when a file it needs is not beside the host | a | 281ca06e4 |
| `9a8413d08` | host: game plugins bind before the runtime's own DLL search | a | e4949aa62 |
| `c7529a9f6` | loader: the dialog is three steps, the line in a box, translated | a | 7a63e1508 |
| `b18343679` | workshop: the description's steps match the loader dialog | c | picked |
| `c34bd61b1` | ci: upload the Workshop item from Actions, approved per run | a | d1a4024b8; its ci/workshop/preview.gif and RELEASES notes came over in "workshop: the item's preview and description notes, as on master" |
| `fb8f1d116` | perfprobe: allocation counters for both players and a frame-rate cap | a | e77b8caeb |
| `d44d4334a` | docs: memory and allocation figures in KB, MB and GB | c | picked, re-applied by hand (u60's ARCHITECTURE had no binary units left there) |
| `7f2b26182` | tweaks: fix-timelapse-snapshots draws snapshots for their own frame | a | 90e4d3f03 |
| `bf68f6a44` | capture: seamless super-sized screenshots at any size, without the UI | a | e02843d31 |
| `43db08e2b` | tweaks: declare what tweaks reach by name and the game code they copy | a | 81b5c2571; minus fast-overlays |
| `dff7d0d5f` | tweaks: check-tweaks fails on undeclared name lookups and mirrors | a | bf32cdcfe |
| `75e014532` | docs: allocation batch 4, the engine's frame and canvas copies | a | 768861d00 |
| `5eecb4c92` | tweaks: nameof lookups of game members need a declaration too | a | e57a3abd2 |
| `b4745cc9b` | rendering: AsyncGPUReadback reads render textures without a stall | a | e575118bd |
| `96f303b7d` | docs: U59 snapshots, previews and screenshots before and after | a | b5ed904c9 (master's is the U59 cut of it) |
| `d1cced8e2` | fmod: slabs with side tables, no bookkeeping beside FMOD's blocks | a | 033114aa2 |
| `9a40d7054` | canvas: overlay draws merge across elements where no pixel changes | a | 26166ef23 |
| `fd6765a4f` | docs: the overlay canvas draw order, built and measured | a | e5b0026cc |
| `15aa12967` | rendering: textures and meshes made ahead of their first draw | a | 1aa4e2c57 |
| `ae8b1691b` | rendering: first uploads start at Apply and go ahead of the frame | a | ebd2f748c |
| `d532e4d1a` | docs: textures and meshes made ahead of their first draw | a | 1aa4e2c57 and u60's ARCHITECTURE/PERFORMANCE GlAhead text |
| `4d6f72b60` | perfprobe: allocations by update section, and a script to compare them | a | 9b8614f58 |
| `95b945fe6` | audio: skip the game's calls on FMOD instances already destroyed | a | 4f3e5b7e7 |
| `860b5a0c9` | audio: patch FMOD's store to a stolen instance it has just freed | a | 585155303 |
| `cbef5d08c` | audio: clear released FMOD handles where the game keeps them | a | 58f5e3caf |
| `0d2540939` | e2e: Moono Tweaks specs follow the colony snapshots row | a | d9328cdf9 |
| `2a184c12e` | gl: the shader warm-up builds while the main thread goes on | a | 9025c5720 |
| `b9a0fcaeb` | tweaks: save-read-ahead reads and decompresses a save from its pick | a | eb13e1c24 |
| `1423bb176` | tweaks: drop save-read-ahead | a | ebc98f10a |
| `ebcbc3b74` | tweaks: loading-animation keeps the loading screen moving | a | 91cddc1e6 |
| `914314591` | tweaks: hold loading-animation until it runs on Vulkan | a | ab07f78f9 |
| `0fd22a2b5` | tweaks: anim batches apply an override texture again only when new | a | b94eec0fd |
| `884be1bfb` | tweaks: loading-animation is back, now that it runs on Vulkan | a | 581e7af95 |
| `03bda03fb` | engine: texel copies reuse their work items and need no pool thread | a | 915ee1eeb |
| `d7bdc2996` | gl: hand a frame over between draws, never in the middle of one | a | 78c1b679c |
| `c36184f68` | tweaks: take loading-animation out again for the hotfix | a | 897a74bcb |
| `2fb86e601` | gl: no mid-frame upload hand-over unless ONI_UPLOAD_FLUSH=1 | a | 804f3ced3 |
| `f4a3a2225` | docs: runq design, a broker for the shared test machine | a | 94d1e9bce, 863177c74 |
| `bff529867` | tweaks: fast-save-roots, stored items leave the save list by reference | a | b545f8572 |
| `dbdbe62f2` | tweaks: loading-animation is back, cleared of the reload hang | a | b48320540 |
| `bd9f4bb03` | scripts: queue the test runners through runq | a | 94d1e9bce |
| `f13cc03e5` | e2e: the tweaks search spec expects loading-animation's row | a | bb5242ea2 |
| `16f9bfe5b` | tweaks: sim-step-pace calls the sim only at its 200 ms steps | a | 5cc92349e |
| `ef656f93e` | tweaks: partitioner event walks and conduit updater sorts | a | 5fbc734b8 |
| `c1446d6bc` | docs: conduit tweaks landed, with their verify counts | a | 13dfbafd1 |
| `bace0db88` | docs: conduit tweaks' Windows A/B, rails -22%, step -5% | a | 002ef6241 |
| `395335fcd` | tweaks: lazy-table-refresh updates only the table rows in sight | a | d797990c5 |
| `a97b14b11` | tweaks: lazy-table-refresh on by default, the game's own while scrolling | a | 1f26277b6 |
| `01c5dc35d` | translations: zh for this week's tweak rows; search specs count them | a | 77a54e7bb |
| `8b4bd5661` | docs: frame generation tools under Moono, research | c | picked |
| `673308830` | docs: performance tables of 2026-11-13, both builds against the original | a | 9dae4e844 |
| `1f6515710` | tweaks: tables build their rows and reopen with less work | a | 7be544bca |
| `766800171` | tweaks: compact-report-history packs a save's notes into blocks | a | add344ed5 |
| `519a2b00d` | tweaks: fast-serialization writes the report history typed | a | 702590f0c |
| `e3a2adb9b` | tweaks: background-save makes a save's colony summary on its writer | a | b353d9072 |
| `23dd88968` | docs: the report history's writers, with the save's times | a | 5af7fb5da |
| `1da87c5d4` | host: pace the frame cap against a running deadline | a | d300ff5e8 |
| `f19d39506` | tweaks: fast-overlay-removals, Decor overlay removals by reference | a | afd88b881 |
| `57434ff42` | perfprobe: PERF lines carry the slowest 1% of frames | a | 9f55fdef6 |
| `d4580ecec` | perfprobe: frametimes.txt writes each window's frame times | a | e15285ea6 |
| `857a3bc4e` | docs: the slowest 1% of frames and what would raise the 1% low | a | c5d4ac80d |
| `1849a1e61` | ui: overlay text is as sharp as the original's at any UI scale | a | d25d1b4e4 |
| `c5739a9e8` | compact-nav-links: check each commit in verify mode; commit's cost | a | a666a3554 |
| `e94a01216` | tweaks: direct-rail-bridges, a bridge moves an item without re-storing | a | b898d481f |
| `657b005e1` | mods: rows for Fast Track and the mods U60's removed members break | a | 03131fffc |
| `f8de2424b` | docs: mod compatibility intro and tweak interactions, shorter | a | d139c2326 |
| `f2465ef85` | docs: step-aside audit covers the tweaks added since 2026-11-02 | a | a069cce1c |
| `70d009607` | tweaks: direct-rail-bridges is on by default | a | 27b7716e7 |
| `12f480fc7` | workshop: the item carries the U60 build alone | a | a45671f7c; u60's version has no description file to change |
| `164449713` | docs: README, shorter | a | 1bd0d68d9 |
| `7eab9509d` | docs: INSTALL-MODES, shorter | a | fe258108b |
| `dc7fb2649` | docs: RELEASES, shorter | a | 353df029e; its Workshop description notes came over in the same commit |
| `3807b73f7` | docs: DEVELOPMENT, shorter | a | 7cb213496 |
| `e87e7186d` | docs: ARCHITECTURE sections 1 to 3, shorter | a | 135d24fff |
| `c9c145418` | docs: research/one-percent-lows, shorter | a | 20eef3f09 |
| `aab996b89` | docs: research notes' summaries, shorter | a | 328c113a1; the two notes u60 lacked got it in a picked copy of this commit |
| `fd7069e7a` | docs: TWEAKS, shorter | a | 975ccf6da |
| `6db715c11` | docs: PERFORMANCE, shorter | a | b8c4e7365 |
| `8b3318e80` | tweaks: off-world-fallers lands around the step, not inside it | a | 883c06722 |
| `737dd1f7c` | docs: Unity's engine as a baseline for Moono's frame | c | picked |
| `dca1f52ae` | tweaks: brain-rate gives every brain a turn within 15 game seconds | a | 72eff11b3 |
| `3f2ca3245` | docs: TWEAKS per-tweak sections, shorter | a | 3777cf29d |
| `b51b7a9e2` | docs: PERFORMANCE tried-and-dropped and table notes, shorter | a | 2a0c73e84 |
| `1e4118007` | docs: ARCHITECTURE rendering, first half, shorter | a | 7b4de2731 |
| `f376dc459` | docs: ARCHITECTURE rendering, second half, shorter | a | 2ff639d18 |
| `3e76ca6d9` | docs: ARCHITECTURE platform, fonts and modules, shorter | a | 67e69c7fc |
| `a3198f5e3` | docs: ARCHITECTURE logging and diagnostics, shorter | a | 97a395d45 |
| `f8d299834` | tweaks: critter-needs-first, critters ready to eat or excrete go first | a | 4660f71b5 |
| `7d04b1ed2` | present: the log says what an overlay's hook sees of a DXGI present | a | a359e2b84 |
| `22313975f` | tweaks: brain-rate caps Duplicant brains only | a | e5af04ed8 |
| `228415406` | present: the log says whether the DXGI device answers as Direct3D 10 | a | 1fb27e4ca |
| `822f70406` | tweaks: brain-rate caps critters again, those with a need first | a | 83e757c8f |
| `7bacb63bd` | tweaks: shorter menu descriptions | a | 5de72a732 |
| `1ad14c965` | e2e: gas-eaters keep excreting under brain-rate when turns are scarce | a | a30c5b4d3 |
| `f549a5e8a` | e2e: moono-tweaks-zh brings brain-rate out from under the search box | a | a13ea2173 |
| `d7f6e9056` | tweaks: one test for a mod's patch in every stand-down check | a | cac1b8905; minus fast-overlays |
| `c33cb2d2f` | tweaks: the screen knows six more tweaks that step aside | a | e2a5f1bc4 |
| `27358d54c` | tweaks: check-tweaks finds patches declared but not made | a | bd4ca476a; minus fast-overlays |
| `de6cbf0bb` | tweaks: fast-overlays guards the helpers' instantiations | b | fast-overlays retired on U60 (c967677ea) |
| `aeb1fd17a` | tweaks: instant-reachability stands down when its drain keeps failing | a | b1212833d |
| `5dc4b94eb` | tweaks: stale comments, unused members and leftover switches | a | c677372ae |
| `d8c6e1564` | scripts: remove nine unused QA scenarios and qa-sheet.py | a | b7458cf9e |
| `2f0d300bf` | scripts: remove unityapi.sh, which nothing on master names | b | u60 still uses unityapi.sh (retarget step, GAME-UPDATES §5) |
| `2c35828a6` | tweaks: shorter comments, KB/MB in three log lines | a | fae4edc05; minus retired and U59-only tweaks |
| `c42e0da37` | scripts: shorter headers, one line for runq in each script | a | e256c4e7c |
| `da943576d` | quit: hide the window first, release nothing one by one | a | cb302158f |
| `38df0d6f2` | e2e: quit-to-desktop, Save and Quit to the desktop | a | dc3bbeb44 |
| `a9886a29e` | workshop: description drops the U60 public test line | a | empty on u60: its description never had the line |
| `7770ec2db` | install: update from Steam's copy of the Workshop item | a | 9b7a3ae0b |
| `7ae78a437` | loader: install off the main thread; Install & copy launch option | a | 16c365247; the description's step name came over in the same commit |
| `0ec33cb49` | input: show the IME's composition in text fields, as in unity | a | 137f49e94 |
| `005027c65` | e2e: game-update smoke specs that name no build | a | a072059fe |
| `11acbdf52` | docs: GAME-UPDATES.md is the procedure for a game update | a | 2b37b4f40 |
| `63482a03d` | tweaks: fast-save-type-checks keeps the save's component arrays | a | af77a18c4 |
| `705420a85` | tweaks: fast-serialization writes properties and number lists unboxed | a | bab055fe7 |
| `89303eb49` | docs: less garbage from each save, and the collection after it | a | f13ee1068 |
| `d0cac00b0` | tmp: check a text's scale only after its transform changed | a | 648b50d33 |
| `ccfa656e5` | docs: the text scale check landed; draw value reuse built, not landed | a | 2b2ba9009 |
| `e1af4e367` | diagnostics: log when Game.simData stops matching the sim's last data | a | e8d5edbea |
| `08b3f0faa` | tweaks: compact-report-entries packs past days' report entries | a | 6ad2e9151 |
| `73ca840c8` | engine: an analyzer keeps records and facades compared by reference | a | e5791da8d |
| `493ed241d` | docs: game-update procedure notes from the 763612 update | a | 2388f4e7c |
| `06bedd558` | e2e: shot specks leave out differences thinner than N pixels | a | 72dabea3f |
| `8a9ad7391` | tweaks: frame-time-cap and logic-in-sim-steps, both opt-in | a | aefb4de70 |
| `937dddcdb` | engine: components keep their own state; scripts have no record | a | 1701a39d0 |
| `5e490d5fc` | tweaks: critter-needs-first gives attacking critters turns too | a | 8e11af19e |
| `627b415a7` | tweaks: low-fps-timing, one row for the frame-rate fixes | a | f8fd227a7 |
| `913dddb98` | tweaks: low-fps-timing runs tile leaks on game time | a | 9230b4610 |
| `2733f5aa1` | docs: past game-code references point to oni-archives | a | a14b0c76a |
| `61c278df1` | e2e: Moono Tweaks specs check the search against the screen | a | 5bb496203 |
| `04245cca1` | e2e: Moono screen specs find rows by name, not by position | a | a2cbc45e1 |
| `24f60971b` | e2e: a failed mod unpack no longer leaves an empty folder | a | 22fa5f2bf |
| `ab359bd63` | build: BRUTAL packages of 2026-10-23, natives in .Native packages | a | b646b9127 |
| `daecd4f89` | workshop: a before/after teaser under the description's first line | c | picked |
| `cf236831c` | tweaks: critter-needs-first forgets the last colony's critters | a | d57cf46a4 |
| `34ce34f4e` | workshop: drop the caption under the teaser | c | picked |
| `808ac53e9` | workshop: the teaser is a 60 fps pan over the colony | c | picked |
