using Casazen.Core.Services;

namespace Casazen.Tests.Unit.Services;

internal sealed class RecordingSupplierPaymentJobScheduler : ISupplierPaymentJobScheduler
{
    private readonly List<Guid> _scheduled = [];

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
