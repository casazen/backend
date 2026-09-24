using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
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
        var (token, _, guestId) = await SeedSessionAsync();
        var client = _factory.CreateClient();
        _ = await client.GetAsync($"/api/public/checkin/{token}");

        var response = await client.PostAsync(
            $"/api/public/checkin/{token}",
            BuildSubmitContent());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = await db.Guests.FindAsync(guestId);
        Assert.Equal(Gender.Male, guest!.Gender);
        var stayGuest = await db.StayGuests.SingleAsync(s => s.GuestId == guestId);
        Assert.Equal(StayGuestType.SingleGuest, stayGuest.Type);
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
        var problem = await ReadJsonAsync(response);
        Assert.Equal("checkin_already_submitted", problem.GetProperty("code").GetString());
        Assert.Equal("Il check-in è già stato completato.", problem.GetProperty("detail").GetString());
    }

    [PostgresFact]
    public async Task GetContext_BeforeCompletion_ReturnsPrefillWithMaskedDocumentNumber()
    {
        Assert.True(_factory.UsesPostgreSql);
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var token = await CreateSessionAsync(seed.BookingId);
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/public/checkin/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("AB123456", body);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.False(root.GetProperty("completed").GetBoolean());
        Assert.Equal("InCompilazione", root.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("propertyName").GetString()));
        Assert.Equal(2, root.GetProperty("declaredGuests").GetInt32());
        Assert.Equal(0, root.GetProperty("availableCodeTables").GetArrayLength());
        var guests = root.GetProperty("guests").EnumerateArray().ToList();
        Assert.Equal(2, guests.Count);
        var prefill = guests[0];
        Assert.Equal("HeadOfFamily", prefill.GetProperty("type").GetString());
        Assert.Equal("*****456", prefill.GetProperty("documentNumberMasked").GetString());
        Assert.False(prefill.TryGetProperty("documentNumber", out _));
        Assert.Equal("Male", prefill.GetProperty("gender").GetString());
        Assert.Equal("Luigi", prefill.GetProperty("firstName").GetString());
        Assert.Equal("FamilyMember", guests[1].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, guests[1].GetProperty("documentNumberMasked").ValueKind);
    }

    [PostgresFact]
    public async Task GetContext_AfterCompletion_ReturnsOnlyCompletedStatusWithoutPii()
    {
        Assert.True(_factory.UsesPostgreSql);
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var token = await CreateSessionAsync(seed.BookingId);
        var client = _factory.CreateClient();
        _ = await client.GetAsync($"/api/public/checkin/{token}");
        var submit = await client.PostAsync($"/api/public/checkin/{token}", BuildSubmitContent());
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var response = await client.GetAsync($"/api/public/checkin/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        foreach (var pii in new[] { "YA1234567", "567", "Luigi", "Verdi", "1990", "Roma", "Italia", "example.com" })
            Assert.DoesNotContain(pii, body);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.True(root.GetProperty("completed").GetBoolean());
        // Alloggiati is only scheduled for the arrival day: without a receipt the session stays Completo (CO-11).
        Assert.Equal("Completo", root.GetProperty("status").GetString());
        var properties = root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "completed", "status" }, properties);
    }

    [Fact]
    public async Task Submit_FrontendSamplePayload_Returns200AndCompletesSession()
    {
        var (token, sessionId, guestId) = await SeedSessionAsync();
        var client = _factory.CreateClient();
        var payload = await File.ReadAllTextAsync(
            Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "public-checkin-submit.frontend.json"));

        var response = await client.PostAsync(
            $"/api/public/checkin/{token}",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.GuestCheckInSessions.FindAsync(sessionId);
        Assert.NotEqual(GuestCheckInSessionStatus.InCompilazione, session!.Status);
        Assert.NotNull(session.CompletedAt);
        var guest = await db.Guests.FindAsync(guestId);
        Assert.Equal(Gender.Female, guest!.Gender);
        Assert.Equal(GuestDocumentType.IdentityCard, guest.DocumentType);
        var rows = await db.StayGuests.Where(s => s.BookingId == session.BookingId).OrderBy(s => s.Position).ToListAsync();
        Assert.Equal(new[] { StayGuestType.HeadOfFamily, StayGuestType.FamilyMember }, rows.Select(r => r.Type));
        Assert.Equal(string.Empty, rows[1].DocumentNumber);
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
        Assert.Equal(string.Empty, guest!.DocumentNumber);
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
        var errors = await ReadFieldErrorsAsync(response);
        Assert.Equal(
            "Per completare il check-in devi acconsentire al trattamento dei dati.",
            Assert.Single(errors["GdprConsent"]));
    }

    [Fact]
    public async Task Submit_MissingGender_Returns400()
    {
        var (token, _, guestId) = await SeedSessionAsync();
        var client = _factory.CreateClient();
        _ = await client.GetAsync($"/api/public/checkin/{token}");

        var response = await client.PostAsync(
            $"/api/public/checkin/{token}",
            BuildSubmitContent(gender: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ReadFieldErrorsAsync(response);
        Assert.Equal("Campo obbligatorio.", Assert.Single(errors["Guests[0].Gender"]));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = await db.Guests.FindAsync(guestId);
        Assert.Null(guest!.Gender);
    }

    [Fact]
    public async Task Submit_MissingGenderInEnglish_Returns400WithEnglishFieldError()
    {
        var (token, _, _) = await SeedSessionAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        var response = await client.PostAsync($"/api/public/checkin/{token}", BuildSubmitContent(gender: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ReadFieldErrorsAsync(response);
        Assert.Equal("This field is required.", Assert.Single(errors["Guests[0].Gender"]));
    }

    [Fact]
    public async Task Submit_GenderOther_Returns400AndKeepsSessionEditable()
    {
        var (token, sessionId, guestId) = await SeedSessionAsync();
        var client = _factory.CreateClient();
        _ = await client.GetAsync($"/api/public/checkin/{token}");

        var response = await client.PostAsync($"/api/public/checkin/{token}", BuildSubmitContent(gender: "Other"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ReadFieldErrorsAsync(response);
        Assert.Equal(
            "Seleziona maschio o femmina: sono gli unici valori accettati da Alloggiati Web.",
            Assert.Single(errors["Guests[0].Gender"]));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.GuestCheckInSessions.FindAsync(sessionId);
        Assert.Equal(GuestCheckInSessionStatus.InCompilazione, session!.Status);
        var guest = await db.Guests.FindAsync(guestId);
        Assert.Null(guest!.Gender);
    }

    [Fact]
    public async Task Submit_FemaleGender_Returns200AndStoresFemale()
    {
        var (token, _, guestId) = await SeedSessionAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/public/checkin/{token}", BuildSubmitContent(gender: "Female"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = await db.Guests.FindAsync(guestId);
        Assert.Equal(Gender.Female, guest!.Gender);
    }

    [Fact]
    public async Task Submit_InvalidDocumentType_Returns400AndKeepsSessionEditable()
    {
        var (token, sessionId, guestId) = await SeedSessionAsync();
        var client = _factory.CreateClient();
        _ = await client.GetAsync($"/api/public/checkin/{token}");

        var response = await client.PostAsync(
            $"/api/public/checkin/{token}",
            BuildSubmitContent(documentType: "AlienPermit"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ReadFieldErrorsAsync(response);
        Assert.Equal("Tipo di documento non valido.", Assert.Single(errors["Guests[0].DocumentType"]));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.GuestCheckInSessions.FindAsync(sessionId);
        Assert.Equal(GuestCheckInSessionStatus.InCompilazione, session!.Status);

        var guest = await db.Guests.FindAsync(guestId);
        Assert.Null(guest!.DocumentType);
        Assert.Equal(string.Empty, guest.DocumentNumber);
        Assert.Null(guest.ConsentDate);
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

    private async Task<string> CreateSessionAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.FindAsync(bookingId);
        var service = new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance);
        return await service.CreateSessionAsync(bookingId, booking!.OrgId);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task<Dictionary<string, string[]>> ReadFieldErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        return problem.GetProperty("errors")
            .EnumerateObject()
            .ToDictionary(
                field => field.Name,
                field => field.Value.EnumerateArray().Select(message => message.GetString() ?? string.Empty).ToArray());
    }

    private static StringContent BuildSubmitContent(
        bool gdprConsent = true,
        string? gender = "Male",
        string documentType = "Passport") =>
        new(BuildSubmitPayload(gdprConsent, gender, documentType), Encoding.UTF8, "application/json");

    private static string BuildSubmitPayload(
        bool gdprConsent = true,
        string? gender = "Male",
        string documentType = "Passport")
    {
        var genderProperty = gender is not null ? $"\"gender\": \"{gender}\"," : string.Empty;

        return $$"""
        {
          "guests": [
            {
              "type": "SingleGuest",
              "firstName": "Luigi",
              "lastName": "Verdi",
              {{genderProperty}}
              "dateOfBirth": "1990-05-15",
              "bornInItaly": true,
              "birthComuneName": "Roma",
              "birthProvince": "RM",
              "citizenshipName": "Italia",
              "documentType": "{{documentType}}",
              "documentNumber": "YA1234567",
              "documentIssuePlaceName": "Roma"
            }
          ],
          "gdprConsent": {{(gdprConsent ? "true" : "false")}},
          "marketingConsent": false
        }
        """;
    }
}
