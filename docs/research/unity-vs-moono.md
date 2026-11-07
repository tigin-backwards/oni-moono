# Unity's player and Moono: how they differ, and what it means for ONI

How Moono's engine differs from Unity's player at the level of design, and which of those
differences matter for Oxygen Not Included. Moono is a clean-room engine: what this page says
about Unity comes from Unity's public documentation and blog, and from what we measured in the
original game (`tools/UnityProbe`, the performance runs). It doesn't rely on Unity's source, its
C# reference source or decompiled Unity assemblies. What it says about Moono comes from
[ARCHITECTURE.md](../ARCHITECTURE.md) and our measurements.

## At a glance

| | Unity's player (as ONI ships it) | Moono |
|---|---|---|
| Where engine objects live | Native C++ objects; the C# object is a wrapper holding a pointer to them [1] | Managed objects throughout; the "native side" is a managed record reached through a handle (ARCHITECTURE §3.2) |
| A call from game code into the engine | Crosses from C# into native code | An ordinary .NET call the JIT can inline |
| `obj == null` on a destroyed object | True, through Unity's custom `==`, which asks whether the native object still exists [1] | The same rule, as a read of one field (ARCHITECTURE §3.2) |
| Runtime | Mono, with its JIT | .NET (CoreCLR), with tiered JIT and profile-guided recompilation (ARCHITECTURE §2.1) |
| Garbage collector | Boehm-Demers-Weiser, with an incremental mode that spreads work over frames in small slices [2][3] (whether ONI's build turns it on: to check in its player settings) | .NET's generational collector (Server GC, concurrent): parallel on every core, mostly short gen0 pauses |
| Engine data layout | Engine-side arrays in native memory; transforms grouped per hierarchy root and updated in jobs [4] | Separate managed objects per GameObject, component and transform |
| Rendering threads | The main thread records intermediate graphics commands, a render thread turns them into graphics API calls; graphics jobs can spread that over worker threads [5] | The same split: draws recorded on the main thread and on worker threads, executed on a GL thread; texture copies on workers (ARCHITECTURE §3.5) |
| Graphics APIs | D3D11 in the Windows build, OpenGL in the Linux build | OpenGL and, on U60, Vulkan with the game's shipped shaders |

## The differences that matter for ONI

### Calls from game code into the engine

ONI's own C# makes thousands of small engine calls per frame: positions (`transform.position`,
`SetPosition`), component lookups (`GetComponent`), null checks on objects, activity flags. In
Unity each of these reaches the native object behind the wrapper [1]. In Moono each is a normal
method call on managed data, which the JIT can inline into the caller.

*Measured per call, original vs Moono:* pending (UnityProbe `callcost` scenario, Linux, main menu).

Moono also keeps a per-prefab layout of each GameObject's components, so `GetComponent` returns
from an array lookup instead of testing each component's type: the game's component lookups went
from 0.87 ms to under 0.1 ms a frame on a late-game colony (frame inventory, Oct 19 and Nov 7).

### Garbage collection

Unity's collector on Mono is Boehm's, a non-moving, non-generational collector; in incremental
mode, which Unity turns on by default, a collection is split into slices of a few milliseconds
across frames instead of one long pause [2][3]. .NET's collector is generational: most garbage dies young and is
collected in short gen0 pauses, with the heavy work spread over all cores. On a late-game colony
with every tweak, collection costs about 0.1 ms a frame on the main thread plus a gen0 or gen1
pause of 30–450 ms about once or twice a minute (frame inventory, Nov 7). The price is memory:
Moono uses about 1.3–1.6× the original's, partly because more of the engine's state is managed.

### Data layout

Unity keeps engine state in native memory, laid out for its own batch work. Its guidance on the
transform hierarchy, for instance, is to keep moving objects under many small roots so the engine
can split transform-change work into jobs across threads [4]. Moono's objects are separate
managed objects, so a loop that walks many of them pays a cache miss per object.

This matters little for ONI. The engine's own loops are small in this game: culling is a 2D
rectangle (0.24 ms a frame on a late-game colony), most objects are scene roots or one level deep,
there is no physics engine (gases, liquids and heat are the game's own grid simulation in
SimDLL) and no skeletal animation (KAnim is the game's sprite system, updated in its C#). Most of
the frame is the game's own C# logic touching engine objects, which is where Moono's design is
cheaper. A 3D game with crowds of skinned characters, deep hierarchies, many renderers or a
physics engine would be the opposite case: there the engine's batch loops dominate, and a managed
engine would need native-style data layouts for them.

Some Unity games of that kind, as illustrations (we haven't profiled them):
- crowds of animated characters: Totally Accurate Battle Simulator, V Rising;
- physics-heavy: Kerbal Space Program, Human: Fall Flat, Besiege;
- large 3D worlds with many renderers and deep hierarchies: Valheim, Subnautica, Rust, Escape from
  Tarkov, Cities: Skylines.

The line isn't 2D against 3D. Some 3D games draw their largest object counts with GPU instancing
and run their simulation in their own data structures (Dyson Sphere Program, a Unity factory
game, is an example), so the engine's per-object costs matter less there; and colony games such as
RimWorld are dominated by game logic as ONI is. RimWorld's developer has since moved it off Unity
to its own Waylith engine (version 1.7, announced in October 2026), reporting 2–4× better
performance with saves and most mods still working [7][8]: the same trade Moono makes for ONI.

### Rendering

Both split recording from execution [5]. Moono records most draw queues on worker threads and
copies the textures the game re-uploads each frame (about 20 MB) on workers, so the main thread
spends about 1.8 ms a frame on rendering on a late-game colony (frame inventory, Nov 7). We don't
have a matching breakdown of Unity's rendering threads for ONI.

## Why Moono is built the way it is

Unity is a general engine: it has to run 2D and 3D games, physics and crowds, on desktops,
phones, consoles and the web, from one editor. Several of its foundations follow from that. Its
scripting backends include IL2CPP, an ahead-of-time pipeline that is required on platforms
where a JIT isn't allowed [6]; a native C++ core runs the same everywhere and owns the heavy
engine-side work. Moono has one game to run, on desktop PCs, where a JIT is allowed and the game's
own C# logic dominates the frame. Each decision below is right for that case and would be wrong
for some games Unity serves.

| Decision | Why, for ONI | What it costs | Why Unity can't make the same choice |
|---|---|---|---|
| **Run the game's own code and data unmodified**, binding its references to Unity to our engine (ARCHITECTURE §1–2) | Mods, saves and game updates keep working; Klei's code is never patched on disk. | We must match Unity's API and behaviour exactly where the game or a mod relies on it, including undocumented behaviour, checked with UnityProbe in the original. | Not applicable: Unity is the original. |
| **Replace all three layers**: the native player, the engine API, and the Mono runtime | Each layer was a bottleneck for this game; replacing only one would keep the others' costs. | A large engine to maintain, on our own. | — |
| **.NET (CoreCLR) instead of Mono** (§2.1) | A faster JIT with tiered, profile-guided recompilation, faster class libraries, and a parallel generational collector. Desktop-only, so a JIT is fine. Harmony, which every ONI mod uses, works on it. | Some behaviours differ from Mono's (reflection order, type loading, Harmony's detour handling), and each needed matching for the game or mods. | IL2CPP platforms need ahead-of-time code [6], and Unity must keep its whole ecosystem's code working. It is moving the same way where it can: its 6000.7 documentation lists a CoreCLR scripting back end [3]. A scripting back end is the runtime for C# code [6]; the engine stays native, so engine objects are still C# wrappers around native ones [1] and engine calls still cross into native code. That back end brings the runtime gains, not the object-model ones. |
| **.NET's generational garbage collector** | Most of ONI's garbage dies young and is collected in short gen0 pauses, spread over all cores: about 0.1 ms a frame of main-thread cost on a late-game colony. | Memory (about 1.3–1.6× the original's), and an occasional gen1 pause of tens to hundreds of ms on very large colonies. | Unity's incremental mode trades throughput for small, predictable slices [3], which suits frame-rate-sensitive games on any hardware. |
| **A managed object model** (§3.2) | ONI's C# makes thousands of small engine calls a frame; as plain .NET calls the JIT can inline them. | More managed objects for the collector to trace; objects are scattered in memory. | Its native core serves IL2CPP and every platform, and runs heavy engine-side work (physics, skinned animation, culling at 3D scale) on compact native data [1][4]. |
| **Only the API the game and its mods use, each member complete or absent** (§3.1) | A smaller engine whose every member is checked against what's referenced (ApiCheck). A missing member fails loudly with its exact name. | A mod calling Unity API that nobody else uses fails until we add it; the mod catalog scan finds these ahead of time. | It must offer its whole documented API to every project. |
| **Run the game's shipped shader programs on our own GL 4.5 (and Vulkan on U60) device**, with Unity's built-in forward renderer restricted to what ONI uses (§3.5) | Every Klei visual effect keeps its exact logic, with no hand-ported shaders; the renderer is tailored (draw recording and texture copies on worker threads). | Tied to each game build's shader data; GL driver quirks (an AMD Windows driver issue, Mesa under WSL). No Direct3D renderer, by choice: the Windows build's shaders also carry Direct3D 11 programs, so one could be added, but one GL/Vulkan renderer covers both platforms (Windows already presents through DXGI). | It supports every pipeline, platform API and shader feature. |
| **Our own reader for Unity's asset files** (§3.4) | No Unity runtime needed; loading can be tuned for this game (the Load screen, the save's reads). | Asset formats can change with a game update's Unity version. | — |
| **Clean-room uGUI, TextMeshPro and Timeline** (docs/cleanroom) | Licence-clean; the UI's per-frame costs can be optimised for this game's HUD (hidden screens skipped, raycasts and canvas work). | Their behaviour must match Unity's packages, including what mods patch with Harmony. | — |
| **Unity's threading rules, plus our own threads** (§4) | The game's worker threads keep their release-player leniency; the GL thread, the draw recorders and the presenter run beside the main thread. | Our engine must stay safe under the same leniency. | Same rules, implemented natively [5]. |
| **Game tweaks**: optimisations of the game's own code, exact by default (TWEAKS.md) | The biggest single source of speed on late-game colonies (about 51 → 144 fps on one). Only possible because there is one game. | Each tweak needs a verify mode and must step aside for mods that patch the same code. | A general engine can't change its games' code. |

**What we gave up, honestly:** memory, an occasional long collection on the biggest colonies, a
native Linux crash reporter (the Linux player has no native host of ours), and the work of
matching every Unity behaviour the game or a mod depends on, again with each game update.

## What this adds up to

On a late-game colony the original runs at about 11 fps and Moono at about 51 fps with no
tweaks, on the same Windows machine ([PERFORMANCE.md](../PERFORMANCE.md)). That gain bundles
the runtime (JIT and collector), the object model and our rendering; we haven't measured how it
splits between them. The per-call numbers above, once measured, cover the object model's part
for single calls.

## Sources

Unity's documentation and blog:

1. Unity Blog, ["Custom == operator, should we keep it?"](https://unity.com/blog/engine-platform/custom-operator-should-we-keep-it) (2014): the C# object holds a pointer to the native object; `==` reports a destroyed object as null.
2. Unity Manual, [Garbage collection](https://docs.unity3d.com/6000.1/Documentation/Manual/dotnet-garbage-collection.html): the Boehm collector for Mono and IL2CPP.
3. Unity Manual, [Garbage collection modes](https://docs.unity.com/en-us/engine/6000.7/manual/analysis/performance-memory/managed-memory/incremental-garbage-collection): incremental mode, on by default, spreads collection over frames; Mono and IL2CPP use Boehm-Demers-Weiser; the 6000.7 page adds that the CoreCLR scripting back end doesn't support incremental mode.
4. Unity Blog, [Spotlight Team best practices: optimizing the hierarchy](https://blog.unity.com/technology/best-practices-from-the-spotlight-team-optimizing-the-hierarchy): transform changes are dispatched as jobs grouped by hierarchy root.
5. Unity Scripting API, [RenderingThreadingMode](https://docs.unity3d.com/ScriptReference/Rendering.RenderingThreadingMode.html): the main thread generates intermediate graphics commands that a render thread converts to API calls; graphics jobs use worker threads.

6. Unity Manual, [Scripting back ends](https://docs.unity.com/en-us/engine/current/manual/programming-environment/scripting-backends): Mono (JIT) and IL2CPP, an ahead-of-time pipeline required on platforms where a JIT isn't supported.

7. PC Gamer, [RimWorld drops Unity for its own in-house engine](https://www.pcgamer.com/gaming-industry/game-development/rimworld-drops-unity-for-its-own-in-house-engine-says-your-saves-still-work-so-do-most-mods-and-the-whole-thing-runs-twice-as-fast/) (October 2026).
8. GamingOnLinux, [RimWorld switching from Unity to their own Waylith engine](https://www.gamingonlinux.com/2026/10/rimworld-switching-from-unity-to-their-own-waylith-engine-and-a-new-game-was-teased/) (October 2026).

Moono: [ARCHITECTURE.md](../ARCHITECTURE.md) §2.1 (runtime), §3.2 (object model), §3.5
(rendering), §4 (threading); [frame-inventory-2026-11.md](frame-inventory-2026-11.md);
[PERFORMANCE.md](../PERFORMANCE.md).
