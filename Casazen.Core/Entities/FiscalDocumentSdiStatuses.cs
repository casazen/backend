namespace Casazen.Core.Entities;

/// <summary>
/// The states of a fiscal document on the SDI (the Italian exchange system of electronic invoices, NF-84, E06/F01/US02/T01), as stable codes stored
/// in <see cref="FiscalDocument.SdiStatus"/>, in the manner of <c>PlatformInvoiceSdiStatuses</c>. The database accepts only these.
/// </summary>
public static class FiscalDocumentSdiStatuses
{
    /// <summary>The document does not go to the SDI (a receipt, a proforma).</summary>
    public const string NotApplicable = "not_applicable";

    /// <summary>The XML is ready and was not sent yet (no provider, or not sent).</summary>
    public const string ToSend = "to_send";

    /// <summary>Sent to the SDI, no outcome yet.</summary>
    public const string Submitted = "submitted";

    /// <summary>Delivered by the SDI.</summary>
    public const string Delivered = "delivered";

    /// <summary>Rejected by the SDI: a corrected document replaces it.</summary>
    public const string Rejected = "rejected";

    /// <summary>The issuer declared it sent outside CasaZen.</summary>
    public const string ManualDeclared = "manual_declared";

    /// <summary>Imported without any information on the SDI.</summary>
    public const string Unknown = "unknown";

    /// <summary>Every state, in the order above.</summary>
    public static IReadOnlyList<string> All { get; } = [NotApplicable, ToSend, Submitted, Delivered, Rejected, ManualDeclared, Unknown];
}
