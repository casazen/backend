using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed class ApePhotoWebApplicationFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // The base test host stubs APE checks for unrelated lease tests: exercise the real implementation here.
        builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IApeComplianceService, ApeComplianceService>()));
    }
}

public class ApePhotoUploadIntegrationTests(ApePhotoWebApplicationFactory factory) : IClassFixture<ApePhotoWebApplicationFactory>
{
    [PostgresFact]
    public async Task JpegApe_IsOcrValidated_AndOnlyPdfRemainsDownloadable()
    {
        var owner = $"auth0|ape-photo-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner, "LongTermLandlord");
        var photo = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ape", "complete.jpg"));
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(photo); file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "APE.jpeg"); form.Add(new StringContent("Ape"), "documentType");
        var response = await client.PostAsync($"/api/properties/{property.Id}/documents", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Ape", json.RootElement.GetProperty("documentType").GetString());
        var id = json.RootElement.GetProperty("id").GetGuid();
        var download = await client.GetAsync($"/api/properties/{property.Id}/documents/{id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        var pdf = await download.Content.ReadAsByteArrayAsync();
        Assert.True(pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8));
        Assert.EndsWith(".pdf", json.RootElement.GetProperty("fileName").GetString());
        using var content = new MemoryStream(pdf);
        Assert.True((await new ApeDocumentInspector().InspectAsync(content)).IsValid);
    }
    [PostgresFact]
    public async Task IncompletePhoto_IsKeptWithWhatWasNotRead_AndConfirmedByItsCodeAndClass()
    {
        var owner = $"auth0|ape-incomplete-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner, "LongTermLandlord");
        var photo = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ape", "page-1.jpg"));
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(photo);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "incomplete.jpg");
        form.Add(new StringContent("Ape"), "documentType");
        var response = await client.PostAsync($"/api/properties/{property.Id}/documents", form);
        // A real certificate the reader cannot read whole (only its first page here) is not refused any more: the landlord confirms it.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Incomplete", json.RootElement.GetProperty("apeCheck").GetString());
        Assert.Contains(json.RootElement.GetProperty("apeUnreadFields").EnumerateArray(),
            field => field.GetString()!.Contains("certificatore", StringComparison.OrdinalIgnoreCase));
        var id = json.RootElement.GetProperty("id").GetGuid();
        var documents = await client.GetAsync($"/api/properties/{property.Id}/documents");
        using var list = JsonDocument.Parse(await documents.Content.ReadAsStringAsync());
        Assert.Equal(1, list.RootElement.GetArrayLength());

        var identification = await client.PutAsJsonAsync(
            $"/api/properties/{property.Id}/documents/{id}/ape", new { code = "1610800012345/17", energyClass = "f" });
        Assert.Equal(HttpStatusCode.OK, identification.StatusCode);
        using var confirmed = JsonDocument.Parse(await identification.Content.ReadAsStringAsync());
        Assert.Equal("F", confirmed.RootElement.GetProperty("apeEnergyClass").GetString());
    }

}
