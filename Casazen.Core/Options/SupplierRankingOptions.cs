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

    // ─── The score of before ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The score of a supplier with nothing against it and nothing for it (70).</summary>
    public int BaseScore { get; set; } = 70;

    /// <summary>Points lost for each request the supplier holds or may hold (8), up to <see cref="LoadPenaltyCap"/>.</summary>
    public int LoadPenaltyPerRequest { get; set; } = 8;

    /// <summary>The most the open requests take away (40).</summary>
    public int LoadPenaltyCap { get; set; } = 40;

    /// <summary>Points added for an emergency (15) and for a high urgency (8): they favour who can come at once.</summary>
    public int EmergencyBonus { get; set; } = 15;

    /// <inheritdoc cref="EmergencyBonus"/>
    public int HighUrgencyBonus { get; set; } = 8;

    /// <summary>Points added to a supplier that wrote a bio (5).</summary>
    public int BioBonus { get; set; } = 5;

    // ─── Reliability ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the reliability counts in the score (true). False is the ranking of before, whatever the weights below say.</summary>
    public bool ReliabilityEnabled { get; set; } = true;

    /// <summary>How many days back the events count (90). A supplier with no event in that time starts neutral.</summary>
    public int WindowDays { get; set; } = 90;

    /// <summary>Points for each request answered in time (1), up to <see cref="RepliedInTimeCap"/> (10).</summary>
    public int RepliedInTimePoints { get; set; } = 1;

    /// <inheritdoc cref="RepliedInTimePoints"/>
    public int RepliedInTimeCap { get; set; } = 10;

    /// <summary>Points for each job completed without a dispute (2), up to <see cref="CompletedJobCap"/> (20).</summary>
    public int CompletedJobPoints { get; set; } = 2;

    /// <inheritdoc cref="CompletedJobPoints"/>
    public int CompletedJobCap { get; set; } = 20;

    /// <summary>Points lost when the supplier the host chose does not answer in time (10).</summary>
    public int NoReplyPenalty { get; set; } = 10;

    /// <summary>
    /// The share of <see cref="NoReplyPenalty"/> that a backup loses for not answering (0.2, that is 2 points): a backup was not the
    /// supplier the host asked for, and it is the small price of being asked as one among several.
    /// </summary>
    public double BackupPenaltyShare { get; set; } = 0.2;

    /// <summary>Points lost for each job the payer disputed (12), besides the points of that job.</summary>
    public int DisputePenalty { get; set; } = 12;

    /// <summary>
    /// The most the penalties take away in all (40): a supplier is never reduced to nothing by a bad month, and the score stays between 1
    /// and 100 in any case.
    /// </summary>
    public int MaxTotalPenalty { get; set; } = 40;

    /// <summary>Configuration errors, empty when the section is valid.</summary>
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

/// <summary>Refuses a <c>SupplierRanking</c> section that is not valid at startup, so a typo never changes who is recommended silently.</summary>
public sealed class SupplierRankingOptionsValidator : IValidateOptions<SupplierRankingOptions>
{
    public ValidateOptionsResult Validate(string? name, SupplierRankingOptions options)
    {
        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
