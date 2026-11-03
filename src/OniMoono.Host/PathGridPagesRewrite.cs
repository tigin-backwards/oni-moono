#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Host
{
    /// The load-time half of `paged-path-grids` (PathGridPages), run in MonoConversions' pass over
    /// the game's assemblies and mods. PathGrid's `Cells` and `ProberCells` are private, so the
    /// game's accesses are PathGrid's own (U59 744825 and U60 761211 alike: the constructor's
    /// `newarr`, GetCell, GetProberCell, SetCell, GetCost, GetCostIgnoreProberOffset and
    /// ResetProberCells). Each gets a page table and a length beside the array field
    /// (`<Cells>pages`, `<Cells>length`, the same for ProberCells), and each access of the array
    /// read from the field:
    ///
    /// | IL on `ldfld Cells` (or ProberCells)        | becomes                                     |
    /// |---------------------------------------------|---------------------------------------------|
    /// | `ldelem.any T`                              | `ldfld <Cells>pages` … `call Load<T>`       |
    /// | `stelem.any T`                              | `ldfld <Cells>pages` … `call Store<T>`      |
    /// | `ldelema T` read by `ldfld` / `ldobj`       | `call Load<T>` (then the same `ldfld`; `ldobj` dropped) |
    /// | `ldelema T` + `initobj T`                   | `call Clear<T>`                             |
    /// | `ldelema T` … `stobj T`                     | `call Store<T>`                             |
    /// | `ldlen`                                     | `ldfld <Cells>length`                       |
    /// | `newarr T` + `stfld Cells` (constructor)    | `call New<T>`, the table, length and Handle into the fields |
    ///
    /// The array's value is followed from its `ldfld` to the instruction that takes it within one
    /// run of straight-line code; anything else (an access across a branch, the array passed on or
    /// stored, the element's reference used otherwise) is a problem and leaves the game's assembly
    /// as it is (no tables: the tweak is off). Every other instruction, the locals and the branches
    /// stay; a transpiler that runs later sees the helpers.
    ///
    /// Mods are not rewritten: one reading `Cells` or `ProberCells` (a publicized assembly) may only
    /// lock on it, as Fast Track's `lock (grid.Cells)` does, which the one-element stand-in keeps.
    /// Any other use (none in the 3,547 Workshop DLLs and Fast Track) is a problem that makes the
    /// session flat (PagedPathGrids).
    internal static class PathGridPagesRewrite
    {
        /// The tweak's switch (Program sets it before any assembly loads).
        internal static bool Enabled { get; set; }

        /// What makes the session flat: a mod's access to the arrays other than a lock.
        internal static readonly List<string> Problems = new();

        /// The assemblies analysed, by simple name (the mode decision checks every loaded
        /// assembly that names the fields is among them).
        internal static readonly HashSet<string> Analysed = new(StringComparer.Ordinal);

        internal static readonly string[] Arrays = ["Cells", "ProberCells"];

        /// The added fields' names.
        internal static string PagesField(string array) => $"<{array}>pages";
        internal static string LengthField(string array) => $"<{array}>length";

        /// Whether the image can name PathGrid ("PathGrid" in its strings).
        internal static bool MayTouch(byte[] image) => image.AsSpan().IndexOf("PathGrid"u8) >= 0;

        /// The module's accesses rewritten; returns how many. `game`: Assembly-CSharp.
        internal static int RewriteModule(Cecil.ModuleDefinition module, bool game, string file, List<string> log)
        {
            var name = module.Assembly.Name.Name;
            if (!game)
            {
                ScanMod(module, file, log);
                lock (Analysed) Analysed.Add(name);
                return 0;
            }
            var pathGrid = module.GetType("PathGrid");
            if (pathGrid == null) return 0;
            var fields = new Dictionary<string, Cecil.FieldDefinition>();
            foreach (var array in Arrays)
            {
                var field = pathGrid.Fields.FirstOrDefault(f => f.Name == array && !f.IsStatic && f.FieldType is Cecil.ArrayType { IsVector: true });
                if (field == null)
                {
                    log.Add($"path grids: {file}: PathGrid has no array {array}; its grids keep their arrays");
                    return 0;
                }
                fields[array] = field;
            }
            var plans = new List<(Cecil.MethodDefinition Method, List<Site> Sites)>();
            var problems = new List<string>();
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    List<Site>? sites = null;
                    foreach (var i in method.Body.Instructions)
                    {
                        if (i.Operand is not Cecil.FieldReference f || f.DeclaringType.FullName != "PathGrid" || !fields.TryGetValue(f.Name, out var field)) continue;
                        var site = Plan(method, i, field, out string? problem);
                        if (problem != null) problems.Add($"{method.DeclaringType.FullName}::{method.Name} IL_{i.Offset:x4}: {problem}");
                        else (sites ??= new()).Add(site!);
                    }
                    if (sites != null) plans.Add((method, sites));
                }
            if (problems.Count > 0)
            {
                foreach (var p in problems.Take(5)) log.Add($"path grids: {file}: {p}");
                log.Add($"path grids: {file}: not rewritten ({problems.Count} access(es) the pages cannot follow); the grids keep their arrays");
                return 0;
            }
            if (!plans.Any(p => p.Sites.Any(s => s.Kind == SiteKind.Construct)))
            {
                log.Add($"path grids: {file}: PathGrid's constructor makes no array; the grids keep their arrays");
                return 0;
            }
            // The fields beside each array: its page table and its length.
            var added = new Dictionary<string, (Cecil.FieldDefinition Pages, Cecil.FieldDefinition Length, Helpers Helpers)>();
            foreach (var (array, field) in fields)
            {
                var element = ((Cecil.ArrayType)field.FieldType).ElementType;
                var pages = new Cecil.FieldDefinition(PagesField(array), Cecil.FieldAttributes.Private, new Cecil.ArrayType(new Cecil.ArrayType(element)));
                var length = new Cecil.FieldDefinition(LengthField(array), Cecil.FieldAttributes.Private, module.TypeSystem.Int32);
                pathGrid.Fields.Add(pages);
                pathGrid.Fields.Add(length);
                added[array] = (pages, length, new Helpers(module, element));
            }
            int count = 0;
            foreach (var (method, sites) in plans)
            {
                var il = method.Body.GetILProcessor();
                foreach (var site in sites)
                {
                    var (pages, length, h) = added[site.Field.Name];
                    switch (site.Kind)
                    {
                        case SiteKind.Load:
                            site.Load.Operand = pages;
                            Replace(site.Consumer!, Cil.OpCodes.Call, h.Load);
                            break;
                        case SiteKind.Store:
                            site.Load.Operand = pages;
                            Replace(site.Consumer!, Cil.OpCodes.Call, h.Store);
                            break;
                        case SiteKind.ReadThrough:
                            site.Load.Operand = pages;
                            Replace(site.Consumer!, Cil.OpCodes.Call, h.Load);
                            if (site.Then!.OpCode.Code == Cil.Code.Ldobj) Replace(site.Then, Cil.OpCodes.Nop, null);
                            break;
                        case SiteKind.Clear:
                            site.Load.Operand = pages;
                            Replace(site.Consumer!, Cil.OpCodes.Call, h.Clear);
                            Replace(site.Then!, Cil.OpCodes.Nop, null);
                            break;
                        case SiteKind.StoreThrough:
                            site.Load.Operand = pages;
                            Replace(site.Consumer!, Cil.OpCodes.Nop, null);
                            Replace(site.Then!, Cil.OpCodes.Call, h.Store);
                            break;
                        case SiteKind.Length:
                            site.Load.Operand = length;
                            Replace(site.Consumer!, Cil.OpCodes.Nop, null);
                            break;
                        case SiteKind.Construct:
                        {
                            // [this, length] newarr → [this, pages]; the table and its length into
                            // their fields, the array field gets the Handle.
                            var newarr = site.Consumer!;
                            var table = new Cil.VariableDefinition(pages.FieldType);
                            method.Body.Variables.Add(table);
                            Replace(newarr, Cil.OpCodes.Call, h.New);
                            var at = newarr;
                            foreach (var add in new[]
                                     {
                                         il.Create(Cil.OpCodes.Stloc, table), il.Create(Cil.OpCodes.Dup), il.Create(Cil.OpCodes.Ldloc, table), il.Create(Cil.OpCodes.Stfld, pages),
                                         il.Create(Cil.OpCodes.Dup), il.Create(Cil.OpCodes.Ldloc, table), il.Create(Cil.OpCodes.Call, h.Length), il.Create(Cil.OpCodes.Stfld, length),
                                         il.Create(Cil.OpCodes.Ldloc, table), il.Create(Cil.OpCodes.Call, h.Handle),
                                     })
                            {
                                il.InsertAfter(at, add);
                                at = add;
                            }
                            break;
                        }
                    }
                    count++;
                }
                CilBranches.Widen(method.Body);
            }
            log.Add($"path grids: {file}: {count} access(es) of PathGrid's Cells and ProberCells in {plans.Count} method(s) go through page tables");
            return count;
        }

        static void Replace(Cil.Instruction i, Cil.OpCode op, object? operand)
        {
            i.OpCode = op;
            i.Operand = operand;
        }

        enum SiteKind { Load, Store, ReadThrough, Clear, StoreThrough, Length, Construct }

        /// One access: the field instruction (`Load`, the `ldfld` or the constructor's `stfld`), the
        /// instruction that takes the array (`Consumer`; for Construct the `newarr`), and for an
        /// element's reference the instruction that takes it (`Then`).
        sealed record Site(SiteKind Kind, Cecil.FieldDefinition Field, Cil.Instruction Load, Cil.Instruction? Consumer, Cil.Instruction? Then);

        static Site? Plan(Cecil.MethodDefinition method, Cil.Instruction i, Cecil.FieldDefinition field, out string? problem)
        {
            problem = null;
            var element = ((Cecil.ArrayType)field.FieldType).ElementType;
            bool Is(Cil.Instruction x, Cil.Code code) => x.OpCode.Code == code && x.Operand is Cecil.TypeReference t && t.FullName == element.FullName;
            switch (i.OpCode.Code)
            {
                case Cil.Code.Stfld:
                    // `ldarg.0; <length>; newarr T; stfld F` in PathGrid's constructor.
                    if (method.IsConstructor && !method.IsStatic && method.DeclaringType.FullName == "PathGrid" && i.Previous is { } p && Is(p, Cil.Code.Newarr)
                        && !Targets(method.Body).Contains(i))
                        return new Site(SiteKind.Construct, field, i, p, null);
                    problem = $"{field.Name} written otherwise than with a new array in the constructor";
                    return null;
                case Cil.Code.Ldfld:
                    break;
                default:
                    problem = $"{field.Name} used by {i.OpCode.Name}";
                    return null;
            }
            var consumer = Consumer(method, i, out int depth);
            if (consumer == null)
            {
                problem = $"{field.Name}'s array is not taken in straight-line code";
                return null;
            }
            if (Is(consumer, Cil.Code.Ldelem_Any) && depth == 1) return new Site(SiteKind.Load, field, i, consumer, null);
            if (Is(consumer, Cil.Code.Stelem_Any) && depth == 2) return new Site(SiteKind.Store, field, i, consumer, null);
            if (consumer.OpCode.Code == Cil.Code.Ldlen && depth == 0) return new Site(SiteKind.Length, field, i, consumer, null);
            if (Is(consumer, Cil.Code.Ldelema) && depth == 1)
            {
                var then = Consumer(method, consumer, out int at);
                if (then != null && at == 0)
                {
                    if (Is(then, Cil.Code.Initobj)) return new Site(SiteKind.Clear, field, i, consumer, then);
                    if (Is(then, Cil.Code.Ldobj)) return new Site(SiteKind.ReadThrough, field, i, consumer, then);
                    if (then.OpCode.Code == Cil.Code.Ldfld && then.Operand is Cecil.FieldReference f && f.DeclaringType.FullName == element.FullName)
                        return new Site(SiteKind.ReadThrough, field, i, consumer, then);
                }
                if (then != null && at == 1 && Is(then, Cil.Code.Stobj)) return new Site(SiteKind.StoreThrough, field, i, consumer, then);
                problem = $"a reference to an element of {field.Name} is used by {then?.OpCode.Name ?? "code past a branch"}";
                return null;
            }
            problem = $"{field.Name}'s array is used by {consumer.OpCode.Name}";
            return null;
        }

        /// The instruction that pops the value `producer` pushes, following straight-line code only
        /// (null at a branch, a branch target, a handler's edge or the end), and how deep the value
        /// was then (0: the top of the stack).
        internal static Cil.Instruction? Consumer(Cecil.MethodDefinition method, Cil.Instruction producer, out int depth)
        {
            depth = 0;
            var targets = Targets(method.Body);
            for (var i = producer.Next; i != null; i = i.Next)
            {
                if (targets.Contains(i)) return null;
                if (!Effect(method, i, out int pops, out int pushes)) return null;
                if (pops > depth) return i;
                depth = depth - pops + pushes;
                var flow = i.OpCode.FlowControl;
                if (flow is Cil.FlowControl.Branch or Cil.FlowControl.Cond_Branch or Cil.FlowControl.Return or Cil.FlowControl.Throw) return null;
            }
            return null;
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Cil.MethodBody, HashSet<Cil.Instruction>> targetCache = new();

        /// Every instruction reached otherwise than by falling through to it: branch targets and
        /// the edges of try blocks, handlers and filters.
        static HashSet<Cil.Instruction> Targets(Cil.MethodBody body) => targetCache.GetValue(body, b =>
        {
            var set = new HashSet<Cil.Instruction>();
            foreach (var i in b.Instructions)
                switch (i.Operand)
                {
                    case Cil.Instruction t: set.Add(t); break;
                    case Cil.Instruction[] ts: foreach (var t in ts) set.Add(t); break;
                }
            foreach (var h in b.ExceptionHandlers)
                foreach (var edge in new[] { h.TryStart, h.TryEnd, h.HandlerStart, h.HandlerEnd, h.FilterStart })
                    if (edge != null) set.Add(edge);
            return set;
        });

        /// How many values the instruction pops and pushes; false when it cannot be said (`leave`,
        /// which empties the stack).
        static bool Effect(Cecil.MethodDefinition method, Cil.Instruction i, out int pops, out int pushes)
        {
            var op = i.OpCode;
            pops = pushes = 0;
            switch (op.StackBehaviourPop)
            {
                case Cil.StackBehaviour.Pop0: break;
                case Cil.StackBehaviour.Pop1 or Cil.StackBehaviour.Popi or Cil.StackBehaviour.Popref: pops = 1; break;
                case Cil.StackBehaviour.Pop1_pop1 or Cil.StackBehaviour.Popi_pop1 or Cil.StackBehaviour.Popi_popi or Cil.StackBehaviour.Popi_popi8
                    or Cil.StackBehaviour.Popi_popr4 or Cil.StackBehaviour.Popi_popr8 or Cil.StackBehaviour.Popref_pop1 or Cil.StackBehaviour.Popref_popi:
                    pops = 2; break;
                case Cil.StackBehaviour.Popi_popi_popi or Cil.StackBehaviour.Popref_popi_popi or Cil.StackBehaviour.Popref_popi_popi8
                    or Cil.StackBehaviour.Popref_popi_popr4 or Cil.StackBehaviour.Popref_popi_popr8 or Cil.StackBehaviour.Popref_popi_popref:
                    pops = 3; break;
                case Cil.StackBehaviour.Varpop:
                    if (op.Code == Cil.Code.Ret) pops = method.ReturnType.MetadataType == Cecil.MetadataType.Void ? 0 : 1;
                    else if (i.Operand is Cecil.MethodReference m) pops = m.Parameters.Count + (m.HasThis && !m.ExplicitThis && op.Code != Cil.Code.Newobj ? 1 : 0);
                    else if (i.Operand is Cecil.CallSite site) pops = site.Parameters.Count + 1 + (site.HasThis && !site.ExplicitThis ? 1 : 0);
                    else return false;
                    break;
                default: return false;
            }
            switch (op.StackBehaviourPush)
            {
                case Cil.StackBehaviour.Push0: break;
                case Cil.StackBehaviour.Push1 or Cil.StackBehaviour.Pushi or Cil.StackBehaviour.Pushi8 or Cil.StackBehaviour.Pushr4 or Cil.StackBehaviour.Pushr8
                    or Cil.StackBehaviour.Pushref:
                    pushes = 1; break;
                case Cil.StackBehaviour.Push1_push1: pushes = 2; break;
                case Cil.StackBehaviour.Varpush:
                    var ret = i.Operand switch { Cecil.MethodReference m => op.Code == Cil.Code.Newobj ? null : m.ReturnType, Cecil.CallSite s => s.ReturnType, _ => null };
                    pushes = op.Code == Cil.Code.Newobj || ret != null && ret.MetadataType != Cecil.MetadataType.Void ? 1 : 0;
                    break;
                default: return false;
            }
            return true;
        }

        sealed class Helpers
        {
            internal readonly Cecil.MethodReference Load, Store, Clear, New, Length, Handle;

            internal Helpers(Cecil.ModuleDefinition module, Cecil.TypeReference element)
            {
                Cecil.MethodReference Make(string name)
                {
                    var method = new Cecil.GenericInstanceMethod(module.ImportReference(typeof(PathGridPages).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!));
                    method.GenericArguments.Add(element);
                    return method;
                }
                Load = Make(nameof(PathGridPages.Load));
                Store = Make(nameof(PathGridPages.Store));
                Clear = Make(nameof(PathGridPages.Clear));
                New = Make(nameof(PathGridPages.New));
                Length = Make(nameof(PathGridPages.Length));
                Handle = Make(nameof(PathGridPages.Handle));
            }
        }

        // ---- Mods ----

        static readonly string[] lockMethods = ["Enter", "Exit", "TryEnter", "IsEntered", "Wait", "Pulse", "PulseAll"];

        /// A mod's uses of the game's PathGrid arrays: a lock, or a problem.
        static void ScanMod(Cecil.ModuleDefinition module, string file, List<string> log)
        {
            int locks = 0;
            var problems = new List<string>();
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var i in method.Body.Instructions)
                    {
                        if (i.Operand is not Cecil.FieldReference f || f is Cecil.FieldDefinition || Array.IndexOf(Arrays, f.Name) < 0
                            || f.DeclaringType.FullName != "PathGrid" || f.DeclaringType is Cecil.TypeDefinition
                            || f.DeclaringType.Scope is not Cecil.AssemblyNameReference { Name: "Assembly-CSharp" }) continue;
                        string where = $"{method.DeclaringType.FullName}::{method.Name} IL_{i.Offset:x4}";
                        if (i.OpCode.Code == Cil.Code.Ldfld && IsLock(method, i)) locks++;
                        else problems.Add($"{where}: PathGrid.{f.Name} used by {i.OpCode.Name} otherwise than in a lock");
                    }
                }
            if (problems.Count > 0)
            {
                foreach (var p in problems.Take(5)) log.Add($"path grids: {file}: {p}");
                lock (Problems) Problems.Add($"{file}: {problems[0]}" + (problems.Count > 1 ? $" (and {problems.Count - 1} more)" : ""));
            }
            if (locks > 0) log.Add($"path grids: {file}: {locks} lock(s) on PathGrid's arrays (kept: each grid's array field is an object of its own)");
        }

        /// Whether the array the instruction loads goes only to Monitor (directly, or through a
        /// local read only by Monitor's calls: C#'s `lock`).
        static bool IsLock(Cecil.MethodDefinition method, Cil.Instruction load)
        {
            var consumer = Consumer(method, load, out int depth);
            if (consumer == null) return false;
            if (IsMonitorArgument(consumer, depth)) return true;
            if (depth != 0 || !TryLocal(method, consumer, out var local)) return false;
            foreach (var i in method.Body.Instructions)
            {
                if (i == consumer) continue;
                if (TryLocal(method, i, out var other) && other == local) return false; // written again
                if (i.OpCode.Code is Cil.Code.Ldloca or Cil.Code.Ldloca_S && i.Operand == local) return false;
                if (!ReadsLocal(method, i, local)) continue;
                var use = Consumer(method, i, out int at);
                if (use == null || !IsMonitorArgument(use, at)) return false;
            }
            return true;
        }

        static bool IsMonitorArgument(Cil.Instruction call, int depth) =>
            call.OpCode.Code is Cil.Code.Call && call.Operand is Cecil.MethodReference m && m.DeclaringType.FullName == "System.Threading.Monitor"
            && Array.IndexOf(lockMethods, m.Name) >= 0 && depth == m.Parameters.Count - 1 && m.Parameters[0].ParameterType.FullName == "System.Object";

        static bool TryLocal(Cecil.MethodDefinition method, Cil.Instruction i, out Cil.VariableDefinition? local)
        {
            local = i.OpCode.Code switch
            {
                Cil.Code.Stloc_0 => method.Body.Variables[0],
                Cil.Code.Stloc_1 => method.Body.Variables[1],
                Cil.Code.Stloc_2 => method.Body.Variables[2],
                Cil.Code.Stloc_3 => method.Body.Variables[3],
                Cil.Code.Stloc or Cil.Code.Stloc_S => (Cil.VariableDefinition)i.Operand,
                _ => null,
            };
            return local != null;
        }

        static bool ReadsLocal(Cecil.MethodDefinition method, Cil.Instruction i, Cil.VariableDefinition? local) => i.OpCode.Code switch
        {
            Cil.Code.Ldloc_0 => method.Body.Variables[0] == local,
            Cil.Code.Ldloc_1 => method.Body.Variables[1] == local,
            Cil.Code.Ldloc_2 => method.Body.Variables[2] == local,
            Cil.Code.Ldloc_3 => method.Body.Variables[3] == local,
            Cil.Code.Ldloc or Cil.Code.Ldloc_S => i.Operand == local,
            _ => false,
        };
    }
}
