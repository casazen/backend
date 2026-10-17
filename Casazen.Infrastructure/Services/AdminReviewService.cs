using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAdminReviewService"/>
/// <remarks>
/// The staff reads across orgs on purpose (<c>IgnoreQueryFilters</c>); the endpoints are <c>AdminOnly</c>. The decision is the only way a
/// review is ever taken down, and it is logged with the ids of the report, the review and the staff user, never with the text.
/// </remarks>
public sealed class AdminReviewService(
    AppDbContext db,
    ILogger<AdminReviewService> logger,
    TimeProvider? timeProvider = null) : IAdminReviewService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<AdminReviewReportPage> ListReportsAsync(
        ReviewReportStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, ReviewRules.MaxPageSize);

        var query = db.ReviewReports.IgnoreQueryFilters().AsNoTracking();
        if (status is { } wanted)
            query = query.Where(r => r.Status == wanted);

        var total = await query.CountAsync(cancellationToken);
        var rows = await Rows(query
                .OrderBy(r => r.ReportedAt)
                .ThenBy(r => r.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize))
            .ToListAsync(cancellationToken);

        return new AdminReviewReportPage(rows.Select(ToReport).ToList(), total, page, pageSize);
    }

    public async Task<AdminReviewReport> DecideAsync(
        Guid reportId,
        ReviewReportDecision decision,
        string reason,
        string? adminUserId,
        CancellationToken cancellationToken = default)
    {
        var text = ReviewRules.NormalizeText(reason);
        if (text is null || text.Length > ReviewRules.DecisionReasonMaxLength)
            throw new DomainRuleException("review_decision_reason_invalid", "ReviewDecisionReasonInvalid");

        var report = await db.ReviewReports.IgnoreQueryFilters()
            .Include(r => r.Review)
            .FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken)
            ?? throw new NotFoundException($"Review report {reportId} not found");
        if (report.Status != ReviewReportStatus.Open)
            throw new DomainConflictException("review_report_already_decided", "ReviewReportAlreadyDecided");

        var now = _clock.GetUtcNow().UtcDateTime;
        report.DecidedAt = now;
        report.DecidedByUserId = adminUserId;
        report.DecisionReason = text;
        if (decision == ReviewReportDecision.Remove)
        {
            report.Status = ReviewReportStatus.Removed;
            report.Review.Status = ReviewStatus.Removed;
            report.Review.RemovedAt = now;
            report.Review.RemovedReason = text;
        }
        else
        {
            report.Status = ReviewReportStatus.Kept;
            report.Review.Status = ReviewStatus.Published;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another staff user decided the same report at the same moment.
            db.ChangeTracker.Clear();
            throw new DomainConflictException("review_report_already_decided", "ReviewReportAlreadyDecided");
        }

        logger.LogInformation(
            "Review report {ReportId} of review {ReviewId} decided: {Decision} by {AdminUserId}",
            reportId, report.ReviewId, decision, adminUserId);

        var row = await Rows(db.ReviewReports.IgnoreQueryFilters().AsNoTracking().Where(r => r.Id == reportId)).FirstAsync(cancellationToken);
        return ToReport(row);
    }

    private static IQueryable<ReportRow> Rows(IQueryable<ReviewReport> reports) =>
        reports.Select(r => new ReportRow(
            r.Id,
            r.ReviewId,
            r.OrgId,
            r.Review.Property.Org.DisplayName,
            r.Review.Property.Name,
            r.Reason,
            r.Note,
            r.ReportedAt,
            r.Status,
            r.DecidedAt,
            r.DecisionReason,
            r.Review.Rating,
            r.Review.Text,
            r.Review.AuthorName,
            r.Review.StayEndedOn,
            r.Review.PublishedAt,
            r.Review.HostReply,
            r.Review.Status));

    private static AdminReviewReport ToReport(ReportRow r) => new(
        r.Id, r.ReviewId, r.OrgId, r.OrgName, r.PropertyName, r.Reason, r.Note, r.ReportedAt, r.Status, r.DecidedAt, r.DecisionReason,
        r.Rating, r.ReviewText, r.AuthorName, ReviewRules.StayMonthOf(r.StayEndedOn), r.PublishedAt, r.HostReply, r.ReviewStatus);

    private sealed record ReportRow(
        Guid Id,
        Guid ReviewId,
        Guid OrgId,
        string OrgName,
        string PropertyName,
        ReviewReportReason Reason,
        string? Note,
        DateTime ReportedAt,
        ReviewReportStatus Status,
        DateTime? DecidedAt,
        string? DecisionReason,
        int Rating,
        string? ReviewText,
        string AuthorName,
        DateTime StayEndedOn,
        DateTime PublishedAt,
        string? HostReply,
        ReviewStatus ReviewStatus);
}
