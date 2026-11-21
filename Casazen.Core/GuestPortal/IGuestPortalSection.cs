namespace Casazen.Core.GuestPortal;

/// <summary>
/// Marks an interface whose implementations the guest portal finds by itself (decision A4): <c>AddGuestPortalRegistries</c> registers, scoped, every
/// concrete class of the scanned assemblies (Core, Infrastructure, Web) that implements an interface carrying this attribute. A later epic adds a class
/// (or a new registry interface with this attribute) and edits no common file.
/// </summary>
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
    /// <summary>One of <see cref="GuestPortalSectionKeys"/>; unique among the sections.</summary>
    string Key { get; }

    /// <summary>The place of the section in the portal, lowest first.</summary>
    int Order { get; }

    /// <summary>The state of the section for the stay of <paramref name="context"/>, or <c>null</c> when the section is not offered for it.</summary>
    Task<GuestPortalSectionStatus?> GetStateAsync(PortalContext context, CancellationToken cancellationToken = default);
}

/// <summary>The fixed keys of the sections of the guest portal (decision A1). Payments, change, cancellation and privacy are panels of <see cref="Stay"/>.</summary>
public static class GuestPortalSectionKeys
{
    public const string Stay = "stay";
    public const string CheckIn = "checkin";
    public const string Guide = "guide";
    public const string Services = "services";
    public const string Chat = "chat";
    public const string Documents = "documents";
    public const string Review = "review";

    /// <summary>Every key, in the order of the specification (§5.2). Keys are only ever added.</summary>
    public static IReadOnlyList<string> All { get; } = [Stay, CheckIn, Guide, Services, Chat, Documents, Review];
}

/// <summary>The state of a section of the guest portal. Its wire name is <see cref="GuestPortalSectionStates.WireName"/>.</summary>
public enum GuestPortalSectionState
{
    /// <summary>The guest can open it and there is something to do or read (<c>available</c>).</summary>
    Available = 0,

    /// <summary>Not yet: it opens later in the stay (<c>locked</c>).</summary>
    Locked = 1,

    /// <summary>What it asked is done (<c>done</c>).</summary>
    Done = 2,

    /// <summary>It needs the guest now (<c>attention</c>).</summary>
    Attention = 3,
}

/// <summary>The words of the states on the wire: <c>available</c>, <c>locked</c>, <c>done</c>, <c>attention</c>.</summary>
public static class GuestPortalSectionStates
{
    public const string Available = "available";
    public const string Locked = "locked";
    public const string Done = "done";
    public const string Attention = "attention";

    /// <summary>The word of <paramref name="state"/>.</summary>
    public static string WireName(GuestPortalSectionState state) => state switch
    {
        GuestPortalSectionState.Available => Available,
        GuestPortalSectionState.Locked => Locked,
        GuestPortalSectionState.Done => Done,
        GuestPortalSectionState.Attention => Attention,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown state of a guest portal section"),
    };
}

/// <summary>The state of an offered section and its badge (a count to show next to it, for example the unread messages), <c>null</c> for none.</summary>
public sealed record GuestPortalSectionStatus(GuestPortalSectionState State, int? Badge = null);
