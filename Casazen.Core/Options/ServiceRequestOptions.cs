using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

public class ServiceRequestOptions
{
    public const string SectionName = "Suppliers:ServiceRequests";

    public const int MinResponseMinutes = 10;

    public const int MaxResponseMinutes = 3 * 24 * 60;

    public int HostResponseMinutes { get; set; } = 120;

    public int FinalAmountTolerancePercent { get; set; } = 20;

    public int RemindIntervalHours { get; set; } = 6;

    /// <summary>
    /// SP-04d (decision SP-D6): how many jobs a host's org must already have taken with a supplier (<c>PresoInCarico</c>, <c>InCorso</c>,
    /// <c>Completato</c> or <c>Pagato</c>) to be one of its regulars, whose requests the supplier accepts automatically when it turned
    /// that on (3). Env var <c>Suppliers__ServiceRequests__RegularHostMinJobs</c>.
    /// </summary>
    public int RegularHostMinJobs { get; set; } = 3;

    public const int MaxBackupSuppliersCap = 5;

    /// <summary>
    /// SP-BKP: minutes the supplier the host chose (the primary) has to take a job that was also sent to backups (60): its
    /// request gets <c>ResponseDueAt = creation + this</c> instead of <see cref="HostResponseMinutes"/>. After it the work goes to
    /// the backup that accepted first. Env var <c>Suppliers__ServiceRequests__PrimaryResponseMinutes</c>.
    /// </summary>
    public int PrimaryResponseMinutes { get; set; } = 60;

    public int BackupResponseMinutes { get; set; } = 60;

    public int MaxBackupSuppliers { get; set; } = 3;

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

public sealed class ServiceRequestOptionsValidator : IValidateOptions<ServiceRequestOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceRequestOptions options)
    {
        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
