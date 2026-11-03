using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OniMoono.Engine;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Host
{
    /// The load-time half of `compact-nav-links` (NavGridLinks), run in MonoConversions' pass over
    /// the game's assemblies and mods: every element access of a `NavGrid.Link[]` calls the
    /// helpers, which index a plain array as before and a store through its handle.
    ///
    /// | IL on a `Link[]`                         | becomes                                   |
    /// |------------------------------------------|-------------------------------------------|
    /// | `ldelem.any Link`                        | `call NavGridLinks.Load`                  |
    /// | `ldelema Link` read by `ldfld` / `ldobj` | `call Load` (then the same `ldfld`; `ldobj` dropped) |
    /// | `stelem.any Link`                        | `call Store`                              |
    /// | `ldelema Link` … `stfld Link::link`      | `ldelema` dropped, `call StoreLink`       |
    /// | `ldlen`                                  | `call Length`                             |
    /// | NavGrid's `newarr Link` for `Links`      | `call New(length, maxLinksPerCell)`       |
    ///
    /// Anything else that could see a store's handle as an array (an `ldelema` result used
    /// otherwise, a `Link[]` passed where another type is declared, boxed or stored as one, a
    /// method whose IL cannot be followed) is a problem: in a mod it makes the session flat (the
    /// store is not used, CompactNavLinks); in the game the assembly is left as it is, so no grid
    /// gets a handle.
    ///
    /// The three hot walks (PathFinder.AddPotentials' first loop, PathFinder.ValidatePath and
    /// TravelTube.OnDirtyNavCellUpdated) read through NavGridLinks.Walk and a cursor local added
    /// to the method instead, when the method's IL is one of the builds' known (HotMethods, U59
    /// 744825 and U60 761211): only those reads change (each `ldelem.any` becomes `ldloca` of
    /// the cursor and a call), so the reviewed transpilers of those methods (Hazard Pathing,
    /// Transit Tube Overlay, Travel Tubes Expanded) find every instruction they look for, local
    /// numbers and short branches included. Other IL there, or a short branch the longer reads
    /// would put out of reach, takes the generic helpers, and the log says so.
    internal static class NavGridLinksRewrite
    {
        /// The tweak's switch (Program sets it before any assembly loads).
        internal static bool Enabled;

        /// What makes the session flat: a mod's access the pass could not rewrite.
        internal static readonly List<string> Problems = new();

        /// The assemblies rewritten, by simple name (the mode decision checks every loaded
        /// assembly that names the link type is among them).
        internal static readonly HashSet<string> Rewritten = new(StringComparer.Ordinal);

        const string LinkName = "NavGrid/Link";

        /// Whether the image can name NavGrid's link type ("NavGrid" in its strings).
        internal static bool MayTouch(byte[] image) => image.AsSpan().IndexOf("NavGrid"u8) >= 0;

        /// The hot walks: the type, the method, and the fingerprints of its IL in the known builds
        /// (Fingerprint; the log names a new one) with the walks' `ldelem.any` count.
        internal static readonly (string Type, string Method, int Walks, string[] Known)[] HotMethods =
        [
            ("PathFinder", "AddPotentials", 1, ["CE24E8E0272A1795", "3790B147D1CFBB2A"]),
            ("PathFinder", "ValidatePath", 2, ["F0E3D9FFA1A2DF36", "0E4BF1841E394E89"]),
            ("TravelTube", "OnDirtyNavCellUpdated", 2, ["7AF92409EF47612E", "14CB6716D4F7458A"]),
        ];

        /// The module's accesses rewritten; returns how many. `game`: Assembly-CSharp, where a
        /// problem leaves the module as it is (no handles: the session is flat).
        internal static int RewriteModule(Cecil.ModuleDefinition module, bool game, string file, List<string> log)
        {
            var plans = new List<(Cecil.MethodDefinition Method, Plan Plan)>();
            var problems = new List<string>();
            Cecil.TypeReference? link = null;
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !Relevant(method, ref link)) continue;
                    var plan = Analyse(method);
                    problems.AddRange(plan.Problems);
                    if (plan.Any || game && method.IsConstructor && !method.IsStatic && type.FullName == "NavGrid") plans.Add((method, plan));
                }
            if (link == null) return 0;
            var name = module.Assembly.Name.Name;
            if (problems.Count > 0)
            {
                foreach (var p in problems.Take(5)) log.Add($"nav links: {file}: {p}");
                if (problems.Count > 5) log.Add($"nav links: {file}: {problems.Count - 5} more");
                if (game)
                {
                    log.Add($"nav links: {file}: not rewritten ({problems.Count} access(es) the store cannot follow); the game's grids keep their arrays");
                    return 0;
                }
                lock (Problems) Problems.Add($"{file}: {problems[0]}" + (problems.Count > 1 ? $" (and {problems.Count - 1} more)" : ""));
            }
            if (plans.Count == 0)
            {
                lock (Rewritten) Rewritten.Add(name);
                return 0;
            }
            var helpers = new Helpers(module, link);
            int sites = 0, walks = 0;
            bool constructed = false;
            foreach (var (method, plan) in plans)
            {
                string? hot = game ? HotMethod(method, plan, helpers, log, ref walks) : null;
                if (hot == "walk")
                {
                    sites += plan.Count;
                    continue;
                }
                bool grew = false;
                var il = method.Body.GetILProcessor();
                foreach (var i in plan.Loads) Replace(i, Cil.OpCodes.Call, helpers.Load);
                foreach (var i in plan.Stores) Replace(i, Cil.OpCodes.Call, helpers.Store);
                foreach (var (producer, objects) in plan.Reads)
                {
                    Replace(producer, Cil.OpCodes.Call, helpers.Load);
                    foreach (var o in objects) Replace(o, Cil.OpCodes.Nop, null);
                }
                foreach (var (producer, field) in plan.Markers)
                {
                    Replace(producer, Cil.OpCodes.Nop, null);
                    Replace(field, Cil.OpCodes.Call, helpers.StoreLink);
                }
                foreach (var i in plan.Lengths)
                {
                    Replace(i, Cil.OpCodes.Call, helpers.Length);
                    grew = true;
                }
                if (game && method.IsConstructor && !method.IsStatic && method.DeclaringType.FullName == "NavGrid")
                    foreach (var i in method.Body.Instructions.ToArray())
                    {
                        if (i.OpCode.Code != Cil.Code.Newarr || !IsLink((Cecil.TypeReference)i.Operand) || i.Next?.OpCode.Code != Cil.Code.Stfld
                            || ((Cecil.FieldReference)i.Next.Operand).Name != "Links") continue;
                        var getter = method.DeclaringType.Methods.FirstOrDefault(m => m.Name == "get_maxLinksPerCell" && !m.IsStatic);
                        if (getter == null) continue;
                        il.InsertBefore(i, il.Create(Cil.OpCodes.Ldarg_0));
                        il.InsertBefore(i, il.Create(Cil.OpCodes.Call, getter));
                        Replace(i, Cil.OpCodes.Call, helpers.New);
                        constructed = grew = true;
                    }
                if (grew) MonoConversions.WidenBranches(method.Body);
                sites += plan.Count;
            }
            if (game && !constructed)
            {
                log.Add($"nav links: {file}: NavGrid's constructor has no `newarr Link` for Links; the game's grids keep their arrays");
            }
            lock (Rewritten) Rewritten.Add(name);
            log.Add($"nav links: {file}: {sites} access(es) of Link[] in {plans.Count} method(s) go through the store's helpers"
                    + (game ? $"; {walks} hot walk(s) through a cursor" : "") + (constructed ? "; NavGrid makes its links with NavGridLinks.New" : ""));
            return sites + (constructed ? 1 : 0);
        }

        static void Replace(Cil.Instruction i, Cil.OpCode op, object? operand)
        {
            i.OpCode = op;
            i.Operand = operand;
        }

        /// A hot walk replaced through a cursor ("walk"), or null when the method is not one or
        /// its IL is not a known build's (the generic helpers then).
        static string? HotMethod(Cecil.MethodDefinition method, Plan plan, Helpers helpers, List<string> log, ref int walks)
        {
            var hot = Array.FindIndex(HotMethods, h => h.Type == method.DeclaringType.FullName && h.Method == method.Name);
            if (hot < 0) return null;
            var (_, _, expected, known) = HotMethods[hot];
            string print = Fingerprint(method);
            string where = $"{method.DeclaringType.Name}.{method.Name}";
            if (Array.IndexOf(known, print) < 0)
            {
                log.Add($"nav links: {where}: IL {print} is not a known build's; its walk reads through the generic helpers");
                return null;
            }
            // The walks: `ldelem.any Link` of NavGrid.Links or of a `links` parameter; the
            // others in these methods are the scratch pad's own arrays and stay as they are.
            var sites = plan.Loads.Where(IsWalk).ToList();
            if (sites.Count != expected || plan.Reads.Count > 0 || plan.Markers.Count > 0 || plan.Lengths.Count > 0)
            {
                log.Add($"nav links: {where}: {sites.Count} walk read(s), {expected} expected; generic helpers");
                return null;
            }
            var cursor = new Cil.VariableDefinition(helpers.Cursor);
            method.Body.Variables.Add(cursor);
            var il = method.Body.GetILProcessor();
            foreach (var site in sites)
            {
                il.InsertBefore(site, il.Create(method.Body.Variables.Count > 256 ? Cil.OpCodes.Ldloca : Cil.OpCodes.Ldloca_S, cursor));
                Replace(site, Cil.OpCodes.Call, helpers.Walk);
            }
            if (!ShortBranchesReach(method.Body))
            {
                // Undo: the generic helpers keep every instruction's size.
                foreach (var site in sites)
                {
                    il.Remove(site.Previous);
                    Replace(site, Cil.OpCodes.Ldelem_Any, helpers.LinkType);
                }
                method.Body.Variables.Remove(cursor);
                log.Add($"nav links: {where}: a short branch would not reach past the walk's reads; generic helpers");
                return null;
            }
            walks += sites.Count;
            return "walk";
        }

        static bool IsWalk(Cil.Instruction load)
        {
            var array = load.Previous?.Previous;
            return array != null && (array.Operand is Cecil.FieldReference { Name: "Links" } f && f.DeclaringType.FullName == "NavGrid"
                                     || array.Operand is Cecil.ParameterDefinition { Name: "links" });
        }

        static bool ShortBranchesReach(Cil.MethodBody body)
        {
            int offset = 0;
            foreach (var i in body.Instructions)
            {
                i.Offset = offset;
                offset += i.GetSize();
            }
            foreach (var i in body.Instructions)
            {
                if (i.OpCode.OperandType != Cil.OperandType.ShortInlineBrTarget) continue;
                int distance = ((Cil.Instruction)i.Operand).Offset - (i.Offset + i.GetSize());
                if (distance is < sbyte.MinValue or > sbyte.MaxValue) return false;
            }
            return true;
        }

        /// A short hash of the method's IL: each instruction's opcode and operand (branch
        /// targets by index), and its locals' types.
        internal static string Fingerprint(Cecil.MethodDefinition method)
        {
            var text = new StringBuilder();
            var instructions = method.Body.Instructions;
            var index = new Dictionary<Cil.Instruction, int>();
            for (int i = 0; i < instructions.Count; i++) index[instructions[i]] = i;
            foreach (var v in method.Body.Variables) text.Append("L ").Append(v.VariableType.FullName).Append('\n');
            foreach (var i in instructions)
            {
                text.Append(i.OpCode.Name).Append(' ');
                text.Append(i.Operand switch
                {
                    Cil.Instruction t => "@" + index[t],
                    Cil.Instruction[] ts => string.Join(",", ts.Select(t => "@" + index[t])),
                    Cil.VariableDefinition v => "V" + v.Index,
                    Cecil.ParameterDefinition p => "A" + p.Index,
                    null => "",
                    var o => o.ToString(),
                });
                text.Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
        }

        sealed class Helpers
        {
            internal readonly Cecil.TypeReference LinkType, Cursor;
            internal readonly Cecil.MethodReference Load, Walk, Store, StoreLink, Length, New;

            internal Helpers(Cecil.ModuleDefinition module, Cecil.TypeReference link)
            {
                LinkType = link;
                Cecil.MethodReference Make(string name)
                {
                    var method = new Cecil.GenericInstanceMethod(module.ImportReference(typeof(NavGridLinks).GetMethod(name)!));
                    method.GenericArguments.Add(link);
                    return method;
                }
                Load = Make(nameof(NavGridLinks.Load));
                Walk = Make(nameof(NavGridLinks.Walk));
                Store = Make(nameof(NavGridLinks.Store));
                StoreLink = Make(nameof(NavGridLinks.StoreLink));
                Length = Make(nameof(NavGridLinks.Length));
                New = Make(nameof(NavGridLinks.New));
                var cursor = new Cecil.GenericInstanceType(module.ImportReference(typeof(NavLinkCursor<>)));
                cursor.GenericArguments.Add(link);
                Cursor = cursor;
            }
        }

        static bool IsLink(Cecil.TypeReference? type) => type != null && type.FullName == LinkName && !type.IsArray && !type.IsByReference;

        /// Whether the type names the link type anywhere (an array, a reference, an argument).
        static bool Names(Cecil.TypeReference? type)
        {
            while (type is Cecil.TypeSpecification spec)
            {
                if (spec is Cecil.GenericInstanceType generic && generic.GenericArguments.Any(Names)) return true;
                type = spec.ElementType;
            }
            return type != null && type.FullName == LinkName;
        }

        /// Whether the method touches link arrays at all: a signature, local or operand naming
        /// the link type (the first one found is kept as the module's reference to it).
        static bool Relevant(Cecil.MethodDefinition method, ref Cecil.TypeReference? link)
        {
            bool found = false;
            var known = link;
            void Note(Cecil.TypeReference t)
            {
                found = true;
                if (known == null)
                {
                    var element = t;
                    while (element is Cecil.TypeSpecification spec && element.FullName != LinkName) element = spec.ElementType;
                    if (element.FullName == LinkName && element is not Cecil.TypeSpecification) known = element;
                }
            }
            if (Names(method.ReturnType)) Note(method.ReturnType);
            foreach (var p in method.Parameters)
                if (Names(p.ParameterType)) Note(p.ParameterType);
            foreach (var v in method.Body.Variables)
                if (Names(v.VariableType)) Note(v.VariableType);
            foreach (var i in method.Body.Instructions)
            {
                switch (i.Operand)
                {
                    case Cecil.TypeReference t when Names(t):
                        Note(t);
                        break;
                    case Cecil.FieldReference f when Names(f.FieldType) || Names(f.DeclaringType):
                        Note(Names(f.FieldType) ? f.FieldType : f.DeclaringType);
                        break;
                    case Cecil.MethodReference m:
                        if (Names(m.ReturnType)) Note(m.ReturnType);
                        foreach (var p in m.Parameters)
                            if (Names(p.ParameterType)) Note(p.ParameterType);
                        if (m is Cecil.GenericInstanceMethod gm)
                            foreach (var a in gm.GenericArguments)
                                if (Names(a)) Note(a);
                        if (Names(m.DeclaringType)) Note(m.DeclaringType);
                        break;
                    case Cecil.CallSite site:
                        if (Names(site.ReturnType) || site.Parameters.Any(p => Names(p.ParameterType))) found = true;
                        break;
                }
            }
            link = known;
            return found;
        }

        /// What a method's rewrite changes, and why it cannot be.
        sealed class Plan
        {
            internal readonly List<Cil.Instruction> Loads = new(), Stores = new(), Lengths = new();
            internal readonly List<(Cil.Instruction Producer, List<Cil.Instruction> Objects)> Reads = new();
            internal readonly List<(Cil.Instruction Producer, Cil.Instruction Field)> Markers = new();
            internal readonly List<string> Problems = new();
            internal bool Any => Count > 0;
            internal int Count => Loads.Count + Stores.Count + Lengths.Count + Reads.Count + Markers.Count;
        }

        /// A stack slot as far as links care: a `Link[]` (Array), a reference to one (ArrayRef),
        /// an array of them (Arrays), an `ldelema Link` result (Element, with its instruction),
        /// something else (Other), or values that met at a join and differ (Unknown).
        enum Kind : byte { Other, Array, ArrayRef, Arrays, Element, Unknown }

        readonly record struct Slot(Kind Kind, int Producer = -1)
        {
            public static readonly Slot Other = new(Kind.Other);
        }

        enum Use : byte { Field, Object, Marker }

        static Plan Analyse(Cecil.MethodDefinition method)
        {
            var plan = new Plan();
            var body = method.Body;
            var instructions = body.Instructions;
            foreach (var i in instructions)
            {
                if (i.OpCode.Code == Cil.Code.Ldelem_Any && IsLink((Cecil.TypeReference)i.Operand)) plan.Loads.Add(i);
                else if (i.OpCode.Code == Cil.Code.Stelem_Any && IsLink((Cecil.TypeReference)i.Operand)) plan.Stores.Add(i);
            }
            var index = new Dictionary<Cil.Instruction, int>(instructions.Count);
            for (int i = 0; i < instructions.Count; i++) index[instructions[i]] = i;
            var entry = new Slot[instructions.Count][];
            var uses = new Dictionary<int, HashSet<(Cil.Instruction, Use)>>();
            var lengths = new HashSet<Cil.Instruction>();
            var problems = new HashSet<string>();
            var work = new Stack<int>();
            string Where(Cil.Instruction i) => $"{method.DeclaringType.FullName}::{method.Name} IL_{i.Offset:x4} {i.OpCode.Name}";
            void Problem(Cil.Instruction i, string what) => problems.Add($"{Where(i)}: {what}");
            void Flow(int target, List<Slot> stack)
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
                for (int k = 0; k < existing.Length; k++)
                {
                    var joined = Join(existing[k], stack[k]);
                    if (joined != existing[k])
                    {
                        existing[k] = joined;
                        changed = true;
                    }
                }
                if (changed) work.Push(target);
            }
            try
            {
                Flow(0, new List<Slot>());
                foreach (var handler in body.ExceptionHandlers)
                {
                    var caught = new List<Slot>();
                    if (handler.HandlerType is Cil.ExceptionHandlerType.Catch or Cil.ExceptionHandlerType.Filter) caught.Add(Slot.Other);
                    Flow(index[handler.HandlerStart], caught);
                    if (handler.HandlerType == Cil.ExceptionHandlerType.Filter) Flow(index[handler.FilterStart], new List<Slot> { Slot.Other });
                }
                while (work.Count > 0)
                {
                    int at = work.Pop();
                    var stack = new List<Slot>(entry[at]);
                    while (true)
                    {
                        var instruction = instructions[at];
                        Step(method, instruction, at, stack, uses, lengths, Problem);
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
                plan.Problems.Add($"{method.DeclaringType.FullName}::{method.Name}: its IL could not be followed ({e.Message})");
                return plan;
            }
            // Each `ldelema Link`: read through (ldfld, ldobj) or the end marker's write, nothing else.
            for (int i = 0; i < instructions.Count; i++)
            {
                var producer = instructions[i];
                if (producer.OpCode.Code != Cil.Code.Ldelema || !IsLink((Cecil.TypeReference)producer.Operand)) continue;
                if (entry[i] == null) continue; // unreachable
                var used = uses.TryGetValue(i, out var u) ? u : new HashSet<(Cil.Instruction, Use)>();
                if (used.All(x => x.Item2 is Use.Field or Use.Object))
                    plan.Reads.Add((producer, used.Where(x => x.Item2 == Use.Object).Select(x => x.Item1).ToList()));
                else if (used.Count == 1 && used.First().Item2 == Use.Marker)
                    plan.Markers.Add((producer, used.First().Item1));
                else Problem(producer, "an element's reference is used otherwise than to read it or to write its link");
            }
            plan.Lengths.AddRange(lengths.OrderBy(i => index[i]));
            plan.Problems.AddRange(problems.Order(StringComparer.Ordinal));
            return plan;
        }

        static Slot Join(Slot a, Slot b)
        {
            if (a == b) return a;
            // `null` (Other) meets an array: still the array.
            if (a.Kind == Kind.Other && b.Kind is Kind.Array or Kind.ArrayRef or Kind.Arrays) return b;
            if (b.Kind == Kind.Other && a.Kind is Kind.Array or Kind.ArrayRef or Kind.Arrays) return a;
            return new Slot(Kind.Unknown);
        }

        /// The slot a value of this (resolved) type is.
        static Slot Of(Cecil.TypeReference type, Cecil.GenericInstanceType? owner = null, Cecil.GenericInstanceMethod? method = null)
        {
            type = Resolve(type, owner, method);
            while (type is Cecil.RequiredModifierType or Cecil.OptionalModifierType or Cecil.PinnedType) type = ((Cecil.TypeSpecification)type).ElementType;
            if (type is Cecil.ArrayType { IsVector: true } array)
            {
                if (IsLink(array.ElementType)) return new Slot(Kind.Array);
                if (array.ElementType is Cecil.ArrayType { IsVector: true } inner && IsLink(inner.ElementType)) return new Slot(Kind.Arrays);
            }
            if (type is Cecil.ByReferenceType { ElementType: Cecil.ArrayType { IsVector: true } referenced } && IsLink(referenced.ElementType)) return new Slot(Kind.ArrayRef);
            return Slot.Other;
        }

        /// A generic parameter by the instantiation that names it (one level, as the signatures
        /// the game and mods use need).
        static Cecil.TypeReference Resolve(Cecil.TypeReference type, Cecil.GenericInstanceType? owner, Cecil.GenericInstanceMethod? method)
        {
            switch (type)
            {
                case Cecil.GenericParameter { Type: Cecil.GenericParameterType.Type } p when owner != null && p.Position < owner.GenericArguments.Count:
                    return owner.GenericArguments[p.Position];
                case Cecil.GenericParameter { Type: Cecil.GenericParameterType.Method } p when method != null && p.Position < method.GenericArguments.Count:
                    return method.GenericArguments[p.Position];
                case Cecil.ArrayType { IsVector: true } a:
                    var element = Resolve(a.ElementType, owner, method);
                    return element == a.ElementType ? a : new Cecil.ArrayType(element);
                case Cecil.ByReferenceType r:
                    var referenced = Resolve(r.ElementType, owner, method);
                    return referenced == r.ElementType ? r : new Cecil.ByReferenceType(referenced);
            }
            return type;
        }

        /// The slot's place, declared as `declared` (unresolved: a generic parameter that a
        /// link array would fill is a place its elements are read without the helpers).
        static bool Fits(Slot slot, Cecil.TypeReference declared)
        {
            var place = Of(declared);
            return slot.Kind switch
            {
                Kind.Other => true,
                Kind.Array or Kind.ArrayRef or Kind.Arrays => place.Kind == slot.Kind,
                _ => false,
            };
        }

        static Slot Pop(List<Slot> stack)
        {
            if (stack.Count == 0) throw new InvalidOperationException("stack underflow");
            var slot = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return slot;
        }

        static void Step(Cecil.MethodDefinition method, Cil.Instruction instruction, int at, List<Slot> stack,
            Dictionary<int, HashSet<(Cil.Instruction, Use)>> uses, HashSet<Cil.Instruction> lengths, Action<Cil.Instruction, string> problem)
        {
            var op = instruction.OpCode;
            void UseOf(Slot slot, Use use)
            {
                if (!uses.TryGetValue(slot.Producer, out var set)) uses[slot.Producer] = set = new HashSet<(Cil.Instruction, Use)>();
                set.Add((instruction, use));
            }
            // A slot consumed where only a plain value may go.
            void Plain(Slot slot, string what)
            {
                if (slot.Kind != Kind.Other) problem(instruction, $"{Describe(slot)} {what}");
            }
            void Place(Slot slot, Cecil.TypeReference declared, string what)
            {
                if (!Fits(slot, declared)) problem(instruction, $"{Describe(slot)} {what} declared {declared.FullName}");
            }
            switch (op.Code)
            {
                case Cil.Code.Ldelem_Any:
                {
                    Plain(Pop(stack), "as an index");
                    var array = Pop(stack);
                    var element = (Cecil.TypeReference)instruction.Operand;
                    if (!IsLink(element) && array.Kind != Kind.Other) problem(instruction, $"{Describe(array)} read as an array of {element.FullName}");
                    stack.Add(Of(element));
                    return;
                }
                case Cil.Code.Ldelem_Ref:
                {
                    Plain(Pop(stack), "as an index");
                    var array = Pop(stack);
                    if (array.Kind is not (Kind.Other or Kind.Arrays)) problem(instruction, $"{Describe(array)} read as an array of references");
                    stack.Add(array.Kind == Kind.Arrays ? new Slot(Kind.Array) : Slot.Other);
                    return;
                }
                case Cil.Code.Ldelema:
                {
                    Plain(Pop(stack), "as an index");
                    var array = Pop(stack);
                    var element = (Cecil.TypeReference)instruction.Operand;
                    if (IsLink(element)) stack.Add(new Slot(Kind.Element, at));
                    else
                    {
                        if (array.Kind is not (Kind.Other or Kind.Arrays)) problem(instruction, $"{Describe(array)} indexed as an array of {element.FullName}");
                        stack.Add(Of(new Cecil.ByReferenceType(element)));
                    }
                    return;
                }
                case Cil.Code.Stelem_Any:
                {
                    var value = Pop(stack);
                    Plain(Pop(stack), "as an index");
                    var array = Pop(stack);
                    var element = (Cecil.TypeReference)instruction.Operand;
                    if (!IsLink(element))
                    {
                        Place(value, element, "stored in an array of");
                        if (array.Kind is not (Kind.Other or Kind.Arrays)) problem(instruction, $"{Describe(array)} written as an array of {element.FullName}");
                    }
                    else Plain(value, "stored as a link");
                    return;
                }
                case Cil.Code.Stelem_Ref:
                {
                    var value = Pop(stack);
                    Plain(Pop(stack), "as an index");
                    var array = Pop(stack);
                    if (!(value.Kind == Kind.Array && array.Kind == Kind.Arrays)) Plain(value, "stored in an array of references");
                    return;
                }
                case Cil.Code.Ldlen:
                {
                    var array = Pop(stack);
                    if (array.Kind == Kind.Array) lengths.Add(instruction);
                    else Plain(array, "has its length read");
                    stack.Add(Slot.Other);
                    return;
                }
                case Cil.Code.Ldfld:
                {
                    var obj = Pop(stack);
                    var field = (Cecil.FieldReference)instruction.Operand;
                    if (obj.Kind == Kind.Element) UseOf(obj, Use.Field);
                    else Plain(obj, "has a field read");
                    stack.Add(Of(field.FieldType, field.DeclaringType as Cecil.GenericInstanceType));
                    return;
                }
                case Cil.Code.Ldflda:
                {
                    var obj = Pop(stack);
                    var field = (Cecil.FieldReference)instruction.Operand;
                    Plain(obj, "has a field's address taken");
                    stack.Add(Of(new Cecil.ByReferenceType(field.FieldType), field.DeclaringType as Cecil.GenericInstanceType));
                    return;
                }
                case Cil.Code.Stfld:
                {
                    var value = Pop(stack);
                    var obj = Pop(stack);
                    var field = (Cecil.FieldReference)instruction.Operand;
                    Place(value, field.FieldType, "stored in a field");
                    if (obj.Kind == Kind.Element)
                    {
                        if (field.Name == "link" && IsLink(field.DeclaringType)) UseOf(obj, Use.Marker);
                        else problem(instruction, $"an element's {field.Name} written through its reference");
                    }
                    else Plain(obj, "has a field written");
                    return;
                }
                case Cil.Code.Ldsfld:
                    stack.Add(Of(((Cecil.FieldReference)instruction.Operand).FieldType));
                    return;
                case Cil.Code.Ldsflda:
                    stack.Add(Of(new Cecil.ByReferenceType(((Cecil.FieldReference)instruction.Operand).FieldType)));
                    return;
                case Cil.Code.Stsfld:
                    Place(Pop(stack), ((Cecil.FieldReference)instruction.Operand).FieldType, "stored in a static field");
                    return;
                case Cil.Code.Ldarg_0 or Cil.Code.Ldarg_1 or Cil.Code.Ldarg_2 or Cil.Code.Ldarg_3:
                    stack.Add(Argument(method, op.Code - Cil.Code.Ldarg_0, false));
                    return;
                case Cil.Code.Ldarg_S or Cil.Code.Ldarg:
                    stack.Add(Argument(method, ArgumentIndex(method, instruction.Operand), false));
                    return;
                case Cil.Code.Ldarga_S or Cil.Code.Ldarga:
                    stack.Add(Argument(method, ArgumentIndex(method, instruction.Operand), true));
                    return;
                case Cil.Code.Starg_S or Cil.Code.Starg:
                {
                    int slot = ArgumentIndex(method, instruction.Operand);
                    Place(Pop(stack), ArgumentType(method, slot), "stored in an argument");
                    return;
                }
                case Cil.Code.Ldloc_0 or Cil.Code.Ldloc_1 or Cil.Code.Ldloc_2 or Cil.Code.Ldloc_3:
                    stack.Add(Of(method.Body.Variables[op.Code - Cil.Code.Ldloc_0].VariableType));
                    return;
                case Cil.Code.Ldloc_S or Cil.Code.Ldloc:
                    stack.Add(Of(((Cil.VariableDefinition)instruction.Operand).VariableType));
                    return;
                case Cil.Code.Ldloca_S or Cil.Code.Ldloca:
                    stack.Add(Of(new Cecil.ByReferenceType(((Cil.VariableDefinition)instruction.Operand).VariableType)));
                    return;
                case Cil.Code.Stloc_0 or Cil.Code.Stloc_1 or Cil.Code.Stloc_2 or Cil.Code.Stloc_3:
                    Place(Pop(stack), method.Body.Variables[op.Code - Cil.Code.Stloc_0].VariableType, "stored in a local");
                    return;
                case Cil.Code.Stloc_S or Cil.Code.Stloc:
                    Place(Pop(stack), ((Cil.VariableDefinition)instruction.Operand).VariableType, "stored in a local");
                    return;
                case Cil.Code.Ldobj:
                {
                    var address = Pop(stack);
                    var type = (Cecil.TypeReference)instruction.Operand;
                    if (address.Kind == Kind.Element && IsLink(type))
                    {
                        UseOf(address, Use.Object);
                        stack.Add(Slot.Other);
                    }
                    else if (address.Kind == Kind.ArrayRef) stack.Add(new Slot(Kind.Array));
                    else
                    {
                        Plain(address, "read through");
                        stack.Add(Of(type));
                    }
                    return;
                }
                case Cil.Code.Ldind_Ref:
                {
                    var address = Pop(stack);
                    if (address.Kind == Kind.ArrayRef) stack.Add(new Slot(Kind.Array));
                    else
                    {
                        Plain(address, "read through");
                        stack.Add(Slot.Other);
                    }
                    return;
                }
                case Cil.Code.Stobj or Cil.Code.Stind_Ref:
                {
                    var value = Pop(stack);
                    var address = Pop(stack);
                    if (address.Kind == Kind.ArrayRef && value.Kind is Kind.Array or Kind.Other) return;
                    Plain(value, "stored through a reference");
                    Plain(address, "written through");
                    return;
                }
                case Cil.Code.Dup:
                {
                    var top = Pop(stack);
                    stack.Add(top);
                    stack.Add(top);
                    return;
                }
                case Cil.Code.Pop:
                    Pop(stack);
                    return;
                case Cil.Code.Castclass or Cil.Code.Isinst:
                {
                    var value = Pop(stack);
                    var type = (Cecil.TypeReference)instruction.Operand;
                    var cast = Of(type);
                    if (value.Kind != Kind.Other && cast.Kind != value.Kind) problem(instruction, $"{Describe(value)} cast to {type.FullName}");
                    stack.Add(cast);
                    return;
                }
                case Cil.Code.Call or Cil.Code.Callvirt or Cil.Code.Newobj:
                {
                    var callee = (Cecil.MethodReference)instruction.Operand;
                    for (int p = callee.Parameters.Count - 1; p >= 0; p--)
                        Place(Pop(stack), callee.Parameters[p].ParameterType, $"passed to {callee.DeclaringType.Name}.{callee.Name} as");
                    if (callee.HasThis && op.Code != Cil.Code.Newobj)
                    {
                        var self = Pop(stack);
                        if (self.Kind != Kind.Other) problem(instruction, $"{Describe(self)} used as `this` of {callee.DeclaringType.Name}.{callee.Name}");
                    }
                    if (op.Code == Cil.Code.Newobj) stack.Add(Slot.Other);
                    else if (callee.ReturnType.MetadataType != Cecil.MetadataType.Void)
                        stack.Add(Of(callee.ReturnType, callee.DeclaringType as Cecil.GenericInstanceType, callee as Cecil.GenericInstanceMethod));
                    return;
                }
                case Cil.Code.Calli:
                {
                    var site = (Cecil.CallSite)instruction.Operand;
                    Plain(Pop(stack), "called as a function pointer");
                    for (int p = site.Parameters.Count - 1; p >= 0; p--) Place(Pop(stack), site.Parameters[p].ParameterType, "passed through a function pointer as");
                    if (site.HasThis) Plain(Pop(stack), "used as `this` of a function pointer");
                    if (site.ReturnType.MetadataType != Cecil.MetadataType.Void) stack.Add(Of(site.ReturnType));
                    return;
                }
                case Cil.Code.Ret:
                    if (method.ReturnType.MetadataType != Cecil.MetadataType.Void) Place(Pop(stack), method.ReturnType, "returned as");
                    return;
                // References compared or tested: an array's identity, which a handle keeps.
                case Cil.Code.Ceq or Cil.Code.Cgt_Un or Cil.Code.Beq or Cil.Code.Beq_S or Cil.Code.Bne_Un or Cil.Code.Bne_Un_S:
                {
                    var b = Pop(stack);
                    var a = Pop(stack);
                    if (a.Kind is Kind.Element or Kind.Unknown || b.Kind is Kind.Element or Kind.Unknown) problem(instruction, "an element's reference compared");
                    if (op.Code is Cil.Code.Ceq or Cil.Code.Cgt_Un) stack.Add(Slot.Other);
                    return;
                }
                case Cil.Code.Brtrue or Cil.Code.Brtrue_S or Cil.Code.Brfalse or Cil.Code.Brfalse_S:
                {
                    var value = Pop(stack);
                    if (value.Kind is Kind.Element or Kind.Unknown) problem(instruction, "an element's reference tested");
                    return;
                }
            }
            // The rest by their stack behaviour: none of them may take a link array or element.
            int pops = op.StackBehaviourPop switch
            {
                Cil.StackBehaviour.Pop0 => 0,
                Cil.StackBehaviour.Pop1 or Cil.StackBehaviour.Popi or Cil.StackBehaviour.Popref => 1,
                Cil.StackBehaviour.Pop1_pop1 or Cil.StackBehaviour.Popi_pop1 or Cil.StackBehaviour.Popi_popi or Cil.StackBehaviour.Popi_popi8
                    or Cil.StackBehaviour.Popi_popr4 or Cil.StackBehaviour.Popi_popr8 or Cil.StackBehaviour.Popref_pop1 or Cil.StackBehaviour.Popref_popi => 2,
                Cil.StackBehaviour.Popi_popi_popi or Cil.StackBehaviour.Popref_popi_popi or Cil.StackBehaviour.Popref_popi_popi8
                    or Cil.StackBehaviour.Popref_popi_popr4 or Cil.StackBehaviour.Popref_popi_popr8 or Cil.StackBehaviour.Popref_popi_popref => 3,
                Cil.StackBehaviour.PopAll => stack.Count,
                _ => throw new NotSupportedException($"stack behaviour of {op}"),
            };
            for (int k = 0; k < pops; k++) Plain(Pop(stack), $"taken by {op.Name}");
            switch (op.StackBehaviourPush)
            {
                case Cil.StackBehaviour.Push0:
                    break;
                case Cil.StackBehaviour.Push1 or Cil.StackBehaviour.Pushi or Cil.StackBehaviour.Pushi8 or Cil.StackBehaviour.Pushr4
                    or Cil.StackBehaviour.Pushr8 or Cil.StackBehaviour.Pushref:
                    stack.Add(op.Code == Cil.Code.Newarr ? Of(new Cecil.ArrayType((Cecil.TypeReference)instruction.Operand)) : Slot.Other);
                    break;
                case Cil.StackBehaviour.Push1_push1:
                    stack.Add(Slot.Other);
                    stack.Add(Slot.Other);
                    break;
                default:
                    throw new NotSupportedException($"stack behaviour of {op}");
            }
        }

        static string Describe(Slot slot) => slot.Kind switch
        {
            Kind.Array => "a Link[]",
            Kind.ArrayRef => "a reference to a Link[]",
            Kind.Arrays => "a Link[][]",
            Kind.Element => "an element's reference",
            _ => "a value that may be a Link[] or an element's reference",
        };

        static int ArgumentIndex(Cecil.MethodDefinition method, object operand) =>
            operand is Cecil.ParameterDefinition p ? p.Index + (method.HasThis ? 1 : 0) : throw new InvalidOperationException("argument operand");

        static Cecil.TypeReference ArgumentType(Cecil.MethodDefinition method, int slot)
        {
            if (method.HasThis)
            {
                if (slot == 0) return method.DeclaringType;
                slot--;
            }
            return method.Parameters[slot].ParameterType;
        }

        static Slot Argument(Cecil.MethodDefinition method, int slot, bool address)
        {
            if (method.HasThis && slot == 0) return Slot.Other;
            var type = ArgumentType(method, slot);
            return Of(address ? new Cecil.ByReferenceType(type) : type);
        }
    }
}
