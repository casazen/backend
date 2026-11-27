using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;

namespace Casazen.Tests.Integration;

/// <summary>
/// E07/F04/US01/T03 (NF-90, CRM-03, AC-NF90-3): the welcome page of a property, over HTTP on the real host (PostgreSQL in CI) with the e-mails
/// recorded. The public endpoints are not behind <c>Features:CrmMarketing</c>, which stays off here: a code already printed keeps working.
/// The page cannot be used to learn who is a customer or to bring back someone who opted out, it takes no consent from a guest who did not
/// declare to be of age, and a code that was replaced stops working.
/// </summary>
public class CrmWelcomeIntegrationTests : IClassFixture<ConsentCollectionFactory>
{
    private readonly ConsentCollectionFactory _factory;

    public CrmWelcomeIntegrationTests(ConsentCollectionFactory factory) => _factory = factory;

    /// <summary>
    /// Privacy rule: a new address, a known one that already confirmed and one that opted out all get the same answer, byte for byte, so the page
    /// tells nothing about who is a customer; the new one is stored and mailed once, the known one is not mailed again, and the one that opted out
    /// stays suppressed (its consent is stored, nothing is sent): the page does not bring anybody back.
    /// </summary>
    [Fact]
    public async Task WelcomeConsent_SameAnswerForKnownOrSuppressedAddress_Test()
    {
        var (orgId, propertyId, token) = await SeedLinkAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var fresh = $"nuovo.{suffix}@example.com";
        var known = $"noto.{suffix}@example.com";
        var optedOut = $"uscito.{suffix}@example.com";
        await SeedKnownAndOptedOutAsync(orgId, known, optedOut);
        using var client = _factory.CreateClient();

        var bodies = new List<string>();
        foreach (var address in new[] { fresh, known, optedOut })
        {
            var response = await PostConsentAsync(client, token, address);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        // The same answer for the three, and it says nothing but that.
        Assert.Single(bodies.Distinct());
        Assert.True(JsonDocument.Parse(bodies[0]).RootElement.GetProperty("confirmationSent").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var contacts = await db.GuestContacts.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync();
        Assert.Equal(3, contacts.Count);

        // The new address: a consent contact of the welcome page, on a lead profile of the property, with its double opt-in e-mail.
        var freshContact = Assert.Single(contacts, c => c.Value == fresh);
        Assert.Equal(GuestContactLegalBasis.Consent, freshContact.LegalBasis);
        Assert.Equal(GuestContactOrigin.WelcomePage, freshContact.Origin);
        Assert.Equal(GuestConsentSource.WelcomePage, freshContact.ConsentSource);
        Assert.Equal(ConsentCollectionFactory.MarketingVersion, freshContact.BasisVersion);
        Assert.Equal("en", freshContact.Language);
        Assert.Null(freshContact.GuestId);
        Assert.Equal((GuestContactConfirmation.Pending, 1, false), (freshContact.Confirmation, freshContact.ConfirmationSentCount, freshContact.IsSuppressed));
        var profile = await db.GuestCrmProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == freshContact.GuestCrmProfileId);
        Assert.Contains(propertyId.ToString(), profile.PropertyIdsJson, StringComparison.OrdinalIgnoreCase);

        // The known address stays confirmed and gets no new message; the one that opted out stays suppressed and gets none.
        var knownContact = Assert.Single(contacts, c => c.Value == known);
        Assert.Equal((GuestContactConfirmation.Confirmed, 0), (knownContact.Confirmation, knownContact.ConfirmationSentCount));
        var optedOutContact = Assert.Single(contacts, c => c.Value == optedOut);
        Assert.True(optedOutContact.IsSuppressed);
        Assert.Equal(0, optedOutContact.ConfirmationSentCount);
        Assert.False(CrmContactRules.CanReceiveMarketing(optedOutContact));

        var mails = _factory.SentEmails()
            .Where(mail => mail.Template == EmailTemplates.Names.MarketingConfirmation && mail.To is { } to && to.EndsWith(suffix + "@example.com", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(fresh, Assert.Single(mails).To);
    }

    /// <summary>
    /// Privacy rule: no consent is recorded from a guest who did not declare to be of age, whether the member says <c>false</c> or is missing;
    /// the other conditions have their own codes; nothing is stored and nothing is mailed.
    /// </summary>
    [Fact]
    public async Task WelcomeConsent_AdultNotConfirmed_IsRefused_Test()
    {
        var (orgId, _, token) = await SeedLinkAsync();
        var address = $"minore.{Guid.NewGuid():N}@example.com";
        using var client = _factory.CreateClient();
        var path = $"/api/public/welcome/{token}/consent";

        var declaredNo = await PostConsentAsync(client, token, address, adultConfirmed: false);
        var memberMissing = await client.PostAsync(path, Json(new
        {
            email = address,
            consentEmail = true,
            consentVersion = ConsentCollectionFactory.MarketingVersion,
        }));
        await AssertProblemAsync(declaredNo, HttpStatusCode.UnprocessableEntity, "welcome_adult_required");
        await AssertProblemAsync(memberMissing, HttpStatusCode.UnprocessableEntity, "welcome_adult_required");

        // The other conditions: a box, a valid address, the current text.
        await AssertProblemAsync(
            await client.PostAsync(path, Json(new { email = address, adultConfirmed = true, consentVersion = ConsentCollectionFactory.MarketingVersion })),
            HttpStatusCode.UnprocessableEntity,
            "welcome_channel_required");
        await AssertProblemAsync(
            await PostConsentAsync(client, token, "not-an-address", adultConfirmed: true),
            HttpStatusCode.UnprocessableEntity,
            "welcome_email_invalid");
        await AssertProblemAsync(
            await PostConsentAsync(client, token, address, adultConfirmed: true, version: "marketing-2020-old"),
            HttpStatusCode.Conflict,
            "welcome_consent_outdated");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.GuestContacts.IgnoreQueryFilters().AnyAsync(c => c.OrgId == orgId));
        Assert.False(await db.GuestCrmProfiles.IgnoreQueryFilters().AnyAsync(p => p.OrgId == orgId));
        Assert.DoesNotContain(_factory.SentEmails(), mail => mail.To == address);
    }

    /// <summary>
    /// Permission rule: a printed QR code that was replaced stops working. The page shows the org and the house with no personal data; after the
    /// rotation the old code answers the same 404 as a code that never existed, for the page and for the consent, the new one works, the old row
    /// stays revoked in the table and the property has one active link.
    /// </summary>
    [Fact]
    public async Task WelcomeLink_Rotate_RevokesTheOldToken_Test()
    {
        var (orgId, propertyId, oldToken) = await SeedLinkAsync();
        using var client = _factory.CreateClient();

        var page = JsonDocument.Parse(await client.GetStringAsync($"/api/public/welcome/{oldToken}")).RootElement;
        Assert.Equal("Villa Benvenuto", page.GetProperty("orgName").GetString());
        Assert.Equal("Casa del Lago", page.GetProperty("propertyName").GetString());
        Assert.Equal(ConsentCollectionFactory.MarketingVersion, page.GetProperty("marketingConsentVersion").GetString());
        Assert.Equal(3, page.EnumerateObject().Count());

        string newToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var links = scope.ServiceProvider.GetRequiredService<IPropertyWelcomeLinkService>();
            newToken = (await links.RotateAsync(orgId, propertyId, createdByUserId: null)).Token;
        }

        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(32, newToken.Length);

        // The old code is the same 404 as one that never existed, on the page and on the consent.
        var neverExisted = await client.GetAsync("/api/public/welcome/this-code-never-existed-0000000000");
        var oldPage = await client.GetAsync($"/api/public/welcome/{oldToken}");
        await AssertProblemAsync(oldPage, HttpStatusCode.NotFound, "welcome_link_invalid");
        await AssertProblemAsync(neverExisted, HttpStatusCode.NotFound, "welcome_link_invalid");
        await AssertProblemAsync(
            await PostConsentAsync(client, oldToken, $"vecchio.{Guid.NewGuid():N}@example.com"),
            HttpStatusCode.NotFound,
            "welcome_link_invalid");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/public/welcome/{newToken}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await PostConsentAsync(client, newToken, $"nuovo.{Guid.NewGuid():N}@example.com")).StatusCode);

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.PropertyWelcomeLinks.IgnoreQueryFilters().AsNoTracking().Where(l => l.PropertyId == propertyId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(oldToken, Assert.Single(rows, l => l.RevokedAt != null).Token);
        Assert.Equal(newToken, Assert.Single(rows, l => l.RevokedAt == null).Token);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> PostConsentAsync(
        HttpClient client,
        string token,
        string email,
        bool adultConfirmed = true,
        string version = ConsentCollectionFactory.MarketingVersion) =>
        client.PostAsync(
            $"/api/public/welcome/{token}/consent",
            Json(new { email, consentEmail = true, adultConfirmed, consentVersion = version, language = "en" }));

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("detail").GetString()));
    }

    /// <summary>An address that confirmed its consent, and one that opted out through the suppression list, both of this org.</summary>
    private async Task SeedKnownAndOptedOutAsync(Guid orgId, string known, string optedOut)
    {
        using var scope = _factory.Services.CreateScope();
        var contacts = scope.ServiceProvider.GetRequiredService<ICrmContactService>();
        var suppression = scope.ServiceProvider.GetRequiredService<ISuppressionService>();

        var consent = await contacts.RecordConsentAsync(new CrmConsentInput(
            orgId,
            GuestContactChannel.Email,
            known,
            GuestContactOrigin.GuestPage,
            ConsentCollectionFactory.MarketingVersion,
            GuestConsentSource.GuestPage));
        Assert.NotNull(consent);
        await contacts.MarkConfirmedAsync(orgId, consent.Id);
        await suppression.AddToSuppressionListAsync(orgId, optedOut, "unsubscribe");
    }

    private async Task<(Guid OrgId, Guid PropertyId, string Token)> SeedLinkAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Welcome Org",
            Slug = $"wlc-{Guid.NewGuid():N}",
            DisplayName = "Villa Benvenuto",
            ContactEmail = "wlc@example.com",
            PlanTier = PlanTierEnum.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = "Casa del Lago",
            Slug = $"casa-{Guid.NewGuid():N}",
            Description = "Integration test property",
            Address = $"Via Benvenuto {Guid.NewGuid():N}",
            City = "Seveso",
            PostalCode = "20822",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CleaningFee = 20m,
            CinCode = "IT108040C2ABCDEFGH",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();

        var links = scope.ServiceProvider.GetRequiredService<IPropertyWelcomeLinkService>();
        var link = await links.CreateOrGetAsync(org.Id, property.Id, createdByUserId: null);
        return (org.Id, property.Id, link.Link.Token);
    }
}
