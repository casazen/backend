using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The document sent to the guest as soon as it is issued, when the issuer asked for it (E12/F01/US02/T01, NF-83, ADR-022-R7): the consumer of <see cref="DomainEventType.FiscalDocumentIssued"/> and
/// <see cref="DomainEventType.FiscalDocumentCreditNoteIssued"/> asks <see cref="IFiscalDocumentDeliveryService.SendAutomaticallyAsync"/>, which sends the e-mail with the link only when the profile has
/// «e-mail to the guest» on (the step «Emissione» of the billing setup). The org is the event's (ADR-022-R6).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Every way of issuing.</b> The host's confirmation, the bulk issue, the daily run and the corrections an event makes all record the same event, so the guest is told whatever way the document
/// was issued. The e-mail of the credit note of a correction goes too: the guest who got the invoice gets what corrects it.</item>
/// <item><b>Never the issue's problem.</b> The issue committed before the event was delivered; whatever goes wrong here never touches it. An answer (the guest has no address, the document is not a guest
/// document, the environment has no e-mail provider or no public address of the web app, a refusal of the rules) is logged with its ids and not delivered again: the document stays in CasaZen and the host
/// sends it from its booking. A queue that refuses while it could send (<c>document_email_not_sent</c>) and any other failure (the database) are failures of the send: logged with the code or the type of the
/// error and ids, and thrown again, so the outbox delivers the event again (nothing was queued, so nothing is sent twice).</item>
/// <item><b>At least once.</b> The event is delivered again when another consumer of it fails, or when the dispatcher stops before it saves. The second delivery does not e-mail the guest again:
/// <see cref="IFiscalDocumentDeliveryService.SendAutomaticallyAsync"/> remembers in memory, for an hour, the documents whose e-mail it queued (<see cref="FiscalDocumentSendOutcome.AlreadyQueued"/>); no state of ours
/// says «sent» (no column, no migration). The limit: another replica, or a restart within the hour, does not know it, and the guest may then get a second e-mail, with a link of its own and as harmless as the first.</item>
/// <item>Nothing while <c>Features:FiscalDocumentIssuance</c> is off. No log carries a value of the guest or of the document.</item>
/// </list>
/// </remarks>
public sealed class FiscalDocumentEmailHandler(
    IFiscalDocumentDeliveryService delivery,
    IFeatureFlags featureFlags,
    ILogger<FiscalDocumentEmailHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type is not (DomainEventType.FiscalDocumentIssued or DomainEventType.FiscalDocumentCreditNoteIssued) || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
            return;

        var documentId = domainEvent.EntityId;
        try
        {
            var outcome = await delivery.SendAutomaticallyAsync(documentId, ct);
            logger.LogInformation(
                "Event {EventId} ({EventType}): fiscal document {DocumentId} of org {OrgId}, e-mail to the guest: {Outcome}",
                domainEvent.Id,
                domainEvent.Type,
                documentId,
                domainEvent.OrgId,
                outcome);
        }
        catch (NotFoundException)
        {
            logger.LogWarning("Event {EventId}: fiscal document {DocumentId} of org {OrgId} not found, nothing to send", domainEvent.Id, documentId, domainEvent.OrgId);
        }
        catch (DomainException ex) when (ex.Code != FiscalDocumentDeliveryErrors.EmailNotSent)
        {
            logger.LogWarning(
                "Event {EventId} ({EventType}): fiscal document {DocumentId} of org {OrgId} not sent to the guest ({Code}): the host can send it from the booking",
                domainEvent.Id,
                domainEvent.Type,
                documentId,
                domainEvent.OrgId,
                ex.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A queue that refused (document_email_not_sent) is here too: nothing was queued, so the event is delivered again without sending anything twice.
            logger.LogError(
                "Event {EventId} ({EventType}): fiscal document {DocumentId} of org {OrgId} could not be sent to the guest ({Error}): the event is delivered again",
                domainEvent.Id,
                domainEvent.Type,
                documentId,
                domainEvent.OrgId,
                ex is DomainException domain ? domain.Code : ex.GetType().Name);
            throw;
        }
    }
}
