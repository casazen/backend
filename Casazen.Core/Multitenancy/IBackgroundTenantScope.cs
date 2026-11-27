namespace Casazen.Core.Multitenancy;

/// <summary>
/// Puts a dependency-injection scope opened outside an HTTP request under the tenant of one org (ADR-022-R6, R17: each handler of
/// the event bus runs in its own scope under the tenant context of its event's org). The scope's <see cref="ITenantContext"/> then
/// answers that org and turns the tenant query filter on for it, so a read of a tenant-owned table that forgets its org predicate
/// still sees that org only.
/// </summary>
/// <remarks>Implemented by the tenant context of the web layer, the only writer of the tenant of a scope.</remarks>
public interface IBackgroundTenantScope
{
    /// <summary>
    /// Scopes the current DI scope to <paramref name="orgId"/>. Once per scope: another org is refused.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="orgId"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The scope already belongs to another org.</exception>
    void EnterOrg(Guid orgId);
}
