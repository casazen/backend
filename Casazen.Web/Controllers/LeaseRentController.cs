using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Recurring rent of a lease (LT-06, #269, audit A7-07): the schedule generated from the lease, its installments with their
/// real state, the payment link emailed to the tenants, offline payments declared by the landlord. Reads need
/// <c>lease.read</c> on the lease; every change needs <c>lease.create</c> (the permission that manages a lease after its
/// creation, as for the erasure request). The lease is authorized as a <see cref="HostResource"/> of its property (TN-3):
/// another org's lease is 404, a lease the caller may not handle 403.
/// </summary>
[ApiController]
[Route("api/leases/{leaseId:guid}/rent")]
[Authorize(Policy = CasazenPolicies.LeaseRead)]
public class LeaseRentController(
    IRentBillingService rentBilling,
    ILeaseWorkflowService leaseService,
    IAuthorizationService authorizationService) : ControllerBase
{
    private const string LeaseNotFoundCode = "lease_not_found";

    /// <summary>Schedule, installments (state, overdue, how paid) and what the landlord can do.</summary>
    [HttpGet]
    public async Task<ActionResult<RentLedgerView>> Get(Guid leaseId, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(leaseId, LeaseOperations.Read) is { } denied)
            return denied;
        return Ok(await rentBilling.GetLedgerAsync(leaseId, cancellationToken));
    }

    /// <summary>
    /// Sets up (or changes) the schedule and generates the installments. 422 <c>rent_lease_not_signed</c> before the full
    /// signature, <c>rent_billing_day_invalid</c>, <c>rent_amount_invalid</c>, <c>rent_cadence_locked</c> (the cadence
    /// cannot change once an installment was paid or is being paid).
    /// </summary>
    [HttpPut("schedule")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    public async Task<ActionResult<RentLedgerView>> Configure(
        Guid leaseId, [FromBody] ConfigureRentScheduleDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(leaseId, LeaseOperations.Create) is { } denied)
            return denied;
        return Ok(await rentBilling.ConfigureScheduleAsync(
            leaseId,
            new ConfigureRentScheduleRequest(dto.Cadence!.Value, dto.BillingDayOfMonth, dto.Amount),
            cancellationToken));
    }

    /// <summary>Stops the schedule: unpaid installments are cancelled (their payable PaymentIntents canceled on Stripe).</summary>
    [HttpPost("schedule/disable")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    public async Task<ActionResult<RentLedgerView>> Disable(Guid leaseId, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(leaseId, LeaseOperations.Create) is { } denied)
            return denied;
        return Ok(await rentBilling.DisableScheduleAsync(leaseId, cancellationToken));
    }

    /// <summary>
    /// Declares an installment paid outside CasaZen (bank transfer, cash). 409 <c>rent_installment_not_payable</c> when it
    /// is already paid (also online meanwhile) or cancelled, 409 <c>rent_installment_in_flight</c> while an online payment
    /// is being processed, 422 <c>rent_paid_on_invalid</c> for a date in the future.
    /// </summary>
    [HttpPost("installments/{installmentId:guid}/mark-paid")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    public async Task<ActionResult<RentInstallmentView>> MarkPaid(
        Guid leaseId, Guid installmentId, [FromBody] MarkRentPaidDto dto, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return Unauthorized();
        if (await AuthorizeLeaseAsync(leaseId, LeaseOperations.Create) is { } denied)
            return denied;
        return Ok(await rentBilling.MarkPaidOfflineAsync(
            leaseId, installmentId, new MarkRentPaidOfflineRequest(dto.PaidOn!.Value, dto.Note), userId, cancellationToken));
    }

    /// <summary>
    /// Emails the payment link of an installment to the tenants now (a new link; the previous one stops working). 422
    /// <c>rent_online_payments_unavailable</c> without a Stripe account able to accept charges, <c>rent_no_tenant_email</c>,
    /// <c>rent_payment_request_not_sent</c> when no email could be queued.
    /// </summary>
    [HttpPost("installments/{installmentId:guid}/payment-request")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    public async Task<ActionResult<RentInstallmentView>> SendPaymentRequest(
        Guid leaseId, Guid installmentId, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(leaseId, LeaseOperations.Create) is { } denied)
            return denied;
        return Ok(await rentBilling.SendPaymentRequestAsync(leaseId, installmentId, cancellationToken));
    }

    private async Task<ActionResult?> AuthorizeLeaseAsync(Guid leaseId, HostOperationRequirement operation)
    {
        var lease = await leaseService.GetLeaseDetailAsync(leaseId);
        if (lease is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound");

        // Without its property the owner is unknown: fail closed rather than treat the lease as an org-level row.
        if (lease.Property is null)
            return Forbid();

        var resource = HostResource.ForProperty(lease.Property) with { OrgId = lease.OrgId };
        return await authorizationService.IsAuthorizedAsync(User, resource, operation) ? null : Forbid();
    }
}

/// <summary>Rent schedule chosen by the landlord: cadence, due day (1-28, default the start day) and amount (default monthly rent × months).</summary>
public sealed class ConfigureRentScheduleDto
{
    [Required(ErrorMessage = "RentCadenceRequired")]
    [EnumDataType(typeof(RentCadence), ErrorMessage = "RentCadenceRequired")]
    public RentCadence? Cadence { get; set; }

    [Range(1, 28, ErrorMessage = "RentBillingDayRange")]
    public int? BillingDayOfMonth { get; set; }

    [Range(typeof(decimal), "0.01", "1000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "RentAmountInvalid")]
    public decimal? Amount { get; set; }
}

/// <summary>Offline payment of an installment: the day it was paid (not in the future) and an optional note.</summary>
public sealed class MarkRentPaidDto
{
    [Required(ErrorMessage = "RentPaidOnInvalid")]
    public DateOnly? PaidOn { get; set; }

    [MaxLength(500, ErrorMessage = "RentPaidOnInvalid")]
    public string? Note { get; set; }
}
