using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

public class LeaseListViewsTests
{
    private static readonly DateTime Today = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    private static readonly LeaseStatus[] NotRegistered =
    [
        LeaseStatus.Draft, LeaseStatus.AwaitingSignature, LeaseStatus.PartiallySigned, LeaseStatus.Signed,
        LeaseStatus.RegistrationPending, LeaseStatus.SentToProvider,
    ];

    private static bool Matches(LeaseListView view, LeaseStatus status, DateTime endDate) =>
        LeaseListViews.Predicate(view, Today).Compile()(new LeaseContract { Status = status, EndDate = endDate });

    private static IReadOnlyList<LeaseListView> ViewsOf(LeaseStatus status, DateTime endDate) =>
        Enum.GetValues<LeaseListView>().Where(v => v != LeaseListView.All && Matches(v, status, endDate)).ToList();

    [Fact]
    public void All_EveryLease_Matches()
    {
        foreach (var status in Enum.GetValues<LeaseStatus>())
        {
            Assert.True(Matches(LeaseListView.All, status, Today.AddYears(-3)));
            Assert.True(Matches(LeaseListView.All, status, Today.AddYears(3)));
        }
    }

    [Theory]
    [InlineData(LeaseStatus.Draft)]
    [InlineData(LeaseStatus.AwaitingSignature)]
    [InlineData(LeaseStatus.PartiallySigned)]
    [InlineData(LeaseStatus.Signed)]
    [InlineData(LeaseStatus.RegistrationPending)]
    [InlineData(LeaseStatus.SentToProvider)]
    public void ALeaseNotRegisteredYet_IsInPreparation_WhateverItsDates(LeaseStatus status)
    {
        foreach (var end in new[] { Today.AddYears(4), Today, Today.AddDays(-1), Today.AddYears(-2) })
            Assert.Equal([LeaseListView.InPreparation], ViewsOf(status, end));
    }

    [Fact]
    public void ARegisteredLeaseThatEndsLater_IsActiveOnly()
    {
        Assert.Equal([LeaseListView.Active], ViewsOf(LeaseStatus.Registered, Today.AddMonths(6).AddDays(1)));
        Assert.Equal([LeaseListView.Active], ViewsOf(LeaseStatus.Registered, Today.AddYears(4)));
    }

    [Fact]
    public void ARegisteredLeaseThatEndsWithinSixMonths_IsActiveAndExpiring()
    {
        Assert.Equal([LeaseListView.Active, LeaseListView.Expiring], ViewsOf(LeaseStatus.Registered, Today.AddMonths(6)));
        Assert.Equal([LeaseListView.Active, LeaseListView.Expiring], ViewsOf(LeaseStatus.Registered, Today.AddDays(1)));
        Assert.Equal([LeaseListView.Active, LeaseListView.Expiring], ViewsOf(LeaseStatus.Registered, new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ARegisteredLeaseThatEndsToday_IsStillActiveAndExpiring_TheLastDayIsInTheLease()
    {
        Assert.Equal([LeaseListView.Active, LeaseListView.Expiring], ViewsOf(LeaseStatus.Registered, Today));
    }

    [Fact]
    public void ARegisteredLeaseThatEndedYesterday_IsEnded()
    {
        Assert.Equal([LeaseListView.Ended], ViewsOf(LeaseStatus.Registered, Today.AddDays(-1)));
        Assert.Equal([LeaseListView.Ended], ViewsOf(LeaseStatus.Registered, Today.AddYears(-5)));
    }

    [Fact]
    public void ARejectedLease_IsEnded_NeverActiveNorInPreparation()
    {
        foreach (var end in new[] { Today.AddYears(4), Today, Today.AddDays(-1) })
            Assert.Equal([LeaseListView.Ended], ViewsOf(LeaseStatus.Rejected, end));
    }

    [Fact]
    public void EveryLease_IsInExactlyOneOfPreparationActiveOrEnded_ExpiringBeingASubsetOfActive()
    {
        var ends = new[] { Today.AddYears(-1), Today.AddDays(-1), Today, Today.AddDays(1), Today.AddMonths(6), Today.AddMonths(6).AddDays(1), Today.AddYears(2) };
        foreach (var status in Enum.GetValues<LeaseStatus>())
        {
            foreach (var end in ends)
            {
                var views = ViewsOf(status, end);
                var exclusive = views.Where(v => v != LeaseListView.Expiring).ToList();
                Assert.Single(exclusive);
                if (views.Contains(LeaseListView.Expiring))
                    Assert.Contains(LeaseListView.Active, views);
            }
        }
    }

    [Fact]
    public void TheNotRegisteredStatuses_AreEverythingButRegisteredAndRejected()
    {
        // A new status of the enum must be thought about here: it lands in "in preparazione" unless it is one of these two.
        Assert.Equal(
            Enum.GetValues<LeaseStatus>().Where(s => s is not (LeaseStatus.Registered or LeaseStatus.Rejected)).Order(),
            NotRegistered.Order());
    }

    [Fact]
    public void TheExpiringHorizon_IsTheNoticePeriod()
    {
        Assert.Equal(LongRentDeadlineRules.NoticeMonthsBeforeEnd, LeaseListViews.ExpiringWithinMonths);
        Assert.Equal(6, LeaseListViews.ExpiringWithinMonths);
    }

    [Fact]
    public void AnUnknownView_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LeaseListViews.Predicate((LeaseListView)99, Today));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("rossi", "rossi")]
    [InlineData("  Casa Corso \n", "Casa Corso")]
    public void Search_IsTrimmed_AndBlankIsNoSearch(string? search, string? expected)
    {
        Assert.Equal(expected, new LeaseListQuery(Search: search).NormalizedSearch);
    }

    [Fact]
    public void Search_LongerThanTheLimit_IsCut()
    {
        var search = new string('a', LeaseListQuery.MaxSearchLength + 50);

        Assert.Equal(LeaseListQuery.MaxSearchLength, new LeaseListQuery(Search: search).NormalizedSearch!.Length);
    }
}
