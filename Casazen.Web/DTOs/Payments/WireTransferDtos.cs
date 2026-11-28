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
    public List<Guid> PayoutIds { get; init; } = [];

    public DateOnly? ExecutionDate { get; init; }

    public List<Guid>? StatementIds { get; init; }

    public string? DebitAccount { get; init; }
}

public sealed record CreateBatchResponse
{
    public Guid BatchId { get; init; }

    public string Currency { get; init; } = string.Empty;

    public decimal TotalAmount { get; init; }

    public int TransferCount { get; init; }

    public DateOnly? ExecutionDate { get; init; }

    public string? Format { get; init; }
}

/// <summary>
/// A batch in the list of the wires (<c>GET api/wire-transfers/batches</c>): the state, the currency, how many transfers and how much, when it was made,
/// downloaded and paid, and the message and the date it asks of the bank. <b>Never an IBAN, a name or the file.</b> <see cref="Format"/> is <c>pain.001.001.03</c> for a
/// EUR batch made before the EUR file moved to <c>pain.001.001.09</c>, and <c>null</c> for a batch of before the Swiss message, which was a <c>pain.001.001.03</c> file.
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

/// <summary>
/// A payout on the wire page (<c>GET api/wire-transfers/payable-payouts</c> and <c>GET api/wire-transfers/batch/{id}/payouts</c>): who is paid (the owner by id and
/// name), the period of the statement it pays, how much (<see cref="Amount"/>: the net, the amount of the transfer) in which currency, when it was confirmed, whether it is
/// still confirmed (<c>Reversed</c> for a payout reversed after its batch was made) and whether the owner has an IBAN now, <b>as a yes or a no: never the IBAN itself</b>,
/// never an address, never the file.
/// </summary>
public sealed record WirePayoutResponse(
    Guid PayoutId,
    Guid OwnerId,
    string OwnerName,
    int PeriodYear,
    int PeriodMonth,
    decimal Amount,
    string Currency,
    DateTime ConfirmedAt,
    OwnerPayoutStatus Status,
    bool OwnerHasIban)
{
    public static WirePayoutResponse From(WirePayoutSummary payout)
    {
        return new WirePayoutResponse(
            payout.PayoutId,
            payout.OwnerId,
            payout.OwnerName,
            payout.PeriodYear,
            payout.PeriodMonth,
            payout.Amount,
            payout.Currency,
            payout.ConfirmedAt,
            payout.Status,
            payout.OwnerHasIban);
    }
}

public sealed record WireBatchPayoutsResponse(WireBatchListItemResponse Batch, IReadOnlyList<WirePayoutResponse> Payouts)
{
    public static WireBatchPayoutsResponse From(WireBatchPayoutsSummary summary)
    {
        return new WireBatchPayoutsResponse(
            WireBatchListItemResponse.From(summary.Batch),
            summary.Payouts.Select(WirePayoutResponse.From).ToList());
    }
}
