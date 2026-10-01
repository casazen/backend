using System.Text.RegularExpressions;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// PL14-SUBP: the legal details of the subprocessors committed in <c>appsettings.json</c> (entity, location, transfer
/// basis) are public facts taken from each provider's official DPA / terms / security pages. Every provider with a
/// detail carries its <c>Source</c> (official URL and consultation date); what depends on a choice of the product owner
/// (Supabase project region, Auth0 tenant region, Railway region) is never written here but read from the running
/// configuration (<see cref="LegalSubprocessorCatalog"/>), and the AI provider's transfer basis stays undecided.
/// Runbook: <c>docs/runbooks/legal-documents.md</c> § 3.
/// </summary>
public class SubprocessorDetailsConfigurationTests
{
    private static readonly string[] ProviderKeys = ["Supabase", "Auth0", "Stripe", "Resend", "Expo", "Railway", "Vercel"];

    private static readonly Regex ConsultationDate = new(@"\b20\d{2}-\d{2}-\d{2}\b", RegexOptions.Compiled);

    /// <summary>A configuration in which every region the code can read is present (a regional Auth0 tenant, a Supabase pooler host, Railway).</summary>
    private static readonly Dictionary<string, string?> FullyDetectedEnvironment = new()
    {
        ["Auth0:Domain"] = "casazen-test.eu.auth0.com",
        ["ConnectionStrings:DefaultConnection"] =
            "Host=aws-0-eu-west-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.ref;Password=x",
        ["Stripe:SecretKey"] = "sk_test_x",
        ["Email:ApiKey"] = "re_test",
        ["App:PublicSiteBaseUrl"] = "https://casazen-test.vercel.app",
        ["RAILWAY_PROJECT_ID"] = "project",
        ["RAILWAY_REPLICA_REGION"] = "europe-west4",
    };

    [Theory]
    [InlineData("Supabase")]
    [InlineData("Auth0")]
    [InlineData("Stripe")]
    [InlineData("Resend")]
    [InlineData("Expo")]
    [InlineData("Railway")]
    [InlineData("Vercel")]
    public void Appsettings_ProviderLegalDetails_CiteAnOfficialSourceWithConsultationDate(string provider)
    {
        var section = Appsettings().GetSection($"{LegalSubprocessorCatalog.ProvidersSection}:{provider}");

        AssertDetailsAreSourced(provider, section["Entity"], section["Region"], section["TransferMechanism"], section["Source"]);
    }

    [Fact]
    public void Appsettings_AiProviderLegalDetails_CiteAnOfficialSourceWithConsultationDate()
    {
        var section = Appsettings().GetSection("Ai:Subprocessor");

        AssertDetailsAreSourced("Ai", section["Entity"], section["Region"], section["TransferMechanism"], section["Source"]);
    }

    [Theory]
    [InlineData("Supabase")]
    [InlineData("Auth0")]
    [InlineData("Railway")]
    public void Appsettings_RegionChosenByTheProductOwner_IsNotWrittenInTheRepository(string provider)
    {
        var region = Appsettings()[$"{LegalSubprocessorCatalog.ProvidersSection}:{provider}:Region"];

        Assert.True(
            string.IsNullOrWhiteSpace(region),
            $"{provider}: the region depends on the project/tenant/service chosen by the product owner; the list reads it from " +
            "the running configuration (pooler host, Storage:S3:Region, tenant domain, RAILWAY_REPLICA_REGION).");
    }

    [Fact]
    public void Appsettings_ContractingEntities_AreTheOnesStatedByTheProviders()
    {
        var configuration = Appsettings();
        string Entity(string provider) => configuration[$"{LegalSubprocessorCatalog.ProvidersSection}:{provider}:Entity"] ?? string.Empty;

        Assert.StartsWith("Supabase Pte. Ltd.", Entity("Supabase"), StringComparison.Ordinal);
        Assert.StartsWith("Okta, Inc.", Entity("Auth0"), StringComparison.Ordinal);
        Assert.StartsWith("Stripe Payments Europe, Limited", Entity("Stripe"), StringComparison.Ordinal);
        Assert.StartsWith("Plus Five Five, Inc.", Entity("Resend"), StringComparison.Ordinal);
        Assert.StartsWith("650 Industries, Inc.", Entity("Expo"), StringComparison.Ordinal);
        Assert.StartsWith("Railway Corporation", Entity("Railway"), StringComparison.Ordinal);
        Assert.StartsWith("Vercel Inc.", Entity("Vercel"), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CommittedDefaultsAndEveryRegionDetected_NoProviderIsPending()
    {
        var items = LegalSubprocessorCatalog.Build(Appsettings(FullyDetectedEnvironment));

        Assert.Equal(
            [LegalSubprocessorCatalog.Supabase, LegalSubprocessorCatalog.Auth0, LegalSubprocessorCatalog.Stripe,
             LegalSubprocessorCatalog.Resend, LegalSubprocessorCatalog.Expo, LegalSubprocessorCatalog.Railway,
             LegalSubprocessorCatalog.Vercel],
            items.Select(i => i.Key).ToArray());
        Assert.All(items, item =>
        {
            Assert.False(item.DetailsPending, $"{item.Name} is still pending");
            Assert.False(string.IsNullOrWhiteSpace(item.Entity), $"{item.Name}: no entity");
            Assert.False(string.IsNullOrWhiteSpace(item.Region), $"{item.Name}: no region");
            Assert.False(string.IsNullOrWhiteSpace(item.TransferMechanism), $"{item.Name}: no transfer basis");
        });
        Assert.Equal("EU", items.Single(i => i.Key == LegalSubprocessorCatalog.Auth0).Region);
        Assert.Equal("eu-west-1", items.Single(i => i.Key == LegalSubprocessorCatalog.Supabase).Region);
        Assert.Equal("europe-west4", items.Single(i => i.Key == LegalSubprocessorCatalog.Railway).Region);
    }

    [Fact]
    public void Build_CommittedDefaultsWithoutDetectableRegion_SupabaseAuth0AndRailwayStayPending()
    {
        // A custom Auth0 domain, a direct Supabase host and Railway without a replica region say nothing about where
        // the data is: the region is not deduced, the entries stay "in definizione" until the product owner sets it.
        var items = LegalSubprocessorCatalog.Build(Appsettings(new()
        {
            ["Auth0:Domain"] = "login.casazen.example",
            ["ConnectionStrings:DefaultConnection"] = "Host=db.abcdef.supabase.co;Database=postgres;Username=postgres;Password=x",
            ["RAILWAY_PROJECT_ID"] = "project",
        }));

        foreach (var key in new[] { LegalSubprocessorCatalog.Supabase, LegalSubprocessorCatalog.Auth0, LegalSubprocessorCatalog.Railway })
        {
            var item = items.Single(i => i.Key == key);
            Assert.True(item.DetailsPending, $"{item.Name} must stay pending without a detectable region");
            Assert.Equal(string.Empty, item.Region);
            Assert.False(string.IsNullOrWhiteSpace(item.Entity));
            Assert.False(string.IsNullOrWhiteSpace(item.TransferMechanism));
        }
    }

    [Fact]
    public void Build_ActiveDeepSeekWithCommittedDefaults_EntityAndRegionKnownTransferBasisStillToDecide()
    {
        var items = LegalSubprocessorCatalog.Build(Appsettings(new() { ["Ai:Provider"] = "DeepSeek", ["Ai:ApiKey"] = "sk-test" }));

        var ai = items.Single(i => i.Key == LegalSubprocessorCatalog.Ai);
        Assert.Contains("DeepSeek", ai.Entity, StringComparison.Ordinal);
        Assert.Contains("cinese", ai.Region, StringComparison.Ordinal);
        Assert.Null(ai.TransferMechanism);
        Assert.True(ai.DetailsPending);
    }

    [Fact]
    public void Build_StubAiProviderWithCommittedDefaults_NoAiProviderIsListed()
    {
        var items = LegalSubprocessorCatalog.Build(Appsettings(FullyDetectedEnvironment));

        Assert.DoesNotContain(items, i => i.Key == LegalSubprocessorCatalog.Ai);
    }

    [Fact]
    public void Appsettings_SubprocessorList_StaysUnpublishedUntilTheProductOwnerSetsTheDate()
    {
        // EffectiveAt (and a new Version when the list changes) is the product owner's decision, not the code's.
        Assert.True(string.IsNullOrWhiteSpace(Appsettings()["Legal:Documents:Subprocessors:EffectiveAt"]));
    }

    private static void AssertDetailsAreSourced(string provider, params string?[] values)
    {
        var (entity, region, transfer, source) = (values[0], values[1], values[2], values[3]);
        var hasDetails = !string.IsNullOrWhiteSpace(entity) || !string.IsNullOrWhiteSpace(region)
                         || !string.IsNullOrWhiteSpace(transfer);
        if (!hasDetails)
            return;

        Assert.True(
            !string.IsNullOrWhiteSpace(source) && source.Contains("https://", StringComparison.Ordinal),
            $"{provider}: legal details without an official source URL (Source).");
        Assert.True(
            ConsultationDate.IsMatch(source!),
            $"{provider}: Source must state the consultation date (yyyy-MM-dd).");
    }

    private static IConfiguration Appsettings(Dictionary<string, string?>? overrides = null)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindSolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false);
        if (overrides is not null)
            builder.AddInMemoryCollection(overrides);
        return builder.Build();
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
