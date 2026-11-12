using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F02/US02/T02 (NF-52, AC-NF52-2): the month as the summary tells it, taken from the coverage report and never made greener than it is. The stays are the
/// ones that owe a communication, the open items are what has no proof or is held only by the channel manager, and «complete» is the report's own word.
/// </summary>
public class ComplianceMonthSummaryTests
{
    private static ComplianceReportRow Row(
        ComplianceItemKind kind,
        ComplianceEvidenceClass evidence,
        ComplianceCockpitAction? action = null,
        string reason = "reason") =>
        new(
            kind,
            Guid.NewGuid(),
            "Casa",
            kind == ComplianceItemKind.Alloggiati ? Guid.NewGuid() : null,
            null,
            null,
            null,
            evidence,
            null,
            null,
            null,
            action,
            reason,
            null);

    private static ComplianceCoverageReport Report(params ComplianceReportRow[] rows) =>
        new(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31),
            null,
            DateTime.UtcNow,
            rows.All(r => r.EvidenceClass is ComplianceEvidenceClass.OfficialProof or ComplianceEvidenceClass.NotApplicable),
            new ComplianceCoverageCounts(
                rows.Count(r => r.EvidenceClass == ComplianceEvidenceClass.OfficialProof),
                rows.Count(r => r.EvidenceClass == ComplianceEvidenceClass.ExternalVerificationRequired),
                rows.Count(r => r.EvidenceClass == ComplianceEvidenceClass.Missing),
                rows.Count(r => r.EvidenceClass == ComplianceEvidenceClass.NotApplicable)),
            rows);

    [Fact]
    public void From_TheStaysAreTheAlloggiatiRowsThatOweACommunication_NotTheNoShowsNorThePeriods()
    {
        var summary = ComplianceMonthSummary.From(
            "2026-10",
            Report(
                Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.OfficialProof),
                Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.Missing),
                Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.ExternalVerificationRequired),
                Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.NotApplicable),
                Row(ComplianceItemKind.Istat, ComplianceEvidenceClass.OfficialProof),
                Row(ComplianceItemKind.TouristTax, ComplianceEvidenceClass.Missing)));

        Assert.Equal(3, summary.Stays);
        Assert.Equal("2026-10", summary.YearMonth);
        Assert.Equal(new ComplianceCoverageCounts(2, 1, 2, 1), summary.Counts);
        Assert.Equal(3, summary.NotSettled);
    }

    [Fact]
    public void From_TheOpenItemsAreWhatHasNoProofOrIsHeldOnlyByTheChannelManager_WithActionIdAndReason()
    {
        var missing = Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.Missing, ComplianceCockpitAction.SendAlloggiati, ComplianceReasonCodes.DeadlineOverdue);
        var held = Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.ExternalVerificationRequired, ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceReasonCodes.EntrustedToChannelManager);
        var period = Row(ComplianceItemKind.Istat, ComplianceEvidenceClass.Missing, ComplianceCockpitAction.UploadIstatProof, ComplianceReasonCodes.PeriodOpen);

        var summary = ComplianceMonthSummary.From(
            "2026-10",
            Report(
                missing,
                held,
                period,
                Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.OfficialProof),
                Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.NotApplicable)));

        Assert.Equal(
            [
                new ComplianceMonthItem(ComplianceCockpitAction.SendAlloggiati, missing.BookingId!.Value, ComplianceReasonCodes.DeadlineOverdue, ComplianceEvidenceClass.Missing),
                new ComplianceMonthItem(ComplianceCockpitAction.UploadAlloggiatiReceipt, held.BookingId!.Value, ComplianceReasonCodes.EntrustedToChannelManager, ComplianceEvidenceClass.ExternalVerificationRequired),
                new ComplianceMonthItem(ComplianceCockpitAction.UploadIstatProof, period.PropertyId, ComplianceReasonCodes.PeriodOpen, ComplianceEvidenceClass.Missing),
            ],
            summary.OpenItems.ToArray());
    }

    [Fact]
    public void From_AMonthWithOnlyProofsAndNoShows_IsCompleteAndAllCompliant()
    {
        var summary = ComplianceMonthSummary.From(
            "2026-10",
            Report(
                Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.OfficialProof),
                Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.NotApplicable)));

        Assert.True(summary.Complete);
        Assert.Empty(summary.OpenItems);
        Assert.Equal(ComplianceMonthlySummaryLog.AllCompliant, summary.Outcome);
    }

    [Fact]
    public void From_AnItemHeldOnlyByTheChannelManager_OrWithoutAProof_NeverMakesTheMonthComplete()
    {
        var held = ComplianceMonthSummary.From("2026-10", Report(Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.ExternalVerificationRequired)));
        var missing = ComplianceMonthSummary.From("2026-10", Report(Row(ComplianceItemKind.Istat, ComplianceEvidenceClass.Missing)));

        Assert.False(held.Complete);
        Assert.False(missing.Complete);
        Assert.Equal(ComplianceMonthlySummaryLog.ItemsMissing, held.Outcome);
        Assert.Equal(ComplianceMonthlySummaryLog.ItemsMissing, missing.Outcome);
    }

    [Fact]
    public void From_AMonthWithNoObligation_IsComplete_NothingIsOwed()
    {
        var summary = ComplianceMonthSummary.From("2026-10", Report());

        Assert.True(summary.Complete);
        Assert.Equal(0, summary.Stays);
        Assert.Empty(summary.OpenItems);
    }
}
