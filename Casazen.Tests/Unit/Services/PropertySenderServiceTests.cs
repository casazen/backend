using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The sender of a property for the host (E03/F01/US02/T01, ADR-029, NF-42): the switch needs the host's confirmation, an automatic sender needs
/// the delegation accepted first, and the property keeps ONE sender (ISTAT always the same, «CasaZen sends» refused in this release); a
/// state is «Active» only on Octorate's own word, a declaration is the host's word and a failure of Octorate proves nothing.
/// </summary>
public class PropertySenderServiceTests
{
    private const string Host = "auth0|sender-host";
    private const string Accommodation = "585492";
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PropertySender_SingleAutomaticSender_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var clock = new FakeTimeProvider(Start);
        var octorate = new Mock<IOctorateApiClient>();
        OctorateExposes(octorate, valid: null, autorun: null);
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var service = NewService(db, clock, octorate);

        var start = await service.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal(
            (ComplianceSenderType.ChannelManager, SenderSource.DefaultConnected, true),
            (start.Sender, start.Source, start.ChannelManagerAvailable));
        Assert.Equal(
            new[] { ComplianceSenderType.ChannelManager, ComplianceSenderType.Manual },
            start.AvailableSenders.ToArray());
        Assert.Equal((SenderState.Unverified, SenderVerification.Unverifiable), (start.State, start.Verification));

        var unconfirmed = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.SwitchAsync(world.Connected, ComplianceSenderType.Manual, false, false, Host, CancellationToken.None));
        Assert.Equal("sender_switch_confirmation_required", unconfirmed.Code);
        Assert.Equal<object?>("ChannelManager", unconfirmed.Extensions["currentSender"]);
        Assert.Equal<object?>("Manual", unconfirmed.Extensions["newSender"]);
        Assert.False(await db.PropertyComplianceSenders.AnyAsync());

        clock.Advance(TimeSpan.FromMinutes(1));
        var manual = await service.SwitchAsync(world.Connected, ComplianceSenderType.Manual, true, false, Host, CancellationToken.None);
        Assert.Equal(
            (ComplianceSenderType.Manual, ComplianceSenderType.Manual, SenderSource.Stored),
            (manual.Sender, manual.IstatSender, manual.Source));
        Assert.Equal((SenderState.NotConfigured, SenderVerification.NotApplicable), (manual.State, manual.Verification));

        clock.Advance(TimeSpan.FromMinutes(1));
        var noConsent = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => service.SwitchAsync(world.Connected, ComplianceSenderType.ChannelManager, true, false, Host, CancellationToken.None));
        Assert.Equal("alloggiati_delegation_required", noConsent.Code);
        Assert.Equal(ComplianceSenderType.Manual, (await StoredAsync(db, world.Connected)).AlloggiatiSender);
        Assert.Null(await DelegationGrantedAtAsync(db, world.OrgId));

        // Every other refusal comes before the consent is recorded. With the consent ticked and the text read, the sender «CasaZen sends», an
        // ISTAT sender that differs, a channel manager the property cannot use and a property outside Italy are all refused, the sender
        // stays and the organization still has no delegation afterwards.
        var seen = AlloggiatiDelegationTexts.CurrentVersion;
        var casaZen = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SwitchAsync(
                world.Connected, ComplianceSenderType.CasaZen, true, true, Host, CancellationToken.None, delegationTextVersion: seen));
        Assert.Equal("sender_casazen_not_available", casaZen.Code);
        var mismatch = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SwitchAsync(
                world.Connected,
                ComplianceSenderType.Manual,
                true,
                true,
                Host,
                CancellationToken.None,
                istatSender: ComplianceSenderType.ChannelManager,
                delegationTextVersion: seen));
        Assert.Equal("sender_istat_mismatch", mismatch.Code);
        var notConnected = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SwitchAsync(
                world.NotConnected, ComplianceSenderType.ChannelManager, true, true, Host, CancellationToken.None, delegationTextVersion: seen));
        Assert.Equal("sender_channel_manager_not_available", notConnected.Code);
        var swiss = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SwitchAsync(
                world.Swiss, ComplianceSenderType.Manual, true, true, Host, CancellationToken.None, delegationTextVersion: seen));
        Assert.Equal("sender_not_applicable", swiss.Code);
        Assert.False(await db.PropertyComplianceSenders.AnyAsync(s => s.PropertyId == world.NotConnected || s.PropertyId == world.Swiss));
        Assert.Equal(ComplianceSenderType.Manual, (await StoredAsync(db, world.Connected)).AlloggiatiSender);
        Assert.Null(await DelegationGrantedAtAsync(db, world.OrgId));

        var unnamed = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SwitchAsync(world.Connected, ComplianceSenderType.ChannelManager, true, true, Host, CancellationToken.None));
        Assert.Equal("alloggiati_delegation_text_version_required", unnamed.Code);
        var stale = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.SwitchAsync(
                world.Connected, ComplianceSenderType.ChannelManager, true, true, Host, CancellationToken.None, delegationTextVersion: "2026-10-v0"));
        Assert.Equal("alloggiati_delegation_text_outdated", stale.Code);
        Assert.Equal<object?>(AlloggiatiDelegationTexts.CurrentVersion, stale.Extensions["delegationTextVersion"]);
        Assert.Equal(ComplianceSenderType.Manual, (await StoredAsync(db, world.Connected)).AlloggiatiSender);
        Assert.Null(await DelegationGrantedAtAsync(db, world.OrgId));

        var automatic = await service.SwitchAsync(
            world.Connected, ComplianceSenderType.ChannelManager, true, true, Host, CancellationToken.None, delegationTextVersion: seen);
        Assert.Equal((ComplianceSenderType.ChannelManager, true), (automatic.Sender, automatic.DelegationAccepted));
        var org = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == world.OrgId);
        Assert.Equal(
            (Host, AlloggiatiDelegationTexts.CurrentVersion),
            (org.AlloggiatiDelegationGrantedByUserId, org.AlloggiatiDelegationTextVersion));

        var again = await service.SwitchAsync(world.Connected, ComplianceSenderType.ChannelManager, false, false, Host, CancellationToken.None);
        Assert.Equal(ComplianceSenderType.ChannelManager, again.Sender);

        var row = await StoredAsync(db, world.Connected);
        Assert.Equal((ComplianceSenderType.ChannelManager, ComplianceSenderType.ChannelManager), (row.AlloggiatiSender, row.IstatSender));
        var lines = await db.OrgActivityEntries.AsNoTracking()
            .Where(e => e.Type == OrgActivityType.PropertySenderChanged)
            .OrderBy(e => e.When)
            .ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal(world.Connected.ToString(), line.SubjectId));
        Assert.Equal(
            new Dictionary<string, string> { [OrgActivityDetailKeys.FromSender] = "ChannelManager", [OrgActivityDetailKeys.ToSender] = "Manual" },
            OrgActivityDetails.Parse(lines[0].DetailsJson));
        Assert.Equal(
            new Dictionary<string, string> { [OrgActivityDetailKeys.FromSender] = "Manual", [OrgActivityDetailKeys.ToSender] = "ChannelManager" },
            OrgActivityDetails.Parse(lines[1].DetailsJson));
    }

    [Fact]
    public async Task PropertySender_VerifiedNeedsProof_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var clock = new FakeTimeProvider(Start);
        var octorate = new Mock<IOctorateApiClient>();
        OctorateExposes(octorate, valid: null, autorun: null);
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var service = NewService(db, clock, octorate);

        var early = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => service.DeclareActivatedAsync(world.Connected, Host, CancellationToken.None));
        Assert.Equal("alloggiati_delegation_required", early.Code);
        await service.SwitchAsync(
            world.Connected,
            ComplianceSenderType.ChannelManager,
            true,
            true,
            Host,
            CancellationToken.None,
            delegationTextVersion: AlloggiatiDelegationTexts.CurrentVersion);

        var test = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.PoliceNotExposed), (test.Outcome, test.Reason));
        var unverified = await service.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderState.Unverified, SenderVerification.Unverifiable), (unverified.State, unverified.Verification));
        Assert.Null(unverified.DeclaredAt);

        clock.Advance(TimeSpan.FromMinutes(1));
        var declared = await service.DeclareActivatedAsync(world.Connected, Host, CancellationToken.None);
        Assert.Equal((SenderState.Declared, SenderVerification.UserDeclared), (declared.State, declared.Verification));
        Assert.Equal<DateTime?>(clock.GetUtcNow().UtcDateTime, declared.DeclaredAt);

        octorate.Setup(o => o.GetPoliceAccountAsync(Accommodation, It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateServerException(503));
        var down = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.Unavailable), (down.Outcome, down.Reason));
        Assert.Equal(SenderState.Declared, (await service.GetAsync(world.Connected, CancellationToken.None)).State);

        octorate.Setup(o => o.GetPoliceAccountAsync(Accommodation, It.IsAny<CancellationToken>())).ThrowsAsync(new TaskCanceledException());
        var slow = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.Unavailable), (slow.Outcome, slow.Reason));
        Assert.Equal(SenderState.Declared, (await service.GetAsync(world.Connected, CancellationToken.None)).State);

        OctorateExposes(octorate, valid: false, autorun: true);
        var invalid = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Inactive, SenderReasonCodes.PoliceAccountInvalid), (invalid.Outcome, invalid.Reason));
        Assert.Equal(SenderState.Inactive, (await service.GetAsync(world.Connected, CancellationToken.None)).State);
        OctorateExposes(octorate, valid: true, autorun: false);
        var autorunOff = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Inactive, SenderReasonCodes.PoliceAutorunOff), (autorunOff.Outcome, autorunOff.Reason));

        OctorateExposes(octorate, valid: true, autorun: null);
        var validOnly = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.PoliceNotExposed), (validOnly.Outcome, validOnly.Reason));
        var halfProof = await service.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderState.Declared, SenderVerification.UserDeclared), (halfProof.State, halfProof.Verification));
        Assert.Equal<(bool?, bool?)>((true, null), (halfProof.PoliceAccountValid, halfProof.PoliceAutorun));
        OctorateExposes(octorate, valid: null, autorun: true);
        var autorunOnly = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.PoliceNotExposed), (autorunOnly.Outcome, autorunOnly.Reason));

        OctorateExposes(octorate, valid: true, autorun: true);
        var proof = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Verified, (string?)null), (proof.Outcome, proof.Reason));
        var active = await service.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderState.Active, SenderVerification.Verified), (active.State, active.Verification));
        Assert.Equal<(bool?, bool?)>((true, true), (active.PoliceAccountValid, active.PoliceAutorun));

        OctorateExposes(octorate, valid: null, autorun: null);
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.SwitchAsync(world.Connected, ComplianceSenderType.Manual, true, false, Host, CancellationToken.None);
        var manualTest = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal(SenderConnectionTestOutcome.NotApplicable, manualTest.Outcome);
        var notDeclared = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.DeclareActivatedAsync(world.Connected, Host, CancellationToken.None));
        Assert.Equal("sender_declaration_not_applicable", notDeclared.Code);
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.SwitchAsync(world.Connected, ComplianceSenderType.ChannelManager, true, false, Host, CancellationToken.None);
        var voided = await service.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderState.Unverified, (DateTime?)null), (voided.State, voided.DeclaredAt));
    }

    [Fact]
    public async Task PropertySender_DeclarationStandsUntilALaterChoice_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var clock = new FakeTimeProvider(Start);
        var octorate = new Mock<IOctorateApiClient>();
        OctorateExposes(octorate, valid: null, autorun: null);
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var service = NewService(db, clock, octorate);

        await new AlloggiatiDelegationService(db, new ActivityLog(db, clock), clock)
            .GrantAsync(world.OrgId, Host, AlloggiatiDelegationTexts.CurrentVersion);
        Assert.False(await db.PropertyComplianceSenders.AnyAsync());

        var declared = await service.DeclareActivatedAsync(world.Connected, Host, CancellationToken.None);
        var row = await StoredAsync(db, world.Connected);
        Assert.Equal<DateTime?>(Start.UtcDateTime, row.LastChangedAt);
        Assert.Equal<DateTime?>(Start.UtcDateTime, row.ActivationDeclaredAt);
        Assert.Equal((SenderState.Declared, SenderVerification.UserDeclared), (declared.State, declared.Verification));
        Assert.Equal<DateTime?>(Start.UtcDateTime, declared.DeclaredAt);

        clock.Advance(TimeSpan.FromTicks(10));
        var manual = await service.SwitchAsync(world.Connected, ComplianceSenderType.Manual, true, false, Host, CancellationToken.None);
        Assert.Null(manual.DeclaredAt);
        var back = await service.SwitchAsync(world.Connected, ComplianceSenderType.ChannelManager, true, false, Host, CancellationToken.None);
        Assert.Equal((SenderState.Unverified, SenderVerification.Unverifiable), (back.State, back.Verification));
        Assert.Null(back.DeclaredAt);
    }

    [Fact]
    public void OctorateApiClient_ReadPoliceAccount_ReadsAMemberOnlyWhenOctorateSendsIt()
    {
        using var full = JsonDocument.Parse(
            """{"policeAccount":{"codice":"TEST","wsCode":"TEST","valid":true,"autorun":false,"lastCron":"2026-11-05T22:00:00Z"}}""");
        var account = OctorateApiClient.ReadPoliceAccount(full.RootElement);
        Assert.Equal<(bool?, bool?)>((true, false), (account.Valid, account.Autorun));
        Assert.Equal<DateTime?>(new DateTime(2026, 11, 5, 22, 0, 0, DateTimeKind.Utc), account.LastCron);

        var cron = new DateTimeOffset(2026, 11, 5, 22, 0, 0, TimeSpan.Zero);
        using var epoch = JsonDocument.Parse(JsonSerializer.Serialize(new { policeAccount = new { lastCron = cron.ToUnixTimeMilliseconds() } }));
        var partial = OctorateApiClient.ReadPoliceAccount(epoch.RootElement);
        Assert.Equal<DateTime?>(cron.UtcDateTime, partial.LastCron);
        Assert.Equal<(bool?, bool?)>((null, null), (partial.Valid, partial.Autorun));

        using var bare = JsonDocument.Parse("""{"id":"585492","name":"Casa"}""");
        var none = OctorateApiClient.ReadPoliceAccount(bare.RootElement);
        Assert.Equal<(bool?, bool?, DateTime?)>((null, null, null), (none.Valid, none.Autorun, none.LastCron));
    }

    /// <summary>
    /// The read-only check of a switch (E10/F02/US01/T01, the dry run of the bulk configuration): it refuses exactly what the switch refuses, in
    /// the same order, takes the confirmation as given and the consent as not given, and writes nothing at all: no sender row (not even for a
    /// property that only has the default), no consent, no audit line, and Octorate is not called.
    /// </summary>
    [Fact]
    public async Task CheckSwitch_RefusesWhatTheSwitchRefuses_AndWritesNothing()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var clock = new FakeTimeProvider(Start);
        var octorate = new Mock<IOctorateApiClient>();
        OctorateExposes(octorate, valid: true, autorun: true);
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var service = NewService(db, clock, octorate);
        var ct = CancellationToken.None;

        var toManual = await service.CheckSwitchAsync(world.Connected, ComplianceSenderType.Manual, ct);
        Assert.Equal((ComplianceSenderType.ChannelManager, ComplianceSenderType.Manual, true), (toManual.Current, toManual.New, toManual.WouldChange));
        var notConnected = await service.CheckSwitchAsync(world.NotConnected, ComplianceSenderType.Manual, ct);
        Assert.False(notConnected.WouldChange);

        async Task<string> RefusalAsync(Guid propertyId, ComplianceSenderType sender, ComplianceSenderType? istat = null)
        {
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => service.CheckSwitchAsync(propertyId, sender, ct, istat));
            return refused switch
            {
                DomainException domain => domain.Code,
                NotFoundException missing => missing.Code!,
                _ => throw refused,
            };
        }

        Assert.Equal("sender_casazen_not_available", await RefusalAsync(world.Connected, ComplianceSenderType.CasaZen));
        Assert.Equal("sender_istat_mismatch", await RefusalAsync(world.Connected, ComplianceSenderType.Manual, ComplianceSenderType.ChannelManager));
        Assert.Equal("sender_channel_manager_not_available", await RefusalAsync(world.NotConnected, ComplianceSenderType.ChannelManager));
        Assert.Equal("sender_not_applicable", await RefusalAsync(world.Swiss, ComplianceSenderType.Manual));
        Assert.Equal("property_not_found", await RefusalAsync(Guid.NewGuid(), ComplianceSenderType.Manual));
        Assert.Equal("alloggiati_delegation_required", await RefusalAsync(world.Connected, ComplianceSenderType.ChannelManager));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CheckSwitchAsync(world.Connected, (ComplianceSenderType)99, ct));

        await new AlloggiatiDelegationService(db, new ActivityLog(db, clock), clock)
            .GrantAsync(world.OrgId, Host, AlloggiatiDelegationTexts.CurrentVersion, ct);
        var lines = await db.OrgActivityEntries.CountAsync();
        var sameSender = await service.CheckSwitchAsync(world.Connected, ComplianceSenderType.ChannelManager, ct);
        Assert.False(sameSender.WouldChange);

        Assert.False(await db.PropertyComplianceSenders.AnyAsync());
        Assert.Equal(lines, await db.OrgActivityEntries.CountAsync());
        octorate.Verify(o => o.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The switch without the view (E10/F02/US01, the bulk configuration, which discards the view): it refuses what the switch refuses, with the
    /// same codes, and writes nothing then; it writes what the switch writes (the sender for both communications, one audit line, the consent of the
    /// delegation only when the caller gives it); and it never reads the police account on Octorate, which only builds the view. The switch of a
    /// single property still builds its view, and so still reads it.
    /// </summary>
    [Fact]
    public async Task SwitchWithoutView_DoesTheSwitch_AndNeverAsksOctorate_WhileTheSwitchStillDoes()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var clock = new FakeTimeProvider(Start);
        var octorate = new Mock<IOctorateApiClient>();
        OctorateExposes(octorate, valid: true, autorun: true);
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var service = NewService(db, clock, octorate);
        var ct = CancellationToken.None;
        var seen = AlloggiatiDelegationTexts.CurrentVersion;
        void NeverAsked() => octorate.Verify(o => o.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Task<int> LinesAsync() => db.OrgActivityEntries.CountAsync(e => e.Type == OrgActivityType.PropertySenderChanged);

        (Guid Property, ComplianceSenderType Sender, bool Confirmed, bool Consent, string? Version, ComplianceSenderType? Istat, string Code)[] refusals =
        [
            (world.Connected, ComplianceSenderType.Manual, false, false, null, null, "sender_switch_confirmation_required"),
            (world.Connected, ComplianceSenderType.CasaZen, true, false, null, null, "sender_casazen_not_available"),
            (world.Connected, ComplianceSenderType.Manual, true, false, null, ComplianceSenderType.ChannelManager, "sender_istat_mismatch"),
            (world.NotConnected, ComplianceSenderType.ChannelManager, true, false, null, null, "sender_channel_manager_not_available"),
            (world.Swiss, ComplianceSenderType.Manual, true, false, null, null, "sender_not_applicable"),
            (Guid.NewGuid(), ComplianceSenderType.Manual, true, false, null, null, "property_not_found"),
            (world.Connected, ComplianceSenderType.ChannelManager, true, false, null, null, "alloggiati_delegation_required"),
            (world.Connected, ComplianceSenderType.ChannelManager, true, true, null, null, "alloggiati_delegation_text_version_required"),
            (world.Connected, ComplianceSenderType.ChannelManager, true, true, "2026-10-v0", null, "alloggiati_delegation_text_outdated"),
        ];
        async Task<string> CodeAsync(Func<Task> call)
        {
            var refused = await Assert.ThrowsAnyAsync<Exception>(call);
            return refused switch
            {
                DomainException domain => domain.Code,
                NotFoundException missing => missing.Code!,
                _ => throw refused,
            };
        }

        foreach (var c in refusals)
        {
            var viaSwitch = await CodeAsync(() => service.SwitchAsync(c.Property, c.Sender, c.Confirmed, c.Consent, Host, ct, c.Istat, c.Version));
            var withoutView = await CodeAsync(() => service.SwitchWithoutViewAsync(c.Property, c.Sender, c.Confirmed, c.Consent, Host, ct, c.Istat, c.Version));
            Assert.Equal((c.Code, c.Code), (viaSwitch, withoutView));
        }

        Assert.False(await db.PropertyComplianceSenders.AnyAsync());
        Assert.Equal(0, await LinesAsync());
        Assert.Null(await DelegationGrantedAtAsync(db, world.OrgId));
        NeverAsked();

        clock.Advance(TimeSpan.FromMinutes(1));
        var manual = await service.SwitchWithoutViewAsync(world.Connected, ComplianceSenderType.Manual, true, false, Host, ct);
        Assert.Equal(
            (ComplianceSenderType.Manual, ComplianceSenderType.Manual, SenderSource.Stored),
            (manual.AlloggiatiSender, manual.IstatSender, manual.Source));
        Assert.Equal(ComplianceSenderType.Manual, (await StoredAsync(db, world.Connected)).AlloggiatiSender);
        Assert.Equal(1, await LinesAsync());
        NeverAsked();

        clock.Advance(TimeSpan.FromMinutes(1));
        var automatic = await service.SwitchWithoutViewAsync(world.Connected, ComplianceSenderType.ChannelManager, true, true, Host, ct, delegationTextVersion: seen);
        Assert.Equal((ComplianceSenderType.ChannelManager, ComplianceSenderType.ChannelManager), (automatic.AlloggiatiSender, automatic.IstatSender));
        var org = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == world.OrgId);
        Assert.Equal((Host, seen), (org.AlloggiatiDelegationGrantedByUserId, org.AlloggiatiDelegationTextVersion));
        Assert.Equal(2, await LinesAsync());
        NeverAsked();

        clock.Advance(TimeSpan.FromMinutes(1));
        await service.SwitchAsync(world.Connected, ComplianceSenderType.Manual, true, false, Host, ct);
        NeverAsked();
        clock.Advance(TimeSpan.FromMinutes(1));
        var view = await service.SwitchAsync(world.Connected, ComplianceSenderType.ChannelManager, true, false, Host, ct);
        Assert.Equal((ComplianceSenderType.ChannelManager, SenderState.Active), (view.Sender, view.State));
        octorate.Verify(o => o.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PropertySender_AutoSendOfTheOrg_IsOffByDefault_AndTheViewReadsItFromTheOrg()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var octorate = new Mock<IOctorateApiClient>();
        OctorateExposes(octorate, valid: null, autorun: null);
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var service = NewService(db, new FakeTimeProvider(Start), octorate);

        Assert.False((await service.GetAsync(world.Connected, CancellationToken.None)).AutoSendGuestsToOctorate);

        var org = await db.Orgs.SingleAsync(o => o.Id == world.OrgId);
        org.AutoSendGuestsToOctorate = true;
        await db.SaveChangesAsync();

        Assert.True((await service.GetAsync(world.Connected, CancellationToken.None)).AutoSendGuestsToOctorate);
    }

    [Fact]
    public async Task AccountChoice_IsInheritedByItalianProperties_WithoutMarkingThemOperational()
    {
        var name = Guid.NewGuid().ToString();
        var world = await SeedAsync(name);
        await using var db = OrgTeamTestData.NewDb(name);
        var org = await db.Orgs.SingleAsync(o => o.Id == world.OrgId);
        org.DefaultComplianceSender = ComplianceSenderType.ChannelManager;
        org.DefaultComplianceSenderChangedAt = Start.UtcDateTime;
        await db.SaveChangesAsync();
        var service = NewService(db, new FakeTimeProvider(Start), new Mock<IOctorateApiClient>());

        var inherited = await service.GetAsync(world.NotConnected, CancellationToken.None);
        Assert.Equal(SenderSource.OrganizationDefault, inherited.Source);
        Assert.Equal(ComplianceSenderType.ChannelManager, inherited.Sender);
        Assert.Equal(inherited.Sender, inherited.IstatSender);
        Assert.NotEqual(SenderState.Active, inherited.State);
        Assert.False(inherited.ChannelManagerAvailable);
        var swiss = await service.GetAsync(world.Swiss, CancellationToken.None);
        Assert.Equal(SenderSource.NotApplicable, swiss.Source);
        Assert.False(swiss.Applies);
    }

    [Fact]
    public async Task AccountChoice_PreservesExplicitPropertyOverride_AndNeverLeaksAcrossOrganizations()
    {
        var name = Guid.NewGuid().ToString();
        var world = await SeedAsync(name);
        await using var db = OrgTeamTestData.NewDb(name);
        var service = NewService(db, new FakeTimeProvider(Start), new Mock<IOctorateApiClient>());
        await service.SwitchAsync(world.Connected, ComplianceSenderType.Manual, true, false, Host, CancellationToken.None);
        var org = await db.Orgs.SingleAsync(o => o.Id == world.OrgId);
        org.DefaultComplianceSender = ComplianceSenderType.ChannelManager;
        var other = OrgTeamTestData.AddOrg(db);
        other.DefaultComplianceSender = ComplianceSenderType.Manual;
        var otherProperty = NewProperty(other.Id, "IT", 4);
        db.Properties.Add(otherProperty);
        await db.SaveChangesAsync();

        var explicitChoice = await service.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderSource.Stored, ComplianceSenderType.Manual), (explicitChoice.Source, explicitChoice.Sender));
        var otherChoice = await service.GetAsync(otherProperty.Id, CancellationToken.None);
        Assert.Equal((SenderSource.OrganizationDefault, ComplianceSenderType.Manual), (otherChoice.Source, otherChoice.Sender));
        var original = await service.GetAsync(world.NotConnected, CancellationToken.None);
        Assert.Equal(ComplianceSenderType.ChannelManager, original.Sender);
        Assert.Single(await db.PropertyComplianceSenders.ToListAsync());
    }

    [Fact]
    public async Task DirectCutover_IsIndependentByService_AndNeverRestoresOctorate()
    {
        var dbName = Guid.NewGuid().ToString();
        var world = await SeedAsync(dbName);
        await using var db = OrgTeamTestData.NewDb(dbName);
        var clock = new FakeTimeProvider(Start);
        var activity = new ActivityLog(db, clock);
        var store = new PropertySenderStore(db, activity, Mock.Of<IHostEnvironment>(h => h.EnvironmentName == "Staging"), clock);
        var routes = new DirectPortalRoutes(db, clock, activity, store);
        var checks = 0;
        Task Verify(CancellationToken _) { checks++; return Task.CompletedTask; }
        var first = await routes.ActivateAsync(world.OrgId, world.Connected, true, Host, Verify, default);
        Assert.NotNull(first.Alloggiati);
        Assert.Null(first.Istat);
        Assert.Equal(first, await routes.ActivateAsync(world.OrgId, world.Connected, true, Host, Verify, default));
        Assert.Equal(1, checks);
        var read = await store.GetAsync(world.Connected, default);
        Assert.Equal(ComplianceSenderType.CasaZen, read.AlloggiatiSender);
        Assert.Equal(ComplianceSenderType.Manual, read.IstatSender);
        Assert.Null((await routes.ReadAsync(Guid.NewGuid(), world.Connected, default)).Alloggiati);
        await Assert.ThrowsAsync<DomainConflictException>(() => store.SetAsync(world.Connected, ComplianceSenderType.ChannelManager, Host, default));
    }

    private static PropertySenderService NewService(AppDbContext db, FakeTimeProvider clock, Mock<IOctorateApiClient> octorate)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        var scope = new Mock<IOctorateOrgScope>();
        scope.Setup(s => s.Use(It.IsAny<Guid>())).Returns(Mock.Of<IDisposable>());
        var questura = new Mock<IQuesturaCredentialsService>();
        questura
            .Setup(q => q.GetStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(QuesturaCredentialsStatus.NotConfigured);
        var activity = new ActivityLog(db, clock);
        return new PropertySenderService(
            db,
            new PropertySenderStore(db, activity, environment.Object, clock),
            new AlloggiatiDelegationService(db, activity, clock),
            questura.Object,
            octorate.Object,
            scope.Object,
            flags.Object,
            environment.Object,
            NullLogger<PropertySenderService>.Instance,
            clock);
    }

    private static void OctorateExposes(Mock<IOctorateApiClient> octorate, bool? valid, bool? autorun)
    {
        octorate
            .Setup(o => o.GetPoliceAccountAsync(Accommodation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePoliceAccount { Valid = valid, Autorun = autorun });
    }

    private static Task<PropertyComplianceSender> StoredAsync(AppDbContext db, Guid propertyId) =>
        db.PropertyComplianceSenders.AsNoTracking().SingleAsync(s => s.PropertyId == propertyId);

    private static async Task<DateTime?> DelegationGrantedAtAsync(AppDbContext db, Guid orgId)
    {
        var org = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
        return org.AlloggiatiDelegationGrantedAt;
    }

    private static async Task<World> SeedAsync(string databaseName)
    {
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var org = OrgTeamTestData.AddOrg(db);
        var connected = NewProperty(org.Id, "IT", 1);
        var notConnected = NewProperty(org.Id, "IT", 2);
        var swiss = NewProperty(org.Id, "CH", 3);
        db.Properties.AddRange(connected, notConnected, swiss);

        var connection = new OctorateConnection
        {
            OrgId = org.Id,
            Environment = OctorateEnvironment.Test,
            Status = OctorateConnectionStatus.Connected,
        };
        db.OctorateConnections.Add(connection);
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = org.Id,
            ConnectionId = connection.Id,
            OctorateAccommodationId = Accommodation,
            PropertyId = connected.Id,
            Status = OctorateMappingStatus.Confirmed,
        });
        await db.SaveChangesAsync();
        return new World(org.Id, connected.Id, notConnected.Id, swiss.Id);
    }

    private static Property NewProperty(Guid orgId, string country, int streetNumber) =>
        new()
        {
            OwnerId = Host,
            OrgId = orgId,
            Name = "Casa TEST",
            Description = "Casa",
            Address = $"Via Roma {streetNumber}",
            City = country == "CH" ? "Lugano" : "Firenze",
            PostalCode = country == "CH" ? "6900" : "50100",
            Country = country,
            Currency = country == "CH" ? "CHF" : "EUR",
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };

    private sealed record World(Guid OrgId, Guid Connected, Guid NotConnected, Guid Swiss);
}
