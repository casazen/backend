using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Casazen.Infrastructure.Services;

public sealed class LeaseDocumentExtractionService(
    AppDbContext db,
    IFileStorage storage,
    ILogger<LeaseDocumentExtractionService> logger) : ILeaseDocumentExtractionService
{
    private const int HighConfidenceThreshold = 80;

    public async Task ExtractAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await db.LeaseDocuments
            .Include(d => d.LeaseContract)
                .ThenInclude(l => l.Property)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new NotFoundException("Document not found") { Code = "document_not_found", MessageKey = "DocumentNotFound" };

        var lease = document.LeaseContract;

        try
        {
            document.ExtractionStartedAt = DateTime.UtcNow;
            document.ExtractionStatus = LeaseDocumentExtractionStatus.Processing;
            await db.SaveChangesAsync(ct);

            // Download the document
            var stream = await storage.OpenReadAsync(StorageBucket.Private, document.StorageKey, ct)
                ?? throw new InvalidOperationException($"Document {documentId} storage key not found");

            // Extract text from PDF pages using PdfPig (digital PDFs with embedded text only; scanned images will return empty)
            var extractedText = await ExtractTextFromPdfAsync(stream, ct);
            if (string.IsNullOrWhiteSpace(extractedText))
            {
                document.ExtractionStatus = LeaseDocumentExtractionStatus.Failed;
                document.ExtractionError = "No text could be extracted from the document";
                document.ExtractionCompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            // Parse extracted fields
            var fields = ParseExtractedFields(extractedText);

            // Validate coherence
            var conflicts = ValidateCoherence(fields, lease);

            // Determine if we should auto-populate
            var allHighConfidence = fields.All(f => f.Confidence >= HighConfidenceThreshold);
            var hasConflicts = conflicts.Count > 0;

            // Populate lease fields if high confidence and no conflicts
            if (allHighConfidence && !hasConflicts)
            {
                await PopulateLeaseFieldsAsync(lease, fields, ct);
                document.ExtractionStatus = LeaseDocumentExtractionStatus.Completed;
            }
            else
            {
                document.ExtractionStatus = LeaseDocumentExtractionStatus.NeedsReview;
            }

            // Store extraction result
            var result = new
            {
                fields = fields.Select(f => new { f.Name, f.Value, f.Confidence }).ToList(),
                conflicts = conflicts
            };
            document.ExtractionResult = JsonSerializer.Serialize(result);
            document.ExtractionCompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Extraction completed for document {DocumentId}: status {Status}, {FieldCount} fields extracted, {ConflictCount} conflicts",
                documentId,
                document.ExtractionStatus,
                fields.Count,
                conflicts.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Extraction failed for document {DocumentId}", documentId);
            document.ExtractionStatus = LeaseDocumentExtractionStatus.Failed;
            document.ExtractionError = $"Extraction error: {ex.Message}";
            document.ExtractionCompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task<string> ExtractTextFromPdfAsync(Stream pdfStream, CancellationToken ct)
    {
        // Extract text from all pages of the PDF using PdfPig (ADR-009-R4: multi-page support)
        var extractedText = new StringBuilder();

        try
        {
            // PdfPig expects a seekable stream
            using var memoryStream = new MemoryStream();
            await pdfStream.CopyToAsync(memoryStream, ct);
            memoryStream.Position = 0;

            using var document = PdfDocument.Open(memoryStream);

            logger.LogInformation("Extracting text from {PageCount} pages", document.NumberOfPages);

            // Extract text from all pages (ADR-009-R4)
            foreach (var page in document.GetPages())
            {
                var pageText = page.Text;
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    extractedText.AppendLine($"--- Page {page.Number} ---");
                    extractedText.AppendLine(pageText);
                    extractedText.AppendLine();
                }
            }

            logger.LogInformation("Extracted {CharCount} characters from {PageCount} pages", extractedText.Length, document.NumberOfPages);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PDF text extraction failed");
            return string.Empty;
        }

        return extractedText.ToString();
    }

    private List<ExtractedField> ParseExtractedFields(string text)
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

    private List<ExtractionConflict> ValidateCoherence(List<ExtractedField> fields, LeaseContract lease)
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
                Expected = "Landlord and Tenant",
                Extracted = hasLandlord ? "Landlord only" : hasTenant ? "Tenant only" : "None",
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
                Expected = "StartDate < EndDate",
                Extracted = $"{startDateField.Value} >= {endDateField.Value}",
                Reason = "Start date must be before end date"
            });
        }

        // Check rent coherence if property has existing rent
        var rentField = fields.FirstOrDefault(f => f.Name == "MonthlyRent");
        if (rentField != null && decimal.TryParse(rentField.Value, out var extractedRent) && lease.MonthlyRent > 0)
        {
            var tolerance = lease.MonthlyRent * 0.5m; // +/- 50%
            if (Math.Abs(extractedRent - lease.MonthlyRent) > tolerance)
            {
                conflicts.Add(new ExtractionConflict
                {
                    Field = "MonthlyRent",
                    Expected = lease.MonthlyRent.ToString("F2"),
                    Extracted = rentField.Value,
                    Reason = "Extracted rent differs from expected by more than 50%"
                });
            }
        }

        // Check address contains city (simplified)
        if (lease.Property != null && !string.IsNullOrEmpty(lease.Property.City))
        {
            // This is a simplified check - in production we'd extract the full address
            // For now, we don't have address extraction implemented
        }

        return conflicts;
    }

    private async Task PopulateLeaseFieldsAsync(LeaseContract lease, List<ExtractedField> fields, CancellationToken ct)
    {
        var conflicts = new List<string>();

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
                logger.LogWarning(
                    "Lease {LeaseId} MonthlyRent already set to {Existing}, extracted {Extracted}",
                    lease.Id,
                    lease.MonthlyRent,
                    rent);
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
                logger.LogWarning(
                    "Lease {LeaseId} SecurityDeposit already set to {Existing}, extracted {Extracted}",
                    lease.Id,
                    lease.SecurityDeposit,
                    deposit);
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
                logger.LogWarning(
                    "Lease {LeaseId} StartDate already set to {Existing}, extracted {Extracted}",
                    lease.Id,
                    lease.StartDate,
                    startDate);
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
                logger.LogWarning(
                    "Lease {LeaseId} EndDate already set to {Existing}, extracted {Extracted}",
                    lease.Id,
                    lease.EndDate,
                    endDate);
            }
        }

        // Note: Party population would require more complex logic to create/update Party entities
        // This is simplified for the proof of concept
    }

    private sealed class ExtractedField
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public int Confidence { get; set; }
    }

    private sealed class ExtractionConflict
    {
        public string Field { get; set; } = string.Empty;
        public string Expected { get; set; } = string.Empty;
        public string Extracted { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }
}
