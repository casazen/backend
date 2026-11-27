using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The stay as Octorate holds it after it refused a change (E08/F03/US03/T02): the stored form is written and read back to the day, and a member that
/// cannot be read is left out, never guessed (a host chooses which version stays on this).
/// </summary>
public class OctorateVersionSnapshotTests
{
    [Fact]
    public void ToJson_FormatsTheRealDaysAsDatesOnly_AndParseReadsThemBack()
    {
        // A real value, not a default: a time specifier on a DateOnly throws only when it runs.
        var snapshot = new OctorateVersionSnapshot(new DateOnly(2026, 11, 21), new DateOnly(2026, 11, 23), 1);

        var json = snapshot.ToJson();

        Assert.Equal("""{"checkIn":"2026-11-21","checkOut":"2026-11-23","guests":1}""", json);
        Assert.Equal(snapshot, OctorateVersionSnapshot.Parse(json));
    }

    [Fact]
    public void ToJson_LeavesOutWhatIsNotKnown()
    {
        Assert.Equal("""{"guests":3}""", new OctorateVersionSnapshot(null, null, 3).ToJson());
        Assert.Equal("{}", new OctorateVersionSnapshot(null, null, null).ToJson());
    }

    [Theory]
    [InlineData("""{"CheckIn":"2026-11-21","CheckOut":"2026-11-23","Guests":2}""", 2026, 11, 21, 2026, 11, 23, 2)]
    [InlineData("""{"checkInDate":"2026-12-01","checkOutDate":"2026-12-04","numberOfGuests":"4"}""", 2026, 12, 1, 2026, 12, 4, 4)]
    [InlineData("""{"checkIn":"2026-11-21","totalGuests":2}""", 2026, 11, 21, 0, 0, 0, 2)]
    public void Parse_ReadsTheNamesInAnyCase_AndTheGuestsAsANumberOrItsText(
        string json, int inYear, int inMonth, int inDay, int outYear, int outMonth, int outDay, int guests)
    {
        var snapshot = OctorateVersionSnapshot.Parse(json)!;

        Assert.Equal(new DateOnly(inYear, inMonth, inDay), snapshot.CheckIn);
        Assert.Equal(outYear == 0 ? null : new DateOnly(outYear, outMonth, outDay), snapshot.CheckOut);
        Assert.Equal(guests, snapshot.Guests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"name":"Mario Rossi","email":"mario@example.com"}""")]
    [InlineData("""{"checkIn":"soon","checkOut":20261123,"guests":0}""")]
    public void Parse_NothingThatCanBeReadIsNull_NeverAGuess(string? json)
    {
        Assert.Null(OctorateVersionSnapshot.Parse(json));
    }

    [Fact]
    public void Parse_ATimeIsNotGuessedIntoAStayDay_AndWhatCannotBeReadIsLeftOut()
    {
        // 23:00 UTC of the 20th is the 21st in Rome: without the clock of the property it is not a day CasaZen can name, so it is left out.
        var snapshot = OctorateVersionSnapshot.Parse("""{"checkIn":"2026-11-20T23:00:00Z","checkOut":"2026-11-23","guests":-2}""")!;

        Assert.Null(snapshot.CheckIn);
        Assert.Equal(new DateOnly(2026, 11, 23), snapshot.CheckOut);
        Assert.Null(snapshot.Guests);
    }
}
