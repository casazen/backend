using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Service for calculating and managing withholding tax on owner payments (ADR-018).
/// Agency acting as sostituto d'imposta withholds 21% tax on owner payments for short-term rentals.
/// Regulatory references: D.L. 50/2017 art. 4 (current), D.Lgs. 117/2026 artt. 207-208 (expected 1/1/2027).
/// </summary>
public interface IWithholdingService
{
    /// <summary>
    /// Calculates withholding tax for a statement and creates WithholdingLog entries.
    /// Skips owners with VAT regime (TaxRegime = VatHolder, ADR-018-R5).
    /// Base includes gross revenue + flat-rate cleaning + flat-rate linen (ADR-018-R4).
    /// Commission treatment per mandate (deductible or not, ADR-018-R4).
    /// Excludes deposits, down payments, and penalties (ADR-018-R6).
    /// </summary>
    /// <param name="statementId">The owner statement to calculate withholding for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of created WithholdingLog entries.</returns>
    /// <exception cref="Exceptions.NotFoundException">Statement not found.</exception>
    Task<List<WithholdingLog>> CalculateWithholdingAsync(
        Guid statementId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an F24 record for a given month and year, aggregating all withholding amounts
    /// for the organization. Tax code 1919, due date 16th of following month (ADR-018-R8).
    /// </summary>
    /// <param name="orgId">The organization to generate F24 for.</param>
    /// <param name="month">Reference month (1-12).</param>
    /// <param name="year">Reference year (e.g., 2026).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated F24 record.</returns>
    /// <exception cref="Exceptions.DomainRuleException">F24 already exists for this period.</exception>
    Task<F24Record> GenerateF24Async(
        Guid orgId,
        int month,
        int year,
        CancellationToken cancellationToken = default);
}
