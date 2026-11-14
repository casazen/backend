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
                // ADR-032-R3: the text of an item of the approval queue (a draft of a message to a guest, a mailing) and the host's edit of it: the same purpose as the
                // words of the conversations (E05/F04/US01/T01).
                "ApprovalQueueItem.ContentText:Casazen.Message.Content",
                "ApprovalQueueItem.EditedContentText:Casazen.Message.Content",
                // NF-66, PO-09: the free text a host writes for the assistant: what a lesson proposes to learn and the response template of a situation (E05/F05/US01/T01).
                "AssistantLesson.ProposedText:Casazen.Assistant.Content",
                "AssistantSituationSetting.ResponseTemplate:Casazen.Assistant.Content",
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
                "DocumentImage.BackImageUrl:Casazen.DocumentImage",
                "DocumentImage.FrontImageUrl:Casazen.DocumentImage",
                // NF-83: the recipient snapshot of a fiscal document and its FatturaPA XML (E06/F01/US02/T01), and the snapshot of its issuer (E11/F01/US01/T01).
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
                // NF-89: the value (e-mail address or phone number) and the display name of a CRM contact.
                "GuestContact.DisplayName:Casazen.GuestContact",
                "GuestContact.Value:Casazen.GuestContact",
                // LR-12: the values a landlord typed in the wizard and kept in the draft, and the residence of a party of a lease.
                "LeaseContract.DraftData:Casazen.LeaseContract.Draft",
                "LeaseContract.OpenapiRegistrationData:Casazen.LeaseContract.OpenapiRegistration",
                // TR: the need of a transitory lease and the documentation that proves it.
                "LeaseContract.TransitoryDocumentation:Casazen.LeaseContract.TransitoryNeed",
                "LeaseContract.TransitoryNeed:Casazen.LeaseContract.TransitoryNeed",
                // ADR-023-R1: the OAuth tokens of the Octorate connection of an organization.
                "OctorateConnection.EncryptedAccessToken:Casazen.Octorate.OAuth",
                "OctorateConnection.EncryptedRefreshToken:Casazen.Octorate.OAuth",
                // E11/F01/US01/T01: the IBAN and the QR-IBAN of the issuer of an organization.
                "OrgFiscalIssuerProfile.Iban:Casazen.OrgFiscalIssuerProfile",
                "OrgFiscalIssuerProfile.QrIban:Casazen.OrgFiscalIssuerProfile",
                "OtaIntegration.ApiKey:Casazen.OtaIntegration.Secrets",
                "OtaIntegration.ApiSecret:Casazen.OtaIntegration.Secrets",
                // ADR-032-R3: the words of the conversations of the guests with the channels, copied from the channel manager.
                "OtaMessage.Text:Casazen.Message.Content",
                // NF-111: the subject of a conversation of the owner inbox (E07/F07/US01/T02).
                "OwnerConversation.Subject:Casazen.OwnerPortal.Message",
                // ADR-012, NF-109: the identifiers, the address and the bank account of the fiscal profile of an owner (E06/F05/US02/T01).
                "OwnerFiscalProfile.BankAccountReference:Casazen.OwnerFiscalProfile",
                "OwnerFiscalProfile.FiscalCode:Casazen.OwnerFiscalProfile",
                "OwnerFiscalProfile.ForeignTaxId:Casazen.OwnerFiscalProfile",
                "OwnerFiscalProfile.LegalAddress:Casazen.OwnerFiscalProfile",
                "OwnerFiscalProfile.LegalName:Casazen.OwnerFiscalProfile",
                "OwnerFiscalProfile.VatNumber:Casazen.OwnerFiscalProfile",
                // NF-111: the words of the owner inbox, the name of the file of a message, and the address and name of the delegated contacts of an owner (E07/F07/US01/T02).
                "OwnerMessage.AttachmentFileName:Casazen.OwnerPortal.Message",
                "OwnerMessage.Body:Casazen.OwnerPortal.Message",
                "OwnerPortalContact.Email:Casazen.OwnerPortal.Contact",
                "OwnerPortalContact.FirstName:Casazen.OwnerPortal.Contact",
                "OwnerPortalContact.LastName:Casazen.OwnerPortal.Contact",
                "Party.ContactPhone:Casazen.LeaseParty",
                "Party.Residence:Casazen.LeaseParty",
                "PlatformAiSettings.ApiKey:Casazen.PlatformAiSettings",
                // NF-13: encrypted property access codes (gate, lockbox, smart lock PIN).
                "PropertyAccessCode.EncryptedCode:Casazen.PropertyAccessCode",
                "PropertyICalFeed.ImportUrl:Casazen.PropertyICalFeed.ImportUrl",
                // NF-66, PO-09: an entry of the knowledge base of the assistant (a fact, a rule, the answer of a FAQ).
                "PropertyKnowledgeEntry.Text:Casazen.Assistant.Content",
                // ADR-012: property owner financial data (IBAN and fiscal code), and the BIC and the VAT number of the owner profile (E10/F02/US02/T01).
                "PropertyOwner.Bic:Casazen.PropertyOwner.Financial",
                // E13/F01/US01/T01: the structured billing address of the owner (personal data, the same treatment for a company).
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
                // NF-46: the tourist tax exemption the host chose (its reason can reveal a health condition).
                "StayGuest.TaxExemptionCode:Casazen.Guest.Document",
                // NF-46: the same reason on the confirmed tax of the stay (kept 10 years, cleared with the guest's data).
                "StayTouristTaxGuest.ExemptionCode:Casazen.Guest.Document",
                // NF-25, NF-113: the wire file of the agency (the IBAN, the name and the address of every owner) and the IBAN of the agency that pays (E16/F01/US01/T01).
                "WireBatch.DebitAccount:Casazen.WireBatch.File",
                "WireBatch.XmlContent:Casazen.WireBatch.File",
                // ADR-024: the personal half of a wizard draft (the fields that name a person, split off what the person typed).
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
