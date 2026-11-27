namespace Casazen.Core.Entities;

/// <summary>
/// The states of a fiscal document on the SDI (the Italian exchange system of electronic invoices, NF-84, E06/F01/US02/T01), as stable codes stored
/// in <see cref="FiscalDocument.SdiStatus"/>, in the manner of <c>PlatformInvoiceSdiStatuses</c>. The database accepts only these.
/// </summary>
public static class FiscalDocumentSdiStatuses
{
    public const string NotApplicable = "not_applicable";

    public const string ToSend = "to_send";

    public const string Submitted = "submitted";

    public const string Delivered = "delivered";

    public const string Rejected = "rejected";

    public const string ManualDeclared = "manual_declared";

    public const string Unknown = "unknown";

    public static IReadOnlyList<string> All { get; } = [NotApplicable, ToSend, Submitted, Delivered, Rejected, ManualDeclared, Unknown];
}
