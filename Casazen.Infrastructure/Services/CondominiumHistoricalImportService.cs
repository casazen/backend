using System.Data;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Condominium;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Stages a CondoLedger snapshot and, once a house is tied to a verified property, posts it through the condominium book.
/// Rows that are still unmapped are left staged and are not read back as balances.
/// </summary>
public sealed class CondominiumHistoricalImportService(
    AppDbContext db,
    ICondominiumAccountingService condominiums,
    ILogger<CondominiumHistoricalImportService> logger) : ICondominiumHistoricalImportService
{
    public const string ReadOnlyEnvironment = "CONDOLEDGER_READ_ONLY";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CondominiumImportBatchView> StageAsync(
        HostScope scope, string snapshotJson, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireUser(userId);
        CondoExportV1.Snapshot snapshot;
        try
        {
            snapshot = CondoExportV1.Parse(snapshotJson);
        }
        catch (FormatException)
        {
            throw new DomainRuleException(CondominiumImportErrorCodes.Invalid, "CondominiumImportInvalid");
        }

        var existing = await db.CondominiumImportBatches
            .Include(batch => batch.Houses).ThenInclude(house => house.Years)
            .FirstOrDefaultAsync(batch => batch.OrgId == scope.OrgId && batch.ContentHash == snapshot.ContentHash, cancellationToken);
        if (existing is not null)
            return ToView(existing, alreadyImported: true);

        var batch = new CondominiumImportBatch
        {
            OrgId = scope.OrgId,
            ContentHash = snapshot.ContentHash,
            Format = CondoExportV1.Format,
        };
        foreach (var house in snapshot.Houses)
        {
            var row = new CondominiumImportHouse
            {
                OrgId = scope.OrgId,
                SourceId = Trim(house.SourceId, CondominiumImportHouse.SourceIdMaxLength),
                Name = Trim(house.Name, CondominiumImportHouse.NameMaxLength),
                Location = house.Location is null ? null : Trim(house.Location, CondominiumImportHouse.LocationMaxLength),
                FiscalStartMonth = house.FiscalStartMonth,
            };
            foreach (var year in house.Years)
            {
                row.Years.Add(new CondominiumImportYear
                {
                    OrgId = scope.OrgId,
                    SourceId = Trim(year.SourceId, CondominiumImportYear.SourceIdMaxLength),
                    Label = Trim(year.Label, CondominiumImportYear.LabelMaxLength),
                    StartDate = year.Start,
                    EndDate = year.End,
                    OrdinaryCents = year.OrdinaryCents,
                    ExtraordinaryCents = year.ExtraordinaryCents,
                    FinalCents = year.FinalCents,
                    PriorCents = year.PriorCents,
                    PaymentCents = year.PaymentCents,
                    SettlementCents = year.SettlementCents,
                    InstallmentCount = year.InstallmentCount,
                    InstallmentTotalCents = year.InstallmentTotalCents,
                    Cadence = year.Cadence,
                    PaymentsJson = JsonSerializer.Serialize(year.Payments, JsonOptions),
                });
            }

            batch.Houses.Add(row);
        }

        db.CondominiumImportBatches.Add(batch);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex))
        {
            // The same hash was inserted by a request that passed the read above. Return that batch.
            Detach(batch);
            var stored = await db.CondominiumImportBatches
                .Include(item => item.Houses).ThenInclude(house => house.Years)
                .FirstOrDefaultAsync(item => item.OrgId == scope.OrgId && item.ContentHash == snapshot.ContentHash, cancellationToken);
            if (stored is null)
                throw;
            logger.LogInformation("CondoLedger snapshot {BatchId} was already staged", stored.Id);
            return ToView(stored, alreadyImported: true);
        }

        logger.LogInformation("CondoLedger snapshot {BatchId} staged ({Houses} houses)", batch.Id, batch.Houses.Count);
        return ToView(batch, alreadyImported: false);
    }

    public async Task<CondominiumImportBatchView> VerifyPropertyAsync(
        HostScope scope, Guid batchId, Guid houseId, Guid propertyId, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireUser(userId);
        var batch = await RequireBatchAsync(scope, batchId, cancellationToken);
        var house = batch.Houses.FirstOrDefault(item => item.Id == houseId)
            ?? throw NotFound();
        if (house.Status == CondominiumImportStatus.Confirmed)
            return ToView(batch, alreadyImported: true);

        var property = await db.Properties
            .Where(item => item.Id == propertyId && item.OrgId == scope.OrgId)
            .InScope(scope)
            .Select(item => new { item.Id })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Property not found")
            {
                Code = CondominiumErrorCodes.PropertyNotFound,
                MessageKey = "CondominiumPropertyNotFound",
            };
        house.PropertyId = property.Id;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(batch, alreadyImported: false);
    }

    public async Task<CondominiumImportBatchView> ConfirmAsync(
        HostScope scope, Guid batchId, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireUser(userId);
        await using var transaction = await BeginUnitOfWorkAsync(cancellationToken);
        try
        {
            var batch = await RequireBatchAsync(scope, batchId, cancellationToken);
            foreach (var house in batch.Houses.Where(item => item.PropertyId is not null && item.Status != CondominiumImportStatus.Confirmed))
            {
                var condominiumId = await ExistingCondominiumIdAsync(scope, house.PropertyId!.Value, cancellationToken);
                var pending = new List<CondominiumImportYear>();
                foreach (var year in house.Years.OrderBy(item => item.StartDate))
                {
                    if (condominiumId is { } existing && await YearAlreadyInBookAsync(scope, house, existing, year, cancellationToken))
                        continue;
                    pending.Add(year);
                }

                if (pending.Count == 0)
                    continue;

                if (condominiumId is null)
                {
                    var condominium = await condominiums.CreateCondominiumAsync(
                        scope,
                        new CreateCondominiumCommand(house.Name, null, null, null, house.FiscalStartMonth),
                        userId,
                        cancellationToken);
                    await condominiums.LinkPropertyAsync(
                        scope, condominium.Id, new LinkPropertyCommand(house.PropertyId!.Value, null, null, null, null), userId, cancellationToken);
                    condominiumId = condominium.Id;
                }

                foreach (var year in pending)
                    await PostYearAsync(scope, condominiumId.Value, year, userId, cancellationToken);

                house.CondominiumId = condominiumId;
                house.Status = CondominiumImportStatus.Confirmed;
            }

            if (batch.Houses.Any(item => item.Status == CondominiumImportStatus.Confirmed))
            {
                batch.Status = batch.Houses.All(item => item.Status == CondominiumImportStatus.Confirmed)
                    ? CondominiumImportStatus.Confirmed
                    : CondominiumImportStatus.Staged;
                batch.ConfirmedAt = DateTime.UtcNow;
                batch.ConfirmedByUserId = userId;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("CondoLedger snapshot {BatchId} confirmed into CasaZen", batch.Id);
            return ToView(batch, alreadyImported: false);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task PostYearAsync(
        HostScope scope, Guid condominiumId, CondominiumImportYear year, string userId, CancellationToken cancellationToken)
    {
        var period = await condominiums.CreatePeriodAsync(
            scope, condominiumId, new CreateCondominiumPeriodCommand(year.Label), userId, cancellationToken);
        // Closing the previous year seeds this period with that settlement. The file's prior is the one that must match,
        // and it stays off the rates: the staged installment total is only the export's rates.
        if (year.PriorCents == 0)
            await RemoveCarriedPriorAsync(period.Id, cancellationToken);
        else
        {
            await condominiums.RecordPriorYearBalanceAsync(
                scope,
                condominiumId,
                period.Id,
                new RecordPriorYearBalanceCommand(
                    CondoExportV1.FromCents(year.PriorCents), PriorYearBalanceDisposition.CarryForward, null, "Import CondoLedger"),
                userId,
                cancellationToken);
        }

        if (year.OrdinaryCents > 0 || year.ExtraordinaryCents > 0)
        {
            if (year.Cadence is not { } cadence || year.OrdinaryCents <= 0)
                throw Mismatch(year.Label, "installments");
            IReadOnlyList<ExtraordinaryExpenseCommand>? extraordinary = year.ExtraordinaryCents == 0
                ? null
                : [new ExtraordinaryExpenseCommand(CondoExportV1.FromCents(year.ExtraordinaryCents), "Straordinario")];
            await condominiums.ConfirmBudgetAsync(
                scope,
                condominiumId,
                period.Id,
                new ConfirmBudgetCommand(CondoExportV1.FromCents(year.OrdinaryCents), "Preventivo", extraordinary, cadence),
                userId,
                cancellationToken);
        }

        var payments = JsonSerializer.Deserialize<List<CondoExportV1.Payment>>(year.PaymentsJson, JsonOptions) ?? [];
        foreach (var payment in payments)
        {
            await condominiums.RecordPaymentAsync(
                scope,
                condominiumId,
                period.Id,
                new RecordCondominiumPaymentCommand(
                    null, false, CondoExportV1.FromCents(payment.AmountCents), payment.PaidOn, "Import", null, payment.SourceId, null),
                userId,
                cancellationToken);
        }

        if (year.FinalCents > 0)
        {
            await condominiums.ConfirmFinalAsync(
                scope,
                condominiumId,
                period.Id,
                new ConfirmFinalCommand(CondoExportV1.FromCents(year.FinalCents), PriorYearBalanceDisposition.CarryForward),
                userId,
                cancellationToken);
        }

        var report = await condominiums.GetReportAsync(scope, condominiumId, period.Id, cancellationToken);
        if (!Same(report.Ordinary, year.OrdinaryCents)
            || !Same(report.Extraordinary, year.ExtraordinaryCents)
            || !Same(report.FinalAmount ?? 0m, year.FinalCents)
            || !Same(report.Prior, year.PriorCents)
            || !Same(report.Paid, year.PaymentCents)
            || !Same(report.Settlement?.Amount ?? 0m, year.SettlementCents)
            || report.Installments.Count != year.InstallmentCount
            || !Same(report.Installments.Sum(row => row.Due), year.InstallmentTotalCents))
        {
            throw Mismatch(year.Label, "cents");
        }
    }

    private async Task<Guid?> ExistingCondominiumIdAsync(HostScope scope, Guid propertyId, CancellationToken cancellationToken)
    {
        var links = await db.CondominiumUnits
            .Where(unit => unit.OrgId == scope.OrgId && unit.PropertyId == propertyId)
            .Select(unit => new { unit.CondominiumId, unit.ValidFrom, unit.ValidTo, unit.CreatedAt })
            .ToListAsync(cancellationToken);
        var current = links
            .OrderByDescending(unit => unit.ValidFrom is null && unit.ValidTo is null)
            .ThenByDescending(unit => unit.CreatedAt)
            .FirstOrDefault();
        return current?.CondominiumId;
    }

    private async Task<bool> YearAlreadyInBookAsync(
        HostScope scope, CondominiumImportHouse house, Guid condominiumId, CondominiumImportYear year, CancellationToken cancellationToken)
    {
        var periodId = await db.CondominiumPeriods
            .Where(period => period.CondominiumId == condominiumId && period.Label == year.Label)
            .Select(period => (Guid?)period.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (periodId is { } id)
        {
            if (await db.CondominiumStatements.AnyAsync(statement => statement.CondominiumPeriodId == id, cancellationToken))
                return true;
            if (await db.CondominiumPayments.AnyAsync(payment => payment.CondominiumPeriodId == id, cancellationToken))
                return true;
        }

        return await db.CondominiumImportYears.AnyAsync(
            item => item.OrgId == scope.OrgId
                && item.HouseId != house.Id
                && item.Label == year.Label
                && item.House.PropertyId == house.PropertyId
                && item.House.Status == CondominiumImportStatus.Confirmed,
            cancellationToken);
    }

    private async Task RemoveCarriedPriorAsync(Guid periodId, CancellationToken cancellationToken)
    {
        var prior = await db.CondominiumPriorBalances
            .FirstOrDefaultAsync(balance => balance.CondominiumPeriodId == periodId, cancellationToken);
        if (prior is null)
            return;
        db.CondominiumPriorBalances.Remove(prior);
        await db.SaveChangesAsync(cancellationToken);
    }

    private void Detach(CondominiumImportBatch batch)
    {
        foreach (var year in batch.Houses.SelectMany(house => house.Years))
            db.Entry(year).State = EntityState.Detached;
        foreach (var house in batch.Houses)
            db.Entry(house).State = EntityState.Detached;
        db.Entry(batch).State = EntityState.Detached;
    }

    private async Task<CondominiumImportBatch> RequireBatchAsync(HostScope scope, Guid batchId, CancellationToken cancellationToken) =>
        await db.CondominiumImportBatches
            .Include(batch => batch.Houses).ThenInclude(house => house.Years)
            .FirstOrDefaultAsync(batch => batch.Id == batchId && batch.OrgId == scope.OrgId, cancellationToken)
        ?? throw NotFound();

    private async Task<IDbContextTransaction?> BeginUnitOfWorkAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return null;
        return await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }

    private static bool Same(decimal amount, long cents) => CondoExportV1.ToCents(amount) == cents;

    private static CondominiumImportBatchView ToView(CondominiumImportBatch batch, bool alreadyImported) =>
        new(
            batch.Id,
            batch.ContentHash,
            alreadyImported,
            batch.Houses.Any(house => house.Status == CondominiumImportStatus.Confirmed),
            batch.Houses.Any(house => house.Status == CondominiumImportStatus.Confirmed) ? ReadOnlyEnvironment : null,
            batch.Houses.Select(house => new CondominiumImportHouseView(
                house.Id,
                house.SourceId,
                house.Name,
                house.PropertyId,
                house.Status == CondominiumImportStatus.Confirmed,
                house.Status == CondominiumImportStatus.Confirmed && house.PropertyId is not null,
                house.Years.OrderBy(year => year.StartDate).Select(year => new CondominiumImportYearView(
                    year.Label,
                    CondoExportV1.FromCents(year.OrdinaryCents),
                    CondoExportV1.FromCents(year.ExtraordinaryCents),
                    CondoExportV1.FromCents(year.FinalCents),
                    CondoExportV1.FromCents(year.PriorCents),
                    CondoExportV1.FromCents(year.PaymentCents),
                    CondoExportV1.FromCents(year.SettlementCents),
                    year.InstallmentCount,
                    CondoExportV1.FromCents(year.InstallmentTotalCents))).ToList())).ToList());

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    private static void RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > CondominiumImportBatch.UserIdMaxLength)
            throw new DomainRuleException(CondominiumImportErrorCodes.Invalid, "CondominiumImportInvalid");
    }

    private static NotFoundException NotFound() =>
        new("Import not found")
        {
            Code = CondominiumImportErrorCodes.NotFound,
            MessageKey = "CondominiumImportNotFound",
        };

    private static DomainRuleException Mismatch(string label, string field) =>
        new(CondominiumImportErrorCodes.Mismatch, "CondominiumImportMismatch", label, field);
}
