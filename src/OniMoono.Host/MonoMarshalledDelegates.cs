using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Host
{
    /// ARCHITECTURE D64. Mono gives a delegate passed to native code (a P/Invoke's delegate
    /// argument, Marshal.GetFunctionPointerForDelegate) a trampoline, and for a delegate to a static
    /// method it keeps the delegate alive for good: its delegate_hash_table holds a strong GC handle
    /// (marshal.c, delegate_hash_table_add), and the trampoline is shared by every delegate of that
    /// type and method. CoreCLR keeps such a delegate only as long as managed code does, and calling
    /// its thunk after a collection fails fast ("A callback was made on a garbage collected
    /// delegate"). The game passes SimDLL its message handler as a fresh delegate
    /// (`Sim.SIM_Initialize(Sim.DLL_MessageHandler)`) that nothing keeps; SimDLL calls it to report
    /// an error, and from its exception handler when the process crashes (issue #75, where that
    /// replaced the crash's own exception). The calls of P/Invokes with delegate parameters in the
    /// game's assemblies and in mods, and their calls of GetFunctionPointerForDelegate, pass a
    /// delegate to a static method through Keep (rewritten as they load, in MonoConversions' pass):
    /// the first delegate of its type and method, kept for good, so native code gets one thunk
    /// that stays valid, as Mono's trampoline does. A delegate with a target passes unchanged:
    /// Mono holds those weakly too.
    public static class MonoMarshalledDelegates
    {
        static readonly ConcurrentDictionary<(Type Type, MethodInfo Method), Delegate> kept = new();

        /// How many delegates are kept (diagnostics, checks).
        public static int Count => kept.Count;

        /// The delegate native code gets for `d`: for a single delegate to a static method, the
        /// first one of its type and method, kept alive; otherwise `d`.
        public static Delegate? Keep(Delegate? d)
        {
            if (d is null || d.Target is not null || d.HasSingleTarget is false) return d;
            var method = d.Method;
            // A delegate over a native function pointer (GetDelegateForFunctionPointer) passes
            // that pointer itself.
            if (method.DeclaringType is null || method.DeclaringType == d.GetType()) return d;
            return kept.GetOrAdd((d.GetType(), method), d);
        }

        /// Marshal.GetFunctionPointerForDelegate(Delegate).
        public static IntPtr GetFunctionPointerForDelegate(Delegate d) => Marshal.GetFunctionPointerForDelegate(Keep(d)!);

        /// Marshal.GetFunctionPointerForDelegate&lt;T&gt;(T).
        public static IntPtr GetFunctionPointerForDelegate<T>(T d) where T : notnull
            => Marshal.GetFunctionPointerForDelegate(d is Delegate x ? (T)(object)Keep(x)! : d);

        /// Whether the image may pass a delegate to native code: it declares a P/Invoke, or refers
        /// to Marshal.GetFunctionPointerForDelegate. A cheap filter before the rewrite.
        internal static bool MayMarshal(byte[] image)
        {
            try
            {
                using var pe = new PEReader(new MemoryStream(image));
                if (!pe.HasMetadata) return false;
                var reader = pe.GetMetadataReader();
                foreach (var handle in reader.MethodDefinitions)
                    if ((reader.GetMethodDefinition(handle).Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0) return true;
                foreach (var handle in reader.MemberReferences)
                    if (reader.StringComparer.Equals(reader.GetMemberReference(handle).Name, nameof(Marshal.GetFunctionPointerForDelegate))) return true;
            }
            catch (BadImageFormatException)
            {
                // Not an assembly: the load reports it.
            }
            return false;
        }

        static bool IsDelegate(Cecil.TypeReference type)
        {
            if (type.IsGenericParameter || type.IsArray || type.IsByReference || type.IsPointer || type.IsValueType) return false;
            try
            {
                for (var t = type.Resolve(); t != null; t = t.BaseType?.Resolve())
                    if (t.FullName is "System.MulticastDelegate" or "System.Delegate") return t != type.Resolve();
            }
            catch (Cecil.AssemblyResolutionException)
            {
            }
            return false;
        }

        /// The P/Invoke a call reaches, when its declaration is at hand.
        static Cecil.MethodDefinition? PInvoke(Cecil.MethodReference callee)
        {
            try
            {
                var definition = callee.Resolve();
                return definition is { IsPInvokeImpl: true } ? definition : null;
            }
            catch (Cecil.AssemblyResolutionException)
            {
                return null;
            }
        }

        /// Sends the module's delegates for native code through Keep. Returns the calls changed.
        internal static int RewriteModule(Cecil.ModuleDefinition module)
        {
            Cecil.MethodReference? keep = null, pointer = null, pointerOf = null;
            int changed = 0;
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    int before = changed;
                    var instructions = method.Body.Instructions;
                    for (int i = 0; i < instructions.Count; i++)
                    {
                        var call = instructions[i];
                        if (call.OpCode.Code is not (Cil.Code.Call or Cil.Code.Callvirt) || call.Operand is not Cecil.MethodReference callee) continue;
                        if (callee.DeclaringType.FullName == "System.Runtime.InteropServices.Marshal" && callee.Name == nameof(Marshal.GetFunctionPointerForDelegate)
                            && callee.Parameters.Count == 1)
                        {
                            if (callee is Cecil.GenericInstanceMethod generic)
                            {
                                pointerOf ??= module.ImportReference(typeof(MonoMarshalledDelegates).GetMethods()
                                    .First(m => m.Name == nameof(GetFunctionPointerForDelegate) && m.IsGenericMethodDefinition));
                                var instance = new Cecil.GenericInstanceMethod(pointerOf);
                                instance.GenericArguments.Add(generic.GenericArguments[0]);
                                call.Operand = instance;
                            }
                            else call.Operand = pointer ??= module.ImportReference(typeof(MonoMarshalledDelegates).GetMethod(nameof(GetFunctionPointerForDelegate), [typeof(Delegate)]));
                            call.OpCode = Cil.OpCodes.Call;
                            changed++;
                            continue;
                        }
                        if (PInvoke(callee) is not { } target) continue;
                        int first = -1;
                        for (int p = 0; p < target.Parameters.Count; p++)
                            if (IsDelegate(target.Parameters[p].ParameterType))
                            {
                                first = p;
                                break;
                            }
                        if (first < 0) continue;
                        keep ??= module.ImportReference(typeof(MonoMarshalledDelegates).GetMethod(nameof(Keep)));
                        i += Insert(method.Body, call, target, first, keep);
                        changed++;
                    }
                    if (changed > before) CilBranches.Widen(method.Body);
                }
            return changed;
        }

        /// Before the call: the arguments from `first` on are stored in new locals, and loaded
        /// again, each delegate through Keep (cast back to its type). The call's instruction
        /// becomes the first of them, so branches to the call reach them. Returns the
        /// instructions added.
        static int Insert(Cil.MethodBody body, Cil.Instruction call, Cecil.MethodDefinition target, int first, Cecil.MethodReference keep)
        {
            var module = body.Method.Module;
            var added = new List<Cil.Instruction>();
            var locals = new Cil.VariableDefinition[target.Parameters.Count];
            for (int p = target.Parameters.Count - 1; p >= first; p--)
            {
                locals[p] = new Cil.VariableDefinition(module.ImportReference(target.Parameters[p].ParameterType));
                body.Variables.Add(locals[p]);
                added.Add(Cil.Instruction.Create(Cil.OpCodes.Stloc, locals[p]));
            }
            for (int p = first; p < target.Parameters.Count; p++)
            {
                added.Add(Cil.Instruction.Create(Cil.OpCodes.Ldloc, locals[p]));
                var type = target.Parameters[p].ParameterType;
                if (!IsDelegate(type)) continue;
                added.Add(Cil.Instruction.Create(Cil.OpCodes.Call, keep));
                added.Add(Cil.Instruction.Create(Cil.OpCodes.Castclass, module.ImportReference(type)));
            }
            var original = Cil.Instruction.Create(call.OpCode, (Cecil.MethodReference)call.Operand);
            call.OpCode = added[0].OpCode;
            call.Operand = added[0].Operand;
            var il = body.GetILProcessor();
            var at = call;
            for (int k = 1; k < added.Count; k++)
            {
                il.InsertAfter(at, added[k]);
                at = added[k];
            }
            il.InsertAfter(at, original);
            return added.Count;
        }
    }
}
