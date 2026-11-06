using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICrmProfileReadService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Scope is in the statement.</b> The profiles of a caller who reaches some properties are narrowed by <c>InScope</c> (one <c>EXISTS</c>
/// on the bookings it reaches); the list sorts, filters and counts the stays with sub-queries over those same bookings, so a page costs the same
/// queries whatever the number of profiles, and the figures of a limited caller never come from the totals stored on the profile.</item>
/// <item><b>Few queries for a page.</b> The profiles of the page are found first (ids only); then one query each for their data, the guests that
/// give the name, the bookings, the contacts and the merge proposals, all by the ids of the page.</item>
/// <item><b>Keyed search.</b> The names are searched in clear (they are not encrypted); an e-mail address or a phone number is found only by its
/// keyed hash (<see cref="ICrmContactIndex"/>), because the contacts are encrypted. Nothing of the search text is logged.</item>
/// <item><b>Privacy.</b> The guest columns read are the id, the name and the dates of creation and erasure: no document, birth date, place of birth,
/// <see cref="StayGuest"/> or Alloggiati field. A guest that is erased gives no name, and the contacts it gave are not shown.</item>
/// </list>
/// </remarks>
public sealed class CrmProfileReadService(
    AppDbContext db,
    ICrmContactIndex contactIndex,
    IOptions<CrmOptions> options,
    TimeProvider timeProvider,
    ILogger<CrmProfileReadService> logger) : ICrmProfileReadService
{
    private const int MaxSearchLength = 100;

    private const int MaxSearchWords = 5;

    private const int MaxFilterLength = 100;

    public IQueryable<GuestCrmProfile> ProfilesInScope(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var orgId = scope.OrgId;
        return db.GuestCrmProfiles
            .Where(profile => profile.OrgId == orgId && profile.MergedIntoProfileId == null)
            .InScope(scope, db.Bookings);
    }

    public IQueryable<Booking> BookingsInScope(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var orgId = scope.OrgId;
        return db.Bookings.Where(booking => booking.OrgId == orgId).InScope(scope);
    }

    public async Task<CrmProfilePage> ListAsync(
        HostScope scope,
        CrmProfileFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(filter);

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, CrmProfileRules.MaxPageSize);

        var query = await FilteredAsync(scope, filter, cancellationToken);
        var total = await query.CountAsync(cancellationToken);
        var skip = (long)(page - 1) * pageSize;
        if (total == 0 || skip >= total)
            return new CrmProfilePage([], total, page, pageSize);

        var ids = await Ordered(query, scope, filter.Sort)
            .Skip((int)skip)
            .Take(pageSize)
            .Select(profile => profile.Id)
            .ToListAsync(cancellationToken);
        var items = await BuildItemsAsync(scope, ids, cancellationToken);
        return new CrmProfilePage(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<CrmProfileListItem>> GetItemsAsync(
        HostScope scope,
        IReadOnlyCollection<Guid> profileIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(profileIds);

        return await BuildItemsAsync(scope, profileIds.Distinct().ToList(), cancellationToken);
    }

    public async Task<CrmProfileDetail> GetAsync(HostScope scope, Guid profileId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var orgId = scope.OrgId;
        var today = timeProvider.TodayInRome();

        var profile = await ProfilesInScope(scope)
            .AsNoTracking()
            .Where(candidate => candidate.Id == profileId)
            .Select(candidate => new ProfileRow(
                candidate.Id,
                candidate.Country,
                candidate.Language,
                candidate.TotalStays,
                candidate.TotalSpend,
                candidate.FirstStayAt,
                candidate.LastStayAt,
                candidate.TagsJson,
                candidate.PrimaryGuestId))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ProfileNotFound();

        var profileIds = new List<Guid> { profileId };
        var guests = await GuestNamesAsync(scope, profileIds, cancellationToken);
        var contacts = await CrmProfileContactReader.LoadAsync(db, orgId, profileIds, cancellationToken);
        var (guestId, name) = ChooseName(guests.Where(guest => guest.ProfileId == profileId), profile.PrimaryGuestId);
        if (name.Length == 0 && scope.IsOrgWide)
            name = LeadName(contacts);

        var stayRows = await BookingsInScope(scope)
            .AsNoTracking()
            .Where(booking => booking.Guest.GuestCrmProfileId == profileId)
            .OrderByDescending(booking => booking.CheckInDate)
            .ThenByDescending(booking => booking.Id)
            .Select(booking => new StayRow(
                booking.Id,
                booking.PropertyId,
                booking.Property.Name,
                booking.CheckInDate,
                booking.CheckOutDate,
                booking.Source,
                booking.ChannelLabel,
                booking.Status,
                booking.TotalPrice,
                booking.NumberOfAdults,
                booking.NumberOfChildren,
                booking.NumberOfGuests,
                booking.CreatedAt))
            .ToListAsync(cancellationToken);

        var happened = stayRows.Where(row => GuestMatchRules.IsStay(row.Status, row.CheckOutDate, today)).ToList();
        var reviews = await ReviewsOfAsync(orgId, stayRows.Select(row => row.BookingId).ToList(), cancellationToken);
        var proposals = await ProposalCountsAsync(scope, profileIds, cancellationToken);

        var shown = stayRows
            .Take(CrmProfileRules.MaxStaysShown)
            .Select(row =>
            {
                var party = CrmProfileRules.PartyOf(row.NumberOfAdults, row.NumberOfChildren, row.NumberOfGuests);
                return new CrmProfileStay(
                    row.BookingId,
                    row.PropertyId,
                    row.PropertyName,
                    row.CheckInDate,
                    row.CheckOutDate,
                    CrmProfileRules.Nights(row.CheckInDate, row.CheckOutDate),
                    row.Source,
                    row.ChannelLabel,
                    row.Status,
                    row.TotalPrice,
                    party.Adults,
                    party.Children,
                    CrmProfileRules.AdvanceDays(row.CreatedAt, row.CheckInDate));
            })
            .ToList();

        return new CrmProfileDetail(
            profile.Id,
            name,
            guestId,
            profile.Country,
            profile.Language,
            happened.Count > 0 ? happened.Min(row => row.CheckInDate) : null,
            happened.Count > 0 ? happened.Max(row => row.CheckInDate) : null,
            CrmProfileRules.ParseTags(profile.TagsJson),
            shown,
            stayRows.Count > shown.Count,
            TotalsOf(happened),
            reviews,
            ViewsOf(contacts),
            proposals.GetValueOrDefault(profileId));
    }

    public async Task SetTagsAsync(
        HostScope scope,
        Guid profileId,
        IReadOnlyCollection<string?>? tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var orgId = scope.OrgId;
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.CrmProfileMerge, orgId.ToString("N")));

        // The profile first: whoever does not reach it is told it is not there, whatever the tags say.
        if (!await ProfilesInScope(scope).AnyAsync(candidate => candidate.Id == profileId, cancellationToken))
            throw ProfileNotFound();

        if (!CrmProfileRules.TryNormalizeTags(tags, out var normalized))
            throw new DomainRuleException("crm_tags_invalid", "CrmTagsInvalid");

        var profile = await db.GuestCrmProfiles.FirstAsync(candidate => candidate.OrgId == orgId && candidate.Id == profileId, cancellationToken);
        var json = CrmProfileRules.SerializeTags(normalized);
        if (!string.Equals(profile.TagsJson, json, StringComparison.Ordinal))
        {
            profile.TagsJson = json;
            profile.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Tags of CRM profile {ProfileId} of org {OrgId} replaced: {Count} tags", profileId, orgId, normalized.Count);
    }

    // ---- the list: filters and order ----------------------------------------------------------------------------------------------

    /// <summary>The profiles of the scope that match every filter given.</summary>
    private async Task<IQueryable<GuestCrmProfile>> FilteredAsync(HostScope scope, CrmProfileFilter filter, CancellationToken cancellationToken)
    {
        var orgId = scope.OrgId;
        var query = ProfilesInScope(scope);

        if (Clean(filter.Country, MaxFilterLength) is { } country)
        {
            var lowered = country.ToLowerInvariant();
            query = query.Where(profile => profile.Country.ToLower() == lowered);
        }

        if (Clean(filter.Language, MaxFilterLength) is { } language)
        {
            var lowered = language.ToLowerInvariant();
            query = query.Where(profile => profile.Language.ToLower() == lowered);
        }

        if (Clean(filter.Tag, MaxFilterLength) is { } tag)
        {
            var needle = CrmProfileRules.TagNeedle(tag);
            query = query.Where(profile => profile.TagsJson.ToLower().Contains(needle));
        }

        if (Clean(filter.Channel, MaxFilterLength) is { } channel)
        {
            // A channel that is not a source of booking matches nobody.
            if (!CrmProfileRules.TryParseChannel(channel, out var source))
                return query.Where(profile => false);

            var booked = BookingsInScope(scope).Where(booking => booking.Status != BookingStatus.Cancelled && booking.Source == source);
            query = query.Where(profile => booked.Any(booking => booking.Guest.GuestCrmProfileId == profile.Id));
        }

        if (filter.MinStays is { } minStays && minStays > 0)
        {
            if (scope.IsOrgWide)
            {
                query = query.Where(profile => profile.TotalStays >= minStays);
            }
            else
            {
                var stays = StaysInScope(scope);
                query = query.Where(profile => stays.Count(stay => stay.Guest.GuestCrmProfileId == profile.Id) >= minStays);
            }
        }

        if (filter.ReachableByEmail is { } reachable)
        {
            // The contacts a screen may show (not those given by an erased guest): the filter and the item of the list say the same thing.
            var marketable = CrmProfileContactReader.Visible(db, orgId).Where(CrmContactRules.MarketableEmail);
            query = reachable
                ? query.Where(profile => marketable.Any(contact => contact.GuestCrmProfileId == profile.Id))
                : query.Where(profile => !marketable.Any(contact => contact.GuestCrmProfileId == profile.Id));
        }

        if (Clean(filter.Search, MaxSearchLength) is { } search)
            query = await SearchedAsync(query, scope, search, cancellationToken);

        return query;
    }

    /// <summary>
    /// The profiles whose guest has the words in the name, or that have a contact (or a phone) with exactly that e-mail address or number. The
    /// contacts are found by the keyed hash of the normalized value, in a query of their own that returns only ids.
    /// </summary>
    private async Task<IQueryable<GuestCrmProfile>> SearchedAsync(
        IQueryable<GuestCrmProfile> query,
        HostScope scope,
        string search,
        CancellationToken cancellationToken)
    {
        var matched = await ProfilesOfExactContactAsync(scope.OrgId, search, cancellationToken);

        var guests = NamedGuests(scope);
        var words = search.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var word in words.Take(MaxSearchWords))
        {
            var part = word;
            guests = guests.Where(guest => guest.FirstName.ToLower().Contains(part) || guest.LastName.ToLower().Contains(part));
        }

        return query.Where(profile => matched.Contains(profile.Id) || guests.Any(guest => guest.GuestCrmProfileId == profile.Id));
    }

    /// <summary>
    /// The profiles of the org that have a contact with this exact e-mail address or phone number (or this phone as their own key). The contacts
    /// are those a screen may show (<see cref="CrmProfileContactReader.Visible"/>): the address or the number of an erased guest finds nothing.
    /// The phone key of the profile itself is a hash on the profile, not a contact, and cannot be told from whom it came: it stays as it was, until
    /// the erasure reaches the CRM (task E07/F06/US02/T01). Empty when the text is neither, or when the keyed index has no key (outside
    /// Development and Testing, with the module closed).
    /// </summary>
    private async Task<List<Guid>> ProfilesOfExactContactAsync(Guid orgId, string search, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        if (!contactIndex.IsConfigured)
            return ids;

        var emailKey = CrmNormalizer.TryNormalizeEmail(search, out var email) ? contactIndex.Hash(email) : null;
        var phone = CrmNormalizer.NormalizePhone(search, options.Value.DefaultCallingCode);
        var phoneKey = phone is null ? null : contactIndex.Hash(phone);

        if (emailKey is not null)
        {
            var byEmail = await CrmProfileContactReader.Visible(db, orgId)
                .AsNoTracking()
                .Where(contact => contact.Channel == GuestContactChannel.Email && contact.ValueHash == emailKey && contact.GuestCrmProfileId != null)
                .Select(contact => contact.GuestCrmProfileId)
                .ToListAsync(cancellationToken);
            ids.AddRange(byEmail.OfType<Guid>());
        }

        if (phoneKey is not null)
        {
            var byPhone = await CrmProfileContactReader.Visible(db, orgId)
                .AsNoTracking()
                .Where(contact => (contact.Channel == GuestContactChannel.Phone || contact.Channel == GuestContactChannel.WhatsApp)
                    && contact.ValueHash == phoneKey
                    && contact.GuestCrmProfileId != null)
                .Select(contact => contact.GuestCrmProfileId)
                .ToListAsync(cancellationToken);
            ids.AddRange(byPhone.OfType<Guid>());

            ids.AddRange(await db.GuestCrmProfiles
                .AsNoTracking()
                .Where(profile => profile.OrgId == orgId && profile.PhoneKeyHash == phoneKey)
                .Select(profile => profile.Id)
                .ToListAsync(cancellationToken));
        }

        return ids.Distinct().ToList();
    }

    /// <summary>
    /// The order of the page. The name sorts by the guest the name is shown from. A caller who reaches the whole org sorts by the totals stored on
    /// the profile; a limited one by the stays, spend and last arrival at the properties it reaches, as sub-queries of the same statement.
    /// </summary>
    private IQueryable<GuestCrmProfile> Ordered(IQueryable<GuestCrmProfile> query, HostScope scope, CrmProfileSort sort)
    {
        if (sort == CrmProfileSort.Name)
        {
            var guests = NamedGuests(scope);
            return query
                .OrderBy(profile => guests
                    .Where(guest => guest.GuestCrmProfileId == profile.Id)
                    .OrderBy(guest => guest.Id == profile.PrimaryGuestId ? 0 : 1)
                    .ThenBy(guest => guest.CreatedAt)
                    .ThenBy(guest => guest.Id)
                    .Select(guest => guest.LastName + " " + guest.FirstName)
                    .FirstOrDefault())
                .ThenBy(profile => profile.Id);
        }

        if (scope.IsOrgWide)
        {
            return sort switch
            {
                CrmProfileSort.Spend => query
                    .OrderByDescending(profile => profile.TotalSpend)
                    .ThenByDescending(profile => profile.LastStayAt)
                    .ThenBy(profile => profile.Id),
                CrmProfileSort.Stays => query
                    .OrderByDescending(profile => profile.TotalStays)
                    .ThenByDescending(profile => profile.LastStayAt)
                    .ThenBy(profile => profile.Id),
                _ => query
                    .OrderByDescending(profile => profile.TotalStays > 0)
                    .ThenByDescending(profile => profile.LastStayAt)
                    .ThenBy(profile => profile.Id),
            };
        }

        var stays = StaysInScope(scope);
        return sort switch
        {
            CrmProfileSort.Spend => query
                .OrderByDescending(profile => stays.Where(stay => stay.Guest.GuestCrmProfileId == profile.Id).Sum(stay => stay.TotalPrice))
                .ThenBy(profile => profile.Id),
            CrmProfileSort.Stays => query
                .OrderByDescending(profile => stays.Count(stay => stay.Guest.GuestCrmProfileId == profile.Id))
                .ThenBy(profile => profile.Id),
            _ => query
                .OrderByDescending(profile => stays.Any(stay => stay.Guest.GuestCrmProfileId == profile.Id))
                .ThenByDescending(profile => stays.Where(stay => stay.Guest.GuestCrmProfileId == profile.Id).Max(stay => (DateTime?)stay.CheckInDate))
                .ThenBy(profile => profile.Id),
        };
    }

    /// <summary>The stays that happened at the properties the caller reaches (<see cref="GuestMatchRules.StayOf"/>).</summary>
    private IQueryable<Booking> StaysInScope(HostScope scope) =>
        BookingsInScope(scope).Where(GuestMatchRules.StayOf(timeProvider.TodayInRome()));

    /// <summary>
    /// The guests of the org that give a profile its name and that the caller reaches (a guest that is erased gives none): the guests of a limited
    /// caller are the ones with a booking at a property it reaches, as everywhere (<c>InScope</c>).
    /// </summary>
    private IQueryable<Guest> NamedGuests(HostScope scope)
    {
        var orgId = scope.OrgId;
        return db.Guests
            .Where(guest => guest.OrgId == orgId
                && guest.GuestCrmProfileId != null
                && !guest.IsDeleted
                && !guest.ErasureRequested
                && guest.DataAnonymizedDate == null)
            .InScope(scope, db.Bookings);
    }

    // ---- the items of a page ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The list items of the profiles <paramref name="ids"/> that the caller reaches, in that order: the data of the profiles, then the guests,
    /// bookings, contacts and proposals of all of them at once.
    /// </summary>
    private async Task<List<CrmProfileListItem>> BuildItemsAsync(HostScope scope, List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return [];

        var today = timeProvider.TodayInRome();
        var profileRows = await ProfilesInScope(scope)
            .AsNoTracking()
            .Where(profile => ids.Contains(profile.Id))
            .Select(profile => new ProfileRow(
                profile.Id,
                profile.Country,
                profile.Language,
                profile.TotalStays,
                profile.TotalSpend,
                profile.FirstStayAt,
                profile.LastStayAt,
                profile.TagsJson,
                profile.PrimaryGuestId))
            .ToListAsync(cancellationToken);
        if (profileRows.Count == 0)
            return [];

        var found = profileRows.Select(row => row.Id).ToList();
        var guests = (await GuestNamesAsync(scope, found, cancellationToken)).ToLookup(guest => guest.ProfileId);
        var bookings = (await BookingRowsAsync(scope, found, cancellationToken)).ToLookup(booking => booking.ProfileId);
        var contacts = (await CrmProfileContactReader.LoadAsync(db, scope.OrgId, found, cancellationToken))
            .ToLookup(contact => contact.GuestCrmProfileId);
        var proposals = await ProposalCountsAsync(scope, found, cancellationToken);
        var byId = profileRows.ToDictionary(row => row.Id);

        var items = new List<CrmProfileListItem>(ids.Count);
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var row))
                continue;

            var contactsOfProfile = contacts[id].ToList();
            var (_, name) = ChooseName(guests[id], row.PrimaryGuestId);
            if (name.Length == 0 && scope.IsOrgWide)
                name = LeadName(contactsOfProfile);

            var bookingsOfProfile = bookings[id].ToList();
            var email = BestContact(contactsOfProfile, GuestContactChannel.Email);
            var whatsapp = BestContact(contactsOfProfile, GuestContactChannel.WhatsApp);

            int stays;
            decimal spend;
            DateTime? first;
            DateTime? last;
            if (scope.IsOrgWide)
            {
                stays = row.TotalStays;
                spend = row.TotalSpend;
                first = stays > 0 ? row.FirstStayAt : null;
                last = stays > 0 ? row.LastStayAt : null;
            }
            else
            {
                var happened = bookingsOfProfile.Where(booking => GuestMatchRules.IsStay(booking.Status, booking.CheckOutDate, today)).ToList();
                stays = happened.Count;
                spend = happened.Sum(booking => booking.TotalPrice);
                first = stays > 0 ? happened.Min(booking => booking.CheckInDate) : null;
                last = stays > 0 ? happened.Max(booking => booking.CheckInDate) : null;
            }

            items.Add(new CrmProfileListItem(
                row.Id,
                name,
                row.Country,
                row.Language,
                stays,
                spend,
                first,
                last,
                bookingsOfProfile
                    .GroupBy(booking => booking.Source)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key.ToString(), StringComparer.Ordinal)
                    .Select(group => group.Key.ToString())
                    .ToList(),
                CrmProfileRules.ParseTags(row.TagsJson),
                PersonalDataMasking.MaskEmail(email?.Value),
                email?.LegalBasis ?? GuestContactLegalBasis.None,
                contactsOfProfile.Any(contact => contact.Channel == GuestContactChannel.Email && CrmContactRules.CanReceiveMarketing(contact)),
                whatsapp?.LegalBasis ?? GuestContactLegalBasis.None,
                proposals.GetValueOrDefault(id) > 0));
        }

        return items;
    }

    /// <summary>The guests that give the profiles their names, among those the caller reaches.</summary>
    private async Task<List<GuestNameRow>> GuestNamesAsync(HostScope scope, List<Guid> profileIds, CancellationToken cancellationToken)
    {
        return await NamedGuests(scope)
            .AsNoTracking()
            .Where(guest => profileIds.Contains(guest.GuestCrmProfileId!.Value))
            .Select(guest => new GuestNameRow(guest.Id, guest.GuestCrmProfileId!.Value, guest.FirstName, guest.LastName, guest.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The bookings, not cancelled, that the caller reaches, of the guests of these profiles: the rows the figures of a page are made of.</summary>
    private async Task<List<BookingRow>> BookingRowsAsync(HostScope scope, List<Guid> profileIds, CancellationToken cancellationToken)
    {
        return await BookingsInScope(scope)
            .AsNoTracking()
            .Where(booking => booking.Status != BookingStatus.Cancelled
                && booking.Guest.GuestCrmProfileId != null
                && profileIds.Contains(booking.Guest.GuestCrmProfileId.Value))
            .Select(booking => new BookingRow(
                booking.Guest.GuestCrmProfileId!.Value,
                booking.Source,
                booking.Status,
                booking.CheckInDate,
                booking.CheckOutDate,
                booking.TotalPrice))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The reviews of these bookings that are public or reported (not the ones the staff took down), the mark and the date only.</summary>
    private async Task<CrmReviewSummary> ReviewsOfAsync(Guid orgId, List<Guid> bookingIds, CancellationToken cancellationToken)
    {
        if (bookingIds.Count == 0)
            return new CrmReviewSummary(0, null, []);

        var reviews = await db.Reviews
            .AsNoTracking()
            .Where(review => review.OrgId == orgId && review.Status != ReviewStatus.Removed && bookingIds.Contains(review.BookingId))
            .Select(review => new CrmReviewEntry(review.Rating, review.PublishedAt))
            .ToListAsync(cancellationToken);
        if (reviews.Count == 0)
            return new CrmReviewSummary(0, null, []);

        return new CrmReviewSummary(
            reviews.Count,
            Math.Round(reviews.Average(review => (double)review.Rating), 2),
            reviews
                .OrderByDescending(review => review.PublishedAt)
                .Take(CrmProfileRules.LatestReviewsShown)
                .ToList());
    }

    /// <summary>
    /// For each of these profiles, the merge proposals waiting for a person that the caller may decide: both profiles of the proposal in its
    /// scope. A proposal with a profile the caller does not reach is not counted, so a limited caller learns nothing of a duplicate it cannot see.
    /// </summary>
    private async Task<Dictionary<Guid, int>> ProposalCountsAsync(HostScope scope, List<Guid> profileIds, CancellationToken cancellationToken)
    {
        var orgId = scope.OrgId;
        var pairs = await db.GuestMergeCandidates
            .AsNoTracking()
            .Where(candidate => candidate.OrgId == orgId
                && candidate.Status == GuestMergeStatus.Proposed
                && (profileIds.Contains(candidate.ProfileAId) || profileIds.Contains(candidate.ProfileBId)))
            .Select(candidate => new { candidate.ProfileAId, candidate.ProfileBId })
            .ToListAsync(cancellationToken);

        var counts = new Dictionary<Guid, int>();
        if (pairs.Count == 0)
            return counts;

        var involved = pairs.SelectMany(pair => new[] { pair.ProfileAId, pair.ProfileBId }).Distinct().ToList();
        var reachable = (await ProfilesInScope(scope)
                .Where(profile => involved.Contains(profile.Id))
                .Select(profile => profile.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var pair in pairs.Where(pair => reachable.Contains(pair.ProfileAId) && reachable.Contains(pair.ProfileBId)))
        {
            foreach (var id in new[] { pair.ProfileAId, pair.ProfileBId })
            {
                if (profileIds.Contains(id))
                    counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return counts;
    }

    // ---- the pieces of an item ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The guest the name is shown from and the name: the primary guest of the profile when the caller reaches it, else the first guest it reaches
    /// (the oldest record). Empty without any.
    /// </summary>
    private static (Guid? GuestId, string Name) ChooseName(IEnumerable<GuestNameRow> guests, Guid? primaryGuestId)
    {
        var chosen = guests
            .OrderBy(guest => guest.GuestId == primaryGuestId ? 0 : 1)
            .ThenBy(guest => guest.CreatedAt)
            .ThenBy(guest => guest.GuestId)
            .FirstOrDefault();
        if (chosen is null)
            return (null, string.Empty);

        var parts = new[] { chosen.FirstName, chosen.LastName }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim());
        return (chosen.GuestId, string.Join(' ', parts));
    }

    /// <summary>
    /// The name a lead gave on the welcome page, for a profile that has no guest to take a name from: the oldest contact that has a name. Asked
    /// for a caller who reaches the whole org only, the only scope a lead is in.
    /// </summary>
    private static string LeadName(IEnumerable<GuestContact> contacts) =>
        contacts
            .Where(contact => !string.IsNullOrWhiteSpace(contact.DisplayName))
            .OrderBy(contact => contact.CreatedAt)
            .ThenBy(contact => contact.Id)
            .Select(contact => contact.DisplayName!.Trim())
            .FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// The contact of the channel the profile is shown by: the one that may receive marketing, else a contact that is not suppressed, not an
    /// OTA forward, with the strongest basis, the oldest first. <c>null</c> without a contact on the channel.
    /// </summary>
    private static GuestContact? BestContact(IEnumerable<GuestContact> contacts, GuestContactChannel channel) =>
        contacts
            .Where(contact => contact.Channel == channel)
            .OrderByDescending(contact => CrmContactRules.CanReceiveMarketing(contact))
            .ThenBy(contact => contact.IsSuppressed)
            .ThenBy(contact => contact.Origin == GuestContactOrigin.OtaForward)
            .ThenByDescending(contact => contact.LegalBasis)
            .ThenBy(contact => contact.CreatedAt)
            .ThenBy(contact => contact.Id)
            .FirstOrDefault();

    private static List<CrmContactView> ViewsOf(IEnumerable<GuestContact> contacts) =>
        contacts
            .OrderBy(contact => contact.Channel)
            .ThenBy(contact => contact.CreatedAt)
            .ThenBy(contact => contact.Id)
            .Select(contact => new CrmContactView(
                contact.Id,
                contact.Channel,
                contact.Origin,
                contact.Confirmation,
                contact.LegalBasis,
                contact.BasisVersion,
                contact.BasisRecordedAt,
                contact.IsSuppressed,
                contact.Origin == GuestContactOrigin.OtaForward,
                contact.Value,
                CrmContactRules.CanReceiveMarketing(contact),
                CrmContactRules.ReachabilityReason(contact)))
            .ToList();

    /// <summary>The totals over the stays that happened (the caller reaches their properties).</summary>
    private static CrmProfileTotals TotalsOf(List<StayRow> stays)
    {
        if (stays.Count == 0)
            return new CrmProfileTotals(0, 0m, 0m, null, [], [], 0d, 0d, null);

        var spend = stays.Sum(stay => stay.TotalPrice);
        var parties = stays.Select(stay => (Stay: stay, Party: CrmProfileRules.PartyOf(stay.NumberOfAdults, stay.NumberOfChildren, stay.NumberOfGuests))).ToList();
        var advances = stays
            .Select(stay => CrmProfileRules.AdvanceDays(stay.CreatedAt, stay.CheckInDate))
            .OfType<int>()
            .ToList();

        return new CrmProfileTotals(
            stays.Count,
            spend,
            Math.Round(spend / stays.Count, 2, MidpointRounding.AwayFromZero),
            advances.Count > 0 ? Math.Round(advances.Average(), 1) : null,
            stays
                .GroupBy(stay => stay.Source)
                .Select(group => new CrmChannelTotal(group.Key, group.Count(), group.Sum(stay => stay.TotalPrice)))
                .OrderByDescending(total => total.Stays)
                .ThenBy(total => total.Channel.ToString(), StringComparer.Ordinal)
                .ToList(),
            stays
                .GroupBy(stay => stay.PropertyId)
                .Select(group => new CrmPropertyTotal(group.Key, group.First().PropertyName, group.Count()))
                .OrderByDescending(total => total.Stays)
                .ThenBy(total => total.PropertyName, StringComparer.Ordinal)
                .ToList(),
            Math.Round(parties.Average(party => (double)party.Party.Adults), 1),
            Math.Round(parties.Average(party => (double)party.Party.Children), 1),
            CrmProfileRules.TypicalGroup(parties.Select(party =>
                (CrmProfileRules.GroupOf(party.Party.Adults, party.Party.Children), party.Stay.CheckInDate))));
    }

    private static string? Clean(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static NotFoundException ProfileNotFound() =>
        new("CRM profile not found.") { Code = "crm_profile_not_found", MessageKey = "CrmProfileNotFound" };

    // ---- the rows read ------------------------------------------------------------------------------------------------------------

    private sealed record ProfileRow(
        Guid Id,
        string Country,
        string Language,
        int TotalStays,
        decimal TotalSpend,
        DateTime FirstStayAt,
        DateTime LastStayAt,
        string TagsJson,
        Guid? PrimaryGuestId);

    private sealed record GuestNameRow(Guid GuestId, Guid ProfileId, string FirstName, string LastName, DateTime CreatedAt);

    private sealed record BookingRow(
        Guid ProfileId,
        BookingSource Source,
        BookingStatus Status,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        decimal TotalPrice);

    private sealed record StayRow(
        Guid BookingId,
        Guid PropertyId,
        string PropertyName,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        BookingSource Source,
        string? ChannelLabel,
        BookingStatus Status,
        decimal TotalPrice,
        int NumberOfAdults,
        int NumberOfChildren,
        int NumberOfGuests,
        DateTime CreatedAt);
}
