using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F02/US02/T01 (NF-48, ADR-025, ADR-026): what a stage that is sent leaves behind. The popup goes only to the people the resolver says reach the property,
/// and only for a legal deadline within 24 hours; the "To do" is idempotent, is closed when its condition clears, and a stay that owes nothing any more loses
/// its items. EF InMemory, the real <see cref="InternalActivityService"/>.
/// </summary>
public class ComplianceAlertFollowUpTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly Mock<IAlertRouter> _router = new();
    private readonly Mock<IComplianceAlertRecipientResolver> _recipients = new();
    private readonly InternalActivityService _activities;

    public ComplianceAlertFollowUpTests()
    {
        _activities = new InternalActivityService(_db, NullLogger<InternalActivityService>.Instance, new FakeTimeProvider(new DateTimeOffset(Now)));
    }

    public void Dispose() => _db.Dispose();

    private ComplianceAlertFollowUp FollowUp() =>
        new(_db, _activities, _router.Object, _recipients.Object, NullLogger<ComplianceAlertFollowUp>.Instance);

    private async Task<(Guid OrgId, Guid PropertyId)> SeedPropertyAsync()
    {
        var org = OrgTeamTestData.AddOrg(_db);
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
        };
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        return (org.Id, property.Id);
    }

    private async Task<Guid> SeedBookingAsync(Guid orgId, Guid propertyId, BookingStatus status, DateTime? noShowAt = null)
    {
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = "guest@example.com" };
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc),
            Status = status,
            NoShowAt = noShowAt,
        };
        _db.AddRange(guest, booking);
        await _db.SaveChangesAsync();
        return booking.Id;
    }

    private static CreateTodoRequest Todo(Guid propertyId, ActivityType type, Guid bookingId) => new()
    {
        Type = type,
        Action = ActivityAction.UploadAlloggiatiProof,
        EntityType = InternalActivity.BookingEntityType,
        EntityId = bookingId,
        Urgency = ActivityUrgency.High,
        PropertyId = propertyId,
    };

    private void Audience(Guid orgId, Guid propertyId, params string[] userIds) =>
        _recipients
            .Setup(r => r.ForPropertyAsync(orgId, propertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplianceAlertRecipients(["org@example.com"], userIds));

    // ─── The popup ───

    [Fact]
    public async Task PopupAsync_ALegalDeadlineWithinTwentyFourHours_AlertsEachPersonOfTheAudience_AndNobodyElse()
    {
        var (orgId, propertyId) = await SeedPropertyAsync();
        Audience(orgId, propertyId, "auth0|owner", "auth0|collab-granted");
        var bookingId = Guid.NewGuid();
        var deadline = Now.AddHours(10);

        await FollowUp().PopupAsync(
            new ComplianceAlertPopup(orgId, propertyId, AlertType.AlloggiatiDeadline, PopupAlert.BookingEntityType, bookingId, deadline),
            Now,
            CancellationToken.None);

        foreach (var userId in new[] { "auth0|owner", "auth0|collab-granted" })
        {
            _router.Verify(
                r => r.RouteAlertAsync(AlertType.AlloggiatiDeadline, bookingId, "Booking", userId, AlertUrgency.High, deadline, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        _router.Verify(
            r => r.RouteAlertAsync(It.IsAny<AlertType>(), It.IsAny<Guid>(), It.IsAny<string>(), "auth0|collab-none", It.IsAny<AlertUrgency>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _router.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PopupAsync_ADeadlineMoreThanTwentyFourHoursAway_OrNone_RaisesNothingAndReadsNoAudience()
    {
        var (orgId, propertyId) = await SeedPropertyAsync();
        Audience(orgId, propertyId, "auth0|owner");

        await FollowUp().PopupAsync(
            new ComplianceAlertPopup(orgId, propertyId, AlertType.AlloggiatiDeadline, PopupAlert.BookingEntityType, Guid.NewGuid(), Now.AddHours(24).AddMinutes(1)),
            Now,
            CancellationToken.None);
        await FollowUp().PopupAsync(
            new ComplianceAlertPopup(orgId, propertyId, AlertType.AlloggiatiDeadline, PopupAlert.BookingEntityType, Guid.NewGuid(), Now.AddDays(-3)),
            Now,
            CancellationToken.None);
        await FollowUp().PopupAsync(
            new ComplianceAlertPopup(orgId, propertyId, AlertType.ComplianceIssue, PopupAlert.PropertyEntityType, propertyId, null),
            Now,
            CancellationToken.None);

        _router.VerifyNoOtherCalls();
        _recipients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PopupAsync_APersonWhoCannotBeAlerted_DoesNotStopTheOthers_AndTheFailureIsThrown()
    {
        var (orgId, propertyId) = await SeedPropertyAsync();
        Audience(orgId, propertyId, "auth0|first", "auth0|second", "auth0|third");
        _router
            .Setup(r => r.RouteAlertAsync(It.IsAny<AlertType>(), It.IsAny<Guid>(), It.IsAny<string>(), "auth0|second", It.IsAny<AlertUrgency>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => FollowUp().PopupAsync(
            new ComplianceAlertPopup(orgId, propertyId, AlertType.IstatDeadline, PopupAlert.PropertyEntityType, propertyId, Now.AddHours(1)),
            Now,
            CancellationToken.None));

        Assert.Single(thrown.InnerExceptions);
        foreach (var userId in new[] { "auth0|first", "auth0|second", "auth0|third" })
        {
            _router.Verify(
                r => r.RouteAlertAsync(AlertType.IstatDeadline, propertyId, "Property", userId, AlertUrgency.High, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    // ─── The "To do" ───

    [Fact]
    public async Task OpenTodoAsync_KeepsOneItemOpen_AndAPropertyThatIsGoneIsNotAnError()
    {
        var (_, propertyId) = await SeedPropertyAsync();
        var bookingId = Guid.NewGuid();
        var followUp = FollowUp();

        await followUp.OpenTodoAsync(Todo(propertyId, ActivityType.AlloggiatiDeadline, bookingId), CancellationToken.None);
        await followUp.OpenTodoAsync(Todo(propertyId, ActivityType.AlloggiatiDeadline, bookingId), CancellationToken.None);
        await followUp.OpenTodoAsync(Todo(Guid.NewGuid(), ActivityType.AlloggiatiDeadline, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(1, await _db.InternalActivities.CountAsync(a => a.Action != null));
    }

    [Fact]
    public async Task CloseTodosAsync_ClosesOnlyTheOpenItemsOfThoseTypesAboutThoseEntities()
    {
        var (_, propertyId) = await SeedPropertyAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var followUp = FollowUp();
        await followUp.OpenTodoAsync(Todo(propertyId, ActivityType.AlloggiatiDeadline, first), CancellationToken.None);
        await followUp.OpenTodoAsync(Todo(propertyId, ActivityType.ReceiptToRetrieve, first), CancellationToken.None);
        await followUp.OpenTodoAsync(Todo(propertyId, ActivityType.AlloggiatiDeadline, second), CancellationToken.None);

        // An empty list of entities is nothing, never «all of them».
        await followUp.CloseTodosAsync([ActivityType.AlloggiatiDeadline], InternalActivity.BookingEntityType, [], CancellationToken.None);
        Assert.Equal(3, await _db.InternalActivities.CountAsync(a => a.CompletedAt == null && a.Action != null));

        await followUp.CloseTodosAsync([ActivityType.AlloggiatiDeadline], InternalActivity.BookingEntityType, [first], CancellationToken.None);

        var open = await _db.InternalActivities.Where(a => a.Action != null && a.CompletedAt == null).Select(a => new { a.ActivityType, a.EntityId }).ToListAsync();
        Assert.Equal(2, open.Count);
        Assert.Contains(open, a => a.ActivityType == nameof(ActivityType.ReceiptToRetrieve) && a.EntityId == first);
        Assert.Contains(open, a => a.ActivityType == nameof(ActivityType.AlloggiatiDeadline) && a.EntityId == second);
        var closed = await _db.InternalActivities.SingleAsync(a => a.CompletedAt != null);
        Assert.Equal(InternalActivity.ClosedBySystem, closed.ClosedBy);
    }

    [Fact]
    public async Task CloseTodosOfStaysThatOweNothingAsync_ClosesACancelledAndANoShowStay_NotAnActiveOne()
    {
        var (orgId, propertyId) = await SeedPropertyAsync();
        var active = await SeedBookingAsync(orgId, propertyId, BookingStatus.Confirmed);
        var cancelled = await SeedBookingAsync(orgId, propertyId, BookingStatus.Cancelled);
        var noShow = await SeedBookingAsync(orgId, propertyId, BookingStatus.Confirmed, noShowAt: Now);
        var followUp = FollowUp();
        foreach (var bookingId in new[] { active, cancelled, noShow })
            await followUp.OpenTodoAsync(Todo(propertyId, ActivityType.CheckInPending, bookingId), CancellationToken.None);

        await followUp.CloseTodosOfStaysThatOweNothingAsync([ActivityType.CheckInPending], CancellationToken.None);

        var open = await _db.InternalActivities.Where(a => a.Action != null && a.CompletedAt == null).Select(a => a.EntityId).ToListAsync();
        Assert.Equal([active], open);
    }
}
