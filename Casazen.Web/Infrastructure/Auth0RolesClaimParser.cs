using System.Text.Json;

namespace Casazen.Web.Infrastructure;

public static class Auth0RolesClaimParser
{
    public static IReadOnlyList<string> Parse(IEnumerable<string?> claimValues)
    {
        var roles = new List<string>();
        foreach (var value in claimValues)
        {
            roles.AddRange(ParseValue(value));
        }

        return roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<string> ParseValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var trimmed = value.Trim();

        if (trimmed.StartsWith('['))
        {
            try
            {
                var roles = JsonSerializer.Deserialize<string[]>(trimmed);
                if (roles is { Length: > 0 })
                    return roles;
            }
            catch (JsonException)
            {
            }
        }

        if (trimmed.StartsWith('"'))
        {
            try
            {
                var role = JsonSerializer.Deserialize<string>(trimmed);
                if (!string.IsNullOrWhiteSpace(role))
                    return [role];
            }
            catch (JsonException)
            {
            }
        }

        return [trimmed];
    }
}
