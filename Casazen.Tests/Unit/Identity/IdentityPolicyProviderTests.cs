using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Infrastructure.Storage;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>Where the policy files of the plan are, and the committed one, for the tests of the policy (E70/F01/US02/T01).</summary>
internal static class IdentityPolicyTestFiles
{
    public const string ShippedFileName = "identity-policy-2026.11-1.json";

    /// <summary>The canonical SHA-256 of the committed policy file, computed outside this code base (a script that sorts the members and writes no white space).</summary>
    public const string ShippedCanonicalHash = "99831295b2352a5e317b84de790531f10f9be8d58aea62296b0a54a70f165aae";

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }

    public static string SourceFolder()
    {
        return Path.Combine(RepositoryRoot(), "Casazen.Web", "IdentityPolicies");
    }

    /// <summary>The text of the committed policy file, as the checkout of this machine has it.</summary>
    public static string ShippedText()
    {
        return File.ReadAllText(Path.Combine(SourceFolder(), ShippedFileName));
    }

    /// <summary>The committed policy with the members at <paramref name="edits"/> set to a JSON literal (a null literal removes the member), written compactly.</summary>
    public static string Edited(params (string Path, string? Literal)[] edits)
    {
        return Apply(ShippedText(), edits);
    }

    /// <summary>The JSON <paramref name="text"/> with the members at the dotted paths of <paramref name="edits"/> set to a JSON literal (a null literal removes the member), written compactly.</summary>
    public static string Apply(string text, params (string Path, string? Literal)[] edits)
    {
        var root = JsonNode.Parse(text)!.AsObject();
        foreach (var (path, literal) in edits)
        {
            var segments = path.Split('.');
            var parent = root;
            foreach (var segment in segments[..^1])
                parent = parent[segment]!.AsObject();

            if (literal is null)
                parent.Remove(segments[^1]);
            else
                parent[segments[^1]] = JsonNode.Parse(literal);
        }

        return root.ToJsonString();
    }
}

/// <summary>
/// E70/F01/US02/T01 (NF-193, NF-194, ADR-013-R18): the decision policy is read once, validated and used only when its canonical SHA-256 is the pinned one; a
/// policy that cannot be used is <see cref="IdentityPolicyState.Unavailable"/> with a code and never an exception; the fingerprint of the configuration is null
/// while any model is missing and changes with the policy hash and with every weight hash.
/// </summary>
public sealed class IdentityPolicyProviderTests : IDisposable
{
    private const string PolicyFile = "policy.json";

    private const string DetectorHash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string AntiSpoofingHash = "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";
    private const string RecognitionHash = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-identity-policy-" + Guid.NewGuid().ToString("N"));

    public IdentityPolicyProviderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Snapshot_TheCommittedPolicyWithItsPin_IsReadyAndProvisional_AndEveryMemberIsRead()
    {
        var provider = ProviderFor(IdentityPolicyTestFiles.ShippedText());

        var snapshot = provider.Snapshot;

        Assert.Equal(IdentityPolicyState.Ready, snapshot.State);
        Assert.True(snapshot.IsReady);
        Assert.Null(snapshot.ReasonCode);
        Assert.Equal(IdentityPolicyTestFiles.ShippedCanonicalHash, snapshot.Hash);
        Assert.True(snapshot.Provisional);
        var policy = snapshot.Policy!;
        Assert.Equal(("identity-policy-2026.11-1", true, "capture-2026.11-1"), (policy.Version, policy.Provisional, policy.CaptureRules));
        Assert.Equal((3, 6), (policy.Document.MaxRecapturesPerSide, policy.Document.PerceptualDuplicateMaxDistance));
        Assert.Equal(["surname", "givenNames", "birthDate", "documentNumber"], policy.Document.IdentifyingFields);
        Assert.Equal((3, 90, 1500, 1000, true), (policy.Presence.Challenge.Steps, policy.Presence.Challenge.LifetimeSeconds, policy.Presence.Challenge.StepMs, policy.Presence.Challenge.LeadInMs, policy.Presence.Challenge.NoImmediateRepeat));
        Assert.Equal(PresenceActions.All, policy.Presence.Challenge.Actions);
        Assert.Equal((8, 20, 60, 3000, 12000, 400, 0.1, 320), (policy.Presence.Sequence.TargetFps, policy.Presence.Sequence.MinFrames, policy.Presence.Sequence.MaxFrames, policy.Presence.Sequence.MinDurationMs, policy.Presence.Sequence.MaxDurationMs, policy.Presence.Sequence.MaxGapMs, policy.Presence.Sequence.MaxShareWithoutFace, policy.Presence.Sequence.DetectorMaxSide));
        Assert.Equal(
            (0.35, 0.3, 0.5, 5, 1.0),
            (policy.Presence.Continuity.MaxCenterJump, policy.Presence.Continuity.MaxSizeChange, policy.Presence.Continuity.MinPairSimilarity, policy.Presence.Continuity.PairSamples, policy.Presence.Continuity.MinPixelDifference));
        Assert.Equal((20.0, 12.0, 15.0), (policy.Presence.Pose.TurnMinYawDegrees, policy.Presence.Pose.LookMinPitchDegrees, policy.Presence.Pose.NeutralMaxDegrees));
        Assert.Equal((5, 0.7, IdentityLivenessRules.AllFrames), (policy.Presence.Liveness.Frames, policy.Presence.Liveness.MinScore, policy.Presence.Liveness.Rule));
        Assert.Equal((3, 300, 2, IdentityAggregationRules.Median, 0.85, 0.7, 0.7), (policy.Match.Frames, policy.Match.MinFrameGapMs, policy.Match.RandomizeAmongTop, policy.Match.Aggregation, policy.Match.Accept, policy.Match.ReviewFloor, policy.Match.MinEachFrame));
        Assert.Equal(1, policy.Closure.GuidedRetries);

        // Nothing the file says is dropped on the way: the policy written back is, canonically, the file.
        var written = JsonSerializer.SerializeToUtf8Bytes(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(IdentityPolicyTestFiles.ShippedCanonicalHash, IdentityPolicyProvider.CanonicalSha256(written));
    }

    [Fact]
    public void CanonicalSha256_TheLfAndTheCrlfVersionsOfOneFile_AreIdentical()
    {
        // A checkout on Windows turns the line endings into CRLF, an editor may add a byte order mark: the pin must not care.
        var lf = IdentityPolicyTestFiles.ShippedText().Replace("\r\n", "\n", StringComparison.Ordinal);
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(crlf)).ToArray();

        var hashes = new[] { Encoding.UTF8.GetBytes(lf), Encoding.UTF8.GetBytes(crlf), withBom }
            .Select(bytes => IdentityPolicyProvider.CanonicalSha256(bytes))
            .Distinct()
            .ToList();

        Assert.Equal(IdentityPolicyTestFiles.ShippedCanonicalHash, Assert.Single(hashes));
    }

    [Fact]
    public void Canonical_TheFormIsTheDocumentedOne_AndOnlyAValueOrANameChangesTheHash()
    {
        const string text = """{"b": 1, "a": [0.70, 1.0, 2, -0.0, "x\n\"q\"\\"], "c": {"z": true, "y": null, "ab": "è\u0001"}}""";
        const string respelled = "{\r\n\t\"c\":{\"ab\":\"\\u00e8\\u0001\",\"y\":null,\"z\":true},\n\"a\":[ 0.7 , 1 , 2 , 0 , \"x\\n\\\"q\\\"\\\\\" ] , \"b\" : 1 }";
        const string changedValue = """{"b": 1, "a": [0.71, 1.0, 2, -0.0, "x\n\"q\"\\"], "c": {"z": true, "y": null, "ab": "è\u0001"}}""";
        const string changedName = """{"B": 1, "a": [0.70, 1.0, 2, -0.0, "x\n\"q\"\\"], "c": {"z": true, "y": null, "ab": "è\u0001"}}""";

        Assert.True(IdentityConfigurationJson.TryCanonicalize(Encoding.UTF8.GetBytes(text), out var canonical));

        Assert.Equal("{\"a\":[0.7,1,2,0,\"x\\n\\\"q\\\"\\\\\"],\"b\":1,\"c\":{\"ab\":\"è\\u0001\",\"y\":null,\"z\":true}}", canonical);
        Assert.Equal(
            IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(text)),
            IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(respelled)));
        Assert.NotEqual(IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(text)), IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(changedValue)));
        Assert.NotEqual(IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(text)), IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(changedName)));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{"a":1} // a comment""")]
    [InlineData("""/* a comment */ {"a":1}""")]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":1}{"b":2}""")]
    [InlineData("""{"a":"\ud800"}""")]
    [InlineData("""{"a":1e999}""")]
    public void CanonicalSha256_TextThatIsNotStrictJson_IsNull(string text)
    {
        Assert.Null(IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Snapshot_AMissingFile_IsUnavailable_WithItsCode_AndNeverThrows()
    {
        var provider = Create("does-not-exist.json", new string('a', 64));

        var snapshot = provider.Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyMissing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a-folder")]
    public void Snapshot_ABlankPathOrAFolder_IsUnavailable_AsMissing(string policyFile)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "a-folder"));

        var snapshot = Create(policyFile, new string('a', 64)).Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyMissing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""// a comment""")]
    public void Snapshot_AFileThatIsNotAPolicy_IsInvalid(string text)
    {
        var snapshot = ProviderFor(text).Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyInvalid);
    }

    [Theory]
    [InlineData("match.accept", "1.5")]
    [InlineData("match.accept", "0")]
    [InlineData("match.accept", "-0.2")]
    [InlineData("match.reviewFloor", "0.9")]
    [InlineData("match.reviewFloor", "0")]
    [InlineData("match.minEachFrame", "0")]
    [InlineData("match.frames", "0")]
    [InlineData("match.frames", "21")]
    [InlineData("match.randomizeAmongTop", "0")]
    [InlineData("match.aggregation", "\"mode\"")]
    [InlineData("match.aggregation", "\"Median\"")]
    [InlineData("presence.challenge.lifetimeSeconds", "301")]
    [InlineData("presence.challenge.lifetimeSeconds", "0")]
    [InlineData("presence.challenge.steps", "0")]
    [InlineData("presence.challenge.steps", "11")]
    [InlineData("presence.challenge.actions", "[]")]
    [InlineData("presence.challenge.actions", "[\"turn_left\",\"turn_left\",\"look_up\",\"look_down\"]")]
    [InlineData("presence.challenge.actions", "[\"turn_left\",\"jump\",\"look_up\",\"look_down\"]")]
    [InlineData("presence.liveness.rule", "\"any\"")]
    [InlineData("presence.liveness.minScore", "1.01")]
    [InlineData("presence.liveness.frames", "21")]
    [InlineData("presence.sequence.minFrames", "61")]
    [InlineData("presence.sequence.minDurationMs", "12001")]
    [InlineData("presence.sequence.maxShareWithoutFace", "1.5")]
    [InlineData("presence.sequence.maxShareWithoutFace", "-0.1")]
    [InlineData("presence.continuity.maxCenterJump", "0")]
    [InlineData("presence.continuity.minPairSimilarity", "2")]
    [InlineData("presence.continuity.minPixelDifference", "0")]
    [InlineData("presence.continuity.minPixelDifference", "256")]
    [InlineData("presence.pose.turnMinYawDegrees", "91")]
    [InlineData("presence.pose.neutralMaxDegrees", "0")]
    [InlineData("closure.guidedRetries", "2")]
    [InlineData("closure.guidedRetries", "-1")]
    [InlineData("document.identifyingFields", "[]")]
    [InlineData("document.identifyingFields", "[\"Surname\"]")]
    [InlineData("document.perceptualDuplicateMaxDistance", "65")]
    [InlineData("version", "\"Identity Policy\"")]
    [InlineData("version", "\"\"")]
    [InlineData("captureRules", "\"\"")]
    public void Snapshot_AMemberOutOfItsBounds_IsInvalid(string path, string literal)
    {
        var snapshot = ProviderFor(IdentityPolicyTestFiles.Edited((path, literal))).Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyInvalid);
    }

    [Theory]
    [InlineData("match", null)]
    [InlineData("match.accept", null)]
    [InlineData("presence.challenge.actions", null)]
    [InlineData("presence.continuity.minPixelDifference", null)]
    [InlineData("provisional", null)]
    [InlineData("extra", "1")]
    [InlineData("match.extra", "true")]
    [InlineData("match.accept", "\"0.85\"")]
    [InlineData("match.accept", "null")]
    [InlineData("provisional", "\"true\"")]
    [InlineData("presence.challenge.noImmediateRepeat", "1")]
    [InlineData("match.frames", "3.5")]
    [InlineData("document", "[]")]
    public void Snapshot_AMemberMissingUnknownOrOfTheWrongType_IsInvalid(string path, string? literal)
    {
        // Strict: a member that is spelled wrong or has the wrong shape is refused, never ignored and never defaulted.
        var snapshot = ProviderFor(IdentityPolicyTestFiles.Edited((path, literal))).Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyInvalid);
    }

    [Fact]
    public void IdentityPolicyProvider_OneBitChallenge_IsInvalid()
    {
        // Security (NF-192): a challenge of one bit (two moves, never the same twice in a row) is a coin toss a replayed video wins half the time.
        var oneBit = IdentityPolicyTestFiles.Edited(
            ("presence.challenge.actions", "[\"turn_left\",\"turn_right\"]"),
            ("presence.challenge.steps", "3"),
            ("presence.challenge.noImmediateRepeat", "true"));

        var snapshot = ProviderFor(oneBit).Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyInvalid);
        Assert.Equal(1.0, IdentityPolicyProvider.ChallengeEntropyBits(2, 3, noImmediateRepeat: true), 9);
    }

    [Theory]
    [InlineData("[\"turn_left\",\"turn_right\"]", 1, true, false)]
    [InlineData("[\"turn_left\",\"turn_right\",\"look_up\"]", 1, true, false)]
    [InlineData("[\"turn_left\"]", 1, true, false)]
    [InlineData("[\"turn_left\"]", 5, false, false)]
    [InlineData("[\"turn_left\",\"turn_right\"]", 5, true, false)]
    [InlineData("[\"turn_left\",\"turn_right\",\"look_up\",\"look_down\"]", 1, true, true)]
    [InlineData("[\"turn_left\",\"turn_right\"]", 2, false, true)]
    [InlineData("[\"turn_left\",\"turn_right\",\"look_up\"]", 2, true, true)]
    [InlineData("[\"turn_left\",\"turn_right\",\"look_up\",\"look_down\"]", 3, true, true)]
    public void Snapshot_TheChallengeNeedsAtLeastTwoBitsOfEntropy(string actions, int steps, bool noImmediateRepeat, bool valid)
    {
        var text = IdentityPolicyTestFiles.Edited(
            ("presence.challenge.actions", actions),
            ("presence.challenge.steps", steps.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("presence.challenge.noImmediateRepeat", noImmediateRepeat ? "true" : "false"));

        var snapshot = ProviderFor(text).Snapshot;

        Assert.Equal(valid ? IdentityPolicyState.Ready : IdentityPolicyState.Unavailable, snapshot.State);
    }

    [Theory]
    [InlineData(4, 3, true, 5.169925001442312)]
    [InlineData(4, 1, true, 2.0)]
    [InlineData(2, 3, true, 1.0)]
    [InlineData(2, 5, false, 5.0)]
    [InlineData(3, 1, false, 1.584962500721156)]
    [InlineData(1, 3, true, 0.0)]
    [InlineData(1, 1, true, 0.0)]
    [InlineData(0, 3, true, 0.0)]
    [InlineData(4, 0, true, 0.0)]
    public void ChallengeEntropyBits_IsLog2OfTheNumberOfChallengesThatCanBeDrawn(int actions, int steps, bool noImmediateRepeat, double expected)
    {
        Assert.Equal(expected, IdentityPolicyProvider.ChallengeEntropyBits(actions, steps, noImmediateRepeat), 9);
    }

    [Fact]
    public void Snapshot_ALawfulChangeOfTheFileSpelling_StaysReady()
    {
        // The LF checkout, the CRLF checkout and the compact rewrite of one policy are one policy: the pin holds.
        var lf = IdentityPolicyTestFiles.ShippedText().Replace("\r\n", "\n", StringComparison.Ordinal);
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        var compact = JsonNode.Parse(lf)!.ToJsonString();

        foreach (var text in new[] { lf, crlf, compact })
        {
            var snapshot = ProviderFor(text, IdentityPolicyTestFiles.ShippedCanonicalHash).Snapshot;

            Assert.Equal(IdentityPolicyState.Ready, snapshot.State);
            Assert.Equal(IdentityPolicyTestFiles.ShippedCanonicalHash, snapshot.Hash);
        }
    }

    [Theory]
    [InlineData("presence.liveness.minScore", "0.5")]
    [InlineData("match.accept", "0.9")]
    [InlineData("match.minEachFrame", "0.6")]
    [InlineData("presence.challenge.steps", "4")]
    [InlineData("version", "\"identity-policy-2026.11-2\"")]
    [InlineData("provisional", "false")]
    public void IdentityPolicyProvider_ModifiedFile_IsUnavailable(string path, string literal)
    {
        // Security: a threshold (or the word "provisional") changed after the file was pinned is still a valid policy, and it must not be used: only the hash
        // tells it is not the one that was qualified. Nothing about the modified file is exposed.
        var logger = new CapturingLogger<IdentityPolicyProvider>();
        var modified = IdentityPolicyTestFiles.Edited((path, literal));
        File.WriteAllText(Path.Combine(_directory, PolicyFile), modified);

        var snapshot = Create(PolicyFile, IdentityPolicyTestFiles.ShippedCanonicalHash, logger).Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyHashMismatch);
        var computed = IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(modified))!;
        Assert.NotEqual(IdentityPolicyTestFiles.ShippedCanonicalHash, computed);

        // The log says what to pin (the hash of the file in the application) and why, with no path and no value.
        var line = Assert.Single(logger.Lines, entry => entry.Contains(IdentityPolicyReasonCodes.PolicyHashMismatch, StringComparison.Ordinal));
        Assert.Contains(computed, line, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, string.Concat(logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_ThePinIsEmpty_IsUnavailable_NotConfigured()
    {
        File.WriteAllText(Path.Combine(_directory, PolicyFile), IdentityPolicyTestFiles.ShippedText());

        var empty = Create(PolicyFile, string.Empty).Snapshot;
        var blank = Create(PolicyFile, "   ").Snapshot;

        AssertUnavailable(empty, IdentityPolicyReasonCodes.PolicyHashNotConfigured);
        AssertUnavailable(blank, IdentityPolicyReasonCodes.PolicyHashNotConfigured);
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("not a hash")]
    [InlineData("99831295b2352a5e317b84de790531f10f9be8d58aea62296b0a54a70f165aa")]
    [InlineData("99831295b2352a5e317b84de790531f10f9be8d58aea62296b0a54a70f165aaef")]
    public void Snapshot_APinThatIsNotTheHash_IsUnavailable_AsMismatch(string pin)
    {
        File.WriteAllText(Path.Combine(_directory, PolicyFile), IdentityPolicyTestFiles.ShippedText());

        var snapshot = Create(PolicyFile, pin).Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyHashMismatch);
    }

    [Fact]
    public void Snapshot_ThePinInUpperCaseOrWithSpaces_IsTheSamePin()
    {
        File.WriteAllText(Path.Combine(_directory, PolicyFile), IdentityPolicyTestFiles.ShippedText());

        var upper = Create(PolicyFile, IdentityPolicyTestFiles.ShippedCanonicalHash.ToUpperInvariant()).Snapshot;
        var spaced = Create(PolicyFile, "  " + IdentityPolicyTestFiles.ShippedCanonicalHash + " ").Snapshot;

        Assert.True(upper.IsReady);
        Assert.True(spaced.IsReady);
        Assert.Equal(IdentityPolicyTestFiles.ShippedCanonicalHash, upper.Hash);
    }

    [Fact]
    public void Snapshot_IsReadOnce_AndKeptForTheLifeOfTheProcess()
    {
        var provider = ProviderFor(IdentityPolicyTestFiles.ShippedText());
        var first = provider.Snapshot;

        File.WriteAllText(Path.Combine(_directory, PolicyFile), "garbage");
        var second = provider.Snapshot;

        Assert.Same(first, second);
        Assert.True(second.IsReady);
    }

    [Fact]
    public void Snapshot_ConfigurationThatCannotBeRead_IsUnavailable_AndNeverThrows()
    {
        var options = new Mock<IOptions<IdentityVerificationOptions>>();
        options.SetupGet(o => o.Value).Throws(new InvalidOperationException("the section could not be bound: 4321"));
        var logger = new CapturingLogger<IdentityPolicyProvider>();
        var provider = new IdentityPolicyProvider(options.Object, logger, _directory);

        var snapshot = provider.Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyInvalid);

        // The type of the failure, never its message.
        Assert.DoesNotContain("4321", string.Concat(logger.Lines), StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), string.Concat(logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_AnAbsolutePath_IsReadAsIs()
    {
        var path = Path.Combine(_directory, "elsewhere.json");
        File.WriteAllText(path, IdentityPolicyTestFiles.ShippedText());

        var snapshot = Create(path, IdentityPolicyTestFiles.ShippedCanonicalHash).Snapshot;

        Assert.True(snapshot.IsReady);
    }

    [Fact]
    public void Snapshot_AFileThatCannotBeRead_IsUnavailable_AsMissing_AndNeverThrows()
    {
        // Another process holds the file open: the read fails with an I/O error, which is no reason to throw at a reader, and never one to approve.
        var path = Path.Combine(_directory, PolicyFile);
        File.WriteAllText(path, IdentityPolicyTestFiles.ShippedText());
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var logger = new CapturingLogger<IdentityPolicyProvider>();

        var snapshot = Create(PolicyFile, IdentityPolicyTestFiles.ShippedCanonicalHash, logger).Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyMissing);
        Assert.Contains(nameof(IOException), string.Concat(logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_ATooLargeFile_IsInvalid_WithoutBeingRead()
    {
        File.WriteAllText(Path.Combine(_directory, PolicyFile), new string(' ', 300_000) + IdentityPolicyTestFiles.ShippedText());

        var snapshot = Create(PolicyFile, IdentityPolicyTestFiles.ShippedCanonicalHash).Snapshot;

        AssertUnavailable(snapshot, IdentityPolicyReasonCodes.PolicyInvalid);
    }

    [Fact]
    public void Validate_TheCommittedPolicy_HasNoFailure_AndANamedMemberIsReportedWithoutItsValue()
    {
        var committed = JsonSerializer.Deserialize<IdentityDecisionPolicy>(IdentityPolicyTestFiles.ShippedText(), IdentityConfigurationJson.Strict)!;
        var broken = committed with { Closure = new IdentityClosurePolicy(987_654), Match = committed.Match with { Accept = 0.31, ReviewFloor = 0.32 } };

        var failures = IdentityPolicyProvider.Validate(broken);

        Assert.Empty(IdentityPolicyProvider.Validate(committed));
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, failure => failure.StartsWith("match.reviewFloor ", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.StartsWith("closure.guidedRetries ", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, failure => failure.Contains("987654", StringComparison.Ordinal) || failure.Contains("0.32", StringComparison.Ordinal));
    }

    [Fact]
    public void IdentityPolicyReasonCodes_AreSpelledAsThePlanFixesThem()
    {
        Assert.Equal(["policy_missing", "policy_invalid", "policy_hash_mismatch", "policy_hash_not_configured"], IdentityPolicyReasonCodes.All);
        Assert.Equal(["median", "min", "mean"], IdentityAggregationRules.All);
        Assert.Equal(["all"], IdentityLivenessRules.All);
    }

    [Fact]
    public void AddCasazenServices_RegistersThePolicyProviderAndTheFingerprint_AsSingletons()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddCasazenServices();

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IdentityPolicyProvider)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IdentityConfigurationFingerprint)).Lifetime);
    }

    [Fact]
    public void IdentityConfigurationFingerprint_TheThreeModelsLoaded_IsTheDocumentedRecipe()
    {
        var provider = ProviderFor(IdentityPolicyTestFiles.ShippedText());
        var store = new FakeModelStore(ModelsSnapshot(DetectorHash, AntiSpoofingHash, RecognitionHash));
        var fingerprint = new IdentityConfigurationFingerprint(provider, store);

        var current = fingerprint.Current();

        Assert.NotNull(current);
        Assert.Equal("identity-policy-2026.11-1", current.PolicyVersion);
        Assert.Equal(IdentityPolicyTestFiles.ShippedCanonicalHash, current.PolicyHash);

        // Independent vectors (a script, not this code): the weights hash is the SHA-256 of the three lower case hashes joined with ":" in role order, the
        // fingerprint the SHA-256 of "policyHash:weightsHash".
        Assert.Equal("f299a350bdadffc513a0ec0a9f230297b0c5b364af3f5b553bbdede224043a46", current.WeightsHash);
        Assert.Equal("d721e2596b1b0b6afb8c634aa42698450c0a4b4041517fd5318c823a79994a19", current.Fingerprint);
        Assert.Equal(current.WeightsHash, IdentityConfigurationFingerprint.ComputeWeightsHash(store.Snapshot));
        Assert.Equal(current.Fingerprint, IdentityConfigurationFingerprint.ComputeFingerprint(current.PolicyHash, current.WeightsHash));
    }

    [Theory]
    [InlineData(BiometricModelRoles.FaceDetector)]
    [InlineData(BiometricModelRoles.AntiSpoofing)]
    [InlineData(BiometricModelRoles.FaceRecognition)]
    public void IdentityConfigurationFingerprint_AnyModelNotLoaded_IsNull(string role)
    {
        var provider = ProviderFor(IdentityPolicyTestFiles.ShippedText());
        var notLoaded = ModelsSnapshot(DetectorHash, AntiSpoofingHash, RecognitionHash, state: BiometricModelsState.Unavailable, notLoadedRole: role);
        var missing = new BiometricModelsSnapshot(
            BiometricModelsState.Ready,
            null,
            ModelsSnapshot(DetectorHash, AntiSpoofingHash, RecognitionHash).Models.Where(model => model.Role != role).ToList());
        var stateNotReady = ModelsSnapshot(DetectorHash, AntiSpoofingHash, RecognitionHash, state: BiometricModelsState.Loading);

        // Even a store that says Ready but whose entry of one role says "not loaded" gives nothing.
        var readyButNotLoaded = ModelsSnapshot(DetectorHash, AntiSpoofingHash, RecognitionHash, notLoadedRole: role);

        foreach (var snapshot in new[] { notLoaded, missing, stateNotReady, readyButNotLoaded })
        {
            Assert.Null(IdentityConfigurationFingerprint.ComputeWeightsHash(snapshot));
            Assert.Null(new IdentityConfigurationFingerprint(provider, new FakeModelStore(snapshot)).Current());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("0123456789ABCDEF")]
    public void IdentityConfigurationFingerprint_AWeightHashThatIsNotASha256_IsNull(string? weightHash)
    {
        var snapshot = new BiometricModelsSnapshot(
            BiometricModelsState.Ready,
            null,
            [
                Model(BiometricModelRoles.FaceDetector, DetectorHash, loaded: true),
                Model(BiometricModelRoles.AntiSpoofing, weightHash, loaded: true),
                Model(BiometricModelRoles.FaceRecognition, RecognitionHash, loaded: true),
            ]);

        Assert.Null(IdentityConfigurationFingerprint.ComputeWeightsHash(snapshot));
    }

    [Fact]
    public void IdentityConfigurationFingerprint_ThePolicyUnavailable_IsNull()
    {
        var provider = Create("does-not-exist.json", new string('a', 64));
        var fingerprint = new IdentityConfigurationFingerprint(provider, new FakeModelStore(ModelsSnapshot(DetectorHash, AntiSpoofingHash, RecognitionHash)));

        Assert.Null(fingerprint.Current());
    }

    [Fact]
    public void IdentityConfigurationFingerprint_ChangesWithThePolicyHash_AndWithEveryWeightHash()
    {
        var store = new FakeModelStore(ModelsSnapshot(DetectorHash, AntiSpoofingHash, RecognitionHash));
        var original = new IdentityConfigurationFingerprint(ProviderFor(IdentityPolicyTestFiles.ShippedText()), store).Current()!;
        var other = "00" + DetectorHash[2..];

        // Another policy (a new calibration is a new file): same models, another policy hash.
        var anotherPolicy = new IdentityConfigurationFingerprint(ProviderFor(IdentityPolicyTestFiles.Edited(("version", "\"identity-policy-2026.12-1\""), ("provisional", "false")), fileName: "calibrated.json"), store).Current()!;

        // Another weight in each of the three roles, and two weights swapped (the role order is part of the recipe).
        var detectorChanged = Current(ModelsSnapshot(other, AntiSpoofingHash, RecognitionHash));
        var antiSpoofingChanged = Current(ModelsSnapshot(DetectorHash, other, RecognitionHash));
        var recognitionChanged = Current(ModelsSnapshot(DetectorHash, AntiSpoofingHash, other));
        var swapped = Current(ModelsSnapshot(AntiSpoofingHash, DetectorHash, RecognitionHash));

        var all = new[] { original, anotherPolicy, detectorChanged, antiSpoofingChanged, recognitionChanged, swapped };
        Assert.Equal(all.Length, all.Select(value => value.Fingerprint).Distinct().Count());
        Assert.NotEqual(original.PolicyHash, anotherPolicy.PolicyHash);
        Assert.Equal(original.WeightsHash, anotherPolicy.WeightsHash);
        Assert.All(new[] { detectorChanged, antiSpoofingChanged, recognitionChanged, swapped }, value => Assert.Equal(original.PolicyHash, value.PolicyHash));
        Assert.All(new[] { detectorChanged, antiSpoofingChanged, recognitionChanged, swapped }, value => Assert.NotEqual(original.WeightsHash, value.WeightsHash));

        // The same models and the same policy: the same value, whatever the case the store spells the hashes in.
        var again = Current(ModelsSnapshot(DetectorHash.ToLowerInvariant(), AntiSpoofingHash.ToLowerInvariant(), RecognitionHash.ToLowerInvariant()));
        Assert.Equal(original, again);

        IdentityFingerprint Current(BiometricModelsSnapshot snapshot)
        {
            return new IdentityConfigurationFingerprint(ProviderFor(IdentityPolicyTestFiles.ShippedText(), fileName: "again.json"), new FakeModelStore(snapshot)).Current()!;
        }
    }

    private static void AssertUnavailable(IdentityPolicySnapshot snapshot, string reasonCode)
    {
        Assert.Equal(IdentityPolicyState.Unavailable, snapshot.State);
        Assert.False(snapshot.IsReady);
        Assert.Equal(reasonCode, snapshot.ReasonCode);
        Assert.Null(snapshot.Policy);
        Assert.Null(snapshot.Hash);
        Assert.True(snapshot.Provisional);
    }

    private static BiometricModelInfo Model(string role, string? weightHash, bool loaded)
    {
        return new BiometricModelInfo(role, "model", "1", "license", "provenance", weightHash, loaded, loaded ? null : "hash_mismatch");
    }

    private static BiometricModelsSnapshot ModelsSnapshot(
        string detector,
        string antiSpoofing,
        string recognition,
        BiometricModelsState state = BiometricModelsState.Ready,
        string? notLoadedRole = null)
    {
        return new BiometricModelsSnapshot(
            state,
            null,
            [
                Model(BiometricModelRoles.FaceDetector, detector, loaded: notLoadedRole != BiometricModelRoles.FaceDetector),
                Model(BiometricModelRoles.AntiSpoofing, antiSpoofing, loaded: notLoadedRole != BiometricModelRoles.AntiSpoofing),
                Model(BiometricModelRoles.FaceRecognition, recognition, loaded: notLoadedRole != BiometricModelRoles.FaceRecognition),
            ]);
    }

    /// <summary>A policy provider over <paramref name="text"/> written to a file of the temporary folder, pinned by its canonical hash unless a pin is given.</summary>
    private IdentityPolicyProvider ProviderFor(string text, string? pin = null, string fileName = PolicyFile)
    {
        File.WriteAllText(Path.Combine(_directory, fileName), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var hash = pin ?? IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(text)) ?? new string('0', 64);
        return Create(fileName, hash);
    }

    private IdentityPolicyProvider Create(string policyFile, string pin, ILogger<IdentityPolicyProvider>? logger = null)
    {
        var options = Options.Create(new IdentityVerificationOptions { PolicyFile = policyFile, PolicySha256 = pin });
        return new IdentityPolicyProvider(options, logger ?? new CapturingLogger<IdentityPolicyProvider>(), _directory);
    }

    private sealed class FakeModelStore(BiometricModelsSnapshot snapshot) : IBiometricModelStore
    {
        public BiometricModelsSnapshot Snapshot { get; } = snapshot;

        public bool IsAvailable => Snapshot.IsReady;

        public LoadedBiometricModel? FaceDetector => null;

        public LoadedBiometricModel? AntiSpoofing => null;

        public LoadedBiometricModel? FaceRecognition => null;
    }
}
