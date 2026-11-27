using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// ADR-023-R1 and R15 (E02/F01/US01/T01): the OAuth tokens of an Octorate connection are stored encrypted, with a purpose of their
/// own. The EF InMemory database keeps the values the model hands it and not the stored text, so the proof is on the model and its
/// converters: what the converter hands to the column is a Data Protection payload with none of the token, and only the converter of
/// the Octorate purpose reads it back.
/// </summary>
public class OctorateConnectionEncryptionTests
{
    // One provider for the whole class: EF keeps one model per provider, and a provider per test would fill the cache the other tests share.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    private const string AccessToken = "oct-access-7c1f0e2a9b4d4f6e8a1b3c5d7e9f0a2b6c8d";
    private const string RefreshToken = "oct-refresh-4e6a8c0b2d4f6a8c0e2b4d6f8a0c2e4f6a8c";

    [Fact]
    public void OctorateConnection_TokenEncrypted_NotPlaintext_Test()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            tenantContext: null,
            Provider);
        var entity = db.Model.FindEntityType(typeof(OctorateConnection))!;
        var otherPurpose = Provider.CreateProtector(EncryptedColumns.OtaSecretsPurpose);

        foreach (var (property, token) in new[]
                 {
                     (nameof(OctorateConnection.EncryptedAccessToken), AccessToken),
                     (nameof(OctorateConnection.EncryptedRefreshToken), RefreshToken),
                 })
        {
            Assert.Contains(
                EncryptedColumns.All,
                c => c.EntityType == typeof(OctorateConnection) && c.Property == property && c.Purpose == EncryptedColumns.OctorateOAuthPurpose);

            var column = entity.FindProperty(property)!;
            var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());
            Assert.Null(column.GetMaxLength());

            var stored = (string)converter.ConvertToProvider(token)!;

            // What the column holds: a payload, and nothing of the token in it.
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(token, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(token[^16..], stored, StringComparison.Ordinal);

            // Read back only through the decryption of the Octorate purpose: the protector of another column cannot read it.
            Assert.Equal(token, converter.ConvertFromProvider(stored));
            Assert.ThrowsAny<CryptographicException>(() => otherPurpose.Unprotect(stored));
        }

        Assert.Equal("Casazen.Octorate.OAuth", EncryptedColumns.OctorateOAuthPurpose);
        Assert.True(EncryptedColumns.IsConfigured(db));
    }
}
