using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// A payment of a mirrored booking that became paid is recorded on its Octorate reservation once, as income ("not a card charge", D9,
/// E08/F01/US01/T04, AC-NF74-4): the consumer of <c>PaymentSucceeded</c>, recorded with the status change by the payment webhook (Stripe), the
/// deferred charge and "Registra incasso" (cash, bank transfer). The work is <see cref="IBookingOctorateSyncService.PushPaymentAsync"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Once.</b> A payment already recorded (<c>Payment.OctorateTransactionId</c>, unique in the org) is skipped, so a delivered-again event
/// records nothing; a registration whose answer was lost is found on the reservation before it is sent again.</item>
/// <item><b>Never lost.</b> A payment that arrives while the reservation is being created makes the event be delivered again; the create records
/// the payments paid before it succeeded.</item>
/// <item><b>Never on the channel's money.</b> A booking of a channel, not mirrored, or on a write-blocked property: nothing.</item>
/// <item>Nothing while <c>Features:OctorateConnector</c> is off; refunds are not mirrored (a known limit of NF-74).</item>
/// </list>
/// </remarks>
public sealed class OctoratePaymentHandler(IBookingOctorateSyncService sync, IFeatureFlags featureFlags) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type != DomainEventType.PaymentSucceeded || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return;

        await sync.PushPaymentAsync(domainEvent.EntityId, ct);
    }
}
