using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using OniMoono.Engine;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Host
{
    /// ARCHITECTURE D29. Mono converts a float to an integer with the processor's truncating
    /// conversion, which gives the target's minimum for NaN, the infinities and values out of range
    /// (Attribute Info Screen shows "Xp:0/-2147483648" for the bionic Duplicant, whose experience
    /// for the next level is NaN); CoreCLR saturates, and NaN gives 0. The unchecked conversions
    /// of a float in the game's assemblies and in mods (conv.i1 … conv.u8, conv.i, conv.u whose
    /// operand is a float32 or float64, found by following the IL's stack types) call these
    /// instead, which convert as Mono does (OniMoono.Engine.MonoConvert). An assembly that cannot
    /// be rewritten loads unchanged. The same pass sends the assembly's directory listings to
    /// MonoDirectoryListing (D30) and its calls of Thread.Abort to MonoThreadAbort (D47), and the
    /// game's rewritten images are cached with them.
    public static class MonoConversions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static sbyte ToSByte(double x) => MonoConvert.ToSByte(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static short ToInt16(double x) => MonoConvert.ToInt16(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static int ToInt32(double x) => MonoConvert.ToInt32(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static long ToInt64(double x) => MonoConvert.ToInt64(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static byte ToByte(double x) => MonoConvert.ToByte(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ushort ToUInt16(double x) => MonoConvert.ToUInt16(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint ToUInt32(double x) => MonoConvert.ToUInt32(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong ToUInt64(double x) => MonoConvert.ToUInt64(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static nint ToIntPtr(double x) => MonoConvert.ToIntPtr(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static nuint ToUIntPtr(double x) => MonoConvert.ToUIntPtr(x);

        /// The helper replacing each unchecked conversion, by its opcode.
        static readonly Dictionary<Cil.Code, string> Helpers = new()
        {
            [Cil.Code.Conv_I1] = nameof(ToSByte), [Cil.Code.Conv_I2] = nameof(ToInt16), [Cil.Code.Conv_I4] = nameof(ToInt32),
            [Cil.Code.Conv_I8] = nameof(ToInt64), [Cil.Code.Conv_U1] = nameof(ToByte), [Cil.Code.Conv_U2] = nameof(ToUInt16),
            [Cil.Code.Conv_U4] = nameof(ToUInt32), [Cil.Code.Conv_U8] = nameof(ToUInt64), [Cil.Code.Conv_I] = nameof(ToIntPtr),
            [Cil.Code.Conv_U] = nameof(ToUIntPtr),
        };

        /// The image with its conversions of floats calling the helpers, or null when it has none.
        /// `symbols` (portable PDB bytes or null) are rewritten to match.
        internal static byte[]? Adjust(byte[] image, ref byte[]? symbols, string path)
        {
            if (!MayConvertFloats(image) && !MonoDirectoryListing.MayList(image) && !MonoModHelperDrop.MayDrop(image) && !MonoThreadAbort.MayAbort(image)
                && !(NavGridLinksRewrite.Enabled && NavGridLinksRewrite.MayTouch(image))) return null;
            try
            {
                return Rewrite(image, ref symbols, path);
            }
            catch (Exception e) when (e is Cecil.AssemblyResolutionException or BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                EngineLog.Engine($"mod conversions: {Path.GetFileName(path)} not rewritten ({e.GetType().Name}: {e.Message}); it loads unchanged");
                return null;
            }
        }

        /// Whether the image's IL could convert a float: it has a float constant, or a member or
        /// local whose signature names float32 or float64, and an unchecked conversion. A cheap
        /// filter over the raw image: the type codes (0x0C, 0x0D) and opcodes are single bytes,
        /// so a false positive costs only the analysis.
        static bool MayConvertFloats(byte[] image)
        {
            bool conv = false, real = false;
            foreach (byte b in image)
            {
                if (b is 0x67 or 0x68 or 0x69 or 0x6A or 0xD2 or 0xD1 or 0x6D or 0x6E or 0xD3 or 0xE0) conv = true;
                else if (b is 0x0C or 0x0D) real = true;
                if (conv && real) return true;
            }
            return false;
        }

        /// A game assembly's image with its conversions of floats calling the helpers, or null when
        /// it has none. Rewriting Assembly-CSharp takes about two seconds, so the result is kept in
        /// the player's cache folder, under the image's hash and this host build's.
        internal static byte[]? AdjustGame(byte[] image, string path)
        {
            if (!MayConvertFloats(image) && !MonoDirectoryListing.MayList(image) && !MonoModHelperDrop.MayDrop(image) && !MonoThreadAbort.MayAbort(image)
                && !(NavGridLinksRewrite.Enabled && NavGridLinksRewrite.MayTouch(image))) return null;
            string? cached = null;
            try
            {
                var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image))[..32]
                          + typeof(MonoConversions).Module.ModuleVersionId.ToString("N")[..16]
                          // compact-nav-links rewrites the image too.
                          + (NavGridLinksRewrite.Enabled ? "-links" : "");
                cached = Path.Combine(EngineConfig.TemporaryCachePath, "oni-moono", "conversions", Path.GetFileNameWithoutExtension(path) + "-" + key + ".dll");
                if (File.Exists(cached))
                {
                    var bytes = File.ReadAllBytes(cached);
                    return bytes.Length == 0 ? null : bytes;  // empty: nothing to rewrite
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                cached = null;
            }
            byte[]? none = null;
            byte[]? result;
            try
            {
                result = Rewrite(image, ref none, path, "game conversions");
            }
            catch (Exception e) when (e is Cecil.AssemblyResolutionException or BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                EngineLog.Engine($"game conversions: {Path.GetFileName(path)} not rewritten ({e.GetType().Name}: {e.Message}); it loads unchanged");
                return null;
            }
            if (cached != null)
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
                    var temporary = cached + "." + Environment.ProcessId + ".tmp";
                    File.WriteAllBytes(temporary, result ?? []);
                    File.Move(temporary, cached, overwrite: true);
                    DeleteOldCache();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Not kept: the next start rewrites it again.
                }
            return result;
        }

        static bool oldCacheDeleted;

        /// The cache folder of players from before the Moono name: its images name an older
        /// host build, so none of them is read again.
        static void DeleteOldCache()
        {
            if (oldCacheDeleted) return;
            oldCacheDeleted = true;
            try { Directory.Delete(Path.Combine(EngineConfig.TemporaryCachePath, "OniBrutal", "conversions"), recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        static byte[]? Rewrite(byte[] image, ref byte[]? symbols, string path, string what = "mod conversions")
        {
            using var resolver = LoadedFirstResolver.ForMod(path);
            bool withSymbols = symbols != null;
            Cecil.ModuleDefinition module;
            try
            {
                module = Cecil.ModuleDefinition.ReadModule(new MemoryStream(image), new Cecil.ReaderParameters
                {
                    AssemblyResolver = resolver,
                    ReadSymbols = withSymbols,
                    SymbolStream = withSymbols ? new MemoryStream(symbols!) : null,
                    SymbolReaderProvider = withSymbols ? new Cil.PortablePdbReaderProvider() : null,
                });
            }
            catch (Exception e) when (withSymbols && e is Cil.SymbolsNotMatchingException or Cil.SymbolsNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                withSymbols = false;
                module = Cecil.ModuleDefinition.ReadModule(new MemoryStream(image), new Cecil.ReaderParameters { AssemblyResolver = resolver });
            }
            using var owned = module;
            // compact-nav-links: the accesses of NavGrid's links go through its helpers (first,
            // while the hot walks' IL is still the game's).
            var linkLog = new List<string>();
            int links = NavGridLinksRewrite.Enabled
                ? NavGridLinksRewrite.RewriteModule(module, what == "game conversions", Path.GetFileName(path), linkLog) : 0;
            foreach (var line in linkLog) EngineLog.Engine(line);
            int converted = RewriteModule(module, out int methods, out int unsure);
            // The same pass sends its directory listings to MonoDirectoryListing (D30).
            int listings = MonoDirectoryListing.RewriteModule(module);
            // And MonoMod's native helper is written through a descriptor closed once (D31).
            int drops = MonoModHelperDrop.RewriteModule(module);
            // And its calls of Thread.Abort abort as Mono's do (D47).
            int aborts = MonoThreadAbort.RewriteModule(module, out int catchEnds);
            if (converted == 0 && listings == 0 && drops == 0 && aborts == 0 && links == 0) return null;
            using var output = new MemoryStream();
            using var symbolOutput = new MemoryStream();
            module.Write(output, new Cecil.WriterParameters { WriteSymbols = withSymbols, SymbolStream = withSymbols ? symbolOutput : null, SymbolWriterProvider = withSymbols ? new Cil.PortablePdbWriterProvider() : null });
            symbols = withSymbols ? symbolOutput.ToArray() : null;
            if (converted > 0)
                EngineLog.Engine($"{what}: {Path.GetFileName(path)}: {converted} conversion(s) of floats in {methods} method(s) convert as on Mono" + (unsure > 0 ? $"; {unsure} left as they were (stack types not followed)" : ""));
            if (listings > 0)
                EngineLog.Engine($"{what.Replace("conversions", "listings")}: {Path.GetFileName(path)}: {listings} directory listing(s) in Mono's order");
            if (drops > 0)
                EngineLog.Engine($"{what.Replace("conversions", "fixes")}: {Path.GetFileName(path)}: MonoMod's native helper file is closed once");
            if (aborts > 0)
                EngineLog.Engine($"{what.Replace("conversions", "aborts")}: {Path.GetFileName(path)}: {aborts} call(s) of Thread.Abort or ResetAbort abort as on Mono"
                                 + (catchEnds > 0 ? $"; {catchEnds} catch exit(s) raise the abort again" : ""));
            return output.ToArray();
        }

        /// Rewrites every method of the module; returns the conversions replaced.
        internal static int RewriteModule(Cecil.ModuleDefinition module, out int methods, out int unsure)
        {
            var references = new Dictionary<Cil.Code, Cecil.MethodReference>();
            Cecil.MethodReference Helper(Cil.Code code)
            {
                if (!references.TryGetValue(code, out var reference))
                    references[code] = reference = module.ImportReference(typeof(MonoConversions).GetMethod(Helpers[code])!);
                return reference;
            }
            int converted = 0;
            methods = unsure = 0;
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    var floats = FloatConversions(method, out int notFollowed);
                    unsure += notFollowed;
                    if (floats.Count == 0) continue;
                    foreach (var instruction in floats)
                    {
                        var helper = Helper(instruction.OpCode.Code);
                        instruction.OpCode = Cil.OpCodes.Call;
                        instruction.Operand = helper;
                    }
                    WidenBranches(method.Body);
                    converted += floats.Count;
                    methods++;
                }
            return converted;
        }

        /// Short branches that the longer calls put out of reach become long ones.
        internal static void WidenBranches(Cil.MethodBody body)
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

        /// What an evaluation stack slot holds, as far as conversions care.
        enum Kind : byte { Other, Float, Mixed }

        /// The method's unchecked conversions whose operand is a float, by following the types on
        /// the evaluation stack through its IL (every path, exception handlers included).
        /// `notFollowed` counts conversions left alone: where paths that join disagree, or the
        /// method's IL could not be followed.
        internal static List<Cil.Instruction> FloatConversions(Cecil.MethodDefinition method, out int notFollowed)
        {
            notFollowed = 0;
            var result = new List<Cil.Instruction>();
            var body = method.Body;
            var instructions = body.Instructions;
            bool any = false;
            foreach (var instruction in instructions)
                if (Helpers.ContainsKey(instruction.OpCode.Code)) { any = true; break; }
            if (!any) return result;
            var index = new Dictionary<Cil.Instruction, int>(instructions.Count);
            for (int i = 0; i < instructions.Count; i++) index[instructions[i]] = i;
            var entry = new Kind[instructions.Count][];
            var conversions = new Kind?[instructions.Count];
            var work = new Stack<int>();
            void Flow(int target, List<Kind> stack)
            {
                var existing = entry[target];
                if (existing == null)
                {
                    entry[target] = stack.ToArray();
                    work.Push(target);
                    return;
                }
                if (existing.Length != stack.Count) throw new InvalidOperationException("stack depths differ at a join");
                bool changed = false;
                for (int i = 0; i < existing.Length; i++)
                    if (existing[i] != stack[i] && existing[i] != Kind.Mixed)
                    {
                        existing[i] = Kind.Mixed;
                        changed = true;
                    }
                if (changed) work.Push(target);
            }
            try
            {
                Flow(0, new List<Kind>());
                foreach (var handler in body.ExceptionHandlers)
                {
                    var caught = new List<Kind>();
                    if (handler.HandlerType is Cil.ExceptionHandlerType.Catch or Cil.ExceptionHandlerType.Filter) caught.Add(Kind.Other);
                    Flow(index[handler.HandlerStart], caught);
                    if (handler.HandlerType == Cil.ExceptionHandlerType.Filter) Flow(index[handler.FilterStart], new List<Kind> { Kind.Other });
                }
                while (work.Count > 0)
                {
                    int at = work.Pop();
                    var stack = new List<Kind>(entry[at]);
                    while (true)
                    {
                        var instruction = instructions[at];
                        Step(method, instruction, stack, conversions, at);
                        var op = instruction.OpCode;
                        bool falls = op.FlowControl is not (Cil.FlowControl.Branch or Cil.FlowControl.Return or Cil.FlowControl.Throw)
                                     && op.Code is not (Cil.Code.Endfinally or Cil.Code.Endfilter or Cil.Code.Jmp);
                        if (op.Code is Cil.Code.Leave or Cil.Code.Leave_S) stack.Clear();
                        if (instruction.Operand is Cil.Instruction target) Flow(index[target], stack);
                        else if (instruction.Operand is Cil.Instruction[] targets)
                            foreach (var t in targets) Flow(index[t], stack);
                        if (!falls) break;
                        if (++at >= instructions.Count) throw new InvalidOperationException("IL runs off the end");
                        if (entry[at] != null)
                        {
                            Flow(at, stack);
                            break;
                        }
                        entry[at] = stack.ToArray();
                    }
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentOutOfRangeException or KeyNotFoundException or NotSupportedException)
            {
                foreach (var instruction in instructions)
                    if (Helpers.ContainsKey(instruction.OpCode.Code)) notFollowed++;
                return result;
            }
            for (int i = 0; i < instructions.Count; i++)
            {
                if (conversions[i] == Kind.Float) result.Add(instructions[i]);
                else if (conversions[i] == Kind.Mixed) notFollowed++;
            }
            return result;
        }

        static Kind Pop(List<Kind> stack)
        {
            if (stack.Count == 0) throw new InvalidOperationException("stack underflow");
            var kind = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return kind;
        }

        static void Pop(List<Kind> stack, int count)
        {
            for (int i = 0; i < count; i++) Pop(stack);
        }

        /// One instruction's effect on the stack's kinds; a conversion's operand is recorded.
        static void Step(Cecil.MethodDefinition method, Cil.Instruction instruction, List<Kind> stack, Kind?[] conversions, int at)
        {
            var op = instruction.OpCode;
            switch (op.Code)
            {
                case Cil.Code.Conv_I1 or Cil.Code.Conv_I2 or Cil.Code.Conv_I4 or Cil.Code.Conv_I8 or Cil.Code.Conv_U1 or Cil.Code.Conv_U2
                    or Cil.Code.Conv_U4 or Cil.Code.Conv_U8 or Cil.Code.Conv_I or Cil.Code.Conv_U:
                {
                    var operand = Pop(stack);
                    // Paths may reach one conversion with different kinds: it is then left alone.
                    conversions[at] = conversions[at] is { } seen && seen != operand ? Kind.Mixed : operand;
                    stack.Add(Kind.Other);
                    return;
                }
                case Cil.Code.Ldarg_0 or Cil.Code.Ldarg_1 or Cil.Code.Ldarg_2 or Cil.Code.Ldarg_3:
                    stack.Add(Argument(method, op.Code - Cil.Code.Ldarg_0));
                    return;
                case Cil.Code.Ldarg_S or Cil.Code.Ldarg:
                    stack.Add(instruction.Operand is Cecil.ParameterDefinition p ? Argument(method, p.Index + (method.HasThis ? 1 : 0)) : throw new InvalidOperationException("ldarg operand"));
                    return;
                case Cil.Code.Ldloc_0 or Cil.Code.Ldloc_1 or Cil.Code.Ldloc_2 or Cil.Code.Ldloc_3:
                    stack.Add(KindOf(method.Body.Variables[op.Code - Cil.Code.Ldloc_0].VariableType, null, null));
                    return;
                case Cil.Code.Ldloc_S or Cil.Code.Ldloc:
                    stack.Add(KindOf(((Cil.VariableDefinition)instruction.Operand).VariableType, null, null));
                    return;
                case Cil.Code.Ldfld:
                    Pop(stack);
                    stack.Add(FieldKind((Cecil.FieldReference)instruction.Operand));
                    return;
                case Cil.Code.Ldsfld:
                    stack.Add(FieldKind((Cecil.FieldReference)instruction.Operand));
                    return;
                case Cil.Code.Ldobj or Cil.Code.Unbox_Any:
                    Pop(stack);
                    stack.Add(KindOf((Cecil.TypeReference)instruction.Operand, null, null));
                    return;
                case Cil.Code.Ldelem_Any:
                    Pop(stack, 2);
                    stack.Add(KindOf((Cecil.TypeReference)instruction.Operand, null, null));
                    return;
                case Cil.Code.Dup:
                {
                    var top = Pop(stack);
                    stack.Add(top);
                    stack.Add(top);
                    return;
                }
                case Cil.Code.Call or Cil.Code.Callvirt or Cil.Code.Newobj:
                {
                    var callee = (Cecil.MethodReference)instruction.Operand;
                    Pop(stack, callee.Parameters.Count + (callee.HasThis && op.Code != Cil.Code.Newobj ? 1 : 0));
                    if (op.Code == Cil.Code.Newobj) stack.Add(Kind.Other);
                    else if (callee.ReturnType.MetadataType != Cecil.MetadataType.Void) stack.Add(ReturnKind(callee));
                    return;
                }
                case Cil.Code.Calli:
                {
                    var site = (Cecil.CallSite)instruction.Operand;
                    Pop(stack, site.Parameters.Count + (site.HasThis ? 1 : 0) + 1);
                    if (site.ReturnType.MetadataType != Cecil.MetadataType.Void) stack.Add(KindOf(site.ReturnType, null, null));
                    return;
                }
                case Cil.Code.Ret:
                    if (method.ReturnType.MetadataType != Cecil.MetadataType.Void) Pop(stack);
                    return;
                case Cil.Code.Add or Cil.Code.Sub or Cil.Code.Mul or Cil.Code.Div or Cil.Code.Rem:
                {
                    // Float arithmetic has float operands only; the kind follows the first.
                    Pop(stack);
                    stack.Add(Pop(stack));
                    return;
                }
                case Cil.Code.Neg:
                    stack.Add(Pop(stack));
                    return;
                case Cil.Code.Ckfinite:
                    Pop(stack);
                    stack.Add(Kind.Float);
                    return;
            }
            // The rest by their stack behaviour: what they push is never a float unless Cecil
            // says so (ldc.r4/r8, ldind.r4/r8, ldelem.r4/r8, conv.r4/r8/r.un).
            Pop(stack, op.StackBehaviourPop switch
            {
                Cil.StackBehaviour.Pop0 => 0,
                Cil.StackBehaviour.Pop1 or Cil.StackBehaviour.Popi or Cil.StackBehaviour.Popref => 1,
                Cil.StackBehaviour.Pop1_pop1 or Cil.StackBehaviour.Popi_pop1 or Cil.StackBehaviour.Popi_popi or Cil.StackBehaviour.Popi_popi8
                    or Cil.StackBehaviour.Popi_popr4 or Cil.StackBehaviour.Popi_popr8 or Cil.StackBehaviour.Popref_pop1 or Cil.StackBehaviour.Popref_popi => 2,
                Cil.StackBehaviour.Popi_popi_popi or Cil.StackBehaviour.Popref_popi_popi or Cil.StackBehaviour.Popref_popi_popi8
                    or Cil.StackBehaviour.Popref_popi_popr4 or Cil.StackBehaviour.Popref_popi_popr8 or Cil.StackBehaviour.Popref_popi_popref => 3,
                Cil.StackBehaviour.PopAll => stack.Count,
                _ => throw new NotSupportedException($"stack behaviour of {op}"),
            });
            switch (op.StackBehaviourPush)
            {
                case Cil.StackBehaviour.Push0:
                    break;
                case Cil.StackBehaviour.Pushr4 or Cil.StackBehaviour.Pushr8:
                    stack.Add(Kind.Float);
                    break;
                case Cil.StackBehaviour.Push1 or Cil.StackBehaviour.Pushi or Cil.StackBehaviour.Pushi8 or Cil.StackBehaviour.Pushref:
                    stack.Add(Kind.Other);
                    break;
                case Cil.StackBehaviour.Push1_push1:
                    stack.Add(Kind.Other);
                    stack.Add(Kind.Other);
                    break;
                default:
                    throw new NotSupportedException($"stack behaviour of {op}");
            }
        }

        static Kind Argument(Cecil.MethodDefinition method, int slot)
        {
            if (method.HasThis)
            {
                if (slot == 0) return Kind.Other;
                slot--;
            }
            return KindOf(method.Parameters[slot].ParameterType, null, null);
        }

        static Kind FieldKind(Cecil.FieldReference field) => KindOf(field.FieldType, field.DeclaringType as Cecil.GenericInstanceType, null);

        static Kind ReturnKind(Cecil.MethodReference callee) =>
            KindOf(callee.ReturnType, callee.DeclaringType as Cecil.GenericInstanceType, callee as Cecil.GenericInstanceMethod);

        /// Float for float32 and float64 (a generic parameter by the instantiation that names it).
        static Kind KindOf(Cecil.TypeReference type, Cecil.GenericInstanceType? owner, Cecil.GenericInstanceMethod? method)
        {
            while (true)
            {
                switch (type)
                {
                    case Cecil.RequiredModifierType or Cecil.OptionalModifierType or Cecil.PinnedType:
                        type = ((Cecil.TypeSpecification)type).ElementType;
                        continue;
                    case Cecil.GenericParameter parameter:
                        if (parameter.Type == Cecil.GenericParameterType.Type && owner != null && parameter.Position < owner.GenericArguments.Count)
                        {
                            type = owner.GenericArguments[parameter.Position];
                            owner = null;
                            continue;
                        }
                        if (parameter.Type == Cecil.GenericParameterType.Method && method != null && parameter.Position < method.GenericArguments.Count)
                        {
                            type = method.GenericArguments[parameter.Position];
                            method = null;
                            continue;
                        }
                        return Kind.Other;
                }
                return type.MetadataType is Cecil.MetadataType.Single or Cecil.MetadataType.Double ? Kind.Float : Kind.Other;
            }
        }
    }
}
