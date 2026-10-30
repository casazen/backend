using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Data.Encryption;

/// <summary>
/// Encryption at rest of <see cref="PropertyOwner.Iban"/> and <see cref="PropertyOwner.FiscalCode"/> (ADR-012-R1):
/// separate encryption purpose for financial data, never shared with GuestDocumentPurpose or OtaSecretsPurpose.
/// Allows independent key rotation.
/// </summary>
public static class PropertyOwnerEncryptionPurpose
{
    /// <summary>
    /// Data Protection purpose for property owner financial data (IBAN and fiscal code).
    /// NEVER change it: stored encrypted values could not be decrypted.
    /// </summary>
    public const string Purpose = "Casazen.PropertyOwner.Financial";
}
