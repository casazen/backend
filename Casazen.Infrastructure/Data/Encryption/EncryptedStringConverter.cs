using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Casazen.Infrastructure.Data.Encryption;

/// <summary>
/// EF value converter that encrypts string columns at rest using ASP.NET Data Protection.
/// </summary>
public sealed class EncryptedStringConverter(IDataProtector protector)
    : ValueConverter<string, string>(
        plain => string.IsNullOrEmpty(plain) ? plain : protector.Protect(plain),
        cipher => string.IsNullOrEmpty(cipher) ? cipher : protector.Unprotect(cipher))
{
    public EncryptedStringConverter(IDataProtectionProvider provider, string purpose)
        : this(provider.CreateProtector(purpose))
    {
    }
}
