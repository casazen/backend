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
    /// <summary>The supplier org (<see cref="SupplierProfile.OrgId"/>): the key, and the foreign key.</summary>
    [Key]
    public Guid OrgId { get; set; }

    /// <summary>Minutes kept free before and after every job (default 30).</summary>
    public int BufferMinutes { get; set; } = SupplierAgendaDefaults.BufferMinutes;

    /// <summary>Jobs a day takes at most (default 3): the day shows as full from then on.</summary>
    public int MaxJobsPerDay { get; set; } = SupplierAgendaDefaults.MaxJobsPerDay;

    /// <summary>Shortest notice, in hours, between a booking and its work (default 24).</summary>
    public int MinNoticeHours { get; set; } = SupplierAgendaDefaults.MinNoticeHours;

    /// <summary>How many days ahead of today a customer can book (default 35).</summary>
    public int HorizonDays { get; set; } = SupplierAgendaDefaults.HorizonDays;

    /// <summary>Minutes between two slots that start inside the same working band (default 60).</summary>
    public int SlotStepMinutes { get; set; } = SupplierAgendaDefaults.SlotStepMinutes;

    /// <summary>Jobs at the same time (decision D10: 1; the console does not offer more).</summary>
    public int ParallelJobs { get; set; } = SupplierAgendaDefaults.ParallelJobs;

    /// <summary>How long the supplier has to answer a new request, in minutes (default 180, SP-04).</summary>
    public int RespondWithinMinutes { get; set; } = SupplierAgendaDefaults.RespondWithinMinutes;

    /// <summary>Whether the public showcase takes bookings (default off, SP-09 and SP-10).</summary>
    public bool OnlineBookingEnabled { get; set; }

    /// <summary>
    /// When the supplier last saved working hours with at least one band; <c>null</c> while it has none (the checklist of
    /// the console asks for them). Set by <c>PUT api/supplier/availability/hours</c>.
    /// </summary>
    public DateTime? HoursConfiguredAt { get; set; }

    /// <summary>The supplier accepts the requests of its regular customers by itself (default off, a later task).</summary>
    public bool AutoAcceptRegulars { get; set; }

    /// <summary>The supplier wants a notification for every new request (default on, a later task).</summary>
    public bool NotifyNewRequests { get; set; } = true;

    /// <summary>The supplier wants the reminder of tomorrow's jobs (default on, a later task).</summary>
    public bool NotifyDayBeforeReminder { get; set; } = true;

    /// <summary>The supplier wants to know when a customer marks a job as paid (default on, a later task).</summary>
    public bool NotifyPaymentMarked { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
