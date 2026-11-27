using System.Globalization;
using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Compliance;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the controlled handoff to the Octorate Cassetto Compliance (E03/F05/US01/T01, CHK-15): the address the host is sent to
/// is never the caller's and always passes the allowlist, a target outside the caller's org or reach is a 404 (permission and data rule),
/// and starting a handoff changes no compliance state or evidence: the one thing it writes is its own line of the activity log
/// (privacy and trust rule).
/// </summary>
public class OctorateComplianceHandoffTests
{
    private const string PanelUrl = "https://admin.octorate.test/octobook";
    private const string ReturnPath = "/app/short-rent/compliance?property=trullo&period=2026-10";

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 30, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);

    [Fact]
    public async Task OctorateComplianceHandoff_RejectsUntrustedDestinationAndOutOfScopeTarget_Test()
    {
        var options = NewOptions();
        await using var seed = new AppDbContext(options);
        var world = await HostScopeScenario.SeedAsync(seed);
        var bookings = await SeedMappingsAsync(seed, world);
        var unmapped = await SeedUnmappedPropertyAsync(seed, world);

        await using var db = new AppDbContext(options);
        var started = 0;

        // The destination is the configuration's, never the caller's. The request has no member that could carry an address.
        var members = new[] { "BookingId", "Period", "PropertyId", "ReturnPath", "Topic" };
        Assert.Equal(members, typeof(OctorateHandoffRequest).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(members, typeof(OctorateComplianceHandoffRequest).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));

        // Nothing configured but the panel: the host is sent to its authenticated home.
        var home = await NewService(db, new OctorateComplianceHandoffOptions())
            .StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Granted, "2026-10"));
        started++;
        Assert.Equal(PanelUrl, home.Destination.Url);
        Assert.Equal(OctorateHandoffDestinationKind.Home, home.Destination.Kind);

        // A deep link counts only when a person verified it, and only as configured, on the allowlist.
        const string deepLink = "https://admin.octorate.test/octobook/pms/alloggiati";
        var verified = await NewService(db, WithAlloggiatiLink(deepLink, verified: true))
            .StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Granted));
        started++;
        Assert.Equal(deepLink, verified.Destination.Url);
        Assert.Equal(OctorateHandoffDestinationKind.DeepLink, verified.Destination.Kind);

        var unverified = await NewService(db, WithAlloggiatiLink(deepLink, verified: false))
            .StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Granted));
        started++;
        Assert.Equal(PanelUrl, unverified.Destination.Url);

        // A link of another host can be allowlisted by configuration only.
        const string otherHostLink = "https://static.octorate.test/help/alloggiati";
        var notListed = await NewService(db, WithAlloggiatiLink(otherHostLink, verified: true))
            .StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Granted));
        started++;
        Assert.Equal(PanelUrl, notListed.Destination.Url);

        var listed = await NewService(db, WithAlloggiatiLink(otherHostLink, verified: true, allowedHosts: "static.octorate.test"))
            .StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Granted));
        started++;
        Assert.Equal(otherHostLink, listed.Destination.Url);

        // A verified link that is not a trusted https address of an allowlisted host is never used: the home is.
        string[] untrusted =
        [
            "https://evil.example/octobook/pms/alloggiati",
            "http://admin.octorate.test/octobook/pms/alloggiati",
            "https://admin.octorate.test@evil.example/octobook",
            "https://admin.octorate.test.evil.example/octobook",
            "https://admin.octorate.test:8443/octobook",
            "https://203.0.113.7/octobook",
            "https://admin.octorate.test\\@evil.example/octobook",
            "//evil.example/octobook",
            "javascript:alert(1)",
            "/octobook/pms/alloggiati",
            "   ",
        ];
        foreach (var link in untrusted)
        {
            var result = await NewService(db, WithAlloggiatiLink(link, verified: true))
                .StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Granted));
            started++;
            Assert.Equal(PanelUrl, result.Destination.Url);
            Assert.Equal(OctorateHandoffDestinationKind.Home, result.Destination.Kind);
        }

        // No panel and no valid link: no destination, and the host stays in CasaZen.
        var unavailable = await Assert.ThrowsAsync<DomainConflictException>(
            () => NewService(db, WithAlloggiatiLink("https://evil.example/", verified: true), panelBaseUrl: null)
                .StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Granted)));
        Assert.Equal(OctorateComplianceHandoffService.DestinationUnavailableCode, unavailable.Code);
        Assert.Equal(
            OctorateHandoffGuides.AlloggiatiDaily,
            Assert.IsType<string>(unavailable.Extensions[OctorateComplianceHandoffService.GuideCodeExtension]));

        // The route to come back to is a relative route of CasaZen: whatever a browser could read as another site is refused, and the
        // route that passes is only echoed, never the destination.
        var service = NewService(db, new OctorateComplianceHandoffOptions());
        string[] notRoutes =
        [
            "https://evil.example/",
            "//evil.example/",
            "/\\evil.example",
            "\\\\evil.example\\share",
            "javascript:alert(1)",
            "app/short-rent/compliance",
            "/app/redirect?to=https://evil.example",
            "/app/short-rent/compliance page",
            "   ",
        ];
        foreach (var path in notRoutes)
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Granted, returnPath: path)));
            Assert.Equal(OctorateComplianceHandoffService.ReturnPathInvalidCode, refused.Code);
        }

        foreach (var period in new[] { "2026-13", "2026-02-30", "26-10", "2026/10", "2026-1-5", "oggi" })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Granted, period: period)));
            Assert.Equal(OctorateComplianceHandoffService.PeriodInvalidCode, refused.Code);
        }

        var echoed = await service.StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Granted));
        started++;
        Assert.Equal(ReturnPath, echoed.ReturnPath);
        Assert.NotEqual(echoed.ReturnPath, echoed.Destination.Url);

        // The org-wide scope reaches the properties of its org, the collaborator «Solo alcuni» only the one it was given.
        var hiddenForOwner = await service.StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Hidden.Id, bookings.Hidden));
        started++;
        Assert.Equal(world.Hidden.Name, hiddenForOwner.PropertyName);
        await service.StartAsync(world.Restricted, world.CollaboratorId, Request(world.Granted.Id, bookings.Granted));
        started++;

        // Everything that is not the caller's answers the same 404: a property out of the reach, another org's property or booking,
        // a booking that is not the property's, an id nobody has.
        await AssertNotFoundAsync(() => service.StartAsync(world.Restricted, world.CollaboratorId, Request(world.Hidden.Id, null)));
        await AssertNotFoundAsync(() => service.StartAsync(world.Restricted, world.CollaboratorId, Request(world.Hidden.Id, bookings.Hidden)));
        await AssertNotFoundAsync(() => service.StartAsync(world.OrgWide, world.OwnerUserId, Request(world.OtherOrgProperty.Id, null)));
        await AssertNotFoundAsync(() => service.StartAsync(world.OrgWide, world.OwnerUserId, Request(world.OtherOrgProperty.Id, bookings.Foreign)));
        await AssertNotFoundAsync(() => service.StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Foreign)));
        await AssertNotFoundAsync(() => service.StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, bookings.Hidden)));
        await AssertNotFoundAsync(() => service.StartAsync(world.Restricted, world.CollaboratorId, Request(world.Granted.Id, bookings.Hidden)));
        await AssertNotFoundAsync(() => service.StartAsync(new HostScope(world.OtherOrgId), "auth0|stranger", Request(world.Granted.Id, bookings.Granted)));
        await AssertNotFoundAsync(() => service.StartAsync(world.OrgWide, world.OwnerUserId, Request(Guid.NewGuid(), null)));
        await AssertNotFoundAsync(() => service.StartAsync(world.OrgWide, world.OwnerUserId, Request(world.Granted.Id, Guid.NewGuid())));

        // A property CasaZen has not connected to Octorate has nothing to check there.
        var notMapped = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.StartAsync(world.OrgWide, world.OwnerUserId, Request(unmapped, null)));
        Assert.Equal(OctorateComplianceHandoffService.PropertyNotMappedCode, notMapped.Code);

        // Only the handoffs that were prepared left a line; every refusal wrote nothing.
        await using var verify = new AppDbContext(options);
        Assert.Equal(started, await verify.OrgActivityEntries.CountAsync());
    }

    [Fact]
    public async Task OctorateComplianceHandoff_NeverMutatesComplianceOrEvidence_Test()
    {
        var options = NewOptions();
        await using (var seed = new AppDbContext(options))
        {
            var seeded = await HostScopeScenario.SeedAsync(seed);
            var seededBookings = await SeedMappingsAsync(seed, seeded);
            seed.AlloggiatiWebReports.Add(new AlloggiatiWebReport
            {
                OrgId = seeded.OrgId,
                BookingId = seededBookings.Granted,
                GuestId = await seed.Bookings.AsNoTracking().Where(b => b.Id == seededBookings.Granted).Select(b => b.GuestId).SingleAsync(),
                Status = AlloggiatiWebStatus.DaInviare,
            });
            await seed.SaveChangesAsync();
        }

        await using var read = new AppDbContext(options);
        var world = await ReadWorldAsync(read);
        var before = await SnapshotAsync(options);

        OctorateHandoff handoff;
        await using (var db = new AppDbContext(options))
        {
            handoff = await NewService(db, new OctorateComplianceHandoffOptions()).StartAsync(
                world.Scope,
                world.OwnerUserId,
                new OctorateHandoffRequest(OctorateHandoffTopic.Alloggiati, world.PropertyId, world.BookingId, "2026-10", ReturnPath));
        }

        var after = await SnapshotAsync(options);

        // The handoff wrote the line of the activity log and nothing else: not a status, a receipt, a proof or any other row of any table.
        var log = typeof(OrgActivityEntry).Name;
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (table, rows) in before.Where(entry => entry.Key != log))
            Assert.True(rows.SequenceEqual(after[table]), $"The handoff changed {table}");

        Assert.Empty(before[log]);
        Assert.Single(after[log]);

        // The line holds the transition and ids only: who, where, what and when, no guest, no credential, no outcome.
        await using var verify = new AppDbContext(options);
        var line = await verify.OrgActivityEntries.AsNoTracking().SingleAsync();
        Assert.Equal(world.Scope.OrgId, line.OrgId);
        Assert.Equal(world.OwnerUserId, line.ActorUserId);
        Assert.Equal(OrgActivityType.OctorateComplianceHandoffStarted, line.Type);
        Assert.Equal(OrgActivityArea.ShortRent, line.Area);
        Assert.Equal(OrgActivitySubjectType.Property, line.SubjectType);
        Assert.Equal(world.PropertyId.ToString(), line.SubjectId);
        Assert.Equal(Now.UtcDateTime, line.When);

        var details = OrgActivityDetails.Parse(line.DetailsJson);
        Assert.Equal(
            new[] { "booking", "handoff", "period", "topic" },
            details.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Alloggiati", details["topic"]);
        Assert.Equal("2026-10", details["period"]);
        Assert.Equal(world.BookingId.ToString(), details["booking"]);
        Assert.Equal(handoff.HandoffId, details["handoff"]);

        var text = string.Join('|', line.ActorUserId, line.SubjectId, line.DetailsJson);
        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Anna", text, StringComparison.Ordinal);
        Assert.DoesNotContain(world.PropertyName, text, StringComparison.Ordinal);
        Assert.DoesNotContain(handoff.Destination.Url, text, StringComparison.Ordinal);

        // What the host is given: an opaque short-lived id, the address from the configuration, a guide, the property and the stay by name and code.
        Assert.Matches("^[0-9a-f]{32}$", handoff.HandoffId);
        Assert.Equal(Now.UtcDateTime, handoff.StartedAt);
        Assert.Equal(Now.UtcDateTime.AddMinutes(OctorateComplianceHandoffOptions.DefaultLifetimeMinutes), handoff.ExpiresAt);
        Assert.Equal(PanelUrl, handoff.Destination.Url);
        Assert.Equal(OctorateHandoffGuides.AlloggiatiDaily, handoff.GuideCode);
        Assert.Equal(world.PropertyName, handoff.PropertyName);
        Assert.Equal("Trullo su Octorate", handoff.OctorateListingName);
        Assert.NotNull(handoff.BookingCode);

        // There is no way to complete the handoff or to report an outcome: one operation in the service, one action in the API.
        Assert.Single(typeof(IOctorateComplianceHandoffService).GetMethods());
        var actions = typeof(OctorateComplianceHandoffController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().Any())
            .ToList();
        var action = Assert.Single(actions);
        Assert.Equal(new[] { "POST" }, Assert.Single(action.GetCustomAttributes<HttpMethodAttribute>()).HttpMethods);
    }

    // ─── Shared set-up ──────────────────────────────────────────────────────────────────────────────────

    private OctorateComplianceHandoffService NewService(
        AppDbContext db,
        OctorateComplianceHandoffOptions handoffOptions,
        string? panelBaseUrl = PanelUrl) =>
        new(
            db,
            new ActivityLog(db, _clock),
            Microsoft.Extensions.Options.Options.Create(handoffOptions),
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions { FrontendBaseUrl = panelBaseUrl }),
            NullLogger<OctorateComplianceHandoffService>.Instance,
            _clock);

    private static OctorateComplianceHandoffOptions WithAlloggiatiLink(string url, bool verified, string? allowedHosts = null) =>
        new()
        {
            AllowedHosts = allowedHosts,
            Alloggiati = new OctorateHandoffLinkOptions { Url = url, Verified = verified },
        };

    private static OctorateHandoffRequest Request(Guid propertyId, Guid? bookingId, string? period = null, string returnPath = ReturnPath) =>
        new(OctorateHandoffTopic.Alloggiati, propertyId, bookingId, period, returnPath);

    private static async Task AssertNotFoundAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(action);
        Assert.Equal(OctorateComplianceHandoffService.TargetNotFoundCode, exception.Code);
        Assert.Equal("OctorateHandoffTargetNotFound", exception.MessageKey);
    }

    private static DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"octorate-handoff-{Guid.NewGuid()}").Options;

    private sealed record Bookings(Guid Granted, Guid Hidden, Guid Foreign);

    private sealed record HandoffWorld(HostScope Scope, string OwnerUserId, Guid PropertyId, string PropertyName, Guid BookingId);

    /// <summary>
    /// The connection and the confirmed mapping of every property of the scenario (two of the org, one of another org) and the
    /// first stay of each of them.
    /// </summary>
    private static async Task<Bookings> SeedMappingsAsync(AppDbContext db, HostScopeWorld world)
    {
        var connection = new OctorateConnection { OrgId = world.OrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        var foreignConnection = new OctorateConnection { OrgId = world.OtherOrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        db.OctorateConnections.AddRange(connection, foreignConnection);
        db.OctorateListingMappings.AddRange(
            Mapping(world.OrgId, connection.Id, "9001", OctorateMappingStatus.Confirmed, world.Granted.Id, "Trullo su Octorate"),
            Mapping(world.OrgId, connection.Id, "9002", OctorateMappingStatus.Confirmed, world.Hidden.Id, "Casa Bianca su Octorate"),
            Mapping(world.OtherOrgId, foreignConnection.Id, "9003", OctorateMappingStatus.Confirmed, world.OtherOrgProperty.Id, "Villa Altrove su Octorate"));
        await db.SaveChangesAsync();

        return new Bookings(
            await FirstBookingOfAsync(db, world.Granted.Id),
            await FirstBookingOfAsync(db, world.Hidden.Id),
            await FirstBookingOfAsync(db, world.OtherOrgProperty.Id));
    }

    /// <summary>A property of the org whose listing was only proposed: not confirmed, so not connected.</summary>
    private static async Task<Guid> SeedUnmappedPropertyAsync(AppDbContext db, HostScopeWorld world)
    {
        var property = HostScopeScenario.NewProperty(world.OrgId, world.OwnerUserId, "Casa Senza Annuncio");
        db.Properties.Add(property);
        var connection = await db.OctorateConnections.AsNoTracking().SingleAsync(c => c.OrgId == world.OrgId);
        db.OctorateListingMappings.Add(Mapping(world.OrgId, connection.Id, "9004", OctorateMappingStatus.Proposed, property.Id, "Casa Senza Annuncio su Octorate"));
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static Task<Guid> FirstBookingOfAsync(AppDbContext db, Guid propertyId) =>
        db.Bookings.AsNoTracking().Where(b => b.PropertyId == propertyId).OrderBy(b => b.CheckInDate).Select(b => b.Id).FirstAsync();

    private static OctorateListingMapping Mapping(
        Guid orgId,
        Guid connectionId,
        string accommodationId,
        OctorateMappingStatus status,
        Guid propertyId,
        string name) =>
        new()
        {
            OrgId = orgId,
            ConnectionId = connectionId,
            OctorateAccommodationId = accommodationId,
            SnapshotName = name,
            Status = status,
            PropertyId = propertyId,
        };

    /// <summary>The owner, the first property of its org and its first stay, as the database holds them after the set-up.</summary>
    private static async Task<HandoffWorld> ReadWorldAsync(AppDbContext db)
    {
        var mapping = await db.OctorateListingMappings.AsNoTracking().OrderBy(m => m.OctorateAccommodationId).FirstAsync();
        var property = await db.Properties.AsNoTracking().SingleAsync(p => p.Id == mapping.PropertyId);
        var bookingId = await FirstBookingOfAsync(db, property.Id);
        return new HandoffWorld(new HostScope(property.OrgId), property.OwnerId, property.Id, property.Name, bookingId);
    }

    /// <summary>
    /// Every row of every table of the model, one string per row: the same text before and after means the call changed nothing there.
    /// </summary>
    private static async Task<SortedDictionary<string, string[]>> SnapshotAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = new AppDbContext(options);
        var set = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;
        var snapshot = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var entityType in db.Model.GetEntityTypes().Where(e => !e.IsOwned() && !e.HasSharedClrType && e.FindPrimaryKey() is not null))
        {
            var rows = ((System.Collections.IEnumerable)set.MakeGenericMethod(entityType.ClrType).Invoke(db, null)!).Cast<object>().ToList();
            snapshot[entityType.ClrType.Name] = [.. rows
                .Select(row => string.Join('|', db.Entry(row).Properties.Select(p => $"{p.Metadata.Name}={Text(p.CurrentValue)}")))
                .Order(StringComparer.Ordinal)];
        }

        return snapshot;
    }

    private static string Text(object? value) => value switch
    {
        null => "<null>",
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime instant => instant.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
