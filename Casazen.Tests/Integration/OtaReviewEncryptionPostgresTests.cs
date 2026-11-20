using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// NF-69 (E05/F06/US01/T01) on a real PostgreSQL database, where it counts: what a guest wrote in a review of a channel, the reply of the host and the host's own review of the
/// guest are stored as a Data Protection payload and the table never holds them in clear, no plaintext column for them exists, and a second writer of the same row is refused by
/// <c>xmin</c> (no reply is saved twice). Same style as <see cref="OtaMessageEncryptionPostgresTests"/>.
/// </summary>
public class OtaReviewEncryptionPostgresTests
{
    private const string GuestWords = "Appartamento pulito e silenzioso, host gentilissimo: ci torneremo con i bambini";
    private const string ReplyWords = "Grazie di cuore! Vi aspettiamo di nuovo la prossima primavera";
    private const string HostReviewWords = "{\"ratings\":[{\"category\":\"cleanliness\",\"value\":5}],\"text\":\"Ospiti ordinati\",\"privateText\":\"Hanno lasciato la casa in ordine perfetto\"}";

    // Whole texts and pieces of them that cannot appear by chance in a base64url payload (a comma, a space, a long word).
    private static readonly string[] Secrets =
    [
        GuestWords, ReplyWords, HostReviewWords, "Appartamento pulito", "gentilissimo", "bambini", "Grazie di cuore", "prossima primavera", "Ospiti ordinati", "ordine perfetto", "privateText",
    ];

    /// <summary>
    /// Privacy rule: the three texts of a review are not plaintext in the database. The table holds the payload only, there is no plaintext column, and read through EF with Data
    /// Protection the clear texts come back.
    /// </summary>
    [PostgresFact]
    public async Task OtaReview_TextIsEncryptedAtRest_Test()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("otarevenc");
        var provider = new EphemeralDataProtectionProvider();
        var (orgId, bookingId) = await SeedAirbnbStayAsync(database);

        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, provider))
        {
            var booking = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == bookingId);
            db.OtaReviews.Add(NewReview(booking, GuestWords, ReplyWords, HostReviewWords));
            await db.SaveChangesAsync();
        }

        // The table itself, whatever EF would have made of it: a context without Data Protection reads the stored text.
        await using var stored = database.CreateContext();
        var columns = new[] { "GuestReviewTextEncrypted", "HostReplyTextEncrypted", "HostReviewJsonEncrypted" };
        var queries = new[]
        {
            "SELECT \"GuestReviewTextEncrypted\" AS \"Value\" FROM \"OtaReviews\"",
            "SELECT \"HostReplyTextEncrypted\" AS \"Value\" FROM \"OtaReviews\"",
            "SELECT \"HostReviewJsonEncrypted\" AS \"Value\" FROM \"OtaReviews\"",
        };
        foreach (var query in queries)
        {
            var texts = await stored.Database.SqlQueryRaw<string>(query).ToListAsync();

            var text = Assert.Single(texts);
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, text, StringComparison.Ordinal);
            foreach (var secret in Secrets)
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        // No plaintext column for the words exists: the only ones are the encrypted ones.
        var names = await stored.Database
            .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'OtaReviews'")
            .ToListAsync();
        Assert.All(columns, column => Assert.Contains(column, names));
        Assert.DoesNotContain("GuestReviewText", names);
        Assert.DoesNotContain("HostReplyText", names);
        Assert.DoesNotContain("HostReviewJson", names);

        // The same through the model of a context without Data Protection: the property holds the payload.
        var row = await stored.OtaReviews.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.OrgId == orgId);
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, row.GuestReviewText, StringComparison.Ordinal);
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, row.HostReplyText, StringComparison.Ordinal);
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, row.HostReviewJson, StringComparison.Ordinal);

        // Read through EF with Data Protection the clear texts come back.
        await using var reader = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        var readBack = await reader.OtaReviews.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.OrgId == orgId);
        Assert.Equal(GuestWords, readBack.GuestReviewText);
        Assert.Equal(ReplyWords, readBack.HostReplyText);
        Assert.Equal(HostReviewWords, readBack.HostReviewJson);
    }

    /// <summary>
    /// Data rule: two writers that read the same row cannot both save it. <c>xmin</c> is the concurrency token, so the second one is refused and reads the truth: a reply of the host is
    /// saved, and so sent, once; and a second row for the same booking and channel is refused by the unique index.
    /// </summary>
    [PostgresFact]
    public async Task OtaReview_SecondWriterOfTheSameRow_IsRefusedByXmin_AndOneRowPerBookingAndChannel_Test()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("otarevxmin");
        var provider = new EphemeralDataProtectionProvider();
        var (_, bookingId) = await SeedAirbnbStayAsync(database);

        Guid reviewId;
        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, provider))
        {
            var booking = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == bookingId);
            var review = NewReview(booking, GuestWords, null, null);
            db.OtaReviews.Add(review);
            await db.SaveChangesAsync();
            reviewId = review.Id;
        }

        await using var first = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        await using var second = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        var seenByFirst = await first.OtaReviews.SingleAsync(r => r.Id == reviewId);
        var seenBySecond = await second.OtaReviews.SingleAsync(r => r.Id == reviewId);

        seenByFirst.HostReplyText = ReplyWords;
        seenByFirst.HostReplyIdempotencyKey = "reply-key-one";
        seenByFirst.HostReplyState = OtaReviewActionState.ManualVerify;
        await first.SaveChangesAsync();

        seenBySecond.HostReplyText = "Una risposta scritta da un altro schermo";
        seenBySecond.HostReplyIdempotencyKey = "reply-key-two";
        seenBySecond.HostReplyState = OtaReviewActionState.ManualVerify;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var read = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        var kept = await read.OtaReviews.AsNoTracking().SingleAsync(r => r.Id == reviewId);
        Assert.Equal("reply-key-one", kept.HostReplyIdempotencyKey);
        Assert.Equal(ReplyWords, kept.HostReplyText);

        // One row per organization, booking and channel: a second one for the same booking is refused by the database.
        await using var duplicate = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        var booking2 = await duplicate.Bookings.AsNoTracking().FirstAsync(b => b.Id == bookingId);
        duplicate.OtaReviews.Add(NewReview(booking2, null, null, null));
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
    }

    private static OtaReview NewReview(Booking booking, string? guestText, string? replyText, string? hostReviewJson) => new()
    {
        OrgId = booking.OrgId,
        PropertyId = booking.PropertyId,
        BookingId = booking.Id,
        Channel = OtaChannel.Airbnb,
        OctorateRefer = booking.OctorateReservationId ?? "refer-1",
        GuestReviewPublished = guestText is not null,
        GuestReviewPublishedAt = guestText is null ? null : new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc),
        GuestRating = guestText is null ? null : 5m,
        GuestCategoryRatingsJson = guestText is null ? null : "[{\"category\":\"cleanliness\",\"value\":5}]",
        GuestReviewText = guestText,
        HostReplyText = replyText,
        HostReplyState = replyText is null ? OtaReviewActionState.None : OtaReviewActionState.Sent,
        HostReviewJson = hostReviewJson,
        HostReviewState = hostReviewJson is null ? OtaReviewActionState.None : OtaReviewActionState.Sent,
        HostReviewWindow = hostReviewJson is null ? HostReviewWindow.Open : HostReviewWindow.Sent,
        NextPollAt = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>A stay of Airbnb that ended on 10 November 2026, with its guest, in an org the scenario of the hosts seeds.</summary>
    private static async Task<(Guid OrgId, Guid BookingId)> SeedAirbnbStayAsync(PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        var world = await HostScopeScenario.SeedAsync(db);
        var guest = new Guest { OrgId = world.OrgId, FirstName = "Anna", LastName = "Prova", Email = $"{Guid.NewGuid():N}@example.com" };
        var checkOut = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var booking = new Booking
        {
            OrgId = world.OrgId,
            PropertyId = world.Granted.Id,
            GuestId = guest.Id,
            CheckInDate = checkOut.AddDays(-3),
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Airbnb,
            OctorateReservationId = "880011",
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return (world.OrgId, booking.Id);
    }
}
