using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Leases.Reading;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Reads a document of a lease on the server (ADR-009, NL-DOCS). The text comes from <see cref="IContractTextReader"/> (the text a digital
/// PDF carries, OCR for a scan or a photo) and never leaves the host.
/// </summary>
/// <remarks>
/// <para><b>The document of an imported draft</b> (the owner has the signed contract and uploads it): the data are read by
/// <see cref="LeaseContractTextParser"/> and put into the draft by <see cref="ILeaseDraftService.ApplyContractReadingAsync"/>, which fills
/// only what is empty and never overwrites what the person typed. <c>Completed</c>: something was read and nothing contradicts what was
/// typed; <c>NeedsReview</c>: the contract says something else than what was typed somewhere; <c>Failed</c>: nothing could be read, with
/// a code in <c>ExtractionError</c> (<see cref="ContractReadingCodes"/>) and the manual wizard still there.</para>
/// <para><b>Any other lease</b> (an attestation or a registration of a lease that already exists, a ConcordatoFacile draft) goes on as it
/// did: a few fields by simple rules, written to the lease only when every field is certain and none contradicts it.</para>
/// <para><b>Privacy.</b> <c>ExtractionResult</c> holds the names of the fields, their confidence and what became of them: <b>no value</b> of
/// a person, no name, fiscal code or address (the one date it keeps is the day of the signature, which is not a person's). The values
/// read are written to the encrypted draft only. Nothing of the text is logged (ADR-009-R10): ids, counts and status codes.</para>
/// </remarks>
public sealed class LeaseDocumentExtractionService(
    AppDbContext db,
    IFileStorage storage,
    IContractTextReader reader,
    ILeaseDraftService drafts,
    ILogger<LeaseDocumentExtractionService> logger) : ILeaseDocumentExtractionService
{
    private const int HighConfidenceThreshold = 80;

    /// <summary>Fewer letters and digits than this in a whole document is no text.</summary>
    private const int MinimumCharacters = 20;

    public async Task ExtractAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await db.LeaseDocuments
            .Include(d => d.LeaseContract)
                .ThenInclude(l => l.Property)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new NotFoundException("Document not found") { Code = "document_not_found", MessageKey = "DocumentNotFound" };

        var lease = document.LeaseContract;

        // Tenancy: a document is never read into a lease of another org, whatever the ids say (the job runs with no org filter).
        if (document.OrgId != lease.OrgId)
            throw new DomainRuleException("tenant_isolation_violation", "TenantIsolationViolation");

        var importedDraft = lease.Status == LeaseStatus.Draft && lease.Origin == LeaseOrigin.Imported;
        try
        {
            if (importedDraft && document.Kind != LeaseDocumentKind.Contract)
            {
                // The identity document of a tenant or the receipt of a registration do not fill the draft of a contract.
                await UpdateAsync(documentId, d =>
                {
                    d.ExtractionStartedAt = DateTime.UtcNow;
                    d.ExtractionStatus = LeaseDocumentExtractionStatus.Skipped;
                    d.ExtractionCompletedAt = DateTime.UtcNow;
                }, ct);
                return;
            }

            await UpdateAsync(documentId, d =>
            {
                d.ExtractionStartedAt = DateTime.UtcNow;
                d.ExtractionStatus = LeaseDocumentExtractionStatus.Processing;
            }, ct);

            var bytes = await ReadStoredAsync(document, ct);
            if (bytes is null)
            {
                await FailAsync(documentId, ContractReadingCodes.FileMissing, ct);
                return;
            }

            ContractText text;
            try
            {
                text = await reader.ReadAsync(bytes, ct);
            }
            catch (InvalidDataException)
            {
                await FailAsync(documentId, ContractReadingCodes.Unreadable, ct);
                return;
            }
            catch (ContractTextUnavailableException)
            {
                await FailAsync(documentId, ContractReadingCodes.OcrUnavailable, ct);
                return;
            }

            if (text.Text.Count(char.IsLetterOrDigit) < MinimumCharacters)
            {
                await FailAsync(documentId, ContractReadingCodes.NoText, ct);
                return;
            }

            var leaseId = lease.Id;
            var status = importedDraft
                ? await ReadIntoDraftAsync(documentId, lease, text, ct)
                : await ReadIntoLeaseAsync(document, lease, text, ct);

            logger.LogInformation(
                "Document read. DocumentId={DocumentId} LeaseId={LeaseId} Status={Status} Pages={Pages} PagesOcr={PagesOcr}",
                documentId, leaseId, status, text.Pages, text.PagesOcr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type of the failure and the ids, never its message: the reader of a document may put a word of it there.
            logger.LogError("Reading the document failed. DocumentId={DocumentId} LeaseId={LeaseId} Error={ErrorType}", documentId, document.LeaseContractId, ex.GetType().Name);
            await FailAsync(documentId, ContractReadingCodes.Error, CancellationToken.None);
            throw;
        }
    }

    // ---- the document of an imported draft -----------------------------------------------------------------------------

    /// <remarks>
    /// The draft service clears the change tracker of the context it takes the lock on (the same scoped context as this service's), so
    /// everything written to the document after it goes through <see cref="UpdateAsync"/>, which reads the document again.
    /// </remarks>
    private async Task<LeaseDocumentExtractionStatus> ReadIntoDraftAsync(Guid documentId, LeaseContract lease, ContractText text, CancellationToken ct)
    {
        var context = new LeaseContractParseContext(lease.Property?.Address, lease.Property?.City);
        LeaseContractReading reading;
        try
        {
            reading = LeaseContractTextParser.Parse(text.Text, context);
        }
        catch (RegexMatchTimeoutException)
        {
            await FailAsync(documentId, ContractReadingCodes.Unreadable, ct);
            return LeaseDocumentExtractionStatus.Failed;
        }

        if (reading.IsEmpty)
        {
            await FailAsync(documentId, ContractReadingCodes.NothingFound, ct);
            return LeaseDocumentExtractionStatus.Failed;
        }

        LeaseDraftFillResult applied;
        try
        {
            applied = await drafts.ApplyContractReadingAsync(lease.Id, reading, ct);
        }
        catch (DomainConflictException ex) when (ex.Code == LeaseDraftErrorCodes.NotDraft)
        {
            // The person went on while the document was being read (the contract was imported, the draft deleted): nothing to fill.
            await UpdateAsync(documentId, d =>
            {
                d.ExtractionStatus = LeaseDocumentExtractionStatus.Skipped;
                d.ExtractionCompletedAt = DateTime.UtcNow;
            }, ct);
            return LeaseDocumentExtractionStatus.Skipped;
        }

        if (applied.Fields.Count == 0)
        {
            await FailAsync(documentId, ContractReadingCodes.NothingFound, ct);
            return LeaseDocumentExtractionStatus.Failed;
        }

        var status = applied.Fields.Any(f => f.State == FieldReadingState.Conflict)
            ? LeaseDocumentExtractionStatus.NeedsReview
            : LeaseDocumentExtractionStatus.Completed;
        var result = JsonSerializer.Serialize(new
        {
            version = 2,
            source = text.PagesOcr == 0 ? "embedded" : text.PagesOcr == text.Pages ? "ocr" : "mixed",
            pages = text.Pages,
            pagesOcr = text.PagesOcr,
            truncated = text.Truncated,
            stipulaDate = reading.StipulaDate is { IsAccepted: true } signed ? signed.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            fields = applied.Fields.Select(f => new
            {
                field = f.Field,
                partyId = f.PartyId,
                role = f.Role?.ToString(),
                state = f.State.ToString(),
                confidence = f.Confidence,
                derived = f.Derived,
            }),
        });
        await UpdateAsync(documentId, d =>
        {
            d.ExtractionStatus = status;
            d.ExtractionError = null;
            d.ExtractionResult = result;
            d.ExtractionCompletedAt = DateTime.UtcNow;
        }, ct);
        return status;
    }

    private Task FailAsync(Guid documentId, string code, CancellationToken ct) =>
        UpdateAsync(documentId, d =>
        {
            d.ExtractionStatus = LeaseDocumentExtractionStatus.Failed;
            d.ExtractionError = code;
            d.ExtractionResult = null;
            d.ExtractionCompletedAt = DateTime.UtcNow;
        }, ct);

    /// <summary>Changes the document as it is in the database now and saves: the instance read at the start may have been detached since.</summary>
    private async Task UpdateAsync(Guid documentId, Action<LeaseDocument> change, CancellationToken ct)
    {
        var current = await db.LeaseDocuments.FirstAsync(d => d.Id == documentId, ct);
        change(current);
        await db.SaveChangesAsync(ct);
    }

    private async Task<byte[]?> ReadStoredAsync(LeaseDocument document, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(StorageBucket.Private, document.StorageKey, ct);
        if (stream is null)
            return null;

        using var content = new MemoryStream();
        await stream.CopyToAsync(content, ct);
        return content.ToArray();
    }

    // ---- any other lease: the first pipeline, as it was (ADR-009) -------------------------------------------------------

    private async Task<LeaseDocumentExtractionStatus> ReadIntoLeaseAsync(LeaseDocument document, LeaseContract lease, ContractText text, CancellationToken ct)
    {
        var fields = ParseExtractedFields(text.Text);
        var conflicts = ValidateCoherence(fields, lease);

        // Fields are written to the lease only when every one is certain and none contradicts the lease.
        var allHighConfidence = fields.All(f => f.Confidence >= HighConfidenceThreshold);
        if (allHighConfidence && conflicts.Count == 0)
        {
            PopulateLeaseFields(lease, fields);
            document.ExtractionStatus = LeaseDocumentExtractionStatus.Completed;
        }
        else
        {
            document.ExtractionStatus = LeaseDocumentExtractionStatus.NeedsReview;
        }

        // The names of the fields and how sure the reading was, never what it read: the name of a person is not kept here.
        document.ExtractionError = null;
        document.ExtractionResult = JsonSerializer.Serialize(new
        {
            version = 1,
            fields = fields.Select(f => new { name = f.Name, confidence = f.Confidence }).ToList(),
            conflicts = conflicts.Select(c => new { field = c.Field, reason = c.Reason }).ToList(),
        });
        document.ExtractionCompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return document.ExtractionStatus;
    }

    private static List<ExtractedField> ParseExtractedFields(string text)
    {
        var fields = new List<ExtractedField>();

        // Extract landlord name (simplified pattern matching)
        var landlordMatch = Regex.Match(text, @"(?:locatore|landlord|proprietario)[:\s]+([A-Z][a-z]+\s+[A-Z][a-z]+)", RegexOptions.IgnoreCase);
        if (landlordMatch.Success)
        {
            fields.Add(new ExtractedField
            {
                Name = "LandlordName",
                Value = landlordMatch.Groups[1].Value.Trim(),
                Confidence = 85
            });
        }

        // Extract tenant name
        var tenantMatch = Regex.Match(text, @"(?:conduttore|tenant|inquilino)[:\s]+([A-Z][a-z]+\s+[A-Z][a-z]+)", RegexOptions.IgnoreCase);
        if (tenantMatch.Success)
        {
            fields.Add(new ExtractedField
            {
                Name = "TenantName",
                Value = tenantMatch.Groups[1].Value.Trim(),
                Confidence = 85
            });
        }

        // Extract monthly rent
        var rentMatch = Regex.Match(text, @"(?:canone|rent|affitto)[:\s]+€?\s*([0-9,\.]+)", RegexOptions.IgnoreCase);
        if (rentMatch.Success && decimal.TryParse(rentMatch.Groups[1].Value.Replace(",", "."), out var rent))
        {
            fields.Add(new ExtractedField
            {
                Name = "MonthlyRent",
                Value = rent.ToString("F2"),
                Confidence = 90
            });
        }

        // Extract security deposit
        var depositMatch = Regex.Match(text, @"(?:deposito|cauzione|deposit)[:\s]+€?\s*([0-9,\.]+)", RegexOptions.IgnoreCase);
        if (depositMatch.Success && decimal.TryParse(depositMatch.Groups[1].Value.Replace(",", "."), out var deposit))
        {
            fields.Add(new ExtractedField
            {
                Name = "SecurityDeposit",
                Value = deposit.ToString("F2"),
                Confidence = 85
            });
        }

        // Extract start date
        var startDateMatch = Regex.Match(text, @"(?:inizio|start|decorrenza)[:\s]+(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{4})", RegexOptions.IgnoreCase);
        if (startDateMatch.Success && DateTime.TryParse(startDateMatch.Groups[1].Value, out var startDate))
        {
            fields.Add(new ExtractedField
            {
                Name = "StartDate",
                Value = startDate.ToString("yyyy-MM-dd"),
                Confidence = 80
            });
        }

        // Extract end date
        var endDateMatch = Regex.Match(text, @"(?:fine|end|scadenza)[:\s]+(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{4})", RegexOptions.IgnoreCase);
        if (endDateMatch.Success && DateTime.TryParse(endDateMatch.Groups[1].Value, out var endDate))
        {
            fields.Add(new ExtractedField
            {
                Name = "EndDate",
                Value = endDate.ToString("yyyy-MM-dd"),
                Confidence = 80
            });
        }

        return fields;
    }

    private static List<ExtractionConflict> ValidateCoherence(List<ExtractedField> fields, LeaseContract lease)
    {
        var conflicts = new List<ExtractionConflict>();

        // Check parties (simplified - we'd need to check the actual parties in the database)
        var hasLandlord = fields.Any(f => f.Name == "LandlordName");
        var hasTenant = fields.Any(f => f.Name == "TenantName");
        if (!hasLandlord || !hasTenant)
        {
            conflicts.Add(new ExtractionConflict
            {
                Field = "Parties",
                Reason = "Contract must have both landlord and tenant parties"
            });
        }

        // Check date coherence
        var startDateField = fields.FirstOrDefault(f => f.Name == "StartDate");
        var endDateField = fields.FirstOrDefault(f => f.Name == "EndDate");
        if (startDateField != null && endDateField != null &&
            DateTime.TryParse(startDateField.Value, out var startDate) &&
            DateTime.TryParse(endDateField.Value, out var endDate) &&
            startDate >= endDate)
        {
            conflicts.Add(new ExtractionConflict
            {
                Field = "Dates",
                Reason = "Start date must be before end date"
            });
        }

        // Check rent coherence if the lease has a rent already
        var rentField = fields.FirstOrDefault(f => f.Name == "MonthlyRent");
        if (rentField != null && decimal.TryParse(rentField.Value, out var extractedRent) && lease.MonthlyRent > 0)
        {
            var tolerance = lease.MonthlyRent * 0.5m; // +/- 50%
            if (Math.Abs(extractedRent - lease.MonthlyRent) > tolerance)
            {
                conflicts.Add(new ExtractionConflict
                {
                    Field = "MonthlyRent",
                    Reason = "Extracted rent differs from expected by more than 50%"
                });
            }
        }

        return conflicts;
    }

    private void PopulateLeaseFields(LeaseContract lease, List<ExtractedField> fields)
    {
        // Only populate if the field is not already set (don't silently overwrite user data)
        var rentField = fields.FirstOrDefault(f => f.Name == "MonthlyRent");
        if (rentField != null && decimal.TryParse(rentField.Value, out var rent))
        {
            if (lease.MonthlyRent == 0)
            {
                lease.MonthlyRent = rent;
            }
            else if (Math.Abs(lease.MonthlyRent - rent) > 0.01m)
            {
                // Field already populated - this is a conflict, don't overwrite
                logger.LogWarning("Lease {LeaseId} MonthlyRent is already set and the document says another: left as it is", lease.Id);
            }
        }

        var depositField = fields.FirstOrDefault(f => f.Name == "SecurityDeposit");
        if (depositField != null && decimal.TryParse(depositField.Value, out var deposit))
        {
            if (!lease.SecurityDeposit.HasValue)
            {
                lease.SecurityDeposit = deposit;
            }
            else if (Math.Abs(lease.SecurityDeposit.Value - deposit) > 0.01m)
            {
                logger.LogWarning("Lease {LeaseId} SecurityDeposit is already set and the document says another: left as it is", lease.Id);
            }
        }

        var startDateField = fields.FirstOrDefault(f => f.Name == "StartDate");
        if (startDateField != null && DateTime.TryParse(startDateField.Value, out var startDate))
        {
            if (lease.StartDate == default || lease.StartDate.Year < 2000)
            {
                lease.StartDate = startDate;
            }
            else if (lease.StartDate.Date != startDate.Date)
            {
                logger.LogWarning("Lease {LeaseId} StartDate is already set and the document says another: left as it is", lease.Id);
            }
        }

        var endDateField = fields.FirstOrDefault(f => f.Name == "EndDate");
        if (endDateField != null && DateTime.TryParse(endDateField.Value, out var endDate))
        {
            if (lease.EndDate == default || lease.EndDate.Year < 2000)
            {
                lease.EndDate = endDate;
            }
            else if (lease.EndDate.Date != endDate.Date)
            {
                logger.LogWarning("Lease {LeaseId} EndDate is already set and the document says another: left as it is", lease.Id);
            }
        }

        // The parties are not written here: a lease that exists has its people already, and an imported draft is filled by LeaseDraftContractFill.
    }

    /// <summary>A field of the first pipeline, in memory only: its value is never kept with the document.</summary>
    private sealed class ExtractedField
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public int Confidence { get; set; }
    }

    private sealed class ExtractionConflict
    {
        public string Field { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }
}
