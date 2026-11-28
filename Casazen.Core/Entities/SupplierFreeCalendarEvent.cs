using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// An event of the supplier's iCal feed that the calendar marks "Available" (<c>TRANSP:TRANSPARENT</c>, for instance every
/// all-day event of Google Calendar) and the answer the supplier gave about it. Until the supplier answers, the event
/// <b>occupies</b> its days or hours (a free-looking event the supplier has not confirmed must not leave the day bookable);
/// <see cref="SupplierFreeEventDecision.Available"/> frees it and it is never asked again, <see cref="SupplierFreeEventDecision.Busy"/>
/// keeps it occupied like any busy event.
/// </summary>
/// <remarks>
/// <para><b>One decision per series.</b> The key is the UID for a recurring event (every occurrence shares the answer) and the UID
/// plus the start for a single event (a single event that moves is asked again). An event without UID is keyed by a hash of its
/// content, as <c>ICalImportService</c> does for the other imports. The key is stored as a SHA-256, never the UID itself.</para>
/// <para><b>Personal data.</b> The <see cref="Summary"/> is the title of the supplier's own event: it is cut to 80 characters,
/// shown only to the supplier, and the rows are deleted when the supplier unlinks or replaces the calendar and when the event
/// leaves the feed. Keyed by the supplier org and not tenant-filtered, like <see cref="SupplierBusyWindow"/>.</para>
/// </remarks>
[Table("SupplierFreeCalendarEvents")]
public class SupplierFreeCalendarEvent
{
    public const int KeyLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid OrgId { get; set; }

    [Required, MaxLength(KeyLength)]
    public string DecisionKey { get; set; } = string.Empty;

    [Required, MaxLength(KeyLength)]
    public string UidHash { get; set; } = string.Empty;

    [MaxLength(SupplierAgendaLimits.LabelMaxLength)]
    public string? Summary { get; set; }

    public bool IsAllDay { get; set; }

    public bool Recurring { get; set; }

    public DateTime NextStartUtc { get; set; }

    public DateTime NextEndUtc { get; set; }

    public int Occurrences { get; set; } = 1;

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public SupplierFreeEventDecision Decision { get; set; } = SupplierFreeEventDecision.Pending;

    public DateTime? DecidedAt { get; set; }

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
