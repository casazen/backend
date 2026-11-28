namespace Casazen.Core.Options;

public class RoutineOptions
{
    public const string SectionName = "Routine";

    public const int DefaultEvaluationIntervalMinutes = 5;

    public int EvaluationIntervalMinutes { get; set; } = DefaultEvaluationIntervalMinutes;

    public int EffectiveEvaluationIntervalMinutes => Math.Clamp(EvaluationIntervalMinutes, 1, 59);

    public string EmitterCron => $"*/{EffectiveEvaluationIntervalMinutes} * * * *";
}
