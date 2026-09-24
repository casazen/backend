using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Casazen.Infrastructure.Data.Encryption;

/// <summary>
/// EF value converter that encrypts string columns at rest using ASP.NET Data Protection.
/// </summary>
/// <remarks>
/// The converter is part of the EF model: <see cref="DataProtectionModelCacheKeyFactory"/> caches the model of
/// <see cref="AppDbContext"/> per Data Protection provider, so each context encrypts with its own provider. Read and
/// write an encrypted column through EF (never raw SQL with a protector of its own). See <c>docs/runbooks/ical.md</c>
/// (PC-11).
/// </remarks>
public sealed class EncryptedStringConverter : ValueConverter<string, string>
{
    public EncryptedStringConverter(IDataProtector protector)
        : this(protector, isLegacyPlaintext: null)
    {
    }

    /// <param name="protector">Protector of the column's purpose.</param>
    /// <param name="isLegacyPlaintext">
    /// Stored values it accepts are read back as they are: values written in clear before the column was encrypted,
    /// until they are rewritten (every write is encrypted). Null: every stored value must be a protected payload.
    /// </param>
    public EncryptedStringConverter(IDataProtector protector, Func<string, bool>? isLegacyPlaintext)
        : base(
            plain => string.IsNullOrEmpty(plain) ? plain : protector.Protect(plain),
            cipher => Unprotect(protector, cipher, isLegacyPlaintext))
    {
    }

    public EncryptedStringConverter(IDataProtectionProvider provider, string purpose)
        : this(provider.CreateProtector(purpose))
    {
    }

    public EncryptedStringConverter(IDataProtectionProvider provider, string purpose, Func<string, bool>? isLegacyPlaintext)
        : this(provider.CreateProtector(purpose), isLegacyPlaintext)
    {
    }

    private static string Unprotect(IDataProtector protector, string cipher, Func<string, bool>? isLegacyPlaintext)
    {
        if (string.IsNullOrEmpty(cipher))
            return cipher;

        return isLegacyPlaintext is not null && isLegacyPlaintext(cipher) ? cipher : protector.Unprotect(cipher);
    }
}
