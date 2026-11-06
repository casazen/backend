using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The decisions on the merges the matching proposes (E07/F01/US03/T02, NF-88, CRM-01): a person lists the proposals, dismisses the ones that
/// are two different people and merges the ones that are one. Nothing merges by itself: <see cref="IGuestMatchingService"/> only writes
/// <see cref="GuestMergeCandidate"/> rows, and these are the only calls that act on them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Scope.</b> A proposal is the caller's to decide when both of its profiles are reached in full (<see cref="ICrmProfileReadService.ProfilesWhollyInScope"/>:
/// every guest of each, and every guest who gave one of its contacts, has a booking at a property the caller reaches), because a merge moves all of them. A
/// caller limited to some properties never decides a proposal that would move what it does not reach: it is as good as missing (404, not listed, the same
/// answer as for a profile out of scope), for the merge and for the dismissal, and so is a proposal of another org. The caller who reaches the whole org
/// decides every proposal of the org. A profile that was merged into another is followed to the profile that took its place, so a second click on a
/// proposal already decided is answered for what it is (409) and not as a missing one.</item>
/// <item><b>One transaction, one lock of the org.</b> A merge or a dismissal takes the advisory lock of the org
/// (<c>PostgresAdvisoryLocks.Scope.CrmProfileMerge</c>), decides on the proposal as it is after the previous holder, and saves the whole change together.
/// A double click, or two people on the same pair, merges once.</item>
/// <item><b>A merge never edits a contact.</b> The contacts and the guests of the profile that goes are moved to the one that stays, as they are: their
/// basis, confirmation and suppression do not change, so a merge can neither grant a consent nor lift a suppression, and no contact is copied (the unique
/// index of the keyed value would not allow two rows for one address).</item>
/// <item><b>The profile that goes stays as a tombstone</b> (<see cref="GuestCrmProfile.MergedIntoProfileId"/>), which the lists and the segments skip and
/// the matching follows to the survivor.</item>
/// </list>
/// </remarks>
public interface ICrmProfileMergeService
{
    /// <summary>
    /// The proposals waiting for a decision that the caller may decide (both profiles reached in full), the surest first, with a side-by-side
    /// summary of the two profiles (computed over what the caller reaches, the contacts masked). <paramref name="page"/> starts at 1; <paramref name="pageSize"/> is cut to
    /// <see cref="CrmProfileRules.MaxPageSize"/>.
    /// </summary>
    Task<CrmMergeCandidatePage> ListProposedAsync(
        HostScope scope,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The two profiles are two people: the proposal is <see cref="GuestMergeStatus.Dismissed"/> with the time and the user, and is never
    /// proposed again.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>crm_merge_candidate_not_found</c> (404): no such proposal, of another org, or a profile out of the caller's scope or reached only in part.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><c>crm_merge_not_proposed</c> (409): the proposal was already decided; nothing changes.</exception>
    Task DismissAsync(HostScope scope, Guid candidateId, string? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The two profiles are one person. The guests and the contacts of the profile that goes move to <paramref name="survivorProfileId"/>, the
    /// tags are joined (up to <see cref="CrmProfileRules.MaxTags"/>), the aggregates are computed again, the other profile becomes a tombstone,
    /// the proposal is <see cref="GuestMergeStatus.Merged"/> with the time and the user, and the other proposals of the profile that goes are
    /// moved to the survivor, or dropped when they would pair it with itself or repeat a pair. Returns the survivor as the list shows it.
    /// </summary>
    /// <param name="scope">What the caller reaches.</param>
    /// <param name="candidateId">The proposal.</param>
    /// <param name="survivorProfileId">The profile that stays: one of the two of the proposal.</param>
    /// <param name="userId">The user who decides (the Auth0 subject).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>crm_merge_candidate_not_found</c> (404).</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><c>crm_merge_survivor_invalid</c> (422): the survivor is not one of the two profiles.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><c>crm_merge_not_proposed</c> (409): already merged or dismissed; nothing changes.</exception>
    Task<CrmProfileListItem> MergeAsync(
        HostScope scope,
        Guid candidateId,
        Guid survivorProfileId,
        string? userId,
        CancellationToken cancellationToken = default);
}

/// <summary>A contact of a profile in the summary of a proposal: the channel, where it came from and the value masked.</summary>
public sealed record CrmMergeContactSummary(GuestContactChannel Channel, GuestContactOrigin Origin, string ValueMasked);

/// <summary>
/// One of the two profiles of a proposal, as much as a person needs to tell whether it is the same guest. Stays and last stay are over the
/// bookings the caller reaches.
/// </summary>
public sealed record CrmMergeProfileSummary(
    Guid Id,
    string DisplayName,
    string Country,
    int Stays,
    DateTime? LastStayAt,
    IReadOnlyList<string> PropertyNames,
    IReadOnlyList<CrmMergeContactSummary> Contacts);

/// <summary>
/// A proposal to decide: how sure the matching is, why (codes, never what matched), and the two profiles. <paramref name="ProfileA"/> is the one with
/// the smaller id, as the pair is stored.
/// </summary>
public sealed record CrmMergeCandidateView(
    Guid Id,
    int Score,
    IReadOnlyList<string> Reasons,
    DateTime CreatedAt,
    CrmMergeProfileSummary ProfileA,
    CrmMergeProfileSummary ProfileB);

/// <summary>A page of proposals. <paramref name="TotalCount"/> is over all the pages.</summary>
public sealed record CrmMergeCandidatePage(IReadOnlyList<CrmMergeCandidateView> Items, int TotalCount, int Page, int PageSize);
