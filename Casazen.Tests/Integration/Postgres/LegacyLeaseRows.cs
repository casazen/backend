using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Lease rows for migration tests that seed an older schema: the columns every <c>LeaseContracts</c> row needs since
/// <c>LeasePartyRetention</c> (the tenant, status, fiscal regime, dates, rent, the erasure flag and the two timestamps);
/// the others are nullable or have a database default (contract type 0). Same reasoning as
/// <see cref="LegacyPropertyRows"/>: a column that a later migration adds to <see cref="LeaseContract"/> cannot break the
/// test. For the lease columns of a later migration (stipula date, signed PDF, deadlines, contract terms…) run an
/// <c>UPDATE</c> after the migration that adds them.
/// </summary>
internal static class LegacyLeaseRows
{
    public static Task InsertAsync(AppDbContext db, LeaseContract lease) =>
        db.Database.ExecuteSqlInterpolatedAsync(Statement(lease));

    internal static FormattableString Statement(LeaseContract lease) => $"""
        INSERT INTO "LeaseContracts" (
            "Id", "PropertyId", "OrgId", "Status", "FiscalRegime", "StartDate", "EndDate", "MonthlyRent",
            "ErasureRequested", "CreatedAt", "UpdatedAt")
        VALUES ({lease.Id}, {lease.PropertyId}, {lease.OrgId}, {(int)lease.Status}, {(int)lease.FiscalRegime},
            {lease.StartDate}, {lease.EndDate}, {lease.MonthlyRent}, {lease.ErasureRequested}, {lease.CreatedAt},
            {lease.UpdatedAt});
        """;
}
