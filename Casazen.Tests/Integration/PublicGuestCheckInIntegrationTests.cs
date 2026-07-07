using System.Net;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Integration tests for the guest check-in portal (AC15 — 6 scenarios).
/// </summary>
public class PublicGuestCheckInIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PublicGuestCheckInIntegrationTests(CasazenWebApplicationFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task AC15_1_GetToken_ValidToken_Returns200WithContext()
    {
        var (token, _, _) = await SeedSessionAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/public/checkin/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("sessionId", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("InCompilazione", body);
    }

    [Fact]
    public async Task AC15_2_Submit_ValidData_Returns200()
    {
        var (token, _, _) = await SeedSessionAsync();
        var client = _factory.CreateClient();
        _ = await client.GetAsync($"/api/public/checkin/{token}");

        var response = await client.PostAsync(
            $"/api/public/checkin/{token}",
            BuildSubmitContent());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AC15_3_DuplicateSubmit_Returns409()
    {
        var (token, _, _) = await SeedSessionAsync();
        var client = _factory.CreateClient();
        _ = await client.GetAsync($"/api/public/checkin/{token}");

        _ = await client.PostAsync($"/api/public/checkin/{token}", BuildSubmitContent());

        var response = await client.PostAsync(
            $"/api/public/checkin/{token}",
            BuildSubmitContent());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AC15_4_ExpiredToken_Returns404()
    {
        var (token, sessionId, _) = await SeedSessionAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.GuestCheckInSessions.FindAsync(sessionId);
        session!.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/public/checkin/{token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetToken_CancelledBooking_Returns404()
    {
        var (token, _, _) = await SeedSessionAsync(BookingStatus.Cancelled);
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/public/checkin/{token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Submit_CancelledBooking_Returns404AndDoesNotUpdateGuest()
    {
        var (token, _, guestId) = await SeedSessionAsync(BookingStatus.Cancelled);
        var client = _factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/public/checkin/{token}",
            BuildSubmitContent());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = await db.Guests.FindAsync(guestId);
        Assert.Null(guest!.DocumentNumber);
        Assert.Null(guest.ConsentDate);
    }

    [Fact]
    public async Task AC15_5_InvalidToken_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/public/checkin/invalidtoken00000000000000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AC15_6_GdprConsentFalse_Returns400()
    {
        var (token, _, _) = await SeedSessionAsync();
        var client = _factory.CreateClient();
        _ = await client.GetAsync($"/api/public/checkin/{token}");

        var response = await client.PostAsync(
            $"/api/public/checkin/{token}",
            new StringContent(BuildSubmitPayload(gdprConsent: false), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<(string Token, Guid SessionId, Guid GuestId)> SeedSessionAsync(
        BookingStatus bookingStatus = BookingStatus.Confirmed)
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Look up the OrgId from the seeded booking
        var booking = await db.Bookings.FindAsync(seed.BookingId);
        var orgId = booking!.OrgId;
        booking.Status = bookingStatus;
        await db.SaveChangesAsync();

        var service = new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance);
        var token = await service.CreateSessionAsync(seed.BookingId, orgId);
        var session = await db.GuestCheckInSessions.OrderByDescending(s => s.CreatedAt).FirstAsync();

        return (token, session.Id, seed.GuestId);
    }

    private static StringContent BuildSubmitContent(bool gdprConsent = true) =>
        new(BuildSubmitPayload(gdprConsent), Encoding.UTF8, "application/json");

    private static string BuildSubmitPayload(bool gdprConsent = true) =>
        $$"""
        {
          "firstName": "Luigi",
          "lastName": "Verdi",
          "dateOfBirth": "1990-05-15",
          "nationality": "Italiana",
          "documentType": "Passport",
          "documentNumber": "YA1234567",
          "documentIssuingCountry": "Italia",
          "placeOfBirth": "Roma",
          "gdprConsent": {{(gdprConsent ? "true" : "false")}},
          "marketingConsent": false
        }
        """;
}
