namespace Casazen.Core.Services;

/// <summary>What one run of the periodic domain job did (BK-17), for the log and the tests.</summary>
public sealed record DomainRecheckSummary(int Checked, int NowVerified, int NoLongerVerified, int Removed, int RemovalsFailed);

/// <summary>
/// Periodic job of the custom domains (BK-17, A3-25): checks again the domains that are not verified yet (they activate by
/// themselves once the host's DNS records are in place), the verified ones (a removed record is noticed), and removes from the
/// Vercel project the domains that were dropped. Each org and each removal is isolated: one failure never stops the run.
/// </summary>
public interface IDomainRecheckService
{
    Task<DomainRecheckSummary> RunAsync(CancellationToken cancellationToken = default);
}
