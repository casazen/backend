using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class AiOptionsMergerTests
{
    [Fact]
    public void Merge_NoRow_KeepsTheServerKey()
    {
        var effective = AiOptionsMerger.Merge(Server(), row: null);

        Assert.Equal(AiOptions.DeepSeekProvider, effective.Provider);
        Assert.Equal("sk-server-key", effective.ApiKey);
        Assert.True(effective.IsExternalProviderActive());
    }

    [Fact]
    public void Merge_AdminStub_TurnsTheExternalProviderOff()
    {
        var effective = AiOptionsMerger.Merge(Server(), new PlatformAiSettings { Provider = "Stub" });

        Assert.Equal("Stub", effective.Provider);
        Assert.Equal("sk-server-key", effective.ApiKey);
        Assert.False(effective.IsExternalProviderActive());
    }

    [Fact]
    public void Merge_AdminKey_ReplacesTheServerKey()
    {
        var effective = AiOptionsMerger.Merge(
            Server(),
            new PlatformAiSettings { Provider = AiOptions.DeepSeekProvider, ApiKey = "sk-admin-key", Model = "deepseek-chat" });

        Assert.Equal("sk-admin-key", effective.ApiKey);
        Assert.Equal("deepseek-chat", effective.Model);
        Assert.True(effective.IsExternalProviderActive());
    }

    [Fact]
    public void ToAdmin_NeverReturnsTheKey()
    {
        const string key = "sk-admin-secret-value";
        var view = AiOptionsMerger.ToAdmin(
            new AiOptions { Provider = "Stub" },
            new PlatformAiSettings { Provider = AiOptions.DeepSeekProvider, ApiKey = key });

        Assert.Equal("••••alue", view.ApiKeyHint);
        Assert.True(view.HasStoredKey);
        Assert.True(view.ExternalActive);
        Assert.Equal(AiOptionsMerger.AdminSource, view.Source);
        Assert.DoesNotContain(key, $"{view.Provider} {view.Model} {view.ApiKeyHint} {view.Source}");
    }

    [Fact]
    public void Merge_OpenAiChoice_UsesOpenAiAndDropsTheDeepSeekHost()
    {
        var effective = AiOptionsMerger.Merge(
            Server(),
            new PlatformAiSettings { Provider = "OpenAI", ApiKey = "sk-openai-key" });

        Assert.Equal(AiOptions.OpenAiProvider, effective.Provider);
        Assert.Equal("https://api.openai.com/v1", effective.OpenAiBaseUrl);
        Assert.Equal("gpt-4o-mini", effective.Model);
        Assert.True(effective.IsExternalProviderActive());
        Assert.Equal(AiOptions.OpenAiProvider, effective.Subprocessor.Name);
        Assert.Null(effective.Subprocessor.Entity);
    }

    [Fact]
    public void NormalizeBaseUrl_HttpOrUserInfo_IsRefused()
    {
        Assert.Throws<DomainRuleException>(() => AiOptionsMerger.NormalizeBaseUrl("http://api.example.com"));
        Assert.Throws<DomainRuleException>(() => AiOptionsMerger.NormalizeBaseUrl("https://user:secret@api.example.com"));
    }

    [Fact]
    public void NormalizeProvider_Unknown_IsRefused()
    {
        var error = Assert.Throws<DomainRuleException>(() => AiOptionsMerger.NormalizeProvider("Gemini"));

        Assert.Equal(AiSettingsErrorCodes.ProviderInvalid, error.Code);
    }

    private static AiOptions Server() => new()
    {
        Provider = AiOptions.DeepSeekProvider,
        ApiKey = "sk-server-key",
        Model = "deepseek-v4-flash",
    };
}
