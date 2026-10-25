using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Service for managing PropertyOwnership records with validation (ADR-012-R4).
/// Validates that the sum of ownership shares for a property equals 100.00.
/// </summary>
public sealed class PropertyOwnershipService(AppDbContext db)
{
    /// <summary>
    /// Validates that the ownership shares for a property sum to exactly 100.00 (ADR-012-R4).
    /// Returns 422 with code "shares_not_hundred" if validation fails.
    /// </summary>
    /// <param name="propertyId">The property to validate ownership shares for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="DomainRuleException">Thrown when shares do not sum to 100.00.</exception>
    public async Task ValidateSharesAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var totalShares = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyId == propertyId && po.OwnershipEnd == null)
            .SumAsync(po => po.SharePercent, cancellationToken);

        if (totalShares != 100.00m)
        {
            throw new DomainRuleException("shares_not_hundred", "shares_not_hundred");
        }
    }
}
