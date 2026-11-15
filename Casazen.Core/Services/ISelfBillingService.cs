using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using FiscalDocumentKind = Casazen.Core.Options.FiscalDocumentKind;

namespace Casazen.Core.Services;

/// <summary>
/// The self-billed invoices (Italian TD17) of the commissions that foreign portals charge an Italian account holder (E17/F01/US03/T01, NF-116, D4, decision P1 of
/// 2026-11-14): a <see cref="FiscalDocument"/> with the purpose <see cref="FiscalDocumentPurpose.SelfBilling"/>, one for each holder, portal and month, whose
/// cedente is the legal entity of the portal (<c>SelfBilling:Portals</c>) and whose cessionario is the holder, issued by CasaZen on the holder's issuer profile with its
/// own series (the TD17 are numbered apart from the sales documents).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Who needs one</b> (<see cref="IPortalAccountHolderResolver"/>, ⚖ I10, the one reader of who holds an account). <b>V1</b>, the owner holds the account: an owner
/// company or a professional with an Italian VAT number (a complete issuer profile) gets the TD17, the owner who signs for the property on the check-out day (the rule of the
/// document plan, <see cref="DocumentPlanRules.SignatoryOf"/>); a private person has none (the portal charged the Italian VAT). <b>V2</b>, the agency holds it: the TD17 is the
/// agency's (and the owner invoice recharges the commission, E14); a Swiss agency has none. A holder that needs a TD17 and cannot get one yet (its profile is incomplete) is
/// reported, never invoiced by a guess.</item>
/// <item><b>The amounts.</b> The taxable amount is the sum of the <see cref="OwnerLedgerEntryType.ChannelFee"/> lines of the month of the listings the holder holds, in the
/// currency of the stay (euro only: the TD17 of an Italian holder is in euro and nothing is converted), a reversal and the line it reverses cancelling out; one line for each
/// property. The VAT is integrated at <see cref="SelfBillingOptions.VatRatePercent"/> (⚖, 22%) on top of it, never read from the tax rules of the sales documents (a flat-rate
/// regime integrates the VAT too).</item>
/// <item><b>Drafts.</b> One live document for a holder, a portal entity and a month (the draft made again from the ledger, an issued one left alone and reported when the ledger
/// changed since); the generation of a month is taken one at a time per organization. The issue goes through the issue of the fiscal documents
/// (<see cref="IFiscalDocumentService.IssueSelfBillingAsync"/>): the gap-free number in the series of the holder, the snapshot of the issuer, the Italian XML with
/// <c>SoggettoEmittente</c> <c>CC</c>, the state <c>to_send</c> for the SDI; and only while the draft still is what the ledger gives (409 <c>document_draft_outdated</c>
/// otherwise).</item>
/// <item><b>Tenant and reach.</b> Every method names the org; a document of another org is 404. The web layer decides who may call: the whole organization only (a TD17 spans
/// the properties of a holder).</item>
/// </list>
/// </remarks>
public interface ISelfBillingService
{
    /// <summary>
    /// The drafts of a month for every holder and portal that needs one: made, or made again when a draft exists, left alone when it is issued (reported with
    /// <see cref="SelfBillingOutcome.ChangedSinceIssue"/>). The commissions that cannot be self-billed are in the answer with the reason (<see cref="SelfBillingNotDue"/>,
    /// <see cref="SelfBillingFailure"/>). 422 <c>self_billing_period_invalid</c>, <c>self_billing_not_configured</c> (no VAT rate, no portal).
    /// </summary>
    Task<SelfBillingGeneration> CreateDraftsForPeriodAsync(Guid orgId, int year, int month, string? userId, CancellationToken ct = default);

    /// <summary>The TD17 of the org (drafts and issued ones, not the discarded ones), the newest period first.</summary>
    Task<IReadOnlyList<SelfBillingRow>> ListAsync(Guid orgId, SelfBillingCriteria criteria, CancellationToken ct = default);

    /// <summary>One TD17 with its lines. 404 <c>fiscal_document_not_found</c> (another org, not a TD17).</summary>
    Task<SelfBillingDetail> GetAsync(Guid orgId, Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// The draft issued after the confirmation (422 <c>document_confirmation_required</c>): numbered, with its XML, to be sent to the SDI. 404
    /// <c>fiscal_document_not_found</c>; 422 <c>document_not_draft</c>, <c>self_billing_not_configured</c>; 409 <c>document_draft_outdated</c> when the ledger, the holder, the
    /// portal or the settings would give another document now (make the drafts of the month again first).
    /// </summary>
    Task<SelfBillingDetail> IssueAsync(Guid orgId, Guid documentId, bool confirm, string? userId, CancellationToken ct = default);

    /// <summary>The draft discarded (no number is consumed): its holder, portal and month are free for a new draft. 404 <c>fiscal_document_not_found</c>; 422 <c>document_not_draft</c>.</summary>
    Task DiscardDraftAsync(Guid orgId, Guid documentId, CancellationToken ct = default);
}

/// <summary>Who a TD17 is made for.</summary>
public enum SelfBillingHolderKind
{
    /// <summary>The agency holds the account (V2): the TD17 is the agency's.</summary>
    Agency = 1,

    /// <summary>The owner holds the account (V1): the TD17 is an owner company's, issued through the agency.</summary>
    Owner = 2,
}

/// <summary>The filters of the list: a month (year and month together).</summary>
public sealed record SelfBillingCriteria(int? Year = null, int? Month = null);

/// <summary>One TD17 of the list: its holder, the portal, the month and its deadline, the amounts, the status and the state towards the SDI.</summary>
/// <param name="HolderOwnerId">The owner whose TD17 it is; <c>null</c> for the agency's.</param>
/// <param name="HolderName">The legal name of the holder (as it was at the issue, for an issued document).</param>
/// <param name="PortalName">The legal name of the foreign portal (the cedente).</param>
/// <param name="PortalCountry">The country of the portal.</param>
/// <param name="DueDate">The day by which the TD17 is issued (⚖ <see cref="SelfBillingOptions.IssueByDay"/> of the month after the commissions).</param>
public sealed record SelfBillingRow(
    Guid Id,
    SelfBillingHolderKind HolderKind,
    Guid? HolderOwnerId,
    string HolderName,
    string PortalName,
    string PortalCountry,
    int PeriodYear,
    int PeriodMonth,
    DateOnly DueDate,
    string? Number,
    DateOnly? IssueDate,
    FiscalDocumentStatus Status,
    decimal TaxableBase,
    decimal VatAmount,
    decimal Total,
    string Currency,
    string SdiStatus);

/// <summary>A TD17 with its lines (<see cref="Document"/>) and its row.</summary>
public sealed record SelfBillingDetail(FiscalDocument Document, SelfBillingRow Row);

/// <summary>What the drafts of a month did.</summary>
/// <param name="Discarded">The drafts of the month the ledger no longer supports (their commissions are gone, their holder or portal changed): dropped, they had no number.</param>
/// <param name="Drafts">One row for each holder and portal that needs a TD17 and could get one: the document, what was done to it, its amounts.</param>
/// <param name="Failed">The holders that need a TD17 and could not get one now, with the code of the reason (an incomplete issuer profile).</param>
/// <param name="NotDue">The commissions of the month that are not self-billed, with the reason: they tell the host what was left out and why.</param>
public sealed record SelfBillingGeneration(
    int Year,
    int Month,
    int Created,
    int Updated,
    int AlreadyIssued,
    int Discarded,
    IReadOnlyList<SelfBillingOutcome> Drafts,
    IReadOnlyList<SelfBillingFailure> Failed,
    IReadOnlyList<SelfBillingNotDue> NotDue);

/// <summary>One TD17 of a generation.</summary>
/// <param name="Result">One of <see cref="SelfBillingResults"/>.</param>
/// <param name="EstimatedAmount">The part of the taxable amount that comes from a rule of the agency or the default of the channel, not from the figure of the portal: to be checked against the invoice of the portal before the issue.</param>
/// <param name="ChangedSinceIssue">The TD17 is issued and the ledger of the month now gives another taxable amount: it is not changed, the host is told.</param>
public sealed record SelfBillingOutcome(
    Guid DocumentId,
    SelfBillingHolderKind HolderKind,
    Guid? HolderOwnerId,
    string PortalName,
    string Result,
    decimal TaxableBase,
    decimal EstimatedAmount,
    bool ChangedSinceIssue);

/// <summary>A holder that needs a TD17 and cannot get one: the code of the reason (<c>owner_issuer_missing</c>, <c>issuer_profile_incomplete</c>) and the fields it misses.</summary>
public sealed record SelfBillingFailure(SelfBillingHolderKind HolderKind, Guid? HolderOwnerId, string PortalName, string Code, IReadOnlyList<string> Fields);

/// <summary>Commissions of the month that are not self-billed: the reason (<see cref="SelfBillingNotDueReasons"/>), the channel, the stays and the amount.</summary>
public sealed record SelfBillingNotDue(string Reason, string Channel, int Stays, decimal Amount, string Currency);

/// <summary>The results of the drafts of a month (<see cref="SelfBillingOutcome.Result"/>).</summary>
public static class SelfBillingResults
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string AlreadyIssued = "already_issued";
}

/// <summary>Why the commissions of a month are not self-billed (<see cref="SelfBillingNotDue.Reason"/>): stable codes the frontend translates.</summary>
public static class SelfBillingNotDueReasons
{
    /// <summary>The portal has no legal entity in <c>SelfBilling:Portals</c>: nothing can be invoiced on its behalf by a guess. The only reason that asks the host or the operator to act.</summary>
    public const string PortalNotConfigured = "portal_not_configured";

    /// <summary>V1, the owner is a private person: the portal charged the Italian VAT, there is nothing to integrate.</summary>
    public const string PrivateHolder = "holder_private_person";

    /// <summary>The holder is not in Italy (a Swiss agency, ⚖ I10): no TD17; the VAT summary of the package tells the purchase tax.</summary>
    public const string HolderNotItalian = "holder_not_italian";

    /// <summary>The holder has no VAT number: it integrates nothing.</summary>
    public const string HolderNotVatRegistered = "holder_not_vat_registered";

    /// <summary>The commission is not in euro: the TD17 of an Italian holder is in euro and nothing is converted by a guess.</summary>
    public const string CurrencyNotSupported = "currency_not_supported";

    /// <summary>The booking of the commission is not a stay of a portal.</summary>
    public const string NotAPortal = "not_a_portal";

    /// <summary>The booking of the commission is not in the registry any more.</summary>
    public const string StayNotFound = "stay_not_found";

    /// <summary>
    /// The booking of the commission is not active now (cancelled, a request or a hold, or marked no-show: <c>AlloggiatiStatusRules.ActiveBookingStatuses</c> and no no-show, the one
    /// definition): the owner ledger is posted by hand, so the line of a stay that changed after the posting is alive until the next one, and it is never integrated meanwhile.
    /// </summary>
    public const string StayNotActive = "stay_not_active";
}

/// <summary>The stable codes of the self-billed invoices, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class SelfBillingErrors
{
    /// <summary>422: the month asked for is not a month.</summary>
    public const string PeriodInvalid = "self_billing_period_invalid";

    /// <summary>422: the settings cannot make a TD17: the VAT rate is not above 0 and at most 100, or no portal is configured.</summary>
    public const string NotConfigured = "self_billing_not_configured";

    /// <summary>
    /// 422: a TD17 is made again, issued and discarded from the self-billed invoices (<c>api/fiscal/self-billing</c>, with <c>agency.invoice.manage</c>), never from the endpoints
    /// of the documents of the guests.
    /// </summary>
    public const string UseSelfBilling = "document_self_billing";
}

/// <summary>The rules of the TD17 that need no database.</summary>
public static class SelfBillingRules
{
    /// <summary>The type of document of FatturaPA of the integration of a service bought abroad (reverse charge, art. 17 c. 2 DPR 633/72).</summary>
    public const string DocumentTypeCode = "TD17";

    /// <summary>The regime code of FatturaPA of a cedente that is not in the Italian regimes: «Altro».</summary>
    public const string ForeignSupplierRegimeCode = "RF18";

    /// <summary>The type of document of a credit note of an invoice, in the rule of a TD17 (never made: a TD17 is not credited from here).</summary>
    private const string CreditNoteTypeCode = "TD04";

    /// <summary>The shortest and the longest VAT code (the number without its country prefix) of a foreign supplier that FatturaPA takes (<c>IdCodice</c>, at most 28 with the prefix).</summary>
    private const int VatCodeMinLength = 4;

    private const int VatCodeMaxLength = FiscalDocument.RecipientVatNumberMaxLength - 2;

    /// <summary>
    /// The VAT number of a supplier without its country prefix, upper case, letters and digits only (<c>NL805734958B01</c> of the Netherlands is <c>805734958B01</c>; the VIES
    /// prefix of Greece, <c>EL</c>, is taken off too); <c>null</c> when nothing usable is left. The number is always written with its prefix, so a code that itself begins with
    /// the letters of the country is written twice.
    /// </summary>
    public static string? VatCodeOf(string? vatNumber, string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(vatNumber))
            return null;

        var cleaned = new string(vatNumber.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        var country = countryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        foreach (var prefix in PrefixesOf(country))
        {
            if (cleaned.StartsWith(prefix, StringComparison.Ordinal))
            {
                cleaned = cleaned[prefix.Length..];
                break;
            }
        }

        return cleaned.Length is >= VatCodeMinLength and <= VatCodeMaxLength ? cleaned : null;
    }

    /// <summary>What identifies a supplier across the documents: the country and the VAT code, whichever way the number is written.</summary>
    public static string PortalKey(string? countryCode, string? vatNumber)
    {
        var country = countryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{country}:{VatCodeOf(vatNumber, country) ?? string.Empty}");
    }

    /// <summary>The day by which a TD17 of the commissions of <paramref name="year"/>-<paramref name="month"/> is issued: <paramref name="issueByDay"/> of the month after.</summary>
    public static DateOnly DueDate(int year, int month, int issueByDay) =>
        new DateOnly(year, month, 1).AddMonths(1).AddDays(Math.Clamp(issueByDay, 1, 28) - 1);

    /// <summary>
    /// The tax rule of a TD17 of an issuer of the regime <paramref name="regime"/> (Italy): the VAT integrated at the rate of the settings (⚖) on every line, whatever the regime
    /// of the sales documents of the issuer (a flat-rate regime integrates the VAT too), the type of document <see cref="DocumentTypeCode"/>, no stamp duty, the version of the
    /// settings. <paramref name="regimeCode"/> is the regime code of the issuer, kept on its snapshot; <paramref name="day"/> the last day of the month of the commissions.
    /// </summary>
    public static FiscalTaxRule RuleOf(SelfBillingOptions options, DocumentRegime regime, string? regimeCode, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rate = new FiscalLineRate(options.VatRatePercent, null);
        return new FiscalTaxRule(
            FiscalDocumentRules.Italy,
            regime,
            options.EffectiveRuleVersion,
            day,
            null,
            null,
            FiscalDocumentKind.Invoice,
            regimeCode,
            DocumentTypeCode,
            CreditNoteTypeCode,
            Enum.GetValues<FiscalLineType>().ToDictionary(type => type, _ => rate),
            null);
    }

    /// <summary>The prefixes a VAT number of <paramref name="country"/> may carry: its ISO code, and <c>EL</c> for Greece.</summary>
    private static IEnumerable<string> PrefixesOf(string country)
    {
        if (country.Length != 2)
            yield break;

        yield return country;
        if (country == "GR")
            yield return "EL";
    }
}
