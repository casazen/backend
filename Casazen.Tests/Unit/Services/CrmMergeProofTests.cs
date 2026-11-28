using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CrmMergeProofTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static GuestMergeCandidate Proposal(
        Guid orgId,
        Guid first,
        Guid second,
        int score,
        GuestMergeStatus status = GuestMergeStatus.Proposed)
    {
        var (a, b) = GuestMatchRules.OrderPair(first, second);
        return new GuestMergeCandidate
        {
            OrgId = orgId,
            ProfileAId = a,
            ProfileBId = b,
            Score = score,
            ReasonsJson = GuestMatchRules.ReasonsJson([GuestMatchRules.SamePhone]),
            Status = status,
            CreatedAt = CrmTestWorld.Day(-5),
        };
    }

    private static string Fingerprint(GuestContact contact) => string.Join(
        "|",
        contact.OrgId,
        contact.GuestId,
        contact.Channel,
        contact.Value,
        contact.ValueHash,
        contact.DisplayName,
        contact.Language,
        contact.Origin,
        contact.Confirmation,
        contact.ConfirmationSentAt?.ToString("O"),
        contact.ConfirmationSentCount,
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

    private static async Task<string> SnapshotAsync(AppDbContext db)
    {
        var guests = (await db.Guests.AsNoTracking().ToListAsync(Ct))
            .OrderBy(guest => guest.Id)
            .Select(guest => $"guest {guest.Id} > {guest.GuestCrmProfileId}");
        var contacts = (await db.GuestContacts.AsNoTracking().ToListAsync(Ct))
            .OrderBy(contact => contact.Id)
            .Select(contact => $"contact {contact.Id} > {contact.GuestCrmProfileId} | {Fingerprint(contact)}");
        var profiles = (await db.GuestCrmProfiles.AsNoTracking().ToListAsync(Ct))
            .OrderBy(profile => profile.Id)
            .Select(profile => $"profile {profile.Id} > {profile.MergedIntoProfileId} | {profile.TotalStays} | {profile.TotalSpend} | {profile.TagsJson} | {profile.UpdatedAt:O}");
        var proposals = (await db.GuestMergeCandidates.AsNoTracking().ToListAsync(Ct))
            .OrderBy(proposal => proposal.Id)
            .Select(proposal => $"proposal {proposal.Id} | {proposal.ProfileAId} | {proposal.ProfileBId} | {proposal.Status} | {proposal.DecidedAt:O} | {proposal.DecidedByUserId}");

        return string.Join('\n', guests.Concat(contacts).Concat(profiles).Concat(proposals));
    }

    /// <summary>
    /// Privacy rule: a merge decides who a person is and never what the person agreed to. The contacts of the profile that goes move to the one
    /// that stays exactly as they were (a confirmed consent, a service-only address, two suppressions: not one field but the link changes), none is
    /// copied, and the flags of consent of the profile are not lifted by the other's. The rest of the merge is as the brief says: the guests move, the
    /// tags are joined, the aggregates are computed again, the other profile is a tombstone that the lists skip, and the other proposals of the profile
    /// that goes follow the survivor or are dropped when they would repeat a pair.
    /// </summary>
    [Fact]
    public async Task CrmMerge_MovesContactsUnchanged_NeverGrantsConsent_Test()
    {
        var store = CrmTestWorld.NewStore();
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();
        Guid survivorId, goneId, goneGuestId, decidedId, movedId, repeatedId, earlierId, thirdId;
        List<GuestContact> before;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var house = CrmProfileApiWorld.AddHouse(seed, orgId, "Trullo");
            var survivorGuest = CrmTestWorld.AddGuest(seed, orgId, "Mario", "Rossi", "mario.rossi@example.com");
            var goneGuest = CrmTestWorld.AddGuest(seed, orgId, "M.", "Rossi", "m.rossi@example.org");
            CrmTestWorld.AddBooking(seed, survivorGuest, house.Id, BookingStatus.CheckedOut, BookingSource.Direct, -30, 2, 200m);
            CrmTestWorld.AddBooking(seed, goneGuest, house.Id, BookingStatus.CheckedOut, BookingSource.Airbnb, -90, 3, 300m);

            var survivor = CrmProfileApiWorld.NewProfile(orgId, survivorGuest.Id, stays: 1, spend: 200m);
            survivor.Country = string.Empty;
            survivor.TagsJson = "[\"vip\",\"fedele\"]";
            survivor.FirstStayAt = CrmTestWorld.Day(-30);
            var gone = CrmProfileApiWorld.NewProfile(orgId, goneGuest.Id, stays: 1, spend: 300m);
            gone.TagsJson = "[\"VIP\",\"nuovo\"]";
            gone.FirstStayAt = CrmTestWorld.Day(-90);
            gone.MarketingConsentEmail = true;
            gone.PhoneKeyHash = new string('a', GuestCrmProfile.KeyHashLength);
            survivorGuest.GuestCrmProfileId = survivor.Id;
            goneGuest.GuestCrmProfileId = gone.Id;
            seed.GuestCrmProfiles.AddRange(survivor, gone);

            var serviceOnly = CrmProfileApiWorld.NewContact(
                orgId, survivor.Id, GuestContactChannel.Email, "mario.rossi@example.com", GuestContactOrigin.DirectBooking, GuestContactLegalBasis.ServiceOnly, guestId: survivorGuest.Id);
            var suppressedPhone = CrmProfileApiWorld.NewContact(
                orgId, survivor.Id, GuestContactChannel.Phone, "+393331112223", GuestContactOrigin.Manual, GuestContactLegalBasis.ServiceOnly, suppressed: true);
            suppressedPhone.SuppressedAt = CrmTestWorld.Day(-10);
            suppressedPhone.SuppressedSource = "manual";
            var consent = CrmProfileApiWorld.NewContact(
                orgId, gone.Id, GuestContactChannel.Email, "m.rossi@example.org", GuestContactOrigin.CheckIn, GuestContactLegalBasis.Consent, GuestContactConfirmation.Confirmed, guestId: goneGuest.Id);
            consent.BasisVersion = "consent-v1";
            consent.BasisRecordedAt = CrmTestWorld.Day(-80);
            consent.ConfirmedAt = CrmTestWorld.Day(-79);
            consent.ConsentSource = GuestConsentSource.GuestPortal;
            consent.ConsentIpAddress = "203.0.113.5";
            var withdrawn = CrmProfileApiWorld.NewContact(
                orgId, gone.Id, GuestContactChannel.WhatsApp, "+393334445556", GuestContactOrigin.CheckIn, GuestContactLegalBasis.ServiceOnly, suppressed: true);
            withdrawn.SuppressedAt = CrmTestWorld.Day(-20);
            withdrawn.SuppressedSource = "unsubscribe";
            seed.GuestContacts.AddRange(serviceOnly, suppressedPhone, consent, withdrawn);

            var third = CrmProfileApiWorld.AddPerson(seed, orgId, "Luca", "Bianchi", house);
            var fourth = CrmProfileApiWorld.AddPerson(seed, orgId, "Elena", "Neri", house);
            var decided = Proposal(orgId, survivor.Id, gone.Id, 90);
            var moved = Proposal(orgId, gone.Id, third.Profile.Id, 80);
            var repeated = Proposal(orgId, gone.Id, fourth.Profile.Id, 60);
            var earlier = Proposal(orgId, survivor.Id, fourth.Profile.Id, 60, GuestMergeStatus.Dismissed);
            seed.GuestMergeCandidates.AddRange(decided, moved, repeated, earlier);
            await seed.SaveChangesAsync(Ct);

            survivorId = survivor.Id;
            goneId = gone.Id;
            goneGuestId = goneGuest.Id;
            decidedId = decided.Id;
            movedId = moved.Id;
            repeatedId = repeated.Id;
            earlierId = earlier.Id;
            thirdId = third.Profile.Id;
            before = await seed.GuestContacts.AsNoTracking().ToListAsync(Ct);
        }

        var scope = new HostScope(orgId);
        CrmProfileListItem item;
        await using (var db = CrmTestWorld.Open(store))
        {
            item = await CrmProfileApiWorld.MergeService(db, clock).MergeAsync(scope, decidedId, survivorId, "auth0|decisore", Ct);
        }

        await using var verify = CrmTestWorld.Open(store);

        var after = await verify.GuestContacts.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(before.Count, after.Count);
        Assert.All(after, contact => Assert.Equal(survivorId, contact.GuestCrmProfileId));
        foreach (var contact in before)
            Assert.Equal(Fingerprint(contact), Fingerprint(after.Single(row => row.Id == contact.Id)));

        Assert.True(CrmContactRules.CanReceiveMarketing(after.Single(contact => contact.Value == "m.rossi@example.org")));
        Assert.False(CrmContactRules.CanReceiveMarketing(after.Single(contact => contact.Value == "mario.rossi@example.com")));
        Assert.Equal(2, after.Count(contact => contact.IsSuppressed));
        Assert.All(after.Where(contact => contact.IsSuppressed), contact => Assert.False(CrmContactRules.CanReceiveMarketing(contact)));

        Assert.Equal(survivorId, (await verify.Guests.AsNoTracking().SingleAsync(guest => guest.Id == goneGuestId, Ct)).GuestCrmProfileId);
        var survivorAfter = await verify.GuestCrmProfiles.AsNoTracking().SingleAsync(profile => profile.Id == survivorId, Ct);
        var goneAfter = await verify.GuestCrmProfiles.AsNoTracking().SingleAsync(profile => profile.Id == goneId, Ct);
        Assert.Equal(survivorId, goneAfter.MergedIntoProfileId);
        Assert.Null(survivorAfter.MergedIntoProfileId);
        Assert.False(survivorAfter.MarketingConsentEmail);
        Assert.Equal("Italia", survivorAfter.Country);
        Assert.Equal(new string('a', GuestCrmProfile.KeyHashLength), survivorAfter.PhoneKeyHash);
        Assert.Equal("vip,fedele,nuovo", string.Join(',', CrmProfileRules.ParseTags(survivorAfter.TagsJson)));
        Assert.Equal(2, survivorAfter.TotalStays);
        Assert.Equal(500m, survivorAfter.TotalSpend);
        Assert.Equal(CrmTestWorld.Day(-90), survivorAfter.FirstStayAt);
        Assert.Equal(CrmTestWorld.Day(-30), survivorAfter.LastStayAt);

        var decidedAfter = await verify.GuestMergeCandidates.AsNoTracking().SingleAsync(proposal => proposal.Id == decidedId, Ct);
        Assert.Equal(GuestMergeStatus.Merged, decidedAfter.Status);
        Assert.Equal("auth0|decisore", decidedAfter.DecidedByUserId);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, decidedAfter.DecidedAt);
        var movedAfter = await verify.GuestMergeCandidates.AsNoTracking().SingleAsync(proposal => proposal.Id == movedId, Ct);
        Assert.Equal(GuestMergeStatus.Proposed, movedAfter.Status);
        Assert.Equal(GuestMatchRules.OrderPair(survivorId, thirdId), (movedAfter.ProfileAId, movedAfter.ProfileBId));
        Assert.False(await verify.GuestMergeCandidates.AnyAsync(proposal => proposal.Id == repeatedId, Ct));
        Assert.Equal(GuestMergeStatus.Dismissed, (await verify.GuestMergeCandidates.AsNoTracking().SingleAsync(proposal => proposal.Id == earlierId, Ct)).Status);

        Assert.Equal(survivorId, item.Id);
        Assert.Equal(2, item.TotalStays);
        Assert.True(item.EmailReachable);
        Assert.Equal(GuestContactLegalBasis.Consent, item.EmailBasis);
        var read = CrmProfileApiWorld.ReadService(verify, clock);
        Assert.DoesNotContain((await read.ListAsync(scope, new CrmProfileFilter(), 1, 50, Ct)).Items, listed => listed.Id == goneId);
        await Assert.ThrowsAsync<NotFoundException>(() => read.GetAsync(scope, goneId, Ct));
    }

    [Fact]
    public async Task CrmMerge_SamePairTwice_Is409AndChangesNothing_Test()
    {
        var store = CrmTestWorld.NewStore();
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();
        Guid mergedId, survivorId, goneId, dismissedId, keptId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var house = CrmProfileApiWorld.AddHouse(seed, orgId, "Trullo");
            seed.PropertyMemberAccesses.Add(new PropertyMemberAccess
            {
                OrgId = orgId,
                UserId = CrmProfileApiWorld.Collaborator,
                PropertyId = house.Id,
            });
            var a = CrmProfileApiWorld.AddPerson(seed, orgId, "Anna", "Rossi", house);
            var b = CrmProfileApiWorld.AddPerson(seed, orgId, "Anna", "Rossi-Verdi", house);
            var c = CrmProfileApiWorld.AddPerson(seed, orgId, "Carla", "Neri", house);
            var d = CrmProfileApiWorld.AddPerson(seed, orgId, "Dario", "Neri-Blu", house);
            seed.GuestContacts.Add(CrmProfileApiWorld.NewContact(
                orgId, b.Profile.Id, GuestContactChannel.Email, "anna.rossi-verdi@example.com", GuestContactOrigin.DirectBooking, GuestContactLegalBasis.ServiceOnly, guestId: b.Guest.Id));
            var toMerge = Proposal(orgId, a.Profile.Id, b.Profile.Id, 90);
            var toDismiss = Proposal(orgId, c.Profile.Id, d.Profile.Id, 80);
            seed.GuestMergeCandidates.AddRange(toMerge, toDismiss);
            await seed.SaveChangesAsync(Ct);

            mergedId = toMerge.Id;
            survivorId = a.Profile.Id;
            goneId = b.Profile.Id;
            dismissedId = toDismiss.Id;
            keptId = c.Profile.Id;
        }

        var restricted = new HostScope(orgId, GrantedToUserId: CrmProfileApiWorld.Collaborator);
        var orgWide = new HostScope(orgId);
        await using var db = CrmTestWorld.Open(store);
        var merge = CrmProfileApiWorld.MergeService(db, clock);

        var merged = await merge.MergeAsync(restricted, mergedId, survivorId, CrmProfileApiWorld.Collaborator, Ct);
        Assert.Equal(survivorId, merged.Id);
        await merge.DismissAsync(restricted, dismissedId, CrmProfileApiWorld.Collaborator, Ct);

        string settled;
        await using (var check = CrmTestWorld.Open(store))
        {
            settled = await SnapshotAsync(check);
            var dismissed = await check.GuestMergeCandidates.AsNoTracking().SingleAsync(proposal => proposal.Id == dismissedId, Ct);
            Assert.Equal(GuestMergeStatus.Dismissed, dismissed.Status);
            Assert.Equal(CrmProfileApiWorld.Collaborator, dismissed.DecidedByUserId);
            Assert.Equal(CrmTestWorld.Now.UtcDateTime, dismissed.DecidedAt);
            Assert.Null((await check.GuestCrmProfiles.AsNoTracking().SingleAsync(profile => profile.Id == keptId, Ct)).MergedIntoProfileId);
        }

        clock.Advance(TimeSpan.FromHours(1));
        var again = await Assert.ThrowsAsync<DomainConflictException>(
            () => merge.MergeAsync(restricted, mergedId, survivorId, CrmProfileApiWorld.Collaborator, Ct));
        Assert.Equal("crm_merge_not_proposed", again.Code);
        await Assert.ThrowsAsync<DomainConflictException>(() => merge.MergeAsync(orgWide, mergedId, goneId, "auth0|titolare", Ct));
        await Assert.ThrowsAsync<DomainConflictException>(() => merge.DismissAsync(orgWide, mergedId, "auth0|titolare", Ct));
        await Assert.ThrowsAsync<DomainConflictException>(() => merge.DismissAsync(restricted, dismissedId, CrmProfileApiWorld.Collaborator, Ct));
        await Assert.ThrowsAsync<DomainConflictException>(() => merge.MergeAsync(orgWide, dismissedId, keptId, "auth0|titolare", Ct));

        await using (var check = CrmTestWorld.Open(store))
        {
            Assert.Equal(settled, await SnapshotAsync(check));
        }
    }

    [Fact]
    public async Task CrmMerge_ProfileOutOfScope_Is404_Test()
    {
        var store = CrmTestWorld.NewStore();
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        Guid reachedId, hiddenPairId, foreignPairId, firstId, firstOfHiddenId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var granted = CrmProfileApiWorld.AddHouse(seed, orgId, "Trullo");
            var hidden = CrmProfileApiWorld.AddHouse(seed, orgId, "Casa Bianca");
            seed.PropertyMemberAccesses.Add(new PropertyMemberAccess
            {
                OrgId = orgId,
                UserId = CrmProfileApiWorld.Collaborator,
                PropertyId = granted.Id,
            });
            var a = CrmProfileApiWorld.AddPerson(seed, orgId, "Anna", "Rossi", granted);
            var b = CrmProfileApiWorld.AddPerson(seed, orgId, "Bruno", "Verdi", hidden);
            var c = CrmProfileApiWorld.AddPerson(seed, orgId, "Carla", "Neri", granted);
            var p = CrmProfileApiWorld.AddPerson(seed, otherOrgId, "Paola", "Gialli");
            var q = CrmProfileApiWorld.AddPerson(seed, otherOrgId, "Quirino", "Blu");
            seed.GuestContacts.Add(CrmProfileApiWorld.NewContact(
                orgId, a.Profile.Id, GuestContactChannel.Email, "anna.rossi@example.com", GuestContactOrigin.DirectBooking, GuestContactLegalBasis.ServiceOnly, guestId: a.Guest.Id));
            seed.GuestContacts.Add(CrmProfileApiWorld.NewContact(
                orgId, c.Profile.Id, GuestContactChannel.Phone, "+393331234567", GuestContactOrigin.Manual, GuestContactLegalBasis.ServiceOnly));
            var reached = Proposal(orgId, a.Profile.Id, c.Profile.Id, 80);
            var hiddenPair = Proposal(orgId, a.Profile.Id, b.Profile.Id, 90);
            var foreignPair = Proposal(otherOrgId, p.Profile.Id, q.Profile.Id, 90);
            seed.GuestMergeCandidates.AddRange(reached, hiddenPair, foreignPair);
            await seed.SaveChangesAsync(Ct);

            reachedId = reached.Id;
            hiddenPairId = hiddenPair.Id;
            foreignPairId = foreignPair.Id;
            firstId = a.Profile.Id;
            firstOfHiddenId = b.Profile.Id;
        }

        var restricted = new HostScope(orgId, GrantedToUserId: CrmProfileApiWorld.Collaborator);
        var orgWide = new HostScope(orgId);
        await using var db = CrmTestWorld.Open(store);
        var merge = CrmProfileApiWorld.MergeService(db, clock);

        var wholeOrg = await merge.ListProposedAsync(orgWide, 1, 50, Ct);
        Assert.Equal(2, wholeOrg.TotalCount);
        Assert.True(wholeOrg.Items.Select(shown => shown.Id).ToHashSet().SetEquals([reachedId, hiddenPairId]));
        var limited = await merge.ListProposedAsync(restricted, 1, 50, Ct);
        var view = Assert.Single(limited.Items);
        Assert.Equal(1, limited.TotalCount);
        Assert.Equal(reachedId, view.Id);
        Assert.Equal(80, view.Score);
        Assert.Equal(GuestMatchRules.SamePhone, Assert.Single(view.Reasons));
        Assert.Equal("Trullo", Assert.Single(view.ProfileA.PropertyNames.Concat(view.ProfileB.PropertyNames).Distinct()));
        var json = JsonSerializer.Serialize(limited);
        Assert.DoesNotContain("anna.rossi@example.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("+393331234567", json, StringComparison.Ordinal);
        Assert.Contains("a***@example.com", json, StringComparison.Ordinal);
        Assert.Contains("567", json, StringComparison.Ordinal);

        string untouched;
        await using (var check = CrmTestWorld.Open(store))
        {
            untouched = await SnapshotAsync(check);
        }

        var merging = await Assert.ThrowsAsync<NotFoundException>(
            () => merge.MergeAsync(restricted, hiddenPairId, firstId, CrmProfileApiWorld.Collaborator, Ct));
        Assert.Equal("crm_merge_candidate_not_found", merging.Code);
        Assert.Equal("CrmMergeCandidateNotFound", merging.MessageKey);
        await Assert.ThrowsAsync<NotFoundException>(() => merge.MergeAsync(restricted, hiddenPairId, firstOfHiddenId, CrmProfileApiWorld.Collaborator, Ct));
        var dismissing = await Assert.ThrowsAsync<NotFoundException>(
            () => merge.DismissAsync(restricted, hiddenPairId, CrmProfileApiWorld.Collaborator, Ct));
        Assert.Equal("crm_merge_candidate_not_found", dismissing.Code);
        await Assert.ThrowsAsync<NotFoundException>(() => merge.DismissAsync(orgWide, foreignPairId, "auth0|titolare", Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => merge.MergeAsync(orgWide, foreignPairId, firstId, "auth0|titolare", Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => merge.DismissAsync(restricted, Guid.NewGuid(), CrmProfileApiWorld.Collaborator, Ct));
        await using (var check = CrmTestWorld.Open(store))
        {
            Assert.Equal(untouched, await SnapshotAsync(check));
        }

        var invalid = await Assert.ThrowsAsync<DomainRuleException>(
            () => merge.MergeAsync(restricted, reachedId, firstOfHiddenId, CrmProfileApiWorld.Collaborator, Ct));
        Assert.Equal("crm_merge_survivor_invalid", invalid.Code);
        var decided = await merge.MergeAsync(restricted, reachedId, firstId, CrmProfileApiWorld.Collaborator, Ct);
        Assert.Equal(firstId, decided.Id);
    }

    /// <summary>
    /// Permission rule, fail closed: a merge moves every guest and every contact of the profile that goes, so a collaborator limited to some
    /// properties decides a proposal only when it reaches both profiles in full. A profile with a guest at a property the collaborator does not
    /// reach (a person who stayed at both houses), or with a contact given by such a guest, makes the proposal as good as missing for it: not
    /// listed, not counted as a proposal on the profile, and 404 (the answer for a profile out of scope) for the merge and for the dismissal, with
    /// nothing changed. The whole org decides it and the guest of the other house moves with the rest; a pair the collaborator reaches in full
    /// is decided as ever.
    /// </summary>
    [Fact]
    public async Task CrmMerge_ProfileReachedOnlyInPart_Is404ForALimitedCaller_Test()
    {
        var store = CrmTestWorld.NewStore();
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();
        Guid withMixedId, withContactId, plainId, annaId, mixedId, carlaId, ericaId, atHiddenId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var granted = CrmProfileApiWorld.AddHouse(seed, orgId, "Trullo");
            var hidden = CrmProfileApiWorld.AddHouse(seed, orgId, "Casa Bianca");
            seed.PropertyMemberAccesses.Add(new PropertyMemberAccess
            {
                OrgId = orgId,
                UserId = CrmProfileApiWorld.Collaborator,
                PropertyId = granted.Id,
            });

            var atGranted = CrmTestWorld.AddGuest(seed, orgId, "Anna", "Rossi", "anna.rossi@example.com");
            var atHidden = CrmTestWorld.AddGuest(seed, orgId, "Annamaria", "Segreta", "anna.segreta@example.com");
            CrmTestWorld.AddBooking(seed, atGranted, granted.Id, BookingStatus.CheckedOut, BookingSource.Direct, -30, 2, 200m);
            CrmTestWorld.AddBooking(seed, atHidden, hidden.Id, BookingStatus.CheckedOut, BookingSource.Airbnb, -60, 3, 300m);
            var mixed = CrmProfileApiWorld.NewProfile(orgId, atGranted.Id, stays: 2, spend: 500m);
            atGranted.GuestCrmProfileId = mixed.Id;
            atHidden.GuestCrmProfileId = mixed.Id;
            seed.GuestCrmProfiles.Add(mixed);

            var anna = CrmProfileApiWorld.AddPerson(seed, orgId, "Anna", "Verdi", granted);
            var carla = CrmProfileApiWorld.AddPerson(seed, orgId, "Carla", "Neri", granted);
            var dario = CrmProfileApiWorld.AddPerson(seed, orgId, "Dario", "Blu", granted);
            var erica = CrmProfileApiWorld.AddPerson(seed, orgId, "Erica", "Gialli", granted);
            var fabio = CrmProfileApiWorld.AddPerson(seed, orgId, "Fabio", "Viola", granted);
            var stranger = CrmTestWorld.AddGuest(seed, orgId, "Elio", "Altrove", "elio.altrove@example.com");
            CrmTestWorld.AddBooking(seed, stranger, hidden.Id, BookingStatus.CheckedOut, BookingSource.Direct, -50, 2, 100m);
            seed.GuestContacts.Add(CrmProfileApiWorld.NewContact(
                orgId, carla.Profile.Id, GuestContactChannel.Email, "elio.altrove@example.com", GuestContactOrigin.DirectBooking, GuestContactLegalBasis.ServiceOnly, guestId: stranger.Id));

            var withMixed = Proposal(orgId, anna.Profile.Id, mixed.Id, 80);
            var withContact = Proposal(orgId, carla.Profile.Id, dario.Profile.Id, 70);
            var plain = Proposal(orgId, erica.Profile.Id, fabio.Profile.Id, 60);
            seed.GuestMergeCandidates.AddRange(withMixed, withContact, plain);
            await seed.SaveChangesAsync(Ct);

            withMixedId = withMixed.Id;
            withContactId = withContact.Id;
            plainId = plain.Id;
            annaId = anna.Profile.Id;
            mixedId = mixed.Id;
            carlaId = carla.Profile.Id;
            ericaId = erica.Profile.Id;
            atHiddenId = atHidden.Id;
        }

        var restricted = new HostScope(orgId, GrantedToUserId: CrmProfileApiWorld.Collaborator);
        var orgWide = new HostScope(orgId);
        await using var db = CrmTestWorld.Open(store);
        var merge = CrmProfileApiWorld.MergeService(db, clock);
        var read = CrmProfileApiWorld.ReadService(db, clock);

        Assert.Equal(3, (await merge.ListProposedAsync(orgWide, 1, 50, Ct)).TotalCount);
        var limited = await merge.ListProposedAsync(restricted, 1, 50, Ct);
        Assert.Equal(plainId, Assert.Single(limited.Items).Id);
        var seen = (await read.ListAsync(restricted, new CrmProfileFilter(), 1, 50, Ct)).Items;
        Assert.False(seen.Single(item => item.Id == annaId).HasMergeProposal);
        Assert.False(seen.Single(item => item.Id == mixedId).HasMergeProposal);
        Assert.True(seen.Single(item => item.Id == ericaId).HasMergeProposal);
        Assert.True((await read.ListAsync(orgWide, new CrmProfileFilter(), 1, 50, Ct)).Items.Single(item => item.Id == annaId).HasMergeProposal);

        string untouched;
        await using (var check = CrmTestWorld.Open(store))
        {
            untouched = await SnapshotAsync(check);
        }

        foreach (var (candidateId, survivorId) in new[] { (withMixedId, annaId), (withMixedId, mixedId), (withContactId, carlaId) })
        {
            var merging = await Assert.ThrowsAsync<NotFoundException>(
                () => merge.MergeAsync(restricted, candidateId, survivorId, CrmProfileApiWorld.Collaborator, Ct));
            Assert.Equal("crm_merge_candidate_not_found", merging.Code);
        }

        foreach (var candidateId in new[] { withMixedId, withContactId })
        {
            var dismissing = await Assert.ThrowsAsync<NotFoundException>(
                () => merge.DismissAsync(restricted, candidateId, CrmProfileApiWorld.Collaborator, Ct));
            Assert.Equal("crm_merge_candidate_not_found", dismissing.Code);
        }

        await using (var check = CrmTestWorld.Open(store))
        {
            Assert.Equal(untouched, await SnapshotAsync(check));
        }

        var decided = await merge.MergeAsync(restricted, plainId, ericaId, CrmProfileApiWorld.Collaborator, Ct);
        Assert.Equal(ericaId, decided.Id);
        var everything = await merge.MergeAsync(orgWide, withMixedId, annaId, "auth0|titolare", Ct);
        Assert.Equal(annaId, everything.Id);
        await using var verify = CrmTestWorld.Open(store);
        Assert.Equal(annaId, (await verify.Guests.AsNoTracking().SingleAsync(guest => guest.Id == atHiddenId, Ct)).GuestCrmProfileId);
    }
}
