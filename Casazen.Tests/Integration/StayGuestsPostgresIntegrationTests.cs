using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-12 (A5-02) on real PostgreSQL: every guest of a stay is registered (one Alloggiati line each, head of family
/// first), the document is required only for a single guest or a head, the guests are tenant data, and the official
/// code tables are imported from a file by an admin. The code files used here are SYNTHETIC TEST DATA (codes starting
/// with 9, marked "TEST"), not official Alloggiati codes.
/// </summary>
public class StayGuestsPostgresIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string OwnerRole = "PropertyOwner";

    private readonly CasazenWebApplicationFactory _factory;

    public StayGuestsPostgresIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task PortalSubmit_FamilyOfFour_GuestSummaryHasFourLinesHeadFirstWithCorrectKinds()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        await SetDeclaredGuestsAsync(seed.BookingId, 4);
        var token = await CreatePortalSessionAsync(seed.BookingId);
        var guestClient = _factory.CreateClient();

        var submit = await guestClient.PostAsync($"/api/public/checkin/{token}", Json(new
        {
            guests = new object[]
            {
                Guest("HeadOfFamily", "Luigi", "Male", "1980-02-01", withDocument: true),
                Guest("FamilyMember", "Anna", "Female", "1982-06-15"),
                Guest("FamilyMember", "Marco", "Male", "2014-09-03"),
                Guest("FamilyMember", "Sofia", "Female", DateTime.UtcNow.Date.AddYears(-3).ToString("yyyy-MM-dd")),
            },
            gdprConsent = true,
            marketingConsent = false,
        }));

        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var owner = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);
        var response = await owner.GetAsync($"/api/alloggiati/{seed.BookingId}/guest-summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var rows = root.GetProperty("guests").EnumerateArray().ToList();

        Assert.Equal(4, rows.Count);
        Assert.Equal(4, root.GetProperty("declaredGuests").GetInt32());
        Assert.Equal(
            new[] { "HeadOfFamily", "FamilyMember", "FamilyMember", "FamilyMember" },
            rows.Select(r => r.GetProperty("type").GetString()));
        Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Select(r => r.GetProperty("position").GetInt32()));
        Assert.Equal(new[] { "Luigi", "Anna", "Marco", "Sofia" }, rows.Select(r => r.GetProperty("firstName").GetString()));
        Assert.Equal(new[] { true, false, false, false }, rows.Select(r => r.GetProperty("requiresDocument").GetBoolean()));
        Assert.Equal(new[] { false, false, true, true }, rows.Select(r => r.GetProperty("isMinor").GetBoolean()));
        Assert.Equal("*****567", rows[0].GetProperty("documentNumberMasked").GetString());
        Assert.Equal(new[] { "GuestPortal", "GuestPortal", "GuestPortal", "GuestPortal" }, rows.Select(r => r.GetProperty("dataSource").GetString()));
        Assert.All(rows, r => Assert.Equal(0, r.GetProperty("missingFields").GetArrayLength()));
        Assert.True(root.GetProperty("dataComplete").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.StayGuests.Where(s => s.BookingId == seed.BookingId).OrderBy(s => s.Position).ToListAsync();
        Assert.Equal(4, stored.Count);
        Assert.Equal(seed.GuestId, stored[0].GuestId);
        Assert.All(stored.Skip(1), s => Assert.Null(s.GuestId));
        Assert.All(stored, s => Assert.Equal(stored[0].OrgId, s.OrgId));
    }

    [PostgresFact]
    public async Task PortalSubmit_DocumentMissing_IsRequiredOnlyForTheHeadOfFamily()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var token = await CreatePortalSessionAsync(seed.BookingId);
        var guestClient = _factory.CreateClient();

        var headWithoutDocument = await guestClient.PostAsync($"/api/public/checkin/{token}", Json(new
        {
            guests = new object[]
            {
                Guest("HeadOfFamily", "Luigi", "Male", "1980-02-01"),
                Guest("FamilyMember", "Anna", "Female", "1982-06-15"),
            },
            gdprConsent = true,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, headWithoutDocument.StatusCode);
        using (var problem = JsonDocument.Parse(await headWithoutDocument.Content.ReadAsStringAsync()))
        {
            var fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(
                new[] { "Guests[0].DocumentIssuePlaceName", "Guests[0].DocumentNumber", "Guests[0].DocumentType" },
                fields);
        }

        var membersWithoutDocument = await guestClient.PostAsync($"/api/public/checkin/{token}", Json(new
        {
            guests = new object[]
            {
                Guest("HeadOfFamily", "Luigi", "Male", "1980-02-01", withDocument: true),
                Guest("FamilyMember", "Anna", "Female", "1982-06-15"),
            },
            gdprConsent = true,
        }));

        Assert.Equal(HttpStatusCode.OK, membersWithoutDocument.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.StayGuests.Where(s => s.BookingId == seed.BookingId).OrderBy(s => s.Position).ToListAsync();
        Assert.Equal("YA1234567", stored[0].DocumentNumber);
        Assert.Equal(GuestDocumentType.Passport, stored[0].DocumentType);
        Assert.Equal(string.Empty, stored[1].DocumentNumber);
        Assert.Null(stored[1].DocumentType);
    }

    [PostgresFact]
    public async Task StayGuests_UserOfOtherOrg_CannotReadOrReplaceThem()
    {
        var seedA = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var seedB = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        using var clientB = _factory.CreateAuthenticatedClient(seedB.OwnerId, OwnerRole);

        var read = await clientB.GetAsync($"/api/alloggiati/{seedA.BookingId}/guest-summary");
        var replace = await clientB.PutAsync($"/api/alloggiati/{seedA.BookingId}/stay-guests", Json(new
        {
            guests = new object[] { Guest("SingleGuest", "Intruso", "Male", "1970-01-01", withDocument: true) },
        }));

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, replace.StatusCode);
        Assert.DoesNotContain("AB123456", await read.Content.ReadAsStringAsync());

        // Directly through the tenant query filter: org B neither reads nor bulk-deletes org A's guests.
        using var scope = _factory.Services.CreateScope();
        var orgIdB = await OrgOfAsync(seedB.BookingId);
        await using (var dbB = NewDb(scope, new FixedTenantContext(orgIdB)))
        {
            Assert.Empty(await dbB.StayGuests.Where(s => s.BookingId == seedA.BookingId).ToListAsync());
            Assert.Equal(2, await dbB.StayGuests.CountAsync(s => s.BookingId == seedB.BookingId));
            Assert.Equal(0, await dbB.StayGuests.Where(s => s.BookingId == seedA.BookingId).ExecuteDeleteAsync());
        }

        await using var unfiltered = NewDb(scope);
        var rowsA = await unfiltered.StayGuests.Where(s => s.BookingId == seedA.BookingId).OrderBy(s => s.Position).ToListAsync();
        Assert.Equal(new[] { "Luigi", "Sofia" }, rowsA.Select(r => r.FirstName));
        Assert.All(rowsA, r => Assert.NotEqual(orgIdB, r.OrgId));
    }

    [PostgresFact]
    public async Task HostReplace_OwnBooking_SavesTheGuestsAndValidatesTheOrder()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        using var owner = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);

        var invalid = await owner.PutAsync($"/api/alloggiati/{seed.BookingId}/stay-guests", Json(new
        {
            guests = new object[] { Guest("GroupMember", "Anna", "Female", "1982-06-15") },
        }));
        var valid = await owner.PutAsync($"/api/alloggiati/{seed.BookingId}/stay-guests", Json(new
        {
            guests = new object[]
            {
                Guest("HeadOfGroup", "Luigi", "Male", "1980-02-01", withDocument: true),
                Guest("GroupMember", "Paolo", "Male", "1981-03-02"),
                Guest("GroupMember", "Anna", "Female", "1982-06-15"),
            },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()))
        {
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Guests[0].Type", out _));
        }

        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var summary = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { "HeadOfGroup", "GroupMember", "GroupMember" },
            summary.RootElement.GetProperty("guests").EnumerateArray().Select(r => r.GetProperty("type").GetString()));
    }

    [PostgresFact]
    public async Task ImportCodeTables_SyntheticTestFiles_ReplaceTheTableAndMakeTheRecordExportReady()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        using var admin = _factory.CreateAuthenticatedClient($"auth0|co12-admin-{Guid.NewGuid():N}", "Admin");
        using var owner = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);

        // A file with an invalid line is rejected as a whole: nothing imported.
        var rejected = await ImportAsync(admin, "Comuni", "Codice;Descrizione;Provincia\n900000001;TEST Milano;MI\n12345678901;TEST Troppo lungo;RM\n");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        using (var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()))
        {
            Assert.Equal("alloggiati_code_import_invalid", problem.RootElement.GetProperty("code").GetString());
            var line = Assert.Single(problem.RootElement.GetProperty("lines").EnumerateArray());
            Assert.Equal((3, "code_invalid"), (line.GetProperty("line").GetInt32(), line.GetProperty("error").GetString()));
        }

        // Synthetic files: names match the seeded guests (Milano, Italia), codes are fake (9...).
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(admin, "Comuni", "Codice;Descrizione;Provincia\r\n900000001;Milano;MI\r\n900000002;\"Reggio nell'Emilia\";RE\r\n")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(admin, "Stati", "Codice\tDescrizione\n900000100\tItalia\n900000101\tTEST Francia\n")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(admin, "Documenti", "Code,Description\nTSTPA,TEST Passaporto\n")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(admin, "TipiAlloggiato", "Codice;Descrizione\n91;Ospite Singolo\n92;Capo Famiglia\n93;Capo Gruppo\n94;Familiare\n95;Membro Gruppo\n")).StatusCode);

        var status = await admin.GetFromJsonAsync<JsonElement>("/api/admin/alloggiati/code-tables");
        Assert.Equal(
            new[] { ("Comuni", 2), ("Stati", 2), ("Documenti", 1), ("TipiAlloggiato", 5) },
            status.EnumerateArray().Select(t => (t.GetProperty("table").GetString() ?? string.Empty, t.GetProperty("rowCount").GetInt32())));

        var search = await owner.GetFromJsonAsync<JsonElement>("/api/alloggiati/codes?list=comuni&q=reggio");
        Assert.Equal("900000002", Assert.Single(search.EnumerateArray()).GetProperty("code").GetString());

        // The seeded head has a document kind but no document table code: only that code is still to complete.
        var before = await owner.GetFromJsonAsync<JsonElement>($"/api/alloggiati/{seed.BookingId}/guest-summary");
        Assert.False(before.GetProperty("exportReady").GetBoolean());
        var head = before.GetProperty("guests")[0];
        Assert.Equal(new[] { "documentType" }, head.GetProperty("codesToComplete").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("92", head.GetProperty("codes").GetProperty("type").GetString());
        Assert.Equal("900000001", head.GetProperty("codes").GetProperty("birthComune").GetString());
        Assert.Equal("900000100", head.GetProperty("codes").GetProperty("citizenship").GetString());
        Assert.Equal("94", before.GetProperty("guests")[1].GetProperty("codes").GetProperty("type").GetString());

        // The host picks the document type from the table: the record is complete and exportable.
        var replace = await owner.PutAsync($"/api/alloggiati/{seed.BookingId}/stay-guests", Json(new
        {
            guests = new object[]
            {
                new
                {
                    type = "HeadOfFamily", firstName = "Luigi", lastName = "Verdi", gender = "Male", dateOfBirth = "1985-03-10",
                    bornInItaly = true, birthComuneCode = "900000001", birthComuneName = "Milano", birthProvince = "MI",
                    citizenshipCode = "900000100", citizenshipName = "Italia", documentTypeCode = "TSTPA",
                    documentNumber = "AB123456", documentIssuePlaceCode = "900000001", documentIssuePlaceName = "Milano",
                },
                Guest("FamilyMember", "Sofia", "Female", DateTime.UtcNow.Date.AddYears(-8).ToString("yyyy-MM-dd")),
            },
        }));
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        using var after = JsonDocument.Parse(await replace.Content.ReadAsStringAsync());
        Assert.True(after.RootElement.GetProperty("exportReady").GetBoolean());
        Assert.Equal(0, after.RootElement.GetProperty("missingCodeTables").GetArrayLength());
        Assert.Equal("TEST Passaporto", after.RootElement.GetProperty("guests")[0].GetProperty("codes").GetProperty("documentTypeDescription").GetString());

        // An unknown code is refused once the table is imported.
        var unknown = await owner.PutAsync($"/api/alloggiati/{seed.BookingId}/stay-guests", Json(new
        {
            guests = new object[]
            {
                new
                {
                    type = "SingleGuest", firstName = "Luigi", lastName = "Verdi", gender = "Male", dateOfBirth = "1985-03-10",
                    bornInItaly = true, birthComuneName = "Milano", birthProvince = "MI", citizenshipName = "Italia",
                    documentTypeCode = "ZZZZZ", documentNumber = "AB123456", documentIssuePlaceName = "Milano",
                },
            },
        }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using (var problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()))
        {
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Guests[0].DocumentTypeCode", out _));
        }

        // A new import replaces the whole table.
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(admin, "Documenti", "Codice;Descrizione\nTSTCI;TEST Carta\n")).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(new[] { "TSTCI" }, await db.AlloggiatiCodeEntries.Where(e => e.Table == AlloggiatiCodeTable.Documenti).Select(e => e.Code).ToListAsync());
        Assert.Equal(2, await db.AlloggiatiCodeTableImports.CountAsync(i => i.Table == AlloggiatiCodeTable.Documenti));
    }

    [PostgresFact]
    public async Task ImportCodeTables_NotAdmin_IsForbidden()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        using var owner = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);

        var response = await ImportAsync(owner, "Comuni", "Codice;Descrizione\n900000009;TEST\n");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static object Guest(string type, string firstName, string gender, string dateOfBirth, bool withDocument = false) => new
    {
        type,
        firstName,
        lastName = "Verdi",
        gender,
        dateOfBirth,
        bornInItaly = true,
        birthComuneName = "Milano",
        birthProvince = "MI",
        citizenshipName = "Italia",
        documentType = withDocument ? "Passport" : null,
        documentNumber = withDocument ? "YA1234567" : null,
        documentIssuePlaceName = withDocument ? "Milano" : null,
    };

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

    private async Task<string> CreatePortalSessionAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.FindAsync(bookingId);
        var service = new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance);
        return await service.CreateSessionAsync(bookingId, booking!.OrgId);
    }

    private async Task SetDeclaredGuestsAsync(Guid bookingId, int guests)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        booking.NumberOfGuests = guests;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> OrgOfAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.Where(b => b.Id == bookingId).Select(b => b.OrgId).SingleAsync();
    }

    private static AppDbContext NewDb(IServiceScope scope, ITenantContext? tenant = null) =>
        new(
            scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(),
            tenant,
            scope.ServiceProvider.GetService<IDataProtectionProvider>());

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
