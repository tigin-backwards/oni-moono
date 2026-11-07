#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace OniMoono.Host
{
    /// ARCHITECTURE D18, for `--check-types` and the unit tests: several instantiations of a generic
    /// class's method patched, as Fast Track patches `Components.Cmps<T>.GetWorldItems` for each of
    /// the game's lists. Emitted (so that its code is optimized and tiers up whatever this build
    /// is): `Box<T>.World()`, `((object)items[0] as Exception).HResult`, as the game's
    /// `(item as KMonoBehaviour).GetMyWorldId()`. `Box<string>` is patched first: string is sealed
    /// and no Exception, so its replacement's cast is null as CoreCLR compiles it (Cmps<GameObject>
    /// in the game). Six instantiations follow, `Box<ArgumentException>` last; a postfix names the
    /// replacement each call runs. Mono runs the last one detoured for all of them, and so must the
    /// shared code once it is recompiled.
    internal static class SharedGenericsOrderCheck
    {
        static readonly object gate = new();
        static readonly Dictionary<Type, int> ran = new();

        static void Ran(MethodBase __originalMethod)
        {
            var argument = __originalMethod.DeclaringType!.GetGenericArguments()[0];
            lock (gate) ran[argument] = ran.TryGetValue(argument, out var n) ? n + 1 : 1;
        }

        /// What the calls ran, or an exception naming what went wrong.
        public static string Run()
        {
            SharedGenerics.Install();
            var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Host.SharedOrderCheck"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("SharedOrderCheck");
            var box = module.DefineType("Box", TypeAttributes.Public);
            var t = box.DefineGenericParameters("T")[0];
            t.SetGenericParameterAttributes(GenericParameterAttributes.ReferenceTypeConstraint);
            var listOfT = typeof(List<>).MakeGenericType(t);
            var items = box.DefineField("items", listOfT, FieldAttributes.Public);
            box.DefineDefaultConstructor(MethodAttributes.Public);
            var world = box.DefineMethod("World", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
            var il = world.GetILGenerator();
            // The item from a call returning T (List<T>'s indexer, as the game's Items[i]): the JIT
            // takes its type from the call's signature. A field typed T would give it the shared
            // code's field (__Canon), and no cast would be folded.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, items);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, TypeBuilder.GetMethod(listOfT, typeof(List<>).GetMethod("get_Item")!));
            il.Emit(OpCodes.Box, t);
            il.Emit(OpCodes.Isinst, typeof(Exception));
            il.Emit(OpCodes.Callvirt, typeof(Exception).GetProperty(nameof(Exception.HResult))!.GetMethod!);
            il.Emit(OpCodes.Ret);
            var boxType = box.CreateType();
            var called = boxType.MakeGenericType(typeof(ArgumentException));
            var caller = module.DefineType("Caller", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);
            il = caller.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(object)]).GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, called);
            il.Emit(OpCodes.Call, called.GetMethod("World")!);
            il.Emit(OpCodes.Ret);
            var run = caller.CreateType().GetMethod("Run")!.CreateDelegate<Func<object, int>>();

            var harmony = new Harmony("OniMoono.Host.SharedOrderCheck");
            var postfix = new HarmonyMethod(typeof(SharedGenericsOrderCheck), nameof(Ran));
            Type[] order = [typeof(string), typeof(object), typeof(Uri), typeof(System.IO.Stream), typeof(System.Text.Encoding), typeof(Exception), typeof(ArgumentException)];
            foreach (var argument in order)
                harmony.Patch(boxType.MakeGenericType(argument).GetMethod("World"), postfix: postfix);

            var instance = Activator.CreateInstance(called)!;
            var exception = new ArgumentException();
            called.GetField("items")!.SetValue(instance, new List<ArgumentException> { exception });
            lock (gate) ran.Clear();
            int recompiles = SharedGenerics.recompiles, calls = 0, threw = 0, wrong = 0;
            // Tier 1 compiles a method after 30 calls once a 100 ms delay without new tier-0
            // compiles has passed, in the background: 2 s of calls covers it on a loaded machine.
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 2 || calls < 3000)
            {
                for (int i = 0; i < 50; i++, calls++)
                {
                    try
                    {
                        if (run(instance) != exception.HResult) wrong++;
                    }
                    catch (NullReferenceException) { threw++; }
                }
                Thread.Sleep(20);
            }
            int recompiled = SharedGenerics.recompiles - recompiles;
            string Report()
            {
                lock (gate)
                {
                    var parts = new List<string>();
                    foreach (var (argument, n) in ran) parts.Add($"Box<{argument.Name}>'s {n}");
                    return string.Join(", ", parts);
                }
            }
            int last;
            lock (gate) last = ran.TryGetValue(typeof(ArgumentException), out var n) ? n : 0;
            if (recompiled == 0) throw new InvalidOperationException($"the shared code was not recompiled in {calls} calls");
            if (threw > 0 || wrong > 0 || last != calls)
                throw new InvalidOperationException($"of {calls} calls of Box<ArgumentException>.World, {threw} threw NullReferenceException and {wrong} returned another value; replacements run: {Report()}");
            return $"every call ran the replacement patched last, after the shared code was recompiled {recompiled} time(s)";
        }
    }
}
