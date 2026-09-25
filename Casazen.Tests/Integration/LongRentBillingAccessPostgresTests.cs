using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PL-16 (A1-36) over the real pipeline on PostgreSQL: a landlord with only the long-rent context, owner of its org,
/// reads the plan entitlement and pays its plan (org policy <c>OrgBillingAdmin</c>, not a context policy), and Stripe
/// brings it back to the plan page of the long-rent shell; a <c>Staff</c> collaborator of the same org gets 403; a
/// return page outside the allow-list is refused before any change.
/// </summary>
public class LongRentBillingAccessPostgresTests(CasazenWebApplicationFactory factory)
    : IClassFixture<CasazenWebApplicationFactory>
{
    /// <summary>Public URL of the web app configured by <see cref="CasazenWebApplicationFactory"/>.</summary>
    private const string PublicSite = "https://casazen-app.vercel.app";

    private const string LongRentPlanPage = "/app/long-rent/settings/plan";
    private const string LongRentBillingPage = "/app/long-rent/settings/billing";

    /// <summary>Role id of the seeded long-rent <c>long_term_landlord</c> role (<c>AppDbContext</c> seed).</summary>
    private const int LongTermLandlordRoleId = 2;

    private FakeStripeBillingService StripeFake =>
        (FakeStripeBillingService)factory.Services.GetRequiredService<IStripeBillingService>();

    [PostgresFact]
    public async Task EntitlementAndCheckout_AsLongTermLandlordOwnerOnly_Return200()
    {
        var (landlord, org) = await NewLongTermLandlordAsync();
        using var client = factory.CreateAuthenticatedClient(landlord, "LongTermLandlord");

        var entitlement = await client.GetAsync("/api/orgs/me/entitlement");
        Assert.Equal(HttpStatusCode.OK, entitlement.StatusCode);
        var body = await entitlement.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(org.Id, body.GetProperty("orgId").GetGuid());
        Assert.Equal("Starter", body.GetProperty("planTier").GetString());

        var checkout = await client.PostAsJsonAsync(
            "/api/billing/checkout-session",
            new { planTier = "Pro", billingCountry = "IT", returnPath = LongRentPlanPage });
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        Assert.Single(StripeFake.SessionsOf(FakeStripeBillingService.CustomerIdFor(org.Id)));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/billing/subscription")).StatusCode);
    }

    [PostgresFact]
    public async Task Entitlement_AsLongTermLandlordWithoutJwtRole_Returns200FromTheLongRentMembership()
    {
        // A JWT issued while the Auth0 role sync was failing: the DB membership of the long-rent context decides (A1-02).
        var (landlord, _) = await NewLongTermLandlordAsync();
        using var client = factory.CreateAuthenticatedClient(landlord);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/orgs/me/entitlement")).StatusCode);
    }

    [PostgresFact]
    public async Task BillingEndpoints_AsStaffCollaboratorOfTheOrg_Return403WithoutCheckout()
    {
        var (landlord, org) = await NewLongTermLandlordAsync();
        var collaborator = await AddStaffCollaboratorAsync(org.Id);
        using var client = factory.CreateAuthenticatedClient(collaborator, "Staff");

        var responses = new[]
        {
            await client.GetAsync("/api/orgs/me/entitlement"),
            await client.PostAsJsonAsync(
                "/api/billing/checkout-session",
                new { planTier = "Pro", billingCountry = "IT", returnPath = LongRentPlanPage }),
            await client.GetAsync("/api/billing/subscription"),
            await client.PostAsync("/api/billing/portal-session", null),
            await client.PutAsJsonAsync("/api/billing/profile", new { billingCountry = "IT" }),
            await client.PutAsJsonAsync("/api/orgs/me/plan", new { planTier = "Starter" }),
        };

        Assert.All(responses, r => Assert.True(
            r.StatusCode == HttpStatusCode.Forbidden,
            $"{r.RequestMessage!.Method} {r.RequestMessage.RequestUri} answered {(int)r.StatusCode} to a Staff collaborator."));
        Assert.Empty(StripeFake.SessionsOf(FakeStripeBillingService.CustomerIdFor(org.Id)));
        var stored = await ReadOrgAsync(org.Id);
        Assert.Null(stored.BillingCountry);
        Assert.Null(stored.StripeCustomerId);

        // The owner of the same org is not affected.
        using var owner = factory.CreateAuthenticatedClient(landlord, "LongTermLandlord");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/orgs/me/entitlement")).StatusCode);
    }

    [PostgresFact]
    public async Task CreateCheckoutSession_LongRentReturnPath_ReturnsToTheLongRentPlanPage()
    {
        var (landlord, _) = await NewLongTermLandlordAsync();
        using var client = factory.CreateAuthenticatedClient(landlord, "LongTermLandlord");

        var response = await client.PostAsJsonAsync(
            "/api/billing/checkout-session",
            new { planTier = "Scale", billingCountry = "IT", returnPath = LongRentPlanPage });

        var query = await QueryOfAsync(response, "checkoutUrl");
        Assert.Equal($"{PublicSite}{LongRentPlanPage}?checkout=success", query["success"]);
        Assert.Equal($"{PublicSite}{LongRentPlanPage}?checkout=cancel", query["cancel"]);
    }

    [PostgresTheory]
    [InlineData("https://evil.example/app/long-rent/settings/plan")]
    [InlineData("//evil.example/app/long-rent/settings/plan")]
    [InlineData("/app/long-rent/leases")]
    [InlineData("/app/long-rent/settings/plan?next=https://evil.example")]
    [InlineData("/app/long-rent/settings/../../../evil")]
    public async Task CreateCheckoutSession_ReturnPathOutsideAllowList_Returns400WithoutCheckout(string returnPath)
    {
        var (landlord, org) = await NewLongTermLandlordAsync();
        using var client = factory.CreateAuthenticatedClient(landlord, "LongTermLandlord");

        var response = await client.PostAsJsonAsync(
            "/api/billing/checkout-session",
            new { planTier = "Pro", billingCountry = "IT", returnPath });

        await AssertValidationProblemAsync(response);
        Assert.Empty(StripeFake.SessionsOf(FakeStripeBillingService.CustomerIdFor(org.Id)));
        var stored = await ReadOrgAsync(org.Id);
        Assert.Null(stored.BillingCountry);
        Assert.Null(stored.StripeCustomerId);
    }

    [PostgresFact]
    public async Task CreatePortalSession_LongRentReturnPath_ReturnsToTheLongRentBillingPage()
    {
        var (landlord, org) = await NewLongTermLandlordAsync(withStripeCustomer: true);
        using var client = factory.CreateAuthenticatedClient(landlord, "LongTermLandlord");

        var response = await client.PostAsJsonAsync("/api/billing/portal-session", new { returnPath = LongRentBillingPage });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var portalUrl = new Uri((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("portalUrl").GetString()!);
        Assert.EndsWith(FakeStripeBillingService.CustomerIdFor(org.Id), portalUrl.AbsolutePath);
        Assert.Equal($"{PublicSite}{LongRentBillingPage}", HttpUtility.ParseQueryString(portalUrl.Query)["return"]);
    }

    [PostgresFact]
    public async Task CreatePortalSession_WithoutBody_ReturnsToTheDefaultPlanPage()
    {
        var (landlord, _) = await NewLongTermLandlordAsync(withStripeCustomer: true);
        using var client = factory.CreateAuthenticatedClient(landlord, "LongTermLandlord");

        var response = await client.PostAsync("/api/billing/portal-session", null);

        var query = await QueryOfAsync(response, "portalUrl");
        Assert.Equal($"{PublicSite}/app/short-rent/settings/plan", query["return"]);
    }

    [PostgresFact]
    public async Task CreatePortalSession_ReturnPathOutsideAllowList_Returns400()
    {
        var (landlord, _) = await NewLongTermLandlordAsync(withStripeCustomer: true);
        using var client = factory.CreateAuthenticatedClient(landlord, "LongTermLandlord");

        var response = await client.PostAsJsonAsync(
            "/api/billing/portal-session",
            new { returnPath = "https://evil.example/app/long-rent/settings/billing" });

        await AssertValidationProblemAsync(response);
    }

    /// <summary>
    /// A host who chose "Locazioni di lungo periodo" at the onboarding: DB role <c>LongTermLandlord</c>, the long-rent
    /// membership only, owner of its own org (PL-02 onboarding and consents done).
    /// </summary>
    private async Task<(string UserId, OrgEntity Org)> NewLongTermLandlordAsync(bool withStripeCustomer = false)
    {
        var landlord = $"auth0|pl16-ltr-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(landlord);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == landlord);
        user.Role = UserRole.LongTermLandlord;
        db.UserContextMemberships.Add(new UserContextMembership
        {
            UserId = landlord,
            ContextKey = "long-rent",
            RoleId = LongTermLandlordRoleId,
        });
        if (withStripeCustomer)
        {
            var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
            stored.StripeCustomerId = FakeStripeBillingService.CustomerIdFor(org.Id);
        }

        await db.SaveChangesAsync();
        return (landlord, org);
    }

    /// <summary>A collaborator of the org with the <c>Staff</c> role, onboarded for that org (PL-02).</summary>
    private async Task<string> AddStaffCollaboratorAsync(Guid orgId)
    {
        var collaborator = $"auth0|pl16-staff-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = collaborator,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collaboratore",
            LastName = "Staff",
            OrgId = orgId,
            Role = UserRole.Staff,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
        return collaborator;
    }

    private async Task<OrgEntity> ReadOrgAsync(Guid orgId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orgs
            .AsNoTracking()
            .SingleAsync(o => o.Id == orgId);
    }

    /// <summary>Query of the fake Stripe URL in <paramref name="property"/>: the return pages it was created with.</summary>
    private static async Task<NameValueCollection> QueryOfAsync(HttpResponseMessage response, string property)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return HttpUtility.ParseQueryString(new Uri(body.GetProperty(property).GetString()!).Query);
    }

    private static async Task AssertValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }
}
