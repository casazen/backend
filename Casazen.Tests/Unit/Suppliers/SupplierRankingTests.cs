using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-REL: the ranking of the suppliers as pure functions over the weights of <c>SupplierRanking:*</c>. One proof for each rule the
/// product owner gave: a supplier with no history keeps the score it always had, replies in time and jobs without disputes raise it,
/// missed deadlines and disputes lower it, the supplier the host chose loses more than a backup, and nothing takes a score out of 1..100.
/// </summary>
public class SupplierRankingTests
{
    private static readonly SupplierRankingOptions Defaults = new();

    private static readonly Guid Job1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Job2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    /// <summary>The score of before SP-REL, written out: 70, minus 8 per open request up to 40, plus the urgency, plus 5 for a bio.</summary>
    private static int ScoreBeforeSpRel(int load, ServiceRequestUrgency urgency, bool hasBio)
    {
        var score = 70 - Math.Min(load * 8, 40);
        score += urgency switch
        {
            ServiceRequestUrgency.Emergency => 15,
            ServiceRequestUrgency.High => 8,
            _ => 0,
        };
        if (hasBio)
            score += 5;
        return Math.Clamp(score, 1, 100);
    }

    private static int Points(params ReliabilityEventRecord[] events) =>
        SupplierRanking.ReliabilityPoints(Defaults, SupplierRanking.Tally(events));

    private static ReliabilityEventRecord Replied(Guid? request = null) => new(SupplierReliabilityEventKind.RepliedInTime, request ?? Guid.NewGuid(), false);

    private static ReliabilityEventRecord Clean(Guid? request = null) => new(SupplierReliabilityEventKind.JobCompletedClean, request ?? Guid.NewGuid(), false);

    private static ReliabilityEventRecord Missed(bool asBackup = false) => new(SupplierReliabilityEventKind.NoReplyInTime, Guid.NewGuid(), asBackup);

    private static ReliabilityEventRecord Dispute(Guid request) => new(SupplierReliabilityEventKind.DisputeOpened, request, false);

    [Fact]
    public void Score_WithNoHistory_IsExactlyTheScoreOfBefore()
    {
        foreach (var load in Enumerable.Range(0, 9))
        {
            foreach (var urgency in Enum.GetValues<ServiceRequestUrgency>())
            {
                foreach (var hasBio in new[] { false, true })
                {
                    Assert.Equal(
                        ScoreBeforeSpRel(load, urgency, hasBio),
                        SupplierRanking.Score(Defaults, load, urgency, hasBio, Points()));
                }
            }
        }
    }

    [Fact]
    public void Score_RepliesInTimeAndCleanJobs_RaiseIt_OneAndTwoPointsEach()
    {
        var plain = SupplierRanking.Score(Defaults, 0, ServiceRequestUrgency.Normal, false, Points());
        var reliable = SupplierRanking.Score(Defaults, 0, ServiceRequestUrgency.Normal, false, Points(Replied(), Replied(), Replied(), Clean(), Clean()));

        Assert.Equal(70, plain);
        Assert.Equal(70 + 3 + 4, reliable);
    }

    [Fact]
    public void Points_TheBonusesStopAtTheirCaps()
    {
        var manyReplies = Enumerable.Range(0, 50).Select(_ => Replied()).ToArray();
        var manyJobs = Enumerable.Range(0, 50).Select(_ => Clean()).ToArray();

        Assert.Equal(10, Points(manyReplies));
        Assert.Equal(20, Points(manyJobs));
        Assert.Equal(30, Points([.. manyReplies, .. manyJobs]));
    }

    [Fact]
    public void Score_AMissedDeadline_LowersIt_TenPointsForThePrimary()
    {
        var score = SupplierRanking.Score(Defaults, 0, ServiceRequestUrgency.Normal, false, Points(Missed()));

        Assert.Equal(60, score);
    }

    [Fact]
    public void NoReplyPenalty_ThePrimaryLosesMuchMoreThanABackup()
    {
        var primary = SupplierRanking.NoReplyPenaltyOf(Defaults, asBackup: false);
        var backup = SupplierRanking.NoReplyPenaltyOf(Defaults, asBackup: true);

        Assert.Equal(10, primary);
        Assert.Equal(2, backup);
        Assert.True(primary > backup);
        Assert.Equal(-10, Points(Missed()));
        Assert.Equal(-2, Points(Missed(asBackup: true)));
    }

    [Fact]
    public void Dispute_CostsMoreThanAMissedDeadline_AndTakesBackThePointsOfThatJob()
    {
        // A clean job earns 2; a dispute on it takes them back and costs 12 on top of that.
        Assert.Equal(2, Points(Clean(Job1)));
        Assert.Equal(-12, Points(Clean(Job1), Dispute(Job1)));
        // Another job without a dispute is not touched by it.
        Assert.Equal(-10, Points(Clean(Job1), Clean(Job2), Dispute(Job1)));
        Assert.True(Math.Abs(Points(Dispute(Job1))) > Math.Abs(Points(Missed())));
    }

    [Fact]
    public void Points_ThePenaltiesStopAtTheTotalCap_SoOneBadMonthNeverZeroesASupplier()
    {
        var misses = Enumerable.Range(0, 10).Select(_ => Missed()).ToArray();

        Assert.Equal(-40, Points(misses));
        Assert.Equal(-40, Points([.. misses, Dispute(Job1), Dispute(Job2)]));
        // Even with the worst history and the worst load the score is still a valid one.
        Assert.Equal(1, SupplierRanking.Score(Defaults, 8, ServiceRequestUrgency.Normal, false, Points(misses)));
    }

    [Fact]
    public void Score_NeverLeavesTheRangeOneToOneHundred()
    {
        var best = SupplierRanking.Score(
            Defaults, 0, ServiceRequestUrgency.Emergency, true, Points([.. Enumerable.Range(0, 50).Select(_ => Replied()), .. Enumerable.Range(0, 50).Select(_ => Clean())]));
        var worst = SupplierRanking.Score(Defaults, 100, ServiceRequestUrgency.Normal, false, -1000);

        Assert.Equal(100, best);
        Assert.Equal(1, worst);
    }

    [Fact]
    public void ReliabilityOff_IsTheRankingOfBefore_WhateverTheHistory()
    {
        var off = new SupplierRankingOptions { ReliabilityEnabled = false };
        var tally = SupplierRanking.Tally([Replied(), Clean(), Missed(), Dispute(Job1)]);

        Assert.Equal(0, SupplierRanking.ReliabilityPoints(off, tally));
    }

    [Fact]
    public void Weights_AreConfiguration_AndZeroingThemRestoresTheScoreOfBefore()
    {
        var harsher = new SupplierRankingOptions { NoReplyPenalty = 20, BackupPenaltyShare = 0.5 };
        var zero = new SupplierRankingOptions
        {
            RepliedInTimePoints = 0,
            CompletedJobPoints = 0,
            NoReplyPenalty = 0,
            DisputePenalty = 0,
        };
        var history = SupplierRanking.Tally([Replied(), Clean(Job1), Missed(), Missed(asBackup: true), Dispute(Job2)]);

        Assert.Equal(20, SupplierRanking.NoReplyPenaltyOf(harsher, asBackup: false));
        Assert.Equal(10, SupplierRanking.NoReplyPenaltyOf(harsher, asBackup: true));
        Assert.Equal(0, SupplierRanking.ReliabilityPoints(zero, history));
    }

    [Fact]
    public void Tally_CountsEachKindAndWhoMissedTheDeadline()
    {
        var tally = SupplierRanking.Tally([Replied(), Replied(), Clean(Job1), Missed(), Missed(asBackup: true), Missed(asBackup: true), Dispute(Job2)]);

        Assert.Equal(new ReliabilityTally(2, 1, 1, 2, 1), tally);
        Assert.Equal(ReliabilityTally.None, SupplierRanking.Tally([]));
    }

    // ─── The options ───

    [Fact]
    public void Options_TheDefaultsAreValid_AndAreTheOnesOfTheBrief()
    {
        Assert.Empty(new SupplierRankingOptions().Validate());
        Assert.Equal(
            (90, 1, 10, 2, 20, 10, 0.2, 12, 40),
            (Defaults.WindowDays, Defaults.RepliedInTimePoints, Defaults.RepliedInTimeCap, Defaults.CompletedJobPoints, Defaults.CompletedJobCap,
                Defaults.NoReplyPenalty, Defaults.BackupPenaltyShare, Defaults.DisputePenalty, Defaults.MaxTotalPenalty));
        Assert.Equal((70, 8, 40, 15, 8, 5), (Defaults.BaseScore, Defaults.LoadPenaltyPerRequest, Defaults.LoadPenaltyCap, Defaults.EmergencyBonus, Defaults.HighUrgencyBonus, Defaults.BioBonus));
    }

    [Theory]
    [InlineData(nameof(SupplierRankingOptions.WindowDays), 0)]
    [InlineData(nameof(SupplierRankingOptions.WindowDays), 400)]
    [InlineData(nameof(SupplierRankingOptions.NoReplyPenalty), -1)]
    [InlineData(nameof(SupplierRankingOptions.DisputePenalty), 51)]
    [InlineData(nameof(SupplierRankingOptions.RepliedInTimeCap), 101)]
    [InlineData(nameof(SupplierRankingOptions.MaxTotalPenalty), -5)]
    public void Options_AValueOutOfItsRange_IsRefusedAtStartup(string property, int value)
    {
        var options = new SupplierRankingOptions();
        typeof(SupplierRankingOptions).GetProperty(property)!.SetValue(options, value);

        var failure = Assert.Single(options.Validate());
        Assert.Contains($"SupplierRanking__{property}", failure);
        var result = new SupplierRankingOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Options_TheBackupShareIsAFractionAndAValidSectionPasses()
    {
        var tooBig = new SupplierRankingOptions { BackupPenaltyShare = 1.5 };

        Assert.Contains("BackupPenaltyShare", Assert.Single(tooBig.Validate()));
        Assert.Equal(ValidateOptionsResult.Success, new SupplierRankingOptionsValidator().Validate(null, new SupplierRankingOptions()));
    }
}
