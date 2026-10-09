using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Service request rows (a request of a host to a supplier) for migration tests that seed an older schema: the columns the
/// releases before the schedule, the showcase and the payments (SP-04, SP-10, SP-15a) wrote. Every column those added is nullable
/// or has a database default, so a request written like this is a host's request of the short-rent or long-rent context. Same
/// reasoning as <see cref="LegacyPropertyRows"/>. The org, the property and the supplier org must exist. <c>LegacyRowsSchemaTests</c>
/// proves, without a database, that the statement fits the schema at the migration points where tests use it.
/// </summary>
internal static class LegacyServiceRequestRows
{
    public static Task InsertAsync(AppDbContext db, ServiceRequest request) =>
        db.Database.ExecuteSqlInterpolatedAsync(Statement(request));

    internal static FormattableString Statement(ServiceRequest request) => $"""
        INSERT INTO "ServiceRequests" (
            "Id", "OrgId", "PropertyId", "SupplierOrgId", "RentalContext", "Category", "Urgency", "Notes", "Status",
            "ChargeToGuest", "CreatedAt", "UpdatedAt")
        VALUES ({request.Id}, {request.OrgId}, {request.PropertyId}, {request.SupplierOrgId}, {(int)request.RentalContext},
            {request.Category}, {(int)request.Urgency}, {request.Notes}, {(int)request.Status}, {request.ChargeToGuest},
            {request.CreatedAt}, {request.UpdatedAt});
        """;
}
