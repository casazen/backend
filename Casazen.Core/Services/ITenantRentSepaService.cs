namespace Casazen.Core.Services;

public sealed record TenantSepaSettings(bool Authorized, bool AutomaticEnabled, bool CanAuthorize,
    DateOnly AuthorizationUntil, DateOnly BlockedFrom, decimal TransactionFee, bool CanManage);
public sealed record TenantSepaSession(string ClientSecret, string PublishableKey, string StripeAccountId, bool UseSavedMandate = false);
public interface ITenantRentSepaService
{
    Task<TenantSepaSettings> GetAsync(Guid orgId, Guid leaseId, string userId, CancellationToken ct = default);
    Task<TenantSepaSession> SetupAsync(Guid orgId, Guid leaseId, string userId, string email, CancellationToken ct = default);
    Task<TenantSepaSettings> SetAutomaticAsync(Guid orgId, Guid leaseId, string userId, bool enabled, CancellationToken ct = default);
    Task<TenantSepaSession> PayAsync(Guid orgId, Guid leaseId, Guid installmentId, CancellationToken ct = default, string? userId = null);
    Task<TenantSepaSession> PayPublicAsync(Guid installmentId, string token, CancellationToken ct = default);
    Task RunAsync(CancellationToken ct = default);
}
