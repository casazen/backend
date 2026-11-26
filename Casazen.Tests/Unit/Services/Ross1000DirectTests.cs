using System.Net;
using System.Text;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Ross1000;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public sealed class Ross1000DirectTests
{
    private static readonly DateOnly Day = new(2026, 11, 25);
    private static readonly Ross1000Capacity Open = new(true, 1, 2, 3);

    private static Booking Family()
    {
        StayGuest Guest(StayGuestType type, int position) => new()
        {
            Type = type, Position = position, DateOfBirth = new DateTime(2000, 2, 4),
            CitizenshipCode = "100000100", ResidenceInItaly = true, ResidenceComuneCode = "403015146",
            Gender = Gender.Female, LastName = "NeverTransmitGuestName", DocumentNumber = "NeverTransmitDocument"
        };
        return new Booking
        {
            ArrivedAt = new DateTime(2026, 11, 24, 23, 15, 0, DateTimeKind.Utc),
            CheckedOutAt = new DateTime(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2, Status = BookingStatus.CheckedOut,
            StayGuests = [Guest(StayGuestType.FamilyMember, 1), Guest(StayGuestType.HeadOfFamily, 0)]
        };
    }

    [Fact]
    public void ArrivalAndDepartureUseTheSameGuestKeysAndLocalDate()
    {
        var booking = Family();
        var arrival = Ross1000MovementBuilder.Build("test", Day, Day, Open, [booking]);
        var departure = Ross1000MovementBuilder.Build("test", Day.AddDays(1), Day, Open, [booking]);
        Assert.Equal(2, arrival.Arrivals);
        Assert.Equal(2, departure.Departures);
        var rows = arrival.Xml.Descendants("arrivo").ToArray();
        Assert.Equal(rows[0].Element("idswh")!.Value, rows[1].Element("idcapo")!.Value);
        Assert.Equal(rows.Select(r => r.Element("idswh")!.Value), departure.Xml.Descendants("idswh").Select(e => e.Value));
        Assert.All(departure.Xml.Descendants("arrivo"), e => Assert.Equal("20261125", e.Value));
        Assert.DoesNotContain("NeverTransmit", arrival.Xml.ToString());
        Assert.Equal("legacy_checkout", Assert.Throws<Ross1000Problem>(() =>
            Ross1000MovementBuilder.Build("test", Day.AddDays(1), Day.AddDays(1), Open, [booking])).Message);
    }

    [Fact]
    public void ClosedDayStillReportsCapacityAndNoArrivals()
    {
        var movement = Ross1000MovementBuilder.Build("test", Day, Day, new(false, 0, 0, 0), []);
        Assert.Equal("NO", movement.Xml.Descendants("apertura").Single().Value);
        Assert.Empty(movement.RecordKeys);
        Assert.Throws<Ross1000Problem>(() => Ross1000MovementBuilder.Build("test", Day, Day, new(false, 0, 1, 1), []));
    }

    private static XDocument Response(Ross1000Movement m, bool success = true)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace ws = "http://checkin.ws.service.turismo5.gies.it/";
        return new XDocument(new XElement(soap + "Envelope", new XElement(soap + "Body", new XElement(ws + "inviaMovimentazioneListResponse",
            new XElement("return", new XElement("struttura", new XAttribute("codice", "test"),
            new XElement("giorno", new XAttribute("data", "20261125"), new XElement("arrivi", m.Xml.Descendants("idswh").Select(i =>
                new XElement("arrivo", new XElement(i), new XElement("successo", success)))))))))));
    }

    [Fact]
    public void ChecksEveryExpectedResultAndTheReportingDay()
    {
        var m = Ross1000MovementBuilder.Build("test", Day, Day, Open, [Family()]);
        var valid = Response(m);
        Assert.All(Ross1000Client.Interpret(valid, m).Records, r => Assert.Equal("accepted", r.State));
        valid.Descendants("successo").First().Value = "false";
        var partial = Ross1000Client.Interpret(valid, m);
        Assert.Equal(("partial", 1, 1), (partial.State, partial.Accepted, partial.Rejected));
        Assert.Equal("rejected", partial.Records.First().State);
        Assert.Equal("accepted", partial.Records.Last().State);
        valid.Descendants("arrivo").First().Remove();
        Assert.Equal("unknown", Ross1000Client.Interpret(valid, m).State);
        var wrong = Response(m);
        wrong.Descendants("giorno").Single().SetAttributeValue("data", "20261124");
        Assert.Equal("unknown", Ross1000Client.Interpret(wrong, m).State);
        Assert.Equal("rejected", Ross1000Client.Interpret(Response(m, false), m).State);
    }

    [Fact]
    public void EmptyDayNeedsANamedDayWithoutAnError()
    {
        var m = Ross1000MovementBuilder.Build("test", Day, Day, new(false, 0, 0, 0), []);
        var xml = Response(m);
        Assert.Equal("accepted", Ross1000Client.Interpret(xml, m).State);
        var day = xml.Descendants("giorno").Single();
        day.Add(new XElement("errore", "data bloccata"));
        Assert.Equal("unknown", Ross1000Client.Interpret(xml, m).State);
        day.Remove();
        Assert.Equal("unknown", Ross1000Client.Interpret(xml, m).State);
    }

    [Fact]
    public async Task SoapClientUsesOnlyTheOfficialEndpointAndHandlesARefusalWithoutExposingSecrets()
    {
        var m = Ross1000MovementBuilder.Build("test", Day, Day, Open, [Family()]);
        using var handler = new Handler(async req =>
        {
            Assert.Equal(Ross1000Client.Lombardia, req.RequestUri);
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("Basic", req.Headers.Authorization?.Scheme);
            var body = await req.Content!.ReadAsStringAsync();
            Assert.Contains("inviaMovimentazioneList", body);
            Assert.DoesNotContain("SecretFixture", body);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var http = new HttpClient(handler);
        var response = await new Ross1000Client(http).SendAsync(new PropertyIstatCredentials { Username = "user", Password = "SecretFixture" }, m, default);
        Assert.Equal("credentials_rejected", response.State);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> exec) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => exec(request);
    }
}
