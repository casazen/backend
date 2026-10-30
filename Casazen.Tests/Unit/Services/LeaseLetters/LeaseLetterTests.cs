using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services.LeaseLetters;
using Casazen.Tests.Unit.Documents;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services.LeaseLetters;

/// <summary>
/// LR-OPS-1: the letters CasaZen prepares for the landlord are drafts until the product owner approves their text on the host. The files
/// committed in <c>Casazen.Web/LeaseLetters</c> are complete and not approved, no committed configuration approves them, a letter that is not
/// approved is printed with the watermark and the closing marker, the reason of the first expiry is the only one printed, a datum the contract
/// lacks is «[da completare]», and no fiscal code or e-mail of a party is ever in a letter.
/// </summary>
public class LeaseLetterTests
{
    private const string TenantFiscalCode = "VRDGLI85C41H501Z";
    private const string TenantEmail = "giulia.verdi@example.test";

    public static TheoryData<LeaseLetterKind> Kinds() => new(Enum.GetValues<LeaseLetterKind>());

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Load_TheCommittedDraft_IsCompleteAndNotApproved(LeaseLetterKind kind)
    {
        var state = LeaseLetterCatalog.Load(kind, new LeaseLetterOptions { LettersDirectory = LettersDirectory() }, Path.GetTempPath());

        Assert.Equal(LeaseLetterStatus.NotApproved, state.Status);
        Assert.Equal(LeaseLetterStructure.DraftVersion(kind), state.VersionId);
        Assert.Empty(state.Problems);
        Assert.False(state.IsApproved);
        Assert.True(state.IsUsable);
        Assert.Contains("BOZZA NON APPROVATA", File.ReadAllText(Path.Combine(LettersDirectory(), kind.ToString(), state.VersionId + ".md")), StringComparison.Ordinal);
    }

    [Fact]
    public void CommittedAppsettings_DoNotApproveAnyLetter()
    {
        var files = Directory.GetFiles(WebProjectDirectory(), "appsettings*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var options = new LeaseLetterOptions();
            new ConfigurationBuilder().AddJsonFile(file).Build().GetSection(LeaseLetterOptions.SectionName).Bind(options);

            foreach (var (kind, variant) in options.Variants)
            {
                Assert.False(variant.Approved, $"{Path.GetFileName(file)}: {kind} is approved");
                Assert.True(
                    string.IsNullOrWhiteSpace(variant.VersionId),
                    $"{Path.GetFileName(file)}: {kind} names version '{variant.VersionId}'. A letter is approved per environment with LeaseLetters__Variants__{kind}__* (runbook lease-operations.md).");
            }
        }
    }

    [Fact]
    public void AnApprovalWithoutAVersionOrWithoutEvidence_StopsTheStartup_ButAValidOneDoesNot()
    {
        var invalid = new LeaseLetterOptions
        {
            LettersDirectory = LettersDirectory(),
            Variants = { [nameof(LeaseLetterKind.Disdetta)] = new LeaseTemplateVariantOptions { Approved = true } },
        };
        var valid = new LeaseLetterOptions
        {
            LettersDirectory = LettersDirectory(),
            Variants = { [nameof(LeaseLetterKind.Disdetta)] = Approved(LeaseLetterKind.Disdetta) },
        };

        var failures = LeaseLetterOptionsValidator.GetFailures(invalid, Path.GetTempPath());

        Assert.Contains(failures, f => f.Contains("VersionId is empty", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("ApprovalReference or ApprovedAt is required", StringComparison.Ordinal));
        Assert.Empty(LeaseLetterOptionsValidator.GetFailures(valid, Path.GetTempPath()));
    }

    [Fact]
    public void ALetterThatIsNotApproved_IsPrintedWithTheWatermarkAndTheMarker_AnApprovedOneWithNeither()
    {
        var lease = Lease();
        var flags = FirstPeriodFlags(LeaseDisdettaReason.OwnUse);

        var draft = LeaseLetterDocument.Build(Draft(LeaseLetterKind.Disdetta), flags, LeaseLetterData.Common(lease));
        var approved = LeaseLetterDocument.Build(ApprovedState(LeaseLetterKind.Disdetta), flags, LeaseLetterData.Common(lease));

        Assert.Equal("BOZZA — testo non ancora approvato", draft.Watermark);
        Assert.Contains(LeaseLetterDocument.DraftMarker, TextOf(draft), StringComparison.Ordinal);
        Assert.Null(approved.Watermark);
        Assert.DoesNotContain("BOZZA", TextOf(approved), StringComparison.Ordinal);

        // The same on the real PDF: the marker is text in the page, so it stays if the text is copied.
        var pdf = PdfTestReader.Text(new MigraDocPdfDocumentRenderer().Render(draft));
        Assert.Contains("BOZZA", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void AtTheFirstExpiryOnlyTheChosenReasonIsPrinted_AtALaterOneTheRenunciationIs()
    {
        var values = LeaseLetterData.Common(Lease());

        var first = TextOf(LeaseLetterDocument.Build(Draft(LeaseLetterKind.Disdetta), FirstPeriodFlags(LeaseDisdettaReason.Sale), values));
        var later = TextOf(LeaseLetterDocument.Build(Draft(LeaseLetterKind.Disdetta), new HashSet<string> { LeaseLetterFlags.LaterPeriod }, values));

        Assert.Contains("lettera g)", first, StringComparison.Ordinal);
        Assert.DoesNotContain("lettera a)", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Rinuncia al rinnovo", first, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"lettera [a-g]\)", later);
        Assert.Contains("Rinuncia al rinnovo", later, StringComparison.Ordinal);
    }

    [Fact]
    public void ADatumTheContractLacks_IsToComplete_AndNoFiscalCodeOrEmailIsEverPrinted()
    {
        var content = LeaseLetterDocument.Build(Draft(LeaseLetterKind.Disdetta), FirstPeriodFlags(LeaseDisdettaReason.OwnUse), LeaseLetterData.Common(Lease()));
        var text = TextOf(content);

        Assert.Contains("Mario Rossi, residente in Monza, via Verdi 2", text, StringComparison.Ordinal);
        Assert.Contains("Giulia Verdi, residente in [da completare]", text, StringComparison.Ordinal);
        Assert.Contains("Via Roma 1, 20900 Monza", text, StringComparison.Ordinal);
        Assert.Contains("31 ottobre 2029", text, StringComparison.Ordinal);
        Assert.DoesNotContain(TenantFiscalCode, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TenantEmail, text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRenewalAgreement_PrintsTheRenewalOrTheExtensionNeverBoth_WithTheDaysItWasGiven()
    {
        var values = LeaseLetterData.Common(Lease());
        values[LeaseLetterPlaceholders.NewPeriodStart] = LeaseLetterData.Day(new DateOnly(2029, 11, 1));
        values[LeaseLetterPlaceholders.NewPeriodEnd] = LeaseLetterData.Day(new DateOnly(2033, 10, 31));

        var renewal = TextOf(LeaseLetterDocument.Build(Draft(LeaseLetterKind.Rinnovo), new HashSet<string> { LeaseLetterFlags.RenewalStandard }, values));
        var extension = TextOf(LeaseLetterDocument.Build(Draft(LeaseLetterKind.Rinnovo), new HashSet<string> { LeaseLetterFlags.RenewalAgreed }, values));

        Assert.Contains("si rinnova alla scadenza", renewal, StringComparison.Ordinal);
        Assert.DoesNotContain("convengono di prorogare", renewal, StringComparison.Ordinal);
        Assert.Contains("convengono di prorogare", extension, StringComparison.Ordinal);
        Assert.DoesNotContain("si rinnova alla scadenza", extension, StringComparison.Ordinal);
        foreach (var text in new[] { renewal, extension })
        {
            Assert.Contains("dal 1 novembre 2029 al 31 ottobre 2033", text, StringComparison.Ordinal);
            Assert.DoesNotContain(TenantFiscalCode, text, StringComparison.Ordinal);
            Assert.DoesNotContain(TenantEmail, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheNeedConfirmation_RepeatsTheNeedAsTheContractWritesIt_OnOneLine_WithTheLastDay()
    {
        var lease = Lease();
        lease.ContractType = LeaseContractType.Transitorio;
        lease.TransitoryNeed = "Trasferimento temporaneo\nper lavoro.";

        var text = TextOf(LeaseLetterDocument.Build(Draft(LeaseLetterKind.ConfermaEsigenza), new HashSet<string>(), LeaseLetterData.Common(lease)));

        Assert.Contains("Trasferimento temporaneo per lavoro. La conferma", text, StringComparison.Ordinal);
        Assert.Contains("cessa il 31 ottobre 2029", text, StringComparison.Ordinal);
        Assert.DoesNotContain(TenantFiscalCode, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TenantEmail, text, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static HashSet<string> FirstPeriodFlags(LeaseDisdettaReason reason) =>
        [LeaseLetterFlags.FirstPeriod, LeaseLetterFlags.Reason(reason)];

    private static LeaseContract Lease() => new()
    {
        Status = LeaseStatus.Registered,
        ContractType = LeaseContractType.Libero,
        StartDate = new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2029, 10, 31, 0, 0, 0, DateTimeKind.Utc),
        Property = new Property { Address = "Via Roma 1", PostalCode = "20900", City = "Monza" },
        Parties =
        [
            new Party { Role = PartyRole.Landlord, FirstName = "Mario", LastName = "Rossi", FiscalCode = "RSSMRA80A01F205X", Residence = "Monza, via Verdi 2" },
            new Party { Role = PartyRole.Tenant, FirstName = "Giulia", LastName = "Verdi", FiscalCode = TenantFiscalCode, ContactEmail = TenantEmail },
        ],
    };

    private static LeaseLetterState Draft(LeaseLetterKind kind) =>
        LeaseLetterCatalog.Load(kind, new LeaseLetterOptions { LettersDirectory = LettersDirectory() }, Path.GetTempPath());

    private static LeaseTemplateVariantOptions Approved(LeaseLetterKind kind) => new()
    {
        VersionId = LeaseLetterStructure.DraftVersion(kind),
        Approved = true,
        ApprovalReference = "test approval",
    };

    private static LeaseLetterState ApprovedState(LeaseLetterKind kind) =>
        LeaseLetterCatalog.Load(
            kind,
            new LeaseLetterOptions { LettersDirectory = LettersDirectory(), Variants = { [kind.ToString()] = Approved(kind) } },
            Path.GetTempPath());

    private static string TextOf(PdfDocumentContent content) =>
        string.Join('\n', content.Blocks.Select(block => block switch
        {
            PdfHeading heading => heading.Text,
            PdfParagraph paragraph => paragraph.Text,
            _ => string.Empty,
        }));

    private static string LettersDirectory() => Path.Combine(WebProjectDirectory(), "LeaseLetters");

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
}
