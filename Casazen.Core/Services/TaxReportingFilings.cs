using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The data the accountant enters in the 770 for the withholding of the owners (E18/F01/US02/T02, NF-117, ⚖ T2): the withholdings made and paid, month by month, with the tax code and the
/// reference of the F24 they were paid with. Built from the applicable withholding evidence of the payouts paid in the year and their reversals, of an Italian rule only: the evidence the
/// F24 of each month is built from. It is the data to fill the form with, never the form and never a telematic file: nothing is filed by CasaZen.
/// </summary>
/// <param name="Year">The year of the payments.</param>
/// <param name="Lines">One line for each month and tax code with evidence, by month then tax code.</param>
public sealed record Withholding770Data(int Year, IReadOnlyList<Withholding770Line> Lines);

/// <summary>
/// The withholding of one month and one tax code (a month with two codes has two lines, never one merged) and the F24 of the month it was paid with, when there is one. A figure
/// nobody recorded is <c>null</c>, never a zero: an F24 not generated, or one not paid, says so with its empty columns.
/// </summary>
/// <param name="Month">The month (1-12) of the payments: the reference month of the F24.</param>
/// <param name="TaxCode">The F24 tax code of the evidence (<c>1919</c> while the approved rule says so).</param>
/// <param name="GrossBase">The base of the decisions of the month less the base of their reversals, in euro.</param>
/// <param name="Withheld">The withholding of the decisions of the month less the withholding of their reversals, in euro: what was made.</param>
/// <param name="EvidenceCount">How many decisions and reversals make the line.</param>
/// <param name="F24Amount">The total of the F24 record of the month, in euro, when the record exists with this tax code.</param>
/// <param name="F24DueDate">The due date of that F24 (the 16th of the next month).</param>
/// <param name="F24PaidOn">The day that F24 was marked as paid; <c>null</c> while it is not.</param>
/// <param name="FilingState">The filing state of that F24 (generated is never filed).</param>
/// <param name="HandoffReference">The reference of the controlled manual handoff of that F24, when it was submitted.</param>
/// <param name="ReceiptReference">The receipt of the authority for that F24, when it was accepted.</param>
public sealed record Withholding770Line(
    int Month,
    string TaxCode,
    decimal GrossBase,
    decimal Withheld,
    int EvidenceCount,
    decimal? F24Amount,
    DateOnly? F24DueDate,
    DateOnly? F24PaidOn,
    TaxFilingState? FilingState,
    string? HandoffReference,
    string? ReceiptReference);

/// <summary>
/// The communication of the short-let contracts handled by the agency without withholding by the agency, due by 30 June of the following year (E18/F01/US02/T02, NF-117, ⚖ T2): the
/// lessor, the property (address, cadastral data and CIN when stored), the duration and the consideration, for the stays the agency did not withhold on. The fields follow the provision of the tax
/// agency on the short lets; the telematic layout is not produced and nothing is sent by CasaZen.
/// </summary>
/// <param name="Year">The year of the payouts that decided the stays.</param>
/// <param name="Rows">The contracts, by lessor name, property and check-in.</param>
public sealed record ContractsCommunication(int Year, IReadOnlyList<ContractCommunicationRow> Rows);

/// <summary>One contract of the communication: a qualifying short let of a lessor that the agency handled and did not withhold on, and why.</summary>
/// <param name="LessorId">The owner the contract is reported on: the owner who was paid, or the signatory of its property (the same rule as the Certificazione Unica).</param>
/// <param name="Lessor">The lessor's data (name, fiscal code, address) as the registry and the fiscal profile in force say it.</param>
/// <param name="Property">The property, as the registry has it; <c>null</c> only for a property that is no longer in the registry.</param>
/// <param name="BookingId">The stay.</param>
/// <param name="Channel">The channel the stay came from (the name of <c>BookingSource</c>).</param>
/// <param name="CheckIn">The first day of the contract.</param>
/// <param name="CheckOut">The last day of the contract (the check-out day).</param>
/// <param name="Nights">The duration in nights.</param>
/// <param name="Currency">The currency of the consideration.</param>
/// <param name="Consideration">The gross consideration of the stay for the lessor (its share of it, or the sum of the co-owners on the signatory), without the tourist tax.</param>
/// <param name="Reason">Why the agency did not withhold: <c>already_withheld_by_portal</c>, <c>no_collection_or_intervention</c> or the reason of a decision with no withholding.</param>
public sealed record ContractCommunicationRow(
    Guid LessorId,
    CuRecipient Lessor,
    CuProperty? Property,
    Guid BookingId,
    string Channel,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Nights,
    string Currency,
    decimal Consideration,
    string Reason);
