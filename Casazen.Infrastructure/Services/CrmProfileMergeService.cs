using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICrmProfileMergeService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Deciding is one change under one lock.</b> A dismissal and a merge take the advisory lock of the org (<c>CrmProfileMerge</c>), read the
/// proposal as the previous holder left it, and save everything they change in a single <c>SaveChanges</c>; the aggregates of the survivor are then
/// computed again by <see cref="IGuestMatchingService.UpdateProfileAggregatesAsync"/> inside the same transaction. Whatever fails before the commit
/// leaves nothing behind.</item>
/// <item><b>The scope of a proposal</b> is the scope of the two living profiles it ends up with: a profile merged into another is followed to the
/// one that took its place (as the matching does), so a proposal already decided answers 409 to the person who decided it, and a proposal with a
/// profile the caller does not reach answers 404 whatever its state.</item>
/// <item><b>Nothing of a contact is edited.</b> The guests and the contacts of the profile that goes are re-pointed with stub rows that carry only
/// the key and the link (no document, no address is read, no encrypted value is decrypted or written): the update sets one column. Basis,
/// confirmation, suppression, version and times stay exactly as they were.</item>
/// <item><b>Privacy.</b> The values in the summary of a proposal are masked; the logs carry ids only.</item>
/// </list>
/// </remarks>
public sealed class CrmProfileMergeService(
    AppDbContext db,
    ICrmProfileReadService profiles,
    IGuestMatchingService matching,
    TimeProvider timeProvider,
    ILogger<CrmProfileMergeService> logger) : ICrmProfileMergeService
{
    /// <summary>Longest chain of merged profiles followed to the one that survives; a longer one is a loop.</summary>
    private const int MaxMergeHops = 10;

    public async Task<CrmMergeCandidatePage> ListProposedAsync(
        HostScope scope,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, CrmProfileRules.MaxPageSize);
        var orgId = scope.OrgId;

        // Both profiles of the proposal in the scope of the caller, in the same statement; a tombstone is in no scope.
        var reachable = profiles.ProfilesInScope(scope).Select(profile => profile.Id);
        var open = db.GuestMergeCandidates
            .AsNoTracking()
            .Where(candidate => candidate.OrgId == orgId
                && candidate.Status == GuestMergeStatus.Proposed
                && reachable.Contains(candidate.ProfileAId)
                && reachable.Contains(candidate.ProfileBId));

        var total = await open.CountAsync(cancellationToken);
        var skip = (long)(page - 1) * pageSize;
        if (total == 0 || skip >= total)
            return new CrmMergeCandidatePage([], total, page, pageSize);

        var rows = await open
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id)
            .Skip((int)skip)
            .Take(pageSize)
            .Select(candidate => new CandidateRow(
                candidate.Id,
                candidate.ProfileAId,
                candidate.ProfileBId,
                candidate.Score,
                candidate.ReasonsJson,
                candidate.CreatedAt))
            .ToListAsync(cancellationToken);

        var ids = rows.SelectMany(row => new[] { row.ProfileAId, row.ProfileBId }).Distinct().ToList();
        var items = (await profiles.GetItemsAsync(scope, ids, cancellationToken)).ToDictionary(item => item.Id);
        var propertyNames = await PropertyNamesAsync(scope, ids, cancellationToken);
        var contacts = await ContactSummariesAsync(orgId, ids, cancellationToken);

        var views = new List<CrmMergeCandidateView>(rows.Count);
        foreach (var row in rows)
        {
            if (!items.TryGetValue(row.ProfileAId, out var first) || !items.TryGetValue(row.ProfileBId, out var second))
                continue;

            views.Add(new CrmMergeCandidateView(
                row.Id,
                row.Score,
                ReasonsOf(row.ReasonsJson),
                row.CreatedAt,
                SummaryOf(first, propertyNames, contacts),
                SummaryOf(second, propertyNames, contacts)));
        }

        return new CrmMergeCandidatePage(views, total, page, pageSize);
    }

    public async Task DismissAsync(HostScope scope, Guid candidateId, string? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var orgId = scope.OrgId;
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.CrmProfileMerge, orgId.ToString("N")));

        var decidable = await LoadDecidableAsync(scope, candidateId, cancellationToken);
        EnsureProposed(decidable);

        var candidate = decidable.Candidate;
        candidate.Status = GuestMergeStatus.Dismissed;
        candidate.DecidedAt = Now();
        candidate.DecidedByUserId = Truncate(userId);
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Merge proposal {CandidateId} of org {OrgId} dismissed", candidateId, orgId);
    }

    public async Task<CrmProfileListItem> MergeAsync(
        HostScope scope,
        Guid candidateId,
        Guid survivorProfileId,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var orgId = scope.OrgId;
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.CrmProfileMerge, orgId.ToString("N")));

        var decidable = await LoadDecidableAsync(scope, candidateId, cancellationToken);
        var candidate = decidable.Candidate;
        if (survivorProfileId != candidate.ProfileAId && survivorProfileId != candidate.ProfileBId)
            throw new DomainRuleException("crm_merge_survivor_invalid", "CrmMergeSurvivorInvalid");

        EnsureProposed(decidable);

        var goneId = survivorProfileId == candidate.ProfileAId ? candidate.ProfileBId : candidate.ProfileAId;
        var survivor = await db.GuestCrmProfiles.FirstAsync(profile => profile.OrgId == orgId && profile.Id == survivorProfileId, cancellationToken);
        var gone = await db.GuestCrmProfiles.FirstAsync(profile => profile.OrgId == orgId && profile.Id == goneId, cancellationToken);
        var now = Now();
        var goneHadStays = gone.TotalStays > 0;
        var goneFirstStayAt = gone.FirstStayAt;

        var guests = await MoveGuestsAsync(orgId, goneId, survivorProfileId, cancellationToken);
        var contacts = await MoveContactsAsync(orgId, goneId, survivorProfileId, cancellationToken);

        // What the survivor takes of the other profile: the tags (up to the cap), and what it has not got. The consent flags of the profile are
        // not touched, and the contacts moved as they are: a merge decides who the person is, never what the person agreed to.
        survivor.TagsJson = CrmProfileRules.SerializeTags(
            CrmProfileRules.UnionTags(CrmProfileRules.ParseTags(survivor.TagsJson), CrmProfileRules.ParseTags(gone.TagsJson)));
        if (string.IsNullOrEmpty(survivor.Country))
            survivor.Country = gone.Country;

        if (string.IsNullOrEmpty(survivor.Language))
            survivor.Language = gone.Language;

        survivor.PhoneKeyHash ??= gone.PhoneKeyHash;
        survivor.NameBirthKeyHash ??= gone.NameBirthKeyHash;
        survivor.PrimaryGuestId ??= gone.PrimaryGuestId;
        survivor.UpdatedAt = now;

        gone.MergedIntoProfileId = survivorProfileId;
        gone.UpdatedAt = now;

        candidate.Status = GuestMergeStatus.Merged;
        candidate.DecidedAt = now;
        candidate.DecidedByUserId = Truncate(userId);

        await MoveOtherProposalsAsync(orgId, candidate.Id, goneId, survivorProfileId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        // The stays, the spend and the properties of the guests that came over; the first stay is the earlier of the two.
        await matching.UpdateProfileAggregatesAsync(survivorProfileId, cancellationToken);
        if (goneHadStays && survivor.TotalStays > 0 && goneFirstStayAt < survivor.FirstStayAt)
        {
            survivor.FirstStayAt = goneFirstStayAt;
            survivor.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        var items = await profiles.GetItemsAsync(scope, [survivorProfileId], cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "CRM profile {GoneId} merged into {SurvivorId} of org {OrgId}: {Guests} guests and {Contacts} contacts moved",
            goneId,
            survivorProfileId,
            orgId,
            guests,
            contacts);
        return items.Single();
    }

    // ---- the proposal -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The proposal of the org, tracked, once the caller is known to reach the two profiles it ends up with (404 otherwise). Called inside the
    /// lock, so what it reads is what the previous holder committed.
    /// </summary>
    private async Task<Decidable> LoadDecidableAsync(HostScope scope, Guid candidateId, CancellationToken cancellationToken)
    {
        var orgId = scope.OrgId;
        var candidate = await db.GuestMergeCandidates
            .FirstOrDefaultAsync(row => row.OrgId == orgId && row.Id == candidateId, cancellationToken)
            ?? throw CandidateNotFound();

        var first = await SurvivorIdAsync(orgId, candidate.ProfileAId, cancellationToken);
        var second = await SurvivorIdAsync(orgId, candidate.ProfileBId, cancellationToken);
        if (first is null || second is null)
            throw CandidateNotFound();

        var wanted = new[] { first.Value, second.Value }.Distinct().ToList();
        var reachable = await profiles.ProfilesInScope(scope).CountAsync(profile => wanted.Contains(profile.Id), cancellationToken);
        if (reachable != wanted.Count)
            throw CandidateNotFound();

        return new Decidable(candidate, first.Value, second.Value);
    }

    /// <summary>
    /// Only a proposal that is still waiting, between the two profiles it was made for, can be decided: one already merged or dismissed, or one
    /// of which a profile was merged into another meanwhile, answers 409 and nothing changes.
    /// </summary>
    private static void EnsureProposed(Decidable decidable)
    {
        var candidate = decidable.Candidate;
        if (candidate.Status != GuestMergeStatus.Proposed
            || decidable.FirstId != candidate.ProfileAId
            || decidable.SecondId != candidate.ProfileBId)
        {
            throw new DomainConflictException("crm_merge_not_proposed", "CrmMergeNotProposed");
        }
    }

    /// <summary>The living profile at the end of the merges that started at <paramref name="profileId"/>; <c>null</c> when it is not the org's or the chain is a loop.</summary>
    private async Task<Guid?> SurvivorIdAsync(Guid orgId, Guid profileId, CancellationToken cancellationToken)
    {
        var current = profileId;
        for (var hop = 0; hop < MaxMergeHops; hop++)
        {
            var row = await db.GuestCrmProfiles
                .AsNoTracking()
                .Where(profile => profile.OrgId == orgId && profile.Id == current)
                .Select(profile => new { profile.MergedIntoProfileId })
                .FirstOrDefaultAsync(cancellationToken);
            if (row is null)
                return null;

            if (row.MergedIntoProfileId is not { } next)
                return current;

            current = next;
        }

        logger.LogWarning("CRM profile {ProfileId} of org {OrgId}: the chain of merges is longer than {Hops}", profileId, orgId, MaxMergeHops);
        return null;
    }

    // ---- the merge ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Points the guests of one profile to the other. Each guest is a stub with its key and the old link, and only the link changes: the update
    /// sets one column and reads no document, no date of birth, no address.
    /// </summary>
    private async Task<int> MoveGuestsAsync(Guid orgId, Guid fromProfileId, Guid toProfileId, CancellationToken cancellationToken)
    {
        var ids = await db.Guests
            .AsNoTracking()
            .Where(guest => guest.OrgId == orgId && guest.GuestCrmProfileId == fromProfileId)
            .Select(guest => guest.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in ids)
        {
            var guest = db.Guests.Local.FirstOrDefault(tracked => tracked.Id == id);
            if (guest is null)
            {
                guest = new Guest { Id = id, OrgId = orgId, GuestCrmProfileId = fromProfileId };
                db.Guests.Attach(guest);
            }

            guest.GuestCrmProfileId = toProfileId;
        }

        return ids.Count;
    }

    /// <summary>
    /// Points the contacts of one profile to the other, as they are. Each contact is a stub with its key and the old link, so its address and
    /// name are neither decrypted nor written, and only the link changes: the basis, the confirmation, the suppression and the times do not.
    /// </summary>
    private async Task<int> MoveContactsAsync(Guid orgId, Guid fromProfileId, Guid toProfileId, CancellationToken cancellationToken)
    {
        var ids = await db.GuestContacts
            .AsNoTracking()
            .Where(contact => contact.OrgId == orgId && contact.GuestCrmProfileId == fromProfileId)
            .Select(contact => contact.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in ids)
        {
            var contact = db.GuestContacts.Local.FirstOrDefault(tracked => tracked.Id == id);
            if (contact is null)
            {
                contact = new GuestContact { Id = id, OrgId = orgId, GuestCrmProfileId = fromProfileId };
                db.GuestContacts.Attach(contact);
            }

            contact.GuestCrmProfileId = toProfileId;
        }

        return ids.Count;
    }

    /// <summary>
    /// The other proposals waiting on the profile that goes: each moves to the survivor (the pair is stored with the smaller id first), unless the
    /// survivor is already paired with that profile in any state, in which case it is dropped (a pair has one row), and unless it would pair the
    /// survivor with itself. Proposals already decided stay as the history they are.
    /// </summary>
    private async Task MoveOtherProposalsAsync(Guid orgId, Guid decidedId, Guid goneId, Guid survivorId, CancellationToken cancellationToken)
    {
        var pending = await db.GuestMergeCandidates
            .Where(row => row.OrgId == orgId
                && row.Id != decidedId
                && row.Status == GuestMergeStatus.Proposed
                && (row.ProfileAId == goneId || row.ProfileBId == goneId))
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
            return;

        var pairs = await db.GuestMergeCandidates
            .AsNoTracking()
            .Where(row => row.OrgId == orgId && (row.ProfileAId == survivorId || row.ProfileBId == survivorId))
            .Select(row => new { row.ProfileAId, row.ProfileBId })
            .ToListAsync(cancellationToken);
        var paired = pairs.Select(row => row.ProfileAId == survivorId ? row.ProfileBId : row.ProfileAId).ToHashSet();

        foreach (var row in pending.OrderByDescending(item => item.Score).ThenBy(item => item.Id))
        {
            var other = row.ProfileAId == goneId ? row.ProfileBId : row.ProfileAId;
            if (other == survivorId || !paired.Add(other))
            {
                db.GuestMergeCandidates.Remove(row);
                continue;
            }

            (row.ProfileAId, row.ProfileBId) = GuestMatchRules.OrderPair(survivorId, other);
        }
    }

    // ---- the list -----------------------------------------------------------------------------------------------------------------

    /// <summary>The names of the properties the caller reaches where the guests of each profile have a booking that is not cancelled, A to Z.</summary>
    private async Task<Dictionary<Guid, List<string>>> PropertyNamesAsync(HostScope scope, List<Guid> profileIds, CancellationToken cancellationToken)
    {
        var rows = await profiles.BookingsInScope(scope)
            .AsNoTracking()
            .Where(booking => booking.Status != BookingStatus.Cancelled
                && booking.Guest.GuestCrmProfileId != null
                && profileIds.Contains(booking.Guest.GuestCrmProfileId.Value))
            .Select(booking => new { ProfileId = booking.Guest.GuestCrmProfileId!.Value, booking.Property.Name })
            .Distinct()
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ProfileId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    /// <summary>The contacts of the profiles with the value masked: the channel, where it came from, and enough of the value to recognise it.</summary>
    private async Task<ILookup<Guid, CrmMergeContactSummary>> ContactSummariesAsync(Guid orgId, List<Guid> profileIds, CancellationToken cancellationToken)
    {
        var contacts = await CrmProfileContactReader.LoadAsync(db, orgId, profileIds, cancellationToken);
        return contacts
            .OrderBy(contact => contact.Channel)
            .ThenBy(contact => contact.CreatedAt)
            .ThenBy(contact => contact.Id)
            .ToLookup(
                contact => contact.GuestCrmProfileId!.Value,
                contact => new CrmMergeContactSummary(
                    contact.Channel,
                    contact.Origin,
                    contact.Channel == GuestContactChannel.Email
                        ? PersonalDataMasking.MaskEmail(contact.Value)
                        : PersonalDataMasking.MaskPhone(contact.Value)));
    }

    private static CrmMergeProfileSummary SummaryOf(
        CrmProfileListItem item,
        Dictionary<Guid, List<string>> propertyNames,
        ILookup<Guid, CrmMergeContactSummary> contacts)
    {
        var names = propertyNames.TryGetValue(item.Id, out var found) ? found : new List<string>();
        return new CrmMergeProfileSummary(
            item.Id,
            item.DisplayName,
            item.Country,
            item.TotalStays,
            item.LastStayAt,
            names,
            contacts[item.Id].ToList());
    }

    /// <summary>The reason codes of a proposal; empty for a text that is not a list.</summary>
    private static List<string> ReasonsOf(string? reasonsJson)
    {
        if (string.IsNullOrWhiteSpace(reasonsJson))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(reasonsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ---- small helpers ------------------------------------------------------------------------------------------------------------

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static string? Truncate(string? userId)
    {
        var text = userId?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        return text.Length <= GuestMergeCandidate.DecidedByUserIdMaxLength ? text : text[..GuestMergeCandidate.DecidedByUserIdMaxLength];
    }

    private static NotFoundException CandidateNotFound() =>
        new("CRM merge proposal not found.") { Code = "crm_merge_candidate_not_found", MessageKey = "CrmMergeCandidateNotFound" };

    private sealed record Decidable(GuestMergeCandidate Candidate, Guid FirstId, Guid SecondId);

    private sealed record CandidateRow(Guid Id, Guid ProfileAId, Guid ProfileBId, int Score, string ReasonsJson, DateTime CreatedAt);
}
