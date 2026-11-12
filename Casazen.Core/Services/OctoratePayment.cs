namespace Casazen.Core.Services;

/// <summary>One collection of an Octorate reservation (<c>payments[]</c> of its single read).</summary>
/// <param name="Amount">The amount collected.</param>
/// <param name="Date">The day of the collection.</param>
/// <param name="Mode">How it was collected, as Octorate names it.</param>
/// <param name="CompanyCollect">Who collects for the reservation (<c>companyCollect</c>: <c>NONE</c>, <c>COMPANY</c>, <c>HOTEL</c>); <c>null</c> when absent.</param>
/// <param name="Transaction">
/// The <c>transaction</c> the payment was recorded with (E08/F01/US01/T04): for a payment CasaZen recorded on the reservation it is the one stored on the
/// <see cref="Casazen.Core.Entities.Payment"/> (<c>OctorateTransactionId</c>), so the ledger never shows the same money twice; <c>null</c> when absent.
/// </param>
public sealed record OctoratePayment(decimal Amount, DateOnly? Date, string? Mode, string? CompanyCollect, string? Transaction = null);
