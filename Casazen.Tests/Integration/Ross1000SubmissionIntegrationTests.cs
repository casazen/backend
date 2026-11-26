using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Ross1000;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed class Ross1000SubmissionFactory : OctorateOnboardingOpenFactory
{
    public readonly TestTransport Transport = new();
    public bool RouteActive;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddRoss1000Direct();
            services.RemoveAll<IRoss1000DirectRoute>();
            var route = new Mock<IRoss1000DirectRoute>();
            route.Setup(x => x.EffectiveFromAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => RouteActive ? new DateOnly(2026, 1, 1) : null);
            services.AddSingleton(route.Object);
            // Model an enabled pilot only inside this scoped service, while replacing the entire HTTP transport.
            services.AddScoped(sp => new Ross1000DayService(sp.GetRequiredService<AppDbContext>(), route.Object,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ross1000:Enabled"] = "true" }).Build(),
                new Ross1000Client(new HttpClient(Transport, disposeHandler: false)), TimeProvider.System,
                Mock.Of<IHostEnvironment>(e => e.EnvironmentName == Environments.Production)));
        });
    }

    public sealed class TestTransport : HttpMessageHandler
    {
        public int Sends;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Sends);
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var code = xml.Descendants("codice").Single().Value;
            var day = xml.Descendants("data").Single().Value;
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            XNamespace ws = "http://checkin.ws.service.turismo5.gies.it/";
            var response = new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(ws + "inviaMovimentazioneListResponse", new XElement("return",
                    new XElement("struttura", new XAttribute("codice", code), new XElement("giorno", new XAttribute("data", day)))))));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToString()) };
        }
    }
}

public sealed class Ross1000SubmissionIntegrationTests(Ross1000SubmissionFactory factory) : IClassFixture<Ross1000SubmissionFactory>
{
    [PostgresFact]
    public async Task ConfirmedDayIsSubmittedOnceAndActiveStructureCodeCannotChange()
    {
        var owner = "ross1000-owner-" + Guid.NewGuid().ToString("N");
        var property = await factory.SeedPropertyAsync(owner);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Properties.IgnoreQueryFilters().Where(p => p.Id == property.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ComuneIstatCode, "013075").SetProperty(p => p.Country, "IT"));
        }
        using var client = factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var propertyPath = $"/api/properties/{property.Id}";
        var credentials = new { username = "sandbox", password = "fictional", structureCode = "test" };
        var credsPath = propertyPath + "/istat-credentials";
        (await client.PutAsJsonAsync(credsPath, credentials)).EnsureSuccessStatusCode();
        var dayPath = propertyPath + "/ross1000/2026-01-02";
        var capacity = new Ross1000Capacity(false, 0, 0, 0);
        var local = await client.PostAsJsonAsync(dayPath + "/preview", capacity);
        local.EnsureSuccessStatusCode();
        Assert.Equal("preview_only", (await local.Content.ReadFromJsonAsync<Ross1000Preview>())!.State);
        factory.RouteActive = true;
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(credsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(credsPath,
            new { credentials.username, credentials.password, structureCode = "other" })).StatusCode);
        (await client.PutAsJsonAsync(credsPath, new { credentials.username, password = "changed", credentials.structureCode })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(dayPath + "/preview", capacity);
        response.EnsureSuccessStatusCode();
        var preview = await response.Content.ReadFromJsonAsync<Ross1000Preview>();
        Assert.Equal("ready", preview!.State);
        var bad = await client.PostAsJsonAsync(dayPath + "/send", new { capacity, hash = "outdated" });
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);
        for (var i = 0; i < 2; i++)
        {
            var sent = await client.PostAsJsonAsync(dayPath + "/send", new { capacity, preview.Hash });
            sent.EnsureSuccessStatusCode();
            Assert.Equal("accepted", (await sent.Content.ReadFromJsonAsync<Ross1000Preview>())!.State);
        }
        Assert.Equal(1, factory.Transport.Sends);
    }
}
