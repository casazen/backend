using Casazen.Core.Entities;
using Casazen.Core.Search;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Search;

/// <summary>
/// The search keys (UI-13a) for the in-memory provider, which has no generated columns: the key PostgreSQL computes for a row
/// (<see cref="SearchKeyModel"/>), written here from the same columns with the same <see cref="SearchText.Fold"/>. The columns of
/// each recipe are those of the SQL expression of <see cref="SearchKeyModel"/>; <c>GlobalSearchPostgresTests</c> compares what the
/// database computes with what this writes for the same rows, so the two cannot drift apart unnoticed.
/// </summary>
internal static class SearchKeysForTests
{
    public static string PropertyText(Property p) => $"{p.Name} {p.City} {p.CinCode}";

    public static string GuestText(Guest g) => $"{g.LastName} {g.FirstName} {g.Email}";

    public static string PartyText(Party p) => $"{p.LastName} {p.FirstName}";

    public static string ServiceRequestText(ServiceRequest r) =>
        $"{r.ServiceNameSnapshot} {r.Category} {r.PublicCode} {r.LocationCity}";

    public static string SupplierProfileText(SupplierProfile s) => s.LegalName;

    /// <summary>Writes the key of every searchable row of the database, then saves: what the generated columns do on PostgreSQL.</summary>
    public static async Task ComputeAsync(AppDbContext db)
    {
        db.ChangeTracker.Clear();

        foreach (var row in await db.Properties.IgnoreQueryFilters().ToListAsync())
            Set(db, row, PropertyText(row));
        foreach (var row in await db.Guests.IgnoreQueryFilters().ToListAsync())
            Set(db, row, GuestText(row));
        foreach (var row in await db.Parties.IgnoreQueryFilters().ToListAsync())
            Set(db, row, PartyText(row));
        foreach (var row in await db.ServiceRequests.IgnoreQueryFilters().ToListAsync())
            Set(db, row, ServiceRequestText(row));
        foreach (var row in await db.SupplierProfiles.IgnoreQueryFilters().ToListAsync())
            Set(db, row, SupplierProfileText(row));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static void Set(AppDbContext db, object row, string text)
    {
        var property = db.Entry(row).Property(SearchKeyModel.KeyProperty);
        property.CurrentValue = SearchText.Fold(text);
        property.IsModified = true;
    }
}
