using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;

namespace Casazen.Tests.Integration;

/// <summary>
/// The web host with the text of the marketing consent and the soft opt-in notice versioned (the product owner's legal texts), the secret of the
/// signed links set and the e-mails recorded instead of queued, so a test can read the double opt-in message a guest would receive.
/// </summary>
public class ConsentCollectionFactory : CasazenWebApplicationFactory
{
    public const string MarketingVersion = "marketing-2026-10-test";
    public const string NoticeVersion = "notice-2026-10-test";

    private readonly RecordingEmailQueue _emails = new();

    /// <summary>What was queued so far: recipient, content and template name.</summary>
    public IReadOnlyList<(string? To, EmailContent Content, string Template)> SentEmails() => _emails.Snapshot();

    /// <summary>While true the queue refuses every e-mail, like the real one when the e-mail provider is not configured.</summary>
    public bool RefuseEmails
    {
        get => _emails.Refuse;
        set => _emails.Refuse = value;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gdpr:MarketingConsentVersion"] = MarketingVersion,
            ["Gdpr:SoftOptInNoticeVersion"] = NoticeVersion,
            ["SIGNED_LINK_SECRET"] = "integration-test-signed-link-secret",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailQueue>();
            services.AddSingleton<IEmailQueue>(_emails);
        });
    }
}

/// <summary>
/// E07/F04/US01/T02 (NF-90, CRM-03): the consents collected at the checkout of the booking site and at the online check-in feed the CRM. The
/// checkout keeps the proof of what each guest agreed to, per channel, in the same insert as the booking, and makes the matching contacts; the
/// soft opt-in notice is recorded as presented only when it is the one in force; a minor never becomes a marketing contact. Over HTTP, on the real
/// host (PostgreSQL in CI), with the e-mails recorded.
/// </summary>
public class CrmConsentCollectionIntegrationTests : IClassFixture<ConsentCollectionFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private readonly ConsentCollectionFactory _factory;

    public CrmConsentCollectionIntegrationTests(ConsentCollectionFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    /// <summary>
    /// Privacy rule: a guest who ticks e-mail and WhatsApp and is shown the notice in force leaves the proof of each act (one row for each
    /// channel and the notice, once each), a consent contact for each channel with the version, the source and the guest, and the double opt-in
    /// e-mail of the address; the contact is not marketable until the guest confirms.
    /// </summary>
    [Fact]
    public async Task DirectCheckout_PerChannelConsent_RecordsHistoryAndContact_Test()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();
        var guestEmail = $"mario.{Guid.NewGuid():N}@example.com";

        var response = await PostAsync(
            client,
            Payload(property.Id, guestEmail, 40, consentEmail: true, consentWhatsApp: true, notice: ConsentCollectionFactory.NoticeVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bookingId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("bookingId").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.IgnoreQueryFilters().AsNoTracking().Include(b => b.Guest).SingleAsync(b => b.Id == bookingId);

        // The privacy history, written with the booking: one row for each ticked channel and the notice, and nothing twice.
        var history = await db.GuestConsentRecords.IgnoreQueryFilters().AsNoTracking().Where(r => r.GuestId == booking.GuestId).ToListAsync();
        Assert.Equal(3, history.Count);
        Assert.Equal(
            2,
            history.Count(r => r is
            {
                Purpose: GuestConsentPurpose.Marketing,
                Action: GuestConsentAction.Granted,
                Source: GuestConsentSource.BookingCheckout,
                Version: ConsentCollectionFactory.MarketingVersion,
            }));
        var notice = Assert.Single(history, r => r.Purpose == GuestConsentPurpose.SoftOptInNotice);
        Assert.Equal(
            (GuestConsentAction.NoticePresented, ConsentCollectionFactory.NoticeVersion, GuestConsentSource.BookingCheckout),
            (notice.Action, notice.Version, notice.Source));
        Assert.True(booking.Guest.MarketingConsent);

        // The contacts of the CRM: a consent for each channel, with the version, the source and the guest.
        var contacts = await db.GuestContacts.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrgId == property.OrgId).ToListAsync();
        var email = Assert.Single(contacts, c => c.Channel == GuestContactChannel.Email);
        var whatsApp = Assert.Single(contacts, c => c.Channel == GuestContactChannel.WhatsApp);
        Assert.Equal(guestEmail, email.Value);
        Assert.Equal("+393331234567", whatsApp.Value);
        foreach (var contact in new[] { email, whatsApp })
        {
            Assert.Equal(GuestContactLegalBasis.Consent, contact.LegalBasis);
            Assert.Equal(GuestContactOrigin.DirectBooking, contact.Origin);
            Assert.Equal(GuestConsentSource.BookingCheckout, contact.ConsentSource);
            Assert.Equal(ConsentCollectionFactory.MarketingVersion, contact.BasisVersion);
            Assert.Equal(booking.GuestId, contact.GuestId);
        }

        // The double opt-in e-mail was queued once, and until the click the address is not marketable.
        Assert.Equal((GuestContactConfirmation.Pending, 1), (email.Confirmation, email.ConfirmationSentCount));
        var sent = Assert.Single(_factory.SentEmails(), mail => mail.To == guestEmail && mail.Template == EmailTemplates.Names.MarketingConfirmation);
        Assert.DoesNotContain(guestEmail, sent.Content.HtmlBody, StringComparison.Ordinal);
        Assert.False(CrmContactRules.CanReceiveMarketing(email));
    }

    /// <summary>
    /// Privacy rule: a notice the guest read that is no longer the one in force is refused (409) and nothing is stored; the notice in force is
    /// recorded as presented, and a checkout that shows none records none.
    /// </summary>
    [Fact]
    public async Task DirectCheckout_SoftOptInNoticeOutdated_Is409_Test()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();

        var outdated = await PostAsync(client, Payload(property.Id, $"old.{Guid.NewGuid():N}@example.com", 50, notice: "notice-2020-old"));

        Assert.Equal(HttpStatusCode.Conflict, outdated.StatusCode);
        var problem = JsonDocument.Parse(await outdated.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("direct_booking_soft_opt_in_notice_outdated", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.PropertyId == property.Id));
            Assert.False(await db.Guests.IgnoreQueryFilters().AnyAsync(g => g.OrgId == property.OrgId));
            Assert.False(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.OrgId == property.OrgId));
        }

        var current = await PostAsync(
            client, Payload(property.Id, $"new.{Guid.NewGuid():N}@example.com", 50, notice: ConsentCollectionFactory.NoticeVersion));
        var shownNone = await PostAsync(client, Payload(property.Id, $"none.{Guid.NewGuid():N}@example.com", 60));

        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.OK, shownNone.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await verifyDb.GuestConsentRecords.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrgId == property.OrgId).ToListAsync();
        var presented = Assert.Single(rows);
        Assert.Equal((GuestConsentPurpose.SoftOptInNotice, ConsentCollectionFactory.NoticeVersion), (presented.Purpose, presented.Version));
    }

    /// <summary>
    /// Data rule: a consent on a channel needs the address of that channel. The WhatsApp box with no phone number, or with one the CRM cannot store,
    /// is 422 <c>direct_booking_whatsapp_requires_phone</c> and nothing is stored; the same box with a number goes through.
    /// </summary>
    [Fact]
    public async Task DirectCheckout_WhatsAppWithoutAPhone_Is422_AndStoresNothing_Test()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();

        var withoutPhone = await PostAsync(
            client, Payload(property.Id, $"nophone.{Guid.NewGuid():N}@example.com", 70, consentWhatsApp: true, phone: null));
        var shortNumber = await PostAsync(
            client, Payload(property.Id, $"short.{Guid.NewGuid():N}@example.com", 70, consentWhatsApp: true, phone: "12"));

        foreach (var refused in new[] { withoutPhone, shortNumber })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("direct_booking_whatsapp_requires_phone", problem.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.PropertyId == property.Id));
            Assert.False(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.OrgId == property.OrgId));
            Assert.False(await db.GuestContacts.IgnoreQueryFilters().AnyAsync(c => c.OrgId == property.OrgId));
        }

        var withPhone = await PostAsync(
            client, Payload(property.Id, $"phone.{Guid.NewGuid():N}@example.com", 70, consentWhatsApp: true));

        Assert.Equal(HttpStatusCode.OK, withPhone.StatusCode);
    }

    /// <summary>
    /// Privacy rule: minors stay excluded from marketing. A check-in whose booker is a minor and who ticks the e-mail channel is refused, and no
    /// consent row, no privacy history and no contact is made.
    /// </summary>
    [Fact]
    public async Task CheckIn_MinorBooker_CreatesNoMarketingContact_Test()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var token = await CreateCheckInSessionAsync(seed.BookingId);
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/public/checkin/{token}",
            CheckInPayload(TimeProvider.System.TodayInRome().AddYears(-10), "Piccolo.Offerte@Example.it"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("MarketingConsentEmail", out _));
        await AssertNoMarketingTraceAsync(seed.BookingId, seed.GuestId);
        Assert.DoesNotContain(_factory.SentEmails(), mail => mail.To == "piccolo.offerte@example.it");
    }

    /// <summary>
    /// A booker of age who ticks the e-mail channel at the check-in leaves the consent contact of the address they typed (origin check-in, source
    /// guest portal, the version, the guest) and the double opt-in e-mail goes to that address.
    /// </summary>
    [Fact]
    public async Task CheckIn_AdultBooker_EmailChannel_MakesTheConsentContactAndQueuesItsConfirmation_Test()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var token = await CreateCheckInSessionAsync(seed.BookingId);
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/public/checkin/{token}",
            CheckInPayload(new DateTime(1990, 5, 15), "Luigi.Offerte@Example.it"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orgId = await db.Bookings.IgnoreQueryFilters().Where(b => b.Id == seed.BookingId).Select(b => b.OrgId).SingleAsync();

        // The value of a contact is encrypted at rest: the contact is found by its org and read in memory.
        var contact = Assert.Single(await db.GuestContacts.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync());
        Assert.Equal("luigi.offerte@example.it", contact.Value);
        Assert.Equal(GuestContactChannel.Email, contact.Channel);
        Assert.Equal(GuestContactOrigin.CheckIn, contact.Origin);
        Assert.Equal(GuestContactLegalBasis.Consent, contact.LegalBasis);
        Assert.Equal(GuestConsentSource.GuestPortal, contact.ConsentSource);
        Assert.Equal(ConsentCollectionFactory.MarketingVersion, contact.BasisVersion);
        Assert.Equal(seed.GuestId, contact.GuestId);
        Assert.Equal((GuestContactConfirmation.Pending, 1), (contact.Confirmation, contact.ConfirmationSentCount));
        Assert.Single(
            _factory.SentEmails(),
            mail => mail.To == "luigi.offerte@example.it" && mail.Template == EmailTemplates.Names.MarketingConfirmation);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private async Task AssertNoMarketingTraceAsync(Guid bookingId, Guid guestId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.MarketingConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.BookingId == bookingId));
        Assert.False(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.GuestId == guestId && r.Purpose == GuestConsentPurpose.Marketing));
        var orgId = await db.Bookings.IgnoreQueryFilters().Where(b => b.Id == bookingId).Select(b => b.OrgId).SingleAsync();
        Assert.False(await db.GuestContacts.IgnoreQueryFilters().AnyAsync(c => c.OrgId == orgId));
    }

    private async Task<string> CreateCheckInSessionAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.FindAsync(bookingId);
        var service = new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance);
        return await service.CreateSessionAsync(bookingId, booking!.OrgId);
    }

    private static StringContent CheckInPayload(DateTime bookerBirthDate, string marketingEmail)
    {
        var body = new
        {
            guests = new[]
            {
                new
                {
                    type = "SingleGuest",
                    firstName = "Luigi",
                    lastName = "Verdi",
                    gender = "Male",
                    dateOfBirth = bookerBirthDate.ToString("yyyy-MM-dd"),
                    bornInItaly = true,
                    birthComuneName = "Roma",
                    birthProvince = "RM",
                    citizenshipName = "Italia",
                    documentType = "Passport",
                    documentNumber = "YA1234567",
                    documentIssuePlaceName = "Roma",
                },
            },
            marketingConsentEmail = true,
            marketingConsentWhatsApp = false,
            marketingEmail,
        };
        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }

    private static Dictionary<string, object?> Payload(
        Guid propertyId,
        string guestEmail,
        int daysAhead,
        bool consentEmail = false,
        bool consentWhatsApp = false,
        string? notice = null,
        string? phone = "+393331234567")
    {
        var checkIn = TimeProvider.System.TodayInRome().AddDays(daysAhead);
        var fields = new Dictionary<string, object?>
        {
            ["propertyId"] = propertyId,
            ["checkInDate"] = checkIn.ToString("yyyy-MM-dd"),
            ["checkOutDate"] = checkIn.AddDays(3).ToString("yyyy-MM-dd"),
            ["numberOfAdults"] = 2,
            ["numberOfChildren"] = 0,
            ["guest"] = new
            {
                firstName = "Mario",
                lastName = "Rossi",
                email = guestEmail,
                phone,
                country = "IT",
            },
            ["consent"] = new { dataProcessing = true, consentVersion = ConsentVersion },
            ["paymentOption"] = "Immediate",
        };
        if (consentEmail)
            fields["marketingConsentEmail"] = true;
        if (consentWhatsApp)
            fields["marketingConsentWhatsApp"] = true;
        if (notice is not null)
            fields["softOptInNoticeVersion"] = notice;

        return fields;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, object payload) =>
        client.PostAsync("/api/public/bookings", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    private async Task<Property> SeedPropertyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Consent Collection Org",
            Slug = $"ccc-{Guid.NewGuid():N}",
            DisplayName = "Consent Collection Org",
            ContactEmail = "ccc@example.com",
            PlanTier = PlanTierEnum.Starter,
            IsActive = true,
            StripeConnectedAccountId = "acct_test_connect_ready",
            ConnectChargesEnabled = true,
        };
        db.Orgs.Add(org);
        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = "Consent Collection Villa",
            Slug = $"villa-{Guid.NewGuid():N}",
            Description = "Integration test property",
            Address = $"Via Consenso {Guid.NewGuid():N}",
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
        return property;
    }
}

/// <summary>
/// The default host: the product owner has set no soft opt-in notice (as in every environment today). The checkout shows none, a version sent
/// back is a notice that is not in force (409) and nothing is recorded, and a checkout that shows none is the one of today.
/// </summary>
public class CrmConsentCollectionWithoutNoticeIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public CrmConsentCollectionWithoutNoticeIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    [Fact]
    public async Task DirectCheckout_NoNoticeConfigured_RefusesAVersionAndTheOrgPublishesNone_Test()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();

        var org = JsonDocument.Parse(await client.GetStringAsync($"/api/public/orgs/{await SlugOfAsync(property.OrgId)}")).RootElement;
        var refused = await PostAsync(client, Payload(property.Id, notice: "notice-2026-10"));
        var plain = await PostAsync(client, Payload(property.Id, notice: null));

        Assert.Equal(JsonValueKind.Null, org.GetProperty("softOptInNoticeVersion").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(
            "direct_booking_soft_opt_in_notice_outdated",
            JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.OrgId == property.OrgId));
    }

    private static Dictionary<string, object?> Payload(Guid propertyId, string? notice)
    {
        var checkIn = TimeProvider.System.TodayInRome().AddDays(40);
        var fields = new Dictionary<string, object?>
        {
            ["propertyId"] = propertyId,
            ["checkInDate"] = checkIn.ToString("yyyy-MM-dd"),
            ["checkOutDate"] = checkIn.AddDays(3).ToString("yyyy-MM-dd"),
            ["numberOfAdults"] = 2,
            ["numberOfChildren"] = 0,
            ["guest"] = new
            {
                firstName = "Mario",
                lastName = "Rossi",
                email = $"mario.{Guid.NewGuid():N}@example.com",
                phone = "+393331234567",
                country = "IT",
            },
            ["consent"] = new { dataProcessing = true, consentVersion = "2026-06-direct-checkout-v1" },
            ["paymentOption"] = "Immediate",
        };
        if (notice is not null)
            fields["softOptInNoticeVersion"] = notice;

        return fields;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, object payload) =>
        client.PostAsync("/api/public/bookings", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    private async Task<string> SlugOfAsync(Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Orgs.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Slug).SingleAsync();
    }

    private async Task<Property> SeedPropertyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "No Notice Org",
            Slug = $"nno-{Guid.NewGuid():N}",
            DisplayName = "No Notice Org",
            ContactEmail = "nno@example.com",
            PlanTier = PlanTierEnum.Starter,
            IsActive = true,
            StripeConnectedAccountId = "acct_test_connect_ready",
            ConnectChargesEnabled = true,
        };
        db.Orgs.Add(org);
        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = "No Notice Villa",
            Slug = $"villa-{Guid.NewGuid():N}",
            Description = "Integration test property",
            Address = $"Via Informativa {Guid.NewGuid():N}",
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
        return property;
    }
}
