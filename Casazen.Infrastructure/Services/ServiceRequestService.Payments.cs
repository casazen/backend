using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Suppliers;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The payment side of the service request lifecycle (SP-15a): the host's confirmation of a final amount above the quote, and the
/// supplier's request and offline record of the payment. Each operation checks who may do it and in which state the request is,
/// then hands the money to <see cref="ISupplierPaymentService"/>, which owns the payment rows, the lock and Stripe.
/// </summary>
public partial class ServiceRequestService
{
    public async Task<ServiceRequest> ConfirmFinalAmountAsync(
        Guid id,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestOfHostOrThrow(id, hostOrgId, cancellationToken);

        if (request is not { Status: ServiceRequestStatus.Completato, FinalAmountNeedsConfirmation: true })
        {
            // Confirming twice (a double click, a second tab) is not an error: the amount is confirmed, and that is what the host asked.
            if (request is { Status: ServiceRequestStatus.Completato, FinalAmountConfirmedAt: not null })
                return request;

            throw new DomainRuleException(ServicePaymentErrors.NoConfirmationNeeded, ServicePaymentErrors.NoConfirmationNeededMessageKey);
        }

        // The confirmation is what lets a request paid inside CasaZen get its payment: the same decision as at the completion,
        // now with nothing left to confirm.
        var lines = ServiceRequestJson.ReadPriceLines(request.PriceLinesJson);
        var plan = await payments.PlanAsync(request, request.FinalAmountCents, lines, needsConfirmation: false, cancellationToken);

        try
        {
            request.FinalAmountNeedsConfirmation = false;
            request.FinalAmountConfirmedAt = Now();
            request.PaymentMode = plan.Mode;
            request.UpdatedAt = Now();
            await SaveAsync(request, "final amount confirmed", cancellationToken);
        }
        catch
        {
            payments.Discard(plan);
            throw;
        }

        logger.LogInformation(
            "ServiceRequest {Id}: the host confirmed the final amount (payment mode {PaymentMode})", request.Id, request.PaymentMode);

        await payments.AnnounceAsync(plan, request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequestPayment> RequestPaymentAsync(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        // A suspended supplier, or one that has not accepted the current Terms, performs no action (SU-12, SU-05).
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
        return await payments.RequestPaymentAsync(request, cancellationToken);
    }

    public async Task<ServiceRequestPayment> RecordOfflinePaymentAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
        return await payments.RecordOfflineAsync(request, userId, reason, cancellationToken);
    }
}
