using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The invoice data of a booking names a company (E18/F01/US03/T01, ⚖ I7, NF-117): the consumer of <c>InvoiceDataSaved</c> and of <c>BookingConfirmed</c> that raises the
/// «Da fare» of the stay of a company tenant in the Italian property of a private owner, so a person decides, with the two readings in plain words, whether the stay stays a
/// short let (<see cref="IOwnerTaxChecks.RaiseCompanyTenantDecisionAsync"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Read from the database, not from the event</b>: the event carries the booking and nothing personal (ADR-022-R21); the kind of the recipient, the status and the
/// owners of the property are read when the event is handled, in the org of the event (ADR-022-R6). The order of the events does not matter: what is judged is the state now,
/// so the two events cover both orders (the data saved while the booking is not yet confirmed raises nothing, the confirmation that follows does; a confirmation that comes
/// first finds no company and raises nothing, the data that follows does). A stay that becomes a case in another way (an owner added to the property later) is not waited for:
/// <c>GET api/owner-tax-decisions</c> reads the state on request and lists it.</item>
/// <item><b>Once for each booking</b>: a redelivered event, a second save of the data and a decision already recorded raise nothing more (ADR-022-R7).</item>
/// <item><b>Only while <c>Features:TaxReportingExports</c> is on</b>: the item is a warning of the UI. A failure (the database) is not swallowed: the outbox delivers the event
/// again. A stay that is no case, or a property gone, is not a failure and ends here.</item>
/// <item>It runs by the outbox dispatcher, so it can never fail the save of the invoice data of a guest or of a host.</item>
/// </list>
/// </remarks>
public sealed class CompanyTenantDecisionHandler(IOwnerTaxChecks taxChecks) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // About the booking (EntityId), for the two events: the data saved, and the stay that becomes active after the data was saved.
        if (domainEvent.Type is not (DomainEventType.InvoiceDataSaved or DomainEventType.BookingConfirmed) || domainEvent.EntityId == Guid.Empty)
            return;

        await taxChecks.RaiseCompanyTenantDecisionAsync(domainEvent.OrgId, domainEvent.EntityId, ct);
    }
}
