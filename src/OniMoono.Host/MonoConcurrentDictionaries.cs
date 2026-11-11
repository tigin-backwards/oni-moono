using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using OniMoono.Engine;
using Cecil = Mono.Cecil;
using OniMoono.Tweaks;

namespace OniMoono.Host
{
    /// ARCHITECTURE D27. A ConcurrentDictionary enumerates its buckets in order, each bucket's
    /// entries newest first, on Mono as on CoreCLR, so its order follows the keys' hashes and the
    /// table's size. For string keys both differ: Mono's table has as many buckets as the capacity
    /// asked for and hashes with Mono's string hash; CoreCLR rounds the capacity up to a prime (32
    /// gives 37) and hashes with its own. PLib's component registry (PRegistryComponent, a
    /// `ConcurrentDictionary<string, PVersionList>(2, 32)`) initializes each component while
    /// enumerating that dictionary, and PLib 4.16's PBuildingManager registers PPatchManager from its
    /// Initialize: on Mono that key's bucket comes later and it is initialized; on CoreCLR the
    /// enumeration had passed it, so PLib's stages from Db.Initialize on never ran (Thermo Sensor
    /// Tile's techs were never registered). A mod's `new ConcurrentDictionary<string, T>(
    /// concurrencyLevel, capacity)` is sent here: the dictionary gets a comparer whose hash puts each
    /// key in the bucket Mono's table of that size puts it in, so it enumerates in Mono's order until
    /// Mono's table would grow (a lock holding more than capacity / concurrencyLevel keys).
    /// The constructors without a size are left alone: Mono sizes their locks by the processor
    /// count, and with them when their table grows, so their order in the original depends on the
    /// machine.
    public static class MonoConcurrentDictionaries
    {
        /// Mono's ConcurrentDictionary(concurrencyLevel, capacity): max(capacity, concurrencyLevel)
        /// buckets.
        public static ConcurrentDictionary<string, TValue> New<TValue>(int concurrencyLevel, int capacity)
        {
            var comparer = new MonoOrderComparer(Math.Max(1, Math.Max(capacity, concurrencyLevel)));
            return comparer.Attach(new ConcurrentDictionary<string, TValue>(concurrencyLevel, capacity, comparer));
        }

        /// Hashes a key to Mono's bucket for a table of `monoBuckets`: a value that is that bucket
        /// modulo the CoreCLR table's own size (read once the dictionary exists), so CoreCLR's
        /// bucket index equals Mono's. The rest of Mono's hash goes above it, so keys of one bucket
        /// rarely share a hash and spread over CoreCLR's table once it grows.
        sealed class MonoOrderComparer(int monoBuckets) : IEqualityComparer<string>
        {
            uint netBuckets;

            [NameLookupReviewed("CoreCLR's ConcurrentDictionary internals, not the game's; read with a fallback")]
            public ConcurrentDictionary<string, TValue> Attach<TValue>(ConcurrentDictionary<string, TValue> dictionary)
            {
                var tables = dictionary.GetType().GetField("_tables", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dictionary);
                var buckets = tables?.GetType().GetField("_buckets", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(tables) as Array;
                // Without CoreCLR's layout every key's hash is its Mono bucket alone: the same order
                // (CoreCLR's table is at least as large), with more keys sharing a hash.
                netBuckets = buckets != null && buckets.Length >= monoBuckets ? (uint)buckets.Length : 0;
                return dictionary;
            }

            public bool Equals(string? x, string? y) => string.Equals(x, y);

            public int GetHashCode(string text)
            {
                uint mono = (uint)MonoStringHash.Compute(text) & 0x7fffffff;
                uint bucket = mono % (uint)monoBuckets;
                if (netBuckets == 0) return (int)bucket;
                // Kept below 2^31, so the bucket is the same whether the hash is read signed or not.
                uint spread = mono / (uint)monoBuckets % (((uint)int.MaxValue - bucket) / netBuckets);
                return (int)(bucket + netBuckets * spread);
            }
        }

        /// The image with its sized string-keyed ConcurrentDictionary constructions sent to New, or
        /// null when it makes none. `symbols` (portable PDB bytes or null) are rewritten to match.
        internal static byte[]? Adjust(byte[] image, ref byte[]? symbols, string path)
        {
            if (!ConstructsStringKeyed(image)) return null;
            try
            {
                return Rewrite(image, ref symbols, path);
            }
            catch (Exception e) when (e is Cecil.AssemblyResolutionException or BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                // The mod loads as it is: only its dictionaries' order differs from Mono's.
                EngineLog.Engine($"mod dictionaries: {Path.GetFileName(path)} not rewritten ({e.GetType().Name}: {e.Message}); it loads unchanged");
                return null;
            }
        }

        static byte[]? Rewrite(byte[] image, ref byte[]? symbols, string path)
        {
            // Writing the module back resolves types of its references (an enum default value's).
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
                    SymbolReaderProvider = withSymbols ? new Cecil.Cil.PortablePdbReaderProvider() : null,
                });
            }
            catch (Exception e) when (withSymbols && e is Cecil.Cil.SymbolsNotMatchingException or Cecil.Cil.SymbolsNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                withSymbols = false;
                module = Cecil.ModuleDefinition.ReadModule(new MemoryStream(image), new Cecil.ReaderParameters { AssemblyResolver = resolver });
            }
            using var owned = module;
            var factory = module.ImportReference(typeof(MonoConcurrentDictionaries).GetMethod(nameof(New))!);
            int calls = 0;
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode != Cecil.Cil.OpCodes.Newobj || instruction.Operand is not Cecil.MethodReference ctor
                            || ctor.DeclaringType is not Cecil.GenericInstanceType { GenericArguments.Count: 2 } declaring
                            || declaring.ElementType.FullName != "System.Collections.Concurrent.ConcurrentDictionary`2"
                            || declaring.GenericArguments[0].MetadataType != Cecil.MetadataType.String
                            || !IsSized(ctor)) continue;
                        var call = new Cecil.GenericInstanceMethod(factory);
                        call.GenericArguments.Add(declaring.GenericArguments[1]);
                        instruction.OpCode = Cecil.Cil.OpCodes.Call;
                        instruction.Operand = call;
                        calls++;
                    }
                }
            if (calls == 0) return null;
            using var output = new MemoryStream();
            using var symbolOutput = new MemoryStream();
            module.Write(output, new Cecil.WriterParameters { WriteSymbols = withSymbols, SymbolStream = withSymbols ? symbolOutput : null, SymbolWriterProvider = withSymbols ? new Cecil.Cil.PortablePdbWriterProvider() : null });
            symbols = withSymbols ? symbolOutput.ToArray() : null;
            EngineLog.Engine($"mod dictionaries: {Path.GetFileName(path)}: {calls} string-keyed ConcurrentDictionary(concurrencyLevel, capacity) enumerate in Mono's order");
            return output.ToArray();
        }

        /// ConcurrentDictionary(int concurrencyLevel, int capacity).
        static bool IsSized(Cecil.MethodReference ctor) => ctor.Parameters.Count == 2
            && ctor.Parameters[0].ParameterType.MetadataType == Cecil.MetadataType.Int32
            && ctor.Parameters[1].ParameterType.MetadataType == Cecil.MetadataType.Int32;

        /// Whether the image names a constructor of ConcurrentDictionary<string, …> (read from its
        /// metadata alone).
        static bool ConstructsStringKeyed(byte[] image)
        {
            try
            {
                using var pe = new PEReader(new MemoryStream(image));
                if (!pe.HasMetadata) return false;
                var reader = pe.GetMetadataReader();
                foreach (var handle in reader.MemberReferences)
                {
                    var member = reader.GetMemberReference(handle);
                    if (!reader.StringComparer.Equals(member.Name, ".ctor") || member.Parent.Kind != HandleKind.TypeSpecification) continue;
                    var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).Signature);
                    if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) continue;
                    blob.ReadByte(); // CLASS
                    var definition = blob.ReadTypeHandle();
                    if (definition.Kind != HandleKind.TypeReference || blob.ReadCompressedInteger() != 2 || blob.ReadSignatureTypeCode() != SignatureTypeCode.String) continue;
                    var parent = reader.GetTypeReference((TypeReferenceHandle)definition);
                    if (reader.StringComparer.Equals(parent.Name, "ConcurrentDictionary`2") && reader.StringComparer.Equals(parent.Namespace, "System.Collections.Concurrent")) return true;
                }
            }
            catch (BadImageFormatException)
            {
                // Not an assembly: the load reports it.
            }
            return false;
        }
    }
}
