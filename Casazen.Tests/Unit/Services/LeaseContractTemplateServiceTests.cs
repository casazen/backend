using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services.LeaseContracts;
using Casazen.Tests.Unit.Documents;
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

                Assert.Contains(LeaseContractDocument.DraftMarker, text, StringComparison.Ordinal);
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

        Assert.Contains(LeaseContractDocument.DraftMarker, text, StringComparison.Ordinal);
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

    [Fact]
    public async Task GeneratePreviewPdfAsync_ApprovedTemplate_EveryPageIsWatermarkedAnteprima()
    {
        _files.Write(
            FiscalRegime.CedolareSecca,
            LeaseTemplateTestFiles.ApprovedVersion,
            LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CedolareSecca));
        var sut = CreateSut(_files.Options(FiscalRegime.CedolareSecca, LeaseTemplateTestFiles.Approved()));

        var pages = PdfTestReader.Pages(await sut.GeneratePreviewPdfAsync(BuildLease(FiscalRegime.CedolareSecca)));

        Assert.All(pages, page => Assert.Equal(LeaseContractDocument.ApprovedPreviewWatermark, page.Watermark));
    }

    [Fact]
    public async Task GeneratePdfAsync_ApprovedTemplateOver20000Characters_SpansSeveralA4PagesWithoutLosingText()
    {
        // LT-09 (A7-14): the old writer printed one Letter page and cut the text at 4000 characters.
        const int wordCount = 3200;
        var regime = FiscalRegime.CanoneConcordato;
        var sections = LeaseContractTemplateStructure.RequiredSections(regime);
        var perSection = (wordCount + sections.Count - 1) / sections.Count;
        var texts = new Dictionary<string, string>();
        for (var i = 0; i < sections.Count; i++)
        {
            var words = Enumerable.Range((i * perSection) + 1, perSection).Where(n => n <= wordCount).Select(Token);
            var placeholders = string.Join(" ", sections[i].RequiredPlaceholders.Select(group => $"{{{{{group[0]}}}}}"));
            texts[sections[i].Id] = $"Clausola {sections[i].Id}. {placeholders}\n" + string.Join("\n", words.Chunk(40).Select(c => string.Join(' ', c)));
        }

        var template = LeaseTemplateTestFiles.CompleteTemplate(regime, texts);
        Assert.True(template.Length > 20_000, $"{template.Length} characters");
        _files.Write(regime, LeaseTemplateTestFiles.ApprovedVersion, template);
        var sut = CreateSut(_files.Options(regime, LeaseTemplateTestFiles.Approved()));

        var pdf = await sut.GeneratePdfAsync(BuildLease(regime));

        var pages = PdfTestReader.Pages(pdf);
        Assert.True(pages.Count >= 3, $"{pages.Count} pages");
        Assert.All(pages, page =>
        {
            Assert.Equal(595, Math.Round(page.Width));
            Assert.Equal(842, Math.Round(page.Height));
            Assert.Equal(string.Empty, page.Watermark);
        });
        var tokens = PdfTestReader.BodyWords(pdf).Where(w => w.Length == 6 && w[0] == 'w' && w[1..].All(char.IsAsciiDigit));
        Assert.Equal(Enumerable.Range(1, wordCount).Select(Token), tokens);
        foreach (var section in sections)
            Assert.Contains($"Clausola {section.Id}.", PdfTestReader.Text(pdf), StringComparison.Ordinal);

        static string Token(int n) => $"w{n:D5}";
    }

    [Fact]
    public async Task GeneratePdfAsync_NonLatinNamesAndAccents_KeepsEveryCharacter()
    {
        _files.Write(
            FiscalRegime.CedolareSecca,
            LeaseTemplateTestFiles.ApprovedVersion,
            LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CedolareSecca, new Dictionary<string, string>
            {
                [LeaseContractTemplateStructure.Parties] = "Città, qualità, però: àèìòù ÀÈÌÒÙ. Parti: {{locatori}} e {{conduttori}}.",
            }));
        var sut = CreateSut(_files.Options(FiscalRegime.CedolareSecca, LeaseTemplateTestFiles.Approved()));
        var lease = BuildLease(FiscalRegime.CedolareSecca);
        lease.Parties.Add(new Party { Role = PartyRole.Landlord, FirstName = "Łukasz", LastName = "Čapek" });
        lease.Parties.Add(new Party { Role = PartyRole.Tenant, FirstName = "Ольга", LastName = "Иванова" });
        lease.Parties.Add(new Party { Role = PartyRole.Tenant, FirstName = "Jürgen", LastName = "Straßmüller" });

        var text = PdfText(await sut.GeneratePdfAsync(lease));

        Assert.Contains("Città, qualità, però: àèìòù ÀÈÌÒÙ.", text, StringComparison.Ordinal);
        Assert.Contains("Łukasz Čapek", text, StringComparison.Ordinal);
        Assert.Contains("Ольга Иванова", text, StringComparison.Ordinal);
        Assert.Contains("Jürgen Straßmüller", text, StringComparison.Ordinal);
        Assert.DoesNotContain("?", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratePreviewPdfAsync_TemplateNotApproved_EveryPageIsWatermarkedBozza()
    {
        var longText = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Riga di prova {i} della clausola, abbastanza lunga da occupare la larghezza della pagina."));
        _files.Write(
            FiscalRegime.RegimeOrdinario,
            "v3",
            LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.RegimeOrdinario, new Dictionary<string, string>
            {
                [LeaseContractTemplateStructure.RenewalAndNotice] = longText,
                [LeaseContractTemplateStructure.Deposit] = longText,
            }));
        var sut = CreateSut(_files.Options(FiscalRegime.RegimeOrdinario, new LeaseTemplateVariantOptions { VersionId = "v3" }));

        var pages = PdfTestReader.Pages(await sut.GeneratePreviewPdfAsync(BuildLease(FiscalRegime.RegimeOrdinario)));

        Assert.True(pages.Count > 1, $"{pages.Count} pages");
        Assert.All(pages, page => Assert.Equal(LeaseContractDocument.DraftWatermark, page.Watermark));
        Assert.Contains(LeaseContractDocument.DraftMarker, pages[0].Text, StringComparison.Ordinal);
        Assert.Contains(LeaseContractDocument.DraftMarker, pages[^1].Text, StringComparison.Ordinal);
    }

    private static LeaseContractTemplateService CreateSut(LeaseTemplateOptions options) =>
        new(LeaseTemplateTestFiles.Catalog(options), new MigraDocPdfDocumentRenderer(), NullLogger<LeaseContractTemplateService>.Instance);

    private static string PdfText(byte[] pdf)
    {
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        return PdfTestReader.Text(pdf);
    }

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
