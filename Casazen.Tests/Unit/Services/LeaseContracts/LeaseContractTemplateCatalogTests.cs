using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services.LeaseContracts;
using Xunit;

namespace Casazen.Tests.Unit.Services.LeaseContracts;

/// <summary>LT-03 (A7-03): a template missing a required section or datum is incomplete and cannot be approved.</summary>
public sealed class LeaseContractTemplateCatalogTests : IDisposable
{
    private readonly LeaseTemplateTestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public void Load_NoVersionConfigured_IsMissingWithEveryRequiredSection()
    {
        var state = Load(FiscalRegime.CanoneConcordato, new LeaseTemplateVariantOptions());

        Assert.Equal(LeaseContractTemplateStatus.Missing, state.Status);
        Assert.Equal(
            LeaseContractTemplateStructure.RequiredSections(FiscalRegime.CanoneConcordato).Select(s => s.Id),
            state.MissingSections);
    }

    [Fact]
    public void Load_ApprovedVersionWithoutFile_IsMissing()
    {
        var state = Load(FiscalRegime.CedolareSecca, LeaseTemplateTestFiles.Approved("not-there"));

        Assert.Equal(LeaseContractTemplateStatus.Missing, state.Status);
        Assert.False(state.IsApproved);
    }

    [Fact]
    public void Load_RequiredSectionWithoutText_IsIncomplete()
    {
        Write(FiscalRegime.CanoneConcordato, LeaseTemplateTestFiles.CompleteTemplate(
            FiscalRegime.CanoneConcordato,
            new Dictionary<string, string> { [LeaseContractTemplateStructure.ConformityAttestation] = "<!-- da fornire -->" }));

        var state = Load(FiscalRegime.CanoneConcordato, LeaseTemplateTestFiles.Approved());

        Assert.Equal(LeaseContractTemplateStatus.Incomplete, state.Status);
        Assert.Equal([LeaseContractTemplateStructure.ConformityAttestation], state.MissingSections);
    }

    [Theory]
    [InlineData(FiscalRegime.CedolareSecca, LeaseContractTemplateStructure.CedolareOption)]
    [InlineData(FiscalRegime.RegimeOrdinario, LeaseContractTemplateStructure.Ape)]
    [InlineData(FiscalRegime.CanoneConcordato, LeaseContractTemplateStructure.TerritorialAgreement)]
    public void Load_RequiredSectionAbsent_IsIncomplete(FiscalRegime regime, string sectionId)
    {
        Write(regime, LeaseTemplateTestFiles.CompleteTemplate(regime, omit: [sectionId]));

        var state = Load(regime, LeaseTemplateTestFiles.Approved());

        Assert.Equal(LeaseContractTemplateStatus.Incomplete, state.Status);
        Assert.Contains(sectionId, state.MissingSections);
    }

    [Fact]
    public void Load_TermSectionWithoutComputedTerm_IsIncomplete()
    {
        Write(FiscalRegime.RegimeOrdinario, LeaseTemplateTestFiles.CompleteTemplate(
            FiscalRegime.RegimeOrdinario,
            new Dictionary<string, string>
            {
                [LeaseContractTemplateStructure.Term] = "Durata di prova dal {{data_decorrenza}} al {{data_scadenza}}.",
            }));

        var state = Load(FiscalRegime.RegimeOrdinario, LeaseTemplateTestFiles.Approved());

        Assert.Equal(LeaseContractTemplateStatus.Incomplete, state.Status);
        Assert.Contains(state.Issues, i => i.Contains("{{durata}}", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_UnknownPlaceholder_IsIncomplete()
    {
        Write(FiscalRegime.CedolareSecca, LeaseTemplateTestFiles.CompleteTemplate(
            FiscalRegime.CedolareSecca,
            new Dictionary<string, string> { [LeaseContractTemplateStructure.Rent] = "Canone di prova {{canone_mensile}} {{iban}}." }));

        var state = Load(FiscalRegime.CedolareSecca, LeaseTemplateTestFiles.Approved());

        Assert.Equal(LeaseContractTemplateStatus.Incomplete, state.Status);
        Assert.Contains(state.Issues, i => i.Contains("{{iban}}", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_TemplateWithoutTitle_IsIncomplete()
    {
        Write(FiscalRegime.CedolareSecca, LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CedolareSecca, title: null));

        var state = Load(FiscalRegime.CedolareSecca, LeaseTemplateTestFiles.Approved());

        Assert.Equal(LeaseContractTemplateStatus.Incomplete, state.Status);
    }

    [Fact]
    public void Load_CompleteTemplateNotDeclaredApproved_IsNotApproved()
    {
        Write(FiscalRegime.CedolareSecca, LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CedolareSecca));

        var state = Load(FiscalRegime.CedolareSecca, new LeaseTemplateVariantOptions { VersionId = LeaseTemplateTestFiles.ApprovedVersion });

        Assert.Equal(LeaseContractTemplateStatus.NotApproved, state.Status);
        Assert.Empty(state.MissingSections);
    }

    [Fact]
    public void Load_CompleteTemplateApprovedWithoutReferenceOrDate_IsNotApproved()
    {
        Write(FiscalRegime.CedolareSecca, LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CedolareSecca));

        var state = Load(FiscalRegime.CedolareSecca, new LeaseTemplateVariantOptions
        {
            VersionId = LeaseTemplateTestFiles.ApprovedVersion,
            Approved = true,
        });

        Assert.Equal(LeaseContractTemplateStatus.NotApproved, state.Status);
        Assert.Contains(state.InvalidApprovalReasons, r => r.Contains("ApprovalReference", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_CompleteTemplateApprovedWithDate_IsApproved()
    {
        Write(FiscalRegime.CanoneConcordato, LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CanoneConcordato));

        var state = Load(FiscalRegime.CanoneConcordato, new LeaseTemplateVariantOptions
        {
            VersionId = LeaseTemplateTestFiles.ApprovedVersion,
            Approved = true,
            ApprovedAt = new DateOnly(2026, 10, 1),
        });

        Assert.Equal(LeaseContractTemplateStatus.Approved, state.Status);
        Assert.Equal("Titolo di prova", state.Title);
    }

    [Fact]
    public void Parse_HeadingsCommentsAndPlaceholders_AreRead()
    {
        var parsed = LeaseContractTemplateParser.Parse(
            "# Titolo\r\n<!-- nota per il legale -->\r\n## parti | Art. 1 - Le parti\r\nRiga uno {{locatori}}\r\n\r\nRiga due {{ conduttori }}\r\n## ape\r\n");

        Assert.Empty(parsed.Errors);
        Assert.Equal("Titolo", parsed.Title);
        var parti = parsed.Sections[0];
        Assert.Equal("Art. 1 - Le parti", parti.Heading);
        Assert.Equal("Riga uno {{locatori}}\n\nRiga due {{ conduttori }}", parti.Text);
        Assert.Equal([LeaseContractPlaceholders.Landlords, LeaseContractPlaceholders.Tenants], parti.Placeholders);
        Assert.Equal(string.Empty, parsed.Sections[1].Text);
    }

    [Fact]
    public void Parse_DuplicateSectionAndTextOutsideSections_AreErrors()
    {
        var parsed = LeaseContractTemplateParser.Parse("# Titolo\nTesto libero\n## parti\nA\n## parti\nB\n## Canone\nC {{x");

        Assert.Contains(parsed.Errors, e => e.Contains("outside of a section", StringComparison.Ordinal));
        Assert.Contains(parsed.Errors, e => e.Contains("more than once", StringComparison.Ordinal));
        Assert.Contains(parsed.Errors, e => e.Contains("'Canone'", StringComparison.Ordinal));
        Assert.Contains(parsed.Errors, e => e.Contains("malformed placeholder", StringComparison.Ordinal));
    }

    private void Write(FiscalRegime regime, string content) =>
        _files.Write(regime, LeaseTemplateTestFiles.ApprovedVersion, content);

    private LeaseContractTemplateState Load(FiscalRegime regime, LeaseTemplateVariantOptions variant) =>
        LeaseContractTemplateCatalog.Load(regime, _files.Options(regime, variant), Path.GetTempPath());
}
