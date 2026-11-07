using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Optional bank link of the signed-in organisation (the landlord), through Stripe Financial Connections on the existing
/// Stripe account. The manual bank transfer is unchanged. Starting a payment does not mark a due paid.
/// </summary>
[ApiController]
[Route("api/orgs/me/bank-links")]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public class OrgBankLinkController(IOrgContextResolver orgContextResolver, IBankLinkService bankLinks) : ControllerBase
{
    /// <summary>Financial Connections session for the org's Stripe customer. The browser completes the link with the client secret.</summary>
    [HttpPost("sessions")]
    [ProducesResponseType(typeof(BankLinkSession), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankLinkSession>> CreateSession(CancellationToken cancellationToken)
    {
        if (await OrgIdAsync(cancellationToken) is not Guid orgId)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");
        return Ok(await bankLinks.CreateOrgSessionAsync(orgId, cancellationToken));
    }

    /// <summary>The linked account id, if any. No IBAN.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(BankLinkAccount), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankLinkAccount>> Get(CancellationToken cancellationToken)
    {
        if (await OrgIdAsync(cancellationToken) is not Guid orgId)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");
        return Ok(await bankLinks.GetOrgAccountAsync(orgId, cancellationToken));
    }

    /// <summary>Stores the Financial Connections account id (<c>fca_…</c>) after the browser flow, once Stripe says it belongs to this org.</summary>
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

/// <summary>The Financial Connections account id returned to the browser. Not an IBAN.</summary>
public sealed class AttachBankLinkDto
{
    [Required(ErrorMessage = "BankLinkAccountIdInvalid")]
    [MaxLength(255, ErrorMessage = "BankLinkAccountIdInvalid")]
    public string AccountId { get; set; } = string.Empty;
}

/// <summary>A transfer from the linked account. Leave the installment empty to start one that is not a rent due.</summary>
public sealed class StartBankLinkPaymentDto
{
    [Range(1, 100_000_000, ErrorMessage = "BankLinkAmountInvalid")]
    public long? AmountCents { get; set; }

    [MaxLength(3, ErrorMessage = "BankLinkCurrencyInvalid")]
    public string? Currency { get; set; }

    public Guid? LeaseId { get; set; }

    public Guid? InstallmentId { get; set; }

    /// <summary>With <see cref="CondominiumInstallmentId"/>, the landlord pays that condominium rate. Not a contract field.</summary>
    public Guid? CondominiumId { get; set; }

    public Guid? CondominiumInstallmentId { get; set; }
}
