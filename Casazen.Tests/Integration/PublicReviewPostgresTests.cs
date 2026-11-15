using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E26/F01/US01/T01 (the review of slice S016) on a real PostgreSQL database: the data rule of the event of a review, one <c>ReviewReceived</c> for each review, holds when the guest
/// sends the same link twice at the same moment. EF InMemory proves the check that comes before the write (the second submission of a review that was saved is refused), never the unique
/// index on the booking: it keeps none, so the write of the second submission, the refusal of the database and what the service leaves in its unit of work are proved here.
/// </summary>
public class PublicReviewPostgresTests
{
    // The stay ended on 11 November 2026 and the services think it is the 12th in Rome, the first day to review.
    private static readonly DateTime CheckOut = new(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Data rule: two submissions of the same link at the same moment make one review and one event. Both read "no review yet" and add their review and their event; each is held when it is
    /// about to write, until both are there, so the unique index on the booking refuses the one that writes second (23505). The refused one answers 409
    /// <c>review_already_submitted</c> and leaves neither its review nor its event in its unit of work; the database keeps one review and the one <c>ReviewReceived</c> of that review.
    /// </summary>
    [PostgresFact]
    public async Task Submit_TheSameLinkTwiceAtTheSameMoment_LeavesOneReviewAndOneEvent_Test()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("reviewonce");
        var (orgId, bookingId) = await SeedDirectStayAsync(database);
        var keys = new EphemeralDataProtectionProvider();
        var token = new ReviewLinkTokens(keys).Create(bookingId, DateTimeOffset.UtcNow.AddDays(1));
        var command = new SubmitReviewCommand(5, "Un soggiorno perfetto.", true);

        var rendezvous = new ReviewRendezvous(parties: 2);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString, npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .AddInterceptors(rendezvous)
            .Options;
        await using var one = new AppDbContext(options);
        await using var other = new AppDbContext(options);

        var outcomes = await Task.WhenAll(
            SubmitAsync(one, keys, token, bookingId, command),
            SubmitAsync(other, keys, token, bookingId, command));

        Assert.True(rendezvous.BothArrived, "The two submissions did not reach the write together.");
        var published = Assert.Single(outcomes, o => o.Review is not null).Review!;
        var refused = Assert.Single(outcomes, o => o.Refused is not null).Refused!;
        Assert.Equal("review_already_submitted", refused.Code);

        // The refused submission leaves nothing behind in its unit of work: neither its review nor its event.
        var loser = outcomes[0].Refused is not null ? one : other;
        Assert.Empty(loser.ChangeTracker.Entries().Where(e => e.State == EntityState.Added));

        await using var read = database.CreateContext();
        var review = Assert.Single(await read.Reviews.AsNoTracking().Where(r => r.BookingId == bookingId).ToListAsync());
        Assert.Equal(published.Id, review.Id);
        var recorded = Assert.Single(await read.OutboxEvents.AsNoTracking().Where(e => e.OrgId == orgId && e.EventType == DomainEventType.ReviewReceived).ToListAsync());
        Assert.Equal(review.Id, recorded.EntityId);
        Assert.Equal(ReviewReceivedPayload.DedupKeyOf(review.Id), recorded.DedupKey);
    }

    /// <summary>The submission of the link on a context of its own, as one request of the guest: the review that was published, or the 409 it was refused with.</summary>
    private static async Task<(PublicReview? Review, DomainConflictException? Refused)> SubmitAsync(
        AppDbContext db,
        IDataProtectionProvider keys,
        string token,
        Guid bookingId,
        SubmitReviewCommand command)
    {
        var clock = new FixedTimeProvider(Now);
        var service = new PublicReviewService(
            db,
            new ReviewLinkTokens(keys),
            NullLogger<PublicReviewService>.Instance,
            clock,
            new DomainEventRecorder(db, NullTenantContext.Instance, clock));

        try
        {
            return (await service.SubmitAsync(bookingId, token, command), null);
        }
        catch (DomainConflictException refused)
        {
            return (null, refused);
        }
    }

    /// <summary>A stay of the booking site that ended the day before the services' today, with its guest, in an org the scenario of the hosts seeds.</summary>
    private static async Task<(Guid OrgId, Guid BookingId)> SeedDirectStayAsync(PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        var world = await HostScopeScenario.SeedAsync(db);
        var guest = new Guest { OrgId = world.OrgId, FirstName = "Anna", LastName = "Prova", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = world.OrgId,
            PropertyId = world.Granted.Id,
            GuestId = guest.Id,
            CheckInDate = CheckOut.AddDays(-3),
            CheckOutDate = CheckOut,
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return (world.OrgId, booking.Id);
    }

    /// <summary>
    /// Holds each context about to write a new review until <paramref name="parties"/> of them are there, so the submissions that run at once have all read "no review yet" before any of
    /// them writes.
    /// </summary>
    private sealed class ReviewRendezvous(int parties) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public bool BothArrived => _allArrived.Task.IsCompletedSuccessfully;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Review>().Any(e => e.State == EntityState.Added))
            {
                if (Interlocked.Increment(ref _arrived) == parties)
                    _allArrived.TrySetResult();
                await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return result;
        }
    }
}
