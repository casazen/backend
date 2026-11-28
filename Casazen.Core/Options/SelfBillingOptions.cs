using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Validation;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>SelfBilling</c> (E17/F01/US03/T01, NF-116, D4): the settings of the self-billed invoices (TD17) CasaZen prepares for the commissions that foreign portals
/// charge an Italian account holder. Every value is a prudent default to be confirmed by an accountant (⚖) and is never written in the code. Nothing here stops the
/// application at startup: a portal entry that is incomplete or not a portal is ignored, and the commissions of that portal are listed as «portal not configured» by the
/// generation of the drafts (never invoiced with a guessed supplier); an invalid VAT rate makes the generation refuse with 422 <c>self_billing_not_configured</c>. Runbook
/// <c>docs/runbooks/fiscal-documents.md</c> § Self-billed invoices (TD17), <c>docs/runbooks/deploy-checklist.md</c> § 2.
/// </summary>
public class SelfBillingOptions
{
    public const string SectionName = "SelfBilling";

    /// <summary>The VAT integrated on the commission, in percent, when nothing is configured (⚖: the ordinary Italian rate, art. 17 c. 2 DPR 633/72).</summary>
    public const decimal DefaultVatRatePercent = 22m;

    public const int DefaultIssueByDay = 15;

    public const string DefaultSeriesPattern = "TD17-{year}";

    public const string DefaultRuleVersion = "td17-2026.11-1";

    public List<SelfBillingPortalOptions> Portals { get; set; } = [];

    /// <summary>The VAT integrated on the commissions, in percent (⚖). Above 0 and at most 100; anything else refuses the generation.</summary>
    public decimal VatRatePercent { get; set; } = DefaultVatRatePercent;

    public int IssueByDay { get; set; } = DefaultIssueByDay;

    public string SeriesPattern { get; set; } = DefaultSeriesPattern;

    public string RuleVersion { get; set; } = DefaultRuleVersion;

    /// <summary>
    /// The run that makes the drafts of the month that ended (<c>SelfBilling:MonthlyDrafts</c>, FIX/BE-TD17-ALERTS, FIX/BE-TD17-JOB-EXIT): <b>on by default</b>, every day from the 5th to the 14th of the month,
    /// because the TD17 is due by the 15th and the draft has no fiscal effect until a person issues it (the real risk is not preparing it).
    /// </summary>
    public SelfBillingMonthlyDraftsOptions MonthlyDrafts { get; set; } = new();

    public bool HasValidVatRate => VatRatePercent is > 0m and <= 100m;

    public int EffectiveIssueByDay => Math.Clamp(IssueByDay, 1, 28);

    public string EffectiveSeriesPattern => string.IsNullOrWhiteSpace(SeriesPattern) ? DefaultSeriesPattern : SeriesPattern.Trim();

    public string EffectiveRuleVersion
    {
        get
        {
            var version = string.IsNullOrWhiteSpace(RuleVersion) ? DefaultRuleVersion : RuleVersion.Trim();
            return version.Length <= FiscalDocument.RuleVersionMaxLength ? version : version[..FiscalDocument.RuleVersionMaxLength];
        }
    }

    /// <summary>
    /// The portals that can be invoiced, by channel: the entries that name a portal (<see cref="FiscalCopy.IsOtaBookingSource"/>, not the generic
    /// <see cref="BookingSource.OtherChannel"/>) with a name, a supplier abroad (an ISO country other than Italy), a VAT number and an address. The first valid entry of a
    /// channel wins; the others are ignored, as an entry that is not valid.
    /// </summary>
    public IReadOnlyDictionary<BookingSource, SelfBillingPortal> ConfiguredPortals()
    {
        var portals = new Dictionary<BookingSource, SelfBillingPortal>();
        foreach (var entry in Portals ?? [])
        {
            if (entry?.ToPortal() is { } portal && !portals.ContainsKey(portal.Channel))
                portals[portal.Channel] = portal;
        }

        return portals;
    }

    public int IgnoredPortalEntries() => (Portals?.Count ?? 0) - ConfiguredPortals().Count;
}

/// <summary>
/// The section <c>SelfBilling:MonthlyDrafts</c> (FIX/BE-TD17-ALERTS, FIX/BE-TD17-JOB-EXIT, ⚖): the Hangfire job <c>self-billing-monthly-drafts</c> makes, for every agency with commissions in the owner ledger of the
/// month that ended, the same drafts as <c>POST api/fiscal/self-billing/drafts</c> (only the missing ones) and raises the «Da fare» to check and issue them, every day from the 5th to the 14th. It never
/// issues: the issue stays a person's action, behind <c>agency.invoice.manage</c> and <c>Features:FiscalDocumentIssuance</c>. Documented in <c>docs/runbooks/deploy-checklist.md</c> § 2.
/// </summary>
public class SelfBillingMonthlyDraftsOptions
{
    public bool Enabled { get; set; } = true;
}

public class SelfBillingPortalOptions
{
    public string? Channel { get; set; }

    public string? Name { get; set; }

    /// <summary>The ISO 3166-1 alpha-2 country of the entity; never <c>IT</c> (an Italian supplier invoices with VAT and needs no TD17).</summary>
    public string? Country { get; set; }

    /// <summary>The VAT number of the entity, with or without the country prefix (<c>NL805734958B01</c>).</summary>
    public string? VatNumber { get; set; }

    public string? Street { get; set; }

    public string? Civic { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public SelfBillingPortal? ToPortal()
    {
        if (!EnumNames.TryParseDefined<BookingSource>(Channel, out var channel)
            || !FiscalCopy.IsOtaBookingSource(channel)
            || channel == BookingSource.OtherChannel)
        {
            return null;
        }

        var country = Country?.Trim().ToUpperInvariant();
        var name = Clean(Name);
        var street = Clean(Street);
        var city = Clean(City);
        if (country is not { Length: 2 } || !country.All(char.IsAsciiLetterUpper) || country == FiscalDocumentRules.Italy
            || name is null || street is null || city is null)
        {
            return null;
        }

        var code = SelfBillingRules.VatCodeOf(VatNumber, country);
        if (code is null)
            return null;

        return new SelfBillingPortal(
            channel,
            Truncate(name, FiscalDocument.RecipientNameMaxLength),
            country,
            code,
            Truncate(street, FiscalDocument.RecipientAddressStreetMaxLength),
            Clean(Civic) is { } civic ? Truncate(civic, FiscalDocument.RecipientAddressCivicMaxLength) : null,
            Clean(PostalCode) is { } postalCode ? Truncate(postalCode, FiscalDocument.RecipientPostalCodeMaxLength) : null,
            Truncate(city, FiscalDocument.RecipientCityMaxLength));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// The legal entity of a foreign portal, valid and ready to be the cedente of a TD17.
/// </summary>
/// <param name="Channel">The channel it invoices.</param>
/// <param name="Name">The legal name.</param>
/// <param name="CountryCode">The ISO country (never Italy).</param>
/// <param name="VatCode">The VAT number without the country prefix, upper case, letters and digits only.</param>
/// <param name="Street">The street.</param>
/// <param name="Civic">The house number.</param>
/// <param name="PostalCode">The postal code as written.</param>
/// <param name="City">The city.</param>
public sealed record SelfBillingPortal(
    BookingSource Channel,
    string Name,
    string CountryCode,
    string VatCode,
    string Street,
    string? Civic,
    string? PostalCode,
    string City)
{
    /// <summary>The VAT number with its country prefix, as the document stores it (<c>NL805734958B01</c>).</summary>
    public string VatNumber => string.Create(CultureInfo.InvariantCulture, $"{CountryCode}{VatCode}");

    /// <summary>What identifies the entity across the documents: the country and the VAT code (two channels of one legal entity make one TD17).</summary>
    public string Key => SelfBillingRules.PortalKey(CountryCode, VatCode);
}
