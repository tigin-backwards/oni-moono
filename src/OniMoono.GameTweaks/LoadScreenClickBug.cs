using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace OniMoono.GameTweaks
{
    /// Whether a build's LoadScreen.AddColonyToList still has the bug fix-load-screen-clicks
    /// fixes, read from its IL: it adds a handler to a KButton's onClick (`onClick +=`, a call of
    /// add_onClick) with no ClearOnClick or remove_onClick of a KButton since the add before it.
    /// U59 and U60 clear the row's cloud and local buttons before adding their handlers, but not
    /// the row's main button. Plain reflection, no game types: the tests compile it in and read
    /// the game's assemblies with it.
    public static class LoadScreenClickBug
    {
        /// Null when `method` has the bug; else what it does instead, for the log line
        /// ("left out, this build already ...").
        public static string Fixed(MethodBase method)
        {
            bool cleared = false, added = false;
            foreach (var called in Calls(method))
            {
                if (called.DeclaringType?.Name != "KButton") continue;
                if (called.Name is "ClearOnClick" or "remove_onClick") cleared = true;
                else if (called.Name == "add_onClick")
                {
                    if (!cleared) return null;
                    added = true;
                    cleared = false;
                }
            }
            return added ? "clears each button before adding its click" : "adds no click to a button";
        }

        static readonly Dictionary<short, OpCode> opCodes = OpCodesByValue();

        static Dictionary<short, OpCode> OpCodesByValue()
        {
            var all = new Dictionary<short, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.GetValue(null) is OpCode op) all[op.Value] = op;
            return all;
        }

        /// The methods `method` calls (call, callvirt), in IL order; a call whose target cannot
        /// be resolved is skipped.
        public static IEnumerable<MethodBase> Calls(MethodBase method)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) yield break;
            var typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            int i = 0;
            while (i < il.Length)
            {
                short value = il[i] == 0xFE && i + 1 < il.Length ? (short)(0xFE00 | il[i + 1]) : il[i];
                if (!opCodes.TryGetValue(value, out var op)) yield break;
                i += op.Size;
                int operand = op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => i + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, i) : 4,
                    _ => 4,
                };
                if ((op.Value == OpCodes.Call.Value || op.Value == OpCodes.Callvirt.Value) && i + 4 <= il.Length)
                {
                    MethodBase called = null;
                    try
                    {
                        called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs);
                    }
                    catch (Exception)
                    {
                    }
                    if (called != null) yield return called;
                }
                i += operand;
            }
        }
    }
}
