using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The pieces the proofs of the profile API of the CRM (E07/F01/US03: the read model and the merge decisions) are built from, on top of
/// <see cref="CrmTestWorld"/>: the real services over the real keyed index, profiles and contacts written by hand, and the people of the scope.
/// </summary>
internal static class CrmProfileApiWorld
{
    /// <summary>The creator of the properties of a test org.</summary>
    public const string Owner = "auth0|titolare";

    /// <summary>A collaborator «Solo alcuni».</summary>
    public const string Collaborator = "auth0|collaboratore";

    public static CrmProfileReadService ReadService(AppDbContext db, TimeProvider clock) =>
        new(db, CrmTestIndex.Create(), Options.Create(new CrmOptions()), clock, NullLogger<CrmProfileReadService>.Instance);

    public static CrmProfileMergeService MergeService(AppDbContext db, TimeProvider clock) =>
        new(db, ReadService(db, clock), CrmTestWorld.Matching(db, clock), clock, NullLogger<CrmProfileMergeService>.Instance);

    /// <summary>
    /// A profile as the sync leaves it, with the totals of the whole org written on it (stored by hand: a test says what they are when it needs
    /// to prove that a caller who reaches only some properties never reads them).
    /// </summary>
    public static GuestCrmProfile NewProfile(Guid orgId, Guid? primaryGuestId, int stays, decimal spend) => new()
    {
        OrgId = orgId,
        CompositeKeyHash = Guid.NewGuid().ToString("N"),
        Country = "Italia",
        Language = "it",
        TotalStays = stays,
        TotalSpend = spend,
        PropertyIdsJson = "[]",
        TagsJson = "[]",
        FirstStayAt = CrmTestWorld.Day(-60),
        LastStayAt = CrmTestWorld.Day(-30),
        PrimaryGuestId = primaryGuestId,
    };

    /// <summary>A contact of a profile, with the keyed hash left as a random text (the proofs here never search by it).</summary>
    public static GuestContact NewContact(
        Guid orgId,
        Guid profileId,
        GuestContactChannel channel,
        string value,
        GuestContactOrigin origin,
        GuestContactLegalBasis basis,
        GuestContactConfirmation confirmation = GuestContactConfirmation.Unconfirmed,
        bool suppressed = false,
        Guid? guestId = null)
    {
        return new GuestContact
        {
            OrgId = orgId,
            GuestCrmProfileId = profileId,
            GuestId = guestId,
            Channel = channel,
            Value = value,
            ValueHash = Guid.NewGuid().ToString("N"),
            Origin = origin,
            LegalBasis = basis,
            Confirmation = confirmation,
            IsSuppressed = suppressed,
            CreatedAt = CrmTestWorld.Day(-40),
            UpdatedAt = CrmTestWorld.Day(-40),
        };
    }

    /// <summary>
    /// One person of an org: a house, a guest with a stay that happened there, and the profile of the guest. Added to the context, not saved.
    /// Returns the profile and the house.
    /// </summary>
    public static (GuestCrmProfile Profile, Property House, Guest Guest) AddPerson(
        AppDbContext db,
        Guid orgId,
        string firstName,
        string lastName,
        Property? house = null)
    {
        house ??= AddHouse(db, orgId, $"Casa {lastName}");
        var guest = CrmTestWorld.AddGuest(db, orgId, firstName, lastName, $"{firstName}.{lastName}@example.com".ToLowerInvariant());
        CrmTestWorld.AddBooking(db, guest, house.Id, BookingStatus.CheckedOut, BookingSource.Direct, -30, 2, 200m);
        var profile = NewProfile(orgId, guest.Id, stays: 1, spend: 200m);
        guest.GuestCrmProfileId = profile.Id;
        db.GuestCrmProfiles.Add(profile);
        return (profile, house, guest);
    }

    /// <summary>A house of the org, added to the context.</summary>
    public static Property AddHouse(AppDbContext db, Guid orgId, string name)
    {
        var house = HostScopeScenario.NewProperty(orgId, Owner, name);
        db.Properties.Add(house);
        return house;
    }
}
