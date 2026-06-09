namespace Casazen.Core.Multitenancy;

/// <summary>
/// Resolves the caller's tenant (<c>OrgId</c>) from the authenticated principal.
/// Consumed by the EF global query filter so every tenant-scoped read is org-scoped
/// server-side (AC7). The client can never supply or widen the tenant key.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// The caller's organization id, or <c>null</c> when the caller has no org
    /// (brand-new user pre-backfill) or no authenticated principal. A <c>null</c>
    /// value combined with <see cref="FilterEnabled"/> = <c>true</c> is fail-closed
    /// (matches nothing), never fail-open.
    /// </summary>
    Guid? OrgId { get; }

    /// <summary>
    /// <c>true</c> when an authenticated caller is present and tenant scoping must be
    /// enforced. <c>false</c> for anonymous/system contexts (background jobs, design-time,
    /// unit tests) where the filter is disabled and cross-org access is explicit.
    /// </summary>
    bool FilterEnabled { get; }
}

/// <summary>
/// No-op tenant context used outside an authenticated request scope (design-time
/// migrations, background jobs, unit tests). Disables the global query filter.
/// </summary>
public sealed class NullTenantContext : ITenantContext
{
    public static readonly NullTenantContext Instance = new();

    public Guid? OrgId => null;
    public bool FilterEnabled => false;
}
