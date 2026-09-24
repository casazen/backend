using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task AC3_GetStatus_ArrivalDayWithoutTransmission_IsToSendManuallyNeverSent()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var client = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");

        var response = await client.GetAsync($"/api/alloggiati/{seed.BookingId}/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(seed.BookingId, root.GetProperty("bookingId").GetGuid());
        // The seeded check-in is today: CasaZen has transmitted nothing, so the host must send it (A5-01, A9-05).
        Assert.Equal("DaInviareManualmente", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reportedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("confirmationNumber").ValueKind);
        Assert.True(root.GetProperty("dataComplete").GetBoolean());
        Assert.False(root.GetProperty("isShortStay").GetBoolean());
        Assert.True(root.TryGetProperty("deadlineAt", out _));
    }

    [Fact]
    public async Task AC4_SendEndpoint_NoAlloggiatiClient_Returns422AndRecordsNothing()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var client = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");

        var response = await client.PostAsync($"/api/alloggiati/{seed.BookingId}/send", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("alloggiati_transmission_unavailable", doc.RootElement.GetProperty("code").GetString());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(db.AlloggiatiWebReports.Any(r => r.BookingId == seed.BookingId));
    }

    [Fact]
    public async Task MarkSentManually_Today_RecordsHostDeclarationAndRejectsASecondOne()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var client = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");
        var sentOn = TimeProvider.System.TodayInRome().ToString("yyyy-MM-dd");

        var response = await client.PostAsJsonAsync($"/api/alloggiati/{seed.BookingId}/mark-sent-manually", new { sentOn });
        var second = await client.PostAsJsonAsync($"/api/alloggiati/{seed.BookingId}/mark-sent-manually", new { sentOn });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("InviatoManualmente", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.GetProperty("isOverdue").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var conflict = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("alloggiati_already_sent", conflict.RootElement.GetProperty("code").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var report = db.AlloggiatiWebReports.Single(r => r.BookingId == seed.BookingId);
        Assert.Equal(AlloggiatiWebStatus.InviatoManualmente, report.Status);
        Assert.True(report.ManuallyCompleted);
        Assert.Null(report.ConfirmationNumber);
    }

    [Fact]
    public async Task MarkSentManually_DateInTheFuture_Returns422()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var client = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");
        var sentOn = TimeProvider.System.TodayInRome().AddDays(3).ToString("yyyy-MM-dd");

        var response = await client.PostAsJsonAsync($"/api/alloggiati/{seed.BookingId}/mark-sent-manually", new { sentOn });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("alloggiati_manual_date_in_future", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MarkSentManually_WithoutDate_Returns400()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var client = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");

        var response = await client.PostAsJsonAsync($"/api/alloggiati/{seed.BookingId}/mark-sent-manually", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MarkSentManually_OtherOwnersBooking_Returns403AndRecordsNothing()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var intruder = _factory.CreateAuthenticatedClient($"auth0|intruder-{Guid.NewGuid():N}", roles: "PropertyOwner");
        var sentOn = TimeProvider.System.TodayInRome().ToString("yyyy-MM-dd");

        var response = await intruder.PostAsJsonAsync($"/api/alloggiati/{seed.BookingId}/mark-sent-manually", new { sentOn });

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(db.AlloggiatiWebReports.Any(r => r.BookingId == seed.BookingId));
    }

    [Fact]
    public async Task AC5_PortalSubmitThenOwnerCheckIn_SchedulesTheAlloggiatiJobOnce()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var token = await CreatePortalSessionAsync(seed.BookingId);
        var guestClient = _factory.CreateClient();
        var owner = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");

        var submit = await guestClient.PostAsync($"/api/public/checkin/{token}", BuildPortalPayload());
        var checkIn = await owner.PostAsync($"/api/bookings/{seed.BookingId}/check-in", null);

        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(
                It.Is<Job>(j => IsReportJobOf(j, seed.BookingId)),
                It.IsAny<ScheduledState>()),
            Times.Once);
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(It.Is<Job>(j => IsReportJobOf(j, seed.BookingId)), It.IsAny<EnqueuedState>()),
            Times.Never);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var report = db.AlloggiatiWebReports.Single(r => r.BookingId == seed.BookingId);
        Assert.Equal(AlloggiatiWebStatus.DaInviare, report.Status);
        Assert.NotNull(report.ScheduledJobId);
        Assert.NotNull(db.Bookings.Single(b => b.Id == seed.BookingId).ArrivedAt);
        // No receipt: the guest session must not claim that Alloggiati was sent.
        Assert.Equal(
            GuestCheckInSessionStatus.Completo,
            db.GuestCheckInSessions.Single(s => s.BookingId == seed.BookingId).Status);
    }

    [Fact]
    public async Task GuestSummary_Owner_ReturnsTheRecordFieldsToCopy()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var client = _factory.CreateAuthenticatedClient(seed.OwnerId, roles: "PropertyOwner");

        var response = await client.GetAsync($"/api/alloggiati/{seed.BookingId}/guest-summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("stayDays").GetInt32());
        Assert.Equal(2, root.GetProperty("declaredGuests").GetInt32());
        Assert.True(root.GetProperty("dataComplete").GetBoolean());
        // No official code table imported in the test database: the codes are still to complete.
        Assert.False(root.GetProperty("exportReady").GetBoolean());
        Assert.Equal(4, root.GetProperty("missingCodeTables").GetArrayLength());
        var guests = root.GetProperty("guests").EnumerateArray().ToList();
        Assert.Equal(2, guests.Count);
        var head = guests[0];
        Assert.Equal("HeadOfFamily", head.GetProperty("type").GetString());
        Assert.Equal(0, head.GetProperty("position").GetInt32());
        Assert.Equal("Verdi", head.GetProperty("lastName").GetString());
        Assert.Equal("Luigi", head.GetProperty("firstName").GetString());
        Assert.Equal("Male", head.GetProperty("gender").GetString());
        Assert.Equal("Milano", head.GetProperty("birthComune").GetString());
        Assert.Equal("MI", head.GetProperty("birthProvince").GetString());
        Assert.Equal("Italia", head.GetProperty("citizenship").GetString());
        Assert.True(head.GetProperty("requiresDocument").GetBoolean());
        Assert.Equal("Passport", head.GetProperty("documentType").GetString());
        // CO-09: masked like on the guest portal, the full number only on an explicit request.
        Assert.Equal("*****456", head.GetProperty("documentNumberMasked").GetString());
        Assert.False(head.TryGetProperty("documentNumber", out _));
        Assert.Equal("Milano", head.GetProperty("documentIssuePlace").GetString());
        Assert.Equal(0, head.GetProperty("missingFields").GetArrayLength());
        Assert.True(head.GetProperty("codesToComplete").GetArrayLength() > 0);
        var member = guests[1];
        Assert.Equal("FamilyMember", member.GetProperty("type").GetString());
        Assert.True(member.GetProperty("isMinor").GetBoolean());
        Assert.False(member.GetProperty("requiresDocument").GetBoolean());
        Assert.Equal(JsonValueKind.Null, member.GetProperty("documentNumberMasked").ValueKind);
        Assert.Equal(0, member.GetProperty("missingFields").GetArrayLength());
    }

    [Fact]
    public async Task GuestSummary_OtherOwnersBooking_DoesNotExposeTheDocument()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var intruder = _factory.CreateAuthenticatedClient($"auth0|intruder-{Guid.NewGuid():N}", roles: "PropertyOwner");

        var response = await intruder.GetAsync($"/api/alloggiati/{seed.BookingId}/guest-summary");

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.DoesNotContain("AB123456", await response.Content.ReadAsStringAsync());
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
    public async Task Summary_WithoutPropertyId_ExcludesOtherOrgBookings()
    {
        var ownerSeed = await _factory.SeedConfirmedBookingWithTokenAsync(
            ownerId: $"auth0|alloggiati-owner-{Guid.NewGuid():N}");
        var otherSeed = await _factory.SeedConfirmedBookingWithTokenAsync(
            ownerId: $"auth0|alloggiati-other-{Guid.NewGuid():N}");
        var client = _factory.CreateAuthenticatedClient(ownerSeed.OwnerId, roles: "PropertyOwner");

        var response = await client.GetAsync("/api/alloggiati/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bookingIds = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("bookingId").GetGuid()).ToList();
        Assert.Contains(ownerSeed.BookingId, bookingIds);
        Assert.DoesNotContain(otherSeed.BookingId, bookingIds);
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

    [Fact]
    public async Task GuestDataSubmit_SharedGuest_CreatesSnapshotAndLeavesOriginalGuestUnchanged()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var sharedBookingId = await SeedBookingSharingGuestAsync(seed.GuestId);
        var client = _factory.CreateClient();

        var payload = BuildGuestDataPayload(includeDob: true);
        var response = await client.PostAsync(
            $"/api/checkin/{seed.CheckInToken}/guest-data",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var originalGuest = db.Guests.Single(g => g.Id == seed.GuestId);
        Assert.Equal(string.Empty, originalGuest.DocumentNumber);
        Assert.Null(originalGuest.ConsentDate);

        var submittedBooking = db.Bookings.Single(b => b.Id == seed.BookingId);
        Assert.NotEqual(seed.GuestId, submittedBooking.GuestId);

        var snapshot = db.Guests.Single(g => g.Id == submittedBooking.GuestId);
        Assert.Equal("YA1234567", snapshot.DocumentNumber);
        Assert.NotNull(snapshot.ConsentDate);

        var sharedBooking = db.Bookings.Single(b => b.Id == sharedBookingId);
        Assert.Equal(seed.GuestId, sharedBooking.GuestId);
    }

    [Fact]
    public async Task GuestDataSubmit_CheckedOutBooking_Returns404AndDoesNotUpdateGuest()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        await MarkBookingStatusAsync(seed.BookingId, BookingStatus.CheckedOut);
        var client = _factory.CreateClient();

        var payload = BuildGuestDataPayload(includeDob: true);
        var response = await client.PostAsync(
            $"/api/checkin/{seed.CheckInToken}/guest-data",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = db.Guests.Single(g => g.Id == seed.GuestId);
        Assert.Null(guest.ConsentDate);
        Assert.Equal(string.Empty, guest.DocumentNumber);
    }

    [Fact]
    public async Task DocumentUpload_CheckedOutBooking_Returns404AndDoesNotUpdateGuest()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        await MarkBookingStatusAsync(seed.BookingId, BookingStatus.CheckedOut);
        var client = _factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", "id-scan.png");

        var response = await client.PostAsync($"/api/checkin/{seed.CheckInToken}/document", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = db.Guests.Single(g => g.Id == seed.GuestId);
        Assert.Null(guest.DocumentScanUrl);
    }

    private static bool IsReportJobOf(Job job, Guid bookingId) =>
        job.Type == typeof(AlloggiatiWebReportJob)
        && job.Method.Name == nameof(AlloggiatiWebReportJob.ReportGuestAsync)
        && job.Args.Count == 2
        && job.Args[1] is Guid id && id == bookingId;

    private async Task<string> CreatePortalSessionAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.FindAsync(bookingId);
        var service = new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance);
        return await service.CreateSessionAsync(bookingId, booking!.OrgId);
    }

    private static StringContent BuildPortalPayload() => new(
        """
        {
          "guests": [
            {
              "type": "SingleGuest",
              "firstName": "Luigi",
              "lastName": "Verdi",
              "gender": "Male",
              "dateOfBirth": "1985-03-10",
              "bornInItaly": true,
              "birthComuneName": "Milano",
              "birthProvince": "MI",
              "citizenshipName": "Italia",
              "documentType": "Passport",
              "documentNumber": "AB123456",
              "documentIssuePlaceName": "Milano"
            }
          ],
          "gdprConsent": true,
          "marketingConsent": false
        }
        """,
        Encoding.UTF8,
        "application/json");

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

    private async Task MarkBookingStatusAsync(Guid bookingId, BookingStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = db.Bookings.Single(b => b.Id == bookingId);
        booking.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedBookingSharingGuestAsync(Guid guestId)
    {
        var ownerId = $"auth0|shared-guest-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);
        var bookingId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Bookings.Add(new Booking
        {
            Id = bookingId,
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guestId,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(10),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(12),
            NumberOfGuests = 1,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 200m,
            TotalPrice = 208m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return bookingId;
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
            OrgId = property.OrgId,
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
            CheckInDate = TimeProvider.System.TodayInRome(),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(3),
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

        if (completeGuestData)
        {
            // CO-12: the two guests of the stay, as registered by the guest portal (head of family and a child).
            db.StayGuests.Add(new StayGuest
            {
                BookingId = bookingId,
                OrgId = property.OrgId,
                GuestId = guestId,
                Position = 0,
                Type = StayGuestType.HeadOfFamily,
                FirstName = "Luigi",
                LastName = "Verdi",
                Gender = Gender.Male,
                DateOfBirth = new DateTime(1985, 3, 10, 0, 0, 0, DateTimeKind.Utc),
                BornInItaly = true,
                BirthComuneName = "Milano",
                BirthProvince = "MI",
                CitizenshipName = "Italia",
                DocumentType = GuestDocumentType.Passport,
                DocumentNumber = "AB123456",
                DocumentIssuePlaceName = "Milano",
            });
            db.StayGuests.Add(new StayGuest
            {
                BookingId = bookingId,
                OrgId = property.OrgId,
                Position = 1,
                Type = StayGuestType.FamilyMember,
                FirstName = "Sofia",
                LastName = "Verdi",
                Gender = Gender.Female,
                DateOfBirth = TimeProvider.System.TodayInRome().AddYears(-8),
                BornInItaly = true,
                BirthComuneName = "Milano",
                BirthProvince = "MI",
                CitizenshipName = "Italia",
            });
        }

        await db.SaveChangesAsync();

        return new ConfirmedBookingSeed(bookingId, guestId, property.Id, checkInToken, ownerId);
    }
}
