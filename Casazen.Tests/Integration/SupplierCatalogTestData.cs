using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Tests.Integration;

/// <summary>Seeds a supplier org with its profile and an account linked to it, for the catalog tests (SP-02).</summary>
internal static class SupplierCatalogTestData
{
    /// <summary>
    /// A supplier org with a profile and an account holding the supplier role. By default a supplier-only account
    /// (<c>User.SupplierOrgId</c>, no <c>User.OrgId</c>, PL-05): the reason the catalog is keyed by the supplier org and not
    /// tenant-filtered. <paramref name="legacyOrgIdLink"/> also sets <c>User.OrgId</c> (the shape before PL-05).
    /// </summary>
    public static async Task<(string UserId, Guid OrgId)> SeedSupplierAsync(
        CasazenWebApplicationFactory factory,
        bool legacyOrgIdLink = false)
    {
        var userId = $"auth0|sp02-{Guid.NewGuid():N}";
        var email = $"sp02.{Guid.NewGuid():N}@example.com";

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Fornitore SP-02",
            Slug = $"supplier-{Guid.NewGuid():N}"[..30],
            DisplayName = "Fornitore SP-02",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = "Fornitore SP-02 Srl",
            Phone = "+39 06 020202",
            Status = SupplierStatus.Active,
            TosAcceptedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
            ComuniJson = """["H501"]""",
            CategoriesJson = """["cleaning"]""",
        });
        db.Users.Add(new User
        {
            Id = userId,
            Email = email,
            FirstName = "Mario",
            LastName = "Fornitore",
            Role = UserRole.Supplier,
            OrgId = legacyOrgIdLink ? org.Id : null,
            SupplierOrgId = org.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return (userId, org.Id);
    }

    /// <summary>A complete, published service row of <paramref name="orgId"/>, written straight to the database by <see cref="SaveRowAsync"/>.</summary>
    public static SupplierServiceListing Row(Guid orgId, string slug, DateTime? deletedAt = null) =>
        new()
        {
            OrgId = orgId,
            Slug = slug,
            Name = $"Servizio {slug}",
            Category = ServiceCategories.Cleaning,
            PriceFromCents = 1000,
            DurationMinutes = 60,
            Status = SupplierServiceListingStatus.Active,
            DeletedAt = deletedAt,
            CreatedAt = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc),
        };

    /// <summary>Saves <paramref name="row"/> on its own context: a failure leaves nothing tracked behind.</summary>
    public static async Task SaveRowAsync(CasazenWebApplicationFactory factory, SupplierServiceListing row)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierServiceListings.Add(row);
        await db.SaveChangesAsync();
    }
}

