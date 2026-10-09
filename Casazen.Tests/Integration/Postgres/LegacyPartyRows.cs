using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Party rows (a signer of a lease) for migration tests that seed an older schema: the columns every <c>Parties</c> row needs since
/// <c>LeaseMultipleParties</c> (LT-14, the order among the parties of a role). The others are nullable or have a database default
/// (the anonymization of LT-12). Same reasoning as <see cref="LegacyPropertyRows"/>: a column that a later migration adds to
/// <see cref="Party"/> cannot break the test. The lease must exist. <c>LegacyRowsSchemaTests</c> proves, without a database, that
/// the statement fits the schema at the migration points where tests use it.
/// </summary>
internal static class LegacyPartyRows
{
    public static Task InsertAsync(AppDbContext db, Party party) =>
        db.Database.ExecuteSqlInterpolatedAsync(Statement(party));

    internal static FormattableString Statement(Party party) => $"""
        INSERT INTO "Parties" (
            "Id", "LeaseContractId", "Role", "Position", "FirstName", "LastName", "FiscalCode", "Citizenship", "ContactEmail",
            "IsExtraEU")
        VALUES ({party.Id}, {party.LeaseContractId}, {(int)party.Role}, {party.Position}, {party.FirstName}, {party.LastName},
            {party.FiscalCode}, {party.Citizenship}, {party.ContactEmail}, {party.IsExtraEU});
        """;
}
