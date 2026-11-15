using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.External.Sdi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.External;

/// <summary>
/// E17/F01/US02/T02 (NF-116, ⚖ T1): the adapter of the SDI channel, against a fake HTTP handler (the real service is never called, from a test or from the CI). With the key the adapter enables the
/// issuer, submits the file once and reads the answers of the SDI, from the webhook and from the poll; without it nothing changes; and nothing of an invoice ever reaches a log.
/// </summary>
public class RestSdiEInvoiceProviderTests
{
    private const string Key = "ik_test_SECRETKEY";
    private const string Marker = "SEGRETO-CONTENUTO-XML";
    private const string IssuerName = "TEST Emittente Riservato Srl";
    private const string FiscalId = "01234567897";

    // ---------------------------------------------------------------- the configuration

    [Theory]
    [InlineData("invoicetronic", Key, null, true)]
    [InlineData("Invoicetronic", Key, "https://api.invoicetronic.com/v1", true)]
    [InlineData("invoicetronic", Key, "http://localhost:5099/v1/", true)]
    [InlineData("invoicetronic", Key, "http://api.invoicetronic.com/v1/", false)]
    [InlineData("invoicetronic", Key, "not an address", false)]
    [InlineData("invoicetronic", null, null, false)]
    [InlineData("invoicetronic", "  ", null, false)]
    [InlineData("another-provider", Key, null, false)]
    [InlineData(null, Key, null, false)]
    public void Options_AreConfiguredOnlyWithAnAdapterThatExistsAKeyAndASafeAddress(string? name, string? apiKey, string? baseUrl, bool configured)
    {
        var options = new SdiProviderOptions { Name = name, ApiKey = apiKey, BaseUrl = baseUrl };

        Assert.Equal(configured, options.IsConfigured);
        Assert.Equal(configured, new RestSdiEInvoiceProvider(new HttpClient(new Handler()), Options.Create(options), NullLogger<RestSdiEInvoiceProvider>.Instance).IsConfigured);
    }

    [Fact]
    public void Options_TheWebhookSecretIsUsableOnlyWhenItIsLongEnough()
    {
        Assert.False(new SdiProviderOptions { WebhookSecret = new string('a', 31) }.HasUsableWebhookSecret);
        Assert.True(new SdiProviderOptions { WebhookSecret = new string('a', 32) }.HasUsableWebhookSecret);
        Assert.False(new SdiProviderOptions().HasUsableWebhookSecret);
    }

    [Fact]
    public async Task APlatformInvoice_IsNotSubmittedThroughTheChannel_AndNoCallIsMade()
    {
        var handler = new Handler();

        var answer = await Adapter(handler).SubmitAsync(new PlatformInvoice(), new OrgEntity());

        Assert.Equal((false, SdiSubmissionErrors.NotSupported), (answer.Accepted, answer.Error));
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------- the submission

    [Fact]
    public async Task Submit_TheIssuerIsKnown_SendsTheFileOnce_WithTheKeyOfTheAccountAndTheIdempotencyKeyOfTheDocument()
    {
        var handler = new Handler(
            _ => Json(HttpStatusCode.OK, $$"""[{"id":7,"vat":"{{FiscalId}}","name":"x"}]"""),
            _ => Json(HttpStatusCode.Created, """{"id":42}"""));
        var submission = Submission();

        var answer = await Adapter(handler).SubmitDocumentAsync(submission);

        Assert.Equal((true, "42", (string?)null), (answer.Accepted, answer.TransmissionId, answer.Error));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(("GET", $"company?q={FiscalId}&page_size=20"), (handler.Requests[0].Method, handler.Requests[0].Target));

        var send = handler.Requests[1];
        Assert.Equal(("POST", "send?validate=true"), (send.Method, send.Target));
        Assert.Equal(submission.DocumentId.ToString("N"), send.Header("Idempotency-Key"));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Key + ":")), send.Header("Authorization"));
        using var body = JsonDocument.Parse(send.Body);
        Assert.Equal("Base64", body.RootElement.GetProperty("encoding").GetString());
        Assert.Equal(submission.Xml, Encoding.UTF8.GetString(Convert.FromBase64String(body.RootElement.GetProperty("payload").GetString()!)));
        Assert.Equal(submission.DocumentId.ToString("N"), body.RootElement.GetProperty("meta_data").GetProperty("casazen_document").GetString());
    }

    [Fact]
    public async Task Submit_TheIssuerIsNew_IsEnabledAtTheProviderFirst()
    {
        var handler = new Handler(
            _ => Json(HttpStatusCode.OK, "[]"),
            _ => Json(HttpStatusCode.Created, """{"id":3}"""),
            _ => Json(HttpStatusCode.Created, """{"id":42}"""));

        var answer = await Adapter(handler).SubmitDocumentAsync(Submission());

        Assert.True(answer.Accepted);
        Assert.Equal(["GET company?q=" + FiscalId + "&page_size=20", "POST company", "POST send?validate=true"], handler.Requests.Select(r => $"{r.Method} {r.Target}").ToList());
        using var company = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal((IssuerName, FiscalId, JsonValueKind.Null), (company.RootElement.GetProperty("name").GetString(), company.RootElement.GetProperty("vat").GetString(), company.RootElement.GetProperty("fiscal_code").ValueKind));
    }

    [Fact]
    public async Task Submit_AnIssuerWithAFiscalCode_IsEnabledByItsFiscalCode()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, "[]"), _ => Json(HttpStatusCode.Created, """{"id":3}"""), _ => Json(HttpStatusCode.Created, """{"id":42}"""));

        await Adapter(handler).SubmitDocumentAsync(Submission() with { IssuerFiscalId = "RSSMRA80A01H501U" });

        using var company = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal((JsonValueKind.Null, "RSSMRA80A01H501U"), (company.RootElement.GetProperty("vat").ValueKind, company.RootElement.GetProperty("fiscal_code").GetString()));
    }

    [Fact]
    public async Task Submit_TheVatNumberIsRegisteredOnAnotherAccount_IsPending_AndNothingIsSent()
    {
        var handler = new Handler(
            _ => Json(HttpStatusCode.OK, "[]"),
            _ => Json(HttpStatusCode.BadRequest, """{"code":"company_already_registered","description":"x"}"""));

        var answer = await Adapter(handler).SubmitDocumentAsync(Submission());

        Assert.Equal((false, SdiSubmissionErrors.IssuerPending, "company_already_registered"), (answer.Accepted, answer.Error, answer.Detail));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, SdiSubmissionErrors.ProviderRefused, """{"code":"invalid_xml"}""", "400 invalid_xml")]
    [InlineData(HttpStatusCode.UnprocessableEntity, SdiSubmissionErrors.ProviderRefused, """{"code":"validation_failed"}""", "422 validation_failed")]
    [InlineData(HttpStatusCode.Unauthorized, SdiSubmissionErrors.ProviderError, "", "401")]
    [InlineData(HttpStatusCode.TooManyRequests, SdiSubmissionErrors.ProviderError, "<html>slow down</html>", "429")]
    [InlineData(HttpStatusCode.InternalServerError, SdiSubmissionErrors.ProviderError, """{"code":"not a plain identifier!"}""", "500")]
    public async Task Submit_ARefusalOrAFailureOfTheProvider_IsACodeOfTheChannel_NeverAnExceptionOrTheBody(HttpStatusCode status, string error, string responseBody, string detail)
    {
        var handler = new Handler(
            _ => Json(HttpStatusCode.OK, $$"""[{"id":7,"vat":"{{FiscalId}}"}]"""),
            _ => Json(status, responseBody));

        var answer = await Adapter(handler).SubmitDocumentAsync(Submission());

        Assert.Equal((false, (string?)null, error, detail), (answer.Accepted, answer.TransmissionId, answer.Error, answer.Detail));
    }

    [Fact]
    public async Task Submit_AnAnswerWithoutTheIdOfTheSubmission_IsNoAcceptance()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, "[{\"id\":7,\"vat\":\"" + FiscalId + "\"}]"), _ => Json(HttpStatusCode.Created, "{}"));

        var answer = await Adapter(handler).SubmitDocumentAsync(Submission());

        Assert.Equal((false, SdiSubmissionErrors.ProviderError), (answer.Accepted, answer.Error));
    }

    [Fact]
    public async Task Submit_TheProviderCannotBeReached_IsAProviderErrorOfTheChannel_ButACancelledCallIsNotSwallowed()
    {
        var down = await Adapter(new Handler(_ => throw new HttpRequestException("no route"))).SubmitDocumentAsync(Submission());
        Assert.Equal((false, SdiSubmissionErrors.ProviderError, "unreachable"), (down.Accepted, down.Error, down.Detail));

        var timeout = await Adapter(new Handler(_ => throw new TaskCanceledException("timeout"))).SubmitDocumentAsync(Submission());
        Assert.Equal((false, SdiSubmissionErrors.ProviderError, "timeout"), (timeout.Accepted, timeout.Error, timeout.Detail));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Adapter(new Handler(_ => Json(HttpStatusCode.OK, "[]"))).SubmitDocumentAsync(Submission(), cancelled.Token));
    }

    // ---------------------------------------------------------------- what the SDI said

    [Fact]
    public async Task Status_TheLatestUpdateDecides_AndNoUpdateIsStillSubmitted()
    {
        Assert.Equal(SdiOutcomeKind.Submitted, (await Adapter(new Handler(_ => Json(HttpStatusCode.OK, "[]"))).GetStatusAsync("42")).Status);

        var handler = new Handler(_ => Json(
            HttpStatusCode.OK,
            """
            [
              {"id":2,"send_id":42,"state":"Consegnato","last_update":"2026-11-20T10:30:00Z"},
              {"id":1,"send_id":42,"state":"Inviato","last_update":"2026-11-20T10:00:00Z"}
            ]
            """));
        var delivered = await Adapter(handler).GetStatusAsync("42");

        Assert.Equal(SdiOutcomeKind.Delivered, delivered.Status);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("update?send_id=42&page_size=100", handler.Requests[0].Target);
    }

    [Theory]
    [InlineData("Consegnato", SdiOutcomeKind.Delivered)]
    [InlineData("NonConsegnato", SdiOutcomeKind.Delivered)]
    [InlineData("ImpossibilitaDiRecapito", SdiOutcomeKind.Delivered)]
    [InlineData("AttestazioneTrasmissioneFattura", SdiOutcomeKind.Delivered)]
    [InlineData("AccettatoDalDestinatario", SdiOutcomeKind.Delivered)]
    [InlineData("DecorrenzaTermini", SdiOutcomeKind.Delivered)]
    [InlineData("Scartato", SdiOutcomeKind.Rejected)]
    [InlineData("Inviato", SdiOutcomeKind.Submitted)]
    [InlineData("RifiutatoDalDestinatario", SdiOutcomeKind.Submitted)]
    [InlineData("SomethingNew", SdiOutcomeKind.Submitted)]
    public async Task Status_TheStatesOfTheProvider_AreTheThreeOutcomesOfTheChannel(string state, SdiOutcomeKind expected)
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, $$"""[{"id":1,"send_id":42,"state":"{{state}}","last_update":"2026-11-20T10:00:00Z"}]"""));

        Assert.Equal(expected, (await Adapter(handler).GetStatusAsync("42")).Status);
    }

    [Fact]
    public async Task Status_ARejection_CarriesTheCodesAndTheCleanedWordsOfTheSdi()
    {
        var handler = new Handler(_ => Json(
            HttpStatusCode.OK,
            """
            [{"id":1,"send_id":42,"state":"Scartato","last_update":"2026-11-20T10:00:00Z",
              "errors":[{"code":"00417","description":"Identificativo fiscale mancante per mario.rossi@example.com"},{"code":"00300","description":"Codice non valido"}]}]
            """));

        var status = await Adapter(handler).GetStatusAsync("42");

        Assert.Equal((SdiOutcomeKind.Rejected, "00417", "00417 Identificativo fiscale mancante per ***; 00300 Codice non valido"), (status.Status, status.ErrorCode, status.ErrorDetail));
    }

    [Fact]
    public async Task Status_AReadThatFails_IsThrown_NeverTurnedIntoNothingHappened()
    {
        var error = await Assert.ThrowsAsync<SdiProviderException>(() => Adapter(new Handler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"))).GetStatusAsync("42"));
        Assert.Equal(("update", 503), (error.Operation, error.StatusCode));

        await Assert.ThrowsAsync<HttpRequestException>(() => Adapter(new Handler(_ => throw new HttpRequestException("down"))).GetStatusAsync("42"));
    }

    // ---------------------------------------------------------------- the webhook

    [Fact]
    public async Task Notification_TheBodyOnlyNamesTheUpdate_TheUpdateIsReadAgainFromTheApi()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, """{"id":9,"send_id":42,"state":"Scartato","last_update":"2026-11-20T10:00:00Z","errors":[{"code":"00311","description":"Codice destinatario non valido"}]}"""));
        const string payload = """{"Id":5,"Endpoint":"/v1/update","Method":"POST","ResourceId":9,"Success":true,"StatusCode":201}""";

        var notifications = await Adapter(handler).ReadNotificationAsync(payload);

        var notification = Assert.Single(notifications);
        Assert.Equal(("42", SdiOutcomeKind.Rejected, "00311"), (notification.TransmissionId, notification.Outcome.Status, notification.Outcome.ErrorCode));
        Assert.Equal("update/9", Assert.Single(handler.Requests).Target);
    }

    [Theory]
    [InlineData("""{"Id":5,"Endpoint":"/v1/send","Method":"POST","ResourceId":9,"Success":true}""")]
    [InlineData("""{"Id":5,"Endpoint":"/v1/update","Method":"DELETE","ResourceId":9,"Success":true}""")]
    [InlineData("""{"Id":5,"Endpoint":"/v1/update","Method":"POST","ResourceId":9,"Success":false}""")]
    [InlineData("""{"Id":5,"Endpoint":"/v1/update","Method":"POST","Success":true}""")]
    [InlineData("""["not","an","event"]""")]
    [InlineData("this is not json")]
    [InlineData("")]
    public async Task Notification_AnEventThatIsNotAboutAnUpdate_IsNothingToRecord_AndNoCallIsMade(string payload)
    {
        var handler = new Handler();

        Assert.Empty(await Adapter(handler).ReadNotificationAsync(payload));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Notification_AnUpdateThatCannotBeRead_IsThrown_SoTheProviderCallsAgain()
    {
        const string payload = """{"Endpoint":"/v1/update","Method":"POST","ResourceId":9,"Success":true}""";

        await Assert.ThrowsAsync<SdiProviderException>(() => Adapter(new Handler(_ => Json(HttpStatusCode.NotFound, "{}"))).ReadNotificationAsync(payload));
    }

    // ---------------------------------------------------------------- privacy

    /// <summary>
    /// PROOF TEST: privacy rule. Nothing of an invoice reaches a log: neither the file (as it is nor in base 64), the fiscal id or the name of the issuer, the key of the account, nor the body of an answer
    /// of the provider (a refusal that echoes the file, a rejection with an address in its words); a call is logged by its operation and the status of the answer.
    /// </summary>
    [Fact]
    public async Task SdiAdapter_NeverLogsInvoicePayload_Test()
    {
        var logger = new CapturingLogger<RestSdiEInvoiceProvider>();
        var submission = Submission();
        var echo = $$"""{"code":"invalid_xml","description":"{{Marker}} {{IssuerName}} {{FiscalId}}","hint":"{{Key}}"}""";
        var handler = new Handler(
            // an accepted submission of a known issuer, an enabling of a new one, a refusal that echoes the file, a failure, and the reads of the SDI's answers
            _ => Json(HttpStatusCode.OK, $$"""[{"id":7,"vat":"{{FiscalId}}"}]"""),
            _ => Json(HttpStatusCode.Created, """{"id":42}"""),
            _ => Json(HttpStatusCode.OK, "[]"),
            _ => Json(HttpStatusCode.Created, """{"id":3}"""),
            _ => Json(HttpStatusCode.UnprocessableEntity, echo),
            _ => Json(HttpStatusCode.OK, $$"""[{"id":7,"vat":"{{FiscalId}}"}]"""),
            _ => Json(HttpStatusCode.InternalServerError, echo),
            _ => Json(HttpStatusCode.OK, $$"""[{"id":1,"send_id":42,"state":"Scartato","last_update":"2026-11-20T10:00:00Z","errors":[{"code":"00417","description":"{{Marker}} {{IssuerName}}"}]}]"""),
            _ => Json(HttpStatusCode.ServiceUnavailable, echo),
            _ => Json(HttpStatusCode.OK, $$"""{"id":9,"send_id":42,"state":"Consegnato","last_update":"2026-11-20T10:00:00Z"}"""));
        var adapter = Adapter(handler, logger);

        await adapter.SubmitDocumentAsync(submission);
        await adapter.SubmitDocumentAsync(submission with { IssuerFiscalId = "09876543217" });
        await adapter.SubmitDocumentAsync(submission);
        await adapter.GetStatusAsync("42");
        await Assert.ThrowsAsync<SdiProviderException>(() => adapter.GetStatusAsync("42"));
        await adapter.ReadNotificationAsync("""{"Endpoint":"/v1/update","Method":"POST","ResourceId":9,"Success":true}""");
        await adapter.SubmitAsync(new PlatformInvoice(), new OrgEntity());

        Assert.NotEmpty(logger.Lines);
        var forbidden = new[]
        {
            Marker, IssuerName, FiscalId, "09876543217", Key, submission.Xml, Convert.ToBase64String(Encoding.UTF8.GetBytes(submission.Xml)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Key + ":")), "mario.rossi",
        };
        Assert.All(logger.Lines, line => Assert.All(forbidden, secret => Assert.DoesNotContain(secret, line, StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- helpers

    private static SdiDocumentSubmission Submission() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), FiscalId, IssuerName, "IT09876543217_AB123.xml", $"<p:FatturaElettronica>{Marker}</p:FatturaElettronica>");

    private static RestSdiEInvoiceProvider Adapter(Handler handler, ILogger<RestSdiEInvoiceProvider>? logger = null) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://sdi.test/v1/") },
            Options.Create(new SdiProviderOptions { Name = "invoicetronic", ApiKey = Key }),
            logger ?? NullLogger<RestSdiEInvoiceProvider>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>One request the adapter made: what the fake handler saw.</summary>
    private sealed record Seen(string Method, string Target, string Body, IReadOnlyDictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>The fake service: answers in order, and remembers what it was asked. With no answer left it fails the test: a call nobody expected.</summary>
    private sealed class Handler(params Func<HttpRequestMessage, HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _answers = new(answers);

        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new Seen(request.Method.Method, request.RequestUri!.PathAndQuery.TrimStart('/').Replace("v1/", string.Empty, StringComparison.Ordinal), body, headers));
            Assert.True(_answers.Count > 0, "The adapter made a call nobody expected.");
            return _answers.Dequeue()(request);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
