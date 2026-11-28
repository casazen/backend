namespace Casazen.Core.Services;

/// <summary>
/// DNS lookups of the records a custom domain must have to be served by Vercel (BK-17, A3-25). Injectable so the domain check
/// is tested with mocked records, not real DNS.
/// </summary>
public interface IDnsRecordLookup
{
    Task<IReadOnlyList<string>> LookupCnameAsync(string host, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> LookupAddressesAsync(string host, CancellationToken cancellationToken = default);
}
