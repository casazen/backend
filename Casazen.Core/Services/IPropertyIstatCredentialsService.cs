namespace Casazen.Core.Services;

/// <summary>
/// The regional ISTAT credentials of a property (E20/F03/US01/T01): <b>write-only</b> towards clients, like the Alloggiati Web credentials
/// (<see cref="IQuesturaCredentialsService"/>). They can be set, replaced or removed, and the only thing ever read back is whether they are configured
/// and since when; the one reader of the values is the controlled opening of the support vault (<see cref="ISupportVaultService.RevealAsync"/>).
/// Callers authorize the property first (TN-3): the service never checks roles.
/// </summary>
public interface IPropertyIstatCredentialsService
{
    /// <summary>Whether the property has ISTAT credentials, and when they were last set. Never decrypts them.</summary>
    Task<IstatCredentialsStatus> GetStatusAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>Sets the credentials of the property, replacing the previous ones (every value every time). Safe under concurrent calls: one row per property.</summary>
    /// <param name="propertyId">Property, already authorized by the caller.</param>
    /// <param name="orgId">Org of the property (tenant of the row).</param>
    /// <param name="credentials">New values, already validated (required, maximum lengths).</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    Task<IstatCredentialsStatus> SetAsync(
        Guid propertyId,
        Guid orgId,
        IstatCredentialsInput credentials,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the credentials of the property; false when there were none.</summary>
    Task<bool> DeleteAsync(Guid propertyId, CancellationToken cancellationToken = default);
}

/// <summary>New ISTAT credentials of a property, in clear (never logged, never returned).</summary>
/// <param name="Username">The user name of the regional portal.</param>
/// <param name="Password">The password, kept exactly as typed.</param>
/// <param name="StructureCode">The code of the structure in the regional system (CIR, CUSR...); <c>null</c> or blank when the portal asks for none.</param>
public sealed record IstatCredentialsInput(string Username, string Password, string? StructureCode)
{
    /// <summary>Hides the values from logs and debugger output.</summary>
    public override string ToString() => nameof(IstatCredentialsInput) + " { *** }";
}

/// <summary>What a client may know about the ISTAT credentials of a property: configured or not, and since when.</summary>
/// <param name="Configured">True when the property has them.</param>
/// <param name="ConfiguredAt">When they were last set (UTC); <c>null</c> when not configured.</param>
public sealed record IstatCredentialsStatus(bool Configured, DateTime? ConfiguredAt)
{
    public static IstatCredentialsStatus NotConfigured { get; } = new(false, null);
}
