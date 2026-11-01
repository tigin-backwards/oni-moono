// The kindchange probe's cases (KindChange.cs), in an assembly of their own built without
// AllowUnsafeBlocks: C# marks an unsafe build's assembly SkipVerification, and Mono's JIT then
// skips the checks these cases measure (mini/mini.c, mini_assembly_can_skip_verification).
// Built against ProbeKind v1 (../v1), run with v2 (../v2): Flip was a struct and is a class,
// Flop the other way round. Each case is a method of its own, called twice: what it returned,
// or the exception's type, message and first stack frame, and whether any of its body ran.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ProbeKind;

namespace ProbeKindCases
{
    public static class Cases
    {
        static Action<string> L;

        static string Esc(string s) => s == null ? "null" : s.Replace("\r", "\\r").Replace("\n", "\\n");

        static int ran;
        static Flip own;
        static string handled;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string UnboxAnyLocal(object o) { ran++; var f = (Flip)o; return f.A + "/" + f.S; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string UnboxField(object o) { ran++; return "" + ((Flip)o).A; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string CallLocal() { ran++; var f = FlipHost.Make(2); return f.A + "/" + f.S; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string CallField() { ran++; return "" + FlipHost.Make(2).A; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string PassThrough() { ran++; return "" + FlipHost.Read(FlipHost.Make(8)); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string StaticFieldMember() { ran++; return "" + FlipHost.Field.A; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string StaticFieldCopy() { ran++; var f = FlipHost.Field; return f.A + "/" + f.S; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string InstFieldMember() { ran++; return "" + new FlipHost().Inst.A; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string NewLocal() { ran++; var f = new Flip(9); return f.A + "/" + f.S; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string NewDirect() { ran++; return "" + new Flip(9).A; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string DefaultLocal() { ran++; Flip f = default; return "" + FlipHost.Read(f); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string BoxCall() { ran++; object o = FlipHost.Make(1); return o.ToString(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ListField() { ran++; return "" + FlipHost.List.Count; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ListNew() { ran++; var l = new List<Flip>(); return "" + l.Count; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ListParam() { ran++; return "" + FlipHost.ReadList(null); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ArrayElem() { ran++; return "" + FlipHost.Arr[0].A; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ArrayCopy() { ran++; var f = FlipHost.Arr[0]; return f.A + "/" + f.S; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string OwnStaticField() { ran++; own = FlipHost.Make(3); return "" + FlipHost.Read(own); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Take(Flip f) => FlipHost.Read(f);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string OwnParam() { ran++; return "" + Take(FlipHost.Make(4)); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string InstanceCall() { ran++; return "" + FlipHost.Make(2).Twice(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string OutParam() { ran++; FlipHost.Out(out var f); return "" + FlipHost.Read(f); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string RefParam() { ran++; var f = FlipHost.Make(5); return "" + FlipHost.Ref(ref f); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ToStringLocal() { ran++; var f = FlipHost.Make(1); return f.ToString(); }

        static void OnEvent(object data) { var f = (Flip)data; handled = f.A + "/" + f.S; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string Handler() { ran++; handled = null; FlipHost.Raise(OnEvent, 12); return handled; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string FlopMake() { ran++; return "" + FlopHost.Make(1).A; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string FlopRead() { ran++; return "" + FlopHost.Read(new Flop(2)); }

        static void Case(string name, Func<string> call)
        {
            for (int i = 1; i <= 2; i++)
            {
                ran = 0;
                try
                {
                    string got = call();
                    L(name + " call " + i + " returned \"" + Esc(got) + "\" ran=" + ran);
                }
                catch (Exception e)
                {
                    var trace = (e.StackTrace ?? "").Split('\n');
                    L(name + " call " + i + " ran=" + ran + " " + e.GetType().FullName + " \"" + Esc(e.Message) + "\" first frame \""
                        + Esc(trace[0].Trim()) + "\" frames " + trace.Length);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ListAdd() { ran++; var l = new List<Flip>(); l.Add(FlipHost.Make(1)); return "" + l.Count; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string DefaultField() { ran++; Flip f = default; return "" + f.A; }

        /// The cases; with `crashing`, last, the two that crash Unity's player: DefaultField (an
        /// assertion in the JIT, method-to-ir.c:12900, `store_opcode != OP_STOREV_MEMBASE`) and
        /// NewLocal (a signal 11, its constructor called on the local's address).
        public static void Run(Action<string> log, bool crashing)
        {
            L = log;
            Case("UnboxAnyLocal", () => UnboxAnyLocal(FlipHost.Boxed(1)));
            Case("UnboxField", () => UnboxField(FlipHost.Boxed(1)));
            Case("CallLocal", CallLocal);
            Case("CallField", CallField);
            Case("PassThrough", PassThrough);
            Case("StaticFieldMember", StaticFieldMember);
            Case("StaticFieldCopy", StaticFieldCopy);
            Case("InstFieldMember", InstFieldMember);
            Case("NewDirect", NewDirect);
            Case("DefaultLocal", DefaultLocal);
            Case("BoxCall", BoxCall);
            Case("ListField", ListField);
            Case("ListNew", ListNew);
            Case("ListParam", ListParam);
            Case("ArrayElem", ArrayElem);
            Case("ArrayCopy", ArrayCopy);
            Case("OwnStaticField", OwnStaticField);
            Case("OwnParam", OwnParam);
            Case("InstanceCall", InstanceCall);
            Case("OutParam", OutParam);
            Case("RefParam", RefParam);
            Case("ToStringLocal", ToStringLocal);
            Case("Handler", Handler);
            Case("FlopMake", FlopMake);
            Case("FlopRead", FlopRead);
            Case("ListAdd", ListAdd);
            if (crashing) Crashing();
        }

        // Of a method of their own: Mono compiles a method a delegate is made of when it compiles
        // the method making it (ldftn), and the compilation is what crashes.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Crashing()
        {
            Case("DefaultField", DefaultField);
            Case("NewLocal", NewLocal);
        }
    }
}
