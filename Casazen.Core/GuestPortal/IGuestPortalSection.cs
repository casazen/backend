namespace Casazen.Core.GuestPortal;

[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class GuestPortalRegistryAttribute : Attribute
{
}

/// <summary>
/// A section of the guest portal (E50/F01/US01/T02, NF-153, decision A4): the overview lists every registered section that is offered for the stay,
/// with its state, in <see cref="Order"/>. Each epic adds its own class (stay, check-in, guide, services, chat, documents, review); none edits a common
/// file. A section that throws is logged with the link id and its key and left out of the overview, never a 500.
/// </summary>
[GuestPortalRegistry]
public interface IGuestPortalSection
{
    string Key { get; }

    int Order { get; }

    Task<GuestPortalSectionStatus?> GetStateAsync(PortalContext context, CancellationToken cancellationToken = default);
}

public static class GuestPortalSectionKeys
{
    public const string Stay = "stay";
    public const string CheckIn = "checkin";
    public const string Guide = "guide";
    public const string Services = "services";
    public const string Chat = "chat";
    public const string Documents = "documents";
    public const string Review = "review";

    public static IReadOnlyList<string> All { get; } = [Stay, CheckIn, Guide, Services, Chat, Documents, Review];
}

public enum GuestPortalSectionState
{
    Available = 0,

    Locked = 1,

    Done = 2,

    Attention = 3,
}

public static class GuestPortalSectionStates
{
    public const string Available = "available";
    public const string Locked = "locked";
    public const string Done = "done";
    public const string Attention = "attention";

    public static string WireName(GuestPortalSectionState state) => state switch
    {
        GuestPortalSectionState.Available => Available,
        GuestPortalSectionState.Locked => Locked,
        GuestPortalSectionState.Done => Done,
        GuestPortalSectionState.Attention => Attention,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown state of a guest portal section"),
    };
}

public sealed record GuestPortalSectionStatus(GuestPortalSectionState State, int? Badge = null);
