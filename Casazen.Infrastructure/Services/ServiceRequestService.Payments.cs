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
    /// <summary>
    /// How a request is paid from now on, decided when it is taken. A request from a supplier's public showcase (SP-10) is always
    /// <see cref="ServiceRequestPaymentMode.Manual"/>: its customer is a private person with no account, no org and no address on file
    /// for a payment link, and its <c>OrgId</c> is the supplier's own, so a payment request would reach the supplier itself. The
    /// supplier records the payment it received outside CasaZen (<c>payment/offline</c>); paying a showcase customer online is a
    /// decision of its own and is not made here.
    /// </summary>
    private async Task<ServiceRequestPaymentMode> ResolvePaymentModeAsync(ServiceRequest request, CancellationToken cancellationToken) =>
        request.RentalContext == ServiceRequestRentalContext.Showcase
            ? ServiceRequestPaymentMode.Manual
            : await payments.ResolveModeAsync(request.SupplierOrgId, cancellationToken);

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
