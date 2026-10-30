using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Casazen.Tests.Integration.LeaseDraftHttp;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-13 on real PostgreSQL, the one rule that costs if it fails: bringing in a contract already signed is <b>not a way round the gate of
/// the models</b>. The same host as <see cref="LeaseDraftPostgresTests"/>, with the real contract templates, which are drafts and not
/// approved (decision LR-D28), "today" 2026-10-09. A lease made in CasaZen is refused by the import and by <c>signed-document</c> (422), a
/// member who may not handle the property is refused (403), and of two imports of the same imported draft at the same moment exactly one
/// goes through (the other 409), with one signed copy, one document and one event, and a third, later, 409 too.
/// </summary>
public class LeaseImportIntegrationTests(LeaseDraftFactory factory) : IClassFixture<LeaseDraftFactory>, IAsyncLifetime
{
    private static readonly byte[] SignedPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% contratto firmato fuori da CasaZen\n%%EOF\n");

    private LongRentWorld _world = null!;

    /// <summary>The property the drafts are written on: owner A's, ready, with no lease (two leases of one property cannot cover the same days, LR-OVERLAP).</summary>
    private Guid _draftProperty;

    public async Task InitializeAsync()
    {
        _world = await LongRentWorld.SeedAsync(factory);
        _draftProperty = await factory.AddReadyPropertyAsync(_world.OrgId, _world.OwnerId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Import_IsNoWayRoundTheGateOfTheModels_AndOfTwoImportsOfTheSameDraftExactlyOneGoesThrough()
    {
        using var client = _world.OwnerClient(factory);

        // A lease made in CasaZen, complete, with a template that is not approved: the same PDF is refused on both paths, nothing is written.
        var (made, _) = await CompleteAsync(client, _draftProperty);
        await ExpectProblemAsync(await PostPdfAsync(client, $"/api/leases/{made}/import"), HttpStatusCode.UnprocessableEntity, "lease_import_not_imported_origin");
        await ExpectProblemAsync(await PostPdfAsync(client, $"/api/leases/{made}/signed-document"), HttpStatusCode.UnprocessableEntity, "contract_template_not_approved");
        var untouched = await LoadAsync(made);
        Assert.Equal(LeaseStatus.Draft, untouched.Status);
        Assert.Null(untouched.SignedPdfStoragePath);
        Assert.DoesNotContain(untouched.Events, e => e.EventType is LeaseEventType.Imported or LeaseEventType.AllPartiesSigned);
        Assert.Empty(StoredFilesOf(made));

        // An imported draft of a property of the owner: a member limited to their own properties may not sign it.
        var (imported, _) = await ImportedDraftAsync(client, _draftProperty);
        using var colleague = _world.ColleagueClient(factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostPdfAsync(colleague, $"/api/leases/{imported}/import")).StatusCode);
        Assert.Equal(LeaseStatus.Draft, (await LoadAsync(imported)).Status);
        Assert.Empty(StoredFilesOf(imported));

        // The owner, twice at the same moment: one signs it without any template, the other finds it signed and writes nothing.
        var responses = await InParallelAsync(
        [
            () => PostPdfAsync(client, $"/api/leases/{imported}/import"),
            () => PostPdfAsync(client, $"/api/leases/{imported}/import"),
        ]);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var lost = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains((await JsonAsync(lost)).GetProperty("code").GetString(), new[] { "lease_not_draft", "lease_draft_conflict" });
        var detail = await JsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal("Signed", detail.GetProperty("status").GetString());
        Assert.Equal("Imported", detail.GetProperty("origin").GetString());
        Assert.True(detail.GetProperty("hasSignedPdf").GetBoolean());

        var lease = await LoadAsync(imported);
        Assert.Equal(LeaseStatus.Signed, lease.Status);
        Assert.Equal(new DateTime(2024, 2, 20), lease.StipulaDate!.Value.Date);
        Assert.Equal(1, lease.Events.Count(e => e.EventType == LeaseEventType.Imported));
        Assert.Equal(1, lease.Events.Count(e => e.EventType == LeaseEventType.AllPartiesSigned));
        Assert.Equal(2, await WithDbAsync(db => db.LeaseSigners.IgnoreQueryFilters().CountAsync(s => s.LeaseContractId == imported && s.Status == LeaseSignerStatus.Signed)));

        // One signed copy and its own copy as the Contract document of the lease; what the lost request had written is gone.
        Assert.Equal(2, StoredFilesOf(imported).Length);
        var documents = await client.GetFromJsonAsync<JsonElement>($"/api/leases/{imported}/documents");
        var document = Assert.Single(documents.EnumerateArray());
        Assert.Equal("Contract", document.GetProperty("kind").GetString());
        var downloaded = await client.GetAsync($"/api/leases/{imported}/documents/{document.GetProperty("id").GetGuid()}/download");
        Assert.Equal(SignedPdf, await downloaded.Content.ReadAsByteArrayAsync());
        Assert.Equal(SignedPdf, await (await client.GetAsync($"/api/leases/{imported}/signed-document")).Content.ReadAsByteArrayAsync());
        var stored = await WithDbAsync(db => db.LeaseDocuments.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.LeaseContractId == imported));
        Assert.NotEqual(lease.SignedPdfStoragePath, stored.StorageKey);

        // And later, again: 409, and nothing more is written.
        await ExpectProblemAsync(await PostPdfAsync(client, $"/api/leases/{imported}/import"), HttpStatusCode.Conflict, "lease_not_draft");
        Assert.Equal(2, StoredFilesOf(imported).Length);
        Assert.Equal(1, (await LoadAsync(imported)).Events.Count(e => e.EventType == LeaseEventType.Imported));
    }

    /// <summary>An imported draft with steps 1 to 4 saved: a libero started in the past, a landlord and a tenant, no APE or model asked.</summary>
    private static async Task<(Guid Id, int Version)> ImportedDraftAsync(HttpClient client, Guid propertyId)
    {
        var (id, version) = await CreateAsync(client, propertyId, "Imported");
        version = (await PatchAsync(client, id, version, 1, PropertyValues(propertyId))).GetProperty("version").GetInt32();
        version = (await PatchAsync(client, id, version, 2, $$"""
            {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"{{LandlordFiscalCode}}","citizenship":"IT","residence":"Bari, via Sparano 1","contactEmail":"mario@example.com"}],
             "tenants":[{"firstName":"Giulia","lastName":"Verdi","fiscalCode":"{{TenantFiscalCode}}","citizenship":"IT","residence":"Bari, via Argiro 7"}]}
            """)).GetProperty("version").GetInt32();
        version = (await PatchAsync(client, id, version, 3, """{"contractType":"Libero","taxRegime":"CedolareSecca","monthlyRent":900,"securityDeposit":1800}""")).GetProperty("version").GetInt32();
        version = (await PatchAsync(client, id, version, 4, """{"startDate":"2024-03-01"}""")).GetProperty("version").GetInt32();
        return (id, version);
    }

    /// <summary>The signed PDF and the day of the signature, to the path of the import or of the offline signature (the same two fields).</summary>
    private static async Task<HttpResponseMessage> PostPdfAsync(HttpClient client, string path)
    {
        using var form = new MultipartFormDataContent { { new StringContent("2024-02-20"), "stipulaDate" } };
        var file = new ByteArrayContent(SignedPdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "signedContract", "contratto-rossi-verdi.pdf");
        return await client.PostAsync(path, form);
    }

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<LeaseContract> LoadAsync(Guid id) =>
        WithDbAsync(db => db.LeaseContracts.IgnoreQueryFilters().AsNoTracking().Include(l => l.Parties).Include(l => l.Events).SingleAsync(l => l.Id == id));

    /// <summary>The files of the lease in the test storage, the signed copy and the document alike (both keys carry the id of the lease).</summary>
    private string[] StoredFilesOf(Guid leaseId) =>
        Directory.Exists(factory.StorageRoot)
            ? Directory.EnumerateFiles(factory.StorageRoot, "*", SearchOption.AllDirectories)
                .Where(path => path.Contains(leaseId.ToString(), StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
}
