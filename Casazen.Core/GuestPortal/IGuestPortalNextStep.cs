namespace Casazen.Core.GuestPortal;

[GuestPortalRegistry]
public interface IGuestPortalNextStep
{
    int Priority { get; }

    Task<GuestPortalStep?> GetAsync(PortalContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// The next step of the guest: <paramref name="Key"/> is a message key owned by the section that proposes it (<c>checkin.start</c>), never a text;
/// <paramref name="Section"/> is the key of the section it opens (<see cref="GuestPortalSectionKeys"/>), <paramref name="Panel"/> a panel of it, and
/// <paramref name="Params"/> the values the message names (no personal data).
/// </summary>
public sealed record GuestPortalStep(string Key, string Section, string? Panel = null, IReadOnlyDictionary<string, string>? Params = null);
