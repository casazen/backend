using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/orgs/me/bank-links")]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public class OrgBankLinkController(IOrgContextResolver orgContextResolver, IBankLinkService bankLinks) : ControllerBase
{
    [HttpPost("sessions")]
    [ProducesResponseType(typeof(BankLinkSession), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankLinkSession>> CreateSession(CancellationToken cancellationToken)
    {
        if (await OrgIdAsync(cancellationToken) is not Guid orgId)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");
        return Ok(await bankLinks.CreateOrgSessionAsync(orgId, cancellationToken));
    }

    [HttpGet]
    [ProducesResponseType(typeof(BankLinkAccount), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankLinkAccount>> Get(CancellationToken cancellationToken)
    {
        if (await OrgIdAsync(cancellationToken) is not Guid orgId)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");
        return Ok(await bankLinks.GetOrgAccountAsync(orgId, cancellationToken));
    }

    [HttpPost]
    [ProducesResponseType(typeof(BankLinkAccount), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankLinkAccount>> Attach(
        [FromBody] AttachBankLinkDto body, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        if (await OrgIdAsync(cancellationToken) is not Guid orgId)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");
        return Ok(await bankLinks.AttachOrgAccountAsync(orgId, body.AccountId, cancellationToken));
    }

    /// <summary>
    /// Starts the debit Stripe supports for the linked account. A rent installment is left in flight and is paid only
    /// when Stripe confirms, or by the existing offline declaration. A condominium installment is the landlord paying
    /// the condominium (SEPA, euros, the Italian bank already linked); it is not a contract or tenant payment. An
    /// ad-hoc debit (no installment) requires the <c>Idempotency-Key</c> header: a retry with that key is the same PaymentIntent.
    /// </summary>
    [HttpPost("payments")]
    [ProducesResponseType(typeof(BankLinkPaymentStarted), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankLinkPaymentStarted>> StartPayment(
        [FromBody] StartBankLinkPaymentDto body, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        if (await OrgIdAsync(cancellationToken) is not Guid orgId)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var started = await bankLinks.StartOrgPaymentAsync(
            orgId,
            new StartBankLinkPayment(
                body.AmountCents,
                body.Currency,
                body.LeaseId,
                body.InstallmentId,
                IdempotencyKey(),
                body.CondominiumId,
                body.CondominiumInstallmentId),
            Caller(),
            cancellationToken);
        return Ok(started);
    }

    private async Task<Guid?> OrgIdAsync(CancellationToken cancellationToken) =>
        await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);

    private BankLinkCaller Caller() =>
        new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());

    private string? IdempotencyKey()
    {
        var key = Request.Headers["Idempotency-Key"].ToString();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }
}

public sealed class AttachBankLinkDto
{
    [Required(ErrorMessage = "BankLinkAccountIdInvalid")]
    [MaxLength(255, ErrorMessage = "BankLinkAccountIdInvalid")]
    public string AccountId { get; set; } = string.Empty;
}

public sealed class StartBankLinkPaymentDto
{
    [Range(1, 100_000_000, ErrorMessage = "BankLinkAmountInvalid")]
    public long? AmountCents { get; set; }

    [MaxLength(3, ErrorMessage = "BankLinkCurrencyInvalid")]
    public string? Currency { get; set; }

    public Guid? LeaseId { get; set; }

    public Guid? InstallmentId { get; set; }

    public Guid? CondominiumId { get; set; }

    public Guid? CondominiumInstallmentId { get; set; }
}
