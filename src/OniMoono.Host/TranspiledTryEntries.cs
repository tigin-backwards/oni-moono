using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// ARCHITECTURE D17. ECMA-335 requires the evaluation stack to be empty where a try block
    /// starts; CoreCLR's JIT refuses a method that enters one with values on the stack
    /// (InvalidProgramException when Harmony applies the patch), and Mono compiles it. Harmony
    /// transpilers that insert a call returning a value nobody uses produce such methods (Better
    /// Info Cards' patch of SelectToolHoverTextCard.UpdateHoverElements leaves a KSelectable behind
    /// before a foreach). Where the try block never consumes those values they are dead: `leave` and
    /// `throw` empty the stack, the only ways out of a try. The host pops them before the try, in the
    /// instructions Harmony's transpilers produce (CodeTranspiler.GetResult); a method whose try
    /// does consume them, or that this analysis cannot follow, is left as it is.
    internal static class TranspiledTryEntries
    {
        public static void Install()
        {
            var getResult = AccessTools.Method("HarmonyLib.CodeTranspiler:GetResult")
                ?? throw new MissingMethodException("HarmonyLib.CodeTranspiler", "GetResult");
            new Harmony("OniMoono.Host.TranspiledTryEntries").Patch(getResult, postfix: new HarmonyMethod(typeof(TranspiledTryEntries), nameof(Postfix)));
        }

        static void Postfix(List<CodeInstruction> __result, ILGenerator generator, MethodBase method)
        {
            // Joins reached with values on the stack from one side only are evened first (D43),
            // since the depths below need consistent ones.
            TranspiledStackJoins.Even(__result, generator, method);
            // Each pass empties the outermost try entered with values; nested ones show in the next.
            for (int pass = 0; pass < 64; pass++)
            {
                var depths = Depths(__result, method);
                if (depths == null) return;
                int start = -1;
                for (int i = 0; i < __result.Count && start < 0; i++)
                    if (depths[i] > 0 && __result[i].blocks.Any(b => b.blockType == ExceptionBlockType.BeginExceptionBlock)) start = i;
                if (start < 0) return;
                int count = depths[start];
                if (!Dead(__result, depths, start, count)) return;
                var pops = Enumerable.Range(0, count).Select(_ => new CodeInstruction(OpCodes.Pop)).ToList();
                pops[0].labels.AddRange(__result[start].labels);
                __result[start].labels.Clear();
                __result.InsertRange(start, pops);
                EngineLog.Engine($"transpiled try entries: {count} value(s) left on the stack before a try block in {method.DeclaringType?.FullName}.{method.Name} popped");
            }
        }

        /// Whether the try block starting at `start`, entered with `count` values, leaves them all
        /// alone: no instruction of its body goes below that depth, and none branches back to its
        /// first instruction (whose labels move to the pops, outside the block). Handlers of
        /// blocks nested in it start from their own stack and are not counted.
        static bool Dead(List<CodeInstruction> code, int[] depths, int start, int count)
        {
            // One entry per open block, from the outermost: whether its handler has begun.
            var handling = new List<bool>();
            for (int i = start; i < code.Count; i++)
            {
                var instruction = code[i];
                if (i > start && instruction.blocks.Any(b => IsHandler(b.blockType)))
                {
                    if (handling.Count == 1) return true;
                    handling[^1] = true;
                }
                foreach (var block in instruction.blocks)
                    if (block.blockType == ExceptionBlockType.BeginExceptionBlock) handling.Add(false);
                if (!handling.Skip(1).Any(h => h) && depths[i] >= 0 && depths[i] - Pops(instruction) < count) return false;
                if (Targets(instruction).Any(label => code[start].labels.Contains(label))) return false;
                foreach (var block in instruction.blocks)
                    if (block.blockType == ExceptionBlockType.EndExceptionBlock && handling.Count > 0) handling.RemoveAt(handling.Count - 1);
            }
            return false;
        }

        internal static bool IsHandler(ExceptionBlockType type) => type is ExceptionBlockType.BeginCatchBlock or ExceptionBlockType.BeginExceptFilterBlock
            or ExceptionBlockType.BeginFinallyBlock or ExceptionBlockType.BeginFaultBlock;

        /// The stack depth before each instruction (-1 where none reaches it); null when the code
        /// cannot be followed (an operand this analysis does not know, inconsistent depths).
        static int[]? Depths(List<CodeInstruction> code, MethodBase method)
        {
            var depths = Enumerable.Repeat(-1, code.Count).ToArray();
            var index = new Dictionary<Label, int>();
            for (int i = 0; i < code.Count; i++)
                foreach (var label in code[i].labels) index[label] = i;
            var work = new Stack<(int, int)>();
            work.Push((0, 0));
            for (int i = 0; i < code.Count; i++)
            {
                var handler = code[i].blocks.FirstOrDefault(b => IsHandler(b.blockType));
                if (handler != null)
                    work.Push((i, handler.blockType is ExceptionBlockType.BeginCatchBlock or ExceptionBlockType.BeginExceptFilterBlock ? 1 : 0));
            }
            while (work.Count > 0)
            {
                var (i, depth) = work.Pop();
                while (i < code.Count)
                {
                    if (depths[i] >= 0)
                    {
                        if (depths[i] != depth) return null;
                        break;
                    }
                    depths[i] = depth;
                    var instruction = code[i];
                    int pops = Pops(instruction, method), pushes = Pushes(instruction);
                    if (pops < 0 || pushes < 0 || depth < pops) return null;
                    depth += pushes - pops;
                    var opcode = instruction.opcode;
                    foreach (var label in Targets(instruction))
                    {
                        if (!index.TryGetValue(label, out int target)) return null;
                        work.Push((target, opcode == OpCodes.Leave || opcode == OpCodes.Leave_S ? 0 : depth));
                    }
                    if (opcode.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw) break;
                    if (opcode == OpCodes.Leave || opcode == OpCodes.Leave_S || opcode == OpCodes.Endfinally || opcode == OpCodes.Endfilter) break;
                    i++;
                    // A handler is entered from its block's exception, not by falling into it.
                    if (i < code.Count && code[i].blocks.Any(b => IsHandler(b.blockType))) break;
                }
            }
            return depths;
        }

        internal static IEnumerable<Label> Targets(CodeInstruction instruction) => instruction.operand switch
        {
            Label label => [label],
            Label[] labels => labels,
            _ => [],
        };

        /// Values the instruction takes from the stack, for the dead-value test (`ret` and calls
        /// as their operands say; unknowns as the whole stack, which keeps the method unchanged).
        static int Pops(CodeInstruction instruction) => Pops(instruction, null) is int n and >= 0 ? n : int.MaxValue / 2;

        internal static int Pops(CodeInstruction instruction, MethodBase? method)
        {
            var opcode = instruction.opcode;
            if (opcode == OpCodes.Ret)
                return method == null ? 0 : method is MethodInfo info && info.ReturnType != typeof(void) ? 1 : 0;
            switch (opcode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
                #pragma warning disable format // a hand-laid table
                case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8: case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref:
                #pragma warning restore format
                case StackBehaviour.Popref_popi_pop1: return 3;
                case StackBehaviour.Varpop:
                    if (instruction.operand is not MethodBase callee || opcode == OpCodes.Calli) return -1;
                    int count = callee.GetParameters().Length;
                    return opcode == OpCodes.Newobj || callee.IsStatic ? count : count + 1;
                default: return -1;
            }
        }

        internal static int Pushes(CodeInstruction instruction)
        {
            var opcode = instruction.opcode;
            switch (opcode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4: case StackBehaviour.Pushr8: case StackBehaviour.Pushref: return 1;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Varpush:
                    if (opcode == OpCodes.Calli) return -1;
                    return instruction.operand switch
                    {
                        ConstructorInfo => opcode == OpCodes.Newobj ? 1 : 0,
                        MethodInfo callee => callee.ReturnType == typeof(void) ? 0 : 1,
                        _ => -1,
                    };
                default: return -1;
            }
        }
    }
}
