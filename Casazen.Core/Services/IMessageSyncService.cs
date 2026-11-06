namespace Casazen.Core.Services;

/// <summary>
/// Copies the conversations of the guests with the channels from the channel manager into CasaZen and keeps them up to date (E05/F01/US01/T02,
/// ADR-032, NF-62, NF-64): the threads and their messages of a listing, once, however many times a message is delivered.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which listings.</b> The listings of the organization's mapping (<see cref="IChannelMappingService.ListAsync"/>, epic E02), the
/// rejected ones left out. Only a <i>confirmed</i> mapping gives a thread its property (OCT-04): a thread of any other listing is stored
/// <b>unassigned</b> (no property, counted in <see cref="SyncResult.Unassigned"/>), never guessed and never given to a property of another
/// organization, and it gets its property at the first pass after a person confirms the mapping. An accommodation that is not a listing of the
/// organization is never read.</item>
/// <item><b>Org.</b> Every method works for one organization and opens <see cref="IOctorateOrgScope.Use"/> for the calls it makes.</item>
/// <item><b>Failures.</b> An error of Octorate is stored as a code on the checkpoints of the listing
/// (<see cref="Entities.OtaMessageSyncState.LastError"/>) and then reaches the caller, so the outbox delivers an event again; a license without
/// the <c>chat</c> module is the state <see cref="OtaMessageSyncErrorCodes.ChatNotLicensed"/>, never an empty inbox. A rate limit stops the
/// run with the checkpoints saved, and the next run continues.</item>
/// <item><b>Privacy.</b> The text of a message, a name and an address are never logged, never put in an event: ids and counts only.</item>
/// </list>
/// </remarks>
public interface IMessageSyncService
{
    /// <summary>
    /// The initial import of every thread and message of the confirmed listing of the property, page after page, saving the position after each
    /// page (<see cref="Entities.OtaMessageSyncState.ImportCursor"/>) and the end at the last
    /// (<see cref="Entities.OtaMessageSyncState.InitialImportCompletedAt"/>). Running it again never duplicates. A property whose listing is not
    /// confirmed reads nothing.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="budget">The longest this run goes on; it stops between two threads when it is over and reports the import as not complete.</param>
    Task<SyncResult> ImportPropertyAsync(Guid propertyId, CancellationToken ct, TimeSpan? budget = null);

    /// <summary>
    /// Reads the thread and its messages again from Octorate and upserts them: the messages created after <paramref name="createdAfter"/>
    /// (default: the newest message of the thread that is stored, minus an overlap of five minutes, and not later than the oldest message that is
    /// still on its way).
    /// </summary>
    Task<SyncResult> SyncThreadAsync(Guid orgId, string accommodationId, string octorateThreadId, DateTime? createdAfter, CancellationToken ct);

    /// <summary>The messages of the last 24 hours of every listing of the organization, then <see cref="SyncThreadAsync"/> for each thread they name.</summary>
    Task<SyncResult> ReconcileRecentAsync(Guid orgId, CancellationToken ct);

    /// <summary>
    /// The threads with activity in the window since the last deep pass, minus <paramref name="overlap"/> (at least two days, the retry window of
    /// the webhook), for every listing of the organization.
    /// </summary>
    Task<SyncResult> ReconcileDeepAsync(Guid orgId, TimeSpan overlap, CancellationToken ct);
}

/// <summary>What a pass of the sync did.</summary>
/// <param name="ThreadsSeen">The threads looked at, the skipped ones included.</param>
/// <param name="MessagesAdded">The messages stored for the first time.</param>
/// <param name="SkippedChannel">The threads of a channel with no integrated chat, not imported.</param>
/// <param name="Unassigned">The threads stored with no property because the mapping of their listing is not confirmed.</param>
/// <param name="Complete"><c>false</c> when the pass stopped before the end (the budget of an import ran out); the next run continues.</param>
/// <param name="NotFound">The threads that were asked for and that Octorate does not know: a notification may name something that is not a thread of the chat.</param>
public sealed record SyncResult(int ThreadsSeen, int MessagesAdded, int SkippedChannel, int Unassigned, bool Complete = true, int NotFound = 0)
{
    /// <summary>Nothing seen, nothing stored.</summary>
    public static SyncResult Empty { get; } = new(0, 0, 0, 0);

    /// <summary>The sum of two results; complete only when both are.</summary>
    public SyncResult Plus(SyncResult other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new SyncResult(
            ThreadsSeen + other.ThreadsSeen,
            MessagesAdded + other.MessagesAdded,
            SkippedChannel + other.SkippedChannel,
            Unassigned + other.Unassigned,
            Complete && other.Complete,
            NotFound + other.NotFound);
    }
}

/// <summary>The stable codes of the failures of the sync of a listing, as kept in <see cref="Entities.OtaMessageSyncState.LastError"/>.</summary>
public static class OtaMessageSyncErrorCodes
{
    /// <summary>
    /// The <c>chat</c> module is not in the license of the listing: Octorate answers the chat reads with 403, and <c>recentMessages</c> with 500, with
    /// the application server's page and not an error of its API. The messages of that listing cannot be read, and a screen says so instead of
    /// showing an empty inbox.
    /// </summary>
    public const string ChatNotLicensed = "chat_not_licensed";
}
