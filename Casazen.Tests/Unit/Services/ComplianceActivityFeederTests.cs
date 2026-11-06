using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The feed of the compliance cockpit into the unified "To do" (E03/F02/US01/T03, NF-52, AC-NF52-13, ADR-026): one To do for each red or yellow item,
/// with the action and the id of the cockpit item, deduplicated by (action, target, period), closed when the item turns green or no longer applies;
/// only against what the evaluation looked at; and the operational items of the cockpit left to their own producers.
/// </summary>
public class ComplianceActivityFeederTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Feed_RaisesOneTodoForEachItemThatNeedsAttention_WithTheActionAndTheIdOfTheCockpit()
    {
        var world = await FeedWorld.SeedAsync();
        var deadline = Start.UtcDateTime.AddHours(5);
        var stay = Guid.NewGuid();
        var entrusted = Guid.NewGuid();
        var manual = Guid.NewGuid();
        var identity = Guid.NewGuid();
        var done = Guid.NewGuid();
        var items = new[]
        {
            Stay(world.Property, stay, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.DeadlineOverdue), deadline),
            Stay(world.Property, entrusted, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.EntrustedToChannelManager)),
            Stay(world.Property, manual, ComplianceCockpitAction.SendAlloggiatiManually, Red(ComplianceReasonCodes.OverThirtyNightsNotSent)),
            Stay(world.Property, identity, ComplianceCockpitAction.VerifyIdentity, Red(ComplianceReasonCodes.IdentityMissing), kind: ComplianceItemKind.Identity),
            Stay(world.Property, done, ComplianceCockpitAction.UploadAlloggiatiReceipt, Green()),
            Property(world.Property, ComplianceCockpitAction.ActivateOctorateSend, ComplianceItemKind.Connection, Red(ComplianceReasonCodes.ConnectionInactive)),
            Property(world.Property, ComplianceCockpitAction.RenewPoliceCredentials, ComplianceItemKind.Credentials, Red(ComplianceReasonCodes.CredentialsRefused)),
            Property(world.Property, ComplianceCockpitAction.EnterMunicipalityTaxRate, ComplianceItemKind.MunicipalityRate, Red(ComplianceReasonCodes.MunicipalityRateMissing)),
            Property(world.Property, ComplianceCockpitAction.UploadIstatProof, ComplianceItemKind.Istat, Yellow(ComplianceReasonCodes.PeriodOpen), period: "2026-10"),
            Property(world.Property, ComplianceCockpitAction.DeclareTouristTax, ComplianceItemKind.TouristTax, Red(ComplianceReasonCodes.EvidenceMissing), period: "2026-10"),
            Property(world.Property, ComplianceCockpitAction.DeclareTouristTax, ComplianceItemKind.TouristTax, Red(ComplianceReasonCodes.EvidenceMissing), period: "2026-09"),
            // The operational items of the cockpit keep their own producers.
            Property(world.Property, ComplianceCockpitAction.ActivateProperty, ComplianceItemKind.Cin, Red(ComplianceReasonCodes.CinMissing)),
        };
        var status = Result(items, bookings: [stay, entrusted, manual, done], identity: [identity], properties: [world.Property.Id], periods: ["2026-09", "2026-10"]);

        await using var db = world.Db();
        await world.Feeder(db).FeedAsync(world.Scope, status, CancellationToken.None);

        var todos = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        Assert.Equal(10, todos.Count);
        Assert.All(todos, t => Assert.Equal<(Guid?, Guid, string)>((world.Property.Id, world.Org.Id, InternalActivity.StatusPending), (t.PropertyId, t.OrgId, t.Status)));

        T Row<T>(Func<InternalActivity, bool> where, Func<InternalActivity, T> pick) => pick(Assert.Single(todos, t => where(t)));

        // The Alloggiati communication to send: the page of the proofs, a high urgency, the deadline and the reason of the item.
        var send = Assert.Single(todos, t => t.EntityId == stay);
        Assert.Equal((ActivityType.AlloggiatiDeadline, ActivityAction.UploadAlloggiatiProof, InternalActivity.BookingEntityType, ActivityUrgency.High, deadline, ComplianceReasonCodes.DeadlineOverdue),
            (Enum.Parse<ActivityType>(send.ActivityType), send.Action!.Value, send.EntityType!, send.Urgency!.Value, send.Deadline!.Value, send.Reason!));
        // Yellow is medium; the receipt to upload has the type the alert stages use; the stay entrusted to the channel manager is a To do too.
        var receipt = Assert.Single(todos, t => t.EntityId == entrusted);
        Assert.Equal((ActivityType.ReceiptToRetrieve.ToString(), ActivityUrgency.Medium), (receipt.ActivityType, receipt.Urgency!.Value));
        Assert.Equal(ActivityAction.SendAlloggiatiManually, Row(t => t.EntityId == manual, t => t.Action!.Value));
        Assert.Equal(ActivityAction.VerifyIdentity, Row(t => t.EntityId == identity, t => t.Action!.Value));
        Assert.DoesNotContain(todos, t => t.EntityId == done);

        // A property item is about the property.
        Assert.Equal(
            (ActivityType.ReconnectChannelManager.ToString(), ActivityAction.ActivateOctorateSend, "Property"),
            Row(t => t.Action == ActivityAction.ActivateOctorateSend, t => (t.ActivityType, t.Action!.Value, t.EntityType!)));
        Assert.Equal(ActivityAction.RenewPoliceCredentials, Row(t => t.ActivityType == nameof(ActivityType.CredentialsToRenew), t => t.Action!.Value));
        Assert.Equal(ActivityAction.EnterMunicipalityTaxRate, Row(t => t.ActivityType == nameof(ActivityType.MunicipalityTaxRateMissing), t => t.Action!.Value));

        // An item of a month is about the property and carries the month: (action, property, month), one for each month.
        var istat = Assert.Single(todos, t => t.Action == ActivityAction.UploadIstatProof);
        Assert.Equal(("PropertyPeriod:2026-10", world.Property.Id), (istat.EntityType, istat.EntityId));
        var taxes = todos.Where(t => t.Action == ActivityAction.DeclareTouristTax).OrderBy(t => t.EntityType, StringComparer.Ordinal).ToList();
        Assert.Equal(["PropertyPeriod:2026-09", "PropertyPeriod:2026-10"], taxes.Select(t => t.EntityType));
        Assert.All(taxes, t => Assert.Equal(world.Property.Id, t.EntityId));

        // The CIN is not one of the items that feed the list.
        Assert.DoesNotContain(todos, t => t.Action == ActivityAction.ResumeWizard);
    }

    [Fact]
    public async Task Feed_IsIdempotent_AndAYellowThatTurnsRedIsStillOneTodo()
    {
        var world = await FeedWorld.SeedAsync();
        var stay = Guid.NewGuid();
        var yellow = Result([Stay(world.Property, stay, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.AwaitingReceipt))], bookings: [stay]);
        var red = Result([Stay(world.Property, stay, ComplianceCockpitAction.UploadAlloggiatiReceipt, Red(ComplianceReasonCodes.ReceiptMissing))], bookings: [stay]);

        await using var db = world.Db();
        await world.Feeder(db).FeedAsync(world.Scope, yellow, CancellationToken.None);
        await world.Feeder(db).FeedAsync(world.Scope, yellow, CancellationToken.None);
        await world.Feeder(db).FeedAsync(world.Scope, red, CancellationToken.None);

        var todos = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        var only = Assert.Single(todos);
        Assert.Equal((stay, InternalActivity.StatusPending), (only.EntityId, only.Status));
        Assert.Null(only.CompletedAt);
    }

    [Fact]
    public async Task Feed_ClosesATodoWhenItsItemTurnsGreenOrNoLongerApplies_AndOnlyWithinWhatWasLookedAt()
    {
        var world = await FeedWorld.SeedAsync();
        var turnsGreen = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var stays = Guid.NewGuid();
        var outside = Guid.NewGuid();
        var raisedBySomeoneElse = Guid.NewGuid();
        var first = Result(
            [
                Stay(world.Property, turnsGreen, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent)),
                Stay(world.Property, gone, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent)),
                Stay(world.Property, stays, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.AwaitingReceipt)),
                Property(world.Property, ComplianceCockpitAction.ActivateOctorateSend, ComplianceItemKind.Connection, Red(ComplianceReasonCodes.ConnectionInactive)),
                Property(world.Property, ComplianceCockpitAction.DeclareTouristTax, ComplianceItemKind.TouristTax, Red(ComplianceReasonCodes.EvidenceMissing), period: "2026-10"),
            ],
            bookings: [turnsGreen, gone, stays],
            properties: [world.Property.Id],
            periods: ["2026-10"]);

        await using var db = world.Db();
        await world.Feeder(db).FeedAsync(world.Scope, first, CancellationToken.None);

        // A To do the alert stages raised for a stay this evaluation did not look at (older than the lookback), and one another producer raised for
        // a stay it did look at, with the same action: the second is the item's To do, the first is nobody's business here.
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock);
        await activities.CreateAsync(Todo(world.Property, ActivityType.ReceiptToRetrieve, ActivityAction.UploadAlloggiatiProof, outside), CancellationToken.None);
        await activities.CreateAsync(Todo(world.Property, ActivityType.ReceiptToRetrieve, ActivityAction.UploadAlloggiatiProof, raisedBySomeoneElse), CancellationToken.None);

        // Later: the first stay turns green, the second is gone (not listed any more), the receipt of the third is still awaited, the connection
        // is no longer an item (active), the tax of October is green; the stay of the other producer is in the universe and has no item.
        var second = Result(
            [
                Stay(world.Property, turnsGreen, ComplianceCockpitAction.UploadAlloggiatiReceipt, Green()),
                Stay(world.Property, stays, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.AwaitingReceipt)),
                Property(world.Property, ComplianceCockpitAction.DeclareTouristTax, ComplianceItemKind.TouristTax, Green(), period: "2026-10"),
            ],
            bookings: [turnsGreen, gone, stays, raisedBySomeoneElse],
            properties: [world.Property.Id],
            periods: ["2026-10"]);
        await world.Feeder(db).FeedAsync(world.Scope, second, CancellationToken.None);

        var todos = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        foreach (var closedId in new[] { turnsGreen, gone, raisedBySomeoneElse })
        {
            var closed = Assert.Single(todos, t => t.EntityId == closedId);
            Assert.Equal((InternalActivity.StatusCompleted, InternalActivity.ClosedBySystem), (closed.Status, closed.ClosedBy));
            Assert.NotNull(closed.CompletedAt);
        }

        Assert.Null(Assert.Single(todos, t => t.EntityId == stays).CompletedAt);
        Assert.Null(Assert.Single(todos, t => t.EntityId == outside).CompletedAt);
        Assert.NotNull(Assert.Single(todos, t => t.Action == ActivityAction.ActivateOctorateSend).CompletedAt);
        Assert.NotNull(Assert.Single(todos, t => t.Action == ActivityAction.DeclareTouristTax).CompletedAt);
    }

    [Fact]
    public async Task Feed_AnItemThatChangesWhatItAsksFor_ReplacesItsTodoInTheSameRun()
    {
        var world = await FeedWorld.SeedAsync();
        var stay = Guid.NewGuid();
        var toSend = Result([Stay(world.Property, stay, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent))], bookings: [stay]);
        var byHand = Result([Stay(world.Property, stay, ComplianceCockpitAction.SendAlloggiatiManually, Red(ComplianceReasonCodes.ConnectionInactive))], bookings: [stay]);

        await using var db = world.Db();
        await world.Feeder(db).FeedAsync(world.Scope, toSend, CancellationToken.None);
        await world.Feeder(db).FeedAsync(world.Scope, byHand, CancellationToken.None);

        var todos = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        Assert.Equal(2, todos.Count);
        Assert.NotNull(Assert.Single(todos, t => t.Action == ActivityAction.UploadAlloggiatiProof).CompletedAt);
        var open = Assert.Single(todos, t => t.Action == ActivityAction.SendAlloggiatiManually);
        Assert.Null(open.CompletedAt);
        Assert.Equal(ComplianceReasonCodes.ConnectionInactive, open.Reason);
    }

    [Fact]
    public async Task Feed_AnOrgAndScopeThatWereFedAMomentAgoAreLeftAlone()
    {
        var world = await FeedWorld.SeedAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var cache = new MemoryCache(new MemoryCacheOptions());

        await using var db = world.Db();
        var feeder = new ComplianceActivityFeeder(
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock), cache, NullLogger<ComplianceActivityFeeder>.Instance);
        await feeder.FeedAsync(world.Scope, Result([Stay(world.Property, first, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent))], bookings: [first]), CancellationToken.None);
        await feeder.FeedAsync(world.Scope, Result([Stay(world.Property, second, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent))], bookings: [second]), CancellationToken.None);

        Assert.Equal([first], (await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync()).Select(t => t.EntityId!.Value));

        // Another scope of the same org is a read of its own.
        var other = world.Scope with { GrantedToUserId = "auth0|collaborator" };
        await feeder.FeedAsync(other, Result([Stay(world.Property, second, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent))], bookings: [second]), CancellationToken.None);
        Assert.Equal(2, await db.InternalActivities.CountAsync(a => a.Action != null));
    }

    [Fact]
    public void FedActions_AndTheOnesLeftToTheirOwnProducers_AreEveryActionOfTheCockpit()
    {
        var fed = ComplianceActivityFeeder.FedActions;
        var notFed = ComplianceActivityFeeder.NotFedActions;

        Assert.Empty(fed.Intersect(notFed));
        Assert.Equal(Enum.GetValues<ComplianceCockpitAction>().Order(), fed.Concat(notFed).Order());
        Assert.Equal(
            new[]
            {
                ComplianceCockpitAction.ActivateProperty, ComplianceCockpitAction.CompleteGuestCheckIn,
                ComplianceCockpitAction.CheckOut, ComplianceCockpitAction.ConfirmPropertyReady,
            }.Order(),
            notFed.Order());
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static ComplianceVerdict Red(string reason) => ComplianceVerdict.ActionNeeded(reason);

    private static ComplianceVerdict Yellow(string reason) => ComplianceVerdict.InProgress(reason);

    private static ComplianceVerdict Green() =>
        ComplianceVerdict.Proven(new ComplianceProof("manual-proof:test", ComplianceEvidenceSources.ManualUpload), ComplianceReasonCodes.ReceiptArchived);

    private static ComplianceStatusItem Stay(
        Property property,
        Guid bookingId,
        ComplianceCockpitAction action,
        ComplianceVerdict verdict,
        DateTime? dueAt = null,
        ComplianceItemKind kind = ComplianceItemKind.Alloggiati) =>
        new(kind, action, property.Id, bookingId, null, "Anna Verdi", verdict, dueAt);

    private static ComplianceStatusItem Property(
        Property property,
        ComplianceCockpitAction action,
        ComplianceItemKind kind,
        ComplianceVerdict verdict,
        string? period = null) =>
        new(kind, action, property.Id, null, period, property.Name, verdict, null);

    private static ComplianceStatusResult Result(
        IReadOnlyList<ComplianceStatusItem> items,
        Guid[]? bookings = null,
        Guid[]? identity = null,
        Guid[]? properties = null,
        string[]? periods = null) =>
        new(
            items,
            new ComplianceEvaluatedUniverse(
                (bookings ?? []).ToHashSet(),
                (identity ?? []).ToHashSet(),
                (properties ?? []).ToHashSet(),
                (periods ?? []).ToHashSet(StringComparer.Ordinal)));

    private static CreateTodoRequest Todo(Property property, ActivityType type, ActivityAction action, Guid bookingId) =>
        new()
        {
            Type = type,
            Action = action,
            EntityType = InternalActivity.BookingEntityType,
            EntityId = bookingId,
            Urgency = ActivityUrgency.High,
            PropertyId = property.Id,
        };

    private sealed class FeedWorld
    {
        private readonly string _database = Guid.NewGuid().ToString();

        public required OrgEntity Org { get; init; }

        public required Property Property { get; init; }

        public FakeTimeProvider Clock { get; } = new(Start);

        public HostScope Scope => new(Org.Id);

        public AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

        public ComplianceActivityFeeder Feeder(AppDbContext db) =>
            new(
                new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock),
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<ComplianceActivityFeeder>.Instance);

        public static async Task<FeedWorld> SeedAsync()
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var org = HostScopeScenario.NewOrg("Host", $"host-{suffix}");
            var owner = HostScopeScenario.NewUser($"auth0|titolare-{suffix}", org.Id, UserRole.PropertyOwner);
            var property = HostScopeScenario.NewProperty(org.Id, owner.Id, "Trullo");
            var world = new FeedWorld { Org = org, Property = property };
            await using var db = world.Db();
            db.Orgs.Add(org);
            db.Users.Add(owner);
            db.Properties.Add(property);
            await db.SaveChangesAsync();
            return world;
        }
    }
}
