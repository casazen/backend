using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>SupplierRanking</c> (SP-REL, the weights SP-10c leaves to the product owner): how the suppliers are ranked for a request,
/// that is who is recommended to a host, in which order the suppliers are listed when a host asks for a job and which suppliers are
/// called as backups (SP-BKP). Every value has a default and is validated at startup. Runbook <c>docs/runbooks/suppliers.md</c>.
/// </summary>
/// <remarks>
/// <para>The first group of values is the score the platform always had (70, minus 8 for each request the supplier has open up to 40, plus
/// the urgency, plus 5 for a supplier that wrote a bio). The second group is the reliability: it adds up the events of the last
/// <see cref="WindowDays"/> days. <b>Zero them (or <see cref="ReliabilityEnabled"/> = false) and the ranking is the one of before</b>:
/// the events are still recorded, so the weights can be changed later and applied to what already happened.</para>
/// </remarks>
public class SupplierRankingOptions
{
    public const string SectionName = "SupplierRanking";

    public int BaseScore { get; set; } = 70;

    public int LoadPenaltyPerRequest { get; set; } = 8;

    public int LoadPenaltyCap { get; set; } = 40;

    public int EmergencyBonus { get; set; } = 15;

    public int HighUrgencyBonus { get; set; } = 8;

    public int BioBonus { get; set; } = 5;

    public bool ReliabilityEnabled { get; set; } = true;

    public int WindowDays { get; set; } = 90;

    public int RepliedInTimePoints { get; set; } = 1;

    public int RepliedInTimeCap { get; set; } = 10;

    public int CompletedJobPoints { get; set; } = 2;

    public int CompletedJobCap { get; set; } = 20;

    public int NoReplyPenalty { get; set; } = 10;

    public double BackupPenaltyShare { get; set; } = 0.2;

    public int DisputePenalty { get; set; } = 12;

    public int MaxTotalPenalty { get; set; } = 40;

    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        void Check(bool ok, string name, string rule)
        {
            if (!ok)
                failures.Add($"{SectionName}__{name} {rule}.");
        }

        Check(BaseScore is >= 1 and <= 100, nameof(BaseScore), "must be between 1 and 100");
        Check(LoadPenaltyPerRequest is >= 0 and <= 50, nameof(LoadPenaltyPerRequest), "must be between 0 and 50");
        Check(LoadPenaltyCap is >= 0 and <= 100, nameof(LoadPenaltyCap), "must be between 0 and 100");
        Check(EmergencyBonus is >= 0 and <= 50, nameof(EmergencyBonus), "must be between 0 and 50");
        Check(HighUrgencyBonus is >= 0 and <= 50, nameof(HighUrgencyBonus), "must be between 0 and 50");
        Check(BioBonus is >= 0 and <= 50, nameof(BioBonus), "must be between 0 and 50");
        Check(WindowDays is >= 1 and <= 365, nameof(WindowDays), "must be between 1 and 365 days");
        Check(RepliedInTimePoints is >= 0 and <= 20, nameof(RepliedInTimePoints), "must be between 0 and 20");
        Check(RepliedInTimeCap is >= 0 and <= 100, nameof(RepliedInTimeCap), "must be between 0 and 100");
        Check(CompletedJobPoints is >= 0 and <= 20, nameof(CompletedJobPoints), "must be between 0 and 20");
        Check(CompletedJobCap is >= 0 and <= 100, nameof(CompletedJobCap), "must be between 0 and 100");
        Check(NoReplyPenalty is >= 0 and <= 50, nameof(NoReplyPenalty), "must be between 0 and 50");
        Check(BackupPenaltyShare is >= 0 and <= 1, nameof(BackupPenaltyShare), "must be between 0 and 1");
        Check(DisputePenalty is >= 0 and <= 50, nameof(DisputePenalty), "must be between 0 and 50");
        Check(MaxTotalPenalty is >= 0 and <= 100, nameof(MaxTotalPenalty), "must be between 0 and 100");
        return failures;
    }
}

public sealed class SupplierRankingOptionsValidator : IValidateOptions<SupplierRankingOptions>
{
    public ValidateOptionsResult Validate(string? name, SupplierRankingOptions options)
    {
        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
