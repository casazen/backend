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

    /// <summary>One renewal at a time: whoever holds it re-reads <see cref="Current"/> before it calls Octorate.</summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>The token last obtained, or <c>null</c> before the first login. Read without the gate, written only under it.</summary>
    internal PartnerToken? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}

/// <summary>A partner token and the instant it expires (UTC). Never prints the token.</summary>
internal sealed record PartnerToken(string Value, DateTime ExpiresAt)
{
    public override string ToString() => $"{nameof(PartnerToken)} {{ Value = ***, ExpiresAt = {ExpiresAt:O} }}";
}
