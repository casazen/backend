using Casazen.Core.Services;
using DnsClient;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// DNS TXT lookup backed by <c>DnsClient</c> (#298). Kept behind <see cref="IDnsTxtLookup"/> so
/// <see cref="DomainVerificationService"/> can be unit tested without real DNS traffic.
/// </summary>
public class DnsClientTxtLookup(ILogger<DnsClientTxtLookup> logger) : IDnsTxtLookup
{
    private readonly LookupClient _lookupClient = new();

    public async Task<IReadOnlyList<string>> LookupTxtAsync(string host, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _lookupClient.QueryAsync(host, QueryType.TXT, cancellationToken: cancellationToken);
            return result.Answers.TxtRecords()
                .SelectMany(r => r.Text)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DNS TXT lookup failed for host {Host}", host);
            return [];
        }
    }
}
