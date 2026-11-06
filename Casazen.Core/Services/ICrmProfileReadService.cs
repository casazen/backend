using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The read model of the CRM profiles for the host screens (E07/F01/US03/T01, NF-88, CRM-01): the list with search and filters, the detail of
/// a guest with its history, totals, reviews and contacts, and the one write on it, the tags. It is also where the <b>scope</b> of the CRM is
/// decided, for everything that reads profiles: the merge of duplicates (<see cref="ICrmProfileMergeService"/>) and the segments of the
/// campaigns ask it which profiles a caller reaches (<see cref="ProfilesInScope"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Scope (AC-NF88-5).</b> A profile is in the scope of a caller who reaches some properties when one of its guests has a booking at one
/// of them (<c>InScope</c>, one <c>EXISTS</c> in the same statement); a lead from the welcome page, with no guest, is in the scope of a caller who
/// reaches the whole org only. A caller with no reach gets nothing and is never widened to the org. Stays, spend, first and last stay, channels,
/// properties, reviews and the name shown are computed over the bookings and the guests the caller reaches, never read from the totals stored on
/// the profile (those are the whole org's); the contacts and the tags are the person's, shown as they are.</item>
/// <item><b>Tombstones</b> (<c>MergedIntoProfileId</c>) are not profiles any more: no list, no detail, no scope.</item>
/// <item><b>Privacy (AC-NF88-6).</b> No document, birth date, place of birth, <see cref="StayGuest"/> or Alloggiati field is read or returned.
/// The e-mail of the list is masked; the detail shows the contact values to the people who may read the guests of the org.</item>
/// <item><b>Tenant.</b> Every query names the org of the scope, on top of the tenant filter of the request.</item>
/// </list>
/// </remarks>
public interface ICrmProfileReadService
{
    /// <summary>
    /// The living profiles (not merged into another) of the caller's org that the caller reaches: an <c>IQueryable</c> of the same context,
    /// to compose in the same statement (the merge proposals and the segments narrow it further). Nothing is loaded.
    /// </summary>
    IQueryable<GuestCrmProfile> ProfilesInScope(HostScope scope);

    /// <summary>The bookings of the caller's org at the properties the caller reaches.</summary>
    IQueryable<Booking> BookingsInScope(HostScope scope);

    /// <summary>
    /// One page of the profiles the caller reaches, filtered and sorted (<paramref name="pageSize"/> is cut to
    /// <see cref="CrmProfileRules.MaxPageSize"/>, <paramref name="page"/> starts at 1).
    /// </summary>
    Task<CrmProfilePage> ListAsync(
        HostScope scope,
        CrmProfileFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The detail of a profile the caller reaches.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>crm_profile_not_found</c> (404): there is no such profile, it is of another org, the caller does not reach it, or it was merged into another.</exception>
    Task<CrmProfileDetail> GetAsync(HostScope scope, Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The list items of these profiles, in the order given; a profile the caller does not reach (or that does not exist) is left out.
    /// The same item the list shows, computed the same way.
    /// </summary>
    Task<IReadOnlyList<CrmProfileListItem>> GetItemsAsync(
        HostScope scope,
        IReadOnlyCollection<Guid> profileIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the tags of a profile the caller reaches. The tags are checked by <see cref="CrmProfileRules.TryNormalizeTags"/>; the write
    /// takes the same lock of the org as the merge, so a tag is never lost to a merge that runs at the same moment.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>crm_profile_not_found</c> (404).</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><c>crm_tags_invalid</c> (422).</exception>
    Task SetTagsAsync(
        HostScope scope,
        Guid profileId,
        IReadOnlyCollection<string?>? tags,
        CancellationToken cancellationToken = default);
}

/// <summary>How a page of profiles is sorted. Always descending but <see cref="Name"/>; ties are broken by the id, so a page never repeats a row.</summary>
public enum CrmProfileSort
{
    /// <summary>The latest stay first; the profiles with no stay come last.</summary>
    LastStay = 0,

    /// <summary>The highest spend first.</summary>
    Spend = 1,

    /// <summary>The most stays first.</summary>
    Stays = 2,

    /// <summary>By last name and first name of the name shown, A to Z.</summary>
    Name = 3,
}

/// <summary>
/// What narrows the list of profiles. Every field is optional and they all apply together (and).
/// </summary>
/// <param name="Search">A part of the name of the guest (each word must be in the first or the last name), or an exact e-mail address or phone number: those are found by the keyed hash of the normalized value, because the values are encrypted.</param>
/// <param name="Country">The country of the profile, as stored, in any case.</param>
/// <param name="Language">The language of the profile (<c>it</c>, <c>en</c>), in any case.</param>
/// <param name="Channel">The channel the guest booked on: the name of a booking source (<c>Direct</c>, <c>Airbnb</c>, <c>BookingCom</c>...), with a booking not cancelled at a property the caller reaches.</param>
/// <param name="Tag">A tag of the profile, in any case.</param>
/// <param name="MinStays">At least this many stays (over the bookings the caller reaches).</param>
/// <param name="ReachableByEmail"><c>true</c>: the profile has an e-mail contact that may receive marketing (<see cref="CrmContactRules.CanReceiveMarketing"/>); <c>false</c>: it has none.</param>
/// <param name="Sort">The order.</param>
public sealed record CrmProfileFilter(
    string? Search = null,
    string? Country = null,
    string? Language = null,
    string? Channel = null,
    string? Tag = null,
    int? MinStays = null,
    bool? ReachableByEmail = null,
    CrmProfileSort Sort = CrmProfileSort.LastStay);

/// <summary>A page of profiles. <paramref name="TotalCount"/> is the number of profiles that match, over all the pages.</summary>
public sealed record CrmProfilePage(IReadOnlyList<CrmProfileListItem> Items, int TotalCount, int Page, int PageSize);

/// <summary>
/// A profile in the list. The e-mail is masked (<c>m***@example.it</c>) and is the best one of the profile: the one that may receive marketing,
/// else the strongest basis. <paramref name="HasMergeProposal"/> is true when a person has a merge to decide on this profile that the caller
/// may decide (the other profile is in the caller's scope too).
/// </summary>
/// <param name="Id">The profile.</param>
/// <param name="DisplayName">The name of the primary guest, or of the first guest the caller reaches when it does not reach the primary one; empty for a lead with no name.</param>
/// <param name="Country">The country, as stored.</param>
/// <param name="Language">The language, as stored.</param>
/// <param name="TotalStays">The stays: of the whole org for a caller who reaches it all, over the bookings the caller reaches otherwise.</param>
/// <param name="TotalSpend">The spend, computed like <paramref name="TotalStays"/>.</param>
/// <param name="FirstStayAt">The first arrival; <c>null</c> when there is no stay.</param>
/// <param name="LastStayAt">The latest arrival; <c>null</c> when there is no stay.</param>
/// <param name="Channels">The channels the guest booked on (names of booking sources), the most used first.</param>
/// <param name="Tags">The tags.</param>
/// <param name="EmailMasked">The masked e-mail address; empty when the profile has none.</param>
/// <param name="EmailBasis">The legal basis of that e-mail contact (<see cref="GuestContactLegalBasis.None"/> when there is none).</param>
/// <param name="EmailReachable">At least one e-mail contact may receive marketing.</param>
/// <param name="WhatsappBasis">The strongest legal basis of the WhatsApp contacts (<see cref="GuestContactLegalBasis.None"/> when there is none).</param>
/// <param name="HasMergeProposal">There is a merge proposal on this profile that the caller may decide.</param>
public sealed record CrmProfileListItem(
    Guid Id,
    string DisplayName,
    string Country,
    string Language,
    int TotalStays,
    decimal TotalSpend,
    DateTime? FirstStayAt,
    DateTime? LastStayAt,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> Tags,
    string EmailMasked,
    GuestContactLegalBasis EmailBasis,
    bool EmailReachable,
    GuestContactLegalBasis WhatsappBasis,
    bool HasMergeProposal);

/// <summary>One booking in the history of a profile.</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="PropertyName">The name of the property.</param>
/// <param name="CheckInDate">The arrival (a date: midnight UTC of the day).</param>
/// <param name="CheckOutDate">The departure.</param>
/// <param name="Nights">The nights.</param>
/// <param name="Channel">The source of the booking.</param>
/// <param name="ChannelLabel">The name of the channel for a source that has no value of its own (<see cref="BookingSource.OtherChannel"/>) or of the calendar feed; <c>null</c> otherwise.</param>
/// <param name="Status">The status of the booking.</param>
/// <param name="Total">What the booking costs (<c>TotalPrice</c>).</param>
/// <param name="Adults">The adults.</param>
/// <param name="Children">The children.</param>
/// <param name="AdvanceDays">Days between the booking and the arrival; <c>null</c> when the booking was recorded after the arrival (<see cref="CrmProfileRules.AdvanceDays"/>).</param>
public sealed record CrmProfileStay(
    Guid BookingId,
    Guid PropertyId,
    string PropertyName,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int Nights,
    BookingSource Channel,
    string? ChannelLabel,
    BookingStatus Status,
    decimal Total,
    int Adults,
    int Children,
    int? AdvanceDays);

/// <summary>Stays and spend of a profile on one channel.</summary>
public sealed record CrmChannelTotal(BookingSource Channel, int Stays, decimal Spend);

/// <summary>Stays of a profile at one property.</summary>
public sealed record CrmPropertyTotal(Guid PropertyId, string PropertyName, int Stays);

/// <summary>
/// How a guest usually comes: a single, a couple, a family (there is a child) or a group (three adults or more), by the stays that happened.
/// </summary>
public enum CrmGroupKind
{
    Single = 1,

    Couple = 2,

    Family = 3,

    Group = 4,
}

/// <summary>
/// The totals of a profile over its stays that happened (<see cref="GuestMatchRules.IsStay"/>) at the properties the caller reaches.
/// </summary>
/// <param name="Stays">The stays.</param>
/// <param name="Spend">The spend.</param>
/// <param name="AverageSpend">The spend of a stay on average; 0 without a stay.</param>
/// <param name="AverageAdvanceDays">The advance of the bookings on average, over the stays whose advance is known; <c>null</c> when none is.</param>
/// <param name="ByChannel">Stays and spend per channel, the most stays first.</param>
/// <param name="ByProperty">Stays per property, the most stays first.</param>
/// <param name="AverageAdults">Adults per stay on average.</param>
/// <param name="AverageChildren">Children per stay on average.</param>
/// <param name="TypicalGroup">The group the guest usually comes as; <c>null</c> without a stay.</param>
public sealed record CrmProfileTotals(
    int Stays,
    decimal Spend,
    decimal AverageSpend,
    double? AverageAdvanceDays,
    IReadOnlyList<CrmChannelTotal> ByChannel,
    IReadOnlyList<CrmPropertyTotal> ByProperty,
    double AverageAdults,
    double AverageChildren,
    CrmGroupKind? TypicalGroup);

/// <summary>One review in the summary: the mark and when it was published.</summary>
public sealed record CrmReviewEntry(int Rating, DateTime PublishedAt);

/// <summary>The reviews of the stays of a profile (not the ones the staff took down): how many, the average mark, the latest ones.</summary>
public sealed record CrmReviewSummary(int Count, double? AverageRating, IReadOnlyList<CrmReviewEntry> Latest);

/// <summary>
/// A contact of a profile, with what <see cref="CrmContactRules"/> says of it. <paramref name="Value"/> is the address or the number as stored.
/// </summary>
/// <param name="Id">The contact.</param>
/// <param name="Channel">E-mail, phone or WhatsApp.</param>
/// <param name="Origin">Where it came from.</param>
/// <param name="Confirmation">The double opt-in state.</param>
/// <param name="LegalBasis">The legal basis it can be used on.</param>
/// <param name="BasisVersion">The version of the text the basis rests on; empty when there is none.</param>
/// <param name="BasisRecordedAt">When the basis was recorded; <c>null</c> when there is none.</param>
/// <param name="IsSuppressed">The person asked not to be written to on this channel.</param>
/// <param name="IsOtaForward">The address is one an online travel agency forwards the mail through.</param>
/// <param name="Value">The address or the number.</param>
/// <param name="Marketable">The contact may receive marketing (<see cref="CrmContactRules.CanReceiveMarketing"/>).</param>
/// <param name="ReachabilityReason">Why the contact cannot be written to for marketing, a code of <see cref="CrmContactRules"/>; <c>null</c> when it can.</param>
public sealed record CrmContactView(
    Guid Id,
    GuestContactChannel Channel,
    GuestContactOrigin Origin,
    GuestContactConfirmation Confirmation,
    GuestContactLegalBasis LegalBasis,
    string BasisVersion,
    DateTime? BasisRecordedAt,
    bool IsSuppressed,
    bool IsOtaForward,
    string Value,
    bool Marketable,
    string? ReachabilityReason);

/// <summary>
/// The detail of a profile: who it is, the history of the stays the caller reaches, the totals, the reviews, the contacts and how many merges
/// wait for a decision.
/// </summary>
/// <param name="Id">The profile.</param>
/// <param name="DisplayName">The name shown (see <see cref="CrmProfileListItem.DisplayName"/>).</param>
/// <param name="PrimaryGuestId">The guest record the name comes from, so that the screen can open it; <c>null</c> when no guest the caller reaches has a name.</param>
/// <param name="Country">The country.</param>
/// <param name="Language">The language.</param>
/// <param name="FirstStayAt">The first arrival, over the stays that happened; <c>null</c> when there is none.</param>
/// <param name="LastStayAt">The latest arrival, over the stays that happened; <c>null</c> when there is none.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Stays">The bookings, the latest arrival first, at most <see cref="CrmProfileRules.MaxStaysShown"/>.</param>
/// <param name="StaysTruncated">There are more bookings than the list holds (the totals count them all).</param>
/// <param name="Totals">The totals.</param>
/// <param name="Reviews">The reviews.</param>
/// <param name="Contacts">The contacts.</param>
/// <param name="MergeProposalCount">The merge proposals on this profile that the caller may decide.</param>
public sealed record CrmProfileDetail(
    Guid Id,
    string DisplayName,
    Guid? PrimaryGuestId,
    string Country,
    string Language,
    DateTime? FirstStayAt,
    DateTime? LastStayAt,
    IReadOnlyList<string> Tags,
    IReadOnlyList<CrmProfileStay> Stays,
    bool StaysTruncated,
    CrmProfileTotals Totals,
    CrmReviewSummary Reviews,
    IReadOnlyList<CrmContactView> Contacts,
    int MergeProposalCount);
