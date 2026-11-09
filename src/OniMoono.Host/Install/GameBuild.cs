#nullable enable
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace OniMoono.Host.Install
{
    /// The installed game's build number: the constant KleiVersion.ChangeList in its
    /// Assembly-CSharp.dll (744825 for U59, 762230 for the U60 release Moono's u60 branch plays),
    /// read from the file's metadata without loading it, as the tweak checks read the game. It is
    /// the number mod_info.yaml's minimumSupportedBuild is compared with.
    internal static class GameBuild
    {
        public static uint? Read(string managedDir) => ReadAssembly(Path.Combine(managedDir, "Assembly-CSharp.dll"));

        public static uint? ReadAssembly(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) return null;
                var md = pe.GetMetadataReader();
                foreach (var handle in md.TypeDefinitions)
                {
                    var type = md.GetTypeDefinition(handle);
                    if (!md.StringComparer.Equals(type.Name, "KleiVersion") || !md.StringComparer.Equals(type.Namespace, "")) continue;
                    foreach (var fieldHandle in type.GetFields())
                    {
                        var field = md.GetFieldDefinition(fieldHandle);
                        if (!md.StringComparer.Equals(field.Name, "ChangeList")) continue;
                        var constantHandle = field.GetDefaultValue();
                        if (constantHandle.IsNil) return null;
                        var constant = md.GetConstant(constantHandle);
                        var blob = md.GetBlobReader(constant.Value);
                        return constant.TypeCode switch
                        {
                            ConstantTypeCode.UInt32 => blob.ReadUInt32(),
                            ConstantTypeCode.Int32 => (uint)blob.ReadInt32(),
                            _ => null,
                        };
                    }
                }
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
        }
    }
}
