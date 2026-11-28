using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public sealed class LoggingSupplierDisputePenalties(ILogger<LoggingSupplierDisputePenalties> logger) : ISupplierDisputePenalties
{
    public Task RecordAsync(SupplierDisputePenalty penalty, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(penalty);
        logger.LogInformation(
            "Supplier {SupplierOrgId}: penalty {Kind} for dispute {DisputeId} of request {RequestId} at {At:O}; the ranking does not use it yet",
            penalty.SupplierOrgId, penalty.Kind, penalty.DisputeId, penalty.ServiceRequestId, penalty.At);
        return Task.CompletedTask;
    }
}
