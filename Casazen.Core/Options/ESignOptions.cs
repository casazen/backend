namespace Casazen.Core.Options;

/// <summary>
/// E-signature provider settings (<c>ESign</c> section, LT-02). Set only as Railway variables (<c>ESign__BaseUrl</c>,
/// <c>ESign__ApiKey</c>, <c>ESign__WebhookSecret</c>), never in a committed file. Read only with
/// <c>Features:ESignProvider</c> on: then <see cref="WebhookSecret"/> is required at startup. Runbook:
/// docs/runbooks/rli.md § Contract signature (LT-02).
/// </summary>
public sealed class ESignOptions
{
    public const string SectionName = "ESign";

    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public string? WebhookSecret { get; set; }
}
