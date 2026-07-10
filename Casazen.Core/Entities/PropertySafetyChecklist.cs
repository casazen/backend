namespace Casazen.Core.Entities;

/// <summary>
/// D.L. 145/2023 safety checklist captured during property activation (AC6).
/// Stored as jsonb on <see cref="Property"/>.
/// </summary>
public class PropertySafetyChecklist
{
    public bool SmokeDetector { get; set; }
    public bool FireExtinguisher { get; set; }
    public bool GasCompliance { get; set; }
    public DateTime? AcknowledgedAt { get; set; }

    public bool IsComplete =>
        SmokeDetector && FireExtinguisher && GasCompliance && AcknowledgedAt.HasValue;
}
