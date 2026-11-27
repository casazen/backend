using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.ServiceRequests;

/// <summary>
/// The answer to «La ditta di pulizie ha applicato l'IVA su questa fattura?» (<c>POST api/service-requests/{id}/cleaning-vat</c>, FIX/BE-CLEANING-VAT). A missing or
/// unknown answer is a 422 <c>cleaning_vat_answer_invalid</c> (a value no member declares is the 400 of every enum of the API).
/// </summary>
public sealed class CleaningVatAnswerRequest
{
    /// <summary><c>Applied</c> («Sì», the rate of <c>standardRatePercent</c>), <c>NotApplied</c> («No, senza IVA») or <c>Unknown</c> («Non lo so»).</summary>
    public CleaningSupplierVat? Answer { get; set; }

    /// <summary>«Ricorda per questa ditta»: the answer is kept for every cleaning of the company with no answer of its own. Not with <c>Unknown</c>; the whole organization only.</summary>
    public bool Remember { get; set; }
}

/// <summary>
/// The question of the VAT of a cleaning recharged to the owner, as the console shows it (<c>GET</c> and <c>POST api/service-requests/{id}/cleaning-vat</c>,
/// FIX/BE-CLEANING-VAT). No personal data: ids, the name of the company, a country, a day and rates.
/// </summary>
/// <param name="ServiceRequestId">The cleaning request (the id the «Da fare» <c>StateCleaningVat</c> carries).</param>
/// <param name="PropertyId">Its property.</param>
/// <param name="BookingId">The stay it was for, when it was for one.</param>
/// <param name="SupplierOrgId">The cleaning company.</param>
/// <param name="SupplierName">Its name.</param>
/// <param name="Country">The country of the cleaning (its property), whose VAT the question is about: <c>CH</c> or <c>IT</c>.</param>
/// <param name="CleaningDate">The day of the cleaning (<c>yyyy-MM-dd</c>), whose rate «Sì» uses.</param>
/// <param name="StandardRatePercent">The rate of the button «Sì» (8.1 in Switzerland, 22 in Italy on today's table); <c>null</c> when the table has none for that day: «Sì» is refused.</param>
/// <param name="Answer">The answer given for this cleaning; <c>null</c> while nobody answered.</param>
/// <param name="AnswerRatePercent">The rate the answer «Sì» used.</param>
/// <param name="AnsweredAt">When it was answered (UTC).</param>
/// <param name="CompanyAppliesVat">What the organization remembers for the company: <c>true</c> applies VAT, <c>false</c> none, <c>null</c> nothing remembered.</param>
/// <param name="StatedAnswer">What the invoice of the agency uses: the answer of this cleaning, else the company's; <c>null</c> when nothing is stated.</param>
/// <param name="StatedRatePercent">The rate of <paramref name="StatedAnswer"/> <c>Applied</c>.</param>
/// <param name="StatedByCompany">Whether <paramref name="StatedAnswer"/> is the company's remembered answer.</param>
/// <param name="CanRemember">Whether the caller may tick «Ricorda per questa ditta» (the whole organization only).</param>
public sealed record CleaningVatDto(
    Guid ServiceRequestId,
    Guid PropertyId,
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
    CleaningSupplierVat? StatedAnswer,
    decimal? StatedRatePercent,
    bool StatedByCompany,
    bool CanRemember)
{
    /// <summary>The question for a caller that may (<paramref name="canRemember"/>) or may not remember the answer for the company.</summary>
    public static CleaningVatDto From(CleaningVatQuestion question, bool canRemember)
    {
        ArgumentNullException.ThrowIfNull(question);
        return new CleaningVatDto(
            question.ServiceRequestId,
            question.PropertyId,
            question.BookingId,
            question.SupplierOrgId,
            question.SupplierName,
            question.Country,
            question.CleaningDate,
            question.StandardRatePercent,
            question.Answer,
            question.AnswerRatePercent,
            question.AnsweredAt,
            question.CompanyAppliesVat,
            question.Stated?.Answer,
            question.Stated?.RatePercent,
            question.Stated?.FromCompany ?? false,
            canRemember);
    }
}
