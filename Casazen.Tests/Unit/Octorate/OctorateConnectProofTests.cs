using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the consent (E02/F01/US02/T01): the OAuth state is single use, expires, and is bound to the org, the user and the
/// redirect address it was created for (permission rule); no token, code or state is returned by the endpoints or written to the log
/// (data rule, ADR-023-R13). The second proof runs the real controller, connect service, state service, token service and client, with
/// Octorate's answers stubbed.
/// </summary>
public class OctorateConnectProofTests
{
    // One provider for the whole class: EF keeps one model per provider.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    private static readonly Guid OrgA = Guid.Parse("2b9e6f1c-4d3a-4c7e-9a51-6e0f3d2c1b01");
    private static readonly Guid OrgB = Guid.Parse("8c1d2e3f-5a6b-4c7d-8e9f-0a1b2c3d4e02");
    private const string UserA = "auth0|owner-a";
    private const string Redirect = "https://app.example.test/app/short-rent/octorate/callback";

    [Fact]
    public async Task OctorateOAuthState_SingleUseAndBoundToTenant_Test()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"octorate-state-{Guid.NewGuid()}").Options);
        var states = new OctorateOAuthStateService(
            db, Microsoft.Extensions.Options.Options.Create(new OctorateOptions()), Host("Staging"), clock, NullLogger<OctorateOAuthStateService>.Instance);

        var state = await states.CreateAsync(OrgA, UserA, Redirect, CancellationToken.None);

        // Only the hash of the state is stored, with its expiry and its environment.
        var row = Assert.Single(await db.OctorateOAuthStates.AsNoTracking().ToListAsync());
        Assert.Equal(OctorateOAuthStateService.HashOf(state), row.StateHash);
        Assert.NotEqual(state, row.StateHash);
        Assert.Equal(OctorateEnvironment.Test, row.Environment);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(OctorateOptions.DefaultStateLifetimeMinutes), row.ExpiresAt);

        // Another org, another user, another redirect address, a made-up state: the same refusal every time.
        var refusals = new[]
        {
            await Assert.ThrowsAsync<OctorateOAuthStateInvalidException>(() => states.ConsumeAsync(state, OrgB, UserA, Redirect, CancellationToken.None)),
            await Assert.ThrowsAsync<OctorateOAuthStateInvalidException>(() => states.ConsumeAsync(state, OrgA, "auth0|someone-else", Redirect, CancellationToken.None)),
            await Assert.ThrowsAsync<OctorateOAuthStateInvalidException>(() => states.ConsumeAsync(state, OrgA, UserA, "https://evil.example.test/callback", CancellationToken.None)),
            await Assert.ThrowsAsync<OctorateOAuthStateInvalidException>(() => states.ConsumeAsync("made-up-state", OrgA, UserA, Redirect, CancellationToken.None)),
        };
        Assert.All(refusals, refusal =>
        {
            Assert.Equal("octorate_state_invalid", refusal.Code);
            Assert.Equal(StatusCodes.Status400BadRequest, refusal.ResponseStatusCode);
        });

        // Its own org, user and redirect address: once.
        Assert.Equal(OctorateEnvironment.Test, await states.ConsumeAsync(state, OrgA, UserA, Redirect, CancellationToken.None));
        await Assert.ThrowsAsync<OctorateOAuthStateInvalidException>(() => states.ConsumeAsync(state, OrgA, UserA, Redirect, CancellationToken.None));
        Assert.Empty(await db.OctorateOAuthStates.AsNoTracking().ToListAsync());

        // Past its lifetime a state is refused even to its owner.
        var late = await states.CreateAsync(OrgA, UserA, Redirect, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(OctorateOptions.DefaultStateLifetimeMinutes) + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<OctorateOAuthStateInvalidException>(() => states.ConsumeAsync(late, OrgA, UserA, Redirect, CancellationToken.None));
    }

    [Fact]
    public async Task OctorateConnect_TokensNeverReturnedToClient_Test()
    {
        var accessToken = "AT-" + Guid.NewGuid().ToString("N");
        var refreshToken = "RT-" + Guid.NewGuid().ToString("N");
        var code = "CODE-" + Guid.NewGuid().ToString("N");
        var logs = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        var tenant = new RequestTenant(OrgA);
        var host = Host("Staging");
        var handler = new OctorateStub(accessToken, refreshToken, code);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect",
                ["Octorate:ClientId"] = "casazen-test-client",
                ["Octorate:ClientSecret"] = "casazen-test-secret-" + Guid.NewGuid().ToString("N"),
            })
            .Build();
        var options = Microsoft.Extensions.Options.Options.Create(new OctorateOptions
        {
            ClientId = configuration["Octorate:ClientId"],
            ClientSecret = configuration["Octorate:ClientSecret"],
            ApiBaseUrl = configuration["Octorate:ApiBaseUrl"],
            AuthorizationEndpoint = "https://admin.octorate.test/octobook/identity/oauth.xhtml",
            RedirectUri = Redirect,
        });

        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"octorate-connect-{Guid.NewGuid()}").Options,
            tenant,
            Provider);
        var factory = new StubHttpClientFactory(handler);
        var tokens = new OctorateTokenService(db, tenant, factory, configuration, host, clock, loggers.CreateLogger<OctorateTokenService>());
        var scope = new OctorateOrgScope(tenant);
        var client = new OctorateApiClient(
            new HttpClient(handler), tokens, new OctorateRateLimiter(clock), scope, configuration, clock, loggers.CreateLogger<OctorateApiClient>());
        var connect = new OctorateConnectService(
            db,
            new OctorateOAuthStateService(db, options, host, clock, loggers.CreateLogger<OctorateOAuthStateService>()),
            tokens,
            client,
            scope,
            factory,
            options,
            host,
            clock,
            loggers.CreateLogger<OctorateConnectService>());
        var controller = new OctorateController(connect, new FixedOrg(OrgA), HostAuthorizationTestHarness.ScopeResolver())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = HostAuthorizationTestHarness.User(UserA, "PropertyManager") },
            },
        };

        // The consent: the state travels only inside the consent address, as OAuth requires.
        var start = Assert.IsType<OctorateConnectStartResponse>(Assert.IsType<OkObjectResult>((await controller.Start(CancellationToken.None)).Result).Value);
        var state = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["state"].ToString();
        Assert.False(string.IsNullOrEmpty(state));

        var complete = Assert.IsType<OkObjectResult>(
            (await controller.Complete(new OctorateConnectCompleteRequest { Code = code, State = state }, CancellationToken.None)).Result).Value!;
        var status = Assert.IsType<OkObjectResult>((await controller.GetConnection(CancellationToken.None)).Result).Value!;

        var completed = Assert.IsType<OctorateConnectCompleteResponse>(complete);
        Assert.Equal(OctorateConnectionStatus.Connected, completed.Status);
        Assert.Equal(2, completed.PropertyCount);
        Assert.Equal(OctorateConnectionStatus.Connected, Assert.IsType<OctorateConnectionResponse>(status).Status);
        Assert.True(handler.ExchangedTheCode);

        // Nothing the endpoints answered carries a token, the code or the state.
        foreach (var answer in new[] { JsonSerializer.Serialize(complete, complete.GetType()), JsonSerializer.Serialize(status, status.GetType()) })
        {
            foreach (var secret in new[] { accessToken, refreshToken, code, state })
                Assert.DoesNotContain(secret, answer, StringComparison.Ordinal);
        }

        // Nothing written to the log carries them either: not the messages, not their values, not an exception.
        Assert.NotEmpty(logs.Lines);
        Assert.Contains(logs.Lines, line => line.Contains("OctorateTokenAccessed", StringComparison.Ordinal));
        foreach (var line in logs.Lines)
        {
            foreach (var secret in new[] { accessToken, refreshToken, code, state })
                Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
        }
    }

    private static IHostEnvironment Host(string environmentName)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns(environmentName);
        return host.Object;
    }

    /// <summary>Octorate as the consent sees it: the code exchange, the configuration of the sandbox, two accommodations.</summary>
    private sealed class OctorateStub(string accessToken, string refreshToken, string code) : HttpMessageHandler
    {
        public bool ExchangedTheCode { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/rest/v1/identity/token", StringComparison.Ordinal))
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                ExchangedTheCode = form.Contains("code=" + code, StringComparison.Ordinal) && form.Contains("grant_type=code", StringComparison.Ordinal);
                return Json($$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","expires_in":86400,"token_type":"Bearer"}""");
            }

            if (path.EndsWith("/rest/v1/api/configuration", StringComparison.Ordinal))
                return Json("""{"environment":"SANDBOX","quotaLimit":180,"quotaStatus":120}""");

            if (path.EndsWith("/rest/v1/accommodation", StringComparison.Ordinal))
            {
                return Json(request.RequestUri.Query.Contains("page=0", StringComparison.Ordinal)
                    ? """[{"id":"585492","name":"Casa del Lago","city":"Como"},{"id":"638388","name":"Casa del Bosco","city":"Como"}]"""
                    : "[]");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedOrg(Guid orgId) : IOrgContextResolver
    {
        public Task<Guid?> GetOrProvisionOrgIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(orgId);
    }

    private sealed class RequestTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    /// <summary>Every log line written: the message, every structured value and the exception, as text.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Add(string line)
        {
            lock (_lines)
                _lines.Add(line);
        }

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = new StringBuilder().Append(eventId.Name).Append(' ').Append(formatter(state, exception));
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var (key, value) in values)
                        text.Append(' ').Append(key).Append('=').Append(value);
                }

                if (exception is not null)
                    text.Append(' ').Append(exception);

                provider.Add(text.ToString());
            }
        }
    }
}
