#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace OniMoono.Host.Install
{
    /// Offline verification of a Sigstore bundle (v0.3, as GitHub's attest actions make it and
    /// `gh attestation download` saves it) against a trusted root (SigstoreTrustedRoot), with
    /// nothing fetched (docs/INSTALL-MODES.md, "What the installer verifies"):
    ///
    /// 1. the DSSE envelope's one signature (ECDSA P-256, SHA-256) over its PAE, by the leaf
    ///    certificate's key;
    /// 2. the Rekor entry: a log of the trusted root (logId, its key valid at the entry's time),
    ///    its body the same envelope (payload hash, signature, certificate), its signed entry
    ///    timestamp, its Merkle inclusion proof and the proof's signed checkpoint, all by the
    ///    log's key;
    /// 3. the leaf certificate: valid at the entry's integrated time, a code-signing key, issued
    ///    by a Fulcio certificate authority of the trusted root (valid at that time), whose chain
    ///    up to its root is checked too; and at least one embedded SCT signed by a CT log of the
    ///    trusted root over the precertificate;
    /// 4. the certificate's identity (Fulcio's extensions) against Identity;
    /// 5. the payload an in-toto v1 statement, whose subjects are returned.
    ///
    /// Not done: revocation (Fulcio publishes none; the certificates live ten minutes), RFC 3161
    /// timestamps (Rekor's integrated time is the time used), Rekor v2 entries (Ed25519 logs) and
    /// bundles before v0.3. Any of these makes the bundle fail, never pass.
    internal static class Sigstore
    {
        public sealed class Failure(string message) : Exception(message);

        public sealed record Subject(string Name, string Sha256);

        /// The signer the certificate must name, from Fulcio's OID 1.3.6.1.4.1.57264.1.* extensions.
        /// Null fields are not checked.
        public sealed record Identity(
            string Issuer,
            IReadOnlyList<string> SubjectAlternativeNames,
            string SourceRepository,
            string? SourceRepositoryId,
            string? SourceRepositoryOwnerId,
            IReadOnlyList<string> SourceRepositoryRefs,
            string? BuildConfigPrefix,
            string? RunnerEnvironment);

        public const string Repository = "tigin-backwards/oni-moono";

        /// Moono's Workshop item: signed by attest.yml as called by workshop.yml, from master or
        /// u60 of tigin-backwards/oni-moono (its GitHub IDs too, so a repository of that name made
        /// later by someone else does not pass), on a GitHub-hosted runner.
        public static readonly Identity MoonoWorkshop = new(
            Issuer: "https://token.actions.githubusercontent.com",
            SubjectAlternativeNames: new[] { "refs/heads/master", "refs/heads/u60" }.Select(r => $"https://github.com/{Repository}/.github/workflows/attest.yml@{r}").ToArray(),
            SourceRepository: $"https://github.com/{Repository}",
            SourceRepositoryId: "1421479132",
            SourceRepositoryOwnerId: "275280754",
            SourceRepositoryRefs: ["refs/heads/master", "refs/heads/u60"],
            BuildConfigPrefix: $"https://github.com/{Repository}/.github/workflows/workshop.yml@",
            RunnerEnvironment: "github-hosted");

        const string FulcioOid = "1.3.6.1.4.1.57264.1.";
        const string SctOid = "1.3.6.1.4.1.11129.2.4.2";

        /// The trusted root: Sigstore's trusted_root.json (application/vnd.dev.sigstore.trustedroot+json).
        public sealed class TrustedRoot
        {
            public sealed record Authority(X509Certificate2[] Chain, DateTimeOffset Start, DateTimeOffset? End);
            public sealed record Log(byte[] KeyId, ECDsa Key, DateTimeOffset Start, DateTimeOffset? End);

            public readonly List<Authority> Authorities = new();
            public readonly List<Log> Tlogs = new(), CtLogs = new();

            /// Logs whose keys aren't ECDSA P-256 (Rekor v2's Ed25519) are left out: their entries fail.
            public static TrustedRoot Parse(string json)
            {
                var root = new TrustedRoot();
                using var doc = JsonDocument.Parse(json);
                var top = doc.RootElement;
                if (Str(top, "mediaType") is not { } media || !media.StartsWith("application/vnd.dev.sigstore.trustedroot", StringComparison.Ordinal))
                    throw new Failure("the trusted root is not a Sigstore trusted root");
                foreach (var ca in Array(top, "certificateAuthorities"))
                {
                    var certs = Array(Obj(ca, "certChain"), "certificates").Select(c => X509CertificateLoader.LoadCertificate(B64(c, "rawBytes"))).ToArray();
                    if (certs.Length == 0) continue;
                    var (start, end) = ValidFor(ca);
                    root.Authorities.Add(new Authority(certs, start, end));
                }
                AddLogs(top, "tlogs", root.Tlogs);
                AddLogs(top, "ctlogs", root.CtLogs);
                return root;
            }

            static void AddLogs(JsonElement top, string name, List<Log> into)
            {
                foreach (var log in Array(top, name))
                {
                    var key = Obj(log, "publicKey");
                    if (Str(key, "keyDetails") != "PKIX_ECDSA_P256_SHA_256") continue;
                    var ecdsa = ECDsa.Create();
                    ecdsa.ImportSubjectPublicKeyInfo(B64(key, "rawBytes"), out _);
                    var (start, end) = ValidFor(key);
                    into.Add(new Log(B64(Obj(log, "logId"), "keyId"), ecdsa, start, end));
                }
            }

            static (DateTimeOffset, DateTimeOffset?) ValidFor(JsonElement e)
            {
                var v = Obj(e, "validFor");
                var start = DateTimeOffset.Parse(Str(v, "start") ?? throw new Failure("a validFor without a start"), System.Globalization.CultureInfo.InvariantCulture);
                DateTimeOffset? end = Str(v, "end") is { } s ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : null;
                return (start, end);
            }

            static TrustedRoot? embedded;
            /// Sigstore's public-good instance as embedded in Moono (SigstoreTrustedRoot.Json).
            public static TrustedRoot Embedded => embedded ??= Parse(SigstoreTrustedRoot.Json);
        }

        static bool Covers(DateTimeOffset start, DateTimeOffset? end, DateTimeOffset t) => t >= start && (end == null || t <= end);

        /// The statement's subjects when the bundle verifies, else Failure saying why.
        public static IReadOnlyList<Subject> Verify(ReadOnlySpan<byte> bundleJson, TrustedRoot trust, Identity identity)
        {
            try { return VerifyCore(bundleJson, trust, identity); }
            catch (Exception e) when (e is JsonException or FormatException or CryptographicException or AsnContentException or InvalidOperationException or KeyNotFoundException or OverflowException or ArgumentException)
            {
                throw new Failure($"the bundle is malformed ({e.GetType().Name}: {e.Message})");
            }
        }

        static IReadOnlyList<Subject> VerifyCore(ReadOnlySpan<byte> bundleJson, TrustedRoot trust, Identity identity)
        {
            using var doc = JsonDocument.Parse(bundleJson.ToArray());
            var bundle = doc.RootElement;
            var media = Str(bundle, "mediaType");
            if (media is not ("application/vnd.dev.sigstore.bundle.v0.3+json" or "application/vnd.dev.sigstore.bundle+json;version=0.3"))
                throw new Failure($"not a v0.3 Sigstore bundle ({media ?? "no mediaType"})");
            var material = Obj(bundle, "verificationMaterial");
            using var leaf = X509CertificateLoader.LoadCertificate(B64(Obj(material, "certificate"), "rawBytes"));

            // 1. The envelope, signed by the leaf's key.
            var envelope = Obj(bundle, "dsseEnvelope");
            var payload = B64(envelope, "payload");
            var payloadType = Str(envelope, "payloadType") ?? throw new Failure("the envelope has no payloadType");
            var signatures = Array(envelope, "signatures").ToArray();
            if (signatures.Length != 1) throw new Failure($"the envelope has {signatures.Length} signatures, not one");
            var signature = B64(signatures[0], "sig");
            using var leafKey = leaf.GetECDsaPublicKey() ?? throw new Failure("the certificate's key is not ECDSA");
            if (leafKey.KeySize != 256) throw new Failure("the certificate's key is not P-256");
            if (!leafKey.VerifyData(Pae(payloadType, payload), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new Failure("the envelope's signature does not verify with the certificate's key");

            // 2. The transparency log's entry for it.
            var entries = Array(material, "tlogEntries").ToArray();
            if (entries.Length == 0) throw new Failure("the bundle has no transparency log entry");
            DateTimeOffset? time = null;
            var problems = new List<string>();
            foreach (var entry in entries)
            {
                try { time = VerifyEntry(entry, trust, leaf, payload, signature); break; }
                catch (Failure f) { problems.Add(f.Message); }
            }
            if (time is not { } integrated) throw new Failure("no transparency log entry verifies: " + string.Join("; ", problems));

            // 3. The certificate, at that time.
            var issuer = VerifyChain(leaf, trust, integrated);
            VerifySct(leaf, issuer, trust);

            // 4. Who signed.
            VerifyIdentity(leaf, identity);

            // 5. What was signed.
            if (payloadType != "application/vnd.in-toto+json") throw new Failure($"the payload is {payloadType}, not an in-toto statement");
            using var statement = JsonDocument.Parse(payload);
            if (Str(statement.RootElement, "_type") != "https://in-toto.io/Statement/v1") throw new Failure("the payload is not an in-toto v1 statement");
            var subjects = new List<Subject>();
            foreach (var s in Array(statement.RootElement, "subject"))
            {
                var name = Str(s, "name") ?? "";
                if (Obj(s, "digest").TryGetProperty("sha256"u8, out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: 64 } hex)
                    subjects.Add(new Subject(name, hex.ToLowerInvariant()));
            }
            return subjects;
        }

        /// DSSE's pre-authentication encoding.
        public static byte[] Pae(string payloadType, byte[] payload)
        {
            var type = Encoding.UTF8.GetBytes(payloadType);
            var head = Encoding.UTF8.GetBytes($"DSSEv1 {type.Length} {payloadType} {payload.Length} ");
            return [.. head, .. payload];
        }

        /// The entry's integrated time when it verifies.
        static DateTimeOffset VerifyEntry(JsonElement entry, TrustedRoot trust, X509Certificate2 leaf, byte[] payload, byte[] signature)
        {
            var kind = Obj(entry, "kindVersion");
            if (Str(kind, "kind") != "dsse" || Str(kind, "version") != "0.0.1")
                throw new Failure($"a {Str(kind, "kind")} {Str(kind, "version")} entry, not dsse 0.0.1");
            var logId = B64(Obj(entry, "logId"), "keyId");
            long logIndex = long.Parse(Str(entry, "logIndex") ?? "", System.Globalization.CultureInfo.InvariantCulture);
            long integrated = long.Parse(Str(entry, "integratedTime") ?? "", System.Globalization.CultureInfo.InvariantCulture);
            var time = DateTimeOffset.FromUnixTimeSeconds(integrated);
            var log = trust.Tlogs.FirstOrDefault(l => l.KeyId.AsSpan().SequenceEqual(logId))
                ?? throw new Failure($"its log {Convert.ToHexStringLower(logId)} is not in the trusted root");
            if (!Covers(log.Start, log.End, time)) throw new Failure("its log's key was not valid at the entry's time");

            // The body: this envelope.
            var bodyText = Str(entry, "canonicalizedBody") ?? throw new Failure("the entry has no body");
            var body = Convert.FromBase64String(bodyText);
            using (var b = JsonDocument.Parse(body))
            {
                var e = b.RootElement;
                if (Str(e, "kind") != "dsse" || Str(e, "apiVersion") != "0.0.1") throw new Failure("the entry's body is not a dsse 0.0.1 entry");
                var spec = Obj(e, "spec");
                var hash = Obj(spec, "payloadHash");
                if (Str(hash, "algorithm") != "sha256" || !string.Equals(Str(hash, "value"), Convert.ToHexStringLower(SHA256.HashData(payload)), StringComparison.OrdinalIgnoreCase))
                    throw new Failure("the entry is for another payload");
                var sigs = Array(spec, "signatures").ToArray();
                if (sigs.Length != 1 || !B64(sigs[0], "signature").AsSpan().SequenceEqual(signature))
                    throw new Failure("the entry is for another signature");
                var verifier = Encoding.ASCII.GetString(B64(sigs[0], "verifier"));
                if (!PemEncoding.TryFind(verifier, out var pem) || verifier[pem.Label] != "CERTIFICATE"
                    || !Convert.FromBase64String(verifier[pem.Base64Data]).AsSpan().SequenceEqual(leaf.RawData))
                    throw new Failure("the entry is for another certificate");
            }

            // The signed entry timestamp: the log's promise to include it.
            var setJson = $"{{\"body\":\"{bodyText}\",\"integratedTime\":{integrated},\"logID\":\"{Convert.ToHexStringLower(logId)}\",\"logIndex\":{logIndex}}}";
            var set = B64(Obj(entry, "inclusionPromise"), "signedEntryTimestamp");
            if (!log.Key.VerifyData(Encoding.UTF8.GetBytes(setJson), set, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new Failure("its signed entry timestamp does not verify with the log's key");

            // The inclusion proof, up to a checkpoint the log signed.
            var proof = Obj(entry, "inclusionProof");
            long index = long.Parse(Str(proof, "logIndex") ?? "", System.Globalization.CultureInfo.InvariantCulture);
            long size = long.Parse(Str(proof, "treeSize") ?? "", System.Globalization.CultureInfo.InvariantCulture);
            var rootHash = B64(proof, "rootHash");
            var path = Array(proof, "hashes").Select(h => Convert.FromBase64String(h.GetString() ?? "")).ToArray();
            if (!MerkleInclusion(LeafHash(body), index, size, path, rootHash)) throw new Failure("its inclusion proof does not lead to the proof's root");
            VerifyCheckpoint(Str(Obj(proof, "checkpoint"), "envelope") ?? "", log, size, rootHash);
            return time;
        }

        static byte[] LeafHash(byte[] leaf) => SHA256.HashData([0, .. leaf]);
        static byte[] NodeHash(byte[] left, byte[] right) => SHA256.HashData([1, .. left, .. right]);

        /// RFC 9162 2.1.3.2: the root an inclusion proof leads to.
        public static bool MerkleInclusion(byte[] leafHash, long index, long size, byte[][] path, byte[] root)
        {
            if (index < 0 || index >= size) return false;
            long fn = index, sn = size - 1;
            var r = leafHash;
            foreach (var p in path)
            {
                if (p.Length != 32 || sn == 0) return false;
                if ((fn & 1) == 1 || fn == sn)
                {
                    r = NodeHash(p, r);
                    if ((fn & 1) == 0)
                        while ((fn & 1) == 0 && fn != 0) { fn >>= 1; sn >>= 1; }
                }
                else r = NodeHash(r, p);
                fn >>= 1;
                sn >>= 1;
            }
            return sn == 0 && r.AsSpan().SequenceEqual(root);
        }

        /// A signed note (origin, tree size, root hash, other lines; a blank line; signature
        /// lines "— NAME BASE64(key hint + signature)"), one of whose signatures is the log's.
        static void VerifyCheckpoint(string note, TrustedRoot.Log log, long size, byte[] rootHash)
        {
            int split = note.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0) throw new Failure("its checkpoint is not a signed note");
            var text = note.Substring(0, split + 1);
            var lines = text.Split('\n');
            if (lines.Length < 4 || lines[1] != size.ToString(System.Globalization.CultureInfo.InvariantCulture) || lines[2] != Convert.ToBase64String(rootHash))
                throw new Failure("its checkpoint is for another tree");
            var data = Encoding.UTF8.GetBytes(text);
            foreach (var line in note.Substring(split + 2).Split('\n'))
            {
                if (!line.StartsWith("— ", StringComparison.Ordinal)) continue;
                var parts = line.Substring(2).Split(' ');
                if (parts.Length != 2) continue;
                byte[] sig;
                try { sig = Convert.FromBase64String(parts[1]); }
                catch (FormatException) { continue; }
                if (sig.Length > 4 && log.Key.VerifyData(data, sig.AsSpan(4), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return;
            }
            throw new Failure("its checkpoint is not signed by the log's key");
        }

        /// The leaf's issuing certificate when the leaf chains to an authority of the trusted root
        /// valid at `time`.
        static X509Certificate2 VerifyChain(X509Certificate2 leaf, TrustedRoot trust, DateTimeOffset time)
        {
            if (!Within(leaf, time)) throw new Failure($"the certificate was not valid at the log entry's time {time:u}");
            if (!leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.3")))
                throw new Failure("the certificate is not for code signing");
            if (leaf.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority)) throw new Failure("the certificate is a CA's");
            foreach (var ca in trust.Authorities)
            {
                if (!Covers(ca.Start, ca.End, time)) continue;
                var issuer = ca.Chain[0];
                if (!leaf.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData) || !SignedBy(leaf, issuer)) continue;
                for (int i = 0; i < ca.Chain.Length; i++)
                {
                    var c = ca.Chain[i];
                    var parent = i + 1 < ca.Chain.Length ? ca.Chain[i + 1] : c;
                    if (!Within(c, time)) throw new Failure($"the authority's certificate {c.Subject} was not valid at {time:u}");
                    if (!c.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority)) throw new Failure($"{c.Subject} is not a CA");
                    if (!c.IssuerName.RawData.AsSpan().SequenceEqual(parent.SubjectName.RawData) || !SignedBy(c, parent))
                        throw new Failure($"the authority's chain is broken at {c.Subject}");
                }
                return issuer;
            }
            throw new Failure($"the certificate is not issued by a trusted Fulcio authority ({leaf.Issuer})");
        }

        static bool Within(X509Certificate2 c, DateTimeOffset t) => t >= new DateTimeOffset(c.NotBefore.ToUniversalTime()) && t <= new DateTimeOffset(c.NotAfter.ToUniversalTime());

        /// The certificate's signature by `issuer`'s ECDSA key.
        static bool SignedBy(X509Certificate2 cert, X509Certificate2 issuer)
        {
            var (tbs, algorithm, signature) = SplitCertificate(cert.RawData);
            HashAlgorithmName hash = algorithm switch
            {
                "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
                "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
                "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
                _ => throw new Failure($"{cert.Subject} is signed with {algorithm}, not ECDSA"),
            };
            using var key = issuer.GetECDsaPublicKey() ?? throw new Failure($"{issuer.Subject}'s key is not ECDSA");
            return key.VerifyData(tbs, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
        }

        static (byte[] Tbs, string Algorithm, byte[] Signature) SplitCertificate(byte[] der)
        {
            var reader = new AsnReader(der, AsnEncodingRules.DER);
            var cert = reader.ReadSequence();
            var tbs = cert.ReadEncodedValue().ToArray();
            var alg = cert.ReadSequence();
            var oid = alg.ReadObjectIdentifier();
            var signature = cert.ReadBitString(out int unused);
            if (unused != 0) throw new Failure("a certificate's signature is not whole bytes");
            return (tbs, oid, signature);
        }

        /// RFC 6962: an SCT embedded in the leaf, signed by a CT log of the trusted root over the
        /// precertificate (the leaf's TBSCertificate without the SCT list) and its issuer's key.
        static void VerifySct(X509Certificate2 leaf, X509Certificate2 issuer, TrustedRoot trust)
        {
            var ext = leaf.Extensions[SctOid] ?? throw new Failure("the certificate has no SCT");
            var list = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadOctetString();
            var precert = PrecertificateTbs(SplitCertificate(leaf.RawData).Tbs);
            var issuerKeyHash = SHA256.HashData(issuer.PublicKey.ExportSubjectPublicKeyInfo());
            var span = list.AsSpan();
            int total = BinaryPrimitives.ReadUInt16BigEndian(span);
            if (total != span.Length - 2) throw new Failure("the certificate's SCT list is malformed");
            span = span.Slice(2);
            while (span.Length > 0)
            {
                int len = BinaryPrimitives.ReadUInt16BigEndian(span);
                var sct = span.Slice(2, len);
                span = span.Slice(2 + len);
                if (sct[0] != 0) continue; // v1 only
                var logId = sct.Slice(1, 32).ToArray();
                ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(sct.Slice(33));
                int extLen = BinaryPrimitives.ReadUInt16BigEndian(sct.Slice(41));
                var extensions = sct.Slice(43, extLen);
                var rest = sct.Slice(43 + extLen);
                if (rest[0] != 4 || rest[1] != 3) continue; // SHA-256, ECDSA
                int sigLen = BinaryPrimitives.ReadUInt16BigEndian(rest.Slice(2));
                var sig = rest.Slice(4, sigLen);
                var log = trust.CtLogs.FirstOrDefault(l => l.KeyId.AsSpan().SequenceEqual(logId));
                if (log == null || !Covers(log.Start, log.End, DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp))) continue;
                var signed = new List<byte> { 0, 0 };
                var eight = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(eight, timestamp);
                signed.AddRange(eight);
                signed.AddRange([0, 1]); // precert_entry
                signed.AddRange(issuerKeyHash);
                signed.AddRange([(byte)(precert.Length >> 16), (byte)(precert.Length >> 8), (byte)precert.Length]);
                signed.AddRange(precert);
                signed.AddRange([(byte)(extLen >> 8), (byte)extLen]);
                signed.AddRange(extensions.ToArray());
                if (log.Key.VerifyData(signed.ToArray(), sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return;
            }
            throw new Failure("no SCT of the certificate is signed by a trusted CT log");
        }

        /// The TBSCertificate with the SCT list extension removed, as the CT log saw it.
        public static byte[] PrecertificateTbs(byte[] tbs)
        {
            var reader = new AsnReader(tbs, AsnEncodingRules.DER).ReadSequence();
            var writer = new AsnWriter(AsnEncodingRules.DER);
            var extensionsTag = new Asn1Tag(TagClass.ContextSpecific, 3, isConstructed: true);
            using (writer.PushSequence())
            {
                while (reader.HasData)
                {
                    if (reader.PeekTag() != extensionsTag) { writer.WriteEncodedValue(reader.ReadEncodedValue().Span); continue; }
                    var outer = reader.ReadSequence(extensionsTag);
                    var extensions = outer.ReadSequence();
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
                    using (writer.PushSequence())
                        while (extensions.HasData)
                        {
                            var encoded = extensions.ReadEncodedValue();
                            var oid = new AsnReader(encoded, AsnEncodingRules.DER).ReadSequence().ReadObjectIdentifier();
                            if (oid != SctOid) writer.WriteEncodedValue(encoded.Span);
                        }
                }
            }
            return writer.Encode();
        }

        static void VerifyIdentity(X509Certificate2 leaf, Identity want)
        {
            var sans = SanUris(leaf);
            if (sans.Count != 1 || !want.SubjectAlternativeNames.Contains(sans[0]))
                throw new Failure($"signed by {string.Join(", ", sans.DefaultIfEmpty("no identity"))}, not {string.Join(" or ", want.SubjectAlternativeNames)}");
            string? Fulcio(int n)
            {
                var e = leaf.Extensions[FulcioOid + n];
                if (e == null) return null;
                // 1.1 to 1.6 hold the raw string; 1.8 on, a DER UTF8String.
                return n <= 6 ? Encoding.UTF8.GetString(e.RawData) : new AsnReader(e.RawData, AsnEncodingRules.DER).ReadCharacterString(UniversalTagNumber.UTF8String);
            }
            void Want(string what, int n, string? expected)
            {
                if (expected == null) return;
                var got = Fulcio(n);
                if (got != expected) throw new Failure($"its {what} is {got ?? "missing"}, not {expected}");
            }
            Want("OIDC issuer", 8, want.Issuer);
            if (Fulcio(1) is { } legacy && legacy != want.Issuer) throw new Failure($"its OIDC issuer is {legacy}, not {want.Issuer}");
            Want("build signer", 9, sans[0]);
            Want("source repository", 12, want.SourceRepository);
            Want("source repository ID", 15, want.SourceRepositoryId);
            Want("source repository owner ID", 17, want.SourceRepositoryOwnerId);
            Want("runner environment", 11, want.RunnerEnvironment);
            var reference = Fulcio(14);
            if (reference == null || !want.SourceRepositoryRefs.Contains(reference)) throw new Failure($"it was built from {reference ?? "no ref"}, not {string.Join(" or ", want.SourceRepositoryRefs)}");
            if (want.BuildConfigPrefix != null && Fulcio(18) is var config && (config == null || !config.StartsWith(want.BuildConfigPrefix, StringComparison.Ordinal)))
                throw new Failure($"its workflow is {config ?? "missing"}, not {want.BuildConfigPrefix}…");
        }

        static List<string> SanUris(X509Certificate2 cert)
        {
            var found = new List<string>();
            var ext = cert.Extensions["2.5.29.17"];
            if (ext == null) return found;
            var names = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence();
            var uri = new Asn1Tag(TagClass.ContextSpecific, 6);
            while (names.HasData)
            {
                if (names.PeekTag() == uri) found.Add(names.ReadCharacterString(UniversalTagNumber.IA5String, uri));
                else
                {
                    names.ReadEncodedValue();
                    found.Add("a non-URI name");
                }
            }
            return found;
        }

        static JsonElement Obj(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : throw new Failure($"no {name}");

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : [];

        static byte[] B64(JsonElement e, string name) => Convert.FromBase64String(Str(e, name) ?? throw new Failure($"no {name}"));
    }
}
