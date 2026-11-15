using Casazen.Core.Documents;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The words of the files of the accountant package and of the agency summary (E15/F01/US01/T01), read from the resource files (<c>SharedResources.resx</c> and
/// <c>.en.resx</c>) through <see cref="IDocumentTexts"/>: no text of a file is written in the code. The package is written in one language, Italian (the language of the
/// reports it embeds, which are Italian whatever the language of the caller), so a file for the accountant never mixes two languages.
/// </summary>
internal sealed class PackageTexts(IDocumentTexts texts, string language)
{
    /// <summary>The language of the package: Italian, as the host reports are.</summary>
    public const string PackageLanguage = DocumentLanguages.Italian;

    /// <summary>The language the words are read in.</summary>
    public string Language { get; } = language;

    /// <summary>The source of the words, for the builders that read them by themselves (the word of a kind of document).</summary>
    public IDocumentTexts Source => texts;

    /// <summary>The words of <paramref name="key"/> with <paramref name="args"/> put in its placeholders.</summary>
    public string Get(string key, params object[] args) => texts.Get(key, Language, args);

    /// <summary>The words of <paramref name="key"/>.</summary>
    public string this[string key] => texts.Get(key, Language);
}

/// <summary>The keys of the resource files the accountant package reads, every one in both languages (a test keeps the two files and this list together).</summary>
internal static class PackageLabels
{
    // Registers and rates.
    public const string GuestRegisterTitle = "AccPkgGuestRegisterTitle";
    public const string OwnerRegisterTitle = "AccPkgOwnerRegisterTitle";
    public const string RateDetailTitle = "AccPkgRateDetailTitle";
    public const string RateTotalsTitle = "AccPkgRateTotalsTitle";
    public const string ColPaymentChannel = "AccPkgColPaymentChannel";
    public const string ColPeriod = "AccPkgColPeriod";
    public const string ColRate = "AccPkgColRate";
    public const string ColNature = "AccPkgColNature";
    public const string ColDocuments = "AccPkgColDocuments";
    public const string ColAmount = "AccPkgColAmount";
    public const string ColChannel = "AccPkgColChannel";
    public const string ColIssuer = "AccPkgColIssuer";
    public const string ChannelCasaZen = "AccPkgChannelCasaZen";
    public const string ChannelPortal = "AccPkgChannelPortal";
    public const string ChannelOutside = "AccPkgChannelOutside";
    public const string ChannelUnknown = "AccPkgChannelUnknown";
    public const string RateUnknown = "AccPkgRateUnknown";

    // VAT.
    public const string VatSwissTitle = "AccPkgVatSwissTitle";
    public const string VatOutsideTitle = "AccPkgVatOutsideTitle";
    public const string VatPortalTitle = "AccPkgVatPortalTitle";
    public const string VatItalyTitle = "AccPkgVatItalyTitle";
    public const string VatOwnerInvoicesOmittedTitle = "AccPkgVatOwnerInvoicesOmittedTitle";
    public const string VatOwnerInvoicesOmittedReason = "AccPkgVatOwnerInvoicesOmittedReason";
    public const string ColTurnover = "AccPkgColTurnover";
    public const string ColVatDue = "AccPkgColVatDue";
    public const string ColClass = "AccPkgColClass";
    public const string ClassOrdinary = "AccPkgClassOrdinary";
    public const string ClassFlatRate = "AccPkgClassFlatRate";
    public const string ClassReceipt = "AccPkgClassReceipt";
    public const string OutsideTouristTax = "AccPkgOutsideTouristTax";
    public const string OutsideExpenses = "AccPkgOutsideExpenses";
    public const string OutsideOther = "AccPkgOutsideOther";

    // Statements.
    public const string StatementsTitle = "AccPkgStatementsTitle";
    public const string ColOwner = "AccPkgColOwner";
    public const string ColVersion = "AccPkgColVersion";
    public const string ColPublishedOn = "AccPkgColPublishedOn";
    public const string ColRevenue = "AccPkgColRevenue";
    public const string ColChannelFees = "AccPkgColChannelFees";
    public const string ColCommission = "AccPkgColCommission";
    public const string ColCharges = "AccPkgColCharges";
    public const string ColPayments = "AccPkgColPayments";
    public const string ColClosingBalance = "AccPkgColClosingBalance";
    public const string ColSha = "AccPkgColSha";

    // Withholding.
    public const string WithholdingTitle = "AccPkgWithholdingTitle";
    public const string F24Title = "AccPkgF24Title";
    public const string CuTitle = "AccPkgCuTitle";
    public const string ColPayoutCount = "AccPkgColPayoutCount";
    public const string ColGrossBase = "AccPkgColGrossBase";
    public const string ColWithheld = "AccPkgColWithheld";
    public const string ColTaxCode = "AccPkgColTaxCode";
    public const string ColDueDate = "AccPkgColDueDate";
    public const string ColPaidOn = "AccPkgColPaidOn";
    public const string ColFilingState = "AccPkgColFilingState";
    public const string ColRuleVersion = "AccPkgColRuleVersion";
    public const string ColEvidence = "AccPkgColEvidence";

    // Tourist tax.
    public const string SwissTouristTaxTitle = "AccPkgSwissTouristTaxTitle";
    public const string ColCanton = "AccPkgColCanton";
    public const string ColCollected = "AccPkgColCollected";
    public const string CantonUnknown = "AccPkgCantonUnknown";

    // The index.
    public const string IndexTitle = "AccPkgIndexTitle";
    public const string IndexColDescription = "AccPkgIndexColDescription";
    public const string IndexColRows = "AccPkgIndexColRows";
    public const string IndexOmittedTitle = "AccPkgIndexOmittedTitle";
    public const string IndexColFolder = "AccPkgIndexColFolder";
    public const string IndexColReason = "AccPkgIndexColReason";
    public const string OmittedOwnerAccounting = "AccPkgOmittedOwnerAccounting";
    public const string OmittedNotSelected = "AccPkgOmittedNotSelected";

    // The agency summary.
    public const string SummaryTitle = "AccPkgSummaryTitle";
    public const string SumStaysTitle = "AccPkgSumStaysTitle";
    public const string SumOccupiedNights = "AccPkgSumOccupiedNights";
    public const string SumAvailableNights = "AccPkgSumAvailableNights";
    public const string SumOccupancy = "AccPkgSumOccupancy";
    public const string SumRevenue = "AccPkgSumRevenue";
    public const string SumStays = "AccPkgSumStays";
    public const string SumAdr = "AccPkgSumAdr";
    public const string SumRevPar = "AccPkgSumRevPar";
    public const string SumGeneratedOn = "AccPkgSumGeneratedOn";
    public const string SumCollectionsTitle = "AccPkgSumCollectionsTitle";
    public const string SumBalancesTitle = "AccPkgSumBalancesTitle";
    public const string SumCollected = "AccPkgSumCollected";
    public const string SumPayments = "AccPkgSumPayments";
    public const string SumToCollect = "AccPkgSumToCollect";
    public const string SumGuestDocsTitle = "AccPkgSumGuestDocsTitle";
    public const string SumPortalCostsTitle = "AccPkgSumPortalCostsTitle";
    public const string SumCostKind = "AccPkgSumCostKind";
    public const string SumGross = "AccPkgSumGross";
    public const string SumNetPaid = "AccPkgSumNetPaid";
    public const string SumCost = "AccPkgSumCost";
    public const string SumNet = "AccPkgSumNet";
    public const string SumCostRate = "AccPkgSumCostRate";
    public const string SumTotalRow = "AccPkgSumTotalRow";
    public const string SumTouristTaxTitle = "AccPkgSumTouristTaxTitle";
    public const string SumArea = "AccPkgSumArea";
    public const string SumAreaItaly = "AccPkgSumAreaItaly";
    public const string SumAreaSwitzerland = "AccPkgSumAreaSwitzerland";
    public const string SumTaxCollected = "AccPkgSumTaxCollected";
    public const string SumTaxRemitted = "AccPkgSumTaxRemitted";
    public const string SumTaxToRemit = "AccPkgSumTaxToRemit";
    public const string SumTaxIncomplete = "AccPkgSumTaxIncomplete";
    public const string SumCommissionsTitle = "AccPkgSumCommissionsTitle";
    public const string SumCommissionTaxable = "AccPkgSumCommissionTaxable";
    public const string SumCommissionVat = "AccPkgSumCommissionVat";
    public const string SumCommissionTotal = "AccPkgSumCommissionTotal";
    public const string SumRechargeTaxable = "AccPkgSumRechargeTaxable";
    public const string SumRechargeVat = "AccPkgSumRechargeVat";
    public const string SumRechargeTotal = "AccPkgSumRechargeTotal";
    public const string SumAdvanced = "AccPkgSumAdvanced";
    public const string SumInvoiced = "AccPkgSumInvoiced";
    public const string SumOwnerInvoicesTitle = "AccPkgSumOwnerInvoicesTitle";
    public const string SumOwnerInvoicesRatesTitle = "AccPkgSumOwnerInvoicesRatesTitle";
    public const string SumInvoices = "AccPkgSumInvoices";
    public const string SumCreditNotes = "AccPkgSumCreditNotes";
    public const string SumPayoutsTitle = "AccPkgSumPayoutsTitle";
    public const string SumPayouts = "AccPkgSumPayouts";
    public const string SumWithholding = "AccPkgSumWithholding";
    public const string SumWiresTitle = "AccPkgSumWiresTitle";
    public const string SumBatches = "AccPkgSumBatches";
    public const string SumTransfers = "AccPkgSumTransfers";
    public const string WireGenerated = "AccPkgWireGenerated";
    public const string WireDownloaded = "AccPkgWireDownloaded";
    public const string WirePaid = "AccPkgWirePaid";
    public const string SumPortalBorneTitle = "AccPkgSumPortalBorneTitle";
    public const string SumMarginTitle = "AccPkgSumMarginTitle";
    public const string SumMargin = "AccPkgSumMargin";
    public const string SumPortalBorne = "AccPkgSumPortalBorne";
    public const string SumOwnersTitle = "AccPkgSumOwnersTitle";
    public const string SumOwnersUnassigned = "AccPkgSumOwnersUnassigned";
    public const string SumProperties = "AccPkgSumProperties";
    public const string SumOwnerAccountingOmitted = "AccPkgSumOwnerAccountingOmitted";

    // The documents issued in the name of an owner, for its accountant (E17/F02/US01/T01).
    public const string OwnDocsTotalsTitle = "AccPkgOwnDocsTotalsTitle";
    public const string OwnDocsSummaryTitle = "AccPkgOwnDocsSummaryTitle";
    public const string OwnDocsAgencyInvoicesTitle = "AccPkgOwnDocsAgencyInvoicesTitle";
    public const string OwnDocsAgencyInvoiceDetailTitle = "AccPkgOwnDocsAgencyInvoiceDetailTitle";

    // Errors.
    public const string TooLargeMessage = "AccountantPackageTooLarge";
    public const string InvalidPeriodMessage = "AccountantPackageInvalidPeriod";
    public const string OwnerIssuedDocumentNotFoundMessage = "OwnerIssuedDocumentNotFound";
    public const string OwnerIssuedDocumentsNotAvailableMessage = "OwnerIssuedDocumentsNotAvailable";

    /// <summary>Every key above: the test that reads the two resource files asks for each of them.</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. typeof(PackageLabels)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];
}
