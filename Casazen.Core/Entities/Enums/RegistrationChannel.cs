namespace Casazen.Core.Entities.Enums;

/// <summary>How the RLI registration of a lease is made (LT-01, D15).</summary>
public enum RegistrationChannel
{
    /// <summary>Filed by an external provider on the landlord's delega (behind <c>Features:RliProvider</c>).</summary>
    Provider,

    /// <summary>Filed by the landlord (or their intermediary) on the official channel, then declared with number, date and receipt.</summary>
    Manual,
}
