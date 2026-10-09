using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// <c>POST /api/properties/{id}/mode/change</c> (PM-02): program the change of the property to <see cref="To"/> on
/// <see cref="EffectiveDate"/>, a calendar day (<c>2026-12-01</c>) of Europe/Rome, tomorrow at the earliest. The mode is
/// exclusive: <see cref="To"/> is the other mode than the property has.
/// </summary>
public class ScheduleModeChangeRequest
{
    /// <summary><c>Short</c> or <c>Long</c>.</summary>
    [Required(ErrorMessage = "PropertyModeTargetRequired")]
    public RentalMode? To { get; set; }

    /// <summary>First day in the new mode (a date, no time).</summary>
    [Required(ErrorMessage = "PropertyModeDateRequired")]
    public DateOnly? EffectiveDate { get; set; }
}

/// <summary>
/// A change of rental mode of a property (PM-02). Dates are calendar days (no time zone); the instants are UTC.
/// <see cref="FailureReason"/> is the stable code of the rule that stopped the job (for instance
/// <c>property_mode_blocked_by_bookings</c>), never a text.
/// </summary>
public sealed record PropertyModeChangeDto(
    Guid Id,
    Guid PropertyId,
    RentalMode FromMode,
    RentalMode ToMode,
    DateOnly EffectiveDate,
    PropertyModeChangeStatus Status,
    DateTime CreatedAt,
    DateTime? AppliedAt,
    DateTime? CancelledAt,
    DateTime? FailedAt,
    string? FailureReason)
{
    public static PropertyModeChangeDto From(PropertyModeChange change) => new(
        change.Id,
        change.PropertyId,
        change.FromMode,
        change.ToMode,
        DateOnly.FromDateTime(change.EffectiveDate),
        change.Status,
        change.CreatedAt,
        change.AppliedAt,
        change.CancelledAt,
        change.FailedAt,
        change.FailureReason);
}

/// <summary>
/// A stay, a block imported from a portal calendar or a lease that stands in the way of a change (PM-02): ids, dates and
/// status only, never a name, an e-mail or the text of a portal. <see cref="End"/> is the departure of a stay, the first
/// free day of a block, the last day of a lease; <see cref="FreeFrom"/> the first day the change can start without being
/// stopped by it (<c>null</c> for a draft lease: no day frees it, it is deleted).
/// </summary>
public sealed record PropertyModeBlockerDto(
    PropertyModeBlockerKind Kind,
    Guid Id,
    DateOnly Start,
    DateOnly End,
    DateOnly? FreeFrom,
    string? Status,
    string? Source)
{
    public static PropertyModeBlockerDto From(PropertyModeBlocker blocker) => new(
        blocker.Kind,
        blocker.Id,
        DateOnly.FromDateTime(blocker.Start),
        DateOnly.FromDateTime(blocker.End),
        blocker.FreeFrom is { } freeFrom ? DateOnly.FromDateTime(freeFrom) : null,
        blocker.Status,
        blocker.Source);
}

/// <summary>
/// <c>GET /api/properties/{id}/mode/preview</c> (PM-02): what a change to <see cref="TargetMode"/> on <see cref="Date"/>
/// would meet today. <see cref="CanSchedule"/> is true when <see cref="Issues"/> is empty. <see cref="Issues"/> are the
/// stable error codes the creation would answer (<c>property_mode_date_too_early</c>, <c>property_mode_blocked_by_bookings</c>,
/// <c>property_mode_blocked_by_lease</c>, <c>property_mode_blocked_by_draft_lease</c>, <c>property_mode_change_exists</c>…).
/// <see cref="CalendarClosedUntil"/>, for a change to long-term, is the first free day after the dates the change closes in
/// the calendar from <see cref="Date"/>.
/// </summary>
public sealed record PropertyModePreviewResponse(
    Guid PropertyId,
    RentalMode CurrentMode,
    RentalMode TargetMode,
    DateOnly Today,
    DateOnly EarliestDate,
    DateOnly Date,
    bool CanSchedule,
    IReadOnlyList<string> Issues,
    IReadOnlyList<PropertyModeBlockerDto> Blockers,
    DateOnly? CalendarClosedUntil,
    PropertyModeChangeDto? ScheduledChange)
{
    public static PropertyModePreviewResponse From(PropertyModePreview preview) => new(
        preview.PropertyId,
        preview.From,
        preview.To,
        DateOnly.FromDateTime(preview.Today),
        DateOnly.FromDateTime(preview.EarliestDate),
        DateOnly.FromDateTime(preview.Date),
        preview.CanSchedule,
        preview.Issues,
        preview.Blockers.Select(PropertyModeBlockerDto.From).ToList(),
        preview.To == RentalMode.Long ? DateOnly.FromDateTime(PropertyModeRules.CalendarBlockEnd(preview.Date)) : null,
        preview.ScheduledChange is { } scheduled ? PropertyModeChangeDto.From(scheduled) : null);
}

/// <summary>
/// <c>GET /api/properties/{id}/mode</c> (PM-02): the mode of the property, the change waiting for its day and the last one
/// that is over (applied, cancelled or failed).
/// </summary>
public sealed record PropertyModeStateResponse(
    Guid PropertyId,
    RentalMode RentalMode,
    PropertyModeChangeDto? ScheduledChange,
    PropertyModeChangeDto? LastChange)
{
    public static PropertyModeStateResponse From(PropertyModeState state) => new(
        state.PropertyId,
        state.Mode,
        state.Scheduled is { } scheduled ? PropertyModeChangeDto.From(scheduled) : null,
        state.Last is { } last ? PropertyModeChangeDto.From(last) : null);
}
