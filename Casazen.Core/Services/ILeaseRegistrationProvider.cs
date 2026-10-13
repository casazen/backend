using Casazen.Core.Entities;
using Casazen.Core.Features;

namespace Casazen.Core.Services;

/// <summary>
/// External RLI filing provider (LT-01, D15). The candidate is Openapi DocuEngine (docs/integrations/rli-esign.md §1),
/// but its lease-registration service id and input fields are not in the public specification: no real client exists
/// yet and the default registration is an unconfigured provider. The provider is used only when
/// <see cref="RliProviderFiling.IsAvailable"/>: <c>Features:RliProvider</c> on <b>and</b> <see cref="IsConfigured"/>.
/// </summary>
/// <remarks>
/// A provider never reports success it has not received: <see cref="SubmitAsync"/> returns the provider's request id
/// (the filing is then in progress), and only <see cref="ProviderRegistrationState.Registered"/> together with a
/// downloadable receipt makes the lease Registered.
/// </remarks>
public interface ILeaseRegistrationProvider
{
    /// <summary>True only when every setting the provider needs (credentials, service id, callback) is present.</summary>
    bool IsConfigured { get; }

    /// <summary>Opens the filing request; returns the provider's request id. Throws <see cref="LeaseRegistrationProviderException"/> on failure.</summary>
    Task<string> SubmitAsync(LeaseContract lease, CancellationToken cancellationToken = default);

    /// <summary>Current state of a request opened by <see cref="SubmitAsync"/>.</summary>
    Task<ProviderRegistrationStatus> GetStatusAsync(string externalRegistrationId, CancellationToken cancellationToken = default);

    /// <summary>Official receipt (PDF) of a <see cref="ProviderRegistrationState.Registered"/> request. The caller disposes the stream.</summary>
    Task<Stream> DownloadReceiptAsync(string externalRegistrationId, CancellationToken cancellationToken = default);
}

/// <summary>State of a provider request.</summary>
public enum ProviderRegistrationState
{
    InProgress,
    Registered,
    Failed,
}

/// <summary>
/// Provider status. <paramref name="RegistrationCode"/> is null when the provider does not return the registration
/// number in a structured field (Openapi, rli-esign.md §1.4). <paramref name="FailureCode"/> is a stable code, never a
/// provider message (it may carry personal data).
/// </summary>
public sealed record ProviderRegistrationStatus(
    ProviderRegistrationState State,
    string? RegistrationCode = null,
    string? FailureCode = null);

/// <summary>The provider could not take or process the request (network, credit, validation, outage).</summary>
public sealed class LeaseRegistrationProviderException : Exception
{
    public LeaseRegistrationProviderException(string failureCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        FailureCode = failureCode;
    }

    /// <summary>Stable code stored on the registration (<see cref="RliRegistrationFailureCodes"/>).</summary>
    public string FailureCode { get; }
}

/// <summary>When the provider path exists (LT-01): the flag on and a configured provider. Otherwise only the manual path.</summary>
public static class RliProviderFiling
{
    public static bool IsAvailable(IFeatureFlags featureFlags, ILeaseRegistrationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(featureFlags);
        ArgumentNullException.ThrowIfNull(provider);
        return featureFlags.IsEnabled(FeatureFlags.RliProvider) && provider.IsConfigured;
    }
}
