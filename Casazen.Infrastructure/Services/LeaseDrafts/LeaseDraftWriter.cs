using Casazen.Core.Entities;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Infrastructure.Data;

namespace Casazen.Infrastructure.Services.LeaseDrafts;

/// <summary>
/// Writes what the landlord typed where every other reader of the lease looks — the columns and the rows of the parties — and keeps
/// the typed state only while something of it cannot live there (LR-12). The one place the projection is written, shared by the
/// draft service (a save, the confirmation of the review) and by the import of a signed contract (LR-13), which must not sign a
/// lease whose columns are not the values it was checked against. The caller holds the row lock of the lease and the transaction.
/// </summary>
internal static class LeaseDraftWriter
{
    /// <summary>
    /// Projects <paramref name="values"/> into the lease: the rows of the parties, the columns, and the typed state (kept only while
    /// the data are not <paramref name="dataComplete"/>). Stages the changes; the first of the two saves of the parties is already
    /// done when it returns.
    /// </summary>
    public static async Task ProjectAsync(
        AppDbContext db,
        LeaseContract lease,
        LeaseDraftValues values,
        LeaseDraftReading reading,
        ConcordatoAssessmentResult? concordato,
        bool dataComplete,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        await RewritePartiesAsync(db, lease, LeaseDraftProjection.ToRows(values, lease.Id, today), cancellationToken);
        LeaseDraftProjection.ApplyColumns(lease, reading, concordato);
        lease.DraftData = dataComplete ? null : values.ToJson();
    }

    /// <summary>
    /// Makes the rows of the parties the ones the typed values give. The rows are deleted and written again (the entry keeps its id)
    /// in two saves, because the unique index of (lease, role, position) refuses the moment two rows swap places; both saves are
    /// in the transaction of the caller. Nothing is written when the rows are already the right ones.
    /// </summary>
    public static async Task RewritePartiesAsync(AppDbContext db, LeaseContract lease, List<Party> rows, CancellationToken cancellationToken)
    {
        if (LeaseDraftProjection.SameRows(lease.Parties, rows))
            return;

        if (lease.Parties.Count > 0)
        {
            db.Parties.RemoveRange(lease.Parties);
            await db.SaveChangesAsync(cancellationToken);
        }

        db.Parties.AddRange(rows);
    }
}
