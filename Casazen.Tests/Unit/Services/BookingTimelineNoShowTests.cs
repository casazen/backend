using Casazen.Core.Entities;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-45 on the history of a booking: a no-show owes no police communication, so its timeline has no «Alloggiati» entry (it would read
/// «to send manually» with a term), while the same stay not marked as a no-show has one.
/// </summary>
public class BookingTimelineNoShowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Timeline_ANoShowHasNoAlloggiatiEntry_WhileTheSameStayNotMarkedHasIt()
    {
        var expected = Stay(noShow: false);
        var noShow = Stay(noShow: true);

        var withEntry = Assert.Single(Build(expected), e => e.Kind == BookingTimelineKind.Alloggiati);
        Assert.Equal(BookingTimelineState.Current, withEntry.State);
        Assert.Equal(AlloggiatiWebStatus.DaInviareManualmente, withEntry.Data.AlloggiatiStatus);
        Assert.DoesNotContain(Build(noShow), e => e.Kind == BookingTimelineKind.Alloggiati);
    }

    private static IReadOnlyList<BookingTimelineEvent> Build(Booking booking) =>
        BookingTimeline.Build(
            booking,
            new BookingTimelineFacts(
                [],
                [],
                new BookingTimelineCheckIn(HostTodayCheckInState.NotSent, null, null),
                new BookingTimelineAlloggiati(null, null)),
            Now,
            includeAmounts: false);

    private static Booking Stay(bool noShow) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = Guid.NewGuid(),
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Manual,
        CheckInDate = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
        CheckOutDate = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc),
        NoShowAt = noShow ? new DateTime(2026, 10, 10, 14, 0, 0, DateTimeKind.Utc) : null,
        CreatedAt = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
    };
}
