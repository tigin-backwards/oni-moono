// Checks a Workshop item folder as Moono's installer does before it installs a version from it
// (src/OniMoono.Host/Install/ItemTrust.cs): attestation/SHA256SUMS.sigstore.json verified offline
// against the trusted root embedded in Moono and Moono's Workshop identity, naming the item's
// SHA256SUMS; then each version's moono.json, mod_info.yaml and the manifests moono.json names as
// SHA256SUMS lists them. .github/workflows/workshop.yml runs it on the item it made, so a bundle
// the installer would refuse (a format or a key Moono doesn't know yet) never reaches Steam.
//
// usage: ItemVerify ITEM
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using OniMoono.Host.Install;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ItemVerify ITEM");
    return 2;
}
var item = Path.GetFullPath(args[0]);
try
{
    var trusted = ItemTrust.Verify(item, Sigstore.TrustedRoot.Embedded, Sigstore.MoonoWorkshop);
    Console.WriteLine($"{item}: signature verified, SHA256SUMS {trusted.ManifestDigest} ({trusted.Files.Count} files)");
    int versions = 0;
    var archived = Path.Combine(item, "archived_versions");
    var dirs = new[] { "" }.Concat(Directory.Exists(archived) ? Directory.GetDirectories(archived).Select(d => "archived_versions/" + Path.GetFileName(d)).Order(StringComparer.Ordinal) : []);
    foreach (var version in dirs)
    {
        var prefix = version.Length == 0 ? "" : version + "/";
        byte[] Checked(string name)
        {
            var bytes = File.ReadAllBytes(Path.Combine(item, prefix + name));
            trusted.Require(prefix + name, bytes);
            return bytes;
        }
        using var info = JsonDocument.Parse(Checked("moono.json"));
        Checked("mod_info.yaml");
        foreach (var platform in info.RootElement.GetProperty("platforms"u8).EnumerateObject())
            Checked(platform.Value.GetProperty("manifest"u8).GetString()!);
        Console.WriteLine($"{(version.Length == 0 ? "(root)" : version)}: {info.RootElement.GetProperty("version"u8).GetString()} as signed");
        versions++;
    }
    return versions > 0 ? 0 : 1;
}
catch (ItemTrust.Refusal r)
{
    Console.Error.WriteLine($"{item}: refused, as the installer would refuse it: {r.Message}");
    return 1;
}
