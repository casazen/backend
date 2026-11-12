using Casazen.Core.Entities;
using Casazen.Core.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The compliance summary of the month that has just ended, sent to every active short-rent organization on the first of the month (E03/F02/US02/T02, NF-52,
/// CHK-12 point 6): «October: 42 stays, all compliant», or the list of what is missing. It decides nothing by itself: the obligations of the month and the state of
/// each are the ones of the coverage report of the proof archive (<see cref="IComplianceArchiveService.GetCoverageReportAsync"/>), which is the traffic light's
/// own (<see cref="IComplianceStatusService"/>, <c>ComplianceLightRules</c>), so <b>«all compliant» is said only when every applicable obligation resolved to an
/// archived proof</b>: an item held only by the channel manager and an item with no proof are never collapsed into compliant.
/// </summary>
/// <remarks>
/// <para><b>Once per organization and month.</b> Before the email is handed to the queue the row of <see cref="ComplianceMonthlySummaryLog"/>
/// (<c>OrgId</c>, <c>YearMonth</c>) is inserted inside the same transaction: the unique index makes a second run, a retry or a concurrent run send nothing more. A run
/// that could not queue a single email takes its row back, so a later run tries again.</para>
/// <para><b>Privacy.</b> The email holds counts, the names of the open actions and a link, never a guest name, a document, a contact or a booking code; the log row holds the
/// month, the outcome and how many addresses it went to.</para>
/// <para><b>Behind the module's flag.</b> With <c>Features:ComplianceTrafficLight</c> off the run does nothing and writes nothing: until the proofs can be archived every stay
/// would be listed as missing.</para>
/// </remarks>
public interface IComplianceMonthlySummaryService
{
    /// <summary>
    /// Sends the summary of the calendar month before <paramref name="runDate"/> (a Europe/Rome day, <c>RomeCalendar</c> on the injected clock) to every active
    /// organization that has a short-rent property and was not sent it yet. One organization that cannot be processed is logged and does not stop the others.
    /// </summary>
    Task<ComplianceMonthlySummaryRunResult> RunAsync(DateOnly runDate, CancellationToken ct);
}

/// <summary>Outcome of one run of <see cref="IComplianceMonthlySummaryService.RunAsync"/>.</summary>
/// <param name="Skipped">The module is off (<c>Features:ComplianceTrafficLight</c>): the run did nothing.</param>
/// <param name="Organizations">The active short-rent organizations looked at.</param>
/// <param name="Sent">The summaries handed to the email queue in this run (an organization that already had its row sends nothing).</param>
public sealed record ComplianceMonthlySummaryRunResult(bool Skipped, int Organizations, int Sent);

/// <summary>
/// One open item of the month: the action that fixes it, the id of its target (the stay or the property) and the stable reason code of the traffic light. No name
/// and no code of a booking: it is what the clients need to open the item, nothing the email shows.
/// </summary>
/// <param name="Action">The cockpit action that fixes it; <c>null</c> when the report names none.</param>
/// <param name="TargetId">The stay (Alloggiati) or the property (ISTAT, tourist tax) the item is about.</param>
/// <param name="Reason">A code of <see cref="ComplianceReasonCodes"/>.</param>
/// <param name="EvidenceClass"><see cref="ComplianceEvidenceClass.Missing"/> or <see cref="ComplianceEvidenceClass.ExternalVerificationRequired"/>.</param>
public sealed record ComplianceMonthItem(ComplianceCockpitAction? Action, Guid TargetId, string Reason, ComplianceEvidenceClass EvidenceClass);

/// <summary>
/// The compliance of one organization for one month, as the email tells it.
/// </summary>
/// <param name="YearMonth">The month, <c>yyyy-MM</c> (Europe/Rome).</param>
/// <param name="Stays">How many stays arrived in the month and owe a communication (a no-show owes none and is not counted).</param>
/// <param name="Counts">The obligations of the month by state: proof, outcome held only by the channel manager, missing, nothing owed.</param>
/// <param name="Complete">True only when every applicable obligation has an archived proof.</param>
/// <param name="OpenItems">What is not settled: the items without a proof and the ones held only by the channel manager, as action + id + reason.</param>
public sealed record ComplianceMonthSummary(
    string YearMonth,
    int Stays,
    ComplianceCoverageCounts Counts,
    bool Complete,
    IReadOnlyList<ComplianceMonthItem> OpenItems)
{
    /// <summary>The <see cref="ComplianceMonthlySummaryLog.Outcome"/> of this month.</summary>
    public string Outcome => Complete ? ComplianceMonthlySummaryLog.AllCompliant : ComplianceMonthlySummaryLog.ItemsMissing;

    /// <summary>How many obligations are not settled: the ones without a proof and the ones held only by the channel manager.</summary>
    public int NotSettled => Counts.Missing + Counts.ExternalVerificationRequired;

    /// <summary>
    /// The month of <paramref name="report"/> as the email tells it. The stays are the Alloggiati rows that owe a communication; the open items are the rows that
    /// are not an archived proof and not «nothing owed». Nothing is inferred: a row held only by the channel manager stays open, and <see cref="Complete"/> is the
    /// report's own (<see cref="ComplianceCoverageReport.Complete"/>).
    /// </summary>
    public static ComplianceMonthSummary From(string yearMonth, ComplianceCoverageReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yearMonth);
        ArgumentNullException.ThrowIfNull(report);

        var stays = report.Rows.Count(r => r.Kind == ComplianceItemKind.Alloggiati && r.EvidenceClass != ComplianceEvidenceClass.NotApplicable);
        var open = report.Rows
            .Where(r => r.EvidenceClass is ComplianceEvidenceClass.Missing or ComplianceEvidenceClass.ExternalVerificationRequired)
            .Select(r => new ComplianceMonthItem(r.Action, r.BookingId ?? r.PropertyId, r.Reason, r.EvidenceClass))
            .ToList();
        return new ComplianceMonthSummary(yearMonth, stays, report.Counts, report.Complete, open);
    }
}
