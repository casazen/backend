using System.Text.RegularExpressions;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Subprocessor list (GDPR art. 28) built from what the running configuration actually uses (PL-14, A1-06, A9-40): a
/// provider is listed only when the code sends it data, and its location is read from the configuration when it can be
/// deduced (Auth0 tenant domain, Supabase pooler host or storage region, Railway replica region). The legal details
/// that cannot be deduced (legal entity, location, transfer basis) come from
/// <c>Legal:Documents:Subprocessors:Providers:{Key}</c> and are never written by code: while one is missing the entry
/// is marked <see cref="SubprocessorItem.DetailsPending"/>. Runbook: <c>docs/runbooks/legal-documents.md</c>.
/// </summary>
public static partial class LegalSubprocessorCatalog
{
    public const string ProvidersSection = "Legal:Documents:Subprocessors:Providers";

    public const string Supabase = "supabase";
    public const string Auth0 = "auth0";
    public const string Stripe = "stripe";
    public const string Resend = "resend";
    public const string Expo = "expo";
    public const string Railway = "railway";
    public const string Vercel = "vercel";
    public const string Ai = "ai";

    /// <summary>Variables Railway sets on every deployment: their presence means the API runs on Railway.</summary>
    private static readonly string[] RailwayMarkers = ["RAILWAY_PROJECT_ID", "RAILWAY_ENVIRONMENT_NAME", "RAILWAY_GIT_COMMIT_SHA"];

    private const string RailwayRegionVariable = "RAILWAY_REPLICA_REGION";

    public static IReadOnlyList<SubprocessorItem> Build(IConfiguration configuration)
    {
        var items = new List<SubprocessorItem>();

        var supabase = DetectSupabase(configuration);
        if (supabase is not null)
            items.Add(Item(configuration, Supabase, "Supabase", "https://supabase.com", supabase.Value.PurposeKey, supabase.Value.Region));

        if (!string.IsNullOrWhiteSpace(configuration["Auth0:Domain"]))
            items.Add(Item(configuration, Auth0, "Auth0", "https://auth0.com", "authentication", DetectAuth0Region(configuration)));

        if (!string.IsNullOrWhiteSpace(configuration["Stripe:SecretKey"]))
            items.Add(Item(configuration, Stripe, "Stripe", "https://stripe.com", "payments", detectedRegion: null));

        if (!string.IsNullOrWhiteSpace(configuration[$"{EmailOptions.SectionName}:{nameof(EmailOptions.ApiKey)}"])
            || !string.IsNullOrWhiteSpace(configuration[EmailOptions.LegacyResendApiKeyKey]))
            items.Add(Item(configuration, Resend, "Resend", "https://resend.com", "email", detectedRegion: null));

        // The Expo push client is always registered: every device token and notification of the app goes through it.
        items.Add(Item(configuration, Expo, "Expo", "https://expo.dev", "pushNotifications", detectedRegion: null));

        if (IsEnabled(configuration, Railway, RailwayMarkers.Any(name => !string.IsNullOrWhiteSpace(configuration[name]))))
            items.Add(Item(configuration, Railway, "Railway", "https://railway.com", "backendHosting",
                NullIfBlank(configuration[RailwayRegionVariable])));

        if (IsEnabled(configuration, Vercel, IsVercelHost(PublicSiteOptions.ResolveBaseUrl(configuration))))
            items.Add(Item(configuration, Vercel, "Vercel", "https://vercel.com", "frontendHosting", detectedRegion: null));

        var ai = BuildActiveAiProviderItem(configuration);
        if (ai is not null)
            items.Add(ai);

        return items;
    }

    /// <summary>
    /// Region of the Auth0 tenant from its canonical domain (<c>Auth0:ManagementApiDomain</c>, else <c>Auth0:Domain</c>):
    /// <c>{tenant}.{region}.auth0.com</c> gives the region, a legacy <c>{tenant}.auth0.com</c> tenant is in the US. A custom
    /// domain says nothing: null, the region then comes from configuration.
    /// </summary>
    public static string? DetectAuth0Region(IConfiguration configuration)
    {
        foreach (var key in new[] { "Auth0:ManagementApiDomain", "Auth0:Domain" })
        {
            var host = NormalizeHost(configuration[key]);
            if (host is null)
                continue;

            var regional = Auth0RegionalDomainRegex().Match(host);
            if (regional.Success)
                return regional.Groups["region"].Value.ToUpperInvariant();
            if (Auth0LegacyDomainRegex().IsMatch(host))
                return "US";
        }

        return null;
    }

    private static (string PurposeKey, string? Region)? DetectSupabase(IConfiguration configuration)
    {
        string? databaseHost = null;
        try
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            if (!string.IsNullOrWhiteSpace(connectionString))
                databaseHost = new NpgsqlConnectionStringBuilder(connectionString).Host?.Trim().ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Malformed connection string: the database is not identified as Supabase.
        }

        var database = databaseHost is not null && IsSupabaseHost(databaseHost);
        var storageProvider = configuration["Storage:Provider"];
        var storage = !string.Equals(storageProvider, "FileSystem", StringComparison.OrdinalIgnoreCase)
                      && IsSupabaseHost(HostOf(configuration["Storage:S3:ServiceUrl"]));
        if (!database && !storage)
            return null;

        string? region = null;
        if (database && SupabasePoolerHostRegex().Match(databaseHost!) is { Success: true } pooler)
            region = pooler.Groups["region"].Value;
        else if (storage)
            region = NullIfBlank(configuration["Storage:S3:Region"]);

        var purposeKey = database && storage ? "databaseAndStorage" : database ? "database" : "storage";
        return (purposeKey, region);
    }

    /// <summary>
    /// The active external AI provider (<see cref="AiOptions"/>, section <c>Ai:Subprocessor</c>): prompts reach it, so it
    /// is listed (A8-15). Its legal details are never filled in here.
    /// </summary>
    private static SubprocessorItem? BuildActiveAiProviderItem(IConfiguration configuration)
    {
        var ai = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
        if (!ai.IsExternalProviderActive())
            return null;

        var details = ai.Subprocessor;
        var region = details.Region?.Trim() ?? string.Empty;
        var transferMechanism = NullIfBlank(details.TransferMechanism);
        var entity = NullIfBlank(details.Entity);
        var customPurpose = NullIfBlank(details.Purpose);
        return new SubprocessorItem(
            Name: NullIfBlank(details.Name) ?? ai.Provider.Trim(),
            Purpose: customPurpose ?? "AI text generation",
            Region: region,
            Website: NullIfBlank(details.Website),
            TransferMechanism: transferMechanism,
            DetailsPending: IsPending(entity, region, transferMechanism),
            Key: Ai,
            PurposeKey: customPurpose is null ? "ai" : null,
            Entity: entity);
    }

    private static SubprocessorItem Item(
        IConfiguration configuration,
        string key,
        string name,
        string website,
        string purposeKey,
        string? detectedRegion)
    {
        var section = configuration.GetSection($"{ProvidersSection}:{Capitalize(key)}");
        // A region read from the provider's own configuration wins: it is what the code talks to.
        var region = detectedRegion ?? NullIfBlank(section["Region"]) ?? string.Empty;
        var transferMechanism = NullIfBlank(section["TransferMechanism"]);
        var entity = NullIfBlank(section["Entity"]);
        return new SubprocessorItem(
            Name: name,
            Purpose: PurposeFallbacks[purposeKey],
            Region: region,
            Website: website,
            TransferMechanism: transferMechanism,
            DetailsPending: IsPending(entity, region, transferMechanism),
            Key: key,
            PurposeKey: purposeKey,
            Entity: entity);
    }

    /// <summary>English purpose for API clients that do not localize <see cref="SubprocessorItem.PurposeKey"/>.</summary>
    private static readonly Dictionary<string, string> PurposeFallbacks = new()
    {
        ["database"] = "Database",
        ["storage"] = "File storage",
        ["databaseAndStorage"] = "Database and file storage",
        ["authentication"] = "Authentication",
        ["payments"] = "Payments",
        ["email"] = "Transactional email",
        ["pushNotifications"] = "Push notifications to the mobile app",
        ["backendHosting"] = "API hosting",
        ["frontendHosting"] = "Web app hosting",
    };

    /// <summary>
    /// Hosting providers cannot always be detected (a custom domain hides Vercel): <c>Enabled</c> true or false wins,
    /// otherwise the detection decides.
    /// </summary>
    private static bool IsEnabled(IConfiguration configuration, string key, bool detected) =>
        bool.TryParse(configuration[$"{ProvidersSection}:{Capitalize(key)}:Enabled"], out var enabled) ? enabled : detected;

    private static bool IsPending(string? entity, string region, string? transferMechanism) =>
        entity is null || region.Length == 0 || transferMechanism is null;

    private static bool IsVercelHost(string? url)
    {
        var host = HostOf(url);
        return host is not null && (host == "vercel.app" || host.EndsWith(".vercel.app", StringComparison.Ordinal));
    }

    private static bool IsSupabaseHost(string? host) =>
        host is not null
        && (host.EndsWith(".supabase.co", StringComparison.Ordinal) || host.EndsWith(".supabase.com", StringComparison.Ordinal));

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;

    private static string? NormalizeHost(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;
        var value = domain.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;
        return HostOf(value);
    }

    private static string Capitalize(string key) => char.ToUpperInvariant(key[0]) + key[1..];

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Auth0 public cloud: {tenant}.{us|eu|au|jp|uk|ca}[-N].auth0.com.
    [GeneratedRegex(@"^[a-z0-9-]+\.(?<region>us|eu|au|jp|uk|ca)(-\d+)?\.auth0\.com$")]
    private static partial Regex Auth0RegionalDomainRegex();

    // Tenants created in the US before regional domains: {tenant}.auth0.com.
    [GeneratedRegex(@"^[a-z0-9-]+\.auth0\.com$")]
    private static partial Regex Auth0LegacyDomainRegex();

    // Supavisor pooler host: aws-0-eu-west-1.pooler.supabase.com (the project region).
    [GeneratedRegex(@"^aws-\d+-(?<region>[a-z]{2}-[a-z]+-\d+)\.pooler\.supabase\.com$")]
    private static partial Regex SupabasePoolerHostRegex();
}
