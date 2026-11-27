namespace Casazen.Core.Services;

/// <summary>
/// The import of the documents Octorate issued (FIS-04, NF-84, D19, E06/F03/US02/T01): read-only in CasaZen, linked to their booking, with their
/// payment and SDI states turned into "To do" items. Octorate creates no fiscal document through the API: this only reads.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Behind two flags and an option.</b> <c>Features:OctorateConnector</c> and <c>Features:OctorateDocumentImport</c> (off), and the option
/// <c>FiscalDocuments:ImportOctorateDocuments</c> (false): CasaZen is the only issuer since 2026-11-14 (E11/F01/US01/T01), so the import never runs by
/// accident. The documents list is documented in the OpenAPI but never seen with content (the sandbox has no invoice module, S25), so the field names are
/// unverified; nothing is written while any of the three is off.</item>
/// <item><b>Tenant.</b> The import writes only the org of the connection, under its Octorate scope; a booking is linked only within that org.</item>
/// <item><b>Never a lock, never two documents.</b> An imported document is stored read-only and linked to its booking: it never locks the issuer of the
/// booking and never raises <c>two_issuers_detected</c>. It is a document issued elsewhere, though: CasaZen issues for every active booking except one that
/// already has a document issued elsewhere, so the booking of an imported document (and a booking a legacy row locked to Octorate) is not issued again by
/// CasaZen (409 <c>document_already_issued</c> / <c>document_issuer_conflict</c>: never two documents for one stay). The import is off by default and no
/// such document or lock exists in the test and production data.</item>
/// </list>
/// </remarks>
public interface IOctorateDocumentImportService
{
    /// <summary>
    /// Reads the documents of every mapped accommodation of the org changed after the last import of that accommodation (else the last
    /// <see cref="OctorateDocumentImport.FirstReadDays"/> days), or after <paramref name="since"/> for all of them when a caller gives one, and upserts them
    /// by (org, Octorate id), each with the accommodation it was read from. A call Octorate refuses is logged with ids only and counted; the date of that
    /// accommodation does not move, so the next run reads it again from where it stopped whatever the other accommodations did.
    /// </summary>
    Task<OctorateDocumentImportResult> ImportAsync(Guid orgId, DateTime? since, CancellationToken ct = default);

    /// <summary>
    /// The orgs an import runs for: none while <c>FiscalDocuments:ImportOctorateDocuments</c> is false (the default); with it, the orgs whose connection of
    /// this environment is connected.
    /// </summary>
    Task<IReadOnlyList<Guid>> OrgsToImportAsync(CancellationToken ct = default);

    /// <summary>When the org's imported documents last changed (informational: every accommodation is read from its own last import); <c>null</c> before the first.</summary>
    Task<DateTime?> LastImportedAtAsync(Guid orgId, CancellationToken ct = default);
}

/// <summary>What one import of an org did.</summary>
public sealed record OctorateDocumentImportResult(int Created, int Updated, int Skipped, int FailedReads);

/// <summary>The constants of the import of the Octorate documents.</summary>
public static class OctorateDocumentImport
{
    /// <summary>How far back the first import of an org reads.</summary>
    public const int FirstReadDays = 400;

    /// <summary>How far before the last change a run starts again, so a document changed during a run is read again.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromDays(2);

    /// <summary>The reason of the "To do" of a document the SDI rejected.</summary>
    public const string SdiRejectedReason = "sdi_rejected_octorate";

    /// <summary>The reason of the "To do" of a document still to collect.</summary>
    public const string ToCollectReason = "to_collect";

    /// <summary>The entity type of the "To do" about the SDI state of a document.</summary>
    public const string SdiEntityType = "FiscalDocument";

    /// <summary>The entity type of the "To do" about the payment of a document.</summary>
    public const string PaymentEntityType = "FiscalDocumentPayment";
}

/// <summary>The stable codes of the errors of the documents of Octorate, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class OctorateDocumentErrors
{
    /// <summary>404: the document is not one of the caller's org (or does not exist).</summary>
    public const string DocumentNotFound = "fiscal_document_not_found";

    /// <summary>404: Octorate holds no PDF of the document, or the PDF cannot be read through the API.</summary>
    public const string PdfUnavailable = "octorate_pdf_unavailable";
}
