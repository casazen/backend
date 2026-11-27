using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Events;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Authenticates a call to the Octorate webhook the only way the Octorate contract allows (ADR-023-R7, OCT-08, S24): Octorate signs
/// nothing and a subscription carries no secret, so CasaZen puts a secret in the address it registers
/// (<c>/api/webhooks/octorate/{secret}</c>) and compares it, in constant time, with <c>Octorate:WebhookSecret</c>. If the sandbox ever
/// shows that Octorate signs its calls, this class is the only place that changes.
/// </summary>
/// <remarks>
/// The body is never trusted: the handlers re-read the object through the API before any effect. A configured secret that is empty,
/// shorter than <see cref="MinSecretLength"/> or a committed placeholder is a configuration error the controller answers with 500,
/// never an accepted call.
/// </remarks>
public sealed class OctorateWebhookVerifier(IConfiguration configuration)
{
    /// <summary>Configuration key of the secret (<c>Octorate__WebhookSecret</c>).</summary>
    public const string SecretKey = "Octorate:WebhookSecret";

    /// <summary>The shortest secret accepted: it is a password in an address.</summary>
    public const int MinSecretLength = 32;

    /// <summary>A longer secret in the address is refused without being compared.</summary>
    private const int MaxPresentedLength = 512;

    /// <summary>The configured secret, trimmed; <c>null</c> when empty.</summary>
    public string? ConfiguredSecret => configuration[SecretKey]?.Trim() is { Length: > 0 } secret ? secret : null;

    /// <summary>True when a secret of at least <see cref="MinSecretLength"/> characters is configured.</summary>
    public bool HasUsableSecret => ConfiguredSecret is { Length: >= MinSecretLength };

    /// <summary>
    /// True when <paramref name="presented"/>, the secret of the address of the call, is the configured one. Compared in constant time
    /// on the SHA-256 of both, so neither the content nor the length of the configured secret leaks through the time of the answer.
    /// </summary>
    public bool IsValid(string? presented)
    {
        var configured = ConfiguredSecret;
        if (configured is null || configured.Length < MinSecretLength
            || string.IsNullOrEmpty(presented) || presented.Length > MaxPresentedLength)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
    }
}

/// <summary>
/// The routing keys of an Octorate notification, and nothing else of its body (ADR-022-R21): the event name, the accommodation code and
/// the object id. The sandbox delivered <c>{"ping": "&lt;string&gt;"}</c> (the content of <c>ping</c> is not recorded yet), so the keys
/// are looked for, without regard to case, in the body (JSON or form), in a <c>data</c> object and in a <c>ping</c> that is itself a JSON
/// object; the event name may also come from the <c>event</c> query parameter of the registered address (one address per subscribed
/// event, docs/runbooks/octorate-webhooks.md). A key that is missing or not a plain identifier is <c>null</c>: such a notification is
/// acknowledged and recorded nowhere, and the reconciliation reads the change.
/// </summary>
/// <param name="EventName">An Octorate event name of <see cref="OctorateWebhookEvents"/>, or <c>null</c>.</param>
/// <param name="AccommodationId">The accommodation id, or <c>null</c>.</param>
/// <param name="ObjectId">The reservation, thread or content id, or <c>null</c>.</param>
public sealed record OctorateWebhookNotification(string? EventName, string? AccommodationId, string? ObjectId)
{
    private static readonly string[] EventKeys = ["event", "eventType", "eventName", "webhookType", "type"];
    private static readonly string[] AccommodationKeys = ["accommodationId", "accommodation", "idAccommodation", "structureId", "propertyId"];
    private static readonly string[] ObjectKeys = ["objectId", "reservationId", "reservation", "refer", "threadId", "contentId"];

    private static readonly Regex Identifier = new("^[A-Za-z0-9_.:-]{1,64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>The keys of a call: its body, its content type, and the <c>event</c> parameter of its address.</summary>
    public static OctorateWebhookNotification Read(string? body, string? contentType, string? eventOfTheAddress)
    {
        var fields = IsForm(contentType) ? FormFields(body) : JsonFields(body);
        var eventName = OctorateWebhookEvents.Normalize(First(fields, EventKeys)) ?? OctorateWebhookEvents.Normalize(eventOfTheAddress);
        return new OctorateWebhookNotification(
            eventName,
            IdentifierOrNull(First(fields, AccommodationKeys)),
            IdentifierOrNull(First(fields, ObjectKeys)));
    }

    private static bool IsForm(string? contentType) =>
        contentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true;

    private static string? First(IReadOnlyDictionary<string, string> fields, string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static string? IdentifierOrNull(string? value) => value is not null && Identifier.IsMatch(value) ? value : null;

    /// <summary>The fields of a form body (<c>a=1&amp;b=2</c>).</summary>
    private static Dictionary<string, string> FormFields(string? body)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body))
            return fields;

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = Uri.UnescapeDataString(pair[..separator].Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            fields.TryAdd(key, value);
        }

        return fields;
    }

    /// <summary>
    /// The plain members of a JSON body, of its <c>data</c> object and of a <c>ping</c> that holds a JSON object; an object member
    /// named like an accommodation (<c>{"accommodation": {"id": "..."}}</c>) gives its <c>id</c>. The first value found wins.
    /// </summary>
    private static Dictionary<string, string> JsonFields(string? body)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body))
            return fields;

        try
        {
            using var document = JsonDocument.Parse(body);
            Collect(document.RootElement, fields, depth: 0);
        }
        catch (JsonException)
        {
            // Not JSON: no keys, the call is acknowledged and recorded nowhere.
        }

        return fields;
    }

    private static void Collect(JsonElement element, Dictionary<string, string> fields, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 2)
            return;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = property.Value.GetString();
                    if (string.Equals(property.Name, "ping", StringComparison.OrdinalIgnoreCase)
                        && text is not null
                        && text.TrimStart().StartsWith('{'))
                    {
                        CollectNested(text, fields, depth + 1);
                    }
                    else if (text is not null)
                        fields.TryAdd(property.Name, text);
                    break;
                case JsonValueKind.Number:
                    fields.TryAdd(property.Name, property.Value.GetRawText());
                    break;
                case JsonValueKind.Object when property.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                                               || property.Name.Equals("ping", StringComparison.OrdinalIgnoreCase):
                    Collect(property.Value, fields, depth + 1);
                    break;
                case JsonValueKind.Object when AccommodationKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase):
                    if (OctorateHttp.StringOf(property.Value, "id") is { } id)
                        fields.TryAdd(property.Name, id);
                    break;
            }
        }
    }

    private static void CollectNested(string json, Dictionary<string, string> fields, int depth)
    {
        try
        {
            using var nested = JsonDocument.Parse(json);
            Collect(nested.RootElement, fields, depth);
        }
        catch (JsonException)
        {
            // A ping that only looks like JSON: nothing to read from it.
        }
    }
}
