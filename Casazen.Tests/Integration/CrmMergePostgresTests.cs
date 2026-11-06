using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// NF-88 (E07/F01/US03/T02) on a real PostgreSQL database, what EF InMemory cannot prove. A merge is one change under the advisory lock of the
/// org, so six merges of the same proposal at the same moment (some keeping the one profile, some the other) merge once and the others find it
/// decided (409 <c>crm_merge_not_proposed</c>). And the change itself, with the foreign keys, the unique index and the order check of the
/// proposals enforced: the guests and the contacts of the profile that goes are moved by setting the link and nothing else (every other column
/// of a guest and of a contact is as it was), and the other proposals of that profile move to the survivor, or are dropped when they would repeat
/// a pair, with the pair always in order.
/// </summary>
public class CrmMergePostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateMigratedAsync("crmmerge");
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    /// <summary>
    /// One proposal between two profiles of one person; each profile has a guest and contacts (a service-only address; a confirmed consent and a
    /// withdrawn WhatsApp number), and three other profiles, each proposed with one or both of the two: with the first, with both, with the second
    /// only. Whichever profile is kept, it ends with one open proposal for each of the three others.
    /// </summary>
    private sealed record World(Guid OrgId, Guid FirstId, Guid SecondId, Guid DecidedId, IReadOnlyList<Guid> OtherProfileIds);

    private async Task<World> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var db = _database!.CreateContext();
        var org = HostScopeScenario.NewOrg("Casa CRM", $"crm-merge-{suffix}");
        var owner = HostScopeScenario.NewUser($"auth0|crm-merge-{suffix}", org.Id, UserRole.PropertyOwner);
        db.Orgs.Add(org);
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var house = HostScopeScenario.NewProperty(org.Id, owner.Id, "Trullo");
        db.Properties.Add(house);
        await db.SaveChangesAsync();

        var mario = CrmTestWorld.AddGuest(db, org.Id, "Mario", "Rossi", "mario.rossi@example.com");
        var emme = CrmTestWorld.AddGuest(db, org.Id, "M.", "Rossi", "m.rossi@example.org");
        CrmTestWorld.AddBooking(db, mario, house.Id, BookingStatus.CheckedOut, BookingSource.Direct, -30, 2, 200m);
        CrmTestWorld.AddBooking(db, emme, house.Id, BookingStatus.CheckedOut, BookingSource.Airbnb, -90, 3, 300m);

        var first = CrmProfileApiWorld.NewProfile(org.Id, mario.Id, stays: 1, spend: 200m);
        var second = CrmProfileApiWorld.NewProfile(org.Id, emme.Id, stays: 1, spend: 300m);
        first.TagsJson = "[\"vip\"]";
        second.TagsJson = "[\"nuovo\"]";
        mario.GuestCrmProfileId = first.Id;
        emme.GuestCrmProfileId = second.Id;
        var others = Enumerable.Range(0, 3).Select(_ => CrmProfileApiWorld.NewProfile(org.Id, null, stays: 0, spend: 0m)).ToList();
        db.GuestCrmProfiles.AddRange(first, second);
        db.GuestCrmProfiles.AddRange(others);

        var address = CrmProfileApiWorld.NewContact(
            org.Id, first.Id, GuestContactChannel.Email, "mario.rossi@example.com", GuestContactOrigin.DirectBooking, GuestContactLegalBasis.ServiceOnly, guestId: mario.Id);
        var consent = CrmProfileApiWorld.NewContact(
            org.Id, second.Id, GuestContactChannel.Email, "m.rossi@example.org", GuestContactOrigin.CheckIn, GuestContactLegalBasis.Consent, GuestContactConfirmation.Confirmed, guestId: emme.Id);
        consent.BasisVersion = "consent-v1";
        consent.BasisRecordedAt = CrmTestWorld.Day(-80);
        consent.ConfirmedAt = CrmTestWorld.Day(-79);
        consent.ConsentSource = GuestConsentSource.GuestPortal;
        consent.ConsentIpAddress = "203.0.113.5";
        var withdrawn = CrmProfileApiWorld.NewContact(
            org.Id, second.Id, GuestContactChannel.WhatsApp, "+393334445556", GuestContactOrigin.CheckIn, GuestContactLegalBasis.ServiceOnly, suppressed: true, guestId: emme.Id);
        withdrawn.SuppressedAt = CrmTestWorld.Day(-20);
        withdrawn.SuppressedSource = "unsubscribe";
        db.GuestContacts.AddRange(address, consent, withdrawn);

        var decided = Proposal(org.Id, first.Id, second.Id, 90);
        db.GuestMergeCandidates.AddRange(
            decided,
            Proposal(org.Id, first.Id, others[0].Id, 80),
            Proposal(org.Id, second.Id, others[0].Id, 70),
            Proposal(org.Id, second.Id, others[1].Id, 60),
            Proposal(org.Id, first.Id, others[2].Id, 50));
        await db.SaveChangesAsync();

        return new World(org.Id, first.Id, second.Id, decided.Id, others.Select(profile => profile.Id).ToList());
    }

    private static GuestMergeCandidate Proposal(Guid orgId, Guid one, Guid other, int score)
    {
        var (a, b) = GuestMatchRules.OrderPair(one, other);
        return new GuestMergeCandidate
        {
            OrgId = orgId,
            ProfileAId = a,
            ProfileBId = b,
            Score = score,
            ReasonsJson = GuestMatchRules.ReasonsJson([GuestMatchRules.SamePhone]),
            Status = GuestMergeStatus.Proposed,
            CreatedAt = CrmTestWorld.Day(-5),
        };
    }

    /// <summary>Every column of a guest but the profile it is linked to: what a merge must leave as it is.</summary>
    private static string GuestFingerprint(Guest guest) => string.Join(
        "|",
        guest.Id,
        guest.OrgId,
        guest.FirstName,
        guest.LastName,
        guest.Email,
        guest.PhoneNumber,
        guest.Country,
        guest.IsDeleted,
        guest.CreatedAt.ToString("O"),
        guest.UpdatedAt.ToString("O"));

    /// <summary>Every column of a contact but the profile it belongs to: what a merge must leave as it is.</summary>
    private static string ContactFingerprint(GuestContact contact) => string.Join(
        "|",
        contact.Id,
        contact.OrgId,
        contact.GuestId,
        contact.Channel,
        contact.Value,
        contact.ValueHash,
        contact.DisplayName,
        contact.Language,
        contact.Origin,
        contact.Confirmation,
        contact.ConfirmedAt?.ToString("O"),
        contact.LegalBasis,
        contact.BasisVersion,
        contact.BasisRecordedAt?.ToString("O"),
        contact.ConsentSource,
        contact.ConsentIpAddress,
        contact.IsSuppressed,
        contact.SuppressedAt?.ToString("O"),
        contact.SuppressedSource,
        contact.CreatedAt.ToString("O"),
        contact.UpdatedAt.ToString("O"));

    [PostgresFact]
    public async Task CrmMerge_SixAtTheSameMoment_MergeOnce_AndTheOthersAreRefused_Test()
    {
        var world = await SeedAsync();
        var clock = CrmTestWorld.Clock();
        var scope = new HostScope(world.OrgId);

        List<string> guestsBefore;
        List<string> contactsBefore;
        await using (var read = _database!.CreateContext())
        {
            guestsBefore = (await read.Guests.AsNoTracking().Where(g => g.OrgId == world.OrgId).ToListAsync()).OrderBy(g => g.Id).Select(GuestFingerprint).ToList();
            contactsBefore = (await read.GuestContacts.AsNoTracking().Where(c => c.OrgId == world.OrgId).ToListAsync()).OrderBy(c => c.Id).Select(ContactFingerprint).ToList();
        }

        async Task<string> TryAsync(int worker)
        {
            await using var db = _database!.CreateContext();

            // Some of them ask to keep the one profile, some the other.
            var survivor = worker % 2 == 0 ? world.FirstId : world.SecondId;
            try
            {
                await CrmProfileApiWorld.MergeService(db, clock).MergeAsync(scope, world.DecidedId, survivor, $"auth0|worker-{worker}", CancellationToken.None);
                return "merged";
            }
            catch (DomainConflictException ex)
            {
                return ex.Code;
            }
        }

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(worker => Task.Run(() => TryAsync(worker))));

        // One merges; every other finds the proposal decided, and nothing else goes wrong (a deadlock, a unique or an order violation would be an exception).
        var merged = outcomes.Select((outcome, worker) => (Outcome: outcome, Worker: worker)).Where(result => result.Outcome == "merged").ToList();
        var winner = Assert.Single(merged).Worker;
        Assert.All(outcomes.Where(outcome => outcome != "merged"), outcome => Assert.Equal("crm_merge_not_proposed", outcome));

        var survivorId = winner % 2 == 0 ? world.FirstId : world.SecondId;
        var goneId = survivorId == world.FirstId ? world.SecondId : world.FirstId;
        await using var check = _database!.CreateContext();

        // One tombstone, pointing at the profile the winner asked to keep, which holds the stays and the tags of both.
        var profiles = await check.GuestCrmProfiles.AsNoTracking().Where(p => p.OrgId == world.OrgId).ToListAsync();
        Assert.Single(profiles, p => p.MergedIntoProfileId != null);
        Assert.Equal(survivorId, profiles.Single(p => p.Id == goneId).MergedIntoProfileId);
        var survivor = profiles.Single(p => p.Id == survivorId);
        Assert.Null(survivor.MergedIntoProfileId);
        Assert.Equal(2, survivor.TotalStays);
        Assert.Equal(500m, survivor.TotalSpend);
        Assert.Equal(new[] { "nuovo", "vip" }, CrmProfileRules.ParseTags(survivor.TagsJson).Order().ToList());

        // Every guest and every contact is on the survivor, and not one other column of them changed (the update of a stub sets the link only).
        var guests = await check.Guests.AsNoTracking().Where(g => g.OrgId == world.OrgId).ToListAsync();
        Assert.All(guests, guest => Assert.Equal(survivorId, guest.GuestCrmProfileId));
        Assert.Equal(guestsBefore, guests.OrderBy(g => g.Id).Select(GuestFingerprint).ToList());
        var contacts = await check.GuestContacts.AsNoTracking().Where(c => c.OrgId == world.OrgId).ToListAsync();
        Assert.All(contacts, contact => Assert.Equal(survivorId, contact.GuestCrmProfileId));
        Assert.Equal(contactsBefore, contacts.OrderBy(c => c.Id).Select(ContactFingerprint).ToList());

        // The proposal is merged once, by the one who won; the others follow the survivor, one for each of the three, none left on the tombstone.
        var proposals = await check.GuestMergeCandidates.AsNoTracking().Where(c => c.OrgId == world.OrgId).ToListAsync();
        var decided = Assert.Single(proposals, proposal => proposal.Status == GuestMergeStatus.Merged);
        Assert.Equal(world.DecidedId, decided.Id);
        Assert.Equal($"auth0|worker-{winner}", decided.DecidedByUserId);
        Assert.NotNull(decided.DecidedAt);
        var open = proposals.Where(proposal => proposal.Status == GuestMergeStatus.Proposed).ToList();
        Assert.Equal(proposals.Count - 1, open.Count);
        Assert.All(open, proposal =>
        {
            Assert.DoesNotContain(goneId, new[] { proposal.ProfileAId, proposal.ProfileBId });
            Assert.Contains(survivorId, new[] { proposal.ProfileAId, proposal.ProfileBId });
            Assert.True(proposal.ProfileAId.CompareTo(proposal.ProfileBId) < 0);
        });
        var partners = open.Select(proposal => proposal.ProfileAId == survivorId ? proposal.ProfileBId : proposal.ProfileAId).ToList();
        Assert.Equal(world.OtherProfileIds.Count, partners.Count);
        Assert.True(world.OtherProfileIds.ToHashSet().SetEquals(partners));
    }
}
