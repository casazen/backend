using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The reading of the reservations of Octorate (E02/F02/US01/T01): the names the sandbox runs recorded, what a missing member means (unknown,
/// never invented), and the shapes of the list.
/// </summary>
public class OctorateReservationReaderTests
{
    [Fact]
    public void Read_ASingleReservationWithItsPaymentsAndPriceBreakdown()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": 7001, "refer": "R-55", "channelId": 274, "channelName": "Airbnb", "channelRefer": "HMABCD1234", "otaId": "OTA-9",
              "status": "CONFIRMED", "product": "956528", "pmsProduct": "956530",
              "checkin": "2026-11-09T23:00:00Z", "checkout": "2026-11-12T10:00:00Z", "updateTime": "2026-11-05T10:15:30Z",
              "roomGross": 300.00, "totalGross": 330.5, "touristTax": "6.00", "totalGuest": 3, "paymentStatus": "PARTIALLY_PAID",
              "firstName": "Giulia", "lastName": "Rossi", "totalPaid": 100.10, "currency": "eur",
              "priceBreakdown": [
                { "type": "ROOM_NET", "amount": 250.00 },
                { "type": "COMMISSION", "amount": 45.25 },
                { "type": "COMMISSION", "amount": 4.75 },
                { "type": "VAT", "amount": 30.5 }
              ],
              "privateNotes": "nota riservata dell'host",
              "guests": [ { "firstName": "Altro", "lastName": "Ospite", "emailAvailable": true, "phoneAvailable": false } ]
            }
            """);

        var reservation = OctorateReservationReader.Read(document.RootElement)!;

        Assert.Equal("7001", reservation.Id);
        Assert.Equal("R-55", reservation.Refer);
        Assert.Equal("274", reservation.ChannelId);
        Assert.Equal("Airbnb", reservation.ChannelName);
        Assert.Equal("HMABCD1234", reservation.ChannelRefer);
        Assert.Equal("OTA-9", reservation.OtaId);
        Assert.Equal(OctorateBookingStatus.Confirmed, reservation.Status);
        Assert.Equal("956528", reservation.Product);
        Assert.Equal("956530", reservation.PmsProduct);
        Assert.Equal("2026-11-09T23:00:00Z", reservation.CheckIn);
        Assert.Equal("2026-11-12T10:00:00Z", reservation.CheckOut);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 15, 30, DateTimeKind.Utc), reservation.UpdatedAt);
        Assert.Equal(300.00m, reservation.RoomGross);
        Assert.Equal(330.5m, reservation.TotalGross);
        Assert.Equal(6.00m, reservation.TouristTax);
        Assert.Equal(3, reservation.TotalGuests);
        Assert.Equal("PARTIALLY_PAID", reservation.PaymentStatus);
        Assert.Equal("Giulia", reservation.FirstName);
        Assert.Equal("Rossi", reservation.LastName);
        Assert.Equal(100.10m, reservation.TotalPaid);
        Assert.Equal("EUR", reservation.Currency);
        Assert.Equal(50.00m, reservation.Commission);
        Assert.Equal(4, reservation.PriceBreakdown.Count);

        // The flags of the guests are not contact values; the private notes of the host are never read.
        Assert.Null(reservation.GuestEmail);
        Assert.Null(reservation.GuestPhone);
        Assert.Null(reservation.Notes);
    }

    [Fact]
    public void Read_AListItemHasNoPaymentsAndNoPriceBreakdown_AndWhatIsMissingIsUnknown()
    {
        using var document = JsonDocument.Parse("""{ "id": "7002", "status": "WAITING", "checkin": "2026-12-01", "checkout": "2026-12-04" }""");

        var reservation = OctorateReservationReader.Read(document.RootElement)!;

        Assert.Equal(OctorateBookingStatus.Waiting, reservation.Status);
        Assert.Null(reservation.TotalPaid);
        Assert.Null(reservation.Commission);
        Assert.Empty(reservation.PriceBreakdown);
        Assert.Null(reservation.UpdatedAt);
        Assert.Null(reservation.TotalGross);
        Assert.Null(reservation.Currency);
        Assert.Null(reservation.TotalGuests);
    }

    // ApiReservationRespDTO.currency is an object with a currencyCode (OpenAPI 2026-11-04); a plain code is read too. Anything else is no currency,
    // and the amounts of a reservation with none are not imported as if they were in the currency of the property.
    [Theory]
    [InlineData("""{ "id": "1", "currency": "eur" }""", "EUR")]
    [InlineData("""{ "id": "1", "currency": { "currencyCode": "chf", "numericCode": 756, "displayName": "Swiss Franc" } }""", "CHF")]
    [InlineData("""{ "id": "1", "currencyCode": "EUR" }""", "EUR")]
    [InlineData("""{ "id": "1", "currency": { "numericCode": 978 } }""", null)]
    [InlineData("""{ "id": "1", "currency": "euro" }""", null)]
    [InlineData("""{ "id": "1", "currency": 978 }""", null)]
    [InlineData("""{ "id": "1" }""", null)]
    public void Read_TheCurrencyIsTheCodeOrTheObjectOfTheOpenApi_AndNullWhenTheAnswerStatesNone(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, OctorateReservationReader.Read(document.RootElement)!.Currency);
    }

    [Theory]
    [InlineData("""{ "id": "1", "status": "CANCELLED" }""", OctorateBookingStatus.Cancelled)]
    [InlineData("""{ "id": "1", "status": "canceled" }""", OctorateBookingStatus.Cancelled)]
    [InlineData("""{ "id": "1", "status": "confirmed" }""", OctorateBookingStatus.Confirmed)]
    [InlineData("""{ "id": "1", "status": "NO_SHOW" }""", null)]
    [InlineData("""{ "id": "1" }""", null)]
    public void Read_AStatusThatIsNoneOfTheThreeIsUnknown(string json, OctorateBookingStatus? expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, OctorateReservationReader.Read(document.RootElement)!.Status);
    }

    // E06/F02/US01/T04: the customer record of the address book that holds the invoice is the invoiceHolderId of the reservation and nothing else. A
    // guestId, or the id of a guest of the stay, is another table (the record it picks could be another customer's); zero, a sign, a fraction or a word
    // is no id at all.
    [Theory]
    [InlineData("""{ "id": "1", "invoiceHolderId": 77001 }""", "77001")]
    [InlineData("""{ "id": "1", "invoiceHolderId": "77001" }""", "77001")]
    [InlineData("""{ "id": "1", "InvoiceHolderId": 5 }""", "5")]
    [InlineData("""{ "id": "1", "invoiceHolderId": 0 }""", null)]
    [InlineData("""{ "id": "1", "invoiceHolderId": -4 }""", null)]
    [InlineData("""{ "id": "1", "invoiceHolderId": 12.5 }""", null)]
    [InlineData("""{ "id": "1", "invoiceHolderId": "abc" }""", null)]
    [InlineData("""{ "id": "1", "invoiceHolderId": null }""", null)]
    [InlineData("""{ "id": "1", "invoiceHolderId": 123456789012345678901234567890123456789012345678901234567890123456789 }""", null)]
    [InlineData("""{ "id": "1", "guestId": 77001, "guests": [ { "id": 67152873 } ] }""", null)]
    [InlineData("""{ "id": "1" }""", null)]
    public void Read_TheCustomerRecordOfTheInvoiceIsTheInvoiceHolderIdAndNothingElse(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, OctorateReservationReader.Read(document.RootElement)!.InvoiceHolderId);
    }

    [Fact]
    public void Read_TheContactOfTheGuestOnlyWhenTheAnswerCarriesOne()
    {
        using var document = JsonDocument.Parse("""
            { "id": "7003", "guests": [ { "firstName": "Luca", "lastName": "Bianchi", "email": "luca.bianchi@example.com", "phone": "+393331112233" } ] }
            """);

        var reservation = OctorateReservationReader.Read(document.RootElement)!;

        Assert.Equal("Luca", reservation.FirstName);
        Assert.Equal("Bianchi", reservation.LastName);
        Assert.Equal("luca.bianchi@example.com", reservation.GuestEmail);
        Assert.Equal("+393331112233", reservation.GuestPhone);
    }

    [Theory]
    [InlineData("""{ "id": "1", "email": "not-an-address" }""")]
    [InlineData("""{ "id": "1", "email": "two words@example.com" }""")]
    public void Read_ADoubtfulEmailIsNoEmail(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(OctorateReservationReader.Read(document.RootElement)!.GuestEmail);
    }

    [Theory]
    [InlineData("""{ "updateTime": "2026-11-05T10:15:30Z" }""", 2026, 11, 5, 10, 15, 30)]
    [InlineData("""{ "updateTime": "2026-11-05T10:15:30" }""", 2026, 11, 5, 10, 15, 30)]
    [InlineData("""{ "updateTime": "2026-11-05T11:15:30+01:00" }""", 2026, 11, 5, 10, 15, 30)]
    [InlineData("""{ "updateTime": 1793873730 }""", 2026, 11, 5, 10, 15, 30)]
    [InlineData("""{ "updateTime": 1793873730000 }""", 2026, 11, 5, 10, 15, 30)]
    public void Read_TheUpdateTimeIsAnInstantInUtc_WhateverItsForm(string json, int year, int month, int day, int hour, int minute, int second)
    {
        using var document = JsonDocument.Parse(json.Replace("{ \"updateTime\"", "{ \"id\": \"9\", \"updateTime\"", StringComparison.Ordinal));

        var updated = OctorateReservationReader.Read(document.RootElement)!.UpdatedAt;

        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc), updated);
    }

    [Fact]
    public void ReadPage_ItemsWithoutAUsableIdAreLeftOut_ButCounted()
    {
        using var document = JsonDocument.Parse("""[ { "status": "CONFIRMED" }, "text", { "id": "7004" }, { "id": "%s" } ]""".Replace("%s", new string('9', 51), StringComparison.Ordinal));

        var page = OctorateReservationReader.ReadPage(document.RootElement)!;

        // Four items, one reservation that can be read: the end of a list is decided on the items the page held, never on the ones that were read.
        Assert.Equal(["7004"], page.Reservations.Select(r => r.Id));
        Assert.Equal(4, page.ItemCount);
        Assert.Equal(3, page.Unreadable);
    }

    [Fact]
    public void ReadPage_AnEmptyListIsAPageWithNoItem_AndAnAnswerThatIsNotAListIsNoPage()
    {
        using var empty = JsonDocument.Parse("""{ "data": [], "page": { "number": 3 } }""");
        using var notAList = JsonDocument.Parse("""{ "message": "ok" }""");

        var page = OctorateReservationReader.ReadPage(empty.RootElement)!;

        Assert.Equal(0, page.ItemCount);
        Assert.Empty(page.Reservations);
        Assert.Equal(0, page.Unreadable);
        Assert.Null(OctorateReservationReader.ReadPage(notAList.RootElement));
    }

    [Fact]
    public void Read_TheIdTheCallAskedForIsUsedWhenTheAnswerDoesNotRepeatIt()
    {
        using var document = JsonDocument.Parse("""{ "status": "CONFIRMED" }""");

        Assert.Equal("7005", OctorateReservationReader.Read(document.RootElement, "7005")!.Id);
        Assert.Null(OctorateReservationReader.Read(document.RootElement));
    }

    [Theory]
    [InlineData("""[ { "id": "1" }, { "id": "2" } ]""", 2)]
    [InlineData("""{ "data": [ { "id": "1" } ], "page": { "number": 0 } }""", 1)]
    [InlineData("""{ "content": [ ] }""", 0)]
    [InlineData("""[ ]""", 0)]
    public void ListItems_ABareArrayOrTheArrayOfAWrapper(string json, int expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, OctorateReservationReader.ListItems(document.RootElement)!.Count());
    }

    [Theory]
    [InlineData("""{ "message": "ok" }""")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void ListItems_AnAnswerThatIsNotAListIsNotAnEmptyList(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(OctorateReservationReader.ListItems(document.RootElement));
    }
}
