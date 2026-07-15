using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>Outcome of a single DNS TXT ownership verification attempt (#298).</summary>
public sealed record DomainVerificationResult(
    DomainVerificationStatus Status,
    string CustomDomain,
    DateTime CheckedAt,
    string? Message);

/// <summary>
/// Verifies custom-domain ownership via a DNS TXT challenge and persists the resulting
/// <see cref="Org.DomainVerificationStatus"/> (#298 / US-024). The caller (<c>IOrgDomainService</c>)
/// is responsible for validating that <paramref name="org"/> is in <c>CustomDomain</c> mode with a
/// non-null <c>CustomDomain</c> + <c>DomainVerificationToken</c> before calling <see cref="VerifyAsync"/>.
/// </summary>
public interface IDomainVerificationService
{
    Task<DomainVerificationResult> VerifyAsync(Org org, CancellationToken cancellationToken = default);
}
