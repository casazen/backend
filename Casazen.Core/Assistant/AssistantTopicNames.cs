using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Assistant;

/// <summary>
/// The names a list of allowed or forbidden topics may hold (E05/F05/US01/T02, NF-66): the members of <see cref="KnowledgeTopic"/> and of
/// <see cref="AssistantSituation"/>, as the enums spell them (<c>Parking</c>, <c>InvoiceRequest</c>). A name that is both (<c>Parking</c>, <c>WiFi</c>, <c>Pets</c>,
/// <c>Directions</c>) covers the topic and the situation. Pure.
/// </summary>
public static class AssistantTopicNames
{
    /// <summary>Every name a list may hold, each once: the topics first, then the situations that are not topics.</summary>
    public static IReadOnlyList<string> Known { get; } =
        [.. Enum.GetNames<KnowledgeTopic>().Concat(Enum.GetNames<AssistantSituation>()).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The canonical spelling of <paramref name="name"/>, a name of <see cref="Known"/> in any case; <c>false</c> for anything else, a number included.
    /// </summary>
    public static bool TryNormalize(string? name, out string canonical)
    {
        canonical = string.Empty;
        var text = name?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        var known = Known.FirstOrDefault(candidate => candidate.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (known is null)
            return false;

        canonical = known;
        return true;
    }

    /// <summary>True when <paramref name="names"/> lists the situation or the topic (by the spelling of <see cref="Known"/>).</summary>
    public static bool IsListed(IEnumerable<string> names, AssistantSituation? situation, KnowledgeTopic? topic)
    {
        ArgumentNullException.ThrowIfNull(names);

        var wanted = new List<string>(2);
        if (situation is { } s)
            wanted.Add(s.ToString());
        if (topic is { } t)
            wanted.Add(t.ToString());

        return wanted.Count > 0 && names.Any(name => wanted.Contains(name, StringComparer.OrdinalIgnoreCase));
    }
}
