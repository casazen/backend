using Casazen.Core.Models;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PL-14 (A1-06, A9-40): the subprocessor list reflects what the configuration actually uses (Resend, not SendGrid; the
/// Auth0 region read from the tenant domain; Expo; hosting; the AI provider only when active, FD-21 / A8-15). Legal
/// details that cannot be deduced are never invented and stay "pending" until configured.
/// </summary>
public class LegalDocumentSubprocessorsTests
{
    private static readonly Dictionary<string, string?> BaseSettings = new()
    {
        ["Legal:Documents:Subprocessors:Version"] = "2026-10-v1",
    };

    [Fact]
    public void GetSubprocessors_MinimalConfiguration_ListsOnlyExpo()
    {
        var document = Service([]).GetSubprocessors();

        Assert.Equal("2026-10-v1", document.Version);
        var item = Assert.Single(document.Items);
        Assert.Equal(LegalSubprocessorCatalog.Expo, item.Key);
        Assert.Equal("pushNotifications", item.PurposeKey);
        Assert.True(item.DetailsPending);
    }

    [Fact]
    public void GetSubprocessors_ResendApiKey_ListsResendAndNeverSendGrid()
    {
        var document = Service(new() { ["Email:ApiKey"] = "re_test" }).GetSubprocessors();

        var resend = Assert.Single(document.Items, i => i.Key == LegalSubprocessorCatalog.Resend);
        Assert.Equal("Resend", resend.Name);
        Assert.Equal("email", resend.PurposeKey);
        Assert.DoesNotContain(document.Items, i => i.Name.Contains("SendGrid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSubprocessors_LegacyResendApiKey_ListsResend()
    {
        var document = Service(new() { ["Email:ResendApiKey"] = "re_legacy" }).GetSubprocessors();

        Assert.Contains(document.Items, i => i.Key == LegalSubprocessorCatalog.Resend);
    }

    [Theory]
    [InlineData("dev-mp6wadq7j6bophl5.us.auth0.com", "US")]
    [InlineData("casazen.eu.auth0.com", "EU")]
    [InlineData("https://casazen.au.auth0.com/", "AU")]
    [InlineData("casazen.auth0.com", "US")]
    public void GetSubprocessors_Auth0TenantDomain_RegionReadFromTheDomain(string domain, string expectedRegion)
    {
        var document = Service(new() { ["Auth0:Domain"] = domain }).GetSubprocessors();

        var auth0 = Assert.Single(document.Items, i => i.Key == LegalSubprocessorCatalog.Auth0);
        Assert.Equal(expectedRegion, auth0.Region);
    }

    [Fact]
    public void GetSubprocessors_Auth0CustomDomain_UsesTheCanonicalDomain()
    {
        var document = Service(new()
        {
            ["Auth0:Domain"] = "login.casazen.example",
            ["Auth0:ManagementApiDomain"] = "casazen-prod.eu.auth0.com",
        }).GetSubprocessors();

        Assert.Equal("EU", Assert.Single(document.Items, i => i.Key == LegalSubprocessorCatalog.Auth0).Region);
    }

    [Fact]
    public void GetSubprocessors_Auth0CustomDomainOnly_RegionFromConfigurationOrPending()
    {
        var withoutRegion = Service(new() { ["Auth0:Domain"] = "login.casazen.example" }).GetSubprocessors();
        var pending = Assert.Single(withoutRegion.Items, i => i.Key == LegalSubprocessorCatalog.Auth0);
        Assert.Equal(string.Empty, pending.Region);
        Assert.True(pending.DetailsPending);

        var configured = Service(new()
        {
            ["Auth0:Domain"] = "login.casazen.example",
            ["Legal:Documents:Subprocessors:Providers:Auth0:Region"] = "Region set by the product owner",
        }).GetSubprocessors();
        Assert.Equal(
            "Region set by the product owner",
            Assert.Single(configured.Items, i => i.Key == LegalSubprocessorCatalog.Auth0).Region);
    }

    [Fact]
    public void GetSubprocessors_DetectedRegionAndConfiguredRegionDiffer_DetectedWins()
    {
        var document = Service(new()
        {
            ["Auth0:Domain"] = "casazen.us.auth0.com",
            ["Legal:Documents:Subprocessors:Providers:Auth0:Region"] = "EU",
        }).GetSubprocessors();

        Assert.Equal("US", Assert.Single(document.Items, i => i.Key == LegalSubprocessorCatalog.Auth0).Region);
    }

    [Fact]
    public void GetSubprocessors_AllLegalDetailsConfigured_NotPending()
    {
        var document = Service(new()
        {
            ["Stripe:SecretKey"] = "sk_test_x",
            ["Legal:Documents:Subprocessors:Providers:Stripe:Entity"] = "Entity set by the product owner",
            ["Legal:Documents:Subprocessors:Providers:Stripe:Region"] = "Region set by the product owner",
            ["Legal:Documents:Subprocessors:Providers:Stripe:TransferMechanism"] = "Mechanism set by the product owner",
        }).GetSubprocessors();

        var stripe = Assert.Single(document.Items, i => i.Key == LegalSubprocessorCatalog.Stripe);
        Assert.Equal("Entity set by the product owner", stripe.Entity);
        Assert.Equal("Mechanism set by the product owner", stripe.TransferMechanism);
        Assert.False(stripe.DetailsPending);
    }

    [Fact]
    public void GetSubprocessors_SupabasePoolerConnection_DatabaseWithRegionFromHost()
    {
        var document = Service(new()
        {
            ["ConnectionStrings:DefaultConnection"] =
                "Host=aws-0-eu-west-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.ref;Password=x",
        }).GetSubprocessors();

        var supabase = Assert.Single(document.Items, i => i.Key == LegalSubprocessorCatalog.Supabase);
        Assert.Equal("database", supabase.PurposeKey);
        Assert.Equal("eu-west-1", supabase.Region);
    }

    [Fact]
    public void GetSubprocessors_SupabaseDirectHostAndStorage_RegionFromStorage()
    {
        var document = Service(new()
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=db.abcdef.supabase.co;Database=postgres;Username=postgres;Password=x",
            ["Storage:Provider"] = "S3",
            ["Storage:S3:ServiceUrl"] = "https://abcdef.storage.supabase.co/storage/v1/s3",
            ["Storage:S3:Region"] = "eu-central-1",
        }).GetSubprocessors();

        var supabase = Assert.Single(document.Items, i => i.Key == LegalSubprocessorCatalog.Supabase);
        Assert.Equal("databaseAndStorage", supabase.PurposeKey);
        Assert.Equal("eu-central-1", supabase.Region);
    }

    [Fact]
    public void GetSubprocessors_LocalDatabaseAndFileSystemStorage_NoSupabase()
    {
        var document = Service(new()
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=casazen_dev;Username=postgres;Password=dev",
            ["Storage:Provider"] = "FileSystem",
            ["Storage:S3:ServiceUrl"] = "https://abcdef.storage.supabase.co/storage/v1/s3",
        }).GetSubprocessors();

        Assert.DoesNotContain(document.Items, i => i.Key == LegalSubprocessorCatalog.Supabase);
    }

    [Fact]
    public void GetSubprocessors_RunningOnRailway_ListsRailwayWithReplicaRegion()
    {
        var document = Service(new()
        {
            ["RAILWAY_PROJECT_ID"] = "project",
            ["RAILWAY_REPLICA_REGION"] = "europe-west4",
        }).GetSubprocessors();

        var railway = Assert.Single(document.Items, i => i.Key == LegalSubprocessorCatalog.Railway);
        Assert.Equal("europe-west4", railway.Region);
        Assert.Equal("backendHosting", railway.PurposeKey);
    }

    [Fact]
    public void GetSubprocessors_VercelUrlOrExplicitFlag_ListsVercel()
    {
        var detected = Service(new() { ["App:PublicSiteBaseUrl"] = "https://casazen-app.vercel.app" }).GetSubprocessors();
        Assert.Contains(detected.Items, i => i.Key == LegalSubprocessorCatalog.Vercel);

        var customDomain = Service(new() { ["App:PublicSiteBaseUrl"] = "https://www.casazen.example" }).GetSubprocessors();
        Assert.DoesNotContain(customDomain.Items, i => i.Key == LegalSubprocessorCatalog.Vercel);

        var declared = Service(new()
        {
            ["App:PublicSiteBaseUrl"] = "https://www.casazen.example",
            ["Legal:Documents:Subprocessors:Providers:Vercel:Enabled"] = "true",
        }).GetSubprocessors();
        Assert.Contains(declared.Items, i => i.Key == LegalSubprocessorCatalog.Vercel);
    }

    [Fact]
    public void GetSubprocessors_StubAiProvider_NotListedAndVersionKept()
    {
        var document = Service(new() { ["Ai:Provider"] = "Stub", ["Ai:ApiKey"] = "sk-unused" }).GetSubprocessors();

        Assert.DoesNotContain(document.Items, i => i.Key == LegalSubprocessorCatalog.Ai);
        Assert.Equal("2026-10-v1", document.Version);
    }

    [Fact]
    public void GetSubprocessors_DeepSeekWithoutApiKey_IsNotListed()
    {
        var document = Service(new() { ["Ai:Provider"] = "DeepSeek" }).GetSubprocessors();

        Assert.DoesNotContain(document.Items, i => i.Name == "DeepSeek");
        Assert.Equal("2026-10-v1", document.Version);
    }

    [Fact]
    public void GetSubprocessors_ActiveDeepSeekWithoutLegalDetails_ListedAsPendingWithNewVersion()
    {
        var document = Service(new() { ["Ai:Provider"] = "DeepSeek", ["Ai:ApiKey"] = "sk-test" }).GetSubprocessors();

        var ai = Assert.Single(document.Items, i => i.Name == "DeepSeek");
        Assert.Equal(string.Empty, ai.Region);
        Assert.Null(ai.TransferMechanism);
        Assert.Equal("ai", ai.PurposeKey);
        Assert.True(ai.DetailsPending);
        Assert.Equal("2026-10-v1+ai-deepseek", document.Version);
    }

    [Fact]
    public void GetSubprocessors_ActiveDeepSeekWithConfiguredDetails_NotPending()
    {
        var document = Service(new()
        {
            ["Ai:Provider"] = "DeepSeek",
            ["Ai:ApiKey"] = "sk-test",
            ["Ai:Subprocessor:Entity"] = "Entity set by the product owner",
            ["Ai:Subprocessor:Region"] = "Region set by the product owner",
            ["Ai:Subprocessor:TransferMechanism"] = "Mechanism set by the product owner",
            ["Ai:Subprocessor:Purpose"] = "SEO content",
        }).GetSubprocessors();

        var ai = Assert.Single(document.Items, i => i.Name == "DeepSeek");
        Assert.Equal("SEO content", ai.Purpose);
        Assert.Null(ai.PurposeKey);
        Assert.Equal("Region set by the product owner", ai.Region);
        Assert.Equal("Mechanism set by the product owner", ai.TransferMechanism);
        Assert.False(ai.DetailsPending);
    }

    [Fact]
    public void GetSubprocessors_EffectiveAtConfigured_ReadAsUtc()
    {
        var document = Service(new() { ["Legal:Documents:Subprocessors:EffectiveAt"] = "2026-10-15" }).GetSubprocessors();

        Assert.Equal(new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc), document.EffectiveAt);
        Assert.Equal(DateTimeKind.Utc, document.EffectiveAt!.Value.Kind);
    }

    [Fact]
    public void GetSubprocessors_EffectiveAtMissing_Null()
    {
        Assert.Null(Service([]).GetSubprocessors().EffectiveAt);
    }

    private static LegalDocumentService Service(Dictionary<string, string?> settings) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(BaseSettings)
                .AddInMemoryCollection(settings)
                .Build(),
            NullLogger<LegalDocumentService>.Instance);
}

/// <summary>
/// PL-14 (A1-06): the texts are the files provided by the product owner (D14); while one is missing the document is not
/// available and nothing is invented.
/// </summary>
public sealed class LegalDocumentTextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"legal-docs-{Guid.NewGuid():N}");

    public LegalDocumentTextTests() => Directory.CreateDirectory(Path.Combine(_root, "tos"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void GetText_NoFile_ReturnsNull()
    {
        Assert.Null(Service().GetText(LegalDocumentKind.Tos, "it"));
        Assert.Null(Service().GetText(LegalDocumentKind.Privacy, "en"));
    }

    [Fact]
    public void GetText_FileOfTheCurrentVersion_ReturnsSanitizedHtml()
    {
        File.WriteAllText(
            Path.Combine(_root, "tos", "2026-10-v1.it.html"),
            "<h2>Art. 1</h2><p onclick=\"x()\">Testo</p><script>alert(1)</script>");

        var text = Service().GetText(LegalDocumentKind.Tos, "it");

        Assert.NotNull(text);
        Assert.Equal("it", text.Language);
        Assert.Equal("<h2>Art. 1</h2><p>Testo</p>", text.Html);
    }

    [Fact]
    public void GetText_FileOfAnotherVersion_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_root, "tos", "2026-06-v1.it.html"), "<p>Vecchia versione</p>");

        Assert.Null(Service().GetText(LegalDocumentKind.Tos, "it"));
    }

    [Fact]
    public void GetText_EnglishMissing_FallsBackToItalian()
    {
        File.WriteAllText(Path.Combine(_root, "tos", "2026-10-v1.it.html"), "<p>Testo italiano</p>");

        var text = Service().GetText(LegalDocumentKind.Tos, "en-GB");

        Assert.NotNull(text);
        Assert.Equal("it", text.Language);
    }

    [Fact]
    public void GetText_EnglishPresent_ReturnsEnglish()
    {
        File.WriteAllText(Path.Combine(_root, "tos", "2026-10-v1.it.html"), "<p>Testo italiano</p>");
        File.WriteAllText(Path.Combine(_root, "tos", "2026-10-v1.en.html"), "<p>English text</p>");

        var text = Service().GetText(LegalDocumentKind.Tos, "en");

        Assert.NotNull(text);
        Assert.Equal("en", text.Language);
        Assert.Equal("<p>English text</p>", text.Html);
    }

    [Fact]
    public void GetText_VersionWithPathTraversal_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_root, "secret.it.html"), "<p>Fuori cartella</p>");

        var text = Service(new() { ["Legal:Documents:Tos:Version"] = "../secret" }).GetText(LegalDocumentKind.Tos, "it");

        Assert.Null(text);
    }

    [Fact]
    public void Get_DocumentUrlAndEffectiveAt_ReadFromConfigurationHttpsOnly()
    {
        var meta = Service(new()
        {
            ["Legal:Documents:Privacy:EffectiveAt"] = "2026-10-15T00:00:00Z",
            ["Legal:Documents:Privacy:DocumentUrl"] = "https://legal.example.test/privacy.pdf",
            ["Legal:Documents:Dpa:DocumentUrl"] = "http://legal.example.test/dpa.pdf",
        });

        var privacy = meta.Get(LegalDocumentKind.Privacy);
        Assert.Equal(new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc), privacy.EffectiveAt);
        Assert.Equal("https://legal.example.test/privacy.pdf", privacy.DocumentUrl);
        Assert.Null(meta.Get(LegalDocumentKind.Dpa).DocumentUrl);
        Assert.Null(meta.Get(LegalDocumentKind.Tos).EffectiveAt);
    }

    private LegalDocumentService Service(Dictionary<string, string?>? settings = null) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Legal:ContentPath"] = _root,
                    ["Legal:Documents:Tos:Version"] = "2026-10-v1",
                    ["Legal:Documents:Privacy:Version"] = "2026-10-v1",
                })
                .AddInMemoryCollection(settings ?? [])
                .Build(),
            NullLogger<LegalDocumentService>.Instance);
}
