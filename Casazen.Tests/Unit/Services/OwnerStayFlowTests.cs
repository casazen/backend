using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F07/US01/T01 (NF-111): the privacy-safe owner calendar and the governed owner stays. The three proof tests of the brief are the privacy rule of the
/// calendar (AC-NF111-1), the data rule of the rules and of the approval before any block (AC-NF111-2, AC 3) and the permission rule between owners
/// (ADR-011-R2). EF InMemory, the real calendar blocks over the real recorder of the calendar changes, the channel manager mocked.
/// </summary>
public class OwnerStayFlowTests
{
    // 9 October 2026, 10:00 UTC: the 9th in Rome.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 9);
    private const string Agent = "auth0|agenzia";

    /// <summary>
    /// PROOF TEST: privacy rule (AC-NF111-1). The calendar of the owner says when a guest is there, never who: no name, e-mail, phone, booking id, channel
    /// or note reaches the answer, whose ranges carry dates and a neutral kind only; another owner gets the same 404 as a missing property, and the days
    /// before the ownership began are not shown.
    /// </summary>
    [Fact]
    public async Task OwnerCalendar_ResponseContainsNoGuestIdentity_Test()
    {
        var w = await World.CreateAsync();
        var service = w.Service();

        var calendar = await service.GetCalendarAsync(await w.AccessAsync(w.UserA), w.PropertyA, Today, Today.AddDays(30));

        var json = JsonSerializer.Serialize(calendar);
        foreach (var secret in new[] { "Mario", "Rossi", "mario.rossi@example.com", "+39 333 1234567", w.BookingId.ToString(), "Famiglia Bianchi", "Airbnb" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);

        // Dates and a neutral kind only: the shape of a range has no field a guest could sit in.
        Assert.Equal(
            ["End", "Kind", "Start", "StayId", "StayState"],
            typeof(OwnerCalendarRange).GetProperties().Select(p => p.Name).Order());
        Assert.Contains(new OwnerCalendarRange(Today.AddDays(5), Today.AddDays(8), OwnerCalendarKinds.Occupied), calendar.Ranges);
        Assert.Contains(new OwnerCalendarRange(Today.AddDays(20), Today.AddDays(22), OwnerCalendarKinds.Blocked), calendar.Ranges);
        Assert.Equal(new OwnerCalendarRange(Today, Today.AddDays(5), OwnerCalendarKinds.Available), calendar.Ranges[0]);

        // Another owner gets what a missing property gets.
        var accessB = await w.AccessAsync(w.UserB);
        var other = await Assert.ThrowsAsync<NotFoundException>(() => service.GetCalendarAsync(accessB, w.PropertyA, null, null));
        Assert.Equal(OwnerPortalErrors.PropertyNotFound, other.Code);

        // An ownership that began ten days ago: the days before are not the owner's and are not shown.
        await w.MoveStartOfOwnershipAsync(w.UserA, Today.AddDays(-10));
        var cut = await service.GetCalendarAsync(await w.AccessAsync(w.UserA), w.PropertyA, Today.AddDays(-30), Today.AddDays(30));
        Assert.Equal(Today.AddDays(-10), cut.From);
        Assert.DoesNotContain(cut.Ranges, r => r.Start < Today.AddDays(-10));
    }

    /// <summary>
    /// PROOF TEST: data rule (AC-NF111-2, AC 3). A request that breaks the rule of the property (the shortest stay, the notice, the blocked dates, the
    /// nights of the year) or takes a night of a guest is refused and stores nothing; one that obeys it blocks nothing until the agency approves; the
    /// approval creates ONE block with the id of the stay (a second approval finds it), and the stay reads «confirmed» only once the channel manager
    /// accepted the block. No guest booking is ever made, a host cannot delete the block by hand, and the cancellation gives back that block and no other.
    /// </summary>
    [Fact]
    public async Task OwnerStay_RulesAndApprovalGateAvailabilityBlock_Test()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        await service.SaveRuleAsync(
            w.OrgId,
            new OwnerStayRuleInput(w.PropertyA, null, 10, 3, 120, 2, 14, true, null, null, [new OwnerStayBlackout(Today.AddDays(40), Today.AddDays(45))]),
            Agent);
        var access = await w.AccessAsync(w.UserA);
        var bookings = await w.Db.Bookings.CountAsync();

        async Task<string> RefusedCode(DateOnly start, DateOnly end) =>
            (await Assert.ThrowsAnyAsync<DomainException>(() => service.RequestAsync(access, new OwnerStayRequestCommand(w.PropertyA, start, end)))).Code;

        Assert.Equal(OwnerStayErrors.TooShort, await RefusedCode(Today.AddDays(10), Today.AddDays(11)));
        Assert.Equal(OwnerStayErrors.NoticeTooShort, await RefusedCode(Today.AddDays(1), Today.AddDays(4)));
        Assert.Equal(OwnerStayErrors.Blackout, await RefusedCode(Today.AddDays(44), Today.AddDays(47)));
        Assert.Equal(OwnerStayErrors.DatesUnavailable, await RefusedCode(Today.AddDays(6), Today.AddDays(9)));
        Assert.Equal(0, await w.Db.OwnerStayRequests.CountAsync());

        var stay = await service.RequestAsync(access, new OwnerStayRequestCommand(w.PropertyA, Today.AddDays(10), Today.AddDays(18)));
        Assert.Equal((OwnerStayStates.Requested, 8), (stay.State, stay.Nights));
        Assert.False(await w.Db.CalendarBlocks.AnyAsync(b => b.ManualReason == CalendarBlockReason.Owner));

        // 8 nights held this year: 3 more would pass the 10 of the rule.
        Assert.Equal(OwnerStayErrors.AllowanceExceeded, await RefusedCode(Today.AddDays(25), Today.AddDays(28)));

        var approved = await service.ApproveAsync(w.OrgId, stay.Id, Agent);
        Assert.Equal(OwnerStayStates.ApprovedSyncing, approved.Stay.State);
        var block = await w.Db.CalendarBlocks.AsNoTracking().SingleAsync(b => b.ManualReason == CalendarBlockReason.Owner);
        Assert.Equal((stay.Id, Today.AddDays(10), Today.AddDays(18)), (block.Id, DateOnly.FromDateTime(block.StartUtc), DateOnly.FromDateTime(block.EndUtc)));

        // A second approval finds it done: still one block.
        Assert.Equal(OwnerStayStates.ApprovedSyncing, (await service.ApproveAsync(w.OrgId, stay.Id, Agent)).Stay.State);
        Assert.Equal(1, await w.Db.CalendarBlocks.CountAsync(b => b.ManualReason == CalendarBlockReason.Owner));

        // Confirmed only once the channel manager accepted the block.
        await w.SetProcessAsync(approved.Stay.Id, CalendarChangeState.Sent);
        Assert.Equal(OwnerStayStates.Confirmed, (await service.ListForOwnerAsync(access)).Single(s => s.Id == stay.Id).State);

        // Not a guest booking, and the block goes away only with the stay.
        Assert.Equal(bookings, await w.Db.Bookings.CountAsync());
        var byHand = await Assert.ThrowsAsync<DomainRuleException>(() => w.Blocks().DeleteAsync(block.Id));
        Assert.Equal(ManualBlockErrorCodes.HeldByOwnerStay, byHand.Code);

        // The cancellation gives back the block of the stay and nothing else.
        var maintenance = await w.Blocks().CreateAsync(new ManualBlockRequest(
            w.PropertyA, Today.AddDays(50).ToDateTime(TimeOnly.MinValue), Today.AddDays(52).ToDateTime(TimeOnly.MinValue), CalendarBlockReason.Maintenance));
        var cancelled = await service.CancelByAgencyAsync(w.OrgId, stay.Id, Agent);
        Assert.Equal(OwnerStayStates.Cancelled, cancelled.Stay.State);
        Assert.False(await w.Db.CalendarBlocks.AnyAsync(b => b.Id == block.Id));
        Assert.True(await w.Db.CalendarBlocks.AnyAsync(b => b.Id == maintenance.Id));
        var again = await service.CancelByAgencyAsync(w.OrgId, stay.Id, Agent);
        Assert.Equal((OwnerStayStates.Cancelled, 0), (again.Stay.State, await w.Db.CalendarBlocks.CountAsync(b => b.ManualReason == CalendarBlockReason.Owner)));

        // Every step is a line of the activity log, codes only.
        var lines = await w.Db.OrgActivityEntries.AsNoTracking().Where(e => e.SubjectId == stay.Id.ToString()).Select(e => e.DetailsJson).ToListAsync();
        foreach (var status in new[] { "requested", "approved", "cancelled", "released" })
            Assert.Contains(lines, line => line.Contains($"\"status\":\"{status}\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// PROOF TEST: permission rule (ADR-011-R2). An owner never sees, withdraws or asks on another owner's property: the stays of another owner are not in
    /// its list, and its withdrawal and its request answer the same 404 as a missing stay or property; another agency cannot decide the stay; an owner
    /// whose ownership ended loses the access at the next call, and the stay stays for the audit.
    /// </summary>
    [Fact]
    public async Task OwnerStay_OtherOwnerCannotReadOrCancel_Test()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var stayOfA = await service.RequestAsync(await w.AccessAsync(w.UserA), new OwnerStayRequestCommand(w.PropertyA, Today.AddDays(10), Today.AddDays(12)));
        var accessB = await w.AccessAsync(w.UserB);

        Assert.DoesNotContain(await service.ListForOwnerAsync(accessB), s => s.Id == stayOfA.Id);
        Assert.Equal(OwnerStayErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => service.CancelByOwnerAsync(accessB, stayOfA.Id))).Code);
        Assert.Equal(
            OwnerPortalErrors.PropertyNotFound,
            (await Assert.ThrowsAsync<NotFoundException>(() => service.RequestAsync(accessB, new OwnerStayRequestCommand(w.PropertyA, Today.AddDays(20), Today.AddDays(22))))).Code);
        Assert.Equal(OwnerStayErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => service.ApproveAsync(w.OtherOrgId, stayOfA.Id, Agent))).Code);
        Assert.Equal(OwnerStayStatus.Requested, (await w.Db.OwnerStayRequests.AsNoTracking().SingleAsync(s => s.Id == stayOfA.Id)).Status);

        // The ownership of A ends: the next call has nothing, the stay is kept.
        await w.EndOwnershipAsync(w.UserA);
        var after = await w.AccessAsync(w.UserA);
        Assert.Empty(after.Grants);
        Assert.Equal(OwnerPortalErrors.PermissionMissing, (await Assert.ThrowsAsync<DomainForbiddenException>(() => service.ListForOwnerAsync(after))).Code);
        Assert.True(await w.Db.OwnerStayRequests.AnyAsync(s => s.Id == stayOfA.Id));
    }

    [Fact]
    public async Task OwnerStay_ARuleWithoutApproval_IsApprovedInTheBackground_AndARefusedBlockIsAToDoUntilAcknowledged()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        await service.SaveRuleAsync(w.OrgId, new OwnerStayRuleInput(w.PropertyA, null, null, 0, null, 1, null, false, null, null, null), Agent);

        var stay = await service.RequestAsync(await w.AccessAsync(w.UserA), new OwnerStayRequestCommand(w.PropertyA, Today.AddDays(30), Today.AddDays(33)));
        Assert.Equal(OwnerStayStates.Requested, stay.State);
        Assert.False(await w.Db.CalendarBlocks.AnyAsync(b => b.Id == stay.Id));

        // The background work approves it and blocks the dates; the channel manager refuses the block: «sync_failed», a «To do» of the agency.
        await service.ProcessAsync(stay.Id);
        Assert.True(await w.Db.CalendarBlocks.AnyAsync(b => b.Id == stay.Id));
        await w.SetProcessAsync(stay.Id, CalendarChangeState.Failed, "octorate_unavailable");
        var run = await service.ReconcileAsync();
        Assert.Equal((1, 1), (run.SyncChanges, run.TodosRaised));
        var failed = (await service.ListForAgencyAsync(w.OrgId, OwnerStayStatus.Approved, null)).Single();
        Assert.Equal((OwnerStayStates.SyncFailed, "octorate_unavailable"), (failed.Stay.State, failed.Stay.SyncErrorCode));
        Assert.True(await w.Db.InternalActivities.AnyAsync(a => a.EntityId == stay.Id && a.ActivityType == nameof(ActivityType.OwnerStaySyncFailed) && a.CompletedAt == null));

        // Accepted at the next attempt: confirmed, the «To do» closed.
        await w.SetProcessAsync(stay.Id, CalendarChangeState.Sent);
        await service.ReconcileAsync();
        Assert.Equal(OwnerStayStates.Confirmed, (await service.ListForAgencyAsync(w.OrgId, null, null)).Single().Stay.State);
        Assert.False(await w.Db.InternalActivities.AnyAsync(a => a.EntityId == stay.Id && a.CompletedAt == null));
    }

    [Fact]
    public async Task OwnerStay_TheToDoOfARefusedBlockClosesWhenTheStayIsOver_AndTheStaysOfADeletedPropertyAreLeftAlone()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        await service.SaveRuleAsync(w.OrgId, new OwnerStayRuleInput(w.PropertyA, null, null, 0, null, 1, null, false, null, null, null), Agent);
        var access = await w.AccessAsync(w.UserA);
        var stay = await service.RequestAsync(access, new OwnerStayRequestCommand(w.PropertyA, Today.AddDays(30), Today.AddDays(33)));
        await service.ProcessAsync(stay.Id);
        await w.SetProcessAsync(stay.Id, CalendarChangeState.Failed, "octorate_unavailable");
        Assert.Equal(1, (await service.ReconcileAsync()).TodosRaised);

        // The dates pass with the block still refused: nothing is left to fix, the «To do» is closed and not raised again.
        await w.MoveStayAsync(stay.Id, Today.AddDays(-5), Today.AddDays(-2));
        var over = await service.ReconcileAsync();
        Assert.Equal((0, 1), (over.TodosRaised, over.TodosClosed));
        Assert.False(await w.Db.InternalActivities.AnyAsync(a => a.EntityId == stay.Id && a.CompletedAt == null));

        // The property is deleted with a request waiting: the request is left as it is and the run does not fail.
        var waiting = await service.RequestAsync(access, new OwnerStayRequestCommand(w.PropertyA, Today.AddDays(40), Today.AddDays(42)));
        await w.DeletePropertyAsync(w.PropertyA);
        Assert.Equal(new OwnerStayReconcileResult(0, 0, 0, 0, 0), await service.ReconcileAsync());
        Assert.Equal(OwnerStayStatus.Requested, (await w.Db.OwnerStayRequests.AsNoTracking().SingleAsync(s => s.Id == waiting.Id)).Status);
    }

    [Fact]
    public void OwnerStayRules_TheNightsOfAStayCountInTheirOwnYear_AndTheAllowanceIsPerYear()
    {
        var nights = OwnerStayRules.NightsByYear(new DateOnly(2026, 12, 29), new DateOnly(2027, 1, 3));

        Assert.Equal(new Dictionary<int, int> { [2026] = 3, [2027] = 2 }, nights);
        OwnerStayRules.EnsureAllowance(3, new Dictionary<int, int>(), nights);
        var ex = Assert.Throws<DomainRuleException>(() => OwnerStayRules.EnsureAllowance(3, new Dictionary<int, int> { [2027] = 2 }, nights));
        Assert.Equal(OwnerStayErrors.AllowanceExceeded, ex.Code);
        Assert.Equal(OwnerStayStates.ApprovedSyncing, OwnerStayRules.StateOf(OwnerStayStatus.Approved, OwnerStaySyncState.Pending));
        Assert.Equal(OwnerStaySyncState.Failed, OwnerStayRules.SyncOf(true, CalendarChangeState.PartiallySent));
    }

    /// <summary>One agency with two properties, each of one owner with its portal account, a guest stay and a block on the first; and another agency.</summary>
    private sealed class World
    {
        private readonly string _name = $"owner-stays-{Guid.NewGuid():N}";
        private readonly FixedTimeProvider _clock = new(Now);

        private World()
        {
            Db = NewDb();
            Targets.Setup(t => t.LookupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(OctorateTargetLookup.Of(new OctorateBookingTarget("ACC1", "956528")));
            Flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        }

        public AppDbContext Db { get; }

        public Mock<IOctorateBookingTargets> Targets { get; } = new();

        public Mock<IFeatureFlags> Flags { get; } = new();

        public Guid OrgId { get; private set; }

        public Guid OtherOrgId { get; private set; }

        public Guid PropertyA { get; private set; }

        public Guid UserA { get; private set; }

        public Guid UserB { get; private set; }

        public Guid BookingId { get; private set; }

        public static async Task<World> CreateAsync()
        {
            var w = new World();
            await w.SeedAsync();
            return w;
        }

        public async Task<OwnerPortalAccess> AccessAsync(Guid ownerUserId) => await new OwnerPortalAccessResolver(NewDb(), _clock).ResolveAsync(ownerUserId);

        public CalendarBlockService Blocks()
        {
            var recorder = new CalendarChangeRecorder(
                Db, Targets.Object, Flags.Object, Microsoft.Extensions.Options.Options.Create(new OctorateCalendarOptions()), _clock, NullLogger<CalendarChangeRecorder>.Instance);
            return new CalendarBlockService(Db, Mock.Of<ICheckoutHoldExpiryService>(), NullLogger<CalendarBlockService>.Instance, _clock, recorder, Mock.Of<IBackgroundJobClient>());
        }

        public OwnerStayService Service() =>
            new(
                Db,
                Blocks(),
                Mock.Of<ICalendarChangeRunner>(),
                new InternalActivityService(Db, NullLogger<InternalActivityService>.Instance, _clock),
                new ActivityLog(Db, _clock),
                _clock,
                NullLogger<OwnerStayService>.Instance,
                Mock.Of<IBackgroundJobClient>());

        /// <summary>The calendar change of the block of a stay reaches <paramref name="state"/> (what the push of the channel manager writes).</summary>
        public async Task SetProcessAsync(Guid stayId, CalendarChangeState state, string? errorCode = null)
        {
            await using var db = NewDb();
            var processId = await db.OwnerStayRequests.Where(s => s.Id == stayId).Select(s => s.BlockChangeProcessId).SingleAsync();
            var process = await db.CalendarChangeProcesses.SingleAsync(p => p.Id == processId);
            process.State = state;
            process.ErrorCode = errorCode;
            await db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task MoveStayAsync(Guid stayId, DateOnly start, DateOnly end)
        {
            await using var db = NewDb();
            var stay = await db.OwnerStayRequests.SingleAsync(s => s.Id == stayId);
            stay.StartDate = start;
            stay.EndDate = end;
            await db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task DeletePropertyAsync(Guid propertyId)
        {
            await using var db = NewDb();
            var property = await db.Properties.SingleAsync(p => p.Id == propertyId);
            property.IsDeleted = true;
            property.DeletedAt = Now.UtcDateTime;
            await db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task MoveStartOfOwnershipAsync(Guid ownerUserId, DateOnly start)
        {
            await using var db = NewDb();
            var ownership = await db.PropertyOwnerships.SingleAsync(o => o.OwnerUserId == ownerUserId);
            ownership.OwnershipStart = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }

        public async Task EndOwnershipAsync(Guid ownerUserId)
        {
            await using var db = NewDb();
            var ownership = await db.PropertyOwnerships.SingleAsync(o => o.OwnerUserId == ownerUserId);
            ownership.OwnershipEnd = Now.UtcDateTime.AddDays(-1);
            await db.SaveChangesAsync();
        }

        private AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_name).Options);

        private async Task SeedAsync()
        {
            await using var db = NewDb();
            var org = HostScopeScenario.NewOrg("Agenzia Mare", $"mare-{Guid.NewGuid():N}"[..20]);
            var otherOrg = HostScopeScenario.NewOrg("Agenzia Monti", $"monti-{Guid.NewGuid():N}"[..20]);
            db.Orgs.AddRange(org, otherOrg);
            var propertyA = HostScopeScenario.NewProperty(org.Id, "auth0|agenzia", "Casa Alfa");
            var propertyB = HostScopeScenario.NewProperty(org.Id, "auth0|agenzia", "Casa Beta");
            db.Properties.AddRange(propertyA, propertyB);

            var userA = new OwnerUser { Auth0Sub = "auth0|anna", Email = "anna@example.com", FirstName = "Anna", LastName = "Alfa", CreatedAt = Now.UtcDateTime };
            var userB = new OwnerUser { Auth0Sub = "auth0|bruno", Email = "bruno@example.com", FirstName = "Bruno", LastName = "Beta", CreatedAt = Now.UtcDateTime };
            db.OwnerUsers.AddRange(userA, userB);
            foreach (var (user, property) in new[] { (userA, propertyA), (userB, propertyB) })
            {
                var owner = new PropertyOwner { OrgId = org.Id, FirstName = user.FirstName, LastName = user.LastName, Email = user.Email, CreatedAt = Now.UtcDateTime };
                db.PropertyOwners.Add(owner);
                db.PropertyOwnerships.Add(new PropertyOwnership
                {
                    OrgId = org.Id,
                    PropertyId = property.Id,
                    PropertyOwnerId = owner.Id,
                    OwnerUserId = user.Id,
                    SharePercent = 100m,
                    OwnershipStart = Now.UtcDateTime.AddYears(-1),
                });
            }

            // A guest with every kind of personal data, staying from the 14th to the 17th, and a block of a channel with a note.
            var guest = new Guest { OrgId = org.Id, FirstName = "Mario", LastName = "Rossi", Email = "mario.rossi@example.com", PhoneNumber = "+39 333 1234567" };
            db.Guests.Add(guest);
            var booking = new Booking
            {
                OrgId = org.Id,
                PropertyId = propertyA.Id,
                GuestId = guest.Id,
                CheckInDate = Today.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                CheckOutDate = Today.AddDays(8).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Airbnb,
                TotalPrice = 300m,
            };
            db.Bookings.Add(booking);
            db.CalendarBlocks.Add(new CalendarBlock
            {
                OrgId = org.Id,
                PropertyId = propertyA.Id,
                Source = CalendarBlockSource.ICalImport,
                ExternalUid = "uid-1",
                StartUtc = Today.AddDays(20).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                EndUtc = Today.AddDays(22).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Summary = "Famiglia Bianchi",
            });
            await db.SaveChangesAsync();

            (OrgId, OtherOrgId, PropertyA, UserA, UserB, BookingId) = (org.Id, otherOrg.Id, propertyA.Id, userA.Id, userB.Id, booking.Id);
        }
    }
}
