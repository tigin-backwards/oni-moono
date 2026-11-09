#nullable enable
// FormatterServices is what the game's serializer and mods call; Mono's answers for it.
#pragma warning disable SYSLIB0050
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;

namespace OniMoono.Host
{
    /// ARCHITECTURE D63. Mono's FormatterServices.GetUninitializedObject (and
    /// GetSafeUninitializedObject) allocates any class that is neither an interface nor abstract
    /// (ActivationServices.AllocateUninitializedClassInstance, metadata/icall.c), delegates
    /// included: the delegate has no method, and invoking it throws NullReferenceException.
    /// CoreCLR refuses a delegate type with ArgumentException ("Type is not supported."). The
    /// game's serializer makes a [Serialize] field's object that way when its type has no
    /// parameterless constructor (KSerialization.DeserializationMapping.ReadValue), and a care
    /// package saved before it opens holds its CarePackageInfo, whose `requirement` is a
    /// Func&lt;bool&gt;: the save stopped loading at the package, and the half-loaded world then
    /// read the grid before it was made and ended the player (issue #69). A delegate type is
    /// answered with a new delegate of that type whose method throws NullReferenceException;
    /// unlike Mono's, its Method is that stub rather than null.
    internal static class MonoUninitializedObjects
    {
        static Harmony? harmony;
        static readonly ConcurrentDictionary<Type, MethodInfo> stubs = new();
        static readonly Type runtimeType = typeof(object).GetType();

        public static void Install()
        {
            if (harmony != null) return;
            harmony = new Harmony("OniMoono.Host.MonoUninitializedObjects");
            var prefix = new HarmonyMethod(typeof(MonoUninitializedObjects), nameof(Prefix));
            foreach (var name in new[] { nameof(FormatterServices.GetUninitializedObject), nameof(FormatterServices.GetSafeUninitializedObject) })
            {
                var method = typeof(FormatterServices).GetMethod(name, BindingFlags.Public | BindingFlags.Static, [typeof(Type)])
                    ?? throw new MissingMethodException(typeof(FormatterServices).FullName, name);
                harmony.Patch(method, prefix: prefix);
            }
        }

        static bool Prefix(Type type, ref object __result)
        {
            if (type is null || type.GetType() != runtimeType || type.IsAbstract || type.ContainsGenericParameters || !type.IsSubclassOf(typeof(Delegate))) return true;
            __result = stubs.GetOrAdd(type, Stub).CreateDelegate(type);
            return false;
        }

        /// A static method of the delegate's signature that throws NullReferenceException, as
        /// invoking Mono's delegate without a method does.
        static MethodInfo Stub(Type type)
        {
            var invoke = type.GetMethod("Invoke") ?? throw new MissingMethodException(type.FullName, "Invoke");
            var method = new DynamicMethod("Invoke", invoke.ReturnType, invoke.GetParameters().Select(p => p.ParameterType).ToArray(),
                typeof(MonoUninitializedObjects).Module, skipVisibility: true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Newobj, typeof(NullReferenceException).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Throw);
            return method;
        }
    }
}
