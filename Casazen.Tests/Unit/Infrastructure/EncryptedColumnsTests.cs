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
    public void All_DeclaresTheSecretsICalUrlsGuestDocumentsQuesturaCredentialsAndTheSupplierCustomers()
    {
        var columns = EncryptedColumns.All.Select(c => $"{c.EntityType.Name}.{c.Property}:{c.Purpose}").Order().ToArray();

        Assert.Equal(
            new[]
            {
                "DocumentImage.BackImageUrl:Casazen.DocumentImage",
                "DocumentImage.FrontImageUrl:Casazen.DocumentImage",
                "Guest.DocumentIssuingCountry:Casazen.Guest.Document",
                "Guest.DocumentNumber:Casazen.Guest.Document",
                // LR-12: the values a landlord typed in the wizard and kept in the draft, and the residence of a party of a lease.
                "LeaseContract.DraftData:Casazen.LeaseContract.Draft",
                // TR: the need of a transitory lease and the documentation that proves it.
                "LeaseContract.TransitoryDocumentation:Casazen.LeaseContract.TransitoryNeed",
                "LeaseContract.TransitoryNeed:Casazen.LeaseContract.TransitoryNeed",
                "OtaIntegration.ApiKey:Casazen.OtaIntegration.Secrets",
                "OtaIntegration.ApiSecret:Casazen.OtaIntegration.Secrets",
                "Party.Residence:Casazen.LeaseParty",
                "PlatformAiSettings.ApiKey:Casazen.PlatformAiSettings",
                // NF-13: encrypted property access codes (gate, lockbox, smart lock PIN).
                "PropertyAccessCode.EncryptedCode:Casazen.PropertyAccessCode",
                "PropertyICalFeed.ImportUrl:Casazen.PropertyICalFeed.ImportUrl",
                // ADR-012: property owner financial data (IBAN and fiscal code).
                "PropertyOwner.FiscalCode:Casazen.PropertyOwner.Financial",
                "PropertyOwner.Iban:Casazen.PropertyOwner.Financial",
                "PropertyQuesturaCredentials.Password:Casazen.PropertyQuesturaCredentials",
                "PropertyQuesturaCredentials.Username:Casazen.PropertyQuesturaCredentials",
                "PropertyQuesturaCredentials.WsKey:Casazen.PropertyQuesturaCredentials",
                // SP-10: the private customers of the suppliers, the booking that waits for their e-mail check (the same data
                // before it is a customer: one purpose) and the place of the work of a request from the public showcase.
                "ServiceCustomer.Email:Casazen.ServiceCustomer",
                "ServiceCustomer.FullName:Casazen.ServiceCustomer",
                "ServiceCustomer.Phone:Casazen.ServiceCustomer",
                // SP-15c-c: the words of a complaint about a job paid through CasaZen (the payer's, the supplier's, the admin's note).
                "ServicePaymentDispute.AdminNote:Casazen.ServicePaymentDispute",
                "ServicePaymentDispute.CustomerText:Casazen.ServicePaymentDispute",
                "ServicePaymentDispute.SupplierReply:Casazen.ServicePaymentDispute",
                "ServiceRequest.LocationAccessNotes:Casazen.ServiceRequest.Location",
                "ServiceRequest.LocationAddress:Casazen.ServiceRequest.Location",
                "ServiceRequest.LocationFloor:Casazen.ServiceRequest.Location",
                // SP-10b: the words of the thread of a request on measure.
                "ServiceRequestMessage.Body:Casazen.ServiceRequestMessage",
                "ShowcaseBookingHold.Payload:Casazen.ServiceCustomer",
                // SP-10b: the request on measure that waits for the e-mail check (the same data as the hold, plus the words).
                "ShowcaseInquiry.Payload:Casazen.ServiceCustomer",
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
