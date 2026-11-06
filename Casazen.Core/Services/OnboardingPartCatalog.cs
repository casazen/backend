using Casazen.Core.Entities.Enums;
using Casazen.Core.Validation;

namespace Casazen.Core.Services;

/// <summary>
/// The codes of «What you need» of the activation plan (NF-104, ONB-01): what the host should have at hand before a part. Stable codes the
/// client translates; never rename one.
/// </summary>
public static class OnboardingNeedCodes
{
    /// <summary>The credentials of the Alloggiati Web web service of the Questura.</summary>
    public const string AlloggiatiCredentials = "alloggiatiCredentials";

    /// <summary>The credentials of the regional ISTAT service.</summary>
    public const string IstatCredentials = "istatCredentials";

    /// <summary>The fiscal data of whoever issues the documents.</summary>
    public const string FiscalData = "fiscalData";

    /// <summary>The access to the accounts of the OTAs, to give the channel manager the access to each channel.</summary>
    public const string OtaAccess = "otaAccess";

    /// <summary>The code of the lock or of the key box of the property.</summary>
    public const string LockCode = "lockCode";

    /// <summary>The five codes, in the order the plan shows them.</summary>
    public static IReadOnlyList<string> All { get; } = [AlloggiatiCredentials, IstatCredentials, FiscalData, OtaAccess, LockCode];
}

/// <summary>The codes the web maps to the wizard that does a part (NF-104): never rename one.</summary>
public static class OnboardingLaunchTargets
{
    public const string Connect = "connect";
    public const string Mapping = "mapping";
    public const string Channels = "channels";
    public const string Listings = "listings";
    public const string Prices = "prices";
    public const string Compliance = "compliance";
    public const string Identification = "identification";
    public const string Messages = "messages";
    public const string Fiscal = "fiscal";
    public const string Marketplace = "marketplace";
    public const string Summary = "summary";
}

/// <summary>
/// One part of the activation plan as the code defines it (NF-104, ONB-01, E10/F01/US01/T03): its stable code, its place, the parts it needs
/// done first, what the host needs at hand and the wizard that does it.
/// </summary>
/// <param name="Key">The part.</param>
/// <param name="Code">The stable code the API speaks (the camel-case name of <paramref name="Key"/>).</param>
/// <param name="Order">The place of the part in the plan, from 1.</param>
/// <param name="Requires">The parts that must be done before this one can be started.</param>
/// <param name="Needs">The codes of <see cref="OnboardingNeedCodes"/> the part needs.</param>
/// <param name="LaunchTarget">One of <see cref="OnboardingLaunchTargets"/>.</param>
public sealed record OnboardingPartDefinition(
    OnboardingPartKey Key,
    string Code,
    int Order,
    IReadOnlyList<OnboardingPartKey> Requires,
    IReadOnlyList<string> Needs,
    string LaunchTarget);

/// <summary>One step of a connected path of UX-10 and the parts that make it.</summary>
/// <param name="Code">The stable code of the step.</param>
/// <param name="Order">The place of the step in the path, from 1.</param>
/// <param name="Parts">The parts the step is made of, in their order.</param>
public sealed record OnboardingPathStepDefinition(string Code, int Order, IReadOnlyList<OnboardingPartKey> Parts);

/// <summary>
/// The catalog of the eleven parts of the activation plan (NF-104, ONB-01, E10/F01/US01/T03). Code-defined, not configured (the open question
/// «code-defined or configured» of the epic is answered «code»): what a part requires, needs and launches is part of the product, and a change
/// of it is a change of the code. Whether a part is done is never here: it is derived on read from the stored facts
/// (<see cref="IOnboardingPartEvaluator"/>).
/// </summary>
public static class OnboardingPartCatalog
{
    /// <summary>The code of the connected path «start with CasaZen» of UX-10.</summary>
    public const string StartWithCasazenPath = "start-with-casazen";

    /// <summary>The eleven parts, in the order of the plan.</summary>
    public static IReadOnlyList<OnboardingPartDefinition> Parts { get; } =
    [
        new(OnboardingPartKey.OctorateConnection, "octorateConnection", 1, [], [], OnboardingLaunchTargets.Connect),
        new(OnboardingPartKey.Properties, "properties", 2, [OnboardingPartKey.OctorateConnection], [], OnboardingLaunchTargets.Mapping),
        new(OnboardingPartKey.Channels, "channels", 3, [OnboardingPartKey.Properties], [OnboardingNeedCodes.OtaAccess], OnboardingLaunchTargets.Channels),
        new(OnboardingPartKey.Listings, "listings", 4, [OnboardingPartKey.Channels], [], OnboardingLaunchTargets.Listings),
        new(OnboardingPartKey.PricesRules, "pricesRules", 5, [OnboardingPartKey.Properties], [], OnboardingLaunchTargets.Prices),
        new(
            OnboardingPartKey.Compliance,
            "compliance",
            6,
            [OnboardingPartKey.Properties],
            [OnboardingNeedCodes.AlloggiatiCredentials, OnboardingNeedCodes.IstatCredentials],
            OnboardingLaunchTargets.Compliance),
        new(OnboardingPartKey.Identification, "identification", 7, [], [OnboardingNeedCodes.LockCode], OnboardingLaunchTargets.Identification),
        new(OnboardingPartKey.MessagesAi, "messagesAi", 8, [], [], OnboardingLaunchTargets.Messages),
        new(OnboardingPartKey.FiscalDocuments, "fiscalDocuments", 9, [], [OnboardingNeedCodes.FiscalData], OnboardingLaunchTargets.Fiscal),
        new(OnboardingPartKey.Partners, "partners", 10, [], [], OnboardingLaunchTargets.Marketplace),
        new(
            OnboardingPartKey.Activation,
            "activation",
            11,
            [OnboardingPartKey.OctorateConnection, OnboardingPartKey.Properties],
            [],
            OnboardingLaunchTargets.Summary),
    ];

    /// <summary>
    /// The six steps of the path «start with CasaZen» of UX-10, each mapped to the parts it is made of. «Later» at the end of a step's wizard is
    /// a deferral or an exit of that step's part.
    /// </summary>
    public static IReadOnlyList<OnboardingPathStepDefinition> StartWithCasazenSteps { get; } =
    [
        new("connectChannels", 1, [OnboardingPartKey.OctorateConnection, OnboardingPartKey.Properties, OnboardingPartKey.Channels]),
        new("checkListings", 2, [OnboardingPartKey.Listings]),
        new("pricesCalendar", 3, [OnboardingPartKey.PricesRules]),
        new("checkInCompliance", 4, [OnboardingPartKey.Compliance]),
        new("messagesAi", 5, [OnboardingPartKey.MessagesAi]),
        new("fiscalDocuments", 6, [OnboardingPartKey.FiscalDocuments]),
    ];

    private static readonly IReadOnlyDictionary<OnboardingPartKey, OnboardingPartDefinition> ByKey = Parts.ToDictionary(part => part.Key);

    /// <summary>The definition of <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key"/> is not a part of the plan.</exception>
    public static OnboardingPartDefinition Of(OnboardingPartKey key) =>
        ByKey.TryGetValue(key, out var part)
            ? part
            : throw new ArgumentOutOfRangeException(nameof(key), key, "Not a part of the activation plan.");

    /// <summary>
    /// The part named by <paramref name="code"/>: its code or the name of its member, without regard to case, once trimmed (never a number).
    /// False for anything else.
    /// </summary>
    public static bool TryParse(string? code, out OnboardingPartKey key) =>
        EnumNames.TryParseDefined(code, out key) && ByKey.ContainsKey(key);

    /// <summary>The parts that require <paramref name="key"/> to be done first.</summary>
    public static IReadOnlyList<OnboardingPartKey> RequiredBy(OnboardingPartKey key) =>
        [.. Parts.Where(part => part.Requires.Contains(key)).Select(part => part.Key)];

    /// <summary>
    /// True when the host may do the part later («Do it later», ONB-01): every part but the prerequisite of another part, which would leave
    /// the parts after it blocked.
    /// </summary>
    public static bool IsDeferrable(OnboardingPartKey key) => RequiredBy(key).Count == 0;
}
