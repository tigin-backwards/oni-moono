# Detours across tier-up: the short-code overrun, and a plan

Plan, 2026-11-26, from issue #127 (the game's own `DailyReport.GetEntry` ran with
compact-report-entries' prefix skipped). No code here yet. It needs the user's go-ahead first.

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
