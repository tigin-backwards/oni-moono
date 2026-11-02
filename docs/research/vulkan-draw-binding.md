# Vulkan draw recording: where the device thread's time goes (2026-11-01)

What the Vulkan device thread spends recording a colony's draws, and which changes would cut
it, ranked by measurement. Starting point: the four commits that added the descriptor cache and
host staging (`72d41c87`..`b9a3eb1a` on `vk-cpu-land`), after which the device thread spent
about 2.1 ms a frame in draws against OpenGL's 0.92 (RTX 3090, U57 save).

Every number below is one of: measured here (Windows, RTX 3090, driver 610.88, U57 save,
1280x720), read from our code, read from the Vulkan specification (Vulkan-Docs `main`,
`chapters/*.adoc` and `xml/vk.xml`, fetched 2026-11-01), or taken from a cited page that was
read. Estimates say how they were derived. Claims not checked say "unverified".

**The short answer.** The 39-descriptor bind is not where the time goes. A draw costs about
1.2-1.4 µs on the device thread, and two thirds of that is our own C# bookkeeping (preparing
textures, building descriptor writes, the cache lookup, the pipeline lookup). The Vulkan calls
themselves add up to about 0.55-0.68 ms a frame. Splitting set 0 so its changing part is pushed
was measured **slower** on NVIDIA. Building descriptor writes only on a cache miss was measured
8-10% faster. The way to reach OpenGL's level is to cut our own per-draw work first, then
consider a bindless path for the large programs.

None of this raises fps today. The device thread was busy 2.3-3.8 ms a frame in these runs. The
main thread, at 25-50 ms a frame, bounds fps. These changes shorten the device thread's frame,
which matters on slower CPUs and once the main thread is faster.

## Method

- **Build**: research branch `vk-draw-binding-proto`, not to land (`1e977421`, `f08ce257`).
  It adds `VkDrawProfile` with these switches:
  - `ONI_VK_PROFILE=time`: a Stopwatch stamp between the sections of `RunDraw`.
  - `ONI_VK_PROFILE=analyse`: what each cached set 0 names, draw after draw.
  - `ONI_VK_DUP=...`: issues chosen Vulkan calls twice. The calls are identical, so the
    rendering does not change.
  - `ONI_VK_PROTO=splitnames|splitimages|idsfirst`: the prototypes.
  - `ONI_VK_ALT=1`: turns the variant on in every other 60-frame `--frame-stats` window.
- **Runs**: the U57 save with PerfProbe at `--game-tweak exact`, 1280x720 windowed on monitor
  2, unfocused, High priority, audio off, 2,400-3,000 frames. Windows from frame 605 on.
- **Why in-run alternation**: other agents' Linux players shared the machine. Two plain base
  runs a few minutes apart measured 1.81 and 2.38 ms of draws, and fps varied 20-55 between
  windows. Comparisons between runs were therefore unreadable. With `ONI_VK_ALT=1`, each variant
  window is compared with the mean of the base windows on either side. The tables give the
  median of those paired differences and their interquartile range, over 19 pairs per run.
- **Timer cost**: one `Stopwatch.GetTimestamp` took 17.6 ns, and a draw takes about 14 of them.
  So about 0.25 µs of each timed draw is the timers (an estimate), spread over the sections. The
  timed totals (1.21-1.51 µs per draw) are upper bounds. Timed runs are used for proportions;
  untimed paired runs are used for savings.
- The logs of the first sequence (runs `an1`, `b1`, `b2`, `t1`, `t2` and the cross-run
  duplication runs) were lost when the session restarted. Their numbers below were recorded
  when the runs finished. The paired runs (`as*`, `ad*`, `ai*`) are in this worktree's
  `out/vdb/win`.

## 1. What a draw costs

Per-section host time in `RunDraw`, from three timed runs (`t1`, `t2`, and the base windows of
`ait`). About 1,500 draws a frame. Of these, about 695 bind a cached 39-binding set 0 and about
805 push their set 0. Each cell is ms per frame (µs per call).

| Section | What it is | t1 | t2 | ait base |
|---|---|---|---|---|
| Prep | `PrepareTextures`: two loops over the draw's textures, `IsAttached` each | 0.349 (0.23) | 0.376 (0.25) | 0.285 (0.20) |
| Begin | `if (!rendering) BeginRendering()` (45 passes a frame) | 0.114 (0.08) | 0.127 (0.08) | 0.099 (0.07) |
| PipeGet | pipeline key hash and lookup | 0.182 (0.12) | 0.204 (0.14) | 0.160 (0.11) |
| PipeBind | `vkCmdBindPipeline`, about 276 a frame | 0.115 (0.42) | 0.129 (0.47) | 0.102 (0.36) |
| TexFill | building set 0's writes and the cache's identities | 0.416 (0.27) | 0.451 (0.30) | 0.388 (0.26) |
| Push | `vkCmdPushDescriptorSetKHR`, about 806 a frame | 0.085 (0.11) | 0.095 (0.12) | 0.071 (0.09) |
| Cache | `VkDescriptorCache.TryGet` (hash of 78 longs), about 695 a frame | 0.175 (0.25) | 0.191 (0.27) | 0.150 (0.22) |
| UniFill | constants copied to the uniform stream | 0.121 (0.08) | 0.131 (0.09) | 0.103 (0.07) |
| Bind01 | `vkCmdBindDescriptorSets`, cached set 0 with set 1 and dynamic offsets | 0.142 (0.21) | 0.151 (0.22) | 0.131 (0.19) |
| Bind1 | `vkCmdBindDescriptorSets`, set 1 only (pushed programs) | 0.040 (0.05) | 0.044 (0.06) | 0.037 (0.05) |
| Vb | vertex buffer array filled and bound in one call | 0.195 (0.13) | 0.221 (0.15) | 0.158 (0.11) |
| Ib | `vkCmdBindIndexBuffer` | 0.039 (0.03) | 0.042 (0.03) | 0.036 (0.02) |
| Draw | `vkCmdDrawIndexed` | 0.067 (0.04) | 0.075 (0.05) | 0.057 (0.04) |
| **Total** | | **2.073 (1.38)** | **2.267 (1.51)** | **1.802 (1.21)** |

Grouped, from `t1`:

- **Our bookkeeping** (Prep, Begin, PipeGet, TexFill, Cache, UniFill): 1.36 ms, 65%.
- **Vulkan calls** (PipeBind, Push, Bind01, Bind1, Vb, Ib, Draw): 0.68 ms, 33%. Vb includes
  our fill loop.
- Both groups include their share of the timers, about 0.36 ms in all (estimate: 14 stamps x
  17.6 ns x 1,500 draws).

**The 39-descriptor bind.** Measured with timers, `vkCmdBindDescriptorSets` for the cached
set (set 1 and its dynamic offsets in the same call) costs 0.19-0.22 µs. Pushing a small set
costs 0.09-0.12 µs. The figure we were given of about 1.4 µs per bind was not reproduced as
host time in the call. On its own, the bind of the large set accounts for about 0.14 ms a
frame.

**Could the driver do the work later instead?** Possibly: the driver could do the bind's real
work during a later call or at submit (unverified). Duplicating calls inside a run tests that,
because a deferred cost would show up in the total. Each duplicate was issued with identical
arguments. A driver may handle a repeated call more cheaply than a real change (unverified), so
these are lower bounds on a call's marginal cost:

| Duplicated (variant windows) | Extra calls a frame | Paired change in draws | IQR |
|---|---|---|---|
| `bind01` | about 695 | +0.025 ms | -0.040..+0.065 |
| `push` | about 806 | +0.070 ms | -0.070..+0.145 |
| `vb,ib,pipe,bind1` | about 1,500 + 1,500 + 276 + 806 | +0.125 ms | +0.010..+0.170 |

No call shows a hidden cost of the order of 1 µs.

## 2. What the 39 descriptors are, draw to draw

Measured with `ONI_VK_PROFILE=analyse`, per 60-frame window. Programs whose set 0 is larger
than the 32 descriptors the device pushes (`maxPushDescriptors` = 32 on the RTX 3090 and on
the AMD iGPU here, from `vulkaninfo`):

| Program | Draws a frame | Set 0 |
|---|---|---|
| Klei/BatchedAnimation | 575-578 | 39 |
| Klei/BatchedAnimationConduit | 86 | 39 |
| Klei/BatchedAnimationHuman | 18-24 | 39 |
| Klei/AnimationSimple | 7-11 | 35 |
| Klei/BatchedAnimationUI | 1 | 39 |

The 39 bindings (BatchedAnimation) are separate images and samplers:

- 12 atlas images (`atlas0`-`atlas11`).
- `_LightBufferTex`, `_WorldZoneTex`, `_BuildingDamagedTex`, `_DiseaseOverlayTex`.
- Four per-batch data textures: `buildAndAnimTex`, `symbolOverrideInfoTex`,
  `symbolInstanceTex`, `instanceTex`.
- A sampler for almost every one of those.

How often each binding changed from the program's previous draw (BatchedAnimation, about
34,700 draws per window):

| Binding | Changed |
|---|---|
| `instanceTex`, `symbolInstanceTex` | every draw (about 580 distinct each per window) |
| `atlas0`, `buildAndAnimTex` | 43% of draws, always together (about 127 distinct) |
| `symbolOverrideInfoTex` | 6% |
| `atlas1`, `atlas2` and their samplers | about 1% |
| `symbolOverrideInfoTex`'s sampler | 2% |
| The other 30 bindings, including every other sampler | never |

Of the draws, 54% changed 2 bindings, 40% changed 4, and the rest 3-8. BatchedAnimationHuman
also changes `atlas2`-`atlas7` on about a quarter of its draws.

- **No draw named the same set as the draw before it.** The counter `sameAsPrevDraw` was 0
  for every 39-binding program except BatchedAnimationUI, which draws once a frame.
- **The same sets come back every frame.** The number of distinct sets in a window was about
  the number of draws in one frame (581-587). This matches the cache's "found 701, made
  0.2" a frame.
- **Draw order.** Cached-program draws come in runs of the same program, from single draws to
  63 or more (the counter's cap). Program switches (about 275 a frame) match the pipeline binds (about 276).

So skipping a set 0 that is already bound saves nothing for these programs: there is never
one. Only a split that separates the 2-4 changing images from the 35 static bindings could keep
most of set 0 bound.

## 3. Prototypes (paired windows in one run, untimed unless marked)

| Prototype | Run | Paired change in draws | IQR | Verdict |
|---|---|---|---|---|
| **c. split set 0, by name**: `instanceTex`, `symbolInstanceTex`, `symbolOverrideInfoTex`, `buildAndAnimTex` and `atlas0` moved to a pushed set 2; the 34 others in a cached set 0, bound only when it changes | as1 | **+0.210 ms** | +0.040..+0.280 | slower |
| | as2 | **+0.165 ms** | +0.100..+0.210 | slower |
| | ast (timed) | +0.035 ms | -0.040..+0.135 | |
| **c. split set 0, by type**: all 20 images pushed, samplers and the buffer cached | asi1 | **+0.285 ms** | +0.220..+0.370 | slower |
| **Identities first**: for cached programs, only each binding's identities are computed; the writes are built only for a new set | ai1 | **-0.110 ms** | -0.310..-0.065 | faster |
| | ai2 | **-0.160 ms** | -0.210..-0.080 | faster |
| | ait (timed) | **-0.200 ms** | -0.225..-0.115 | faster |

**The split.**

- **It worked as designed.** The static set 0 was re-bound only about 45 times a frame; about
  650 binds a frame were skipped.
- **The cost moved to the push.** Pushing the 5 dynamic descriptors cost about 0.47 µs a draw.
  This is the variant's push total minus the base windows' push total, over about 655 draws:
  (0.384 - 0.072) ms / 655. That is more than the cache lookup and the bind it replaced
  (0.18 + 0.16 µs in the same run).
- **Correctness.** A 900-frame run with the Khronos validation layer (SDK 1.4.363) reported no
  validation errors with the split on. The only messages were warnings about unused fragment
  outputs, which predate it. A captured frame rendered the animations normally.
- **The SPIR-V patch** is cheap and safe: rewrite the literal of each `OpDecorate %var
  DescriptorSet 0` to 2 for the moved bindings. The word count does not change.
- **The layout rules** come from the spec's "Pipeline Layout Compatibility" section:
  - Binding set 1 with another layout disturbs the sets above it, so set 2 must be pushed
    after set 1.
  - Only one push-descriptor set layout is allowed per pipeline layout
    (VUID-VkPipelineLayoutCreateInfo-pSetLayouts-00293).

**Identities first** removed 0.16 ms of TexFill (0.388 to 0.224 ms in `ait`). The rest did not
change.

## 4. Options, ranked

Savings are on the device thread's draws, about 1.8-2.4 ms a frame here. All the extensions
named are listed by this machine's `vulkaninfo` for the RTX 3090 (610.88) and, where noted, for
the AMD Radeon iGPU (Windows driver 2026.04.17).

### 1. Trim our own per-draw work. Do first.

Expected 0.4-0.7 ms in all (estimate: the sum of the parts below). Effort: low to medium.
Risk: low. Every driver. No SPIR-V changes.

- **Identities before writes** (measured: -0.11 to -0.20 ms). Done in the prototype as
  `IdsOf` + `TryGet`, falling back to the full path on a miss.
- **A cheaper cache key for the large sets** (estimate: most of Cache's 0.15-0.19 ms).
  - Today every hit hashes 78 longs.
  - The per-batch textures are the only bindings that change (section 2). So a key built from
    just those, plus a "static part unchanged" check, would hash 4-10 words instead of 78.
  - Alternatively, keep the set on the draw's batch: Klei's `KAnimBatch` owns these textures.
    That needs a stable hook from the batch to the draw (unverified; not explored).
- **Prep** (0.29-0.38 ms).
  - `PrepareTextures` walks the draw's textures twice and calls `IsAttached` (a loop over the
    target's colours) for each.
  - A per-texture "ready in this pass" stamp would make the common case one compare per
    texture. The stamp would be cleared on target change, upload and layout change.
  - Estimate: most of the 0.3 ms, unmeasured.
- **PipeGet** (0.16-0.20 ms). The key is hashed per draw. `ResolvedState`'s hash could be
  computed once when the draw is recorded on the main thread's worker (estimate, unmeasured).
- **Write templates for pushed sets.**
  - `vkCmdPushDescriptorSetWithTemplate` is core in Vulkan 1.4: vk.xml lists it under
    `VK_VERSION_1_4`, promoted from `VK_KHR_push_descriptor`.
  - It would replace the per-slot `VkWriteDescriptorSet` building of the 806 pushed draws with
    one array of image infos (unmeasured).
  - AMD's guide prefers `vkUpdateDescriptorSets` over update templates. That advice is for
    updates, not pushes (see Sources), so measure on AMD too.

### 2. A bindless path for the large programs. Later, if parity is wanted.

Expected: up to the large programs' TexFill, Cache and Bind01 time, about 0.3-0.4 ms
(estimate: 695 draws x (about 0.25 + 0.22 + 0.2) µs, less what writing indices costs, which is
unmeasured). Effort: high. Two variants:

- **2a. `VK_EXT_descriptor_heap` with set-and-binding mappings.**
  - The proposal (Vulkan-Docs `proposals/VK_EXT_descriptor_heap.adoc`, read) maps a
    `DescriptorSet`/`Binding` resource to a heap offset (`VkShaderDescriptorSetAndBindingMappingInfoEXT`).
    One source, `VkDescriptorMappingSourcePushIndexEXT`, takes the index from push data.
  - So Unity's shipped SPIR-V would run **unchanged**, and each draw would write a few indices
    instead of binding a set.
  - Requires Vulkan 1.2 or `VK_KHR_buffer_device_address`, plus
    `VK_KHR_shader_untyped_pointers`.
  - Coverage:
    - Exposed by NVIDIA 610.88 here; not by the AMD Windows driver here.
    - RADV (Mesa 26.1), ANV (Mesa 26.2, experimental) and NVIDIA's R595 series, per search
      snippets of Phoronix articles that could not be opened (403): **unverified**.
    - VKD3D-Proton is said to gate it behind a flag because of NVIDIA bugs (same snippets,
      unverified).
  - Risk: high. It is a new extension, and both paths would have to be kept.
- **2b. Descriptor indexing (core 1.2 features).**
  - Bind one large image array per frame and index it per draw.
  - Needs the SPIR-V rewritten: each sampled-image variable becomes an access chain into a
    runtime array, with the index taken from push constants.
  - No `spirv-opt` pass that does this was found (search turned up only the bindless
    validation instrumentation pass), so it would be our own patcher.
  - Coverage: the RTX 3090 and AMD iGPU here both report `runtimeDescriptorArray`,
    `shaderSampledImageArrayNonUniformIndexing` and
    `descriptorBindingSampledImageUpdateAfterBind`. The spec's minimum for
    `maxPerStageDescriptorUpdateAfterBindSampledImages` is 500,000.
  - Risk: medium-high. Every patched shader is a new failure mode, and it touches the
    "shipped SPIR-V" rule.

### 3. `VK_EXT_descriptor_buffer`. Low value here.

- Exposed on both GPUs here.
- The Khronos blog says it replaces pools, sets and `vkUpdateDescriptorSets` with memory and
  `memcpy`. Per draw, the only call would be `vkCmdSetDescriptorBufferOffsetsEXT`.
- It still needs one descriptor block per distinct set (about 700 cached here), and the bind
  it replaces costs only about 0.2 µs (measured). The saving is bounded by Bind01's 0.13-0.15
  ms plus some of Cache (estimate).
- The same blog warns that changing descriptor buffer bindings is "highly discouraged" and
  that mixing buffers and sets should be avoided. That means converting every program, not
  only the large ones.
- Effort: high. Value: low.
- Set 1's dynamic uniform buffers would become offsets into a buffer holding those buffer
  descriptors, or push data (unverified detail).

### 4. Splitting set 0 (option c). Measured, rejected on NVIDIA.

+0.17 to +0.29 ms (section 3). It could be retried as an incremental push of only the changed
2-4 bindings: the spec allows pushes to update a push set incrementally while the layout stays
the same ("Push descriptors can be updated incrementally"). But a push of 5 cost about 0.47 µs,
so even half of that would only about break even with the cached bind it replaces. Not
measured on AMD or Mesa.

### 5. Skipping redundant binds and reordering draws (option d). Nothing to gain for set 0.

- **Repeated sets**: no consecutive large-set draw repeats a set (section 2). Skipping an
  identical set 0 (prototype `skip0`) skipped 0 binds.
- **Reordering** to group draws is not considered: draw order is Klei's sort order. Whether
  any of these draws could be reordered safely, for example because none of them blend, was
  not checked (unverified).
- **Pipeline binds**: about 276 a frame, of which only about 21 keep the same program. Those
  21 come from `boundPipeline` being reset at each of the 45 pass starts.
- `VK_EXT_multi_draw` records several draws with the same bound state. Every consecutive
  large-program draw here changes set 0, so it does not apply.

### 6. Recording on several threads (option e). Not now.

- Recording is serial today: `GlDrawRecorder`'s `RecordAhead` resolves draws on worker threads
  for the main thread, but every `RunDraw` runs on the device thread in order.
- Secondary command buffers recorded on workers would shorten the device thread's frame but
  not fps, which the main thread bounds.
- AMD's RDNA guide says to "avoid using bundles and secondary command buffers" because they
  "likely hurt GPU performance".
- NVIDIA's Do's and Don'ts recommend building command buffers in parallel, but warn against
  tiny command buffers and say to "check carefully" that secondary buffers help.
- Effort: high. It is worth revisiting only if the device thread ever bounds the frame.

### The review's "left on the table" items

1. **More dynamic state.**
   - The spec confirms these are core in 1.3: cull mode, front face, primitive topology (only
     within a topology class unless `dynamicPrimitiveTopologyUnrestricted` from
     `VK_EXT_extended_dynamic_state3`), depth test/write/compare, stencil test/op, depth-bias
     enable and vertex-input binding stride. vk.xml lists them in the `VK_VERSION_1_3` require
     blocks "Promoted from VK_EXT_extended_dynamic_state" and "...state2".
   - Blend state and the colour write mask need `VK_EXT_extended_dynamic_state3`. The vertex
     layout needs `VK_EXT_vertex_input_dynamic_state`. Both are exposed on both GPUs here.
   - **Frame time**: at most about 21 pipeline binds a frame keep the program, at 0.36-0.47
     µs. That is under 0.01 ms (measured counts, estimated product).
   - **Hitches**: on NVIDIA with a fresh run home, 67 pipelines were made in the first 60
     frames and 72 in all, 81 ms of creation over 2,400 frames (about 1.1 ms each). Fewer
     pipeline variants would cut first-use stalls, not steady frames. The lavapipe figures in
     the review are not a basis for timing.
   - Verdict: low for frame time; a possible hitch improvement, unmeasured.
2. **`VK_EXT_graphics_pipeline_library` / `VK_EXT_shader_object`.**
   - Both are exposed on both GPUs here.
   - They only help first-use hitches, and the driver's pipeline cache is already kept across
     launches.
   - Low priority; agreed.
3. **Copying texels twice.**
   - The device thread's texture uploads measured 0.43-0.70 ms a frame (0.5-2 MiB a frame).
     Removing the second copy saves at most part of that.
   - The renderer-thread copy into `PixelMemory` was not measured here.
   - It is plausible but outside draw binding, and neither copy is on the thread that bounds
     fps.
   - Unmeasured; worth a stopwatch on both copies before any work.
4. **`VK_EXT_host_image_copy` and a transfer queue.**
   - `host_image_copy` is exposed by NVIDIA 610.88 here and not by the AMD iGPU driver.
   - There is no measurement here that argues against the review's "low value"; agreed, for
     the same reason as item 3.

## 5. Recommendation and what parity with OpenGL needs

1. Land the identities-first change (measured -0.11 to -0.20 ms). Then measure, one at a
   time, the cheaper large-set key, the per-pass "ready" stamp in `PrepareTextures` and
   pipeline-key hashing ahead of time. Use paired windows, as here.
2. Re-profile. If the device thread still matters, prototype `VK_EXT_descriptor_heap` with
   push-index mappings for the over-32 programs, on NVIDIA first. It keeps the shipped SPIR-V.
   Use descriptor indexing with a SPIR-V patch only if heaps prove unusable on AMD or Mesa.
3. Do not pursue the split set 0 (slower on NVIDIA), redundant-bind skipping for set 0
   (nothing to skip), descriptor buffers or multi-threaded recording, unless a new measurement
   changes the picture.

**Parity.** OpenGL's draws took 0.92 ms a frame (the earlier agent's measurement, same machine
and save). Vulkan's own calls already add up to about 0.55-0.68 ms (section 1, including the
timers' share). So matching OpenGL needs our bookkeeping cut from about 1.2-1.36 ms to about
0.3-0.4 ms. Step 1 should reach roughly halfway (estimate). The rest needs either the bindless
path (step 2), which removes TexFill, Cache and the set bind for the large programs, or more of
the same trimming.

## Sources

- Vulkan specification, Vulkan-Docs `main`: `chapters/descriptorsets.adoc` (Pipeline Layout
  Compatibility, Push Descriptor Updates, VUID-VkPipelineLayoutCreateInfo-pSetLayouts-00293),
  `chapters/limits.adoc` (`maxPushDescriptors`, minimum 32; update-after-bind limits),
  `chapters/drawing.adoc` (topology classes), `chapters/features.adoc`, `xml/vk.xml`
  (`VK_VERSION_1_3` and `VK_VERSION_1_4` promotions):
  https://github.com/KhronosGroup/Vulkan-Docs
- `VK_EXT_descriptor_heap` proposal:
  https://github.com/KhronosGroup/Vulkan-Docs/blob/main/proposals/VK_EXT_descriptor_heap.adoc
- Khronos blog, "VK_EXT_descriptor_buffer": https://www.khronos.org/blog/vk-ext-descriptor-buffer
- NVIDIA, "Vulkan Dos and Don'ts": https://developer.nvidia.com/blog/vulkan-dos-donts/
- AMD GPUOpen, "RDNA Performance Guide": https://gpuopen.com/learn/rdna-performance-guide/
- Arseny Kapoulkine, "Writing an efficient Vulkan renderer":
  https://zeux.io/2020/02/27/writing-an-efficient-vulkan-renderer/
- Faith Ekstrand, "Descriptors are hard":
  https://www.gfxstrand.net/faith/blog/2022/08/descriptors-are-hard/
- RADV `MAX_PUSH_DESCRIPTORS 32`:
  https://fuchsia.googlesource.com/third_party/mesa/+/main/src/amd/vulkan/radv_constants.h
- Driver support for `VK_EXT_descriptor_heap`: Phoronix search snippets only (pages returned
  403), unverified: https://www.phoronix.com/news/Intel-ANV-Descriptor-Heap-Merge,
  https://phoronix.com/news/VKD3D-Proton-Descriptor-Heaps
- This machine: `vulkaninfo` (Windows, 2026-11-01) for extensions and limits on the RTX 3090
  (610.88) and the AMD Radeon iGPU (2026.04.17).
