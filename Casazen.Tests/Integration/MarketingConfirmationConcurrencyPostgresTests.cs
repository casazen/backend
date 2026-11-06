using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;

namespace Casazen.Tests.Integration;

/// <summary>
/// E07/F04/US01/T01 (NF-90, AC-NF90-5) on a real PostgreSQL database: the limits of the double opt-in e-mail hold when requests arrive together.
/// The welcome page and the guest page are public, so a script can send many calls for the same address at once; a read of the counters followed
/// by the queue and a save would let all of them through. The send is booked with one conditional update before the message is queued.
/// </summary>
public class MarketingConfirmationConcurrencyPostgresTests : IAsyncLifetime
{
    private const int Together = 8;

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    /// <summary>
    /// Data rule: eight requests for the same new address at the same moment queue one message and count one send; the others are told it was
    /// sent a moment ago. Without the booking of the send each of them reads a counter of zero and queues its own message.
    /// </summary>
    [PostgresFact]
    public async Task MarketingConfirmation_RequestsAtTheSameMoment_QueueOneMessageAndCountOneSend_Test()
    {
        var (orgId, contactId, address) = await SeedConsentContactAsync(sentCount: 0);
        var queue = new RecordingEmailQueue();

        var outcomes = await SendTogetherAsync(orgId, contactId, queue);

        Assert.Equal(1, outcomes.Count(outcome => outcome == MarketingConfirmationSendOutcome.Queued));
        Assert.All(
            outcomes.Where(outcome => outcome != MarketingConfirmationSendOutcome.Queued),
            outcome => Assert.Equal(MarketingConfirmationSendOutcome.TooSoon, outcome));
        Assert.Equal(address, Assert.Single(queue.Snapshot()).To);
        var stored = await ReadAsync(contactId);
        Assert.Equal((1, GuestContactConfirmation.Pending), (stored.ConfirmationSentCount, stored.Confirmation));
        Assert.NotNull(stored.ConfirmationSentAt);
    }

    /// <summary>
    /// Data rule: a person is never mailed more than the maximum, however many requests meet at the last send. The contact has two sends and the
    /// last went out long ago: of eight requests together one takes the third and the others find the limit reached.
    /// </summary>
    [PostgresFact]
    public async Task MarketingConfirmation_RequestsAtTheLastSend_NeverGoBeyondTheMaximum_Test()
    {
        var (orgId, contactId, _) = await SeedConsentContactAsync(sentCount: 2, lastSentMinutesAgo: 11);
        var queue = new RecordingEmailQueue();

        var outcomes = await SendTogetherAsync(orgId, contactId, queue);

        Assert.Equal(1, outcomes.Count(outcome => outcome == MarketingConfirmationSendOutcome.Queued));
        Assert.All(
            outcomes.Where(outcome => outcome != MarketingConfirmationSendOutcome.Queued),
            outcome => Assert.Equal(MarketingConfirmationSendOutcome.LimitReached, outcome));
        Assert.Single(queue.Snapshot());
        Assert.Equal(3, (await ReadAsync(contactId)).ConfirmationSentCount);
    }

    /// <summary>
    /// Data rule: a message the queue refuses is not a send. The booking is given back on the database too (the counter, the time of the last
    /// send, the confirmation), so the next call is not told it is too soon; a request that comes while the e-mail provider is off costs nothing.
    /// </summary>
    [PostgresFact]
    public async Task MarketingConfirmation_QueueRefuses_GivesTheSendBack_Test()
    {
        var (orgId, contactId, address) = await SeedConsentContactAsync(sentCount: 0);
        var queue = new RecordingEmailQueue { Refuse = true };

        await using (var db = _database!.CreateContext())
        {
            var refused = await NewService(db, queue).SendConfirmationAsync(orgId, contactId);

            Assert.Equal(MarketingConfirmationSendOutcome.Unavailable, refused);
        }

        var untouched = await ReadAsync(contactId);
        Assert.Equal((0, GuestContactConfirmation.Unconfirmed), (untouched.ConfirmationSentCount, untouched.Confirmation));
        Assert.Null(untouched.ConfirmationSentAt);
        Assert.Empty(queue.Snapshot());

        queue.Refuse = false;
        await using (var db = _database!.CreateContext())
        {
            var accepted = await NewService(db, queue).SendConfirmationAsync(orgId, contactId);

            Assert.Equal(MarketingConfirmationSendOutcome.Queued, accepted);
        }

        Assert.Equal(address, Assert.Single(queue.Snapshot()).To);
        Assert.Equal(1, (await ReadAsync(contactId)).ConfirmationSentCount);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The same call from <see cref="Together"/> requests, each with a context of its own, released together.</summary>
    private async Task<MarketingConfirmationSendOutcome[]> SendTogetherAsync(Guid orgId, Guid contactId, RecordingEmailQueue queue)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, Together)
            .Select(async _ =>
            {
                await using var db = _database!.CreateContext();
                var service = NewService(db, queue);
                await start.Task;
                return await service.SendConfirmationAsync(orgId, contactId);
            })
            .ToList();

        start.SetResult();
        return await Task.WhenAll(calls);
    }

    private static MarketingConfirmationService NewService(AppDbContext db, RecordingEmailQueue queue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SIGNED_LINK_SECRET"] = "a-long-random-secret-of-the-signed-links" })
            .Build();
        var crm = Options.Create(new CrmOptions());
        return new MarketingConfirmationService(
            db,
            new MarketingConfirmationLinks(configuration, crm, TimeProvider.System),
            EmailTestHelpers.Links(),
            queue,
            CrmTestWorld.Contacts(db, TimeProvider.System),
            crm,
            TimeProvider.System,
            NullLogger<MarketingConfirmationService>.Instance);
    }

    private async Task<GuestContact> ReadAsync(Guid contactId)
    {
        await using var db = _database!.CreateContext();
        return await db.GuestContacts.AsNoTracking().SingleAsync(c => c.Id == contactId);
    }

    /// <summary>An org and a consent on an e-mail address that is not confirmed, with <paramref name="sentCount"/> messages already sent.</summary>
    private async Task<(Guid OrgId, Guid ContactId, string Address)> SeedConsentContactAsync(int sentCount, int lastSentMinutesAgo = 0)
    {
        await using var db = _database!.CreateContext();
        var org = new OrgEntity
        {
            Name = "Concurrency Org",
            Slug = $"cnc-{Guid.NewGuid():N}",
            DisplayName = "Concurrency Org",
            ContactEmail = "cnc@example.com",
            PlanTier = PlanTierEnum.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();

        var address = $"insieme.{Guid.NewGuid():N}@example.com";
        var contact = await CrmTestWorld.Contacts(db, TimeProvider.System).RecordConsentAsync(
            new CrmConsentInput(org.Id, GuestContactChannel.Email, address, GuestContactOrigin.GuestPage, "consent-v1", GuestConsentSource.GuestPage));
        Assert.NotNull(contact);

        if (sentCount > 0)
        {
            contact.Confirmation = GuestContactConfirmation.Pending;
            contact.ConfirmationSentCount = sentCount;
            contact.ConfirmationSentAt = DateTime.UtcNow.AddMinutes(-lastSentMinutesAgo);
            await db.SaveChangesAsync();
        }

        return (org.Id, contact.Id, address);
    }
}
