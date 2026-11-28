using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Dates the host closes by hand on a property (PC-09, A2-25): owner stay, maintenance, anything else. Arrival day
/// <paramref name="StartDate"/> included, <paramref name="EndDate"/> excluded (the nights of
/// <see cref="PropertyOccupancy"/>), both stay dates without time.
/// </summary>
/// <param name="Note">Optional text for the host only (at most <see cref="CalendarBlock.ManualNoteMaxLength"/> characters).</param>
/// <param name="BlockId">
/// The id the block takes; a new one when <c>null</c>. An owner stay gives its own id (E07/F07/US01/T01), so the block of a stay is found again by it and a
/// second approval never creates a second block.
/// </param>
public sealed record ManualBlockRequest(
    Guid PropertyId,
    DateTime StartDate,
    DateTime EndDate,
    CalendarBlockReason Reason,
    string? Note = null,
    Guid? BlockId = null);

public sealed record ManualBlockChange(CalendarBlock Block, Guid? ChangeProcessId, CalendarChangeState? ChangeState);

/// <summary>
/// Manual calendar blocks (<see cref="CalendarBlockSource.Manual"/>, PC-09). A manual block is an ordinary
/// <see cref="CalendarBlock"/>: its nights are taken for the booking site, the booking checks and the export to the OTAs
/// through <see cref="PropertyOccupancy"/>, nothing else counts them. Rules: <see cref="ManualBlocks"/>; runbook
/// docs/runbooks/ical.md "Manual blocks (PC-09)". The caller authorizes the property (TN-3); the tenant filter hides
/// another org's rows.
/// </summary>
public interface ICalendarBlockService
{
    Task<IReadOnlyList<CalendarBlock>> ListManualAsync(
        Guid propertyId,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the nights of the request. Under the dates lock of the property, the same one the bookings take when they
    /// are saved: a booking and a block can never take the same night at the same time.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ManualBlockErrorCodes.PropertyNotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ManualBlockErrorCodes.InvalidRange"/>, <see cref="ManualBlockErrorCodes.InPast"/>,
    /// <see cref="ManualBlockErrorCodes.TooLong"/>, <see cref="ManualBlockErrorCodes.InvalidReason"/>,
    /// <see cref="ManualBlockErrorCodes.NoteTooLong"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <see cref="ManualBlockErrorCodes.OverlapsBooking"/>, <see cref="ManualBlockErrorCodes.OverlapsBlock"/>.
    /// </exception>
    Task<CalendarBlock> CreateAsync(ManualBlockRequest request, CancellationToken cancellationToken = default);

    Task<ManualBlockChange> CreateTrackedAsync(ManualBlockRequest request, CancellationToken cancellationToken = default);

    Task<CalendarBlock?> FindAsync(Guid blockId, CancellationToken cancellationToken = default);

    /// <summary>Removes a manual block: its nights are free again at once (site, bookings, export).</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ManualBlockErrorCodes.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ManualBlockErrorCodes.NotManual"/>: an imported block goes away with its feed, never by hand.
    /// <see cref="ManualBlockErrorCodes.HeldByModeChange"/>: the block of a property that went long-term goes away with the
    /// return to short stays (PM-02), never by hand.
    /// <see cref="ManualBlockErrorCodes.HeldByOwnerStay"/>: the block of an owner stay goes away with the withdrawal or the cancellation of the stay
    /// (E07/F07/US01/T01), never by hand.
    /// </exception>
    Task DeleteAsync(Guid blockId, CancellationToken cancellationToken = default);

    Task<ManualBlockChange?> ReleaseOwnerStayBlockAsync(Guid blockId, CancellationToken cancellationToken = default);
}

public static class ManualBlocks
{
    public const int MaxNights = 366;
}

public static class ManualBlockErrorCodes
{
    public const string PropertyNotFound = "property_not_found";

    public const string NotFound = "calendar_block_not_found";

    public const string InvalidRange = "calendar_block_invalid_range";

    public const string InPast = "calendar_block_in_past";

    public const string TooLong = "calendar_block_too_long";

    public const string InvalidReason = "calendar_block_invalid_reason";

    public const string NoteTooLong = "calendar_block_note_too_long";

    public const string OverlapsBooking = "calendar_block_overlaps_booking";

    public const string OverlapsBlock = "calendar_block_overlaps_block";

    public const string NotManual = "calendar_block_not_manual";

    public const string HeldByModeChange = "calendar_block_held_by_mode_change";

    public const string HeldByOwnerStay = "calendar_block_held_by_owner_stay";

    public const string PropertyNotFoundMessageKey = "PropertyNotFound";
    public const string NotFoundMessageKey = "CalendarBlockNotFound";
    public const string InvalidRangeMessageKey = "CalendarBlockInvalidRange";
    public const string InPastMessageKey = "CalendarBlockInPast";
    public const string TooLongMessageKey = "CalendarBlockTooLong";
    public const string InvalidReasonMessageKey = "CalendarBlockInvalidReason";
    public const string NoteTooLongMessageKey = "CalendarBlockNoteTooLong";
    public const string OverlapsBookingMessageKey = "CalendarBlockOverlapsBooking";
    public const string OverlapsBlockMessageKey = "CalendarBlockOverlapsBlock";
    public const string NotManualMessageKey = "CalendarBlockNotManual";
    public const string HeldByModeChangeMessageKey = "CalendarBlockHeldByModeChange";
    public const string HeldByOwnerStayMessageKey = "CalendarBlockHeldByOwnerStay";
}
