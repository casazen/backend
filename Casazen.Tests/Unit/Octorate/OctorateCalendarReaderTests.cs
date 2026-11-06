using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The reading of the calendar of Octorate (E02/F02/US01/T03): the shape the sandbox runs recorded (units with days), the other shapes such an
/// answer can have, what a missing member means (unknown, never invented), and which days are closed nights.
/// </summary>
public class OctorateCalendarReaderTests
{
    private static readonly string[] OneRoom = ["956528"];
    private static readonly string[] TwoRooms = ["956528", "956530"];

    [Fact]
    public void TheShapeOfTheSandbox_UnitsWithDays_IsRead_WithEveryValueOfTheDay()
    {
        // `GET calendar/{acc}` answers {data: [units with days], page}; a day is {availability, bookable, closeToArrival, closeToDeparture,
        // cutOffDays, date, maxStay, minStay, price, stopSells} (sandbox runs of 2026-11-04).
        using var document = JsonDocument.Parse("""
            {
              "data": [
                { "id": "956528", "name": "Double Room", "days": [
                  { "date": "2026-11-10", "availability": 1, "bookable": true, "closeToArrival": false, "closeToDeparture": false,
                    "cutOffDays": 0, "maxStay": 0, "minStay": 2, "price": 120.5, "stopSells": false },
                  { "date": "2026-11-11", "availability": 0, "bookable": false, "closeToArrival": true, "closeToDeparture": true,
                    "cutOffDays": 1, "maxStay": 7, "minStay": 3, "price": 130, "stopSells": true }
                ] }
              ],
              "page": { "number": 0, "size": 20, "totalElements": 1 }
            }
            """);

        var days = OctorateCalendarReader.Read(document.RootElement, OneRoom).OrderBy(d => d.Date).ToList();

        Assert.Equal(2, days.Count);
        Assert.Equal(new OctorateCalendarDay("956528", new DateOnly(2026, 11, 10), 1, 120.5m, false, 2, 0, false, false), days[0]);
        Assert.Equal(new OctorateCalendarDay("956528", new DateOnly(2026, 11, 11), 0, 130m, true, 3, 7, true, true), days[1]);
    }

    [Fact]
    public void AListOfDaysThatEachNameTheirProduct_IsRead()
    {
        using var document = JsonDocument.Parse("""
            [
              { "product": "956528", "date": "2026-11-10", "availability": 0, "price": 99 },
              { "productId": "956530", "date": "2026-11-10", "availability": 1, "price": 80 },
              { "roomId": 956528, "date": "2026-11-11T00:00:00Z", "availability": 1, "price": 99 }
            ]
            """);

        var days = OctorateCalendarReader.Read(document.RootElement, TwoRooms);

        Assert.Equal(3, days.Count);
        Assert.Equal(0, days.Single(d => d.ProductId == "956528" && d.Date == new DateOnly(2026, 11, 10)).Availability);
        Assert.Equal(1, days.Single(d => d.ProductId == "956530").Availability);
        Assert.Equal(new DateOnly(2026, 11, 11), days.Single(d => d.ProductId == "956528" && d.Availability == 1).Date);
    }

    [Fact]
    public void AnObjectKeyedByProduct_WithDaysKeyedByDate_IsRead()
    {
        using var document = JsonDocument.Parse("""
            { "956528": { "2026-11-10": { "availability": 0, "price": 50 }, "2026-11-11": { "availability": 1, "price": 50 } } }
            """);

        var days = OctorateCalendarReader.Read(document.RootElement, TwoRooms).OrderBy(d => d.Date).ToList();

        Assert.Equal(2, days.Count);
        Assert.All(days, d => Assert.Equal("956528", d.ProductId));
        Assert.Equal([0, 1], days.Select(d => d.Availability));
    }

    [Fact]
    public void ADayThatNamesNoProduct_IsTheOneRoomTheCallAskedFor_AndNothingWhenSeveralWereAsked()
    {
        const string body = """{ "data": [ { "date": "2026-11-10", "availability": 0, "price": 70 } ] }""";
        using var document = JsonDocument.Parse(body);

        // One room asked: the day is its day. Two asked: it is nobody's, and a day nobody owns is not made up.
        Assert.Equal("956528", Assert.Single(OctorateCalendarReader.Read(document.RootElement, OneRoom)).ProductId);
        Assert.Empty(OctorateCalendarReader.Read(document.RootElement, TwoRooms));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{ "data": [] }""")]
    [InlineData("""{ "message": "ok", "page": { "number": 0 } }""")]
    [InlineData("""[ { "availability": 0, "price": 10 } ]""")]
    [InlineData("""{ "data": [ { "date": "not-a-date", "availability": 0 } ] }""")]
    [InlineData("\"text\"")]
    public void AnAnswerWithNoRecognizableDay_HasNoDay_NeverAnEmptyCalendarMadeUp(string body)
    {
        using var document = JsonDocument.Parse(body);

        Assert.Empty(OctorateCalendarReader.Read(document.RootElement, OneRoom));
    }

    [Fact]
    public void AMemberOfAnotherKind_IsUnknown_NotAnError()
    {
        using var document = JsonDocument.Parse("""
            { "data": [ { "date": "2026-11-10", "availability": "0", "price": "abc", "stopSells": "true", "minstay": null, "maxStay": [] } ] }
            """);

        var day = Assert.Single(OctorateCalendarReader.Read(document.RootElement, OneRoom));

        Assert.Equal(0, day.Availability);
        Assert.Null(day.Price);
        Assert.True(day.StopSell);
        Assert.Null(day.MinStay);
        Assert.Null(day.MaxStay);
        Assert.Null(day.CloseToArrival);
    }

    [Fact]
    public void ADayThatAppearsTwice_IsKeptOnce_TheLastOneRead()
    {
        using var document = JsonDocument.Parse("""
            [ { "product": "956528", "date": "2026-11-10", "availability": 1, "price": 10 },
              { "product": "956528", "date": "2026-11-10", "availability": 0, "price": 10 } ]
            """);

        var day = Assert.Single(OctorateCalendarReader.Read(document.RootElement, TwoRooms));

        Assert.Equal(0, day.Availability);
    }

    // What a closed night is (OctorateCalendarDay.IsClosed): stopped, or no unit on a day that is on sale. A day nobody opened (availability 0
    // and price 0, how the sandbox shows a calendar that was never filled) is not closed: no channel can sell it, and reading it as closed
    // would shut the property on the booking site.
    [Theory]
    [InlineData(1, 100, true, true)]
    [InlineData(0, 100, false, true)]
    [InlineData(0, 100, null, true)]
    [InlineData(null, 100, true, true)]
    [InlineData(0, null, null, true)]
    [InlineData(-1, 100, false, true)]
    [InlineData(0, 0, true, true)]
    [InlineData(0, 0, false, false)]
    [InlineData(0, 0, null, false)]
    [InlineData(1, 100, false, false)]
    [InlineData(2, 0, null, false)]
    [InlineData(null, null, null, false)]
    [InlineData(null, 100, false, false)]
    public void ANight_IsClosed_WhenStoppedOrSoldOutOnADayThatIsOnSale(int? availability, int? price, bool? stopSell, bool expectedClosed)
    {
        var day = new OctorateCalendarDay("956528", new DateOnly(2026, 11, 10), availability, price, stopSell, 3, 7, true, true);

        Assert.Equal(expectedClosed, day.IsClosed);
    }

    // A day is opened when it has units, or a price, or is stopped, or the answer does not say. Only a day with no unit, no price and nothing
    // stopped was never opened; a calendar made of those alone is no live calendar of the room.
    [Theory]
    [InlineData(0, 0, null, false)]
    [InlineData(0, 0, false, false)]
    [InlineData(-1, 0, false, false)]
    [InlineData(0, 0, true, true)]
    [InlineData(1, 0, null, true)]
    [InlineData(0, 100, null, true)]
    [InlineData(0, null, null, true)]
    [InlineData(null, 0, null, true)]
    [InlineData(null, null, null, true)]
    public void ADay_IsOpened_UnlessItHasNoUnitNoPriceAndIsNotStopped(int? availability, int? price, bool? stopSell, bool expectedOpened)
    {
        var day = new OctorateCalendarDay("956528", new DateOnly(2026, 11, 10), availability, price, stopSell, null, null, null, null);

        Assert.Equal(expectedOpened, day.IsOpened);
        // A closed night is always an opened day: the two never disagree.
        Assert.True(!day.IsClosed || day.IsOpened);
    }

    [Fact]
    public void TheRestrictionsOfAnOpenDay_CloseNothing()
    {
        var day = new OctorateCalendarDay("956528", new DateOnly(2026, 11, 10), 1, 100m, false, 5, 10, true, true);

        Assert.False(day.IsClosed);
    }
}
