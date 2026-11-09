using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `off-world-fallers` (opt-in, a fix of the game's bug: in the Bug fixes group,
    /// out of `exact`). GravityComponents.FixedUpdate
    /// steps every falling object thirty times a game second. An object falling through a hole in
    /// the bottom of a world (a chute out of a rocket interior) is never moved again: each step's
    /// next position would leave its world, so the step leaves it where it is, half a cell below
    /// the grid's bottom row, still falling. It is stepped forever, reading its scattered transform
    /// and GameObject each time: Gluttony has 17,552 such objects (10,317 diamonds and about 7,200
    /// egg shells below two rocket interiors), about 8.5 ms a frame.
    ///
    /// Such an object is landed where it is instead, through the game's own landing for items
    /// (FallerComponents.OnLanded, as when one lands on a tile), once a step has left it in place
    /// and no later step could move it: it falls straight down (no sideways speed), the cell under
    /// it is off the grid (no liquid can slow it), and at half its speed the next position would
    /// still be below the grid's bottom row, and so would the cell under it (later steps are no slower; a step's time is fixed for
    /// its update rate). Only items are landed: objects whose landing is the item one.
    ///
    /// What changes: the object stops counting as falling (GameComps.Gravities.Has) and no Landed
    /// event is sent (an item's handler would only play a sound, or delete one landing outside the
    /// grid). It stays where it was. If its rocket interior is destroyed and its items are moved to
    /// the world below, it falls from there at rest instead of at the speed it had. Gravity is not
    /// saved: after a load the object falls again until its first step lands it.
    [GameMember(GameUse.Uses, "System.Action`1<UnityEngine.Transform> FallerComponents::OnLandedAction")]
    [GameMember(GameUse.Postfix | GameUse.Transpiler, "System.Void GravityComponents::FixedUpdate(System.Single)", "740814f5168a2a2f linux-744825 win-744825", "8ae11e603474819f linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230", Rewrites = 1)]
    public static class OffWorldFallers
    {
        public const string Name = "off-world-fallers";

        static readonly List<Transform> landing = new List<Transform>();
        static Action<Transform> itemLanding;
        static bool loggedOnce;

        public static void Install(Harmony harmony)
        {
            itemLanding = FallerComponents.OnLandedAction;
            var fixedUpdate = AccessTools.Method(typeof(GravityComponents), nameof(GravityComponents.FixedUpdate));
            harmony.Patch(fixedUpdate, transpiler: new HarmonyMethod(typeof(OffWorldFallers), nameof(Transpiler)),
                postfix: new HarmonyMethod(typeof(OffWorldFallers), nameof(Land)));
        }

        /// The step's last test, `Grid.IsValidCellInWorld(next, world)` (false: the object is left
        /// in place), becomes `InWorld(next, world, component, position, groundOffset, dt)`.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var list = new List<CodeInstruction>(instructions);
            var inWorld = AccessTools.Method(typeof(Grid), nameof(Grid.IsValidCellInWorld));
            var getPosition = AccessTools.Method(typeof(TransformExtensions), nameof(TransformExtensions.GetPosition), [typeof(Transform)]);
            var groundOffset = AccessTools.Method(typeof(GravityComponent), nameof(GravityComponent.GetGroundOffset), [typeof(GravityComponent)]);
            var mayLeaveWorld = AccessTools.Field(typeof(GravityComponent), nameof(GravityComponent.mayLeaveWorld));
            int calls = list.FindAll(i => i.Calls(inWorld)).Count;
            int at = list.FindIndex(i => i.Calls(inWorld));
            int position = list.FindIndex(i => i.Calls(getPosition));
            int offset = list.FindIndex(i => i.Calls(groundOffset));
            int component = list.FindIndex(i => i.LoadsField(mayLeaveWorld));
            if (calls != 1 || position < 0 || offset < 0 || component < 1)
                throw new InvalidOperationException($"{Name}: GravityComponents.FixedUpdate is not as expected ({calls} IsValidCellInWorld calls)");
            list[at].operand = AccessTools.Method(typeof(OffWorldFallers), nameof(InWorld));
            list.InsertRange(at, new[]
            {
                list[component - 1].Clone(),
                LoadOf(list[position + 1]),
                LoadOf(list[offset + 1]),
                new CodeInstruction(OpCodes.Ldarg_1),
            });
            OniMoono.Tweaks.TranspilerCounts.Report(Name, original, 1);
            return list;
        }

        /// The load of the local that `store` stores to.
        static CodeInstruction LoadOf(CodeInstruction store)
        {
            if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
            if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
            if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
            if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
            if (store.opcode == OpCodes.Stloc_S || store.opcode == OpCodes.Stloc) return new CodeInstruction(OpCodes.Ldloc, store.operand);
            throw new InvalidOperationException($"{Name}: expected a local store after a call in GravityComponents.FixedUpdate, found {store.opcode}");
        }

        static bool InWorld(int cell, int world, GravityComponent component, Vector3 position, float groundOffset, float dt)
        {
            if (Grid.IsValidCellInWorld(cell, world)) return true;
            if (component.onLanded == itemLanding && component.velocity.x == 0f && component.velocity.y < 0f
                && !Grid.IsValidCell(Grid.PosToCell((Vector2)position - new Vector2(0f, groundOffset))))
            {
                var slowest = (Vector2)position + new Vector2(0f, component.velocity.y * 0.5f) * dt;
                if (Grid.PosToCell(slowest) < 0 && Grid.PosToCell(slowest - new Vector2(0f, groundOffset)) < 0) landing.Add(component.transform);
            }
            return false;
        }

        /// After the steps: the objects left in place for good land.
        static void Land()
        {
            if (landing.Count == 0) return;
            int count = 0;
            foreach (var transform in landing)
            {
                if (transform == null || !GameComps.Gravities.Has(transform.gameObject)) continue;
                itemLanding(transform);
                count++;
            }
            landing.Clear();
            // Logged for the first landing and for large batches (a save's objects, after a load).
            if (count > 0 && (!loggedOnce || count >= 100)) Debug.Log($"[engine] {Name}: {count} objects landed below their worlds");
            loggedOnce |= count > 0;
        }
    }
}
