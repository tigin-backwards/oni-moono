// Delegates to instance methods with a null target (lines "ND ..."): what the player's runtime
// does when code builds one (C#'s `new D(nullObj.Method)`, an ldftn and a newobj; an ldvirtftn on
// null; Delegate.CreateDelegate with a null first argument), what invoking it does (a method that
// never reads `this`, one that reads a field, one that calls a virtual method on `this`), and
// what such a delegate reports (Target, Method, Equals, GetHashCode) and gives when combined.
// The game's ManagementMenu.OnPrefabInit builds `clusterMapScreen.TryHandleCancel` with
// clusterMapScreen null when a base-game save is loaded into a Spaced Out session.
//
// Each line is "ND <case> = <result>", an exception by its type name.
//
// Run alone with PROBE_SCENARIO=nulldelegate (at mod load).
//
// Measured 2026-11-05 (U59 and U60 originals on Unity's MonoBleedingEdge, Linux amd64; ours on
// .NET 10 CoreCLR): see docs/ARCHITECTURE.md D62.
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SemanticsProbe
{
    public class NullTarget
    {
        public static int Ran;
        public int Field = 7;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void NoThis() { Ran++; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadsField() { Ran++; return Field; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string ThisIsNull() { Ran++; return ((object)this == null) ? "this==null" : "this!=null"; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int CallsVirtual() { Ran++; return Virtual(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int AddOne(int x) { Ran++; return x + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual int Virtual() { Ran++; return 5; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public T Generic<T>(T x) { Ran++; return x; }
    }

    public struct NullStruct
    {
        public int Field;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void NoThis() { NullTarget.Ran++; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadsField() { NullTarget.Ran++; return Field; }
    }

    public static class NullDelegate
    {
        public static NullTarget nothing = null;

        static void L(string s) => Rec.Log("ND " + s);

        static Exception last;

        static string Name(Exception e) => e.GetType().Name;

        static void Line(string label, Func<string> what)
        {
            int before = NullTarget.Ran;
            string r;
            try { r = what(); }
            catch (Exception e) { r = Name(e); last = e; }
            L(label + " = " + r + " ran=" + (NullTarget.Ran - before));
        }

        // The IL sites, each in a method of its own the JIT may not inline.
        [MethodImpl(MethodImplOptions.NoInlining)] static System.Action NewNoThis(NullTarget t) => new System.Action(t.NoThis);
        [MethodImpl(MethodImplOptions.NoInlining)] static Func<int> NewReadsField(NullTarget t) => t.ReadsField;
        [MethodImpl(MethodImplOptions.NoInlining)] static Func<string> NewThisIsNull(NullTarget t) => t.ThisIsNull;
        [MethodImpl(MethodImplOptions.NoInlining)] static Func<int> NewCallsVirtual(NullTarget t) => t.CallsVirtual;
        [MethodImpl(MethodImplOptions.NoInlining)] static Func<int, int> NewAddOne(NullTarget t) => t.AddOne;
        [MethodImpl(MethodImplOptions.NoInlining)] static Func<int> NewVirtual(NullTarget t) => t.Virtual;  // ldvirtftn
        [MethodImpl(MethodImplOptions.NoInlining)] static Func<int, int> NewGeneric(NullTarget t) => t.Generic<int>;
        [MethodImpl(MethodImplOptions.NoInlining)] static Func<string, string> NewGenericRef(NullTarget t) => t.Generic<string>;
        [MethodImpl(MethodImplOptions.NoInlining)] static System.Action NewField() => nothing.NoThis;

        static string Describe(Delegate d) =>
            d == null ? "null" : "made target=" + (d.Target == null ? "null" : d.Target.GetType().Name) + " method=" + (d.Method == null ? "null" : d.Method.Name);

        /// ldnull; ldftn method; newobj type::.ctor(object, native int); ret, in a dynamic method.
        static Delegate Emitted(Type type, MethodInfo method, bool virtualFtn)
        {
            var dm = new DynamicMethod("NullFtn_" + method.Name, type, Type.EmptyTypes, typeof(NullDelegate).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldnull);
            if (virtualFtn)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldvirtftn, method);
            }
            else il.Emit(OpCodes.Ldftn, method);
            il.Emit(OpCodes.Newobj, type.GetConstructor(new[] { typeof(object), typeof(IntPtr) }));
            il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(typeof(Func<>).MakeGenericType(type)) is Delegate maker ? (Delegate)maker.DynamicInvoke() : null;
        }

        static Exception Inner(Exception e) => e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;

        public static void Run()
        {
            var noThis = typeof(NullTarget).GetMethod("NoThis");
            var readsField = typeof(NullTarget).GetMethod("ReadsField");
            var virt = typeof(NullTarget).GetMethod("Virtual");

            // Construction (ldftn + newobj from C#).
            Line("new NoThis", () => Describe(NewNoThis(null)));
            Line("new ReadsField", () => Describe(NewReadsField(null)));
            Line("new from a null field", () => Describe(NewField()));
            Line("new Generic<int>", () => Describe(NewGeneric(null)));
            Line("new Generic<string>", () => Describe(NewGenericRef(null)));
            Line("new Virtual (ldvirtftn)", () => Describe(NewVirtual(null)));
            Line("emitted ldftn NoThis", () => { try { return Describe(Emitted(typeof(System.Action), noThis, false)); } catch (Exception e) { return Name(Inner(e)); } });
            Line("emitted ldftn Virtual", () => { try { return Describe(Emitted(typeof(Func<int>), virt, false)); } catch (Exception e) { return Name(Inner(e)); } });
            Line("emitted ldvirtftn Virtual", () => { try { return Describe(Emitted(typeof(Func<int>), virt, true)); } catch (Exception e) { return Name(Inner(e)); } });
            Line("emitted ldftn struct NoThis", () => { try { return Describe(Emitted(typeof(System.Action), typeof(NullStruct).GetMethod("NoThis"), false)); } catch (Exception e) { return Name(Inner(e)); } });

            // Invocation.
            Line("invoke NoThis", () => { var d = NewNoThis(null); d(); return "returned"; });
            Line("invoke ReadsField", () => NewReadsField(null)().ToString());
            Line("invoke ThisIsNull", () => NewThisIsNull(null)());
            Line("invoke CallsVirtual", () => NewCallsVirtual(null)().ToString());
            Line("invoke AddOne(41)", () => NewAddOne(null)(41).ToString());
            Line("invoke Generic<int>(3)", () => NewGeneric(null)(3).ToString());
            Line("invoke Generic<string>(s)", () => NewGenericRef(null)("s"));
            Line("DynamicInvoke NoThis", () => { try { NewNoThis(null).DynamicInvoke(); return "returned"; } catch (Exception e) { return "threw " + Name(e) + "/" + Name(Inner(e)); } });
            Line("BeginInvoke-free Invoke NoThis", () => { NewNoThis(null).Invoke(); return "returned"; });
            Line("invoke emitted ldftn Virtual", () => { try { return ((Func<int>)Emitted(typeof(Func<int>), virt, false))().ToString(); } catch (Exception e) { return Name(Inner(e)); } });
            Line("invoke emitted struct NoThis", () => { try { ((System.Action)Emitted(typeof(System.Action), typeof(NullStruct).GetMethod("NoThis"), false))(); return "returned"; } catch (Exception e) { return Name(Inner(e)); } });
            Line("invoke emitted struct ReadsField", () => { try { return ((Func<int>)Emitted(typeof(Func<int>), typeof(NullStruct).GetMethod("ReadsField"), false))().ToString(); } catch (Exception e) { return Name(Inner(e)); } });

            // What it reports.
            Line("Target", () => { var d = NewNoThis(null); return d.Target == null ? "null" : "set"; });
            Line("Method", () => NewNoThis(null).Method.ToString() + " of " + NewNoThis(null).Method.DeclaringType.Name);
            Line("Method == GetMethod", () => (NewNoThis(null).Method == noThis).ToString());
            Line("Equals another null one", () => NewNoThis(null).Equals(NewNoThis(null)).ToString());
            Line("== another null one", () => (NewNoThis(null) == NewNoThis(null)).ToString());
            Line("Equals one with a target", () => NewNoThis(null).Equals(NewNoThis(new NullTarget())).ToString());
            Line("Equals other method", () => NewNoThis(null).Equals((System.Action)(() => { })).ToString());
            Line("GetHashCode same as another null one", () => (NewNoThis(null).GetHashCode() == NewNoThis(null).GetHashCode()).ToString());
            Line("GetHashCode same as one with a target", () => (NewNoThis(null).GetHashCode() == NewNoThis(new NullTarget()).GetHashCode()).ToString());
            Line("GetHashCode same as a static one", () => (NewNoThis(null).GetHashCode() == ((System.Action)StaticNoThis).GetHashCode()).ToString());
            Line("GetHashCode same as AddOne's", () => (NewNoThis(null).GetHashCode() == NewAddOne(null).GetHashCode()).ToString());
            Line("GetHashCode of the type", () => (NewNoThis(null).GetHashCode() == typeof(System.Action).GetHashCode()).ToString());
            Line("GetInvocationList", () => NewNoThis(null).GetInvocationList().Length.ToString());
            Line("Clone equal", () => NewNoThis(null).Clone().Equals(NewNoThis(null)).ToString());

            // Combining.
            Line("combine null + null, invoke", () => { System.Action c = NewNoThis(null); c += NewNoThis(null); c(); return c.GetInvocationList().Length.ToString(); });
            Line("combine target + null, invoke", () => { System.Action c = NewNoThis(new NullTarget()); c += NewNoThis(null); c(); return c.GetInvocationList().Length.ToString(); });
            Line("combine then -= another null one", () => { System.Action c = NewNoThis(null); c += NewNoThis(new NullTarget()); c -= NewNoThis(null); return c == null ? "null" : c.GetInvocationList().Length + " target=" + (c.Target == null ? "null" : "set"); });
            Line("null - itself", () => { System.Action c = NewNoThis(null); c -= NewNoThis(null); return c == null ? "null" : "left"; });
            Line("event-style null += null-target", () => { System.Action c = null; c += NewNoThis(null); return c == null ? "null" : c.GetInvocationList().Length.ToString(); });

            // Delegate.CreateDelegate.
            Line("CreateDelegate(type, null, NoThis)", () => Describe(Delegate.CreateDelegate(typeof(System.Action), null, noThis)));
            Line("CreateDelegate(type, null, NoThis, false)", () => Describe(Delegate.CreateDelegate(typeof(System.Action), null, noThis, false)));
            Line("CreateDelegate(type, null, Virtual)", () => Describe(Delegate.CreateDelegate(typeof(Func<int>), null, virt)));
            Line("CreateDelegate(type, (object)null, \"NoThis\")", () => Describe(Delegate.CreateDelegate(typeof(System.Action), (object)null, "NoThis")));
            Line("CreateDelegate(Action<T>, null, NoThis) open", () => Describe(Delegate.CreateDelegate(typeof(System.Action<NullTarget>), null, noThis)));
            Line("CreateDelegate(type, NoThis) static overload", () => Describe(Delegate.CreateDelegate(typeof(System.Action), noThis)));
            Line("MethodInfo.CreateDelegate(type, null)", () => Describe(noThis.CreateDelegate(typeof(System.Action), null)));
            Line("invoke CreateDelegate NoThis", () => { ((System.Action)Delegate.CreateDelegate(typeof(System.Action), null, noThis))(); return "returned"; });
            Line("invoke CreateDelegate ReadsField", () => ((Func<int>)Delegate.CreateDelegate(typeof(Func<int>), null, readsField))().ToString());
            Line("invoke CreateDelegate Virtual", () => ((Func<int>)Delegate.CreateDelegate(typeof(Func<int>), null, virt))().ToString());
            Line("CreateDelegate NoThis Equals new NoThis", () => Delegate.CreateDelegate(typeof(System.Action), null, noThis).Equals(NewNoThis(null)).ToString());
            // The construction's exception in full, once.
            last = null;
            try { NewNoThis(null); } catch (Exception e) { last = e; }
            if (last != null) L("message " + last.GetType().FullName + ": " + last.Message.Replace("\n", " | ") + " | at " + last.StackTrace.Replace("\n", " | "));
            L("done");
        }

        static void StaticNoThis() { }
    }
}
