using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>The progress of a bulk run as the job keeps it (<see cref="OwnerBulkCloseJob.ProgressParameter"/>): where it is and the results so far.</summary>
public sealed record OwnerBulkProgressState(OwnerBulkRunStatus Status, IReadOnlyList<OwnerBulkOwnerResult> Results, string? ErrorCode = null);

/// <summary>The JSON of <see cref="OwnerBulkProgressState"/>: member names for the enums, as the API writes them, and never an exception for a record that cannot be read.</summary>
public static class OwnerBulkJobState
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(OwnerBulkProgressState state) => JsonSerializer.Serialize(state, Options);

    /// <summary>The state a job kept; <c>null</c> when there is none or it cannot be read.</summary>
    public static OwnerBulkProgressState? Deserialize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JsonSerializer.Deserialize<OwnerBulkProgressState>(text, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <inheritdoc cref="IOwnerBulkJobs"/>
/// <remarks>
/// A run is a Hangfire job of <see cref="OwnerBulkCloseJob"/>. Who it is for and what it is (the org, the month, the step, the owners) is read from the arguments the job was queued
/// with, never from what the caller says; the progress is the job parameter the run writes. A job of another type, of another org, one that cannot be read or one that Hangfire
/// has already expired is the same «not found» (<c>null</c>), so the id of a run tells nothing about the runs of other agencies.
/// </remarks>
public sealed class HangfireOwnerBulkJobs(IBackgroundJobClient client, JobStorage? storage = null) : IOwnerBulkJobs
{
    // The arguments of OwnerBulkCloseJob.RunAsync, in order.
    private const int OrgArg = 0;
    private const int YearArg = 1;
    private const int MonthArg = 2;
    private const int ActionArg = 3;
    private const int OwnersArg = 4;
    private const int ArgCount = 5;

    public string Enqueue(Guid orgId, int year, int month, OwnerBulkAction action, IReadOnlyList<Guid> ownerIds, string? userId)
    {
        ArgumentNullException.ThrowIfNull(ownerIds);
        var owners = ownerIds.ToArray();
        return client.Enqueue<OwnerBulkCloseJob>(job => job.RunAsync(orgId, year, month, action, owners, userId, null, CancellationToken.None));
    }

    public Task<OwnerBulkRun?> GetAsync(Guid orgId, string jobId, CancellationToken ct = default)
    {
        // Hangfire numbers its jobs: anything else is not one of them.
        if (string.IsNullOrEmpty(jobId) || !long.TryParse(jobId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return Task.FromResult<OwnerBulkRun?>(null);

        using var connection = (storage ?? JobStorage.Current).GetConnection();
        var data = connection.GetJobData(jobId);
        if (data?.Job is not { } job
            || job.Type != typeof(OwnerBulkCloseJob)
            || job.Args.Count < ArgCount
            || job.Args[OrgArg] is not Guid jobOrg
            || jobOrg != orgId
            || job.Args[YearArg] is not int year
            || job.Args[MonthArg] is not int month
            || job.Args[ActionArg] is not OwnerBulkAction action
            || job.Args[OwnersArg] is not IEnumerable<Guid> owners)
        {
            return Task.FromResult<OwnerBulkRun?>(null);
        }

        var progress = OwnerBulkJobState.Deserialize(connection.GetJobParameter(jobId, OwnerBulkCloseJob.ProgressParameter));
        var results = progress?.Results ?? [];
        var fromState = StatusOf(data.State);

        // The run's own word counts once it is over (all its results are there); until then the job's state decides between waiting and working.
        var status = progress is { Status: OwnerBulkRunStatus.Completed or OwnerBulkRunStatus.Failed }
            ? progress.Status
            : fromState is OwnerBulkRunStatus.Completed or OwnerBulkRunStatus.Failed
                ? fromState
                : progress?.Status ?? fromState;
        var errorCode = status == OwnerBulkRunStatus.Failed ? progress?.ErrorCode ?? OwnerAccountingErrors.BulkFailed : null;
        return Task.FromResult<OwnerBulkRun?>(new OwnerBulkRun(jobId, action, year, month, status, owners.Count(), results, errorCode));
    }

    /// <summary>The state of a Hangfire job as the status of a run.</summary>
    private static OwnerBulkRunStatus StatusOf(string? state) => state switch
    {
        "Processing" => OwnerBulkRunStatus.Running,
        "Succeeded" => OwnerBulkRunStatus.Completed,
        "Failed" or "Deleted" => OwnerBulkRunStatus.Failed,
        _ => OwnerBulkRunStatus.Queued,
    };
}
