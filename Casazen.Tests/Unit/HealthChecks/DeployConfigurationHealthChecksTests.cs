using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Web.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.HealthChecks;

/// <summary>
/// DEPLOY-CFG (D9): two settings the code needs but nothing reported are now visible in <c>/api/health/ready</c>: the
/// public URL of the API (iCal export links) and the legal documents / subprocessor list. Both are <c>degraded</c>
/// (the API works without them), name the variables and never a value.
/// </summary>
public class DeployConfigurationHealthChecksTests
{
    private const string ApiUrl = "https://api-secret-host.example";

    [Fact]
    public async Task ApiUrlCheck_HttpsUrl_ReturnsHealthy()
    {
        var result = await CheckApiUrlAsync(ApiUrl, Environments.Production);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("YOUR_API_URL")]
    [InlineData("https://your-api.example")]
    public async Task ApiUrlCheck_MissingOrPlaceholder_ReturnsDegradedNamingTheVariable(string? value)
    {
        var result = await CheckApiUrlAsync(value, Environments.Production);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("App__ApiBaseUrl is missing", result.Description);
        Assert.Contains("iCal", result.Description);
    }

    [Theory]
    [InlineData("api.example.org")]
    [InlineData("ftp://api.example.org")]
    [InlineData("https://api.example.org/?token=x")]
    [InlineData("https://user:pass@api.example.org")]
    public async Task ApiUrlCheck_NotAnAbsoluteHttpUrl_ReturnsDegradedWithoutTheValue(string value)
    {
        var result = await CheckApiUrlAsync(value, Environments.Production);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("App__ApiBaseUrl is not an absolute http(s) URL", result.Description);
        Assert.DoesNotContain(value, result.Description);
    }

    [Fact]
    public async Task ApiUrlCheck_HttpOutsideDevelopment_ReturnsDegraded()
    {
        var result = await CheckApiUrlAsync("http://api.example.org", Environments.Production);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("must use https", result.Description);
    }

    [Fact]
    public async Task ApiUrlCheck_HttpInDevelopment_ReturnsHealthy()
    {
        var result = await CheckApiUrlAsync("http://localhost:5000", Environments.Development);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void Appsettings_ApiBaseUrl_HasNoCommittedValue()
    {
        // A committed host would look configured on an environment that never set the variable (the test environment
        // would publish the production host in its iCal links): the key stays empty and the check reports it.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindSolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

        Assert.True(string.IsNullOrWhiteSpace(configuration["App:ApiBaseUrl"]));
        Assert.NotNull(ApiBaseUrlHealthCheck.GetProblem(configuration["App:ApiBaseUrl"], requireHttps: true));
    }

    [Fact]
    public async Task LegalCheck_EveryDocumentPublishedWithDateAndSubprocessorsComplete_ReturnsHealthy()
    {
        var result = await CheckLegalAsync(Published());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task LegalCheck_NoTextAndNoExternalCopy_ReturnsDegradedNamingTheFileAndTheVariable()
    {
        var legal = Published(tosText: false);

        var result = await CheckLegalAsync(legal);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Tos 2026-10-v1: no text file for this version (LegalDocuments/tos/2026-10-v1.it.html)", result.Description);
        Assert.Contains("Legal__Documents__Tos__DocumentUrl", result.Description);
        Assert.DoesNotContain("Privacy", result.Description);
        Assert.DoesNotContain("Dpa", result.Description);
    }

    [Fact]
    public async Task LegalCheck_ExternalCopyInsteadOfTheText_ReturnsHealthy()
    {
        var legal = Published(tosText: false, tosUrl: "https://documents.example/tos.pdf");

        var result = await CheckLegalAsync(legal);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task LegalCheck_DocumentWithoutDateInForce_ReturnsDegradedNamingTheVariable()
    {
        var legal = Published(privacyDated: false);

        var result = await CheckLegalAsync(legal);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Legal__Documents__Privacy__EffectiveAt is not set", result.Description);
    }

    [Fact]
    public async Task LegalCheck_SubprocessorsPendingOrWithoutDate_ReturnsDegradedNamingTheProviders()
    {
        var legal = Published(subprocessorsDated: false, pendingProviders: ["Supabase", "Railway"]);

        var result = await CheckLegalAsync(legal);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Legal__Documents__Subprocessors__EffectiveAt is not set", result.Description);
        Assert.Contains("Subprocessor details still pending for Supabase, Railway", result.Description);
    }

    [Fact]
    public async Task LegalCheck_Degraded_NeverCarriesAConfiguredValue()
    {
        var legal = Published(tosText: false, tosUrl: null);
        legal.Setup(l => l.GetSubprocessors()).Returns(new SubprocessorsDocument(
            "2026-10-v1",
            null,
            [new SubprocessorItem("Stripe", "Payments", "Region-secret", null, "Mechanism-secret", true, Entity: "Entity-secret")]));

        var result = await CheckLegalAsync(legal);

        Assert.DoesNotContain("Region-secret", result.Description);
        Assert.DoesNotContain("Mechanism-secret", result.Description);
        Assert.DoesNotContain("Entity-secret", result.Description);
    }

    private static Task<HealthCheckResult> CheckApiUrlAsync(string? value, string environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:ApiBaseUrl"] = value })
            .Build();
        var hostEnvironment = new Mock<IHostEnvironment>();
        hostEnvironment.SetupGet(e => e.EnvironmentName).Returns(environment);
        return new ApiBaseUrlHealthCheck(configuration, hostEnvironment.Object).CheckHealthAsync(new HealthCheckContext());
    }

    private static Task<HealthCheckResult> CheckLegalAsync(Mock<ILegalDocumentService> legal) =>
        new LegalDocumentsHealthCheck(legal.Object).CheckHealthAsync(new HealthCheckContext());

    /// <summary>A service where every document has its version, text and date unless the arguments say otherwise.</summary>
    private static Mock<ILegalDocumentService> Published(
        bool tosText = true,
        string? tosUrl = null,
        bool privacyDated = true,
        bool subprocessorsDated = true,
        string[]? pendingProviders = null)
    {
        var inForce = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        var legal = new Mock<ILegalDocumentService>();
        foreach (var kind in new[] { LegalDocumentKind.Tos, LegalDocumentKind.Privacy, LegalDocumentKind.Dpa })
        {
            DateTime? effectiveAt = kind == LegalDocumentKind.Privacy && !privacyDated ? null : inForce;
            legal.Setup(l => l.Get(kind)).Returns(new LegalDocumentMeta(
                "2026-10-v1", effectiveAt, kind.ToString(), "summary", kind == LegalDocumentKind.Tos ? tosUrl : null));
            var hasText = kind != LegalDocumentKind.Tos || tosText;
            legal.Setup(l => l.GetText(kind, "it")).Returns(hasText ? new LegalDocumentText("it", "<p>Testo</p>") : null);
            var hasExternalCopy = kind == LegalDocumentKind.Tos && tosUrl is not null;
            legal.Setup(l => l.GetPublication(kind)).Returns(new LegalDocumentPublication(
                kind, "2026-10-v1", hasText, hasExternalCopy, hasText, [], []));
        }

        var items = (pendingProviders ?? []).Select(name => new SubprocessorItem(name, "Purpose", string.Empty, null, null, true))
            .Append(new SubprocessorItem("Resend", "Email", "Stati Uniti", null, "SCC", false))
            .ToList();
        legal.Setup(l => l.GetSubprocessors()).Returns(new SubprocessorsDocument(
            "2026-10-v1", subprocessorsDated ? inForce : null, items));
        return legal;
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
