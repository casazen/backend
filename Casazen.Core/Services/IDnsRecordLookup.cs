namespace Casazen.Core.Services;

/// <summary>
/// DNS lookups of the records a custom domain must have to be served by Vercel (BK-17, A3-25). Injectable so the domain check
/// is tested with mocked records, not real DNS.
/// </summary>
public interface IDnsRecordLookup
{
    /// <summary>The CNAME targets of <paramref name="host"/>, lower case and without the trailing dot; empty on miss or timeout.</summary>
    Task<IReadOnlyList<string>> LookupCnameAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>The IPv4 addresses <paramref name="host"/> resolves to (a CNAME chain is followed by the resolver); empty on miss or timeout.</summary>
    Task<IReadOnlyList<string>> LookupAddressesAsync(string host, CancellationToken cancellationToken = default);
}
