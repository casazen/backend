using Casazen.Core.Services;
using DnsClient;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public sealed class DnsClientRecordLookup(ILogger<DnsClientRecordLookup> logger) : IDnsRecordLookup
{
    private readonly LookupClient _lookupClient = new();

    public async Task<IReadOnlyList<string>> LookupCnameAsync(string host, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _lookupClient.QueryAsync(host, QueryType.CNAME, cancellationToken: cancellationToken);
            return result.Answers.CnameRecords().Select(r => Normalize(r.CanonicalName.Value)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DNS CNAME lookup failed for host {Host}", host);
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> LookupAddressesAsync(string host, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _lookupClient.QueryAsync(host, QueryType.A, cancellationToken: cancellationToken);
            return result.Answers.ARecords().Select(r => r.Address.ToString()).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DNS A lookup failed for host {Host}", host);
            return [];
        }
    }

    private static string Normalize(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();
}
