using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class PlgOnboardingIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-v1";
    private readonly CasazenWebApplicationFactory _factory;

    public PlgOnboardingIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AC4_LegalEndpoints_AreAnonymous()
    {
        var client = _factory.CreateClient();

        var subprocessors = await client.GetAsync("/api/legal/subprocessors");
        Assert.Equal(HttpStatusCode.OK, subprocessors.StatusCode);

        var dpa = await client.GetAsync("/api/legal/dpa");
        Assert.Equal(HttpStatusCode.OK, dpa.StatusCode);

        var tos = await client.GetAsync("/api/legal/tos");
        Assert.Equal(HttpStatusCode.OK, tos.StatusCode);

        var privacy = await client.GetAsync("/api/legal/privacy");
        Assert.Equal(HttpStatusCode.OK, privacy.StatusCode);

        var body = await subprocessors.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ConsentVersion, body.GetProperty("version").GetString());
        // PL-14 (A9-40): the list comes from the configuration in use, not from a hand-written list.
        var items = body.GetProperty("items").EnumerateArray().ToList();
        var auth0 = Assert.Single(items, i => i.GetProperty("key").GetString() == "auth0");
        Assert.Equal("US", auth0.GetProperty("region").GetString()); // Testing tenant: test.auth0.com
        Assert.Contains(items, i => i.GetProperty("key").GetString() == "expo");
        Assert.DoesNotContain(items, i => i.GetProperty("name").GetString() == "SendGrid");
    }

    [Fact]
    public async Task GetTos_TextNotProvided_NotAvailableWithVersionAndDate()
    {
        var client = _factory.CreateClient();

        var tos = await client.GetFromJsonAsync<JsonElement>("/api/legal/tos?lang=en");

        Assert.Equal("tos", tos.GetProperty("key").GetString());
        Assert.Equal(ConsentVersion, tos.GetProperty("version").GetString());
        Assert.Equal("2026-06-01T00:00:00Z", tos.GetProperty("effectiveAt").GetString());
        // No text from the product owner yet (D14): "in preparation", never an invented text.
        Assert.False(tos.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tos.GetProperty("contentHtml").ValueKind);
    }

    [Fact]
    public async Task AC1_PostOnboarding_WithoutConsents_Returns400()
    {
        var userId = $"auth0|plg-no-consent-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);
        var response = await client.PostAsJsonAsync("/api/users/onboarding", new { rentalType = "ShortTerm" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        Assert.Null(user);
        Assert.False(await db.ConsentRecords.IgnoreQueryFilters().AnyAsync(c => c.UserId == userId));
    }

    [Fact]
    public async Task AC2_PostOnboarding_StaleConsentVersion_Returns400()
    {
        var userId = $"auth0|plg-stale-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);
        var payload = BuildOnboardingPayload("ShortTerm", consents: new
        {
            tosAccepted = true,
            tosVersion = "old-version",
            privacyAccepted = true,
            privacyVersion = ConsentVersion,
            dpaAccepted = true,
            dpaVersion = ConsentVersion,
            subprocessorsAcknowledged = true,
            subprocessorsVersion = ConsentVersion,
        });
        var response = await client.PostAsJsonAsync("/api/users/onboarding", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        Assert.Null(user);
        Assert.False(await db.ConsentRecords.IgnoreQueryFilters().AnyAsync(c => c.UserId == userId));
    }

    [Fact]
    public async Task AC1_AC3_PostOnboarding_RecordsConsentsAndProvisionsOrg()
    {
        var userId = $"auth0|plg-success-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");

        var response = await client.PostAsJsonAsync(
            "/api/users/onboarding",
            BuildOnboardingPayload("ShortTerm", consents: ValidConsents(marketingOptIn: true)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("orgProvisioned").GetBoolean());
        Assert.True(body.GetProperty("consentsRecorded").GetBoolean());
        Assert.NotEqual(Guid.Empty.ToString(), body.GetProperty("orgId").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orgId = Guid.Parse(body.GetProperty("orgId").GetString()!);
        var records = await db.ConsentRecords.IgnoreQueryFilters()
            .Where(c => c.UserId == userId && c.OrgId == orgId)
            .ToListAsync();

        Assert.Equal(5, records.Count);
        Assert.All(records, r => Assert.Equal("203.0.113.10", r.IpAddress));
        Assert.Contains(records, r => r.Type == ConsentType.Tos && r.Version == ConsentVersion);
        Assert.Contains(records, r => r.Type == ConsentType.Marketing);
    }

    [Fact]
    public async Task AC5_GetOnboardingStatus_RequiresAuth()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/onboarding/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC5_AC6_GetOnboardingStatus_ReflectsActivationMilestones()
    {
        var userId = $"auth0|plg-status-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);

        var onboard = await client.PostAsJsonAsync("/api/users/onboarding", BuildOnboardingPayload("ShortTerm"));
        Assert.Equal(HttpStatusCode.OK, onboard.StatusCode);

        var statusResponse = await client.GetAsync("/api/onboarding/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(status.GetProperty("roleChosen").GetBoolean());
        Assert.True(status.GetProperty("orgProvisioned").GetBoolean());
        Assert.True(status.GetProperty("consentsAccepted").GetBoolean());
        Assert.False(status.GetProperty("propertyCreated").GetBoolean());
        Assert.False(status.GetProperty("sitePublished").GetBoolean());
        Assert.False(status.GetProperty("firstBookingTaken").GetBoolean());
        Assert.False(status.GetProperty("activated").GetBoolean());
    }

    [Fact]
    public async Task AC6_AC10_GetOnboardingStatus_ActivatedRequiresSixBoolConjunction()
    {
        var userId = $"auth0|plg-activated-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);

        var onboard = await client.PostAsJsonAsync("/api/users/onboarding", BuildOnboardingPayload("ShortTerm"));
        Assert.Equal(HttpStatusCode.OK, onboard.StatusCode);
        var onboardBody = await onboard.Content.ReadFromJsonAsync<JsonElement>();
        var orgId = Guid.Parse(onboardBody.GetProperty("orgId").GetString()!);

        await SeedActivationMilestonesAsync(userId, orgId);

        var statusResponse = await client.GetAsync("/api/onboarding/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(status.GetProperty("propertyCreated").GetBoolean());
        Assert.True(status.GetProperty("sitePublished").GetBoolean());
        Assert.True(status.GetProperty("firstBookingTaken").GetBoolean());
        Assert.True(status.GetProperty("activated").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("publicBookingUrl").GetString()));
        Assert.Contains("/book/", status.GetProperty("publicBookingUrl").GetString()!);
        Assert.Equal("done", StepOf(status, "sitePublished").GetProperty("state").GetString());
        Assert.Equal("done", StepOf(status, "payments").GetProperty("state").GetString());
        Assert.Equal("done", StepOf(status, "cin").GetProperty("state").GetString());
        Assert.Equal("done", StepOf(status, "firstBooking").GetProperty("state").GetString());
    }

    // ─── PL-15: the checklist reflects the real state (A1-37, A3-26, PLG-AC10) ──────────────────

    [Fact]
    public async Task GetOnboardingStatus_NewHost_ListsEveryStepAndTheGeneratedProfileIsNotConfigured()
    {
        var (client, _, _) = await OnboardAsync("steps");

        var status = await GetStatusAsync(client);

        Assert.Equal(
            ["account", "organization", "property", "cin", "payments", "sitePublished", "firstBooking"],
            status.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("key").GetString()!).ToArray());
        Assert.Equal("done", StepOf(status, "account").GetProperty("state").GetString());
        // The org was provisioned with a generated slug (and the placeholder name): the host has not chosen them.
        Assert.Equal("todo", StepOf(status, "organization").GetProperty("state").GetString());
        Assert.Equal("org_profile_incomplete", ReasonOf(status, "organization"));
        Assert.Equal("todo", StepOf(status, "property").GetProperty("state").GetString());
        Assert.Equal("blocked", StepOf(status, "cin").GetProperty("state").GetString());
        Assert.Equal("todo", StepOf(status, "payments").GetProperty("state").GetString());
        Assert.Equal("connect_not_started", ReasonOf(status, "payments"));
        Assert.Equal("blocked", StepOf(status, "sitePublished").GetProperty("state").GetString());
        Assert.Equal("blocked", StepOf(status, "firstBooking").GetProperty("state").GetString());
    }

    [Fact]
    public async Task GetOnboardingStatus_OrgNameAndSlugChosenInSettings_OrganizationStepIsDone()
    {
        var (client, _, _) = await OnboardAsync("org-settings");
        Assert.Equal("todo", StepOf(await GetStatusAsync(client), "organization").GetProperty("state").GetString());

        var put = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Villa Parco Rentals",
            slug = $"villa-parco-{Guid.NewGuid():N}"[..30],
            contactEmail = "host@villaparco.it",
            contactEmailPublic = false,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var step = StepOf(await GetStatusAsync(client), "organization");
        Assert.Equal("done", step.GetProperty("state").GetString());
    }

    [Fact]
    public async Task GetOnboardingStatus_PublishedPropertyButStripeNotReady_SiteIsNotPublishedAndHasNoLink()
    {
        var (client, userId, orgId) = await OnboardAsync("no-stripe");
        await SeedPropertyAsync(userId, orgId);

        // A published property, no Stripe account: the link would answer every guest with a 409 (A3-26).
        var status = await GetStatusAsync(client);
        Assert.True(status.GetProperty("propertyCreated").GetBoolean());
        Assert.False(status.GetProperty("sitePublished").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("publicBookingUrl").ValueKind);
        Assert.Equal("inProgress", StepOf(status, "sitePublished").GetProperty("state").GetString());
        Assert.Equal("payments_not_ready", ReasonOf(status, "sitePublished"));
        Assert.Equal("connect_not_started", ReasonOf(status, "payments"));

        // The Connect onboarding is started, Stripe still asks for data: started is not done.
        await UpdateOrgAsync(orgId, org =>
        {
            org.StripeConnectedAccountId = "acct_started";
            org.ConnectDetailsSubmitted = false;
            org.ConnectRequirementsDueJson = "[\"individual.verification.document\"]";
        });
        status = await GetStatusAsync(client);
        Assert.False(status.GetProperty("sitePublished").GetBoolean());
        Assert.Equal("inProgress", StepOf(status, "payments").GetProperty("state").GetString());
        Assert.Equal("connect_requirements_due", ReasonOf(status, "payments"));

        // Everything submitted, Stripe has not enabled charges yet.
        await UpdateOrgAsync(orgId, org =>
        {
            org.ConnectDetailsSubmitted = true;
            org.ConnectRequirementsDueJson = null;
        });
        status = await GetStatusAsync(client);
        Assert.False(status.GetProperty("sitePublished").GetBoolean());
        Assert.Equal("connect_pending_verification", ReasonOf(status, "payments"));

        // Stripe enables charges (account.updated): now guests can book.
        await UpdateOrgAsync(orgId, org => org.ConnectChargesEnabled = true);
        status = await GetStatusAsync(client);
        Assert.True(status.GetProperty("sitePublished").GetBoolean());
        Assert.Contains("/book/", status.GetProperty("publicBookingUrl").GetString()!);
        Assert.Equal("done", StepOf(status, "payments").GetProperty("state").GetString());
        Assert.Equal("awaiting_first_booking", ReasonOf(status, "firstBooking"));
    }

    [Fact]
    public async Task GetOnboardingStatus_AllPropertiesPaused_SiteIsNotPublishedUntilOneIsReactivated()
    {
        var (client, userId, orgId) = await OnboardAsync("paused");
        await UpdateOrgAsync(orgId, org => EnableCharges(org));
        var propertyId = await SeedPropertyAsync(userId, orgId, p => { p.IsPaused = true; p.PausedAt = DateTime.UtcNow; });

        // PC-03: a paused property is created but it is not a published site.
        var status = await GetStatusAsync(client);
        Assert.True(status.GetProperty("propertyCreated").GetBoolean());
        Assert.False(status.GetProperty("sitePublished").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("publicBookingUrl").ValueKind);
        Assert.Equal("todo", StepOf(status, "sitePublished").GetProperty("state").GetString());
        Assert.Equal("properties_paused", ReasonOf(status, "sitePublished"));
        Assert.Equal(0, StepOf(status, "sitePublished").GetProperty("done").GetInt32());
        Assert.Equal(1, StepOf(status, "sitePublished").GetProperty("total").GetInt32());

        await UpdatePropertyAsync(propertyId, p => { p.IsPaused = false; p.PausedAt = null; });
        status = await GetStatusAsync(client);
        Assert.True(status.GetProperty("sitePublished").GetBoolean());
        Assert.Equal(1, StepOf(status, "sitePublished").GetProperty("done").GetInt32());
    }

    [Fact]
    public async Task GetOnboardingStatus_PausedAndPublishedProperty_SiteIsPublished()
    {
        var (client, userId, orgId) = await OnboardAsync("one-paused");
        await UpdateOrgAsync(orgId, org => EnableCharges(org));
        await SeedPropertyAsync(userId, orgId, p => { p.IsPaused = true; p.PausedAt = DateTime.UtcNow; });
        await SeedPropertyAsync(userId, orgId);

        var status = await GetStatusAsync(client);

        Assert.True(status.GetProperty("sitePublished").GetBoolean());
        var site = StepOf(status, "sitePublished");
        Assert.Equal(1, site.GetProperty("done").GetInt32());
        Assert.Equal(2, site.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData(PropertyComplianceStatus.Pending)]
    [InlineData(PropertyComplianceStatus.Suspended)]
    public async Task GetOnboardingStatus_PropertyWithoutTheComplianceActivation_SiteSaysCompliancePending(PropertyComplianceStatus compliance)
    {
        var (client, userId, orgId) = await OnboardAsync($"compliance-{compliance}");
        await UpdateOrgAsync(orgId, org => EnableCharges(org));
        await SeedPropertyAsync(userId, orgId, p => p.ComplianceStatus = compliance);

        var status = await GetStatusAsync(client);

        Assert.False(status.GetProperty("sitePublished").GetBoolean());
        Assert.Equal("compliance_pending", ReasonOf(status, "sitePublished"));
    }

    [Fact]
    public async Task GetOnboardingStatus_DeactivatedProperty_SiteSaysPropertiesInactive()
    {
        var (client, userId, orgId) = await OnboardAsync("inactive");
        await UpdateOrgAsync(orgId, org => EnableCharges(org));
        await SeedPropertyAsync(userId, orgId, p => p.IsActive = false);

        var status = await GetStatusAsync(client);

        Assert.False(status.GetProperty("sitePublished").GetBoolean());
        Assert.Equal("properties_inactive", ReasonOf(status, "sitePublished"));
    }

    [Fact]
    public async Task GetOnboardingStatus_SoftDeletedProperty_CountsAsNoPropertyAtAll()
    {
        var (client, userId, orgId) = await OnboardAsync("deleted");
        await UpdateOrgAsync(orgId, org => EnableCharges(org));
        await SeedPropertyAsync(userId, orgId, p => { p.IsDeleted = true; p.DeletedAt = DateTime.UtcNow; });

        var status = await GetStatusAsync(client);

        Assert.False(status.GetProperty("propertyCreated").GetBoolean());
        Assert.False(status.GetProperty("sitePublished").GetBoolean());
        Assert.Equal("no_property", ReasonOf(status, "property"));
        Assert.Equal("blocked", StepOf(status, "cin").GetProperty("state").GetString());
    }

    [Fact]
    public async Task GetOnboardingStatus_CinOnlyOnSomeProperties_CinStepIsInProgressWithTheCounts()
    {
        var (client, userId, orgId) = await OnboardAsync("cin");
        await SeedPropertyAsync(userId, orgId);
        await SeedPropertyAsync(userId, orgId, p => p.CinCode = null);
        // The old invented format is not a CIN (compliance.md).
        await SeedPropertyAsync(userId, orgId, p => p.CinCode = "IT-12345-1234567890");

        var cin = StepOf(await GetStatusAsync(client), "cin");

        Assert.Equal("inProgress", cin.GetProperty("state").GetString());
        Assert.Equal("cin_missing_or_invalid", cin.GetProperty("reason").GetString());
        Assert.Equal(1, cin.GetProperty("done").GetInt32());
        Assert.Equal(3, cin.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetOnboardingStatus_PublishedPropertyOfAnotherOrg_DoesNotMakeThisSitePublished()
    {
        var (client, _, orgId) = await OnboardAsync("tenant-a");
        await UpdateOrgAsync(orgId, org => EnableCharges(org));
        var (_, otherUserId, otherOrgId) = await OnboardAsync("tenant-b");
        await UpdateOrgAsync(otherOrgId, org => EnableCharges(org));
        await SeedPropertyAsync(otherUserId, otherOrgId);

        var status = await GetStatusAsync(client);

        Assert.False(status.GetProperty("propertyCreated").GetBoolean());
        Assert.False(status.GetProperty("sitePublished").GetBoolean());
        Assert.Equal("no_property", ReasonOf(status, "sitePublished"));
    }

    [Fact]
    public async Task GetOnboardingStatus_DisabledOrg_SiteIsBlocked()
    {
        var (client, userId, orgId) = await OnboardAsync("disabled");
        await UpdateOrgAsync(orgId, org =>
        {
            EnableCharges(org);
            org.IsActive = false;
        });
        await SeedPropertyAsync(userId, orgId);

        var status = await GetStatusAsync(client);

        Assert.False(status.GetProperty("sitePublished").GetBoolean());
        Assert.Equal("blocked", StepOf(status, "sitePublished").GetProperty("state").GetString());
        Assert.Equal("org_inactive", ReasonOf(status, "sitePublished"));
    }

    [Fact]
    public async Task AC7_AC9_PutOnboarding_DoesNotRequireConsents()
    {
        var userId = $"auth0|plg-put-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);

        var post = await client.PostAsJsonAsync("/api/users/onboarding", BuildOnboardingPayload("ShortTerm"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var put = await client.PutAsJsonAsync("/api/users/onboarding", new { rentalType = "LongTerm" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("orgProvisioned").GetBoolean());
        Assert.False(body.GetProperty("consentsRecorded").GetBoolean());
    }

    [Fact]
    public async Task PutOnboarding_WithoutOrgAndWithoutConsents_Returns422ConsentsRequiredAndDoesNotProvision()
    {
        var userId = $"auth0|plg-put-first-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/users/onboarding", new { rentalType = "ShortTerm" });

        // A1-01: a stable code the client answers with the consents step, not a generic 400 dead end.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("consents_required", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        Assert.Null(user);
    }

    [Fact]
    public async Task PutOnboarding_WithoutOrgWithConsents_ProvisionsOrgAndRecordsConsents()
    {
        var userId = $"auth0|plg-put-consents-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: "PropertyOwner");

        var response = await client.PutAsJsonAsync(
            "/api/users/onboarding",
            BuildOnboardingPayload("ShortTerm"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("orgProvisioned").GetBoolean());
        Assert.True(body.GetProperty("consentsRecorded").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orgId = Guid.Parse(body.GetProperty("orgId").GetString()!);
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        Assert.Equal(orgId, user.OrgId);
        Assert.Equal(4, await db.ConsentRecords.IgnoreQueryFilters().CountAsync(c => c.UserId == userId && c.OrgId == orgId));
    }

    [Fact]
    public async Task PostOnboarding_UserAlreadyLinkedToSupplierOrg_ProvisionsANewHostOrgAndKeepsTheSupplierLink()
    {
        // A1-40: a supplier registers first (User.OrgId = User.SupplierOrgId = the Supplier org, as
        // SupplierService links them), then does the host onboarding. It must get a real, new Host org — never
        // the Supplier org — and the supplier link must survive.
        var userId = $"auth0|plg-supplier-then-host-{Guid.NewGuid():N}";
        Guid supplierOrgId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var supplierOrg = new Casazen.Core.Entities.Org
            {
                Name = "Fornitore Srl",
                Slug = $"plg-supplier-{Guid.NewGuid():N}"[..30],
                DisplayName = "Fornitore Srl",
                ContactEmail = "fornitore@example.com",
                OrgType = OrgType.Supplier,
            };
            db.Orgs.Add(supplierOrg);
            db.Users.Add(new User
            {
                Id = userId,
                Email = "fornitore@example.com",
                FirstName = "Mario",
                LastName = "Fornitore",
                Role = UserRole.Supplier,
                OrgId = supplierOrg.Id,
                SupplierOrgId = supplierOrg.Id,
                IsActive = true,
            });
            await db.SaveChangesAsync();
            supplierOrgId = supplierOrg.Id;
        }

        using var client = _factory.CreateAuthenticatedClient(userId, roles: "Supplier", email: "fornitore@example.com");
        var response = await client.PostAsJsonAsync("/api/users/onboarding", BuildOnboardingPayload("ShortTerm"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var orgId = Guid.Parse(body.GetProperty("orgId").GetString()!);
        // The core A1-40 guarantee: never the Supplier org (orgProvisioned is not asserted here — it reflects
        // whether the account had *any* OrgId before the call, which was already true for this supplier).
        Assert.NotEqual(supplierOrgId, orgId);

        using var checkScope = _factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await checkDb.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        Assert.Equal(orgId, user.OrgId);
        Assert.Equal(supplierOrgId, user.SupplierOrgId);
        var hostOrg = await checkDb.Orgs.IgnoreQueryFilters().SingleAsync(o => o.Id == orgId);
        Assert.Equal(OrgType.Host, hostOrg.OrgType);
    }

    private async Task SeedActivationMilestonesAsync(string userId, Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = await db.Orgs.IgnoreQueryFilters().FirstAsync(o => o.Id == orgId);
        org.IsActive = true;
        if (string.IsNullOrWhiteSpace(org.Slug))
            org.Slug = $"plg-{Guid.NewGuid():N}"[..20];
        // PL-15 (A1-37, A3-26): the site counts as published only when guests can pay, like the checkout requires.
        org.StripeConnectedAccountId = $"acct_{Guid.NewGuid():N}"[..20];
        org.ConnectChargesEnabled = true;
        org.ConnectDetailsSubmitted = true;

        var property = new Property
        {
            OwnerId = userId,
            OrgId = orgId,
            Name = "PLG Activation Villa",
            Description = "Activation milestone property",
            Address = $"Via PLG {Guid.NewGuid():N}",
            City = "Rome",
            PostalCode = "00100",
            Latitude = 41.9028m,
            Longitude = 12.4964m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 120m,
            CleaningFee = 40m,
            DamageDeposit = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);

        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = "PLG",
            LastName = "Guest",
            Email = $"plg-guest-{Guid.NewGuid():N}@example.com",
            PhoneNumber = "+390612345678",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Guests.Add(guest);

        db.Bookings.Add(new Booking
        {
            PropertyId = property.Id,
            OrgId = orgId,
            GuestId = guest.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(7),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(10),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 360m,
            TouristTax = 12m,
            TotalPrice = 372m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient Client, string UserId, Guid OrgId)> OnboardAsync(string label)
    {
        var userId = $"auth0|plg-{label}-{Guid.NewGuid():N}";
        // PropertyOwner: the host role, so the org settings (OrgBillingAdmin) are open to this client once onboarded.
        var client = _factory.CreateAuthenticatedClient(userId, roles: "PropertyOwner");
        var response = await client.PostAsJsonAsync("/api/users/onboarding", BuildOnboardingPayload("ShortTerm"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (client, userId, Guid.Parse(body.GetProperty("orgId").GetString()!));
    }

    private static async Task<JsonElement> GetStatusAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/onboarding/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement StepOf(JsonElement status, string key) =>
        status.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    private static string? ReasonOf(JsonElement status, string key) =>
        StepOf(status, key).TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
            ? reason.GetString()
            : null;

    private static void EnableCharges(OrgEntity org)
    {
        org.StripeConnectedAccountId = $"acct_{Guid.NewGuid():N}"[..20];
        org.ConnectChargesEnabled = true;
        org.ConnectDetailsSubmitted = true;
    }

    private async Task UpdateOrgAsync(Guid orgId, Action<OrgEntity> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.IgnoreQueryFilters().FirstAsync(o => o.Id == orgId);
        change(org);
        await db.SaveChangesAsync();
    }

    private async Task UpdatePropertyAsync(Guid propertyId, Action<Property> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.IgnoreQueryFilters().FirstAsync(p => p.Id == propertyId);
        change(property);
        await db.SaveChangesAsync();
    }

    /// <summary>A property that is published (active, compliance activated, valid CIN) unless <paramref name="change"/> says otherwise.</summary>
    private async Task<Guid> SeedPropertyAsync(string userId, Guid orgId, Action<Property>? change = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = userId,
            OrgId = orgId,
            Name = "PLG Checklist Villa",
            Description = "Checklist property",
            Address = $"Via PLG {Guid.NewGuid():N}",
            City = "Rome",
            PostalCode = "00100",
            Latitude = 41.9028m,
            Longitude = 12.4964m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 120m,
            CleaningFee = 40m,
            DamageDeposit = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        change?.Invoke(property);
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static object BuildOnboardingPayload(string rentalType, object? consents = null) => new
    {
        rentalType,
        consents = consents ?? ValidConsents(),
    };

    private static object ValidConsents(bool marketingOptIn = false) => new
    {
        tosAccepted = true,
        tosVersion = ConsentVersion,
        privacyAccepted = true,
        privacyVersion = ConsentVersion,
        dpaAccepted = true,
        dpaVersion = ConsentVersion,
        subprocessorsAcknowledged = true,
        subprocessorsVersion = ConsentVersion,
        marketingOptIn,
    };
}
