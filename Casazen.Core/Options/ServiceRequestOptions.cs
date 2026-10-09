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
