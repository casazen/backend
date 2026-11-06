using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestMatchingService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Keyed, never stored in clear</b>: the e-mail key, the phone key and the name-and-birth key are HMAC-SHA256 hashes of
/// <see cref="ICrmContactIndex"/> over the normalized values (<see cref="CrmNormalizer"/>). The profile holds the last two and the contact holds
/// the first; the birth date exists only inside the third.</item>
/// <item><b>Decisions</b> are the table of <see cref="GuestMatchRules"/>: the same e-mail (not an OTA forward) or the same name and birth date
/// link the guest to a profile; a phone or a name at a property only propose a merge, stored as <see cref="GuestMergeCandidate"/> (smaller id
/// first, one row for each pair, a pair already there in any state is never proposed again). A merge is never executed here.</item>
/// <item><b>Tenant</b>: every query names the org of the guest or of the profile, on top of the tenant filter of the scope the service runs in.</item>
/// <item><b>Privacy</b>: reads the columns of the booker it needs (name, e-mail, phone, birth date, country), never a document or any other
/// Alloggiati field, and never <see cref="StayGuest"/>. The logs carry ids, never a value or a hash.</item>
/// </list>
/// </remarks>
public class GuestMatchingService(
    AppDbContext db,
    ICrmContactIndex contactIndex,
    IOptions<CrmOptions> options,
    TimeProvider timeProvider,
    ILogger<GuestMatchingService> logger) : IGuestMatchingService
{
    /// <summary>Longest chain of merged profiles followed to the one that survives; a longer one is a loop.</summary>
    private const int MaxMergeHops = 10;

    /// <summary>Profiles read when looking for the ones that share a phone: the proposals keep five, this is only the pool.</summary>
    private const int PhoneCandidateReadLimit = 50;

    /// <summary>Guests read when looking for the ones with the same name at the same properties.</summary>
    private const int NameCandidateReadLimit = 2000;

    /// <summary>Properties kept in <see cref="GuestCrmProfile.PropertyIdsJson"/>: the column holds 2,000 characters, 39 for each id.</summary>
    private const int MaxPropertyIds = 50;

    private const int CountryMaxLength = 100;

    private const int LanguageMaxLength = GuestContact.LanguageMaxLength;

    public string ComputeCompositeKeyHash(
        string email,
        string firstName,
        string lastName,
        DateTime? dateOfBirth,
        string? fiscalCode = null) =>
        contactIndex.Hash(GuestMatchRules.CompositeKeyText(email, firstName, lastName, dateOfBirth, fiscalCode));

    public async Task<GuestCrmProfile> FindOrCreateProfileAsync(
        Guest guest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guest);

        if (guest.OrgId == Guid.Empty)
            throw new ArgumentException("Guest must have an OrgId.", nameof(guest));

        guest = Track(guest);
        var orgId = guest.OrgId;
        var keys = KeysOf(guest, await LatestSourceAsync(orgId, guest.Id, cancellationToken));

        // 1. The profile the guest already has; a merged one is followed to the profile that took its place.
        if (guest.GuestCrmProfileId is { } linkedId && await SurvivorAsync(orgId, linkedId, cancellationToken) is { } kept)
        {
            var changed = false;
            if (guest.GuestCrmProfileId != kept.Id)
            {
                guest.GuestCrmProfileId = kept.Id;
                changed = true;
            }

            changed |= FillEmpty(kept, guest, keys);
            if (changed)
                await db.SaveChangesAsync(cancellationToken);

            await ProposeSameEmailAsync(kept, keys, cancellationToken);
            return kept;
        }

        // 2. The profile of the e-mail contact with the same keyed hash, else the profile with the same name and birth date.
        var byEmail = keys.EmailKey is null ? null : await ProfileOfEmailAsync(orgId, keys.EmailKey, cancellationToken);
        var byNameBirth = keys.NameBirthKey is null ? null : await ProfileOfNameBirthAsync(orgId, keys.NameBirthKey, cancellationToken);
        var found = byEmail ?? byNameBirth;
        if (found is not null)
        {
            guest.GuestCrmProfileId = found.Id;
            FillEmpty(found, guest, keys);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Matched guest {GuestId} to CRM profile {ProfileId} of org {OrgId}", guest.Id, found.Id, orgId);

            // The two keys point to two profiles: the guest goes to the e-mail one and a person is asked about the pair.
            if (byEmail is not null && byNameBirth is not null && byEmail.Id != byNameBirth.Id)
            {
                await StoreProposalsAsync(
                    orgId,
                    byEmail.Id,
                    new Dictionary<Guid, List<string>> { [byNameBirth.Id] = [GuestMatchRules.SameNameBirth] },
                    cancellationToken);
            }

            return found;
        }

        // 3. None: a profile of its own, and a question for a person about the ones that may be the same guest.
        var now = Now();
        var profile = new GuestCrmProfile
        {
            OrgId = orgId,
            CompositeKeyHash = ComputeCompositeKeyHash(guest.Email, guest.FirstName, guest.LastName, guest.DateOfBirth),
            ConfirmedMarketingEmail = string.Empty,
            MarketingConsentEmail = false,
            MarketingConsentWhatsApp = false,
            Language = string.Empty,
            Country = Truncate(guest.Country, CountryMaxLength),
            TotalStays = 0,
            TotalSpend = 0m,
            PropertyIdsJson = "[]",
            TagsJson = "[]",
            FirstStayAt = now,
            LastStayAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            PhoneKeyHash = keys.PhoneKey,
            NameBirthKeyHash = keys.NameBirthKey,
            PrimaryGuestId = guest.Id,
        };
        db.GuestCrmProfiles.Add(profile);
        guest.GuestCrmProfileId = profile.Id;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created CRM profile {ProfileId} of org {OrgId} for guest {GuestId}", profile.Id, orgId, guest.Id);

        await ProposeForNewProfileAsync(profile, guest, keys, cancellationToken);
        return profile;
    }

    public async Task<GuestCrmProfile> FindOrCreateLeadProfileAsync(
        Guid orgId,
        string? email,
        string? phone,
        string? displayName,
        string? language,
        CancellationToken cancellationToken = default)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException("A lead needs an org.", nameof(orgId));

        var settings = options.Value;
        string? emailKey = null;
        if (CrmNormalizer.TryNormalizeEmail(email, out var normalizedEmail)
            && !OtaForwardAddress.IsOnRelayDomain(normalizedEmail, settings.OtaForwardDomains))
        {
            emailKey = contactIndex.Hash(normalizedEmail);
        }

        var normalizedPhone = CrmNormalizer.NormalizePhone(phone, settings.DefaultCallingCode);
        var phoneKey = normalizedPhone is null ? null : contactIndex.Hash(normalizedPhone);
        var leadLanguage = NormalizeLanguage(language);

        var byEmail = emailKey is null ? null : await ProfileOfEmailAsync(orgId, emailKey, cancellationToken);
        if (byEmail is not null)
        {
            await FillLeadAsync(byEmail, phoneKey, leadLanguage, cancellationToken);
            return byEmail;
        }

        // A phone is shared by many people: it finds a profile only when it points to exactly one. With several the lead is told apart.
        var sharing = new List<Guid>();
        if (phoneKey is not null)
        {
            sharing = await Profiles(orgId)
                .Where(p => p.MergedIntoProfileId == null && p.PhoneKeyHash == phoneKey)
                .OrderBy(p => p.CreatedAt)
                .ThenBy(p => p.Id)
                .Select(p => p.Id)
                .Take(PhoneCandidateReadLimit)
                .ToListAsync(cancellationToken);
            if (sharing.Count == 1 && await SurvivorAsync(orgId, sharing[0], cancellationToken) is { } byPhone)
            {
                await FillLeadAsync(byPhone, phoneKey, leadLanguage, cancellationToken);
                return byPhone;
            }
        }

        var now = Now();
        var profile = new GuestCrmProfile
        {
            OrgId = orgId,
            CompositeKeyHash = ComputeCompositeKeyHash(normalizedEmail, displayName ?? string.Empty, string.Empty, null),
            ConfirmedMarketingEmail = string.Empty,
            MarketingConsentEmail = false,
            MarketingConsentWhatsApp = false,
            Language = leadLanguage,
            Country = string.Empty,
            TotalStays = 0,
            TotalSpend = 0m,
            PropertyIdsJson = "[]",
            TagsJson = "[]",
            FirstStayAt = now,
            LastStayAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            PhoneKeyHash = phoneKey,
            NameBirthKeyHash = null,
            PrimaryGuestId = null,
        };
        db.GuestCrmProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created lead CRM profile {ProfileId} of org {OrgId}", profile.Id, orgId);

        if (sharing.Count > 1)
        {
            var candidates = sharing.ToDictionary(id => id, _ => new List<string> { GuestMatchRules.SamePhone });
            await StoreProposalsAsync(orgId, profile.Id, candidates, cancellationToken);
        }

        return profile;
    }

    public async Task UpdateProfileAggregatesAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await db.GuestCrmProfiles.FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        if (profile is null)
        {
            logger.LogWarning("CRM profile {ProfileId} not found for aggregate update", profileId);
            return;
        }

        if (profile.MergedIntoProfileId is not null)
        {
            logger.LogInformation("CRM profile {ProfileId} was merged into another one: no aggregate to update", profileId);
            return;
        }

        var orgId = profile.OrgId;
        var guestIds = db.Guests.Where(g => g.OrgId == orgId && g.GuestCrmProfileId == profileId).Select(g => g.Id);
        var bookings = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.Status != BookingStatus.Cancelled && guestIds.Contains(b.GuestId))
            .Select(b => new { b.PropertyId, b.Status, b.CheckInDate, b.CheckOutDate, b.TotalPrice })
            .ToListAsync(cancellationToken);

        var today = timeProvider.TodayInRome();
        var stays = bookings.Where(b => GuestMatchRules.IsStay(b.Status, b.CheckOutDate, today)).ToList();

        // With no stay yet the two dates are the creation of the profile: TotalStays is 0, which is what a reader looks at first.
        var totalStays = stays.Count;
        var totalSpend = stays.Sum(s => s.TotalPrice);
        var firstStay = totalStays == 0 ? profile.CreatedAt : AsUtc(stays.Min(s => s.CheckInDate));
        var lastStay = totalStays == 0 ? profile.CreatedAt : AsUtc(stays.Max(s => s.CheckInDate));

        // Every booking that is not cancelled, the ones to come included, so the scope of a host sees the guest before the arrival. Sorted, so
        // that the same bookings always write the same text and a second run changes nothing.
        var propertyIds = bookings.Select(b => b.PropertyId).Distinct().Order().ToList();
        if (propertyIds.Count > MaxPropertyIds)
        {
            logger.LogWarning(
                "CRM profile {ProfileId} of org {OrgId} has {Count} properties: only {Max} are kept",
                profileId,
                orgId,
                propertyIds.Count,
                MaxPropertyIds);
            propertyIds = propertyIds.Take(MaxPropertyIds).ToList();
        }

        var propertyIdsJson = JsonSerializer.Serialize(propertyIds);

        var changed = false;
        if (profile.TotalStays != totalStays)
        {
            profile.TotalStays = totalStays;
            changed = true;
        }

        if (profile.TotalSpend != totalSpend)
        {
            profile.TotalSpend = totalSpend;
            changed = true;
        }

        if (profile.FirstStayAt != firstStay)
        {
            profile.FirstStayAt = firstStay;
            changed = true;
        }

        if (profile.LastStayAt != lastStay)
        {
            profile.LastStayAt = lastStay;
            changed = true;
        }

        if (!string.Equals(profile.PropertyIdsJson, propertyIdsJson, StringComparison.Ordinal))
        {
            profile.PropertyIdsJson = propertyIdsJson;
            changed = true;
        }

        if (!changed)
            return;

        profile.UpdatedAt = Now();
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Updated CRM profile {ProfileId}: {Stays} stays, {Properties} properties",
            profileId,
            totalStays,
            propertyIds.Count);
    }

    // ---- keys ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The keys of a guest: what it is found by, hashed. <c>null</c> where there is nothing usable to hash.</summary>
    private sealed record MatchKeys(string? EmailKey, string? PhoneKey, string? NameBirthKey, string NormalizedName);

    private MatchKeys KeysOf(Guest guest, BookingSource? source)
    {
        var settings = options.Value;

        // An address that an online travel agency forwards the mail through is never a key (AC-NF88-2): two guests may get the same one.
        string? emailKey = null;
        if (CrmNormalizer.TryNormalizeEmail(guest.Email, out var email)
            && !OtaForwardAddress.IsForward(email, source, settings.OtaForwardDomains))
        {
            emailKey = contactIndex.Hash(email);
        }

        var phone = CrmNormalizer.NormalizePhone(guest.PhoneNumber, settings.DefaultCallingCode);
        var phoneKey = phone is null ? null : contactIndex.Hash(phone);

        var name = CrmNormalizer.NormalizeName(guest.FirstName, guest.LastName);
        var birthText = GuestMatchRules.NameBirthKeyText(name, guest.DateOfBirth, timeProvider.TodayInRome());
        var nameBirthKey = birthText is null ? null : contactIndex.Hash(birthText);

        return new MatchKeys(emailKey, phoneKey, nameBirthKey, name);
    }

    /// <summary>The source of the latest booking of the guest, <c>null</c> for a guest with none.</summary>
    private async Task<BookingSource?> LatestSourceAsync(Guid orgId, Guid guestId, CancellationToken cancellationToken) =>
        await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.GuestId == guestId)
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id)
            .Select(b => (BookingSource?)b.Source)
            .FirstOrDefaultAsync(cancellationToken);

    // ---- profiles -----------------------------------------------------------------------------------------------------------------

    private IQueryable<GuestCrmProfile> Profiles(Guid orgId) => db.GuestCrmProfiles.Where(p => p.OrgId == orgId);

    /// <summary>
    /// The profile that is alive at the end of the merges that started at <paramref name="profileId"/> (a merged profile is a tombstone that
    /// points to the one that took its place), tracked. <c>null</c> when the org has no such profile or the chain is a loop.
    /// </summary>
    private async Task<GuestCrmProfile?> SurvivorAsync(Guid orgId, Guid profileId, CancellationToken cancellationToken)
    {
        var current = profileId;
        for (var hop = 0; hop < MaxMergeHops; hop++)
        {
            var profile = await Profiles(orgId).FirstOrDefaultAsync(p => p.Id == current, cancellationToken);
            if (profile is null)
                return null;

            if (profile.MergedIntoProfileId is not { } next)
                return profile;

            current = next;
        }

        logger.LogWarning("CRM profile {ProfileId} of org {OrgId}: the chain of merges is longer than {Hops}", profileId, orgId, MaxMergeHops);
        return null;
    }

    /// <summary>The profile of the e-mail contact with the keyed hash, surviving; an OTA forward is not a way to find a person.</summary>
    private async Task<GuestCrmProfile?> ProfileOfEmailAsync(Guid orgId, string emailKey, CancellationToken cancellationToken)
    {
        var profileId = await db.GuestContacts
            .AsNoTracking()
            .Where(c => c.OrgId == orgId
                && c.Channel == GuestContactChannel.Email
                && c.ValueHash == emailKey
                && c.GuestCrmProfileId != null
                && c.Origin != GuestContactOrigin.OtaForward)
            .Select(c => c.GuestCrmProfileId)
            .FirstOrDefaultAsync(cancellationToken);

        return profileId is { } id ? await SurvivorAsync(orgId, id, cancellationToken) : null;
    }

    /// <summary>The oldest living profile with the keyed name-and-birth hash, surviving.</summary>
    private async Task<GuestCrmProfile?> ProfileOfNameBirthAsync(Guid orgId, string nameBirthKey, CancellationToken cancellationToken)
    {
        var profileId = await Profiles(orgId)
            .Where(p => p.MergedIntoProfileId == null && p.NameBirthKeyHash == nameBirthKey)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return profileId is { } id ? await SurvivorAsync(orgId, id, cancellationToken) : null;
    }

    /// <summary>
    /// Fills what the profile has not got and the guest gives: the keys, the guest shown as its name, the country. Never overwrites. Returns
    /// whether something changed (and then the update time moves); the caller saves.
    /// </summary>
    private bool FillEmpty(GuestCrmProfile profile, Guest guest, MatchKeys keys)
    {
        var changed = false;
        if (profile.PhoneKeyHash is null && keys.PhoneKey is not null)
        {
            profile.PhoneKeyHash = keys.PhoneKey;
            changed = true;
        }

        if (profile.NameBirthKeyHash is null && keys.NameBirthKey is not null)
        {
            profile.NameBirthKeyHash = keys.NameBirthKey;
            changed = true;
        }

        if (profile.PrimaryGuestId is null)
        {
            profile.PrimaryGuestId = guest.Id;
            changed = true;
        }

        if (string.IsNullOrEmpty(profile.Country) && Truncate(guest.Country, CountryMaxLength) is { Length: > 0 } country)
        {
            profile.Country = country;
            changed = true;
        }

        if (changed)
            profile.UpdatedAt = Now();

        return changed;
    }

    /// <summary>A lead found again gives what it has not given before: a phone key and a language. Saves when something changed.</summary>
    private async Task FillLeadAsync(GuestCrmProfile profile, string? phoneKey, string language, CancellationToken cancellationToken)
    {
        var changed = false;
        if (profile.PhoneKeyHash is null && phoneKey is not null)
        {
            profile.PhoneKeyHash = phoneKey;
            changed = true;
        }

        if (profile.Language.Length == 0 && language.Length > 0)
        {
            profile.Language = language;
            changed = true;
        }

        if (!changed)
            return;

        profile.UpdatedAt = Now();
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- proposals ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A guest that kept its profile: if the address of the guest is held by a contact of another profile, the two profiles hold the same
    /// address (a race of two syncs, a guest whose address was corrected to one already known): proposed, score 100.
    /// </summary>
    private async Task ProposeSameEmailAsync(GuestCrmProfile profile, MatchKeys keys, CancellationToken cancellationToken)
    {
        if (keys.EmailKey is null)
            return;

        var other = await ProfileOfEmailAsync(profile.OrgId, keys.EmailKey, cancellationToken);
        if (other is null || other.Id == profile.Id)
            return;

        await StoreProposalsAsync(
            profile.OrgId,
            profile.Id,
            new Dictionary<Guid, List<string>> { [other.Id] = [GuestMatchRules.SameEmail] },
            cancellationToken);
    }

    /// <summary>
    /// A profile that was just created: the profiles that share its phone (80) or a normalized name and a property stayed in (60) may be the same
    /// guest. A person decides; nothing is merged here.
    /// </summary>
    private async Task ProposeForNewProfileAsync(GuestCrmProfile profile, Guest guest, MatchKeys keys, CancellationToken cancellationToken)
    {
        var orgId = profile.OrgId;
        var candidates = new Dictionary<Guid, List<string>>();

        if (keys.PhoneKey is not null)
        {
            var sharing = await Profiles(orgId)
                .Where(p => p.Id != profile.Id && p.MergedIntoProfileId == null && p.PhoneKeyHash == keys.PhoneKey)
                .OrderBy(p => p.CreatedAt)
                .ThenBy(p => p.Id)
                .Select(p => p.Id)
                .Take(PhoneCandidateReadLimit)
                .ToListAsync(cancellationToken);
            foreach (var id in sharing)
                AddReason(candidates, id, GuestMatchRules.SamePhone);
        }

        if (GuestMatchRules.IsMatchableName(keys.NormalizedName))
        {
            foreach (var id in await ProfilesWithSameNameAtMyPropertiesAsync(profile, guest, keys.NormalizedName, cancellationToken))
                AddReason(candidates, id, GuestMatchRules.SameNameSameProperty);
        }

        if (candidates.Count > 0)
            await StoreProposalsAsync(orgId, profile.Id, candidates, cancellationToken);
    }

    /// <summary>
    /// The living profiles of other guests of the org who have the same normalized name and a booking, not cancelled, at a property this guest
    /// has a booking at. The names are compared after the normalization, which the database cannot do, over the few guests of those properties.
    /// </summary>
    private async Task<List<Guid>> ProfilesWithSameNameAtMyPropertiesAsync(
        GuestCrmProfile profile,
        Guest guest,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        var orgId = profile.OrgId;
        var propertyIds = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.GuestId == guest.Id && b.Status != BookingStatus.Cancelled)
            .Select(b => b.PropertyId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (propertyIds.Count == 0)
            return [];

        var rows = await (
                from booking in db.Bookings.AsNoTracking()
                join other in db.Guests.AsNoTracking() on booking.GuestId equals other.Id
                where booking.OrgId == orgId
                    && other.OrgId == orgId
                    && booking.Status != BookingStatus.Cancelled
                    && propertyIds.Contains(booking.PropertyId)
                    && other.Id != guest.Id
                    && other.GuestCrmProfileId != null
                    && other.GuestCrmProfileId != profile.Id
                select new { ProfileId = other.GuestCrmProfileId, other.FirstName, other.LastName })
            .Distinct()
            .Take(NameCandidateReadLimit)
            .ToListAsync(cancellationToken);

        var sameName = rows
            .Where(row => row.ProfileId is not null && CrmNormalizer.NormalizeName(row.FirstName, row.LastName) == normalizedName)
            .Select(row => row.ProfileId!.Value)
            .Distinct()
            .ToList();
        if (sameName.Count == 0)
            return [];

        return await Profiles(orgId)
            .Where(p => sameName.Contains(p.Id) && p.MergedIntoProfileId == null)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
    }

    private static void AddReason(Dictionary<Guid, List<string>> candidates, Guid profileId, string reason)
    {
        if (!candidates.TryGetValue(profileId, out var reasons))
            candidates[profileId] = reasons = [];

        if (!reasons.Contains(reason))
            reasons.Add(reason);
    }

    /// <summary>
    /// Stores the proposals of a pair of profiles, the strongest first, within the cap of <see cref="GuestMatchRules.MaxProposalsPerProfile"/>
    /// for the profile and for each profile it points at. A pair that has a row in any state (proposed, merged, dismissed) is skipped: a dismissed
    /// pair is never proposed again. The pair is stored with the smaller id first.
    /// </summary>
    private async Task StoreProposalsAsync(
        Guid orgId,
        Guid profileId,
        Dictionary<Guid, List<string>> candidates,
        CancellationToken cancellationToken)
    {
        var rowsOfProfile = await db.GuestMergeCandidates
            .AsNoTracking()
            .Where(c => c.OrgId == orgId && (c.ProfileAId == profileId || c.ProfileBId == profileId))
            .Select(c => new { c.ProfileAId, c.ProfileBId, c.Status })
            .ToListAsync(cancellationToken);
        var known = rowsOfProfile.Select(c => (c.ProfileAId, c.ProfileBId)).ToHashSet();
        var room = GuestMatchRules.MaxProposalsPerProfile - rowsOfProfile.Count(c => c.Status == GuestMergeStatus.Proposed);
        if (room <= 0)
            return;

        var candidateIds = candidates.Keys.ToList();
        var openRows = await db.GuestMergeCandidates
            .AsNoTracking()
            .Where(c => c.OrgId == orgId
                && c.Status == GuestMergeStatus.Proposed
                && (candidateIds.Contains(c.ProfileAId) || candidateIds.Contains(c.ProfileBId)))
            .Select(c => new { c.ProfileAId, c.ProfileBId })
            .ToListAsync(cancellationToken);
        var open = candidateIds.ToDictionary(id => id, id => openRows.Count(c => c.ProfileAId == id || c.ProfileBId == id));

        var now = Now();
        var added = new List<GuestMergeCandidate>();
        foreach (var candidate in candidates
                     .OrderByDescending(pair => GuestMatchRules.ScoreOf(pair.Value))
                     .ThenBy(pair => pair.Key))
        {
            if (room <= 0)
                break;

            var (profileAId, profileBId) = GuestMatchRules.OrderPair(profileId, candidate.Key);
            if (known.Contains((profileAId, profileBId)) || open[candidate.Key] >= GuestMatchRules.MaxProposalsPerProfile)
                continue;

            var proposal = new GuestMergeCandidate
            {
                OrgId = orgId,
                ProfileAId = profileAId,
                ProfileBId = profileBId,
                Score = GuestMatchRules.ScoreOf(candidate.Value),
                ReasonsJson = GuestMatchRules.ReasonsJson(candidate.Value),
                Status = GuestMergeStatus.Proposed,
                CreatedAt = now,
            };
            db.GuestMergeCandidates.Add(proposal);
            added.Add(proposal);
            known.Add((profileAId, profileBId));
            open[candidate.Key]++;
            room--;
        }

        if (added.Count == 0)
            return;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Proposed {Count} merge(s) for CRM profile {ProfileId} of org {OrgId}", added.Count, profileId, orgId);
        }
        catch (DbUpdateException ex) when (CrmDatabaseErrors.IsUniqueViolation(ex) && db.Database.CurrentTransaction is null)
        {
            // Another sync proposed one of these pairs at the same moment: the database kept the first, and the question is asked once.
            foreach (var row in added)
                db.Entry(row).State = EntityState.Detached;

            logger.LogInformation("A merge proposal for CRM profile {ProfileId} of org {OrgId} was already there", profileId, orgId);
        }
    }

    // ---- small helpers ------------------------------------------------------------------------------------------------------------

    /// <summary>The tracked instance of the guest: the one the context already has, else the given one, attached.</summary>
    private Guest Track(Guest guest)
    {
        if (db.Entry(guest).State != EntityState.Detached)
            return guest;

        var tracked = db.Guests.Local.FirstOrDefault(g => g.Id == guest.Id);
        if (tracked is not null)
            return tracked;

        db.Guests.Attach(guest);
        return guest;
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Truncate(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string NormalizeLanguage(string? language)
    {
        var text = Truncate(language, LanguageMaxLength).ToLowerInvariant();
        return text;
    }
}
