using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-01: the one definition of "overdue", "open" and "pending" of a rent installment, used by the ledger of a lease, the rent register,
/// the lease list and the overview. The method form and the SQL expression form must say the same for every status and every day.
/// </summary>
public class RentInstallmentRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly DateOnly[] Dues = [Today.AddDays(-65), Today.AddDays(-1), Today, Today.AddDays(1), Today.AddDays(30)];

    private static RentLedgerEntry Entry(RentLedgerStatus status, DateOnly due) => new() { Status = status, DueDate = due };

    [Theory]
    [InlineData(RentLedgerStatus.Scheduled, -1, true)]
    [InlineData(RentLedgerStatus.Scheduled, 0, false)] // due today is not overdue yet
    [InlineData(RentLedgerStatus.Scheduled, 1, false)]
    [InlineData(RentLedgerStatus.Failed, -1, true)]
    [InlineData(RentLedgerStatus.Failed, 0, false)]
    [InlineData(RentLedgerStatus.Processing, -30, false)] // a payment in flight is settled by Stripe, not late
    [InlineData(RentLedgerStatus.Paid, -30, false)]
    [InlineData(RentLedgerStatus.Cancelled, -30, false)]
    public void IsOverdue_NotPaidAndPastItsDueDate_OnlyForScheduledAndFailed(RentLedgerStatus status, int daysFromToday, bool expected)
    {
        Assert.Equal(expected, RentInstallmentRules.IsOverdue(status, Today.AddDays(daysFromToday), Today));
    }

    [Fact]
    public void DaysOverdue_IsTheDaysSinceTheDueDate_OnlyWhenOverdue()
    {
        Assert.Equal(65, RentInstallmentRules.DaysOverdue(RentLedgerStatus.Scheduled, Today.AddDays(-65), Today));
        Assert.Equal(1, RentInstallmentRules.DaysOverdue(RentLedgerStatus.Failed, Today.AddDays(-1), Today));
        Assert.Null(RentInstallmentRules.DaysOverdue(RentLedgerStatus.Scheduled, Today, Today));
        Assert.Null(RentInstallmentRules.DaysOverdue(RentLedgerStatus.Paid, Today.AddDays(-10), Today));
    }

    [Fact]
    public void TheExpressionForms_SayTheSameAsTheMethods_ForEveryStatusAndDay()
    {
        var overdue = RentInstallmentRules.OverdueOn(Today).Compile();
        var pending = RentInstallmentRules.PendingOn(Today).Compile();
        var open = RentInstallmentRules.Open.Compile();

        foreach (var status in Enum.GetValues<RentLedgerStatus>())
        {
            foreach (var due in Dues)
            {
                var entry = Entry(status, due);
                Assert.Equal(RentInstallmentRules.IsOverdue(status, due, Today), overdue(entry));
                Assert.Equal(RentInstallmentRules.IsOpen(status), open(entry));
                // Pending: expected, not paid, not overdue.
                Assert.Equal(RentInstallmentRules.IsOpen(status) && !RentInstallmentRules.IsOverdue(status, due, Today), pending(entry));
            }
        }
    }

    [Fact]
    public void EveryInstallmentThatIsNotCancelled_IsInExactlyOneOfPaidOverdueAndPending()
    {
        var overdue = RentInstallmentRules.OverdueOn(Today).Compile();
        var pending = RentInstallmentRules.PendingOn(Today).Compile();

        foreach (var status in Enum.GetValues<RentLedgerStatus>().Where(s => s != RentLedgerStatus.Cancelled))
        {
            foreach (var due in Dues)
            {
                var entry = Entry(status, due);
                var buckets = new[] { status == RentLedgerStatus.Paid, overdue(entry), pending(entry) };
                Assert.Equal(1, buckets.Count(b => b));
            }
        }
    }

    [Theory]
    [InlineData(RentLedgerStatus.Scheduled, true, true)]
    [InlineData(RentLedgerStatus.Failed, true, true)]
    [InlineData(RentLedgerStatus.Processing, true, false)]
    [InlineData(RentLedgerStatus.Paid, false, false)]
    [InlineData(RentLedgerStatus.Cancelled, false, false)]
    public void OpenAndRemindable_AreWhatIsStillToBeCollectedAndWhatTheLandlordCanAskFor(RentLedgerStatus status, bool open, bool remindable)
    {
        Assert.Equal(open, RentInstallmentRules.IsOpen(status));
        Assert.Equal(remindable, RentInstallmentRules.IsRemindable(status));
    }
}
