using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>HostInvites</c>: the limits of the invitation of new independent hosts. Platform admins have no cap. Env vars
/// <c>HostInvites__DailyQuotaPerHost</c>, <c>HostInvites__MaxPendingPerHost</c>, <c>HostInvites__RetentionDays</c>.
/// </summary>
public class HostInviteOptions
{
    public const string SectionName = "HostInvites";

    public const int DefaultDailyQuotaPerHost = 10;
    public const int DefaultMaxPendingPerHost = 50;
    public const int DefaultRetentionDays = 30;

    /// <summary>Invites a host org can create or send again in 24 hours (rolling). Default 10.</summary>
    public int DailyQuotaPerHost { get; set; } = DefaultDailyQuotaPerHost;

    /// <summary>Pending invites a host org can hold at once. Default 50.</summary>
    public int MaxPendingPerHost { get; set; } = DefaultMaxPendingPerHost;

    /// <summary>Days after which the email of a closed invite is erased (GDPR minimization). Default 30.</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>Configuration errors, empty when the section is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        if (DailyQuotaPerHost is < 1 or > 1000)
            failures.Add("HostInvites__DailyQuotaPerHost must be between 1 and 1000.");
        if (MaxPendingPerHost is < 1 or > 10000)
            failures.Add("HostInvites__MaxPendingPerHost must be between 1 and 10000.");
        if (RetentionDays is < 1 or > 3650)
            failures.Add("HostInvites__RetentionDays must be between 1 and 3650.");
        return failures;
    }
}

/// <summary>Refuses a <c>HostInvites</c> section that is not valid at startup, so a typo never lifts a limit silently.</summary>
public sealed class HostInviteOptionsValidator : IValidateOptions<HostInviteOptions>
{
    public ValidateOptionsResult Validate(string? name, HostInviteOptions options)
    {
        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
