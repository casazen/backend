using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services.LeaseContracts;
using Casazen.Tests.Unit.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services.LeaseContracts;

public sealed partial class LeaseTemplateDraftsTests
{
    private const string AutofillVersion = "2026-11-autocompilato-v2";

    [Theory]
    [InlineData(FiscalRegime.CedolareSecca)]
    [InlineData(FiscalRegime.RegimeOrdinario)]
    [InlineData(FiscalRegime.CanoneConcordato)]
    public async Task Autofill_FinalPdf_UsesEveryPartyAndPropertyDataWithoutManualFields(FiscalRegime regime)
    {
        var lease = BuildAutofillLease(regime);
        var sut = AutofillService(regime);

        var pdf = await sut.GeneratePdfAsync(lease);
        var text = PdfTestReader.Text(pdf);

        Assert.DoesNotContain("___", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DATO MANCANTE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cancellare", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("piano 0", text, StringComparison.Ordinal);
        Assert.Contains("2 camere da letto e 1 bagno", text, StringComparison.Ordinal);
        Assert.Contains("123 metri quadrati", text, StringComparison.Ordinal);
        foreach (var party in lease.Parties)
        {
            Assert.Contains($"{party.FirstName} {party.LastName}: {party.Residence}", text, StringComparison.Ordinal);
            Assert.Contains($"{party.FirstName} {party.LastName}: {party.ContactEmail}", text, StringComparison.Ordinal);
            if (party.Role == PartyRole.Tenant)
                Assert.Contains($"{party.FirstName} {party.LastName}: {party.Citizenship}", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("residence", LeaseContractPlaceholders.TenantResidences)]
    [InlineData("citizenship", LeaseContractPlaceholders.TenantCitizenships)]
    [InlineData("email", LeaseContractPlaceholders.TenantEmails)]
    [InlineData("floor", LeaseContractPlaceholders.PropertyFloor)]
    [InlineData("area", LeaseContractPlaceholders.PropertyArea)]
    public async Task Autofill_MissingData_BlocksFinalAndMarksPreview(string field, string placeholder)
    {
        var lease = BuildAutofillLease(FiscalRegime.RegimeOrdinario);
        // Only the second tenant is incomplete: the first one's data must not hide that omission.
        var tenant = lease.Parties.Single(p => p.Role == PartyRole.Tenant && p.Position == 1);
        switch (field)
        {
            case "residence": tenant.Residence = null; break;
            case "citizenship": tenant.Citizenship = " "; break;
            case "email": tenant.ContactEmail = ""; break;
            case "floor": lease.Property.FloorLevel = null; break;
            case "area": lease.Property.AreaSqm = null; break;
        }
        var sut = AutofillService(FiscalRegime.RegimeOrdinario);

        Assert.Equal(LeaseContractTemplateService.DataMissingCode, sut.GetFinalContractBlocker(lease));
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => sut.GeneratePdfAsync(lease));
        Assert.Equal(LeaseContractTemplateService.DataMissingCode, ex.Code);
        var preview = PdfTestReader.Text(await sut.GeneratePreviewPdfAsync(lease));
        Assert.Contains($"[DATO MANCANTE: {placeholder}]", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("___", preview, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FiscalRegime.CedolareSecca)]
    [InlineData(FiscalRegime.RegimeOrdinario)]
    [InlineData(FiscalRegime.CanoneConcordato)]
    public void Autofill_NewVersion_StillRequiresItsOwnApproval(FiscalRegime regime)
    {
        var state = LeaseContractTemplateCatalog.Load(
            LeaseTemplateVariants.Of(regime), OptionsFor(regime, AutofillVersion), Path.GetTempPath());
        Assert.Equal(LeaseContractTemplateStatus.NotApproved, state.Status);
        Assert.Empty(state.Problems);
        Assert.False(state.IsApproved);
    }

    [Theory]
    [InlineData(LeaseTaxRegime.CedolareSecca, "cedolare secca")]
    [InlineData(LeaseTaxRegime.Ordinario, "regime ordinario")]
    public async Task Autofill_Concordato_PrintsTheSelectedTaxRegime(LeaseTaxRegime taxRegime, string expected)
    {
        var lease = BuildAutofillLease(FiscalRegime.CanoneConcordato);
        lease.TaxRegime = taxRegime;
        var text = PdfTestReader.Text(await AutofillService(FiscalRegime.CanoneConcordato).GeneratePdfAsync(lease));
        Assert.Contains($"Il locatore dichiara di applicare il seguente regime fiscale: {expected}.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Autofill_Concordato_DoesNotGuessAnUnselectedTaxRegime()
    {
        var lease = BuildAutofillLease(FiscalRegime.CanoneConcordato);
        lease.TaxRegime = null;
        Assert.Equal(LeaseContractTemplateService.DataMissingCode,
            AutofillService(FiscalRegime.CanoneConcordato).GetFinalContractBlocker(lease));
    }

    private static Casazen.Core.Entities.LeaseContract BuildAutofillLease(FiscalRegime regime)
    {
        var lease = BuildLease(regime);
        lease.TaxRegime ??= LeaseTaxRegime.CedolareSecca;
        lease.Property.FloorLevel = 0;
        lease.Property.AreaSqm = 123;
        lease.Property.Bedrooms = 2;
        lease.Property.Bathrooms = 1;
        foreach (var party in lease.Parties)
        {
            party.Residence = $"Via {party.LastName} {party.Position + 1}, Milano";
            party.Citizenship = party.Position == 0 ? "IT" : "FR";
            party.ContactEmail = $"{party.FirstName.ToLowerInvariant()}@example.test";
        }
        return lease;
    }

    private static LeaseContractTemplateService AutofillService(FiscalRegime regime)
    {
        var options = OptionsFor(regime, AutofillVersion);
        var variant = options.Variants[LeaseTemplateVariants.Of(regime).ToString()];
        variant.Approved = true;
        variant.ApprovalReference = "Unit test only";
        return new LeaseContractTemplateService(
            LeaseTemplateTestFiles.Catalog(options), new MigraDocPdfDocumentRenderer(),
            NullLogger<LeaseContractTemplateService>.Instance);
    }
}
