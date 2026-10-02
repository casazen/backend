using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// <c>POST /api/properties/{id}/blocks</c> (PC-09): the host closes the nights from <see cref="StartDate"/> (included) to
/// <see cref="EndDate"/> (excluded, the first free day), both stay dates (<c>2026-10-10</c>).
/// </summary>
public class CreateManualBlockRequest
{
    [Required(ErrorMessage = "CalendarBlockStartRequired")]
    public DateTime? StartDate { get; set; }

    [Required(ErrorMessage = "CalendarBlockEndRequired")]
    public DateTime? EndDate { get; set; }

    /// <summary><c>Owner</c>, <c>Maintenance</c> or <c>Other</c>.</summary>
    [Required(ErrorMessage = "CalendarBlockReasonRequired")]
    public CalendarBlockReason? Reason { get; set; }

    /// <summary>Optional note for the host only: never exported, never shown to guests.</summary>
    [MaxLength(CalendarBlock.ManualNoteMaxLength, ErrorMessage = "CalendarBlockNoteTooLong")]
    public string? Note { get; set; }
}

/// <summary>A manual block of a property (PC-09).</summary>
public class ManualBlockDto
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }

    /// <summary>First closed night (stay date, no time zone: <c>2026-10-10T00:00:00</c>).</summary>
    public DateTime StartDate { get; set; }

    /// <summary>First free day after the block (stay date, no time zone).</summary>
    public DateTime EndDate { get; set; }

    public int Nights { get; set; }

    /// <summary><c>Owner</c>, <c>Maintenance</c> or <c>Other</c>.</summary>
    public string Reason { get; set; } = string.Empty;

    public string? Note { get; set; }

    public static ManualBlockDto From(CalendarBlock block) => new()
    {
        Id = block.Id,
        PropertyId = block.PropertyId,
        StartDate = HostCalendarRange.StayDate(block.StartUtc),
        EndDate = HostCalendarRange.StayDate(block.EndUtc),
        Nights = Math.Max(0, (block.EndUtc.Date - block.StartUtc.Date).Days),
        Reason = (block.ManualReason ?? CalendarBlockReason.Other).ToString(),
        Note = block.Summary,
    };
}
