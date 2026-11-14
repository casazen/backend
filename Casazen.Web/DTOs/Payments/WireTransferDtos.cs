using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Payments;

/// <summary>
/// Request to create a wire transfer batch (<c>POST api/wire-transfers/batch</c>, E16/F01/US01/T01): the <b>confirmed payouts</b> to pay and the day the bank is
/// asked to execute them. The debtor and the debit account are the issuer profile of the org, so the request names no account.
/// </summary>
/// <remarks>
/// <see cref="StatementIds"/> and <see cref="DebitAccount"/> are the request of before the payouts (a batch of owner statements and an account typed by the caller):
/// they are still read, so that a client that sends them gets the code <c>wire_batch_from_statement_retired</c> (422) and not an error of shape, and they are
/// refused whatever else the request says. A typed debit account is never ignored in silence: the money would leave another account than the one the caller named.
/// </remarks>
public sealed record CreateBatchRequest
{
    /// <summary>IDs of the confirmed payouts to pay (one transfer for each). An empty list is never «all the payouts».</summary>
    public List<Guid> PayoutIds { get; init; } = [];

    /// <summary>The day the bank is asked to execute the transfers (<c>yyyy-MM-dd</c>): today or later, at most a year ahead. Required.</summary>
    public DateOnly? ExecutionDate { get; init; }

    /// <summary>Retired: the batch is made from payouts. Any value is refused (422 <c>wire_batch_from_statement_retired</c>).</summary>
    public List<Guid>? StatementIds { get; init; }

    /// <summary>Retired: the debit account is the IBAN of the issuer profile of the org. Any value is refused (422 <c>wire_batch_from_statement_retired</c>).</summary>
    public string? DebitAccount { get; init; }
}

/// <summary>Response after creating a wire transfer batch. No IBAN and no name.</summary>
public sealed record CreateBatchResponse
{
    /// <summary>The ID of the created batch.</summary>
    public Guid BatchId { get; init; }

    /// <summary>Currency of the batch.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>Total amount of all transfers in the batch: the sum of the net amounts of its payouts.</summary>
    public decimal TotalAmount { get; init; }

    /// <summary>Number of wire transfers in the batch: one for each payout.</summary>
    public int TransferCount { get; init; }

    /// <summary>The day the file asks the bank to execute the transfers.</summary>
    public DateOnly? ExecutionDate { get; init; }

    /// <summary>The message of the file: <c>pain.001.001.09.ch.03</c> for CHF, <c>pain.001.001.03</c> for EUR.</summary>
    public string? Format { get; init; }
}

/// <summary>
/// A batch in the list of the wires (<c>GET api/wire-transfers/batches</c>): the state, the currency, how many transfers and how much, when it was made,
/// downloaded and paid, and the message and the date it asks of the bank. <b>Never an IBAN, a name or the file.</b> <see cref="Format"/> is <c>null</c> for a batch of
/// before the Swiss message, which was a <c>pain.001.001.03</c> file.
/// </summary>
public sealed record WireBatchListItemResponse(
    Guid Id,
    string Currency,
    WireBatchStatus Status,
    int TransferCount,
    decimal TotalAmount,
    DateOnly? ExecutionDate,
    string? Format,
    DateTime CreatedAt,
    DateTime? DownloadedAt,
    DateTime? PaidAt)
{
    public static WireBatchListItemResponse From(WireBatchSummary batch)
    {
        return new WireBatchListItemResponse(
            batch.Id,
            batch.Currency,
            batch.Status,
            batch.TransferCount,
            batch.TotalAmount,
            batch.ExecutionDate,
            batch.MessageFormat,
            batch.CreatedAt,
            batch.DownloadedAt,
            batch.PaidAt);
    }
}
