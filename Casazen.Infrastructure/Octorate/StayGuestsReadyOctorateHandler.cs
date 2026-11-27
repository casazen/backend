using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The guests of a stay reach Octorate when their data is ready, with no one confirming it (E20/F01/US01/T01, decision O1): the consumer, on the event
/// bus, of the two facts that can make a stay ready, the guests saved (<see cref="DomainEventType.StayGuestsChanged"/>, by the guest on the check-in
/// page or by the host) and the identity of the guest verified (<see cref="DomainEventType.IdVerificationCompleted"/>, by the agent or an operator). It
/// asks <see cref="IStayGuestsAutoSendService"/> to do what the org allows.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Setting off, nothing changes.</b> An org that did not ask (the default) gets no send from here; the wizard's confirmation is its only way.</item>
/// <item><b>Idempotent.</b> The handler reads the stay as it is now, never what the event says, so an event delivered again, or out of order, finds the
/// same answer: the send path keeps the hash of what it sent and sends nothing twice. There is no state to wait for in an event: when the stay is not
/// ready yet, the fact that makes it ready brings its own event.</item>
/// <item><b>Failures.</b> An unexpected error (the database) is not caught: the dispatcher delivers the event again. So is a send of the same stay
/// that holds the lock (<see cref="StayGuestsSendBusyException"/>): what the event brought may not be in that send, so the event is never consumed with
/// no effect. A refusal or a failure of Octorate is kept on the links of the guests by the send path, and the retry job takes it again.</item>
/// <item><b>The org is the event's</b> (ADR-022-R6): a stay of another org named by the event is never touched. Own registration:
/// <c>AddCasazenOctorateGuestAutoSend</c>.</item>
/// </list>
/// </remarks>
public sealed class StayGuestsReadyOctorateHandler(
    IStayGuestsAutoSendService autoSend,
    ILogger<StayGuestsReadyOctorateHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.Type is not (DomainEventType.StayGuestsChanged or DomainEventType.IdVerificationCompleted) || domainEvent.EntityId == Guid.Empty)
            return;

        var result = await autoSend.TrySendAsync(domainEvent.OrgId, domainEvent.EntityId, ct);
        logger.LogInformation(
            "Event {EventType} of booking {BookingId} of org {OrgId}: guests to Octorate on their own {Outcome}",
            domainEvent.Type, domainEvent.EntityId, domainEvent.OrgId, result.Outcome);
    }
}
