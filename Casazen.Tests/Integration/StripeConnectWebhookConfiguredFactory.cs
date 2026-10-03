using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Casazen.Tests.Integration;

/// <summary>
/// The default integration factory with a real <c>Stripe:ConnectWebhookSecret</c> configured, on top of everything
/// <see cref="CasazenWebApplicationFactory"/> already sets (including <c>Stripe:WebhookSecret</c>). By default the
/// factory ships the public placeholder from <c>appsettings.json</c> for the connect secret, which the endpoint
/// correctly refuses to sign with (see
/// <c>WebhookSignatureTests.StripeConnectWebhook_SignedWithCommittedPlaceholderSecret_Returns500AndQueuesNothing</c>);
/// this factory lets other tests exercise the connect webhook's real signature/API-version handling (A3-39).
/// </summary>
public sealed class StripeConnectWebhookConfiguredFactory : CasazenWebApplicationFactory
{
    public const string ConnectWebhookSecret = "whsec_test_casazen_connect";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:ConnectWebhookSecret"] = ConnectWebhookSecret }));
    }
}
