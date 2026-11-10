#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OniMoono.Host.Install;
using Xunit;

namespace OniMoono.Tests
{
    /// The offline Sigstore check the installer runs on the Workshop item (Install/Sigstore.cs):
    /// a real bundle (TestData/sigstore, a part of a Workshop item's attestation from a
    /// workshop.yml run on master) against the embedded public-good root, and the tests' own
    /// Sigstore (TestSigstore) for what no real bundle can show.
    public sealed class SigstoreTests
    {
        static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "sigstore", "workshop-item-part.sigstore.json");
        static byte[] Fixture => File.ReadAllBytes(FixturePath);
        const string FixtureFile = "workshop-item/windows-x64/player/System.IO.Compression.dll";

        static Sigstore.Failure Refused(byte[] bundle, Sigstore.TrustedRoot? trust = null, Sigstore.Identity? identity = null) =>
            Assert.Throws<Sigstore.Failure>(() => Sigstore.Verify(bundle, trust ?? Sigstore.TrustedRoot.Embedded, identity ?? Sigstore.MoonoWorkshop));

        /// The bundle with one JSON edit.
        static byte[] Edit(byte[] bundle, Action<JsonNode> edit)
        {
            var node = JsonNode.Parse(bundle)!;
            edit(node);
            return Encoding.UTF8.GetBytes(node.ToJsonString());
        }

        static byte[] EditPayload(byte[] bundle, Func<string, string> edit) => Edit(bundle, n =>
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String((string)n["dsseEnvelope"]!["payload"]!));
            n["dsseEnvelope"]!["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(edit(payload)));
        });

        [Fact]
        public void RealBundleVerifiesOfflineAgainstTheEmbeddedRoot()
        {
            var subjects = Sigstore.Verify(Fixture, Sigstore.TrustedRoot.Embedded, Sigstore.MoonoWorkshop);
            Assert.Equal(191, subjects.Count);
            Assert.Contains(subjects, s => s.Name == FixtureFile && s.Sha256.Length == 64);
            Assert.All(subjects, s => Assert.StartsWith("workshop-item/", s.Name));
        }

        [Fact]
        public void RealBundleChangedIsRefused()
        {
            // A subject's digest changed: the envelope's signature fails.
            var tampered = EditPayload(Fixture, p => p.Replace("\"sha256\":\"", "\"sha256\":\"0", StringComparison.Ordinal));
            Assert.Contains("envelope's signature", Refused(tampered).Message);
            // The entry's time moved: the signed entry timestamp fails.
            var moved = Edit(Fixture, n => n["verificationMaterial"]!["tlogEntries"]![0]!["integratedTime"] = ((long.Parse((string)n["verificationMaterial"]!["tlogEntries"]![0]!["integratedTime"]!) + 1)).ToString());
            Assert.Contains("signed entry timestamp", Refused(moved).Message);
            // The inclusion proof's path changed.
            var proof = Edit(Fixture, n => n["verificationMaterial"]!["tlogEntries"]![0]!["inclusionProof"]!["hashes"]![0] = Convert.ToBase64String(new byte[32]));
            Assert.Contains("inclusion proof", Refused(proof).Message);
            // Another certificate (one of the tests' Sigstore) in place of Fulcio's.
            var other = JsonNode.Parse(TestSigstore.Shared.Sign([("x", new string('0', 64))]))!;
            var swapped = Edit(Fixture, n => n["verificationMaterial"]!["certificate"] = other["verificationMaterial"]!["certificate"]!.DeepClone());
            Refused(swapped);
            // Not a bundle.
            Assert.Contains("malformed", Refused("{]"u8.ToArray()).Message);
            Refused("{}"u8.ToArray());
        }

        [Fact]
        public void RealBundleOfAnotherSignerIsRefused()
        {
            var moono = Sigstore.MoonoWorkshop;
            Assert.Contains("source repository", Refused(Fixture, identity: moono with { SourceRepository = "https://github.com/someone/oni-moono" }).Message);
            Assert.Contains("signed by", Refused(Fixture, identity: moono with { SubjectAlternativeNames = ["https://github.com/someone/oni-moono/.github/workflows/attest.yml@refs/heads/master"] }).Message);
            Assert.Contains("repository ID", Refused(Fixture, identity: moono with { SourceRepositoryId = "1" }).Message);
            Assert.Contains("owner ID", Refused(Fixture, identity: moono with { SourceRepositoryOwnerId = "1" }).Message);
            Assert.Contains("OIDC issuer", Refused(Fixture, identity: moono with { Issuer = "https://accounts.google.com" }).Message);
            Assert.Contains("built from", Refused(Fixture, identity: moono with { SourceRepositoryRefs = ["refs/heads/u60"] }).Message);
            // A release's attestation (release.yml calling attest.yml) is not a Workshop item's.
            Assert.Contains("workflow", Refused(Fixture, identity: moono with { BuildConfigPrefix = $"https://github.com/{Sigstore.Repository}/.github/workflows/release.yml@" }).Message);
        }

        [Fact]
        public void RealBundleNeedsEachPartOfTheRoot()
        {
            static Sigstore.TrustedRoot Without(string key)
            {
                var root = JsonNode.Parse(SigstoreTrustedRoot.Json)!;
                root[key] = new JsonArray();
                return Sigstore.TrustedRoot.Parse(root.ToJsonString());
            }
            Assert.Contains("Fulcio authority", Refused(Fixture, Without("certificateAuthorities")).Message);
            Assert.Contains("not in the trusted root", Refused(Fixture, Without("tlogs")).Message);
            Assert.Contains("SCT", Refused(Fixture, Without("ctlogs")).Message);
            // The tests' own root trusts none of it.
            Refused(Fixture, TestSigstore.Shared.Trust);
        }

        [Fact]
        public void TestSigstoresBundleVerifies()
        {
            var s = TestSigstore.Shared;
            var subjects = Sigstore.Verify(s.Sign([("a", new string('a', 64)), ("b", new string('b', 64))]), s.Trust, TestSigstore.Identity);
            Assert.Equal(["a", "b"], subjects.Select(x => x.Name).ToArray());
            // The embedded root does not trust it.
            Refused(s.Sign([("a", new string('a', 64))]));
        }

        [Fact]
        public void ExpiredOrUntrustedChainIsRefused()
        {
            var s = TestSigstore.Shared;
            (string, string)[] one = [("a", new string('a', 64))];
            // Logged after the ten minutes the certificate lives, or before it.
            Assert.Contains("not valid at", Refused(s.Sign(one, new() { EntryAfterIssue = TimeSpan.FromMinutes(11) }), s.Trust, TestSigstore.Identity).Message);
            Assert.Contains("not valid at", Refused(s.Sign(one, new() { EntryAfterIssue = TimeSpan.FromMinutes(-1) }), s.Trust, TestSigstore.Identity).Message);
            // Issued by an authority the root doesn't name (its CT log isn't trusted either).
            Assert.Contains("Fulcio authority", Refused(s.Sign(one, new() { Issuer2 = new TestSigstore("other") }), s.Trust, TestSigstore.Identity).Message);
            // No SCT.
            Assert.Contains("SCT", Refused(s.Sign(one, new() { NoSct = true }), s.Trust, TestSigstore.Identity).Message);
            // Another runner, ref or workflow.
            Assert.Contains("runner", Refused(s.Sign(one, new() { Runner = "self-hosted" }), s.Trust, TestSigstore.Identity).Message);
            Assert.Contains("built from", Refused(s.Sign(one, new() { Ref = "refs/heads/feature" }), s.Trust, TestSigstore.Identity).Message);
            Assert.Contains("workflow", Refused(s.Sign(one, new() { BuildConfig = $"https://github.com/{Sigstore.Repository}/.github/workflows/release.yml@refs/heads/master" }), s.Trust, TestSigstore.Identity).Message);
        }

        [Fact]
        public void MerkleInclusionFollowsRfc9162()
        {
            for (int n = 1; n <= 9; n++)
            {
                var leaves = Enumerable.Range(0, n).Select(i => Encoding.UTF8.GetBytes("leaf " + i)).ToArray();
                var root = TestSigstore.TreeHash(leaves);
                for (int m = 0; m < n; m++)
                {
                    var leaf = System.Security.Cryptography.SHA256.HashData([0, .. leaves[m]]);
                    var path = TestSigstore.InclusionPath(m, leaves).ToArray();
                    Assert.True(Sigstore.MerkleInclusion(leaf, m, n, path, root), $"{m} of {n}");
                    Assert.False(Sigstore.MerkleInclusion(leaf, m, n, [.. path, new byte[32]], root));
                    if (path.Length > 0) Assert.False(Sigstore.MerkleInclusion(leaf, m, n, [new byte[32], .. path[1..]], root));
                    Assert.False(Sigstore.MerkleInclusion(leaf, n, n, path, root));
                    if (n > 1) Assert.False(Sigstore.MerkleInclusion(leaf, (m + 1) % n, n, path, root));
                }
            }
        }
    }
}
