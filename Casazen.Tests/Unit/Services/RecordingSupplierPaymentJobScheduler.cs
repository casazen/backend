using Casazen.Core.Services;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// An <see cref="ISupplierPaymentJobScheduler"/> that records the supplier orgs whose pending payment requests would have been
/// queued on Hangfire (SP-15b); nothing is scheduled.
/// </summary>
internal sealed class RecordingSupplierPaymentJobScheduler : ISupplierPaymentJobScheduler
{
    private readonly List<Guid> _scheduled = [];

    /// <summary>The supplier orgs scheduled so far, in order.</summary>
    public IReadOnlyList<Guid> Scheduled
    {
        get
        {
            lock (_scheduled)
                return _scheduled.ToList();
        }
    }

    public void SchedulePendingRequests(Guid supplierOrgId)
    {
        lock (_scheduled)
            _scheduled.Add(supplierOrgId);
    }
}
