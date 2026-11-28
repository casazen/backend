using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

public class IdentityContractsTests
{
    private static readonly JsonSerializerOptions FlowJson = (JsonSerializerOptions)typeof(IdentityVerificationFlowService)
        .GetField("Json", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null)!;

    [Fact]
    public void Codes_AreSpelledAsThePlanFixesThem()
    {
        Assert.Equal(["automatic", "assisted"], IdentityChoices.All);
        Assert.Equal(
            ["auto_approved", "review", "assisted", "consent_refused", "format_unsupported", "technical_error", "unavailable", "minor_excluded", "abandoned"],
            IdentityOutcomeCodes.All);
        Assert.Equal(
            ["web:ios-safari", "web:android-chrome", "web:desktop-chromium", "web:desktop-safari", "web:desktop-firefox", "web:other"],
            IdentityChannels.All);
        Assert.Equal(["turn_left", "turn_right", "look_up", "look_down"], PresenceActions.All);
        Assert.Equal(
            [
                "document_not_found", "document_edges_not_visible", "document_layout_mismatch", "document_orientation", "document_portrait_missing",
                "document_side_duplicate", "document_wrong_side", "document_format_unsupported", "document_glare", "document_blur", "document_field_conflict",
                "document_expired", "document_unreadable", "presence_too_short", "presence_timing", "presence_quality", "presence_single_face",
                "presence_challenge_not_followed", "presence_continuity",
            ],
            IdentityGuidanceCodes.All);
        Assert.Equal(
            [
                "identity_already_closed", "notice_version_unknown", "presence_challenge_unavailable", "presence_challenge_required", "presence_challenge_expired",
                "presence_sequence_required", "presence_sequence_invalid", "presence_sequence_too_large", "idempotency_key_required",
                "identity_segment_not_qualified", "identity_segment_invalid", "identity_evidence_not_found",
            ],
            IdentityApiErrors.All);
        Assert.Equal(["consistency", "cryptographic"], IdentityAssuranceLevels.All);
        Assert.Equal(["decided", "expired", "abandoned", "erased", "replaced"], IdentityEvidenceDeletionReasons.All);
        Assert.Equal("Idempotency-Key", IdentityHeaders.IdempotencyKey);
        Assert.Equal("^[A-Za-z0-9-]{8,64}$", IdentityHeaders.IdempotencyKeyPattern);
        Assert.Equal("presence", IdentityVerificationSteps.Presence);
    }

    [Fact]
    public void Codes_EveryConstantIsInTheAllListOfItsClass()
    {
        (Type Type, IReadOnlyList<string> All)[] classes =
        [
            (typeof(IdentityChoices), IdentityChoices.All),
            (typeof(IdentityOutcomeCodes), IdentityOutcomeCodes.All),
            (typeof(IdentityChannels), IdentityChannels.All),
            (typeof(PresenceActions), PresenceActions.All),
            (typeof(IdentityGuidanceCodes), IdentityGuidanceCodes.All),
            (typeof(IdentityApiErrors), IdentityApiErrors.All),
            (typeof(IdentityAssuranceLevels), IdentityAssuranceLevels.All),
            (typeof(IdentityEvidenceDeletionReasons), IdentityEvidenceDeletionReasons.All),
        ];

        Assert.All(classes, entry =>
        {
            Assert.Equal(StringConstants(entry.Type).Order(), entry.All.Order());
            Assert.Equal(entry.All.Count, entry.All.Distinct().Count());
        });
    }

    [Theory]
    [InlineData("0b4a3c1e-9f2d-4d8e-a1b2-3c4d5e6f7a8b", true)]
    [InlineData("abcdefgh", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-a", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-ab", false)]
    [InlineData("abcdefg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("with space", false)]
    [InlineData("under_score", false)]
    [InlineData("accentè123", false)]
    public void IsValidIdempotencyKey_AgreesWithThePatternOfTheHeader(string? key, bool valid)
    {
        Assert.Equal(valid, IdentityHeaders.IsValidIdempotencyKey(key));
        Assert.Equal(valid, key is not null && Regex.IsMatch(key, IdentityHeaders.IdempotencyKeyPattern, RegexOptions.None, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Enums_KeepTheirNumbers_AndBiometricOutcomeIsUnchanged()
    {
        Assert.Equal([(0, "Legacy"), (1, "Observation"), (2, "Automatic")], Values<IdentityDecisionMode>());
        Assert.Equal([(0, "Observation"), (1, "Automatic"), (2, "Suspended")], Values<IdentitySegmentMode>());
        Assert.Equal([(0, "DocumentPortrait"), (1, "Frame")], Values<IdentityEvidenceKind>());

        Assert.Equal([(0, "Accept"), (1, "Escalate"), (2, "Reject")], Values<BiometricOutcome>());
    }

    [Fact]
    public void VerificationReasons_Checks_HoldActivePoseAndTheElevenChecksOfTheSelfHostedFlow()
    {
        Assert.Equal(
            new[]
            {
                "quality", "liveness", "document_validity", "mrz_consistency", "similarity", "processing", "active_pose",
                "presence_challenge", "presence_timing", "presence_continuity", "presence_single_face", "presence_quality", "document_layout",
                "document_side", "document_coherence", "document_format", "assurance_level", "observation_mode",
            }.Order(),
            StringConstants(typeof(VerificationReasons.Checks)).Order());
    }

    [Fact]
    public void StoredDocumentCheck_WrittenBeforeThePresenceChallenge_StillReads_WithoutOne()
    {
        const string before = """
            {"check":{"mrzRead":true,"checkDigitsValid":true,"expired":false,"namesConsistent":true,"birthDateConsistent":true,
            "documentNumberConsistent":null,"nationalityConsistent":true,"holderIsMinor":false,"valid":true,"consistent":true},
            "photoSha256":"5F1A","checkedAt":"2026-11-20T10:00:00Z","backSha256":"9C0D",
            "front":{"face":true,"qualityOk":true,"mrz":null,"numberProof":null},
            "back":{"face":null,"qualityOk":true,"mrz":{"mrzRead":true,"checkDigitsValid":true,"expired":false,"namesConsistent":true,
            "birthDateConsistent":true,"documentNumberConsistent":null,"nationalityConsistent":true,"holderIsMinor":false,"valid":true,"consistent":true},
            "numberProof":"3E7B"},"twoSided":true,"verifyingGuestPosition":1,"proofKey":"4A2F",
            "pose":{"id":"7d1f5c44-2b8e-4f61-9a3d-0c6e5b2a1f90","noseToRight":true,"expiresAt":"2026-11-20T10:02:00+00:00","attempt":1}}
            """;

        var read = JsonSerializer.Deserialize<StoredDocumentCheck>(before, FlowJson);

        Assert.NotNull(read);
        Assert.Null(read.Presence);
        Assert.True(read.TwoSided);
        Assert.Equal(1, read.VerifyingGuestPosition);
        Assert.Equal("3E7B", read.Back?.NumberProof);
        Assert.Equal(1, read.Pose?.Attempt);
        Assert.True(read.Check.Valid);
    }

    [Fact]
    public void StoredDocumentCheck_WithAPresenceChallenge_KeepsCodesAndTimesOnly_AndReadsItBack()
    {
        var issued = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
        var challenge = new StoredPresenceChallenge(
            Guid.NewGuid(), [PresenceActions.TurnLeft, PresenceActions.LookUp, PresenceActions.TurnRight], issued, issued.AddSeconds(45), 2);
        var check = new StoredDocumentCheck(
            new DocumentCheck(true, true, false, true, true, null, null, false), "5F1A", issued.UtcDateTime, Presence: challenge);

        var json = JsonSerializer.Serialize(check, FlowJson);
        var read = JsonSerializer.Deserialize<StoredDocumentCheck>(json, FlowJson)!;

        Assert.Equal(challenge.Id, read.Presence?.Id);
        Assert.Equal(challenge.Steps, read.Presence?.Steps);
        Assert.Equal((challenge.IssuedAt, challenge.ExpiresAt, 2), (read.Presence!.IssuedAt, read.Presence.ExpiresAt, read.Presence.Attempt));
        using var stored = JsonDocument.Parse(json);
        Assert.Equal(
            ["attempt", "expiresAt", "id", "issuedAt", "steps"],
            stored.RootElement.GetProperty("presence").EnumerateObject().Select(member => member.Name).Order());
    }

    [Fact]
    public void IdentityVerificationState_OfTheLegacyPath_LeavesTheSelfHostedMembersAtTheirDefaults_AndThePageReadsThem()
    {
        var state = new IdentityVerificationState(IdentityVerificationSteps.Document, null, true, true, false, false, 3, false, false, 0);

        var dto = PublicIdentityVerificationStateDto.From(state);

        Assert.Equal(
            (false, false, (string?)null, (string?)null, (string?)null, false, false, (string?)null),
            (state.SelfHosted, state.Opened, state.NoticeVersion, state.Choice, state.FormatId, state.GuidedRetryAvailable, state.Closed, state.Outcome));
        Assert.Equal(
            (false, false, (string?)null, (string?)null, (string?)null, false, false, (string?)null),
            (dto.SelfHosted, dto.Opened, dto.NoticeVersion, dto.Choice, dto.FormatId, dto.GuidedRetryAvailable, dto.Closed, dto.Outcome));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        foreach (var member in new[] { "selfHosted", "opened", "noticeVersion", "choice", "formatId", "guidedRetryAvailable", "closed", "outcome" })
            Assert.True(json.RootElement.TryGetProperty(member, out _), $"{member} is missing from the state of the page");

        var selfHosted = PublicIdentityVerificationStateDto.From(state with
        {
            Step = IdentityVerificationSteps.Presence,
            SelfHosted = true,
            Opened = true,
            NoticeVersion = "checkin-identity-2026-11",
            Choice = IdentityChoices.Automatic,
            FormatId = "it-cie-3",
            GuidedRetryAvailable = true,
        });
        Assert.Equal(
            ("presence", true, true, "checkin-identity-2026-11", "automatic", "it-cie-3", true),
            (selfHosted.Step, selfHosted.SelfHosted, selfHosted.Opened, selfHosted.NoticeVersion, selfHosted.Choice, selfHosted.FormatId,
                selfHosted.GuidedRetryAvailable));
    }

    [Fact]
    public void RateLimits_TheTwoIdentityPolicies_ArePerClientIpAndToken()
    {
        var identity = Assert.Single(RateLimitingServiceCollectionExtensions.Policies, policy => policy.Name == RateLimitPolicies.GuestIdentity);
        var upload = Assert.Single(RateLimitingServiceCollectionExtensions.Policies, policy => policy.Name == RateLimitPolicies.GuestIdentityUpload);

        Assert.Equal(("GuestIdentity", 30, TimeSpan.FromMinutes(1), true, false, "token"),
            (identity.Name, identity.DefaultPermitLimit, identity.DefaultWindow, identity.PartitionByToken, identity.PartitionByUser, identity.TokenRouteValue));
        Assert.Equal(("GuestIdentityUpload", 6, TimeSpan.FromMinutes(1), true, false, "token"),
            (upload.Name, upload.DefaultPermitLimit, upload.DefaultWindow, upload.PartitionByToken, upload.PartitionByUser, upload.TokenRouteValue));
    }

    private static IEnumerable<string> StringConstants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static List<(int Number, string Name)> Values<TEnum>()
        where TEnum : struct, Enum
    {
        return Enum.GetValues<TEnum>().Select(value => (Convert.ToInt32(value, CultureInfo.InvariantCulture), value.ToString())).ToList();
    }
}
