using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>Suppliers:ServiceRequests</c> (SP-04): the times and the tolerance of the service request flows. Every value
/// has a default (the decisions of the wave spec) and is validated at startup. Runbook <c>docs/runbooks/suppliers.md</c>,
/// section 21.
/// </summary>
public class ServiceRequestOptions
{
    public const string SectionName = "Suppliers:ServiceRequests";

    /// <summary>Shortest and longest time a request waits for its answer, in minutes (10 minutes to 3 days).</summary>
    public const int MinResponseMinutes = 10;

    /// <inheritdoc cref="MinResponseMinutes"/>
    public const int MaxResponseMinutes = 3 * 24 * 60;

    /// <summary>
    /// Minutes a host's request waits for the supplier's answer (decision D8: 120). The request gets
    /// <c>ResponseDueAt = creation + this</c>; the job <c>service-request-auto-cancel</c> (behind the flag
    /// <c>SupplierRequestAutoCancel</c>) cancels it after that. Env var <c>Suppliers__ServiceRequests__HostResponseMinutes</c>.
    /// </summary>
    public int HostResponseMinutes { get; set; } = 120;

    /// <summary>
    /// Decision D7: a final amount more than this percentage above the quote needs the customer's confirmation (20). Env var
    /// <c>Suppliers__ServiceRequests__FinalAmountTolerancePercent</c>.
    /// </summary>
    public int FinalAmountTolerancePercent { get; set; } = 20;

    /// <summary>
    /// Hours between two reminders of the host to the same supplier (6). Env var
    /// <c>Suppliers__ServiceRequests__RemindIntervalHours</c>.
    /// </summary>
    public int RemindIntervalHours { get; set; } = 6;

    /// <summary>
    /// SP-04d (decision SP-D6): how many jobs a host's org must already have taken with a supplier (<c>PresoInCarico</c>, <c>InCorso</c>,
    /// <c>Completato</c> or <c>Pagato</c>) to be one of its regulars, whose requests the supplier accepts automatically when it turned
    /// that on (3). Env var <c>Suppliers__ServiceRequests__RegularHostMinJobs</c>.
    /// </summary>
    public int RegularHostMinJobs { get; set; } = 3;

    /// <summary>Most backup suppliers a group can have (5): the cap of <see cref="MaxBackupSuppliers"/>.</summary>
    public const int MaxBackupSuppliersCap = 5;

    /// <summary>
    /// SP-BKP: minutes the supplier the host chose (the primary) has to take a job that was also sent to backups (60): its
    /// request gets <c>ResponseDueAt = creation + this</c> instead of <see cref="HostResponseMinutes"/>. After it the work goes to
    /// the backup that accepted first. Env var <c>Suppliers__ServiceRequests__PrimaryResponseMinutes</c>.
    /// </summary>
    public int PrimaryResponseMinutes { get; set; } = 60;

    /// <summary>
    /// SP-BKP: minutes of the second window (60), which starts when the primary's window passed with no backup having accepted (or when
    /// the primary refused): every supplier of the group that is still waiting can take the job and the first to do so has it; if
    /// nobody does, the requests lapse. Env var <c>Suppliers__ServiceRequests__BackupResponseMinutes</c>.
    /// </summary>
    public int BackupResponseMinutes { get; set; } = 60;

    /// <summary>
    /// SP-BKP: how many backup suppliers a request is sent to at most (3). Fewer when the comune and the category have fewer
    /// suppliers that are free at that time; none means the request is sent to one supplier only, as before. Env var
    /// <c>Suppliers__ServiceRequests__MaxBackupSuppliers</c>.
    /// </summary>
    public int MaxBackupSuppliers { get; set; } = 3;

    /// <summary>Configuration errors, empty when the section is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        if (HostResponseMinutes is < MinResponseMinutes or > MaxResponseMinutes)
        {
            failures.Add(
                $"Suppliers__ServiceRequests__HostResponseMinutes must be between {MinResponseMinutes} and {MaxResponseMinutes} minutes.");
        }

        if (FinalAmountTolerancePercent is < 0 or > 100)
            failures.Add("Suppliers__ServiceRequests__FinalAmountTolerancePercent must be a percentage between 0 and 100.");
        if (RemindIntervalHours is < 1 or > 72)
            failures.Add("Suppliers__ServiceRequests__RemindIntervalHours must be between 1 and 72 hours.");
        if (RegularHostMinJobs is < 1 or > 100)
            failures.Add("Suppliers__ServiceRequests__RegularHostMinJobs must be between 1 and 100 jobs.");
        if (PrimaryResponseMinutes is < MinResponseMinutes or > MaxResponseMinutes)
        {
            failures.Add(
                $"Suppliers__ServiceRequests__PrimaryResponseMinutes must be between {MinResponseMinutes} and {MaxResponseMinutes} minutes.");
        }

        if (BackupResponseMinutes is < MinResponseMinutes or > MaxResponseMinutes)
        {
            failures.Add(
                $"Suppliers__ServiceRequests__BackupResponseMinutes must be between {MinResponseMinutes} and {MaxResponseMinutes} minutes.");
        }

        if (MaxBackupSuppliers is < 1 or > MaxBackupSuppliersCap)
            failures.Add($"Suppliers__ServiceRequests__MaxBackupSuppliers must be between 1 and {MaxBackupSuppliersCap} suppliers.");
        return failures;
    }
}

/// <summary>Refuses a <c>Suppliers:ServiceRequests</c> section that is not valid at startup, so a typo never changes a deadline silently.</summary>
public sealed class ServiceRequestOptionsValidator : IValidateOptions<ServiceRequestOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceRequestOptions options)
    {
        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
