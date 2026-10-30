using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The sink of the penalty of a lost dispute until the reliability of the suppliers is connected to the ranking (SP-15c-c, decisions VT-D16 and VT-D17): it only
/// writes a log line with ids and the kind of the penalty. Nothing changes in the ranking and nothing is shown to the public, which is what VT-D17 asks until
/// the weights are approved. The dispute row (<c>ServicePaymentDisputes</c>: <c>ResolvedForCustomer</c>, with <c>NoShow</c>) is the record from which the events
/// are rebuilt when the real sink replaces this one (follow-up SP-15c-c2, after the reliability events of SP-REL are merged).
/// </summary>
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
