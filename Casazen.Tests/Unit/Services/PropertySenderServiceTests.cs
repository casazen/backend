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

        // The connected property is sent by the channel manager by default (PO-02); nothing proves it yet.
        var start = await service.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal(
            (ComplianceSenderType.ChannelManager, SenderSource.DefaultConnected, true),
            (start.Sender, start.Source, start.ChannelManagerAvailable));
        Assert.Equal(
            new[] { ComplianceSenderType.ChannelManager, ComplianceSenderType.Manual },
            start.AvailableSenders.ToArray());
        Assert.Equal((SenderState.Unverified, SenderVerification.Unverifiable), (start.State, start.Verification));

        // A change needs the host's confirmation: without it the answer is 409 and nothing is written.
        var unconfirmed = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.SwitchAsync(world.Connected, ComplianceSenderType.Manual, false, false, Host, CancellationToken.None));
        Assert.Equal("sender_switch_confirmation_required", unconfirmed.Code);
        Assert.Equal<object?>("ChannelManager", unconfirmed.Extensions["currentSender"]);
        Assert.Equal<object?>("Manual", unconfirmed.Extensions["newSender"]);
        Assert.False(await db.PropertyComplianceSenders.AnyAsync());

        // Confirmed, the manual sender is set: no delegation is needed for the host's own hand.
        clock.Advance(TimeSpan.FromMinutes(1));
        var manual = await service.SwitchAsync(world.Connected, ComplianceSenderType.Manual, true, false, Host, CancellationToken.None);
        Assert.Equal(
            (ComplianceSenderType.Manual, ComplianceSenderType.Manual, SenderSource.Stored),
            (manual.Sender, manual.IstatSender, manual.Source));
        Assert.Equal((SenderState.NotConfigured, SenderVerification.NotApplicable), (manual.State, manual.Verification));

        // An automatic sender needs the delegation accepted first: refused, the sender stays and no consent is recorded.
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

        // The consent names the text the person read, and it has to be the text in force: without the version, or with another one (the
        // screen was loaded before the text changed), nothing is recorded and the 409 carries the version to show.
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

        // With the consent and the text in force it is recorded on the organization (who, when, which text), then the channel manager is set.
        var automatic = await service.SwitchAsync(
            world.Connected, ComplianceSenderType.ChannelManager, true, true, Host, CancellationToken.None, delegationTextVersion: seen);
        Assert.Equal((ComplianceSenderType.ChannelManager, true), (automatic.Sender, automatic.DelegationAccepted));
        var org = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == world.OrgId);
        Assert.Equal(
            (Host, AlloggiatiDelegationTexts.CurrentVersion),
            (org.AlloggiatiDelegationGrantedByUserId, org.AlloggiatiDelegationTextVersion));

        // The same sender again changes and records nothing.
        var again = await service.SwitchAsync(world.Connected, ComplianceSenderType.ChannelManager, false, false, Host, CancellationToken.None);
        Assert.Equal(ComplianceSenderType.ChannelManager, again.Sender);

        // Whatever was asked, the property has one sender (both columns equal) and the audit holds the two real changes, once each.
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

        // A declaration is for an automatic sender the org delegated: refused before the delegation is accepted.
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

        // Octorate does not expose the police account: nothing is verified, and the test says so.
        var test = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.PoliceNotExposed), (test.Outcome, test.Reason));
        var unverified = await service.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderState.Unverified, SenderVerification.Unverifiable), (unverified.State, unverified.Verification));
        Assert.Null(unverified.DeclaredAt);

        // The host's word is stored with its date as a declaration, never as a verification.
        clock.Advance(TimeSpan.FromMinutes(1));
        var declared = await service.DeclareActivatedAsync(world.Connected, Host, CancellationToken.None);
        Assert.Equal((SenderState.Declared, SenderVerification.UserDeclared), (declared.State, declared.Verification));
        Assert.Equal<DateTime?>(clock.GetUtcNow().UtcDateTime, declared.DeclaredAt);

        // Octorate cannot be reached: still unknown (a failure proves nothing) and the read of the sender does not fail.
        octorate.Setup(o => o.GetPoliceAccountAsync(Accommodation, It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateServerException(503));
        var down = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.Unavailable), (down.Outcome, down.Reason));
        Assert.Equal(SenderState.Declared, (await service.GetAsync(world.Connected, CancellationToken.None)).State);

        // Octorate does not answer within the cap and the call is cut short: the same for the test as for the read, unknown and not an error.
        octorate.Setup(o => o.GetPoliceAccountAsync(Accommodation, It.IsAny<CancellationToken>())).ThrowsAsync(new TaskCanceledException());
        var slow = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.Unavailable), (slow.Outcome, slow.Reason));
        Assert.Equal(SenderState.Declared, (await service.GetAsync(world.Connected, CancellationToken.None)).State);

        // Octorate says the account is not valid, or the automatic send is off: inactive, whatever the host declared.
        OctorateExposes(octorate, valid: false, autorun: true);
        var invalid = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Inactive, SenderReasonCodes.PoliceAccountInvalid), (invalid.Outcome, invalid.Reason));
        Assert.Equal(SenderState.Inactive, (await service.GetAsync(world.Connected, CancellationToken.None)).State);
        OctorateExposes(octorate, valid: true, autorun: false);
        var autorunOff = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Inactive, SenderReasonCodes.PoliceAutorunOff), (autorunOff.Outcome, autorunOff.Reason));

        // Octorate says the account is valid but leaves out the automatic send (or the other way round): half a proof is no proof, so
        // nothing is verified and the host's declaration is still what the state rests on.
        OctorateExposes(octorate, valid: true, autorun: null);
        var validOnly = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.PoliceNotExposed), (validOnly.Outcome, validOnly.Reason));
        var halfProof = await service.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderState.Declared, SenderVerification.UserDeclared), (halfProof.State, halfProof.Verification));
        Assert.Equal<(bool?, bool?)>((true, null), (halfProof.PoliceAccountValid, halfProof.PoliceAutorun));
        OctorateExposes(octorate, valid: null, autorun: true);
        var autorunOnly = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.PoliceNotExposed), (autorunOnly.Outcome, autorunOnly.Reason));

        // Only Octorate's own word makes it active.
        OctorateExposes(octorate, valid: true, autorun: true);
        var proof = await service.TestConnectionAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderConnectionTestOutcome.Verified, (string?)null), (proof.Outcome, proof.Reason));
        var active = await service.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((SenderState.Active, SenderVerification.Verified), (active.State, active.Verification));
        Assert.Equal<(bool?, bool?)>((true, true), (active.PoliceAccountValid, active.PoliceAutorun));

        // CasaZen is not testable, manual has nothing to test, and a change of sender voids the declaration for good.
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

        // The org accepted the delegation, and the property still has the default sender: the channel manager, with no row yet.
        await new AlloggiatiDelegationService(db, new ActivityLog(db, clock), clock)
            .GrantAsync(world.OrgId, Host, AlloggiatiDelegationTexts.CurrentVersion);
        Assert.False(await db.PropertyComplianceSenders.AnyAsync());

        // The declaration makes the row of the default sender at this very instant, so the moment of the declaration and the moment of the
        // last choice of the sender are the same one. On that boundary the declaration stands.
        var declared = await service.DeclareActivatedAsync(world.Connected, Host, CancellationToken.None);
        var row = await StoredAsync(db, world.Connected);
        Assert.Equal<DateTime?>(Start.UtcDateTime, row.LastChangedAt);
        Assert.Equal<DateTime?>(Start.UtcDateTime, row.ActivationDeclaredAt);
        Assert.Equal((SenderState.Declared, SenderVerification.UserDeclared), (declared.State, declared.Verification));
        Assert.Equal<DateTime?>(Start.UtcDateTime, declared.DeclaredAt);

        // One microsecond later the sender is chosen again (the other one, and back): a later choice, so the declaration was about the
        // choice before it and is void, the sender is not even said to be declared.
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
        // A text date and the three members; the code and the web service key of the object are not read.
        using var full = JsonDocument.Parse(
            """{"policeAccount":{"codice":"TEST","wsCode":"TEST","valid":true,"autorun":false,"lastCron":"2026-11-05T22:00:00Z"}}""");
        var account = OctorateApiClient.ReadPoliceAccount(full.RootElement);
        Assert.Equal<(bool?, bool?)>((true, false), (account.Valid, account.Autorun));
        Assert.Equal<DateTime?>(new DateTime(2026, 11, 5, 22, 0, 0, DateTimeKind.Utc), account.LastCron);

        // A date in epoch milliseconds, and the members Octorate leaves out stay unknown: nothing is assumed.
        var cron = new DateTimeOffset(2026, 11, 5, 22, 0, 0, TimeSpan.Zero);
        using var epoch = JsonDocument.Parse(JsonSerializer.Serialize(new { policeAccount = new { lastCron = cron.ToUnixTimeMilliseconds() } }));
        var partial = OctorateApiClient.ReadPoliceAccount(epoch.RootElement);
        Assert.Equal<DateTime?>(cron.UtcDateTime, partial.LastCron);
        Assert.Equal<(bool?, bool?)>((null, null), (partial.Valid, partial.Autorun));

        // An accommodation created through the API has no police account at all.
        using var bare = JsonDocument.Parse("""{"id":"585492","name":"Casa"}""");
        var none = OctorateApiClient.ReadPoliceAccount(bare.RootElement);
        Assert.Equal<(bool?, bool?, DateTime?)>((null, null, null), (none.Valid, none.Autorun, none.LastCron));
    }

    /// <summary>The sender service over the real store and delegation service, with the clock, Octorate and the flags under the test's control.</summary>
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

    /// <summary>What Octorate answers for the police account of the connected property: the members it exposes (<c>null</c> = left out).</summary>
    private static void OctorateExposes(Mock<IOctorateApiClient> octorate, bool? valid, bool? autorun)
    {
        octorate
            .Setup(o => o.GetPoliceAccountAsync(Accommodation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePoliceAccount { Valid = valid, Autorun = autorun });
    }

    private static Task<PropertyComplianceSender> StoredAsync(AppDbContext db, Guid propertyId) =>
        db.PropertyComplianceSenders.AsNoTracking().SingleAsync(s => s.PropertyId == propertyId);

    /// <summary>When the org accepted the delegation (UTC); <c>null</c> while it has not: what a refused change has to leave untouched.</summary>
    private static async Task<DateTime?> DelegationGrantedAtAsync(AppDbContext db, Guid orgId)
    {
        var org = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
        return org.AlloggiatiDelegationGrantedAt;
    }

    /// <summary>One org with three properties: one confirmed on a connected Octorate account, one that is not connected, one in Switzerland.</summary>
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
