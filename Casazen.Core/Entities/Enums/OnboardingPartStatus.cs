namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the host chose to do with a part of the activation plan (<see cref="OnboardingPartKey"/>), the only part of its state that is stored.
/// Stored as its number; every member has an explicit value and an existing number never changes.
/// </summary>
public enum OnboardingPartStatus
{
    /// <summary>The host works on the part, or has not decided to leave it for later.</summary>
    Open = 1,

    /// <summary>The host chose «Do it later» (or left the part half-way): a "To do" item stands for it and the plan reminds the host.</summary>
    Deferred = 2,
}
