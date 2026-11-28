namespace Casazen.Core.Services;

public enum VercelCallStatus
{
    Ok,

    NotFound,

    Conflict,

    Unauthorized,

    Rejected,

    RateLimited,

    Unavailable,

    NotConfigured,
}

public sealed record VercelVerificationChallenge(string Type, string Domain, string Value, string? Reason);

public sealed record VercelDomain(string Name, bool Verified, IReadOnlyList<VercelVerificationChallenge> Verification);

/// <param name="Value">The answer, only with <see cref="VercelCallStatus.Ok"/>.</param>
/// <param name="HttpStatus">HTTP status when there was an answer (for the logs).</param>
/// <param name="ErrorCode">Error code Vercel sent (for the logs only, never shown), at most 64 characters.</param>
public sealed record VercelCallResult<T>(VercelCallStatus Status, T? Value = default, int? HttpStatus = null, string? ErrorCode = null)
{
    public bool IsOk => Status == VercelCallStatus.Ok;
}

/// <summary>
/// The domains of the Vercel project that serves the web app, through the Vercel REST API (BK-17, A3-25): read, add, verify
/// and remove one. Every call is idempotent; none throws for an API failure (the result says what happened).
/// </summary>
public interface IVercelDomainsClient
{
    bool IsConfigured { get; }

    Task<VercelCallResult<VercelDomain>> GetDomainAsync(string domain, CancellationToken cancellationToken = default);

    Task<VercelCallResult<VercelDomain>> AddDomainAsync(string domain, CancellationToken cancellationToken = default);

    Task<VercelCallResult<VercelDomain>> VerifyDomainAsync(string domain, CancellationToken cancellationToken = default);

    Task<VercelCallResult<bool>> RemoveDomainAsync(string domain, CancellationToken cancellationToken = default);
}
