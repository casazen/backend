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
                ["Billing:Prices:Starter"] = "price_test_starter",
                ["Billing:Prices:Pro"] = "price_test_pro",
                ["Billing:Prices:Scale"] = "price_test_scale",
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
        });
    }

    public HttpClient CreateAuthenticatedClient(string userId = TestAuthHandler.DefaultUserId, string? roles = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", userId);
        if (!string.IsNullOrWhiteSpace(roles))
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        return client;
    }

    public async Task<Property> SeedPropertyAsync(string ownerId = TestAuthHandler.DefaultUserId, decimal nightlyRate = 100m)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = await EnsureOrgAsync(db, ownerId);

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
    public async Task<Org> SeedOrgForOwnerAsync(string ownerId = TestAuthHandler.DefaultUserId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await EnsureOrgAsync(db, ownerId);
        await db.SaveChangesAsync();
        return org;
    }

    private static async Task<Org> EnsureOrgAsync(AppDbContext db, string ownerId)
    {
        var slug = $"test-org-{ownerId}";
        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Slug == slug);
        if (org is null)
        {
            org = new Org
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

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor != null)
            services.Remove(descriptor);
    }

}
