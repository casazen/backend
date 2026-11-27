using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Data.Encryption;

/// <summary>
/// Encryption at rest of <see cref="PropertyOwner.Iban"/> and <see cref="PropertyOwner.FiscalCode"/> (ADR-012-R1):
/// separate encryption purpose for financial data, never shared with GuestDocumentPurpose or OtaSecretsPurpose.
/// Allows independent key rotation.
/// </summary>
public static class PropertyOwnerEncryptionPurpose
{
    public const string Purpose = "Casazen.PropertyOwner.Financial";
}
