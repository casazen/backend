using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Dates the host closes by hand on a property (PC-09, A2-25): owner stay, maintenance, anything else. Arrival day
/// <paramref name="StartDate"/> included, <paramref name="EndDate"/> excluded (the nights of
/// <see cref="PropertyOccupancy"/>), both stay dates without time.
/// </summary>
/// <param name="Note">Optional text for the host only (at most <see cref="CalendarBlock.ManualNoteMaxLength"/> characters).</param>
public sealed record ManualBlockRequest(
    Guid PropertyId,
    DateTime StartDate,
    DateTime EndDate,
    CalendarBlockReason Reason,
    string? Note = null);

/// <summary>
/// Manual calendar blocks (<see cref="CalendarBlockSource.Manual"/>, PC-09). A manual block is an ordinary
/// <see cref="CalendarBlock"/>: its nights are taken for the booking site, the booking checks and the export to the OTAs
/// through <see cref="PropertyOccupancy"/>, nothing else counts them. Rules: <see cref="ManualBlocks"/>; runbook
/// docs/runbooks/ical.md "Manual blocks (PC-09)". The caller authorizes the property (TN-3); the tenant filter hides
/// another org's rows.
/// </summary>
public interface ICalendarBlockService
{
    /// <summary>
    /// Manual blocks of the property with a night from <paramref name="fromDate"/> (included) to <paramref name="toDate"/>
    /// (excluded), by start date. Without dates: the blocks not over yet (end after today, Europe/Rome).
    /// </summary>
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

    /// <summary>The block, or null when it does not exist or belongs to another org (tenant filter). Read only.</summary>
    Task<CalendarBlock?> FindAsync(Guid blockId, CancellationToken cancellationToken = default);

    /// <summary>Removes a manual block: its nights are free again at once (site, bookings, export).</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ManualBlockErrorCodes.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ManualBlockErrorCodes.NotManual"/>: an imported block goes away with its feed, never by hand.
    /// <see cref="ManualBlockErrorCodes.HeldByModeChange"/>: the block of a property that went long-term goes away with the
    /// return to short stays (PM-02), never by hand.
    /// </exception>
    Task DeleteAsync(Guid blockId, CancellationToken cancellationToken = default);
}

/// <summary>Rules of the manual blocks (PC-09).</summary>
public static class ManualBlocks
{
    /// <summary>Longest block: one year of nights. Longer closures are a property put on pause, not a block.</summary>
    public const int MaxNights = 366;
}

/// <summary>Error codes of the manual blocks (FD-05), with the keys of their localized messages.</summary>
public static class ManualBlockErrorCodes
{
    /// <summary>404: the property does not exist or is of another org.</summary>
    public const string PropertyNotFound = "property_not_found";

    /// <summary>404: no block with that id on the property (or of another org).</summary>
    public const string NotFound = "calendar_block_not_found";

    /// <summary>422: the end date is not after the start date.</summary>
    public const string InvalidRange = "calendar_block_invalid_range";

    /// <summary>422: the block starts before today (Europe/Rome).</summary>
    public const string InPast = "calendar_block_in_past";

    /// <summary>422: more than <see cref="ManualBlocks.MaxNights"/> nights.</summary>
    public const string TooLong = "calendar_block_too_long";

    /// <summary>
    /// 422: reason not one of the three the host chooses (<see cref="CalendarBlockReason.Owner"/>,
    /// <see cref="CalendarBlockReason.Maintenance"/>, <see cref="CalendarBlockReason.Other"/>): not defined, or
    /// <see cref="CalendarBlockReason.ModeChange"/>, which only CasaZen writes.
    /// </summary>
    public const string InvalidReason = "calendar_block_invalid_reason";

    /// <summary>422: note longer than <see cref="CalendarBlock.ManualNoteMaxLength"/> characters.</summary>
    public const string NoteTooLong = "calendar_block_note_too_long";

    /// <summary>409: a booking (not cancelled, not an expired checkout hold) already takes one of the nights.</summary>
    public const string OverlapsBooking = "calendar_block_overlaps_booking";

    /// <summary>409: another manual block already closes one of the nights.</summary>
    public const string OverlapsBlock = "calendar_block_overlaps_block";

    /// <summary>422: the block was imported from an iCal feed: it is removed with its feed or by the channel.</summary>
    public const string NotManual = "calendar_block_not_manual";

    /// <summary>
    /// 422: the block is the one CasaZen holds for a property that went long-term (<see cref="CalendarBlockReason.ModeChange"/>,
    /// PM-02): it is removed when the property goes back to short stays, never by hand.
    /// </summary>
    public const string HeldByModeChange = "calendar_block_held_by_mode_change";

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
}
