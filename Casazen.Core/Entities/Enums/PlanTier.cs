namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Subscription plan tier for an <see cref="Casazen.Core.Entities.Org"/>.
/// Append-only: do not reorder or insert before existing values (persisted as int).
/// </summary>
public enum PlanTier
{
    Starter,
    Pro,
    Scale
}
