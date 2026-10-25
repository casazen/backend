using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Service for calculating and managing withholding tax on owner payments (ADR-018).
/// Agency acting as sostituto d'imposta withholds 21% tax on owner payments for short-term rentals.
/// Regulatory references: D.L. 50/2017 art. 4 (current), D.Lgs. 117/2026 artt. 207-208 (expected 1/1/2027).
/// </summary>
public sealed class WithholdingService(AppDbContext db) : IWithholdingService
{
    private const decimal WithholdingRate = 0.21m; // 21% withholding tax rate

    /// <inheritdoc />
    public async Task<List<WithholdingLog>> CalculateWithholdingAsync(
        Guid statementId,
        CancellationToken cancellationToken = default)
    {
        // Get the statement with related entities
        var statement = await db.Set<OwnerStatement>()
            .Include(s => s.PropertyOwner)
            .Include(s => s.Property)
            .Include(s => s.Lines)
                .ThenInclude(l => l.Booking)
            .Where(s => s.Id == statementId)
            .FirstOrDefaultAsync(cancellationToken);

        if (statement is null)
        {
            throw new NotFoundException($"Statement {statementId} not found");
        }

        var withholdingLogs = new List<WithholdingLog>();

        // Skip if owner has VAT regime (ADR-018-R5)
        if (statement.PropertyOwner.TaxRegime == OwnerTaxRegime.VatHolder)
        {
            return withholdingLogs;
        }

        // Get the active mandate for commission treatment
        var ownership = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyOwnerId == statement.PropertyOwnerId
                && po.PropertyId == statement.PropertyId
                && po.OwnershipEnd == null)
            .FirstOrDefaultAsync(cancellationToken);

        if (ownership is null)
        {
            throw new DomainRuleException("owner_does_not_own_property", "OwnerDoesNotOwnProperty");
        }

        var mandate = await db.Set<OwnerMandate>()
            .Where(om => om.PropertyOwnershipId == ownership.Id
                && (om.EffectiveUntil == null || om.EffectiveUntil > DateTime.UtcNow))
            .OrderByDescending(om => om.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

        // Process each booking line in the statement
        foreach (var line in statement.Lines.Where(l => l.Type == OwnerStatementLineType.Booking && l.BookingId.HasValue))
        {
            var booking = line.Booking;
            if (booking is null)
            {
                continue;
            }

            // Calculate gross base (ADR-018-R4)
            // Base = Gross revenue + flat-rate cleaning + flat-rate linen
            var grossBase = line.GrossRevenue;

            // Add flat rates if mandate specifies them
            if (mandate?.CleaningFlatRate.HasValue == true)
            {
                grossBase += mandate.CleaningFlatRate.Value;
            }

            if (mandate?.LinenFlatRate.HasValue == true)
            {
                grossBase += mandate.LinenFlatRate.Value;
            }

            // Commission treatment per mandate (ADR-018-R4)
            // If commission is NOT deductible, add it to the base
            if (mandate?.WithholdingTreatment == CommissionTreatment.NotDeductible)
            {
                grossBase += line.Commission;
            }

            // Calculate withholding amount (21% of base)
            var withholdingAmount = Math.Round(grossBase * WithholdingRate, 2, MidpointRounding.ToEven);

            // Create withholding log entry
            var log = new WithholdingLog
            {
                OrgId = statement.OrgId,
                OwnerId = statement.PropertyOwnerId,
                PropertyId = statement.PropertyId,
                BookingId = booking.Id,
                Month = statement.Month,
                Year = statement.Year,
                CashYear = line.CashYear,
                StayYear = line.StayYear ?? line.CashYear, // Fallback to cash year if stay year not set
                GrossBase = grossBase,
                WithholdingAmount = withholdingAmount,
                CreatedAt = DateTime.UtcNow
            };

            db.Set<WithholdingLog>().Add(log);
            withholdingLogs.Add(log);
        }

        await db.SaveChangesAsync(cancellationToken);
        return withholdingLogs;
    }

    /// <inheritdoc />
    public async Task<F24Record> GenerateF24Async(
        Guid orgId,
        int month,
        int year,
        CancellationToken cancellationToken = default)
    {
        // Check if F24 already exists for this period
        var existing = await db.Set<F24Record>()
            .Where(f => f.OrgId == orgId && f.ReferenceMonth == month && f.ReferenceYear == year)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            throw new DomainRuleException("f24_already_exists", "F24AlreadyExists");
        }

        // Sum all withholding amounts for this period
        var totalAmount = await db.Set<WithholdingLog>()
            .Where(w => w.OrgId == orgId && w.Month == month && w.Year == year)
            .SumAsync(w => w.WithholdingAmount, cancellationToken);

        // Calculate due date: 16th of following month (ADR-018-R8)
        var dueDate = CalculateDueDate(month, year);

        var f24 = new F24Record
        {
            OrgId = orgId,
            ReferenceMonth = month,
            ReferenceYear = year,
            TotalAmount = totalAmount,
            TaxCode = "1919",
            DueDate = dueDate,
            CreatedAt = DateTime.UtcNow
        };

        db.Set<F24Record>().Add(f24);
        await db.SaveChangesAsync(cancellationToken);

        return f24;
    }

    /// <summary>
    /// Calculates the F24 due date: 16th of the month following the reference month.
    /// </summary>
    private static DateTime CalculateDueDate(int month, int year)
    {
        // Move to next month
        var nextMonth = month == 12 ? 1 : month + 1;
        var nextYear = month == 12 ? year + 1 : year;

        // 16th of the following month
        return new DateTime(nextYear, nextMonth, 16, 0, 0, 0, DateTimeKind.Utc);
    }
}
