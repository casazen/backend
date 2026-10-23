using System.Net;
using System.Text;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CenedApeRegistryTests
{
    private static readonly ApeRegistryQuery Monza = new("Monza", 123.45m, new DateOnly(2018, 3, 15));

    [Fact]
    public async Task FindAsync_OneCertificateOfTheComuneWithTheIndex_GivesItsCodeAndClass()
    {
        var handler = new StubHandler("""[{"cod_ape":"12345000067819","classe_energetica":"F","data_ins":"15/03/2018"}]""");

        var entry = await Registry(handler).FindAsync(Monza);

        Assert.Equal(new ApeRegistryEntry("12345000067819", "F", new DateOnly(2018, 3, 15)), entry);
        // Only the comune and the index are sent (open data, Socrata): no document, no person.
        var query = Uri.UnescapeDataString(handler.Requests.Single().Query);
        Assert.Contains("upper(comune)=upper('Monza') AND ep_gl_nren=123.45", query);
        Assert.StartsWith("https://www.dati.lombardia.it/resource/bbky-sde5.json", handler.Requests.Single().GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task FindAsync_TwoCertificatesWithTheSameIndex_TheIssueDateTellsThemApart()
    {
        var handler = new StubHandler("""
            [{"cod_ape":"12345000067819","classe_energetica":"F","data_ins":"15/03/2018"},
             {"cod_ape":"12345000099918","classe_energetica":"F","data_ins":"03/05/2018"}]
            """);

        Assert.Equal("12345000067819", (await Registry(handler).FindAsync(Monza))?.Code);
        Assert.Null(await Registry(handler).FindAsync(Monza with { IssueDate = null }));
    }

    [Fact]
    public async Task FindAsync_NoAnswerOrSwitchedOff_GivesNothing()
    {
        Assert.Null(await Registry(new StubHandler("[]")).FindAsync(Monza));
        Assert.Null(await Registry(new StubHandler("oops", HttpStatusCode.InternalServerError)).FindAsync(Monza));

        var handler = new StubHandler("""[{"cod_ape":"12345000067819"}]""");
        Assert.Null(await Registry(handler, enabled: false).FindAsync(Monza));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FindAsync_ApostropheOfAComune_IsEscaped()
    {
        var handler = new StubHandler("[]");

        await Registry(handler).FindAsync(new ApeRegistryQuery("Cassina de' Pecchi", 98.1m, null));

        Assert.Contains("upper('Cassina de'' Pecchi')", Uri.UnescapeDataString(handler.Requests.Single().Query));
    }

    private static CenedApeRegistry Registry(StubHandler handler, bool enabled = true) =>
        new(new HttpClient(handler), Options.Create(new ApeRegistryOptions { Enabled = enabled }), NullLogger<CenedApeRegistry>.Instance);

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
