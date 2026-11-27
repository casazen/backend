using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using FiscalDocumentKind = Casazen.Core.Options.FiscalDocumentKind;

namespace Casazen.Core.Services;

/// <summary>
/// The question of the VAT of a cleaning recharged to the owner (FIX/BE-CLEANING-VAT, decision 4 of <c>fatturazione/decisioni-fiscali-2026-11-20.md</c>): «La ditta di
/// pulizie ha applicato l'IVA su questa fattura?» — Sì (the standard rate of the country of the cleaning on its day, <see cref="Regulatory.VatRates"/>), No, Non lo so — with
/// «Ricorda per questa ditta». The answer is stored on the cleaning request and closes its «Da fare» (<see cref="ActivityType.CleaningVatToState"/>); the owner invoice reads it
/// for the line of the recharge (<see cref="CleaningVatRules.RechargeVatOf"/>).
/// </summary>
/// <remarks>
/// Every method names the caller's <see cref="HostScope"/>: a request of another organization, of a property the scope does not reach, of the supplier's showcase or
/// of another category is the 404 <c>service_request_not_found</c> of one that does not exist. The web layer decides who may ask (the permission on the property of the
/// request) and who may remember for the company (the whole organization only).
/// </remarks>
public interface ICleaningVatService
{
    Task<CleaningVatQuestion> GetAsync(HostScope scope, Guid requestId, CancellationToken ct = default);

    Task<CleaningVatQuestion> AnswerAsync(
        HostScope scope,
        Guid requestId,
        CleaningSupplierVat? answer,
        bool remember,
        string userId,
        CancellationToken ct = default);
}

/// <summary>
/// The VAT of the company stated for a cleaning request: the request's own answer, else the answer the organization remembers for the company (with the rate of
/// the table on the day of the cleaning). <c>null</c> where it is used means that nobody stated it.
/// </summary>
/// <param name="Answer">The answer.</param>
/// <param name="RatePercent">The rate of <see cref="CleaningSupplierVat.Applied"/>; <c>null</c> otherwise.</param>
/// <param name="Country">The country of the cleaning (its property): whose VAT the answer is about.</param>
/// <param name="FromCompany">Whether it is the remembered answer of the company and not the request's own.</param>
public sealed record StatedCleaningVat(CleaningSupplierVat Answer, decimal? RatePercent, string Country, bool FromCompany);

/// <summary>The question of a cleaning request as the agency reads it (no personal data: ids, the name of the company, a country, a day, rates).</summary>
/// <param name="ServiceRequestId">The cleaning request.</param>
/// <param name="PropertyId">Its property.</param>
/// <param name="PropertyOwnerUserId">The user who created the property (for the authorization of the web layer; never answered).</param>
/// <param name="RentalContext">The context of the request (the web layer authorizes a short-rent and a long-rent request each with its own operation).</param>
/// <param name="BookingId">The stay it was for, when it was for one.</param>
/// <param name="SupplierOrgId">The cleaning company.</param>
/// <param name="SupplierName">Its name.</param>
/// <param name="Country">The country of the cleaning (its property), whose VAT the question is about.</param>
/// <param name="CleaningDate">The day of the cleaning (Europe/Rome), whose rate «Sì» uses.</param>
/// <param name="StandardRatePercent">The standard rate of <paramref name="Country"/> on <paramref name="CleaningDate"/>; <c>null</c> when the table has none (then «Sì» is refused).</param>
/// <param name="Answer">The request's own answer; <c>null</c> while nobody answered.</param>
/// <param name="AnswerRatePercent">The rate the answer «Sì» used.</param>
/// <param name="AnsweredAt">When the answer was given (UTC).</param>
/// <param name="CompanyAppliesVat">What the organization remembers for the company; <c>null</c> when nothing.</param>
/// <param name="Stated">What the invoice uses: the request's answer, else the company's; <c>null</c> when nothing is stated.</param>
public sealed record CleaningVatQuestion(
    Guid ServiceRequestId,
    Guid PropertyId,
    string? PropertyOwnerUserId,
    ServiceRequestRentalContext RentalContext,
    Guid? BookingId,
    Guid SupplierOrgId,
    string? SupplierName,
    string Country,
    DateOnly CleaningDate,
    decimal? StandardRatePercent,
    CleaningSupplierVat? Answer,
    decimal? AnswerRatePercent,
    DateTime? AnsweredAt,
    bool? CompanyAppliesVat,
    StatedCleaningVat? Stated);

/// <summary>The VAT a line of the recharge of a cleaning carries instead of the rule's (<see cref="CleaningVatRules.RechargeVatOf"/>).</summary>
/// <param name="Percent">The rate; 0 for a line without VAT.</param>
/// <param name="NatureCode">The nature code of an Italian line without VAT; <c>null</c> otherwise.</param>
/// <param name="Contained">Whether the amount of the line contains the VAT (the gross the company invoiced): its VAT is taken out of it, never added on top.</param>
public sealed record CleaningRechargeVat(decimal Percent, string? NatureCode, bool Contained);

/// <summary>The rules of the VAT of a cleaning recharged to the owner (FIX/BE-CLEANING-VAT), pure.</summary>
public static class CleaningVatRules
{
    public const string TodoEntityType = nameof(ServiceRequest);

    /// <summary>
    /// The nature code of an Italian recharge of a cleaning whose company applied no VAT («non soggette - altri casi», the code the catalog gives every line of an issuer
    /// outside the VAT). A product default to confirm with the accountant: R4 found no source on the recharge of a company in the flat-rate regime.
    /// </summary>
    public const string ItalianNatureWithoutVat = "N2.2";

    /// <summary>
    /// The VAT the line of the recharge of a cleaning carries on the invoice of the agency to the owner, or <c>null</c> for the VAT of a recharge as before the question
    /// (the rule's rate, added on top or taken out as the mandate says). On the recharge the cleaning follows the VAT of the company (art. 3 c. 3 DPR 633/72, AdE Risposta
    /// 623/2021; art. 20 and 24 LIVA):
    /// <list type="bullet">
    /// <item>«Sì» → the rate the answer used, contained in the gross amount (VAT = gross × r / (100 + r));</item>
    /// <item>«No» → no VAT (Italy: <see cref="ItalianNatureWithoutVat"/>);</item>
    /// <item>«Non lo so», nothing stated → as before.</item>
    /// </list>
    /// Only an issuer that charges VAT on a recharge (an invoice with a rate above 0) applies the company's VAT: an agency outside the VAT (not registered, a receipt, the
    /// flat-rate regime) has none to charge, and a cleaning in another country than the rule's is not the rule's VAT; both stay as before.
    /// </summary>
    public static CleaningRechargeVat? RechargeVatOf(StatedCleaningVat? stated, FiscalTaxRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (stated is null || stated.Answer == CleaningSupplierVat.Unknown || !IsAskedUnder(rule, stated.Country))
            return null;

        return stated.Answer switch
        {
            CleaningSupplierVat.Applied when stated.RatePercent is { } rate and > 0m => new CleaningRechargeVat(rate, null, Contained: true),
            CleaningSupplierVat.NotApplied => new CleaningRechargeVat(0m, rule.IsSwiss ? null : ItalianNatureWithoutVat, Contained: false),
            _ => null,
        };
    }

    /// <summary>
    /// Whether the VAT of a stored recharge line of an owner invoice is one <see cref="RechargeVatOf"/> gives a stated cleaning under <paramref name="rule"/>: none (the nature of
    /// «No» of the rule's country), or a standard rate of the table of the rule's country. The issue of an owner invoice accepts it besides the rule's VAT of the line type; any
    /// other VAT, or any VAT of the company under a rule that charges none on a recharge, is a draft of another rule.
    /// </summary>
    public static bool IsCompanyVatOf(decimal ratePercent, string? natureCode, FiscalTaxRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!ChargesVatOnRecharge(rule))
            return false;

        if (ratePercent == 0m)
            return string.Equals(natureCode, rule.IsSwiss ? null : ItalianNatureWithoutVat, StringComparison.Ordinal);

        return natureCode is null
            && Regulatory.VatRates.Table.Any(rate => rate.Country == rule.Jurisdiction && rate.Kind == Regulatory.VatRateKind.Standard && rate.Percent == ratePercent);
    }

    /// <summary>
    /// Whether the issuer of <paramref name="rule"/> charges VAT on a recharge: an invoice whose recharge rate is above 0. Only then does the company's VAT change the line of
    /// the recharge (<see cref="RechargeVatOf"/>); an agency not registered, a receipt or the Italian flat-rate regime has no VAT to charge on it.
    /// </summary>
    public static bool ChargesVatOnRecharge(FiscalTaxRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.DocumentKind == FiscalDocumentKind.Invoice && rule.RateOf(FiscalLineType.Recharge).Percent > 0m;
    }

    /// <summary>
    /// Whether the question is worth asking for a cleaning in <paramref name="country"/> (its property) under <paramref name="rule"/>, the rule of the owner invoices of the
    /// organization on the last day of the month: the rule charges VAT on a recharge and the cleaning is in its country, so <see cref="RechargeVatOf"/> would use the answer.
    /// No rule (no issuer profile, no rule in the catalog) is never a question: an item nobody can use is only noise.
    /// </summary>
    public static bool IsAskedUnder(FiscalTaxRule? rule, string? country) =>
        rule is not null && ChargesVatOnRecharge(rule) && string.Equals(country?.Trim(), rule.Jurisdiction, StringComparison.OrdinalIgnoreCase);
}

public static class CleaningVatErrors
{
    public const string AnswerInvalid = "cleaning_vat_answer_invalid";

    public const string AnswerInvalidMessageKey = "CleaningVatAnswerInvalid";

    public const string NotCompleted = "cleaning_vat_not_completed";

    public const string NotCompletedMessageKey = "CleaningVatNotCompleted";

    public const string RateUnknown = "cleaning_vat_rate_unknown";

    public const string RateUnknownMessageKey = "CleaningVatRateUnknown";

    public const string RememberUnknown = "cleaning_vat_remember_unknown";

    public const string RememberUnknownMessageKey = "CleaningVatRememberUnknown";

    public const string RememberForbidden = "cleaning_vat_remember_forbidden";

    public const string RememberForbiddenMessageKey = "CleaningVatRememberForbidden";
}
