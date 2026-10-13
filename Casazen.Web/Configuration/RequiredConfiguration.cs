namespace Casazen.Web.Configuration;

/// <summary>
/// Settings the API cannot run without (FD-12, A9-19). Outside Development and Testing (Production, Staging, …) a
/// missing value stops the startup with an explicit message, like the email (FD-13), the Hangfire schema (FD-11) and
/// the storage (FD-07) settings. Optional integrations (Stripe, Auth0 Management API) never block the startup:
/// <c>/api/health/ready</c> reports them as <c>degraded</c>. Variables: <c>docs/INFRA.md</c> § Production variables.
/// </summary>
public static class RequiredConfiguration
{
    public const string ConnectionStringVariable = "ConnectionStrings__DefaultConnection";

    /// <summary>True where the configuration must be complete: every environment except Development and Testing.</summary>
    public static bool IsEnforced(IHostEnvironment environment) =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");

    /// <summary>
    /// True when a value is empty or still one of the placeholders committed in <c>appsettings.json</c>, the
    /// <c>secrets/*.example.json</c> files or the docs (<c>YOUR_…</c>, <c>your-domain…</c>, <c>dev-xxxxxxxx…</c>,
    /// <c>sk_live_...</c>, <c>[whsec_…]</c>).
    /// </summary>
    public static bool IsMissing(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();
        return trimmed.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("your-", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("xxxxxxxx", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("...", StringComparison.Ordinal)
            || trimmed.Contains('…')
            || trimmed.StartsWith('[');
    }

    /// <summary>
    /// Without a connection string the app falls back to an in-memory database (tests only) and runs no background
    /// job: outside Development and Testing that is a configuration error, not a mode.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection string is missing where it is required.</exception>
    public static void EnsureDatabaseConnection(string? connectionString, IHostEnvironment environment)
    {
        if (!IsEnforced(environment) || !string.IsNullOrWhiteSpace(connectionString))
            return;

        throw new InvalidOperationException(
            $"{ConnectionStringVariable} is missing (environment {environment.EnvironmentName}): the API would keep its " +
            "data in memory and run no background job. Set the Supabase connection string of this environment, " +
            "see docs/INFRA.md.");
    }
}
