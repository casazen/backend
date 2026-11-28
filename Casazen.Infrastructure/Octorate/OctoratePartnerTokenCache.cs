namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The partner token of the application, kept in memory for the whole process (E09/F01/US01/T02, ADR-023-R5): one instance, registered as a
/// singleton, because the services that use it live one request or one job long and the token is valid about 36 hours. The token is
/// replaced as a whole (the value and its expiry together), and only under <see cref="Gate"/>, so that two callers never renew it at once.
/// Nothing here is persisted and nothing is ever logged.
/// </summary>
public sealed class OctoratePartnerTokenCache
{
    private PartnerToken? _current;

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal PartnerToken? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}

internal sealed record PartnerToken(string Value, DateTime ExpiresAt)
{
    public override string ToString() => $"{nameof(PartnerToken)} {{ Value = ***, ExpiresAt = {ExpiresAt:O} }}";
}
