using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Assistant;

public static class AssistantTopicNames
{
    public static IReadOnlyList<string> Known { get; } =
        [.. Enum.GetNames<KnowledgeTopic>().Concat(Enum.GetNames<AssistantSituation>()).Distinct(StringComparer.Ordinal)];

    public static bool TryNormalize(string? name, out string canonical)
    {
        canonical = string.Empty;
        var text = name?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        var known = Known.FirstOrDefault(candidate => candidate.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            canonical = known;
            return true;
        }

        if (AssistantSituationCatalog.TryFromCode(text, out var situation))
        {
            canonical = situation.ToString();
            return true;
        }

        return false;
    }

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
