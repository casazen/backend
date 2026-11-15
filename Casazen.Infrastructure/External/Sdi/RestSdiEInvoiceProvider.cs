using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External.Sdi;

/// <summary>
/// The adapter of the SDI channel for an intermediary with a public REST API, a sandbox and the transmission on behalf of many VAT numbers (E17/F01/US02/T02, NF-116, ⚖ T1): the API v1 of
/// Invoicetronic, read on its official documentation (<c>docs/runbooks/fiscal-documents.md</c> says which pages, which version and when). Everything provider-specific is here: another
/// provider is another adapter, chosen by <see cref="SdiProviderOptions.Name"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Submission.</b> The issuer is enabled first: its VAT number (or fiscal code) must be a <i>company</i> of the account, which the adapter looks for and, when it is not there, creates; a number
/// registered on another account cannot be enabled here (the provider answers <c>company_already_registered</c>): the submission is refused with <see cref="SdiSubmissionErrors.IssuerPending"/>, the
/// state <c>sdi_issuer_registration_pending</c>. Then the file goes in one call (<c>POST send</c>, validated by the provider before it is sent, with the id of the document as the idempotency key, so a
/// second submission of one document never sends it twice). The provider names the file and signs it itself.</item>
/// <item><b>Notifications.</b> The webhook body only says that an update exists: the adapter re-reads the update from the API (<c>GET update/{id}</c>) and never trusts the body; the poll reads the
/// updates of a submission (<c>GET update?send_id=</c>). «Delivered» is the receipt of delivery, the notification of non-delivery and the attestation of transmission (the invoice is issued and available to
/// its recipient: ⚖ to be confirmed by the accountant), and the decorrenza dei termini; «rejected» is the notification of rejection (<c>Scartato</c>), with the codes of the SDI. A state a private recipient never
/// gets (the outcome of a public body) leaves the document «submitted».</item>
/// <item><b>Privacy.</b> The file, the fiscal ids, the names and the key never reach a log: a call is logged by its operation and the status of the answer. A refusal is turned into a short code
/// (<see cref="SdiSubmissionErrors"/>) and a cleaned note (the provider's own error code); a failure of the transport is the code <c>provider_error</c>, never an exception of a submission.</item>
/// <item>Without the SaaS invoices: <see cref="SubmitAsync"/> answers <c>not_supported</c> (nothing builds the e-invoice of a platform invoice yet).</item>
/// </list>
/// </remarks>
public sealed partial class RestSdiEInvoiceProvider(
    HttpClient http,
    IOptions<SdiProviderOptions> options,
    ILogger<RestSdiEInvoiceProvider> logger) : ISdiEInvoiceProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The longest provider error code kept in a note.</summary>
    private const int MaxCodeLength = 60;

    public bool IsConfigured => options.Value.IsConfigured;

    public string Name => options.Value.AdapterName ?? "none";

    public Task<SdiSubmissionResult> SubmitAsync(PlatformInvoice invoice, Org org, CancellationToken cancellationToken = default)
    {
        // Nothing builds the FatturaPA of a SaaS invoice of the platform (its seller, its lines): the platform keeps issuing them by hand, as the admin queue says.
        logger.LogInformation("SDI channel {Provider}: a platform invoice is not submitted through it", Name);
        return Task.FromResult(SdiSubmissionResult.Failure(SdiSubmissionErrors.NotSupported, "platform_invoice"));
    }

    public async Task<SdiSubmissionResult> SubmitDocumentAsync(SdiDocumentSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        try
        {
            if (await EnsureIssuerAsync(submission, cancellationToken) is { } refused)
                return refused;

            var body = new Dictionary<string, object?>
            {
                ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(submission.Xml)),
                ["encoding"] = "Base64",
                ["meta_data"] = new Dictionary<string, string> { ["casazen_document"] = submission.DocumentId.ToString("N") },
            };
            using var response = await SendAsync(HttpMethod.Post, "send?validate=true", body, submission.DocumentId.ToString("N"), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var created = await ReadAsync(response, cancellationToken);
                var id = created is null ? null : IdOf(created.RootElement);
                if (id is null)
                    return Refused("send", response.StatusCode, SdiSubmissionErrors.ProviderError, "no_id");

                logger.LogInformation("SDI channel {Provider}: document {DocumentId} accepted", Name, submission.DocumentId);
                return SdiSubmissionResult.Success(id);
            }

            return await RefusalAsync("send", response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("SDI channel {Provider}: the provider could not be reached for document {DocumentId} ({Error})", Name, submission.DocumentId, ex.GetType().Name);
            return SdiSubmissionResult.Failure(SdiSubmissionErrors.ProviderError, "unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("SDI channel {Provider}: the provider did not answer in time for document {DocumentId}", Name, submission.DocumentId);
            return SdiSubmissionResult.Failure(SdiSubmissionErrors.ProviderError, "timeout");
        }
    }

    public async Task<SdiStatusResult> GetStatusAsync(string transmissionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transmissionId);

        using var response = await SendAsync(HttpMethod.Get, $"update?send_id={Uri.EscapeDataString(transmissionId.Trim())}&page_size=100", null, null, cancellationToken);
        using var updates = await ReadOrThrowAsync("update", response, cancellationToken);
        if (updates.RootElement.ValueKind != JsonValueKind.Array)
            throw new SdiProviderException("update", (int)response.StatusCode);

        var latest = updates.RootElement.EnumerateArray()
            .Where(update => update.ValueKind == JsonValueKind.Object)
            .OrderBy(update => DateOf(update))
            .ThenBy(update => IntegerOf(update, "id") ?? 0)
            .LastOrDefault();
        return latest.ValueKind == JsonValueKind.Object ? StatusOf(latest) : new SdiStatusResult(SdiOutcomeKind.Submitted);
    }

    public async Task<IReadOnlyList<SdiNotification>> ReadNotificationAsync(string payload, CancellationToken cancellationToken = default)
    {
        // The body only says what changed (an event of the log of the provider: the endpoint, the method, the id of the resource): the update itself is read again from the API.
        long updateId;
        try
        {
            using var body = JsonDocument.Parse(payload);
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !ContainsIgnoreCase(StringOf(root, "Endpoint"), "update")
                || (StringOf(root, "Method") is { } method && !method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                || (BooleanOf(root, "Success") == false)
                || IntegerOf(root, "ResourceId") is not { } id)
            {
                return [];
            }

            updateId = id;
        }
        catch (JsonException)
        {
            return [];
        }

        using var response = await SendAsync(HttpMethod.Get, $"update/{updateId.ToString(CultureInfo.InvariantCulture)}", null, null, cancellationToken);
        using var update = await ReadOrThrowAsync("update", response, cancellationToken);
        var sendId = IntegerOf(update.RootElement, "send_id");
        return sendId is null || update.RootElement.ValueKind != JsonValueKind.Object
            ? []
            : [new SdiNotification(sendId.Value.ToString(CultureInfo.InvariantCulture), StatusOf(update.RootElement))];
    }

    // ---------------------------------------------------------------- the issuer

    /// <summary>
    /// Enables the issuer at the provider: its VAT number (or fiscal code) is a company of the account, found or created. <c>null</c> when it is; the refusal otherwise (a number registered on another
    /// account is <see cref="SdiSubmissionErrors.IssuerPending"/>).
    /// </summary>
    private async Task<SdiSubmissionResult?> EnsureIssuerAsync(SdiDocumentSubmission submission, CancellationToken ct)
    {
        var fiscalId = submission.IssuerFiscalId.Trim().ToUpperInvariant();
        using (var lookup = await SendAsync(HttpMethod.Get, $"company?q={Uri.EscapeDataString(fiscalId)}&page_size=20", null, null, ct))
        {
            if (!lookup.IsSuccessStatusCode)
                return await RefusalAsync("company", lookup, ct);

            using var companies = await ReadAsync(lookup, ct);
            if (companies is not null && HasCompany(companies.RootElement, fiscalId))
                return null;
        }

        var isVat = fiscalId.Length == 11 && fiscalId.All(char.IsAsciiDigit);
        var body = new Dictionary<string, object?>
        {
            ["name"] = submission.IssuerName,
            ["vat"] = isVat ? fiscalId : null,
            ["fiscal_code"] = isVat ? null : fiscalId,
        };
        using var create = await SendAsync(HttpMethod.Post, "company", body, null, ct);
        if (create.IsSuccessStatusCode)
        {
            logger.LogInformation("SDI channel {Provider}: issuer {IssuerProfileId} enabled", Name, submission.IssuerProfileId);
            return null;
        }

        var code = await ErrorCodeOfAsync(create, ct);
        if (create.StatusCode == HttpStatusCode.BadRequest && string.Equals(code, "company_already_registered", StringComparison.Ordinal))
        {
            logger.LogWarning("SDI channel {Provider}: issuer {IssuerProfileId} is registered on another account: its enabling is pending", Name, submission.IssuerProfileId);
            return SdiSubmissionResult.Failure(SdiSubmissionErrors.IssuerPending, code);
        }

        return Refused("company", create.StatusCode, KindOf(create.StatusCode), code);
    }

    private static bool HasCompany(JsonElement companies, string fiscalId)
    {
        if (companies.ValueKind != JsonValueKind.Array)
            return false;

        return companies.EnumerateArray().Any(company =>
            company.ValueKind == JsonValueKind.Object
            && (SameFiscalId(StringOf(company, "vat"), fiscalId) || SameFiscalId(StringOf(company, "fiscal_code"), fiscalId)));
    }

    private static bool SameFiscalId(string? stored, string fiscalId)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return false;

        var normalized = stored.Trim().ToUpperInvariant();
        return normalized == fiscalId || (normalized.StartsWith("IT", StringComparison.Ordinal) && normalized[2..] == fiscalId);
    }

    // ---------------------------------------------------------------- what the SDI said

    /// <summary>
    /// The state of an update of the provider. «Delivered»: the receipt of delivery, the notification of non-delivery, the attestation of transmission, the acceptance and the lapse of the terms (the
    /// invoice is issued and available to its recipient). «Rejected»: <c>Scartato</c>, with the codes and the words the SDI gave. «Submitted»: anything else, <c>Inviato</c> included.
    /// </summary>
    private SdiStatusResult StatusOf(JsonElement update)
    {
        var state = StringOf(update, "state");
        switch (state)
        {
            case "Consegnato":
            case "NonConsegnato":
            case "ImpossibilitaDiRecapito":
            case "AttestazioneTrasmissioneFattura":
            case "AccettatoDalDestinatario":
            case "DecorrenzaTermini":
                return new SdiStatusResult(SdiOutcomeKind.Delivered);
            case "Scartato":
                var (code, detail) = ErrorsOf(update);
                return new SdiStatusResult(SdiOutcomeKind.Rejected, code, detail);
            default:
                if (state is not null && state != "Inviato")
                    logger.LogWarning("SDI channel {Provider}: an update in a state CasaZen does not act on ({State})", Name, SafeState(state));
                return new SdiStatusResult(SdiOutcomeKind.Submitted);
        }
    }

    /// <summary>The codes and the words of the errors of a notification of rejection: the first code, and every error as «code description», cleaned.</summary>
    private static (string? Code, string? Detail) ErrorsOf(JsonElement update)
    {
        if (!update.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return (null, null);

        var codes = new List<string>();
        var lines = new List<string>();
        foreach (var error in errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
        {
            var code = StringOf(error, "code")?.Trim();
            if (!string.IsNullOrEmpty(code))
                codes.Add(code);

            lines.Add($"{code} {StringOf(error, "description")}".Trim());
        }

        return (codes.FirstOrDefault(), SdiRejectionReasons.Clean(string.Join("; ", lines.Where(line => line.Length > 0))));
    }

    // ---------------------------------------------------------------- the calls

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);

        // The key of the account is the user name of HTTP Basic, the password is empty (the provider's documentation); it is never logged.
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(options.Value.ApiKey!.Trim() + ":")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);

        return await http.SendAsync(request, ct);
    }

    /// <summary>The refusal a non-successful answer is: a short code of the channel and the provider's own error code, never the body.</summary>
    private async Task<SdiSubmissionResult> RefusalAsync(string operation, HttpResponseMessage response, CancellationToken ct) =>
        Refused(operation, response.StatusCode, KindOf(response.StatusCode), await ErrorCodeOfAsync(response, ct));

    private SdiSubmissionResult Refused(string operation, HttpStatusCode status, string error, string? code)
    {
        logger.LogWarning("SDI channel {Provider}: {Operation} answered {StatusCode}", Name, operation, (int)status);
        return SdiSubmissionResult.Failure(error, string.IsNullOrEmpty(code) ? ((int)status).ToString(CultureInfo.InvariantCulture) : $"{(int)status} {code}");
    }

    /// <summary>A file the provider refused (it does not pass its checks) is <c>provider_refused</c>; the credentials, the quota, a missing route and a failure of the provider are <c>provider_error</c>.</summary>
    private static string KindOf(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity ? SdiSubmissionErrors.ProviderRefused : SdiSubmissionErrors.ProviderError;

    /// <summary>The <c>code</c> of the error body of the provider (<c>company_already_registered</c>), when it is a plain identifier; <c>null</c> otherwise.</summary>
    private static async Task<string?> ErrorCodeOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var body = await ReadAsync(response, ct);
        var code = body is null ? null : StringOf(body.RootElement, "code");
        return code is not null && code.Length <= MaxCodeLength && Identifier().IsMatch(code) ? code : null;
    }

    private static async Task<JsonDocument?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The body of a read that must succeed: a failure is thrown (never turned into «nothing happened»), so the caller reads it again.</summary>
    private async Task<JsonDocument> ReadOrThrowAsync(string operation, HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("SDI channel {Provider}: {Operation} answered {StatusCode}", Name, operation, (int)response.StatusCode);
            throw new SdiProviderException(operation, (int)response.StatusCode);
        }

        return await ReadAsync(response, ct) ?? throw new SdiProviderException(operation, (int)response.StatusCode);
    }

    // ---------------------------------------------------------------- the JSON of the provider

    private static string? IdOf(JsonElement root) => IntegerOf(root, "id") is { } id ? id.ToString(CultureInfo.InvariantCulture) : null;

    private static DateTimeOffset DateOf(JsonElement update) =>
        StringOf(update, "last_update") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.MinValue;

    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && FindProperty(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? IntegerOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && FindProperty(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    private static bool? BooleanOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && FindProperty(element, name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    /// <summary>The member called <paramref name="name"/>, without regard to case (the log events of the provider are in PascalCase, its resources in snake_case).</summary>
    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static bool ContainsIgnoreCase(string? text, string part) => text?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;

    private static string SafeState(string state) => Identifier().IsMatch(state) && state.Length <= MaxCodeLength ? state : "unknown";

    [GeneratedRegex("^[A-Za-z0-9_.:-]+$")]
    private static partial Regex Identifier();
}

/// <summary>The provider of the SDI channel did not answer a read that must succeed (a status, a notification): nothing is recorded and the caller reads again. Carries the operation and the status, never a body.</summary>
public sealed class SdiProviderException(string operation, int statusCode)
    : Exception($"The SDI provider answered {statusCode} to {operation}.")
{
    public string Operation { get; } = operation;

    public int StatusCode { get; } = statusCode;
}
