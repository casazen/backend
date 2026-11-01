using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.BankStatements;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.LongRent;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// Import of a bank statement on the rents of the long-term area («Importa estratto conto» of the Canoni page): the landlord uploads the
/// movements file of their bank (CSV, Excel, CAMT.053 or MT940), the server proposes for each credit the installment it pays, and the landlord
/// confirms. Both calls need <c>lease.create</c> (the permission that declares a payment) and are read inside the caller's scope: org and, unless
/// org-wide, the properties they reach (<see cref="HostScope"/>, the scope the rent register uses). The file is not stored; the preview writes
/// nothing; the confirmation declares each installment paid through the offline path of the ledger, and each one is authorized as a
/// <see cref="HostResource"/> of its property (one that is not the caller's is skipped as not found).
/// </summary>
[ApiController]
[Route("api/long-rent/rents/bank-import")]
[Authorize(Policy = CasazenPolicies.LeaseCreate)]
[EnableRateLimiting(RateLimitPolicies.RentBankImport)]
public class LongRentBankImportController(
    IRentBankImportService bankImport,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The largest request: the file and a margin for the other fields. The multipart body is buffered in memory up to this size
    /// (<see cref="RequestFormLimitsAttribute.MemoryBufferThreshold"/>), so a statement is never spilled to a temporary file on disk
    /// (ADR-013-R2): the default threshold is 64 KB.
    /// </summary>
    private const int MaxRequestBytes = BankStatementLimits.MaxFileBytes + (256 * 1024);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the statement and proposes, for each credit not imported before, the installment still to collect that it pays: the same amount
    /// to the cent, and the tenant, the payer seen before, the property or the month the reason of the payment names. Nothing is written and the
    /// file is not kept. 400 <c>bank_statement_file_missing</c>, 413 <c>bank_statement_too_large</c>, 415 <c>bank_statement_format_unsupported</c>,
    /// 422 <c>bank_statement_unreadable</c>, <c>bank_statement_empty</c>, <c>bank_statement_too_many_rows</c>, <c>bank_statement_mapping_invalid</c> and
    /// <c>bank_statement_columns_unrecognized</c> (with the first rows of the table, so the landlord can say which column is which).
    /// </summary>
    [HttpPost("preview")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxRequestBytes, MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType(typeof(RentBankImportPreview), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RentBankImportPreview>> Preview(
        [FromForm] BankStatementUpload upload, CancellationToken cancellationToken)
    {
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        var (file, problem) = await ReadFileAsync(upload.File, cancellationToken);
        if (problem is not null)
            return problem;
        if (!TryReadMapping(upload.Mapping, out var mapping))
            return MappingProblem();

        return Ok(await bankImport.PreviewAsync(scope, file!, mapping, cancellationToken));
    }

    /// <summary>
    /// Takes the same statement again with the choices of the landlord and declares each installment paid (the value date of the credit, the
    /// note «Da estratto conto», the offline path of the rent ledger). The server reads amount, date and payer from the file; a choice that
    /// cannot be applied is skipped with its code and never stops the others: <c>bank_movement_already_imported</c>,
    /// <c>bank_movement_not_found</c>, <c>bank_movement_amount_mismatch</c>, <c>rent_installment_not_found</c>,
    /// <c>rent_installment_not_payable</c>, <c>rent_installment_in_flight</c>, <c>rent_paid_on_invalid</c>. 400
    /// <c>bank_import_selections_invalid</c> for no choice, more than 500, or one that is not an id; the file is refused as in the preview.
    /// </summary>
    [HttpPost("confirm")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxRequestBytes, MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType(typeof(RentBankConfirmation), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RentBankConfirmation>> Confirm(
        [FromForm] BankStatementConfirmUpload upload, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return Unauthorized();
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        if (!TryReadSelections(upload.Selections, out var selections))
            return this.ApiProblem(StatusCodes.Status400BadRequest, BankStatementErrorCodes.SelectionsInvalid, "BankImportSelectionsInvalid");
        var (file, problem) = await ReadFileAsync(upload.File, cancellationToken);
        if (problem is not null)
            return problem;
        if (!TryReadMapping(upload.Mapping, out var mapping))
            return MappingProblem();

        return Ok(await bankImport.ConfirmAsync(scope, file!, mapping, selections, userId, MayChangeAsync, cancellationToken));
    }

    // ---------------------------------------------------------------- request

    private async Task<(byte[]? File, ObjectResult? Problem)> ReadFileAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return (null, this.ApiProblem(StatusCodes.Status400BadRequest, BankStatementErrorCodes.FileMissing, "BankStatementFileMissing"));
        if (file.Length > BankStatementLimits.MaxFileBytes)
        {
            return (null, this.ApiProblem(
                StatusCodes.Status413PayloadTooLarge,
                BankStatementErrorCodes.TooLarge,
                "BankStatementTooLarge",
                BankStatementLimits.MaxFileBytes / (1024 * 1024)));
        }

        var bytes = new byte[file.Length];
        await using var stream = file.OpenReadStream();
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return (bytes, null);
    }

    private ObjectResult MappingProblem() =>
        this.ApiProblem(StatusCodes.Status422UnprocessableEntity, BankStatementErrorCodes.MappingInvalid, "BankStatementMappingInvalid");

    /// <summary>The mapping the landlord chose; none is fine (the importer finds the columns), a mapping that is not JSON or lacks the three columns it needs is not.</summary>
    private static bool TryReadMapping(string? json, out BankColumnMapping? mapping)
    {
        mapping = null;
        if (string.IsNullOrWhiteSpace(json))
            return true;

        try
        {
            var body = JsonSerializer.Deserialize<BankColumnMappingBody>(json, JsonOptions);
            if (body is not { HeaderRow: { } headerRow, Date: { } date, Description: { } description })
                return false;

            mapping = new BankColumnMapping(
                headerRow,
                date,
                description,
                body.ValueDate,
                body.Amount,
                body.Credit,
                body.Debit,
                body.Counterparty,
                body.Iban);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadSelections(string? json, out List<RentBankSelection> selections)
    {
        selections = [];
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var body = JsonSerializer.Deserialize<List<BankSelectionBody?>>(json, JsonOptions);
            if (body is null || body.Count == 0 || body.Count > BankStatementLimits.MaxSelections)
                return false;

            foreach (var item in body)
            {
                if (item?.Fingerprint is not { Length: 64 } fingerprint
                    || !fingerprint.All(char.IsAsciiHexDigitLower)
                    || item.InstallmentId is not { } installmentId
                    || installmentId == Guid.Empty)
                {
                    return false;
                }

                selections.Add(new RentBankSelection(fingerprint, installmentId));
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- who

    private async Task<HostScope?> GetHostScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    /// <summary>May the caller declare payments on the installments of this property? Asked once per property by the service.</summary>
    private async Task<bool> MayChangeAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        return resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Create);
    }
}
