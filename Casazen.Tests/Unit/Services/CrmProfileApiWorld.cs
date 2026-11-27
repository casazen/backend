using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Unit.Services;

internal static class CrmProfileApiWorld
{
    public const string Owner = "auth0|titolare";

    public const string Collaborator = "auth0|collaboratore";

    public static CrmProfileReadService ReadService(AppDbContext db, TimeProvider clock) =>
        new(db, CrmTestIndex.Create(), Options.Create(new CrmOptions()), clock, NullLogger<CrmProfileReadService>.Instance);

    public static CrmProfileMergeService MergeService(AppDbContext db, TimeProvider clock) =>
        new(db, ReadService(db, clock), CrmTestWorld.Matching(db, clock), clock, NullLogger<CrmProfileMergeService>.Instance);

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

    public static Property AddHouse(AppDbContext db, Guid orgId, string name)
    {
        var house = HostScopeScenario.NewProperty(orgId, Owner, name);
        db.Properties.Add(house);
        return house;
    }
}
