using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
/// E07/F06/US01/T02 (NF-95, AC-NF95-6): the withdrawal of a marketing consent that the host records on the guest's request also puts the
/// addresses of the guest in the suppression list, in the same unit of work.
/// </summary>
public class GdprMarketingWithdrawalSuppressionTests
{
    private static readonly ICrmContactIndex Index = CrmTestIndex.Create();

    private static DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"gdpr-withdrawal-suppression-{Guid.NewGuid()}").Options;

    private static GdprService NewService(
        AppDbContext db,
        ISuppressionService? suppression,
        ILogger<GdprService>? logger = null,
        ICrmContactService? crmContacts = null) =>
        new(
            db,
            Mock.Of<IGuestRepository>(),
            new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance),
            Options.Create(new GdprOptions()),
            TimeProvider.System,
            logger ?? NullLogger<GdprService>.Instance,
            suppression,
            crmContacts);

    /// <summary>A contact the guest gave on a channel of the CRM (E07/F04/US01): a consent, by default.</summary>
    private static GuestContact ContactOf(
        Guid orgId,
        Guid guestId,
        GuestContactChannel channel,
        string value,
        GuestContactLegalBasis basis = GuestContactLegalBasis.Consent) => new()
        {
            OrgId = orgId,
            GuestId = guestId,
            Channel = channel,
            Value = value,
            ValueHash = Index.Hash(value),
            Origin = GuestContactOrigin.GuestPage,
            LegalBasis = basis,
            BasisVersion = "consent-v1",
            BasisRecordedAt = DateTime.UtcNow,
            ConsentSource = GuestConsentSource.GuestPage,
        };

    private static MarketingConsentRecord Consent(Guid orgId, Guid guestId, string marketingEmail) => new()
    {
        OrgId = orgId,
        BookingId = Guid.NewGuid(),
        GuestId = guestId,
        MarketingEmail = marketingEmail,
        ConsentEmail = true,
        EmailConfirmed = true,
        ConsentTextVersion = "marketing-v2",
    };

    /// <summary>
    /// Proof test (privacy rule, AC-NF95-6): a revocation recorded by the host on the guest's request blocks the marketing e-mail address. The
    /// withdrawal puts the e-mail of the guest and the marketing e-mail of its consent records (once each, however they are spelled) in the
    /// suppression list with the keyed hash and the source <c>manual</c>, flags the contact with the same hash, and touches nobody else.
    /// </summary>
    [Fact]
    public async Task GdprWithdrawalByHost_EntersSuppression_Test()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Mario", LastName = "Rossi", Email = "Mario.Rossi@Example.com", MarketingConsent = true };
        var other = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Neri", Email = "anna.neri@example.com", MarketingConsent = true };
        await using (var seed = new AppDbContext(options))
        {
            seed.Guests.AddRange(guest, other);
            seed.MarketingConsentRecords.AddRange(
                Consent(orgId, guest.Id, "mario.marketing@example.it"),
                Consent(orgId, guest.Id, " MARIO.Marketing@Example.it"),
                Consent(orgId, other.Id, "anna.marketing@example.it"));
            seed.GuestContacts.Add(new GuestContact
            {
                OrgId = orgId,
                Channel = GuestContactChannel.Email,
                Value = "mario.rossi@example.com",
                ValueHash = Index.Hash("mario.rossi@example.com"),
                Origin = GuestContactOrigin.CheckIn,
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var suppression = new SuppressionService(db, Index, NullLogger<SuppressionService>.Instance);
            await NewService(db, suppression).UpdateMarketingConsentAsync(orgId, guest.Id, false, "Email dell'ospite del 02/11", "auth0|host");
        }

        await using var verify = new AppDbContext(options);
        var suppressionAfter = new SuppressionService(verify, Index, NullLogger<SuppressionService>.Instance);

        // The withdrawal itself is recorded...
        Assert.False((await verify.Guests.SingleAsync(g => g.Id == guest.Id)).MarketingConsent);
        Assert.Equal(1, await verify.GuestConsentRecords.CountAsync(r => r.GuestId == guest.Id && r.Action == GuestConsentAction.Withdrawn));

        // ...and so is the suppression of the two addresses of the guest, nothing else, with the keyed hash (never the plain SHA-256).
        var entries = await verify.SuppressionListEntries.Where(e => e.OrgId == orgId).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal("manual", entry.Source));
        Assert.Contains(entries, entry => entry.EmailHash == Index.Hash("mario.rossi@example.com"));
        Assert.Contains(entries, entry => entry.EmailHash == Index.Hash("mario.marketing@example.it"));
        Assert.True(await suppressionAfter.IsSuppressedAsync(orgId, "MARIO.ROSSI@example.com"));
        Assert.True(await suppressionAfter.IsSuppressedAsync(orgId, "mario.marketing@example.it"));
        Assert.False(await suppressionAfter.IsSuppressedAsync(orgId, "anna.neri@example.com"));
        Assert.False(await suppressionAfter.IsSuppressedAsync(orgId, "anna.marketing@example.it"));

        var contact = await verify.GuestContacts.SingleAsync();
        Assert.True(contact.IsSuppressed);
        Assert.Equal("manual", contact.SuppressedSource);
        Assert.True((await verify.Guests.SingleAsync(g => g.Id == other.Id)).MarketingConsent);
    }

    /// <summary>
    /// Proof test (legal record, a regression on an existing flow): the withdrawal the host records never depends on the key of the index. On an
    /// environment that starts without <c>Crm:ContactIndexKey</c> (<c>Features:CrmMarketing</c> off, as production is today) the call ends without
    /// an error (the endpoint answers 204, as before this task), the consent record, the audit entry and the flag are saved, nothing enters the
    /// suppression list or flags a contact, and an error in the log names the withdrawal, the guest and the variable, and nothing that identifies
    /// the guest. The campaigns still skip the guest: their recipients are read from the flag the withdrawal has cleared.
    /// </summary>
    [Fact]
    public async Task GdprWithdrawalByHost_WithoutTheKey_IsRecordedAndTheSuppressionIsSkipped_Test()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Mario", LastName = "Rossi", Email = "mario.rossi@example.com", MarketingConsent = true };
        await using (var seed = new AppDbContext(options))
        {
            seed.Guests.Add(guest);
            seed.MarketingConsentRecords.Add(Consent(orgId, guest.Id, "mario.marketing@example.it"));
            seed.GuestContacts.Add(new GuestContact
            {
                OrgId = orgId,
                Channel = GuestContactChannel.Email,
                Value = "mario.rossi@example.com",
                ValueHash = Index.Hash("mario.rossi@example.com"),
                Origin = GuestContactOrigin.CheckIn,
            });
            await seed.SaveChangesAsync();
        }

        var log = new CapturingLogger<GdprService>();
        await using (var db = new AppDbContext(options))
        {
            var keyless = new SuppressionService(db, CrmTestIndex.Create(key: null), NullLogger<SuppressionService>.Instance);
            await NewService(db, keyless, log).UpdateMarketingConsentAsync(orgId, guest.Id, false, "Email dell'ospite del 02/11", "auth0|host");
        }

        await using var verify = new AppDbContext(options);
        Assert.False((await verify.Guests.SingleAsync(g => g.Id == guest.Id)).MarketingConsent);
        var withdrawal = await verify.GuestConsentRecords.SingleAsync(r => r.GuestId == guest.Id);
        Assert.Equal(
            (GuestConsentPurpose.Marketing, GuestConsentAction.Withdrawn, GuestConsentSource.HostOnGuestRequest, "auth0|host"),
            (withdrawal.Purpose, withdrawal.Action, withdrawal.Source, withdrawal.RecordedByUserId));
        Assert.Equal(GuestPrivacyAuditAction.MarketingConsentWithdrawn, (await verify.GuestPrivacyAuditEntries.SingleAsync(a => a.GuestId == guest.Id)).Action);

        // The suppression is skipped, nothing of it is half done.
        Assert.False(await verify.SuppressionListEntries.AnyAsync());
        Assert.False((await verify.GuestContacts.SingleAsync()).IsSuppressed);

        // The error says what to fix and which withdrawal it is about, with no personal data.
        var error = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("Crm__ContactIndexKey", error.Message);
        Assert.Equal(withdrawal.Id, Assert.IsType<Guid>(error.Values["ConsentRecordId"]));
        Assert.Equal(guest.Id, Assert.IsType<Guid>(error.Values["GuestId"]));
        Assert.DoesNotContain("example", log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mario", log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rossi", log.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What a withdrawal does not do: a consent that is not in force changes nothing, as the interface says, and the service refuses to withdraw
    /// without the suppression service (a wiring error that cannot happen in the running application: the container always supplies it).
    /// </summary>
    [Fact]
    public async Task GdprWithdrawalByHost_WithNothingToWithdrawOrNoSuppressionService_RecordsNothing()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Mario", LastName = "Rossi", Email = "mario.rossi@example.com", MarketingConsent = true };
        var notInForce = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Neri", Email = "anna.neri@example.com", MarketingConsent = false };
        await using (var seed = new AppDbContext(options))
        {
            seed.Guests.AddRange(guest, notInForce);
            await seed.SaveChangesAsync();
        }

        // One context per call, as one request has its own scope: a context that failed is thrown away with what it had tracked.
        await using (var db = new AppDbContext(options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => NewService(db, suppression: null).UpdateMarketingConsentAsync(orgId, guest.Id, false, "Richiesta via email", "auth0|host"));
        }

        await using (var db = new AppDbContext(options))
        {
            var working = new SuppressionService(db, Index, NullLogger<SuppressionService>.Instance);
            await NewService(db, working).UpdateMarketingConsentAsync(orgId, notInForce.Id, false, "Richiesta via email", "auth0|host");
        }

        await using var verify = new AppDbContext(options);
        Assert.True((await verify.Guests.SingleAsync(g => g.Id == guest.Id)).MarketingConsent);
        Assert.False(await verify.GuestConsentRecords.AnyAsync());
        Assert.False(await verify.SuppressionListEntries.AnyAsync());
    }

    /// <summary>
    /// Proof test (privacy rule, E07/F04/US01, art. 7.3 GDPR): the withdrawal the host records on the guest's request ends the consent the flag
    /// does not say. A guest who consented on WhatsApp alone has the flag unset, and the call used to answer «nothing to withdraw»: now the
    /// contact of the guest is suppressed (source <c>manual</c>, back to service only, no longer marketable), the privacy history has the
    /// withdrawal with the actor and the documented request, the audit entry is written, the flag stays unset, and the contact of another guest
    /// and a contact that was no consent are not touched.
    /// </summary>
    [Fact]
    public async Task GdprWithdrawalByHost_EndsTheWhatsAppConsentThatTheFlagDoesNotSay_Test()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Mario", LastName = "Rossi", Email = "mario.rossi@example.com", MarketingConsent = false };
        var other = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Neri", Email = "anna.neri@example.com", MarketingConsent = false };
        await using (var seed = new AppDbContext(options))
        {
            seed.Guests.AddRange(guest, other);
            seed.GuestContacts.AddRange(
                ContactOf(orgId, guest.Id, GuestContactChannel.WhatsApp, "+393331112222"),
                ContactOf(orgId, guest.Id, GuestContactChannel.Phone, "+393331113333", GuestContactLegalBasis.ServiceOnly),
                ContactOf(orgId, other.Id, GuestContactChannel.WhatsApp, "+393339998888"));
            await seed.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var suppression = new SuppressionService(db, Index, NullLogger<SuppressionService>.Instance);
            await NewService(db, suppression, crmContacts: CrmTestWorld.Contacts(db, TimeProvider.System))
                .UpdateMarketingConsentAsync(orgId, guest.Id, false, "Richiesta dell'ospite per telefono del 03/11", "auth0|host");
        }

        await using var verify = new AppDbContext(options);
        var whatsApp = await verify.GuestContacts.SingleAsync(c => c.GuestId == guest.Id && c.Channel == GuestContactChannel.WhatsApp);
        Assert.True(whatsApp.IsSuppressed);
        Assert.Equal("manual", whatsApp.SuppressedSource);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, whatsApp.LegalBasis);
        Assert.False(CrmContactRules.CanReceiveMarketing(whatsApp));

        // The record: the history has the withdrawal, who recorded it and the request; the audit entry; the flag was not set and still is not.
        var row = await verify.GuestConsentRecords.SingleAsync(r => r.GuestId == guest.Id);
        Assert.Equal(
            (GuestConsentPurpose.Marketing, GuestConsentAction.Withdrawn, GuestConsentSource.HostOnGuestRequest, "auth0|host", "Richiesta dell'ospite per telefono del 03/11"),
            (row.Purpose, row.Action, row.Source, row.RecordedByUserId, row.Note));
        Assert.Equal(GuestPrivacyAuditAction.MarketingConsentWithdrawn, (await verify.GuestPrivacyAuditEntries.SingleAsync(a => a.GuestId == guest.Id)).Action);
        Assert.False((await verify.Guests.SingleAsync(g => g.Id == guest.Id)).MarketingConsent);

        // Nobody else: the contact that was no consent, and the consent of another guest.
        Assert.False((await verify.GuestContacts.SingleAsync(c => c.GuestId == guest.Id && c.Channel == GuestContactChannel.Phone)).IsSuppressed);
        var theirs = await verify.GuestContacts.SingleAsync(c => c.GuestId == other.Id);
        Assert.False(theirs.IsSuppressed);
        Assert.True(CrmContactRules.CanReceiveMarketing(theirs));
        Assert.False(await verify.GuestConsentRecords.AnyAsync(r => r.GuestId == other.Id));
    }

    /// <summary>
    /// Proof test (privacy rule): with the flag set, the withdrawal also ends the contacts of the guest that the addresses it knows do not
    /// reach: an e-mail typed on the guest page is not in the consent records of the check-in, and its address now enters the suppression list
    /// with the others; the WhatsApp contact is suppressed; the flag is cleared and the withdrawal is recorded once as before.
    /// </summary>
    [Fact]
    public async Task GdprWithdrawalByHost_WithTheFlag_AlsoEndsTheContactsTheAddressesDoNotReach_Test()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Mario", LastName = "Rossi", Email = "mario.rossi@example.com", MarketingConsent = true };
        await using (var seed = new AppDbContext(options))
        {
            seed.Guests.Add(guest);
            seed.GuestContacts.AddRange(
                ContactOf(orgId, guest.Id, GuestContactChannel.Email, "mario.digitato@example.it"),
                ContactOf(orgId, guest.Id, GuestContactChannel.WhatsApp, "+393331112222"));
            await seed.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var suppression = new SuppressionService(db, Index, NullLogger<SuppressionService>.Instance);
            await NewService(db, suppression, crmContacts: CrmTestWorld.Contacts(db, TimeProvider.System))
                .UpdateMarketingConsentAsync(orgId, guest.Id, false, "Email dell'ospite del 03/11", "auth0|host");
        }

        await using var verify = new AppDbContext(options);
        Assert.False((await verify.Guests.SingleAsync(g => g.Id == guest.Id)).MarketingConsent);
        Assert.All(await verify.GuestContacts.ToListAsync(), contact => Assert.True(contact.IsSuppressed));
        var entries = await verify.SuppressionListEntries.Where(e => e.OrgId == orgId).ToListAsync();
        Assert.Contains(entries, entry => entry.EmailHash == Index.Hash("mario.digitato@example.it"));
        Assert.Contains(entries, entry => entry.EmailHash == Index.Hash("mario.rossi@example.com"));
        Assert.Contains(
            await verify.GuestConsentRecords.Where(r => r.GuestId == guest.Id).ToListAsync(),
            record => record is { Action: GuestConsentAction.Withdrawn, Source: GuestConsentSource.HostOnGuestRequest, RecordedByUserId: "auth0|host" });
    }

    /// <summary>
    /// Proof test (the withdrawal never depends on the key): with the flag set and no key of the index the withdrawal is recorded as before, the
    /// contacts of the guest are left as they are, and the log has an error for the contacts besides the one for the addresses, with ids only.
    /// </summary>
    [Fact]
    public async Task GdprWithdrawalByHost_WithoutTheKey_LeavesTheContactsAndSaysSo_Test()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Mario", LastName = "Rossi", Email = "mario.rossi@example.com", MarketingConsent = true };
        await using (var seed = new AppDbContext(options))
        {
            seed.Guests.Add(guest);
            seed.GuestContacts.Add(ContactOf(orgId, guest.Id, GuestContactChannel.WhatsApp, "+393331112222"));
            await seed.SaveChangesAsync();
        }

        var log = new CapturingLogger<GdprService>();
        await using (var db = new AppDbContext(options))
        {
            var keyless = new SuppressionService(db, CrmTestIndex.Create(key: null), NullLogger<SuppressionService>.Instance);
            await NewService(db, keyless, log, CrmTestWorld.Contacts(db, TimeProvider.System))
                .UpdateMarketingConsentAsync(orgId, guest.Id, false, "Email dell'ospite del 03/11", "auth0|host");
        }

        await using var verify = new AppDbContext(options);
        Assert.False((await verify.Guests.SingleAsync(g => g.Id == guest.Id)).MarketingConsent);
        Assert.Equal(GuestConsentAction.Withdrawn, (await verify.GuestConsentRecords.SingleAsync(r => r.GuestId == guest.Id)).Action);
        Assert.False((await verify.GuestContacts.SingleAsync()).IsSuppressed);
        Assert.Equal(2, log.Entries.Count(entry => entry.Level == LogLevel.Error && entry.Message.Contains("Crm__ContactIndexKey", StringComparison.Ordinal)));
        Assert.DoesNotContain("example", log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mario", log.AllOutput, StringComparison.OrdinalIgnoreCase);
    }
}
