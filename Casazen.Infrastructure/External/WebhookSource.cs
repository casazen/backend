namespace Casazen.Infrastructure.External;

/// <summary>RF2 discriminator — platform billing vs connected-account (direct checkout) webhooks.</summary>
public enum WebhookSource
{
    Platform,
    Connected,
}
