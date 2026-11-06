using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>FIS-01, NF-81, E06/F02/US01/T02: how a VAT number, a recipient code and a PEC are written, and the one rule table of the recipient.</summary>
public class InvoiceDataFormatsTests
{
    [Theory]
    [InlineData("IT12345678903", true)]
    [InlineData("12345678903", true)]
    [InlineData("it 123.456.789-03", true)]
    [InlineData("DE123456789", true)]
    [InlineData("EL123456789", true)]
    [InlineData("FR12345678901", true)]
    [InlineData("IT12345678900", false)]
    [InlineData("1234567890", false)]
    [InlineData("IT1234567890A", false)]
    [InlineData("123456789", false)]
    [InlineData("ITALIA", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidVatNumber_ItalianWithItsCheckDigit_OtherCountriesByShape(string? vat, bool expected) =>
        Assert.Equal(expected, InvoiceDataFormats.IsValidVatNumber(vat));

    [Theory]
    [InlineData("ABC1234", true)]
    [InlineData("0000000", true)]
    [InlineData("abc1234", false)]
    [InlineData("ABC123", false)]
    [InlineData("ABC12345", false)]
    [InlineData("ABC-123", false)]
    [InlineData(null, false)]
    public void IsValidRecipientCode_SevenUpperCaseLettersOrDigits(string? code, bool expected) =>
        Assert.Equal(expected, InvoiceDataFormats.IsValidRecipientCode(code));

    [Theory]
    [InlineData("fatture@pec.example.com", true)]
    [InlineData("mario.rossi+fatture@pec.it", true)]
    [InlineData("fatture.pec.it", false)]
    [InlineData("a@b", false)]
    [InlineData("a b@pec.it", false)]
    [InlineData(".a@pec.it", false)]
    [InlineData("a..b@pec.it", false)]
    [InlineData("", false)]
    public void IsValidPec_AnEmailAddress(string pec, bool expected) =>
        Assert.Equal(expected, InvoiceDataFormats.IsValidPec(pec));

    [Fact]
    public void MissingIdentification_OneRuleTable_CompanyNeedsVatOrFiscalCode_PrivateInItalyNeedsFiscalCode_AbroadNeedsNeither()
    {
        Assert.Equal("vatNumber", InvoiceDataFormats.MissingIdentification(InvoiceRecipientKind.Company, "IT", null, null));
        Assert.Null(InvoiceDataFormats.MissingIdentification(InvoiceRecipientKind.Company, "IT", null, "IT12345678903"));
        Assert.Null(InvoiceDataFormats.MissingIdentification(InvoiceRecipientKind.Company, "IT", "12345678903", null));
        Assert.Equal("fiscalCode", InvoiceDataFormats.MissingIdentification(InvoiceRecipientKind.Guest, "IT", null, null));
        Assert.Equal("fiscalCode", InvoiceDataFormats.MissingIdentification(InvoiceRecipientKind.Guest, null, null, "IT12345678903"));
        Assert.Null(InvoiceDataFormats.MissingIdentification(InvoiceRecipientKind.Guest, "IT", "RSSMRA80A01H501U", null));
        Assert.Null(InvoiceDataFormats.MissingIdentification(InvoiceRecipientKind.Guest, "DE", null, null));
        Assert.Null(InvoiceDataFormats.MissingIdentification(InvoiceRecipientKind.Company, "FR", null, null));
    }

    [Fact]
    public void Normalize_WritesTheTextTheWayItIsStored_AndKeepsWhatWasNotSent()
    {
        var italian = InvoiceDataFormats.Normalize(
            new SaveInvoiceDataCommand
            {
                Name = "  Mario   Rossi ",
                FiscalCode = "rssmra80a01 h501u",
                VatNumber = "12345678903",
                SdiRecipientCode = " abc1234 ",
                Pec = " Fatture@PEC.example.com ",
                Province = "mi",
                PostalCode = "20 121",
            },
            "IT");

        Assert.Equal("Mario Rossi", italian.Name);
        Assert.Equal("RSSMRA80A01H501U", italian.FiscalCode);
        Assert.Equal("IT12345678903", italian.VatNumber);
        Assert.Equal("ABC1234", italian.SdiRecipientCode);
        Assert.Equal("fatture@pec.example.com", italian.Pec);
        Assert.Equal("MI", italian.Province);
        Assert.Equal("20121", italian.PostalCode);
        Assert.Null(italian.City);

        // A number without letters takes the prefix of the country of the recipient (EL for Greece); a postal code abroad keeps its space.
        Assert.Equal("DE123456789", InvoiceDataFormats.Normalize(new SaveInvoiceDataCommand { VatNumber = "123456789" }, "DE").VatNumber);
        Assert.Equal("EL123456789", InvoiceDataFormats.Normalize(new SaveInvoiceDataCommand { VatNumber = "123456789" }, "GR").VatNumber);
        Assert.Equal("SW1A 1AA", InvoiceDataFormats.Normalize(new SaveInvoiceDataCommand { PostalCode = "sw1a  1aa" }, "GB").PostalCode);
    }
}
