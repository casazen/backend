namespace Casazen.Core.Services;

public record ConnectAccountSnapshot(
    string AccountId,
    bool ChargesEnabled,
    bool PayoutsEnabled,
    bool DetailsSubmitted,
    IReadOnlyList<string> RequirementsDue);

public interface IStripeConnectGateway
{
    Task<string> CreateExpressAccountAsync(string email, CancellationToken cancellationToken = default);
    Task<ConnectAccountSnapshot> GetAccountAsync(string connectedAccountId, CancellationToken cancellationToken = default);
    Task<string> CreateAccountOnboardingLinkAsync(
        string connectedAccountId,
        string returnUrl,
        string refreshUrl,
        CancellationToken cancellationToken = default);
}

public record ConnectStatus(
    string? ConnectedAccountId,
    bool ChargesEnabled,
    bool PayoutsEnabled,
    bool DetailsSubmitted,
    IReadOnlyList<string> RequirementsDue);

public interface IConnectOnboardingService
{
    Task<ConnectStatus> GetStatusAsync(Guid orgId, bool refreshFromStripe, CancellationToken cancellationToken = default);
    Task<ConnectStatus> EnsureExpressAccountAsync(Guid orgId, CancellationToken cancellationToken = default);
    Task<string> CreateOnboardingLinkAsync(
        Guid orgId,
        string returnUrl,
        string refreshUrl,
        CancellationToken cancellationToken = default);
    Task ApplyAccountUpdatedAsync(ConnectAccountSnapshot snapshot, CancellationToken cancellationToken = default);
}
