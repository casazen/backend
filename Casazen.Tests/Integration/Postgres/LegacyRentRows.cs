using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Rent schedule and installment rows for migration tests that seed an older schema (the sibling of <see cref="LegacyLeaseRows"/>,
/// same rule): saving a <see cref="RentLedgerEntry"/> through the model writes every column of TODAY's table, among them the ones
/// that LR-01 adds (<c>LastReminderAt</c>, <c>ReminderCount</c>) and those of the migration after it, and before the migration that
/// adds them <c>42703: column "LastReminderAt" of relation "RentLedgerEntries" does not exist</c>. These statements name only the
/// columns that exist since <c>AddRentCollection</c> and that a row needs (the NOT NULL ones without a default, plus the amounts
/// and the state): every column added afterwards is nullable or has a database default. For a column of a later migration run an
/// <c>UPDATE</c> after the migration that adds it. <c>LegacyRowsSchemaTests</c> proves, without a database, that each statement
/// fits the schema at the migration points where a test uses it.
/// </summary>
internal static class LegacyRentRows
{
    public static Task InsertScheduleAsync(AppDbContext db, RentSchedule schedule) =>
        db.Database.ExecuteSqlInterpolatedAsync(ScheduleStatement(schedule));

    public static Task InsertInstallmentAsync(AppDbContext db, RentLedgerEntry installment) =>
        db.Database.ExecuteSqlInterpolatedAsync(InstallmentStatement(installment));

    internal static FormattableString ScheduleStatement(RentSchedule schedule) => $"""
        INSERT INTO "RentSchedules" (
            "Id", "OrgId", "LeaseContractId", "Cadence", "BillingDayOfMonth", "Currency", "Amount", "NextRunDate",
            "IsActive", "CreatedAt", "UpdatedAt")
        VALUES ({schedule.Id}, {schedule.OrgId}, {schedule.LeaseContractId}, {(int)schedule.Cadence}, {schedule.BillingDayOfMonth},
            {schedule.Currency}, {schedule.Amount}, {schedule.NextRunDate}, {schedule.IsActive}, {schedule.CreatedAt},
            {schedule.UpdatedAt});
        """;

    internal static FormattableString InstallmentStatement(RentLedgerEntry installment) => $"""
        INSERT INTO "RentLedgerEntries" (
            "Id", "OrgId", "LeaseContractId", "RentScheduleId", "PeriodStart", "PeriodEnd", "DueDate", "AmountDue", "Status",
            "PaymentIntentCount", "IsVatExempt", "StampDutyAmount", "CreatedAt", "UpdatedAt")
        VALUES ({installment.Id}, {installment.OrgId}, {installment.LeaseContractId}, {installment.RentScheduleId},
            {installment.PeriodStart}, {installment.PeriodEnd}, {installment.DueDate}, {installment.AmountDue},
            {(int)installment.Status}, {installment.PaymentIntentCount}, {installment.IsVatExempt}, {installment.StampDutyAmount},
            {installment.CreatedAt}, {installment.UpdatedAt});
        """;
}
