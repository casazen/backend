namespace Casazen.Core.Services;

/// <summary>
/// Thin DNS TXT record lookup abstraction (#298). Injectable so
/// <see cref="IDomainVerificationService"/> can be unit tested with mocked records instead of
/// hitting real DNS.
/// </summary>
public interface IDnsTxtLookup
{
    /// <summary>Returns all TXT record values for <paramref name="host"/>, or an empty list on miss/timeout.</summary>
    Task<IReadOnlyList<string>> LookupTxtAsync(string host, CancellationToken cancellationToken = default);
}
