using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.GuestPortal;

public sealed class GuestPortalSetupSuggestions(
    IAiProvider ai, IWebSearchClient web, IAiResponseCache cache, IComuneDirectory comuni, ILogger<GuestPortalSetupSuggestions> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Suggestion?> GenerateAsync(Property property, string kind, CancellationToken ct, bool fast = false)
    {
        if (kind == "guide")
        {
            var draft = GuideFromProperty(property);
            return fast ? PropertySuggestion(draft) : await GenerateGuideAsync(property, draft, ct);
        }

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

    private async Task<Suggestion> GenerateGuideAsync(Property property, GuestHouseGuide fallback, CancellationToken ct)
    {
        // Only the accommodation facts go to the provider. Arrival directions, codes, credentials and contacts stay out of the prompt.
        var amenities = AmenityCatalog.Entries.Where(entry => property.Amenities.Contains(entry.Amenity)).Select(entry => entry.LabelIt);
        var facts = JsonSerializer.Serialize(new
        {
            property.AccommodationType,
            property.AreaKind,
            property.MaxGuests,
            amenities,
        }, Json);
        var prompt = """
            Scrivi tre brevi testi in italiano, rivolti all'ospite. Per l'arrivo rimanda ai dettagli della prenotazione;
            per la casa usa soltanto le dotazioni indicate; per la partenza ricorda di controllare gli effetti personali.
            Non aggiungere orari, indirizzi, istruzioni di accesso, recapiti, password, costi o luoghi.
            I dati sono fatti sull'alloggio. Non aggiungere fatti assenti. Rispondi solo con JSON:
            {"guide":{"arrival":"…","house":"…","departure":"…"}}.
            Dati:
            """ + facts;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
        var key = $"guest-portal-setup:guide:v2:{hash}";
        try
        {
            if (!cache.TryGet<GuestHouseGuide>(property.OrgId, key, out var draft) || draft is null)
            {
                // The host can still fill the guide if the platform provider is slow or absent.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                var reply = await ai.GenerateAsync(new AiRequest(prompt, [], Temperature: 0.2,
                    MaxCompletionTokens: 1600, DisableThinking: true), AiModelTier.Standard, key, deadline.Token);
                draft = reply.ProviderConfigured ? Parse(reply.Content, "guide")?.Guide : null;
                if (draft is null)
                {
                    logger.LogInformation("Guest portal guide provider returned no usable draft (configured: {Configured})", reply.ProviderConfigured);
                    return PropertySuggestion(fallback);
                }
                cache.Set(property.OrgId, key, draft);
            }

            return new Suggestion(fallback with
            {
                Arrival = draft.Arrival ?? fallback.Arrival,
                House = draft.House ?? fallback.House,
                Departure = draft.Departure ?? fallback.Departure,
            }, null, null)
            {
                GuideSource = "ai",
            };
        }
        catch (Exception e) when (e is AiBudgetExceededException or HttpRequestException or JsonException ||
                                  (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Guest portal guide provider unavailable ({ErrorType})", e.GetType().Name);
            return PropertySuggestion(fallback);
        }
    }

    private static Suggestion PropertySuggestion(GuestHouseGuide guide) =>
        new(guide, null, null) { GuideSource = "property" };

    private static GuestHouseGuide GuideFromProperty(Property property)
    {
        var amenities = AmenityCatalog.Entries.Where(entry => property.Amenities.Contains(entry.Amenity)).ToArray();
        var parking = amenities.Where(entry => entry.Amenity is PropertyAmenity.FreeParking or PropertyAmenity.PaidParking or PropertyAmenity.Garage)
            .Select(entry => entry.LabelIt).ToArray();
        var house = amenities.Where(entry => entry.Group is "comfort" or "kitchen" or "bedbath").Select(entry => entry.LabelIt).ToArray();
        var checkInHours = (property.CheckInFrom, property.CheckInTo) switch
        {
            ({ } from, { } to) => $"Dalle {from:HH\\:mm} alle {to:HH\\:mm}, ora locale.",
            ({ } from, _) => $"Dalle {from:HH\\:mm}, ora locale.",
            (_, { } to) => $"Entro le {to:HH\\:mm}, ora locale.",
            _ => "Concorda l’orario di arrivo con l’host nella chat del soggiorno.",
        };

        return new GuestHouseGuide
        {
            Arrival = "Consulta i dettagli del soggiorno per raggiungere l’alloggio. Se hai bisogno di indicazioni, scrivi all’host nella chat.",
            CheckInHours = checkInHours,
            House = house.Length == 0
                ? "Consulta le regole della casa nel portale. Per informazioni sulle dotazioni, scrivi all’host."
                : $"Dotazioni dell’alloggio: {string.Join(", ", house)}.",
            Departure = "Prima di partire, controlla di avere con te tutti i tuoi effetti personali. Per la riconsegna delle chiavi, segui le indicazioni dell’host.",
            CheckOutHours = property.CheckOutBy is { } by
                ? $"Entro le {by:HH\\:mm}, ora locale."
                : "Concorda l’orario di partenza con l’host nella chat del soggiorno.",
            Parking = parking.Length == 0
                ? "La disponibilità del parcheggio non è indicata. Chiedi all’host prima di arrivare in auto."
                : $"Tra le dotazioni trovi: {string.Join(", ", parking)}. Chiedi all’host come accedervi.",
            Contacts = "Per assistenza durante il soggiorno, contatta l’host dalla chat del portale.",
            LocalTips = Limit(property.City) is { } city
                ? $"Per scoprire cosa visitare a {city}, consulta le informazioni turistiche locali o chiedi un consiglio all’host nella chat."
                : "Per suggerimenti su cosa visitare, dove mangiare e come spostarti, chiedi all’host nella chat del soggiorno.",
            Access = "Dopo aver completato il check-in, segui le istruzioni di accesso ricevute dall’host. Se non le trovi, scrivi nella chat del soggiorno.",
            Wifi = property.Amenities.Contains(PropertyAmenity.WiFi)
                ? "Il Wi-Fi è indicato tra le dotazioni. Se non hai ricevuto il nome della rete e la password, chiedili all’host nella chat."
                : "La disponibilità del Wi-Fi non è indicata. Chiedi all’host nella chat del soggiorno.",
        };
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
            "guide" when response?.Guide is { } guide && new[] { guide.Arrival, guide.House, guide.Departure }.Any(value => !string.IsNullOrWhiteSpace(value)) => new Suggestion(new GuestHouseGuide
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

    public sealed record Suggestion(GuestHouseGuide? Guide, ExtraSuggestion[]? Extras, string[]? Categories)
    {
        public string? GuideSource { get; init; }
    }
    public sealed record ExtraSuggestion(string Name, decimal Price, string? Reason);
}
