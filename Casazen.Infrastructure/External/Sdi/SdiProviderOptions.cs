namespace Casazen.Infrastructure.External.Sdi;

/// <summary>
/// The channel to the SDI through an intermediary (E17/F01/US02/T02, NF-116, ⚖ T1), section <c>Sdi:Provider</c>. Railway variables <c>Sdi__Provider__Name</c>,
/// <c>Sdi__Provider__BaseUrl</c>, <c>Sdi__Provider__ApiKey</c> and <c>Sdi__Provider__WebhookSecret</c>. Without the name of an adapter that exists and the key, the channel is not
/// configured and nothing is ever submitted: the invoices stay «to send» and their issuers declare them sent by hand. Everything of the provider stays in its adapter: another
/// provider is another adapter and another <see cref="Name"/>.
/// </summary>
/// <remarks>The key and the secret of the webhook are never logged and never leave the server; <c>appsettings.json</c> holds empty placeholders only.</remarks>
public sealed class SdiProviderOptions
{
    public const string SectionName = "Sdi:Provider";

    public const string InvoicetronicName = "invoicetronic";

    public const string InvoicetronicBaseUrl = "https://api.invoicetronic.com/v1/";

    public const int MinWebhookSecretLength = 32;

    public string? Name { get; set; }

    public string? BaseUrl { get; set; }

    /// <summary>The key of the account at the provider (a sandbox key for the test environment, a live key for production). A secret: never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The secret of the address of the provider's webhook (<c>api/webhooks/sdi/{secret}</c>), at least <see cref="MinWebhookSecretLength"/> characters. A secret: never logged.</summary>
    public string? WebhookSecret { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public string? AdapterName => string.IsNullOrWhiteSpace(Name) ? null : Name.Trim().ToLowerInvariant();

    public Uri? ApiAddress
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(BaseUrl) ? InvoicetronicBaseUrl : BaseUrl.Trim();
            if (!text.EndsWith('/'))
                text += "/";

            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
                ? uri
                : null;
        }
    }

    public bool IsConfigured =>
        AdapterName == InvoicetronicName
        && !string.IsNullOrWhiteSpace(ApiKey)
        && ApiAddress is not null;

    public bool HasUsableWebhookSecret => WebhookSecret?.Trim() is { Length: >= MinWebhookSecretLength };
}
