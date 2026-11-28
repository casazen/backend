using System.Globalization;
using System.Text.Json;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

public sealed class CenedApeRegistry(
    HttpClient httpClient,
    IOptions<ApeRegistryOptions> options,
    ILogger<CenedApeRegistry> logger) : IApeRegistryLookup
{
    private const int MaxRows = 10;

    public async Task<ApeRegistryEntry?> FindAsync(ApeRegistryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var settings = options.Value;
        var comune = query.Comune.Trim();
        if (!settings.Enabled || comune.Length == 0 || query.GlobalNonRenewableIndex <= 0
            || !Uri.TryCreate(settings.CenedBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            return null;

        var where = $"upper(comune)=upper('{comune.Replace("'", "''", StringComparison.Ordinal)}') AND ep_gl_nren={query.GlobalNonRenewableIndex.ToString("0.##", CultureInfo.InvariantCulture)}";
        var uri = new Uri(baseUri,
            $"resource/{Uri.EscapeDataString(settings.CenedDataset)}.json?$select=cod_ape,classe_energetica,data_ins&$where={Uri.EscapeDataString(where)}&$limit={MaxRows}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 30)));
            using var response = await httpClient.GetAsync(uri, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("CENED register did not answer. Status={Status}", (int)response.StatusCode);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            var entries = Entries(json.RootElement).ToList();
            if (entries.Select(e => e.Code).Distinct(StringComparer.Ordinal).Count() > 1 && query.IssueDate is { } issued)
                entries = entries.Where(e => e.IssueDate == issued).ToList();
            var codes = entries.Select(e => e.Code).Distinct(StringComparer.Ordinal).ToList();
            logger.LogInformation("CENED register asked. Matches={Matches}", codes.Count);
            return codes.Count == 1 ? entries.First() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogWarning("CENED register not reachable: {Error}", ex.GetType().Name);
            return null;
        }
    }

    private static IEnumerable<ApeRegistryEntry> Entries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var row in root.EnumerateArray())
        {
            var code = Text(row, "cod_ape");
            if (code is null || code.Length is < 4 or > 40)
                continue;
            var energyClass = Text(row, "classe_energetica")?.ToUpperInvariant();
            DateOnly? filed = DateOnly.TryParseExact(Text(row, "data_ins"), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
            yield return new ApeRegistryEntry(code, energyClass is { Length: >= 1 and <= 3 } ? energyClass : null, filed);
        }
    }

    private static string? Text(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
}
