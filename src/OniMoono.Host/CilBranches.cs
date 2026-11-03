using System;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Host
{
    /// The load-time rewrites' branch fix-up (MonoConversions and the passes beside it): plain
    /// Cecil, compiled into the tests with PathGridPagesRewrite.
    internal static class CilBranches
    {
        /// Short branches that the longer calls put out of reach become long ones.
        internal static void Widen(Cil.MethodBody body)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                int offset = 0;
                foreach (var instruction in body.Instructions)
                {
                    instruction.Offset = offset;
                    offset += instruction.GetSize();
                }
                foreach (var instruction in body.Instructions)
                {
                    if (instruction.OpCode.OperandType != Cil.OperandType.ShortInlineBrTarget) continue;
                    int distance = ((Cil.Instruction)instruction.Operand).Offset - (instruction.Offset + instruction.GetSize());
                    if (distance is >= sbyte.MinValue and <= sbyte.MaxValue) continue;
                    instruction.OpCode = LongForm(instruction.OpCode);
                    changed = true;
                }
            }
        }

        static Cil.OpCode LongForm(Cil.OpCode op) => op.Code switch
        {
            Cil.Code.Br_S => Cil.OpCodes.Br, Cil.Code.Brfalse_S => Cil.OpCodes.Brfalse, Cil.Code.Brtrue_S => Cil.OpCodes.Brtrue,
            Cil.Code.Beq_S => Cil.OpCodes.Beq, Cil.Code.Bge_S => Cil.OpCodes.Bge, Cil.Code.Bgt_S => Cil.OpCodes.Bgt,
            Cil.Code.Ble_S => Cil.OpCodes.Ble, Cil.Code.Blt_S => Cil.OpCodes.Blt, Cil.Code.Bne_Un_S => Cil.OpCodes.Bne_Un,
            Cil.Code.Bge_Un_S => Cil.OpCodes.Bge_Un, Cil.Code.Bgt_Un_S => Cil.OpCodes.Bgt_Un, Cil.Code.Ble_Un_S => Cil.OpCodes.Ble_Un,
            Cil.Code.Blt_Un_S => Cil.OpCodes.Blt_Un, Cil.Code.Leave_S => Cil.OpCodes.Leave,
            _ => throw new InvalidOperationException($"no long form of {op}"),
        };
    }
}
