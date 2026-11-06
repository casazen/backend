using System.Reflection;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.DTOs.Compliance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The connection health of a property (E03/F02/US01/T02, NF-52, AC-NF52-11): a channel-manager send is active only on Octorate's own word
/// (the police account valid and its automatic send on, both read), a member the API does not expose or an Octorate that cannot be reached
/// is unknown, never active; a manual send and a house outside Italy are manual; the credentials kept in CasaZen are only said to exist and
/// are never usable in this release; and the answer, like the logs, carries no credential, token or key.
/// </summary>
public class ComplianceConnectionHealthServiceTests
{
    private const string Host = "auth0|health-host";
    private const string Accommodation = "585492";
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConnectionHealth_ActiveOnlyOnOctoratesOwnWord_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var octorate = new Mock<IOctorateApiClient>();
        var lastCron = new DateTime(2026, 11, 5, 22, 0, 0, DateTimeKind.Utc);
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var harness = new Harness(db, octorate);

        // The API exposes nothing: unknown (the host is asked to confirm), never active, and every police field is null.
        Exposes(octorate, null, null, null);
        var nothing = await harness.Health.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal(ConnectionHealthState.Unknown, nothing.Alloggiati.State);
        Assert.Equal(SenderReasonCodes.PoliceNotExposed, nothing.Alloggiati.Reason);
        Assert.Equal<(bool?, bool?, DateTime?)>((null, null, null), (nothing.Alloggiati.ConnectionValid, nothing.Alloggiati.Autorun, nothing.Alloggiati.LastAutomaticRunAt));

        // Half a proof is no proof: valid with the automatic send left out, and the other way round.
        Exposes(octorate, true, null, null);
        Assert.Equal(ConnectionHealthState.Unknown, (await harness.Health.GetAsync(world.Connected, CancellationToken.None)).Alloggiati.State);
        Exposes(octorate, null, true, null);
        Assert.Equal(ConnectionHealthState.Unknown, (await harness.Health.GetAsync(world.Connected, CancellationToken.None)).Alloggiati.State);

        // Octorate says it does not send: inactive, with the reason.
        Exposes(octorate, false, true, null);
        var invalid = await harness.Health.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((ConnectionHealthState.Inactive, SenderReasonCodes.PoliceAccountInvalid), (invalid.Alloggiati.State, invalid.Alloggiati.Reason));
        Exposes(octorate, true, false, null);
        var autorunOff = await harness.Health.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((ConnectionHealthState.Inactive, SenderReasonCodes.PoliceAutorunOff), (autorunOff.Alloggiati.State, autorunOff.Alloggiati.Reason));

        // Octorate cannot be reached: a failure proves nothing, so unknown and not an error.
        octorate
            .Setup(o => o.GetPoliceAccountAsync(Accommodation, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        var down = await harness.Health.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal((ConnectionHealthState.Unknown, SenderReasonCodes.Unavailable), (down.Alloggiati.State, down.Alloggiati.Reason));

        // Only both members true make it active, with the last run of the daily job.
        Exposes(octorate, true, true, lastCron);
        var active = await harness.Health.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal(ConnectionHealthState.Active, active.Alloggiati.State);
        Assert.Null(active.Alloggiati.Reason);
        Assert.Equal<(bool?, bool?, DateTime?)>((true, true, lastCron), (active.Alloggiati.ConnectionValid, active.Alloggiati.Autorun, active.Alloggiati.LastAutomaticRunAt));
        Assert.Equal(ComplianceSenderType.ChannelManager, active.Alloggiati.Sender);

        // ISTAT follows Alloggiati: the same sender, state and readings.
        Assert.Equal(active.Alloggiati, active.Istat);
    }

    [Fact]
    public async Task ConnectionHealth_ManualAndOutsideItaly_AreManualWithoutAnyReading_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var octorate = new Mock<IOctorateApiClient>();
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var harness = new Harness(db, octorate);

        // Not connected to a channel manager: the host sends (the default of PO-02).
        var manual = await harness.Health.GetAsync(world.NotConnected, CancellationToken.None);
        Assert.True(manual.Applies);
        Assert.Equal((ComplianceSenderType.Manual, ConnectionHealthState.Manual), (manual.Alloggiati.Sender, manual.Alloggiati.State));
        Assert.Equal((ComplianceSenderType.Manual, ConnectionHealthState.Manual), (manual.Istat.Sender, manual.Istat.State));
        Assert.Equal<(bool?, bool?, DateTime?, string?)>(
            (null, null, null, null),
            (manual.Alloggiati.ConnectionValid, manual.Alloggiati.Autorun, manual.Alloggiati.LastAutomaticRunAt, manual.Alloggiati.Reason));

        // A house in Switzerland has no police or ISTAT sender to speak of.
        var swiss = await harness.Health.GetAsync(world.Swiss, CancellationToken.None);
        Assert.False(swiss.Applies);
        Assert.Equal(ConnectionHealthState.Manual, swiss.Alloggiati.State);
        Assert.Equal(ConnectionHealthState.Manual, swiss.Istat.State);

        // Nothing was asked of Octorate for either.
        octorate.Verify(o => o.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConnectionHealth_HostDeclaration_IsShownButNeverMakesItActive_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var octorate = new Mock<IOctorateApiClient>();
        Exposes(octorate, null, null, null);
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var harness = new Harness(db, octorate);
        await new AlloggiatiDelegationService(db, new ActivityLog(db, harness.Clock), harness.Clock)
            .GrantAsync(world.OrgId, Host, AlloggiatiDelegationTexts.CurrentVersion);

        await harness.Sender.DeclareActivatedAsync(world.Connected, Host, CancellationToken.None);

        var health = await harness.Health.GetAsync(world.Connected, CancellationToken.None);
        Assert.Equal(ConnectionHealthState.Unknown, health.Alloggiati.State);
        Assert.Equal<DateTime?>(Start.UtcDateTime, health.Alloggiati.DeclaredAt);
    }

    [Fact]
    public async Task ConnectionHealth_CredentialsInCasaZen_AreNeverUsableInThisRelease_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var octorate = new Mock<IOctorateApiClient>();
        Exposes(octorate, true, true, null);
        await using var db = OrgTeamTestData.NewDb(databaseName);
        db.PropertyQuesturaCredentials.Add(new PropertyQuesturaCredentials
        {
            OrgId = world.OrgId,
            PropertyId = world.Connected,
            Username = "u",
            Password = "p",
            WsKey = "k",
            LastVerifiedAt = Start.UtcDateTime,
            LastVerificationOk = true,
        });
        await db.SaveChangesAsync();
        var harness = new Harness(db, octorate, credentialsConfigured: [world.Connected]);

        var withCredentials = await harness.Health.GetAsync(world.Connected, CancellationToken.None);
        var without = await harness.Health.GetAsync(world.NotConnected, CancellationToken.None);

        // They exist, and even a verification that passed does not make them usable: the verification is postponed, so CasaZen never
        // says the receipts can be downloaded.
        Assert.True(withCredentials.CredentialsInCasaZen);
        Assert.False(withCredentials.CredentialsUsable);
        Assert.False(without.CredentialsInCasaZen);
        Assert.False(without.CredentialsUsable);
    }

    [Fact]
    public async Task ConnectionHealth_SenderCasaZen_IsNeverActive_AndRefusedCredentialsAreInactive_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var octorate = new Mock<IOctorateApiClient>();
        await using var db = OrgTeamTestData.NewDb(databaseName);
        db.PropertyComplianceSenders.Add(new PropertyComplianceSender
        {
            OrgId = world.OrgId,
            PropertyId = world.NotConnected,
            AlloggiatiSender = ComplianceSenderType.CasaZen,
            IstatSender = ComplianceSenderType.CasaZen,
            LastChangedAt = Start.UtcDateTime,
            LastChangedByUserId = Host,
        });
        var credentials = new PropertyQuesturaCredentials
        {
            OrgId = world.OrgId,
            PropertyId = world.NotConnected,
            Username = "u",
            Password = "p",
            WsKey = "k",
            LastVerificationOk = null,
        };
        db.PropertyQuesturaCredentials.Add(credentials);
        await db.SaveChangesAsync();
        var harness = new Harness(db, octorate, credentialsConfigured: [world.NotConnected]);

        // Never verified: CasaZen cannot tell, and the sender is not available in this release.
        var unverified = await harness.Health.GetAsync(world.NotConnected, CancellationToken.None);
        Assert.Equal((ComplianceSenderType.CasaZen, ConnectionHealthState.Unknown, SenderReasonCodes.CasaZenNotAvailable),
            (unverified.Alloggiati.Sender, unverified.Alloggiati.State, unverified.Alloggiati.Reason));

        // Verified once and the service said yes: still not active, because the credentials are never usable here.
        credentials.LastVerificationOk = true;
        await db.SaveChangesAsync();
        Assert.Equal(ConnectionHealthState.Unknown, (await harness.Health.GetAsync(world.NotConnected, CancellationToken.None)).Alloggiati.State);

        // The service refused them: inactive, with the reason.
        credentials.LastVerificationOk = false;
        await db.SaveChangesAsync();
        var refused = await harness.Health.GetAsync(world.NotConnected, CancellationToken.None);
        Assert.Equal((ConnectionHealthState.Inactive, "credentials_refused"), (refused.Alloggiati.State, refused.Alloggiati.Reason));
        Assert.False(refused.CredentialsUsable);
    }

    [Fact]
    public async Task ComplianceConnectionHealth_NeverExposesCredentials_Test()
    {
        const string username = "UTENTE-SEGRETO-3141";
        const string password = "PASSWORD-SEGRETA-2718";
        const string wsKey = "CHIAVE-WS-SEGRETA-1618";
        const string octorateToken = "TOKEN-OCTORATE-SEGRETO-1414";

        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName);
        var octorate = new Mock<IOctorateApiClient>();
        Exposes(octorate, true, true, new DateTime(2026, 11, 5, 22, 0, 0, DateTimeKind.Utc));
        await using var db = OrgTeamTestData.NewDb(databaseName);
        db.PropertyQuesturaCredentials.Add(new PropertyQuesturaCredentials
        {
            OrgId = world.OrgId,
            PropertyId = world.Connected,
            Username = username,
            Password = password,
            WsKey = wsKey,
            LastVerificationOk = false,
        });
        await db.SaveChangesAsync();
        var senderLog = new CapturingLogger<PropertySenderService>();
        var healthLog = new CapturingLogger<ComplianceConnectionHealthService>();
        var harness = new Harness(db, octorate, credentialsConfigured: [world.Connected], senderLog: senderLog, healthLog: healthLog);

        // The healthy read, and the reads that fail or leave members out (the paths that log): none may carry a secret.
        var health = await harness.Health.GetAsync(world.Connected, CancellationToken.None);
        octorate
            .Setup(o => o.GetPoliceAccountAsync(Accommodation, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503, new InvalidOperationException($"upstream said {octorateToken}")));
        var refused = await harness.Health.GetAsync(world.Connected, CancellationToken.None);
        octorate
            .Setup(o => o.GetPoliceAccountAsync(Accommodation, It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.Refused(403));
        var forbidden403 = await harness.Health.GetAsync(world.Connected, CancellationToken.None);
        var many = await harness.Health.GetManyAsync([world.Connected, world.NotConnected], CancellationToken.None);

        var everything = new List<string>
        {
            JsonSerializer.Serialize(ComplianceConnectionHealthDto.From(health)),
            JsonSerializer.Serialize(ComplianceConnectionHealthDto.From(refused)),
            JsonSerializer.Serialize(ComplianceConnectionHealthDto.From(forbidden403)),
            JsonSerializer.Serialize(many.Values.Select(ComplianceConnectionHealthDto.From)),
            senderLog.AllOutput,
            healthLog.AllOutput,
        };
        foreach (var secret in new[] { username, password, wsKey, octorateToken })
            Assert.DoesNotContain(everything, text => text.Contains(secret, StringComparison.Ordinal));

        // The shape says it too: no member of the answer is a credential, a token or a key; the credentials are only said to exist.
        string[] forbidden = ["password", "username", "wskey", "token", "secret", "apikey"];
        foreach (var type in new[] { typeof(ComplianceConnectionHealthDto), typeof(ConnectionHealthLineDto), typeof(ComplianceConnectionHealth), typeof(ConnectionHealthLine) })
        {
            var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name.ToLowerInvariant());
            Assert.DoesNotContain(names, name => forbidden.Any(name.Contains));
        }

        Assert.True(health.CredentialsInCasaZen);
    }

    [Fact]
    public async Task ConnectionHealth_ManyProperties_AreTenantScopedCachedAndReadWithinALimit_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        var world = await SeedAsync(databaseName, extraConnected: 6);
        var foreign = await SeedForeignOrgAsync(databaseName);
        var octorate = new Mock<IOctorateApiClient>();
        octorate
            .Setup(o => o.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePoliceAccount { Valid = true, Autorun = true });
        await using var db = TenantDb(databaseName, world.OrgId);
        var harness = new Harness(db, octorate);
        var ids = world.AllConnected.Append(foreign).ToList();
        Assert.Equal(8, ids.Count);

        // The first call reads at most five properties from Octorate; the others are unknown, not checked yet, never active. The
        // property of another org is not read nor served.
        var first = await harness.Health.GetManyAsync(ids, CancellationToken.None);
        Assert.DoesNotContain(foreign, first.Keys);
        Assert.Equal(7, first.Count);
        octorate.Verify(o => o.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
        var waiting = first.Values.Where(h => h.Alloggiati.Reason == ComplianceConnectionHealthRules.NotCheckedYetReason).ToList();
        Assert.Equal(2, waiting.Count);
        Assert.All(waiting, h => Assert.Equal(ConnectionHealthState.Unknown, h.Alloggiati.State));

        // The second call reuses what it knows and reads the rest; the third reads nothing.
        var second = await harness.Health.GetManyAsync(ids, CancellationToken.None);
        Assert.DoesNotContain(second.Values, h => h.Alloggiati.Reason == ComplianceConnectionHealthRules.NotCheckedYetReason);
        Assert.Equal(7, second.Count);
        var third = await harness.Health.GetManyAsync(ids, CancellationToken.None);
        Assert.Equal(7, third.Count);
        octorate.Verify(o => o.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(7));

        // An empty list is nothing, never "everything".
        Assert.Empty(await harness.Health.GetManyAsync([], CancellationToken.None));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static void Exposes(Mock<IOctorateApiClient> octorate, bool? valid, bool? autorun, DateTime? lastCron) =>
        octorate
            .Setup(o => o.GetPoliceAccountAsync(Accommodation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePoliceAccount { Valid = valid, Autorun = autorun, LastCron = lastCron });

    private static AppDbContext TenantDb(string databaseName, Guid orgId) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, new FixedTenant(orgId));

    /// <summary>The services over one context, with the clock, Octorate and the logs under the test's control.</summary>
    private sealed class Harness
    {
        public Harness(
            AppDbContext db,
            Mock<IOctorateApiClient> octorate,
            IReadOnlyCollection<Guid>? credentialsConfigured = null,
            ILogger<PropertySenderService>? senderLog = null,
            ILogger<ComplianceConnectionHealthService>? healthLog = null)
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
                .ReturnsAsync((Guid id, CancellationToken _) => credentialsConfigured?.Contains(id) == true
                    ? new QuesturaCredentialsStatus(true, Start.UtcDateTime)
                    : QuesturaCredentialsStatus.NotConfigured);
            var activity = new ActivityLog(db, Clock);
            Sender = new PropertySenderService(
                db,
                new PropertySenderStore(db, activity, environment.Object, Clock),
                new AlloggiatiDelegationService(db, activity, Clock),
                questura.Object,
                octorate.Object,
                scope.Object,
                flags.Object,
                environment.Object,
                senderLog ?? NullLogger<PropertySenderService>.Instance,
                Clock);
            Health = new ComplianceConnectionHealthService(
                Sender,
                db,
                new MemoryCache(new MemoryCacheOptions()),
                healthLog ?? NullLogger<ComplianceConnectionHealthService>.Instance,
                Clock);
        }

        public FakeTimeProvider Clock { get; } = new(Start);

        public PropertySenderService Sender { get; }

        public ComplianceConnectionHealthService Health { get; }
    }

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    /// <summary>One org with a property confirmed on a connected Octorate account (the one of <see cref="Accommodation"/>), one that is not, one in Switzerland.</summary>
    private static async Task<World> SeedAsync(string databaseName, int extraConnected = 0)
    {
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var org = OrgTeamTestData.AddOrg(db);
        var connected = NewProperty(org.Id, "IT", 1);
        var notConnected = NewProperty(org.Id, "IT", 2);
        var swiss = NewProperty(org.Id, "CH", 3);
        db.Properties.AddRange(connected, notConnected, swiss);

        var connection = new OctorateConnection { OrgId = org.Id, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        db.OctorateConnections.Add(connection);
        db.OctorateListingMappings.Add(Mapping(org.Id, connection.Id, Accommodation, connected.Id));

        var others = new List<Guid>();
        for (var i = 0; i < extraConnected; i++)
        {
            var property = NewProperty(org.Id, "IT", 10 + i);
            db.Properties.Add(property);
            db.OctorateListingMappings.Add(Mapping(org.Id, connection.Id, $"9000{i}", property.Id));
            others.Add(property.Id);
        }

        await db.SaveChangesAsync();
        return new World(org.Id, connected.Id, notConnected.Id, swiss.Id, [connected.Id, .. others]);
    }

    /// <summary>A property of another org, with a confirmed listing like the others: the caller must never read it.</summary>
    private static async Task<Guid> SeedForeignOrgAsync(string databaseName)
    {
        await using var db = OrgTeamTestData.NewDb(databaseName);
        var org = OrgTeamTestData.AddOrg(db);
        var property = NewProperty(org.Id, "IT", 99);
        db.Properties.Add(property);
        var connection = new OctorateConnection { OrgId = org.Id, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        db.OctorateConnections.Add(connection);
        db.OctorateListingMappings.Add(Mapping(org.Id, connection.Id, "777000", property.Id));
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static OctorateListingMapping Mapping(Guid orgId, Guid connectionId, string accommodationId, Guid propertyId) =>
        new()
        {
            OrgId = orgId,
            ConnectionId = connectionId,
            OctorateAccommodationId = accommodationId,
            PropertyId = propertyId,
            Status = OctorateMappingStatus.Confirmed,
        };

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

    private sealed record World(Guid OrgId, Guid Connected, Guid NotConnected, Guid Swiss, IReadOnlyList<Guid> AllConnected);
}
