namespace Casazen.Core.Services;

/// <summary>
/// The nightly retention of the copy of the OTA conversations (E05/F01/US01/T03, ADR-032-R13), run by <c>GdprDataRetentionJob</c> after the
/// retention of the guest data: the copy follows the retention of the guest data (assumption of the brief, to be confirmed by the product owner:
/// no period of its own), so the words and the names of a conversation go when the data of its stay does.
/// </summary>
public interface IOtaMessageRetentionService
{
    /// <summary>
    /// Anonymizes the threads of the stays whose check-out is past the period of <c>Gdpr:Retention:AlloggiatiData</c> (a stay that is still open is
    /// left alone), and the threads with no booking whose last message is. Like every category, <b>without a period and its source nothing is
    /// anonymized by the retention</b> and a warning says so at every run. Idempotent: an anonymized thread is marked and not selected again. Whatever
    /// the period, a thread that is marked as anonymized but holds a name or a link of a person again (a read of the channel manager raced the erasure and
    /// stored a message after it) is emptied again, and counted.
    /// </summary>
    Task<OtaMessageRetentionResult> ApplyRetentionAsync(CancellationToken cancellationToken = default);
}

/// <summary>What one run of <see cref="IOtaMessageRetentionService.ApplyRetentionAsync"/> did.</summary>
/// <param name="RetentionConfigured">The period (with its source) is configured; false means the retention anonymized nothing.</param>
/// <param name="Threads">The threads anonymized in this run (the ones emptied again included).</param>
/// <param name="Messages">The messages that lost their words in this run.</param>
public sealed record OtaMessageRetentionResult(bool RetentionConfigured, int Threads, int Messages);
