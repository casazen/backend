using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>E05/F02/US01/T01: what <c>AddMessagesHub</c> registers, and the connection guard of the attachment client.</summary>
public class MessagesHubRegistrationTests
{
    [Fact]
    public void AddMessagesHub_RegistersTheServiceAndTheAttachmentFetcher_AndNothingElse()
    {
        var services = new ServiceCollection();

        services.AddMessagesHub();

        var hub = Assert.Single(services, d => d.ServiceType == typeof(IMessageHubService));
        Assert.Equal(typeof(MessageHubService), hub.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, hub.Lifetime);
        Assert.Single(services, d => d.ServiceType == typeof(IOtaAttachmentFetcher));
    }

    [Fact]
    public void TheAttachmentFetcher_IsResolvedFromTheContainer_WithItsOwnClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMessagesHub();
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();

        Assert.IsType<OtaAttachmentFetcher>(scope.ServiceProvider.GetRequiredService<IOtaAttachmentFetcher>());
    }

    [Fact]
    public void TheHandlerOfTheAttachmentClient_FollowsNoRedirectAndKeepsNoCookie_AndChecksTheAddressWhereTheSocketIsOpened()
    {
        using var handler = OtaAttachmentFetcher.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.7")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.0.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    public async Task AHostThatResolvesToTheMachineOrToThePrivateNetwork_IsRefusedWhereTheSocketWouldBeOpened(string host)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => OtaAttachmentFetcher.ResolvePublicAddressesAsync(host, CancellationToken.None));
    }

    [Fact]
    public async Task AHostThatResolvesToAPublicAddress_IsKept()
    {
        var addresses = await OtaAttachmentFetcher.ResolvePublicAddressesAsync("93.184.216.34", CancellationToken.None);

        Assert.Equal(["93.184.216.34"], addresses.Select(a => a.ToString()));
    }
}
