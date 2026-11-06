using System.Net;
using System.Net.Http.Headers;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RichardSzalay.MockHttp;
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

    private const string Address = "https://files.example.com/attachments/SECRET-TOKEN-9c1/photo.png?sig=SECRET-SIG";
    private const string GoneAddress = "https://files.example.com/attachments/SECRET-TOKEN-9c1/gone.png?sig=SECRET-SIG";

    /// <summary>
    /// The privacy rule of the proxy, through the client the container really builds: the address the channel gave (it may carry a token or the name
    /// of a file) is in no log, of any category. The handlers of logging that every client of <c>IHttpClientFactory</c> gets by default write the host and
    /// the path of the request at Information, so <c>AddMessagesHub</c> removes them; a spy on every category, from the real pipeline, must see none.
    /// </summary>
    [Fact]
    public async Task TheAttachmentClient_LogsNothingOfTheAddress_InAnyCategory()
    {
        var log = new CapturingLoggerProvider();
        var http = new MockHttpMessageHandler();
        http.When(Address).Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Png() });
        http.When(GoneAddress).Respond(HttpStatusCode.NotFound);
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        services.AddMessagesHub();
        services.AddHttpClient<IOtaAttachmentFetcher, OtaAttachmentFetcher>().ConfigurePrimaryHttpMessageHandler(() => http);
        await using var provider = services.BuildServiceProvider();
        var fetcher = provider.GetRequiredService<IOtaAttachmentFetcher>();

        var file = await fetcher.FetchAsync(Address);
        var gone = await fetcher.FetchAsync(GoneAddress);

        Assert.NotNull(file);
        Assert.Null(gone);
        Assert.NotEmpty(log.Entries);
        Assert.All(log.Entries, entry =>
        {
            Assert.DoesNotContain("files.example.com", entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SECRET", entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("attachments", entry, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>The control of the check above: a client that keeps the default logging of the factory does write the host and the path, so the spy sees them.</summary>
    [Fact]
    public async Task AClientThatKeepsTheDefaultLogging_WritesTheHostAndThePath_SoTheCheckAboveCanSeeIt()
    {
        var log = new CapturingLoggerProvider();
        var http = new MockHttpMessageHandler();
        http.When(Address).Respond(HttpStatusCode.OK);
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        services.AddHttpClient("control").ConfigurePrimaryHttpMessageHandler(() => http);
        await using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("control");
        using var response = await client.GetAsync(Address);

        Assert.Contains(log.Entries, entry => entry.Contains("files.example.com", StringComparison.OrdinalIgnoreCase));
    }

    private static ByteArrayContent Png()
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        return content;
    }

    /// <summary>Records every message and every scope of every category, as a provider that writes them somewhere would see them.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, List<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            lock (entries)
                entries.Add($"{category} scope: {state}");

            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add($"{category}: {formatter(state, exception)}");
                if (exception is not null)
                    entries.Add(exception.ToString());
            }
        }
    }
}
