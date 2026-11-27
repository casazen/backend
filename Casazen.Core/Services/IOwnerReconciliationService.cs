using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The reconciliation of a month of an agency before it is closed (E13/F01/US03/T02, NF-110, NF-115; acceptance step C1 of <c>pilota-flusso-completo-2026-11-14.md</c>): per
/// channel, what the owner ledgers posted for the stays of the month, what the portals kept as commission and where that figure comes from, what the portal is expected to
/// have paid out and what the payment ledger records as paid, and the published statements against the ledger, with every difference named by its stay or its entry.
/// </summary>
/// <remarks>
/// <para><b>Read only.</b> Nothing is written, no entity is new, and a difference blocks nothing: the agency decides (the close of an owner does not read this). The bulk close
/// (<see cref="IOwnerAccountingService.RunBulkAsync"/>) is preceded by it in the screen: the agency sees the summary before it closes.</para>
/// <para><b>Currencies.</b> Every amount is in the currency it was posted or recorded in and is never added to an amount of another currency: a channel has a row for each
/// currency, a statement total one for each statement currency. The rows of the channels are in the currency of the property of the stays (the one of the payment ledger),
/// the statement rows in the statement currency of the owners.</para>
/// <para><b>Sources.</b> The ledger side is the live lines of the owner ledger of the month (neither reversals nor reversed lines); the bookings side the bookings the lines come
/// from; the payment side what the payment ledger (<see cref="IBookingPaymentLedgerService"/>) stores for those bookings, read from the database without a call to the channel
/// manager: the payments collected in CasaZen (recorded by hand, net of refunds and without their share of the tourist tax, as the owner ledger posts them) and, when the booking
/// has none, the net the channel manager reported for it (<see cref="Booking.OtaNetAmount"/>, never derived from another figure). The statements are the published versions (the
/// newest of each owner for the month).</para>
/// <para><b>Not available is not zero.</b> A stay the portal collected, with an expected payout and neither a payment in CasaZen nor a reported net, has no payout figure: it is told as
/// <see cref="OwnerReconciliationReasons.PayoutSourceUnavailable"/> with a <c>null</c> recorded figure and difference (ADR-033-R2: a screen says «not available», not zero), and a row
/// with no figure for any of its expected payouts has a <c>null</c> recorded total and difference. The Octorate import gives no net (<see cref="Booking.OtaNetAmount"/> stays
/// <c>null</c>) and a portal stay takes no payment by hand, so today that is the state of every stay of a portal.</para>
/// </remarks>
public interface IOwnerReconciliationService
{
    /// <summary>The reconciliation of the month. 422 <c>owner_period_invalid</c>.</summary>
    Task<OwnerReconciliation> GetAsync(Guid orgId, int year, int month, CancellationToken ct = default);
}

/// <summary>The reconciliation of a month.</summary>
/// <param name="Channels">One row for each channel and currency with a stay posted in the month, by channel then currency.</param>
/// <param name="Statements">One row for each statement currency of the month, by currency.</param>
/// <param name="Differences">Every difference, by stay, entry or statement, with its reason: the channels' payouts, the stays missing from the ledger, the ledger against the published statements.</param>
public sealed record OwnerReconciliation(
    int Year,
    int Month,
    IReadOnlyList<ChannelReconciliationRow> Channels,
    IReadOnlyList<StatementReconciliationRow> Statements,
    IReadOnlyList<ReconciliationDifference> Differences);

/// <summary>
/// The commission the portals kept, split by where the figure comes from, so the agency sees how much of it rests on a rule (⚖ R2) and not on the portal (all positive, in the
/// currency of the row).
/// </summary>
/// <param name="FromPortal">The commission the reservation brought (<c>payload</c>); also the lines posted before the rules existed.</param>
/// <param name="FromRule">A rule of the agency for the channel in force on the check-out (<c>channel-rule:{id}</c>).</param>
/// <param name="FromDefault">The default of the channel the configuration gives (<c>channel-default</c>).</param>
public sealed record ChannelCommissionSplit(decimal FromPortal, decimal FromRule, decimal FromDefault)
{
    /// <summary>Every commission of the row.</summary>
    public decimal Total => FromPortal + FromRule + FromDefault;

    /// <summary>What rests on a rule of the agency or on the default, not on the portal's own figure.</summary>
    public decimal OnRule => FromRule + FromDefault;
}

/// <summary>
/// A channel in a currency (E13/F01/US03/T02). The payout figures are for the portals only (<see cref="IsPortal"/>) and for the stays the portal collected itself: a stay the
/// host collected (<see cref="Booking.OtaCompanyCollect"/> <c>false</c>) is not paid out by the portal and is only counted in <see cref="HostCollectedStays"/>.
/// </summary>
/// <param name="Channel">The channel of the bookings (<see cref="Booking.Source"/>).</param>
/// <param name="Currency">The currency of the stays and of every figure of the row.</param>
/// <param name="IsPortal">Whether the channel is a portal (<c>FiscalCopy.IsOtaBookingSource</c>): a portal has a commission and a payout, the other channels neither.</param>
/// <param name="Stays">The stays posted in the owner ledgers of the month (a stay with a live revenue line).</param>
/// <param name="LedgerGross">The gross posted for them, without the tourist tax: the sum of the revenue lines in the currency of the stays (the owners' shares add up to the whole).</param>
/// <param name="Commissions">The commission the portal kept on them, as the ledger posted it, by where it comes from.</param>
/// <param name="ExpectedNetPayout">What the portal is expected to have paid out for the stays it collected: their gross less their commission; <c>null</c> for a channel that is not a portal.</param>
/// <param name="RecordedPayouts">What CasaZen records as paid for the stays that have a payout figure; <c>null</c> for a channel that is not a portal, and <c>null</c> (not available, never
/// zero) when the row has an expected payout and not one of its stays has a figure.</param>
/// <param name="Difference">The recorded payouts less the expected ones of the stays that have a figure (negative: less was recorded than expected); <c>null</c> for a channel that is not a
/// portal, and <c>null</c> when <paramref name="RecordedPayouts"/> is.</param>
/// <param name="StaysWithoutRecordedPayout">The stays the portal collected with an expected payout and no payout figure in CasaZen (each one is a
/// <see cref="OwnerReconciliationReasons.PayoutSourceUnavailable"/> difference); <c>null</c> for a channel that is not a portal.</param>
/// <param name="HostCollectedStays">The stays of a portal the host collected itself: no payout is expected for them; <c>null</c> for a channel that is not a portal.</param>
public sealed record ChannelReconciliationRow(
    BookingSource Channel,
    string Currency,
    bool IsPortal,
    int Stays,
    decimal LedgerGross,
    ChannelCommissionSplit Commissions,
    decimal? ExpectedNetPayout,
    decimal? RecordedPayouts,
    decimal? Difference,
    int? StaysWithoutRecordedPayout,
    int? HostCollectedStays);

/// <summary>The published statements of the month against the ledger in one statement currency.</summary>
/// <param name="Currency">The statement currency.</param>
/// <param name="LedgerOwners">The owners with a line of the month in this currency.</param>
/// <param name="PublishedStatements">The owners' published statements in this currency (the newest published version of each owner).</param>
/// <param name="LedgerTotal">The total of the ledger of the month in this currency: every line, a reversal cancelling the line it reverses.</param>
/// <param name="PublishedTotal">The total of the month of the published statements (closing balance less opening balance).</param>
/// <param name="Difference">The published total less the ledger total (negative: the statements show less than the ledger).</param>
public sealed record StatementReconciliationRow(
    string Currency,
    int LedgerOwners,
    int PublishedStatements,
    decimal LedgerTotal,
    decimal PublishedTotal,
    decimal Difference);

/// <summary>What a difference is named by.</summary>
public enum ReconciliationDifferenceKind
{
    /// <summary>A stay (a booking): the lines of the ledger that come from it are in <see cref="ReconciliationDifference.Id"/>'s difference together.</summary>
    Stay = 1,

    /// <summary>A line of the ledger that comes from no booking (a charge, a correction, a payment to the owner, an owner invoice).</summary>
    Entry = 2,

    /// <summary>A published statement whose total is not the one of its lines.</summary>
    Statement = 3,
}

/// <summary>
/// One difference (E13/F01/US03/T02): what it is about and why. <see cref="Difference"/> is always <see cref="Recorded"/> less <see cref="Expected"/>, and <c>null</c> when there is no
/// recorded figure to subtract (<see cref="OwnerReconciliationReasons.PayoutSourceUnavailable"/>: not available, never zero); what the two are depends on the
/// <see cref="Reason"/> (<see cref="OwnerReconciliationReasons"/>). Never a name or an address.
/// </summary>
/// <param name="Kind">What <paramref name="Id"/> is: a booking, a ledger line or a statement version.</param>
/// <param name="Id">The booking, the ledger line or the statement version.</param>
/// <param name="Reason">The stable code of the reason.</param>
/// <param name="Currency">The currency of the amounts.</param>
/// <param name="Expected">What the reference says: the payout the ledger expects, the gross the booking says, the ledger amount.</param>
/// <param name="Recorded">What was found: the payout CasaZen records (<c>null</c> when there is no figure), 0 for what the ledger or the statement does not hold.</param>
/// <param name="Difference">The recorded less the expected; <c>null</c> when <paramref name="Recorded"/> is not available.</param>
/// <param name="Channel">The channel of the stay, when the difference is about a stay.</param>
/// <param name="OwnerId">The owner whose ledger or statement it is, when it is about one owner.</param>
public sealed record ReconciliationDifference(
    ReconciliationDifferenceKind Kind,
    Guid Id,
    string Reason,
    string Currency,
    decimal? Expected,
    decimal? Recorded,
    decimal? Difference,
    BookingSource? Channel = null,
    Guid? OwnerId = null);

/// <summary>The stable codes of the reasons of a difference (E13/F01/US03/T02).</summary>
public static class OwnerReconciliationReasons
{
    /// <summary>
    /// A stay the portal collected has an expected payout and CasaZen has no figure for it: no payment collected in CasaZen and no net reported by the channel manager (the Octorate
    /// import gives none). <c>Recorded</c> and <c>Difference</c> are <c>null</c>: the payout is not available, never zero (ADR-033-R2).
    /// </summary>
    public const string PayoutSourceUnavailable = "payout_source_unavailable";

    /// <summary>The payout recorded for a stay is not the one expected.</summary>
    public const string PayoutDiffers = "payout_differs";

    /// <summary>
    /// An active stay of the month that was not paid in CasaZen (a portal, the agency's own site, one entered by hand), with a gross to post, has no revenue line in the ledger: it
    /// is not posted yet, or something keeps it out (the readiness says what). <c>Expected</c> is its gross, <c>Recorded</c> 0.
    /// </summary>
    public const string StayNotPosted = "stay_not_posted";

    /// <summary>The owner has no published statement for the month: its ledger lines are in none. <c>Expected</c> is the amount of the lines, <c>Recorded</c> 0.</summary>
    public const string StatementNotPublished = "statement_not_published";

    /// <summary>A line was posted after the cut-off of the published statement of its owner: the statement does not hold it. <c>Expected</c> is its amount, <c>Recorded</c> 0.</summary>
    public const string PostedAfterPublication = "posted_after_publication";

    /// <summary>The total of a published statement is not the total of the lines it was cut from: <c>Expected</c> is the total of those lines, <c>Recorded</c> the one of the statement.</summary>
    public const string StatementTotalDiffers = "statement_total_differs";
}

/// <summary>The rules of the reconciliation that need no database (E13/F01/US03/T02).</summary>
public static class OwnerReconciliationRules
{
    /// <summary>
    /// Where the commission of a ledger line comes from, by its rule version (<see cref="ChannelFeeSources"/>): a rule of the agency (<c>channel-rule:{id}</c>), the configured
    /// default (<c>channel-default</c>), else the portal's own figure (<c>payload</c>, and the lines of before the rules existed, which could only come from the reservation).
    /// </summary>
    public static ChannelCommissionSource SourceOf(string? ruleVersion)
    {
        if (ruleVersion is not null && ruleVersion.StartsWith(ChannelFeeSources.RulePrefix, StringComparison.Ordinal))
            return ChannelCommissionSource.Rule;

        return string.Equals(ruleVersion, ChannelFeeSources.Default, StringComparison.Ordinal) ? ChannelCommissionSource.Default : ChannelCommissionSource.Portal;
    }

    /// <summary>
    /// Whether the portal collected the stay itself and so pays it out: unless the channel manager says the host collected it (<see cref="Booking.OtaCompanyCollect"/> <c>false</c>),
    /// as the payment ledger reads it.
    /// </summary>
    public static bool CollectedByPortal(bool? companyCollect) => companyCollect != false;
}

/// <summary>Where the commission of a portal comes from (<see cref="OwnerReconciliationRules.SourceOf"/>).</summary>
public enum ChannelCommissionSource
{
    /// <summary>The reservation of the portal.</summary>
    Portal = 1,

    /// <summary>A rule of the agency.</summary>
    Rule = 2,

    /// <summary>The configured default of the channel.</summary>
    Default = 3,
}
