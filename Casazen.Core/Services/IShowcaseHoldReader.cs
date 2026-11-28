namespace Casazen.Core.Services;

public readonly record struct ShowcaseHoldInterval(DateTime StartUtc, DateTime EndUtc, DateTime ExpiresAtUtc);

public interface IShowcaseHoldReader
{
    Task<IReadOnlyList<ShowcaseHoldInterval>> ListLiveAsync(
        Guid supplierOrgId,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
