using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A proof the host uploaded, for one flat it covers: the proof, the flat, what it proves, the day it is about and when it was uploaded. No file key
/// and no person: the rows of the traffic light and of the purge of the documents read this and nothing more.
/// </summary>
/// <param name="Id">The proof.</param>
/// <param name="PropertyId">The flat it covers (one row for each flat of a proof).</param>
/// <param name="Kind">What it proves.</param>
/// <param name="Day">The day it is about, a Europe/Rome calendar date.</param>
/// <param name="UploadedAt">When it was uploaded (UTC).</param>
internal sealed record CoveredProof(Guid Id, Guid PropertyId, ManualProofKind Kind, DateOnly Day, DateTime UploadedAt);

/// <summary>
/// The one reading of which flats a proof covers (E20/F02/US01/T01, NF-43, NF-47, decision O3): the traffic light of the stays, the per-stay status and
/// the purge of the documents after the receipt all ask it, so a stay is «covered by a receipt» in exactly one way. A receipt of the Alloggiati
/// communication of a day is the host's word that it covers the stays of the flats it names that arrived that day; the content of the file is never read.
/// </summary>
/// <remarks>
/// <para>A proof covers the flat it was filed under (<see cref="ManualComplianceProof.PropertyId"/>) and the flats of its
/// <see cref="ManualComplianceProof.CoveredProperties"/>. The flat it was filed under is read from the proof itself, not only from its line in
/// <c>ManualComplianceProofProperties</c>: a proof written without a line there (an older row the migration did not backfill, a row a test or a seed
/// wrote by hand) proves its own flat, as it always did. A proof and a flat are one row even when both reads find them.</para>
/// <para>The reads are of one org and of the flats asked for: nothing outside them is read, so the caller's reach narrows what comes back.</para>
/// </remarks>
internal static class ManualProofCoverage
{
    /// <summary>
    /// The proofs that cover any of <paramref name="propertyIds"/>, one row for each (proof, flat) pair, for the days from <paramref name="from"/> to
    /// <paramref name="to"/> (both included; <c>null</c> = no last day). Empty for no flat: it never means «every flat».
    /// </summary>
    public static async Task<List<CoveredProof>> ReadAsync(
        AppDbContext db,
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        DateOnly from,
        DateOnly? to,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(propertyIds);

        if (propertyIds.Count == 0)
            return [];

        var flats = propertyIds.Distinct().ToList();

        // The flat the proof was filed under.
        var filed = db.ManualComplianceProofs
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && flats.Contains(p.PropertyId) && p.Day >= from);
        if (to is { } lastFiled)
            filed = filed.Where(p => p.Day <= lastFiled);

        var rows = await filed
            .Select(p => new CoveredProof(p.Id, p.PropertyId, p.Kind, p.Day, p.UploadedAt))
            .ToListAsync(ct);

        // The flats it also covers.
        var covered = db.ManualComplianceProofProperties
            .AsNoTracking()
            .Where(link => link.OrgId == orgId && flats.Contains(link.PropertyId) && link.Proof.Day >= from);
        if (to is { } lastCovered)
            covered = covered.Where(link => link.Proof.Day <= lastCovered);

        rows.AddRange(await covered
            .Select(link => new CoveredProof(link.ProofId, link.PropertyId, link.Proof.Kind, link.Proof.Day, link.Proof.UploadedAt))
            .ToListAsync(ct));

        return rows.DistinctBy(row => (row.Id, row.PropertyId)).ToList();
    }
}
