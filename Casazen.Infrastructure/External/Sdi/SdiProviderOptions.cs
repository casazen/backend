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

    /// <summary>The adapter of <see cref="Name"/> for the provider the documentation of this build was read from (<c>docs/runbooks/fiscal-documents.md</c>).</summary>
    public const string InvoicetronicName = "invoicetronic";

    /// <summary>The address of the API of the adapter of <see cref="InvoicetronicName"/>: the sandbox and the live service share it, the key chooses between them.</summary>
    public const string InvoicetronicBaseUrl = "https://api.invoicetronic.com/v1/";

    /// <summary>The shortest secret of the webhook accepted: it is a password in an address.</summary>
    public const int MinWebhookSecretLength = 32;

    /// <summary>The adapter to use (<see cref="InvoicetronicName"/>, without regard to case); empty = no provider.</summary>
    public string? Name { get; set; }

    /// <summary>The origin of the provider's API, with <c>https</c>; empty = the address the adapter was written for (<see cref="InvoicetronicBaseUrl"/>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The key of the account at the provider (a sandbox key for the test environment, a live key for production). A secret: never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The secret of the address of the provider's webhook (<c>api/webhooks/sdi/{secret}</c>), at least <see cref="MinWebhookSecretLength"/> characters. A secret: never logged.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>How long a call to the provider may last, in seconds (the file of an invoice is a few kilobytes).</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>The name of the adapter, trimmed and in lower case; <c>null</c> when empty.</summary>
    public string? AdapterName => string.IsNullOrWhiteSpace(Name) ? null : Name.Trim().ToLowerInvariant();

    /// <summary>The address the adapter calls: <see cref="BaseUrl"/>, else the one of the adapter; always with the closing slash the relative paths need.</summary>
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

    /// <summary>True when a submission can be attempted: an adapter that exists, a key and a valid address (<c>https</c>, or <c>http</c> on the machine itself for a test).</summary>
    public bool IsConfigured =>
        AdapterName == InvoicetronicName
        && !string.IsNullOrWhiteSpace(ApiKey)
        && ApiAddress is not null;

    /// <summary>True when a webhook can be authenticated: a secret of at least <see cref="MinWebhookSecretLength"/> characters.</summary>
    public bool HasUsableWebhookSecret => WebhookSecret?.Trim() is { Length: >= MinWebhookSecretLength };
}
