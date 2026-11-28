namespace Casazen.Core.Entities.Enums;

public enum OwnerLedgerEntryType
{
    BookingRevenue = 1,

    ChannelFee = 2,

    AgencyCommission = 3,

    OwnerCharge = 4,

    OwnerExpense = 5,

    OwnerPayment = 6,

    AgencyPayment = 7,

    Correction = 8,

    Withholding = 9,

    /// <summary>
    /// The VAT an owner invoice added on top of the commission and the recharges (E14/F01/US01/T01, ⚖ R4: a mandate «+ IVA»): posted when the invoice is
    /// issued (debit, source <see cref="OwnerLedgerSourceType.OwnerInvoice"/>), so the net paid to the owner is right; a credit note of the invoice posts
    /// its reversal. A mandate «IVA inclusa» posts none: the VAT is inside the amounts the ledger already holds.
    /// </summary>
    AgencyVat = 10,

    /// <summary>
    /// The owner's share of the commission a portal kept on a stay of a listing whose account the agency holds (V2) while the organization does not recharge the
    /// commissions of the accounts it holds (<see cref="OrgFiscalIssuerProfile.RechargePortalCommissions"/> off, ⚖ I11; decision 1 of 20/11/2026,
    /// FIX/BE-V2-NO-RECHARGE): the portal bills the agency, no invoice of the property manager carries the commission to the owner, so it is a cost of the agency and
    /// the owner is not charged. Posted instead of a <see cref="ChannelFee"/>, with the amount a channel fee would have (negative), <b>for information only</b>: the
    /// statement shows it, the TD17, the accountant package and the reconciliation read it as the commission of the portal, and no balance of the owner counts it
    /// (<c>OwnerLedgerBalance</c>: the closing, the opening of the next month, the totals of a version).
    /// </summary>
    ChannelFeeBorneByAgency = 11,
}

public enum OwnerLedgerSourceType
{
    Payment = 1,

    /// <summary>
    /// A booking: the channel commission, and since E13/F01/US01/T02 the revenue (and its commission) of a stay without a CasaZen payment (a portal, the
    /// agency's own site, a booking entered by hand), from its amounts at the check-out.
    /// </summary>
    Booking = 2,

    OwnerCharge = 3,

    WireBatch = 4,

    Manual = 5,

    OwnerPayout = 6,

    /// <summary>
    /// A flat fee of the owner's mandate for a stay (E13/F01/US01/T02): the <see cref="OwnerLedgerEntry.SourceId"/> is the booking, the
    /// <see cref="OwnerLedgerEntry.SourceReference"/> the kind (<c>cleaning</c>, <c>linen</c>), the line an <see cref="OwnerLedgerEntryType.OwnerCharge"/>. A
    /// recharge of the agency, approved by the mandate itself: the owner invoice (E14) applies its VAT.
    /// </summary>
    MandateFlatFee = 7,

    /// <summary>
    /// An owner invoice CasaZen issued, or a credit note of one (E14/F01/US01/T01): the <see cref="OwnerLedgerEntry.SourceId"/> and the
    /// <see cref="OwnerLedgerEntry.SourceDocumentId"/> are the document, the line an <see cref="OwnerLedgerEntryType.AgencyVat"/>. Never planned from the
    /// sources of a month: the posting of a month neither posts nor reverses it.
    /// </summary>
    OwnerInvoice = 8,

    /// <summary>
    /// A cleaning completed by the agency's company or staff and recharged to the owner at its cost (E14/F01/US02/T01, ⚖ I13): the <see cref="OwnerLedgerEntry.SourceId"/> is the
    /// cleaning request (or the staff activity), the <see cref="OwnerLedgerEntry.SourceReference"/> its short reference
    /// (<c>OwnerLedgerCleaningRecharges.ReferenceOf</c>), the <see cref="OwnerLedgerEntry.AttachmentReference"/> the reference of the supplier's invoice when the model links one, the
    /// line an <see cref="OwnerLedgerEntryType.OwnerCharge"/>. A recharge of the agency like a flat fee, which it replaces for the mandate (never both): the owner invoice (E14)
    /// shows one line for each request and applies its VAT.
    /// </summary>
    CleaningRequest = 9,
}

public enum PortalAccountHolder
{
    Owner = 1,

    Agency = 2,
}

public enum OwnerStatementVersionStatus
{
    Draft = 1,

    Flagged = 2,

    Approved = 3,

    Published = 4,

    Superseded = 5,
}

public enum OwnerClosePeriodStatus
{
    Open = 1,

    Closed = 2,
}

public enum OwnerChargeKind
{
    Charge = 1,

    Expense = 2,
}

public enum OwnerChargeRecurrence
{
    OneTime = 1,

    Monthly = 2,
}

public enum OwnerPayoutStatus
{
    Confirmed = 1,

    Reversed = 2,
}

public enum TaxDecision
{
    Applicable = 1,

    NotApplicable = 2,
}

/// <summary>
/// Where a tax file is (an F24, an annual report, E06/F05/US02/T03): generated and reviewed are never filed; submitted only through a controlled manual
/// handoff; accepted with the receipt, or rejected. Stored as its number.
/// </summary>
public enum TaxFilingState
{
    Generated = 1,
    Reviewed = 2,
    Submitted = 3,
    Accepted = 4,
    Rejected = 5,
}

public enum OwnerExternalDocumentStatus
{
    Entered = 1,

    Verified = 2,
}

public enum OwnerChargeStatus
{
    Pending = 1,

    Approved = 2,

    Cancelled = 3,
}

public enum CommissionBase
{
    GrossExclTouristTax = 1,

    GrossExclTouristTaxAndCleaning = 2,
}
