using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.LongRent;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The rent register of the long-term area (LR-01, B1): the installments of every lease the caller reaches, month by month,
/// with the numbers of the month, and the landlord's reminders. Reads need <c>lease.read</c> and are restricted in SQL to the
/// caller's org and, unless org-wide, to the properties they reach (<see cref="HostScope"/>, TN-3 and AM-03, the scope the lease
/// endpoints use). The reminders need <c>lease.create</c> (the permission that manages a lease after its creation, as the payment request
/// of <c>api/leases/{id}/rent</c>) and each installment is authorized as a <see cref="HostResource"/> of its property: another
/// org's installment is 404, one of a property the caller may not handle 403 (in a bulk reminder: skipped as not found).
/// </summary>
[ApiController]
[Route("api/long-rent/rents")]
[Authorize(Policy = CasazenPolicies.LeaseRead)]
public class LongRentRentsController(
    IRentRegisterService register,
    IRentBillingService rentBilling,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private const string InstallmentNotFoundCode = RentBillingErrorCodes.InstallmentNotFound;
    private const int FirstMonthYear = 2000;
    private const int LastMonthYear = 2100;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The installments whose due date is in <c>month</c> (<c>yyyy-MM</c>, default the current month in Rome), narrowed by
    /// <c>status</c> (<c>All</c>, <c>Paid</c>, <c>Pending</c> or <c>Overdue</c>, default all), <c>pageSize</c> at a time
    /// (default 25, at most 100), ordered by due date then id. <c>counters</c> are the numbers of the month, whatever the
    /// status and the page: expected, collected, still to come and overdue. 400 for a month or a status that is not valid.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(RentRegisterPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RentRegisterPage>> Get(
        [FromQuery] string? month = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = RentRegisterQuery.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        DateOnly firstDay;
        if (string.IsNullOrWhiteSpace(month))
        {
            var today = _clock.TodayInRomeAsDateOnly();
            firstDay = new DateOnly(today.Year, today.Month, 1);
        }
        else if (!TryParseMonth(month, out firstDay))
        {
            return this.ApiProblem(StatusCodes.Status400BadRequest, RentRegisterErrorCodes.MonthInvalid, "RentRegisterMonthInvalid");
        }

        var statusFilter = RentRegisterStatus.All;
        if (!string.IsNullOrWhiteSpace(status) && !EnumNames.TryParseDefined(status, out statusFilter))
            return this.ApiProblem(StatusCodes.Status400BadRequest, RentRegisterErrorCodes.StatusUnknown, "RentRegisterStatusUnknown");

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        var query = new RentRegisterQuery(
            firstDay, statusFilter, Math.Max(1, page), Math.Clamp(pageSize, 1, RentRegisterQuery.MaxPageSize));
        return Ok(await register.GetRegisterAsync(scope, query, cancellationToken));
    }

    /// <summary>
    /// Reminds the tenants of one installment by email, with the optional short <c>note</c> of the landlord (and the payment link
    /// when the org accepts rent online). At most one reminder every <c>RentBilling:ReminderIntervalHours</c> hours (default 24)
    /// for the same installment: 422 <c>rent_reminder_too_soon</c>; 422 <c>rent_no_tenant_email</c>, 422
    /// <c>rent_reminder_not_sent</c> when no email could be queued (nothing is recorded); 409 <c>rent_installment_not_payable</c>
    /// for a paid or cancelled installment, 409 <c>rent_installment_in_flight</c> while a payment is being processed; 404
    /// <c>rent_installment_not_found</c>. The body may be omitted.
    /// </summary>
    [HttpPost("{id:guid}/reminder")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    [ProducesResponseType(typeof(RentReminderResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RentReminderResult>> SendReminder(
        Guid id, [FromBody] RentReminderRequest? request = null, CancellationToken cancellationToken = default)
    {
        var installment = (await register.FindInstallmentsAsync([id], cancellationToken)).SingleOrDefault();
        if (installment is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, InstallmentNotFoundCode, "RentInstallmentNotFound");

        var resource = await hostResources.ForPropertyAsync(installment.PropertyId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, InstallmentNotFoundCode, "RentInstallmentNotFound");
        if (!await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Create))
            return Forbid();

        return Ok(await rentBilling.SendReminderAsync(installment.LeaseId, id, request?.Note, cancellationToken));
    }

    /// <summary>
    /// Reminds the tenants of several installments (one to 50) with the same optional note: the same rules as the single
    /// reminder for each one, applied in the order given. An installment that cannot be reminded (too soon, no tenant address, paid,
    /// in flight, not the caller's) is skipped with its code and never stops the others: the answer is 200 with <c>sent</c> and
    /// <c>skipped</c>. 400 <c>rent_reminder_batch_invalid</c> for no id, more than 50, or an empty id.
    /// </summary>
    [HttpPost("reminders")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    [ProducesResponseType(typeof(RentBulkReminderResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RentBulkReminderResult>> SendReminders(
        [FromBody] RentBulkReminderRequest request, CancellationToken cancellationToken = default)
    {
        var ids = request.InstallmentIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0 || ids.Count > RentCharges.MaxBulkReminders || ids.Contains(Guid.Empty))
            return this.ApiProblem(StatusCodes.Status400BadRequest, RentRegisterErrorCodes.BatchInvalid, "RentReminderBatchInvalid");

        var found = (await register.FindInstallmentsAsync(ids, cancellationToken)).ToDictionary(i => i.InstallmentId);

        // One authorization per property, however many of its installments are in the batch.
        var allowed = new Dictionary<Guid, bool>();
        async Task<bool> MayRemindAsync(Guid propertyId)
        {
            if (allowed.TryGetValue(propertyId, out var known))
                return known;

            var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
            var may = resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Create);
            allowed[propertyId] = may;
            return may;
        }

        var sent = new List<RentReminderResult>();
        var skipped = new List<RentReminderSkipped>();
        foreach (var id in ids)
        {
            if (!found.TryGetValue(id, out var installment) || !await MayRemindAsync(installment.PropertyId))
            {
                // Not there, another org's, or of a property the caller may not handle: all the same to the caller.
                skipped.Add(new RentReminderSkipped(id, InstallmentNotFoundCode));
                continue;
            }

            try
            {
                sent.Add(await rentBilling.SendReminderAsync(installment.LeaseId, id, request.Note, cancellationToken));
            }
            catch (DomainException ex)
            {
                skipped.Add(new RentReminderSkipped(id, ex.Code));
            }
            catch (NotFoundException ex)
            {
                skipped.Add(new RentReminderSkipped(id, ex.Code ?? InstallmentNotFoundCode));
            }
        }

        return Ok(new RentBulkReminderResult(ids.Count, sent, skipped));
    }

    /// <summary><c>yyyy-MM</c> between the years 2000 and 2100, as the first day of the month.</summary>
    private static bool TryParseMonth(string value, out DateOnly firstDay)
    {
        if (DateOnly.TryParseExact($"{value.Trim()}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out firstDay)
            && firstDay.Year is >= FirstMonthYear and <= LastMonthYear)
        {
            return true;
        }

        firstDay = default;
        return false;
    }

    private async Task<HostScope?> GetHostScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
