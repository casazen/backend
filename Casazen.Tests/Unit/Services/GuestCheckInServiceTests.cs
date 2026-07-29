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
        Assert.Equal(64, rawToken.Length); // 32 bytes → 64 hex chars

        var session = await seed.Db.GuestCheckInSessions.FirstAsync();
        Assert.NotEqual(rawToken, session.TokenHash); // hash differs from raw
        Assert.Equal(GuestCheckInSessionStatus.Inviato, session.Status);
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

        var result = await svc.SubmitAsync(token, new GuestCheckInSubmitRequest
        {
            FirstName = "Luigi",
            LastName = "Verdi",
            DateOfBirth = new DateTime(1990, 5, 15),
            Nationality = "Italiana",
            DocumentType = "Passport",
            DocumentNumber = "YA1234567",
            DocumentIssuingCountry = "Italia",
            PlaceOfBirth = "Roma",
            GdprConsent = true,
        });

        Assert.True(result.Success);
        Assert.False(result.Duplicate);
        Assert.Equal(seed.BookingId, result.BookingId);
        Assert.Equal(seed.GuestId, result.GuestId);

        var session = await seed.Db.GuestCheckInSessions.FirstAsync();
        Assert.Equal(GuestCheckInSessionStatus.Completo, session.Status);

        var guest = await seed.Db.Guests.FindAsync(seed.GuestId);
        Assert.Equal("YA1234567", guest!.DocumentNumber);
        Assert.Equal(DateTime.UtcNow.Year + 7, guest.DataRetentionUntil.Year);
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

        var result = await svc.SubmitAsync(token, new GuestCheckInSubmitRequest
        {
            FirstName = "Luigi",
            LastName = "Verdi",
            DateOfBirth = new DateTime(1990, 5, 15),
            Nationality = "Italiana",
            DocumentType = "Passport",
            DocumentNumber = "YA1234567",
            DocumentIssuingCountry = "Italia",
            PlaceOfBirth = "Roma",
            GdprConsent = true,
        });

        Assert.False(result.Success);
        Assert.False(result.Duplicate);

        var guest = await seed.Db.Guests.FindAsync(seed.GuestId);
        Assert.Equal(string.Empty, guest!.DocumentNumber);
        Assert.Null(guest.ConsentDate);
    }

    [Fact]
    public async Task Submit_DuplicateSubmit_ReturnsDuplicate()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        _ = await svc.GetSessionByTokenAsync(token);

        var req = new GuestCheckInSubmitRequest
        {
            GdprConsent = true,
            DocumentType = "Passport",
            DocumentNumber = "AA999",
            DocumentIssuingCountry = "Italia",
            Nationality = "Italiana",
            PlaceOfBirth = "Milano",
        };
        _ = await svc.SubmitAsync(token, req);

        var secondResult = await svc.SubmitAsync(token, req);

        Assert.False(secondResult.Success);
        Assert.True(secondResult.Duplicate);
    }

    [Fact]
    public async Task Submit_GdprConsentFalse_ReturnsFailure()
    {
        await using var seed = await SeedAsync();
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        _ = await svc.GetSessionByTokenAsync(token);

        var result = await svc.SubmitAsync(token, new GuestCheckInSubmitRequest { GdprConsent = false });

        Assert.False(result.Success);
        Assert.False(result.Duplicate);
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
}
