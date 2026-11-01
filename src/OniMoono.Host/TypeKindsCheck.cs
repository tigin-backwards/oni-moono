using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Host
{
    /// `--check-types`: a mod built against a library whose struct is now a class, and whose class
    /// is now a struct (MonoTypeKinds, D56). The library is built twice: as the mod was built
    /// against it (Flip a struct, Flop a class), and as it loads (the other way round). Each of the
    /// mod's methods is one IL pattern of UnityProbe's kindchange cases; it gives what the
    /// original gave there, or, where the original read unrelated memory or crashed, the class's
    /// value.
    internal static class TypeKindsCheck
    {
        const string Ns = "OniMoonoCheckKinds";

        public static string Run()
        {
            ModAccess.Install();
            var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
            try
            {
                var libPath = Path.Combine(dir, Ns + "Lib.dll");
                Library(libPath, now: true);
                var lib = ModAccess.LoadFrom(libPath);
                var built = Path.Combine(dir, "built");
                Directory.CreateDirectory(built);
                // The mod is built against the library as it was.
                using var old = Library(Path.Combine(built, Ns + "Lib.dll"), now: false);
                var modPath = Path.Combine(dir, Ns + "Mod.dll");
                var tokens = Mod(modPath, old.MainModule, skipVerification: false);
                var skipPath = Path.Combine(dir, Ns + "SkipMod.dll");
                Mod(skipPath, old.MainModule, skipVerification: true);
                var probe = ModAccess.LoadFrom(modPath).GetType("Probe", throwOnError: true)!;
                var skipping = ModAccess.LoadFrom(skipPath).GetType("Probe", throwOnError: true)!;
                var boxed = lib.GetType(Ns + ".FlipHost")!.GetMethod("Boxed")!.Invoke(null, [1]);

                object? Returns(Type type, string name, params object?[] args) => type.GetMethod(name)!.Invoke(null, BindingFlags.DoNotWrapExceptions, null, args, null);
                Exception Fails(string name, Type expected, string message, params object?[] args)
                {
                    try
                    {
                        Returns(probe, name, args);
                    }
                    catch (Exception e) when (e.GetType() == expected)
                    {
                        if (e.Message != message) throw new InvalidOperationException($"{name}: \"{e.Message}\", not \"{message}\"");
                        return e;
                    }
                    throw new InvalidOperationException($"{name} did not throw {expected.Name}");
                }
                void Gives(Type type, string name, object expected, params object?[] args)
                {
                    var got = Returns(type, name, args);
                    if (!Equals(got, expected)) throw new InvalidOperationException($"{name}: {got ?? "null"}, not {expected}");
                }
                string Invalid(string method, string at) => $"Invalid IL code in Probe:{method}: {at}\n\n";

                // As Smart Radbolt Storage's OnLogicValueChanged: unbox.any into the local, from the
                // caller's frame.
                var unboxed = Fails("Outer", typeof(InvalidProgramException), Invalid("UnboxAnyLocal (object)", "IL_0006: stloc.0   "), boxed);
                var first = unboxed.StackTrace!.Split('\n')[0].Trim();
                if (!first.StartsWith("at Probe.Outer(", StringComparison.Ordinal)) throw new InvalidOperationException("UnboxAnyLocal's first frame: " + first);
                Fails("CallLocal", typeof(InvalidProgramException), Invalid("CallLocal ()", "IL_0006: stloc.0   "));
                Fails("DefaultLocal", typeof(InvalidProgramException), Invalid("DefaultLocal ()", $"IL_0009: call      0x{tokens["Read"]:x8}"));
                Fails("OwnStaticField", typeof(InvalidProgramException), Invalid("OwnStaticField ()", $"IL_0010: call      0x{tokens["Read"]:x8}"));
                Fails("ListAdd", typeof(InvalidProgramException), Invalid("ListAdd ()", $"IL_000d: callvirt  0x{tokens["Add"]:x8}"));
                Fails("ListField", typeof(MissingFieldException), $"Field not found: System.Collections.Generic.List`1<{Ns}.Flip> {Ns}.FlipHost.List Due to: Could not find field in class");
                Fails("ClassTagged", typeof(BadImageFormatException), "Expected reference type but got type kind 17");
                // What runs on Mono runs here.
                Gives(probe, "PassThrough", 8);
                Gives(probe, "BoxCall", "Flip1");
                Gives(probe, "ListNew", 0);
                Gives(probe, "OwnParam", 4);
                // Where Mono reads unrelated memory or crashes, the class's members.
                Gives(probe, "StaticFieldMember", 3);
                Gives(probe, "InstFieldMember", 4);
                Gives(probe, "ArrayElem", 7);
                Gives(probe, "NewLocal", 9);
                Gives(probe, "UnboxField", 1, boxed);
                // An assembly that skips verification: Mono stores the object into the local
                // unchecked (and then fails an assertion); here it runs as the class.
                Gives(skipping, "UnboxAnyLocal", 1, boxed);
                Gives(skipping, "NewLocal", 9);
                return $"stores and arguments Mono's JIT refuses throw its InvalidProgramException from the caller's frame ({first}), a generic argument tagged so is another instantiation, a class tag for a struct fails; the rest runs as the class";
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// The library: Flip and Flop as a struct and a class (`now` false, as the mod was built
        /// against it) or the other way round, and FlipHost's members naming them.
        static Cecil.AssemblyDefinition Library(string path, bool now)
        {
            var name = Ns + "Lib";
            var assembly = Cecil.AssemblyDefinition.CreateAssembly(new Cecil.AssemblyNameDefinition(name, new Version(1, 0)), name, Cecil.ModuleKind.Dll);
            var module = assembly.MainModule;
            var ts = module.TypeSystem;
            var valueType = module.ImportReference(typeof(ValueType));
            Cecil.TypeDefinition Kind(string typeName, bool isStruct)
            {
                var attributes = Cecil.TypeAttributes.Public | Cecil.TypeAttributes.BeforeFieldInit | (isStruct ? Cecil.TypeAttributes.Sealed | Cecil.TypeAttributes.SequentialLayout : 0);
                var type = new Cecil.TypeDefinition(Ns, typeName, attributes, isStruct ? valueType : ts.Object);
                module.Types.Add(type);
                return type;
            }
            var flip = Kind("Flip", isStruct: !now);
            var a = new Cecil.FieldDefinition("A", Cecil.FieldAttributes.Public, ts.Int32);
            flip.Fields.Add(a);
            var ctor = Constructor(module, flip, il =>
            {
                il.Emit(Cil.OpCodes.Ldarg_0);
                il.Emit(Cil.OpCodes.Ldarg_1);
                il.Emit(Cil.OpCodes.Stfld, a);
            }, ts.Int32);
            var toString = new Cecil.MethodDefinition("ToString", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Virtual | Cecil.MethodAttributes.HideBySig, ts.String);
            flip.Methods.Add(toString);
            {
                var il = toString.Body.GetILProcessor();
                il.Emit(Cil.OpCodes.Ldstr, "Flip");
                il.Emit(Cil.OpCodes.Ldarg_0);
                il.Emit(Cil.OpCodes.Ldflda, a);
                il.Emit(Cil.OpCodes.Call, module.ImportReference(typeof(int).GetMethod("ToString", Type.EmptyTypes)!));
                il.Emit(Cil.OpCodes.Call, module.ImportReference(typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!));
                il.Emit(Cil.OpCodes.Ret);
            }
            var flop = Kind("Flop", isStruct: now);
            var flopA = new Cecil.FieldDefinition("A", Cecil.FieldAttributes.Public, ts.Int32);
            flop.Fields.Add(flopA);
            if (!now) Constructor(module, flop, _ => { });
            var host = Kind("FlipHost", isStruct: false);
            Constructor(module, host, _ => { });
            var list = new Cecil.GenericInstanceType(module.ImportReference(typeof(List<>)));
            list.GenericArguments.Add(flip);
            var staticAttributes = Cecil.FieldAttributes.Public | Cecil.FieldAttributes.Static;
            var field = new Cecil.FieldDefinition("Field", staticAttributes, flip);
            var inst = new Cecil.FieldDefinition("Inst", Cecil.FieldAttributes.Public, flip);
            var array = new Cecil.FieldDefinition("Arr", staticAttributes, new Cecil.ArrayType(flip));
            var listField = new Cecil.FieldDefinition("List", staticAttributes, list);
            foreach (var f in new[] { field, inst, array, listField }) host.Fields.Add(f);
            Cecil.MethodDefinition Static(string methodName, Cecil.TypeReference returns, Action<Cil.ILProcessor> body, params Cecil.TypeReference[] parameters)
            {
                var method = new Cecil.MethodDefinition(methodName, Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, returns);
                foreach (var parameter in parameters) method.Parameters.Add(new Cecil.ParameterDefinition(parameter));
                host.Methods.Add(method);
                body(method.Body.GetILProcessor());
                return method;
            }
            void New(Cil.ILProcessor il, int value) { il.Emit(Cil.OpCodes.Ldc_I4, value); il.Emit(Cil.OpCodes.Newobj, ctor); }
            var cctor = new Cecil.MethodDefinition(".cctor", Cecil.MethodAttributes.Private | Cecil.MethodAttributes.Static | Cecil.MethodAttributes.HideBySig
                | Cecil.MethodAttributes.SpecialName | Cecil.MethodAttributes.RTSpecialName, ts.Void);
            host.Methods.Add(cctor);
            {
                var il = cctor.Body.GetILProcessor();
                New(il, 3);
                il.Emit(Cil.OpCodes.Stsfld, field);
                il.Emit(Cil.OpCodes.Ldc_I4_1);
                il.Emit(Cil.OpCodes.Newarr, flip);
                il.Emit(Cil.OpCodes.Dup);
                il.Emit(Cil.OpCodes.Ldc_I4_0);
                New(il, 7);
                il.Emit(Cil.OpCodes.Stelem_Ref);
                il.Emit(Cil.OpCodes.Stsfld, array);
                il.Emit(Cil.OpCodes.Newobj, new Cecil.MethodReference(".ctor", ts.Void, list) { HasThis = true });
                il.Emit(Cil.OpCodes.Stsfld, listField);
                il.Emit(Cil.OpCodes.Ret);
            }
            // Inst is 4 in a new FlipHost.
            var hostCtor = host.Methods.Single(m => m.Name == ".ctor");
            if (now)
            {
                var il = hostCtor.Body.GetILProcessor();
                var ret = hostCtor.Body.Instructions.Last();
                il.InsertBefore(ret, il.Create(Cil.OpCodes.Ldarg_0));
                il.InsertBefore(ret, il.Create(Cil.OpCodes.Ldc_I4_4));
                il.InsertBefore(ret, il.Create(Cil.OpCodes.Newobj, ctor));
                il.InsertBefore(ret, il.Create(Cil.OpCodes.Stfld, inst));
            }
            Static("Make", flip, il => { il.Emit(Cil.OpCodes.Ldarg_0); il.Emit(Cil.OpCodes.Newobj, ctor); il.Emit(Cil.OpCodes.Ret); }, ts.Int32);
            Static("Boxed", ts.Object, il => { il.Emit(Cil.OpCodes.Ldarg_0); il.Emit(Cil.OpCodes.Newobj, ctor); if (!now) il.Emit(Cil.OpCodes.Box, flip); il.Emit(Cil.OpCodes.Ret); }, ts.Int32);
            Static("Read", ts.Int32, il =>
            {
                if (now)
                {
                    var some = il.Create(Cil.OpCodes.Ldarg_0);
                    il.Emit(Cil.OpCodes.Ldarg_0);
                    il.Emit(Cil.OpCodes.Brtrue, some);
                    il.Emit(Cil.OpCodes.Ldc_I4_M1);
                    il.Emit(Cil.OpCodes.Ret);
                    il.Append(some);
                }
                else il.Emit(Cil.OpCodes.Ldarga_S, il.Body.Method.Parameters[0]);
                il.Emit(Cil.OpCodes.Ldfld, a);
                il.Emit(Cil.OpCodes.Ret);
            }, flip);
            Static("MakeFlop", flop, il => { il.Emit(Cil.OpCodes.Ldnull); il.Emit(Cil.OpCodes.Ret); }, ts.Int32);
            if (now)
            {
                // A struct's MakeFlop returns a default Flop.
                var make = host.Methods.Single(m => m.Name == "MakeFlop");
                make.Body.Instructions.Clear();
                make.Body.Variables.Add(new Cil.VariableDefinition(flop));
                var il = make.Body.GetILProcessor();
                il.Emit(Cil.OpCodes.Ldloca_S, make.Body.Variables[0]);
                il.Emit(Cil.OpCodes.Initobj, flop);
                il.Emit(Cil.OpCodes.Ldloc_0);
                il.Emit(Cil.OpCodes.Ret);
            }
            assembly.Write(path);
            return assembly;
        }

        static Cecil.MethodDefinition Constructor(Cecil.ModuleDefinition module, Cecil.TypeDefinition type, Action<Cil.ILProcessor> body, params Cecil.TypeReference[] parameters)
        {
            var ctor = new Cecil.MethodDefinition(".ctor", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.HideBySig | Cecil.MethodAttributes.SpecialName | Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            foreach (var parameter in parameters) ctor.Parameters.Add(new Cecil.ParameterDefinition(parameter));
            type.Methods.Add(ctor);
            var il = ctor.Body.GetILProcessor();
            if (!type.IsValueType)
            {
                il.Emit(Cil.OpCodes.Ldarg_0);
                il.Emit(Cil.OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            }
            body(il);
            il.Emit(Cil.OpCodes.Ret);
            return ctor;
        }

        /// The mod, built against the library as it was: one method per IL pattern. Returns the
        /// tokens Mono's messages name.
        static Dictionary<string, int> Mod(string path, Cecil.ModuleDefinition built, bool skipVerification)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            using var assembly = Cecil.AssemblyDefinition.CreateAssembly(new Cecil.AssemblyNameDefinition(name, new Version(1, 0)), name, Cecil.ModuleKind.Dll);
            var module = assembly.MainModule;
            var ts = module.TypeSystem;
            if (skipVerification)
            {
                // What C# emits for a build with unsafe code allowed.
                var declaration = new Cecil.SecurityDeclaration(Cecil.SecurityAction.RequestMinimum);
                var permission = new Cecil.SecurityAttribute(new Cecil.TypeReference("System.Security.Permissions", "SecurityPermissionAttribute", module, ts.CoreLibrary));
                permission.Properties.Add(new Cecil.CustomAttributeNamedArgument("SkipVerification", new Cecil.CustomAttributeArgument(ts.Boolean, true)));
                declaration.SecurityAttributes.Add(permission);
                assembly.SecurityDeclarations.Add(declaration);
            }
            Cecil.TypeDefinition Type(string typeName) => built.GetType(Ns + "." + typeName);
            var flip = module.ImportReference(Type("Flip"));
            var host = Type("FlipHost");
            Cecil.MethodReference Host(string methodName) => module.ImportReference(host.Methods.Single(m => m.Name == methodName));
            Cecil.FieldReference HostField(string fieldName) => module.ImportReference(host.Fields.Single(f => f.Name == fieldName));
            var flipDefinition = Type("Flip");
            var a = module.ImportReference(flipDefinition.Fields.Single(f => f.Name == "A"));
            var ctor = module.ImportReference(flipDefinition.Methods.Single(m => m.Name == ".ctor"));
            var make = Host("Make");
            var read = Host("Read");
            var list = new Cecil.GenericInstanceType(module.ImportReference(typeof(List<>)));
            list.GenericArguments.Add(flip);
            var listCtor = new Cecil.MethodReference(".ctor", ts.Void, list) { HasThis = true };
            var count = new Cecil.MethodReference("get_Count", ts.Int32, list) { HasThis = true };
            var add = new Cecil.MethodReference("Add", ts.Void, list) { HasThis = true };
            var definition = list.ElementType;
            if (definition.GenericParameters.Count == 0) definition.GenericParameters.Add(new Cecil.GenericParameter("T", definition));
            add.Parameters.Add(new Cecil.ParameterDefinition(definition.GenericParameters[0]));
            var probe = new Cecil.TypeDefinition("", "Probe", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, ts.Object);
            module.Types.Add(probe);
            var own = new Cecil.FieldDefinition("own", Cecil.FieldAttributes.Private | Cecil.FieldAttributes.Static, flip);
            probe.Fields.Add(own);
            Cecil.MethodDefinition Method(string methodName, Cecil.TypeReference returns, Action<Cil.ILProcessor, Cil.MethodBody> body, params Cecil.TypeReference[] parameters)
            {
                var method = new Cecil.MethodDefinition(methodName, Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, returns);
                foreach (var parameter in parameters) method.Parameters.Add(new Cecil.ParameterDefinition(parameter));
                probe.Methods.Add(method);
                body(method.Body.GetILProcessor(), method.Body);
                return method;
            }
            Cil.VariableDefinition Local(Cil.MethodBody body, Cecil.TypeReference type)
            {
                var variable = new Cil.VariableDefinition(type);
                body.Variables.Add(variable);
                body.InitLocals = true;
                return variable;
            }
            // IL_0000 ldarg.0, IL_0001 unbox.any, IL_0006 stloc.0.
            var unboxAny = Method("UnboxAnyLocal", ts.Int32, (il, body) =>
            {
                Local(body, flip);
                il.Emit(Cil.OpCodes.Ldarg_0);
                il.Emit(Cil.OpCodes.Unbox_Any, flip);
                il.Emit(Cil.OpCodes.Stloc_0);
                il.Emit(Cil.OpCodes.Ldloc_0);
                il.Emit(Cil.OpCodes.Ldfld, a);
                il.Emit(Cil.OpCodes.Ret);
            }, ts.Object);
            Method("Outer", ts.Int32, (il, _) => { il.Emit(Cil.OpCodes.Ldarg_0); il.Emit(Cil.OpCodes.Call, unboxAny); il.Emit(Cil.OpCodes.Ret); }, ts.Object);
            // IL_0000 ldc.i4.2, IL_0001 call, IL_0006 stloc.0.
            Method("CallLocal", ts.Int32, (il, body) =>
            {
                Local(body, flip);
                il.Emit(Cil.OpCodes.Ldc_I4_2);
                il.Emit(Cil.OpCodes.Call, make);
                il.Emit(Cil.OpCodes.Stloc_0);
                il.Emit(Cil.OpCodes.Ldloca_S, body.Variables[0]);
                il.Emit(Cil.OpCodes.Ldfld, a);
                il.Emit(Cil.OpCodes.Ret);
            });
            // IL_0000 ldloca.s 0, IL_0002 initobj, IL_0008 ldloc.0, IL_0009 call.
            Method("DefaultLocal", ts.Int32, (il, body) =>
            {
                var f = Local(body, flip);
                il.Emit(Cil.OpCodes.Ldloca_S, f);
                il.Emit(Cil.OpCodes.Initobj, flip);
                il.Emit(Cil.OpCodes.Ldloc_0);
                il.Emit(Cil.OpCodes.Call, read);
                il.Emit(Cil.OpCodes.Ret);
            });
            // IL_0000 ldc.i4.3, IL_0001 call, IL_0006 stsfld, IL_000b ldsfld, IL_0010 call.
            Method("OwnStaticField", ts.Int32, (il, _) =>
            {
                il.Emit(Cil.OpCodes.Ldc_I4_3);
                il.Emit(Cil.OpCodes.Call, make);
                il.Emit(Cil.OpCodes.Stsfld, own);
                il.Emit(Cil.OpCodes.Ldsfld, own);
                il.Emit(Cil.OpCodes.Call, read);
                il.Emit(Cil.OpCodes.Ret);
            });
            // IL_0000 newobj, IL_0005 stloc.0, IL_0006 ldloc.0, IL_0007 ldc.i4.1, IL_0008 call, IL_000d callvirt.
            Method("ListAdd", ts.Int32, (il, body) =>
            {
                Local(body, list);
                il.Emit(Cil.OpCodes.Newobj, listCtor);
                il.Emit(Cil.OpCodes.Stloc_0);
                il.Emit(Cil.OpCodes.Ldloc_0);
                il.Emit(Cil.OpCodes.Ldc_I4_1);
                il.Emit(Cil.OpCodes.Call, make);
                il.Emit(Cil.OpCodes.Callvirt, add);
                il.Emit(Cil.OpCodes.Ldloc_0);
                il.Emit(Cil.OpCodes.Callvirt, count);
                il.Emit(Cil.OpCodes.Ret);
            });
            Method("ListField", ts.Int32, (il, _) => { il.Emit(Cil.OpCodes.Ldsfld, HostField("List")); il.Emit(Cil.OpCodes.Callvirt, count); il.Emit(Cil.OpCodes.Ret); });
            Method("ClassTagged", ts.Int32, (il, _) => { il.Emit(Cil.OpCodes.Ldc_I4_1); il.Emit(Cil.OpCodes.Call, Host("MakeFlop")); il.Emit(Cil.OpCodes.Pop); il.Emit(Cil.OpCodes.Ldc_I4_0); il.Emit(Cil.OpCodes.Ret); });
            Method("PassThrough", ts.Int32, (il, _) => { il.Emit(Cil.OpCodes.Ldc_I4_8); il.Emit(Cil.OpCodes.Call, make); il.Emit(Cil.OpCodes.Call, read); il.Emit(Cil.OpCodes.Ret); });
            Method("BoxCall", ts.String, (il, _) =>
            {
                il.Emit(Cil.OpCodes.Ldc_I4_1);
                il.Emit(Cil.OpCodes.Call, make);
                il.Emit(Cil.OpCodes.Box, flip);
                il.Emit(Cil.OpCodes.Callvirt, module.ImportReference(typeof(object).GetMethod("ToString")!));
                il.Emit(Cil.OpCodes.Ret);
            });
            Method("ListNew", ts.Int32, (il, _) => { il.Emit(Cil.OpCodes.Newobj, listCtor); il.Emit(Cil.OpCodes.Callvirt, count); il.Emit(Cil.OpCodes.Ret); });
            var take = Method("Take", ts.Int32, (il, _) => { il.Emit(Cil.OpCodes.Ldarg_0); il.Emit(Cil.OpCodes.Call, read); il.Emit(Cil.OpCodes.Ret); }, flip);
            Method("OwnParam", ts.Int32, (il, _) => { il.Emit(Cil.OpCodes.Ldc_I4_4); il.Emit(Cil.OpCodes.Call, make); il.Emit(Cil.OpCodes.Call, take); il.Emit(Cil.OpCodes.Ret); });
            Method("StaticFieldMember", ts.Int32, (il, _) => { il.Emit(Cil.OpCodes.Ldsflda, HostField("Field")); il.Emit(Cil.OpCodes.Ldfld, a); il.Emit(Cil.OpCodes.Ret); });
            Method("InstFieldMember", ts.Int32, (il, _) =>
            {
                il.Emit(Cil.OpCodes.Newobj, module.ImportReference(host.Methods.Single(m => m.Name == ".ctor")));
                il.Emit(Cil.OpCodes.Ldflda, HostField("Inst"));
                il.Emit(Cil.OpCodes.Ldfld, a);
                il.Emit(Cil.OpCodes.Ret);
            });
            Method("ArrayElem", ts.Int32, (il, _) =>
            {
                il.Emit(Cil.OpCodes.Ldsfld, HostField("Arr"));
                il.Emit(Cil.OpCodes.Ldc_I4_0);
                il.Emit(Cil.OpCodes.Ldelema, flip);
                il.Emit(Cil.OpCodes.Ldfld, a);
                il.Emit(Cil.OpCodes.Ret);
            });
            Method("NewLocal", ts.Int32, (il, body) =>
            {
                var f = Local(body, flip);
                il.Emit(Cil.OpCodes.Ldloca_S, f);
                il.Emit(Cil.OpCodes.Ldc_I4_S, (sbyte)9);
                il.Emit(Cil.OpCodes.Call, ctor);
                il.Emit(Cil.OpCodes.Ldloca_S, f);
                il.Emit(Cil.OpCodes.Ldfld, a);
                il.Emit(Cil.OpCodes.Ret);
            });
            Method("UnboxField", ts.Int32, (il, _) => { il.Emit(Cil.OpCodes.Ldarg_0); il.Emit(Cil.OpCodes.Unbox, flip); il.Emit(Cil.OpCodes.Ldfld, a); il.Emit(Cil.OpCodes.Ret); }, ts.Object);
            assembly.Write(path);
            // The member references' tokens, as written.
            using var written = Cecil.ModuleDefinition.ReadModule(path);
            var tokens = new Dictionary<string, int>();
            foreach (var method in written.GetType("Probe").Methods.Where(m => m.HasBody))
                foreach (var instruction in method.Body.Instructions)
                    if (instruction.Operand is Cecil.MethodReference called && called.Name is "Read" or "Add")
                        tokens[called.Name] = called.MetadataToken.ToInt32();
            return tokens;
        }
    }
}
