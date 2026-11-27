using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.Assistant;

/// <summary>
/// What the classifier said about the last message of a guest (E05/F05/US02/T01, NF-66, NF-67): the situation it recognized and how well the message matched it, how urgent it
/// is, the sensitive categories it carries, the intents it flags (<c>date_change</c>) and the language the guest wrote in. Read from the answer of the model by
/// <see cref="Parse"/>, which never throws: an answer that cannot be read, or a value out of range, is the situation <see cref="AssistantSituation.Other"/> with a match of 0, the
/// one that is never answered by itself.
/// </summary>
/// <param name="Situation">The situation; <see cref="AssistantSituation.Other"/> when none was recognized.</param>
/// <param name="Urgency">How soon a person must look at the message; <see cref="AssistantUrgency.Normal"/> when the answer does not say it in a known word.</param>
/// <param name="SensitiveCategories">The categories the classifier saw in this message (those a situation carries by nature are added by the pipeline).</param>
/// <param name="IntentFlags">Short codes of what the guest asks for (<c>date_change</c>), lower case, each once, at most <see cref="AssistantClassification.MaxIntentFlags"/>.</param>
/// <param name="Language">The language of the guest, a two-letter code; <c>null</c> when not given.</param>
/// <param name="SituationMatch">How well the message matched the situation, 0 to 1; always 0 for <see cref="AssistantSituation.Other"/>.</param>
public sealed partial record AssistantClassification(
    AssistantSituation Situation,
    AssistantUrgency Urgency,
    SensitiveCategory SensitiveCategories,
    IReadOnlyList<string> IntentFlags,
    string? Language,
    decimal SituationMatch)
{
    /// <summary>The most intent flags kept from an answer.</summary>
    public const int MaxIntentFlags = 10;

    /// <summary>The words of the categories in the answer (lower case, the separators taken out), and the category each one is.</summary>
    private static readonly IReadOnlyDictionary<string, SensitiveCategory> CategoryWords = new Dictionary<string, SensitiveCategory>(StringComparer.Ordinal)
    {
        ["money"] = SensitiveCategory.Money,
        ["cancellationrefund"] = SensitiveCategory.CancellationRefund,
        ["complaint"] = SensitiveCategory.Complaint,
        ["damage"] = SensitiveCategory.Damage,
        ["safety"] = SensitiveCategory.Safety,
        ["legal"] = SensitiveCategory.Legal,
        ["personaldata"] = SensitiveCategory.PersonalData,
    };

    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 8 };

    /// <summary>The classification of an answer that cannot be used: <see cref="AssistantSituation.Other"/>, a match of 0, normal urgency, no category, no flag, no language.</summary>
    public static AssistantClassification Unreadable { get; } = new(AssistantSituation.Other, AssistantUrgency.Normal, SensitiveCategory.None, [], null, 0m);

    /// <summary>
    /// The classification in the answer of the model, strictly: the JSON object it holds (a code fence or a sentence around it, and the reasoning of a model that thinks aloud,
    /// <c>&lt;think&gt;…&lt;/think&gt;</c>, are left out; see <see cref="DocumentOf"/>) with <c>situation</c> (the stable code of a situation of
    /// <see cref="AssistantSituationCatalog"/>, or the name of its member) and <c>situationMatch</c> (a number from 0 to 1). <b>Anything unreadable or out of range is
    /// <see cref="AssistantSituation.Other"/> with a match of 0, never an exception</b>: no object, an unknown situation, a match missing, not a number or outside 0 to 1. The rest is
    /// read on its own: <c>urgency</c> (<c>normal</c>, <c>high</c>, <c>urgent</c>; any other value is normal), <c>sensitiveCategories</c> (the known words; the others are left out),
    /// <c>intentFlags</c> (codes of lower-case letters, digits and underscores) and <c>language</c> (two letters).
    /// </summary>
    public static AssistantClassification Parse(string? answer)
    {
        using var document = DocumentOf(answer, "situation");
        if (document is null)
            return Unreadable;

        var root = document.RootElement;
        var urgency = UrgencyOf(root);
        var categories = CategoriesOf(root);
        var flags = FlagsOf(root);
        var language = LanguageOf(root);

        var situation = AssistantSituation.Other;
        var known = String(root, "situation") is { } text && AssistantSituationCatalog.TryParse(text, out situation);
        var match = MatchOf(root);
        return known && match is { } value && situation != AssistantSituation.Other
            ? new AssistantClassification(situation, urgency, categories, flags, language, value)
            : new AssistantClassification(AssistantSituation.Other, urgency, categories, flags, language, 0m);
    }

    /// <summary>
    /// The JSON object of the answer the caller reads, parsed (the caller disposes it): the objects that follow a reasoning (<see cref="AiAnswerText.JsonObjectsLastFirst"/>,
    /// the last one first), and of them the first that has <paramref name="member"/>, else the first that is an object at all; <c>null</c> when the answer holds none.
    /// Trying each object in turn is what keeps a brace inside a string of the answer (the <c>{{guest_name}}</c> of a draft) or an object in a reasoning from hiding the answer.
    /// </summary>
    internal static JsonDocument? DocumentOf(string? answer, string member)
    {
        JsonDocument? anyObject = null;
        foreach (var candidate in AiAnswerText.JsonObjectsLastFirst(answer))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(candidate, ReadOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object && Member(document.RootElement, member) is not null)
            {
                anyObject?.Dispose();
                return document;
            }

            if (anyObject is null && document.RootElement.ValueKind == JsonValueKind.Object)
                anyObject = document;
            else
                document.Dispose();
        }

        return anyObject;
    }

    /// <summary>The string of <paramref name="name"/> in <paramref name="root"/> (in any case of the name); <c>null</c> when it is missing or not a string.</summary>
    internal static string? String(JsonElement root, string name) =>
        Member(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>The member <paramref name="name"/> of <paramref name="root"/>, in any case of the name.</summary>
    internal static JsonElement? Member(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static decimal? MatchOf(JsonElement root)
    {
        if (Member(root, "situationMatch") is not { } value)
            return null;

        decimal number;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var parsed))
            number = parsed;
        else if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fromText))
            number = fromText;
        else
            return null;

        return number is >= 0m and <= 1m ? Math.Round(number, 3, MidpointRounding.AwayFromZero) : null;
    }

    private static AssistantUrgency UrgencyOf(JsonElement root) =>
        String(root, "urgency")?.Trim().ToLowerInvariant() switch
        {
            "urgent" => AssistantUrgency.Urgent,
            "high" => AssistantUrgency.High,
            _ => AssistantUrgency.Normal,
        };

    private static SensitiveCategory CategoriesOf(JsonElement root)
    {
        if (Member(root, "sensitiveCategories") is not { ValueKind: JsonValueKind.Array } items)
            return SensitiveCategory.None;

        var categories = SensitiveCategory.None;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var word = Separators().Replace(item.GetString() ?? string.Empty, string.Empty).ToLowerInvariant();
            if (CategoryWords.TryGetValue(word, out var category))
                categories |= category;
        }

        return categories;
    }

    private static List<string> FlagsOf(JsonElement root)
    {
        var flags = new List<string>();
        if (Member(root, "intentFlags") is not { ValueKind: JsonValueKind.Array } items)
            return flags;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var flag = item.GetString()?.Trim().ToLowerInvariant();
            if (flag is not null && FlagPattern().IsMatch(flag) && !flags.Contains(flag, StringComparer.Ordinal))
                flags.Add(flag);

            if (flags.Count == MaxIntentFlags)
                break;
        }

        return flags;
    }

    private static string? LanguageOf(JsonElement root) =>
        String(root, "language")?.Trim().ToLowerInvariant() is { Length: 2 } code && code.All(c => c is >= 'a' and <= 'z') ? code : null;

    [GeneratedRegex(@"[\s_\-]", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex FlagPattern();
}
