using Casazen.Core.Features;
using Casazen.Core.Options;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Configuration;

/// <summary>
/// Startup check of the e-signature settings (LT-02, A7-20). With <c>Features:ESignProvider</c> on, the webhook
/// <c>POST webhooks/esign</c> exists and accepts bodies signed with <c>ESign:WebhookSecret</c>: a missing secret, or a
/// value that is public (a placeholder committed in the repository or the docs), would let anyone forge a signature
/// event, so the startup stops in every environment. With the flag off nothing is read.
/// </summary>
public sealed class ESignOptionsValidator(IFeatureFlags featureFlags) : IValidateOptions<ESignOptions>
{
    public const string Runbook = "docs/runbooks/rli.md";

    /// <summary>A shorter secret is not a provider-generated HMAC secret.</summary>
    public const int MinimumSecretLength = 16;

    public ValidateOptionsResult Validate(string? name, ESignOptions options)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.ESignProvider))
            return ValidateOptionsResult.Success;

        return IsWebhookSecretMissing(options.WebhookSecret)
            ? ValidateOptionsResult.Fail(
                $"Features:ESignProvider is on but ESign:WebhookSecret (ESign__WebhookSecret) is missing, a placeholder or " +
                $"shorter than {MinimumSecretLength} characters: the e-sign webhook would accept forged events. Set the " +
                $"secret of the provider's webhook subscription on Railway or turn the flag off. See {Runbook}.")
            : ValidateOptionsResult.Success;
    }

    /// <summary>Empty, a committed placeholder (<c>PLACEHOLDER_SET_IN_ENV</c>, <c>YOUR_…</c>, …) or too short.</summary>
    public static bool IsWebhookSecretMissing(string? secret) =>
        RequiredConfiguration.IsMissing(secret)
        || secret!.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
        || secret.Trim().Length < MinimumSecretLength;
}
