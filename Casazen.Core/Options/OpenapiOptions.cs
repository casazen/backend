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

    public string? BaseUrl { get; set; }

    public string? OAuthUrl { get; set; }

    public string? Token { get; set; }

    public string? Email { get; set; }

    public string? ApiKey { get; set; }

    public string? LeaseDocumentId { get; set; } = "67e13cfd2cbb7e30d3d93e9d";

    public string? CallbackUrl { get; set; }

    /// <summary>Secret placed in the callback header <c>X-Casazen-Openapi-Secret</c>. Never logged.</summary>
    public string? CallbackSecret { get; set; }

    public string? LeaseFieldOrder { get; set; }

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
