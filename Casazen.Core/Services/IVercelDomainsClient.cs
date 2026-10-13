namespace Casazen.Core.Services;

/// <summary>How a call to the Vercel Domains API ended (BK-17). Stable names: stored as the last error of a pending removal.</summary>
public enum VercelCallStatus
{
    Ok,

    /// <summary>404: the project or the domain on the project does not exist.</summary>
    NotFound,

    /// <summary>409: the domain is assigned to another project or account.</summary>
    Conflict,

    /// <summary>401/403: the token is invalid, expired or has no access to the project.</summary>
    Unauthorized,

    /// <summary>Another 4xx (400 included: an invalid domain, or one that is already on the project).</summary>
    Rejected,

    /// <summary>429: slow down, retried later.</summary>
    RateLimited,

    /// <summary>5xx, timeout or no connection.</summary>
    Unavailable,

    /// <summary><c>Vercel__ApiToken</c> or <c>Vercel__ProjectId</c> is not set: no call was made.</summary>
    NotConfigured,
}

/// <summary>A verification challenge Vercel asks for before a domain may be used on the project (<c>TXT</c> at <c>_vercel.{domain}</c>).</summary>
public sealed record VercelVerificationChallenge(string Type, string Domain, string Value, string? Reason);

/// <summary>A domain of the Vercel project: <paramref name="Verified"/> means Vercel accepts it on the project (ownership), not that its DNS is right.</summary>
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
    /// <summary>The token and the project are configured (<c>Vercel__ApiToken</c>, <c>Vercel__ProjectId</c>).</summary>
    bool IsConfigured { get; }

    /// <summary><c>GET /v9/projects/{project}/domains/{domain}</c>: the domain on the project, <see cref="VercelCallStatus.NotFound"/> when it is not there.</summary>
    Task<VercelCallResult<VercelDomain>> GetDomainAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary><c>POST /v10/projects/{project}/domains</c>: adds the domain to the project.</summary>
    Task<VercelCallResult<VercelDomain>> AddDomainAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary><c>POST /v9/projects/{project}/domains/{domain}/verify</c>: asks Vercel to check the verification challenge again.</summary>
    Task<VercelCallResult<VercelDomain>> VerifyDomainAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary><c>DELETE /v9/projects/{project}/domains/{domain}</c>: removes the domain; a domain that is not on the project is <see cref="VercelCallStatus.NotFound"/>.</summary>
    Task<VercelCallResult<bool>> RemoveDomainAsync(string domain, CancellationToken cancellationToken = default);
}
