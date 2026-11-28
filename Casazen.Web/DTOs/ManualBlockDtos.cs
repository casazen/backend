using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public class CreateManualBlockRequest
{
    [Required(ErrorMessage = "CalendarBlockStartRequired")]
    public DateTime? StartDate { get; set; }

    [Required(ErrorMessage = "CalendarBlockEndRequired")]
    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "CalendarBlockReasonRequired")]
    public CalendarBlockReason? Reason { get; set; }

    [MaxLength(CalendarBlock.ManualNoteMaxLength, ErrorMessage = "CalendarBlockNoteTooLong")]
    public string? Note { get; set; }
}

public class ManualBlockDto
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public int Nights { get; set; }

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
