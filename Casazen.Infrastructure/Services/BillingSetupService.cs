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
/// <item><b>Logs</b> carry the org, the section and the number of fields: never a value of the setting.</item>
/// </list>
/// </remarks>
public sealed class BillingSetupService(
    IDocumentIssuerService issuers,
    IEnumerable<IBillingSetupContributor> contributors,
    ILogger<BillingSetupService> logger,
    IPortalAccountSettingsService? portalAccounts = null,
    IFeatureFlags? featureFlags = null) : IBillingSetupService
{
    public async Task<BillingSetup> GetAsync(Guid orgId, CancellationToken ct = default)
    {
        var view = await issuers.GetSettingsAsync(orgId, ct);
        var evaluations = await EvaluateAsync(orgId, new SettingReader(issuers, orgId, view), null, ct);
        var channels = await ChannelValuesAsync(orgId, ct);
        return new BillingSetup([.. evaluations.Select(evaluation => SectionOf(evaluation, view, channels))]);
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

        // Every field the payload carries belongs to the section: a step never saves another step's answers by the way.
        var provided = payload.ProvidedFields();
        var allowed = BillingSetupSections.FieldsOf(name);
        var strays = provided.Where(field => !allowed.Contains(field)).ToList();
        if (strays.Count > 0)
            throw Refusal(BillingSetupErrorCodes.FieldNotInSection, BillingSetupErrorCodes.MessageKeyOf(BillingSetupErrorCodes.FieldNotInSection), strays);

        var current = await issuers.GetSettingsAsync(orgId, ct);
        if (provided.Count == 0)
            return await SectionAfterAsync(orgId, name, current, ct);

        // The step «Emissione» confirms itself while the profile can issue; one already confirmed keeps its time (the same answers saved again change nothing).
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

        // A regime is the regime of a country: the one chosen for the country the issuer had does not follow it to another.
        var country = CountryOf(command.CountryCode);
        if (command.Regime is { } kept && !FiscalDocumentRules.IsRegimeOfCountry(kept, country))
            command = command with { Regime = null };

        // The regime of the payload, else the one the VAT registration tells (a Swiss issuer, an Italian one that is not registered), else the one it has.
        if (payload.Regime is { } chosen)
            command = command with { Regime = chosen };
        else if (payload.VatRegistered is { } registered && RegimeOfRegistration(country, registered) is { } told)
            command = command with { Regime = told };

        // The registration follows the regime; without one it is what the issuer answered (the answer of the payload, else the one it had).
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

    /// <summary>
    /// Saves the step of the automatic issue and confirms it when the profile can issue; a profile that cannot yet is saved as a draft (the issuer service refuses
    /// the confirmation before it writes anything), and the item <c>auto_issue_unconfirmed</c> stays until the step is saved again with a complete profile.
    /// </summary>
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

    /// <summary>The section as it is after a save (or as it is, when nothing was saved), from the setting the save answered.</summary>
    private async Task<BillingSetupSection> SectionAfterAsync(Guid orgId, string name, IssuerSettingsView view, CancellationToken ct)
    {
        var evaluations = await EvaluateAsync(orgId, new SettingReader(issuers, orgId, view), [name], ct);
        return SectionOf(evaluations.Single(), view);
    }

    /// <summary>The section with its values: the ones of the profile of the issuer, and for <c>channels</c> the defaults of the portal accounts (E14/F01/US01/T01).</summary>
    private static BillingSetupSection SectionOf(Evaluation evaluation, IssuerSettingsView view, IReadOnlyDictionary<string, object?>? channels = null) =>
        new(
            evaluation.Section,
            evaluation.Applies,
            evaluation.Section == BillingSetupSections.Channels && channels is not null ? channels : ValuesOf(evaluation.Section, view),
            evaluation.Items);

    /// <summary>
    /// The values of the section <c>channels</c> the owner invoice adds (E14/F01/US01/T01, ⚖ I10, I11): who holds the accounts of the portals by default and
    /// whether their commissions are recharged, saved by <c>PUT api/billing-setup/channels</c>; <c>null</c> without the settings of the portal accounts and while
    /// <c>Features:OwnerInvoicing</c> is off (the endpoints that save them answer 404 then, so the section shows nothing it could not save).
    /// </summary>
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

    /// <summary>
    /// The sections asked (all when <paramref name="only"/> is <c>null</c>), in the order of the wizard and then the ones an epic added, each with the items of its
    /// contributors in the order they were registered. A section with no contributor, or whose only contributors say it does not apply, does not apply.
    /// </summary>
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

    /// <summary>The country as the issuer service stores it: upper case, Italy when blank.</summary>
    private static string CountryOf(string? countryCode) =>
        string.IsNullOrWhiteSpace(countryCode) ? OrgFiscalIssuerProfile.DefaultCountryCode : countryCode.Trim().ToUpperInvariant();

    /// <summary>What is saved in a section, by the names of <see cref="BillingSetupFields"/>; the bank accounts of the issuer are in none. A section no epic fills here has none.</summary>
    private static Dictionary<string, object?> ValuesOf(string section, IssuerSettingsView view)
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

                    // The Italian tax ids are those of the organization (its tax profile): read here, never copied.
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

    /// <summary>A refusal with the code of the first failure and the list of the fields, in the shape of the issuer setting (<c>field</c> and <c>errors</c>).</summary>
    private static DomainRuleException Refusal(string code, string messageKey, IReadOnlyList<string> fields) =>
        new(code, messageKey)
        {
            Extensions = new Dictionary<string, object?>
            {
                ["field"] = fields[0],
                ["errors"] = fields.Select(field => new { field, code }).ToList(),
            },
        };

    /// <summary>What one section found: whether it applies, and its missing items.</summary>
    private sealed record Evaluation(string Section, bool Applies, IReadOnlyList<BillingSetupItem> Items);

    /// <summary>The setting of the organization, read at most once for all the contributors that need it (or given, when the caller has just read or saved it).</summary>
    private sealed class SettingReader(IDocumentIssuerService issuers, Guid orgId, IssuerSettingsView? known)
    {
        private IssuerSettingsView? _view = known;

        public async Task<IssuerSettingsView> GetAsync(CancellationToken ct) => _view ??= await issuers.GetSettingsAsync(orgId, ct);
    }
}
