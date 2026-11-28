using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class EncryptedColumnsTests
{
    [Fact]
    public void All_DeclaresTheSecretsICalUrlsGuestDocumentsQuesturaCredentialsAndTheSupplierCustomers()
    {
        var columns = EncryptedColumns.All.Select(c => $"{c.EntityType.Name}.{c.Property}:{c.Purpose}").Order().ToArray();

        Assert.Equal(
            new[]
            {
                "AccountantPackageSettings.AccountantEmail:Casazen.AccountantPackage.Settings",
                "ApprovalQueueItem.ContentText:Casazen.Message.Content",
                "ApprovalQueueItem.EditedContentText:Casazen.Message.Content",
                "AssistantLesson.ProposedText:Casazen.Assistant.Content",
                "AssistantSituationSetting.ResponseTemplate:Casazen.Assistant.Content",
                "Booking.VehiclePlate:Casazen.Guest.Document",
                // NF-81: who an invoice of a booking is made out to (name, fiscal code, VAT number, address, PEC) and, with it, the hash of what was
                // last written to Octorate (E06/F02/US01/T04).
                "BookingInvoiceData.AddressCivic:Casazen.BookingInvoiceData",
                "BookingInvoiceData.AddressStreet:Casazen.BookingInvoiceData",
                "BookingInvoiceData.City:Casazen.BookingInvoiceData",
                "BookingInvoiceData.FiscalCode:Casazen.BookingInvoiceData",
                "BookingInvoiceData.Name:Casazen.BookingInvoiceData",
                "BookingInvoiceData.OctorateBillingHash:Casazen.BookingInvoiceData",
                "BookingInvoiceData.Pec:Casazen.BookingInvoiceData",
                "BookingInvoiceData.PostalCode:Casazen.BookingInvoiceData",
                "BookingInvoiceData.VatNumber:Casazen.BookingInvoiceData",
                // E23/F01/US01/T01 (NF-122): the raw token of a check-in link whose delivery waits (only the hash of a link is stored otherwise).
                "CheckInLinkDelivery.LinkToken:Casazen.CheckInLink.Token",
                "DocumentImage.BackImageUrl:Casazen.DocumentImage",
                "DocumentImage.FrontImageUrl:Casazen.DocumentImage",
                // NF-83: the recipient snapshot of a fiscal document and its FatturaPA XML (E06/F01/US02/T01), and the snapshot of its issuer (E11/F01/US01/T01); FIX/BE-TD17-JOB-EXIT: the free
                // note of the closing of a replacement draft of a rejected TD17 as «non più dovuta».
                "FiscalDocument.ClosedNotDueNote:Casazen.FiscalDocument",
                "FiscalDocument.FatturaPaXml:Casazen.FiscalDocument",
                "FiscalDocument.IssuerSnapshot:Casazen.FiscalDocument",
                "FiscalDocument.RecipientAddressCivic:Casazen.FiscalDocument",
                "FiscalDocument.RecipientAddressStreet:Casazen.FiscalDocument",
                "FiscalDocument.RecipientCity:Casazen.FiscalDocument",
                "FiscalDocument.RecipientCountryCode:Casazen.FiscalDocument",
                "FiscalDocument.RecipientFiscalCode:Casazen.FiscalDocument",
                "FiscalDocument.RecipientName:Casazen.FiscalDocument",
                "FiscalDocument.RecipientPec:Casazen.FiscalDocument",
                "FiscalDocument.RecipientPostalCode:Casazen.FiscalDocument",
                "FiscalDocument.RecipientProvince:Casazen.FiscalDocument",
                "FiscalDocument.RecipientSdiCode:Casazen.FiscalDocument",
                "FiscalDocument.RecipientVatNumber:Casazen.FiscalDocument",
                "Guest.DocumentIssuingCountry:Casazen.Guest.Document",
                "Guest.DocumentNumber:Casazen.Guest.Document",
                "GuestContact.DisplayName:Casazen.GuestContact",
                "GuestContact.Value:Casazen.GuestContact",
                "LeaseContract.DraftData:Casazen.LeaseContract.Draft",
                "LeaseContract.OpenapiRegistrationData:Casazen.LeaseContract.OpenapiRegistration",
                "LeaseContract.TransitoryDocumentation:Casazen.LeaseContract.TransitoryNeed",
                "LeaseContract.TransitoryNeed:Casazen.LeaseContract.TransitoryNeed",
                "OctorateConnection.EncryptedAccessToken:Casazen.Octorate.OAuth",
                "OctorateConnection.EncryptedRefreshToken:Casazen.Octorate.OAuth",
                "OctoratePreparation.EncryptedRefreshToken:Casazen.Octorate.Preparation",
                // E11/F01/US01/T01: the IBAN and the QR-IBAN of the issuer of an organization; E17/F01/US01/T01: the personal data of an issuer (an owner who
                // issues is often a private person): legal name, fiscal code, VAT number, street, house number, postal code, city and PEC.
                "OrgFiscalIssuerProfile.AddressCivic:Casazen.OrgFiscalIssuerProfile",
                "OrgFiscalIssuerProfile.AddressStreet:Casazen.OrgFiscalIssuerProfile",
                "OrgFiscalIssuerProfile.City:Casazen.OrgFiscalIssuerProfile",
                "OrgFiscalIssuerProfile.FiscalCode:Casazen.OrgFiscalIssuerProfile",
                "OrgFiscalIssuerProfile.Iban:Casazen.OrgFiscalIssuerProfile",
                "OrgFiscalIssuerProfile.IssuerPec:Casazen.OrgFiscalIssuerProfile",
                "OrgFiscalIssuerProfile.LegalName:Casazen.OrgFiscalIssuerProfile",
                "OrgFiscalIssuerProfile.PostalCode:Casazen.OrgFiscalIssuerProfile",
                "OrgFiscalIssuerProfile.QrIban:Casazen.OrgFiscalIssuerProfile",
                "OrgFiscalIssuerProfile.VatNumber:Casazen.OrgFiscalIssuerProfile",
                "OtaIntegration.ApiKey:Casazen.OtaIntegration.Secrets",
                "OtaIntegration.ApiSecret:Casazen.OtaIntegration.Secrets",
                "OtaMessage.Text:Casazen.Message.Content",
                "OtaReview.GuestReviewText:Casazen.Review.Content",
                "OtaReview.HostReplyText:Casazen.Review.Content",
                "OtaReview.HostReviewJson:Casazen.Review.Content",
                "OwnerConversation.Subject:Casazen.OwnerPortal.Message",
                "OwnerFiscalProfile.BankAccountReference:Casazen.OwnerFiscalProfile",
                "OwnerFiscalProfile.FiscalCode:Casazen.OwnerFiscalProfile",
                "OwnerFiscalProfile.ForeignTaxId:Casazen.OwnerFiscalProfile",
                "OwnerFiscalProfile.LegalAddress:Casazen.OwnerFiscalProfile",
                "OwnerFiscalProfile.LegalName:Casazen.OwnerFiscalProfile",
                "OwnerFiscalProfile.VatNumber:Casazen.OwnerFiscalProfile",
                "OwnerMessage.AttachmentFileName:Casazen.OwnerPortal.Message",
                "OwnerMessage.Body:Casazen.OwnerPortal.Message",
                "OwnerPortalContact.Email:Casazen.OwnerPortal.Contact",
                "OwnerPortalContact.FirstName:Casazen.OwnerPortal.Contact",
                "OwnerPortalContact.LastName:Casazen.OwnerPortal.Contact",
                "Party.ContactPhone:Casazen.LeaseParty",
                "Party.Residence:Casazen.LeaseParty",
                "PlatformAiSettings.ApiKey:Casazen.PlatformAiSettings",
                "PropertyAccessCode.EncryptedCode:Casazen.PropertyAccessCode",
                "PropertyActivationCredential.Password:Casazen.PropertyActivation.Credential",
                "PropertyActivationCredential.Username:Casazen.PropertyActivation.Credential",
                "PropertyActivationCredential.WebServiceKey:Casazen.PropertyActivation.Credential",
                "PropertyActivationCredentialAssignment.UnitCode:Casazen.PropertyActivation.Assignment",
                "PropertyICalFeed.ImportUrl:Casazen.PropertyICalFeed.ImportUrl",
                // E20/F03/US01/T01: the regional ISTAT credentials the host enters (write-only, opened only through the support vault).
                "PropertyIstatCredentials.Password:Casazen.PropertyIstatCredentials",
                "PropertyIstatCredentials.StructureCode:Casazen.PropertyIstatCredentials",
                "PropertyIstatCredentials.Username:Casazen.PropertyIstatCredentials",
                "PropertyKnowledgeEntry.Text:Casazen.Assistant.Content",
                // ADR-012: property owner financial data (IBAN and fiscal code), and the BIC and the VAT number of the owner profile (E10/F02/US02/T01).
                "PropertyOwner.Bic:Casazen.PropertyOwner.Financial",
                "PropertyOwner.BillingCity:Casazen.PropertyOwner.Financial",
                "PropertyOwner.BillingCivic:Casazen.PropertyOwner.Financial",
                "PropertyOwner.BillingCountry:Casazen.PropertyOwner.Financial",
                "PropertyOwner.BillingPostalCode:Casazen.PropertyOwner.Financial",
                "PropertyOwner.BillingProvince:Casazen.PropertyOwner.Financial",
                "PropertyOwner.BillingStreet:Casazen.PropertyOwner.Financial",
                "PropertyOwner.FiscalCode:Casazen.PropertyOwner.Financial",
                "PropertyOwner.Iban:Casazen.PropertyOwner.Financial",
                "PropertyOwner.VatNumber:Casazen.PropertyOwner.Financial",
                "PropertyQuesturaCredentials.Password:Casazen.PropertyQuesturaCredentials",
                "PropertyQuesturaCredentials.Username:Casazen.PropertyQuesturaCredentials",
                "PropertyQuesturaCredentials.WsKey:Casazen.PropertyQuesturaCredentials",
                "ScheduledMessage.TextOverride:Casazen.Message.Content",
                "ServiceCustomer.Email:Casazen.ServiceCustomer",
                "ServiceCustomer.FullName:Casazen.ServiceCustomer",
                "ServiceCustomer.Phone:Casazen.ServiceCustomer",
                "ServicePaymentDispute.AdminNote:Casazen.ServicePaymentDispute",
                "ServicePaymentDispute.CustomerText:Casazen.ServicePaymentDispute",
                "ServicePaymentDispute.SupplierReply:Casazen.ServicePaymentDispute",
                "ServiceRequest.LocationAccessNotes:Casazen.ServiceRequest.Location",
                "ServiceRequest.LocationAddress:Casazen.ServiceRequest.Location",
                "ServiceRequest.LocationFloor:Casazen.ServiceRequest.Location",
                "ServiceRequestMessage.Body:Casazen.ServiceRequestMessage",
                "ShowcaseBookingHold.Payload:Casazen.ServiceCustomer",
                "ShowcaseInquiry.Payload:Casazen.ServiceCustomer",
                "StayGuest.DocumentIssuePlaceName:Casazen.Guest.Document",
                "StayGuest.DocumentNumber:Casazen.Guest.Document",
                "StayGuest.ResidenceCityName:Casazen.Guest.Document",
                "StayGuest.ResidencePostalCode:Casazen.Guest.Document",
                "StayGuest.ResidenceStreet:Casazen.Guest.Document",
                "StayGuest.TaxExemptionCode:Casazen.Guest.Document",
                "StayTouristTaxGuest.ExemptionCode:Casazen.Guest.Document",
                "StorefrontChatMessage.Text:Casazen.StorefrontChat.Text",
                "StorefrontChatThread.VisitorEmail:Casazen.StorefrontChat.Contact",
                "SupportVaultSecret.ExtraCode:Casazen.SupportVault",
                "SupportVaultSecret.Password:Casazen.SupportVault",
                "SupportVaultSecret.Username:Casazen.SupportVault",
                "TaxReportingDataset.PayloadJson:Casazen.TaxReportingDataset",
                "WireBatch.DebitAccount:Casazen.WireBatch.File",
                "WireBatch.XmlContent:Casazen.WireBatch.File",
                "WizardDraft.EncryptedFieldsJson:Casazen.WizardDraft.PII",
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
