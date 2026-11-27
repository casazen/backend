using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Casazen.Web.Controllers;

public partial class CondominiumsController
{
    [HttpGet("manual-payments")]
    public async Task<IActionResult> ManualPayments([FromServices] CondominiumPaymentReminderService reminders, CancellationToken ct)
    {
        var (scope, _) = await ScopeAsync(ct);
        return scope is null ? Unauthorized() : Ok(await reminders.ListAsync(scope, ct));
    }
    [HttpPost("manual-payments/{installmentId:guid}/mark-paid")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<IActionResult> MarkManualPayment(Guid installmentId, [FromServices] CondominiumPaymentReminderService reminders, CancellationToken ct)
    {
        var (scope, user) = await ScopeAsync(ct);
        if (scope is null || user is null) return Unauthorized();
        await reminders.MarkPaidAsync(scope, installmentId, user, ct);
        return NoContent();
    }
}
