using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-13 on real PostgreSQL: the Alloggiati record file is built on request from the stay's guests and the imported
/// official codes, downloaded only by the host of the booking's org, never stored, and downloading it changes no status
/// (decision D6: nothing is "sent" without a real receipt). The code files used here are SYNTHETIC TEST DATA (codes
/// starting with 9, 91-95 for the kinds of guest, descriptions marked "TEST"), not official Alloggiati codes.
/// </summary>
public class AlloggiatiRecordFilePostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string OwnerRole = "PropertyOwner";

    private readonly CasazenWebApplicationFactory _factory;

    public AlloggiatiRecordFilePostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task DownloadRecordFile_ExportReadyStay_ReturnsThePrivateFileAndTheStatusStaysHonest()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        using var admin = _factory.CreateAuthenticatedClient($"auth0|co13-admin-{Guid.NewGuid():N}", "Admin");
        using var owner = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);
        await ImportSyntheticTablesAsync(admin);
        var saved = await owner.PutAsync($"/api/alloggiati/{seed.BookingId}/stay-guests", Json(new
        {
            guests = new object[]
            {
                new
                {
                    type = "HeadOfFamily", firstName = "Luigi", lastName = "D'Verdi-Müller", gender = "Male", dateOfBirth = "1985-03-10",
                    bornInItaly = true, birthComuneCode = "900000001", birthComuneName = "Milano", birthProvince = "MI",
                    citizenshipCode = "900000100", citizenshipName = "Italia", documentTypeCode = "TSTPA",
                    documentNumber = "ab 123456", documentIssuePlaceCode = "900000001", documentIssuePlaceName = "Milano",
                },
                new
                {
                    type = "FamilyMember", firstName = "Sofia", lastName = "Verdi", gender = "Female", dateOfBirth = "2019-06-01",
                    bornInItaly = false, birthCountryCode = "900000101", birthCountryName = "TEST Francia",
                    citizenshipCode = "900000100", citizenshipName = "Italia",
                },
            },
        }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var arrival = await ArrivalOfAsync(seed.BookingId);

        var response = await owner.GetAsync($"/api/alloggiati/{seed.BookingId}/record-file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("private", response.Headers.CacheControl?.ToString());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"alloggiati-{arrival:yyyy-MM-dd}-{seed.BookingId.ToString("N")[..8]}.txt", response.Content.Headers.ContentDisposition?.FileName);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray()); // UTF-8 without byte order mark
        var lines = Encoding.UTF8.GetString(bytes).Split("\r\n");
        Assert.Equal(2, lines.Length); // CR+LF between lines, none after the last
        Assert.All(lines, line => Assert.Equal(168, line.Length));

        // Head of family: kind, arrival, 3 days, names (capitals, no accents), sex, birth, comune/province, Italy, citizenship, document.
        Assert.Equal(
            "92" + $"{arrival:dd/MM/yyyy}" + "03" + "D'VERDI MULLER".PadRight(50) + "LUIGI".PadRight(30) + "1" + "10/03/1985"
            + "900000001" + "MI" + "900000100" + "900000100" + "TSTPA" + "AB123456".PadRight(20) + "900000001",
            lines[0]);
        // Family member born abroad: kind 94, comune and province blank, state of birth France, the three document fields blank.
        Assert.Equal(
            "94" + $"{arrival:dd/MM/yyyy}" + "03" + "VERDI".PadRight(50) + "SOFIA".PadRight(30) + "2" + "01/06/2019"
            + new string(' ', 9) + "  " + "900000101" + "900000100" + new string(' ', 34),
            lines[1]);

        // Downloading is not sending (D6): no report row was created or changed and no status reads sent.
        var status = await owner.GetFromJsonAsync<JsonElement>($"/api/alloggiati/{seed.BookingId}/status");
        Assert.NotEqual("Inviato", status.GetProperty("status").GetString());
        Assert.NotEqual("InviatoManualmente", status.GetProperty("status").GetString());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.AlloggiatiWebReports.AnyAsync(r => r.Status == AlloggiatiWebStatus.Inviato || r.Status == AlloggiatiWebStatus.InviatoManualmente));
    }

    [PostgresFact]
    public async Task DownloadRecordFile_CodeTablesNotImported_Is422WithAStableCodeAndNoFile()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        using var owner = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);

        var response = await owner.GetAsync($"/api/alloggiati/{seed.BookingId}/record-file");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.StartsWith("application/", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("alloggiati_file_not_ready", problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("AB123456", problem.RootElement.GetRawText()); // no personal data in the error
    }

    [PostgresFact]
    public async Task DownloadRecordFile_NameInAnotherAlphabet_Is422AndNamesTheGuestPosition()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        using var admin = _factory.CreateAuthenticatedClient($"auth0|co13-admin-{Guid.NewGuid():N}", "Admin");
        using var owner = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);
        await ImportSyntheticTablesAsync(admin);
        var saved = await owner.PutAsync($"/api/alloggiati/{seed.BookingId}/stay-guests", Json(new
        {
            guests = new object[]
            {
                new
                {
                    type = "SingleGuest", firstName = "Иван", lastName = "Петров", gender = "Male", dateOfBirth = "1985-03-10",
                    bornInItaly = false, birthCountryCode = "900000101", birthCountryName = "TEST Francia",
                    citizenshipCode = "900000100", citizenshipName = "Italia", documentTypeCode = "TSTPA",
                    documentNumber = "AB123456", documentIssuePlaceCode = "900000001", documentIssuePlaceName = "Milano",
                },
            },
        }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var response = await owner.GetAsync($"/api/alloggiati/{seed.BookingId}/record-file");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("alloggiati_file_name_not_representable", problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("Петров", problem.RootElement.GetRawText());
    }

    [PostgresFact]
    public async Task DownloadRecordFile_UserOfAnotherOrg_Gets404AndNoData()
    {
        var seedA = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var seedB = await _factory.SeedConfirmedBookingWithTokenAsync();
        using var admin = _factory.CreateAuthenticatedClient($"auth0|co13-admin-{Guid.NewGuid():N}", "Admin");
        await ImportSyntheticTablesAsync(admin);
        using var intruder = _factory.CreateAuthenticatedClient(seedB.OwnerId, OwnerRole);

        var response = await intruder.GetAsync($"/api/alloggiati/{seedA.BookingId}/record-file");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("VERDI", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AB123456", body);
    }

    [PostgresFact]
    public async Task DownloadRecordFile_Anonymous_Gets401()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);

        var response = await _factory.CreateClient().GetAsync($"/api/alloggiati/{seed.BookingId}/record-file");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<DateTime> ArrivalOfAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.Where(b => b.Id == bookingId).Select(b => b.CheckInDate).SingleAsync();
    }

    private static async Task ImportSyntheticTablesAsync(HttpClient admin)
    {
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(admin, "Comuni", "Codice;Descrizione;Provincia\n900000001;Milano;MI\n")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(admin, "Stati", "Codice;Descrizione\n900000100;Italia\n900000101;TEST Francia\n")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(admin, "Documenti", "Codice;Descrizione\nTSTPA;TEST Passaporto\n")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await ImportAsync(admin, "TipiAlloggiato", "Codice;Descrizione\n91;Ospite Singolo\n92;Capo Famiglia\n93;Capo Gruppo\n94;Familiare\n95;Membro Gruppo\n")).StatusCode);
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, string table, string content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", $"{table}-TEST.csv");
        form.Add(new StringContent("TEST synthetic"), "sourceVersion");
        return await client.PostAsync($"/api/admin/alloggiati/code-tables/{table}", form);
    }
}
