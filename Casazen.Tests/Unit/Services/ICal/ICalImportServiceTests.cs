using Casazen.Core.Entities;
using Casazen.Infrastructure.Services.ICal;
using Xunit;

namespace Casazen.Tests.Unit.Services.ICal;

/// <summary>
/// PC-10 (A2-12, A2-23): blocks that always fit the CalendarBlocks columns and unique index, and the recurrence
/// window around today in Europe/Rome.
/// </summary>
public class ICalImportServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    private static ICalImportService Service(ICalImportOptions? options = null) =>
        ICalTestServices.ImportService(new FixedTimeProvider(Now), options);

    private static string Calendar(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n"
        + string.Concat(events.Select(e => $"BEGIN:VEVENT\r\n{e.Replace("\n", "\r\n", StringComparison.Ordinal)}\r\nEND:VEVENT\r\n"))
        + "END:VCALENDAR\r\n";

    private static PropertyICalBlocks Blocks(string ics) =>
        ICalImportService.ToPropertyBlocks(Service().Parse(ics).Occurrences);

    [Fact]
    public void ToPropertyBlocks_ValidEvent_ReturnsItsUidAndSummary()
    {
        var block = Assert.Single(Blocks(Calendar("UID:test-uid-1\nDTSTART:20260710T100000Z\nDTEND:20260712T100000Z\nSUMMARY:Reserved")).Blocks);

        Assert.Equal("test-uid-1", block.ExternalUid);
        Assert.Equal("Reserved", block.Summary);
    }

    // A2-23: the old service swallowed every exception (catch {}) and returned "no events", which the sync then
    // applied as an empty calendar. An unreadable document is now an error the caller stores and logs.
    [Fact]
    public void Parse_InvalidContent_ThrowsFormatExceptionInsteadOfReturningNoEvents()
    {
        var ex = Assert.Throws<ICalFormatException>(() => Service().Parse("not an ics file"));

        Assert.Equal(ICalFormatFailure.NotICalendar, ex.Failure);
    }

    [Fact]
    public void ToPropertyBlocks_SummaryOf2000Characters_IsCutToTheColumnLength()
    {
        var summary = new string('x', 2000);

        var block = Assert.Single(Blocks(Calendar($"UID:long\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012\nSUMMARY:{summary}")).Blocks);

        Assert.Equal(CalendarBlock.SummaryMaxLength, block.Summary!.Length);
        Assert.StartsWith("xxxx", block.Summary);
    }

    [Fact]
    public void Truncate_CutInsideASurrogatePair_DropsTheWholeCharacter()
    {
        var value = new string('a', 499) + "\U0001F600" + "tail";

        var truncated = ICalImportService.Truncate(value, 500);

        Assert.Equal(new string('a', 499), truncated);
    }

    [Fact]
    public void ToPropertyBlocks_UidOfMoreThan500Characters_IsStoredAsAStableHash()
    {
        var ics = Calendar($"UID:{new string('u', 800)}\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012");

        var first = Assert.Single(Blocks(ics).Blocks);
        var second = Assert.Single(Blocks(ics).Blocks);

        Assert.True(first.ExternalUid.Length <= CalendarBlock.ExternalUidMaxLength);
        Assert.StartsWith("sha256:", first.ExternalUid);
        Assert.Equal(first.ExternalUid, second.ExternalUid);
    }

    [Fact]
    public void ToPropertyBlocks_DuplicateUids_AreMergedOrKeyedByDateWithoutLosingNights()
    {
        var result = Blocks(Calendar(
            "UID:dup\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012",
            "UID:dup\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012",
            "UID:dup\nDTSTART;VALUE=DATE:20261020\nDTEND;VALUE=DATE:20261022",
            "UID:dup\nDTSTART;VALUE=DATE:20261020\nDTEND;VALUE=DATE:20261025",
            "UID:same\nDTSTART;VALUE=DATE:20261101\nDTEND;VALUE=DATE:20261103",
            "UID:same\nDTSTART;VALUE=DATE:20261101\nDTEND;VALUE=DATE:20261103"));

        Assert.Equal(result.Blocks.Count, result.Blocks.Select(b => b.ExternalUid).Distinct(StringComparer.Ordinal).Count());
        var dup = result.Blocks.Where(b => b.ExternalUid.StartsWith("dup", StringComparison.Ordinal)).OrderBy(b => b.StartUtc).ToList();
        Assert.Equal(["dup#20261010", "dup#20261020"], dup.Select(b => b.ExternalUid));
        Assert.Equal(new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc), dup[1].EndUtc); // longest of the same start
        Assert.Equal("same", Assert.Single(result.Blocks, b => b.ExternalUid.StartsWith("same", StringComparison.Ordinal)).ExternalUid);
        Assert.Equal(3, result.MergedDuplicates);
    }

    [Theory]
    [InlineData("reservation-1", true)]
    [InlineData("reservation-1#20261010", true)]
    [InlineData("reservation-10", false)]
    [InlineData("reservation-1#2026101", false)]
    [InlineData("reservation-1#2026101x", false)]
    [InlineData("other", false)]
    public void IsKeyOfAny_BlockKeys_MatchTheUidAndItsOccurrenceKeys(string externalUid, bool expected)
    {
        var uids = new HashSet<string>(StringComparer.Ordinal) { "reservation-1" };

        Assert.Equal(expected, ICalImportService.IsKeyOfAny(externalUid, uids));
    }

    [Fact]
    public void IsKeyOfAny_UidLongerThanTheColumn_MatchesItsHashedKey()
    {
        var longUid = new string('u', 800);
        var key = Assert.Single(Blocks(Calendar($"UID:{longUid}\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012")).Blocks).ExternalUid;

        Assert.True(ICalImportService.IsKeyOfAny(key, new HashSet<string>(StringComparer.Ordinal) { longUid }));
        Assert.False(ICalImportService.IsKeyOfAny(key, new HashSet<string>(StringComparer.Ordinal) { "short" }));
    }

    [Fact]
    public void ToPropertyBlocks_DatesAreMidnightUtcOfTheNights()
    {
        var block = Assert.Single(Blocks(Calendar("UID:a\nDTSTART:20261010T233000Z\nDTEND:20261012T090000Z")).Blocks);

        Assert.Equal(new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc), block.StartUtc);
        Assert.Equal(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), block.EndUtc);
        Assert.Equal(DateTimeKind.Utc, block.StartUtc.Kind);
    }

    [Fact]
    public void ToPropertyBlocks_EventWithoutNight_IsNotABlock()
    {
        Assert.Empty(Blocks(Calendar("UID:cleaning\nDTSTART:20261010T080000Z\nDTEND:20261010T100000Z")).Blocks);
    }

    [Fact]
    public void ToPropertyBlocks_EventWithoutUid_GetsTheSameKeyAtEverySync()
    {
        var ics = Calendar("DTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012\nSUMMARY:Blocked");

        Assert.Equal(Assert.Single(Blocks(ics).Blocks).ExternalUid, Assert.Single(Blocks(ics).Blocks).ExternalUid);
    }

    [Fact]
    public void ToPropertyBlocks_WeeklyRecurrence_GetsOneKeyPerOccurrence()
    {
        var result = Blocks(Calendar("UID:weekend\nDTSTART;VALUE=DATE:20261003\nDTEND;VALUE=DATE:20261005\nRRULE:FREQ=WEEKLY;COUNT=3"));

        Assert.Equal(["weekend#20261003", "weekend#20261010", "weekend#20261017"], result.Blocks.Select(b => b.ExternalUid).Order());
    }

    [Theory]
    [InlineData(18, "2028-03-24")]
    [InlineData(6, "2027-03-24")]
    public void Parse_EndlessRecurrence_IsExpandedUntilTheConfiguredMonthsAfterTodayInRome(int monthsAhead, string until)
    {
        var service = Service(new ICalImportOptions { RecurrenceMonthsAhead = monthsAhead, RecurrenceMonthsBack = 0 });

        var result = service.Parse(Calendar("UID:daily\nDTSTART;VALUE=DATE:20200101\nDTEND;VALUE=DATE:20200102\nRRULE:FREQ=DAILY"));

        var starts = result.Occurrences.Select(o => o.StartDate).Order().ToList();
        Assert.Equal(new DateOnly(2026, 9, 24), starts[0]);
        Assert.Equal(DateOnly.Parse(until, System.Globalization.CultureInfo.InvariantCulture).AddDays(-1), starts[^1]);
    }

    [Fact]
    public void Parse_SingleEventsOutsideTheRecurrenceWindow_AreStillImported()
    {
        var result = Service().Parse(Calendar(
            "UID:past\nDTSTART;VALUE=DATE:20250110\nDTEND;VALUE=DATE:20250112",
            "UID:far\nDTSTART;VALUE=DATE:20300110\nDTEND;VALUE=DATE:20300112"));

        Assert.Equal(2, result.Occurrences.Count);
    }

    [Fact]
    public void ToBusyDays_TimedAndAllDayEvents_ReturnEveryDayTouched()
    {
        var parsed = Service().Parse(Calendar(
            "UID:all-day\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012",
            "UID:overnight\nDTSTART:20261020T160000Z\nDTEND:20261021T080000Z",
            "UID:morning\nDTSTART:20261025T080000Z\nDTEND:20261025T100000Z"));

        var days = ICalImportService.ToBusyDays(parsed.Occurrences).Order().ToList();

        Assert.Equal(
            [
                new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 11),
                new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 21),
                new DateOnly(2026, 10, 25),
            ],
            days);
    }
}
