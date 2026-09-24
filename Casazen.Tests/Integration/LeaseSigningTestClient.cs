using System.Net.Http.Headers;
using System.Text;

namespace Casazen.Tests.Integration;

/// <summary>Offline signature through the API (LT-02): the helpers the lease integration tests share.</summary>
internal static class LeaseSigningTestClient
{
    /// <summary>A stipula before the start date of the test leases (1/9/2026): RLI deadline 19/9/2026.</summary>
    public const string DefaultStipulaDate = "2026-08-20";

    /// <summary>A minimal file with the PDF signature: the API checks the content, not the declared type.</summary>
    public static readonly byte[] SignedContractPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% contratto firmato da tutte le parti\n%%EOF\n");

    /// <summary><c>POST /api/leases/{id}/signed-document</c> with the signed PDF and the stipula date.</summary>
    public static async Task<HttpResponseMessage> UploadSignedContractAsync(
        HttpClient client,
        Guid leaseId,
        string stipulaDate = DefaultStipulaDate,
        byte[]? content = null,
        string fileName = "contratto-firmato.pdf")
    {
        using var form = new MultipartFormDataContent { { new StringContent(stipulaDate), "stipulaDate" } };
        var file = new ByteArrayContent(content ?? SignedContractPdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "signedContract", fileName);
        return await client.PostAsync($"/api/leases/{leaseId}/signed-document", form);
    }
}
