using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-15b: when a payment is late and when the payer is reminded. The first request is day 0; the reminders go at +2 and +7 days
/// (<c>SupplierPayments:ReminderDays</c>), at most three emails with a link in all, never twice in a day, and a payment is late 7
/// days after it was asked for. Pure rules, so the daily job is only a loop over them.
/// </summary>
public class ServicePaymentScheduleTests
{
    private static readonly DateTime Asked = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly int[] Days = [2, 7];

    // ─── Late ───

    [Fact]
    public void LateSince_IsTheRequestPlusTheConfiguredDays_AndNullWhileNotAsked()
    {
        Assert.Equal(Asked.AddDays(7), ServicePaymentSchedule.LateSince(Asked, 7));
        Assert.Null(ServicePaymentSchedule.LateSince(null, 7));
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested, true)]
    [InlineData(ServicePaymentStatus.Failed, true)]
    [InlineData(ServicePaymentStatus.Processing, false)]
    [InlineData(ServicePaymentStatus.Paid, false)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded, false)]
    [InlineData(ServicePaymentStatus.Refunded, false)]
    [InlineData(ServicePaymentStatus.Canceled, false)]
    [InlineData(ServicePaymentStatus.NeedsReview, false)]
    public void IsOwed_OnlyAPaymentThePayerStillHasToMake(ServicePaymentStatus status, bool owed)
    {
        Assert.Equal(owed, ServicePaymentSchedule.IsOwed(status));
    }

    [Fact]
    public void IsLate_FromTheSeventhDay_OnlyWhileOwed()
    {
        Assert.False(ServicePaymentSchedule.IsLate(ServicePaymentStatus.Requested, Asked, 7, Asked.AddDays(7).AddTicks(-1)));
        Assert.True(ServicePaymentSchedule.IsLate(ServicePaymentStatus.Requested, Asked, 7, Asked.AddDays(7)));
        Assert.True(ServicePaymentSchedule.IsLate(ServicePaymentStatus.Failed, Asked, 7, Asked.AddDays(30)));
        // Paid, in flight, withdrawn or waiting for a review are not "late"; neither is a payment nobody asked for yet.
        foreach (var status in new[] { ServicePaymentStatus.Paid, ServicePaymentStatus.Processing, ServicePaymentStatus.Canceled, ServicePaymentStatus.NeedsReview })
            Assert.False(ServicePaymentSchedule.IsLate(status, Asked, 7, Asked.AddDays(30)));
        Assert.False(ServicePaymentSchedule.IsLate(ServicePaymentStatus.Requested, null, 7, Asked.AddDays(30)));
    }

    // ─── Reminders ───

    private static bool Due(int sentCount, DateTime now, DateTime? lastSentAt = null, ServicePaymentStatus status = ServicePaymentStatus.Requested, IReadOnlyList<int>? days = null) =>
        ServicePaymentSchedule.ReminderIsDue(status, sentCount, Asked, lastSentAt ?? Asked, days ?? Days, now);

    [Fact]
    public void ReminderIsDue_TheFirstReminderGoesAtTheSecondDay_NotBefore()
    {
        Assert.False(Due(1, Asked.AddDays(2).AddTicks(-1)));
        Assert.True(Due(1, Asked.AddDays(2)));
        Assert.True(Due(1, Asked.AddDays(5)));
    }

    [Fact]
    public void ReminderIsDue_TheSecondReminderGoesAtTheSeventhDay_AfterTheFirstWasSent()
    {
        var firstSent = Asked.AddDays(2).AddHours(1);

        Assert.False(Due(2, Asked.AddDays(6), firstSent));
        Assert.True(Due(2, Asked.AddDays(7), firstSent));
    }

    [Fact]
    public void ReminderIsDue_AtMostThreeEmailsWithALink_RequestPlusTwoReminders()
    {
        Assert.False(Due(3, Asked.AddDays(30), Asked.AddDays(8)));
        Assert.False(Due(4, Asked.AddDays(30), Asked.AddDays(8)));
        Assert.Equal(3, ServicePaymentLimits.MaxPaymentEmails);
    }

    [Fact]
    public void ReminderIsDue_ManyConfiguredDays_NeverGoPastTheCap()
    {
        int[] many = [1, 2, 3, 4, 5, 6];

        Assert.True(Due(1, Asked.AddDays(10), Asked, days: many));
        Assert.True(Due(2, Asked.AddDays(10), Asked.AddDays(1), days: many));
        Assert.False(Due(3, Asked.AddDays(10), Asked.AddDays(2), days: many));
    }

    [Fact]
    public void ReminderIsDue_ARemindersTheSupplierSentByHand_CountsAsOneOfThem()
    {
        // The supplier reminded at day 1 (two emails so far): the reminder of day 2 is already made up for, the one of day 7 is not.
        Assert.False(Due(2, Asked.AddDays(2), Asked.AddDays(1)));
        Assert.False(Due(2, Asked.AddDays(6), Asked.AddDays(1)));
        Assert.True(Due(2, Asked.AddDays(7), Asked.AddDays(1)));
    }

    [Fact]
    public void ReminderIsDue_NeverTwiceInADay()
    {
        var sentAt = Asked.AddDays(2).AddHours(3);

        // The supplier asked three hours ago and the day of the second reminder has come: it waits for the day to pass.
        Assert.False(ServicePaymentSchedule.ReminderIsDue(ServicePaymentStatus.Requested, 2, Asked, Asked.AddDays(7).AddHours(-3), Days, Asked.AddDays(7)));
        Assert.True(ServicePaymentSchedule.ReminderIsDue(ServicePaymentStatus.Requested, 2, Asked, Asked.AddDays(7).AddHours(-24), Days, Asked.AddDays(7)));
        Assert.False(Due(2, sentAt.AddHours(23), sentAt));
    }

    [Fact]
    public void ReminderIsDue_APendingPaymentIsNotReminded_ItHasNotBeenAskedForYet()
    {
        Assert.False(ServicePaymentSchedule.ReminderIsDue(ServicePaymentStatus.Requested, 0, null, null, Days, Asked.AddDays(30)));
        Assert.False(ServicePaymentSchedule.ReminderIsDue(ServicePaymentStatus.Requested, 0, Asked, null, Days, Asked.AddDays(30)));
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested, true)]
    [InlineData(ServicePaymentStatus.Failed, true)]
    [InlineData(ServicePaymentStatus.Processing, false)]
    [InlineData(ServicePaymentStatus.Paid, false)]
    [InlineData(ServicePaymentStatus.Canceled, false)]
    [InlineData(ServicePaymentStatus.NeedsReview, false)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded, false)]
    [InlineData(ServicePaymentStatus.Refunded, false)]
    public void ReminderIsDue_OnlyWhileThePayerStillOwesIt(ServicePaymentStatus status, bool due)
    {
        Assert.Equal(due, Due(1, Asked.AddDays(3), status: status));
    }

    [Fact]
    public void ReminderIsDue_NoConfiguredDays_NoReminder()
    {
        Assert.False(Due(1, Asked.AddDays(30), days: []));
    }
}
