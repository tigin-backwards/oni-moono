#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace OniMoono.Host.GameTweaks
{
    /// compact-report-entries' reading of assemblies (OniMoono.GameTweaks.CompactReportDays): a
    /// packed day's `DailyReport.reportEntries` is null, so code that reads that field itself,
    /// rather than through DailyReport.GetEntry or ReportManager.FindReport, would see no
    /// entries. Plain .NET over System.Reflection.Metadata (the tests compile it in).
    ///
    /// - A mod (ModAccess, from its metadata): a reference to the field (any ldfld, ldflda,
    ///   stfld or ldtoken of it needs one), a Harmony patch parameter `___reportEntries` (Harmony
    ///   passes the field's value), or the name as a string (reflection: AccessTools.Field,
    ///   Traverse, GetField). Any of them keeps the tweak off for the session. Entries' own
    ///   `contextEntries` do not count: every entry given out is a whole object, children and
    ///   all.
    /// - The game (FieldSites, from its IL): the methods that read or write the field, which the
    ///   tweak compares with the ones it knows.
    internal static class ReportAccessScan
    {
        const string Field = "reportEntries", Outer = "ReportManager", Inner = "DailyReport";

        /// What in a mod's metadata could reach `DailyReport.reportEntries` other than through
        /// the game's methods, or null.
        public static string? ModAccess(MetadataReader reader)
        {
            foreach (var handle in reader.MemberReferences)
            {
                var member = reader.GetMemberReference(handle);
                if (!reader.StringComparer.Equals(member.Name, Field) || member.GetKind() != MemberReferenceKind.Field) continue;
                if (member.Parent.Kind == HandleKind.TypeReference && IsDailyReport(reader, (TypeReferenceHandle)member.Parent))
                    return "reads or writes DailyReport.reportEntries directly";
            }
            foreach (var method in reader.MethodDefinitions)
                foreach (var handle in reader.GetMethodDefinition(method).GetParameters())
                    if (reader.StringComparer.Equals(reader.GetParameter(handle).Name, "___" + Field))
                        return "takes DailyReport.reportEntries as a Harmony patch parameter";
            var us = MetadataTokens.UserStringHandle(1);
            int heap = reader.GetHeapSize(HeapIndex.UserString);
            while (!us.IsNil && MetadataTokens.GetHeapOffset(us) < heap)
            {
                if (reader.GetUserString(us) == Field) return "names DailyReport.reportEntries in a string (reflection)";
                us = reader.GetNextHandle(us);
            }
            return null;
        }

        /// ModAccess for a loaded assembly; null too when its metadata cannot be read.
        public static unsafe string? ModAccess(Assembly assembly)
        {
            try
            {
                return assembly.TryGetRawMetadata(out byte* blob, out int length) ? ModAccess(new MetadataReader(blob, length)) : null;
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
            {
                return null;
            }
        }

        static bool IsDailyReport(MetadataReader reader, TypeReferenceHandle handle)
        {
            var type = reader.GetTypeReference(handle);
            if (!reader.StringComparer.Equals(type.Name, Inner) || type.ResolutionScope.Kind != HandleKind.TypeReference) return false;
            return reader.StringComparer.Equals(reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope).Name, Outer);
        }

        static bool IsDailyReport(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!reader.StringComparer.Equals(type.Name, Inner)) return false;
            var outer = type.GetDeclaringType();
            return !outer.IsNil && reader.StringComparer.Equals(reader.GetTypeDefinition(outer).Name, Outer);
        }

        /// The methods of the image whose IL reads or writes `DailyReport.reportEntries` (ldfld,
        /// ldflda, stfld, ldtoken), as "Type::Method", each with its kinds of access
        /// ("SaveLoader::GetDailyReportMetrics ldfld").
        public static List<string> FieldSites(PEReader pe)
        {
            var reader = pe.GetMetadataReader();
            var tokens = new HashSet<int>();
            foreach (var handle in reader.FieldDefinitions)
            {
                var field = reader.GetFieldDefinition(handle);
                if (reader.StringComparer.Equals(field.Name, Field) && IsDailyReport(reader, field.GetDeclaringType())) tokens.Add(MetadataTokens.GetToken(handle));
            }
            foreach (var handle in reader.MemberReferences)
            {
                var member = reader.GetMemberReference(handle);
                if (reader.StringComparer.Equals(member.Name, Field) && member.Parent.Kind == HandleKind.TypeReference && IsDailyReport(reader, (TypeReferenceHandle)member.Parent))
                    tokens.Add(MetadataTokens.GetToken(handle));
            }
            var sites = new List<string>();
            if (tokens.Count == 0) return sites;
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0) continue;
                    var kinds = Accesses(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes(), tokens);
                    if (kinds != null) sites.Add($"{TypeName(reader, typeHandle)}::{reader.GetString(method.Name)} {kinds}");
                }
            }
            sites.Sort(StringComparer.Ordinal);
            return sites;
        }

        static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var type = reader.GetTypeDefinition(handle);
            string name = reader.GetString(type.Name);
            var outer = type.GetDeclaringType();
            if (!outer.IsNil) return TypeName(reader, outer) + "/" + name;
            string ns = reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        /// The kinds of access of the tokens in the IL ("ldfld stfld"), or null for none.
        internal static string? Accesses(byte[]? il, HashSet<int> tokens)
        {
            if (il == null) return null;
            bool ldfld = false, ldflda = false, stfld = false, ldtoken = false;
            int i = 0;
            while (i < il.Length)
            {
                int op = il[i++];
                if (op == 0xFE)
                {
                    if (i >= il.Length) break;
                    i += TwoByteOperand(il[i++]);
                    continue;
                }
                if (op is 0x7B or 0x7C or 0x7D or 0xD0 && i + 4 <= il.Length)
                {
                    if (tokens.Contains(BitConverter.ToInt32(il, i)))
                        switch (op)
                        {
                            case 0x7B: ldfld = true; break;
                            case 0x7C: ldflda = true; break;
                            case 0x7D: stfld = true; break;
                            default: ldtoken = true; break;
                        }
                    i += 4;
                    continue;
                }
                if (op == 0x45)
                {
                    if (i + 4 > il.Length) break;
                    int n = BitConverter.ToInt32(il, i);
                    i += 4 + 4 * n;
                    continue;
                }
                i += OneByteOperand(op);
            }
            if (!(ldfld || ldflda || stfld || ldtoken)) return null;
            var kinds = new List<string>();
            if (ldfld) kinds.Add("ldfld");
            if (ldflda) kinds.Add("ldflda");
            if (stfld) kinds.Add("stfld");
            if (ldtoken) kinds.Add("ldtoken");
            return string.Join(" ", kinds);
        }

        /// The operand's size of a one-byte opcode (ECMA-335 III).
        static int OneByteOperand(int op) => op switch
        {
            >= 0x0E and <= 0x13 => 1,       // ldarg.s … stloc.s
            0x1F => 1,                      // ldc.i4.s
            0x20 or 0x22 => 4,              // ldc.i4, ldc.r4
            0x21 or 0x23 => 8,              // ldc.i8, ldc.r8
            0x27 or 0x28 or 0x29 => 4,      // jmp, call, calli
            >= 0x2B and <= 0x37 => 1,       // short branches
            >= 0x38 and <= 0x44 => 4,       // branches
            0x6F or (>= 0x70 and <= 0x75) => 4, // callvirt, cpobj, ldobj, ldstr, newobj, castclass, isinst
            0x79 => 4,                      // unbox
            >= 0x7B and <= 0x81 => 4,       // ldfld … stobj
            0x8C or 0x8D or 0x8F => 4,      // box, newarr, ldelema
            0xA3 or 0xA4 or 0xA5 => 4,      // ldelem, stelem, unbox.any
            0xC2 or 0xC6 => 4,              // refanyval, mkrefany
            0xD0 => 4,                      // ldtoken
            0xDD => 4,                      // leave
            0xDE => 1,                      // leave.s
            _ => 0,
        };

        static int TwoByteOperand(int op) => op switch
        {
            0x06 or 0x07 => 4,              // ldftn, ldvirtftn
            >= 0x09 and <= 0x0E => 2,       // ldarg … stloc
            0x12 => 1,                      // unaligned.
            0x15 or 0x16 or 0x1C => 4,      // initobj, constrained., sizeof
            0x19 => 1,                      // no.
            _ => 0,
        };
    }
}
