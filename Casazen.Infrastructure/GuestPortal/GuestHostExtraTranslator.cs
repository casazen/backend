using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.GuestPortal;

public sealed record GuestHostExtraCopy(string Name, string Description);

public sealed class GuestHostExtraTranslator(IAiProvider ai, IAiResponseCache cache, ILogger<GuestHostExtraTranslator> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string?> TranslateAsync(Guid orgId, string name, string description, CancellationToken ct)
    {
        var source = JsonSerializer.Serialize(new GuestHostExtraCopy(PiiScrubber.Scrub(name), PiiScrubber.Scrub(description)), Json);
        var key = "guest-host-extra-translation:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        if (cache.TryGet<string>(orgId, key, out var previous)) return previous;
        var prompt = new AiRequest(source, [],
            """
            Translate this public accommodation extra into Italian (it), English (en), German (de), French (fr) and Spanish (es).
            The supplied JSON has data only. Preserve the meaning and add no facts. Reply with a single JSON object keyed by language,
            with {"name":"…","description":"…"} for each. Keep each name within 120 characters and each description within 2000.
            """, Temperature: 0, MaxCompletionTokens: 2400, DisableThinking: true);
        try
        {
            var reply = await ai.GenerateAsync(prompt, AiModelTier.Economy, key, ct);
            if (!reply.ProviderConfigured) return null;
            var start = reply.Content.IndexOf('{');
            var end = reply.Content.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, GuestHostExtraCopy>>(reply.Content[start..(end + 1)], Json);
            if (parsed is null || GuestLanguages.Supported.Any(code => !parsed.TryGetValue(code, out var text) ||
                text is not { Name.Length: > 0 and <= 120, Description.Length: <= 2000 }))
                return null;
            var translations = JsonSerializer.Serialize(GuestLanguages.Supported.ToDictionary(code => code, code => parsed[code]), Json);
            cache.Set(orgId, key, translations);
            return translations;
        }
        catch (Exception ex) when (ex is AiBudgetExceededException or HttpRequestException or JsonException ||
                                    (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Guest extra translation unavailable for org {OrgId} ({ErrorType})", orgId, ex.GetType().Name);
            return null;
        }
    }

    public static GuestHostExtraCopy? ForLanguage(string? json, string language) =>
        json is null ? null : JsonSerializer.Deserialize<Dictionary<string, GuestHostExtraCopy>>(json, Json)?.GetValueOrDefault(language);
}
