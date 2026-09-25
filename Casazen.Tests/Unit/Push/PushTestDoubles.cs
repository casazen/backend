using System.Collections.Concurrent;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Push;

/// <summary>Mock of the Expo push API: records every request and answers as configured (default: every ticket ok).</summary>
public sealed class FakeExpoPushClient : IExpoPushClient
{
    private readonly ConcurrentQueue<IReadOnlyList<ExpoPushMessage>> _sendRequests = new();
    private readonly ConcurrentQueue<IReadOnlyList<string>> _receiptRequests = new();
    private readonly ConcurrentQueue<Func<IReadOnlyList<ExpoPushMessage>, ExpoSendResult>> _nextSendAnswers = new();
    private int _ticketNumber;

    /// <summary>Send requests received, in order: one list of messages per HTTP call.</summary>
    public IReadOnlyList<IReadOnlyList<ExpoPushMessage>> SendRequests => _sendRequests.ToList();

    /// <summary>Every message received, all calls together.</summary>
    public IReadOnlyList<ExpoPushMessage> Messages => _sendRequests.SelectMany(r => r).ToList();

    public IReadOnlyList<IReadOnlyList<string>> ReceiptRequests => _receiptRequests.ToList();

    /// <summary>Tokens whose ticket is an error with this code (e.g. <c>DeviceNotRegistered</c>).</summary>
    public Dictionary<string, string> TicketErrors { get; } = new(StringComparer.Ordinal);

    /// <summary>Receipts answered by <see cref="GetReceiptsAsync"/>; a ticket id missing here has no receipt yet.</summary>
    public Dictionary<string, ExpoPushReceipt> Receipts { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, <see cref="GetReceiptsAsync"/> fails with this error.</summary>
    public string? ReceiptsError { get; set; }

    /// <summary>When set, a send waits for it: a call made inside an HTTP request would block that request.</summary>
    public Task? SendGate { get; set; }

    /// <summary>Answers the next send request with <paramref name="answer"/> instead of ok tickets.</summary>
    public void AnswerNextSend(Func<IReadOnlyList<ExpoPushMessage>, ExpoSendResult> answer) => _nextSendAnswers.Enqueue(answer);

    /// <summary>Answers the next send request with a whole-request failure.</summary>
    public void FailNextSend(ExpoSendOutcome outcome, string error = "Http503") =>
        AnswerNextSend(_ => ExpoSendResult.Failure(outcome, error));

    public async Task<ExpoSendResult> SendAsync(IReadOnlyList<ExpoPushMessage> messages, CancellationToken cancellationToken = default)
    {
        _sendRequests.Enqueue(messages.ToList());
        if (SendGate is not null)
            await SendGate.WaitAsync(cancellationToken);

        if (_nextSendAnswers.TryDequeue(out var answer))
            return answer(messages);

        return new ExpoSendResult(
            ExpoSendOutcome.Accepted,
            messages
                .Select(m => TicketErrors.TryGetValue(m.To, out var error)
                    ? new ExpoPushTicket(false, null, error)
                    : new ExpoPushTicket(true, $"ticket-{Interlocked.Increment(ref _ticketNumber)}", null))
                .ToList());
    }

    public Task<ExpoReceiptsResult> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken = default)
    {
        _receiptRequests.Enqueue(ticketIds.ToList());
        if (ReceiptsError is not null)
            return Task.FromResult(new ExpoReceiptsResult(new Dictionary<string, ExpoPushReceipt>(), ReceiptsError));

        var receipts = ticketIds
            .Where(Receipts.ContainsKey)
            .ToDictionary(id => id, id => Receipts[id], StringComparer.Ordinal);
        return Task.FromResult(new ExpoReceiptsResult(receipts));
    }
}

/// <summary>Records the pushes queued by the services (key, audience, payload).</summary>
public sealed class RecordingPushQueue : IPushNotificationService
{
    private readonly ConcurrentQueue<QueuedPushRecord> _queued = new();

    public IReadOnlyList<QueuedPushRecord> Queued => _queued.ToList();

    public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
    {
        _queued.Enqueue(new QueuedPushRecord(deliveryKey, audience, payload));
        return true;
    }
}

public sealed record QueuedPushRecord(string DeliveryKey, PushAudience Audience, PushNotificationPayload Payload);

/// <summary>
/// The real push pipeline without a Hangfire server: <see cref="HangfirePushQueue"/> on a job client that keeps the jobs,
/// and <see cref="RunQueuedJobsAsync"/> that runs them with <see cref="PushDeliveryJob"/> against <see cref="Expo"/>, as
/// the Hangfire worker would after the request.
/// </summary>
public sealed class PushPipeline
{
    private readonly ConcurrentQueue<Job> _jobs = new();

    public PushPipeline(TimeProvider? timeProvider = null)
    {
        TimeProvider = timeProvider ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
        var client = new Mock<IBackgroundJobClient>();
        client
            .Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => _jobs.Enqueue(job))
            .Returns(() => $"job-{_jobs.Count}");
        Queue = new HangfirePushQueue(client.Object, NullLogger<HangfirePushQueue>.Instance);
    }

    public FakeExpoPushClient Expo { get; } = new();

    public HangfirePushQueue Queue { get; }

    public TimeProvider TimeProvider { get; }

    /// <summary>Jobs queued so far (not removed when run: a run again is a Hangfire retry).</summary>
    public IReadOnlyList<Job> Jobs => _jobs.ToList();

    public PushDeliveryJob CreateJob(AppDbContext db) =>
        new(db, Expo, TimeProvider, NullLogger<PushDeliveryJob>.Instance);

    /// <summary>Runs every queued job once, in order.</summary>
    public async Task RunQueuedJobsAsync(AppDbContext db)
    {
        foreach (var job in Jobs)
            await RunAsync(db, job);
    }

    public Task RunAsync(AppDbContext db, Job job) =>
        CreateJob(db).SendAsync((string)job.Args[0], (QueuedPush)job.Args[1], CancellationToken.None);
}
