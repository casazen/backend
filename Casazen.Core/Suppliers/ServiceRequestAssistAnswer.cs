using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

/// <summary>What the model wrote in its answer to the description of a job, before any check: names read without regard to case, unknown ones ignored.</summary>
/// <param name="Category">The code the model chose; <c>null</c> when it said it could not tell (an explicit <c>null</c> or an empty text).</param>
public sealed record RawAssistAnswer(string? Category, string? Urgency, string? Summary, string? Reason);

/// <summary>An answer that passed the checks: a known category, an urgency of the enum, short texts on one line.</summary>
public sealed record AcceptedAssistAnswer(string Category, ServiceRequestUrgency Urgency, string? Summary, string? Explanation);

/// <summary>
/// Reads the answer of the AI to the description of a job (WIZ-AI-REQUEST): one JSON object with <c>category</c>, <c>urgency</c>, <c>summary</c>
/// and <c>reason</c>, maybe in a code fence or after a reasoning. Of several objects the last one with a <c>category</c> is the answer; the ones
/// before it are drafts. Anything that is not that is <c>null</c>: the caller does not guess.
/// </summary>
public static class ServiceRequestAssistAnswerParser
{
    private const string CategoryMember = "category";

    public static RawAssistAnswer? Parse(string? content)
    {
        foreach (var json in AiAnswerText.JsonObjectsLastFirst(content))
        {
            var answer = ParseObject(json);
            if (answer is not null)
                return answer;
        }

        return null;
    }

    private static RawAssistAnswer? ParseObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var hasCategory = false;
            string? category = null;
            string? urgency = null;
            string? summary = null;
            string? reason = null;

            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (Is(member, CategoryMember))
                {
                    // A string, or null for "I cannot tell"; any other kind (a number, a list) is not an answer to the question.
                    if (member.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        return null;

                    hasCategory = true;
                    category = member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString() : null;
                }
                else if (Is(member, "urgency") && member.Value.ValueKind == JsonValueKind.String)
                {
                    urgency = member.Value.GetString();
                }
                else if (Is(member, "summary") && member.Value.ValueKind == JsonValueKind.String)
                {
                    summary = member.Value.GetString();
                }
                else if ((Is(member, "reason") || Is(member, "explanation")) && member.Value.ValueKind == JsonValueKind.String)
                {
                    reason = member.Value.GetString();
                }
            }

            return hasCategory ? new RawAssistAnswer(category, urgency, summary, reason) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Is(JsonProperty member, string name) =>
        string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The checks of the answer of the AI before the host sees it (WIZ-AI-REQUEST).</summary>
public static class ServiceRequestAssistRules
{
    /// <summary>
    /// The answer once checked, or why there is none: <see cref="ServiceRequestAssistReasons.NotUnderstood"/> when the model said it could not
    /// tell what the work is, <see cref="AiSuggestionFallbackReasons.AiInvalidAnswer"/> when the category is not one of
    /// <see cref="ServiceCategories.All"/> (a code is never invented for it). The urgency is one of the three of the enum, by name; anything else
    /// is <see cref="ServiceRequestUrgency.Normal"/>, the host being free to change it. The texts are one line each, cut at the limits.
    /// </summary>
    public static (AcceptedAssistAnswer? Answer, string? Reason) Check(RawAssistAnswer raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var category = ServiceCategories.Normalize(raw.Category);
        if (category is null)
            return (null, ServiceRequestAssistReasons.NotUnderstood);

        if (!ServiceCategories.IsKnown(category))
            return (null, AiSuggestionFallbackReasons.AiInvalidAnswer);

        return (
            new AcceptedAssistAnswer(
                category,
                UrgencyOf(raw.Urgency),
                OneLine(raw.Summary, ServiceRequestAssistLimits.MaxSummaryLength),
                OneLine(raw.Reason, ServiceRequestAssistLimits.MaxExplanationLength)),
            null);
    }

    private static ServiceRequestUrgency UrgencyOf(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "high" => ServiceRequestUrgency.High,
            "emergency" => ServiceRequestUrgency.Emergency,
            _ => ServiceRequestUrgency.Normal,
        };

    /// <summary>
    /// The text on one line (white space runs and control characters become one space), cut at <paramref name="limit"/>: at the end of the last
    /// sentence that fits, else at the last word, else where the limit is. <c>null</c> when nothing is left.
    /// </summary>
    public static string? OneLine(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = line.Length > 0;
                continue;
            }

            if (pendingSpace)
                line.Append(' ');
            pendingSpace = false;
            line.Append(c);
        }

        if (line.Length == 0)
            return null;

        if (line.Length <= limit)
            return line.ToString();

        var cut = line.ToString(0, limit);
        var sentence = cut.LastIndexOfAny(['.', '!', '?']);
        if (sentence >= limit / 2)
            return cut[..(sentence + 1)];

        // The cut falls on the end of a word when a space follows it; otherwise the last word is half written and goes.
        var word = line[limit] == ' ' ? limit : cut.LastIndexOf(' ');
        return (word >= limit / 2 ? cut[..word] : cut).TrimEnd(',', ';', ':', ' ');
    }
}
