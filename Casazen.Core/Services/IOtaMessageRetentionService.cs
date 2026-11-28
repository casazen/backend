namespace Casazen.Core.Services;

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

public sealed record OtaMessageRetentionResult(bool RetentionConfigured, int Threads, int Messages);
