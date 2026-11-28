using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// The supplier's own settings, one row per supplier (SP-03, primary key = the supplier org): the rules of the agenda
/// (buffer, jobs a day, notice, horizon, slot step) that <c>api/supplier/availability/rules</c> edits, and the columns the
/// next tasks use (how long to answer, online booking, automatic acceptance of regular customers, notifications).
/// </summary>
/// <remarks>
/// <para><b>Lazy row.</b> A supplier has no row until it saves a rule or its working hours: a read without a row answers
/// the defaults of <see cref="SupplierAgendaDefaults"/> and writes nothing. The row is created, under the agenda lock, by the
/// first write.</para>
/// <para><b>Who uses what.</b> SP-03 reads and writes <see cref="BufferMinutes"/>, <see cref="MaxJobsPerDay"/>,
/// <see cref="MinNoticeHours"/>, <see cref="HorizonDays"/>, <see cref="SlotStepMinutes"/> and
/// <see cref="HoursConfiguredAt"/>; the planner reads <see cref="ParallelJobs"/> (decision D10: 1, not editable from the
/// console). <see cref="RespondWithinMinutes"/> (SP-04), <see cref="OnlineBookingEnabled"/> (SP-09, SP-10) and the other
/// columns are in the table so that the migration is one; no endpoint changes them yet (<c>api/supplier/settings</c> is a
/// later task).</para>
/// <para>Keyed by the supplier org and not tenant-filtered, like <see cref="SupplierWorkingHours"/> (same reasons, same
/// guard).</para>
/// </remarks>
[Table("SupplierSettings")]
public class SupplierSettings
{
    [Key]
    public Guid OrgId { get; set; }

    public int BufferMinutes { get; set; } = SupplierAgendaDefaults.BufferMinutes;

    public int MaxJobsPerDay { get; set; } = SupplierAgendaDefaults.MaxJobsPerDay;

    public int MinNoticeHours { get; set; } = SupplierAgendaDefaults.MinNoticeHours;

    public int HorizonDays { get; set; } = SupplierAgendaDefaults.HorizonDays;

    public int SlotStepMinutes { get; set; } = SupplierAgendaDefaults.SlotStepMinutes;

    /// <summary>Jobs at the same time (decision D10: 1; the console does not offer more).</summary>
    public int ParallelJobs { get; set; } = SupplierAgendaDefaults.ParallelJobs;

    public int RespondWithinMinutes { get; set; } = SupplierAgendaDefaults.RespondWithinMinutes;

    public bool OnlineBookingEnabled { get; set; }

    public DateTime? HoursConfiguredAt { get; set; }

    /// <summary>
    /// The supplier accepts the requests of its regular hosts by itself (default off). Read when a host's request is created
    /// (SP-04d, <see cref="ServiceRequestAutoAccept"/>); written by <c>PUT api/supplier/settings</c> (SP-16a-1), under the same
    /// agenda lock that creation takes, so a request is judged on the value as it is saved.
    /// </summary>
    public bool AutoAcceptRegulars { get; set; }

    public bool NotifyNewRequests { get; set; } = true;

    public bool NotifyDayBeforeReminder { get; set; } = true;

    public bool NotifyPaymentMarked { get; set; } = true;

    /// <summary>
    /// The supplier accepts urgent requests (SP-04c, decision SP-D9): for today and tomorrow, with 30 minutes of notice, inside
    /// <see cref="UrgentFromMinute"/>–<see cref="UrgentToMinute"/>, for the services that offer them
    /// (<see cref="SupplierServiceListing.UrgentEnabled"/>). Default off. Written by <c>PUT api/supplier/availability/urgent</c>, under
    /// the agenda lock; read by the slot planner.
    /// </summary>
    public bool UrgentEnabled { get; set; }

    public int UrgentFromMinute { get; set; } = SupplierAgendaDefaults.UrgentFromMinute;

    public int UrgentToMinute { get; set; } = SupplierAgendaDefaults.UrgentToMinute;

    public int UrgentSurchargeCents { get; set; } = SupplierAgendaDefaults.UrgentSurchargeCents;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
