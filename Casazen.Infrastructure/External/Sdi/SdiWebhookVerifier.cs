using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External.Sdi;

/// <summary>
/// Authenticates a call to the webhook of the SDI channel (E17/F01/US02/T02, NF-116): the provider's call carries no CasaZen credential, so CasaZen puts a secret in the address it registers
/// (<c>/api/webhooks/sdi/{secret}</c>) and compares it, in constant time, with <c>Sdi:Provider:WebhookSecret</c>. The body of the call is never trusted either: the adapter re-reads what
/// it names from the provider's API before anything is recorded.
/// </summary>
/// <remarks>
/// A configured secret that is empty or shorter than <see cref="SdiProviderOptions.MinWebhookSecretLength"/> is a configuration error the controller answers with 500, never an accepted call.
/// The secret is part of the path: the request logs must not print it (<c>docs/runbooks/fiscal-documents.md</c>).
/// </remarks>
public sealed class SdiWebhookVerifier(IOptions<SdiProviderOptions> options)
{
    private const int MaxPresentedLength = 512;

    public bool HasUsableSecret => options.Value.HasUsableWebhookSecret;

    public bool IsValid(string? presented)
    {
        if (!HasUsableSecret || string.IsNullOrEmpty(presented) || presented.Length > MaxPresentedLength)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.WebhookSecret!.Trim())));
    }
}
