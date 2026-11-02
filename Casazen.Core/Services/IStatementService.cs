using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Service for generating and managing monthly owner statements (ADR-017).
/// Statements are generated per property owner per property per month, with revenue breakdown,
/// commission, expenses, withholding tax, and net payable computed with banker's rounding.
/// Co-owners each receive their own statement, split by percentage (ADR-017-R3/R4).
/// </summary>
public interface IStatementService
{
    /// <summary>
    /// Generates a new monthly statement for the given owner, property, and period.
    /// For co-ownership, one statement is created per owner (split by percentage).
    /// The statement is created in Draft status with line items for all bookings collected
    /// in that month (cash year, ADR-017). Returns the generated statement(s).
    /// </summary>
    /// <param name="propertyOwnerId">The property owner to generate the statement for.</param>
    /// <param name="propertyId">The property to generate the statement for.</param>
    /// <param name="month">Month of the statement period (1-12).</param>
    /// <param name="year">Year of the statement period (e.g., 2026).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated statement in Draft status.</returns>
    /// <exception cref="Exceptions.DomainRuleException">
    /// Statement already exists for this owner/property/period, or the property owner does not own the property.
    /// </exception>
    Task<OwnerStatement> GenerateStatementAsync(
        Guid propertyOwnerId,
        Guid propertyId,
        int month,
        int year,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a Draft statement, locking it for review. The statement can no longer be edited.
    /// An Approved statement must be reviewed by the host or agency before publishing.
    /// </summary>
    /// <param name="statementId">The statement to approve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The approved statement.</returns>
    /// <exception cref="Exceptions.NotFoundException">Statement not found.</exception>
    /// <exception cref="Exceptions.DomainRuleException">Statement is not in Draft status.</exception>
    Task<OwnerStatement> ApproveStatementAsync(Guid statementId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an Approved statement, sending it to the owner. The statement becomes immutable.
    /// Any corrections after publication must be made via adjustment rows in the next month's statement (ADR-017-R5).
    /// </summary>
    /// <param name="statementId">The statement to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The published statement with PublishedAt timestamp.</returns>
    /// <exception cref="Exceptions.NotFoundException">Statement not found.</exception>
    /// <exception cref="Exceptions.DomainRuleException">Statement is not in Approved status.</exception>
    Task<OwnerStatement> PublishStatementAsync(Guid statementId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recalculates the totals of a Draft statement (after manual OTA revenue entry or CSV import).
    /// Computes TotalRevenue, Commission, Expenses, WithholdingTax, and NetPayable with banker's rounding (ADR-017-R2).
    /// For co-owners, applies the ownership split.
    /// </summary>
    /// <param name="statementId">The statement to recalculate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recalculated statement.</returns>
    /// <exception cref="Exceptions.NotFoundException">Statement not found.</exception>
    /// <exception cref="Exceptions.DomainRuleException">Statement is not in Draft status.</exception>
    Task<OwnerStatement> RecalculateStatementAsync(Guid statementId, CancellationToken cancellationToken = default);
}
