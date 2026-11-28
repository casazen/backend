using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

public class HostInviteOptions
{
    public const string SectionName = "HostInvites";

    public const int DefaultDailyQuotaPerHost = 10;
    public const int DefaultMaxPendingPerHost = 50;
    public const int DefaultRetentionDays = 30;

    public int DailyQuotaPerHost { get; set; } = DefaultDailyQuotaPerHost;

    public int MaxPendingPerHost { get; set; } = DefaultMaxPendingPerHost;

    /// <summary>Days after which the email of a closed invite is erased (GDPR minimization). Default 30.</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

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

public sealed class HostInviteOptionsValidator : IValidateOptions<HostInviteOptions>
{
    public ValidateOptionsResult Validate(string? name, HostInviteOptions options)
    {
        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
