using Casazen.Core.Models;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A contributor of the billing setup that reads the setting of the issuer (E11/F01/US01/T04): <see cref="IDocumentIssuerService.GetSettingsAsync"/> is the one
/// definition of what the issuer still has to give, so the items never drift from what <c>issuer_profile_incomplete</c> says. The service that gathers the
/// contributors reads the setting once and hands it to every contributor of this kind (<see cref="Evaluate"/>); asked alone (<see cref="EvaluateAsync"/>) a
/// contributor reads it itself. A contributor of another epic implements <see cref="IBillingSetupContributor"/> and nothing more.
/// </summary>
/// <param name="issuers">The issuer service: the setting of the organization, its missing fields and the tax rule in force.</param>
public abstract class IssuerSetupContributor(IDocumentIssuerService issuers) : IBillingSetupContributor
{
    public abstract string Section { get; }

    /// <summary>False when the section has nothing to do for the issuer of <paramref name="view"/> (the Swiss tourist tax of an Italian issuer): it is then not counted.</summary>
    public virtual bool Applies(IssuerSettingsView view) => true;

    /// <summary>The items missing in the setting <paramref name="view"/>, once <see cref="Applies"/> said yes.</summary>
    public abstract IReadOnlyList<BillingSetupItem> Evaluate(IssuerSettingsView view);

    public async Task<IReadOnlyList<BillingSetupItem>> EvaluateAsync(Guid orgId, CancellationToken ct)
    {
        var view = await issuers.GetSettingsAsync(orgId, ct);
        return Applies(view) ? Evaluate(view) : [];
    }

    /// <summary>An item of this section.</summary>
    protected BillingSetupItem Item(string key, string reason) => new(key, Section, reason);
}

/// <summary>
/// The section «Chi emette» (E11/F01/US01/T04): the country (while the organization saved nothing), the legal name, the structured address and the tax ids:
/// the Italian VAT number or fiscal code of the organization (what the regime asks, saved in its tax profile) and the Swiss UID of a registered Swiss issuer.
/// </summary>
public sealed class IssuerSectionContributor(IDocumentIssuerService issuers) : IssuerSetupContributor(issuers)
{
    public override string Section => BillingSetupSections.Issuer;

    public override IReadOnlyList<BillingSetupItem> Evaluate(IssuerSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var missing = view.MissingFields;
        var items = new List<BillingSetupItem>();

        // No profile at all: the issuer has not even said which country it issues from.
        if (view.Mode is null)
            items.Add(Item(BillingSetupItemKeys.CountryMissing, BillingSetupReasons.Required));
        if (missing.Contains(IssuerFields.LegalName))
            items.Add(Item(BillingSetupItemKeys.LegalNameMissing, BillingSetupReasons.Required));
        if (missing.Contains(IssuerFields.AddressStreet) || missing.Contains(IssuerFields.PostalCode)
            || missing.Contains(IssuerFields.City) || missing.Contains(IssuerFields.Province))
        {
            items.Add(Item(BillingSetupItemKeys.AddressMissing, BillingSetupReasons.Required));
        }

        if (missing.Contains(IssuerFields.VatNumber))
            items.Add(Item(BillingSetupItemKeys.VatNumberMissing, BillingSetupReasons.Required));
        if (missing.Contains(IssuerFields.FiscalCode))
            items.Add(Item(BillingSetupItemKeys.FiscalCodeMissing, BillingSetupReasons.Required));
        if (missing.Contains(IssuerFields.SwissVatNumber))
            items.Add(Item(BillingSetupItemKeys.SwissUidMissing, BillingSetupReasons.Required));

        return items;
    }
}

/// <summary>
/// The section «IVA» (E11/F01/US01/T04): the regime (the VAT registration follows it), the FatturaPA regime code of a VAT regime, and the tax rule in force for the
/// regime with the treatments of the stay and of the cleaning (⚖ I1, C2: they have prudent defaults, so they are never absent; what can be missing is a rule
/// of the catalog that covers the choice, because without it nothing is issued).
/// </summary>
public sealed class VatSectionContributor(IDocumentIssuerService issuers) : IssuerSetupContributor(issuers)
{
    public override string Section => BillingSetupSections.Vat;

    public override IReadOnlyList<BillingSetupItem> Evaluate(IssuerSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var items = new List<BillingSetupItem>();
        if (view.MissingFields.Contains(IssuerFields.Regime))
            items.Add(Item(BillingSetupItemKeys.RegimeMissing, BillingSetupReasons.Required));
        if (view.MissingFields.Contains(IssuerFields.FatturaPaRegimeCode))
            items.Add(Item(BillingSetupItemKeys.RegimeCodeMissing, BillingSetupReasons.Required));

        // The regime is chosen and the catalog has no rule in force for it today: nothing would be issued with a rate nobody approved.
        if (view.Regime is not null && view.Rule is null)
            items.Add(Item(BillingSetupItemKeys.TaxRuleMissing, BillingSetupReasons.Unavailable));

        return items;
    }
}

/// <summary>
/// The section «Numerazione» (E11/F01/US01/T04): both series, the one of the documents to the guests and the one of the invoices to the owners. A series that
/// was never saved has the default (the year, <c>PM{year}</c>), which is shown but not yet confirmed: the step saves it explicitly.
/// </summary>
public sealed class NumberingSectionContributor(IDocumentIssuerService issuers) : IssuerSetupContributor(issuers)
{
    public override string Section => BillingSetupSections.Numbering;

    public override IReadOnlyList<BillingSetupItem> Evaluate(IssuerSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var items = new List<BillingSetupItem>();
        if (string.IsNullOrWhiteSpace(view.Issuer.GuestDocumentSeries))
            items.Add(Item(BillingSetupItemKeys.GuestSeriesUnconfirmed, BillingSetupReasons.Unconfirmed));
        if (string.IsNullOrWhiteSpace(view.Issuer.OwnerInvoiceSeries))
            items.Add(Item(BillingSetupItemKeys.OwnerSeriesUnconfirmed, BillingSetupReasons.Unconfirmed));

        return items;
    }
}

/// <summary>
/// The section «Tassa di soggiorno» (E11/F01/US01/T04), for a Swiss issuer: the portals that collect the tourist tax themselves are confirmed at least once,
/// even to say that none does (a default of the configuration alone does not count). An Italian issuer has the municipal rules of the compliance, not this.
/// </summary>
public sealed class TouristTaxSectionContributor(IDocumentIssuerService issuers) : IssuerSetupContributor(issuers)
{
    public override string Section => BillingSetupSections.TouristTax;

    public override bool Applies(IssuerSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return FiscalDocumentRules.IsSwiss(view.Issuer.CountryCode);
    }

    public override IReadOnlyList<BillingSetupItem> Evaluate(IssuerSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return view.Issuer.PlatformCollectedTouristTaxChannelsConfirmed
            ? []
            : [Item(BillingSetupItemKeys.TouristTaxChannelsUnconfirmed, BillingSetupReasons.Unconfirmed)];
    }
}

/// <summary>
/// The section «Emissione» (E11/F01/US01/T04): the choice of the automatic issue (and of the e-mail to the guest) is made once. The profile has no «not chosen»
/// state for a yes or no, so the choice counts as made when the step was saved while the profile of the issuer could issue (<c>ConfirmedAt</c>, which the
/// issuer service clears whenever the profile stops being complete): until then the item <c>auto_issue_unconfirmed</c> is missing.
/// </summary>
public sealed class AutomationSectionContributor(IDocumentIssuerService issuers) : IssuerSetupContributor(issuers)
{
    public override string Section => BillingSetupSections.Automation;

    public override IReadOnlyList<BillingSetupItem> Evaluate(IssuerSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return view.ConfirmedAt is null
            ? [Item(BillingSetupItemKeys.AutoIssueUnconfirmed, BillingSetupReasons.Unconfirmed)]
            : [];
    }
}

/// <summary>
/// The section «Italia» (E11/F01/US01/T04), only for an Italian issuer: the agreement for the free electronic conservation of the documents is confirmed, and
/// so is the stamp duty: the reference of the virtual stamp or of the physical one, or the confirmation that the virtual stamp is paid (⚖ I4). The owner
/// issuers and the SDI add their items to this section (E17).
/// </summary>
public sealed class ItalySectionContributor(IDocumentIssuerService issuers) : IssuerSetupContributor(issuers)
{
    public override string Section => BillingSetupSections.Italy;

    public override bool Applies(IssuerSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return string.Equals(view.Issuer.CountryCode, FiscalDocumentRules.Italy, StringComparison.OrdinalIgnoreCase);
    }

    public override IReadOnlyList<BillingSetupItem> Evaluate(IssuerSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var items = new List<BillingSetupItem>();
        if (!view.Issuer.ConservationAgreementConfirmed)
            items.Add(Item(BillingSetupItemKeys.ConservationAgreementUnconfirmed, BillingSetupReasons.Unconfirmed));
        if (string.IsNullOrWhiteSpace(view.Issuer.StampDutyReference) && !view.Issuer.VirtualStampConfirmed)
            items.Add(Item(BillingSetupItemKeys.StampDutyUnconfirmed, BillingSetupReasons.Unconfirmed));

        return items;
    }
}
