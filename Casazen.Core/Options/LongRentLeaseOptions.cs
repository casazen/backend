using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

public class LongRentLeaseOptions
{
    public const string SectionName = "LongRentLease";

    public const int DefaultDraftRetentionDays = 180;

    /// <summary>
    /// Days a draft lease can stay without a change before it is deleted with its parties and files (decision LR-D33): a draft
    /// holds the personal data of the parties and is not kept for ever. A draft with a signed copy is never deleted by this.
    /// Default 180.
    /// </summary>
    public int DraftRetentionDays { get; set; } = DefaultDraftRetentionDays;

    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        if (DraftRetentionDays is < 1 or > 3650)
            failures.Add("LongRentLease__DraftRetentionDays must be between 1 and 3650.");
        return failures;
    }
}

public sealed class LongRentLeaseOptionsValidator : IValidateOptions<LongRentLeaseOptions>
{
    public ValidateOptionsResult Validate(string? name, LongRentLeaseOptions options)
    {
        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
