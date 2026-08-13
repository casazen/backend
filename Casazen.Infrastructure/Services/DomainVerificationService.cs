using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// DNS TXT ownership challenge verification for custom domains (#298 / US-024). Looks up
/// <c>{TxtRecordPrefix}.{CustomDomain}</c> via <see cref="IDnsTxtLookup"/>, bounded by
/// <see cref="PublicHostOptions.DnsLookupTimeoutSeconds"/>, and persists the resulting status.
/// </summary>
public class DomainVerificationService(
    AppDbContext dbContext,
    IDnsTxtLookup dnsTxtLookup,
    IOptions<PublicHostOptions> options) : IDomainVerificationService
{
    private const string FailedMessage =
        "Non è stato possibile verificare il record TXT. Controlla che il record DNS sia stato propagato e riprova.";

    public async Task<DomainVerificationResult> VerifyAsync(Org org, CancellationToken cancellationToken = default)
    {
        var customDomain = org.CustomDomain!;
        var txtHost = $"{options.Value.TxtRecordPrefix}.{customDomain}";

        var records = await LookupWithTimeoutAsync(txtHost, cancellationToken);
        var isVerified = records.Any(r => string.Equals(r, org.DomainVerificationToken, StringComparison.Ordinal));

        var wasAlreadyVerified = org.DomainVerificationStatus == DomainVerificationStatus.Verified;
        var status = isVerified || wasAlreadyVerified
            ? DomainVerificationStatus.Verified
            : DomainVerificationStatus.Failed;
        org.DomainVerificationStatus = status;
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var checkedAt = DateTime.UtcNow;
        return new DomainVerificationResult(
            status,
            customDomain,
            checkedAt,
            status == DomainVerificationStatus.Verified ? null : FailedMessage);
    }

    private async Task<IReadOnlyList<string>> LookupWithTimeoutAsync(string host, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.DnsLookupTimeoutSeconds)));

        try
        {
            return await dnsTxtLookup.LookupTxtAsync(host, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out, not caller-cancelled — treat as a lookup miss (Failed).
            return [];
        }
    }
}
