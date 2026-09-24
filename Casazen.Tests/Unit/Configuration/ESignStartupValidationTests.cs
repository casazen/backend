using Casazen.Core.Features;
using Casazen.Infrastructure.Features;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// LT-02 (A7-20): with <c>Features:ESignProvider</c> on the e-sign webhook exists, so its HMAC secret is required at
/// startup and a public placeholder never counts as a secret. With the flag off (default) nothing is required.
/// </summary>
public class ESignStartupValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PLACEHOLDER_SET_IN_ENV")]
    [InlineData("YOUR_ESIGN_WEBHOOK_SECRET")]
    [InlineData("short-secret")]
    public void Startup_ProviderFlagOnWithoutARealSecret_Fails(string? secret)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Features:ESignProvider"] = "true",
            ["ESign:WebhookSecret"] = secret,
        });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("ESign:WebhookSecret", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_ProviderFlagOnWithASecret_Succeeds()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Features:ESignProvider"] = "true",
            ["ESign:WebhookSecret"] = "whsec-0123456789abcdef0123456789",
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Startup_ProviderFlagOffWithTheCommittedEmptySecret_Succeeds()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["ESign:WebhookSecret"] = "" });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Theory]
    [InlineData("PLACEHOLDER_SET_IN_ENV", true)]
    [InlineData("  ", true)]
    [InlineData("esign-test-secret", false)]
    public void IsWebhookSecretMissing_Value_DetectsPublicOrEmptySecrets(string secret, bool missing) =>
        Assert.Equal(missing, ESignOptionsValidator.IsWebhookSecretMissing(secret));

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFeatureFlags>(new ConfigurationFeatureFlags(configuration));
        services.AddCasazenLeaseSigning(configuration);
        return services.BuildServiceProvider();
    }
}
