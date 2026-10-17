using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// What the HTTP tests of the guided lease draft (LR-12) share: the bodies of the requests, the way an answer is read and checked,
/// and the walk through the five data steps with valid values. The values are the ones of <c>LeaseDraftTestData</c> (a valid fiscal
/// code for each party), so a lease made here passes every rule of the flow.
/// </summary>
internal static class LeaseDraftHttp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const string LandlordFiscalCode = "RSSMRA80A01H501U";

    public const string TenantFiscalCode = "VRDGLI85B02F205A";

    public static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>The body of one save: the version it was made on, the step and the values of the step (a JSON object).</summary>
    public static StringContent SaveBody(int version, int step, string values) =>
        Body($$"""{"version":{{version}},"step":{{step}},"values":{{values}}}""");

    public static string PropertyValues(Guid propertyId) => $$"""{"propertyId":"{{propertyId}}"}""";

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

    /// <summary>The answer must have this status; returns its body (default for an empty one), cloned so it outlives the response.</summary>
    public static async Task<JsonElement> ExpectAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode}: {text}");
        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>The answer must be a problem with this status and this stable code, and a localized text.</summary>
    public static async Task<JsonElement> ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var problem = await ExpectAsync(response, status);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()), "the problem has a localized text");
        return problem;
    }

    public static async Task<(Guid Id, int Version)> CreateAsync(HttpClient client, Guid propertyId, string? origin = null)
    {
        var json = origin is null ? PropertyValues(propertyId) : $$"""{"propertyId":"{{propertyId}}","origin":"{{origin}}"}""";
        var created = await ExpectAsync(await client.PostAsync("/api/leases/drafts", Body(json)), HttpStatusCode.Created);
        return (created.GetProperty("id").GetGuid(), created.GetProperty("version").GetInt32());
    }

    public static async Task<JsonElement> PatchAsync(
        HttpClient client, Guid id, int version, int step, string values, HttpStatusCode status = HttpStatusCode.OK) =>
        await ExpectAsync(await client.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, step, values)), status);

    /// <summary>Saves the five data steps with valid values; returns the draft and the version after the last save.</summary>
    public static async Task<(Guid Id, int Version)> CompleteAsync(
        HttpClient client, Guid propertyId, string tenantFiscalCode = TenantFiscalCode, string startDate = "2026-11-01")
    {
        var (id, version) = await CreateAsync(client, propertyId);
        version = (await PatchAsync(client, id, version, 1, PropertyValues(propertyId))).GetProperty("version").GetInt32();
        version = (await PatchAsync(client, id, version, 2, $$"""
            {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"{{LandlordFiscalCode}}","citizenship":"IT","residence":"Bari, via Sparano 1","contactEmail":"mario@example.com"}],
             "tenants":[{"firstName":"Giulia","lastName":"Verdi","fiscalCode":"{{tenantFiscalCode}}","citizenship":"IT","residence":"Bari, via Argiro 7"}]}
            """)).GetProperty("version").GetInt32();
        version = (await PatchAsync(client, id, version, 3, """{"contractType":"Libero","taxRegime":"CedolareSecca","monthlyRent":900,"securityDeposit":1800}""")).GetProperty("version").GetInt32();
        version = (await PatchAsync(client, id, version, 4, $$"""{"startDate":"{{startDate}}"}""")).GetProperty("version").GetInt32();
        version = (await PatchAsync(client, id, version, 5, "{}")).GetProperty("version").GetInt32();
        return (id, version);
    }

    /// <summary>The codes of what is missing in a step of a <c>flow</c>.</summary>
    public static IReadOnlyList<string> NeedCodes(JsonElement flow, int step) =>
        flow.GetProperty("steps")[step - 1].GetProperty("needs").EnumerateArray().Select(n => n.GetProperty("code").GetString()!).ToList();

    public static string StateOf(JsonElement flow, int step) => flow.GetProperty("steps")[step - 1].GetProperty("state").GetString()!;

    /// <summary>Starts the requests together and waits for all of them (few at a time: each holds a connection while it waits for the row lock).</summary>
    public static async Task<IReadOnlyList<HttpResponseMessage>> InParallelAsync(IEnumerable<Func<Task<HttpResponseMessage>>> requests)
    {
        var start = new TaskCompletionSource();
        var tasks = requests.Select(request => Task.Run(async () =>
        {
            await start.Task;
            return await request();
        })).ToList();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }
}
