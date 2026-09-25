using System.Globalization;
using System.Text.Json;
using Casazen.Core.Exceptions;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Middleware;

public class ErrorHandlingMiddlewareTests
{
    private const string ItalianGenericError = "Si è verificato un errore imprevisto. Riprova più tardi.";

    [Fact]
    public async Task InvokeAsync_InvalidOperationException_Returns500WithGenericDetailAndNoInternalMessage()
    {
        var response = await InvokeAsync(new InvalidOperationException("The LINQ expression 'DbSet<Guest>' could not be translated"));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.Status);
        Assert.Equal("internal_error", response.Code);
        Assert.Equal(ItalianGenericError, response.Detail);
        Assert.DoesNotContain("LINQ", response.Body);
        Assert.False(string.IsNullOrWhiteSpace(response.TraceId));
        Assert.Equal("application/problem+json", response.ContentType);
    }

    [Fact]
    public async Task InvokeAsync_UnhandledExceptionWithInnerUnauthorized_DoesNotUnwrapToInnerException()
    {
        var response = await InvokeAsync(
            new InvalidOperationException("outer", new UnauthorizedAccessException("inner")));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.Status);
        Assert.Equal("internal_error", response.Code);
    }

    [Fact]
    public async Task InvokeAsync_UnauthorizedAccessException_Returns403Forbidden()
    {
        var response = await InvokeAsync(new UnauthorizedAccessException("Lease does not belong to this owner."));

        Assert.Equal(StatusCodes.Status403Forbidden, response.Status);
        Assert.Equal("forbidden", response.Code);
        Assert.Equal("Non si dispone dei permessi per accedere a questa risorsa.", response.Detail);
        Assert.DoesNotContain("owner", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_NotFoundExceptionWithCode_Returns404WithCodeAndLocalizedMessage()
    {
        var guestId = Guid.NewGuid();
        var response = await InvokeAsync(
            new NotFoundException($"Guest {guestId} not found") { Code = "guest_not_found", MessageKey = "GuestNotFound" });

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
        Assert.Equal("guest_not_found", response.Code);
        Assert.Equal("Ospite non trovato", response.Detail);
        Assert.DoesNotContain(guestId.ToString(), response.Body);
    }

    [Fact]
    public async Task InvokeAsync_NotFoundExceptionWithoutCode_Returns404GenericNotFound()
    {
        var response = await InvokeAsync(new NotFoundException("Lease 42 not found."));

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
        Assert.Equal("not_found", response.Code);
        Assert.Equal("La risorsa richiesta non esiste o non è accessibile.", response.Detail);
    }

    [Fact]
    public async Task InvokeAsync_DomainConflictException_Returns409WithCodeAndLocalizedMessage()
    {
        var response = await InvokeAsync(new DomainConflictException("duplicate_property_slug", "PropertySlugTaken"));

        Assert.Equal(StatusCodes.Status409Conflict, response.Status);
        Assert.Equal("duplicate_property_slug", response.Code);
        Assert.Equal(
            "Questo indirizzo web (slug) è già usato da un altro immobile della tua organizzazione.",
            response.Detail);
    }

    [Fact]
    public async Task InvokeAsync_DomainRuleException_Returns422WithFormattedMessage()
    {
        var response = await InvokeAsync(new DomainRuleException("activation_incomplete", "ActivationIncomplete", "CIN"));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Status);
        Assert.Equal("activation_incomplete", response.Code);
        Assert.Equal("Attivazione non completata: CIN", response.Detail);
    }

    [Fact]
    public async Task InvokeAsync_DomainExceptionWithUnknownMessageKey_ReturnsGenericDetailNotRawKey()
    {
        var response = await InvokeAsync(new DomainRuleException("some_rule", "KeyMissingFromResources"));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Status);
        Assert.Equal("some_rule", response.Code);
        Assert.Equal("L'operazione non è consentita nello stato attuale.", response.Detail);
        Assert.DoesNotContain("KeyMissingFromResources", response.Body);
    }

    [Fact]
    public async Task InvokeAsync_EnglishUiCulture_ReturnsEnglishDetail()
    {
        var response = await InvokeAsync(
            new DomainConflictException("booking_dates_unavailable", "BookingDatesUnavailable"),
            culture: "en");

        Assert.Equal("The property is not available for the selected dates.", response.Detail);
    }

    [Fact]
    public async Task InvokeAsync_StripeException_Returns503WithoutProviderMessage()
    {
        var response = await InvokeAsync(new StripeException("Invalid API Key provided: sk_test_****1234"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.Status);
        Assert.Equal("payment_provider_error", response.Code);
        Assert.DoesNotContain("sk_test", response.Body);
    }

    [Fact]
    public async Task InvokeAsync_PaymentProcessingException_Returns503WithoutInternalMessage()
    {
        var response = await InvokeAsync(
            new PaymentProcessingException("Stripe is not configured on the API server. Set Stripe__SecretKey in Railway."));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.Status);
        Assert.Equal("payment_provider_error", response.Code);
        Assert.DoesNotContain("Railway", response.Body);
    }

    [Fact]
    public async Task InvokeAsync_CoreValidationException_Returns422WithFieldErrors()
    {
        var response = await InvokeAsync(new Casazen.Core.Exceptions.ValidationException("cin", "CIN non valido"));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Status);
        Assert.Equal("validation_error", response.Code);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal("CIN non valido", json.RootElement.GetProperty("errors").GetProperty("cin")[0].GetString());
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized, "unauthorized")]
    [InlineData(StatusCodes.Status403Forbidden, "forbidden")]
    [InlineData(StatusCodes.Status429TooManyRequests, "too_many_requests")]
    public async Task InvokeAsync_EmptyErrorResponse_WritesProblemWithStatusCode(int statusCode, string expectedCode)
    {
        var context = CreateContext();
        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });

        await WithCultureAsync("it-IT", () => middleware.InvokeAsync(context));
        var response = await ReadAsync(context);

        Assert.Equal(statusCode, response.Status);
        Assert.Equal(expectedCode, response.Code);
        Assert.False(string.IsNullOrWhiteSpace(response.Detail));
        Assert.False(response.Detail!.EndsWith("Detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_EmptyUnauthorizedResponse_ReturnsLocalizedTextNotResourceKey()
    {
        var context = CreateContext();
        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        });

        await WithCultureAsync("it-IT", () => middleware.InvokeAsync(context));
        var response = await ReadAsync(context);

        Assert.NotEqual("UnauthorizedDetail", response.Detail);
        Assert.StartsWith("È richiesta l'autenticazione", response.Detail);
    }

    [Fact]
    public async Task InvokeAsync_RequestAbortedByClient_DoesNotWriteProblem()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var context = CreateContext();
        context.RequestAborted = aborted.Token;
        var middleware = CreateMiddleware(_ => throw new OperationCanceledException(aborted.Token));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task InvokeAsync_UnhandledException_LogsRouteTemplateAndTraceIdButNotConcretePath()
    {
        var logger = new RecordingLogger();
        var context = CreateContext("/api/guests/email/mario.rossi@example.com");
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("api/guests/email/{email}"),
            0,
            EndpointMetadataCollection.Empty,
            "test"));
        var middleware = new ErrorHandlingMiddleware(_ => throw new InvalidOperationException("boom"), logger);

        await WithCultureAsync("it-IT", () => middleware.InvokeAsync(context));
        var response = await ReadAsync(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("api/guests/email/{email}", entry.Message);
        Assert.Contains(response.TraceId!, entry.Message);
        Assert.DoesNotContain("mario.rossi@example.com", entry.Message);
    }

    [Fact]
    public async Task InvokeAsync_DomainException_LogsCodeAsInformationWithoutStackTrace()
    {
        var logger = new RecordingLogger();
        var context = CreateContext();
        var middleware = new ErrorHandlingMiddleware(
            _ => throw new DomainConflictException("duplicate_property_slug", "PropertySlugTaken"),
            logger);

        await WithCultureAsync("it-IT", () => middleware.InvokeAsync(context));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("duplicate_property_slug", entry.Message);
    }

    private static async Task<ProblemResponse> InvokeAsync(Exception exception, string culture = "it-IT")
    {
        var context = CreateContext();
        var middleware = CreateMiddleware(_ => throw exception);

        await WithCultureAsync(culture, () => middleware.InvokeAsync(context));
        return await ReadAsync(context);
    }

    private static ErrorHandlingMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next, new RecordingLogger());

    private static DefaultHttpContext CreateContext(string path = "/api/test")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task WithCultureAsync(string culture, Func<Task> action)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            await action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static async Task<ProblemResponse> ReadAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new ProblemResponse(
            context.Response.StatusCode,
            context.Response.ContentType,
            root.TryGetProperty("code", out var code) ? code.GetString() : null,
            root.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
            root.TryGetProperty("traceId", out var traceId) ? traceId.GetString() : null,
            body);
    }

    private sealed record ProblemResponse(
        int Status,
        string? ContentType,
        string? Code,
        string? Detail,
        string? TraceId,
        string Body);

    private sealed class RecordingLogger : ILogger<ErrorHandlingMiddleware>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
