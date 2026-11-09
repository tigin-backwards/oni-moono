#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Host
{
    /// The load-time half of `exact-worldgen` (GameTweaks.ExactWorldgen), run in MonoConversions'
    /// pass over the game's assemblies: worldgen's float arithmetic is evaluated as Unity's Mono
    /// evaluates it (ARCHITECTURE D25). Unity runs Mono with `-O=-float32`, so a float32 value is
    /// widened to double where it is loaded, arithmetic and comparisons run on doubles, and the
    /// result is rounded to float32 only where it is stored (a local, field, array element,
    /// argument, return value) or cast. CoreCLR rounds every operation to float32. A seed's map
    /// depends on the last bit of millions of such operations (noise, Poisson-disk sites, Voronoi
    /// relaxation, border widths), so without this the same seed draws slightly different biome
    /// borders than the original (issue #66).
    ///
    /// | IL that pushes a float32                                    | becomes                    |
    /// |-------------------------------------------------------------|----------------------------|
    /// | `ldloc`, `ldarg`, `ldfld`, `ldsfld`, `ldelem.r4`, `ldind.r4`, `ldobj`/`ldelem.any`/`unbox.any` float32, `ldc.r4`, `conv.r4`, a call returning float32 | the same, then `conv.r8` |
    /// | `box`, `stobj`, `stelem.any` of float32                     | `conv.r4` first            |
    ///
    /// Stores, arguments and returns narrow the double back to float32 by themselves (the CLI's
    /// F stack type converts implicitly to the location's type; RyuJIT emits cvtsd2ss), as Mono's
    /// stores do. Generic code whose float32 comes from a type parameter is not widened.
    ///
    /// Only worldgen is rewritten (Scope): it runs once, before a game starts, so doubles cost
    /// nothing in play. The simulation's hot code keeps its float32 arithmetic.
    internal static class MonoFloatWidening
    {
        /// The tweak's switch (Program sets it before any assembly loads).
        internal static bool Enabled { get; set; }

        /// What is widened, by assembly: whole namespaces (with their nested namespaces) and
        /// single top-level types of the global namespace. Found by comparing generated clusters
        /// with the original's (tools/E2eDriver's worldgen capture, the `exact-worldgen` spec):
        /// this is the narrowest set that reproduces every world bit for bit.
        internal static readonly (string Assembly, string[] Namespaces, string[] Types)[] Scope =
        [
            // Klei's copy of libnoise: every noise primitive, filter and modifier.
            ("LibNoiseDotNet", ["*"], []),
            // The layout: Poisson-disk sites, the Voronoi tree and its relaxation, noise trees,
            // element bands; and the global helpers they call.
            ("Assembly-CSharp-firstpass",
                ["ProcGen", "Delaunay", "VoronoiTree", "Geometry", "LibNoiseDotNet"],
                ["PointGenerator", "UniformPoissonDiskSampler", "MathUtil", "SeededRandom", "Vector2f", "ElementBandConfiguration", "ElementGradient", "Gradient`1", "LevelLayer"]),
            // Rendering the layout into cells: borders, features, templates, settling, mobs.
            ("Assembly-CSharp", ["ProcGenGame"], []),
        ];

        /// The scope's entry for an assembly's file, or null.
        static (string Assembly, string[] Namespaces, string[] Types)? EntryFor(string file)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            foreach (var entry in Scope)
                if (string.Equals(entry.Assembly, name, StringComparison.OrdinalIgnoreCase)) return entry;
            return null;
        }

        /// Whether the assembly file is in the scope.
        internal static bool MayTouch(string file) => EntryFor(file) != null;

        /// Whether a type (nested types by their outermost) is in the entry's scope.
        internal static bool InScope(Cecil.TypeDefinition type, string[] namespaces, string[] types)
        {
            var top = type;
            while (top.DeclaringType != null) top = top.DeclaringType;
            var ns = top.Namespace ?? "";
            if (ns.Length == 0) return Array.IndexOf(types, top.Name) >= 0 || Array.IndexOf(namespaces, "*") >= 0;
            foreach (var n in namespaces)
                if (n == "*" || ns == n || (ns.Length > n.Length && ns.StartsWith(n, StringComparison.Ordinal) && ns[n.Length] == '.')) return true;
            return false;
        }

        /// Widens the module's float arithmetic when it is in the scope; returns the loads widened.
        internal static int RewriteModule(Cecil.ModuleDefinition module, string file, out int methods)
        {
            methods = 0;
            if (EntryFor(file) is not { } entry) return 0;
            int sites = 0;
            foreach (var type in module.GetTypes())
            {
                if (!InScope(type, entry.Namespaces, entry.Types)) continue;
                foreach (var method in type.Methods)
                {
                    int n = WidenMethod(method);
                    if (n == 0) continue;
                    sites += n;
                    methods++;
                }
            }
            return sites;
        }

        static bool IsSingle(Cecil.TypeReference? type) => type != null && type.MetadataType == Cecil.MetadataType.Single;

        /// Whether the instruction leaves a float32 on the stack.
        static bool PushesSingle(Cil.Instruction instruction, Cecil.MethodDefinition method) => instruction.OpCode.Code switch
        {
            Cil.Code.Ldc_R4 or Cil.Code.Ldelem_R4 or Cil.Code.Ldind_R4 or Cil.Code.Conv_R4 => true,
            Cil.Code.Ldloc or Cil.Code.Ldloc_S => IsSingle(((Cil.VariableDefinition)instruction.Operand).VariableType),
            Cil.Code.Ldloc_0 or Cil.Code.Ldloc_1 or Cil.Code.Ldloc_2 or Cil.Code.Ldloc_3
                => IsSingle(method.Body.Variables[instruction.OpCode.Code - Cil.Code.Ldloc_0].VariableType),
            Cil.Code.Ldarg or Cil.Code.Ldarg_S => IsSingle(((Cecil.ParameterDefinition)instruction.Operand).ParameterType),
            Cil.Code.Ldarg_0 or Cil.Code.Ldarg_1 or Cil.Code.Ldarg_2 or Cil.Code.Ldarg_3 => IsSingle(Argument(method, instruction.OpCode.Code - Cil.Code.Ldarg_0)),
            Cil.Code.Ldfld or Cil.Code.Ldsfld => IsSingle(((Cecil.FieldReference)instruction.Operand).FieldType),
            Cil.Code.Ldobj or Cil.Code.Ldelem_Any or Cil.Code.Unbox_Any => IsSingle((Cecil.TypeReference)instruction.Operand),
            Cil.Code.Call or Cil.Code.Callvirt => IsSingle(((Cecil.MethodReference)instruction.Operand).ReturnType),
            _ => false,
        };

        /// The type of ldarg.N's argument (`this` first for an instance method).
        static Cecil.TypeReference? Argument(Cecil.MethodDefinition method, int n)
        {
            if (method.HasThis)
            {
                if (n == 0) return method.DeclaringType;
                n--;
            }
            return n < method.Parameters.Count ? method.Parameters[n].ParameterType : null;
        }

        /// Whether the instruction takes a float32 value from the top of the stack where the CLI
        /// does not narrow by itself (a box or a store through a type token).
        static bool TakesSingle(Cil.Instruction instruction) => instruction.OpCode.Code is Cil.Code.Box or Cil.Code.Stobj or Cil.Code.Stelem_Any
                                                               && IsSingle(instruction.Operand as Cecil.TypeReference);

        /// Widens one method's float32 loads; returns how many.
        internal static int WidenMethod(Cecil.MethodDefinition method)
        {
            if (!method.HasBody) return 0;
            var body = method.Body;
            var processor = body.GetILProcessor();
            var instructions = body.Instructions.ToList();
            int widened = 0;
            foreach (var instruction in instructions)
            {
                if (TakesSingle(instruction))
                {
                    // Keep branches to the box landing on the conversion before it.
                    var narrow = processor.Create(Cil.OpCodes.Conv_R4);
                    processor.InsertBefore(instruction, narrow);
                    Retarget(body, instruction, narrow);
                }
                if (!PushesSingle(instruction, method)) continue;
                if (instruction.Next is { OpCode.Code: Cil.Code.Conv_R8 }) continue;  // already widened (a cast to double)
                processor.InsertAfter(instruction, processor.Create(Cil.OpCodes.Conv_R8));
                widened++;
            }
            if (widened > 0) CilBranches.Widen(body);
            return widened;
        }

        /// Program's runtime checks (ExactWorldgen.SelfCheck): a module built here with float
        /// arithmetic whose float32 and double evaluations differ, from tools/UnityProbe's
        /// FloatPrecision measurements of the original, gives the original's values once widened.
        internal static string SelfCheck()
        {
            using var module = Cecil.ModuleDefinition.CreateModule("WideningCheck", Cecil.ModuleKind.Dll);
            var single = module.TypeSystem.Single;
            var type = new Cecil.TypeDefinition("", "Subject", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
            module.Types.Add(type);
            Cecil.MethodDefinition Method(string name, Cecil.TypeReference result, params Cil.OpCode[] ops)
            {
                var m = new Cecil.MethodDefinition(name, Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, result);
                for (int i = 0; i < 3; i++) m.Parameters.Add(new Cecil.ParameterDefinition(single));
                var il = m.Body.GetILProcessor();
                foreach (var op in ops) il.Append(il.Create(op));
                il.Append(il.Create(Cil.OpCodes.Ret));
                type.Methods.Add(m);
                return m;
            }
            // (int)(a * b): 0.7f * 10f is 6 on Mono, 7 rounded to float32 first.
            Method("Truncated", module.TypeSystem.Int32, Cil.OpCodes.Ldarg_0, Cil.OpCodes.Ldarg_1, Cil.OpCodes.Mul, Cil.OpCodes.Conv_I4);
            // a * b == c: 0.7f * 10f == 7f is false on Mono.
            Method("Equal", module.TypeSystem.Boolean, Cil.OpCodes.Ldarg_0, Cil.OpCodes.Ldarg_1, Cil.OpCodes.Mul, Cil.OpCodes.Ldarg_2, Cil.OpCodes.Ceq);
            // (double)(a * b - c): -1.1920929E-07 on Mono for 0.7f, 10f, 7f.
            Method("Difference", module.TypeSystem.Double, Cil.OpCodes.Ldarg_0, Cil.OpCodes.Ldarg_1, Cil.OpCodes.Mul, Cil.OpCodes.Ldarg_2, Cil.OpCodes.Sub, Cil.OpCodes.Conv_R8);
            // a * b + c returned as a float: rounded once, at the return.
            Method("Returned", single, Cil.OpCodes.Ldarg_0, Cil.OpCodes.Ldarg_1, Cil.OpCodes.Mul, Cil.OpCodes.Ldarg_2, Cil.OpCodes.Add);
            int sites = 0;
            foreach (var m in type.Methods) sites += WidenMethod(m);
            using var image = new System.IO.MemoryStream();
            module.Write(image);
            var context = new System.Runtime.Loader.AssemblyLoadContext("WideningCheck", isCollectible: true);
            try
            {
                var subject = context.LoadFromStream(new System.IO.MemoryStream(image.ToArray())).GetType("Subject")!;
                object? Call(string name, float a, float b, float c) => subject.GetMethod(name)!.Invoke(null, [a, b, c]);
                float x = 0.7f, y = 10f, z = 7f;
                var truncated = (int)Call("Truncated", x, y, z)!;
                var equal = (bool)Call("Equal", x, y, z)!;
                var difference = (double)Call("Difference", x, y, z)!;
                // 1/3 * 3 + 5e-8: 1.0000001192 rounded once from double; 1 with a rounding after each step.
                float p = 1f / 3f, q = 3f, r = 5e-8f;
                var returned = (float)Call("Returned", p, q, r)!;
                float once = (float)((double)p * q + r);
                if (truncated != 6 || equal || difference != (double)x * y - z || BitConverter.SingleToInt32Bits(returned) != BitConverter.SingleToInt32Bits(once))
                    throw new InvalidOperationException($"widened: (int)(0.7f*10f) {truncated}, == {equal}, difference {difference:R}, returned {returned:R} (want 6, False, {(double)x * y - z:R}, {once:R})");
                return $"{sites} loads widened; (int)(0.7f*10f) is 6, 0.7f*10f == 7f is false, a*b+c rounds once";
            }
            finally
            {
                context.Unload();
            }
        }

        /// Branches, switches and exception handlers that pointed at `from` point at `to`.
        static void Retarget(Cil.MethodBody body, Cil.Instruction from, Cil.Instruction to)
        {
            foreach (var i in body.Instructions)
            {
                if (i.Operand == from) i.Operand = to;
                else if (i.Operand is Cil.Instruction[] targets)
                    for (int k = 0; k < targets.Length; k++)
                        if (targets[k] == from) targets[k] = to;
            }
            foreach (var h in body.ExceptionHandlers)
            {
                if (h.TryStart == from) h.TryStart = to;
                if (h.TryEnd == from) h.TryEnd = to;
                if (h.HandlerStart == from) h.HandlerStart = to;
                if (h.HandlerEnd == from) h.HandlerEnd = to;
                if (h.FilterStart == from) h.FilterStart = to;
            }
        }
    }
}
