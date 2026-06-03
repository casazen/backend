using System.Text.RegularExpressions;
using Npgsql;

namespace Casazen.Infrastructure.Data;

/// <summary>
/// Converts Supabase-style PostgreSQL URIs to Npgsql key=value connection strings.
/// Railway and Supabase dashboards often expose URIs; EF Core and Hangfire require Npgsql format.
/// </summary>
public static partial class NpgsqlConnectionStringNormalizer
{
    public static string? Normalize(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var trimmed = connectionString.Trim();
        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var uri = new Uri(trimmed);
        var (username, password) = ParseUserInfo(uri.UserInfo);

        var database = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(database))
        {
            database = "postgres";
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = database,
            Username = username,
            Password = password,
            SslMode = SslMode.Require,
        };

        var searchPath = TryGetSearchPath(uri.Query);
        if (!string.IsNullOrWhiteSpace(searchPath))
        {
            builder.SearchPath = searchPath;
        }

        return builder.ConnectionString;
    }

    private static (string Username, string Password) ParseUserInfo(string userInfo)
    {
        if (string.IsNullOrEmpty(userInfo))
        {
            return ("postgres", string.Empty);
        }

        var colon = userInfo.IndexOf(':');
        if (colon < 0)
        {
            return (Uri.UnescapeDataString(userInfo), string.Empty);
        }

        return (
            Uri.UnescapeDataString(userInfo[..colon]),
            Uri.UnescapeDataString(userInfo[(colon + 1)..]));
    }

    private static string? TryGetSearchPath(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        var parameters = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var parameter in parameters)
        {
            var parts = parameter.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(parts[0]);
            var value = Uri.UnescapeDataString(parts[1]);

            if (key.Equals("SearchPath", StringComparison.OrdinalIgnoreCase)
                || key.Equals("search_path", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (key.Equals("options", StringComparison.OrdinalIgnoreCase))
            {
                var fromOptions = TryParseSearchPathFromOptions(value);
                if (!string.IsNullOrWhiteSpace(fromOptions))
                {
                    return fromOptions;
                }
            }
        }

        return null;
    }

    private static string? TryParseSearchPathFromOptions(string options)
    {
        if (string.IsNullOrWhiteSpace(options))
        {
            return null;
        }

        var match = SearchPathOptionsRegex().Match(options);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"search_path[= ]([^,&\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchPathOptionsRegex();
}
