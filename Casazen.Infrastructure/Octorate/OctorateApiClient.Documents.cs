using System.Text.Json;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The documents Octorate issued (FIS-04, E06/F03/US02/T01): the list of an accommodation and the PDF of one, read only. Same HTTPS client, tenant
/// isolation, rate-limit gate and typed errors as every other call of <see cref="OctorateApiClient"/>.
/// </summary>
/// <remarks>
/// <b>Not proven on the sandbox.</b> The operations are those of the OpenAPI recorded in the requirements (S24): <c>GET /rest/v2/invoice/{codice}</c>
/// lists the documents of an accommodation (filters by reservation, type, payment and <c>fiscalInvoiceGroup</c>) and the PDF that is documented is
/// the summary one, <c>GET /rest/v2/invoice/pdf/{acc}/summary/{idDocumentEncrypted}</c>; Octorate documents no download of the PDF or the XML of a
/// fiscal document. The sandbox list answers 200 with an empty <c>data</c> (no invoice module, S25), so the member names below are the OpenAPI's and
/// were never seen with content: the import stays behind <c>Features:OctorateDocumentImport</c> (off) until they are verified on a licensed structure.
/// </remarks>
public sealed partial class OctorateApiClient
{
    /// <summary>The largest PDF CasaZen proxies (20 MB): a larger answer is an error, never a cut file.</summary>
    internal const int MaxPdfBytes = 20 * 1024 * 1024;

    /// <summary>The documents of an accommodation per page of the list.</summary>
    internal const int DocumentPageSize = 100;

    /// <summary>The pages of documents read at most in one call; more is an answer that is not understood.</summary>
    internal const int MaxDocumentPages = 100;

    public async Task<OctorateFiscalDocument[]> GetDocumentsAsync(string accommodationId, DateTime modifiedAfter, CancellationToken ct)
    {
        var id = CheckedAccommodationId(accommodationId);
        var from = AsUtcInstant(modifiedAfter).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var documents = new List<OctorateFiscalDocument>();
        for (var page = 0; page < MaxDocumentPages; page++)
        {
            using var answer = await GetJsonAsync(
                $"/rest/v2/invoice/{Uri.EscapeDataString(id)}?modifiedFrom={from}&size={DocumentPageSize}&page={page}",
                "documents",
                ct);
            var items = ItemsOf(answer.RootElement);
            foreach (var item in items)
            {
                if (DocumentOf(item) is { } document)
                    documents.Add(document);
            }

            if (items.Count < DocumentPageSize)
                return [.. documents];
        }

        throw new OctorateServerException(null);
    }

    public async Task<Stream> GetDocumentPdfAsync(string accommodationId, string documentId, CancellationToken ct)
    {
        var id = CheckedAccommodationId(accommodationId);
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("A document id is required.", nameof(documentId));

        var answer = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                Address($"/rest/v2/invoice/pdf/{Uri.EscapeDataString(id)}/summary/{Uri.EscapeDataString(documentId.Trim())}")),
            "document-pdf",
            ct,
            binary: true);
        var bytes = answer.Bytes ?? [];
        if (bytes.Length == 0 || bytes.Length > MaxPdfBytes || !IsPdf(bytes))
            throw new OctorateServerException(answer.StatusCode);

        return new MemoryStream(bytes, writable: false);
    }

    /// <summary>The documents of an answer: the <c>data</c> of a v2 answer, or a bare array.</summary>
    private static List<JsonElement> ItemsOf(JsonElement root)
    {
        var list = root.ValueKind == JsonValueKind.Array ? root : OctorateJson.Find(root, "data") ?? default;
        return list.ValueKind == JsonValueKind.Array ? [.. list.EnumerateArray()] : [];
    }

    /// <summary>One document of the list; <c>null</c> for an item without an id (it cannot be imported idempotently).</summary>
    internal static OctorateFiscalDocument? DocumentOf(JsonElement item)
    {
        var id = OctorateJson.Text(item, "id", "documentId", "idDocument");
        if (id is null)
            return null;

        var encryptedId = OctorateJson.Text(item, "idDocumentEncrypted", "encryptedId");
        return new OctorateFiscalDocument(
            encryptedId ?? id,
            OctorateJson.Text(item, "refer", "reservationId", "reservation"),
            TypeOf(OctorateJson.Text(item, "type", "documentType")),
            OctorateJson.Text(item, "number", "documentNumber", "progressive"),
            OctorateJson.DateOf(OctorateJson.Text(item, "date", "documentDate", "issueDate")),
            OctorateJson.Decimal(item, "taxable", "taxableAmount", "netAmount"),
            OctorateJson.Decimal(item, "vat", "vatAmount", "tax"),
            OctorateJson.Decimal(item, "total", "totalAmount", "amount"),
            OctorateJson.Text(item, "currency")?.ToUpperInvariant(),
            OctorateJson.Bool(item, "paid"),
            OctorateJson.Text(item, "fiscalInvoiceGroup")?.ToUpperInvariant(),
            OctorateJson.Bool(item, "pdf", "hasPdf", "pdfAvailable") ?? encryptedId is not null,
            OctorateJson.Text(item, "holderName", "customerName", "name"));
    }

    /// <summary>The type of a document by the name the OpenAPI gives it; an unknown name is <see cref="OctorateFiscalDocumentType.Unknown"/>.</summary>
    internal static OctorateFiscalDocumentType TypeOf(string? type) => type?.ToUpperInvariant() switch
    {
        "INVOICE" => OctorateFiscalDocumentType.Invoice,
        "CORRECTING_INVOICE" => OctorateFiscalDocumentType.CorrectingInvoice,
        "SIMPLIFIED_INVOICE" => OctorateFiscalDocumentType.SimplifiedInvoice,
        "FISCALRECEIPT" => OctorateFiscalDocumentType.FiscalReceipt,
        "GENERICRECEIPT" => OctorateFiscalDocumentType.GenericReceipt,
        "CREDIT_NOTE" => OctorateFiscalDocumentType.CreditNote,
        "PROFORMA" => OctorateFiscalDocumentType.Proforma,
        "SUMMARY_DOCUMENT" => OctorateFiscalDocumentType.SummaryDocument,
        _ => OctorateFiscalDocumentType.Unknown,
    };

    private static bool IsPdf(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F';
}
