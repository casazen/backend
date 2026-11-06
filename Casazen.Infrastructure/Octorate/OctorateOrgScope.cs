using Casazen.Core.Multitenancy;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IOctorateOrgScope"/> of one DI scope (ADR-023-R4): the org opened with <see cref="Use"/>, otherwise the org of the
/// authenticated request (<see cref="ITenantContext"/>, also the org an event handler runs under), otherwise none and
/// <see cref="OrgId"/> throws. Not shared between threads: a scope makes its calls one after the other.
/// </summary>
public sealed class OctorateOrgScope(ITenantContext tenantContext) : IOctorateOrgScope
{
    private Guid? _used;

    public Guid OrgId =>
        _used
        ?? RequestOrg()
        ?? throw new InvalidOperationException(
            "An Octorate call needs an organization: a job or a handler opens IOctorateOrgScope.Use(orgId) first.");

    public IDisposable Use(Guid orgId)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException("An Octorate call is made for an organization.", nameof(orgId));

        // Inside an authenticated request the calls are the request's org's: never another one, also by mistake.
        if (tenantContext.FilterEnabled && tenantContext.OrgId != orgId)
            throw new InvalidOperationException("An authenticated request cannot make Octorate calls for another organization.");

        var previous = _used;
        _used = orgId;
        return new Restore(this, previous);
    }

    private Guid? RequestOrg() =>
        tenantContext.FilterEnabled && tenantContext.OrgId is { } orgId && orgId != Guid.Empty ? orgId : null;

    private sealed class Restore(OctorateOrgScope scope, Guid? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            scope._used = previous;
        }
    }
}
