namespace Casazen.Core.Services;

/// <summary>
/// The nature of the VAT of the amount of a <see cref="CompletedCleaning"/> (E24/F01/US01/T03, NF-120): what the invoicing of the owners may rely on, never a guess.
/// </summary>
public enum CleaningAmountVat
{
    /// <summary>
    /// The amount is the gross price of the supplier, as it declared it: the service requests keep every amount «gross as declared» (decision D4: no VAT split), so
    /// the VAT of the supplier, if it charges any, is in it and nothing is to be added.
    /// </summary>
    IncludesSupplierVat = 1,

    /// <summary>The amount is net of VAT. No amount of CasaZen is: the model never says it, so this value is not produced today and is kept for the sources that will.</summary>
    ExcludesVat = 2,

    /// <summary>A staff cost: an internal rate of the org, which has no VAT of a supplier.</summary>
    NotApplicable = 3,

    /// <summary>The model does not say (for example a cleaning completed with no amount at all): the invoicing decides, nothing is assumed.</summary>
    Unknown = 4,
}

/// <summary>Who did a <see cref="CompletedCleaning"/>.</summary>
public enum CleaningKind
{
    /// <summary>A supplier of the marketplace: the source is a service request.</summary>
    Supplier = 1,

    /// <summary>A member of the org's staff: the source is a staff activity.</summary>
    Staff = 2,
}

/// <summary>Where the cost of a <see cref="CompletedCleaning"/> was read.</summary>
public enum CleaningSourceKind
{
    /// <summary>A <c>ServiceRequest</c> of category cleaning, completed or paid.</summary>
    ServiceRequest = 1,

    /// <summary>An <c>InternalActivity</c> of type Cleaning, completed.</summary>
    InternalActivity = 2,
}

/// <summary>
/// One cleaning completed, with its cost, as the invoicing of the owners reads it (E24/F01/US01/T03, NF-120): one for each completed cleaning of the period.
/// No personal data: ids, a date, an amount and codes.
/// </summary>
/// <param name="PropertyId">The property that was cleaned.</param>
/// <param name="BookingId">The stay the cleaning was for, when it was for one.</param>
/// <param name="CompletedOn">
/// The Europe/Rome date the cost became firm, the very date the period is judged on: the day the cleaning was completed or, for a supplier request whose final amount the host had to confirm
/// (it was above the quote by more than the tolerance, decision D7), the day of the confirmation. A late confirmation therefore falls in the period of its confirmation, never in an
/// earlier one that may already be invoiced.
/// </param>
/// <param name="AmountCents">
/// What the cleaning cost, in cents of <paramref name="Currency"/>. A supplier: the final amount the supplier declared when it completed the work, else the price it
/// committed to (the one it took the request at, or the quote the customer accepted), else the estimated amount of the request. Staff: the internal rate of the activity.
/// 0 when no amount is known (<see cref="CleaningAmountVat.Unknown"/>).
/// </param>
/// <param name="Currency">The currency of the property: the requests carry none, and a staff rate is in the currency of the org's property.</param>
/// <param name="AmountVat">The nature of the VAT of the amount.</param>
/// <param name="Kind">Who did the cleaning.</param>
/// <param name="RechargeToOwner">Whether the cost is recharged to the owner: the flag of a staff activity, always for a supplier.</param>
/// <param name="SourceKind">Where the cleaning was read.</param>
/// <param name="SourceId">The id of the request or of the activity.</param>
/// <param name="SupplierInvoiceReference">
/// The reference of the invoice or receipt of the supplier, when the model links one to the request or to its payment. The model links none today (a payment
/// holds the references of the payment provider, which are not an invoice), so it is <c>null</c> until it does.
/// </param>
public sealed record CompletedCleaning(
    Guid PropertyId,
    Guid? BookingId,
    DateOnly CompletedOn,
    long AmountCents,
    string Currency,
    CleaningAmountVat AmountVat,
    CleaningKind Kind,
    bool RechargeToOwner,
    CleaningSourceKind SourceKind,
    Guid SourceId,
    string? SupplierInvoiceReference);

/// <summary>
/// One read of the cleanings completed in a period, with their cost, for the cleaning recharges to the owners (E24/F01/US01/T03, NF-120): the contract agreed with
/// the invoicing plan (its task E14/F01/US02/T01 consumes this interface). Read only; the invoicing never changes how a cleaning is created.
/// </summary>
/// <remarks>
/// <para><b>What counts.</b> A service request of category cleaning that is <c>Completato</c> or <c>Pagato</c> (<c>Annullato</c> and <c>Rifiutato</c> never, nor one that
/// is still open), and a staff activity of type Cleaning that is completed. Each comes back once.</para>
/// <para><b>Never an amount nobody confirmed.</b> A supplier request whose final amount is above the quote by more than the tolerance (decision D7) is a cost only once the host
/// confirmed it: until then it is not returned. It comes back once, in the period of the confirmation (its <c>CompletedOn</c> is the day of the confirmation, not of the completion),
/// so the statement of a period already closed never changes after it was issued and an amount confirmed late falls in the open one. A request the host marked as paid by hand with the
/// confirmation still open is settled by that payment: it counts from the day it was paid, for the same reason. <c>SourceId</c> is the request, always the same.</para>
/// <para><b>Whose.</b> Only what belongs to <c>orgId</c>: the read is scoped by the tenant filter and by an explicit <c>orgId</c> check on each row and on its property, so a
/// property id of another organization returns nothing. A request born from a supplier's public showcase is never a cleaning of a host.</para>
/// </remarks>
public interface ICleaningCostSource
{
    /// <summary>
    /// The cleanings of <paramref name="orgId"/> completed from <paramref name="from"/> to <paramref name="to"/>, both included, on the Europe/Rome calendar, oldest
    /// first. <paramref name="propertyIds"/> <c>null</c> = every property of the org; a list = only those of them that are the org's properties (an id of another org
    /// is left out), and an empty list is an empty answer, never «every property».
    /// </summary>
    Task<IReadOnlyList<CompletedCleaning>> GetCompletedAsync(
        Guid orgId,
        IReadOnlyCollection<Guid>? propertyIds,
        DateOnly from,
        DateOnly to,
        CancellationToken ct);
}
