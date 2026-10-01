using System.Net;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Tests.Unit.Logging;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>The Vercel Domains API client (BK-17, A3-25): paths, auth, answer mapping, and that nothing secret is logged.</summary>
public class VercelDomainsClientTests
{
    private const string Token = "vcl_secret_token_value";
    private const string Base = "https://api.vercel.com";

    private readonly MockHttpMessageHandler _http = new();
    private readonly CapturingLogger<VercelDomainsClient> _logger = new();
    private readonly VercelDomainsOptions _options = new()
    {
        ApiToken = Token,
        ProjectId = "prj_abc123",
        TeamId = "team_xyz",
        TimeoutSeconds = 5,
    };

    [Fact]
    public async Task GetDomainAsync_Configured_CallsTheProjectDomainWithTheBearerTokenAndTeam()
    {
        var request = _http.Expect(HttpMethod.Get, $"{Base}/v9/projects/prj_abc123/domains/www.example.it")
            .WithQueryString("teamId", "team_xyz")
            .WithHeaders("Authorization", $"Bearer {Token}")
            .Respond("application/json", """{"name":"www.example.it","verified":true}""");

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.Equal(VercelCallStatus.Ok, result.Status);
        Assert.True(result.Value!.Verified);
        Assert.Equal("www.example.it", result.Value.Name);
        _http.VerifyNoOutstandingExpectation();
        Assert.Equal(1, _http.GetMatchCount(request));
    }

    [Fact]
    public async Task GetDomainAsync_WithoutTeam_SendsNoTeamId()
    {
        _options.TeamId = null;
        var request = _http.Expect(HttpMethod.Get, $"{Base}/v9/projects/prj_abc123/domains/www.example.it")
            .With(r => r.RequestUri!.Query.Length == 0)
            .Respond("application/json", """{"name":"www.example.it","verified":false}""");

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.Equal(VercelCallStatus.Ok, result.Status);
        Assert.False(result.Value!.Verified);
        Assert.Equal(1, _http.GetMatchCount(request));
    }

    [Fact]
    public async Task AddDomainAsync_PostsTheNameToTheProject()
    {
        var request = _http.Expect(HttpMethod.Post, $"{Base}/v10/projects/prj_abc123/domains")
            .WithQueryString("teamId", "team_xyz")
            .WithContent("""{"name":"www.example.it"}""")
            .Respond("application/json", """
                {"name":"www.example.it","verified":false,
                 "verification":[{"type":"TXT","domain":"_vercel.example.it","value":"vc-domain-verify=www.example.it,abc","reason":"pending_domain_verification"}]}
                """);

        var result = await CreateClient().AddDomainAsync("www.example.it");

        Assert.Equal(VercelCallStatus.Ok, result.Status);
        Assert.False(result.Value!.Verified);
        var challenge = Assert.Single(result.Value.Verification);
        Assert.Equal("TXT", challenge.Type);
        Assert.Equal("_vercel.example.it", challenge.Domain);
        Assert.Equal("vc-domain-verify=www.example.it,abc", challenge.Value);
        Assert.Equal("pending_domain_verification", challenge.Reason);
        Assert.Equal(1, _http.GetMatchCount(request));
    }

    [Fact]
    public async Task VerifyDomainAsync_PostsToTheVerifyPath()
    {
        var request = _http.Expect(HttpMethod.Post, $"{Base}/v9/projects/prj_abc123/domains/www.example.it/verify")
            .Respond("application/json", """{"name":"www.example.it","verified":true}""");

        var result = await CreateClient().VerifyDomainAsync("www.example.it");

        Assert.True(result.IsOk);
        Assert.True(result.Value!.Verified);
        Assert.Equal(1, _http.GetMatchCount(request));
    }

    [Fact]
    public async Task RemoveDomainAsync_SendsDelete()
    {
        var request = _http.Expect(HttpMethod.Delete, $"{Base}/v9/projects/prj_abc123/domains/www.example.it")
            .Respond(HttpStatusCode.OK, "application/json", "{}");

        var result = await CreateClient().RemoveDomainAsync("www.example.it");

        Assert.Equal(VercelCallStatus.Ok, result.Status);
        Assert.True(result.Value);
        Assert.Equal(1, _http.GetMatchCount(request));
    }

    [Fact]
    public async Task GetDomainAsync_AnswerWithoutVerifiedField_IsNotVerified()
    {
        // An answer that does not say "verified": true proves nothing: never a verified domain by omission.
        _http.When(HttpMethod.Get, $"{Base}/v9/*").Respond("application/json", """{"name":"www.example.it"}""");

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.True(result.IsOk);
        Assert.False(result.Value!.Verified);
    }

    [Fact]
    public async Task GetDomainAsync_VerifiedAsAString_IsNotVerified()
    {
        _http.When(HttpMethod.Get, $"{Base}/v9/*").Respond("application/json", """{"name":"x","verified":"true"}""");

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.False(result.Value!.Verified);
    }

    [Theory]
    [InlineData(401, VercelCallStatus.Unauthorized)]
    [InlineData(403, VercelCallStatus.Unauthorized)]
    [InlineData(404, VercelCallStatus.NotFound)]
    [InlineData(409, VercelCallStatus.Conflict)]
    [InlineData(400, VercelCallStatus.Rejected)]
    [InlineData(422, VercelCallStatus.Rejected)]
    [InlineData(429, VercelCallStatus.RateLimited)]
    [InlineData(500, VercelCallStatus.Unavailable)]
    [InlineData(503, VercelCallStatus.Unavailable)]
    public async Task GetDomainAsync_ErrorStatus_IsMappedToAStableStatus(int httpStatus, VercelCallStatus expected)
    {
        _http.When(HttpMethod.Get, $"{Base}/v9/*")
            .Respond((HttpStatusCode)httpStatus, "application/json", """{"error":{"code":"forbidden","message":"A secret detail"}}""");

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(httpStatus, result.HttpStatus);
        Assert.Equal("forbidden", result.ErrorCode);
    }

    [Fact]
    public async Task RemoveDomainAsync_NotFound_IsReportedAsNotFound()
    {
        _http.When(HttpMethod.Delete, $"{Base}/v9/*").Respond(HttpStatusCode.NotFound);

        var result = await CreateClient().RemoveDomainAsync("www.example.it");

        Assert.Equal(VercelCallStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task GetDomainAsync_ErrorCodeWithOddCharacters_IsSanitizedAndBounded()
    {
        var longCode = new string('a', 200);
        _http.When(HttpMethod.Get, $"{Base}/v9/*")
            .Respond(HttpStatusCode.BadRequest, "application/json", $$$"""{"error":{"code":"bad code!<script>{{{longCode}}}"}}""");

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.NotNull(result.ErrorCode);
        Assert.True(result.ErrorCode!.Length <= 64);
        Assert.DoesNotContain(' ', result.ErrorCode);
        Assert.DoesNotContain('<', result.ErrorCode);
    }

    [Fact]
    public async Task GetDomainAsync_ErrorBodyThatIsNotJson_HasNoErrorCode()
    {
        _http.When(HttpMethod.Get, $"{Base}/v9/*")
            .Respond(HttpStatusCode.BadGateway, "text/html", "<html>Bad gateway</html>");

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.Equal(VercelCallStatus.Unavailable, result.Status);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task GetDomainAsync_AnswerThatIsNotJson_IsUnavailableNotAnException()
    {
        _http.When(HttpMethod.Get, $"{Base}/v9/*").Respond("text/html", "<html>login</html>");

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.Equal(VercelCallStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task GetDomainAsync_ConnectionFails_IsUnavailable()
    {
        _http.When(HttpMethod.Get, $"{Base}/v9/*").Throw(new HttpRequestException("no route to host"));

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.Equal(VercelCallStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task GetDomainAsync_TimesOut_IsUnavailable()
    {
        _http.When(HttpMethod.Get, $"{Base}/v9/*").Throw(new TaskCanceledException("timeout"));

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.Equal(VercelCallStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task GetDomainAsync_CallerCancels_Propagates()
    {
        _http.When(HttpMethod.Get, $"{Base}/v9/*").Throw(new TaskCanceledException("cancelled"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient().GetDomainAsync("www.example.it", cts.Token));
    }

    [Theory]
    [InlineData(null, "prj_abc123")]
    [InlineData("", "prj_abc123")]
    [InlineData("  ", "prj_abc123")]
    [InlineData(Token, null)]
    [InlineData(Token, "")]
    public async Task AnyCall_NotConfigured_MakesNoRequest(string? token, string? project)
    {
        _options.ApiToken = token;
        _options.ProjectId = project;
        var anything = _http.When("*").Respond(HttpStatusCode.OK, "application/json", "{}");
        var client = CreateClient();

        Assert.False(client.IsConfigured);
        Assert.Equal(VercelCallStatus.NotConfigured, (await client.GetDomainAsync("www.example.it")).Status);
        Assert.Equal(VercelCallStatus.NotConfigured, (await client.AddDomainAsync("www.example.it")).Status);
        Assert.Equal(VercelCallStatus.NotConfigured, (await client.VerifyDomainAsync("www.example.it")).Status);
        Assert.Equal(VercelCallStatus.NotConfigured, (await client.RemoveDomainAsync("www.example.it")).Status);
        Assert.Equal(0, _http.GetMatchCount(anything));
    }

    [Fact]
    public async Task AnyCall_BaseUrlIsNotHttps_IsDisabledAndTheTokenIsNeverSent()
    {
        _options.ApiBaseUrl = "http://api.vercel.com";
        var anything = _http.When("*").Respond(HttpStatusCode.OK, "application/json", "{}");

        var result = await CreateClient().GetDomainAsync("www.example.it");

        Assert.Equal(VercelCallStatus.NotConfigured, result.Status);
        Assert.Equal(0, _http.GetMatchCount(anything));
    }

    [Fact]
    public async Task AnyCall_Failing_NeverLogsTheTokenOrTheProviderMessage()
    {
        _http.When(HttpMethod.Get, $"{Base}/v9/*")
            .Respond(HttpStatusCode.Forbidden, "application/json", $$$"""{"error":{"code":"forbidden","message":"token {{{Token}}} is not allowed"}}""");
        _http.When(HttpMethod.Post, $"{Base}/v10/*").Throw(new HttpRequestException("no route to host"));
        var client = CreateClient();

        await client.GetDomainAsync("www.example.it");
        await client.AddDomainAsync("www.example.it");

        Assert.NotEmpty(_logger.Entries);
        Assert.DoesNotContain(Token, _logger.AllOutput);
        Assert.DoesNotContain("is not allowed", _logger.AllOutput);
    }

    private VercelDomainsClient CreateClient() =>
        new(new HttpClient(_http), Options.Create(_options), _logger);
}
