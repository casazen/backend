using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Casazen.Tests.Integration;

public class LongRentPropertyAccessIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public LongRentPropertyAccessIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task LongTermLandlord_CanUploadApeDocumentForLeasePrerequisite()
    {
        var ownerId = $"auth0|long-rent-property-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var client = _factory.CreateAuthenticatedClient(ownerId, "LongTermLandlord");
        using var form = new MultipartFormDataContent();
        var pdf = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj <<>> endobj\n%%EOF"));
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", "ape.pdf");
        form.Add(new StringContent("Ape"), "documentType");

        var response = await client.PostAsync($"/api/properties/{property.Id}/documents", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
