using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E05/F05/US03/T03 (AC-NF67-6): the dedup key of the event the copy of the conversations records for a thread it read (<c>OtaThreadSynced</c>). While the
/// assistant of Octorate reads on the ten minutes of the pass are in the key, so the guard that keeps it off hears of it at every pass (a person who switches
/// it on again, a call that failed); a flag that is off or not said keeps the key it had (once for each newest message), and every key fits the column.
/// </summary>
public class MessageSyncEventKeyTests
{
    private static readonly Guid Thread = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private const string Last = "20261106075500000";

    [Fact]
    public void PassWindowOf_IsTheTenMinutesAnInstantFallsIn_AsAStampOfTheRealDate()
    {
        // Real instants, not defaults: the stamp is a DateTime format, and a specifier that does not fit throws only when it runs.
        Assert.Equal("202611061500", MessageSyncService.PassWindowOf(new DateTime(2026, 11, 6, 15, 3, 27, DateTimeKind.Utc)));
        Assert.Equal("202611061500", MessageSyncService.PassWindowOf(new DateTime(2026, 11, 6, 15, 9, 59, 999, DateTimeKind.Utc)));
        Assert.Equal("202611061510", MessageSyncService.PassWindowOf(new DateTime(2026, 11, 6, 15, 10, 0, DateTimeKind.Utc)));
        Assert.Equal("202611061550", MessageSyncService.PassWindowOf(new DateTime(2026, 11, 6, 15, 59, 59, DateTimeKind.Utc)));
        Assert.Equal("202611060000", MessageSyncService.PassWindowOf(new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("202612312350", MessageSyncService.PassWindowOf(new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void TheKey_FormatsARealInstant_WithTheInvariantCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var at = new DateTime(2026, 11, 6, 15, 3, 27, DateTimeKind.Utc);

            Assert.Equal("202611061500", MessageSyncService.PassWindowOf(at));
            Assert.Equal($"ota-thread:11111111222233334444555555555555:{Last}:on:202611061500", MessageSyncService.SyncedEventKey(Thread, Last, true, at));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void WhileTheAssistantReadsOn_EachTenMinutesOfThePass_IsAnotherKey_AndTheSameWindowIsOne()
    {
        var first = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);

        var sameWindow = MessageSyncService.SyncedEventKey(Thread, Last, true, first.AddMinutes(9).AddSeconds(59));
        var nextWindow = MessageSyncService.SyncedEventKey(Thread, Last, true, first.AddMinutes(10));

        Assert.Equal(MessageSyncService.SyncedEventKey(Thread, Last, true, first), sameWindow);
        Assert.NotEqual(sameWindow, nextWindow);
        Assert.NotEqual(MessageSyncService.SyncedEventKey(Thread, Last, true, first), MessageSyncService.SyncedEventKey(Thread, "20261106080100000", true, first));
        Assert.NotEqual(MessageSyncService.SyncedEventKey(Thread, Last, true, first), MessageSyncService.SyncedEventKey(Guid.NewGuid(), Last, true, first));
    }

    [Fact]
    public void AFlagThatIsOffOrNotSaid_KeepsTheKeyItHad_WhateverTheInstant()
    {
        var early = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);
        var late = early.AddHours(5);

        Assert.Equal($"ota-thread:11111111222233334444555555555555:{Last}:off", MessageSyncService.SyncedEventKey(Thread, Last, false, early));
        Assert.Equal(MessageSyncService.SyncedEventKey(Thread, Last, false, early), MessageSyncService.SyncedEventKey(Thread, Last, false, late));
        Assert.Equal($"ota-thread:11111111222233334444555555555555:{Last}:unknown", MessageSyncService.SyncedEventKey(Thread, Last, null, early));
        Assert.Equal(MessageSyncService.SyncedEventKey(Thread, Last, null, early), MessageSyncService.SyncedEventKey(Thread, Last, null, late));

        // The three states are three keys: a change of the flag with no new message is a new event.
        Assert.Equal(3, new[] { true, false, (bool?)null }.Select(flag => MessageSyncService.SyncedEventKey(Thread, Last, flag, early)).Distinct().Count());
    }

    [Fact]
    public void EveryKey_FitsTheColumnOfTheDedupKey()
    {
        var at = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);

        foreach (bool? flag in new bool?[] { true, false, null })
            Assert.True(MessageSyncService.SyncedEventKey(Guid.NewGuid(), Last, flag, at).Length <= OutboxEvent.DedupKeyMaxLength);
    }
}
