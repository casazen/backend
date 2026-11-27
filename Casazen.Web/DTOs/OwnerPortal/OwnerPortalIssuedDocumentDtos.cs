using Casazen.Core.Services;

namespace Casazen.Web.DTOs.OwnerPortal;

/// <summary>
/// A document CasaZen issued in the name of the owner (<c>GET api/owner-portal/issued-documents</c>, E17/F02/US01/T01, NF-116): the number, the day, the kind, the total, the
/// currency and the state of the SDI, and whether it has an XML. Nothing of a guest, not even its name: the PDF and the XML hold what the document says. The PDF is at <c>…/issued-documents/{id}/pdf</c>, the XML (when <c>hasXml</c>) at <c>…/xml</c>.
/// </summary>
/// <param name="DocumentId">The fiscal document.</param>
/// <param name="AgencyId">The agency that issues it for the owner.</param>
/// <param name="AgencyName">The name of the agency.</param>
/// <param name="Number">The number, with the series of the owner.</param>
/// <param name="IssueDate">The day it was issued.</param>
/// <param name="Kind">The kind of document: <c>Invoice</c>, <c>Receipt</c> or <c>CreditNote</c>.</param>
/// <param name="Total">The total, in <paramref name="Currency"/>.</param>
/// <param name="Currency">The currency.</param>
/// <param name="SdiStatus">Where the document is with the SDI: <c>not_applicable</c> (a receipt), <c>to_send</c>, <c>submitted</c>, <c>delivered</c>, <c>rejected</c> or <c>manual_declared</c>.</param>
/// <param name="HasXml">True when the document has a FatturaPA XML to download.</param>
public sealed record OwnerPortalIssuedDocumentDto(
    Guid DocumentId,
    Guid AgencyId,
    string AgencyName,
    string Number,
    DateOnly IssueDate,
    string Kind,
    decimal Total,
    string Currency,
    string SdiStatus,
    bool HasXml)
{
    public static OwnerPortalIssuedDocumentDto From(OwnerPortalIssuedDocumentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new OwnerPortalIssuedDocumentDto(
            info.DocumentId,
            info.AgencyId,
            info.AgencyName,
            info.Number,
            info.IssueDate,
            info.Kind.ToString(),
            info.Total,
            info.Currency,
            info.SdiStatus,
            info.HasXml);
    }
}
