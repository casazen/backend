using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E70/F02/US01/T02 (NF-194, NF-191, ADR-013-R19, D4): the segments of the automation. Observation is the behaviour everywhere; the automatic approval is allowed only in a
/// segment in automatic mode, qualified for the policy version, the policy hash and the weights hash in force, under a calibrated policy, with the format giving the assurance
/// required on the channel and the platform permission on, and each failing condition has its own reason; a new policy or new weights take the segment back to observation
/// without a write; automatic needs a qualification, a suspension needs a reason; every change leaves an <c>OrgSettingsChanged</c> line; another org sees nothing.
/// </summary>
public sealed class IdentitySegmentServiceTests : IAsyncLifetime
{
    private const string Cie = "it-cie-3.0";
    private const string DetectorHash = "0ba9fbfa0ba9fbfa0ba9fbfa0ba9fbfa0ba9fbfa0ba9fbfa0ba9fbfa0ba9fbfa";
    private const string AntiSpoofingHash = "1c2d3e4f1c2d3e4f1c2d3e4f1c2d3e4f1c2d3e4f1c2d3e4f1c2d3e4f1c2d3e4f";
    private const string RecognitionHash = "2e3f4a5b2e3f4a5b2e3f4a5b2e3f4a5b2e3f4a5b2e3f4a5b2e3f4a5b2e3f4a5b";
    private const string ReportHash = "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD";
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"identity-segments-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly List<string> _logs = [];
    private readonly TestFeatureFlags _flags = new(
        FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityAutoApproval);

    private readonly DocumentFormatRegistry _registry =
        new(Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, System.AppContext.BaseDirectory);

    private readonly Models _models = new(DetectorHash, AntiSpoofingHash, RecognitionHash);
    private AppDbContext _db = null!;
    private IdentityPolicyProvider _calibrated = null!;
    private Guid _org;
    private Guid _other;
    private Guid _flat;
    private Guid _villa;
    private Guid _otherFlat;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _calibrated = Policy(Calibrated("identity-policy-2026.12-1"), "calibrated.json");

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg("Agenzia", $"agenzia-{suffix}");
        var other = HostScopeScenario.NewOrg("Altra agenzia", $"altra-{suffix}");
        _db.Orgs.AddRange(org, other);
        var flat = HostScopeScenario.NewProperty(org.Id, "auth0|owner", "Bilocale Navigli");
        var villa = HostScopeScenario.NewProperty(org.Id, "auth0|owner", "Villa Brera");
        var otherFlat = HostScopeScenario.NewProperty(other.Id, "auth0|other", "Trilocale Altrove");
        _db.Properties.AddRange(flat, villa, otherFlat);
        await _db.SaveChangesAsync(Ct);
        (_org, _other, _flat, _villa, _otherFlat) = (org.Id, other.Id, flat.Id, villa.Id, otherFlat.Id);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // ─── The resolution ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>AC1: with no segment row the resolution is observation, never automatic, whatever the flags say.</summary>
    [Fact]
    public async Task IdentitySegment_NoRow_IsObservation()
    {
        var resolution = await Service().ResolveAsync(_org, _flat, Cie, IdentityChannels.IosSafari, Ct);

        Assert.Null(resolution.SegmentId);
        Assert.Equal(IdentitySegmentMode.Observation, resolution.Mode);
        Assert.False(resolution.Qualified);
        Assert.False(resolution.AutoApprovalAllowed);
        Assert.Equal(IdentityAssuranceLevels.Consistency, resolution.RequiredAssurance);
        Assert.Equal(IdentitySegmentReasonCodes.SegmentObservation, resolution.ReasonCode);

        // A format or a channel the request does not name resolves to nothing either.
        Assert.False((await Service().ResolveAsync(_org, _flat, null, IdentityChannels.IosSafari, Ct)).AutoApprovalAllowed);
        Assert.False((await Service().ResolveAsync(_org, _flat, Cie, " ", Ct)).AutoApprovalAllowed);
    }

    /// <summary>AC2, the positive control: every condition holds, so the automatic approval is allowed and no reason is given.</summary>
    [Fact]
    public async Task IdentitySegment_QualifiedAutomaticSegment_AllowsAutoApproval_WhenEveryConditionHolds()
    {
        var service = Service();
        var segment = await QualifiedAutomaticAsync(service);

        var resolution = await service.ResolveAsync(_org, _flat, Cie, IdentityChannels.AndroidChrome, Ct);

        Assert.Equal(segment, resolution.SegmentId);
        Assert.Equal(IdentitySegmentMode.Automatic, resolution.Mode);
        Assert.True(resolution.Qualified);
        Assert.True(resolution.AutoApprovalAllowed);
        Assert.Null(resolution.ReasonCode);
    }

    /// <summary>
    /// Proof (security): never automatic with the global permission off. The same qualified automatic segment allows nothing as soon as
    /// <c>CheckInIdentityAutoApproval</c>, or the self-hosted flow it works with, is off; the row is not touched, and the permission back on allows it again.
    /// </summary>
    [Fact]
    public async Task IdentitySegment_GlobalFlagOff_NeverAllowsAutoApproval()
    {
        var segment = await QualifiedAutomaticAsync(Service());
        var before = await RowAsync(segment);

        foreach (var off in new[] { FeatureFlags.CheckInIdentityAutoApproval, FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityVerification })
        {
            _flags.Set(off, false);
            var resolution = await Service().ResolveAsync(_org, _flat, Cie, IdentityChannels.AndroidChrome, Ct);
            Assert.False(resolution.AutoApprovalAllowed);
            Assert.Equal(IdentitySegmentReasonCodes.AutoApprovalDisabled, resolution.ReasonCode);
            Assert.False((await Service().ListAsync(_org, Ct)).AutoApprovalEnabled);
            _flags.Set(off, true);
        }

        Assert.Equivalent(before, await RowAsync(segment));
        Assert.True((await Service().ResolveAsync(_org, _flat, Cie, IdentityChannels.AndroidChrome, Ct)).AutoApprovalAllowed);
    }

    /// <summary>AC2: each condition that fails yields its own reason, in the order of the checks.</summary>
    [Theory]
    [InlineData(IdentitySegmentReasonCodes.SegmentObservation)]
    [InlineData(IdentitySegmentReasonCodes.SegmentSuspended)]
    [InlineData(IdentitySegmentReasonCodes.SegmentNotQualified)]
    [InlineData(IdentitySegmentReasonCodes.QualificationMismatch)]
    [InlineData(IdentitySegmentReasonCodes.PolicyProvisional)]
    [InlineData(IdentitySegmentReasonCodes.PolicyUnavailable)]
    [InlineData(IdentitySegmentReasonCodes.AssuranceRequiresChip)]
    public async Task IdentitySegment_EachFailingCondition_IsItsReason(string expected)
    {
        var resolver = Service();
        switch (expected)
        {
            case IdentitySegmentReasonCodes.SegmentObservation:
                var observed = await Service().CreateAsync(_org, null, Cie, IdentityChannels.AndroidChrome, "auth0|admin", Ct);
                await QualifyAsync(Service(), observed.Id);
                break;
            case IdentitySegmentReasonCodes.SegmentSuspended:
                var suspended = await QualifiedAutomaticAsync(Service());
                await Service().SetModeAsync(_org, suspended, IdentitySegmentMode.Suspended, "Anomalia nel pilota", "auth0|admin", Ct);
                break;
            case IdentitySegmentReasonCodes.SegmentNotQualified:
                await AddRowAsync(IdentitySegmentMode.Automatic, qualifiedFor: null);
                break;
            case IdentitySegmentReasonCodes.QualificationMismatch:
                await QualifiedAutomaticAsync(Service());
                resolver = Service(Policy(Calibrated("identity-policy-2026.12-2"), "recalibrated.json"));
                break;
            case IdentitySegmentReasonCodes.PolicyProvisional:
                // A row qualified for the provisional policy (the service never writes one: it is planted) still approves nothing.
                var provisional = Policy(IdentityPolicyTestFiles.ShippedText(), "provisional.json");
                await AddRowAsync(IdentitySegmentMode.Automatic, qualifiedFor: new IdentityConfigurationFingerprint(provisional, _models).Current());
                resolver = Service(provisional);
                break;
            case IdentitySegmentReasonCodes.PolicyUnavailable:
                await QualifiedAutomaticAsync(Service());
                resolver = Service(MissingPolicy());
                break;
            case IdentitySegmentReasonCodes.AssuranceRequiresChip:
                await QualifiedAutomaticAsync(Service(), requiredAssurance: IdentityAssuranceLevels.Cryptographic);
                break;
        }

        var resolution = await resolver.ResolveAsync(_org, _flat, Cie, IdentityChannels.AndroidChrome, Ct);

        Assert.False(resolution.AutoApprovalAllowed);
        Assert.Equal(expected, resolution.ReasonCode);
        Assert.NotNull(resolution.SegmentId);
    }

    /// <summary>
    /// Proof (security, AC3): the qualification is bound to the fingerprint. A qualified automatic segment allows nothing once a weight of any of the three models, the
    /// policy hash or the loading of the models changes (<c>qualification_mismatch</c>), without any write to its row; with the qualified configuration back, it allows
    /// again: the fallback to observation is by construction.
    /// </summary>
    [Fact]
    public async Task IdentitySegment_WeightsChanged_FallsBackToObservation()
    {
        var segment = await QualifiedAutomaticAsync(Service());
        var before = await RowAsync(segment);
        var changed = "ff" + DetectorHash[2..];

        IdentitySegmentService[] elsewhere =
        [
            Service(models: new Models(changed, AntiSpoofingHash, RecognitionHash)),
            Service(models: new Models(DetectorHash, changed, RecognitionHash)),
            Service(models: new Models(DetectorHash, AntiSpoofingHash, changed)),
            Service(models: new Models(DetectorHash, AntiSpoofingHash, RecognitionHash, loaded: false)),
            Service(Policy(Calibrated("identity-policy-2026.12-2"), "recalibrated.json")),
        ];
        foreach (var service in elsewhere)
        {
            var resolution = await service.ResolveAsync(_org, _flat, Cie, IdentityChannels.AndroidChrome, Ct);
            Assert.False(resolution.AutoApprovalAllowed);
            Assert.False(resolution.Qualified);
            Assert.Equal(IdentitySegmentReasonCodes.QualificationMismatch, resolution.ReasonCode);
            var listed = Assert.Single((await service.ListAsync(_org, Ct)).Segments);
            Assert.Equal(IdentitySegmentReasonCodes.QualificationMismatch, listed.QualificationReason);
            Assert.False(listed.AutoApprovalAllowed);
        }

        Assert.Equivalent(before, await RowAsync(segment));
        Assert.True((await Service().ResolveAsync(_org, _flat, Cie, IdentityChannels.AndroidChrome, Ct)).AutoApprovalAllowed);
    }

    [Fact]
    public async Task IdentitySegment_TheSegmentOfTheProperty_ComesBeforeTheOneOfTheOrg()
    {
        var orgWide = await QualifiedAutomaticAsync(Service());
        var ofTheVilla = await Service().CreateAsync(_org, _villa, Cie, IdentityChannels.AndroidChrome, "auth0|admin", Ct);

        var villa = await Service().ResolveAsync(_org, _villa, Cie, IdentityChannels.AndroidChrome, Ct);
        var flat = await Service().ResolveAsync(_org, _flat, Cie, IdentityChannels.AndroidChrome, Ct);
        var otherChannel = await Service().ResolveAsync(_org, _flat, Cie, IdentityChannels.IosSafari, Ct);

        Assert.Equal((ofTheVilla.Id, IdentitySegmentReasonCodes.SegmentObservation), (villa.SegmentId!.Value, villa.ReasonCode));
        Assert.Equal(orgWide, flat.SegmentId);
        Assert.True(flat.AutoApprovalAllowed);
        Assert.Null(otherChannel.SegmentId);
        Assert.False(otherChannel.AutoApprovalAllowed);
    }

    // ─── The changes ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>AC4: automatic on an unqualified segment is refused with identity_segment_not_qualified; a suspension always works and needs a reason, kept as given.</summary>
    [Fact]
    public async Task IdentitySegment_AutomaticOnAnUnqualifiedSegment_IsRefused_AndSuspensionAlwaysWorks()
    {
        var segment = (await Service().CreateAsync(_org, _flat, Cie, IdentityChannels.DesktopChromium, "auth0|admin", Ct)).Id;

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service().SetModeAsync(_org, segment, IdentitySegmentMode.Automatic, null, "auth0|admin", Ct));
        Assert.Equal(IdentityApiErrors.IdentitySegmentNotQualified, refused.Code);
        Assert.Equal(IdentitySegmentMode.Observation, (await RowAsync(segment)).Mode);

        // Qualified for another policy: still not qualified for the one in force.
        await QualifyAsync(Service(Policy(Calibrated("identity-policy-2026.12-2"), "recalibrated.json")), segment);
        Assert.Equal(
            IdentityApiErrors.IdentitySegmentNotQualified,
            (await Assert.ThrowsAsync<DomainRuleException>(() => Service().SetModeAsync(_org, segment, IdentitySegmentMode.Automatic, null, "auth0|admin", Ct))).Code);

        // A suspension needs a reason of 1 to 200 characters, and works from observation and from automatic.
        foreach (var reason in new[] { null, "", "   ", new string('x', IdentitySegmentRules.SuspendedReasonMaxLength + 1) })
        {
            var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => Service().SetModeAsync(_org, segment, IdentitySegmentMode.Suspended, reason, "auth0|admin", Ct));
            Assert.Equal(IdentityApiErrors.IdentitySegmentInvalid, invalid.Code);
            Assert.Equal("IdentitySegmentReasonRequired", invalid.MessageKey);
        }

        var fromObservation = await Service().SetModeAsync(_org, segment, IdentitySegmentMode.Suspended, "  Regressione sul lotto di novembre ", "auth0|admin", Ct);
        Assert.Equal((IdentitySegmentMode.Suspended, "Regressione sul lotto di novembre"), (fromObservation!.Mode, fromObservation.SuspendedReason));

        var automatic = await QualifiedAutomaticAsync(Service(), channel: IdentityChannels.IosSafari);
        var fromAutomatic = await Service().SetModeAsync(_org, automatic, IdentitySegmentMode.Suspended, new string('r', IdentitySegmentRules.SuspendedReasonMaxLength), "auth0|admin", Ct);
        Assert.Equal(IdentitySegmentMode.Suspended, fromAutomatic!.Mode);
        Assert.False((await Service().ResolveAsync(_org, _flat, Cie, IdentityChannels.IosSafari, Ct)).AutoApprovalAllowed);

        // Back to observation: the reason goes with the suspension.
        var observed = await Service().SetModeAsync(_org, automatic, IdentitySegmentMode.Observation, "ignored", "auth0|admin", Ct);
        Assert.Null(observed!.SuspendedReason);
    }

    [Fact]
    public async Task IdentitySegment_Create_WantsAFormatOfTheRegistryAChannelOfTheListAndAPropertyOfTheOrg_AndIsIdempotent()
    {
        var deleted = HostScopeScenario.NewProperty(_org, "auth0|owner", "Casa chiusa");
        deleted.IsDeleted = true;
        _db.Properties.Add(deleted);
        await _db.SaveChangesAsync(Ct);

        var invalid = new (Guid? Property, string? Format, string? Channel)[]
        {
            (null, "it-passport-2006", IdentityChannels.IosSafari),
            (null, null, IdentityChannels.IosSafari),
            (null, Cie, "web:netscape"),
            (null, Cie, null),
            (_otherFlat, Cie, IdentityChannels.IosSafari),
            (deleted.Id, Cie, IdentityChannels.IosSafari),
            (Guid.NewGuid(), Cie, IdentityChannels.IosSafari),
        };
        foreach (var (property, format, channel) in invalid)
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service().CreateAsync(_org, property, format, channel, "auth0|admin", Ct));
            Assert.Equal(IdentityApiErrors.IdentitySegmentInvalid, refused.Code);
        }

        Assert.Empty(await _db.IdentitySegmentSettings.ToListAsync(Ct));
        Assert.Empty(await _db.OrgActivityEntries.ToListAsync(Ct));

        var created = await Service().CreateAsync(_org, _flat, $" {Cie} ", $" {IdentityChannels.IosSafari} ", "auth0|admin", Ct);
        var again = await Service().CreateAsync(_org, _flat, Cie, IdentityChannels.IosSafari, "auth0|other-admin", Ct);

        Assert.Equal(created.Id, again.Id);
        Assert.Equal((Cie, IdentityChannels.IosSafari, IdentitySegmentMode.Observation, "Bilocale Navigli"), (created.FormatId, created.Channel, created.Mode, created.PropertyName));
        Assert.Equal(IdentityAssuranceLevels.Consistency, created.RequiredAssurance);
        Assert.Single(await _db.IdentitySegmentSettings.ToListAsync(Ct));
        Assert.Single(await _db.OrgActivityEntries.ToListAsync(Ct));
    }

    /// <summary>Only the fingerprint in force is qualified: another version or hash, a provisional or unusable policy, a malformed report or assurance are refused.</summary>
    [Fact]
    public async Task IdentitySegment_Qualify_RecordsOnlyTheFingerprintInForce()
    {
        var segment = (await Service().CreateAsync(_org, null, Cie, IdentityChannels.AndroidChrome, "auth0|admin", Ct)).Id;
        var current = Fingerprint();

        var refusals = new (IdentitySegmentService Service, string? Version, string? PolicyHash, string? WeightsHash, string? Report, string? Assurance, string MessageKey)[]
        {
            (Service(), "identity-policy-2026.12-0", current.PolicyHash, current.WeightsHash, ReportHash, IdentityAssuranceLevels.Consistency, "IdentitySegmentQualificationMismatch"),
            (Service(), current.PolicyVersion, Flip(current.PolicyHash), current.WeightsHash, ReportHash, IdentityAssuranceLevels.Consistency, "IdentitySegmentQualificationMismatch"),
            (Service(), current.PolicyVersion, current.PolicyHash, Flip(current.WeightsHash), ReportHash, IdentityAssuranceLevels.Consistency, "IdentitySegmentQualificationMismatch"),
            (Service(), current.PolicyVersion, current.PolicyHash, current.WeightsHash, "not-a-hash", IdentityAssuranceLevels.Consistency, "IdentitySegmentQualificationInvalid"),
            (Service(), current.PolicyVersion, current.PolicyHash, current.WeightsHash, ReportHash, "chip", "IdentitySegmentQualificationInvalid"),
            (Service(), null, current.PolicyHash, current.WeightsHash, ReportHash, IdentityAssuranceLevels.Consistency, "IdentitySegmentQualificationInvalid"),
            (Service(MissingPolicy()), current.PolicyVersion, current.PolicyHash, current.WeightsHash, ReportHash, IdentityAssuranceLevels.Consistency, "IdentitySegmentFingerprintUnavailable"),
            (Service(models: new Models(DetectorHash, AntiSpoofingHash, RecognitionHash, loaded: false)), current.PolicyVersion, current.PolicyHash, current.WeightsHash, ReportHash, IdentityAssuranceLevels.Consistency, "IdentitySegmentFingerprintUnavailable"),
        };
        foreach (var (service, version, policyHash, weightsHash, report, assurance, messageKey) in refusals)
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.QualifyAsync(_org, segment, version, policyHash, weightsHash, report, assurance, "auth0|staff", Ct));
            Assert.Equal((IdentityApiErrors.IdentitySegmentInvalid, messageKey), (refused.Code, refused.MessageKey));
        }

        // The provisional policy in force: never qualified, even with its own fingerprint.
        var provisional = Policy(IdentityPolicyTestFiles.ShippedText(), "provisional.json");
        var ofTheProvisional = new IdentityConfigurationFingerprint(provisional, _models).Current()!;
        var notCalibrated = await Assert.ThrowsAsync<DomainRuleException>(() => Service(provisional).QualifyAsync(
            _org, segment, ofTheProvisional.PolicyVersion, ofTheProvisional.PolicyHash, ofTheProvisional.WeightsHash, ReportHash, IdentityAssuranceLevels.Consistency, "auth0|staff", Ct));
        Assert.Equal("IdentitySegmentPolicyProvisional", notCalibrated.MessageKey);
        Assert.Null((await RowAsync(segment)).QualifiedAt);

        // The fingerprint in force (the hashes in any case): recorded as the platform computes it, the mode unchanged.
        var qualified = await Service().QualifyAsync(
            _org, segment, current.PolicyVersion, current.PolicyHash.ToUpperInvariant(), current.WeightsHash, ReportHash, IdentityAssuranceLevels.Consistency, "auth0|staff", Ct);
        var row = await RowAsync(segment);
        Assert.True(qualified!.Qualified);
        Assert.Equal(IdentitySegmentMode.Observation, qualified.Mode);
        Assert.Equal(
            (current.PolicyVersion, current.PolicyHash, current.WeightsHash, ReportHash.ToLowerInvariant(), Now.UtcDateTime, "auth0|staff"),
            (row.QualifiedPolicyVersion, row.QualifiedPolicyHash, row.QualifiedWeightsHash, row.QualificationReportSha256, row.QualifiedAt, row.QualifiedByUserId));
    }

    [Fact]
    public async Task IdentitySegment_RevokingTheQualification_TakesAnAutomaticSegmentBackToObservation()
    {
        var segment = await QualifiedAutomaticAsync(Service());

        var revoked = await Service().RevokeQualificationAsync(_org, segment, "auth0|staff", Ct);

        Assert.Equal(IdentitySegmentMode.Observation, revoked!.Mode);
        Assert.False(revoked.Qualified);
        Assert.Equal(IdentitySegmentReasonCodes.SegmentNotQualified, revoked.QualificationReason);
        var row = await RowAsync(segment);
        Assert.Null(row.QualifiedPolicyVersion);
        Assert.Null(row.QualifiedPolicyHash);
        Assert.Null(row.QualifiedWeightsHash);
        Assert.Null(row.QualificationReportSha256);
        Assert.Null(row.QualifiedAt);
        Assert.False((await Service().ResolveAsync(_org, _flat, Cie, IdentityChannels.AndroidChrome, Ct)).AutoApprovalAllowed);
    }

    /// <summary>AC6: every change records OrgSettingsChanged in the activity log (the fact and who, never a value); a change that changes nothing records nothing.</summary>
    [Fact]
    public async Task IdentitySegment_EveryChange_RecordsOrgSettingsChanged()
    {
        var segment = (await Service().CreateAsync(_org, _flat, Cie, IdentityChannels.AndroidChrome, "auth0|admin", Ct)).Id;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await QualifyAsync(Service(), segment);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().SetModeAsync(_org, segment, IdentitySegmentMode.Automatic, null, "auth0|admin", Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().SetModeAsync(_org, segment, IdentitySegmentMode.Automatic, null, "auth0|admin", Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RevokeQualificationAsync(_org, segment, "auth0|staff", Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RevokeQualificationAsync(_org, segment, "auth0|staff", Ct);

        var lines = await _db.OrgActivityEntries.OrderBy(entry => entry.When).ToListAsync(Ct);
        Assert.Equal(4, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Equal(OrgActivityType.OrgSettingsChanged, line.Type);
            Assert.Equal(_org, line.OrgId);
            Assert.Equal(_org.ToString(), line.SubjectId);
            Assert.Equal("{}", line.DetailsJson);
        });
        Assert.Equal(["auth0|admin", "auth0|staff", "auth0|admin", "auth0|staff"], lines.Select(line => line.ActorUserId));
    }

    /// <summary>Another org sees and changes nothing of a segment: not found for every change, absent from its list and from its resolution.</summary>
    [Fact]
    public async Task IdentitySegment_AnotherOrg_SeesAndChangesNothing()
    {
        var segment = await QualifiedAutomaticAsync(Service());
        var before = await RowAsync(segment);
        var current = Fingerprint();

        Assert.Null(await Service().SetModeAsync(_other, segment, IdentitySegmentMode.Suspended, "Non mio", "auth0|intruder", Ct));
        Assert.Null(await Service().QualifyAsync(_other, segment, current.PolicyVersion, current.PolicyHash, current.WeightsHash, ReportHash, IdentityAssuranceLevels.Consistency, "auth0|staff", Ct));
        Assert.Null(await Service().RevokeQualificationAsync(_other, segment, "auth0|staff", Ct));
        Assert.Empty((await Service().ListAsync(_other, Ct)).Segments);
        Assert.Null((await Service().ResolveAsync(_other, _otherFlat, Cie, IdentityChannels.AndroidChrome, Ct)).SegmentId);

        Assert.Equivalent(before, await RowAsync(segment));
        Assert.DoesNotContain(await _db.OrgActivityEntries.ToListAsync(Ct), entry => entry.OrgId == _other);
    }

    [Fact]
    public async Task IdentitySegment_TheList_SaysTheFormatsTheChannelsAndThePolicyInForce()
    {
        await QualifiedAutomaticAsync(Service());
        await Service().CreateAsync(_org, _villa, Cie, IdentityChannels.DesktopSafari, "auth0|admin", Ct);

        var overview = await Service().ListAsync(_org, Ct);

        Assert.Equal([null, _villa], overview.Segments.Select(segment => segment.PropertyId));
        Assert.Equal("Villa Brera", overview.Segments[1].PropertyName);
        Assert.Contains(overview.Formats, format => format.Id == Cie && !string.IsNullOrWhiteSpace(format.Label));
        Assert.Equal(IdentityChannels.All, overview.Channels);
        Assert.Equal(("identity-policy-2026.12-1", false, true), (overview.ActivePolicyVersion, overview.ActivePolicyProvisional, overview.AutoApprovalEnabled));

        var unavailable = await Service(MissingPolicy()).ListAsync(_org, Ct);
        Assert.Null(unavailable.ActivePolicyVersion);
        Assert.True(unavailable.ActivePolicyProvisional);
        Assert.All(unavailable.Segments, segment => Assert.False(segment.AutoApprovalAllowed));
    }

    /// <summary>Privacy: the reason of a suspension is the staff's text: it is stored, never logged.</summary>
    [Fact]
    public async Task IdentitySegment_TheReasonOfASuspension_IsNeverLogged()
    {
        var segment = (await Service().CreateAsync(_org, null, Cie, IdentityChannels.AndroidChrome, "auth0|admin", Ct)).Id;

        await Service().SetModeAsync(_org, segment, IdentitySegmentMode.Suspended, "Reclamo di Mario Rossi sul pilota", "auth0|admin", Ct);

        Assert.Equal("Reclamo di Mario Rossi sul pilota", (await RowAsync(segment)).SuspendedReason);
        Assert.NotEmpty(_logs);
        Assert.All(_logs, line => Assert.DoesNotContain("Rossi", line, StringComparison.Ordinal));
    }

    [Fact]
    public void IdentitySegmentCodes_AreSpelledAsThePlanFixesThem()
    {
        Assert.Equal(
            [
                "auto_approval_disabled", "segment_observation", "segment_suspended", "segment_not_qualified", "qualification_mismatch", "policy_provisional",
                "policy_unavailable", "assurance_requires_chip",
            ],
            IdentitySegmentReasonCodes.All);
        Assert.Equal(["observation", "automatic", "suspended"], IdentitySegmentModeCodes.All);
        foreach (var mode in Enum.GetValues<IdentitySegmentMode>())
        {
            Assert.True(IdentitySegmentModeCodes.TryParse(IdentitySegmentModeCodes.CodeOf(mode).ToUpperInvariant(), out var parsed));
            Assert.Equal(mode, parsed);
        }

        Assert.False(IdentitySegmentModeCodes.TryParse("auto", out _));
        Assert.False(IdentitySegmentModeCodes.TryParse(null, out _));
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────────────────────────────────

    private IdentitySegmentService Service(IdentityPolicyProvider? policy = null, IBiometricModelStore? models = null)
    {
        var provider = policy ?? _calibrated;
        return new IdentitySegmentService(
            _db,
            provider,
            new IdentityConfigurationFingerprint(provider, models ?? _models),
            _registry,
            _flags,
            _clock,
            new ListLogger<IdentitySegmentService>(_logs),
            new ActivityLog(_db, _clock));
    }

    private IdentityFingerprint Fingerprint() => new IdentityConfigurationFingerprint(_calibrated, _models).Current()!;

    /// <summary>An org-wide CIE segment of <paramref name="channel"/>, qualified for the configuration in force and set to automatic; its id.</summary>
    private async Task<Guid> QualifiedAutomaticAsync(
        IdentitySegmentService service,
        string channel = IdentityChannels.AndroidChrome,
        string requiredAssurance = IdentityAssuranceLevels.Consistency)
    {
        var segment = await service.CreateAsync(_org, null, Cie, channel, "auth0|admin", Ct);
        await QualifyAsync(service, segment.Id, requiredAssurance);
        var automatic = await service.SetModeAsync(_org, segment.Id, IdentitySegmentMode.Automatic, null, "auth0|admin", Ct);
        Assert.Equal(IdentitySegmentMode.Automatic, automatic!.Mode);
        return segment.Id;
    }

    /// <summary>Qualifies a segment for the fingerprint the <paramref name="service"/> sees.</summary>
    private async Task QualifyAsync(IdentitySegmentService service, Guid segment, string requiredAssurance = IdentityAssuranceLevels.Consistency)
    {
        var overview = await service.ListAsync(_org, Ct);
        Assert.NotNull(overview.ActivePolicyVersion);
        var fingerprint = Fingerprint();
        var version = overview.ActivePolicyVersion == fingerprint.PolicyVersion ? fingerprint : FingerprintOf(overview.ActivePolicyVersion);
        await service.QualifyAsync(_org, segment, version.PolicyVersion, version.PolicyHash, version.WeightsHash, ReportHash, requiredAssurance, "auth0|staff", Ct);
    }

    /// <summary>The fingerprint of a calibrated policy file this class wrote, by its version.</summary>
    private IdentityFingerprint FingerprintOf(string version)
    {
        var file = version == "identity-policy-2026.12-2" ? "recalibrated.json" : "calibrated.json";
        return new IdentityConfigurationFingerprint(Policy(Calibrated(version), file), _models).Current()!;
    }

    /// <summary>A row written as is, for the states the service never writes (automatic without a qualification, a qualification for a provisional policy).</summary>
    private async Task AddRowAsync(IdentitySegmentMode mode, IdentityFingerprint? qualifiedFor)
    {
        _db.IdentitySegmentSettings.Add(new IdentitySegmentSetting
        {
            OrgId = _org,
            FormatId = Cie,
            Channel = IdentityChannels.AndroidChrome,
            Mode = mode,
            QualifiedPolicyVersion = qualifiedFor?.PolicyVersion,
            QualifiedPolicyHash = qualifiedFor?.PolicyHash,
            QualifiedWeightsHash = qualifiedFor?.WeightsHash,
            QualifiedAt = qualifiedFor is null ? null : Now.UtcDateTime,
        });
        await _db.SaveChangesAsync(Ct);
    }

    private async Task<IdentitySegmentSetting> RowAsync(Guid segment)
    {
        return await _db.IdentitySegmentSettings.AsNoTracking().SingleAsync(row => row.Id == segment, Ct);
    }

    /// <summary>The committed policy made a calibration of <paramref name="version"/> (provisional false), the shape a new calibration file has.</summary>
    private static string Calibrated(string version) =>
        IdentityPolicyTestFiles.Edited(("version", $"\"{version}\""), ("provisional", "false"));

    /// <summary>A policy provider over <paramref name="text"/> written to the temporary folder, pinned by its canonical hash.</summary>
    private IdentityPolicyProvider Policy(string text, string fileName)
    {
        File.WriteAllText(Path.Combine(_directory, fileName), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var pin = IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(text))!;
        return new IdentityPolicyProvider(
            Options.Create(new IdentityVerificationOptions { PolicyFile = fileName, PolicySha256 = pin }),
            NullLogger<IdentityPolicyProvider>.Instance,
            _directory);
    }

    private IdentityPolicyProvider MissingPolicy() => new(
        Options.Create(new IdentityVerificationOptions { PolicyFile = "missing.json", PolicySha256 = new string('a', 64) }),
        NullLogger<IdentityPolicyProvider>.Instance,
        _directory);

    private static string Flip(string hash) => (hash[0] == 'a' ? "b" : "a") + hash[1..];

    /// <summary>A store of the three models with the weight hashes given, all loaded unless said.</summary>
    private sealed class Models(string detector, string antiSpoofing, string recognition, bool loaded = true) : IBiometricModelStore
    {
        public BiometricModelsSnapshot Snapshot { get; } = new(
            loaded ? BiometricModelsState.Ready : BiometricModelsState.Unavailable,
            loaded ? null : "file_missing",
            [
                new BiometricModelInfo(BiometricModelRoles.FaceDetector, "yunet", "2023mar", "MIT", "test", detector, loaded, loaded ? null : "file_missing"),
                new BiometricModelInfo(BiometricModelRoles.AntiSpoofing, "minifasnet", "v2", "Apache-2.0", "test", antiSpoofing, loaded, loaded ? null : "file_missing"),
                new BiometricModelInfo(BiometricModelRoles.FaceRecognition, "sface", "2021dec", "Apache-2.0", "test", recognition, loaded, loaded ? null : "file_missing"),
            ]);

        public bool IsAvailable => Snapshot.IsReady;

        public LoadedBiometricModel? FaceDetector => null;

        public LoadedBiometricModel? AntiSpoofing => null;

        public LoadedBiometricModel? FaceRecognition => null;
    }

    /// <summary>A logger that keeps every formatted line, to prove what is not written.</summary>
    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
