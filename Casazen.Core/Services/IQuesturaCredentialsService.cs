namespace Casazen.Core.Services;

/// <summary>
/// Alloggiati Web (Questura) credentials of a property (CO-14, A5-30). Write-only towards clients: they can be set,
/// replaced or removed, and the only thing ever read back is whether they are configured and since when. The values are
/// encrypted at rest (<c>docs/runbooks/encryption.md</c>). Callers authorize the property first (TN-3): the service never
/// checks roles.
/// </summary>
public interface IQuesturaCredentialsService
{
    /// <summary>Whether the property has credentials, and when they were last set. Never decrypts them.</summary>
    Task<QuesturaCredentialsStatus> GetStatusAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the credentials of the property, replacing the previous ones (all three values every time). Safe under
    /// concurrent calls: one row per property.
    /// </summary>
    /// <param name="propertyId">Property, already authorized by the caller.</param>
    /// <param name="orgId">Org of the property (tenant of the row).</param>
    /// <param name="credentials">New values, already validated (required, maximum lengths).</param>
    Task<QuesturaCredentialsStatus> SetAsync(
        Guid propertyId,
        Guid orgId,
        QuesturaCredentialsInput credentials,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the credentials of the property; false when there were none.</summary>
    Task<bool> DeleteAsync(Guid propertyId, CancellationToken cancellationToken = default);
}

/// <summary>New Alloggiati Web credentials of a property, in clear (never logged, never returned).</summary>
public sealed record QuesturaCredentialsInput(string Username, string Password, string WsKey)
{
    /// <summary>Hides the values from logs and debugger output.</summary>
    public override string ToString() => nameof(QuesturaCredentialsInput) + " { *** }";
}

/// <summary>What a client may know about the credentials of a property: configured or not, and since when.</summary>
/// <param name="Configured">True when the property has credentials.</param>
/// <param name="ConfiguredAt">When they were last set (UTC); null when not configured.</param>
public sealed record QuesturaCredentialsStatus(bool Configured, DateTime? ConfiguredAt)
{
    public static QuesturaCredentialsStatus NotConfigured { get; } = new(false, null);
}
