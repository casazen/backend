using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-10c: the reminders to the supplier of a request on measure that nobody answered — one at half of the time it has and one ten minutes before
/// the deadline, each at most once, none after the deadline (the job closes the request), none for a wait too short to deserve two.
/// </summary>
public class ShowcaseReplyReminderRulesTests
{
    private static readonly DateTime Begin = new(2026, 10, 31, 10, 0, 0, DateTimeKind.Utc);

    private static ShowcaseReplyReminder Due(int waitMinutes, int minutesIn, bool halfSent = false, bool lastSent = false) =>
        ShowcaseReplyReminderRules.DueOf(Begin, Begin.AddMinutes(waitMinutes), Begin.AddMinutes(minutesIn), halfSent, lastSent);

    [Theory]
    [InlineData(0, ShowcaseReplyReminder.None)]
    [InlineData(29, ShowcaseReplyReminder.None)]
    [InlineData(30, ShowcaseReplyReminder.Half)]
    [InlineData(49, ShowcaseReplyReminder.Half)]
    [InlineData(50, ShowcaseReplyReminder.Last)]
    [InlineData(59, ShowcaseReplyReminder.Last)]
    [InlineData(60, ShowcaseReplyReminder.None)]
    [InlineData(90, ShowcaseReplyReminder.None)]
    public void AnHour_HalfAtThirtyMinutes_TheLastCallAtFifty_NothingFromTheDeadline(int minutesIn, ShowcaseReplyReminder expected)
    {
        Assert.Equal(expected, Due(60, minutesIn));
    }

    [Fact]
    public void WhenTheHalfReminderWasSent_ItIsNotDueAgain_AndTheLastCallStillComesAtItsTime()
    {
        Assert.Equal(ShowcaseReplyReminder.None, Due(60, 40, halfSent: true));
        Assert.Equal(ShowcaseReplyReminder.None, Due(60, 49, halfSent: true));
        Assert.Equal(ShowcaseReplyReminder.Last, Due(60, 50, halfSent: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenTheLastCallWasSent_NothingIsDueAnyMore(bool halfSent)
    {
        Assert.Equal(ShowcaseReplyReminder.None, Due(60, 55, halfSent, lastSent: true));
    }

    [Fact]
    public void ARunThatIsLate_SendsOnlyTheLastCall_NotTheHalfOneAndThenTheLast()
    {
        // The job missed the half moment (a restart, a lock held): at 55 minutes only the last call is due, once.
        Assert.Equal(ShowcaseReplyReminder.Last, Due(60, 55, halfSent: false));
    }

    [Theory]
    [InlineData(20, 9, ShowcaseReplyReminder.None)]
    [InlineData(20, 10, ShowcaseReplyReminder.Last)]
    [InlineData(15, 4, ShowcaseReplyReminder.None)]
    [InlineData(15, 5, ShowcaseReplyReminder.Last)]
    [InlineData(11, 0, ShowcaseReplyReminder.None)]
    [InlineData(11, 1, ShowcaseReplyReminder.Last)]
    public void AShortWait_HasNoHalfReminder_OnlyTheLastCall(int waitMinutes, int minutesIn, ShowcaseReplyReminder expected)
    {
        Assert.Equal(expected, Due(waitMinutes, minutesIn));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(5)]
    public void AWaitNoLongerThanTheLeadOfTheLastCall_IsNeverReminded_TheSupplierWasToldAMomentAgo(int waitMinutes)
    {
        for (var minutesIn = 0; minutesIn <= waitMinutes; minutesIn++)
            Assert.Equal(ShowcaseReplyReminder.None, Due(waitMinutes, minutesIn));
    }

    [Fact]
    public void ALongWait_HasItsHalfInTheMiddle_AndItsLastCallTenMinutesBeforeTheEnd()
    {
        Assert.Equal(ShowcaseReplyReminder.None, Due(240, 119));
        Assert.Equal(ShowcaseReplyReminder.Half, Due(240, 120));
        // The half reminder went: nothing is due until the last call opens (a half reminder never sent would stay due until then).
        Assert.Equal(ShowcaseReplyReminder.None, Due(240, 229, halfSent: true));
        Assert.Equal(ShowcaseReplyReminder.Last, Due(240, 230));
    }

    [Theory]
    [InlineData(ServiceRequestRentalContext.Showcase, ServiceRequestKind.Custom, ServiceRequestStatus.Richiesto, true, false, true)]
    [InlineData(ServiceRequestRentalContext.Showcase, ServiceRequestKind.Custom, ServiceRequestStatus.Richiesto, false, false, false)] // no deadline: the supplier already answered
    [InlineData(ServiceRequestRentalContext.Showcase, ServiceRequestKind.Custom, ServiceRequestStatus.Richiesto, true, true, false)] // an offer waits: the deadline is the customer's
    [InlineData(ServiceRequestRentalContext.Showcase, ServiceRequestKind.Custom, ServiceRequestStatus.PresoInCarico, true, false, false)]
    [InlineData(ServiceRequestRentalContext.Showcase, ServiceRequestKind.Custom, ServiceRequestStatus.Annullato, true, false, false)]
    [InlineData(ServiceRequestRentalContext.Showcase, ServiceRequestKind.Standard, ServiceRequestStatus.Richiesto, true, false, false)] // a booking has the supplier's own setting
    [InlineData(ServiceRequestRentalContext.ShortRent, ServiceRequestKind.Standard, ServiceRequestStatus.Richiesto, true, false, false)] // a host's request has its own reminder
    public void AReminderIsOnlyForARequestOnMeasureThatTheSupplierHasNotAnsweredYet(
        ServiceRequestRentalContext context,
        ServiceRequestKind kind,
        ServiceRequestStatus status,
        bool hasDeadline,
        bool hasOffer,
        bool expected)
    {
        var due = hasDeadline ? Begin.AddHours(1) : (DateTime?)null;
        var offerSentAt = hasOffer ? Begin : (DateTime?)null;

        Assert.Equal(expected, ShowcaseReplyReminderRules.Applies(context, kind, status, due, offerSentAt));
    }
}
