using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed class StatementService(AppDbContext db) : IStatementService
{
    public async Task<OwnerStatement> GenerateStatementAsync(
        Guid propertyOwnerId,
        Guid propertyId,
        int month,
        int year,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.Set<OwnerStatement>()
            .Where(s => s.PropertyOwnerId == propertyOwnerId && s.PropertyId == propertyId && s.Month == month && s.Year == year)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            throw new DomainRuleException("statement_already_exists", "StatementAlreadyExists");
        }

        var ownership = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyOwnerId == propertyOwnerId && po.PropertyId == propertyId && po.OwnershipEnd == null)
            .FirstOrDefaultAsync(cancellationToken);

        if (ownership is null)
        {
            throw new DomainRuleException("owner_does_not_own_property", "OwnerDoesNotOwnProperty");
        }

        var property = await db.Set<Property>()
            .Where(p => p.Id == propertyId)
            .FirstOrDefaultAsync(cancellationToken);

        if (property is null)
        {
            throw new NotFoundException($"Property {propertyId} not found");
        }

        var allOwnerships = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyId == propertyId && po.OwnershipEnd == null)
            .ToListAsync(cancellationToken);

        var today = DateOnly.FromDateTime(RomeCalendar.TodayAt(DateTimeOffset.UtcNow));
        var ownerIds = allOwnerships.Select(po => po.PropertyOwnerId).Append(propertyOwnerId).Distinct().ToList();
        var ownershipIds = allOwnerships.Select(po => po.Id).Append(ownership.Id).Distinct().ToList();
        var mandates = await db.Set<OwnerMandate>()
            .Where(om => om.OrgId == property.OrgId
                && (ownerIds.Contains(om.PropertyOwnerId) || (om.PropertyOwnershipId != null && ownershipIds.Contains(om.PropertyOwnershipId.Value))))
            .ToListAsync(cancellationToken);
        var mandate = OwnerMandateRules.InForce(mandates, propertyOwnerId, ownership.Id, today);

        if (allOwnerships.Count > 1)
        {
            var commissionRates = new List<decimal?>();
            foreach (var own in allOwnerships)
                commissionRates.Add(OwnerMandateRules.InForce(mandates, own.PropertyOwnerId, own.Id, today)?.CommissionPercent);

            if (commissionRates.Distinct().Count() > 1)
            {
                throw new DomainRuleException("mixed_commission_coownership_not_supported", "MixedCommissionCoOwnershipNotSupported");
            }
        }

        var statement = new OwnerStatement
        {
            OrgId = property.OrgId,
            PropertyOwnerId = propertyOwnerId,
            PropertyId = propertyId,
            Month = month,
            Year = year,
            Currency = StayCurrency.ForProperty(property),
            Status = StatementStatus.Draft,
            OwnershipPercent = ownership.SharePercent,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Set<OwnerStatement>().Add(statement);
        await db.SaveChangesAsync(cancellationToken);

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

        foreach (var payment in payments)
        {
            var cashYear = payment.ProcessedAt!.Value.Year;
            var stayYear = payment.Booking.CheckInDate.Year;

            var grossRevenue = payment.Amount;
            var commission = mandate != null
                ? grossRevenue * mandate.CommissionPercent / 100m
                : 0m;

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

        var result = await RecalculateStatementAsync(statement.Id, cancellationToken);

        await RecalculateLastOwnerIfAllStatementsExistAsync(propertyId, month, year, cancellationToken);

        return result;
    }

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

        statement.TotalRevenue = statement.Lines.Sum(l => l.GrossRevenue);
        statement.Commission = statement.Lines.Sum(l => l.Commission);
        statement.Expenses = statement.Lines.Sum(l => l.Expenses);
        statement.WithholdingTax = statement.Lines.Sum(l => l.Withholding);

        var netBeforeRounding = statement.TotalRevenue - statement.Commission - statement.Expenses - statement.WithholdingTax;

        var allOwnerships = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyId == statement.PropertyId && po.OwnershipEnd == null)
            .OrderBy(po => po.PropertyOwnerId)
            .ToListAsync(cancellationToken);

        if (allOwnerships.Count > 1)
        {
            var isLastOwner = allOwnerships[^1].PropertyOwnerId == statement.PropertyOwnerId;

            if (isLastOwner)
            {
                var otherStatements = await db.Set<OwnerStatement>()
                    .Where(s => s.PropertyId == statement.PropertyId
                        && s.Month == statement.Month
                        && s.Year == statement.Year
                        && s.PropertyOwnerId != statement.PropertyOwnerId)
                    .ToListAsync(cancellationToken);

                if (otherStatements.Count == allOwnerships.Count - 1)
                {
                    var propertyTotal = netBeforeRounding / (statement.OwnershipPercent / 100m);
                    var otherOwnersTotal = otherStatements.Sum(s => s.NetPayable);
                    statement.NetPayable = Math.Round(propertyTotal, 2, MidpointRounding.ToEven) - otherOwnersTotal;
                }
                else
                {
                    statement.NetPayable = Math.Round(netBeforeRounding, 2, MidpointRounding.ToEven);
                }
            }
            else
            {
                statement.NetPayable = Math.Round(netBeforeRounding, 2, MidpointRounding.ToEven);
            }
        }
        else
        {
            statement.NetPayable = Math.Round(netBeforeRounding, 2, MidpointRounding.ToEven);
        }

        statement.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return statement;
    }

    private async Task RecalculateLastOwnerIfAllStatementsExistAsync(
        Guid propertyId,
        int month,
        int year,
        CancellationToken cancellationToken)
    {
        var allOwnerships = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyId == propertyId && po.OwnershipEnd == null)
            .OrderBy(po => po.PropertyOwnerId)
            .ToListAsync(cancellationToken);

        if (allOwnerships.Count <= 1)
            return;

        var allStatements = await db.Set<OwnerStatement>()
            .Where(s => s.PropertyId == propertyId && s.Month == month && s.Year == year)
            .ToListAsync(cancellationToken);

        if (allStatements.Count != allOwnerships.Count)
            return;

        var lastOwnerId = allOwnerships[^1].PropertyOwnerId;
        var lastOwnerStatement = allStatements.FirstOrDefault(s => s.PropertyOwnerId == lastOwnerId);

        if (lastOwnerStatement?.Status == StatementStatus.Draft)
        {
            await RecalculateStatementAsync(lastOwnerStatement.Id, cancellationToken);
        }
    }
}
