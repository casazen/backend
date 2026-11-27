using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// DEPLOY-CFG: the committed example files of the Railway variables (<c>secrets/railway.*.variables.example.json</c>) keep the
/// two environments aligned. The checks that compared them with the deploy checklist were removed on 2026-11-27, when the
/// project documentation moved out of the repository (the checklist now lives in the workspace folder
/// <c>docs/03-documenti-ai/runbook/backend/deploy-checklist.md</c>, the only source of truth for documents).
/// </summary>
public class DeployExampleFilesTests
{
    [Fact]
    public void Examples_RailwayTestAndProduction_DefineTheSameVariables()
    {
        // The two environments differ in values, never in which variables exist (a Production-only variable is a surprise
        // at the first production deploy); the preview pattern of the Vercel project is test only.
        var test = JsonKeys(Path.Combine(SolutionRoot(), "secrets", "railway.test.variables.example.json"));
        var production = JsonKeys(Path.Combine(SolutionRoot(), "secrets", "railway.prod.variables.example.json"));

        Assert.Equal(
            test.Where(key => key != "Cors__VercelPreviewPattern").Order(),
            production.Order());
    }

    [Fact]
    public void Examples_RailwayTest_DeclaresEveryVariableTheRailwayEnvironmentRequires()
    {
        // Startup requirements (the «S» rows of the backend deploy checklist, section 2): without them the container exits.
        string[] startupRequired =
        [
            "ASPNETCORE_ENVIRONMENT", "ConnectionStrings__DefaultConnection", "Hangfire__Schema", "Auth0__Domain", "Auth0__Audience",
            "Email__Provider", "Email__ApiKey", "Email__FromAddress", "App__PublicSiteBaseUrl", "Cors__AllowedOrigins",
            "Storage__Provider", "Storage__PublicBaseUrl", "Storage__S3__ServiceUrl", "Storage__S3__Region",
            "Storage__S3__AccessKeyId", "Storage__S3__SecretAccessKey", "Storage__S3__PublicBucket", "Storage__S3__PrivateBucket",
            "DataProtection__CertificatePfxBase64", "DataProtection__CertificatePassword",
            "App__ApiBaseUrl", "Stripe__SecretKey", "Stripe__PublishableKey", "Stripe__WebhookSecret", "Stripe__ConnectWebhookSecret",
            "Billing__Prices__Starter", "Billing__Prices__Pro", "Billing__Prices__Scale",
            "Auth0__ManagementClientId", "Auth0__ManagementClientSecret",
        ];

        foreach (var file in new[] { "railway.test.variables.example.json", "railway.prod.variables.example.json" })
        {
            var keys = JsonKeys(Path.Combine(SolutionRoot(), "secrets", file));
            Assert.Empty(startupRequired.Except(keys));
        }
    }

    private static IReadOnlyList<string> JsonKeys(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().Select(property => property.Name).Where(key => !key.StartsWith('_')).ToList();
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
