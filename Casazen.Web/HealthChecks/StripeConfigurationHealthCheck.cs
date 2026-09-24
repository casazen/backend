using Casazen.Web.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Stripe keys (A3-24). Payments are optional, so an incomplete configuration is <c>degraded</c>, never a startup
/// failure. Without <c>Stripe__ConnectWebhookSecret</c> the Connect webhook rejects every event and no direct booking
/// is ever confirmed; without <c>Stripe__PublishableKey</c> the checkout cannot load Stripe. Setup: <c>docs/INFRA.md</c>
/// § Stripe. The description names variables, never values.
/// </summary>
public sealed class StripeConfigurationHealthCheck(IConfiguration configuration) : IHealthCheck
{
    private static readonly StripeSetting[] Settings =
    [
        new("Stripe:SecretKey", "Stripe__SecretKey", ["sk_", "rk_"], "a secret or restricted key (sk_… / rk_…)"),
        new("Stripe:PublishableKey", "Stripe__PublishableKey", ["pk_"], "a publishable key (pk_…)"),
        new("Stripe:WebhookSecret", "Stripe__WebhookSecret", ["whsec_"], "a webhook signing secret (whsec_…)"),
        new("Stripe:ConnectWebhookSecret", "Stripe__ConnectWebhookSecret", ["whsec_"], "a webhook signing secret (whsec_…)"),
    ];

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problems = GetProblems(configuration);
        return Task.FromResult(problems.Count == 0
            ? HealthCheckResult.Healthy($"Stripe configured ({GetMode(configuration["Stripe:SecretKey"]) ?? "unknown"} mode).")
            : HealthCheckResult.Degraded("Payments are not fully configured: " + string.Join(" ", problems)));
    }

    public static IReadOnlyList<string> GetProblems(IConfiguration configuration)
    {
        var problems = new List<string>();

        var missing = Settings
            .Where(s => RequiredConfiguration.IsMissing(configuration[s.Key]))
            .Select(s => s.Variable)
            .ToList();
        if (missing.Count > 0)
            problems.Add($"missing or placeholder {string.Join(", ", missing)}.");

        foreach (var setting in Settings.Where(s => !missing.Contains(s.Variable)))
        {
            var value = configuration[setting.Key]!.Trim();
            if (!setting.Prefixes.Any(p => value.StartsWith(p, StringComparison.Ordinal)))
                problems.Add($"{setting.Variable} is not {setting.Expected}.");
        }

        var secretMode = GetMode(configuration["Stripe:SecretKey"]);
        var publishableMode = GetMode(configuration["Stripe:PublishableKey"]);
        if (secretMode is not null && publishableMode is not null && secretMode != publishableMode)
        {
            problems.Add(
                $"Stripe__SecretKey ({secretMode}) and Stripe__PublishableKey ({publishableMode}) belong to different modes.");
        }

        return problems;
    }

    private static string? GetMode(string? key)
    {
        if (RequiredConfiguration.IsMissing(key))
            return null;

        if (key!.Contains("_live_", StringComparison.Ordinal))
            return "live";

        return key.Contains("_test_", StringComparison.Ordinal) ? "test" : null;
    }

    private sealed record StripeSetting(string Key, string Variable, string[] Prefixes, string Expected);
}
