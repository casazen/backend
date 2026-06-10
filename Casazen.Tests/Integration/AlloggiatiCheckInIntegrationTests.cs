using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

public class AlloggiatiCheckInIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public AlloggiatiCheckInIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AC1_GuestDataMissingDob_Returns400()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var client = _factory.CreateClient();

        var payload = BuildGuestDataPayload(includeDob: false);
        var response = await client.PostAsync(
            $"/api/checkin/{seed.CheckInToken}/guest-data",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AC2_DocumentUpload_ReturnsUrl()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var client = _factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", "id-scan.png");

        var response = await client.PostAsync($"/api/checkin/{seed.CheckInToken}/document", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("documentScanUrl").GetString()));
    }

    [Fact]
    public async Task AC3_GetAlloggiatiStatus_ReturnsStatusDto()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var client = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");

        var response = await client.GetAsync($"/api/alloggiati/{seed.BookingId}/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(seed.BookingId, root.GetProperty("bookingId").GetGuid());
        Assert.True(root.TryGetProperty("status", out _));
        Assert.True(root.TryGetProperty("dataComplete", out _));
        Assert.True(root.TryGetProperty("hoursUntilDeadline", out _));
    }

    [Fact]
    public async Task AC4_ManualSend_UpdatesReport()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var client = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");

        var response = await client.PostAsync($"/api/alloggiati/{seed.BookingId}/send", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Submitted", doc.RootElement.GetProperty("status").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var report = db.AlloggiatiWebReports.Single(r => r.BookingId == seed.BookingId);
        Assert.True(report.ManuallyCompleted);
    }

    [Fact]
    public async Task AC5_OwnerCheckIn_EnqueuesAlloggiatiReportJob()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var client = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");

        _ = await client.PostAsync($"/api/bookings/{seed.BookingId}/check-in", null);

        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(
                It.Is<Job>(j =>
                    j.Type == typeof(AlloggiatiWebReportJob) &&
                    j.Method.Name == nameof(AlloggiatiWebReportJob.ReportGuestAsync)),
                It.IsAny<EnqueuedState>()),
            Times.Once);
    }

    [Fact]
    public async Task AC7_SummaryListsBookings()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var client = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");

        var response = await client.GetAsync("/api/alloggiati/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetArrayLength() >= 1);
        var bookingIds = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("bookingId").GetGuid()).ToList();
        Assert.Contains(seed.BookingId, bookingIds);
    }

    [Fact]
    public async Task AC11_GuestDataSubmit_SetsConsentFields()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var client = _factory.CreateClient();

        var payload = BuildGuestDataPayload(includeDob: true);
        var response = await client.PostAsync(
            $"/api/checkin/{seed.CheckInToken}/guest-data",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = db.Guests.Single(g => g.Id == seed.GuestId);
        Assert.NotNull(guest.ConsentDate);
        Assert.Equal("2026-06-alloggiati-checkin-v1", guest.ConsentVersion);
    }

    private static string BuildGuestDataPayload(bool includeDob)
    {
        var dob = includeDob ? "\"1990-05-15\"" : "null";
        return $$"""
            {
              "dateOfBirth": {{dob}},
              "placeOfBirth": "Roma",
              "nationality": "Italiana",
              "gender": "Male",
              "documentType": "Passport",
              "documentNumber": "YA1234567",
              "documentExpiryDate": "2030-12-31",
              "documentIssuingCountry": "Italia",
              "address": "Via Roma 1",
              "city": "Roma",
              "postalCode": "00100",
              "country": "Italia",
              "consentAccepted": true
            }
            """;
    }
}

public sealed record ConfirmedBookingSeed(
    Guid BookingId,
    Guid GuestId,
    Guid PropertyId,
    Guid CheckInToken,
    string OwnerId);

public static class AlloggiatiTestSeedExtensions
{
    public static async Task<ConfirmedBookingSeed> SeedConfirmedBookingWithTokenAsync(
        this CasazenWebApplicationFactory factory,
        bool completeGuestData = false,
        string? ownerId = null)
    {
        ownerId ??= $"auth0|alloggiati-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(ownerId);
        var checkInToken = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var guest = new Guest
        {
            Id = guestId,
            FirstName = "Luigi",
            LastName = "Verdi",
            Email = $"guest-{guestId:N}@example.com",
            PhoneNumber = "+393331234567",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        if (completeGuestData)
        {
            guest.DateOfBirth = new DateTime(1985, 3, 10, 0, 0, 0, DateTimeKind.Utc);
            guest.PlaceOfBirth = "Milano";
            guest.Nationality = "Italiana";
            guest.Gender = Gender.Male;
            guest.DocumentType = GuestDocumentType.Passport;
            guest.DocumentNumber = "AB123456";
            guest.DocumentIssuingCountry = "Italia";
        }

        db.Guests.Add(guest);

        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guestId,
            CheckInDate = DateTime.UtcNow.Date,
            CheckOutDate = DateTime.UtcNow.Date.AddDays(3),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            CheckInToken = checkInToken,
            BasePrice = 300m,
            TotalPrice = 312m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        return new ConfirmedBookingSeed(bookingId, guestId, property.Id, checkInToken, ownerId);
    }
}
