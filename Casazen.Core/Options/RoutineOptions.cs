namespace Casazen.Core.Options;

/// <summary>
/// The routine engine (ADR-022), section <c>Routine</c>: how often the time triggers of the stays ("2 days before check-in") are
/// written to the outbox by the <c>time-event-emitter</c> job (ADR-022-R8).
/// </summary>
public class RoutineOptions
{
    public const string SectionName = "Routine";

    /// <summary>Default of <see cref="EvaluationIntervalMinutes"/> (proposed default Q-A55).</summary>
    public const int DefaultEvaluationIntervalMinutes = 5;

    /// <summary>Minutes between two runs of the emitter, 1 to 59 (a minute step of a cron expression). Default 5.</summary>
    public int EvaluationIntervalMinutes { get; set; } = DefaultEvaluationIntervalMinutes;

    /// <summary><see cref="EvaluationIntervalMinutes"/> brought into 1..59: a value outside it cannot be a cron step.</summary>
    public int EffectiveEvaluationIntervalMinutes => Math.Clamp(EvaluationIntervalMinutes, 1, 59);

    /// <summary>The schedule of the emitter, every <see cref="EffectiveEvaluationIntervalMinutes"/> minutes (UTC).</summary>
    public string EmitterCron => $"*/{EffectiveEvaluationIntervalMinutes} * * * *";
}
