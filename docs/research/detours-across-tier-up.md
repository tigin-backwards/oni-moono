# Detours across tier-up: the short-code overrun, and a plan

Plan, 2026-11-26, from issue #127 (the game's own `DailyReport.GetEntry` ran with
compact-report-entries' prefix skipped). Option 1 is done (`src/OniMoono.Host/PatchedTiering.cs`);
see "Done" at the end.

## What happens now

Harmony's MonoMod detours a patched method by writing a jump to the replacement at the start of
the method's compiled code. Tiered compilation compiles that method again later: tier 0, then
tier 0 instrumented, then tier 1, after its 30th call (from start-up, since the call-counting
delay of 0 in v2026.11.21.01). MonoMod's JIT hook then raises OnMethodCompiled.
`PlatformTripleDetourFactory+Detour+ManagedDetourBox.OnMethodCompiled` writes a new jump into the
new code, through the JIT's writable copy, before the runtime publishes it. In a 2minnow session
with `MONOMOD_LogToFile`, GetEntry's detour was applied at start-up and moved twice, at 18 s and
22 s.

The patched method's own body never runs while its detour holds: every call jumps to the
replacement. Its tier-up compiles code nobody runs. Each recompile is also one more chance for
the move to go wrong:

- **Overrun.** On x64 MonoMod picks the shortest jump that reaches: rel32 (5 bytes), rel32
  through a slot it allocates nearby (6), or absolute (14). It falls back to absolute when the
  code is shorter than all of them ("Size too small for all known detour kinds; defaulting to
  Abs64"). An empty virtual method tiers up to a 1-byte `ret`. `OffsetTracker.UpdateCell`, which
  fast-offset-tables patches, does this in every session: 14 bytes are written into a 1-byte
  method.
- **Skipped moves.**
  - The move returns at once while the detour is being applied or undone (`IsApplying`), and that
    flag is set under a lock that is not the move's. So a recompile that lands while another
    thread re-patches the method leaves the new code without a jump.
  - The hook reports only a compile that is not nested in another on the same thread.
  - A native exception caught in the JIT, or an exception in any earlier OnMethodCompiled handler,
    ends the report before the move.
  - Until now only MonoMod's own debug log said so.

Where the overrun lands: the method's allocation in the code heap is its 8-byte CodeHeader, then
its code at the next 16- or 32-byte boundary. With W^X (the default) the JIT writes into a buffer
of that allocation, which is copied out after the compile. The test below shows the 14-byte jump
still works from a 1-byte method under W^X: the copy carries it. The bytes past the code are
alignment padding, and at most the start of the next allocation's CodeHeader. The next method's
code starts at the next aligned boundary, past those 14 bytes, so the overrun cannot reach
another method's jump. With W^X off, the write goes straight into the heap, so a following
allocation's header is what it can corrupt. That breaks stack walks of that method, not its
detour.

## Options

1. **Keep a patched method off tier-up (recommended).** When Harmony detours a method that has no
   code yet, clear its MethodDesc's eligibility for tiered compilation first. UnboxedMethods
   already sets MethodDesc flags this way (NotInline). MonoMod's own compile of it is then fully
   optimised and final: no recompiles, so no moves, overruns or skipped moves for it.
   - A method compiled before its patch (a late patch, a mod's) keeps tiering. For those, the move
     stays as it is, with the new logging.
   - **Cost:** nothing at run time. The method's own body is unreachable behind the jump, and the
     replacement (a DynamicMethod) never tiered anyway. One flag write per patch.
   - **Risk:** the flag must be cleared before the runtime installs a call-counting stub. That
     needs a check against the .NET 10 runtime source (MIT) of `MethodDesc::DetermineTieredCompilationConfig`,
     `CallCountingManager` and `CodeVersionManager`. The flag also has to stay at the CoreCLR 10
     MethodDesc layout, checked at start-up as UnboxedMethods does, and off (with a log line) if
     it differs.
2. **Skip the move when the code is shorter than the jump.** That leaves the new code unpatched,
   so the patch is lost: worse than the overrun. Rejected.
3. **A shorter jump.** Nothing shorter than 5 bytes reaches (a 2-byte jump spans ±127 bytes).
   Rejected.
4. **Point the method's entry at the replacement instead of writing into its code.** That means
   setting the precode or the slot, which the runtime rewrites at each tier-up and backpatch. It
   is more of the runtime's internals than option 1. Rejected.
5. **Serialise re-patching with tier-up** (for methods option 1 cannot cover). Hold MonoMod's move
   lock (the box's `Sync`) in Apply and Undo too, so a recompile waits instead of returning. That
   is a Harmony prefix and finalizer on `DetourBase.Apply` and `Undo` taking `Sync`. Cost: an
   uncontended lock per patch. Lock order must stay box, then `Sync`, as the move takes them.
   Worth doing after option 1, if the new log lines show the race in players' logs.

## Tests

- **Unit** (tests/OniMoono.UnityEngine.Tests):
  - An emitted empty virtual method and a loop-shaped method, patched before their first call
    and called until tier-up would have happened: their compiled code must not change (no
    "recompiled" from MonoModLog), and the prefix must run on every call.
  - A method compiled before its patch must keep tiering, with its detour moved.
- **Host:** `--check-types` and the `late-patch` and `issue-52-*` specs, which check patches
  across tier-up.
- **Player:** the 2minnow Tune Up spec from #127, with `MONOMOD_LogToFile`: no "Updating detour"
  for GetEntry, and no "Size too small" line.
- **Perf:** none needed, since nothing that runs changes. A start-up time before and after on one
  colony, as a check.

## Done (option 1)

`PatchedTiering`, installed with the host's first patches (after MonoModLog), is a prefix of
MonoMod's `PlatformTripleDetourFactory.CreateDetour`, which compiles the method it detours and
applies the detour. For an IL method eligible for tiering whose native code slot is still empty,
it clears `enum_flag3_IsEligibleForTieredCompilation` (0x8000 in `m_wFlags3AndTokenRemainder`).
A prefix there, not on `TryDisableInlining`, because ModLoaderInlining calls that too, on methods
it does not detour. `ONI_PATCHED_TIERING=0` turns it off; Player.log gets a summary line at quit.

The premise, in the .NET 10.0.13 runtime (`src/coreclr/vm`):

- Eligibility is set once per chunk (`MethodDescChunk::DetermineAndSetIsEligibleForTieredCompilation`,
  method.cpp), for a type's methods by `MethodTableBuilder` as it builds the type
  (methodtablebuilder.cpp, "Make sure that eligibility for versionability is computed"), so it is
  final before reflection can name a method. A set flag means determined and eligible.
- Call counting starts only once the method has code. `CodeVersionManager::PublishVersionableCodeIfNecessary`
  (codeversion.cpp) compiles the default code version when `GetNativeCode()` is null and only then
  publishes it through `CallCountingManager::SetCodeEntryPoint` (callcounting.cpp), the one place
  a call-counting stub is installed. That function reads the flag again first: with it clear it
  sets the entry point and counts nothing.
- With the flag clear before the first compile, the prestub takes a never-eligible method's path:
  `DoPrestub`'s `IsVersionable()` is false (prestub.cpp), `PrepareILBasedCode` compiles with
  `shouldTier` false, `TieredCompilationManager::GetJitFlags` gives no tier-0 flags and
  `GetInitialOptimizationTier` says Optimized (tieredcompilation.cpp). That is the path of a method
  with AggressiveOptimization, which `DetermineAndSetIsEligibleForTieredCompilation` excludes.
- `GetNativeCode` reads the native code slot, after the MethodDesc (16 bytes), the optional
  non-vtable slot (8) and MethodImpl (16) (`GetAddrOfNativeCodeSlot`).

The start-up check emits a probe type and reads two static and two virtual methods' flags and
code slots before and after `RuntimeHelpers.PrepareMethod`; any mismatch turns it off with a log
line. A virtual method's `PrepareMethod` compiles nothing until it has a temporary entry point
(`MethodDesc::IsPointingToPrestub`), so the probe asks for its function pointer first, as MonoMod's
detour does. A first call racing the patch on another thread: the flag is set back when code
appeared meanwhile, and otherwise that tier-0 code stays final.

Methods patched before compile are now compiled fully optimised by MonoMod's own compile, so an
empty method is a 1-byte `ret` at apply time. The apply's jump is not the move's: MonoMod passes
no size hint there and picks rel32 (5 bytes) or rel32 through a nearby slot (6), Abs64 only when
it cannot allocate near (`x86_64Arch.ComputeDetourInfo`). The code heap
(`ExplicitControlLoaderHeap::AllocMemForCode_NoThrow`, 16-byte `CODE_SIZE_ALIGN`) puts the next
allocation's 8-byte CodeHeader at the earliest 8 bytes past a 1-byte method's start, so a 5- or
6-byte jump stays in its padding, where the 14-byte move's ran into the header.

Results (2026-11-26, Linux):

- Unit (`PatchedTieringTests`, `MonoModLogTests`; the latter now calls its method before the
  patch, as a late patch): an empty virtual method and a loop patched before their first call
  are never compiled again while a method compiled before its patch tiers up beside them; every
  call runs its prefix.
- SharedGenerics' order check (D18) now runs its shared code once before the patches, so it
  keeps tiering and the check still covers the recompile; a second unit test patches it before
  its first compile, where it stays off tier-up, and the replacement detoured last runs on every
  call too. The shared code of a generic class's method is an ordinary IL MethodDesc, so
  PatchedTiering covers it.
- `--check-types`: a new last check, the same three shapes in the host (5,000 calls each).
- Specs `late-patch`, `issue-52-automation-overlay`, `issue-52-click-cycle-patched` pass;
  `issue-52-fast-track-no-tiering` is skipped by the mod gate on U60 (Fast Track runs on U59 only).
- #127's 2minnow Tune Up run with `MONOMOD_LogToFile`: 156 patched methods kept off tier-up, 27
  compiled before their patch keep tiering, 75 not tiered anyway (stubs, generic code, methods
  marked AggressiveOptimization). MonoMod's detour moves fell from 146 to 34, all of Harmony's,
  MonoMod's and the runtime's own methods, which run before they are patched. No move of
  GetEntry or UpdateCell, no "Size too small", and no MonoModLog short-code line in Player.log.

- Start-up and a CPU-side figure, one pair on a Linux lane of a loaded machine (release QA ran
  beside it), 2minnow, after a warm-up launch: the save's load (load-times.json, all stages)
  9.2 s off (`ONI_PATCHED_TIERING=0`) and 9.9 s on, where the warm-up launch, also off, took
  9.9 s: within the spread. 60 game seconds at speed 3 took 20.9 s wall off (and in the warm-up)
  and 21.3 s on, 2 % from one pair on a loaded machine; nothing that runs changed (the patched
  bodies never run), so read as noise, to confirm on Windows if it matters.
