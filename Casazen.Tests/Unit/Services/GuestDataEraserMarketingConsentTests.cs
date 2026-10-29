using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-05 (Q1-05 item 7): the erasure of a guest includes the per-channel marketing consents of the check-in. The marketing
/// e-mail and the IP are personal data; the consent ends with them, and only time, text version and booking stay as proof.
/// </summary>
public class GuestDataEraserMarketingConsentTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AnonymizeAsync_GuestWithPerChannelConsents_ClearsTheirEmailAndIpEndsTheConsentAndKeepsTheProof()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"eraser-marketing-{Guid.NewGuid()}")
            .Options);
        var orgId = Guid.NewGuid();
        var erased = new Guest { OrgId = orgId, FirstName = "Luigi", LastName = "Verdi", Email = "luigi@example.com" };
        var other = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Neri", Email = "anna@example.com" };
        var grantedAt = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
        var bookingId = Guid.NewGuid();
        db.Guests.AddRange(erased, other);
        db.MarketingConsentRecords.AddRange(
            Consent(orgId, bookingId, erased.Id, "luigi.marketing@example.it", grantedAt),
            Consent(orgId, Guid.NewGuid(), other.Id, "anna.marketing@example.it", grantedAt));
        await db.SaveChangesAsync();
        var eraser = new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance);

        var outcome = await eraser.AnonymizeAsync(erased, Now, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.True(outcome.Changed);
        db.ChangeTracker.Clear();
        var record = await db.MarketingConsentRecords.SingleAsync(r => r.GuestId == erased.Id);
        Assert.Equal((string.Empty, string.Empty), (record.MarketingEmail, record.IpAddress));
        Assert.Equal((false, false, false), (record.ConsentEmail, record.ConsentWhatsApp, record.EmailConfirmed));
        Assert.Equal(("marketing-v2", grantedAt, bookingId), (record.ConsentTextVersion, record.ConsentTimestamp, record.BookingId));

        var untouched = await db.MarketingConsentRecords.SingleAsync(r => r.GuestId == other.Id);
        Assert.Equal(("anna.marketing@example.it", "203.0.113.7", true, true, true), (untouched.MarketingEmail, untouched.IpAddress, untouched.ConsentEmail, untouched.ConsentWhatsApp, untouched.EmailConfirmed));

        // Idempotent: a second run finds nothing left to clear.
        var again = await eraser.AnonymizeAsync(await db.Guests.SingleAsync(g => g.Id == erased.Id), Now.AddDays(1), CancellationToken.None);
        Assert.False(again.Changed);
    }

    private static MarketingConsentRecord Consent(Guid orgId, Guid bookingId, Guid guestId, string email, DateTime grantedAt) => new()
    {
        OrgId = orgId,
        BookingId = bookingId,
        GuestId = guestId,
        MarketingEmail = email,
        ConsentEmail = true,
        ConsentWhatsApp = true,
        EmailConfirmed = true,
        ConsentTimestamp = grantedAt,
        ConsentTextVersion = "marketing-v2",
        IpAddress = "203.0.113.7",
    };
}
