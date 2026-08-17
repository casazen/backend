using System.Net.Http.Headers;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Casazen.Tests.Integration;

/// <summary>
/// WebApplicationFactory for integration tests — in-memory EF, test auth, mocked Hangfire.
/// </summary>
public class CasazenWebApplicationFactory : WebApplicationFactory<Program>
{
    public Mock<IBackgroundJobClient> BackgroundJobClientMock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", string.Empty);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", string.Empty);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = string.Empty,
                ["Auth0:Domain"] = "test.auth0.com",
                ["Auth0:Audience"] = "https://test-api.casazen.app",
                ["Stripe:PublishableKey"] = "pk_test_integration",
                ["DirectBooking:ConsentVersion"] = "2026-06-direct-checkout-v1",
                ["DirectBooking:PendingTtlMinutes"] = "15",
                ["DirectBooking:RateLimitPermitLimit"] = "1000",
                ["CheckIn:RateLimitPermitLimit"] = "1000",
                ["CheckIn:SubmitRateLimitPermitLimit"] = "1000",
                ["Billing:Prices:Starter"] = "price_test_starter",
                ["Billing:Prices:Pro"] = "price_test_pro",
                ["Billing:Prices:Scale"] = "price_test_scale",
                ["Vies:StubMode"] = "true",
                ["Seo:BootstrapOnStartup"] = "false",
                ["Billing:Sdi:Enabled"] = "false",
                ["Billing:PlatformVatNumber"] = "IT12345678901",
                ["Vies:Enabled"] = "false",
                ["App:PublicSiteBaseUrl"] = "https://casazen-app.vercel.app",
                ["App:ApiBaseUrl"] = "https://casazen-api-test.up.railway.app",
                ["Legal:Documents:Tos:Version"] = "2026-06-v1",
                ["Legal:Documents:Privacy:Version"] = "2026-06-v1",
                ["Legal:Documents:Dpa:Version"] = "2026-06-v1",
                ["Legal:Documents:Subprocessors:Version"] = "2026-06-v1",
                ["Legal:Documents:Subprocessors:Items:0:Name"] = "Supabase",
                ["Legal:Documents:Subprocessors:Items:0:Purpose"] = "Database",
                ["Legal:Documents:Subprocessors:Items:0:Region"] = "EU",
                ["Legal:Documents:Subprocessors:Items:1:Name"] = "Auth0",
                ["Legal:Documents:Subprocessors:Items:1:Purpose"] = "Auth",
                ["Legal:Documents:Subprocessors:Items:1:Region"] = "EU",
                ["Legal:Documents:Subprocessors:Items:2:Name"] = "Stripe",
                ["Legal:Documents:Subprocessors:Items:2:Purpose"] = "Payments",
                ["Legal:Documents:Subprocessors:Items:2:Region"] = "EU",
                ["Legal:Documents:Subprocessors:Items:3:Name"] = "SendGrid",
                ["Legal:Documents:Subprocessors:Items:3:Purpose"] = "Email",
                ["Legal:Documents:Subprocessors:Items:3:Region"] = "EU",
                ["Compliance:CinGuidanceUrl"] = "https://www.bdsr.it/cin",
                ["Compliance:CheckoutReminderHourLocal"] = "20",
                ["Compliance:GdprRetentionYears"] = "7",
                ["Compliance:RequiredDocuments:default:0"] = "CinCertificate",
                ["Compliance:RequiredDocuments:default:1"] = "SafetyCompliance",
                ["CheckIn:RateLimitPermitLimit"] = "100",
                ["CheckIn:SubmitRateLimitPermitLimit"] = "100",
                ["ESign:WebhookSecret"] = "esign-test-secret",
                ["Stripe:WebhookSecret"] = "whsec_test_casazen_integration",
                ["Rli:TosVersion"] = "2026-08-rli-delega-bozza",
                ["LeaseTemplates:Variants:CedolareSecca:VersionId"] = "dev-stub",
                ["LeaseTemplates:Variants:CedolareSecca:Approved"] = "true",
                ["LeaseTemplates:Variants:RegimeOrdinario:VersionId"] = "dev-stub",
                ["LeaseTemplates:Variants:RegimeOrdinario:Approved"] = "true",
                ["LeaseTemplates:Variants:CanoneConcordato:VersionId"] = "dev-stub",
                ["LeaseTemplates:Variants:CanoneConcordato:Approved"] = "true",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            RemoveService<IPublicHolidayService>(services);
            var holidayMock = new Mock<IPublicHolidayService>();
            holidayMock.Setup(h => h.IsPublicHolidayAsync(It.IsAny<DateTime>())).ReturnsAsync(false);
            services.AddScoped(_ => holidayMock.Object);

            RemoveService<IBackgroundJobClient>(services);
            BackgroundJobClientMock
                .Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
                .Returns("job-integration-test-001");
            services.AddSingleton(BackgroundJobClientMock.Object);

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });

            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    _ => { });

            RemoveService<IStripeConnectGateway>(services);
            services.AddSingleton<IStripeConnectGateway, FakeStripeConnectGateway>();

            RemoveService<IStripeService>(services);
            services.AddSingleton<IStripeService, FakeStripeService>();

            RemoveService<IStripeBillingService>(services);
            services.AddSingleton<IStripeBillingService, FakeStripeBillingService>();

            RemoveService<IBillingEntryGate>(services);
            services.AddSingleton<IBillingEntryGate>(sp =>
            {
                var gate = new Mock<IBillingEntryGate>();
                gate.Setup(g => g.AssertCanChargeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
                return gate.Object;
            });

            // Lease create requires a verified APE; integration tests stub the gate unless a suite replaces it.
            RemoveAllOf<IApeComplianceService>(services);
            var ape = new Mock<IApeComplianceService>();
            ape.Setup(s => s.EnsurePropertyHasValidApeAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
            ape.Setup(s => s.EnsureUploadedFileIsOfficialApeAsync(It.IsAny<IFormFile>()))
                .Returns(Task.CompletedTask);
            services.AddSingleton(ape.Object);
        });
    }

    public HttpClient CreateAuthenticatedClient(
        string userId = TestAuthHandler.DefaultUserId,
        string? roles = null,
        string? email = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", userId);
        if (!string.IsNullOrWhiteSpace(roles))
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        if (!string.IsNullOrWhiteSpace(email))
            client.DefaultRequestHeaders.Add("X-Test-Email", email);
        return client;
    }

    public async Task<Property> SeedPropertyAsync(string ownerId = TestAuthHandler.DefaultUserId, decimal nightlyRate = 100m)
    {
        var org = await SeedOrgForOwnerAsync(ownerId);

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = "Pricing Integration Property",
            Description = "Integration test property",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = "Rome",
            PostalCode = $"00{Random.Shared.Next(100, 999)}",
            Latitude = 41.9028m,
            Longitude = 12.4964m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = nightlyRate,
            CleaningFee = 50m,
            DamageDeposit = 200m,
            CinCode = "IT-ABC123-DEF456",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    /// <summary>
    /// Finds or creates the default <see cref="Org"/> for an owner and ensures the owner's
    /// <see cref="User"/> row carries its <c>OrgId</c>, so the tenant query filter makes seeded
    /// rows visible to the authenticated owner (US-004). Returns the owner's org.
    /// </summary>
    public async Task<OrgEntity> SeedOrgForOwnerAsync(string ownerId = TestAuthHandler.DefaultUserId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await EnsureOrgAsync(db, ownerId);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (ArgumentException ex) when (ex.Message.Contains("same key", StringComparison.OrdinalIgnoreCase))
        {
            // Parallel test already seeded the same user/org — re-query for committed values
            db.ChangeTracker.Clear();
            var slug = $"test-org-{ownerId}";
            org = await db.Orgs.FirstOrDefaultAsync(o => o.Slug == slug);
            if (org is null)
                throw;
        }

        return org;
    }

    private static async Task<OrgEntity> EnsureOrgAsync(AppDbContext db, string ownerId)
    {
        var slug = $"test-org-{ownerId}";
        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Slug == slug);
        if (org is null)
        {
            org = new OrgEntity
            {
                Name = $"Org {ownerId}",
                Slug = slug,
                DisplayName = $"Org {ownerId}",
                ContactEmail = "owner@example.com",
                PlanTier = PlanTier.Starter,
                IsActive = true,
            };
            db.Orgs.Add(org);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ownerId);
        if (user is null)
        {
            db.Users.Add(new User
            {
                Id = ownerId,
                Email = $"{Guid.NewGuid():N}@example.com",
                FirstName = "Test",
                LastName = "Owner",
                OrgId = org.Id,
                IsActive = true,
            });
        }
        else if (user.OrgId is null)
        {
            user.OrgId = org.Id;
        }

        return org;
    }

    public async Task SeedPricingHistoryAsync(Guid propertyId, int count = 3)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        for (var i = 0; i < count; i++)
        {
            db.PricingHistories.Add(new PricingHistory
            {
                PropertyId = propertyId,
                AdaptationDate = DateTime.UtcNow.AddDays(-i),
                PreviousPrice = 100m,
                NewPrice = 110m + i,
                ChangeReason = $"Adaptation {i + 1}",
                AiConfidence = 0.85m,
                OtasSynced = "airbnb",
                SyncStatus = "Synced",
                CreatedAt = DateTime.UtcNow.AddDays(-i),
            });
        }

        await db.SaveChangesAsync();
    }

    protected static void RemoveService<T>(IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor != null)
            services.Remove(descriptor);
    }

    protected static void RemoveAllOf<T>(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
            services.Remove(descriptor);
    }

}
