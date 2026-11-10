#nullable enable
using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using OniMoono.Host.Install;

namespace OniMoono.Tests
{
    /// A Sigstore of the tests' own, in the public-good instance's formats: a Fulcio-like
    /// authority (a P-384 root and intermediate), a CT log and a Rekor log (P-256 keys), and a
    /// trusted root naming them. It signs in-toto statements into v0.3 bundles as GitHub's attest
    /// action does (a leaf certificate with Fulcio's identity extensions and an SCT, a DSSE
    /// envelope, a Rekor dsse entry with its signed entry timestamp, inclusion proof and
    /// checkpoint), so Sigstore.Verify checks them as it checks a real one.
    internal sealed class TestSigstore
    {
        public static readonly TestSigstore Shared = new();

        public readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        readonly ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP384), interKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        readonly ECDsa ctKey = ECDsa.Create(ECCurve.NamedCurves.nistP256), rekorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        readonly X509Certificate2 root, inter;
        public readonly string TrustedRootJson;
        public readonly Sigstore.TrustedRoot Trust;

        /// Moono's identity, with this Sigstore's test values.
        public static Sigstore.Identity Identity => Sigstore.MoonoWorkshop;

        public TestSigstore(string name = "test")
        {
            var rootReq = new CertificateRequest($"O={name}, CN={name}-root", rootKey, HashAlgorithmName.SHA384);
            rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            root = rootReq.CreateSelfSigned(Start.AddYears(-1), Start.AddYears(10));
            var interReq = new CertificateRequest($"O={name}, CN={name}-intermediate", interKey, HashAlgorithmName.SHA384);
            interReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            // CreateSelfSigned's certificate has its private key already.
            inter = interReq.Create(root, Start.AddYears(-1), Start.AddYears(9), [1, 2, 3]).CopyWithPrivateKey(interKey);
            TrustedRootJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["mediaType"] = "application/vnd.dev.sigstore.trustedroot+json;version=0.1",
                ["tlogs"] = new[] { Log("https://rekor.test", rekorKey) },
                ["certificateAuthorities"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["subject"] = new { organization = name, commonName = name },
                        ["uri"] = "https://fulcio.test",
                        ["certChain"] = new { certificates = new[] { new { rawBytes = Convert.ToBase64String(inter.RawData) }, new { rawBytes = Convert.ToBase64String(root.RawData) } } },
                        ["validFor"] = new { start = Start.AddYears(-1).ToString("O") },
                    },
                },
                ["ctlogs"] = new[] { Log("https://ctfe.test", ctKey) },
            });
            Trust = Sigstore.TrustedRoot.Parse(TrustedRootJson);
        }

        static Dictionary<string, object> Log(string url, ECDsa key) => new()
        {
            ["baseUrl"] = url,
            ["hashAlgorithm"] = "SHA2_256",
            ["publicKey"] = new { rawBytes = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), keyDetails = "PKIX_ECDSA_P256_SHA_256", validFor = new { start = "2020-01-01T00:00:00Z" } },
            ["logId"] = new { keyId = Convert.ToBase64String(SHA256.HashData(key.ExportSubjectPublicKeyInfo())) },
        };

        /// What a bundle is signed as; each field changes one thing a check looks at.
        public sealed record Options
        {
            public string San { get; init; } = $"https://github.com/{Sigstore.Repository}/.github/workflows/attest.yml@refs/heads/master";
            public string Issuer { get; init; } = "https://token.actions.githubusercontent.com";
            public string Repository { get; init; } = $"https://github.com/{Sigstore.Repository}";
            public string RepositoryId { get; init; } = "1421479132";
            public string OwnerId { get; init; } = "275280754";
            public string Ref { get; init; } = "refs/heads/master";
            public string BuildConfig { get; init; } = $"https://github.com/{Sigstore.Repository}/.github/workflows/workshop.yml@refs/heads/master";
            public string Runner { get; init; } = "github-hosted";
            /// The log entry's time after the certificate's start (it lives ten minutes).
            public TimeSpan EntryAfterIssue { get; init; } = TimeSpan.FromMinutes(1);
            /// Another authority issues the certificate.
            public TestSigstore? Issuer2 { get; init; }
            public bool NoSct { get; init; }
        }

        /// A bundle for an in-toto statement of these subjects (name, SHA-256 hex).
        public byte[] Sign(IEnumerable<(string Name, string Sha256)> subjects, Options? options = null)
        {
            var o = options ?? new Options();
            var issuing = o.Issuer2 ?? this;
            var notBefore = Start;
            using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var leafReq = new CertificateRequest(new X500DistinguishedName(""), leafKey, HashAlgorithmName.SHA384);
            leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddUri(new Uri(o.San));
            leafReq.CertificateExtensions.Add(san.Build(critical: true));
            leafReq.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.57264.1.1", Encoding.UTF8.GetBytes(o.Issuer), false));
            void Utf8(int n, string value)
            {
                var w = new AsnWriter(AsnEncodingRules.DER);
                w.WriteCharacterString(UniversalTagNumber.UTF8String, value);
                leafReq.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.57264.1." + n, w.Encode(), false));
            }
            Utf8(8, o.Issuer);
            Utf8(9, o.San);
            Utf8(11, o.Runner);
            Utf8(12, o.Repository);
            Utf8(14, o.Ref);
            Utf8(15, o.RepositoryId);
            Utf8(17, o.OwnerId);
            Utf8(18, o.BuildConfig);
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7f;
            var notAfter = notBefore.AddMinutes(10);
            var leaf = leafReq.Create(issuing.inter, notBefore, notAfter, serial);
            if (!o.NoSct)
            {
                leafReq.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.11129.2.4.2", issuing.Sct(leaf, notBefore), false));
                leaf = leafReq.Create(issuing.inter, notBefore, notAfter, serial);
            }

            var statement = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["_type"] = "https://in-toto.io/Statement/v1",
                ["subject"] = subjects.Select(s => new { name = s.Name, digest = new { sha256 = s.Sha256 } }).ToArray(),
                ["predicateType"] = "https://slsa.dev/provenance/v1",
                ["predicate"] = new { },
            });
            const string type = "application/vnd.in-toto+json";
            var signature = leafKey.SignData(Sigstore.Pae(type, statement), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var pem = PemEncoding.WriteString("CERTIFICATE", leaf.RawData) + "\n";
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                apiVersion = "0.0.1",
                kind = "dsse",
                spec = new
                {
                    envelopeHash = new { algorithm = "sha256", value = new string('0', 64) },
                    payloadHash = new { algorithm = "sha256", value = Convert.ToHexStringLower(SHA256.HashData(statement)) },
                    signatures = new[] { new { signature = Convert.ToBase64String(signature), verifier = Convert.ToBase64String(Encoding.ASCII.GetBytes(pem)) } },
                },
            }));
            var bodyText = Convert.ToBase64String(body);
            long integrated = (notBefore + o.EntryAfterIssue).ToUnixTimeSeconds(), logIndex = 4242;
            var logId = SHA256.HashData(rekorKey.ExportSubjectPublicKeyInfo());
            var set = rekorKey.SignData(Encoding.UTF8.GetBytes($"{{\"body\":\"{bodyText}\",\"integratedTime\":{integrated},\"logID\":\"{Convert.ToHexStringLower(logId)}\",\"logIndex\":{logIndex}}}"),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            // A tree of seven leaves, this entry the sixth.
            var leaves = Enumerable.Range(0, 7).Select(i => i == 5 ? body : Encoding.UTF8.GetBytes("leaf " + i)).ToArray();
            var rootHash = TreeHash(leaves);
            var proof = InclusionPath(5, leaves);
            var note = $"rekor.test - 1\n7\n{Convert.ToBase64String(rootHash)}\n";
            var noteSig = rekorKey.SignData(Encoding.UTF8.GetBytes(note), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var checkpoint = note + "\n— rekor.test " + Convert.ToBase64String([.. logId.Take(4), .. noteSig]) + "\n";

            return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["mediaType"] = "application/vnd.dev.sigstore.bundle.v0.3+json",
                ["verificationMaterial"] = new
                {
                    certificate = new { rawBytes = Convert.ToBase64String(leaf.RawData) },
                    tlogEntries = new[]
                    {
                        new
                        {
                            logIndex = logIndex.ToString(),
                            logId = new { keyId = Convert.ToBase64String(logId) },
                            kindVersion = new { kind = "dsse", version = "0.0.1" },
                            integratedTime = integrated.ToString(),
                            inclusionPromise = new { signedEntryTimestamp = Convert.ToBase64String(set) },
                            inclusionProof = new { logIndex = "5", rootHash = Convert.ToBase64String(rootHash), treeSize = "7", hashes = proof.Select(Convert.ToBase64String).ToArray(), checkpoint = new { envelope = checkpoint } },
                            canonicalizedBody = bodyText,
                        },
                    },
                    timestampVerificationData = new { },
                },
                ["dsseEnvelope"] = new { payload = Convert.ToBase64String(statement), payloadType = type, signatures = new[] { new { sig = Convert.ToBase64String(signature) } } },
            });
        }

        /// RFC 6962's SCT list (one SCT, v1) for the precertificate of `pre`.
        byte[] Sct(X509Certificate2 pre, DateTimeOffset at)
        {
            var tbs = new AsnReader(pre.RawData, AsnEncodingRules.DER).ReadSequence().ReadEncodedValue().ToArray();
            var precert = Sigstore.PrecertificateTbs(tbs);
            ulong timestamp = (ulong)at.ToUnixTimeMilliseconds();
            var ts = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(ts, timestamp);
            byte[] signed = [0, 0, .. ts, 0, 1, .. SHA256.HashData(inter.PublicKey.ExportSubjectPublicKeyInfo()),
                (byte)(precert.Length >> 16), (byte)(precert.Length >> 8), (byte)precert.Length, .. precert, 0, 0];
            var sig = ctKey.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            byte[] sct = [0, .. SHA256.HashData(ctKey.ExportSubjectPublicKeyInfo()), .. ts, 0, 0, 4, 3, (byte)(sig.Length >> 8), (byte)sig.Length, .. sig];
            byte[] list = [(byte)((sct.Length + 2) >> 8), (byte)(sct.Length + 2), (byte)(sct.Length >> 8), (byte)sct.Length, .. sct];
            var w = new AsnWriter(AsnEncodingRules.DER);
            w.WriteOctetString(list);
            return w.Encode();
        }

        static byte[] Leaf(byte[] d) => SHA256.HashData([0, .. d]);
        static byte[] Node(byte[] l, byte[] r) => SHA256.HashData([1, .. l, .. r]);
        static int Split(int n) { int k = 1; while (k << 1 < n) k <<= 1; return k; }

        /// RFC 6962's MTH.
        public static byte[] TreeHash(byte[][] d) => d.Length == 1 ? Leaf(d[0]) : Node(TreeHash(d[..Split(d.Length)]), TreeHash(d[Split(d.Length)..]));

        /// RFC 6962's PATH(m, D[n]).
        public static List<byte[]> InclusionPath(int m, byte[][] d)
        {
            if (d.Length == 1) return new();
            int k = Split(d.Length);
            if (m < k) { var p = InclusionPath(m, d[..k]); p.Add(TreeHash(d[k..])); return p; }
            var q = InclusionPath(m - k, d[k..]);
            q.Add(TreeHash(d[..k]));
            return q;
        }

        /// Signs a folder laid out as the Workshop item as workshop.yml does: its SHA256SUMS over
        /// every file but itself and attestation/, and attestation/SHA256SUMS.sigstore.json for it.
        public void SignItem(string item, Options? options = null, string? coveredDigest = null)
        {
            var paths = Directory.GetFiles(item, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(item, f).Replace('\\', '/'))
                .Where(p => p != ItemTrust.ManifestName && !p.StartsWith("attestation/", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal);
            var sums = string.Concat(paths.Select(p => $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(item, p))))}  {p}\n"));
            File.WriteAllText(Path.Combine(item, ItemTrust.ManifestName), sums);
            var digest = coveredDigest ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sums)));
            Directory.CreateDirectory(Path.Combine(item, "attestation"));
            File.WriteAllBytes(Path.Combine(item, "attestation", "SHA256SUMS.sigstore.json"), Sign([(ItemTrust.ManifestSubject, digest)], options));
        }
    }
}
