# Merging the engine records into the facades (2026-11-17)

Status: the guard rails (step 1 of the recommendation), step 2 (every component's state on the
component) and step 3 (every GameObject's state on the GameObject) landed ("Progress" below). The study answers two questions about candidate 3 of
[live-managed-heap.md](live-managed-heap.md), "`BehaviourNative` merged into the facade":
how much frame time the facade-to-record step costs, and how safe the merge would be.

Each UnityEngine object in Moono is two .NET objects:

- **The facade** is what the game sees: `UnityEngine.Object` and its subclasses `GameObject`,
  `Component`, `Transform`, `Behaviour` and `MonoBehaviour`.
- **The engine record** is what Unity keeps natively: `OniMoono.Engine.NativeObject` and its
  subclasses `GameObjectNative`, `ComponentNative`, `BehaviourNative` and `TransformNative`, in
  `src/OniMoono.UnityEngine/Engine/NativeObjects.cs`.

The facade holds its record in `m_Native` (`Core/Object.cs:21`), the record points back through
`Managed`, and `ObjectTable` holds the record in a slot. The merge would keep the record's fields
in the facade itself, so each object is one .NET object.

## The short version

- **Frame time: little to gain.** The members the game calls most never reach the record:
  - The fake-null checks (`==`, `!=`, the bool conversion) read only `m_CachedPtr` on the facade
    (`Core/Object.cs:166`). They are 270,000 calls a frame on the late-game colony.
  - `Component.gameObject` and `.transform` are cached on the facade already
    (`Core/Component.cs:29`). They take the cached path in over 99% of their 35,000 calls a
    frame.
  - What does reach a record is about 92,000 calls a frame at the instrumented run's pace. At
    the tables' frame rate that is about 51,000 a frame (9,000 to 92,000, depending on how much
    of it follows game time).
  - Merged, each call would save about 0.5–1 ns while its objects are in the caches, as the
    game's calls mostly are, and 7–120 ns when they are not.
  - That is an estimated 0.03–0.2 ms of a 9.2 ms Windows frame (0.3–2%), most likely under
    0.1 ms.
  - The sampled profile agrees. The pure-indirection members that the JIT keeps as their own
    frames take about 0.5% of the main thread, their real work included.
- **The heap is the real gain.** The merge removes 2.72 M objects (9% of the live ones) and
  5.4 M references. That is about 90–110 MB on the late-game colony, and the heap study's
  35–45 ms of a 0.75 s blocking full collection.
- **It is safe, with work.** No category found a game-visible change that cannot be kept exact.
  The risks are inside the engine:
  - `==` on a record type would become Unity's fake-null comparison (105 sites).
  - Collections keyed by records would become keyed by facades, whose `Equals` treats every
    destroyed object as equal (11 hashed collections and 28 lists).
  - Engine state would have to be set before the game's constructors run, without field
    initializers.
  - The clone order changes.
  - One field-walking game method, `Localization.SetFont`, restricts where new fields may go.
  - Each of these has a mechanical rule and a test that would catch a slip.
- **Size: large.** About 80–100 files and 4,000–6,000 lines:
  - 824 references to the record types in 65 files;
  - 282 reads of `Native`;
  - 36 record classes with about 390 fields and 4,800 lines of code that moves into facade
    partials.
- **Recommendation: not now.** It is not a frame-time change, and for the pause it ranks behind
  the report entries (~70 ms) and the event tables (25–40 ms), as the heap study found. If the
  pause is taken on later, do the guard rails first; they are small and worth landing alone. Then
  merge in this order: `BehaviourNative`, `GameObjectNative`, `TransformNative`. Leave the
  engine components' own records alone ("Recommendation").

## Method

- **Build.** u60 at 5c232fce (U60 build 763325, every DLC), release, a player's default tweaks
  (`ONI_TWEAK_SETTINGS=1`), `--frame-stats`, a late-game Spaced Out colony (cycle 17,000+, the
  e2e fixture), Linux under Xvfb at 1280×720, PerfProbe holding speed 3.
- **Counts.** A temporary instrumentation build, not committed
  (`out/facade/instrumentation.patch` in the study's worktree), counted every call of:
  - the facade members that reach the record;
  - the fake-null operators;
  - the typed record accessors (`GameObjectNative`, `ComponentNative`, `BehaviourNative`,
    `TransformNative`, `RectTransform.Rect`);
  - `Object.Native` itself, the record's `Managed`, the script dispatch, Instantiate and record
    construction.

  Counts were plain increments, logged per 600-frame window. One in 4,096 `Native` reads also
  sampled a stack, for the record-reading frame and the first game frame above it.
- **Run.** One runq `linux-player` job of 15 minutes: the colony was up after 192 s, then
  8 steady windows of 600 frames.
  - The instrumentation and the loaded machine held it at 9.9 fps. The game hit its limit of game
    time a frame: 0.30 game seconds and 0.85 sim steps a frame, against about 0.03 and 0.15 at
    the tables' 98 fps.
  - Counts per frame of work tied to the frame (drawing, animation, UI, script `Update`) carry
    over to a faster frame. Counts of work tied to game time shrink about tenfold.
- **Cost per call.** A console micro-benchmark (`out/facade/FacadeBench`, local, .NET 10,
  workstation GC) builds the same shapes as the engine:
  - 150,000 game objects with 14 behaviours each (2.1 M behaviours, about the colony's 2.33 M),
    with game data between them, compacted before timing;
  - the shapes **two-adjacent** (each record allocated just before its facade, as `Lifecycle`,
    the cloner and the loaders do), **two-scattered** (records apart from their facades, in
    random order) and **merged** (the record's fields in the facade);
  - each member's code path copied from the engine: `Volatile` handle read, typed cast, field.

  Each is timed in random order over all the objects (**cold**: the working set is far larger
  than the caches) and over 672 behaviours of 48 game objects (**hot**: in L1 and L2). A first
  series ran each shape in its own process. Its cold figures moved by up to 4× between processes
  on the loaded machine, so the figures below come from a second series:
  - all three shapes in one process, each operation timed in 31 alternating rounds;
  - three processes, in runq's perf slot (`linux-perf`, not flagged noisy);
  - per operation, the median of the paired differences, with the quartiles across rounds.
- **Sampling cross-check.** No new profile was taken. The render study's `perf record` of the
  same colony was reused ([main-thread-rendering.md](main-thread-rendering.md), u60 56e7855b,
  60 s at 499 Hz, user mode, 39 fps, taken on a busy machine).
- **Safety.** Read from the code, with file:line references to u60 5c232fce.
  - The `==`/`!=` sites were found by a probe build: `GameObjectNative` and `ComponentNative`
    were given `[Obsolete]` comparison operators, so the compiler named every site.
  - Game and mod reflection was read in the decompiled 763325 assemblies and in a string scan
    of 2,898 Workshop folders, with a sample decompiled.

## Part 1: frame time

### What reaches a record

Calls a frame, the mean of the 8 steady windows (0.30 game seconds a frame):

| Member | Calls a frame | Reaches the record? |
|---|---:|---|
| `Object ==` | 161,030 | no: reads `m_CachedPtr` |
| `Object !=` | 109,936 | no |
| `Component.transform` | 19,177 | no in 99.8% (the facade's cache); 43 a frame take the record path |
| `Component.gameObject` | 16,252 | no in 99.2%; 130 a frame take the record path |
| `Object.Equals` | 8,127 | no |
| `Object` to bool | 4,283 | no |
| **`Transform` members** (position, localPosition, rotation, parent, `GetPosition` and the rest; the `TransformNative` accessor) | **32,427** | yes |
| `RectTransform` members (`Rect`) | 1,255 | yes |
| **`GameObject` members** (the `GameObjectNative` accessor) | **20,839** | yes |
| · `GetComponent<T>` | 12,380 | yes |
| · `transform` | 4,717 | yes |
| · `TryGetComponent` | 1,712 | yes |
| · `layer` | 1,008 | yes |
| · `GetComponents` | 630 | yes |
| · `activeSelf`, `activeInHierarchy`, `SetActive`, `GetComponentIn*` | 365 | yes |
| `Component.GetComponent<T>` (the `ComponentNative` accessor) | 5,913 | yes, and on to the GameObject's record |
| `Behaviour.enabled` and other `BehaviourNative` reads | 1,620 | yes |
| `Behaviour.isActiveAndEnabled` | 8,377 | yes |
| `Object.GetInstanceID` | 6,335 | yes |
| `name` | 34 | yes |
| record to facade (`Managed`): script `Update`/`LateUpdate` dispatch | 5,531 of 5,964 | yes, the other way |
| other `Object.Native` reads by the engine | about 23,000 | about half are asset records (textures, meshes, materials: the draw recorder's worker threads mostly), outside the merge |

The total is about **92,000 calls a frame that reach a record of the merge's types**. Every
`Object.Native` read was 116,544 a frame.

**Where they come from** (sampled `Native` reads, 94,563 samples over the run, the load
included; shares of all reads):

- **Drawing and animation, tied to the frame:**
  - `KBatchedAnimController.UpdateAnim` and `GetTransformMatrix` reading transforms: about 7%
    (some on worker threads);
  - `KBatchedAnimUpdater`: 4%;
  - the canvas system (ordering, overlays, batching): 7%;
  - culling: 2%; the UI raycast: 1.6%; particles: 1.6%.
  - The draw recorder's texture reads, 10%, are outside the merge.
- **Game time:**
  - the state machine buckets: 2%;
  - `CellChangeMonitor.MarkDirtyRecursive`: 2%;
  - `TransitionDriver`: 2.3%;
  - `RequireInputs`: 1%;
  - `DecorProvider`: 0.8%;
  - many state-machine callbacks under 0.6% each.
- **The load:** `SaveLoadRoot.Load`: 3%.
- The 60 largest sites, the ones the run logged, cover 65% of the samples.

At the tables' frame rate (about 98 fps with the default tweaks on U60, 0.03 game seconds a
frame; [PERFORMANCE.md](../PERFORMANCE.md), "The numbers"):

- the frame-tied half stays at about 46,000 calls;
- the game-time half shrinks to about 5,000.

So a frame there makes about **51,000 record-reaching calls**, between 9,000 (were all of it tied
to game time) and 92,000 (were all of it tied to the frame).

### Cost per call

Nanoseconds a call. Each range is over the three processes; each process gives the median of
its 31 rounds. "Saved" is the median, over the rounds, of a two-object shape's time minus the
merged shape's time in the same round.

| Operation (engine path copied) | Cold: two-adjacent | Cold: merged | Cold: saved (adjacent) | Cold: saved (scattered) | Hot: two-adjacent | Hot: merged | Hot: saved (adjacent) | Hot: saved (scattered) |
|---|---|---|---|---|---|---|---|---|
| fake-null check (control: the facade only, in both) | 11.0–34.9 | 9.7–29.9 | 1.2–9.4 | −3.2 to −0.8 | 0.6 | 0.6 | 0.0 | 0.0 |
| `Behaviour.enabled` | 30.0–108.7 | 14.2–52.0 | 14.9–49.5 | 21.4–59.1 | 1.2–1.8 | 0.8–1.2 | 0.4–0.6 | 0.6–1.0 |
| `isActiveAndEnabled` | 26.1–51.1 | 11.3–19.2 | 13.2–28.8 | 21.1–35.6 | 1.0–1.3 | 0.6–0.7 | 0.5–0.6 | 0.6–0.9 |
| `GameObject.activeInHierarchy` | 18.8–80.8 | 8.3–26.8 | 10.8–52.6 | 10.0–50.7 | 1.0–1.1 | 0.9 | 0.1–0.2 | 0.1–0.3 |
| `Component.GetComponent<T>` (component → game object → layout → match) | 161.3–319.0 | 81.2–190.5 | 72.8–122.3 | 70.4–134.3 | 3.2–8.4 | 2.2–3.6 | 0.9–2.3 | 1.7–3.8 |
| `Component.name` | 59.6–89.3 | 25.8–39.7 | 35.9–52.9 | 32.9–74.0 | 1.1–1.7 | 0.8–1.1 | 0.3–0.7 | 0.6–0.9 |
| `Transform.localPosition` | 15.9–44.3 | 8.2–17.7 | 7.4–33.0 | 10.5–30.4 | 2.4–3.1 | 2.4–2.5 | 0.0 | 0.0 |
| script dispatch (record → facade → `Update`) | 46.8–110.6 | 23.6–46.4 | 21.5–71.1 | 36.6–94.0 | 1.5–2.7 | 1.0–1.7 | 0.6–1.0 | 0.9–1.4 |

- **Cold**, the merge saves one cache miss a call: 7–70 ns for a single field, 70–120 ns for
  `GetComponent<T>`, which leaves two records behind. The loaded machine moves the cold
  absolute figures by up to 3× between processes, but every paired difference is positive and
  the control stays near zero.
- **Hot**, it saves a dependent load and a type check: 0.1–1 ns, up to 2.3 ns for
  `GetComponent<T>`. `localPosition` saves nothing hot: the benchmark's chain of float additions
  hides the load.
- **Records allocated next to their facades** (as the engine allocates them) are hardly cheaper
  cold than scattered ones: the record is its own cache line either way.
- **In the game the calls run nearly hot.** The game touches the same objects again and again
  within a frame. The sampled profile gives `Components.First`, the non-inlined core of
  `GetComponent<T>`, 0.05 ms a frame at 39 fps. That is 2.5–10 ns a call for the 5,000–20,000
  calls such a frame makes, against the benchmark's 3–8 ns hot and 160–320 ns cold.

### The estimate

- **Calls.** About 51,000 record-reaching calls a frame at the tables' 98 fps, between 9,000
  and 92,000 (above).
- **Saving a call.** Near the hot figures, so 0.5 ns or so on the mix of members. Transform reads
  are the largest group and save the least; `GetComponent<T>` saves 1–2 ns.
- **A frame.** 51,000 × 0.5–1 ns ≈ **0.03–0.05 ms**. If one call in ten missed the cache as in
  the cold case (about 30 ns), that adds about 0.15 ms. The range is **0.03–0.2 ms of a 9.2 ms
  frame, 0.3–2%**, and most likely under 1%. On the every-tweak frame of 5.5 ms it is the same
  milliseconds, a little more of the frame.
- **How sure.**
  - The counts are exact for the run (every call counted).
  - The split between frame-tied and game-time calls is an estimate from sampled sites; it moves
    the result by at most 2× either way.
  - The per-call saving is the weakest part. The benchmark bounds it (0.1–120 ns), and the
    profile's `Components.First` puts the game near the hot end.
  - The answer, under about 0.2 ms a frame, holds across that range.
  - The machine was under load throughout. Only the paired benchmark differences and the counts
    are free of it; the cold absolute figures are not.

### Sampling cross-check

In the reused profile (main thread, 27,116 samples, 60 s at 39 fps), the facade and record
members that the JIT kept as their own frames take 1.5% of the main thread, their real work
included:

- `TransformNative.MarkDirty` 0.41%;
- `Components.First` 0.22%;
- `RectTransformNative.ComputeRect` 0.15%;
- `Component.get_transform` 0.10%;
- `Object.Equals` 0.08%;
- `GameObject.get_transform` 0.04%.

The pure-indirection members among them (the two `transform` getters, `gameObject`, `enabled`,
`Transform.Wrap`, `Components.First`) take about 0.5%. Inlined accessors don't show as frames:
their time counts in the game's callers. So sampling cannot isolate the record step, but it
bounds it: the members that make it take about 0.5–1.5% of the main thread, everything in them
included.

### Allocations and memory

- **Instantiate:**
  - In steady play the game instantiated 10 times a frame. Each made 14.4 records and 14.4
    facades (a game object, its transform and about 12 components).
  - Merged, each would allocate 14 objects fewer, about 460 bytes. At 33 instantiations a game
    second that is about 15 KB a game second, under 1% of the 2 MB or so the late-game colony
    allocates a game second ([PERFORMANCE.md](../PERFORMANCE.md), "Why the engine alone is
    faster"). It is not a measurable saving.
- **Memory** (the live heap study's dump, `types.tsv`). The records the merge would remove:

  | Record | Objects | MB |
  |---|---:|---:|
  | `BehaviourNative` | 2,334,613 | 280 |
  | `GameObjectNative` | 167,711 | 35 |
  | `TransformNative` | 101,687 | 38 |
  | `RectTransformNative` | 66,024 | 31 |
  | `CanvasRendererNative` | 50,246 | 37 |
  | the others (cameras, renderers, colliders, animators, groups) | about 3,000 | under 1 |
  | **All** | **2,723,000** | **421** |

  - **What one merged object saves:**
    - the record's header and method table: 16 bytes;
    - its `Managed`: 8 bytes;
    - the facade's `m_Native`: 8 bytes;
    - a component record's `Name`, which is always "" (a component's name is its game
      object's): another 8 bytes, if dropped.

    That is 32–40 bytes, so about **87–109 MB**: 2% of the 4.92 GB live heap, under 1% of the
    working set.
  - **Objects and references:** 2.72 M objects fewer (9.3% of the 29.2 M live) and 5.4 M
    references (5.9% of 92.1 M: `Managed` and `m_Native`).
  - **The pause:** at the heap study's model (5 ns an object, 2.5 ns a reference, 0.04 ns a
    byte) that is about 31 ms of the model's 521 ms (6%), or about 45 ms of the 0.75 s Windows
    pause. The heap study's 35–45 ms stands.
  - **Unchanged:** the `ObjectTable` slot (2.8 M references in one array, nearly free to trace).
    Each game object's `ComponentLayout.Cached` (141,621 objects, 4.5 MB) could be folded into
    the game object as well, a further 0.14 M objects.

## Part 2: safety

Each category lists what the merge has to handle:

- **Mechanical** sites are a rename, a retype or a moved field.
- **Design** sites need a new form.

"Merged types" means `GameObject`, `Component`, `Behaviour`, `MonoBehaviour`, `Transform`,
`RectTransform` and the engine components, whose records would move into them.

### 1. Records without a facade, or before or after it

- **The record is always made first:**
  - `Lifecycle.CreateComponent` (`Engine/Lifecycle.cs:138`), `AttachTransform` (:43),
    `ConvertTransform` (:159);
  - the cloner's `CloneStructure` (`Engine/Serialization/Cloner.cs:170`);
  - the loaders (`Engine/Assets/Binders.cs:26`, :65, :127, :197; `ObjectLoader.cs:92`).

  `Object.ConstructBound` (`Core/Object.cs:43`) then hands the record to the facade's
  constructor through the thread-static `s_PendingNative` (:26). The base constructor binds it
  after the subclass's field initializers ran. A type without a parameterless constructor is made
  with `GetUninitializedObject` and bound with no constructor run (:54).
- **Merged:** the facade has to exist first, or the base constructor has to copy a pending state
  in. The second keeps today's exact order:
  - Field initializers run before the binding, as now: they see the object unbound, as now.
  - The constructor body runs after it.

  All merged state must be set explicitly, in `Bind` and the factories, never by C# field
  initializers. An uninitialized object would miss them, and `Object()` would overwrite state
  set before it. About 20 fields of the six core records start non-zero (for example
  `Enabled = true`, `Tag = "Untagged"`, `ActiveSelf = true`, `RootSlot = -1`,
  `LocalScale = one`); the engine components' records add about 110 more.
- **Records outliving their facade:**
  - `Unbind` (`Core/Object.cs:89`) clears `m_CachedPtr` and `m_Native`. The game then keeps a
    fake-null facade, and the record lives on only while an engine structure holds it: phase
    lists, collider snapshots, GUI participants, coroutine lists. Those test `Destroyed`.
  - Merged, the record is the facade, so a destroyed facade the game still holds would keep its
    engine state alive: the component array, the child arrays, the script, the coroutines.
  - So `Unbind` must clear the merged references, with empty arrays rather than null for what
    worker threads read (the component array).
  - Today the game's destroyed facades hold only `m_GameObject` and `m_Transform` (the
    precedent, `Core/Component.cs:29`).
- **Facades without a record:** a component made with `new` by game or mod code (Unity warns)
  gets no record and stays fake-null (`Core/Object.cs:32`). Merged, it carries the unused fields.
  This is harmless.
- **Assets:** prefab hierarchies are records bound when loaded, with `Serialized` kept until a
  subsystem's `Bind` (`NativeObjects.cs:37`). Merged, `Serialized` is a facade field, which is
  mechanical. Materials, textures, meshes, shaders and fonts are not merged types; they keep
  their records.
- **Counts:**

  | Item | Count | Class |
  |---|---|---|
  | Construction sites | 14 | design (the pending state) |
  | Engine fields with non-zero initial values | about 20 core, about 110 more in engine components | design (no initializers), then mechanical |
  | `Unbind` clearing merged references | 1 | design |
  | Fake-null facades made with `new` | 1 | none |

- **Risk:** medium. A missed initial value makes a component start disabled or a transform
  start at scale 0, which the lifecycle and transform tests would catch at once.

### 2. Destroyed objects and fake null

- **Liveness:**
  - is `m_CachedPtr` alone (`Core/Object.cs:84`, :92, :166);
  - is set by `Bind` and cleared by `Unbind`;
  - does not change with the merge.
- **The record's own flags:**
  - `DestroyRequested`: set at `Lifecycle.cs:508`, 6 sites in 3 files;
  - `DestroyPending`: set at :530 and :541, 9 sites;
  - `Destroyed`: set at :609, :615, :659 and :679, 49 sites in 18 files, read by the cull caches,
    canvas system, phase lists, particles, physics and GUI.

  They move into the facade; each read is a rename.
- **Engine code that tests liveness through the record** (`c.Native != null`,
  `Native is ComponentNative cn`): 282 `Native` reads become `IsAlive(c)` or direct field reads.
  This is mechanical.
- **`GetInstanceID`** must keep returning 0 once destroyed (Unity 6, `Core/Object.cs:124`): it
  tests the handle first. Mechanical.
- **`ObjectTable`:**
  - Its slots (`ObjectTable.cs:21`) are typed `object` and hold a record or a
    `WeakReference<NativeObject>`. `At` (:35) tests the weak type first and casts unchecked.
  - Merged, a slot holds the merged facade, and asset records as now. `At` and the weak type
    become `WeakReference<object>`.
  - `Resolve` and `FindByInstanceId` (:129, :142, 6 callers) return either kind, behind one
    accessor. Mechanical with one shape change.
- **`Resources.UnloadUnusedAssets`:**
  - The probe (`AssetUnloader.cs:46`) weakens the candidates' slots, collects twice and frees
    what was collected (`ObjectTable.Weaken`/`Strengthen`, :97, :112).
  - It relies on the record living exactly as long as its facade (each references the other).
    Merged, it is one object, so reachability is the same.
  - The candidate test (:57) switches on record types; it would switch on facade types.
- **Bundle and scene unloads** destroy through `ObjectLoader.LoadedFrom` and
  `SceneLoader.cs:135`. Mechanical.
- **The hazard: equality.**
  - Today the engine compares records with `==`, which is reference equality.
  - Retyped to facades, every `x == null`, `x != null` and `a == b` would call Unity's
    `op_Equality`, which treats destroyed objects as null and as equal to each other. The parent
    walks (`for (var p = Parent; p != null; …)`) would stop at a destroyed parent and pay the
    handle read too.
  - The probe build found **105 such sites in 21 files**: CanvasSystem 34, Hierarchy 17,
    FrameRenderer 7, SceneRoots 6, NativeObjects 5, Lifecycle 5, Transform 5, Finder 4, and the
    rest 1–3 each.
  - All need `is null`, `is not null` or `ReferenceEquals`.
- **The same hazard in collections:**
  - Default comparers call the facade's `Equals`, under which two destroyed objects are equal.
    Hash codes stay by reference, so a collision is rare, but when it happens one destroyed
    entry can stand for another.
  - **11 hashed collections** would be keyed by merged types:
    - BehaviourManager's `pendingAddAt`, `members` and `pendingRemove`
      (`BehaviourManager.cs:102`–104);
    - `GUIManager.byBehaviour` (`IMGUI/Engine/GUIManager.cs:48`);
    - `TransformMoveWatch.tokens` and `queued` (`TransformMoveWatch.cs:14`–15);
    - `CanvasSystem.movedSet` (:507);
    - `FrameRenderer.visibleThisFrame` and `visibleLastFrame` (:114–115);
    - the activation set at `Lifecycle.cs:273`;
    - `UiDump`'s paths (:56).
  - **28 `List<>` declarations of merged record types**, whose `Remove`, `Contains` and
    `IndexOf` would use the same `Equals`.
  - These need `ReferenceEqualityComparer.Instance` or by-reference loops, as `ChildList.IndexOf`
    already does for this reason (`Engine/ChildList.cs:22`).
  - The `ConditionalWeakTable`s keyed by merged types (5) compare by reference already.
- **Counts:**

  | Item | Count | Class |
  |---|---|---|
  | `==`/`!=` on record types | 105 sites, 21 files | design rule (`is null`), then mechanical |
  | Hashed collections keyed by merged types | 11 | design rule (reference comparer) |
  | Lists of merged types | 28 | mechanical once the rule holds |
  | Destroy flags | 64 sites | mechanical |
  | `Native` reads | 282 | mechanical |
  | `ObjectTable` slot and weak-reference shape | 1 type, 6 callers | design (small) |
  | UnloadUnusedAssets candidates | 1 | mechanical |

- **Risk: medium-high without guards, low with them.** A missed `==` changes behaviour only once
  something is destroyed, which is the hardest case to test. A Roslyn analyzer that refuses `==`,
  `!=` and default-comparer collections on `UnityEngine.Object`-typed operands in the engine's
  own code (`OniMoono.Engine` and the engine's facade partials, not the game-facing API) turns it
  into a build error.

### 3. Instantiate and serialization

- **Our serializers already skip the merged fields:**
  - The one field filter, `SerializedLayout.ScriptFields`
    (`Engine/Serialization/SerializedLayout.cs:91`, fields at :216–243), walks base types but
    stops at `Object`, `Component`, `Behaviour`, `MonoBehaviour` and `ScriptableObject`
    (`IsEngineBase`, :263–264).
  - It also takes only public or `[SerializeField]`, non-static, non-readonly fields (:266–271).
    So private engine fields on those bases are excluded twice.
  - It serves three paths: the Instantiate field copier (`Cloner.CompileScriptCopier`), the asset
    readers (`ScriptData.ReadFields`, `ReadFieldsByTree`) and the JSON writer and reader.
  - Unity's MonoBehaviour header fields in asset files (`m_GameObject`, `m_Enabled`, `m_Script`,
    `m_Name`) are read separately into the record (`Engine/Assets/Binders.cs:101`–143), which
    is mechanical.
  - `JsonUtility` refuses engine types other than MonoBehaviour and ScriptableObject
    (`Core/JsonUtility.cs:14`).
- **The clone order changes.** `CloneStructure` makes the record, copies its state
  (`CopyStateFrom`, `Cloner.cs:193`) and then constructs the facade. Merged, the facade comes
  first and receives the state afterwards, or through the pending state of category 1. Sites:
  - 32 `CopyStateFrom` overrides in 17 files;
  - 4 `RemapReferences` overrides (VideoPlayerNative, Canvas, ParticleSystemNative,
    PlayableDirectorNative; called at `Cloner.cs:114`);
  - 3 `EngineCloneAsset` overrides.

  The source of a copy is a live facade either way.
- **The game's save format can't see them.** KSerialization's `SerializationTemplate` walks base
  types up to `object`:
  - Under OptOut, the default for a `KMonoBehaviour` without `[SerializationConfig]`, it takes
    public fields and public settable properties.
  - Under OptIn it takes only `[Serialize]` members.

  Private merged fields are never saved. The rule is the one the facades already keep
  (ARCHITECTURE.md §3.2): no public field or public settable property beyond Unity's. The API
  conformance check guards it.
- **Not affected:** the game's component auto-wiring (`MyAttributeManager`, `[MyCmpGet]`/`Req`/
  `Add`, which stops at `KMonoBehaviour`); the game's YamlIO and Newtonsoft uses (data classes,
  not components).
- **A small edge:** `MemberwiseClone` of a component would now copy engine state instead of
  sharing the record. No game or sampled mod code clones a Unity object that way.
- **Counts:**

  | Item | Count | Class |
  |---|---|---|
  | Serializer filter | 1 filter, 3 users | none (already excludes) |
  | Clone state hand-over | 32 + 4 + 3 overrides, 14 construction sites | design |
  | KSerialization templates | 1 | none, while fields stay non-public |

- **Risk:** low for serialization, medium for the clone order (every Instantiate goes through
  it; the Instantiate tests and the verify run cover it well).

### 4. Readers on other threads

- **Transforms' world data:**
  - Worker threads read it: the game's jobs, `KBatchedAnimController.GetTransformMatrix`,
    `KBatchedAnimInstanceData.WriteToTexture` and `fast-anim-batches` (sampled on workers above).
  - The caches publish with release writes and acquire reads (`NativeObjects.cs:290`–345, 29
    `Volatile`/`Interlocked` sites in the core files).
  - Merged, the fields and their `Volatile` accesses move together; the guarantee is per field,
    not per object. Mechanical.
- **Copy-on-write arrays:**
  - The component arrays (`NativeObjects.cs:51`) and child lists (:173) are read without locks
    by `Components` (`Engine/Components.cs`, "safe from worker threads").
  - They stay copy-on-write.
  - `Unbind` must not set them to null; give them an empty array (category 1).
- **The handle:** `Object.Native` reads `m_CachedPtr` with `Volatile` (`Core/Object.cs:101`). A
  worker that read a live record before a destruction keeps reading consistent data today. A
  worker holding the facade does the same when merged.
- **Bounds and cull caches:**
  - `FrameRenderer.BoundsInputsChanged` is called from the main thread, from the component-array
    setter (`NativeObjects.cs:57`) and `TransformNative.WorldChanged`.
  - The cull caches key renderers by record (`HashSet<RendererNative>`, category 2).
  - Both are main-thread only. Mechanical, plus the comparer rule.
- **The draw recorder's helpers and the GL, Vulkan and presenter threads** read only asset
  records (textures, shaders, meshes: `OniMoono.Desktop/Gl/GlDrawRecorder.cs:267`, :298, :682).
  These are not merged types, so nothing changes.
- **Counts:**

  | Item | Count | Class |
  |---|---|---|
  | `Volatile`/`Interlocked` sites in the core records and facades | 29 | mechanical (move with the fields) |
  | Copy-on-write arrays | 2 | mechanical, plus the `Unbind` rule |
  | Device-thread readers of merged types | 0 | none |

- **Risk:** low. `TransformConcurrentReadTests` and `ParallelDrawRecordingTests` cover the
  readers.

### 5. Reflection visibility

- **What the facades declare today:**
  - Unity's own facades (763325's CoreModule):
    - `Object`: `private IntPtr m_CachedPtr` only. Unity 6 has no `m_InstanceID`.
    - `MonoBehaviour`: `m_CancellationTokenSource`.
    - `Component`, `Behaviour`, `GameObject`, `Transform`, `RectTransform`: no instance fields.
  - Ours:
    - `Object` adds `m_Native`;
    - `Component` adds `m_GameObject` and `m_Transform`;
    - all private.
  - ARCHITECTURE.md §3.2 still says the facades declare no other fields and still names
    `m_InstanceID`. It is out of date on both and should say what is there.
- **The one hazard is the game's `Localization.SetFont`:**
  - Assembly-CSharp, 763325, already noted in [runtime.md](runtime.md).
  - It walks a type's fields with `Instance | Public | NonPublic | FlattenHierarchy`, without a
    visited set. A field typed `GameObject` makes it walk every component of that object by its
    actual type. Any other field whose type differs from its declaring class sends it into the
    field's declared type. A null `GameObject` field throws.
  - Private fields of a base class are not returned for a derived type; inherited internal and
    protected ones are. Every transform and engine component is walked as its own type, so the
    fields those classes declare are seen.
  - The rules the merge must keep:
    1. Merged fields on `Object`, `Component`, `Behaviour` and `MonoBehaviour` are `private`,
       never `internal`. The engine reaches them through internal inlined accessors; the walk
       ignores properties.
    2. No `GameObject`-typed field declared on `Transform`, `RectTransform` or an engine
       component. Keep the one in `Component.m_GameObject`.
    3. The fields a concrete facade declares form no cycle of declared types with non-null
       values. A field of the class's own type, such as `Transform parent`, is skipped by the
       walk. Arrays, structs and primitives are harmless.
  - `tests/e2e/translation-screen.yaml` runs this path (the restart dialog in German, then
    Cancel) and passes with today's fields.
- **Other game reflection** doesn't reach component fields: `ResourceSet.ResolveReferences`,
  `StateMachine`, `LocString`, and the debug walkers `MemorySnapshot` and `GarbageProfiler`,
  which would only count the fields. `DevToolSceneInspector` reads public fields. No game code
  looks up a facade field by name.
- **Mods** (a string scan of 2,898 Workshop folders, a sample decompiled):
  - Field enumeration is rare and declared-only: Rephysicalized on `CodexEntry`;
    NaturalSeedPlanting and TrueSmartMopping on tools; PLib's `PUIUtils.AddComponentText` on the
    component's own type; a shared library's debug dump.
  - Name lookups (`AccessTools.Field`, `Traverse`, Harmony's `___name` arguments) walk base types
    and find private base fields by name, but only a mod asking for that exact name would get
    one. Keep the `m_` prefixes and avoid Unity's member names (`name`, `enabled`, `transform`).
  - New base fields shift game components' field offsets. Only D55 (Harmony's unchecked field
    read, `MonoInstanceFieldInjection.cs:9`) depends on offsets, and it type-checks.
- **Our host and tweaks** enumerate no facade fields. `MonoReflectionOrder` reorders `GetFields`
  results, in which private base fields do not appear.
- **Counts:**

  | Item | Count | Class |
  |---|---|---|
  | `Localization.SetFont` walk | 1 game method, 1 e2e spec | design (rules 1–3) |
  | Game debug walkers | 2 | none |
  | Mod field enumerators found | about 6 patterns | none |
  | Name lookups | many, all on game types | naming rule |

- **Risk:** low for the base classes. Medium for state merged into `Transform`, `RectTransform`
  and engine components, which the walk sees.

### 6. Facades and records that are not one to one

- **The hierarchies match.** Each facade class has one record class, in the same shape:
  - `Collider2D : Behaviour` with `Collider2DNative : BehaviourNative`;
  - `Renderer : Component` with `RendererNative : ComponentNative`;
  - `MeshRenderer`, `SpriteRenderer`, `LineRenderer` and `ParticleSystemRenderer` with their
    `RendererNative` subclasses;
  - and so on through the record classes of `ComponentNatives` (`BehaviourManager.cs:12`).
- **Every game and mod script shares one record class,** `BehaviourNative`. Merged, that state
  lives in `Behaviour` and `MonoBehaviour`, which is the natural place.
- **Swapped records:**
  - Only `ConvertTransform` (`Lifecycle.cs:159`) swaps records. A `Transform` becomes a
    `RectTransform` with a new record and a new facade; the old facade is unbound (:180) and the
    components forget their cached transform (:179).
  - `TextScaleWatch.Moved(oldNative, native)` moves the text scale check's slot to the new
    record.
  - Merged, the same happens to facades: `Moved(oldTransform, newTransform)`. Mechanical.
  - Nothing else rebinds a record to another facade.
- **Engine types that hold records directly** retype to facade types:
  - the phase lists (`List<BehaviourNative>`, `BehaviourManager.cs:91`–105);
  - the start queue (:203);
  - parent links and child arrays (`TransformNative[]`, `NativeObjects.cs:159`–178);
  - scene roots, `Finder`, the canvas system's caches, the cull lists, GUI participants;
  - the reverse step `(MonoBehaviour)b.Managed` (`BehaviourManager.cs:185`, :189, :278).

  This is the bulk of the 824 type references. Mechanical, given the rules of category 2.
- **Engine-component records** (`CameraNative`, `CanvasNative`, `CanvasRendererNative`,
  renderers, colliders, animators, particles, video, directors, text meshes: 30 classes, about
  300 fields and most of the 4,800 lines):
  - They hold 53,000 of the 2.72 M records, 2%.
  - Merging them means moving their code into the facade partials for little heap.
  - Keeping them as separate state objects (without identity: no slot, id or `Managed`) behind
    one facade field is the design form that keeps the merge to the core five.
- **Counts:**

  | Item | Count | Class |
  |---|---|---|
  | Core records (NativeObject, GameObjectNative, ComponentNative, BehaviourNative, TransformNative, RectTransformNative) | 6 classes, about 83 fields | mechanical (move into partials) |
  | Engine-component records | 30 classes | design (keep as state objects) or mechanical (move, large) |
  | Record swap (`ConvertTransform`) | 1 | mechanical |
  | Fields, lists and arrays holding records | most of 824 references in 65 files | mechanical |

- **Risk:** low.

### 7. Anything else

- **Two hot casts become none.** `(TransformNative)NativeOrThrow` and the other typed accessors
  are `castclass` on non-sealed types. Merged, `this` is already typed. This is where part of the
  per-call saving comes from.
- **The packages:**
  - uGUI and TextMesh Pro reach the records in 2 files (`LayoutRebuilder.KnownBelow`/`KnownOn`,
    `TextMeshProUGUI` with `TextScaleWatch.Watch`).
  - The host does in 2 (`TransformMoves`, `BindCheck` for assets).
  - Mechanical.
- **Harmony patches on our facade members** keep their targets, since the signatures do not
  change; transpilers on our IL are not supported anyway.
- **Diagnostics:** `ObjectCensus` (ConditionalWeakTable by record), `UiDump`, `--dump-hierarchy`
  and the heap study's tool count records by type and need their type lists updated.
  Mechanical.
- **Facade size:** every game behaviour's object grows by about 90 bytes while its record goes
  away. This is net 32–40 bytes fewer, but the game's own fields move further apart from each
  object's header. That costs nothing measurable (part 1's merged shape includes it).
- **Exactness:** no change the game can observe was found. What the game sees is the same API,
  the same values and the same order of callbacks. It also sees the same private fields to
  reflection, plus the new private ones, under rules 1–3 of category 5.

## Size and tests

**Size.**

- **Full merge (the core records and the 30 engine-component records):** about 90–100 files and
  5,000–6,000 lines changed.
- **Core-only merge (engine-component records kept as state objects):** about 70–80 files and
  3,500–4,500 lines. Most of it is mechanical, but every lifecycle path moves.
- **Of the core-only merge:**
  - 824 type references in 65 files;
  - 282 `Native` reads (97 files read `Native` or `NativeOrThrow`);
  - 51 `Managed` reads;
  - 105 equality sites;
  - 39 collections;
  - 14 construction sites;
  - the clone hand-over;
  - the `ObjectTable` shape.

**What covers each category.**

| Category | Existing coverage | New tests needed |
|---|---|---|
| 1 construction, lifetime | `LifecycleTests`, `FieldInitializerBindingTests`, `NoParameterlessConstructorTests`, `MonoUninitializedObjectsTests`, `ScriptableObjectNewTests`, `ObjectLoaderTests`, `CanvasGroupBindTests`; dogfood load, save and reload | every merged type's initial state, against today's record defaults; a destroyed facade held by a test does not keep its game object's components or children alive (weak references after a collection) |
| 2 fake null, table, unload | `LifecycleTests` (destroy order, flags), `PendingStartDestroyTests`, `AssetUnloadTests`, `UnloadAssetTests`, `SceneRootsTests`, `FindObjectsOrderTests` | the analyzer and its own test; two destroyed behaviours in the phase lists' sets at once (a forced hash collision) |
| 3 clone, serialization | `InstantiateFieldTests`, `InstantiateReentryTests`, `InstantiateTypedCopyTests`, `JsonUtilityTests`, `TypeTreeReadTests`; `qa.sh verify` and the CI verify runs | a sample component's KSerialization OptOut template, the same before and after |
| 4 threads | `TransformConcurrentReadTests`, `TransformCacheTests`, `ParallelDrawRecordingTests`, `RendererCullTests`, `ComponentQueryTests` (and `ONI_VERIFY_COMPONENT_LAYOUTS`, `ONI_VERIFY_CULL_CACHE`) | none |
| 5 reflection | `tests/e2e/translation-screen.yaml`; the API conformance check | a walk of every facade type the way `SetFont` walks, asserting rules 1–3 |
| 6 hierarchy, swaps | `RectTransformTests`, `TextScaleWatchTests`, `ChildListTests` | none |
| all | `scripts/check.sh`; `scripts/qa.sh dogfood verify`; the release e2e list; an `ONI_OBJECT_CENSUS` and a heap count on the late-game colony, before and after | |

## Verdict

- **Frame time.** The merge would not help normal frames measurably.
  - The estimate is 0.03–0.2 ms of a 9.2 ms frame (0.3–2%), most likely under 0.1 ms. That is
    below what a frame-rate run can resolve, and not a reason for the change on its own.
  - The members the game calls most, the fake-null checks and the cached `gameObject` and
    `transform`, already read only the facade.
- **Safety.** The merge is safe to do, and can be exact.
  - Nothing the game or the sampled mods can observe needs to change: serializers, saves and
    clones skip private base fields, and the field-walking game method is satisfied by three
    placement rules.
  - The real risks are the engine's own: equality on destroyed objects, construction order and
    the clone hand-over.
  - All of them can be made build errors or caught by the existing lifecycle tests, with the
    guard rails below.
  - The change is large and touches every lifecycle path. It is a rewrite of the object model,
    not a tweak.

## Recommendation

**Not worth doing now.** Its one gain is the full collection's pause and about 100 MB, a
smaller share than candidates 1 and 2 of the heap study for more risk. If the pause is taken on:

1. **Guard rails first.** They are small, useful on their own, and make the rest mechanical:
   - an analyzer against `==`, `!=` and default-comparer collections on UnityEngine.Object types
     in the engine's code;
   - reference comparers in the 11 collections that would need them;
   - the `SetFont` walk test and the KSerialization template test;
   - ARCHITECTURE §3.2 brought up to date.
2. **`BehaviourNative` into `Component` and `Behaviour`**, with `NativeObject`'s identity into
   `Object` for the merged types. This is 86% of the records (2.33 M). The engine components
   keep a state object behind one field.
3. **`GameObjectNative` into `GameObject`**, folding in `ComponentLayout.Cached`: 0.31 M
   objects.
4. **`TransformNative` and `RectTransformNative` into `Transform`**: 0.17 M objects, the
   largest records (376 and 472 bytes) and the most code. Rules 2 and 3 of category 5 apply.
5. **Leave the engine-component records** (2%) as they are.

Measure each step with an object census and a heap count on the late-game colony, and the
in-game full-collection probe of the heap study, before going on.

## Progress

### Step 1: the guard rails (landed)

What landed, on master and u60, before any record moves:

- **The analyzer** (`src/OniMoono.Analyzers`, `IdentityAnalyzer`), run by every project that sees
  the records (the engine, the desktop platform, the host, uGUI, TextMeshPro, Timeline and the
  engine bridge). Its errors:
  - MOONO001: `==`/`!=` on the records of game objects and components (`GameObjectNative`,
    `ComponentNative`, their subclasses, and `NativeObject` itself), anywhere. Asset records
    (textures, meshes, shaders…) stay records and are not checked.
  - MOONO002: Unity's `==`/`!=` on facades in the engine's own code (the OniMoono namespaces of
    the engine and the desktop platform, and the facades' non-public members). The host is left
    out: its UI and game tweaks are game code.
  - MOONO003: `Equals` on either. MOONO004: hashed collections, `EqualityComparer<T>.Default`
    and LINQ's set operators keyed by either (tuples and C# records holding them included) with
    the default comparer. MOONO005: `List`/`Array`/LINQ searches by `Equals`.
- **The sites it found**, all rewritten in the same commit: 128 record comparisons (the study's
  probe found 105 on `GameObjectNative` and `ComponentNative`; `NativeObject` and the subclasses
  add the rest), 176 facade comparisons, 20 default-comparer collections and 22 searches in the
  engine, and 8 facade comparisons in the desktop platform (10 on u60, with the Vulkan
  device's two).
  - Record comparisons became `is null`, `is not null` and `ReferenceEquals`: the same IL, as
    the records have no operators.
  - Facade comparisons with null became the bool conversion (`x == null` is `!x`, `x != null` is
    `x`): the same call, `IsAlive`. The five that compared two facades became `ReferenceEquals`
    where one side is known alive (the camera being rendered, a bundle being unloaded, an object
    just bound), which is the same answer; `AnimationNative.RemoveClip` keeps Unity's equality
    (it is the facade's own API) and says so.
  - The 11 hashed collections of the study and the others take
    `ReferenceEqualityComparer.Instance`; the lists search with `ByReference`. For records this
    is the same comparison as before. For facades (the enabled cameras, sorting groups,
    animation notifications, merged canvas runs, bundles) every entry is alive when it is added,
    searched or removed (the lists change in `EngineOnEnable`/`EngineOnDisable` and before
    `Unbind`), where Unity's equality is reference equality too.
  - `SpriteRendererNative.GeometryMesh` keeps its tuple's `Equals`: its sprite is alive there.
- **Tests:**
  - `IdentityAnalyzerTests`: each rule on samples, the scopes, and that every project above runs
    the analyzer.
  - `SetFontWalkTests`: the base classes' fields are private; no facade but `Component` declares
    a `GameObject` field; `Localization.SetFont`'s walk run on every component type, a
    `GameObject`, transforms and a behaviour seen as each base type. It finds three things today,
    recorded as known: a component walked as `Component` meets `m_GameObject` null (the
    `gameObject` cache is filled on first read) and would throw, or, filled, go round its
    object's components; and an object walked as `Object` meets `m_Native`, whose `Managed` is
    the object again. Neither is reachable from the one object the game walks (its confirm
    dialog); both come from fields Unity's facades lack. The second goes away with the merge.
  - `SaveTemplateTests`: what KSerialization's OptOut and OptIn templates take from `Object`,
    `Component`, `Behaviour` and `MonoBehaviour` equals what they take from Unity's (the game's
    CoreModule, read with MetadataLoadContext).
  - `DestroyedFacadeRetentionTests`: a destroyed behaviour, game object, transform or child
    transform the game keeps holds none of its hierarchy's other facades or any record after a
    collection (a mutation that keeps `m_Native` fails all six cases). The physics world holds
    a destroyed collider, and so its whole hierarchy, until its next query, which the game makes
    every frame; the test makes one.
  - `IdentityCollisionTests`: two behaviours whose keys' hashes collide, destroyed together,
    added and removed in one frame, and one destroyed while the other is removed and added
    again; and every hashed collection of the study compares by reference.
- **ARCHITECTURE.md §3.2** says what the facades declare (`m_CachedPtr` is a table handle, not a
  `GCHandle`; no `m_InstanceID`; `m_Native`, `m_GameObject`, `m_Transform`), the three
  reflection rules and the identity rules.

### Step 2: components keep their own state (landed)

The owner chose the form for the engine components: their records stay, as the state of their
subsystem, without an identity of their own. Three commits:

1. **Every object keeps its identity.** The instance id, hide flags, source file and
   destruction flags moved from `NativeObject` to `UnityEngine.Object` (private fields). The
   records read them there through properties of the same names, so their readers did not
   change. A record without an object (the engine's own meshes, or one not bound yet) keeps
   them itself and hands them over when bound, before the object's constructor body runs, as
   the record's own fields were set before it. `ObjectTable` holds the objects, not their
   records.
2. **Every component keeps its state.** `Component` holds its GameObject's record and its
   lifecycle flags; `Behaviour` its enabled state, Awake/Start/OnEnable flags, script,
   execution order and coroutines. Scripts and plain components have no record:
   `BehaviourNative` and `ComponentNative` are now the state of engine components only, and
   read the component's state through properties.
   - **Construction.** `Object.ConstructComponent` hands a component its starting state
     (`ComponentInit`: GameObject record, hide flags, enabled, useGUILayout, script) through the
     same thread-static pending binding the records used, so it is in place before the
     constructor body runs, as before. The loaders, which learn a component's GameObject only
     when they fill it, set it then.
   - **Destruction.** Liveness is still `m_CachedPtr` alone. A destroyed component drops its
     GameObject's record (`Component.Unbind`), so one the game keeps holds nothing of its
     hierarchy (`DestroyedFacadeRetentionTests`; a mutation that keeps it fails 4 of 6 cases).
   - **Instantiate** copies enabled (disabled while the source awaits destruction),
     useGUILayout and hide flags into the clone's starting state, as the records' copy did.
   - **Compile-time guard.** `Component` hides `Object.Native` and `NativeOrThrow` with
     `[Obsolete(error)]` members, so no engine code reads a component's record for its liveness
     or state: each such read was rewritten to `Alive`, `EngineState` or the component's own
     fields.
   - **Reflection.** The new fields are private and declared after `Component.m_GameObject`, so
     `Localization.SetFont`'s walk of a component seen as `Component` still meets that field
     first. A script walked as `UnityEngine.Object` no longer goes round through its record (it
     has none): one of the walk test's known cycles is gone for scripts; objects with records
     keep it.
3. **The slot comes from the handle.** `m_CachedPtr` already packs the slot and generation, so
   the object keeps no copy; the source file is boxed (few objects have one). Each object is 16
   bytes smaller than in commit 1.

**Verified** on master and u60 (U60 763612 after the u60 rebase):
- `check.sh` and `ci-unit-tests.sh` green on both; the analyzer clean; new unit tests
  (`MergedComponentStateTests`: starting state, a constructor's view of it, a component made
  without a constructor, engine components' state, inactive objects, destroy and fake null,
  Destroy's deferred disable, Instantiate's copy, the Transform→RectTransform conversion, the
  table holding components without records).
- The core e2e list on both players, in 15-minute jobs on Linux lanes. Failures, each checked
  against the code before the step: `moono-tweaks-en`, `-search-en` and `-search-zh` fail the
  same way on the base (their counts predate the latest tweaks); `issue-52-states-bic` failed
  once in five runs (an ignored click) and passed twice of two on the base; the rest were
  missing local test mods and Workshop copies, which passed once provided.
- Mods: on master, Ronivan's Legacy, Flow Splitters, Rotate Everything and Fast Track
  (`issue-52-fast-track`), both players, besides the core list's mod specs. On u60 the mod gate
  skipped them: onimaxxing had not reviewed the uploads for 763612 yet.
- `qa.sh verify` on the late-game colony, every tweak and engine verify mode: 0 wrong in each
  (component layouts 270 M queries, cull cache 1.1 M, parallel draws 8.5 M, canvas products,
  skips, raycast cache, text scales and the rest), 1,640 game seconds, no new log signature.
- Saves: the late-game colony loaded and saved at once differs from the base's save only in
  `GameClock`'s float of real time (2 bytes of a 710 MB body), as two saves of the same build
  do.

**Heap** (master, the late-game colony 90 s after the load, a heap dump through HeapInv):

| | Base | Step 2 | |
|---|---:|---:|---:|
| Reachable objects | 25.29 M | 23.10 M | −2.19 M (−8.7%) |
| References | 77.7 M | 71.3 M | −6.45 M (−8.3%) |
| Bytes | 5.13 GB | 5.00 GB | −126 MB |
| `BehaviourNative` | 2.29 M, 275 MB | 0 | |

On u60 (U60 763612) the same colony: 24.30 M → 22.08 M reachable objects (−2.22 M), 81.9 M →
75.3 M references, 4.52 → 4.40 GB.

The facades grew by the state they took (a game script by about 64 bytes, a GameObject by 24).
Net, each removed record saved about 55 bytes, more than the study's estimate of 32–40: the
records' fields were padded, and their copies of the GameObject link went with them.

**Hot paths** (a micro-benchmark of the engine itself, base against step 2, 150,000 game objects
with 14 behaviours, six alternating processes in runq's perf slot on a quiet machine; ns a call,
medians):

| Member | Hot base | Hot new | Cold base | Cold new |
|---|---:|---:|---:|---:|
| fake-null check (control) | 1.25 | 1.24 | 15.6 | 15.1 |
| `enabled` | 1.81 | 1.31 | 31.5 | 18.2 |
| `isActiveAndEnabled` | 1.85 | 1.25 | 36.1 | 16.4 |
| `GetComponent<T>` | 4.14 | 3.22 | 125.4 | 100.2 |
| `name` | 2.43 | 1.23 | 53.0 | 24.9 |
| `gameObject` | 1.33 | 1.33 | 19.2 | 19.4 |
| `activeInHierarchy` | 1.77 | 1.70 | 38.9 | 41.0 |
| `localPosition` | 2.02 | 1.93 | 48.1 | 49.9 |
| `GetInstanceID` | 1.31 | 1.23 | 23.7 | 16.0 |
| Update dispatch (a behaviour) | | | 8.45 | 7.27 |
| Instantiate (14 behaviours), ns | | | 5,189 | 4,867 |
| Instantiate, bytes | | | 3,232 | 2,480 |

No hot member is slower. Cold, the members that read a GameObject or transform through the
component (`gameObject`, `activeInHierarchy`, `localPosition`) are 1–5% slower: the facade's
own fields now sit further from its handle, so the two more often fall in different cache
lines. The members that read the merged state save a cache miss (14–30 ns cold).

### Step 3: GameObjects keep their own state (landed)

- **What moved.** `GameObjectNative`'s fields (component array, layer, tag, active flags, scene,
  transform record, root slot, the canvas system's answers, name, serialized data) are private
  fields of `GameObject`, read through internal properties of the record's names, so the engine
  reads a GameObject as it read the record. `GameObjectNative` is gone; GameObjects have no
  record. `GameObject` hides `Object.Native` and `NativeOrThrow` at compile time, as `Component`
  does.
- **Construction.** Every constructor sets the starting state (tag Untagged, active, root slot
  −1, canvas answers −1) before the object is bound, as the record's field initializers did;
  the loaders and Instantiate set the rest after, as nothing runs in between.
- **Destruction.** A destroyed GameObject drops its components, transform record and canvas
  caches, so one the game keeps holds nothing of its hierarchy (a mutation that keeps them fails
  4 of 6 retention cases). A component an OnDestroy adds to an object of the destroyed subtree is
  now destroyed with it: before, it stayed alive on the dead object (also before the merge),
  and with the object's transform gone a collider left so would have made the physics world's
  queries throw. The original's handling of this was not measured.
- **Reflection.** A component's link to its GameObject is stored untyped, so `m_GameObject`
  stays the only `GameObject`-typed field `Localization.SetFont` follows. A GameObject walked as
  `UnityEngine.Object` no longer goes round through a record.
- **Not folded:** `ComponentLayout.Cached` stays a separate object. Worker threads read a
  GameObject's component array and its layout while the main thread replaces both; only one
  reference publishes the pair together. Folding it needs a seqlock on the hottest lookup or an
  eager layout computed for every component-array change; neither was worth its cost or risk.
- **A fix to step 2,** from an independent review: Instantiate had lost the hide flags of engine
  components (cameras, renderers, colliders, transforms…) since step 2, as binding handed the
  record's own flags over the clone's. The record's leftovers now go first and the starting state
  over them; a test instantiates every engine component type, active or not, enabled or not.
- **Tests:** `MergedGameObjectStateTests` (starting state, constructors, destruction, Instantiate,
  the untyped link) and `MergedStateLifetimeTests` (Instantiate of every engine component type;
  a component added by an OnDestroy; a prefab loaded from the game's files: ids, persistence,
  source, links, scripts; and unloaded with its scripts and record-less GameObject).

**Verified** on master and u60 as for step 2: `check.sh` and `ci-unit-tests.sh` green, the
analyzer clean, the core e2e list (the same pre-existing `moono-tweaks-search` failures as the
base; `issue-52-states-bic`'s one failure was the original's, passing 2 of 2 on a re-run), the
master mod specs (Fast Track's local build was refused by the gate this time: not run), `qa.sh
verify` (0 wrong in every mode, no log signatures), and saves equal to the base's but for
`GameClock`'s real-time float.

**Heap** (the late-game colony, HeapInv): master 23.10 M → 22.93 M reachable objects (−163 k,
the GameObject records), 71.27 M → 70.94 M references, 5.002 → 4.995 GB; u60 22.08 M → 21.92 M,
75.34 M → 75.00 M, 4.401 → 4.390 GB. The GameObject facades grew by 136 bytes each (9.3 → 31.9
MB) while their records' 33.1 MB went.

**Hot and cold paths** (the engine micro-benchmark, step 2 against step 3, ns a call, medians
of six alternating rounds):

| Member | Hot step 2 | Hot step 3 | Cold step 2 | Cold step 3 |
|---|---:|---:|---:|---:|
| `go.activeSelf` | 1.28 | 1.19 | 13.1 | 10.2 |
| `go.activeInHierarchy` | 1.29 | 1.21 | 12.6 | 9.0 |
| `go.layer` | 1.29 | 1.19 | 12.0 | 9.4 |
| `go.transform` | 1.82 | 1.56 | 32.1 | 22.6 |
| `go.GetComponent<T>` | 3.20 | 2.92 | 76.5 | 62.9 |
| `go.tag` | 1.38 | 1.18 | 14.3 | 11.0 |
| `go.name` | 1.25 | 1.18 | 10.0 | 11.1 |
| GameObject fake-null check | 1.18 | 1.19 | 8.3 | 8.4 |
| `component.gameObject.activeInHierarchy` | 1.69 | 1.40 | 38.3 | 21.7 |
| `component.gameObject` | 1.32 | 1.33 | 19.7 | 14.2 |

No hot member is slower; cold, the GameObject members save the record's cache miss. In that
series Instantiate measured 26% slower after the other members' loops, but alone, on the same
150,000-object world, it was 10% faster than step 2 (2,824 against 3,148 ns) and on a small
world 30% (3,177 against 4,536): the slower figure follows the benchmark's earlier loops, not
Instantiate. It allocates 64 bytes less.

### Step 4: transforms keep their own state (landed)

- **What moved.** `TransformNative`'s and `RectTransformNative`'s fields (local placement, the
  world and inverse caches, the parent and the copy-on-write child array, the watch flags and
  text-scale slot, the anchoring, the rect epoch cache, the driving object, serialized data)
  are private fields of `Transform` and `RectTransform`, read through internal members of the
  record's names. Both records are gone; transforms have no record.
- **Construction.** A transform sets its starting state (identity rotation, unit scale,
  `hasChanged`, dirty caches; a RectTransform's centred anchors, 100 × 100 size and
  `sendChildDimensionsChange`) when it is bound, before its constructor body, so one the
  loaders make without a constructor starts the same. Instantiate copies the placement and the
  anchoring onto the clone after binding it.
- **Transform to RectTransform.** Adding a RectTransform builds the new transform in the old
  one's place: the same sibling index, the children re-parented, the local placement and the
  text-scale slot moved, the old one destroyed.
- **Destruction.** A destroyed transform drops its parent and its children, so one the game
  keeps holds nothing of the hierarchy. Its local placement stays.
- **Reflection.** The object driving a RectTransform is stored untyped. The parent is a
  `Transform` and the children a `Transform[]`, which `Localization.SetFont`'s walk does not
  follow, and the walk's cycle through a transform's record (`Object.m_Native` → `Managed`) is
  gone.
- **Found, not changed:** a child list read on a worker *while* the main thread removes a child
  can show a null slot: `RemoveChildAt` publishes the new array before the new count. The code
  is as it was before the merge, and the workers read the hierarchy only while nothing changes
  it; a list read before a change keeps its children, as documented.
- **Tests:** `MergedTransformStateTests`: the starting state (made and loaded from the game's
  files), the conversion, SetParent and sibling order with world and local values,
  invalidation below a changed ancestor and none beside it, the rect epoch, Instantiate, a
  destroyed transform, and four worker threads reading the world values of 400 transforms at
  once after each change, and a child list read before 2,000 changes. Five mutations (no
  starting state, no placement copy, the parent kept at destruction, no
  `sendChildDimensionsChange` default, the parent's list left at conversion) each fail them.

**Verified** on master and u60 as for steps 2 and 3: `check.sh` and `ci-unit-tests.sh` green,
the analyzer clean, the core e2e list (the same pre-existing `moono-tweaks-search` failures as
the base, and on u60 `issue-52-fast-track-no-tiering`, which fails the same way on the base:
the local Fast Track build does not load), the master mod specs (0 failures), `qa.sh verify`
(0 wrong in every mode: parallel draws, text scales, raycast cache, pass states and the rest;
no log signatures), and saves equal to the base's byte for byte on master, and on u60 but for
`GameClock`'s real-time float.

**Heap** (the late-game colony, HeapInv): master 22.93 M → 22.77 M reachable objects (−165 k,
the transform records), 70.95 M → 70.28 M references, 4.992 → 4.980 GB. The transforms'
facades and records were 80.4 MB together and are 68.7 MB (a Transform 376 bytes, a
RectTransform 472). On u60 21.92 M → 21.75 M objects, 75.00 M → 74.33 M references, 4.390 → 4.380 GB.

**Hot and cold paths** (the engine micro-benchmark, step 3 against step 4, ns a call, medians
of six alternating rounds on a quiet machine; the world is 150,000 objects in a hierarchy of
about nine levels, cold reads in random order, hot ones on 48 leaves):

| Member | Hot step 3 | Hot step 4 | Cold step 3 | Cold step 4 |
|---|---:|---:|---:|---:|
| `position` | 1.81 | 1.48 | 26.0 | 20.1 |
| `localPosition` | 1.60 | 1.17 | 15.5 | 9.8 |
| `rotation` | 1.76 | 1.42 | 19.1 | 12.4 |
| `lossyScale` | 1.70 | 1.36 | 18.9 | 12.3 |
| `localToWorldMatrix` | 1.75 | 1.36 | 21.0 | 12.2 |
| `worldToLocalMatrix` | 2.28 | 1.54 | 33.0 | 21.3 |
| `parent` | 2.25 | 1.19 | 34.8 | 8.4 |
| `GetChild(0)` | 2.83 | 1.26 | 24.3 | 6.5 |
| `childCount` | 1.62 | 1.17 | 20.0 | 11.6 |
| `localPosition` set | 10.75 | 10.68 | 206 | 181 |
| `SetParent` out and back | 511 | 498 | 8,081 | 7,965 |
| `component.transform.localPosition` | 1.98 | 1.58 | 43.3 | 29.0 |

Every transform member is as fast or faster, hot and cold; the other members are unchanged.

**Open: Instantiate of a 14-script object, measured alone, is about 12–20% slower** on the
150,000-object world (3,012 against 3,362 ns over six alternating rounds; 2.9 k against 3.6 k
in a second series of four), while it allocates 72 bytes less a clone. By shape, a bare
GameObject is level or faster (about 800 against 750 ns) and one with eight child objects
faster (5.7–8.9 k against 4.6–5.1 k); only the behaviours' object is slower, about 40 ns a
behaviour. Ruled out: collections (the same counts and pause time, 1.1 s either side) and added
work (the step adds one type check a component in the clone loop and a branch behaviours never
reach). On a 2,000-object world the pairs are mixed (3,575 and 3,790 against 3,242 and 4,092).
It looks like a memory-layout effect of the large world rather than the code; no profiler was
available to settle it. Landed as is.

## Not measured

- The merge built in the game.
- A sampling profile of the instrumented build (the counts' slowdown would distort it).
- The split of record-reaching calls between frame-tied and game-time work, which is estimated
  from sampled sites rather than measured at two speeds.
- Windows.
- The micro-benchmark's per-call figures are from a loaded Linux machine. They are relative
  figures; the absolute cold ones vary with the load.
