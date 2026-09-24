using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services.LeaseContracts;
using Casazen.Tests.Unit.Services.LeaseContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LT-03 (A7-03): the final contract exists only with a complete, approved template; the preview is always marked BOZZA
/// and every datum comes from the lease.
/// </summary>
public sealed class LeaseContractTemplateServiceTests : IDisposable
{
    private readonly LeaseTemplateTestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Theory]
    [InlineData(FiscalRegime.CedolareSecca)]
    [InlineData(FiscalRegime.RegimeOrdinario)]
    [InlineData(FiscalRegime.CanoneConcordato)]
    public async Task GeneratePdfAsync_DefaultOptions_ThrowsTemplateNotApproved(FiscalRegime regime)
    {
        var sut = CreateSut(new LeaseTemplateOptions());

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => sut.GeneratePdfAsync(BuildLease(regime)));

        Assert.Equal(LeaseContractTemplateService.TemplateNotApprovedCode, ex.Code);
        Assert.Equal("LeaseContractTemplateNotApproved", ex.MessageKey);
    }

    [Fact]
    public void CommittedAppsettings_EveryLeaseTemplateVariant_IsNotApproved()
    {
        var webProject = FindWebProjectDirectory();
        var files = Directory.GetFiles(webProject, "appsettings*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var options = new LeaseTemplateOptions();
            new ConfigurationBuilder().AddJsonFile(file).Build()
                .GetSection(LeaseTemplateOptions.SectionName)
                .Bind(options);

            Assert.All(options.Variants, v => Assert.False(v.Value.Approved, $"{Path.GetFileName(file)}: {v.Key} is approved"));
            Assert.DoesNotContain(options.Variants.Values, v => LeaseTemplateApproval.IsDevStub(v.VersionId));
        }
    }

    [Fact]
    public async Task GeneratePdfAsync_DevStubDeclaredApproved_ThrowsTemplateNotApproved()
    {
        var sut = CreateSut(_files.Options(FiscalRegime.CedolareSecca, new LeaseTemplateVariantOptions
        {
            VersionId = LeaseTemplateVariantOptions.DevStubVersionId,
            Approved = true,
            ApprovalReference = "none",
        }));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            sut.GeneratePdfAsync(BuildLease(FiscalRegime.CedolareSecca)));

        Assert.Equal(LeaseContractTemplateService.TemplateNotApprovedCode, ex.Code);
    }

    [Fact]
    public async Task GeneratePdfAsync_CompleteTemplateWithoutApproval_ThrowsTemplateNotApproved()
    {
        _files.Write(FiscalRegime.CedolareSecca, "v1", LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CedolareSecca));
        var sut = CreateSut(_files.Options(FiscalRegime.CedolareSecca, new LeaseTemplateVariantOptions { VersionId = "v1" }));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            sut.GeneratePdfAsync(BuildLease(FiscalRegime.CedolareSecca)));

        Assert.Equal(LeaseContractTemplateService.TemplateNotApprovedCode, ex.Code);
    }

    [Fact]
    public async Task GeneratePdfAsync_ApprovedButIncompleteTemplate_ThrowsTemplateNotApproved()
    {
        _files.Write(
            FiscalRegime.CanoneConcordato,
            LeaseTemplateTestFiles.ApprovedVersion,
            LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CanoneConcordato, omit: [LeaseContractTemplateStructure.Ape]));
        var sut = CreateSut(_files.Options(FiscalRegime.CanoneConcordato, LeaseTemplateTestFiles.Approved()));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            sut.GeneratePdfAsync(BuildLease(FiscalRegime.CanoneConcordato)));

        Assert.Equal(LeaseContractTemplateService.TemplateNotApprovedCode, ex.Code);
    }

    [Fact]
    public async Task GeneratePdfAsync_ApprovedCompleteTemplate_RendersTextsWithLeaseData()
    {
        _files.Write(
            FiscalRegime.CanoneConcordato,
            LeaseTemplateTestFiles.ApprovedVersion,
            LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CanoneConcordato));
        var sut = CreateSut(_files.Options(FiscalRegime.CanoneConcordato, LeaseTemplateTestFiles.Approved()));
        var lease = BuildLease(FiscalRegime.CanoneConcordato);
        lease.Parties.Add(new Party { Role = PartyRole.Landlord, FirstName = "Anna", LastName = "Bianchi", FiscalCode = "BNCNNA82A41F205Z" });

        var text = PdfText(await sut.GeneratePdfAsync(lease));

        Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
        Assert.Contains("Titolo di prova", text, StringComparison.Ordinal);
        Assert.Contains("Mario Rossi", text, StringComparison.Ordinal);
        Assert.Contains("Anna Bianchi", text, StringComparison.Ordinal);
        Assert.Contains("Luigi Verdi", text, StringComparison.Ordinal);
        Assert.Contains("Via Roma 1, 20822 Seveso", text, StringComparison.Ordinal);
        Assert.Contains("3 anni", text, StringComparison.Ordinal);
        Assert.Contains("01/09/2026", text, StringComparison.Ordinal);
        Assert.Contains("31/08/2029", text, StringComparison.Ordinal);
        Assert.Contains("850,00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BOZZA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("3+2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratePdfAsync_ApprovedTemplateUsingDataNotInTheLease_ThrowsDataMissing()
    {
        _files.Write(
            FiscalRegime.RegimeOrdinario,
            LeaseTemplateTestFiles.ApprovedVersion,
            LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.RegimeOrdinario, new Dictionary<string, string>
            {
                [LeaseContractTemplateStructure.Deposit] = "Deposito di prova {{deposito_cauzionale}}.",
                [LeaseContractTemplateStructure.Property] = "Immobile di prova {{immobile_indirizzo}} {{dati_catastali}}.",
            }));
        var sut = CreateSut(_files.Options(FiscalRegime.RegimeOrdinario, LeaseTemplateTestFiles.Approved()));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            sut.GeneratePdfAsync(BuildLease(FiscalRegime.RegimeOrdinario)));

        Assert.Equal(LeaseContractTemplateService.DataMissingCode, ex.Code);
        var missing = Assert.Single(ex.MessageArgs).ToString()!;
        Assert.Contains(LeaseContractPlaceholders.SecurityDeposit, missing, StringComparison.Ordinal);
        Assert.Contains(LeaseContractPlaceholders.CadastralData, missing, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFinalContractBlocker_DefaultOptionsApprovedOrMissingData_SameCodeAsGeneratePdf()
    {
        // LT-02: the signature panel shows why the final contract cannot be downloaded, without throwing.
        Assert.Equal(
            LeaseContractTemplateService.TemplateNotApprovedCode,
            CreateSut(new LeaseTemplateOptions()).GetFinalContractBlocker(BuildLease(FiscalRegime.CedolareSecca)));

        _files.Write(
            FiscalRegime.CanoneConcordato,
            LeaseTemplateTestFiles.ApprovedVersion,
            LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CanoneConcordato));
        Assert.Null(CreateSut(_files.Options(FiscalRegime.CanoneConcordato, LeaseTemplateTestFiles.Approved()))
            .GetFinalContractBlocker(BuildLease(FiscalRegime.CanoneConcordato)));

        _files.Write(
            FiscalRegime.RegimeOrdinario,
            LeaseTemplateTestFiles.ApprovedVersion,
            LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.RegimeOrdinario, new Dictionary<string, string>
            {
                [LeaseContractTemplateStructure.Deposit] = "Deposito di prova {{deposito_cauzionale}}.",
            }));
        Assert.Equal(
            LeaseContractTemplateService.DataMissingCode,
            CreateSut(_files.Options(FiscalRegime.RegimeOrdinario, LeaseTemplateTestFiles.Approved()))
                .GetFinalContractBlocker(BuildLease(FiscalRegime.RegimeOrdinario)));
    }

    [Theory]
    [InlineData(FiscalRegime.CedolareSecca)]
    [InlineData(FiscalRegime.RegimeOrdinario)]
    [InlineData(FiscalRegime.CanoneConcordato)]
    public async Task GeneratePreviewPdfAsync_NoTemplate_IsMarkedBozzaWithComputedDataAndMissingClauses(FiscalRegime regime)
    {
        var sut = CreateSut(new LeaseTemplateOptions());
        var lease = BuildLease(regime);
        lease.EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc);

        var text = PdfText(await sut.GeneratePreviewPdfAsync(lease));

        Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
        Assert.Contains("(BOZZA - template non approvato)", text, StringComparison.Ordinal);
        Assert.Contains("modello assente", text, StringComparison.Ordinal);
        Assert.Contains(LeaseContractDocument.MissingClauseMarker, text, StringComparison.Ordinal);
        Assert.Contains("Mario Rossi", text, StringComparison.Ordinal);
        Assert.Contains("Luigi Verdi", text, StringComparison.Ordinal);
        Assert.Contains("4 anni", text, StringComparison.Ordinal);
        Assert.Contains("[DATO MANCANTE: dati_catastali]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("3+2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4+4", text, StringComparison.Ordinal);
        foreach (var section in LeaseContractTemplateStructure.RequiredSections(regime))
            Assert.Contains(section.Id, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratePreviewPdfAsync_IncompleteTemplate_ShowsProvidedTextsAndMarksTheRest()
    {
        _files.Write(
            FiscalRegime.CedolareSecca,
            "v2",
            LeaseTemplateTestFiles.CompleteTemplate(
                FiscalRegime.CedolareSecca,
                new Dictionary<string, string> { [LeaseContractTemplateStructure.RenewalAndNotice] = string.Empty },
                omit: [LeaseContractTemplateStructure.Ape]));
        var sut = CreateSut(_files.Options(FiscalRegime.CedolareSecca, new LeaseTemplateVariantOptions { VersionId = "v2" }));

        var text = PdfText(await sut.GeneratePreviewPdfAsync(BuildLease(FiscalRegime.CedolareSecca)));

        Assert.Contains("(BOZZA - template non approvato)", text, StringComparison.Ordinal);
        Assert.Contains("modello incompleto", text, StringComparison.Ordinal);
        Assert.Contains("Sezioni senza testo: rinnovo_disdetta, ape", text, StringComparison.Ordinal);
        Assert.Contains("Testo di prova parti. Mario Rossi", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratePreviewPdfAsync_ApprovedTemplate_IsMarkedAsPreviewNotValidForSignature()
    {
        _files.Write(
            FiscalRegime.CedolareSecca,
            LeaseTemplateTestFiles.ApprovedVersion,
            LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CedolareSecca));
        var sut = CreateSut(_files.Options(FiscalRegime.CedolareSecca, LeaseTemplateTestFiles.Approved()));

        var text = PdfText(await sut.GeneratePreviewPdfAsync(BuildLease(FiscalRegime.CedolareSecca)));

        Assert.Contains(LeaseContractDocument.ApprovedPreviewMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain("BOZZA", text, StringComparison.Ordinal);
    }

    private static LeaseContractTemplateService CreateSut(LeaseTemplateOptions options) =>
        new(LeaseTemplateTestFiles.Catalog(options), NullLogger<LeaseContractTemplateService>.Instance);

    private static string PdfText(byte[] pdf) => Encoding.ASCII.GetString(pdf);

    private static string FindWebProjectDirectory()
    {
        for (var directory = new DirectoryInfo(System.AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Casazen.Web");
            if (File.Exists(Path.Combine(candidate, "Casazen.Web.csproj")))
                return candidate;
        }

        throw new DirectoryNotFoundException("Casazen.Web project not found above the test output folder.");
    }

    private static LeaseContract BuildLease(FiscalRegime regime) => new()
    {
        Id = Guid.NewGuid(),
        FiscalRegime = regime,
        MonthlyRent = 850m,
        StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2029, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        Property = new Property
        {
            Name = "Condominio Il Parco",
            Address = "Via Roma 1",
            PostalCode = "20822",
            City = "Seveso",
        },
        Parties =
        [
            new Party { Role = PartyRole.Landlord, FirstName = "Mario", LastName = "Rossi", FiscalCode = "RSSMRA80A01H501U" },
            new Party { Role = PartyRole.Tenant, FirstName = "Luigi", LastName = "Verdi", FiscalCode = "VRDLGU85B02F205X" },
        ],
    };
}
