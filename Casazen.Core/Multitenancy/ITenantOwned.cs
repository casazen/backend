namespace Casazen.Core.Multitenancy;

/// <summary>
/// Marks an entity whose rows belong to exactly one tenant (<c>OrgId</c>). <c>AppDbContext</c> registers
/// the global tenant query filter (<c>!FilterEnabled || OrgId == tenant.OrgId</c>) on every entity that
/// implements it, so authenticated reads are org-scoped without per-query code (TN-2).
/// </summary>
/// <remarks>
/// A new tenant entity implements this interface; an entity that must stay unfiltered goes in the
/// motivated allow-list of <c>TenantQueryFilterArchitectureTests</c>, which fails for any other DbSet.
/// Child rows copy <c>OrgId</c> from their parent (property, booking) when they are created.
/// </remarks>
public interface ITenantOwned
{
    Guid OrgId { get; }
}
