namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Lifecycle status of a <see cref="Casazen.Core.Entities.SupplierProfile"/>.
/// </summary>
public enum SupplierStatus
{
    /// <summary>Profile created; activation wizard not yet completed.</summary>
    Pending,

    /// <summary>Activation wizard completed and ToS accepted; visible to hosts.</summary>
    Active,

    /// <summary>Manually suspended by platform admin.</summary>
    Suspended,
}
