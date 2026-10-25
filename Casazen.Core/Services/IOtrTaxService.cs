namespace Casazen.Core.Services;

/// <summary>
/// OTR (Overnight Tourist Registry) tax CSV generator for Ticino.
/// Generates CSV files of overnight stays per period for tax reporting (ADR-016).
/// </summary>
public interface IOtrTaxService
{
    /// <summary>
    /// Generates the OTR CSV file for a Ticino property and period.
    /// Returns the CSV content and metadata for archiving.
    /// </summary>
    /// <param name="propertyId">Property in canton Ticino (TI).</param>
    /// <param name="startDate">Period start date in Europe/Zurich.</param>
    /// <param name="endDate">Period end date in Europe/Zurich.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>CSV content and file reference for archiving.</returns>
    Task<OtrTaxResult> GeneratePeriodReportAsync(
        Guid propertyId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of an OTR tax report generation.</summary>
public sealed record OtrTaxResult(
    string CsvContent,
    string Reference,
    string FileHash);
