using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F04/US01/T01 (NF-90, CRM-03, AC-NF90-5): the proofs of the double opt-in. The link holds no personal data and a changed value is refused;
/// a person is never mailed more than the limit of confirmations and never within ten minutes of the last one; nothing is sent to an address that
/// opted out and an old link cannot bring it back; a wrong signature confirms nobody. On EF InMemory, the real services over the real keyed index.
/// </summary>
public class MarketingConfirmationProofTests
{
    private const string Secret = "a-long-random-secret-of-the-signed-links";
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// Privacy rule: the link holds the contact, the expiry and the signature and nothing else (no address, no org), the page address keeps them in
    /// the fragment, and a changed contact, expiry or signature, a missing secret or a late click is refused.
    /// </summary>
    [Fact]
    public void MarketingConfirmation_Link_CarriesNoAddressAndRejectsTampering_Test()
    {
        var clock = CrmTestWorld.Clock();
        var links = NewLinks(clock);
        var contactId = Guid.NewGuid();

        var link = links.Create(contactId);

        var values = QueryHelpers.ParseQuery(link.SignedQuery);
        Assert.Equal(["contactId", "expiresAt", "signature"], values.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.DoesNotContain("@", link.SignedQuery, StringComparison.Ordinal);
        Assert.Equal(contactId.ToString("D"), values["contactId"].ToString());
        var expiresAt = values["expiresAt"].ToString();
        var signature = values["signature"].ToString();
        Assert.Equal("2026-11-09T08:00:00Z", expiresAt);
        Assert.Matches("^[0-9a-f]{64}$", signature);

        // The values travel in the fragment of the address of the page: a browser never sends it to a server.
        var page = EmailTestHelpers.Links().MarketingConfirm(link.SignedQuery);
        Assert.StartsWith("https://casazen-app.test/marketing/confirm#contactId=", page, StringComparison.Ordinal);
        Assert.DoesNotContain("?", page, StringComparison.Ordinal);

        Assert.Equal(MarketingConfirmationCheck.Valid, links.Verify(contactId, expiresAt, signature));
        Assert.Equal(MarketingConfirmationCheck.Valid, links.Verify(contactId, expiresAt, signature.ToUpperInvariant()));

        // Another contact, a longer life, a changed signature or none: the signature is of these values and no others.
        Assert.Equal(MarketingConfirmationCheck.InvalidSignature, links.Verify(Guid.NewGuid(), expiresAt, signature));
        Assert.Equal(MarketingConfirmationCheck.InvalidSignature, links.Verify(contactId, "2026-12-31T08:00:00Z", signature));
        Assert.Equal(
            MarketingConfirmationCheck.InvalidSignature,
            links.Verify(contactId, expiresAt, signature[..^1] + (signature[^1] == '0' ? '1' : '0')));
        Assert.Equal(MarketingConfirmationCheck.InvalidSignature, links.Verify(contactId, expiresAt, string.Empty));
        Assert.Equal(MarketingConfirmationCheck.InvalidTimestamp, links.Verify(contactId, "tomorrow", signature));

        // Without the secret nothing is signed and nothing is checked.
        var unconfigured = NewLinks(CrmTestWorld.Clock(), secret: null);
        Assert.Throws<EmailConfigurationException>(() => unconfigured.Create(contactId));
        Assert.Equal(MarketingConfirmationCheck.NotConfigured, unconfigured.Verify(contactId, expiresAt, signature));

        // Valid for 72 hours, and not a second more.
        clock.Advance(TimeSpan.FromHours(72) + TimeSpan.FromSeconds(1));
        Assert.Equal(MarketingConfirmationCheck.Expired, links.Verify(contactId, expiresAt, signature));
    }

    /// <summary>
    /// Data rule: a person is never mailed more than three confirmations, and never twice inside ten minutes. The fourth call, and a call that
    /// comes too soon, send nothing and are not counted.
    /// </summary>
    [Fact]
    public async Task MarketingConfirmation_SendLimit_StopsAtMax_Test()
    {
        using var world = new Harness();
        var orgId = Guid.NewGuid();
        var contact = await world.ConsentAsync(orgId, "ospite@example.com");
        Assert.Equal(GuestContactConfirmation.Unconfirmed, contact.Confirmation);

        Assert.Equal(MarketingConfirmationSendOutcome.Queued, await world.Service.SendConfirmationAsync(orgId, contact.Id, Ct));

        // A repeat inside the ten minutes sends nothing.
        world.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(MarketingConfirmationSendOutcome.TooSoon, await world.Service.SendConfirmationAsync(orgId, contact.Id, Ct));
        Assert.Single(world.Queue.Snapshot());

        world.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(MarketingConfirmationSendOutcome.Queued, await world.Service.SendConfirmationAsync(orgId, contact.Id, Ct));
        world.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(MarketingConfirmationSendOutcome.Queued, await world.Service.SendConfirmationAsync(orgId, contact.Id, Ct));

        // The fourth never goes out, however long the person waits.
        world.Clock.Advance(TimeSpan.FromDays(5));
        Assert.Equal(MarketingConfirmationSendOutcome.LimitReached, await world.Service.SendConfirmationAsync(orgId, contact.Id, Ct));

        Assert.Equal(3, world.Queue.Snapshot().Count);
        var stored = await world.Db.GuestContacts.AsNoTracking().SingleAsync(c => c.Id == contact.Id, Ct);
        Assert.Equal((3, GuestContactConfirmation.Pending), (stored.ConfirmationSentCount, stored.Confirmation));

        // A message the queue refuses (no e-mail provider) is not counted as sent.
        var another = await world.ConsentAsync(orgId, "altro@example.com");
        world.Queue.Refuse = true;
        Assert.Equal(MarketingConfirmationSendOutcome.Unavailable, await world.Service.SendConfirmationAsync(orgId, another.Id, Ct));
        var untouched = await world.Db.GuestContacts.AsNoTracking().SingleAsync(c => c.Id == another.Id, Ct);
        Assert.Equal((0, GuestContactConfirmation.Unconfirmed), (untouched.ConfirmationSentCount, untouched.Confirmation));
    }

    /// <summary>
    /// Privacy rule: nothing is sent to an address that opted out. A consent typed for an address that was already on the suppression list, and a
    /// consent that the person withdrew, get no e-mail; the click on a link made before the opt-out confirms nothing and does not lift it.
    /// </summary>
    [Fact]
    public async Task MarketingConfirmation_SuppressedContact_GetsNone_Test()
    {
        using var world = new Harness();
        var orgId = Guid.NewGuid();

        // The address opted out before it was a contact; a consent is then typed for it: stored, flagged suppressed, never written to.
        await world.Suppression.AddToSuppressionListAsync(orgId, "uscito@example.com", "unsubscribe", Ct);
        var late = await world.ConsentAsync(orgId, "uscito@example.com");
        Assert.True(late.IsSuppressed);
        Assert.Equal(MarketingConfirmationSendOutcome.Suppressed, await world.Service.SendConfirmationAsync(orgId, late.Id, Ct));

        // A person who consented and then withdrew.
        var consented = await world.ConsentAsync(orgId, "ospite@example.com");
        var link = world.Links.Create(consented.Id);
        await world.Contacts.WithdrawAsync(orgId, consented.Id, "unsubscribe", GuestConsentSource.GuestPage, cancellationToken: Ct);
        Assert.Equal(MarketingConfirmationSendOutcome.Suppressed, await world.Service.SendConfirmationAsync(orgId, consented.Id, Ct));
        Assert.Empty(world.Queue.Snapshot());

        // The link made before the opt-out is still inside its 72 hours: the click confirms nothing.
        var values = QueryHelpers.ParseQuery(link.SignedQuery);
        var outcome = await world.Service.ConfirmAsync(consented.Id, values["expiresAt"].ToString(), values["signature"].ToString(), Ct);
        Assert.Equal(MarketingConfirmationOutcome.ContactSuppressed, outcome);
        var stored = await world.Db.GuestContacts.AsNoTracking().SingleAsync(c => c.Id == consented.Id, Ct);
        Assert.True(stored.IsSuppressed);
        Assert.NotEqual(GuestContactConfirmation.Confirmed, stored.Confirmation);
        Assert.False(CrmContactRules.CanReceiveMarketing(stored));
    }

    /// <summary>
    /// Data rule: only the signed click confirms a consent. A wrong signature changes nothing; the right one confirms, makes the address marketable
    /// and confirms the rows of the check-in of the same guest and address; a second click changes nothing and says so.
    /// </summary>
    [Fact]
    public async Task MarketingConfirmation_Confirm_NeedsTheSignatureAndIsIdempotent_Test()
    {
        using var world = new Harness();
        var orgId = Guid.NewGuid();
        var guest = CrmTestWorld.AddGuest(world.Db, orgId, "Mario", "Rossi", "mario@example.com");
        await world.Db.SaveChangesAsync(Ct);
        var contact = (await world.Contacts.RecordConsentAsync(
            new CrmConsentInput(
                orgId, GuestContactChannel.Email, "Mario.Offerte@Example.com", GuestContactOrigin.CheckIn, "consent-v1", GuestConsentSource.GuestPortal, GuestId: guest.Id),
            Ct))!;
        world.Db.MarketingConsentRecords.Add(new MarketingConsentRecord
        {
            OrgId = orgId,
            BookingId = Guid.NewGuid(),
            GuestId = guest.Id,
            MarketingEmail = "mario.offerte@example.com",
            ConsentEmail = true,
            EmailConfirmed = false,
            ConsentTextVersion = "consent-v1",
        });
        await world.Db.SaveChangesAsync(Ct);
        Assert.False(CrmContactRules.CanReceiveMarketing(contact));

        Assert.Equal(MarketingConfirmationSendOutcome.Queued, await world.Service.SendConfirmationAsync(orgId, contact.Id, Ct));
        var values = QueryHelpers.ParseQuery(world.Links.Create(contact.Id).SignedQuery);
        var expiresAt = values["expiresAt"].ToString();
        var signature = values["signature"].ToString();

        // A signature of another contact confirms nobody.
        var forged = await world.Service.ConfirmAsync(contact.Id, expiresAt, world.Links.Create(Guid.NewGuid()).SignedQuery.Split("signature=")[1], Ct);
        Assert.Equal(MarketingConfirmationOutcome.InvalidSignature, forged);
        Assert.NotEqual(GuestContactConfirmation.Confirmed, (await world.Db.GuestContacts.AsNoTracking().SingleAsync(c => c.Id == contact.Id, Ct)).Confirmation);

        Assert.Equal(MarketingConfirmationOutcome.Confirmed, await world.Service.ConfirmAsync(contact.Id, expiresAt, signature, Ct));
        Assert.Equal(MarketingConfirmationOutcome.AlreadyConfirmed, await world.Service.ConfirmAsync(contact.Id, expiresAt, signature, Ct));

        world.Db.ChangeTracker.Clear();
        var stored = await world.Db.GuestContacts.AsNoTracking().SingleAsync(c => c.Id == contact.Id, Ct);
        Assert.Equal(GuestContactConfirmation.Confirmed, stored.Confirmation);
        Assert.NotNull(stored.ConfirmedAt);
        Assert.True(CrmContactRules.CanReceiveMarketing(stored));
        Assert.True((await world.Db.MarketingConsentRecords.AsNoTracking().SingleAsync(Ct)).EmailConfirmed);

        // A link for a contact that does not exist is refused only after the signature is right.
        var ghost = Guid.NewGuid();
        var ghostValues = QueryHelpers.ParseQuery(world.Links.Create(ghost).SignedQuery);
        Assert.Equal(
            MarketingConfirmationOutcome.ContactNotFound,
            await world.Service.ConfirmAsync(ghost, ghostValues["expiresAt"].ToString(), ghostValues["signature"].ToString(), Ct));
    }

    /// <summary>
    /// Trust rule: the e-mail is in the language of the contact, every dynamic value is HTML-encoded, the address that receives it is not in its body
    /// and it holds the link of the confirmation page.
    /// </summary>
    [Fact]
    public async Task MarketingConfirmation_Email_IsInTheContactLanguageAndEncodesTheOrgName_Test()
    {
        using var world = new Harness();
        var org = new OrgEntity
        {
            Name = "Org Test",
            Slug = $"org-{Guid.NewGuid():N}",
            DisplayName = "<script>alert(1)</script> Villa",
            ContactEmail = "org@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        world.Db.Orgs.Add(org);
        await world.Db.SaveChangesAsync(Ct);

        var english = await world.ConsentAsync(org.Id, "guest.en@example.com", language: "en");
        var italian = await world.ConsentAsync(org.Id, "ospite.it@example.com");
        Assert.Equal(MarketingConfirmationSendOutcome.Queued, await world.Service.SendConfirmationAsync(org.Id, english.Id, Ct));
        Assert.Equal(MarketingConfirmationSendOutcome.Queued, await world.Service.SendConfirmationAsync(org.Id, italian.Id, Ct));

        var sent = world.Queue.Snapshot();
        var toEnglish = Assert.Single(sent, mail => mail.To == "guest.en@example.com");
        var toItalian = Assert.Single(sent, mail => mail.To == "ospite.it@example.com");
        Assert.All(sent, mail => Assert.Equal("marketing-confirmation", mail.Template));
        Assert.StartsWith("Confirm your subscription", toEnglish.Content.Subject, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", toEnglish.Content.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("Conferma l'iscrizione", toItalian.Content.Subject, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"it\">", toItalian.Content.HtmlBody, StringComparison.Ordinal);
        foreach (var mail in sent)
        {
            Assert.DoesNotContain("<script>", mail.Content.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", mail.Content.HtmlBody, StringComparison.Ordinal);
            Assert.DoesNotContain("@example.com", mail.Content.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("/marketing/confirm#contactId=", mail.Content.HtmlBody, StringComparison.Ordinal);
            Assert.DoesNotContain("{0}", mail.Content.HtmlBody, StringComparison.Ordinal);
        }
    }

    private static MarketingConfirmationLinks NewLinks(TimeProvider clock, string? secret = Secret) =>
        new(Configuration(secret), Options.Create(new CrmOptions()), clock);

    private static IConfiguration Configuration(string? secret) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(secret is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["SIGNED_LINK_SECRET"] = secret })
            .Build();

    /// <summary>The world of the proofs: a store, a clock, the contact service and the suppression list over it, a queue that records, the service under test.</summary>
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Db = CrmTestWorld.Open(CrmTestWorld.NewStore());
            Clock = CrmTestWorld.Clock();
            Contacts = CrmTestWorld.Contacts(Db, Clock);
            Suppression = new SuppressionService(Db, CrmTestIndex.Create(), NullLogger<SuppressionService>.Instance);
            Queue = new RecordingEmailQueue();
            Links = NewLinks(Clock);
            Service = new MarketingConfirmationService(
                Db,
                Links,
                EmailTestHelpers.Links(),
                Queue,
                Contacts,
                Options.Create(new CrmOptions()),
                Clock,
                NullLogger<MarketingConfirmationService>.Instance);
        }

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public CrmContactService Contacts { get; }

        public SuppressionService Suppression { get; }

        public RecordingEmailQueue Queue { get; }

        public MarketingConfirmationLinks Links { get; }

        public MarketingConfirmationService Service { get; }

        /// <summary>A consent on an e-mail address, as a collection point records it: a contact with the basis consent, not confirmed.</summary>
        public async Task<GuestContact> ConsentAsync(Guid orgId, string address, string? language = null) =>
            (await Contacts.RecordConsentAsync(
                new CrmConsentInput(
                    orgId,
                    GuestContactChannel.Email,
                    address,
                    GuestContactOrigin.GuestPage,
                    "consent-v1",
                    GuestConsentSource.GuestPage,
                    IpAddress: "203.0.113.5",
                    Language: language),
                Ct))!;

        public void Dispose() => Db.Dispose();
    }
}
