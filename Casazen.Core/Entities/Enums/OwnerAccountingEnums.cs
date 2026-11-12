namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a line of the owner ledger records (E06/F05/US02/T02, NF-110). The sign of <see cref="OwnerLedgerEntry.Amount"/> says who owes whom: positive
/// is owed to the owner, negative reduces it. Stored as its number: never renumber.
/// </summary>
public enum OwnerLedgerEntryType
{
    /// <summary>The owner's share of a payment collected for a booking of the property (credit).</summary>
    BookingRevenue = 1,

    /// <summary>The owner's share of the commission the channel kept on a booking whose payments are gross (debit).</summary>
    ChannelFee = 2,

    /// <summary>The agency's commission under the owner's mandate on the owner's revenue (debit).</summary>
    AgencyCommission = 3,

    /// <summary>A charge the agency bills the owner, a management fee or a service (debit).</summary>
    OwnerCharge = 4,

    /// <summary>An expense the agency paid for the owner, a repair or a utility (debit).</summary>
    OwnerExpense = 5,

    /// <summary>A payment from the agency to the owner, a paid wire transfer (debit: it settles what is owed).</summary>
    OwnerPayment = 6,

    /// <summary>A payment from the owner to the agency, for instance to cover a negative balance (credit).</summary>
    AgencyPayment = 7,

    /// <summary>A manual correction with its reason, posted after the reversal of the entry it replaces.</summary>
    Correction = 8,
}

/// <summary>Where a line of the owner ledger comes from (E06/F05/US02/T02). Stored as its number: never renumber.</summary>
public enum OwnerLedgerSourceType
{
    /// <summary>A completed payment of a booking.</summary>
    Payment = 1,

    /// <summary>A booking (the channel commission).</summary>
    Booking = 2,

    /// <summary>An approved charge or expense of the owner.</summary>
    OwnerCharge = 3,

    /// <summary>A wire batch marked paid.</summary>
    WireBatch = 4,

    /// <summary>A manual correction.</summary>
    Manual = 5,
}

/// <summary>The life of a version of an owner statement (E06/F05/US02/T02, AC-NF110-2). Stored as its number: never renumber.</summary>
public enum OwnerStatementVersionStatus
{
    /// <summary>Generated from a ledger cut-off with no blocking issue: it can be approved.</summary>
    Draft = 1,

    /// <summary>Generated with blocking readiness issues: it cannot be approved, a new version follows their resolution.</summary>
    Flagged = 2,

    /// <summary>Approved: it can be published.</summary>
    Approved = 3,

    /// <summary>Published to the owner: immutable, its file and hash stay retrievable.</summary>
    Published = 4,

    /// <summary>Replaced by a later version of the same period: kept, never changed.</summary>
    Superseded = 5,
}

/// <summary>Whether the month of an owner is still open (E06/F05/US02/T02, AC-NF110-3). Stored as its number.</summary>
public enum OwnerClosePeriodStatus
{
    /// <summary>The ledger of the month can still change.</summary>
    Open = 1,

    /// <summary>Closed after the reconciliation or an explicit waiver: nothing is posted in it any more.</summary>
    Closed = 2,
}

/// <summary>What the agency debits to an owner (E06/F05/US02/T02). Stored as its number.</summary>
public enum OwnerChargeKind
{
    /// <summary>A charge of the agency (a fee, a service).</summary>
    Charge = 1,

    /// <summary>An expense paid for the owner.</summary>
    Expense = 2,
}

/// <summary>How often a charge is posted (E06/F05/US02/T02). Stored as its number.</summary>
public enum OwnerChargeRecurrence
{
    /// <summary>Once, on its first day.</summary>
    OneTime = 1,

    /// <summary>Every month from its first day to its last.</summary>
    Monthly = 2,
}

/// <summary>The approval of a charge (E06/F05/US02/T02). Stored as its number.</summary>
public enum OwnerChargeStatus
{
    /// <summary>Waiting for the approval: never posted.</summary>
    Pending = 1,

    /// <summary>Approved: posted in its months.</summary>
    Approved = 2,

    /// <summary>Cancelled: what it posted is reversed.</summary>
    Cancelled = 3,
}
