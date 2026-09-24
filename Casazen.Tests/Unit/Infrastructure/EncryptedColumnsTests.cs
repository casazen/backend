using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>CO-14 (A5-30): the one list of encrypted columns and its converter.</summary>
public class EncryptedColumnsTests
{
    [Fact]
    public void All_DeclaresTheSecretsGuestDocumentsAndQuesturaCredentials()
    {
        var columns = EncryptedColumns.All.Select(c => $"{c.EntityType.Name}.{c.Property}:{c.Purpose}").Order().ToArray();

        Assert.Equal(
            new[]
            {
                "Guest.DocumentIssuingCountry:Casazen.Guest.Document",
                "Guest.DocumentNumber:Casazen.Guest.Document",
                "OtaIntegration.ApiKey:Casazen.OtaIntegration.Secrets",
                "OtaIntegration.ApiSecret:Casazen.OtaIntegration.Secrets",
                "PropertyQuesturaCredentials.Password:Casazen.PropertyQuesturaCredentials",
                "PropertyQuesturaCredentials.Username:Casazen.PropertyQuesturaCredentials",
                "PropertyQuesturaCredentials.WsKey:Casazen.PropertyQuesturaCredentials",
                "StayGuest.DocumentIssuePlaceName:Casazen.Guest.Document",
                "StayGuest.DocumentNumber:Casazen.Guest.Document",
            },
            columns);
    }

    [Fact]
    public void Model_WithProvider_EncryptsEveryDeclaredColumn_WithoutProviderNone()
    {
        using var encrypted = NewContext(new EphemeralDataProtectionProvider());
        using var plain = NewContext(null);

        Assert.True(EncryptedColumns.IsConfigured(encrypted));
        Assert.False(EncryptedColumns.IsConfigured(plain));
        Assert.All(EncryptedColumns.All, c =>
            Assert.Null(plain.Model.FindEntityType(c.EntityType)!.FindProperty(c.Property)!.GetValueConverter()));
    }

    [Theory]
    [InlineData("CA12345AB")]
    [InlineData("Comune di Firenze")]
    [InlineData("password con spazi ")]
    public void Converter_ClearValue_IsStoredAsPayloadWithoutTheValueAndReadBack(string value)
    {
        var converter = NewConverter(new EphemeralDataProtectionProvider());

        var stored = (string)converter.ConvertToProvider(value)!;

        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored);
        Assert.DoesNotContain(value.Trim(), stored);
        Assert.False(EncryptedColumns.IsLegacyPlaintext(stored));
        Assert.Equal(value, converter.ConvertFromProvider(stored));
    }

    [Fact]
    public void Converter_SameValueTwice_GivesDifferentPayloads()
    {
        var converter = NewConverter(new EphemeralDataProtectionProvider());

        Assert.NotEqual(converter.ConvertToProvider("CA12345AB"), converter.ConvertToProvider("CA12345AB"));
    }

    // Values written in clear before CO-14 stay readable until the startup step encrypts them.
    [Theory]
    [InlineData("CA12345AB")]
    [InlineData("Italia")]
    public void Converter_LegacyClearValue_IsReadAsItIs(string legacy)
    {
        Assert.True(EncryptedColumns.IsLegacyPlaintext(legacy));
        Assert.Equal(legacy, NewConverter(new EphemeralDataProtectionProvider()).ConvertFromProvider(legacy));
    }

    [Fact]
    public void Converter_PayloadOfAnotherPurpose_IsRejected()
    {
        var provider = new EphemeralDataProtectionProvider();
        var otaPayload = provider.CreateProtector(EncryptedColumns.OtaSecretsPurpose).Protect("CA12345AB");

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => NewConverter(provider).ConvertFromProvider(otaPayload));
    }

    private static EncryptedStringConverter NewConverter(IDataProtectionProvider provider) =>
        new(provider, EncryptedColumns.GuestDocumentPurpose, EncryptedColumns.IsLegacyPlaintext);

    private static AppDbContext NewContext(IDataProtectionProvider? provider) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            tenantContext: null,
            provider);
}
