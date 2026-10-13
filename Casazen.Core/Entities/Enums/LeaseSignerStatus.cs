namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Signature state of one party (LT-02, A7-16). An expired provider link is not a state: it is computed on read from
/// <see cref="LeaseSigner.SigningUrlExpiresAt"/>.
/// </summary>
public enum LeaseSignerStatus
{
    Pending,
    Signed,
}
