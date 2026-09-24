using System.Text.RegularExpressions;

namespace Casazen.Web.Configuration;

/// <summary>
/// Commit the running API was built from, exposed by the health endpoints so CI can check that the deployment it
/// verifies is the one of the pushed commit (A9-19). Railway sets <c>RAILWAY_GIT_COMMIT_SHA</c> on every deployment
/// triggered from GitHub; other hosts can set <c>GIT_COMMIT_SHA</c>. Anything that is not a hexadecimal SHA is ignored.
/// </summary>
public sealed partial class BuildInfo(string? commitSha)
{
    public const string RailwayCommitVariable = "RAILWAY_GIT_COMMIT_SHA";
    public const string CommitVariable = "GIT_COMMIT_SHA";

    /// <summary>Lowercase commit SHA (7-40 hex characters), or null when the host does not provide it.</summary>
    public string? CommitSha { get; } = commitSha;

    public static BuildInfo FromConfiguration(IConfiguration configuration)
    {
        foreach (var key in new[] { RailwayCommitVariable, CommitVariable })
        {
            var value = configuration[key]?.Trim().ToLowerInvariant();
            if (value is not null && CommitShaPattern().IsMatch(value))
                return new BuildInfo(value);
        }

        return new BuildInfo(null);
    }

    [GeneratedRegex("^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaPattern();
}
