using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F01/US02/T02 (NF-89, CRM-02): the proofs of the rules of who may be written to. A forward address of an online travel agency is never
/// marketable whatever consent is recorded on it; a later booking cannot lower a consent and nothing re-enables a suppressed contact; an old yes/no
/// consent is not enough to write to someone. On EF InMemory, the real service over the real keyed index and the real suppression list.
/// </summary>
public class CrmContactProofTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// Privacy rule: a forward address is never reachable for marketing, whatever consent is recorded on it. The address arrives with an OTA
    /// booking and then, on a relay domain of the list, with a direct one; the guest ticks the marketing box on it and confirms it; the contact stays
    /// an OTA forward on service only, the consent is in the privacy history, and the rule says no even to a contact forged with a confirmed consent.
    /// </summary>
    [Fact]
    public async Task CrmContact_OtaForwardAddress_NeverMarketable_Test()
    {
        await using var db = CrmTestWorld.Open(CrmTestWorld.NewStore());
        var clock = CrmTestWorld.Clock();
        var options = new CrmOptions { OtaForwardDomains = ["relay.example.org"] };
        var contacts = CrmTestWorld.Contacts(db, clock, options);
        var orgId = Guid.NewGuid();
        var guest = CrmTestWorld.AddGuest(db, orgId, "Giulia", "Bianchi", "giulia@example.com");
        await db.SaveChangesAsync(Ct);

        // It arrives with a booking from a channel.
        var viaChannel = await contacts.UpsertContactAsync(
            new CrmContactInput(orgId, GuestContactChannel.Email, "Reservation-77@guest.example.com", GuestContactOrigin.Manual, GuestId: guest.Id, BookedVia: BookingSource.Airbnb),
            Ct);
        Assert.NotNull(viaChannel);
        Assert.Equal(GuestContactOrigin.OtaForward, viaChannel.Origin);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, viaChannel.LegalBasis);

        // The guest ticks the marketing box on it at the check-in, and confirms it: recorded, not marketable.
        var consented = await contacts.RecordConsentAsync(
            new CrmConsentInput(orgId, GuestContactChannel.Email, "reservation-77@guest.example.com", GuestContactOrigin.CheckIn, "consent-v1", GuestConsentSource.GuestPortal, GuestId: guest.Id),
            Ct);
        Assert.NotNull(consented);
        Assert.Equal(viaChannel.Id, consented.Id);
        var confirmed = await contacts.MarkConfirmedAsync(orgId, consented.Id, Ct);
        Assert.NotNull(confirmed);
        Assert.Equal(GuestContactConfirmation.Confirmed, confirmed.Confirmation);

        db.ChangeTracker.Clear();
        var stored = await db.GuestContacts.AsNoTracking().SingleAsync(c => c.Id == viaChannel.Id, Ct);
        Assert.Equal(GuestContactOrigin.OtaForward, stored.Origin);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, stored.LegalBasis);
        Assert.False(CrmContactRules.CanReceiveMarketing(stored));
        Assert.Equal(CrmContactRules.ReasonOtaForward, CrmContactRules.ReachabilityReason(stored));
        Assert.True(CrmContactRules.CanReceiveService(stored));
        // The privacy history is complete: the person did tick the box.
        Assert.Single(await db.GuestConsentRecords.AsNoTracking().Where(r => r.Purpose == GuestConsentPurpose.Marketing && r.Action == GuestConsentAction.Granted).ToListAsync(Ct));

        // The same on a relay domain of the list, with a booking of the public checkout.
        var onRelay = await contacts.UpsertContactAsync(
            new CrmContactInput(orgId, GuestContactChannel.Email, "abc123@relay.example.org", GuestContactOrigin.DirectBooking, GuestId: guest.Id, BookedVia: BookingSource.Direct, PurchaseConfirmed: true),
            Ct);
        Assert.NotNull(onRelay);
        Assert.Equal(GuestContactOrigin.OtaForward, onRelay.Origin);
        Assert.False(CrmContactRules.CanReceiveMarketing(onRelay));

        // The rule itself does not depend on what the service stored.
        var forged = new GuestContact
        {
            Channel = GuestContactChannel.Email,
            Origin = GuestContactOrigin.OtaForward,
            LegalBasis = GuestContactLegalBasis.Consent,
            Confirmation = GuestContactConfirmation.Confirmed,
        };
        Assert.False(CrmContactRules.CanReceiveMarketing(forged));
    }

    /// <summary>
    /// Privacy rule: a later booking cannot lower a consent, and nothing re-enables a suppressed contact. A confirmed consent is not touched by a
    /// direct booking that brings the same address on a weaker basis; the withdrawal suppresses it, puts the address on the suppression list and
    /// returns the basis to service only; a new consent, a new booking and a new confirmation afterwards change nothing about the suppression.
    /// </summary>
    [Fact]
    public async Task CrmContact_WeakerBasisNeverOverwritesStronger_SuppressionSticky_Test()
    {
        await using var db = CrmTestWorld.Open(CrmTestWorld.NewStore());
        var clock = CrmTestWorld.Clock();
        var contacts = CrmTestWorld.Contacts(db, clock);
        var orgId = Guid.NewGuid();
        var guest = CrmTestWorld.AddGuest(db, orgId, "Mario", "Rossi", "ospite@example.com");
        await db.SaveChangesAsync(Ct);

        var consent = await contacts.RecordConsentAsync(
            new CrmConsentInput(orgId, GuestContactChannel.Email, "ospite@example.com", GuestContactOrigin.CheckIn, "consent-v1", GuestConsentSource.GuestPortal, GuestId: guest.Id, IpAddress: "203.0.113.5"),
            Ct);
        Assert.NotNull(consent);
        await contacts.MarkConfirmedAsync(orgId, consent.Id, Ct);
        Assert.True(CrmContactRules.CanReceiveMarketing(consent));

        // A later booking brings the same address: the basis it gives (service only, no notice) is weaker and changes nothing.
        var later = await contacts.UpsertContactAsync(
            new CrmContactInput(orgId, GuestContactChannel.Email, "OSPITE@example.com", GuestContactOrigin.DirectBooking, GuestId: guest.Id, BookedVia: BookingSource.Direct, PurchaseConfirmed: true),
            Ct);
        Assert.NotNull(later);
        Assert.Equal(consent.Id, later.Id);
        Assert.Equal(GuestContactLegalBasis.Consent, later.LegalBasis);
        Assert.Equal(GuestContactConfirmation.Confirmed, later.Confirmation);
        Assert.Equal("consent-v1", later.BasisVersion);
        Assert.Single(await db.GuestContacts.AsNoTracking().ToListAsync(Ct));

        // The guest withdraws.
        var withdrawn = await contacts.WithdrawAsync(orgId, consent.Id, "unsubscribe", GuestConsentSource.GuestPortal, cancellationToken: Ct);
        Assert.NotNull(withdrawn);
        Assert.True(withdrawn.IsSuppressed);
        Assert.Equal("unsubscribe", withdrawn.SuppressedSource);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, withdrawn.LegalBasis);
        Assert.False(CrmContactRules.CanReceiveMarketing(withdrawn));
        Assert.Equal(CrmContactRules.ReasonSuppressed, CrmContactRules.ReachabilityReason(withdrawn));
        Assert.True(CrmContactRules.CanReceiveService(withdrawn));
        Assert.Single(await db.SuppressionListEntries.AsNoTracking().Where(e => e.OrgId == orgId).ToListAsync(Ct));
        var history = await db.GuestConsentRecords.AsNoTracking().Where(r => r.Action == GuestConsentAction.Withdrawn).ToListAsync(Ct);
        Assert.Equal("consent-v1", Assert.Single(history).Version);

        // Nothing brings it back: not a new consent, not a new booking, not a confirmation, not a second withdrawal.
        await contacts.RecordConsentAsync(
            new CrmConsentInput(orgId, GuestContactChannel.Email, "ospite@example.com", GuestContactOrigin.CheckIn, "consent-v2", GuestConsentSource.GuestPortal, GuestId: guest.Id),
            Ct);
        await contacts.UpsertContactAsync(
            new CrmContactInput(orgId, GuestContactChannel.Email, "ospite@example.com", GuestContactOrigin.DirectBooking, GuestId: guest.Id, BookedVia: BookingSource.Direct, PurchaseConfirmed: true),
            Ct);
        await contacts.MarkConfirmedAsync(orgId, consent.Id, Ct);
        await contacts.WithdrawAsync(orgId, consent.Id, "unsubscribe", GuestConsentSource.GuestPortal, cancellationToken: Ct);

        db.ChangeTracker.Clear();
        var stored = await db.GuestContacts.AsNoTracking().SingleAsync(c => c.Id == consent.Id, Ct);
        Assert.True(stored.IsSuppressed);
        Assert.False(CrmContactRules.CanReceiveMarketing(stored));
        Assert.Equal(CrmContactRules.ReasonSuppressed, CrmContactRules.ReachabilityReason(stored));
        Assert.Single(await db.GuestConsentRecords.AsNoTracking().Where(r => r.Action == GuestConsentAction.Withdrawn).ToListAsync(Ct));
        Assert.Single(await db.SuppressionListEntries.AsNoTracking().Where(e => e.OrgId == orgId).ToListAsync(Ct));
    }

    /// <summary>
    /// Privacy rule: an old yes/no consent is not enough to write to someone. The single flag of the old check-in and checkout, and the per-channel
    /// consent of the check-in whose address was not confirmed, become consent contacts that are not marketable ("not confirmed"); the one whose
    /// address the guest confirmed is. Reading them again changes nothing.
    /// </summary>
    [Fact]
    public async Task CrmContact_LegacyConsent_NotMarketableUntilConfirmed_Test()
    {
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        Guid legacy;
        Guid unconfirmed;
        Guid confirmed;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var flagOnly = CrmTestWorld.AddGuest(seed, orgId, "Anna", "Verdi", "anna.verdi@example.com");
            flagOnly.MarketingConsent = true;
            flagOnly.MarketingConsentDate = CrmTestWorld.Day(-40);
            CrmTestWorld.AddBooking(seed, flagOnly, propertyId, BookingStatus.CheckedOut, BookingSource.Direct, -40, 2, 200m);
            var notConfirmed = CrmTestWorld.AddGuest(seed, orgId, "Luca", "Neri", "luca.neri@example.com");
            var notConfirmedStay = CrmTestWorld.AddBooking(seed, notConfirmed, propertyId, BookingStatus.CheckedOut, BookingSource.Direct, -30, 2, 220m);
            var sure = CrmTestWorld.AddGuest(seed, orgId, "Carla", "Bruni", "carla.bruni@example.com");
            var sureStay = CrmTestWorld.AddBooking(seed, sure, propertyId, BookingStatus.CheckedOut, BookingSource.Direct, -20, 2, 240m);
            seed.MarketingConsentRecords.Add(ConsentOf(notConfirmedStay, notConfirmed, confirmed: false));
            seed.MarketingConsentRecords.Add(ConsentOf(sureStay, sure, confirmed: true));
            await seed.SaveChangesAsync(Ct);
            legacy = flagOnly.Id;
            unconfirmed = notConfirmed.Id;
            confirmed = sure.Id;
        }

        await using var db = CrmTestWorld.Open(store);
        var clock = CrmTestWorld.Clock();
        var matching = CrmTestWorld.Matching(db, clock);
        var contacts = CrmTestWorld.Contacts(db, clock);
        foreach (var guestId in new[] { legacy, unconfirmed, confirmed })
        {
            var guest = await db.Guests.SingleAsync(g => g.Id == guestId, Ct);
            await matching.FindOrCreateProfileAsync(guest, Ct);
            await contacts.SyncFromGuestAsync(guestId, Ct);
        }

        async Task<GuestContact> EmailOf(string address)
        {
            db.ChangeTracker.Clear();
            var hash = CrmTestIndex.Create().Hash(address);
            return await db.GuestContacts.AsNoTracking().SingleAsync(c => c.Channel == GuestContactChannel.Email && c.ValueHash == hash, Ct);
        }

        var oldFlag = await EmailOf("anna.verdi@example.com");
        Assert.Equal(GuestContactLegalBasis.Consent, oldFlag.LegalBasis);
        Assert.Equal(GuestContactConfirmation.Unconfirmed, oldFlag.Confirmation);
        Assert.False(CrmContactRules.CanReceiveMarketing(oldFlag));
        Assert.Equal(CrmContactRules.ReasonNotConfirmed, CrmContactRules.ReachabilityReason(oldFlag));

        var perChannel = await EmailOf("luca.neri@example.com");
        Assert.Equal(GuestContactLegalBasis.Consent, perChannel.LegalBasis);
        Assert.Equal(GuestContactConfirmation.Unconfirmed, perChannel.Confirmation);
        Assert.False(CrmContactRules.CanReceiveMarketing(perChannel));

        var doubleOptIn = await EmailOf("carla.bruni@example.com");
        Assert.Equal(GuestContactLegalBasis.Consent, doubleOptIn.LegalBasis);
        Assert.Equal(GuestContactConfirmation.Confirmed, doubleOptIn.Confirmation);
        Assert.True(CrmContactRules.CanReceiveMarketing(doubleOptIn));
        Assert.Null(CrmContactRules.ReachabilityReason(doubleOptIn));

        // Reading them again, a day later, changes nothing.
        var before = await db.GuestContacts.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.LegalBasis, c.Confirmation, c.ConfirmedAt, c.BasisVersion, c.BasisRecordedAt, c.UpdatedAt }).ToListAsync(Ct);
        clock.Advance(TimeSpan.FromDays(1));
        foreach (var guestId in new[] { legacy, unconfirmed, confirmed })
            await contacts.SyncFromGuestAsync(guestId, Ct);

        db.ChangeTracker.Clear();
        var after = await db.GuestContacts.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.LegalBasis, c.Confirmation, c.ConfirmedAt, c.BasisVersion, c.BasisRecordedAt, c.UpdatedAt }).ToListAsync(Ct);
        Assert.Equal(before, after);
    }

    private static MarketingConsentRecord ConsentOf(Booking booking, Guest guest, bool confirmed) => new()
    {
        OrgId = booking.OrgId,
        BookingId = booking.Id,
        GuestId = guest.Id,
        MarketingEmail = guest.Email,
        ConsentEmail = true,
        ConsentWhatsApp = false,
        EmailConfirmed = confirmed,
        ConsentTimestamp = booking.CreatedAt,
        ConsentTextVersion = "consent-v1",
    };
}
