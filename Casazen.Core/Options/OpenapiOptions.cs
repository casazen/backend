namespace Casazen.Core.Options;

/// <summary>
/// Openapi SpA DocuEngine (the Italian openapi.com service), section <c>Openapi</c>.
/// Railway variables <c>Openapi__BaseUrl</c>, <c>Openapi__Token</c> (or <c>Openapi__Email</c> + <c>Openapi__ApiKey</c>)
/// and <c>Openapi__LeaseDocumentId</c>. Empty means the client is not configured: nothing is filed.
/// The return URL of the filing fee reuses <c>ConcordatoFacile:BaseUrl</c> (the web app address the concordato checkout already uses).
/// </summary>
public sealed class OpenapiOptions
{
    public const string SectionName = "Openapi";

    /// <summary>DocuEngine origin. Sandbox <c>https://test.docuengine.openapi.com</c>, production <c>https://docuengine.openapi.com</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>OAuth origin, used only when <see cref="Token"/> is empty. Sandbox <c>https://test.oauth.openapi.com</c>.</summary>
    public string? OAuthUrl { get; set; }

    /// <summary>Bearer token created in the console or via <c>POST /tokens</c>.</summary>
    public string? Token { get; set; }

    /// <summary>Account email. With <see cref="ApiKey"/>, used as HTTP Basic against <see cref="OAuthUrl"/> to mint a token.</summary>
    public string? Email { get; set; }

    /// <summary>Account API key. Never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// <c>documentId</c> of the lease-contract registration service, read from <c>GET /documents</c>.
    /// Openapi publishes one registration product (not a separate non-concordato product); CasaZen maps the supported residential contract types to the provider codes.
    /// </summary>
    public string? LeaseDocumentId { get; set; } = "67e13cfd2cbb7e30d3d93e9d";

    /// <summary>Optional callback URL. Sent only together with <see cref="CallbackSecret"/>.</summary>
    public string? CallbackUrl { get; set; }

    /// <summary>Secret placed in the callback header <c>X-Casazen-Openapi-Secret</c>. Never logged.</summary>
    public string? CallbackSecret { get; set; }

    /// <summary>
    /// Comma-separated logical field names, in the order DocuEngine expects as <c>field0</c>…<c>fieldN</c>.
    /// Empty: the default order in <c>OpenapiLeaseSearch</c>. Confirm the order with <c>GET /documents</c> before production.
    /// </summary>
    public string? LeaseFieldOrder { get; set; }

    /// <summary>True when a filing can be attempted: origin, service id, and either a token or email plus API key.</summary>
    public bool IsConfigured =>
        IsAbsoluteHttp(BaseUrl)
        && !string.IsNullOrWhiteSpace(LeaseDocumentId)
        && (!string.IsNullOrWhiteSpace(Token)
            || (!string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(ApiKey) && IsAbsoluteHttp(OAuthUrl)));

    public IReadOnlyList<string> FieldOrderOrEmpty()
    {
        if (string.IsNullOrWhiteSpace(LeaseFieldOrder))
            return [];
        return LeaseFieldOrder
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToArray();
    }

    private static bool IsAbsoluteHttp(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
