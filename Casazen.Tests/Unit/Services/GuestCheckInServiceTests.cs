using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class GuestCheckInServiceTests
{
    private record Seed(AppDbContext Db, Guid BookingId, Guid GuestId, Guid OrgId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"checkin-{Guid.NewGuid()}")
            .Options);

    private static async Task<Seed> SeedAsync()
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        db.Guests.Add(new Guest
        {
            Id = guestId,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "mario@example.com",
        });

        db.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "owner-test",
            Name = "Test Property",
            Address = "Via Roma 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            IsActive = true,
        });

        db.Bookings.Add(new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            GuestId = guestId,
            OrgId = orgId,
            CheckInDate = DateTime.UtcNow.Date.AddDays(3),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(6),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        });

        await db.SaveChangesAsync();
        return new Seed(db, bookingId, guestId, orgId);
    }

    [Fact]
    public async Task CreateSession_StoresHashedToken_ReturnsRawToken()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);

        var rawToken = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        Assert.NotNull(rawToken);
        Assert.Equal(64, rawToken.Length); // 32 bytes ΓåÆ 64 hex chars

        var session = await seed.Db.GuestCheckInSessions.FirstAsync();
        Assert.NotEqual(rawToken, session.TokenHash); // hash differs from raw
        Assert.Equal(GuestCheckInSessionStatus.Inviato, session.Status);
    }

    [Fact]
    public async Task CreateSession_WhenUnopenedSessionExists_PreservesPreviousTokenAndCreatesReplacement()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var firstToken = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        var replacementToken = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        Assert.NotEqual(firstToken, replacementToken);
        var sessions = await seed.Db.GuestCheckInSessions
            .OrderBy(s => s.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(GuestCheckInSessionStatus.InCompilazione, sessions[0].Status);
        Assert.Equal(GuestCheckInSessionStatus.Inviato, sessions[1].Status);

        var previousSession = await svc.GetSessionByTokenAsync(firstToken);
        Assert.NotNull(previousSession);
        Assert.Equal(GuestCheckInSessionStatus.InCompilazione, previousSession.Status);
    }

    [Fact]
    public async Task GetSessionByToken_ValidToken_TransitionsToInCompilazione()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        var session = await svc.GetSessionByTokenAsync(token);

        Assert.NotNull(session);
        Assert.Equal(GuestCheckInSessionStatus.InCompilazione, session.Status);
    }

    [Fact]
    public async Task GetSessionByToken_ExpiredToken_ReturnsNull()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        var sess = await seed.Db.GuestCheckInSessions.FirstAsync();
        sess.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await seed.Db.SaveChangesAsync();

        var result = await svc.GetSessionByTokenAsync(token);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSessionByToken_CancelledBooking_ReturnsNull()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        var booking = await seed.Db.Bookings.FindAsync(seed.BookingId);
        booking!.Status = BookingStatus.Cancelled;
        await seed.Db.SaveChangesAsync();

        var result = await svc.GetSessionByTokenAsync(token);

        Assert.Null(result);
        var session = await seed.Db.GuestCheckInSessions.FirstAsync();
        Assert.Equal(GuestCheckInSessionStatus.Inviato, session.Status);
    }

    [Fact]
    public async Task GetSessionByToken_InvalidToken_ReturnsNull()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);

        var result = await svc.GetSessionByTokenAsync("000000000000000000000000000000000000000000000000000000000000abcd");
        Assert.Null(result);
    }

    [Fact]
    public async Task Submit_ValidDataWithConsent_ReturnsSuccess()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        _ = await svc.GetSessionByTokenAsync(token); // advance to InCompilazione

        var result = await svc.SubmitAsync(token, BuildValidSubmitRequest());

        Assert.True(result.Success);
        Assert.False(result.Duplicate);
        Assert.Equal(seed.BookingId, result.BookingId);
        Assert.Equal(seed.GuestId, result.GuestId);

        var session = await seed.Db.GuestCheckInSessions.FirstAsync();
        Assert.Equal(GuestCheckInSessionStatus.Completo, session.Status);

        var guest = await seed.Db.Guests.FindAsync(seed.GuestId);
        Assert.Equal("YA1234567", guest!.DocumentNumber);
        Assert.Equal(Gender.Male, guest.Gender);
        Assert.Equal(DateTime.UtcNow.Year + 7, guest.DataRetentionUntil.Year);

        // CO-12: the stay's guest line, linked to the booker.
        var stayGuest = await seed.Db.StayGuests.SingleAsync();
        Assert.Equal(StayGuestType.SingleGuest, stayGuest.Type);
        Assert.Equal(0, stayGuest.Position);
        Assert.Equal(seed.GuestId, stayGuest.GuestId);
        Assert.Equal(seed.OrgId, stayGuest.OrgId);
        Assert.Equal("Roma", stayGuest.BirthComuneName);
        Assert.Equal("RM", stayGuest.BirthProvince);
    }

    [Fact]
    public async Task Submit_FamilyOfThree_SavesOneRowPerGuestInOrderAndDocumentOnlyForTheHead()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        var request = BuildValidSubmitRequest(type: "HeadOfFamily");
        request.Guests =
        [
            request.Guests[0],
            Member("Anna", documentNumber: "IGNORED123"),
            Member("Luca", dateOfBirth: new DateTime(2019, 6, 1)),
        ];

        var result = await svc.SubmitAsync(token, request);

        Assert.True(result.Success, string.Join(", ", result.ValidationErrors.Select(e => $"{e.Index}.{e.Field}")));
        var rows = await seed.Db.StayGuests.OrderBy(s => s.Position).ToListAsync();
        Assert.Equal(
            new[] { StayGuestType.HeadOfFamily, StayGuestType.FamilyMember, StayGuestType.FamilyMember },
            rows.Select(r => r.Type));
        Assert.Equal(new[] { "Luigi", "Anna", "Luca" }, rows.Select(r => r.FirstName));
        Assert.Equal("YA1234567", rows[0].DocumentNumber);
        Assert.All(rows.Skip(1), r =>
        {
            Assert.Equal(string.Empty, r.DocumentNumber);
            Assert.Null(r.DocumentType);
            Assert.Null(r.GuestId);
        });
    }

    [Fact]
    public async Task Submit_HeadOfFamilyWithoutDocument_ReturnsDocumentErrorsOnTheHeadOnly()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        var request = BuildValidSubmitRequest(type: "HeadOfFamily", documentType: "", documentNumber: "");
        request.Guests = [request.Guests[0], Member("Anna")];

        var result = await svc.SubmitAsync(token, request);

        Assert.False(result.Success);
        Assert.Equal(
            new[] { "0.DocumentType", "0.DocumentNumber" },
            result.ValidationErrors.Select(e => $"{e.Index}.{e.Field}"));
        Assert.Empty(seed.Db.StayGuests);
    }

    [Fact]
    public async Task Submit_MemberWithoutHead_ReturnsCompositionError()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        var request = BuildValidSubmitRequest();
        request.Guests = [request.Guests[0], Member("Anna")];

        var result = await svc.SubmitAsync(token, request);

        Assert.False(result.Success);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal((1, "Type", CheckInValidationKeys.MemberWithoutHead), (error.Index, error.Field, error.MessageKey));
        Assert.Equal("Guests[1].Type", error.ModelStateKey("Guests"));
    }

    [Fact]
    public async Task Submit_InvalidDocumentType_ReturnsValidationFailureWithoutCompletingSession()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        _ = await svc.GetSessionByTokenAsync(token);

        var result = await svc.SubmitAsync(token, BuildValidSubmitRequest(documentType: "AlienPermit"));

        Assert.False(result.Success);
        Assert.False(result.Duplicate);
        AssertSingleError(result, 0, nameof(StayGuestInput.DocumentType), CheckInValidationKeys.DocumentTypeInvalid);

        var session = await seed.Db.GuestCheckInSessions.FirstAsync();
        Assert.Equal(GuestCheckInSessionStatus.InCompilazione, session.Status);

        var guest = await seed.Db.Guests.FindAsync(seed.GuestId);
        Assert.Equal(string.Empty, guest!.DocumentNumber);
        Assert.Null(guest.DocumentType);
        Assert.Null(guest.ConsentDate);
    }

    [Fact]
    public async Task Submit_SharedGuest_CreatesSnapshotBeforeWritingCheckInData()
    {
        await using var seed = await SeedAsync();
        var sharedBookingId = await AddBookingSharingGuestAsync(seed.Db, seed.GuestId);
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        _ = await svc.GetSessionByTokenAsync(token);

        var result = await svc.SubmitAsync(token, BuildValidSubmitRequest());

        Assert.True(result.Success);
        Assert.True(result.GuestId.HasValue);
        var snapshotGuestId = result.GuestId.Value;
        Assert.NotEqual(seed.GuestId, snapshotGuestId);

        var originalGuest = await seed.Db.Guests.AsNoTracking().SingleAsync(g => g.Id == seed.GuestId);
        Assert.Equal(string.Empty, originalGuest.DocumentNumber);
        Assert.Null(originalGuest.ConsentDate);

        var snapshot = await seed.Db.Guests.AsNoTracking().SingleAsync(g => g.Id == snapshotGuestId);
        Assert.Equal("YA1234567", snapshot.DocumentNumber);
        Assert.NotNull(snapshot.ConsentDate);

        var submittedBooking = await seed.Db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId);
        Assert.Equal(snapshotGuestId, submittedBooking.GuestId);

        var sharedBooking = await seed.Db.Bookings.AsNoTracking().SingleAsync(b => b.Id == sharedBookingId);
        Assert.Equal(seed.GuestId, sharedBooking.GuestId);
    }

    [Fact]
    public async Task Submit_CancelledBooking_ReturnsFailureWithoutUpdatingGuest()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        var booking = await seed.Db.Bookings.FindAsync(seed.BookingId);
        booking!.Status = BookingStatus.Cancelled;
        await seed.Db.SaveChangesAsync();

        var result = await svc.SubmitAsync(token, BuildValidSubmitRequest());

        Assert.False(result.Success);
        Assert.False(result.Duplicate);

        var guest = await seed.Db.Guests.FindAsync(seed.GuestId);
        Assert.Equal(string.Empty, guest!.DocumentNumber);
        Assert.Null(guest.Gender);
        Assert.Null(guest.ConsentDate);
    }

    [Fact]
    public async Task Submit_DuplicateSubmit_ReturnsDuplicate()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        _ = await svc.GetSessionByTokenAsync(token);

        var req = BuildValidSubmitRequest();
        _ = await svc.SubmitAsync(token, req);

        var secondResult = await svc.SubmitAsync(token, req);

        Assert.False(secondResult.Success);
        Assert.True(secondResult.Duplicate);
    }

    [Fact]
    public async Task GetSessionByToken_CompletedSession_ReturnsNullButSubmitReturnsDuplicate()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        _ = await svc.GetSessionByTokenAsync(token);
        _ = await svc.SubmitAsync(token, BuildValidSubmitRequest());

        var contextSession = await svc.GetSessionByTokenAsync(token);
        var duplicateSubmit = await svc.SubmitAsync(token, BuildValidSubmitRequest());

        Assert.Null(contextSession);
        Assert.False(duplicateSubmit.Success);
        Assert.True(duplicateSubmit.Duplicate);
    }

    [Fact]
    public async Task Submit_GdprConsentFalse_ReturnsGdprConsentValidationError()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        _ = await svc.GetSessionByTokenAsync(token);

        var result = await svc.SubmitAsync(token, new GuestCheckInSubmitRequest { GdprConsent = false });

        Assert.False(result.Success);
        Assert.False(result.Duplicate);
        AssertSingleError(result, null, nameof(GuestCheckInSubmitRequest.GdprConsent), CheckInValidationKeys.GdprConsentRequired);
    }

    [Fact]
    public async Task Submit_MissingGender_ReturnsFieldRequiredForGender()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        var request = BuildValidSubmitRequest(gender: null);

        var result = await svc.SubmitAsync(token, request);

        Assert.False(result.Success);
        AssertSingleError(result, 0, nameof(StayGuestInput.Gender), CheckInValidationKeys.FieldRequired);
    }

    [Fact]
    public async Task Submit_GenderOther_ReturnsGenderInvalidWithoutCompletingSession()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        var request = BuildValidSubmitRequest(gender: Gender.Other);

        var result = await svc.SubmitAsync(token, request);

        Assert.False(result.Success);
        AssertSingleError(result, 0, nameof(StayGuestInput.Gender), CheckInValidationKeys.GenderInvalid);
        var session = await seed.Db.GuestCheckInSessions.FirstAsync();
        Assert.Equal(GuestCheckInSessionStatus.InCompilazione, session.Status);
        var guest = await seed.Db.Guests.FindAsync(seed.GuestId);
        Assert.Null(guest!.Gender);
    }

    [Fact]
    public async Task GetPublicView_OpenSession_ReturnsContextWithMaskedDocumentNumber()
    {
        await using var seed = await SeedAsync();
        var guest = await seed.Db.Guests.FindAsync(seed.GuestId);
        guest!.DocumentNumber = "YA1234567";
        guest.Gender = Gender.Female;
        await seed.Db.SaveChangesAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        var view = await svc.GetPublicViewAsync(token);

        Assert.NotNull(view);
        Assert.False(view.IsCompleted);
        Assert.Equal(GuestCheckInSessionStatus.InCompilazione, view.Status);
        Assert.Equal("Test Property", view.PropertyName);
        Assert.Equal(1, view.DeclaredGuests);
        Assert.NotNull(view.Guests);
        var prefill = Assert.Single(view.Guests);
        Assert.Equal(StayGuestType.SingleGuest, prefill.Type);
        Assert.Equal("Mario", prefill.FirstName);
        Assert.Equal(Gender.Female, prefill.Gender);
        Assert.Equal("*****567", prefill.DocumentNumberMasked);
        Assert.NotNull(view.AvailableCodeTables);
        Assert.Empty(view.AvailableCodeTables);
    }

    [Fact]
    public async Task GetPublicView_CompletedSession_ReturnsOnlyCompletedStatus()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        var submit = await svc.SubmitAsync(token, BuildValidSubmitRequest());
        Assert.True(submit.Success);

        var view = await svc.GetPublicViewAsync(token);

        Assert.NotNull(view);
        Assert.True(view.IsCompleted);
        Assert.Equal(GuestCheckInSessionStatus.Completo, view.Status);
        Assert.Null(view.SessionId);
        Assert.Null(view.PropertyName);
        Assert.Null(view.CheckInDate);
        Assert.Null(view.CheckOutDate);
        Assert.Null(view.Guests);
    }

    [Fact]
    public async Task GetPublicView_ExpiredToken_ReturnsNull()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        var session = await seed.Db.GuestCheckInSessions.FirstAsync();
        session.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await seed.Db.SaveChangesAsync();

        var view = await svc.GetPublicViewAsync(token);

        Assert.Null(view);
    }

    [Theory]
    [InlineData("YA1234567", "*****567")]
    [InlineData("  CA12345AB ", "*****5AB")]
    [InlineData("AB1234", "*****234")]
    [InlineData("AB123", "*****")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void MaskDocumentNumber_Value_HidesAllButLastCharacters(string? documentNumber, string? expected)
    {
        Assert.Equal(expected, GuestCheckInService.MaskDocumentNumber(documentNumber));
    }

    [Fact]
    public async Task RegenerateToken_ExpiresPreviousSession_ReturnsNewToken()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var firstToken = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        var newToken = await svc.RegenerateTokenAsync(seed.BookingId, seed.OrgId);

        Assert.NotEqual(firstToken, newToken);
        var sessions = await seed.Db.GuestCheckInSessions.ToListAsync();
        Assert.Equal(2, sessions.Count);
        var expired = sessions.Single(s => s.Status == GuestCheckInSessionStatus.Scaduto);
        Assert.NotNull(expired);
    }

    [Fact]
    public async Task ExpireToken_ValidToken_MarksSessionExpired()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        await svc.ExpireTokenAsync(token);

        var session = await seed.Db.GuestCheckInSessions.SingleAsync();
        Assert.Equal(GuestCheckInSessionStatus.Scaduto, session.Status);
    }

    [Fact]
    public async Task ExpireOtherActiveSessions_KeepsReplacementTokenActive()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        _ = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        var replacementToken = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        await svc.ExpireOtherActiveSessionsAsync(seed.BookingId, replacementToken);

        var replacement = await svc.GetSessionByTokenAsync(replacementToken);
        Assert.NotNull(replacement);

        var sessions = await seed.Db.GuestCheckInSessions.ToListAsync();
        Assert.Single(sessions, s => s.Status == GuestCheckInSessionStatus.Scaduto);
        Assert.Single(sessions, s => s.Status == GuestCheckInSessionStatus.InCompilazione);
    }

    [Fact]
    public async Task ExpireOtherActiveSessions_DoesNotExpireCompletedSession()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var completedToken = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        _ = await svc.GetSessionByTokenAsync(completedToken);
        var submit = await svc.SubmitAsync(completedToken, BuildValidSubmitRequest());
        Assert.True(submit.Success);

        var replacementToken = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        await svc.ExpireOtherActiveSessionsAsync(seed.BookingId, replacementToken);

        var sessions = await seed.Db.GuestCheckInSessions.ToListAsync();
        Assert.Single(sessions, s => s.Status == GuestCheckInSessionStatus.Completo);
        Assert.Single(sessions, s => s.Status == GuestCheckInSessionStatus.Inviato);
    }

    private static async Task<Guid> AddBookingSharingGuestAsync(AppDbContext db, Guid guestId)
    {
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        db.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "owner-shared",
            Name = "Shared Guest Property",
            Address = "Via Milano 1",
            City = "Milano",
            PostalCode = "20100",
            NightlyRate = 120m,
            IsActive = true,
        });

        db.Bookings.Add(new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            GuestId = guestId,
            OrgId = orgId,
            CheckInDate = DateTime.UtcNow.Date.AddDays(10),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(12),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        });

        await db.SaveChangesAsync();
        return bookingId;
    }

    private static void AssertSingleError(GuestCheckInSubmitResult result, int? index, string field, string key)
    {
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal((index, field, key), (error.Index, error.Field, error.MessageKey));
    }

    private static GuestCheckInSubmitRequest BuildValidSubmitRequest(
        string type = "SingleGuest",
        string documentType = "Passport",
        string documentNumber = "YA1234567",
        Gender? gender = Gender.Male) => new()
        {
            Guests =
        [
            new StayGuestInput
            {
                Type = type,
                FirstName = "Luigi",
                LastName = "Verdi",
                DateOfBirth = new DateTime(1990, 5, 15, 0, 0, 0, DateTimeKind.Utc),
                Gender = gender,
                BornInItaly = true,
                BirthComuneName = "Roma",
                BirthProvince = "rm",
                CitizenshipName = "Italia",
                DocumentType = documentType,
                DocumentNumber = documentNumber,
                DocumentIssuePlaceName = "Roma",
            },
        ],
            GdprConsent = true,
        };

    private static StayGuestInput Member(string firstName, string? documentNumber = null, DateTime? dateOfBirth = null) => new()
    {
        Type = "FamilyMember",
        FirstName = firstName,
        LastName = "Verdi",
        DateOfBirth = dateOfBirth ?? new DateTime(1992, 1, 20, 0, 0, 0, DateTimeKind.Utc),
        Gender = Gender.Female,
        BornInItaly = false,
        BirthCountryName = "Francia",
        CitizenshipName = "Italia",
        DocumentType = documentNumber is null ? null : "Passport",
        DocumentNumber = documentNumber,
    };
}
