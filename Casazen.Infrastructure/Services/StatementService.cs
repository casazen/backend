using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Service for generating and managing monthly owner statements (ADR-017).
/// Implements rounding once at total (banker's rounding) and co-owner split (last owner gets remainder).
/// </summary>
public sealed class StatementService(AppDbContext db) : IStatementService
{
    /// <inheritdoc />
    public async Task<OwnerStatement> GenerateStatementAsync(
        Guid propertyOwnerId,
        Guid propertyId,
        int month,
        int year,
        CancellationToken cancellationToken = default)
    {
        // Check if statement already exists
        var existing = await db.Set<OwnerStatement>()
            .Where(s => s.PropertyOwnerId == propertyOwnerId && s.PropertyId == propertyId && s.Month == month && s.Year == year)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            throw new DomainRuleException("statement_already_exists", "StatementAlreadyExists");
        }

        // Get active ownership for this owner and property
        var ownership = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyOwnerId == propertyOwnerId && po.PropertyId == propertyId && po.OwnershipEnd == null)
            .FirstOrDefaultAsync(cancellationToken);

        if (ownership is null)
        {
            throw new DomainRuleException("owner_does_not_own_property", "OwnerDoesNotOwnProperty");
        }

        // Get the property for OrgId and currency
        var property = await db.Set<Property>()
            .Where(p => p.Id == propertyId)
            .FirstOrDefaultAsync(cancellationToken);

        if (property is null)
        {
            throw new NotFoundException($"Property {propertyId} not found");
        }

        // Get the active mandate for this ownership (most recent EffectiveFrom where EffectiveUntil is null or in the future)
        var mandate = await db.Set<OwnerMandate>()
            .Where(om => om.PropertyOwnershipId == ownership.Id && (om.EffectiveUntil == null || om.EffectiveUntil > DateTime.UtcNow))
            .OrderByDescending(om => om.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

        // For co-ownership: ensure all co-owners have the same commission rate (ADR-017-R4 requirement)
        // Mixed commission rates would break the "last owner gets remainder" logic
        var allOwnerships = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyId == propertyId && po.OwnershipEnd == null)
            .ToListAsync(cancellationToken);

        if (allOwnerships.Count > 1)
        {
            var commissionRates = new List<decimal?>();
            foreach (var own in allOwnerships)
            {
                var m = await db.Set<OwnerMandate>()
                    .Where(om => om.PropertyOwnershipId == own.Id && (om.EffectiveUntil == null || om.EffectiveUntil > DateTime.UtcNow))
                    .OrderByDescending(om => om.EffectiveFrom)
                    .FirstOrDefaultAsync(cancellationToken);
                commissionRates.Add(m?.CommissionPercent);
            }

            if (commissionRates.Distinct().Count() > 1)
            {
                throw new DomainRuleException("mixed_commission_coownership_not_supported", "MixedCommissionCoOwnershipNotSupported");
            }
        }

        // Create statement
        var statement = new OwnerStatement
        {
            OrgId = property.OrgId,
            PropertyOwnerId = propertyOwnerId,
            PropertyId = propertyId,
            Month = month,
            Year = year,
            Currency = "EUR",
            Status = StatementStatus.Draft,
            OwnershipPercent = ownership.SharePercent,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Set<OwnerStatement>().Add(statement);
        await db.SaveChangesAsync(cancellationToken);

        // Get all payments collected in this period (cash year, ADR-017)
        // ProcessedAt is when the payment was collected
        var startDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = startDate.AddMonths(1);

        var payments = await db.Set<Payment>()
            .Include(p => p.Booking)
            .Where(p => p.Booking.PropertyId == propertyId
                && p.Status == PaymentStatus.Completed
                && p.ProcessedAt != null
                && p.ProcessedAt >= startDate
                && p.ProcessedAt < endDate)
            .ToListAsync(cancellationToken);

        // Create statement lines for each payment
        foreach (var payment in payments)
        {
            var cashYear = payment.ProcessedAt!.Value.Year;
            var stayYear = payment.Booking.CheckInDate.Year;

            // Calculate commission based on mandate (if available)
            // Store full precision without rounding (ADR-017-R1)
            var grossRevenue = payment.Amount;
            var commission = mandate != null
                ? grossRevenue * mandate.CommissionPercent / 100m
                : 0m;

            // For co-ownership, apply the ownership split with FULL PRECISION (no rounding at line level per ADR-017-R2)
            var ownerShare = grossRevenue * ownership.SharePercent / 100m;
            var commissionShare = commission * ownership.SharePercent / 100m;

            var line = new StatementLine
            {
                OrgId = property.OrgId,
                StatementId = statement.Id,
                Type = OwnerStatementLineType.Booking,
                BookingId = payment.BookingId,
                GrossRevenue = ownerShare,
                Commission = commissionShare,
                Expenses = 0m,
                Withholding = 0m,
                CashYear = cashYear,
                StayYear = stayYear,
                CreatedAt = DateTime.UtcNow
            };

            db.Set<StatementLine>().Add(line);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Recalculate totals
        var result = await RecalculateStatementAsync(statement.Id, cancellationToken);

        // After generating any statement, check if all co-owners now have statements
        // If yes, recalculate the last owner's statement to apply remainder logic (ADR-017-R4)
        await RecalculateLastOwnerIfAllStatementsExistAsync(propertyId, month, year, cancellationToken);

        return result;
    }

    /// <inheritdoc />
    public async Task<OwnerStatement> ApproveStatementAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        var statement = await db.Set<OwnerStatement>()
            .FirstOrDefaultAsync(s => s.Id == statementId, cancellationToken);

        if (statement is null)
        {
            throw new NotFoundException($"Statement {statementId} not found");
        }

        if (statement.Status != StatementStatus.Draft)
        {
            throw new DomainRuleException("statement_not_draft", "StatementNotDraft");
        }

        statement.Status = StatementStatus.Approved;
        statement.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return statement;
    }

    /// <inheritdoc />
    public async Task<OwnerStatement> PublishStatementAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        var statement = await db.Set<OwnerStatement>()
            .FirstOrDefaultAsync(s => s.Id == statementId, cancellationToken);

        if (statement is null)
        {
            throw new NotFoundException($"Statement {statementId} not found");
        }

        if (statement.Status != StatementStatus.Approved)
        {
            throw new DomainRuleException("statement_not_approved", "StatementNotApproved");
        }

        statement.Status = StatementStatus.Published;
        statement.PublishedAt = DateTime.UtcNow;
        statement.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return statement;
    }

    /// <inheritdoc />
    public async Task<OwnerStatement> RecalculateStatementAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        var statement = await db.Set<OwnerStatement>()
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == statementId, cancellationToken);

        if (statement is null)
        {
            throw new NotFoundException($"Statement {statementId} not found");
        }

        if (statement.Status != StatementStatus.Draft)
        {
            throw new DomainRuleException("statement_not_draft", "StatementNotDraft");
        }

        // Sum line items with full precision (ADR-017-R1)
        statement.TotalRevenue = statement.Lines.Sum(l => l.GrossRevenue);
        statement.Commission = statement.Lines.Sum(l => l.Commission);
        statement.Expenses = statement.Lines.Sum(l => l.Expenses);
        statement.WithholdingTax = statement.Lines.Sum(l => l.Withholding);

        // Compute net with full precision
        var netBeforeRounding = statement.TotalRevenue - statement.Commission - statement.Expenses - statement.WithholdingTax;

        // Apply "last owner gets remainder" logic for co-ownership (ADR-017-R3/R4)
        // Get all active ownerships for this property in deterministic order (by PropertyOwnerId)
        var allOwnerships = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyId == statement.PropertyId && po.OwnershipEnd == null)
            .OrderBy(po => po.PropertyOwnerId)
            .ToListAsync(cancellationToken);

        if (allOwnerships.Count > 1)
        {
            // Co-ownership exists: check if this is the last owner in the deterministic order
            var isLastOwner = allOwnerships[^1].PropertyOwnerId == statement.PropertyOwnerId;

            if (isLastOwner)
            {
                // Last owner gets remainder: total - sum(other owners' rounded shares)
                // Query other owners' statements for the same property and period
                var otherStatements = await db.Set<OwnerStatement>()
                    .Where(s => s.PropertyId == statement.PropertyId
                        && s.Month == statement.Month
                        && s.Year == statement.Year
                        && s.PropertyOwnerId != statement.PropertyOwnerId)
                    .ToListAsync(cancellationToken);

                if (otherStatements.Count == allOwnerships.Count - 1)
                {
                    // All other co-owners have statements: compute property total and apply remainder
                    // Property total = sum of all owner shares (full precision)
                    var propertyTotal = netBeforeRounding / (statement.OwnershipPercent / 100m);
                    var otherOwnersTotal = otherStatements.Sum(s => s.NetPayable);
                    statement.NetPayable = Math.Round(propertyTotal, 2, MidpointRounding.ToEven) - otherOwnersTotal;
                }
                else
                {
                    // Other co-owners don't have statements yet: round normally for now
                    // (will be recalculated when other statements are generated)
                    statement.NetPayable = Math.Round(netBeforeRounding, 2, MidpointRounding.ToEven);
                }
            }
            else
            {
                // Not the last owner: round normally (ADR-017-R2)
                statement.NetPayable = Math.Round(netBeforeRounding, 2, MidpointRounding.ToEven);
            }
        }
        else
        {
            // Sole ownership: round normally (ADR-017-R2)
            statement.NetPayable = Math.Round(netBeforeRounding, 2, MidpointRounding.ToEven);
        }

        statement.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return statement;
    }

    /// <summary>
    /// Helper method to recalculate the last owner's statement if all co-owners now have statements.
    /// This ensures the "last owner gets remainder" logic (ADR-017-R4) is applied even when
    /// statements are generated out of order.
    /// </summary>
    private async Task RecalculateLastOwnerIfAllStatementsExistAsync(
        Guid propertyId,
        int month,
        int year,
        CancellationToken cancellationToken)
    {
        // Get all active ownerships for this property
        var allOwnerships = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyId == propertyId && po.OwnershipEnd == null)
            .OrderBy(po => po.PropertyOwnerId)
            .ToListAsync(cancellationToken);

        // Only proceed if there's co-ownership (2+ owners)
        if (allOwnerships.Count <= 1)
            return;

        // Check if all co-owners have statements for this period
        var allStatements = await db.Set<OwnerStatement>()
            .Where(s => s.PropertyId == propertyId && s.Month == month && s.Year == year)
            .ToListAsync(cancellationToken);

        if (allStatements.Count != allOwnerships.Count)
            return; // Not all co-owners have statements yet

        // All co-owners have statements: recalculate the last owner's statement to apply remainder
        var lastOwnerId = allOwnerships[^1].PropertyOwnerId;
        var lastOwnerStatement = allStatements.FirstOrDefault(s => s.PropertyOwnerId == lastOwnerId);

        if (lastOwnerStatement?.Status == StatementStatus.Draft)
        {
            // Recalculate to apply remainder logic
            await RecalculateStatementAsync(lastOwnerStatement.Id, cancellationToken);
        }
    }
}
