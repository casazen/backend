using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-16 (A5-29, A9-30): the anonymous legacy portal <c>/api/checkin/{token}</c> is removed. It returned every personal
/// data field of the guest and accepted anonymous document uploads with a token stored in clear on the booking. Its
/// routes answer 404 and change nothing; the guest check-in goes through <c>/api/public/checkin/{token}</c> only.
/// </summary>
public class LegacyCheckInPortalRemovedIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly CasazenWebApplicationFactory _factory;

    public LegacyCheckInPortalRemovedIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task LegacyCheckInRoutes_ConfirmedBooking_Return404AndChangeNothing()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        using var client = _factory.CreateClient();

        var responses = new List<HttpResponseMessage>();
        foreach (var token in new[] { seed.BookingId, Guid.NewGuid() })
        {
            responses.Add(await client.GetAsync($"/api/checkin/{token}"));
            responses.Add(await client.PostAsync($"/api/checkin/{token}/guest-data", GuestDataContent()));
            responses.Add(await client.PostAsync($"/api/checkin/{token}/document", DocumentContent()));
        }

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        foreach (var response in responses)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Verdi", body, StringComparison.Ordinal);
            Assert.DoesNotContain("documentScanUrl", body, StringComparison.OrdinalIgnoreCase);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == seed.BookingId);
        Assert.Equal(seed.GuestId, booking.GuestId);
        var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seed.GuestId);
        Assert.Null(guest.DocumentScanUrl);
        Assert.Null(guest.ConsentDate);
        Assert.Equal(string.Empty, guest.DocumentNumber);
        Assert.False(Directory.Exists(Path.Combine(_factory.StorageRoot, "private", "guest-documents", booking.OrgId.ToString())));
    }

    [Fact]
    public async Task PublicCheckInPortal_ValidSession_StillAnswersOnItsOwnRoute()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var token = await CreatePortalSessionAsync(seed.BookingId);
        using var client = _factory.CreateClient();

        var portal = await client.GetAsync($"/api/public/checkin/{token}");
        var legacy = await client.GetAsync($"/api/checkin/{token}");

        Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
    }

    private async Task<string> CreatePortalSessionAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
        // The host's own service (its clock and options): the session is valid for the API that reads it.
        var service = scope.ServiceProvider.GetRequiredService<IGuestCheckInService>();
        return await service.CreateSessionAsync(bookingId, booking.OrgId);
    }

    private static StringContent GuestDataContent() => new(
        """
        {
          "dateOfBirth": "1990-05-15",
          "placeOfBirth": "Roma",
          "nationality": "Italiana",
          "gender": "Male",
          "documentType": "Passport",
          "documentNumber": "YA1234567",
          "documentIssuingCountry": "Italia",
          "consentAccepted": true
        }
        """,
        Encoding.UTF8,
        "application/json");

    private static MultipartFormDataContent DocumentContent()
    {
        var form = new MultipartFormDataContent();
        var scan = new ByteArrayContent(PngBytes);
        scan.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(scan, "file", "carta-identita.png");
        return form;
    }
}
