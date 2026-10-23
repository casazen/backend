using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services.LeaseContracts;
using Casazen.Tests.Unit.Documents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services.LeaseContracts;

/// <summary>
/// LG-02 (decision D27): the draft contract templates committed in <c>Casazen.Web/LeaseTemplates</c> are complete, use
/// only the placeholders the product knows, are not approved and are not switched on by any committed configuration
/// (that is an act of the product owner after the lawyer's review: runbook section "Bozze 2026-11"). The preview of a
/// lease shows them whole, marked BOZZA. The two drafts with no pipeline (transitorio, studenti) are only reference files.
/// </summary>
public sealed partial class LeaseTemplateDraftsTests
{
    private const string DraftVersion = "2026-11-bozza-v1";

    /// <summary>Sections of the drafts that have no article number (the others are "Art. N · Title").</summary>
    private static readonly string[] UnnumberedSections = ["parti", "premesse", "allegati", "clausole_approvate", "firme"];

    /// <summary>The clauses the drafts ask the parties to approve in writing (arts. 1341-1342 c.c.), as in the 2026-10-08 drafts.</summary>
    private static readonly string[] ApprovedInWriting =
    [
        LeaseContractTemplateStructure.Term,
        LeaseContractTemplateStructure.RenewalAndNotice,
        LeaseContractTemplateStructure.TenantWithdrawal,
        LeaseContractTemplateStructure.Deposit,
        "uso",
        "innovazioni",
        "accesso",
    ];

    [Theory]
    [InlineData(FiscalRegime.CedolareSecca)]
    [InlineData(FiscalRegime.RegimeOrdinario)]
    [InlineData(FiscalRegime.CanoneConcordato)]
    public void Load_DraftTemplate_IsCompleteAndNotApproved(FiscalRegime regime)
    {
        var state = LoadDraft(regime);

        Assert.Equal(LeaseContractTemplateStatus.NotApproved, state.Status);
        Assert.False(state.IsApproved);
        Assert.False(state.DeclaredApproved);
        Assert.Equal(DraftVersion, state.VersionId);
        Assert.Empty(state.MissingSections);
        Assert.Empty(state.Issues);
        Assert.Empty(state.InvalidApprovalReasons);
        Assert.Empty(state.Problems);
        Assert.False(string.IsNullOrWhiteSpace(state.Title));
        foreach (var definition in LeaseContractTemplateStructure.RequiredSections(regime))
            Assert.Contains(state.Sections, s => s.Id == definition.Id && s.Text.Length > 0);
    }

    [Fact]
    public void CommittedTemplates_EveryFile_IsWellPlacedCompleteAndNotApproved()
    {
        var root = TemplatesDirectory();
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFileName(f), ".gitkeep", StringComparison.Ordinal))
            .Select(f => (Path: f, Relative: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();

        foreach (var regime in Enum.GetValues<FiscalRegime>())
            Assert.Contains(files, f => f.Relative == $"{regime}/{DraftVersion}.md");

        foreach (var (path, relative) in files)
        {
            var parts = relative.Split('/');
            Assert.True(parts.Length == 2, $"{relative}: a template is Casazen.Web/LeaseTemplates/<Regime>/<VersionId>.md");
            Assert.True(
                Enum.TryParse<FiscalRegime>(parts[0], ignoreCase: false, out var regime)
                    && Enum.IsDefined(regime)
                    && string.Equals(regime.ToString(), parts[0], StringComparison.Ordinal),
                $"{relative}: '{parts[0]}' is not a fiscal regime with a template pipeline (transitorio and studenti have none)");
            Assert.Equal(LeaseContractTemplateCatalog.FileExtension, Path.GetExtension(parts[1]));

            var versionId = Path.GetFileNameWithoutExtension(parts[1]);
            Assert.True(
                LeaseTemplateApproval.IsValidVersionId(versionId) && !LeaseTemplateApproval.IsDevStub(versionId),
                $"{relative}: '{versionId}' is not a valid version id");

            var content = File.ReadAllText(path, Encoding.UTF8);
            foreach (var name in PlaceholdersOf(content))
                Assert.True(LeaseContractPlaceholders.IsKnown(name), $"{relative}: unknown placeholder {{{{{name}}}}}");

            var state = LeaseContractTemplateCatalog.Load(regime, OptionsFor(regime, versionId), Path.GetTempPath());
            Assert.True(
                state.Status == LeaseContractTemplateStatus.NotApproved,
                $"{relative}: {state.Status} instead of NotApproved: {string.Join("; ", state.Problems)}");
        }
    }

    [Fact]
    public void CommittedTemplates_DraftVersions_CarryTheDraftNoticeInTheFile()
    {
        var root = TemplatesDirectory();
        var drafts = Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(f => Path.GetFileNameWithoutExtension(f).Contains("bozza", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(3, drafts.Count(f => Path.GetFileNameWithoutExtension(f) == DraftVersion));
        foreach (var file in drafts)
        {
            var content = File.ReadAllText(file, Encoding.UTF8);
            Assert.Contains("BOZZA NON APPROVATA", content, StringComparison.Ordinal);
            Assert.Contains("agente AI", content, StringComparison.Ordinal);
            Assert.Contains("legale", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CommittedAppsettings_DoNotSwitchOnTheDrafts()
    {
        var files = Directory.GetFiles(WebProjectDirectory(), "appsettings*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var options = new LeaseTemplateOptions();
            new ConfigurationBuilder().AddJsonFile(file).Build()
                .GetSection(LeaseTemplateOptions.SectionName)
                .Bind(options);

            foreach (var (regime, variant) in options.Variants)
            {
                Assert.False(variant.Approved, $"{Path.GetFileName(file)}: {regime} is approved");
                Assert.True(
                    string.IsNullOrWhiteSpace(variant.VersionId),
                    $"{Path.GetFileName(file)}: {regime} names version '{variant.VersionId}'. A draft is switched on per environment with " +
                    $"LeaseTemplates__Variants__{regime}__VersionId (runbook lease-contract-templates.md, section Bozze 2026-11).");
            }
        }
    }

    [Theory]
    [InlineData(FiscalRegime.CedolareSecca)]
    [InlineData(FiscalRegime.RegimeOrdinario)]
    [InlineData(FiscalRegime.CanoneConcordato)]
    public async Task GeneratePreviewPdfAsync_DraftTemplate_ShowsTheWholeContractMarkedBozza(FiscalRegime regime)
    {
        var sut = CreateSut(regime);
        var lease = BuildLease(regime);

        var pdf = await sut.GeneratePreviewPdfAsync(lease);

        var pages = PdfTestReader.Pages(pdf);
        Assert.True(pages.Count >= 5, $"{pages.Count} pages");
        Assert.All(pages, page => Assert.Equal(LeaseContractDocument.DraftWatermark, page.Watermark));
        Assert.All(pages, page => Assert.Equal(595, Math.Round(page.Width)));

        var text = PdfTestReader.Text(pdf);
        Assert.Contains(LeaseContractDocument.DraftMarker, text, StringComparison.Ordinal);
        Assert.Contains("modello completo ma non approvato", text, StringComparison.Ordinal);
        Assert.DoesNotContain(LeaseContractDocument.ApprovedPreviewMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(LeaseContractDocument.MissingClauseMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(LeaseContractDocument.MissingTitleMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain("[DATO MANCANTE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sezioni senza testo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Problemi di formato", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dati mancanti", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
        Assert.DoesNotContain("}}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", text, StringComparison.Ordinal);

        foreach (var expected in LeaseDataPrinted(regime))
            Assert.Contains(expected, text, StringComparison.Ordinal);

        var title = LoadDraft(regime).Title!;
        Assert.Contains(title, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FiscalRegime.CedolareSecca)]
    [InlineData(FiscalRegime.RegimeOrdinario)]
    [InlineData(FiscalRegime.CanoneConcordato)]
    public async Task GeneratePdfAsync_DraftTemplate_StaysBlockedAsNotApproved(FiscalRegime regime)
    {
        var sut = CreateSut(regime);
        var lease = BuildLease(regime);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => sut.GeneratePdfAsync(lease));

        Assert.Equal(LeaseContractTemplateService.TemplateNotApprovedCode, ex.Code);
        Assert.Equal(LeaseContractTemplateService.TemplateNotApprovedCode, sut.GetFinalContractBlocker(lease));
    }

    [Theory]
    [InlineData(FiscalRegime.CedolareSecca)]
    [InlineData(FiscalRegime.RegimeOrdinario)]
    [InlineData(FiscalRegime.CanoneConcordato)]
    public void Draft_ArticleNumbersInternalReferencesAndApprovedList_AreConsistent(FiscalRegime regime)
    {
        // The format has no automatic numbering: articles, "articolo N" references and the list of the clauses to
        // approve in writing are written by hand and must agree.
        var state = LoadDraft(regime);

        var articles = new List<(int Number, string Heading, string SectionId)>();
        foreach (var section in state.Sections)
        {
            var match = ArticleHeadingPattern().Match(section.Heading ?? string.Empty);
            if (match.Success)
                articles.Add((int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), section.Heading!, section.Id));
            else
                Assert.True(UnnumberedSections.Contains(section.Id), $"{regime}: section '{section.Id}' needs an 'Art. N · Title' heading");
        }

        Assert.Equal(Enumerable.Range(1, articles.Count), articles.Select(a => a.Number));

        var numberToSection = articles.ToDictionary(a => a.Number, a => a.SectionId);
        var actualReferences = new Dictionary<string, List<string>>();
        foreach (var section in state.Sections)
        {
            foreach (Match reference in InternalReferencePattern().Matches(section.Text))
            {
                var number = int.Parse(reference.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(numberToSection.ContainsKey(number), $"{regime}: '{section.Id}' refers to articolo {number}, which does not exist");
                if (!actualReferences.TryGetValue(section.Id, out var targets))
                    actualReferences[section.Id] = targets = [];
                targets.Add(numberToSection[number]);
            }
        }

        var expected = ExpectedReferences(regime);
        Assert.Equal(
            expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
            actualReferences.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (sectionId, targets) in expected)
        {
            Assert.Equal(
                targets.OrderBy(t => t, StringComparer.Ordinal),
                actualReferences[sectionId].Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal));
        }

        var approvedSection = state.Sections.Single(s => s.Id == "clausole_approvate");
        var listed = approvedSection.Text.Split('\n')
            .Where(line => line.StartsWith("- Art. ", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .ToList();
        var approvedIds = new List<string>();
        foreach (var item in listed)
        {
            Assert.True(articles.Any(a => a.Heading == item), $"{regime}: '{item}' in the approved clauses is not an article heading");
            approvedIds.Add(articles.First(a => a.Heading == item).SectionId);
        }

        Assert.Equal(ApprovedInWriting.Order(StringComparer.Ordinal), approvedIds.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(FiscalRegime.CedolareSecca)]
    [InlineData(FiscalRegime.RegimeOrdinario)]
    [InlineData(FiscalRegime.CanoneConcordato)]
    public void Draft_DataTheProductDoesNotHave_IsABlankToFillInAndNeverInvented(FiscalRegime regime)
    {
        var state = LoadDraft(regime);
        var all = string.Join('\n', state.Sections.Select(s => s.Text));

        // No example datum slipped in: no email, IBAN or currency sign, and no fixed "4+4" / "3+2" outside the title.
        Assert.DoesNotContain("@", all, StringComparison.Ordinal);
        Assert.DoesNotContain("€", all, StringComparison.Ordinal);
        Assert.DoesNotMatch(IbanPattern(), all);
        Assert.DoesNotContain("4+4", all, StringComparison.Ordinal);
        Assert.DoesNotContain("3+2", all, StringComparison.Ordinal);

        // The data of the 2026-10-08 drafts that the product has no placeholder for stay as blanks in the printed contract.
        foreach (var (sectionId, minimumBlanks) in ExpectedBlanks(regime))
        {
            var section = state.Sections.Single(s => s.Id == sectionId);
            Assert.True(
                BlankPattern().Matches(section.Text).Count >= minimumBlanks,
                $"{regime}: section '{sectionId}' should have at least {minimumBlanks} blank field(s) to fill in by hand");
        }
    }

    [Fact]
    public void ReferenceDrafts_TransitorioAndStudenti_AreSavedAsDocumentationWithoutAPipeline()
    {
        var folder = Path.Combine(Directory.GetParent(WebProjectDirectory())!.FullName, "docs", "legal-drafts", "leases");

        foreach (var name in new[] { "contratto-transitorio.md", "contratto-studenti.md" })
        {
            var path = Path.Combine(folder, name);
            Assert.True(File.Exists(path), $"{name} not found in docs/legal-drafts/leases");

            var content = File.ReadAllText(path, Encoding.UTF8);
            Assert.Contains("Bozza non approvata", content, StringComparison.Ordinal);
            Assert.Contains("nessuna pipeline di prodotto", content, StringComparison.Ordinal);
            Assert.Contains("| Variabile |", content, StringComparison.Ordinal);
            Assert.Contains("D.M. 16 gennaio 2017", content, StringComparison.Ordinal);
        }

        // Never a template of the product: the folders of LeaseTemplates are fiscal regimes only.
        var templateFolders = Directory.GetDirectories(TemplatesDirectory()).Select(d => Path.GetFileName(d)).ToList();
        Assert.All(templateFolders, f => Assert.True(Enum.TryParse<FiscalRegime>(f, out _), $"LeaseTemplates/{f} is not a fiscal regime"));
    }

    [GeneratedRegex(@"^Art\. (\d+) · \S", RegexOptions.CultureInvariant)]
    private static partial Regex ArticleHeadingPattern();

    [GeneratedRegex(@"\barticolo\s+(\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex InternalReferencePattern();

    [GeneratedRegex(@"\{\{\s*([^{}\s]*)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}\s?[A-Z]\d{3}", RegexOptions.CultureInvariant)]
    private static partial Regex IbanPattern();

    [GeneratedRegex("_{5,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankPattern();

    /// <summary>The "articolo N" references of the drafts: section that refers → sections it refers to.</summary>
    private static Dictionary<string, string[]> ExpectedReferences(FiscalRegime regime) => regime switch
    {
        FiscalRegime.RegimeOrdinario => new()
        {
            [LeaseContractTemplateStructure.RentUpdate] = ["regime_fiscale"],
            ["regime_fiscale"] = [LeaseContractTemplateStructure.RentUpdate, "registrazione"],
        },
        FiscalRegime.CedolareSecca => new()
        {
            [LeaseContractTemplateStructure.RentUpdate] = [LeaseContractTemplateStructure.CedolareOption],
            ["registrazione"] = [LeaseContractTemplateStructure.CedolareOption],
            [LeaseContractTemplateStructure.CedolareOption] = [LeaseContractTemplateStructure.RentUpdate],
        },
        FiscalRegime.CanoneConcordato => new()
        {
            ["premesse"] = [LeaseContractTemplateStructure.TerritorialAgreement],
            [LeaseContractTemplateStructure.RentUpdate] = ["regime_fiscale"],
            ["regime_fiscale"] = [LeaseContractTemplateStructure.RentUpdate],
            ["manutenzione"] = [LeaseContractTemplateStructure.AncillaryCharges],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(regime), regime, null),
    };

    /// <summary>Sections with data the product does not have and the minimum number of blank fields in each.</summary>
    private static Dictionary<string, int> ExpectedBlanks(FiscalRegime regime)
    {
        var blanks = new Dictionary<string, int>
        {
            [LeaseContractTemplateStructure.Parties] = 2,
            [LeaseContractTemplateStructure.Property] = 3,
            [LeaseContractTemplateStructure.Rent] = 2,
            [LeaseContractTemplateStructure.AncillaryCharges] = 1,
            ["comunicazioni"] = 2,
            ["firme"] = 4,
        };
        if (regime == FiscalRegime.CanoneConcordato)
        {
            // The agreed rent has no free withdrawal and its ISTAT share is the 75% of the agreement; it asks for the
            // agreement's details, the rent band and the bodies that attest instead.
            blanks[LeaseContractTemplateStructure.TerritorialAgreement] = 1;
            blanks[LeaseContractTemplateStructure.ConformityAttestation] = 1;
        }
        else
        {
            // Months of notice of the free withdrawal and the ISTAT percentage (also after a cedolare revocation).
            blanks[LeaseContractTemplateStructure.TenantWithdrawal] = 1;
            blanks[LeaseContractTemplateStructure.RentUpdate] = 1;
        }

        return blanks;
    }

    /// <summary>What the preview must print of the test lease: parties, address, cadastral and APE data, amounts, dates, term.</summary>
    private static string[] LeaseDataPrinted(FiscalRegime regime) =>
    [
        "Mario Rossi (C.F. RSSMRA80A01H501U)",
        "Anna Bianchi (C.F. BNCNNA82A41F205W)",
        "Luigi Verdi (C.F. VRDLGU85B02F205C)",
        "Giulia Verdi (C.F. VRDGLI85B42F205E)",
        "Via Roma 1, 20822 Seveso",
        "Foglio 12, particella 345, subalterno 6, categoria A/2, rendita catastale euro 512,30",
        "codice 1510800012345, classe energetica B",
        "euro 1.200,00 al mese",
        "euro 14.400,00 all’anno",
        "euro 2.400,00",
        "01/09/2026",
        regime == FiscalRegime.CanoneConcordato ? "31/08/2029" : "31/08/2030",
        regime == FiscalRegime.CanoneConcordato ? "3 anni" : "4 anni",
    ];

    private static LeaseContractTemplateService CreateSut(FiscalRegime regime) =>
        new(
            LeaseTemplateTestFiles.Catalog(OptionsFor(regime, DraftVersion)),
            new MigraDocPdfDocumentRenderer(),
            NullLogger<LeaseContractTemplateService>.Instance);

    private static LeaseContractTemplateState LoadDraft(FiscalRegime regime) =>
        LeaseContractTemplateCatalog.Load(regime, OptionsFor(regime, DraftVersion), Path.GetTempPath());

    /// <summary>The committed folder with the version named, never approved (as the committed appsettings).</summary>
    private static LeaseTemplateOptions OptionsFor(FiscalRegime regime, string versionId) => new()
    {
        TemplatesDirectory = TemplatesDirectory(),
        Variants = new Dictionary<string, LeaseTemplateVariantOptions>(StringComparer.OrdinalIgnoreCase)
        {
            [regime.ToString()] = new LeaseTemplateVariantOptions { VersionId = versionId, Approved = false },
        },
    };

    /// <summary>Names of the placeholders written in the clause texts (comment lines are not text).</summary>
    private static IEnumerable<string> PlaceholdersOf(string content)
    {
        var clauseLines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => !(line.Trim().StartsWith("<!--", StringComparison.Ordinal) && line.Trim().EndsWith("-->", StringComparison.Ordinal)));
        return PlaceholderPattern().Matches(string.Join('\n', clauseLines)).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);
    }

    private static string TemplatesDirectory() => Path.Combine(WebProjectDirectory(), "LeaseTemplates");

    private static string WebProjectDirectory()
    {
        for (var directory = new DirectoryInfo(System.AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Casazen.Web");
            if (File.Exists(Path.Combine(candidate, "Casazen.Web.csproj")))
                return candidate;
        }

        throw new DirectoryNotFoundException("Casazen.Web project not found above the test output folder.");
    }

    /// <summary>A lease with every datum the drafts use: two landlords, two tenants, cadastral data, APE, deposit.</summary>
    private static LeaseContract BuildLease(FiscalRegime regime)
    {
        var (contractType, taxRegime) = LeaseContractTerms.FromLegacy(regime);
        return new LeaseContract
        {
            Id = Guid.NewGuid(),
            FiscalRegime = regime,
            ContractType = contractType,
            TaxRegime = taxRegime,
            MonthlyRent = 1200m,
            SecurityDeposit = 2400m,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = regime == FiscalRegime.CanoneConcordato
                ? new DateTime(2029, 8, 31, 0, 0, 0, DateTimeKind.Utc)
                : new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            Property = new Property
            {
                Name = "Condominio Il Parco",
                Address = "Via Roma 1",
                PostalCode = "20822",
                City = "Seveso",
                CadastralSheet = "12",
                CadastralParcel = "345",
                CadastralSubaltern = "6",
                CadastralCategory = "A/2",
                CadastralIncome = 512.30m,
                PropertyDocuments =
                [
                    new PropertyDocument
                    {
                        DocumentType = DocumentType.Ape,
                        ApeCode = "1510800012345",
                        ApeEnergyClass = "B",
                        UploadedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                    },
                ],
            },
            Parties =
            [
                new Party { Role = PartyRole.Landlord, Position = 0, FirstName = "Mario", LastName = "Rossi", FiscalCode = "RSSMRA80A01H501U" },
                new Party { Role = PartyRole.Landlord, Position = 1, FirstName = "Anna", LastName = "Bianchi", FiscalCode = "BNCNNA82A41F205W" },
                new Party { Role = PartyRole.Tenant, Position = 0, FirstName = "Luigi", LastName = "Verdi", FiscalCode = "VRDLGU85B02F205C" },
                new Party { Role = PartyRole.Tenant, Position = 1, FirstName = "Giulia", LastName = "Verdi", FiscalCode = "VRDGLI85B42F205E" },
            ],
        };
    }
}
