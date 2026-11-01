using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Host
{
    /// `--check-types`: a mod naming what the original's assemblies have and ours lack (D57), as
    /// Artificial Natural Tiles builds a `System.Drawing.Bitmap` and ONI Boost Kit read
    /// `JobsUtility.JobWorkerCount`, each inside a try of its own. Each method sets a flag before
    /// its try, so the check sees that it ran; the instruction naming the gap throws inside the try
    /// and the method's catch sees it, where CoreCLR would fail the method as a whole.
    internal static class ApiGapsCheck
    {
        const string Name = "OniMoonoCheckGapsMod";

        public static string Run()
        {
            ModAccess.Install();
            var dir = Directory.CreateTempSubdirectory("oni-moono-check-").FullName;
            try
            {
                var engine = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "UnityEngine.CoreModule");
                var (gapType, gapMethod, gapEnum) = Gaps(engine);
                var modPath = Path.Combine(dir, Name + ".dll");
                Mod(modPath, gapType, gapMethod, gapEnum);
                var probe = ModAccess.LoadFrom(modPath).GetType("Probe", throwOnError: true)!;
                Exception Caught(string method)
                {
                    probe.GetField("Before")!.SetValue(null, 0);
                    probe.GetField("Caught")!.SetValue(null, null);
                    var result = (string)probe.GetMethod(method)!.Invoke(null, BindingFlags.DoNotWrapExceptions, null, null, null)!;
                    if ((int)probe.GetField("Before")!.GetValue(null)! != 1) throw new InvalidOperationException(method + " did not run");
                    if (result != "caught" || probe.GetField("Caught")!.GetValue(null) is not Exception e)
                        throw new InvalidOperationException($"{method} returned \"{result}\", its catch saw nothing");
                    return e;
                }
                var bitmap = Caught("NewBitmap");
                if (bitmap is not FileNotFoundException { FileName: { } file } || !file.StartsWith("System.Drawing.Common,", StringComparison.Ordinal)
                    || bitmap.Message != $"Could not load file or assembly '{file}' or one of its dependencies.")
                    throw new InvalidOperationException($"NewBitmap: {bitmap.GetType().Name} \"{bitmap.Message}\"");
                var isInst = Caught("IsInstBitmap");
                if (isInst is not TypeLoadException || isInst.Message != "Failure has occurred while loading a type.")
                    throw new InvalidOperationException($"IsInstBitmap: {isInst.GetType().Name} \"{isInst.Message}\"");
                string member = "no member of UnityEngine.CoreModule the original has and ours lacks";
                if (gapMethod != null)
                {
                    var missing = Caught("MemberGap");
                    var expected = $"Method not found: int {gapType!.Namespace}.{gapType.Name}.{gapMethod}()";
                    if (missing is not MissingMethodException || missing.Message != expected)
                        throw new InvalidOperationException($"MemberGap: {missing.GetType().Name} \"{missing.Message}\", not \"{expected}\"");
                    member = $"MissingMethodException for {gapType.Name}.{gapMethod}";
                }
                // An enum local holds its underlying type: the store before the box compiles.
                if (gapEnum != null)
                {
                    var boxed = Caught("EnumGap");
                    if (boxed is not TypeLoadException || boxed.Message != "Failure has occurred while loading a type.")
                        throw new InvalidOperationException($"EnumGap: {boxed.GetType().Name} \"{boxed.Message}\"");
                    member += $", an enum {gapEnum.Value.Name} local";
                }
                var jobs = (string)probe.GetMethod("Jobs")!.Invoke(null, null)!;
                if (jobs != Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount.ToString()) throw new InvalidOperationException("Jobs returned " + jobs);
                return $"each method runs and its catch sees Mono's exception for the reference: FileNotFoundException ({file}), TypeLoadException for isinst, {member}; JobWorkerCount reads {jobs}";
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// A public static int method without parameters of a public class that both copies of
        /// UnityEngine.CoreModule have, which the original's has and ours lacks, and a public enum
        /// of the original's that ours lacks; nulls when ours has them all.
        static (Type?, string?, (string Namespace, string Name)?) Gaps(Assembly engine)
        {
            if (AssemblyPolicy.ReferenceDirectories[0] is not { Length: > 0 } managed) return (null, null, null);
            using var pe = new PEReader(File.OpenRead(Path.Combine(managed, "UnityEngine.CoreModule.dll")));
            var md = pe.GetMetadataReader();
            (string, string)? enumGap = null;
            foreach (var handle in md.TypeDefinitions)
            {
                var definition = md.GetTypeDefinition(handle);
                if ((definition.Attributes & System.Reflection.TypeAttributes.VisibilityMask) != System.Reflection.TypeAttributes.Public
                    || definition.BaseType.Kind != HandleKind.TypeReference || md.GetString(md.GetTypeReference((TypeReferenceHandle)definition.BaseType).Name) != "Enum") continue;
                var (ns, name) = (md.GetString(definition.Namespace), md.GetString(definition.Name));
                if (ns.Length > 0 && engine.GetType(ns + "." + name) == null) { enumGap = (ns, name); break; }
            }
            foreach (var handle in md.TypeDefinitions)
            {
                var definition = md.GetTypeDefinition(handle);
                if ((definition.Attributes & System.Reflection.TypeAttributes.VisibilityMask) != System.Reflection.TypeAttributes.Public
                    || definition.GetGenericParameters().Count > 0) continue;
                var ns = md.GetString(definition.Namespace);
                if (ns.Length == 0 || engine.GetType(ns + "." + md.GetString(definition.Name)) is not { IsClass: true } ours || ours.IsGenericType) continue;
                foreach (var m in definition.GetMethods())
                {
                    var method = md.GetMethodDefinition(m);
                    if ((method.Attributes & (System.Reflection.MethodAttributes.MemberAccessMask | System.Reflection.MethodAttributes.Static))
                        != (System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static)) continue;
                    // A default-convention signature: no parameters, returning int.
                    var blob = md.GetBlobBytes(method.Signature);
                    if (blob is not [0x00, 0x00, 0x08]) continue;
                    var name = md.GetString(method.Name);
                    if (ours.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy, Type.EmptyTypes) == null)
                        return (ours, name, enumGap);
                }
            }
            return (null, null, enumGap);
        }

        static void Mod(string path, Type? gapType, string? gapMethod, (string Namespace, string Name)? gapEnum)
        {
            using var assembly = Cecil.AssemblyDefinition.CreateAssembly(new Cecil.AssemblyNameDefinition(Name, new Version(1, 0)), Name, Cecil.ModuleKind.Dll);
            var module = assembly.MainModule;
            var ts = module.TypeSystem;
            // Mono's System.Drawing, as a mod built against the game's class libraries names it.
            var drawing = new Cecil.AssemblyNameReference("System.Drawing", new Version(4, 0, 0, 0)) { PublicKeyToken = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a] };
            module.AssemblyReferences.Add(drawing);
            var bitmap = new Cecil.TypeReference("System.Drawing", "Bitmap", module, drawing);
            // Unity's module, as a mod names it (ours forwards it to OniMoono.UnityEngine).
            var core = new Cecil.AssemblyNameReference("UnityEngine.CoreModule", new Version(0, 0, 0, 0));
            module.AssemblyReferences.Add(core);
            var image = new Cecil.TypeReference("System.Drawing", "Image", module, drawing);
            var bitmapCtor = new Cecil.MethodReference(".ctor", ts.Void, bitmap) { HasThis = true };
            bitmapCtor.Parameters.Add(new Cecil.ParameterDefinition(module.ImportReference(typeof(Stream))));
            var getWidth = new Cecil.MethodReference("get_Width", ts.Int32, image) { HasThis = true };
            var probe = new Cecil.TypeDefinition("", "Probe", Cecil.TypeAttributes.Public | Cecil.TypeAttributes.Abstract | Cecil.TypeAttributes.Sealed, ts.Object);
            module.Types.Add(probe);
            var before = new Cecil.FieldDefinition("Before", Cecil.FieldAttributes.Public | Cecil.FieldAttributes.Static, ts.Int32);
            var caught = new Cecil.FieldDefinition("Caught", Cecil.FieldAttributes.Public | Cecil.FieldAttributes.Static, ts.Object);
            probe.Fields.Add(before);
            probe.Fields.Add(caught);
            var exception = module.ImportReference(typeof(Exception));
            // `Before = 1; try { <emit>; return "ran"; } catch (Exception e) { Caught = e; return "caught"; }`
            void Method(string name, Action<Cil.ILProcessor, Cecil.MethodDefinition> emit)
            {
                var method = new Cecil.MethodDefinition(name, Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, ts.String);
                probe.Methods.Add(method);
                var result = new Cil.VariableDefinition(ts.String);
                method.Body.Variables.Add(result);
                var il = method.Body.GetILProcessor();
                var end = il.Create(Cil.OpCodes.Ldloc, result);
                il.Emit(Cil.OpCodes.Ldc_I4_1);
                il.Emit(Cil.OpCodes.Stsfld, before);
                var tryStart = il.Create(Cil.OpCodes.Nop);
                il.Append(tryStart);
                emit(il, method);
                il.Emit(Cil.OpCodes.Ldstr, "ran");
                il.Emit(Cil.OpCodes.Stloc, result);
                il.Emit(Cil.OpCodes.Leave, end);
                var handler = il.Create(Cil.OpCodes.Stsfld, caught);
                il.Append(handler);
                il.Emit(Cil.OpCodes.Ldstr, "caught");
                il.Emit(Cil.OpCodes.Stloc, result);
                il.Emit(Cil.OpCodes.Leave, end);
                il.Append(end);
                il.Emit(Cil.OpCodes.Ret);
                method.Body.ExceptionHandlers.Add(new Cil.ExceptionHandler(Cil.ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = end, CatchType = exception,
                });
            }
            // As TryLoadSprite: a Bitmap local, the constructor, then a property of its base.
            Method("NewBitmap", (il, method) =>
            {
                var local = new Cil.VariableDefinition(bitmap);
                method.Body.Variables.Add(local);
                il.Emit(Cil.OpCodes.Ldnull);
                il.Emit(Cil.OpCodes.Newobj, bitmapCtor);
                il.Emit(Cil.OpCodes.Stloc, local);
                il.Emit(Cil.OpCodes.Ldloc, local);
                il.Emit(Cil.OpCodes.Callvirt, getWidth);
                il.Emit(Cil.OpCodes.Pop);
            });
            Method("IsInstBitmap", (il, _) =>
            {
                il.Emit(Cil.OpCodes.Ldnull);
                il.Emit(Cil.OpCodes.Isinst, bitmap);
                il.Emit(Cil.OpCodes.Pop);
            });
            if (gapType != null)
            {
                var gap = new Cecil.MethodReference(gapMethod, ts.Int32, new Cecil.TypeReference(gapType.Namespace, gapType.Name, module, core));
                Method("MemberGap", (il, _) =>
                {
                    il.Emit(Cil.OpCodes.Call, gap);
                    il.Emit(Cil.OpCodes.Pop);
                });
            }
            if (gapEnum is var (enumNs, enumName))
            {
                var type = new Cecil.TypeReference(enumNs, enumName, module, core, valueType: true);
                Method("EnumGap", (il, method) =>
                {
                    var local = new Cil.VariableDefinition(type);
                    method.Body.Variables.Add(local);
                    il.Emit(Cil.OpCodes.Ldc_I4_1);
                    il.Emit(Cil.OpCodes.Stloc, local);
                    il.Emit(Cil.OpCodes.Ldloc, local);
                    il.Emit(Cil.OpCodes.Box, type);
                    il.Emit(Cil.OpCodes.Pop);
                });
            }
            // GCSentinel's read, which ours now has.
            var jobs = new Cecil.MethodDefinition("Jobs", Cecil.MethodAttributes.Public | Cecil.MethodAttributes.Static, ts.String);
            probe.Methods.Add(jobs);
            var count = jobs.Body.GetILProcessor();
            var value = new Cil.VariableDefinition(ts.Int32);
            jobs.Body.Variables.Add(value);
            count.Emit(Cil.OpCodes.Call, new Cecil.MethodReference("get_JobWorkerCount", ts.Int32, new Cecil.TypeReference("Unity.Jobs.LowLevel.Unsafe", "JobsUtility", module, core)));
            count.Emit(Cil.OpCodes.Stloc, value);
            count.Emit(Cil.OpCodes.Ldloca, value);
            count.Emit(Cil.OpCodes.Call, module.ImportReference(typeof(int).GetMethod("ToString", Type.EmptyTypes)!));
            count.Emit(Cil.OpCodes.Ret);
            assembly.Write(path);
        }
    }
}
