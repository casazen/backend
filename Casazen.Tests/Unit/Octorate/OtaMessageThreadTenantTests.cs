using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E05/F01/US01/T01: the copy of the OTA conversations is a row of the organization whose connection read it. The rule that costs if it fails:
/// the conversations of the guests of another organization (who wrote, what, about which reservation) are never read by a caller, because every
/// table of the copy carries the global tenant filter like any <see cref="ITenantOwned"/> entity (TN-2).
/// </summary>
public class OtaMessageThreadTenantTests
{
    private static readonly DateTime At = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// PROOF TEST: data rule (tenant isolation). Two organizations have a thread, a message, the checkpoints of a listing and a template each.
    /// The caller of organization A reads its own rows only: the thread of B is neither in the list nor found by its Octorate id, its messages
    /// and its checkpoints neither, and a caller with no organization matches nothing.
    /// </summary>
    [Fact]
    public async Task OtaMessageThread_OtherOrg_NotVisible_Test()
    {
        var databaseName = $"ota-message-thread-{Guid.NewGuid():N}";
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var mine = NewThread(orgA, "7001", "100");
        var theirs = NewThread(orgB, "7001", "200");

        // The sync writes both organizations' rows from a job (no caller, so no filter); the same Octorate thread id in two organizations is
        // two rows, each in its own.
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.OtaMessageThreads.AddRange(mine, theirs);
            system.OtaMessages.AddRange(NewMessage(mine, "8001"), NewMessage(theirs, "8002"));
            system.OtaMessageSyncStates.AddRange(NewState(orgA, "100"), NewState(orgB, "200"));
            system.MessageTemplates.AddRange(NewTemplate(orgA), NewTemplate(orgB));
            await system.SaveChangesAsync();
        }

        await using var asOrgA = NewDb(databaseName, new FixedTenantContext(orgA));
        var threads = await asOrgA.OtaMessageThreads.AsNoTracking().ToListAsync();

        var row = Assert.Single(threads);
        Assert.Equal(mine.Id, row.Id);
        Assert.Equal(orgA, row.OrgId);

        // By its Octorate id, by its id, by its accommodation, by its messages and by its checkpoints: nothing of the other organization.
        Assert.Equal(mine.Id, (await asOrgA.OtaMessageThreads.AsNoTracking().SingleAsync(t => t.OctorateThreadId == "7001")).Id);
        Assert.Null(await asOrgA.OtaMessageThreads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theirs.Id));
        Assert.False(await asOrgA.OtaMessageThreads.AsNoTracking().AnyAsync(t => t.OctorateAccommodationId == "200"));
        Assert.Equal("8001", Assert.Single(await asOrgA.OtaMessages.AsNoTracking().ToListAsync()).OctorateMessageId);
        Assert.Empty(await asOrgA.OtaMessages.AsNoTracking().Where(m => m.ThreadId == theirs.Id).ToListAsync());
        Assert.Equal("100", Assert.Single(await asOrgA.OtaMessageSyncStates.AsNoTracking().ToListAsync()).OctorateAccommodationId);
        Assert.Equal(orgA, Assert.Single(await asOrgA.MessageTemplates.AsNoTracking().ToListAsync()).OrgId);

        // A caller with no organization is fail-closed: it matches nothing.
        await using var nobody = NewDb(databaseName, new FixedTenantContext(null));
        Assert.Empty(await nobody.OtaMessageThreads.AsNoTracking().ToListAsync());
        Assert.Empty(await nobody.OtaMessages.AsNoTracking().ToListAsync());
        Assert.Empty(await nobody.OtaMessageSyncStates.AsNoTracking().ToListAsync());
    }

    private static OtaMessageThread NewThread(Guid orgId, string octorateThreadId, string accommodationId) => new()
    {
        OrgId = orgId,
        OctorateThreadId = octorateThreadId,
        OctorateAccommodationId = accommodationId,
        ChannelSource = OtaChannel.Booking,
        Kind = OtaThreadKind.Request,
        LastMessageAt = At,
        LastMessageDirection = OtaMessageDirection.Inbound,
        CreatedAt = At,
        UpdatedAt = At,
    };

    private static OtaMessage NewMessage(OtaMessageThread thread, string octorateMessageId) => new()
    {
        OrgId = thread.OrgId,
        ThreadId = thread.Id,
        OctorateMessageId = octorateMessageId,
        Direction = OtaMessageDirection.Inbound,
        SenderName = "Ospite di prova",
        Text = "Testo di prova",
        SentAt = At,
        DeliveryState = OtaMessageDeliveryState.Sent,
        Origin = MessageOrigin.Guest,
        CreatedAt = At,
    };

    private static OtaMessageSyncState NewState(Guid orgId, string accommodationId) => new()
    {
        OrgId = orgId,
        OctorateAccommodationId = accommodationId,
        ConnectionId = Guid.NewGuid(),
        UpdatedAt = At,
    };

    private static MessageTemplate NewTemplate(Guid orgId) => new()
    {
        OrgId = orgId,
        Name = "Benvenuto",
        Language = "it",
        Body = "Benvenuti!",
        CreatedAt = At,
        UpdatedAt = At,
    };

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
