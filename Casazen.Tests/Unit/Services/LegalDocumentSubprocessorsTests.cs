using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FD-21 (A8-15): an active external AI provider receives prompts, so it must appear in the subprocessor list shown in
/// onboarding; its legal details are never invented and stay "pending" until configured.
/// </summary>
public class LegalDocumentSubprocessorsTests
{
    private static readonly Dictionary<string, string?> BaseList = new()
    {
        ["Legal:Documents:Subprocessors:Version"] = "2026-06-v1",
        ["Legal:Documents:Subprocessors:Items:0:Name"] = "Supabase",
        ["Legal:Documents:Subprocessors:Items:0:Purpose"] = "Database",
        ["Legal:Documents:Subprocessors:Items:0:Region"] = "EU",
    };

    [Fact]
    public void GetSubprocessors_StubProvider_ListsOnlyConfiguredItems()
    {
        var document = Service(new() { ["Ai:Provider"] = "Stub", ["Ai:ApiKey"] = "sk-unused" }).GetSubprocessors();

        Assert.Equal("2026-06-v1", document.Version);
        var item = Assert.Single(document.Items);
        Assert.Equal("Supabase", item.Name);
        Assert.False(item.DetailsPending);
    }

    [Fact]
    public void GetSubprocessors_DeepSeekWithoutApiKey_IsNotListed()
    {
        var document = Service(new() { ["Ai:Provider"] = "DeepSeek" }).GetSubprocessors();

        Assert.DoesNotContain(document.Items, i => i.Name == "DeepSeek");
        Assert.Equal("2026-06-v1", document.Version);
    }

    [Fact]
    public void GetSubprocessors_ActiveDeepSeekWithoutLegalDetails_ListedAsPendingWithNewVersion()
    {
        var document = Service(new() { ["Ai:Provider"] = "DeepSeek", ["Ai:ApiKey"] = "sk-test" }).GetSubprocessors();

        var ai = Assert.Single(document.Items, i => i.Name == "DeepSeek");
        Assert.Equal(string.Empty, ai.Region);
        Assert.Null(ai.TransferMechanism);
        Assert.True(ai.DetailsPending);
        Assert.Equal("2026-06-v1+ai-deepseek", document.Version);
    }

    [Fact]
    public void GetSubprocessors_ActiveDeepSeekWithConfiguredDetails_NotPending()
    {
        var document = Service(new()
        {
            ["Ai:Provider"] = "DeepSeek",
            ["Ai:ApiKey"] = "sk-test",
            ["Ai:Subprocessor:Region"] = "Region set by the product owner",
            ["Ai:Subprocessor:TransferMechanism"] = "Mechanism set by the product owner",
            ["Ai:Subprocessor:Purpose"] = "SEO content",
        }).GetSubprocessors();

        var ai = Assert.Single(document.Items, i => i.Name == "DeepSeek");
        Assert.Equal("SEO content", ai.Purpose);
        Assert.Equal("Region set by the product owner", ai.Region);
        Assert.Equal("Mechanism set by the product owner", ai.TransferMechanism);
        Assert.False(ai.DetailsPending);
    }

    [Fact]
    public void GetSubprocessors_ProviderAlreadyInConfiguredList_NotDuplicatedAndVersionKept()
    {
        var document = Service(new()
        {
            ["Ai:Provider"] = "DeepSeek",
            ["Ai:ApiKey"] = "sk-test",
            ["Legal:Documents:Subprocessors:Items:1:Name"] = "DeepSeek",
            ["Legal:Documents:Subprocessors:Items:1:Purpose"] = "AI",
            ["Legal:Documents:Subprocessors:Items:1:Region"] = "Configured region",
        }).GetSubprocessors();

        Assert.Single(document.Items, i => i.Name == "DeepSeek");
        Assert.Equal("2026-06-v1", document.Version);
    }

    private static LegalDocumentService Service(Dictionary<string, string?> settings) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(BaseList)
            .AddInMemoryCollection(settings)
            .Build());
}
