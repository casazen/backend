using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IBillingSetupService"/> (NF-82, NF-104, NF-118, E11/F01/US01/T04): the sections of the wizard «Fatturazione» over the setting of the issuer
/// (<see cref="IDocumentIssuerService"/>), and the missing items of every <see cref="IBillingSetupContributor"/> registered.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The profile of the issuer is the only store.</b> A save of a section is the setting as it is now with the fields of the payload laid over it, saved
/// by <see cref="IDocumentIssuerService.SaveSettingsAsync"/>: its validation, its field codes, its flag and its tenant rules are the ones of the issuer
/// setting, and what the section does not carry (the IBAN, the other sections) is saved back as it was. No second copy of any value, no migration.</item>
/// <item><b>Draft and idempotent.</b> A field that is not sent keeps its value; the same answers saved twice leave the same setting. A refusal saves nothing.</item>
/// <item><b>A regime follows the country.</b> Changing the country of the issuer drops what only made sense in the other one: its regime (the step «IVA» asks
/// again), the postal code and the province (asked again, unless the same save carries them); the VAT registration of a Swiss issuer, and of an Italian one
/// that is not registered, tells the regime where only one fits.</item>
/// <item><b>Open to the other epics.</b> The missing items are asked of every contributor of the section, in the order they were registered; a section with
/// none does not apply and is not counted. The save of a section another epic owns (<c>channels</c>, <c>mandates</c>) is that epic's endpoint, not this one.</item>
/// <item><b>Italian tourist tax.</b> For an Italian issuer the section <c>touristTax</c> also carries, read only, the proposals for the comuni of its properties and the data-quality hint of
/// <see cref="IItalianTouristTaxSetup"/> (FIX/BE-IT-TOURISTTAX-DEFAULTS): nothing is saved or applied by reading them, and a service built without it carries none.</item>
/// <item><b>Logs</b> carry the org, the section and the number of fields: never a value of the setting.</item>
/// </list>
/// </remarks>
public sealed class BillingSetupService(
    IDocumentIssuerService issuers,
    IEnumerable<IBillingSetupContributor> contributors,
    ILogger<BillingSetupService> logger,
    IPortalAccountSettingsService? portalAccounts = null,
    IFeatureFlags? featureFlags = null,
    IGuestDocumentLineBuilder? documentLines = null,
    IItalianTouristTaxSetup? italianTouristTax = null) : IBillingSetupService
{
    /// <summary>
    /// The key of the section <c>touristTax</c> that lists the portals that collect the tourist tax by default, for an issuer that has not confirmed its own list yet:
    /// read-only, the default of its country as the guest documents read it (<c>SwissTouristTax:PlatformCollected</c> for a Swiss issuer, <c>ItalianTouristTax:PlatformCollected</c>, none by
    /// default, for an Italian one), never copied into the profile.
    /// </summary>
    public const string DefaultPlatformCollectedTouristTaxChannels = "defaultPlatformCollectedTouristTaxChannels";

    public async Task<BillingSetup> GetAsync(Guid orgId, CancellationToken ct = default)
    {
        var view = await issuers.GetSettingsAsync(orgId, ct);
        var evaluations = await EvaluateAsync(orgId, new SettingReader(issuers, orgId, view), null, ct);
        var channels = await ChannelValuesAsync(orgId, ct);
        var italianTax = await ItalianTouristTaxValuesAsync(orgId, view.Issuer, ct);
        return new BillingSetup([.. evaluations.Select(evaluation => SectionOf(evaluation, view, channels, italianTax))]);
    }

    public async Task<BillingSetupSection> PutSectionAsync(
        Guid orgId,
        string section,
        BillingSetupPayload payload,
        string? updatedByUserId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var name = BillingSetupSections.Of(section)
            ?? throw new NotFoundException("The name is not a section of the billing setup")
            {
                Code = BillingSetupErrorCodes.SectionUnknown,
                MessageKey = BillingSetupErrorCodes.MessageKeyOf(BillingSetupErrorCodes.SectionUnknown),
            };
        if (!BillingSetupSections.Writable.Contains(name))
        {
            throw new DomainRuleException(BillingSetupErrorCodes.SectionNotWritable, BillingSetupErrorCodes.MessageKeyOf(BillingSetupErrorCodes.SectionNotWritable))
            {
                Extensions = new Dictionary<string, object?> { ["section"] = name },
            };
        }

        var provided = payload.ProvidedFields();
        var allowed = BillingSetupSections.FieldsOf(name);
        var strays = provided.Where(field => !allowed.Contains(field)).ToList();
        if (strays.Count > 0)
            throw Refusal(BillingSetupErrorCodes.FieldNotInSection, BillingSetupErrorCodes.MessageKeyOf(BillingSetupErrorCodes.FieldNotInSection), strays);

        var current = await issuers.GetSettingsAsync(orgId, ct);
        if (provided.Count == 0)
            return await SectionAfterAsync(orgId, name, current, ct);

        var command = Merge(current, payload);
        var saved = name == BillingSetupSections.Automation && current.ConfirmedAt is null
            ? await SaveConfirmingAsync(orgId, command, updatedByUserId, ct)
            : await issuers.SaveSettingsAsync(orgId, command, updatedByUserId, ct);
        logger.LogInformation("Billing setup section {Section} of org {OrgId} saved: {FieldCount} fields", name, orgId, provided.Count);

        return await SectionAfterAsync(orgId, name, saved, ct);
    }

    public async Task<BillingSetupProgress> GetProgressAsync(Guid orgId, IReadOnlyCollection<string>? sections = null, CancellationToken ct = default)
    {
        var evaluations = await EvaluateAsync(orgId, new SettingReader(issuers, orgId, null), sections, ct);
        var applicable = evaluations.Where(evaluation => evaluation.Applies).ToList();
        return new BillingSetupProgress(
            applicable.Count(evaluation => evaluation.Items.Count == 0),
            applicable.Count,
            [.. evaluations.SelectMany(evaluation => evaluation.Items)]);
    }

    /// <summary>
    /// The setting with the fields of the payload laid over it, as the command that saves it whole: what the payload does not carry is the value it has now. The
    /// IBAN and the QR-IBAN go back as they were (they belong to no section), the VAT registration follows the regime, and the confirmation is the caller's.
    /// </summary>
    private static SaveIssuerSettingsCommand Merge(IssuerSettingsView view, BillingSetupPayload payload)
    {
        var issuer = view.Issuer;

        // The channels stay as they were (unconfirmed stays unconfirmed) unless the payload carries them.
        var channels = payload.PlatformCollectedTouristTaxChannels;
        if (channels is null && issuer.PlatformCollectedTouristTaxChannelsConfirmed)
            channels = [.. issuer.PlatformCollectedTouristTaxChannels.Select(source => source.ToString())];

        // A new country drops what only made sense in the old one: the postal code (four digits in Switzerland, five in Italy) and the province (the canton of a
        // Swiss issuer) are asked again, unless the same save carries them.
        var newCountry = payload.CountryCode is not null && CountryOf(payload.CountryCode) != CountryOf(issuer.CountryCode);

        var command = new SaveIssuerSettingsCommand
        {
            Mode = DocumentIssuerMode.CasaZen,
            Regime = view.Regime,
            LegalName = payload.LegalName ?? issuer.LegalName,
            AddressStreet = payload.AddressStreet ?? issuer.AddressStreet,
            AddressCivic = payload.AddressCivic ?? issuer.AddressCivic,
            PostalCode = payload.PostalCode ?? (newCountry ? null : issuer.PostalCode),
            City = payload.City ?? issuer.City,
            Province = payload.Province ?? (newCountry ? null : issuer.Province),
            CountryCode = payload.CountryCode ?? issuer.CountryCode,
            FatturaPaRegimeCode = payload.FatturaPaRegimeCode ?? issuer.FatturaPaRegimeCode,
            IssuerSdiRecipientCode = issuer.IssuerSdiRecipientCode,
            IssuerPec = issuer.IssuerPec,
            SwissVatNumber = payload.SwissVatNumber ?? issuer.SwissVatNumber,
            Iban = issuer.Iban,
            QrIban = issuer.QrIban,
            StayVatTreatment = payload.StayVatTreatment ?? issuer.StayVatTreatment,
            CleaningFeeTreatment = payload.CleaningFeeTreatment ?? issuer.CleaningFeeTreatment,
            GuestDocumentSeries = payload.GuestDocumentSeries ?? issuer.GuestDocumentSeries,
            OwnerInvoiceSeries = payload.OwnerInvoiceSeries ?? issuer.OwnerInvoiceSeries,
            AutoIssueGuestDocuments = payload.AutoIssueGuestDocuments ?? issuer.AutoIssueGuestDocuments,
            EmailGuestDocuments = payload.EmailGuestDocuments ?? issuer.EmailGuestDocuments,
            PlatformCollectedTouristTaxChannels = channels,
            StampDutyReference = payload.StampDutyReference ?? issuer.StampDutyReference,
            ConservationAgreementConfirmed = payload.ConservationAgreementConfirmed ?? issuer.ConservationAgreementConfirmed,
            VirtualStampConfirmed = payload.VirtualStampConfirmed ?? issuer.VirtualStampConfirmed,
        };

        var country = CountryOf(command.CountryCode);
        if (command.Regime is { } kept && !FiscalDocumentRules.IsRegimeOfCountry(kept, country))
            command = command with { Regime = null };

        // The regime of the payload, else the one the VAT registration tells (a Swiss issuer, an Italian one that is not registered), else the one it has.
        if (payload.Regime is { } chosen)
            command = command with { Regime = chosen };
        else if (payload.VatRegistered is { } registered && RegimeOfRegistration(country, registered) is { } told)
            command = command with { Regime = told };

        var vatRegistered = payload.VatRegistered;
        if (vatRegistered is null && command.Regime is null)
            vatRegistered = issuer.VatRegistered;

        return command with { VatRegistered = vatRegistered };
    }

    /// <summary>The regime a VAT registration decides alone: Swiss (registered or not), and the private person of an Italian issuer that is not registered.</summary>
    private static DocumentRegime? RegimeOfRegistration(string country, bool registered)
    {
        if (FiscalDocumentRules.IsSwiss(country))
            return registered ? DocumentRegime.SwissVatRegistered : DocumentRegime.SwissNotRegistered;

        return registered ? null : DocumentRegime.PrivatePerson;
    }

    private async Task<IssuerSettingsView> SaveConfirmingAsync(Guid orgId, SaveIssuerSettingsCommand command, string? updatedByUserId, CancellationToken ct)
    {
        try
        {
            return await issuers.SaveSettingsAsync(orgId, command with { Confirmed = true }, updatedByUserId, ct);
        }
        catch (DomainRuleException ex) when (ex.Code == DocumentIssuerErrorCodes.ProfileIncomplete)
        {
            return await issuers.SaveSettingsAsync(orgId, command, updatedByUserId, ct);
        }
    }

    private async Task<BillingSetupSection> SectionAfterAsync(Guid orgId, string name, IssuerSettingsView view, CancellationToken ct)
    {
        var evaluations = await EvaluateAsync(orgId, new SettingReader(issuers, orgId, view), [name], ct);
        var italianTax = name == BillingSetupSections.TouristTax ? await ItalianTouristTaxValuesAsync(orgId, view.Issuer, ct) : null;
        return SectionOf(evaluations.Single(), view, null, italianTax);
    }

    private BillingSetupSection SectionOf(
        Evaluation evaluation,
        IssuerSettingsView view,
        IReadOnlyDictionary<string, object?>? channels = null,
        IReadOnlyDictionary<string, object?>? italianTax = null)
    {
        if (evaluation.Section == BillingSetupSections.Channels && channels is not null)
            return new BillingSetupSection(evaluation.Section, evaluation.Applies, channels, evaluation.Items);

        var values = ValuesOf(evaluation.Section, view);
        if (evaluation.Section == BillingSetupSections.TouristTax && italianTax is not null)
        {
            foreach (var (key, value) in italianTax)
                values[key] = value;
        }

        return new BillingSetupSection(evaluation.Section, evaluation.Applies, values, evaluation.Items);
    }

    /// <summary>
    /// What the section <c>touristTax</c> adds for an Italian issuer (FIX/BE-IT-TOURISTTAX-DEFAULTS): the proposals «Airbnb incassa, da confermare» for the comuni of its properties
    /// (<see cref="ItalianTouristTaxSetupKeys.Proposals"/>) and the data-quality hint (<see cref="ItalianTouristTaxSetupKeys.ChannelDataConflict"/>), read from the organization's rule exactly as the
    /// guest documents read it. <c>null</c> for any other issuer, and for a service built without the reader or without the line builder (it has no rule to compare with): nothing is made up.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, object?>?> ItalianTouristTaxValuesAsync(Guid orgId, IssuerData issuer, CancellationToken ct)
    {
        if (italianTouristTax is null || documentLines is null || CountryOf(issuer.CountryCode) != FiscalDocumentRules.Italy)
            return null;

        var channels = issuer.PlatformCollectedTouristTaxChannelsConfirmed ? issuer.PlatformCollectedTouristTaxChannels : DefaultChannelsOf(issuer.CountryCode);
        var setup = await italianTouristTax.ReadAsync(orgId, channels, issuer.PlatformCollectedTouristTaxChannelsConfirmed, ct);
        return new Dictionary<string, object?>
        {
            [ItalianTouristTaxSetupKeys.Proposals] = setup.Proposals,
            [ItalianTouristTaxSetupKeys.ChannelDataConflict] = setup.ChannelDataConflict,
            [ItalianTouristTaxSetupKeys.ConfirmedComuni] = setup.ConfirmedComuni,
        };
    }

    private async Task<IReadOnlyDictionary<string, object?>?> ChannelValuesAsync(Guid orgId, CancellationToken ct)
    {
        if (portalAccounts is null || featureFlags?.IsEnabled(FeatureFlags.OwnerInvoicing) != true)
            return null;

        var settings = await portalAccounts.GetAsync(orgId, ct);
        return new Dictionary<string, object?>
        {
            [PortalAccountFields.PortalAccountHolder] = settings.PortalAccountHolder.ToString(),
            [PortalAccountFields.RechargePortalCommissions] = settings.RechargePortalCommissions,
        };
    }

    private async Task<IReadOnlyList<Evaluation>> EvaluateAsync(Guid orgId, SettingReader setting, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        var wanted = only?.Select(NameOf).ToHashSet(StringComparer.Ordinal);
        var registered = contributors.ToLookup(contributor => NameOf(contributor.Section), StringComparer.Ordinal);
        var names = BillingSetupSections.All.Concat(registered.Select(group => group.Key).Where(name => !BillingSetupSections.All.Contains(name))).ToList();

        var evaluations = new List<Evaluation>(names.Count);
        foreach (var name in names)
        {
            if (wanted is not null && !wanted.Contains(name))
                continue;

            var applies = false;
            var items = new List<BillingSetupItem>();
            foreach (var contributor in registered[name])
            {
                IReadOnlyList<BillingSetupItem>? found;
                if (contributor is IssuerSetupContributor reader)
                {
                    var view = await setting.GetAsync(ct);
                    if (!reader.Applies(view))
                        continue;

                    found = reader.Evaluate(view);
                }
                else
                {
                    if (!await contributor.AppliesAsync(orgId, ct))
                        continue;

                    found = await contributor.EvaluateAsync(orgId, ct);
                }

                applies = true;
                items.AddRange((found ?? []).Select(item => item with { Section = name }));
            }

            evaluations.Add(new Evaluation(name, applies, items));
        }

        return evaluations;
    }

    private static string NameOf(string? section) => BillingSetupSections.Of(section) ?? section?.Trim() ?? string.Empty;

    private static string CountryOf(string? countryCode) =>
        string.IsNullOrWhiteSpace(countryCode) ? OrgFiscalIssuerProfile.DefaultCountryCode : countryCode.Trim().ToUpperInvariant();

    /// <summary>
    /// The portals that collect the tourist tax by default for an issuer of <paramref name="countryCode"/> that has confirmed none, by the names of
    /// <see cref="Casazen.Core.Entities.Enums.BookingSource"/>: exactly what the guest documents say of a profile with nothing confirmed
    /// (<see cref="IGuestDocumentLineBuilder.PlatformCollectedChannelsOf"/>: the shipped default of a Swiss issuer, <c>ItalianTouristTax:PlatformCollected</c>, none by default, for an
    /// Italian one), so the wizard and the documents never disagree. Empty when the service is built without the line builder.
    /// </summary>
    private IReadOnlyList<string> PlatformDefaultsOf(string? countryCode) => [.. DefaultChannelsOf(countryCode).Select(source => source.ToString())];

    private IReadOnlyList<BookingSource> DefaultChannelsOf(string? countryCode)
    {
        if (documentLines is null)
            return [];

        var unconfirmed = new OrgFiscalIssuerProfile { CountryCode = CountryOf(countryCode) };
        return documentLines.PlatformCollectedChannelsOf(unconfirmed);
    }

    private Dictionary<string, object?> ValuesOf(string section, IssuerSettingsView view)
    {
        var issuer = view.Issuer;
        switch (section)
        {
            case BillingSetupSections.Issuer:
                return new Dictionary<string, object?>
                {
                    [BillingSetupFields.CountryCode] = issuer.CountryCode,
                    [BillingSetupFields.LegalName] = issuer.LegalName,
                    [BillingSetupFields.AddressStreet] = issuer.AddressStreet,
                    [BillingSetupFields.AddressCivic] = issuer.AddressCivic,
                    [BillingSetupFields.PostalCode] = issuer.PostalCode,
                    [BillingSetupFields.City] = issuer.City,
                    [BillingSetupFields.Province] = issuer.Province,
                    [BillingSetupFields.SwissVatNumber] = issuer.SwissVatNumber,

                    ["vatNumber"] = view.VatNumber,
                    ["fiscalCode"] = view.FiscalCode,
                };
            case BillingSetupSections.Vat:
                return new Dictionary<string, object?>
                {
                    [BillingSetupFields.Regime] = view.Regime?.ToString(),
                    [BillingSetupFields.VatRegistered] = issuer.VatRegistered,
                    [BillingSetupFields.SwissVatNumber] = issuer.SwissVatNumber,
                    [BillingSetupFields.FatturaPaRegimeCode] = issuer.FatturaPaRegimeCode,
                    ["effectiveRegimeCode"] = view.EffectiveRegimeCode,
                    [BillingSetupFields.StayVatTreatment] = issuer.StayVatTreatment.ToString(),
                    [BillingSetupFields.CleaningFeeTreatment] = issuer.CleaningFeeTreatment.ToString(),
                    ["ruleVersion"] = view.Rule?.Version,
                };
            case BillingSetupSections.Numbering:
                return new Dictionary<string, object?>
                {
                    [BillingSetupFields.GuestDocumentSeries] = issuer.GuestDocumentSeries,
                    [BillingSetupFields.OwnerInvoiceSeries] = issuer.OwnerInvoiceSeries,
                    ["defaultGuestDocumentSeries"] = FiscalNumberFormat.DefaultPattern,
                    ["defaultOwnerInvoiceSeries"] = OrgFiscalIssuerProfile.DefaultOwnerInvoiceSeries,
                };
            case BillingSetupSections.TouristTax:
                return new Dictionary<string, object?>
                {
                    ["confirmed"] = issuer.PlatformCollectedTouristTaxChannelsConfirmed,
                    [BillingSetupFields.PlatformCollectedTouristTaxChannels] = issuer.PlatformCollectedTouristTaxChannelsConfirmed
                        ? issuer.PlatformCollectedTouristTaxChannels.Select(source => source.ToString()).ToList()
                        : null,

                    [DefaultPlatformCollectedTouristTaxChannels] = PlatformDefaultsOf(issuer.CountryCode),
                };
            case BillingSetupSections.Automation:
                return new Dictionary<string, object?>
                {
                    [BillingSetupFields.AutoIssueGuestDocuments] = issuer.AutoIssueGuestDocuments,
                    [BillingSetupFields.EmailGuestDocuments] = issuer.EmailGuestDocuments,
                    ["confirmedAt"] = view.ConfirmedAt,
                };
            case BillingSetupSections.Italy:
                return new Dictionary<string, object?>
                {
                    [BillingSetupFields.StampDutyReference] = issuer.StampDutyReference,
                    [BillingSetupFields.ConservationAgreementConfirmed] = issuer.ConservationAgreementConfirmed,
                    [BillingSetupFields.VirtualStampConfirmed] = issuer.VirtualStampConfirmed,
                };
            default:
                return new Dictionary<string, object?>();
        }
    }

    private static DomainRuleException Refusal(string code, string messageKey, IReadOnlyList<string> fields) =>
        new(code, messageKey)
        {
            Extensions = new Dictionary<string, object?>
            {
                ["field"] = fields[0],
                ["errors"] = fields.Select(field => new { field, code }).ToList(),
            },
        };

    private sealed record Evaluation(string Section, bool Applies, IReadOnlyList<BillingSetupItem> Items);

    private sealed class SettingReader(IDocumentIssuerService issuers, Guid orgId, IssuerSettingsView? known)
    {
        private IssuerSettingsView? _view = known;

        public async Task<IssuerSettingsView> GetAsync(CancellationToken ct) => _view ??= await issuers.GetSettingsAsync(orgId, ct);
    }
}
