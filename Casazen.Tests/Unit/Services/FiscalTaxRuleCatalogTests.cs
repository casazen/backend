using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;
using RuleKind = Casazen.Core.Options.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E11/F01/US01/T02 (NF-82, NF-83, NF-113): the effective-dated catalog of the tax rules. The rules that cost money if they fail: the VAT of every line
/// type of a Swiss document to the cent, no stamp duty and no XML on a Swiss document, the rule chosen by the date of the stay, a catalog that would issue
/// an Italian 0% without its nature never starting, and the VAT summary of the XML being the stored VAT of the lines.
/// </summary>
public class FiscalTaxRuleCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly StayEnd = new(2026, 11, 18);

    /// <summary>The catalog shipped in <c>appsettings.json</c> starts, and holds the ⚖ defaults of the analysis of 2026-11-14 (F1, C2, I1-I5).</summary>
    [Fact]
    public void TaxRuleCatalog_ShippedDefaults_AreValid_AndHoldTheDecidedRates()
    {
        var shipped = TaxRuleCatalogs.Shipped();
        Assert.Empty(FiscalTaxRulesOptionsValidator.Problems(shipped));
        Assert.Equal(5, shipped.TaxRules.Count);
        var resolver = TaxRuleCatalogs.Resolver(shipped);

        // CH registered (F1, C2): stay 3.8, cleaning with the stay 3.8 or as a service 8.1, services 8.1, tourist tax and advanced expenses outside.
        var swiss = resolver.Resolve("CH", DocumentRegime.SwissVatRegistered, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, StayEnd);
        Assert.Equal((RuleKind.Invoice, "ch-vat-2024.1", true), (swiss.DocumentKind, swiss.Version, swiss.IsSwiss));
        Assert.Equal(new FiscalLineRate(3.8m, null), swiss.RateOf(FiscalLineType.Accommodation));
        Assert.Equal(new FiscalLineRate(3.8m, null), swiss.RateOf(FiscalLineType.Cleaning));
        Assert.Equal(new FiscalLineRate(8.1m, null), swiss.RateOf(FiscalLineType.ExtraService));
        Assert.Equal(new FiscalLineRate(8.1m, null), swiss.RateOf(FiscalLineType.Commission));
        Assert.Equal(new FiscalLineRate(8.1m, null), swiss.RateOf(FiscalLineType.Recharge));
        Assert.Equal(new FiscalLineRate(0m, null), swiss.RateOf(FiscalLineType.TouristTax));
        Assert.Equal(new FiscalLineRate(0m, null), swiss.RateOf(FiscalLineType.AdvancedExpense));
        Assert.Null(swiss.StampDuty);
        Assert.Null(swiss.FatturaPaRegimeCode);
        var cleaningAsService = resolver.Resolve("CH", DocumentRegime.SwissVatRegistered, StayVatTreatment.Standard, CleaningFeeTreatment.Service, StayEnd);
        Assert.Equal(new FiscalLineRate(8.1m, null), cleaningAsService.RateOf(FiscalLineType.Cleaning));

        // CH not registered: a receipt without VAT.
        var notRegistered = resolver.Resolve("CH", DocumentRegime.SwissNotRegistered, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, StayEnd);
        Assert.Equal(RuleKind.Receipt, notRegistered.DocumentKind);
        Assert.All(notRegistered.Rates.Values, rate => Assert.Equal(new FiscalLineRate(0m, null), rate));

        // No rule for an Italian regime in Switzerland, nor before the first day of a rule.
        Assert.Null(resolver.TryResolve("CH", DocumentRegime.VatOrdinary, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, StayEnd));
        Assert.Null(resolver.TryResolve("IT", DocumentRegime.VatOrdinary, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, new DateOnly(2025, 12, 31)));
        var refused = Assert.Throws<DomainRuleException>(
            () => resolver.Resolve("IT", null, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, StayEnd));
        Assert.Equal(DocumentIssuerErrorCodes.RuleNotConfigured, refused.Code);
    }

    /// <summary>
    /// PROOF TEST: money rule. A Swiss invoice of a registered issuer: 4 nights CHF 600, cleaning 80 (with the stay), an extra service 40, the tourist tax of 2
    /// guests × 4 nights × 2.00. The stay and the cleaning at 3.8%, the extra at 8.1%, the tourist tax outside the VAT: every line and every total to the
    /// cent, the total exactly what the guest paid, no stamp duty.
    /// </summary>
    [Fact]
    public void SwissInvoice_VatPerLineType_ExactCents_Test()
    {
        var rule = TaxRuleCatalogs.Resolver().Resolve("CH", DocumentRegime.SwissVatRegistered, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, StayEnd);
        var touristTax = 2 * 4 * 2.00m;

        var amounts = FiscalDocumentTotals.FromGross(
            [
                Line(rule, "Soggiorno di 4 notti", 600m, FiscalLineType.Accommodation),
                Line(rule, "Pulizie finali", 80m, FiscalLineType.Cleaning),
                Line(rule, "Servizio extra", 40m, FiscalLineType.ExtraService),
                Line(rule, "Tassa di soggiorno", touristTax, FiscalLineType.TouristTax),
            ],
            rule.StampDuty);

        Assert.Equal(
            new[] { (3.8m, 578.04m, 21.96m), (3.8m, 77.07m, 2.93m), (8.1m, 37.00m, 3.00m), (0m, 16.00m, 0m) },
            amounts.Lines.Select(l => (l.VatRatePercent, l.TotalPrice, l.VatAmount!.Value)).ToArray());
        Assert.All(amounts.Lines, line => Assert.Null(line.NatureCode));
        Assert.Equal(
            new[] { new FiscalDocumentVatSummary(8.1m, null, 37.00m, 3.00m), new FiscalDocumentVatSummary(3.8m, null, 655.11m, 24.89m), new FiscalDocumentVatSummary(0m, null, 16.00m, 0m) },
            amounts.Summary.ToArray());
        Assert.Equal(708.11m, amounts.TaxableBase);
        Assert.Equal(27.89m, amounts.VatAmount);
        Assert.Equal(0m, amounts.StampDuty);
        Assert.Equal(736.00m, amounts.Total);
        Assert.Equal(600m + 80m + 40m + touristTax, amounts.Total);
    }

    /// <summary>
    /// PROOF TEST: money rule. A Swiss document never has a stamp duty nor an XML: a CHF 500 receipt of an issuer that is not registered (above the Italian
    /// threshold of 77.47) and a CHF 500 invoice of a registered one are issued with no stamp duty, no FatturaPA XML (the builder is never called) and the
    /// SDI state <c>not_applicable</c>.
    /// </summary>
    [Fact]
    public async Task SwissDocument_NeverStampDutyNorXml_Test()
    {
        foreach (var regime in new[] { DocumentRegime.SwissNotRegistered, DocumentRegime.SwissVatRegistered })
        {
            await using var db = NewDb();
            var booking = await SeedSwissAsync(db, regime, basePrice: 500m);
            var xml = new Mock<IFatturaPaXmlBuilder>(MockBehavior.Strict);
            var service = Service(db, xml.Object);

            var draft = await service.CreateDraftAsync(booking, null);
            var issued = await service.IssueAsync(draft.Id, confirm: true);

            Assert.Equal(regime == DocumentRegime.SwissNotRegistered ? DocumentKind.Receipt : DocumentKind.Invoice, issued.Kind);
            Assert.Equal("CHF", issued.Currency);
            Assert.Equal(500m, issued.Total);
            Assert.Equal(0m, issued.StampDuty);
            Assert.Null(issued.FatturaPaXml);
            Assert.Equal(FiscalDocumentSdiStatuses.NotApplicable, issued.SdiStatus);
            Assert.All(issued.Lines, line => Assert.Null(line.VatNatureCode));
            Assert.Equal(regime == DocumentRegime.SwissNotRegistered ? 0m : 18.30m, issued.VatAmount);
            xml.Verify(x => x.Build(It.IsAny<FatturaPaInput>()), Times.Never);

            // Its PDF is the Swiss one (E11/F01/US01/T03), from the snapshot of the issuer kept at the issue.
            var pdf = await service.PdfContentAsync(issued.Id, "it");
            Assert.Equal(regime == DocumentRegime.SwissNotRegistered ? "Ricevuta n. 2026/1" : "Fattura n. 2026/1", pdf.Title);
            Assert.Equal("it-CH", pdf.Language);
        }
    }

    /// <summary>
    /// PROOF TEST: money rule. The rule is chosen by the date of the stay: with a new version of the Swiss rates from 2028-01-01, a stay that ends on
    /// 2027-12-31 keeps the old rate (3.8%) and its version, one that ends on 2028-01-01 takes the new one (4.0%).
    /// </summary>
    [Fact]
    public void TaxRule_ChosenByStayDate_Test()
    {
        var catalog = new FiscalTaxRulesOptions
        {
            TaxRules =
            [
                SwissSet("ch-vat-2024.1", new DateOnly(2024, 1, 1), new DateOnly(2027, 12, 31), stay: 3.8m, normal: 8.1m),
                SwissSet("ch-vat-2028.1", new DateOnly(2028, 1, 1), null, stay: 4.0m, normal: 8.5m),
            ],
        };
        Assert.Empty(FiscalTaxRulesOptionsValidator.Problems(catalog));
        var resolver = TaxRuleCatalogs.Resolver(catalog);

        var before = resolver.Resolve("CH", DocumentRegime.SwissVatRegistered, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, new DateOnly(2027, 12, 31));
        var after = resolver.Resolve("CH", DocumentRegime.SwissVatRegistered, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, new DateOnly(2028, 1, 1));

        Assert.Equal(("ch-vat-2024.1", 3.8m), (before.Version, before.RateOf(FiscalLineType.Accommodation).Percent));
        Assert.Equal(("ch-vat-2028.1", 4.0m), (after.Version, after.RateOf(FiscalLineType.Accommodation).Percent));
        Assert.Equal(38.00m, FiscalDocumentTotals.FromGross([Line(before, "Soggiorno", 1038m, FiscalLineType.Accommodation)], before.StampDuty).VatAmount);
        Assert.Equal(40.00m, FiscalDocumentTotals.FromGross([Line(after, "Soggiorno", 1040m, FiscalLineType.Accommodation)], after.StampDuty).VatAmount);
    }

    /// <summary>
    /// PROOF TEST: money rule. A catalog that would issue an Italian 0% line without its nature code never starts: the validator names the rule set and the
    /// line type, and the options of the container throw at their first use (the startup, with ValidateOnStart). Overlapping versions and a missing rate stop
    /// it too.
    /// </summary>
    [Fact]
    public void TaxRuleCatalog_ItalianZeroLineWithoutNatureStopsStartup_Test()
    {
        var withoutNature = ItalianOrdinarySet("it-ordinary-test.1", new DateOnly(2026, 1, 1), null);
        withoutNature.Rates[FiscalLineType.TouristTax] = new FiscalTaxRateOptions { Percent = 0m };
        var problem = Assert.Single(FiscalTaxRulesOptionsValidator.Problems(new FiscalTaxRulesOptions { TaxRules = [withoutNature] }));
        Assert.Contains("it-ordinary-test.1", problem, StringComparison.Ordinal);
        Assert.Contains("TouristTax has no nature code", problem, StringComparison.Ordinal);

        // The same set read from the configuration: the container refuses the options (the startup fails).
        var settings = new Dictionary<string, string?>
        {
            ["FiscalDocuments:TaxRules:0:Jurisdiction"] = "IT",
            ["FiscalDocuments:TaxRules:0:Regime"] = "VatOrdinary",
            ["FiscalDocuments:TaxRules:0:ValidFrom"] = "2026-01-01",
            ["FiscalDocuments:TaxRules:0:Version"] = "it-ordinary-test.1",
            ["FiscalDocuments:TaxRules:0:DocumentKind"] = "Invoice",
        };
        foreach (var type in Enum.GetValues<FiscalLineType>())
        {
            var zero = type is FiscalLineType.TouristTax or FiscalLineType.AdvancedExpense;
            settings[$"FiscalDocuments:TaxRules:0:Rates:{type}:Percent"] = zero ? "0" : "22";
            if (type == FiscalLineType.AdvancedExpense)
                settings[$"FiscalDocuments:TaxRules:0:Rates:{type}:Nature"] = "N1";
        }

        var services = new ServiceCollection();
        services.AddCasazenFiscalDocuments(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        var startup = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<FiscalTaxRulesOptions>>().Value);
        Assert.Contains("TouristTax has no nature code", startup.Message, StringComparison.Ordinal);

        // Two versions of one country and regime that overlap, and a line type without a rate.
        var first = ItalianOrdinarySet("it-ordinary-a", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var second = ItalianOrdinarySet("it-ordinary-b", new DateOnly(2026, 6, 1), null);
        second.Rates.Remove(FiscalLineType.Other);
        var problems = FiscalTaxRulesOptionsValidator.Problems(new FiscalTaxRulesOptions { TaxRules = [first, second] });
        Assert.Contains(problems, p => p.Contains("overlap", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("the line type Other has no rate", StringComparison.Ordinal));

        // An empty catalog starts: nothing is issued (document_rule_not_configured).
        Assert.Empty(FiscalTaxRulesOptionsValidator.Problems(new FiscalTaxRulesOptions()));
    }

    /// <summary>
    /// The Italian rules as shipped (I1-I5): the stay at 10% or exempt N4, the cleaning with the stay or at 22% as a service, the extras and the commission at
    /// 22%, the tourist tax and the advanced expenses outside the VAT (N1), every 0% with its nature; the stamp duty on a receipt and on a flat-rate invoice
    /// above 77.47 of amounts without VAT, borne by the issuer.
    /// </summary>
    [Fact]
    public void ItalianRules_AsConfigured_StayTenOrExempt_ExtrasTouristTaxAndStampDuty()
    {
        var resolver = TaxRuleCatalogs.Resolver();
        var ordinary = resolver.Resolve("IT", DocumentRegime.VatOrdinary, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, StayEnd);
        Assert.Equal(("it-ordinary-2026.1", "RF01", RuleKind.Invoice), (ordinary.Version, ordinary.FatturaPaRegimeCode, ordinary.DocumentKind));
        Assert.Equal(new FiscalLineRate(10m, null), ordinary.RateOf(FiscalLineType.Accommodation));
        Assert.Equal(new FiscalLineRate(10m, null), ordinary.RateOf(FiscalLineType.Cleaning));
        Assert.Equal(new FiscalLineRate(22m, null), ordinary.RateOf(FiscalLineType.ExtraService));
        Assert.Equal(new FiscalLineRate(22m, null), ordinary.RateOf(FiscalLineType.Commission));
        Assert.Equal(new FiscalLineRate(0m, "N1"), ordinary.RateOf(FiscalLineType.TouristTax));
        Assert.Equal(new FiscalLineRate(0m, "N1"), ordinary.RateOf(FiscalLineType.AdvancedExpense));

        var exempt = resolver.Resolve("IT", DocumentRegime.VatOrdinary, StayVatTreatment.ExemptArt10, CleaningFeeTreatment.FollowsStay, StayEnd);
        Assert.Equal(new FiscalLineRate(0m, "N4"), exempt.RateOf(FiscalLineType.Accommodation));
        Assert.Equal(new FiscalLineRate(0m, "N4"), exempt.RateOf(FiscalLineType.Cleaning));
        var service = resolver.Resolve("IT", DocumentRegime.VatOrdinary, StayVatTreatment.Standard, CleaningFeeTreatment.Service, StayEnd);
        Assert.Equal(new FiscalLineRate(22m, null), service.RateOf(FiscalLineType.Cleaning));

        var privatePerson = resolver.Resolve("IT", DocumentRegime.PrivatePerson, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, StayEnd);
        var flatRate = resolver.Resolve("IT", DocumentRegime.VatFlatRate, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, StayEnd);
        Assert.Equal(RuleKind.Receipt, privatePerson.DocumentKind);
        Assert.Equal(("RF19", RuleKind.Invoice), (flatRate.FatturaPaRegimeCode, flatRate.DocumentKind));
        Assert.Equal(new FiscalLineRate(0m, "N2.2"), privatePerson.RateOf(FiscalLineType.Accommodation));
        Assert.Equal(new FiscalLineRate(0m, "N2.2"), flatRate.RateOf(FiscalLineType.Accommodation));

        // Every Italian 0% carries a nature, in every rule set and with every treatment.
        foreach (var rule in new[] { ordinary, exempt, service, privatePerson, flatRate })
            Assert.All(rule.Rates.Values.Where(rate => rate.Percent == 0m), rate => Assert.False(string.IsNullOrWhiteSpace(rate.NatureCode)));

        // The stamp duty: due on a receipt and a flat-rate invoice above 77.47 of amounts without VAT, borne by the issuer (not in the total).
        foreach (var rule in new[] { privatePerson, flatRate })
        {
            var above = FiscalDocumentTotals.FromGross([Line(rule, "Soggiorno", 300m, FiscalLineType.Accommodation)], rule.StampDuty);
            Assert.Equal((2.00m, 300m, 0m), (above.StampDuty, above.Total, above.VatAmount));
            var below = FiscalDocumentTotals.FromGross([Line(rule, "Soggiorno", 77.47m, FiscalLineType.Accommodation)], rule.StampDuty);
            Assert.Equal(0m, below.StampDuty);
        }
    }

    /// <summary>
    /// A Swiss document and an Italian receipt need only the name of the recipient (no address, never an Italian fiscal code); an Italian invoice still
    /// needs its identification and address. The document stores the version of its rule.
    /// </summary>
    [Fact]
    public async Task SwissDocumentsAndItalianReceipts_NeedOnlyTheRecipientName_AndStoreTheRuleVersion()
    {
        await using (var swissDb = NewDb())
        {
            var booking = await SeedSwissAsync(swissDb, DocumentRegime.SwissVatRegistered, basePrice: 500m);
            var draft = await Service(swissDb, Mock.Of<IFatturaPaXmlBuilder>()).CreateDraftAsync(booking, null);
            Assert.Equal("ch-vat-2024.1", draft.RuleVersion);
            Assert.Equal("TEST Ospite Svizzero", draft.RecipientName);
            Assert.Null(draft.RecipientFiscalCode);
        }

        await using var db = NewDb();
        var receipt = await SeedItalianAsync(db, DocumentRegime.PrivatePerson);
        var italianReceipt = await Service(db, Mock.Of<IFatturaPaXmlBuilder>()).CreateDraftAsync(receipt, null);
        Assert.Equal(DocumentKind.Receipt, italianReceipt.Kind);
        Assert.Equal("it-private-2026.1", italianReceipt.RuleVersion);

        await using var invoiceDb = NewDb();
        var invoice = await SeedItalianAsync(invoiceDb, DocumentRegime.VatOrdinary);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service(invoiceDb, Mock.Of<IFatturaPaXmlBuilder>()).CreateDraftAsync(invoice, null));
        Assert.Equal(FiscalDocumentErrors.InvoiceDataMissing, refused.Code);
        Assert.Contains(InvoiceDataFields.AddressStreet, Assert.IsAssignableFrom<IReadOnlyList<string>>(refused.Extensions["fields"]));
    }

    /// <summary>
    /// PROOF TEST: money rule. The VAT summary of the XML is the stored VAT of the lines, per rate: lodging 300.02 at 10% and cleaning 50.10 at 22% (as a
    /// service) have the VAT 27.27 and 9.03, while <c>taxable × rate</c> would give 27.28 and 9.04, two cents more than the document. The summary the XML
    /// gets adds up to the VAT of the document and, with the taxable amounts, to its total.
    /// </summary>
    [Fact]
    public async Task FatturaPaSummary_IsTheStoredVatPerRate_NotTaxableTimesRate_Test()
    {
        await using var db = NewDb();
        var booking = await SeedItalianAsync(db, DocumentRegime.VatOrdinary, basePrice: 350.12m, cleaningFee: 50.10m, cleaning: CleaningFeeTreatment.Service, withFullRecipient: true);
        FatturaPaInput? captured = null;
        var xml = new Mock<IFatturaPaXmlBuilder>();
        xml.Setup(x => x.Build(It.IsAny<FatturaPaInput>())).Callback<FatturaPaInput>(input => captured = input).Returns("<p:FatturaElettronica/>");
        var service = Service(db, xml.Object);

        var issued = await service.IssueAsync((await service.CreateDraftAsync(booking, null)).Id, confirm: true);

        Assert.NotNull(captured);
        Assert.Equal(
            new[] { new FiscalDocumentVatSummary(22m, null, 41.07m, 9.03m), new FiscalDocumentVatSummary(10m, null, 272.75m, 27.27m) },
            captured.Summary.ToArray());
        Assert.Equal(36.30m, issued.VatAmount);
        Assert.Equal(issued.VatAmount, captured.Summary.Sum(s => s.Tax));
        Assert.Equal(issued.Total, captured.Summary.Sum(s => s.TaxableAmount + s.Tax));
        Assert.NotEqual(Math.Round(41.07m * 22m / 100m, 2, MidpointRounding.AwayFromZero), captured.Summary[0].Tax);
        Assert.Equal(new[] { FiscalLineType.Accommodation, FiscalLineType.Cleaning }, issued.Lines.OrderBy(l => l.LineNumber).Select(l => l.LineType).ToArray());

        // The lines written before their VAT was stored keep the old summary: the VAT of the document on its one taxed rate.
        var legacy = new[] { new FiscalDocumentLineAmounts(1, "TEST", 1m, 100m, 100m, 22m, null) };
        Assert.Equal(22m, Assert.Single(FiscalDocumentTotals.SummaryOf(legacy, 22m)).Tax);
    }

    private static FiscalDocumentGrossLine Line(FiscalTaxRule rule, string description, decimal gross, FiscalLineType type)
    {
        var rate = rule.RateOf(type);
        return new FiscalDocumentGrossLine(description, 1m, gross, rate.Percent, rate.NatureCode, type);
    }

    private static FiscalTaxRuleSetOptions SwissSet(string version, DateOnly from, DateOnly? to, decimal stay, decimal normal) => new()
    {
        Jurisdiction = "CH",
        Regime = DocumentRegime.SwissVatRegistered,
        ValidFrom = from,
        ValidTo = to,
        Version = version,
        DocumentKind = RuleKind.Invoice,
        Rates = Enum.GetValues<FiscalLineType>().ToDictionary(
            type => type,
            type => new FiscalTaxRateOptions
            {
                Percent = type switch
                {
                    FiscalLineType.Accommodation => stay,
                    FiscalLineType.TouristTax or FiscalLineType.AdvancedExpense => 0m,
                    _ => normal,
                },
            }),
    };

    private static FiscalTaxRuleSetOptions ItalianOrdinarySet(string version, DateOnly from, DateOnly? to) => new()
    {
        Jurisdiction = "IT",
        Regime = DocumentRegime.VatOrdinary,
        ValidFrom = from,
        ValidTo = to,
        Version = version,
        DocumentKind = RuleKind.Invoice,
        FatturaPaRegimeCode = "RF01",
        Rates = Enum.GetValues<FiscalLineType>().ToDictionary(
            type => type,
            type => type is FiscalLineType.TouristTax or FiscalLineType.AdvancedExpense
                ? new FiscalTaxRateOptions { Percent = 0m, Nature = "N1" }
                : new FiscalTaxRateOptions { Percent = type == FiscalLineType.Accommodation ? 10m : 22m }),
    };

    private static FiscalDocumentService Service(AppDbContext db, IFatturaPaXmlBuilder xml)
    {
        var featureFlags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var resolver = TaxRuleCatalogs.Resolver();
        var issuers = new DocumentIssuerService(db, featureFlags, resolver, environment.Object, new FakeTimeProvider(Now), NullLogger<DocumentIssuerService>.Instance);
        var invoiceData = new Mock<IInvoiceDataService>();
        invoiceData.Setup(i => i.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid bookingId, CancellationToken _) => Recipients.TryGetValue(bookingId, out var view) ? view : null);
        return new FiscalDocumentService(
            db,
            issuers,
            invoiceData.Object,
            new FiscalNumberingService(db, new FakeTimeProvider(Now), NullLogger<FiscalNumberingService>.Instance),
            xml,
            Mock.Of<IDomainEventRecorder>(),
            new MigraDocPdfDocumentRenderer(),
            new ResxDocumentTexts(),
            resolver,
            new FakeTimeProvider(Now),
            NullLogger<FiscalDocumentService>.Instance);
    }

    /// <summary>The invoice data of each seeded booking (the mock of the invoice data reads it).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, InvoiceDataView> Recipients = new();

    private static InvoiceDataView NameOnly(Guid bookingId, string name, string country) =>
        new(bookingId, true, Now.UtcDateTime, InvoiceRecipientKind.Guest, name, null, null, null, null, null, null, null, country, null, null, InvoiceDataSource.Host, Now.UtcDateTime);

    private static InvoiceDataView Company(Guid bookingId) =>
        new(bookingId, true, Now.UtcDateTime, InvoiceRecipientKind.Company, "TEST Cliente Srl", null, "IT09876543217", "Via del Cliente", "5",
            "00100", "Roma", "RM", "IT", "ABC1234", null, InvoiceDataSource.Host, Now.UtcDateTime);

    /// <summary>An org with a Swiss profile of <paramref name="regime"/>, a property in CHF and a stay of a portal with a guest known by name only.</summary>
    private static async Task<Guid> SeedSwissAsync(AppDbContext db, DocumentRegime regime, decimal basePrice)
    {
        var orgId = Guid.NewGuid();
        db.Orgs.Add(NewOrg(orgId, hasVat: false));
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = orgId,
            IssuerMode = DocumentIssuerMode.CasaZen,
            DocumentRegime = regime,
            VatRegistered = regime == DocumentRegime.SwissVatRegistered,
            SwissVatNumber = regime == DocumentRegime.SwissVatRegistered ? "CHE-109.322.551" : null,
            LegalName = "TEST Quokka Sagl",
            AddressStreet = "Via Nassa",
            AddressCivic = "5",
            PostalCode = "6900",
            City = "Lugano",
            CountryCode = "CH",
        });
        var booking = await SeedBookingAsync(db, orgId, "CHF", basePrice, cleaningFee: 0m);
        Recipients[booking] = NameOnly(booking, "TEST Ospite Svizzero", "CH");
        return booking;
    }

    /// <summary>An org with an Italian profile of <paramref name="regime"/>, a property in EUR and a stay whose recipient is known by name only, or in full.</summary>
    private static async Task<Guid> SeedItalianAsync(
        AppDbContext db,
        DocumentRegime regime,
        decimal basePrice = 362m,
        decimal cleaningFee = 50m,
        CleaningFeeTreatment cleaning = CleaningFeeTreatment.FollowsStay,
        bool withFullRecipient = false)
    {
        var orgId = Guid.NewGuid();
        db.Orgs.Add(NewOrg(orgId, hasVat: true));
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = orgId,
            IssuerMode = DocumentIssuerMode.CasaZen,
            DocumentRegime = regime,
            VatRegistered = regime != DocumentRegime.PrivatePerson,
            CleaningFeeTreatment = cleaning,
            LegalName = "TEST Casa Vacanze Srl",
            AddressStreet = "Via di Test",
            AddressCivic = "1",
            PostalCode = "00187",
            City = "Roma",
            Province = "RM",
            CountryCode = "IT",
        });
        var booking = await SeedBookingAsync(db, orgId, "EUR", basePrice, cleaningFee);
        Recipients[booking] = withFullRecipient ? Company(booking) : NameOnly(booking, "TEST Ospite", "IT");
        return booking;
    }

    private static async Task<Guid> SeedBookingAsync(AppDbContext db, Guid orgId, string currency, decimal basePrice, decimal cleaningFee)
    {
        var property = new Property { OrgId = orgId, OwnerId = "auth0|host", Name = "TEST Casa", Address = "Via 1", City = "Lugano", Currency = currency };
        var guest = new Guest { OrgId = orgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 14),
            CheckOutDate = new DateTime(2026, 11, 18),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.BookingCom,
            BasePrice = basePrice,
            CleaningFee = cleaningFee,
            TotalPrice = basePrice,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private static OrgEntity NewOrg(Guid id, bool hasVat) => new()
    {
        Id = id,
        Name = "TEST Org",
        Slug = $"test-{id:N}"[..20],
        DisplayName = "TEST Org",
        ContactEmail = "test@example.com",
        HasPartitaIva = hasVat,
        PartitaIvaNumber = hasVat ? "01234567897" : null,
        FiscalCode = hasVat ? "01234567897" : null,
    };

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"TaxRules_{Guid.NewGuid()}").Options, new CatalogTenant(), new EphemeralDataProtectionProvider());
}

/// <summary>The catalog of the tax rules for the tests: the one shipped in <c>appsettings.json</c>, or one built by a test.</summary>
internal static class TaxRuleCatalogs
{
    private static FiscalTaxRulesOptions? _shipped;

    /// <summary>The catalog <c>FiscalDocuments:TaxRules</c> of <c>Casazen.Web/appsettings.json</c>, bound as the application binds it.</summary>
    public static FiscalTaxRulesOptions Shipped()
    {
        if (_shipped is not null)
            return _shipped;

        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "Casazen.Web", "appsettings.json"), optional: false).Build();
        var options = new FiscalTaxRulesOptions();
        configuration.GetSection(FiscalTaxRulesOptions.SectionName).Bind(options);
        return _shipped = options;
    }

    /// <summary>A resolver over <paramref name="options"/>, the shipped catalog by default.</summary>
    public static IFiscalTaxRuleResolver Resolver(FiscalTaxRulesOptions? options = null) => new FiscalTaxRuleResolver(Options.Create(options ?? Shipped()));
}

file sealed class CatalogTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
