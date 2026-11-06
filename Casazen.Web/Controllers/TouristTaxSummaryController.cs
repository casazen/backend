using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// The tourist tax per comune and period (NF-100, REP-05): what the stays owe, what the host paid and the receipts. It is money, so the
/// class asks <c>payment.read</c> (the owner, the administrators, the property managers and the accountant hold it; the collaborator
/// never does) and the writes, recording and deleting a payment, ask <c>payment.write</c> on top (the accountant, who reads, cannot change
/// what was paid). Everything works inside the org of the caller and the reach of its <see cref="HostScope"/>, as the fiscal reports do
/// (<see cref="FiscalController"/>): another org's payment is a 404, never a 403.
/// <para>
/// A payment belongs to the org and to no property. A caller who reaches only some of the properties therefore gets, from the summary, what
/// is owed on those properties and nothing about the payments (<c>paymentsHidden</c>), and cannot record a payment, delete one or open its
/// receipt: 403 <c>tourist_tax_payments_need_whole_org</c>.
/// </para>
/// <para>
/// <c>GET summary</c> takes <c>from</c> and <c>to</c> (<c>yyyy-MM-dd</c>, Europe/Rome days, both included, at most 366 days like the tourist
/// tax report: 400 <c>fiscal_report_period_invalid</c>) and <c>format</c> (<c>json</c>, the default, or <c>csv</c>: 400
/// <c>report_invalid_format</c>). <c>POST remittances</c> is <c>multipart/form-data</c> (<see cref="RecordRemittanceForm"/>); its errors
/// are 422 <c>tourist_tax_comune_unknown</c>, <c>tourist_tax_amount_invalid</c>, <c>tourist_tax_date_in_future</c>,
/// <c>tourist_tax_period_invalid</c>, <c>tourist_tax_receipt_invalid</c> and <c>tourist_tax_text_too_long</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/tourist-tax")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class TouristTaxSummaryController(
    ITouristTaxSummaryService summaries,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    /// <summary>The language of the CSV: Italian, whatever the language the caller asked in.</summary>
    private static readonly CultureInfo ItalianCulture = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>
    /// The largest request: the receipt and a margin for the other fields. The multipart body is buffered in memory up to this size
    /// (<see cref="RequestFormLimitsAttribute.MemoryBufferThreshold"/>), so a receipt is never spilled to a temporary file on disk: the
    /// default threshold is 64 KB.
    /// </summary>
    private const int MaxRequestBytes = (int)TouristTaxRemittanceLimits.MaxReceiptBytes + (64 * 1024);

    /// <summary>
    /// The tax owed, paid and receipted per comune for the period: for each comune with stays (check-in in the period) or with payments
    /// (whose period lies inside it), the owed amount (the one recorded on the stays, with the stays that have none counted apart), the
    /// paid amount, the balance, the status and the payments; and the totals. As JSON, or as a CSV to download. For a caller who reaches only
    /// some of the properties: only the owed amount of those properties, with <c>paymentsHidden</c> true and the rest null.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(TouristTaxSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSummary(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (this.CheckFormat(format, out var csv) is { } formatProblem)
            return formatProblem;
        if (fromDay is not { } from || toDay is not { } to)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "fiscal_report_period_invalid", "FiscalReportPeriodInvalid");
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        try
        {
            var summary = await summaries.GetSummaryAsync(scope, new FiscalReportPeriod(from, to), cancellationToken);
            return csv
                ? File(TouristTaxSummaryDocuments.Csv(summary, CsvTexts()), "text/csv", TouristTaxSummaryDocuments.FileName(summary.Period))
                : Ok(TouristTaxSummaryDto.From(summary, TouristTaxSummaryDocuments.TitleOf(summary)));
        }
        catch (FiscalValidationException ex)
        {
            return this.ApiProblem(StatusCodes.Status400BadRequest, ex.Code, ex.MessageKey, ex.MessageArgs);
        }
    }

    /// <summary>
    /// Records a payment of the tourist tax to a comune, with its receipt when one is sent. The comune has to be one of the comuni of the
    /// caller's properties; the amount is above zero to the cent; the day of the payment is not after today in Europe/Rome; the period is
    /// not backwards and lasts at most a year; the receipt is a PDF, a JPEG or a PNG of at most 10 MB, judged on its content and stored in
    /// the private bucket. Needs <c>payment.write</c> and a scope that reaches the whole org (403 <c>tourist_tax_payments_need_whole_org</c>
    /// otherwise). 201 with the payment.
    /// </summary>
    [HttpPost("remittances")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxRequestBytes, MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType(typeof(TouristTaxRemittanceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RecordRemittance([FromForm] RecordRemittanceForm form, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return Unauthorized();
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (!TouristTaxSummaryRules.TryParseAmount(form.PaidAmount, out var amount))
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, TouristTaxErrorCodes.AmountInvalid, "TouristTaxAmountInvalid");

        // No file chosen is an empty part with no name (a plain HTML form); an empty file that has a name is a receipt, and an invalid one.
        var file = form.Receipt is { } sent && !(sent.Length == 0 && string.IsNullOrEmpty(sent.FileName)) ? sent : null;
        var request = new RecordRemittanceRequest(
            form.Comune ?? string.Empty,
            form.PeriodFrom!.Value,
            form.PeriodTo!.Value,
            amount,
            form.PaidOn!.Value,
            form.Reference,
            form.Notes,
            userId,
            file?.FileName);

        TouristTaxRemittanceLine line;
        if (file is null)
        {
            line = await summaries.RecordRemittanceAsync(scope, request, null, cancellationToken);
        }
        else
        {
            await using var receipt = file.OpenReadStream();
            line = await summaries.RecordRemittanceAsync(scope, request, receipt, cancellationToken);
        }

        return StatusCode(StatusCodes.Status201Created, TouristTaxRemittanceDto.From(line));
    }

    /// <summary>
    /// The receipt of a payment, from the private bucket (never a public address): <c>Cache-Control: private, no-store</c>. 404 for a payment
    /// of another org, for one that does not exist and for one with no receipt; 403 <c>tourist_tax_payments_need_whole_org</c> for a caller
    /// who reaches only some of the properties.
    /// </summary>
    [HttpGet("remittances/{id:guid}/receipt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReceipt(Guid id, CancellationToken cancellationToken)
    {
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var receipt = await summaries.OpenReceiptAsync(scope, id, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return File(receipt.Content, receipt.ContentType, receipt.FileName);
    }

    /// <summary>
    /// Deletes a wrong entry: the payment and its receipt. Needs <c>payment.write</c> and a scope that reaches the whole org (403
    /// <c>tourist_tax_payments_need_whole_org</c> otherwise). 204; 404 for a payment of another org or one that does not exist.
    /// </summary>
    [HttpDelete("remittances/{id:guid}")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRemittance(Guid id, CancellationToken cancellationToken)
    {
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        await summaries.DeleteRemittanceAsync(scope, id, cancellationToken);
        return NoContent();
    }

    /// <summary>The caller's reach (AM-03): the whole org for the roles that reach it, only the properties it was given otherwise; null without an org.</summary>
    private async Task<HostScope?> GetScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    /// <summary>
    /// The words the CSV takes from the resources, read in Italian: the file is Italian (title, header, statuses) whatever the language the
    /// caller asked in, so a file for the accountant never mixes two languages. The language is set for these two lookups only.
    /// </summary>
    private TouristTaxCsvTexts CsvTexts()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = ItalianCulture;
        try
        {
            return new TouristTaxCsvTexts(
                localizer["TouristTaxStatusNothingRecorded"].Value,
                localizer["TouristTaxOwedIncomplete"].Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
