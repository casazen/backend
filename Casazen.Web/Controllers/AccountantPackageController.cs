using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The package of the accountant and the summary of the agency (E15/F01/US01/T01, NF-101, NF-87, NF-115): <c>GET api/accountant-package?period=2026-10</c> (or <c>?period=2026</c>)
/// streams one ZIP, <c>casazen-commercialista-2026-10.zip</c>, with a fixed structure (the documents of the guests, the invoices to the owners, the statements, the agency summary, the
/// VAT, the withholding, the tourist tax, the collections and <c>indice.csv</c>), and <c>GET api/accountant-package/agency-summary?period=</c> answers the summary of the ZIP as JSON
/// for the screen: the same figures, because both are made from the same <see cref="AgencySummary"/>.
/// </summary>
/// <remarks>
/// Behind <c>Features:AccountantPackage</c>: every endpoint answers 404 while it is off, before authentication. The class asks <c>payment.read</c> (the owner, the administrators, the
/// property managers and the accountant hold it), as the reports do, and everything works inside the org of the caller and the reach of its <see cref="HostScope"/>: the documents of
/// the guests are those of the properties it reaches. The owner accounting (the statements, the invoices to the owners, the withholding and the owner parts of the summary) is read only
/// with <c>agency.statement.view</c> and a scope that reaches the whole org (<see cref="AccountantPackageAccess"/>): otherwise those folders are left out of the ZIP and
/// <c>indice.csv</c> lists them as omitted, and the owner fields of the summary are empty. <c>period</c> is <c>yyyy-MM</c> or <c>yyyy</c> (400 <c>accountant_package_invalid_period</c>
/// for anything else). Beyond the caps of the document archive (<c>DocumentArchive:MaxExportFiles</c>, <c>DocumentArchive:MaxExportBytes</c>) the ZIP is refused with 422
/// <c>accountant_package_too_large</c>, with the number of files found, before the first byte.
/// <para>The settings of the monthly send to the accountant (E15/F01/US02/T01, NF-101): <c>GET api/accountant-package/settings</c> with <c>payment.read</c> and <c>PUT</c> with <c>property.write</c> on top, both
/// only for a caller whose scope reaches the whole org: the address of the accountant, the day and the folders are the organization's, never a part of it, and a caller restricted to some properties (a collaborator «Solo
/// alcuni», an account outside the org team) is refused (403) before anything is read or written, even with the permissions. The monthly e-mail with the signed link, and «send a test»
/// (<c>POST api/accountant-package/send</c>), are E15/F01/US02/T02; the link is opened by <see cref="PublicAccountantPackageController"/>.</para>
/// </remarks>
[ApiController]
[Route("api/accountant-package")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
[FeatureGate(FeatureFlags.AccountantPackage)]
public class AccountantPackageController(
    IAccountantPackageService packages,
    IAgencySummaryService summaries,
    IAccountantPackageSettingsService settings,
    IAccountantPackageDeliveryService delivery,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IAuthorizationService authorizationService,
    ILogger<AccountantPackageController> logger) : ControllerBase
{
    /// <summary>
    /// The ZIP of a month or a year, <c>application/zip</c>, streamed to the response. <c>Cache-Control: no-store</c> (it is money and personal data, built on request, never stored). The
    /// headers of the ZIP are set when the first byte goes out, and only for a 200, so a 422 is a plain problem that carries no name of a file to save.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Download([FromQuery] string? period, CancellationToken cancellationToken)
    {
        if (!AccountantPackagePeriod.TryParse(period, out var parsed))
            return this.ApiProblem(StatusCodes.Status400BadRequest, AccountantPackageErrors.InvalidPeriod, "AccountantPackageInvalidPeriod");
        if (await ResolveCallerAsync(cancellationToken) is not { } caller)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        Response.Headers.CacheControl = "private, no-store";
        ZipDownloadHeaders.WhenTheResponseStarts(Response, parsed.FileName);
        var result = await packages.BuildAsync(caller.Scope, caller.Access, parsed, Response.Body, cancellationToken);

        logger.LogInformation(
            "Accountant package downloaded. OrgId={OrgId} UserId={UserId} Documents={Documents} Files={Files} Bytes={Bytes} OmittedFolders={OmittedFolders}",
            caller.Scope.OrgId,
            caller.UserId,
            result.Documents,
            result.Files,
            result.Bytes,
            result.OmittedFolders.Count);
        return new EmptyResult();
    }

    /// <summary>
    /// The summary of the agency for the period as JSON (<see cref="AgencySummaryDto"/>): occupancy and revenue, collections, the documents issued to the guests for each issuer, the costs
    /// of the channels, the tourist tax and, for a caller who may read the owner accounting, the commissions, the invoices to the owners, the payouts, the wires, the margin and the report
    /// by owner. The very figures of <c>riepilogo-agenzia/</c> in the ZIP. For a year it is the annual summary of the commissions.
    /// </summary>
    [HttpGet("agency-summary")]
    [ProducesResponseType(typeof(AgencySummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAgencySummary([FromQuery] string? period, CancellationToken cancellationToken)
    {
        if (!AccountantPackagePeriod.TryParse(period, out var parsed))
            return this.ApiProblem(StatusCodes.Status400BadRequest, AccountantPackageErrors.InvalidPeriod, "AccountantPackageInvalidPeriod");
        if (await ResolveCallerAsync(cancellationToken) is not { } caller)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var summary = await summaries.GetAsync(caller.Scope, caller.Access, parsed, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(AgencySummaryDto.Of(summary));
    }

    /// <summary>
    /// How the package is sent to the accountant (<see cref="AccountantPackageSettingsDto"/>): the address, the monthly send, its day and its folders and what the last send ended in; the defaults for an org that saved
    /// none. <c>payment.read</c>, and a scope that reaches the whole org (403 otherwise, before anything is read). <c>Cache-Control: private, no-store</c>: it holds the address of a third party.
    /// </summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(AccountantPackageSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        if (await WholeOrgAsync(cancellationToken) is not { } orgId)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var view = await settings.GetAsync(orgId, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(AccountantPackageSettingsDto.From(view));
    }

    /// <summary>
    /// Saves all the settings of the send (<see cref="SaveAccountantPackageSettingsRequest"/>) and answers them as they are. <c>property.write</c> on top of <c>payment.read</c>, and a scope that reaches the whole org: a
    /// caller restricted to some properties cannot save, even with the permission (403, nothing saved). 422 <c>accountant_package_settings_invalid</c> (a field is missing), <c>accountant_package_recipient_invalid</c>,
    /// <c>accountant_package_recipient_missing</c> (the send on with no address), <c>accountant_package_send_day_invalid</c>, <c>accountant_package_contents_invalid</c>; a refused save changes nothing.
    /// </summary>
    [HttpPut("settings")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(AccountantPackageSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SaveSettings([FromBody] SaveAccountantPackageSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await WholeOrgAsync(cancellationToken) is not { } orgId)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var saved = await settings.SaveAsync(
            orgId,
            new AccountantPackageSettingsChange(request.Enabled, request.Email, request.SendDay, request.Contents),
            User.GetUserId(),
            cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(AccountantPackageSettingsDto.From(saved));
    }

    /// <summary>
    /// «Send a test» (E15/F01/US02/T02): the package of the previous month, with the folders of the settings, is made and kept in the private storage, and the e-mail with its signed link (30 days) is queued for the address
    /// saved in the settings: whatever the day, and whether the monthly send is on. It is the send of that month, so the monthly run does not send it again. <c>property.write</c> on top of <c>payment.read</c>, a scope that
    /// reaches the whole org, and a limit per person (<see cref="RateLimitPolicies.AccountantPackageSend"/>). The owner accounting is in the package only when the caller holds <c>agency.statement.view</c>: a person never
    /// shares with a third party what it may not read. 422 <c>accountant_package_recipient_missing</c> (no address saved), <c>accountant_package_too_large</c> and <c>accountant_package_not_sent</c> (the package could not
    /// be stored or the e-mail not queued: nothing was sent, and the answer never says it was). The answer holds the month and the expiry of the link, never the address or the link.
    /// </summary>
    [HttpPost("send")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [EnableRateLimiting(RateLimitPolicies.AccountantPackageSend)]
    [ProducesResponseType(typeof(AccountantPackageEmailQueuedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Send(CancellationToken cancellationToken)
    {
        if (await WholeOrgAsync(cancellationToken) is not { } orgId)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var ownerAccounting = (await authorizationService.AuthorizeAsync(User, CasazenPolicies.AgencyStatementView)).Succeeded;
        var sent = await delivery.SendNowAsync(orgId, new AccountantPackageAccess(ownerAccounting), cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(AccountantPackageEmailQueuedDto.From(sent));
    }

    /// <summary>The org of the caller, only when its reach is the whole org: the settings of the send are the organization's, never a part of it (null without an org, a scope or the whole reach).</summary>
    private async Task<Guid?> WholeOrgAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return null;

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        return scope is { IsOrgWide: true } ? orgId : null;
    }

    private sealed record Caller(HostScope Scope, AccountantPackageAccess Access, string UserId);

    /// <summary>
    /// The caller's reach and what it may read: its scope (null without a user, an org or a scope, never widened to the whole org), and the owner accounting only with
    /// <c>agency.statement.view</c> (the policy of the owner statements: the agency plan and the permission of the role).
    /// </summary>
    private async Task<Caller?> ResolveCallerAsync(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return null;

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return null;

        var ownerAccounting = (await authorizationService.AuthorizeAsync(User, CasazenPolicies.AgencyStatementView)).Succeeded;
        return new Caller(scope, new AccountantPackageAccess(ownerAccounting), userId);
    }
}
