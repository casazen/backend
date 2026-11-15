namespace Casazen.Core.Services;

/// <summary>
/// Deletes the documents of the guests of a stay once the stay is covered by an archived Alloggiati receipt (E20/F02/US01/T02, NF-121, NF-16, decision O4, step B11 of
/// the scenario: «the guest's document is deleted after the receipt»). Behind <c>Features:PurgeDocumentsAfterReceipt</c>, <b>off by default</b>: the deletion cannot be
/// undone, so production turns it on only after the test environment has shown it. With the flag off a run reads nothing and deletes nothing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>When a stay is covered.</b> A receipt of the Alloggiati communication that names the flat of the stay (a receipt may cover several flats,
/// E20/F02/US01/T01) and whose day is the arrival day of the stay. A stay only «entrusted» to the channel manager, with no receipt, deletes nothing; neither does
/// a stay that owes no communication (a request, a cancelled stay, a no-show): the receipt of a day proves the stays that were communicated.</item>
/// <item><b>What goes.</b> The photos of identity documents a verification kept (<c>DocumentImage</c>: the encrypted objects of the private bucket and the rows,
/// also the rows a verification already marked deleted) and the legacy scan of the booker (<c>Guest.DocumentScanUrl</c>: the object and the reference). The scan
/// belongs to the guest, not to the stay, so it waits until no other stay of that guest still waits for its receipt.</item>
/// <item><b>What stays.</b> The document data on <c>StayGuest</c> (number, type, dates, place of issue): it is the legal record of the communication and stays
/// until the existing Alloggiati retention (<c>Gdpr:Retention:AlloggiatiData</c>). Never a booking, a payment or a receipt.</item>
/// <item><b>Idempotent, and told only in counts.</b> A second run finds nothing. For each stay that lost something one line of the log with the ids of the org and of
/// the stay and the counts (never a name, a number or a key), and one line in the send log of the stay («Documenti cancellati dopo la ricevuta», with the date).</item>
/// <item><b>Isolation.</b> A system job over the orgs it is asked for: every query names the org, and a run for one org touches no other.</item>
/// </list>
/// </remarks>
public interface IGuestDocumentPurgeService
{
    /// <summary>
    /// One run over the stays of <paramref name="orgId"/> (the run queued right after a receipt upload) or, with <c>null</c>, of every org (the daily job
    /// <c>guest-document-purge</c>). Each stay is deleted and saved on its own, so one that fails does not undo the others.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A document could not be deleted from the storage (the run went on with the other stays and fails at its end, so Hangfire repeats it): the rows and references
    /// of the stays that failed are still there, and the next run deletes what is left.
    /// </exception>
    Task<GuestDocumentPurgeResult> PurgeAsync(Guid? orgId, CancellationToken ct);
}

/// <summary>What a run deleted: counts only, never a name, a number or a key.</summary>
/// <param name="Enabled">Whether <c>Features:PurgeDocumentsAfterReceipt</c> was on: with it off nothing was read or deleted.</param>
/// <param name="Stays">The stays that lost a document.</param>
/// <param name="Images">The rows of <c>DocumentImages</c> removed (each one a document of a verification, front and back together) with their objects.</param>
/// <param name="Scans">The legacy scans of a booker deleted.</param>
public sealed record GuestDocumentPurgeResult(bool Enabled, int Stays, int Images, int Scans)
{
    /// <summary>The answer of a run while the flag is off.</summary>
    public static GuestDocumentPurgeResult Off { get; } = new(false, 0, 0, 0);
}
