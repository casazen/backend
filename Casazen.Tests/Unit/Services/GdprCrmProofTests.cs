using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F06/US02/T01 (NF-95, NF-20, art. 15, 17 and 20 GDPR): the rights of the guest cover what the CRM holds. The export has the per-channel
/// consents, the profile, the contacts and the messages and nothing of anybody else; the erasure deletes the person and keeps the opt-out as a row
/// with no personal data; a profile of several guests survives the erasure of one; the end of the marketing consent leaves the contacts reachable
/// for the stay and not suppressed. On EF InMemory, the real services over the real keyed index.
/// </summary>
public class GdprCrmProofTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly ICrmContactIndex Index = CrmTestIndex.Create();
    private static readonly DateTime ConsentAt = new(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime OptOutAt = new(2026, 10, 15, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Proof test (privacy rule, art. 15 and 20): the export of a guest is complete. It holds the per-channel marketing consent (channels, address,
    /// confirmation, version, time, IP), the CRM profile with its aggregates, tags, language and country, the contacts with origin, confirmation,
    /// basis, version and suppression, and the messages exchanged with the host; and nothing of the keyed hashes, of the proposals of merge or of
    /// another guest.
    /// </summary>
    [Fact]
    public async Task GdprExport_IncludesCrmProfileContactsConsentsAndMessages_Test()
    {
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        var world = await SeedMarioAndAnnaAsync(store, orgId);

        GuestDataExport export;
        await using (var db = CrmTestWorld.Open(store))
            export = await Service(db).ExportGuestDataAsync(orgId, world.MarioId, new HostScope(orgId), true, "auth0|host", Ct);

        // The per-channel consent of the check-in, with everything that proves it.
        var channel = Assert.Single(export.MarketingChannels);
        Assert.Equal(world.MarioBookingId, channel.BookingId);
        Assert.True(channel.ConsentEmail);
        Assert.True(channel.ConsentWhatsApp);
        Assert.True(channel.EmailConfirmed);
        Assert.Equal("mario.marketing@example.it", channel.MarketingEmail);
        Assert.Equal("marketing-v2", channel.ConsentTextVersion);
        Assert.Equal(ConsentAt, channel.ConsentTimestamp);
        Assert.Equal("203.0.113.7", channel.IpAddress);

        // The profile of the guest alone: language, country, tags and the aggregates of its stays.
        var profile = export.CrmProfile;
        Assert.NotNull(profile);
        Assert.Equal(world.MarioProfileId, profile.Id);
        Assert.Equal("it", profile.Language);
        Assert.Equal("Italia", profile.Country);
        Assert.Equal(new[] { "vip", "famiglia" }, profile.Tags);
        Assert.False(profile.SharedWithOtherGuests);
        Assert.Equal<int?>(2, profile.TotalStays);
        Assert.Equal<decimal?>(450m, profile.TotalSpend);

        // The contacts of the guest with origin, confirmation, basis, version and suppression: the three he gave and the lead on his profile (the
        // profile is his alone, so its contacts are his). Not Anna's.
        Assert.Equal(4, export.Contacts.Count);
        Assert.Single(export.Contacts, c => c.Value == "lead@example.com");
        var email = Assert.Single(export.Contacts, c => c.Value == "mario.rossi@example.com");
        Assert.Equal("mario.rossi@example.com", email.Value);
        Assert.Equal("Mario Rossi", email.DisplayName);
        Assert.Equal(GuestContactOrigin.CheckIn, email.Origin);
        Assert.Equal(GuestContactConfirmation.Confirmed, email.Confirmation);
        Assert.Equal(GuestContactLegalBasis.Consent, email.LegalBasis);
        Assert.Equal("marketing-v2", email.BasisVersion);
        Assert.Equal(ConsentAt, email.BasisRecordedAt);
        Assert.Equal(GuestConsentSource.GuestPortal, email.ConsentSource);
        var optedOut = Assert.Single(export.Contacts, c => c.IsSuppressed);
        Assert.Equal("mario.old@example.com", optedOut.Value);
        Assert.Equal(OptOutAt, optedOut.SuppressedAt);
        Assert.Equal("unsubscribe", optedOut.SuppressedSource);
        var phone = Assert.Single(export.Contacts, c => c.Channel == GuestContactChannel.Phone);
        Assert.Equal("+393331234567", phone.Value);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, phone.LegalBasis);
        Assert.DoesNotContain(export.Contacts, c => c.Value.Contains("anna", StringComparison.OrdinalIgnoreCase));

        // The messages of the conversation with the host, oldest first, on the booking they belong to; not Anna's.
        Assert.Equal(2, export.Messages.Count);
        Assert.Equal(
            new[] { GuestMessageSender.Guest, GuestMessageSender.Host },
            export.Messages.Select(m => m.Sender).ToArray());
        Assert.All(export.Messages, m => Assert.Equal(world.MarioBookingId, m.BookingId));
        Assert.Equal("Arriveremo verso le 18.", export.Messages[0].Text);
        Assert.Equal("Perfetto, vi aspettiamo.", export.Messages[1].Text);

        // Never the keyed hashes of the profile, nor a proposal of merge, nor the data of another guest.
        var json = JsonSerializer.Serialize(export);
        Assert.DoesNotContain(world.PhoneKeyHash, json, StringComparison.Ordinal);
        Assert.DoesNotContain(world.NameBirthKeyHash, json, StringComparison.Ordinal);
        Assert.DoesNotContain(world.CompositeKeyHash, json, StringComparison.Ordinal);
        Assert.DoesNotContain("anna.neri", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Siamo in due.", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proof test (privacy rule): a profile that other guests are linked to adds their stays to its aggregates, so the export of one of them leaves
    /// the aggregates out and says the profile is shared; the contacts of the profile that other guests gave are not in it.
    /// </summary>
    [Fact]
    public async Task GdprExport_ProfileSharedWithOtherGuests_LeavesOutTheirAggregatesAndContacts_Test()
    {
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        var world = await SeedSharedProfileAsync(store, orgId);

        GuestDataExport export;
        await using (var db = CrmTestWorld.Open(store))
            export = await Service(db).ExportGuestDataAsync(orgId, world.FirstGuestId, new HostScope(orgId), true, "auth0|host", Ct);

        var profile = export.CrmProfile;
        Assert.NotNull(profile);
        Assert.True(profile.SharedWithOtherGuests);
        Assert.Null(profile.TotalStays);
        Assert.Null(profile.TotalSpend);
        Assert.Null(profile.FirstStayAt);
        Assert.Null(profile.LastStayAt);

        // Only the two contacts the guest gave: not the contact of the other guest, not the lead of the profile.
        Assert.Equal(2, export.Contacts.Count);
        Assert.Contains(export.Contacts, c => c.Value == "mario.rossi@example.com");
        Assert.Contains(export.Contacts, c => c.Value == "mario.old@example.com");
        Assert.DoesNotContain(export.Contacts, c => c.Value is "luisa.verdi@example.com" or "lead@example.com");
    }

    /// <summary>
    /// Proof test (permission and scope rule): the messages of an export follow the rules of the message screens of the host. A guest with a stay at
    /// two properties: the owner's export holds both conversations; the export of a collaborator «Solo alcuni», who was given the first property
    /// only, holds the conversation of that stay and nothing of the other; a caller without <c>booking.read</c> gets none, and the rest of the
    /// export is the same. A scope of another org is refused, never widened.
    /// </summary>
    [Fact]
    public async Task GdprExport_Messages_OnlyOfThePropertiesTheCallerReaches_AndOnlyWithBookingRead_Test()
    {
        const string collaborator = "auth0|collaboratore";
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        Guid guestId, grantedBookingId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var granted = NewProperty(orgId);
            var hidden = NewProperty(orgId);
            seed.Properties.AddRange(granted, hidden);
            seed.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = orgId, UserId = collaborator, PropertyId = granted.Id });
            var mario = CrmTestWorld.AddGuest(seed, orgId, "Mario", "Rossi", "mario.rossi@example.com");
            var atGranted = CrmTestWorld.AddBooking(seed, mario, granted.Id, BookingStatus.CheckedOut, BookingSource.Direct, -20, 3, 300m);
            var atHidden = CrmTestWorld.AddBooking(seed, mario, hidden.Id, BookingStatus.CheckedOut, BookingSource.Direct, -60, 3, 300m);
            AddConversation(seed, orgId, granted.Id, atGranted.Id, "Arriveremo verso le 18.", "Perfetto, vi aspettiamo.");
            AddConversation(seed, orgId, hidden.Id, atHidden.Id, "Ci sono le zanzare?", "Abbiamo le zanzariere.");
            await seed.SaveChangesAsync(Ct);
            guestId = mario.Id;
            grantedBookingId = atGranted.Id;
        }

        GuestDataExport wholeOrg, limited, withoutBookingRead;
        await using (var db = CrmTestWorld.Open(store))
            wholeOrg = await Service(db).ExportGuestDataAsync(orgId, guestId, new HostScope(orgId), true, "auth0|titolare", Ct);
        await using (var db = CrmTestWorld.Open(store))
            limited = await Service(db).ExportGuestDataAsync(orgId, guestId, new HostScope(orgId, GrantedToUserId: collaborator), true, collaborator, Ct);
        await using (var db = CrmTestWorld.Open(store))
            withoutBookingRead = await Service(db).ExportGuestDataAsync(orgId, guestId, new HostScope(orgId), false, "auth0|contabile", Ct);

        Assert.Equal(4, wholeOrg.Messages.Count);

        // The collaborator reads the conversation of the property it was given, not the one of the other.
        Assert.Equal(2, limited.Messages.Count);
        Assert.All(limited.Messages, m => Assert.Equal(grantedBookingId, m.BookingId));
        Assert.DoesNotContain(limited.Messages, m => m.Text.Contains("zanzar", StringComparison.OrdinalIgnoreCase));

        // Without booking.read no message leaves; the rest of the export is as it was.
        Assert.Empty(withoutBookingRead.Messages);
        Assert.Equal(wholeOrg.Bookings.Count, withoutBookingRead.Bookings.Count);
        Assert.Equal(wholeOrg.Contacts.Count, withoutBookingRead.Contacts.Count);

        // A scope of another org is a mistake of the caller: refused, not read as a wider reach.
        await using var other = CrmTestWorld.Open(store);
        await Assert.ThrowsAsync<ArgumentException>(
            () => Service(other).ExportGuestDataAsync(orgId, guestId, new HostScope(Guid.NewGuid()), true, "auth0|altro", Ct));
    }

    /// <summary>
    /// Proof test (privacy rule): the erasure of a guest takes the person out of the CRM and keeps the opt-out. The contacts the guest gave and
    /// the other contacts of its profile are deleted, the profile and the proposals of merge that involve it go with it, and the guest is
    /// unlinked; the contact that carries a suppression stays as a row with no value, no name, no IP, no guest and no profile (only its keyed hash,
    /// the flag and its date), so the suppression list and the contact still say the same thing. Nobody else is touched, a second erasure changes
    /// nothing, and nothing the erasure writes to a log holds an address or a hash.
    /// </summary>
    [Fact]
    public async Task GdprErasure_RemovesContactsButKeepsTheOptOut_Test()
    {
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        var world = await SeedMarioAndAnnaAsync(store, orgId);
        var serviceLog = new CapturingLogger<GdprService>();
        var eraserLog = new CapturingLogger<GuestDataEraser>();

        await using (var db = CrmTestWorld.Open(store))
            await Service(db, serviceLog, eraserLog).EraseGuestDataAsync(orgId, world.MarioId, "Richiesta dell'ospite", "auth0|host", Ct);

        await using var verify = CrmTestWorld.Open(store);

        // Mario's contacts are gone but the opt-out; Anna's two are as they were.
        var contacts = await verify.GuestContacts.AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync(Ct);
        Assert.Equal(3, contacts.Count);
        Assert.Equal(2, contacts.Count(c => c.GuestId == world.AnnaId));
        var marker = Assert.Single(contacts, c => c.GuestId != world.AnnaId);
        Assert.Equal(Index.Hash("mario.old@example.com"), marker.ValueHash);
        Assert.Equal(GuestContactChannel.Email, marker.Channel);
        Assert.True(marker.IsSuppressed);
        Assert.Equal(OptOutAt, marker.SuppressedAt);
        Assert.Equal("unsubscribe", marker.SuppressedSource);
        Assert.Equal(string.Empty, marker.Value);
        Assert.Null(marker.DisplayName);
        Assert.Null(marker.ConsentIpAddress);
        Assert.Null(marker.GuestId);
        Assert.Null(marker.GuestCrmProfileId);
        Assert.Equal(string.Empty, marker.Language);
        Assert.Equal(GuestContactLegalBasis.None, marker.LegalBasis);
        Assert.Equal(string.Empty, marker.BasisVersion);
        Assert.Null(marker.BasisRecordedAt);
        Assert.Null(marker.ConsentSource);
        Assert.DoesNotContain(contacts, c => c.Value.Contains("mario", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(contacts, c => c.Value.Contains("lead", StringComparison.OrdinalIgnoreCase));

        // The entry of the suppression list is where it was: the opt-out says the same in both places.
        var suppression = new SuppressionService(verify, Index, NullLogger<SuppressionService>.Instance);
        Assert.True(await suppression.IsSuppressedAsync(orgId, "mario.old@example.com", Ct));
        Assert.True(await verify.GuestContacts.AnyAsync(c => c.OrgId == orgId && c.ValueHash == Index.Hash("mario.old@example.com") && c.IsSuppressed, Ct));

        // The profile of Mario is deleted with the proposal that involved it; Anna's profile and the proposal that does not involve him stay.
        var profiles = await verify.GuestCrmProfiles.AsNoTracking().Where(p => p.OrgId == orgId).ToListAsync(Ct);
        Assert.DoesNotContain(profiles, p => p.Id == world.MarioProfileId);
        Assert.Contains(profiles, p => p.Id == world.AnnaProfileId);
        var proposals = await verify.GuestMergeCandidates.AsNoTracking().Where(m => m.OrgId == orgId).ToListAsync(Ct);
        var left = Assert.Single(proposals);
        Assert.Equal(world.UnrelatedProposalId, left.Id);

        // The guest is unlinked and erased; the erasure is audited once.
        var mario = await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == world.MarioId, Ct);
        Assert.Null(mario.GuestCrmProfileId);
        Assert.True(mario.IsDeleted);
        Assert.Equal(1, await verify.GuestPrivacyAuditEntries.CountAsync(a => a.GuestId == world.MarioId && a.Action == GuestPrivacyAuditAction.Erased, Ct));

        // Anna is as she was.
        var anna = await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == world.AnnaId, Ct);
        Assert.Equal(world.AnnaProfileId, anna.GuestCrmProfileId);
        Assert.False(anna.IsDeleted);

        // A second erasure finds nothing left: no new audit entry, the same rows.
        await using (var again = CrmTestWorld.Open(store))
            await Service(again).EraseGuestDataAsync(orgId, world.MarioId, "Richiesta dell'ospite", "auth0|host", Ct);
        await using var last = CrmTestWorld.Open(store);
        Assert.Equal(1, await last.GuestPrivacyAuditEntries.CountAsync(a => a.GuestId == world.MarioId, Ct));
        Assert.Equal(3, await last.GuestContacts.CountAsync(c => c.OrgId == orgId, Ct));

        // The logs say what was done in numbers: no address, no hash, no name.
        var output = serviceLog.AllOutput + Environment.NewLine + eraserLog.AllOutput;
        Assert.Contains(world.MarioId.ToString(), output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mario", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Index.Hash("mario.old@example.com"), output, StringComparison.Ordinal);
        Assert.DoesNotContain(world.PhoneKeyHash, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proof test (data rule): the erasure of one guest never deletes the data of another. A profile that other guests are linked to is kept
    /// when one of them is erased: only the guest's link and contacts go (the contact that carries a suppression stays as a bare marker, off the
    /// profile), and the profile loses its keyed hashes, its tags and its country, which may be the erased guest's. The other guest, its contact,
    /// the contact of the profile nobody is named for and the proposal of merge that involves the profile are as they were.
    /// </summary>
    [Fact]
    public async Task GdprErasure_ProfileOfOtherGuests_Survives_Test()
    {
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        var world = await SeedSharedProfileAsync(store, orgId);

        await using (var db = CrmTestWorld.Open(store))
            await Service(db).EraseGuestDataAsync(orgId, world.FirstGuestId, "Richiesta dell'ospite", "auth0|host", Ct);

        await using var verify = CrmTestWorld.Open(store);

        // The profile is there, linked to the other guest, without what may be the erased guest's.
        var profile = await verify.GuestCrmProfiles.AsNoTracking().SingleAsync(p => p.Id == world.ProfileId, Ct);
        Assert.Null(profile.PhoneKeyHash);
        Assert.Null(profile.NameBirthKeyHash);
        Assert.Equal(string.Empty, profile.CompositeKeyHash);
        Assert.Equal("[]", profile.TagsJson);
        Assert.Equal(string.Empty, profile.Country);
        Assert.Null(profile.PrimaryGuestId);
        var second = await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == world.SecondGuestId, Ct);
        Assert.Equal(world.ProfileId, second.GuestCrmProfileId);
        var first = await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == world.FirstGuestId, Ct);
        Assert.Null(first.GuestCrmProfileId);

        // The erased guest's contact is gone, its opt-out is a bare marker off the profile; the other guest's contact and the lead stay.
        var contacts = await verify.GuestContacts.AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync(Ct);
        Assert.Equal(3, contacts.Count);
        Assert.DoesNotContain(contacts, c => c.Value == "mario.rossi@example.com");
        var marker = Assert.Single(contacts, c => c.ValueHash == Index.Hash("mario.old@example.com"));
        Assert.Equal(string.Empty, marker.Value);
        Assert.True(marker.IsSuppressed);
        Assert.Null(marker.GuestId);
        Assert.Null(marker.GuestCrmProfileId);
        var kept = Assert.Single(contacts, c => c.GuestId == world.SecondGuestId);
        Assert.Equal("luisa.verdi@example.com", kept.Value);
        Assert.Equal(world.ProfileId, kept.GuestCrmProfileId);
        Assert.Equal(GuestContactLegalBasis.Consent, kept.LegalBasis);
        var lead = Assert.Single(contacts, c => c.Value == "lead@example.com");
        Assert.Equal(world.ProfileId, lead.GuestCrmProfileId);

        // The proposal of merge that involves the surviving profile is still there.
        Assert.Equal(1, await verify.GuestMergeCandidates.CountAsync(m => m.OrgId == orgId && (m.ProfileAId == world.ProfileId || m.ProfileBId == world.ProfileId), Ct));
    }

    /// <summary>
    /// Proof test (data rule): the guests of one profile who are all erased in the same run (the retention erases a chunk of guests before one save)
    /// take the profile with them. Each is read against a database that still has the other linked, so the first leaves the profile and the last takes
    /// it, with its proposals and the contact of no guest; nothing is left with no guest, and the opt-out stays as a bare marker.
    /// </summary>
    [Fact]
    public async Task GdprErasure_GuestsOfOneProfileErasedInTheSameRun_TakeTheProfileWithThem_Test()
    {
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        var world = await SeedSharedProfileAsync(store, orgId);

        await using (var db = CrmTestWorld.Open(store))
        {
            var eraser = new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance);
            var first = await db.Guests.SingleAsync(g => g.Id == world.FirstGuestId, Ct);
            var second = await db.Guests.SingleAsync(g => g.Id == world.SecondGuestId, Ct);

            var afterFirst = await eraser.AnonymizeAsync(first, CrmTestWorld.Now.UtcDateTime, Ct);
            var afterSecond = await eraser.AnonymizeAsync(second, CrmTestWorld.Now.UtcDateTime, Ct);
            await db.SaveChangesAsync(Ct);

            Assert.False(afterFirst.CrmProfileRemoved);
            Assert.True(afterSecond.CrmProfileRemoved);
        }

        await using var verify = CrmTestWorld.Open(store);
        Assert.False(await verify.GuestCrmProfiles.AnyAsync(p => p.Id == world.ProfileId, Ct));
        Assert.Equal(0, await verify.GuestMergeCandidates.CountAsync(m => m.OrgId == orgId && (m.ProfileAId == world.ProfileId || m.ProfileBId == world.ProfileId), Ct));
        Assert.All(await verify.Guests.AsNoTracking().Where(g => g.OrgId == orgId).ToListAsync(Ct), g => Assert.Null(g.GuestCrmProfileId));

        // Their contacts and the lead are gone; the opt-out of the first guest is the one row left, bare.
        var marker = Assert.Single(await verify.GuestContacts.AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync(Ct));
        Assert.Equal(Index.Hash("mario.old@example.com"), marker.ValueHash);
        Assert.True(marker.IsSuppressed);
        Assert.Equal(string.Empty, marker.Value);
        Assert.Null(marker.GuestId);
        Assert.Null(marker.GuestCrmProfileId);
    }

    /// <summary>
    /// Proof test (privacy rule, AC-NF95-7): when the retention ends the marketing consent of a guest, its contacts on consent go back to service
    /// only, and are not suppressed (an expiry is not an opt-out: the guest can consent again). A soft opt-in and a suppression stay as they were,
    /// and a guest whose consent has not ended keeps its contacts.
    /// </summary>
    [Fact]
    public async Task GdprRetention_EndOfMarketingConsent_MovesConsentContactsToServiceOnlyWithoutSuppressing_Test()
    {
        var store = CrmTestWorld.NewStore();
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();
        Guid dueId, freshId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var due = CrmTestWorld.AddGuest(seed, orgId, "Mario", "Rossi", "mario.rossi@example.com");
            due.MarketingConsent = true;
            due.MarketingConsentDate = CrmTestWorld.Day(-400);
            var fresh = CrmTestWorld.AddGuest(seed, orgId, "Anna", "Neri", "anna.neri@example.com");
            fresh.MarketingConsent = true;
            fresh.MarketingConsentDate = CrmTestWorld.Day(-30);
            dueId = due.Id;
            freshId = fresh.Id;
            seed.GuestContacts.AddRange(
                Contact(orgId, due.Id, null, GuestContactChannel.Email, "mario.rossi@example.com", "Mario Rossi", GuestContactLegalBasis.Consent),
                Contact(orgId, due.Id, null, GuestContactChannel.Email, "mario.soft@example.com", "Mario Rossi", GuestContactLegalBasis.SoftOptIn),
                Contact(orgId, due.Id, null, GuestContactChannel.Phone, "+393331234567", "Mario Rossi", GuestContactLegalBasis.Consent, suppressed: true),
                Contact(orgId, fresh.Id, null, GuestContactChannel.Email, "anna.neri@example.com", "Anna Neri", GuestContactLegalBasis.Consent));
            await seed.SaveChangesAsync(Ct);
        }

        await using (var db = CrmTestWorld.Open(store))
        {
            var options = new GdprOptions
            {
                Retention = new GdprRetentionOptions { Marketing = new RetentionPeriodOptions { Years = 1, Source = "Decisione del titolare del 2026-11" } },
            };
            var retention = new GuestDataRetentionService(
                db,
                new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance),
                Options.Create(options),
                clock,
                NullLogger<GuestDataRetentionService>.Instance);

            var result = await retention.ApplyAsync(Ct);

            var marketing = Assert.Single(result.Categories, c => c.Category == GuestDataCategory.Marketing);
            Assert.Equal(1, marketing.Guests);
        }

        await using var verify = CrmTestWorld.Open(store);
        Assert.False((await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == dueId, Ct)).MarketingConsent);
        Assert.Equal(1, await verify.GuestConsentRecords.CountAsync(r => r.GuestId == dueId && r.Action == GuestConsentAction.Expired, Ct));

        var email = await verify.GuestContacts.AsNoTracking().SingleAsync(c => c.GuestId == dueId && c.Value == "mario.rossi@example.com", Ct);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, email.LegalBasis);
        Assert.False(email.IsSuppressed);
        Assert.Null(email.SuppressedAt);
        Assert.True(CrmContactRules.CanReceiveService(email));
        Assert.False(CrmContactRules.CanReceiveMarketing(email));

        var soft = await verify.GuestContacts.AsNoTracking().SingleAsync(c => c.GuestId == dueId && c.Value == "mario.soft@example.com", Ct);
        Assert.Equal(GuestContactLegalBasis.SoftOptIn, soft.LegalBasis);

        var suppressed = await verify.GuestContacts.AsNoTracking().SingleAsync(c => c.GuestId == dueId && c.Channel == GuestContactChannel.Phone, Ct);
        Assert.True(suppressed.IsSuppressed);
        Assert.Equal(OptOutAt, suppressed.SuppressedAt);

        var untouched = await verify.GuestContacts.AsNoTracking().SingleAsync(c => c.GuestId == freshId, Ct);
        Assert.Equal(GuestContactLegalBasis.Consent, untouched.LegalBasis);
    }

    /// <summary>
    /// Proof test (privacy rule, AC-NF95-7): the end of the marketing consent is not undone by the next sync of the guest. The per-channel consent of the
    /// check-in stays on record as proof, and the sync that follows every booking event reads it again: after the retention it leaves the contacts on
    /// service only, while a consent the guest gives afterwards counts as a new one.
    /// </summary>
    [Fact]
    public async Task GdprRetention_EndOfMarketingConsent_IsNotUndoneByTheNextSyncOfTheGuest_Test()
    {
        var store = CrmTestWorld.NewStore();
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();
        Guid guestId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var mario = CrmTestWorld.AddGuest(seed, orgId, "Mario", "Rossi", "mario.rossi@example.com");
            mario.MarketingConsent = true;
            mario.MarketingConsentDate = CrmTestWorld.Day(-400);
            seed.MarketingConsentRecords.Add(MarketingConsentOf(orgId, mario.Id, CrmTestWorld.Day(-400)));
            await seed.SaveChangesAsync(Ct);
            guestId = mario.Id;
        }

        async Task<GuestContact> MarketingAddressAsync()
        {
            await using var read = CrmTestWorld.Open(store);
            return await read.GuestContacts.AsNoTracking().SingleAsync(c => c.OrgId == orgId && c.ValueHash == Index.Hash("mario.marketing@example.it"), Ct);
        }

        async Task SyncAsync()
        {
            await using var db = CrmTestWorld.Open(store);
            await CrmTestWorld.Contacts(db, clock).SyncFromGuestAsync(guestId, Ct);
        }

        // The sync after the check-in puts the marketing address on consent, confirmed by the guest: marketable.
        await SyncAsync();
        Assert.True(CrmContactRules.CanReceiveMarketing(await MarketingAddressAsync()));

        // The retention ends the consent: the address goes back to service only.
        await using (var db = CrmTestWorld.Open(store))
        {
            var options = new GdprOptions
            {
                Retention = new GdprRetentionOptions { Marketing = new RetentionPeriodOptions { Years = 1, Source = "Decisione del titolare del 2026-11" } },
            };
            var retention = new GuestDataRetentionService(
                db,
                new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance),
                Options.Create(options),
                clock,
                NullLogger<GuestDataRetentionService>.Instance);
            await retention.ApplyAsync(Ct);
        }

        Assert.Equal(GuestContactLegalBasis.ServiceOnly, (await MarketingAddressAsync()).LegalBasis);

        // The next sync of the guest (any booking event) reads the same record again and leaves the address where the end put it.
        await SyncAsync();
        var afterSync = await MarketingAddressAsync();
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, afterSync.LegalBasis);
        Assert.False(CrmContactRules.CanReceiveMarketing(afterSync));
        Assert.False(afterSync.IsSuppressed);

        // A consent the guest gives afterwards is a new consent and counts.
        clock.Advance(TimeSpan.FromDays(1));
        await using (var seed = CrmTestWorld.Open(store))
        {
            seed.MarketingConsentRecords.Add(MarketingConsentOf(orgId, guestId, clock.GetUtcNow().UtcDateTime));
            await seed.SaveChangesAsync(Ct);
        }

        await SyncAsync();
        Assert.Equal(GuestContactLegalBasis.Consent, (await MarketingAddressAsync()).LegalBasis);
    }

    /// <summary>The per-channel consent the check-in records for an e-mail address typed for marketing, confirmed by the guest.</summary>
    private static MarketingConsentRecord MarketingConsentOf(Guid orgId, Guid guestId, DateTime at) => new()
    {
        OrgId = orgId,
        BookingId = Guid.NewGuid(),
        GuestId = guestId,
        MarketingEmail = "mario.marketing@example.it",
        ConsentEmail = true,
        ConsentWhatsApp = false,
        EmailConfirmed = true,
        ConsentTimestamp = at,
        ConsentTextVersion = "marketing-v2",
        IpAddress = "203.0.113.7",
    };

    // ---- the world of the proofs --------------------------------------------------------------------------------------------------

    /// <summary>The ids and the hashes of <see cref="SeedMarioAndAnnaAsync"/>.</summary>
    private sealed record MarioAndAnna(
        Guid MarioId,
        Guid AnnaId,
        Guid MarioProfileId,
        Guid AnnaProfileId,
        Guid MarioBookingId,
        Guid UnrelatedProposalId,
        string PhoneKeyHash,
        string NameBirthKeyHash,
        string CompositeKeyHash);

    /// <summary>The ids of <see cref="SeedSharedProfileAsync"/>.</summary>
    private sealed record SharedProfile(Guid FirstGuestId, Guid SecondGuestId, Guid ProfileId);

    /// <summary>
    /// Mario Rossi, with a profile of his own (two stays, tags, keyed hashes), three contacts he gave (an e-mail on consent, a phone number, an old
    /// address that carries an unsubscribe), a contact of no guest on his profile (a lead), the per-channel consent of his booking, and a
    /// conversation with the host; Anna Neri, with a profile, two contacts and a conversation of her own; one proposal of merge that involves Mario's
    /// profile and one that does not.
    /// </summary>
    private static async Task<MarioAndAnna> SeedMarioAndAnnaAsync(DbContextOptions<AppDbContext> store, Guid orgId)
    {
        await using var seed = CrmTestWorld.Open(store);
        var property = NewProperty(orgId);
        seed.Properties.Add(property);

        var mario = CrmTestWorld.AddGuest(seed, orgId, "Mario", "Rossi", "mario.rossi@example.com", "+393331234567");
        var anna = CrmTestWorld.AddGuest(seed, orgId, "Anna", "Neri", "anna.neri@example.com");
        var marioProfile = NewProfile(orgId, mario.Id, "+393331234567", "rossi mario|1980-01-01", "mario.rossi@example.com|rossi|mario");
        var annaProfile = NewProfile(orgId, anna.Id, "+393339999999", "neri anna|1985-05-05", "anna.neri@example.com|neri|anna");
        var otherProfile = NewProfile(orgId, null, "+393338888888", "bianchi luca|1990-02-02", "luca.bianchi@example.com|bianchi|luca");
        mario.GuestCrmProfileId = marioProfile.Id;
        anna.GuestCrmProfileId = annaProfile.Id;
        seed.GuestCrmProfiles.AddRange(marioProfile, annaProfile, otherProfile);

        var marioBooking = CrmTestWorld.AddBooking(seed, mario, property.Id, BookingStatus.CheckedOut, BookingSource.Direct, -20, 3, 300m);
        var annaBooking = CrmTestWorld.AddBooking(seed, anna, property.Id, BookingStatus.CheckedOut, BookingSource.Direct, -15, 2, 150m);
        seed.MarketingConsentRecords.Add(new MarketingConsentRecord
        {
            OrgId = orgId,
            BookingId = marioBooking.Id,
            GuestId = mario.Id,
            MarketingEmail = "mario.marketing@example.it",
            ConsentEmail = true,
            ConsentWhatsApp = true,
            EmailConfirmed = true,
            ConsentTimestamp = ConsentAt,
            ConsentTextVersion = "marketing-v2",
            IpAddress = "203.0.113.7",
        });

        seed.GuestContacts.AddRange(
            Contact(orgId, mario.Id, marioProfile.Id, GuestContactChannel.Email, "mario.rossi@example.com", "Mario Rossi", GuestContactLegalBasis.Consent),
            Contact(orgId, mario.Id, marioProfile.Id, GuestContactChannel.Phone, "+393331234567", "Mario Rossi", GuestContactLegalBasis.ServiceOnly),
            Contact(orgId, mario.Id, marioProfile.Id, GuestContactChannel.Email, "mario.old@example.com", "Mario Rossi", GuestContactLegalBasis.ServiceOnly, suppressed: true),
            Contact(orgId, null, marioProfile.Id, GuestContactChannel.Email, "lead@example.com", "Lead", GuestContactLegalBasis.ServiceOnly),
            Contact(orgId, anna.Id, annaProfile.Id, GuestContactChannel.Email, "anna.neri@example.com", "Anna Neri", GuestContactLegalBasis.Consent),
            Contact(orgId, anna.Id, annaProfile.Id, GuestContactChannel.Phone, "+393339999999", "Anna Neri", GuestContactLegalBasis.ServiceOnly));

        var withMario = NewProposal(orgId, marioProfile.Id, annaProfile.Id);
        var unrelated = NewProposal(orgId, annaProfile.Id, otherProfile.Id);
        seed.GuestMergeCandidates.AddRange(withMario, unrelated);

        AddConversation(seed, orgId, property.Id, marioBooking.Id, "Arriveremo verso le 18.", "Perfetto, vi aspettiamo.");
        AddConversation(seed, orgId, property.Id, annaBooking.Id, "Siamo in due.", "Benvenuti.");

        await seed.SaveChangesAsync(Ct);
        return new MarioAndAnna(
            mario.Id,
            anna.Id,
            marioProfile.Id,
            annaProfile.Id,
            marioBooking.Id,
            unrelated.Id,
            marioProfile.PhoneKeyHash!,
            marioProfile.NameBirthKeyHash!,
            marioProfile.CompositeKeyHash);
    }

    /// <summary>
    /// One profile that two guests are linked to (a merge of two records of a family), with tags, a country and keyed hashes: an e-mail contact on
    /// consent for each guest, an old address of the first guest that carries an unsubscribe, a lead on the profile that is nobody's, and a proposal
    /// of merge that involves the profile.
    /// </summary>
    private static async Task<SharedProfile> SeedSharedProfileAsync(DbContextOptions<AppDbContext> store, Guid orgId)
    {
        await using var seed = CrmTestWorld.Open(store);
        var first = CrmTestWorld.AddGuest(seed, orgId, "Mario", "Rossi", "mario.rossi@example.com", "+393331234567");
        var second = CrmTestWorld.AddGuest(seed, orgId, "Luisa", "Verdi", "luisa.verdi@example.com", "+393331234567");
        var profile = NewProfile(orgId, first.Id, "+393331234567", "rossi mario|1980-01-01", "mario.rossi@example.com|rossi|mario");
        var other = NewProfile(orgId, null, "+393338888888", "bianchi luca|1990-02-02", "luca.bianchi@example.com|bianchi|luca");
        first.GuestCrmProfileId = profile.Id;
        second.GuestCrmProfileId = profile.Id;
        seed.GuestCrmProfiles.AddRange(profile, other);

        seed.GuestContacts.AddRange(
            Contact(orgId, first.Id, profile.Id, GuestContactChannel.Email, "mario.rossi@example.com", "Mario Rossi", GuestContactLegalBasis.Consent),
            Contact(orgId, first.Id, profile.Id, GuestContactChannel.Email, "mario.old@example.com", "Mario Rossi", GuestContactLegalBasis.ServiceOnly, suppressed: true),
            Contact(orgId, second.Id, profile.Id, GuestContactChannel.Email, "luisa.verdi@example.com", "Luisa Verdi", GuestContactLegalBasis.Consent),
            Contact(orgId, null, profile.Id, GuestContactChannel.Email, "lead@example.com", "Lead", GuestContactLegalBasis.ServiceOnly));
        seed.GuestMergeCandidates.Add(NewProposal(orgId, profile.Id, other.Id));

        await seed.SaveChangesAsync(Ct);
        return new SharedProfile(first.Id, second.Id, profile.Id);
    }

    private static GdprService Service(
        AppDbContext db,
        CapturingLogger<GdprService>? serviceLog = null,
        CapturingLogger<GuestDataEraser>? eraserLog = null)
    {
        // No stay is open: the erasure is not refused (art. 17.3.b).
        var repository = new Mock<IGuestRepository>();
        repository
            .Setup(r => r.GetUsageAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestUsage(HasReferences: true, HasOpenBookings: false));

        return new GdprService(
            db,
            repository.Object,
            new GuestDataEraser(db, Mock.Of<IFileStorage>(), eraserLog ?? (ILogger<GuestDataEraser>)NullLogger<GuestDataEraser>.Instance),
            Options.Create(new GdprOptions()),
            CrmTestWorld.Clock(),
            serviceLog ?? (ILogger<GdprService>)NullLogger<GdprService>.Instance);
    }

    private static Property NewProperty(Guid orgId) => new()
    {
        OrgId = orgId,
        OwnerId = "auth0|owner",
        Name = "Casa del Lago",
        Address = "Via del Lago 1",
        City = "Como",
        MaxGuests = 4,
        Timezone = "Europe/Rome",
        Currency = "EUR",
    };

    private static GuestCrmProfile NewProfile(Guid orgId, Guid? primaryGuestId, string phone, string nameBirth, string composite) => new()
    {
        OrgId = orgId,
        CompositeKeyHash = Index.Hash(composite),
        Language = "it",
        Country = "Italia",
        TotalStays = 2,
        TotalSpend = 450m,
        TagsJson = """["vip","famiglia"]""",
        PropertyIdsJson = "[]",
        FirstStayAt = CrmTestWorld.Day(-40),
        LastStayAt = CrmTestWorld.Day(-10),
        PhoneKeyHash = Index.Hash(phone),
        NameBirthKeyHash = Index.Hash(nameBirth),
        PrimaryGuestId = primaryGuestId,
    };

    private static GuestContact Contact(
        Guid orgId,
        Guid? guestId,
        Guid? profileId,
        GuestContactChannel channel,
        string value,
        string name,
        GuestContactLegalBasis basis,
        bool suppressed = false) => new()
        {
            OrgId = orgId,
            GuestId = guestId,
            GuestCrmProfileId = profileId,
            Channel = channel,
            Value = value,
            ValueHash = Index.Hash(value),
            DisplayName = name,
            Language = "it",
            Origin = GuestContactOrigin.CheckIn,
            Confirmation = GuestContactConfirmation.Confirmed,
            ConfirmedAt = ConsentAt.AddMinutes(5),
            LegalBasis = basis,
            BasisVersion = "marketing-v2",
            BasisRecordedAt = ConsentAt,
            ConsentSource = GuestConsentSource.GuestPortal,
            ConsentIpAddress = "203.0.113.7",
            IsSuppressed = suppressed,
            SuppressedAt = suppressed ? OptOutAt : null,
            SuppressedSource = suppressed ? "unsubscribe" : null,
            CreatedAt = ConsentAt,
            UpdatedAt = ConsentAt,
        };

    /// <summary>A proposal of merge of two profiles, stored the way the database wants it: the smaller id first.</summary>
    private static GuestMergeCandidate NewProposal(Guid orgId, Guid profileId, Guid otherProfileId)
    {
        var first = profileId.CompareTo(otherProfileId) < 0 ? profileId : otherProfileId;
        var second = first == profileId ? otherProfileId : profileId;
        return new GuestMergeCandidate
        {
            OrgId = orgId,
            ProfileAId = first,
            ProfileBId = second,
            Score = 90,
            ReasonsJson = """["same_phone"]""",
        };
    }

    private static void AddConversation(AppDbContext db, Guid orgId, Guid propertyId, Guid bookingId, string guestText, string hostText)
    {
        var thread = new GuestThread
        {
            OrgId = orgId,
            PropertyId = propertyId,
            BookingId = bookingId,
            CreatedAt = ConsentAt,
            LastMessageAt = ConsentAt.AddHours(2),
        };
        db.GuestThreads.Add(thread);
        db.GuestMessages.AddRange(
            new GuestMessage { OrgId = orgId, ThreadId = thread.Id, Sender = GuestMessageSender.Guest, Text = guestText, CreatedAt = ConsentAt.AddHours(1) },
            new GuestMessage { OrgId = orgId, ThreadId = thread.Id, Sender = GuestMessageSender.Host, Text = hostText, CreatedAt = ConsentAt.AddHours(2), AuthorUserId = "auth0|host" });
    }
}
