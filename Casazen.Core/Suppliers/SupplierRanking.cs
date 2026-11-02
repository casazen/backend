using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;

namespace Casazen.Core.Suppliers;

/// <summary>An event of a supplier as the ranking needs it: what it was, which request it was about and whether the supplier was a backup.</summary>
public readonly record struct ReliabilityEventRecord(SupplierReliabilityEventKind Kind, Guid? RequestId, bool AsBackup);

/// <summary>What the events of a supplier in the window add up to, before the weights are applied.</summary>
/// <param name="RepliedInTime">Requests it answered before their deadline.</param>
/// <param name="CleanJobs">Jobs it completed that nobody disputed.</param>
/// <param name="MissedAsPrimary">Deadlines it missed as the supplier the host chose (or the only one asked).</param>
/// <param name="MissedAsBackup">Deadlines it missed as one of the backups.</param>
/// <param name="Disputes">Jobs a payer disputed.</param>
public sealed record ReliabilityTally(int RepliedInTime, int CleanJobs, int MissedAsPrimary, int MissedAsBackup, int Disputes)
{
    /// <summary>A supplier with no history: it starts neutral.</summary>
    public static ReliabilityTally None { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// The ranking of the suppliers for a request (SP-REL), as pure functions over the weights of <see cref="SupplierRankingOptions"/>: one
/// place that says who is recommended to a host, in which order the suppliers are listed when a host asks for a job, and which
/// suppliers are called as backups (SP-BKP). The score is the one the platform always had (70, minus 8 for each open request up to 40,
/// plus the urgency, plus 5 for a bio) plus the <b>reliability</b> of the supplier: points for what it answered on time and the jobs it
/// completed without a dispute, points lost for the deadlines it missed (the supplier chosen by the host loses the most, a backup a small
/// share) and for the jobs a payer disputed. Always between 1 and 100.
/// </summary>
public static class SupplierRanking
{
    /// <summary>The score of a supplier for a request: 1 to 100, the higher the better.</summary>
    /// <param name="openLoad">Requests the supplier holds or may hold now (a backup that only waits for its primary is not one).</param>
    /// <param name="reliabilityPoints">The result of <see cref="ReliabilityPoints"/> for the supplier; 0 when nothing is known.</param>
    public static int Score(SupplierRankingOptions options, int openLoad, ServiceRequestUrgency urgency, bool hasBio, int reliabilityPoints)
    {
        ArgumentNullException.ThrowIfNull(options);

        var score = options.BaseScore;
        score -= Math.Min(openLoad * options.LoadPenaltyPerRequest, options.LoadPenaltyCap);
        score += urgency switch
        {
            ServiceRequestUrgency.Emergency => options.EmergencyBonus,
            ServiceRequestUrgency.High => options.HighUrgencyBonus,
            _ => 0,
        };
        if (hasBio)
            score += options.BioBonus;

        return Math.Clamp(score + reliabilityPoints, 1, 100);
    }

    /// <summary>
    /// What the history of a supplier adds to its score (negative when it subtracts): the replies in time and the clean jobs, each up to its
    /// cap, minus the penalties up to <see cref="SupplierRankingOptions.MaxTotalPenalty"/>. Zero when the reliability is off.
    /// </summary>
    public static int ReliabilityPoints(SupplierRankingOptions options, ReliabilityTally tally)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tally);
        if (!options.ReliabilityEnabled)
            return 0;

        var bonus = Math.Min(tally.RepliedInTime * options.RepliedInTimePoints, options.RepliedInTimeCap)
                    + Math.Min(tally.CleanJobs * options.CompletedJobPoints, options.CompletedJobCap);
        var penalty = (tally.MissedAsPrimary * NoReplyPenaltyOf(options, asBackup: false))
                      + (tally.MissedAsBackup * NoReplyPenaltyOf(options, asBackup: true))
                      + (tally.Disputes * options.DisputePenalty);
        return bonus - Math.Min(penalty, options.MaxTotalPenalty);
    }

    /// <summary>The points a missed deadline costs: all of <see cref="SupplierRankingOptions.NoReplyPenalty"/> for the supplier the host chose, its small share for a backup.</summary>
    public static int NoReplyPenaltyOf(SupplierRankingOptions options, bool asBackup)
    {
        ArgumentNullException.ThrowIfNull(options);
        return asBackup
            ? (int)Math.Round(options.NoReplyPenalty * options.BackupPenaltyShare, MidpointRounding.AwayFromZero)
            : options.NoReplyPenalty;
    }

    /// <summary>
    /// Adds up the events of one supplier. A job that was disputed no longer counts as completed without a dispute: the dispute takes back
    /// the points of the job (and costs its own penalty).
    /// </summary>
    public static ReliabilityTally Tally(IEnumerable<ReliabilityEventRecord> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var all = events.ToList();
        var disputed = all
            .Where(e => e.Kind == SupplierReliabilityEventKind.DisputeOpened && e.RequestId is not null)
            .Select(e => e.RequestId!.Value)
            .ToHashSet();

        return new ReliabilityTally(
            all.Count(e => e.Kind == SupplierReliabilityEventKind.RepliedInTime),
            all.Count(e => e.Kind == SupplierReliabilityEventKind.JobCompletedClean && (e.RequestId is null || !disputed.Contains(e.RequestId.Value))),
            all.Count(e => e.Kind == SupplierReliabilityEventKind.NoReplyInTime && !e.AsBackup),
            all.Count(e => e.Kind == SupplierReliabilityEventKind.NoReplyInTime && e.AsBackup),
            all.Count(e => e.Kind == SupplierReliabilityEventKind.DisputeOpened));
    }
}
