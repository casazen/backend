using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Casazen.Core.BankStatements;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The import of a bank statement on the rents, over the real pipeline (PostgreSQL in CI, InMemory in a local run): the upload, who may do
/// it and see what, the offline payment the confirmation goes through, and the refusals. The world is <see cref="LongRentWorld"/> ("today" is
/// 2026-10-09 in Rome): the owner reaches P1 and P2, the colleague only P3, the manager every property.
/// </summary>
public class LongRentBankImportIntegrationTests(LongRentAggregatesFactory factory) : IClassFixture<LongRentAggregatesFactory>, IAsyncLifetime
{
    private const string Preview = "/api/long-rent/rents/bank-import/preview";
    private const string Confirm = "/api/long-rent/rents/bank-import/confirm";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string Statement =
        "Data contabile;Data valuta;Descrizione;Importo\r\n"
        + "03/10/2026;03/10/2026;BONIFICO DA VERDI GIULIA CANONE OTTOBRE 2026;900,00\r\n"
        + "04/10/2026;04/10/2026;BONIFICO DA MARRA VALENTINA CANONE OTTOBRE;900,00\r\n";

    private LongRentWorld _world = null!;

    public async Task InitializeAsync() => _world = await LongRentWorld.SeedAsync(factory);

    public Task DisposeAsync() => Task.CompletedTask;

    private static MultipartFormDataContent Form(string csv, string? selections = null, string? mapping = null, string fileName = "movimenti.csv")
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", fileName } };
        if (selections is not null)
            form.Add(new StringContent(selections), "selections");
        if (mapping is not null)
            form.Add(new StringContent(mapping), "mapping");
        return form;
    }

    private static string Selections(params (string Fingerprint, Guid InstallmentId)[] choices) =>
        JsonSerializer.Serialize(choices.Select(c => new { fingerprint = c.Fingerprint, installmentId = c.InstallmentId }));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);

    private static async Task<JsonElement> PreviewAsync(HttpClient client, string csv)
    {
        var response = await client.PostAsync(Preview, Form(csv));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    private static string FingerprintOf(JsonElement preview, string payer) =>
        preview.GetProperty("suggestions").EnumerateArray().Single(s => s.GetProperty("description").GetString()!.Contains(payer, StringComparison.Ordinal))
            .GetProperty("fingerprint").GetString()!;

    private static List<Guid> InstallmentIds(JsonElement preview) =>
        preview.GetProperty("suggestions").EnumerateArray()
            .SelectMany(s => s.GetProperty("alternatives").EnumerateArray().Append(s.GetProperty("installment")))
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

    // --- Preview ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_TheOwner_GetsTheSureMatchOfItsTenant_AndNeverAnInstallmentOfTheColleaguesProperty()
    {
        using var client = _world.OwnerClient(factory);

        var preview = await PreviewAsync(client, Statement);

        Assert.Equal("Csv", preview.GetProperty("format").GetString());
        var summary = preview.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("movements").GetInt32());
        Assert.Equal(2, summary.GetProperty("credits").GetInt32());
        Assert.Equal(0, summary.GetProperty("alreadyImported").GetInt32());

        var verdi = preview.GetProperty("suggestions").EnumerateArray().Single(s => s.GetProperty("description").GetString()!.Contains("VERDI", StringComparison.Ordinal));
        Assert.Equal("High", verdi.GetProperty("confidence").GetString());
        Assert.Equal(_world.Installment("ActiveOct"), verdi.GetProperty("installment").GetProperty("id").GetGuid());
        Assert.Equal(_world.Active, verdi.GetProperty("installment").GetProperty("leaseId").GetGuid());
        Assert.Equal("Bilocale Sparano", verdi.GetProperty("installment").GetProperty("propertyName").GetString());
        Assert.Equal("Giulia Verdi", verdi.GetProperty("installment").GetProperty("tenantName").GetString());
        Assert.Equal("2026-10-03", verdi.GetProperty("date").GetString());
        Assert.Equal(900m, verdi.GetProperty("amount").GetDecimal());
        Assert.Contains(verdi.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()), r => r == "Surname");

        // The colleague's installment is outside the owner's reach: not proposed, not even as an option.
        Assert.DoesNotContain(_world.Installment("ColleagueOct"), InstallmentIds(preview));
    }

    [Fact]
    public async Task Preview_TheColleagueWithOnePropertyOnly_SeesOnlyItsOwnTenantsMatch()
    {
        using var client = _world.ColleagueClient(factory);

        var preview = await PreviewAsync(client, Statement);

        var only = Assert.Single(preview.GetProperty("suggestions").EnumerateArray());
        Assert.Equal("High", only.GetProperty("confidence").GetString());
        Assert.Equal(_world.Installment("ColleagueOct"), only.GetProperty("installment").GetProperty("id").GetGuid());
        Assert.DoesNotContain(_world.Installment("ActiveOct"), InstallmentIds(preview));
        Assert.Equal(1, preview.GetProperty("summary").GetProperty("unmatched").GetInt32());
        // The tenant of the other property is nowhere in what the colleague gets back as an installment.
        Assert.DoesNotContain("Giulia Verdi", preview.GetProperty("suggestions").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_TheManagerOfTheWholeOrg_GetsBothMatches()
    {
        using var client = _world.ManagerClient(factory);

        var preview = await PreviewAsync(client, Statement);

        Assert.Equal(2, preview.GetProperty("summary").GetProperty("confident").GetInt32());
        Assert.Equal(
            new[] { _world.Installment("ActiveOct"), _world.Installment("ColleagueOct") }.Order(),
            preview.GetProperty("suggestions").EnumerateArray().Select(s => s.GetProperty("installment").GetProperty("id").GetGuid()).Order());
    }

    [Fact]
    public async Task Preview_AnotherOrg_SeesNothingOfThisWorld()
    {
        var stranger = await LongRentWorld.SeedAsync(factory);
        using var client = stranger.ManagerClient(factory);

        var preview = await PreviewAsync(client, Statement);

        // The stranger has its own installments of 900: they are what it is offered, never this world's.
        Assert.DoesNotContain(_world.Installment("ActiveOct"), InstallmentIds(preview));
        Assert.DoesNotContain(_world.Installment("ColleagueOct"), InstallmentIds(preview));
    }

    [Fact]
    public async Task Preview_WithoutThePermissionToDeclarePayments_IsForbidden()
    {
        using var wrongRole = factory.CreateAuthenticatedClient(_world.OwnerId, "PropertyOwner");

        Assert.Equal(HttpStatusCode.Forbidden, (await wrongRole.PostAsync(Preview, Form(Statement))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await wrongRole.PostAsync(Confirm, Form(Statement, Selections((new string('a', 64), _world.Installment("ActiveOct")))))).StatusCode);
    }

    [Fact]
    public async Task Preview_WithoutBeingSignedIn_IsUnauthorized()
    {
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Preview, Form(Statement))).StatusCode);
    }

    // --- Confirm ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_DeclaresTheInstallmentPaidOffline_WithTheDayOfTheCreditAndTheNote_AndTheRegisterShowsIt()
    {
        using var client = _world.OwnerClient(factory);
        var preview = await PreviewAsync(client, Statement);
        var fingerprint = FingerprintOf(preview, "VERDI");

        var response = await client.PostAsync(Confirm, Form(Statement, Selections((fingerprint, _world.Installment("ActiveOct")))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadAsync(response);
        Assert.Equal(1, result.GetProperty("requested").GetInt32());
        var confirmed = Assert.Single(result.GetProperty("confirmed").EnumerateArray());
        Assert.Equal(_world.Installment("ActiveOct"), confirmed.GetProperty("installmentId").GetGuid());
        Assert.Empty(result.GetProperty("skipped").EnumerateArray());

        var ledger = await ReadAsync(await client.GetAsync($"/api/leases/{_world.Active}/rent"));
        var installment = ledger.GetProperty("installments").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == _world.Installment("ActiveOct"));
        Assert.Equal("Paid", installment.GetProperty("status").GetString());
        Assert.Equal("Offline", installment.GetProperty("paidVia").GetString());
        Assert.Equal("2026-10-03", installment.GetProperty("paidOn").GetString());
        Assert.Equal("Da estratto conto", installment.GetProperty("offlinePaymentNote").GetString());

        var register = await ReadAsync(await client.GetAsync("/api/long-rent/rents?month=2026-10&status=Paid"));
        Assert.Contains(
            register.GetProperty("items").EnumerateArray(),
            row => row.GetProperty("id").GetGuid() == _world.Installment("ActiveOct") && row.GetProperty("paidVia").GetString() == "Offline");

        // The credit is not offered any more.
        var again = await PreviewAsync(client, Statement);
        Assert.Equal(1, again.GetProperty("summary").GetProperty("alreadyImported").GetInt32());
        Assert.DoesNotContain(_world.Installment("ActiveOct"), InstallmentIds(again));
    }

    [Fact]
    public async Task Confirm_TheSameChoiceTwice_IsSkippedTheSecondTime()
    {
        using var client = _world.OwnerClient(factory);
        var fingerprint = FingerprintOf(await PreviewAsync(client, Statement), "VERDI");
        var body = Selections((fingerprint, _world.Installment("ActiveOct")));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Confirm, Form(Statement, body))).StatusCode);

        var second = await ReadAsync(await client.PostAsync(Confirm, Form(Statement, body)));

        Assert.Empty(second.GetProperty("confirmed").EnumerateArray());
        var skipped = Assert.Single(second.GetProperty("skipped").EnumerateArray());
        Assert.Equal("bank_movement_already_imported", skipped.GetProperty("code").GetString());
        Assert.Equal(fingerprint, skipped.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task Confirm_AnInstallmentOfAPropertyTheCallerDoesNotReach_IsNotFound_AndStaysUnpaid()
    {
        using var manager = _world.ManagerClient(factory);
        var fingerprint = FingerprintOf(await PreviewAsync(manager, Statement), "VERDI");
        using var colleague = _world.ColleagueClient(factory);

        var response = await colleague.PostAsync(Confirm, Form(Statement, Selections((fingerprint, _world.Installment("ActiveOct")))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadAsync(response);
        Assert.Empty(result.GetProperty("confirmed").EnumerateArray());
        Assert.Equal("rent_installment_not_found", Assert.Single(result.GetProperty("skipped").EnumerateArray()).GetProperty("code").GetString());

        using var owner = _world.OwnerClient(factory);
        var ledger = await ReadAsync(await owner.GetAsync($"/api/leases/{_world.Active}/rent"));
        Assert.Equal(
            "Scheduled",
            ledger.GetProperty("installments").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == _world.Installment("ActiveOct")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Confirm_ACreditForTheWrongAmount_IsSkippedAndNothingIsPaid()
    {
        using var client = _world.OwnerClient(factory);
        var fingerprint = FingerprintOf(await PreviewAsync(client, Statement), "VERDI");

        // 700 is the expiring lease's rent: the credit is 900.
        var response = await client.PostAsync(Confirm, Form(Statement, Selections((fingerprint, _world.Installment("ExpiringOct")))));

        var skipped = Assert.Single((await ReadAsync(response)).GetProperty("skipped").EnumerateArray());
        Assert.Contains(skipped.GetProperty("code").GetString(), new[] { "bank_movement_amount_mismatch", "rent_installment_not_payable" });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("[{\"fingerprint\":\"abc\",\"installmentId\":\"11111111-1111-1111-1111-111111111111\"}]")]
    [InlineData("[{\"fingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"installmentId\":\"00000000-0000-0000-0000-000000000000\"}]")]
    [InlineData("[null]")]
    public async Task Confirm_ChoicesThatAreNotChoices_AreRefused(string? selections)
    {
        using var client = _world.OwnerClient(factory);

        var response = await client.PostAsync(Confirm, Form(Statement, selections));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bank_import_selections_invalid", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    // --- The file ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_ThereIsNoFile_Is400()
    {
        using var client = _world.OwnerClient(factory);

        var response = await client.PostAsync(Preview, new MultipartFormDataContent { { new StringContent("x"), "mapping" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bank_statement_file_missing", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Preview_APdf_Is415()
    {
        using var client = _world.OwnerClient(factory);
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7 not a statement")), "file", "estratto.pdf" } };

        var response = await client.PostAsync(Preview, form);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("bank_statement_format_unsupported", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Preview_AFileTheImporterCannotRead_Is422()
    {
        using var client = _world.OwnerClient(factory);

        var response = await client.PostAsync(Preview, Form("Data;Descrizione;Importo\r\n"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("bank_statement_empty", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Preview_ColumnsTheImporterDoesNotKnow_AreAskedToTheLandlord_WhoThenSaysWhichIsWhich()
    {
        using var client = _world.OwnerClient(factory);
        const string csv = "Foo;Bar;Baz\r\n03/10/2026;BONIFICO DA VERDI GIULIA CANONE OTTOBRE 2026;900,00\r\n";

        var refusal = await client.PostAsync(Preview, Form(csv));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refusal.StatusCode);
        var problem = await ReadAsync(refusal);
        Assert.Equal("bank_statement_columns_unrecognized", problem.GetProperty("code").GetString());
        Assert.Equal(0, problem.GetProperty("headerRow").GetInt32());
        var rows = problem.GetProperty("rows").EnumerateArray().Select(row => row.EnumerateArray().Select(cell => cell.GetString()).ToArray()).ToArray();
        Assert.Equal(new[] { "Foo", "Bar", "Baz" }, rows[0]);
        Assert.Equal("900,00", rows[1][2]);

        var mapped = await client.PostAsync(Preview, Form(csv, mapping: "{\"headerRow\":0,\"date\":0,\"description\":1,\"amount\":2}"));

        Assert.Equal(HttpStatusCode.OK, mapped.StatusCode);
        var suggestion = Assert.Single((await ReadAsync(mapped)).GetProperty("suggestions").EnumerateArray());
        Assert.Equal(_world.Installment("ActiveOct"), suggestion.GetProperty("installment").GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"headerRow\":0}")]
    [InlineData("{\"headerRow\":0,\"date\":0,\"description\":1,\"amount\":9}")]
    [InlineData("{\"headerRow\":0,\"date\":0,\"description\":1}")]
    public async Task Preview_AMappingThatDoesNotFit_Is422(string mapping)
    {
        using var client = _world.OwnerClient(factory);

        var response = await client.PostAsync(Preview, Form("Foo;Bar;Baz\r\n03/10/2026;Canone;900,00\r\n", mapping: mapping));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("bank_statement_mapping_invalid", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Preview_ThePersonalDataOfTheFile_AreNotInAnyHeaderOfTheAnswer()
    {
        using var client = _world.OwnerClient(factory);

        var response = await client.PostAsync(Preview, Form(Statement));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var headers = string.Join('\n', response.Headers.Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
        Assert.DoesNotContain("VERDI", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Giulia", headers, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(nameof(LongRentBankImportController.Preview))]
    [InlineData(nameof(LongRentBankImportController.Confirm))]
    public void TheStatement_IsBufferedInMemory_NeverSpilledToATemporaryFile(string action)
    {
        // ASP.NET Core keeps a multipart file above 64 KB in a temporary file on disk unless the action raises the threshold (ADR-013-R2):
        // a statement is personal data, and the importer promises that it is kept nowhere.
        var method = typeof(LongRentBankImportController).GetMethod(action)!;
        var formLimits = method.GetCustomAttribute<RequestFormLimitsAttribute>();
        var sizeLimit = method.GetCustomAttribute<RequestSizeLimitAttribute>();

        Assert.NotNull(formLimits);
        Assert.NotNull(sizeLimit);
        var largest = Assert.IsType<long>(((IRequestSizeLimitMetadata)sizeLimit).MaxRequestBodySize);
        Assert.True(largest > BankStatementLimits.MaxFileBytes);
        Assert.True(formLimits.MemoryBufferThreshold >= largest);
    }
}
