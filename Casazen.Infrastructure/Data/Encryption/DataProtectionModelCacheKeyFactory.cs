using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Casazen.Infrastructure.Data.Encryption;

/// <summary>
/// Caches the EF model of <see cref="AppDbContext"/> per Data Protection provider (PC-11). The encrypted columns use
/// an <see cref="EncryptedStringConverter"/> built in <c>OnModelCreating</c> from the context's provider; with EF's
/// default key (the context type) the first context of the process fixed that provider for every later one (the limit
/// noted by FD-20). With this key every context encrypts and decrypts with its own provider. The application has one
/// provider, hence one model; contexts without provider (design time, some tests) get their own model without
/// converters.
/// </summary>
public sealed class DataProtectionModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is AppDbContext appContext
            ? (context.GetType(), appContext.EncryptionProvider, designTime)
            : (context.GetType(), designTime);
}
