namespace Casazen.Core.Services;

/// <summary>
/// The import of the documents Octorate issued (FIS-04, NF-84, D19, E06/F03/US02/T01): read-only in CasaZen, linked to their booking, with their
/// payment and SDI states turned into "To do" items. Octorate creates no fiscal document through the API: this only reads.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Behind two flags.</b> <c>Features:OctorateConnector</c> and <c>Features:OctorateDocumentImport</c> (off): the documents list is documented in the
/// OpenAPI but never seen with content (the sandbox has no invoice module, S25), so the field names are unverified; nothing is written while either is
/// off, and only for an org whose issuer is Octorate with a connected connection.</item>
/// <item><b>Tenant.</b> The import writes only the org of the connection, under its Octorate scope; a booking is linked only within that org.</item>
/// <item><b>Never two issuers hidden.</b> A booking of an imported document is locked to Octorate; when CasaZen already issued for it, the imported
/// document is stored anyway and an urgent "To do" says <c>two_issuers_detected</c>.</item>
/// </list>
/// </remarks>
public interface IOctorateDocumentImportService
{
    /// <summary>
    /// Reads the documents of every mapped accommodation of the org changed after <paramref name="since"/> (the last import, else the last
    /// <see cref="OctorateDocumentImport.FirstReadDays"/> days) and upserts them by (org, Octorate id). A call Octorate refuses is logged with ids only
    /// and counted; the next run reads it again.
    /// </summary>
    Task<OctorateDocumentImportResult> ImportAsync(Guid orgId, DateTime? since, CancellationToken ct = default);

    /// <summary>The orgs an import runs for: the issuer is Octorate and the connection of this environment is connected.</summary>
    Task<IReadOnlyList<Guid>> OrgsToImportAsync(CancellationToken ct = default);

    /// <summary>When the org's imported documents last changed, the start of the next read (with an overlap); <c>null</c> before the first import.</summary>
    Task<DateTime?> LastImportedAtAsync(Guid orgId, CancellationToken ct = default);
}

/// <summary>What one import of an org did.</summary>
public sealed record OctorateDocumentImportResult(int Created, int Updated, int Skipped, int FailedReads, int TwoIssuers);

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

    /// <summary>The reason of the urgent "To do" of a booking with documents of two issuers.</summary>
    public const string TwoIssuersReason = "two_issuers_detected";

    /// <summary>The entity type of the "To do" about the SDI state of a document.</summary>
    public const string SdiEntityType = "FiscalDocument";

    /// <summary>The entity type of the "To do" about the payment of a document.</summary>
    public const string PaymentEntityType = "FiscalDocumentPayment";

    /// <summary>The entity type of the "To do" about the issuers of a booking.</summary>
    public const string IssuersEntityType = "BookingIssuers";
}

/// <summary>The stable codes of the errors of the documents of Octorate, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class OctorateDocumentErrors
{
    /// <summary>404: the document is not one of the caller's org (or does not exist).</summary>
    public const string DocumentNotFound = "fiscal_document_not_found";

    /// <summary>404: Octorate holds no PDF of the document, or the PDF cannot be read through the API.</summary>
    public const string PdfUnavailable = "octorate_pdf_unavailable";
}
