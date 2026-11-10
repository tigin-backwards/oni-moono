#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OniMoono.Host.Install
{
    /// A Workshop item's own signature (docs/INSTALL-MODES.md, "What the installer verifies"):
    /// attestation/SHA256SUMS.sigstore.json, the Sigstore bundle attest.yml made for the item's
    /// SHA256SUMS in workshop.yml, verified offline (Sigstore) against the embedded trusted root and
    /// Moono's identity, and naming the item's SHA256SUMS by its digest. That manifest then vouches
    /// for every other file of the item, each version's own manifest, mod_info.yaml and moono.json
    /// among them. Nothing the item says about itself is trusted before this passes.
    internal sealed class ItemTrust
    {
        public const string BundlePath = "attestation/SHA256SUMS.sigstore.json";
        public const string ManifestName = "SHA256SUMS";
        /// The subject name attest.yml gives the item's SHA256SUMS.
        public const string ManifestSubject = "workshop-item/SHA256SUMS";
        const long MaxBundle = 16 << 20, MaxManifest = 16 << 20;

        /// The item's verified SHA256SUMS: path (relative, `/`) to SHA-256 (hex, lowercase).
        public readonly IReadOnlyDictionary<string, string> Files;
        /// The SHA-256 of the item's SHA256SUMS.
        public readonly string ManifestDigest;

        ItemTrust(IReadOnlyDictionary<string, string> files, string digest)
        {
            Files = files;
            ManifestDigest = digest;
        }

        /// Why an item is refused.
        public sealed class Refusal(string message) : Exception(message);

        public static ItemTrust Verify(string itemRoot, Sigstore.TrustedRoot trust, Sigstore.Identity identity)
        {
            var bundlePath = Path.Combine(itemRoot, BundlePath.Replace('/', Path.DirectorySeparatorChar));
            var manifestPath = Path.Combine(itemRoot, ManifestName);
            byte[] bundle, manifest;
            try
            {
                if (!File.Exists(bundlePath)) throw new Refusal($"it has no signature ({BundlePath})");
                if (!File.Exists(manifestPath)) throw new Refusal($"it has no {ManifestName}");
                bundle = ReadCapped(bundlePath, MaxBundle);
                manifest = ReadCapped(manifestPath, MaxManifest);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new Refusal($"its signature or {ManifestName} can't be read: {e.Message}"); }

            IReadOnlyList<Sigstore.Subject> subjects;
            try { subjects = Sigstore.Verify(bundle, trust, identity); }
            catch (Sigstore.Failure f) { throw new Refusal("its signature does not verify: " + f.Message); }
            var digest = Convert.ToHexStringLower(SHA256.HashData(manifest));
            if (!subjects.Any(s => s.Name == ManifestSubject && s.Sha256 == digest))
                throw new Refusal($"its {ManifestName} (SHA-256 {digest}) is not the one its signature covers");

            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in Encoding.UTF8.GetString(manifest).Split('\n'))
            {
                if (raw.Length == 0) continue;
                if (raw.Length < 67 || raw[64] != ' ' || raw[65] != ' ' || !raw.Take(64).All(char.IsAsciiHexDigit) || !PlainRelative(raw.Substring(66)))
                    throw new Refusal($"its {ManifestName} has a malformed line");
                if (!files.TryAdd(raw.Substring(66), raw.Substring(0, 64).ToLowerInvariant())) throw new Refusal($"its {ManifestName} names {raw.Substring(66)} twice");
            }
            return new ItemTrust(files, digest);
        }

        /// `bytes` are the item's file at `path` (relative, `/`) as its SHA256SUMS lists it, else Refusal.
        public void Require(string path, byte[] bytes)
        {
            if (!Files.TryGetValue(path, out var want)) throw new Refusal($"{path} is not in its signed {ManifestName}");
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != want) throw new Refusal($"{path} is not as its signed {ManifestName} lists it");
        }

        /// Relative, `/`-separated, no `.` or `..` (VersionedInstall.IsPlainRelative).
        static bool PlainRelative(string path) =>
            path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':')
            && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

        static byte[] ReadCapped(string path, long max)
        {
            using var f = File.OpenRead(path);
            if (f.Length > max) throw new Refusal($"{Path.GetFileName(path)} is too large");
            var bytes = new byte[f.Length];
            f.ReadExactly(bytes);
            return bytes;
        }
    }
}
