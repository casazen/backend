using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Data.Encryption;

/// <summary>
/// Encryption at rest of <see cref="PropertyAccessCode.EncryptedCode"/> (NF-13, E06/F03/US01/T01):
/// separate encryption purpose for access codes, never shared with other purposes.
/// Allows independent key rotation per ADR-012 pattern.
/// </summary>
public static class PropertyAccessCodeEncryptionPurpose
{
    /// <summary>
    /// Data Protection purpose for property access codes.
    /// NEVER change it: stored encrypted values could not be decrypted.
    /// </summary>
    public const string Purpose = "Casazen.PropertyAccessCode";
}
