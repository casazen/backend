using System.Net.Http.Headers;
using System.Text;

namespace Casazen.Tests.Integration;

internal static class LeaseSigningTestClient
{
    public const string DefaultStipulaDate = "2026-08-20";

    public static readonly byte[] SignedContractPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% contratto firmato da tutte le parti\n%%EOF\n");

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
