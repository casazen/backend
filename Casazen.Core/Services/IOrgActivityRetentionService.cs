namespace Casazen.Core.Services;

public sealed record OrgActivityRetentionResult(bool Skipped, int Deleted, DateTime? Cutoff)
{
    public static OrgActivityRetentionResult SkippedRun { get; } = new(true, 0, null);
}

/// <summary>
/// The retention of the activity log (AM-02b, wave decision D17): the lines older than <c>OrgTeam:ActivityRetentionMonths</c>
/// (12 by default, <b>to be confirmed with the legal advisor</b>) are deleted, for every org. Idempotent: a second run right
/// after the first deletes nothing. One run at a time (a session advisory lock on top of Hangfire's own).
/// </summary>
public interface IOrgActivityRetentionService
{
    Task<OrgActivityRetentionResult> RunAsync(CancellationToken cancellationToken = default);
}
