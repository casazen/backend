using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// NF-05: the per-channel marketing consent of the public check-in over HTTP. The JSON names the frontend sends reach the
/// service, the errors come back keyed by property together with the ones of the guests, and the consent is stored for the
/// ticked channel only, not confirmed.
/// </summary>
public class PublicCheckInMarketingConsentIntegrationTests : IClassFixture<MarketingConsentConfiguredFactory>
{
    private readonly MarketingConsentConfiguredFactory _factory;

    public PublicCheckInMarketingConsentIntegrationTests(MarketingConsentConfiguredFactory factory) => _factory = factory;

    [Fact]
    public async Task Submit_EmailChannelTicked_RejectsAMissingEmailThenStoresOnlyThatChannelNotConfirmed()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var token = await CreateSessionAsync(seed.BookingId);
        using var client = _factory.CreateClient();

        var rejected = await client.PostAsync(
            $"/api/public/checkin/{token}",
            Payload(email: true, marketingEmail: null, documentType: "AlienPermit"));

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var errors = await ReadFieldErrorsAsync(rejected);
        Assert.Equal(
            "Indica l'indirizzo email a cui vuoi ricevere le comunicazioni promozionali.",
            Assert.Single(errors["MarketingEmail"]));
        Assert.Equal("Tipo di documento non valido.", Assert.Single(errors["Guests[0].DocumentType"]));
        Assert.Empty(await RecordsAsync(seed.BookingId));

        var accepted = await client.PostAsync(
            $"/api/public/checkin/{token}",
            Payload(email: true, marketingEmail: "Luigi.Offerte@Example.it"));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var record = Assert.Single(await RecordsAsync(seed.BookingId));
        Assert.Equal(
            ("luigi.offerte@example.it", true, false, false, MarketingConsentConfiguredFactory.Version, seed.GuestId),
            (record.MarketingEmail, record.ConsentEmail, record.ConsentWhatsApp, record.EmailConfirmed, record.ConsentTextVersion, record.GuestId));
    }

    private async Task<List<MarketingConsentRecord>> RecordsAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.MarketingConsentRecords.IgnoreQueryFilters().AsNoTracking().Where(r => r.BookingId == bookingId).ToListAsync();
    }

    private async Task<string> CreateSessionAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.FindAsync(bookingId);
        var service = new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance);
        return await service.CreateSessionAsync(bookingId, booking!.OrgId);
    }

    private static async Task<Dictionary<string, string[]>> ReadFieldErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_error", json.RootElement.GetProperty("code").GetString());
        return json.RootElement.GetProperty("errors")
            .EnumerateObject()
            .ToDictionary(
                field => field.Name,
                field => field.Value.EnumerateArray().Select(message => message.GetString() ?? string.Empty).ToArray());
    }

    private static StringContent Payload(
        bool email = false,
        bool whatsApp = false,
        string? marketingEmail = null,
        string documentType = "Passport")
    {
        var body = new
        {
            guests = new[]
            {
                new
                {
                    type = "SingleGuest",
                    firstName = "Luigi",
                    lastName = "Verdi",
                    gender = "Male",
                    dateOfBirth = "1990-05-15",
                    bornInItaly = true,
                    birthComuneName = "Roma",
                    birthProvince = "RM",
                    citizenshipName = "Italia",
                    documentType,
                    documentNumber = "YA1234567",
                    documentIssuePlaceName = "Roma",
                },
            },
            marketingConsentEmail = email,
            marketingConsentWhatsApp = whatsApp,
            marketingEmail,
        };
        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }
}
