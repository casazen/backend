using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// How the fiscal documents of the organization are issued (FIS-02, NF-82, E06/F02/US02/T02, E11/F01/US01/T01): CasaZen issues; the regime and the data of
/// the issuer, what CasaZen may do in this environment and what is still missing for it to issue. An organization that has saved nothing has no
/// <c>mode</c>.
/// </summary>
public sealed class FiscalDocumentSettingsDto
{
    /// <summary><c>CasaZen</c>, the only issuer since 2026-11-14; absent while the organization has saved nothing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }

    /// <summary>
    /// <c>PrivatePerson</c>, <c>VatOrdinary</c> or <c>VatFlatRate</c> (Italy), <c>SwissVatRegistered</c> or <c>SwissNotRegistered</c> (Switzerland); absent
    /// until the issuer says it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Regime { get; init; }

    public required IssuerDataDto Issuer { get; init; }

    /// <summary>When the wizard was completed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ConfirmedAt { get; init; }

    /// <summary>The flag <c>Features:FiscalDocumentIssuance</c>: the option «CasaZen issues» is available only while it is on.</summary>
    public bool CasazenIssuanceEnabled { get; init; }

    /// <summary>The answer to D34 (<c>FiscalDocuments:OctorateIssuesAllBookings</c>): Octorate is the recommended issuer only while it is true.</summary>
    public bool OctorateIssuesAllBookings { get; init; }

    /// <summary>The organization has an active Octorate connection in this environment (the mode Octorate needs it).</summary>
    public bool OctorateConnected { get; init; }

    public required SdiProviderDto Sdi { get; init; }

    /// <summary>What the mode «CasaZen issues» would still need (the names of the fields, <c>vatNumber</c> and <c>fiscalCode</c> are those of the tax profile); empty when the profile is complete.</summary>
    public required IReadOnlyList<string> MissingFields { get; init; }

    /// <summary>The rule of the regime as configured; absent while there is no regime or no rule for it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentRuleDto? Rule { get; init; }

    public static FiscalDocumentSettingsDto From(
        IssuerSettingsView view,
        bool casazenIssuanceEnabled,
        bool octorateIssuesAllBookings,
        bool sdiProviderConfigured,
        string? sdiProviderName)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new FiscalDocumentSettingsDto
        {
            Mode = view.Mode?.ToString(),
            Regime = view.Regime?.ToString(),
            Issuer = IssuerDataDto.From(view),
            ConfirmedAt = view.ConfirmedAt,
            CasazenIssuanceEnabled = casazenIssuanceEnabled,
            OctorateIssuesAllBookings = octorateIssuesAllBookings,
            OctorateConnected = view.OctorateConnected,
            Sdi = new SdiProviderDto { ProviderConfigured = sdiProviderConfigured, ProviderName = sdiProviderConfigured ? sdiProviderName : null },
            MissingFields = view.MissingFields,
            Rule = view.Rule is null ? null : DocumentRuleDto.From(view.Rule),
        };
    }
}

/// <summary>The data of the issuer. <c>vatNumber</c> and <c>fiscalCode</c> are read from the organization and are edited in its tax profile.</summary>
public sealed class IssuerDataDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegalName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AddressStreet { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AddressCivic { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PostalCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Province { get; init; }

    public string CountryCode { get; init; } = "IT";

    /// <summary>The regime code the accountant chose; absent when the one of the rule is used.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FatturaPaRegimeCode { get; init; }

    /// <summary>The regime code that would be used: the one above, else the rule's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EffectiveRegimeCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IssuerSdiRecipientCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IssuerPec { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VatNumber { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FiscalCode { get; init; }

    /// <summary>The issuer is registered for the VAT (it follows the regime).</summary>
    public bool VatRegistered { get; init; }

    /// <summary>The Swiss VAT number (UID) of a registered Swiss issuer, <c>CHE-123.456.789</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SwissVatNumber { get; init; }

    /// <summary>The IBAN the issuer is paid on (encrypted at rest; shown to the organization, printed on its documents).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Iban { get; init; }

    /// <summary>The QR-IBAN of a Swiss issuer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QrIban { get; init; }

    /// <summary><c>Standard</c> or <c>ExemptArt10</c> (⚖ I1).</summary>
    public string StayVatTreatment { get; init; } = nameof(Casazen.Core.Entities.Enums.StayVatTreatment.Standard);

    /// <summary><c>FollowsStay</c> or <c>Service</c> (⚖ C2).</summary>
    public string CleaningFeeTreatment { get; init; } = nameof(Casazen.Core.Entities.Enums.CleaningFeeTreatment.FollowsStay);

    /// <summary>The series pattern of the documents to the guests (<c>{year}</c> is the year); absent = the year.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GuestDocumentSeries { get; init; }

    /// <summary>The series pattern of the invoices to the owners; absent = <c>PM{year}</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerInvoiceSeries { get; init; }

    public bool AutoIssueGuestDocuments { get; init; }

    public bool EmailGuestDocuments { get; init; }

    /// <summary>The channels (<c>BookingSource</c> names) whose portal collects the tourist tax itself.</summary>
    public IReadOnlyList<string> PlatformCollectedTouristTaxChannels { get; init; } = [];

    /// <summary>The authorization of the virtual stamp duty or the number of the physical stamp (Italy).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StampDutyReference { get; init; }

    public bool ConservationAgreementConfirmed { get; init; }

    public bool VirtualStampConfirmed { get; init; }

    public static IssuerDataDto From(IssuerSettingsView view)
    {
        var issuer = view.Issuer;
        return new IssuerDataDto
        {
            LegalName = issuer.LegalName,
            AddressStreet = issuer.AddressStreet,
            AddressCivic = issuer.AddressCivic,
            PostalCode = issuer.PostalCode,
            City = issuer.City,
            Province = issuer.Province,
            CountryCode = issuer.CountryCode,
            FatturaPaRegimeCode = issuer.FatturaPaRegimeCode,
            EffectiveRegimeCode = view.EffectiveRegimeCode,
            IssuerSdiRecipientCode = issuer.IssuerSdiRecipientCode,
            IssuerPec = issuer.IssuerPec,
            VatNumber = view.VatNumber,
            FiscalCode = view.FiscalCode,
            VatRegistered = issuer.VatRegistered,
            SwissVatNumber = issuer.SwissVatNumber,
            Iban = issuer.Iban,
            QrIban = issuer.QrIban,
            StayVatTreatment = issuer.StayVatTreatment.ToString(),
            CleaningFeeTreatment = issuer.CleaningFeeTreatment.ToString(),
            GuestDocumentSeries = issuer.GuestDocumentSeries,
            OwnerInvoiceSeries = issuer.OwnerInvoiceSeries,
            AutoIssueGuestDocuments = issuer.AutoIssueGuestDocuments,
            EmailGuestDocuments = issuer.EmailGuestDocuments,
            PlatformCollectedTouristTaxChannels = issuer.PlatformCollectedTouristTaxChannels.Select(source => source.ToString()).ToList(),
            StampDutyReference = issuer.StampDutyReference,
            ConservationAgreementConfirmed = issuer.ConservationAgreementConfirmed,
            VirtualStampConfirmed = issuer.VirtualStampConfirmed,
        };
    }
}

/// <summary>The e-invoice channel CasaZen has to the SDI in this environment (<c>ISdiEInvoiceProvider</c>).</summary>
public sealed class SdiProviderDto
{
    public bool ProviderConfigured { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderName { get; init; }
}

/// <summary>The rule of the documents of the regime of the issuer, as configured (<c>FiscalDocuments:Rules</c>).</summary>
public sealed class DocumentRuleDto
{
    /// <summary><c>None</c> (not decided yet), <c>Receipt</c>, <c>Invoice</c> or <c>DualHeadingInvoice</c>.</summary>
    public required string DocumentKind { get; init; }

    /// <summary>The rule is complete enough to issue with; false means issuance refuses with <c>document_rule_not_configured</c>.</summary>
    public bool Configured { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FatturaPaRegimeCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? VatRatePercent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VatNatureCode { get; init; }

    public required string InvoiceTypeCode { get; init; }

    public required string CreditNoteTypeCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TouristTaxNatureCode { get; init; }

    public required string SeriesPattern { get; init; }

    public static DocumentRuleDto From(FiscalDocumentRule rule) => new()
    {
        DocumentKind = rule.DocumentKind.ToString(),
        Configured = rule.IsConfigured,
        FatturaPaRegimeCode = string.IsNullOrWhiteSpace(rule.FatturaPaRegimeCode) ? null : rule.FatturaPaRegimeCode,
        VatRatePercent = rule.VatRatePercent,
        VatNatureCode = string.IsNullOrWhiteSpace(rule.VatNatureCode) ? null : rule.VatNatureCode,
        InvoiceTypeCode = rule.InvoiceTypeCode,
        CreditNoteTypeCode = rule.CreditNoteTypeCode,
        TouristTaxNatureCode = string.IsNullOrWhiteSpace(rule.TouristTaxNatureCode) ? null : rule.TouristTaxNatureCode,
        SeriesPattern = rule.SeriesPattern,
    };
}

/// <summary>
/// The answers of the wizard (<c>PUT /api/fiscal/documents/settings</c>). A save replaces the setting as a whole: an issuer text that is left out is
/// cleared, and <c>confirmed</c> is the wizard being completed (refused while something is missing; without it an incomplete profile is saved as a
/// draft). The Italian VAT number and fiscal code are not sent: they live on the organization (the tax profile).
/// </summary>
public sealed class SaveFiscalDocumentSettingsRequest
{
    /// <summary>The name of the old mode «CasaZen issues the direct bookings», read as <c>CasaZen</c> (the same number) for the clients that still send it.</summary>
    private const string LegacyCasazenModeName = "CasaZenDirectOnly";

    /// <summary>
    /// <c>CasaZen</c> (also when absent). <c>Octorate</c> and <c>Accountant</c> are retired: 422 <c>issuer_mode_retired</c>, and nothing is saved.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary><c>PrivatePerson</c>, <c>VatOrdinary</c>, <c>VatFlatRate</c> (Italy), <c>SwissVatRegistered</c> or <c>SwissNotRegistered</c> (Switzerland).</summary>
    public string? Regime { get; init; }

    public SaveIssuerDataRequest? Issuer { get; init; }

    public bool Confirmed { get; init; }

    /// <summary>
    /// The command of the service. A mode, a regime or a treatment that is not one of the names above is 422 <c>issuer_field_invalid</c> on its field.
    /// </summary>
    public SaveIssuerSettingsCommand ToCommand()
    {
        var failures = new List<InvoiceDataFailure>();
        var modeName = string.Equals(Mode?.Trim(), LegacyCasazenModeName, StringComparison.OrdinalIgnoreCase) ? nameof(DocumentIssuerMode.CasaZen) : Mode;
        var mode = ParseEnum<DocumentIssuerMode>(modeName, required: false, IssuerFields.Mode, failures) ?? DocumentIssuerMode.CasaZen;
        var regime = ParseEnum<DocumentRegime>(Regime, required: false, IssuerFields.Regime, failures);
        var issuer = Issuer;
        var stay = ParseEnum<StayVatTreatment>(issuer?.StayVatTreatment, required: false, IssuerFields.StayVatTreatment, failures);
        var cleaning = ParseEnum<CleaningFeeTreatment>(issuer?.CleaningFeeTreatment, required: false, IssuerFields.CleaningFeeTreatment, failures);
        if (failures.Count > 0)
        {
            throw new Casazen.Core.Exceptions.DomainRuleException(
                DocumentIssuerErrorCodes.FieldInvalid, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.FieldInvalid))
            {
                Extensions = new Dictionary<string, object?>
                {
                    ["field"] = failures[0].Field,
                    ["errors"] = failures.Select(f => new { field = f.Field, code = f.Code }).ToList(),
                },
            };
        }

        return new SaveIssuerSettingsCommand
        {
            Mode = mode,
            Regime = regime,
            LegalName = issuer?.LegalName,
            AddressStreet = issuer?.AddressStreet,
            AddressCivic = issuer?.AddressCivic,
            PostalCode = issuer?.PostalCode,
            City = issuer?.City,
            Province = issuer?.Province,
            CountryCode = issuer?.CountryCode,
            FatturaPaRegimeCode = issuer?.FatturaPaRegimeCode,
            IssuerSdiRecipientCode = issuer?.IssuerSdiRecipientCode,
            IssuerPec = issuer?.IssuerPec,
            VatRegistered = issuer?.VatRegistered,
            SwissVatNumber = issuer?.SwissVatNumber,
            Iban = issuer?.Iban,
            QrIban = issuer?.QrIban,
            StayVatTreatment = stay,
            CleaningFeeTreatment = cleaning,
            GuestDocumentSeries = issuer?.GuestDocumentSeries ?? issuer?.DefaultSeries,
            OwnerInvoiceSeries = issuer?.OwnerInvoiceSeries,
            AutoIssueGuestDocuments = issuer?.AutoIssueGuestDocuments ?? false,
            EmailGuestDocuments = issuer?.EmailGuestDocuments ?? false,
            PlatformCollectedTouristTaxChannels = issuer?.PlatformCollectedTouristTaxChannels,
            StampDutyReference = issuer?.StampDutyReference,
            ConservationAgreementConfirmed = issuer?.ConservationAgreementConfirmed ?? false,
            VirtualStampConfirmed = issuer?.VirtualStampConfirmed ?? false,
            Confirmed = Confirmed,
        };
    }

    private static T? ParseEnum<T>(string? text, bool required, string field, List<InvoiceDataFailure> failures)
        where T : struct, Enum
    {
        var name = text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            if (required)
                failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, field));
            return null;
        }

        if (!Enum.TryParse<T>(name, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || name.All(char.IsAsciiDigit))
        {
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, field));
            return null;
        }

        return parsed;
    }
}

/// <summary>The issuer's data of <see cref="SaveFiscalDocumentSettingsRequest"/>.</summary>
public sealed class SaveIssuerDataRequest
{
    public string? LegalName { get; init; }

    public string? AddressStreet { get; init; }

    public string? AddressCivic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    public string? CountryCode { get; init; }

    public string? FatturaPaRegimeCode { get; init; }

    public string? IssuerSdiRecipientCode { get; init; }

    public string? IssuerPec { get; init; }

    /// <summary>The old name of <see cref="GuestDocumentSeries"/>, still read when that one is absent.</summary>
    public string? DefaultSeries { get; init; }

    /// <summary>Registered for the VAT; absent = what the regime says. A value that contradicts the regime is 422 on <c>vatRegistered</c>.</summary>
    public bool? VatRegistered { get; init; }

    /// <summary>The UID of a registered Swiss issuer (<c>CHE-123.456.789</c>, with or without separators or the suffix <c>MWST</c>/<c>TVA</c>/<c>IVA</c>).</summary>
    public string? SwissVatNumber { get; init; }

    public string? Iban { get; init; }

    public string? QrIban { get; init; }

    /// <summary><c>Standard</c> or <c>ExemptArt10</c>; absent = <c>Standard</c>.</summary>
    public string? StayVatTreatment { get; init; }

    /// <summary><c>FollowsStay</c> or <c>Service</c>; absent = <c>FollowsStay</c>.</summary>
    public string? CleaningFeeTreatment { get; init; }

    public string? GuestDocumentSeries { get; init; }

    public string? OwnerInvoiceSeries { get; init; }

    public bool AutoIssueGuestDocuments { get; init; }

    public bool EmailGuestDocuments { get; init; }

    /// <summary>The names of the channels (<c>BookingSource</c>) whose portal collects the tourist tax itself.</summary>
    public IReadOnlyList<string>? PlatformCollectedTouristTaxChannels { get; init; }

    public string? StampDutyReference { get; init; }

    public bool ConservationAgreementConfirmed { get; init; }

    public bool VirtualStampConfirmed { get; init; }
}

/// <summary>Who would issue the documents of a booking and why (<c>GET /api/fiscal/documents/issuer-preview</c>).</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="Issuer"><c>None</c>, <c>Octorate</c> (a legacy lock) or <c>CasaZen</c>.</param>
/// <param name="Reason">A stable code: <c>issuer_casazen</c>, <c>issuer_locked_octorate</c> or <c>issuance_off</c>.</param>
public sealed record IssuerPreviewDto(Guid BookingId, string Issuer, string Reason);
