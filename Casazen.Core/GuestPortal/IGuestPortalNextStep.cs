namespace Casazen.Core.GuestPortal;

/// <summary>
/// A proposer of the next step of the guest (E50/F01/US01/T02, NF-153, §5.2 of the specification): the one block in evidence at the top of the portal
/// (do the check-in, pay the tourist tax, read how to arrive, the guide and the chat during the stay, the review after it). Every registered proposer
/// is asked in <see cref="Priority"/> order, lowest first, and the first that proposes a step wins. Each epic adds its own class; none edits a common
/// file. A proposer that throws is logged and skipped.
/// </summary>
[GuestPortalRegistry]
public interface IGuestPortalNextStep
{
    /// <summary>Lowest wins: a step that must come first has a lower number (the welcome of the stay, the fallback, is 1000).</summary>
    int Priority { get; }

    /// <summary>The step this proposer has for the stay of <paramref name="context"/>, or <c>null</c> when it has none now.</summary>
    Task<GuestPortalStep?> GetAsync(PortalContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// The next step of the guest: <paramref name="Key"/> is a message key owned by the section that proposes it (<c>checkin.start</c>), never a text;
/// <paramref name="Section"/> is the key of the section it opens (<see cref="GuestPortalSectionKeys"/>), <paramref name="Panel"/> a panel of it, and
/// <paramref name="Params"/> the values the message names (no personal data).
/// </summary>
public sealed record GuestPortalStep(string Key, string Section, string? Panel = null, IReadOnlyDictionary<string, string>? Params = null);
