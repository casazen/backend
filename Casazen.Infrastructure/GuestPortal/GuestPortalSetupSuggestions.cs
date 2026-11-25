using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.GuestPortal;

public sealed class GuestPortalSetupSuggestions(
    IAiProvider ai, IWebSearchClient web, IAiResponseCache cache, IComuneDirectory comuni, ILogger<GuestPortalSetupSuggestions> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Suggestion?> GenerateAsync(Property property, string kind, CancellationToken ct)
    {
        // Send the structured accommodation facts. The name, street address, access codes and guest data never enter a prompt.
        var facts = JsonSerializer.Serialize(new
        {
            property.AccommodationType,
            property.AreaKind,
            property.MaxGuests,
            property.Beds,
            property.Amenities,
            property.CheckInFrom,
            property.CheckOutBy,
            property.PetsPolicy,
            property.SmokingPolicy,
            property.PartiesPolicy,
        }, Json);
        var town = kind == "local" ? await comuni.FindByIstatCodeAsync(property.ComuneIstatCode, activeOnly: false, ct) : null;
        var prompt = kind switch
        {
            "guide" => """Scrivi in italiano una bozza di guida della casa, usando soltanto i dati disponibili. Non inventare orari, dettagli di accesso, servizi o contatti. Rispondi solo con JSON: {"guide":{"arrival":"…","house":"…","departure":"…"}}.""",
            "extras" => """Proponi fino a tre extra tipici in italiano, con prezzi indicativi in EUR e motivazione. Rispondi solo con JSON: {"extras":[{"name":"…","price":10,"reason":"…"}]}.""",
            "suppliers" => """Seleziona fino a tre codici di categoria offerti ai visitatori compatibili con questi dati. Rispondi solo con JSON: {"categories":["code"]}. Codici disponibili: """ + string.Join(", ", ServiceCategories.All),
            "local" when town is not null => $"Cerca consigli turistici veri nel comune italiano di {town.Name} ({town.ProvinceCode}), includendo nome e link HTTPS della fonte per ogni risultato (ristoranti, musei, spiagge o eventi). Riporta fino a cinque risultati con le fonti in testo semplice.",
            _ => null,
        };
        if (prompt is null)
            return null;

        if (kind != "local")
            prompt += "\nDati: " + facts;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
        var key = $"guest-portal-setup:{kind}:{hash}";
        Guid? cacheScope = kind == "local" ? null : property.OrgId;
        if (cache.TryGet<Suggestion>(cacheScope, key, out var prior))
            return prior;

        try
        {
            Suggestion? suggestion;
            if (kind == "local")
            {
                var result = await web.SearchAsync(prompt, ct);
                var tips = Limit(PiiScrubber.Scrub(result ?? string.Empty));
                suggestion = tips is null ? null : new Suggestion(new GuestHouseGuide { LocalTips = tips }, null, null);
            }
            else
            {
                var reply = await ai.GenerateAsync(new AiRequest(prompt, [], Temperature: 0.2,
                    MaxCompletionTokens: 900, DisableThinking: true), AiModelTier.Standard, key, ct);
                suggestion = reply.ProviderConfigured ? Parse(reply.Content, kind) : null;
            }

            if (suggestion is not null)
                cache.Set(cacheScope, key, suggestion);
            return suggestion;
        }
        catch (Exception e) when (e is AiBudgetExceededException or HttpRequestException or JsonException ||
                                  (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Guest portal suggestion {Kind} unavailable ({ErrorType})", kind, e.GetType().Name);
            return null;
        }
    }

    private static Suggestion? Parse(string text, string kind)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        var response = JsonSerializer.Deserialize<Suggestion>(text[start..(end + 1)], Json);
        return kind switch
        {
            "guide" when response?.Guide is { } guide => new Suggestion(new GuestHouseGuide
            {
                Arrival = Limit(guide.Arrival),
                House = Limit(guide.House),
                Departure = Limit(guide.Departure),
            }, null, null),
            "extras" when response?.Extras is { } extras => new Suggestion(null,
                extras.Where(extra => extra is not null && !string.IsNullOrWhiteSpace(extra.Name) && extra.Price is > 0 and <= 10_000)
                    .Take(3).Select(extra => extra with { Name = Limit(extra.Name)!, Reason = Limit(extra.Reason) }).ToArray(), null),
            "suppliers" when response?.Categories is { } categories => new Suggestion(null, null,
                categories.Where(ServiceCategories.IsKnown).Distinct().Take(3).ToArray()),
            _ => null,
        };
    }

    private static string? Limit(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, 2000)];

    public sealed record Suggestion(GuestHouseGuide? Guide, ExtraSuggestion[]? Extras, string[]? Categories);
    public sealed record ExtraSuggestion(string Name, decimal Price, string? Reason);
}
