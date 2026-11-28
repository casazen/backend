using Casazen.Core.Entities.Enums;
using Casazen.Core.Validation;

namespace Casazen.Core.Services;

public static class OnboardingNeedCodes
{
    public const string AlloggiatiCredentials = "alloggiatiCredentials";

    public const string IstatCredentials = "istatCredentials";

    public const string FiscalData = "fiscalData";

    public const string OtaAccess = "otaAccess";

    public const string LockCode = "lockCode";

    public static IReadOnlyList<string> All { get; } = [AlloggiatiCredentials, IstatCredentials, FiscalData, OtaAccess, LockCode];
}

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

    public const string GuestRegistration = "guestRegistration";
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

public sealed record OnboardingPathStepDefinition(string Code, int Order, IReadOnlyList<OnboardingPartKey> Parts);

/// <summary>
/// The catalog of the twelve parts of the activation plan (NF-104, ONB-01, E10/F01/US01/T03; E29/F02/US01/T02 added the twelfth). Code-defined, not configured (the open question
/// «code-defined or configured» of the epic is answered «code»): what a part requires, needs and launches is part of the product, and a change
/// of it is a change of the code. Whether a part is done is never here: it is derived on read from the stored facts
/// (<see cref="IOnboardingPartEvaluator"/>).
/// </summary>
public static class OnboardingPartCatalog
{
    public const string StartWithCasazenPath = "start-with-casazen";

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
        new(OnboardingPartKey.GuestRegistration, "guestRegistration", 11, [], [], OnboardingLaunchTargets.GuestRegistration),
        new(
            OnboardingPartKey.Activation,
            "activation",
            12,
            [OnboardingPartKey.OctorateConnection, OnboardingPartKey.Properties],
            [],
            OnboardingLaunchTargets.Summary),
    ];

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

    public static OnboardingPartDefinition Of(OnboardingPartKey key) =>
        ByKey.TryGetValue(key, out var part)
            ? part
            : throw new ArgumentOutOfRangeException(nameof(key), key, "Not a part of the activation plan.");

    public static bool TryParse(string? code, out OnboardingPartKey key) =>
        EnumNames.TryParseDefined(code, out key) && ByKey.ContainsKey(key);

    public static IReadOnlyList<OnboardingPartKey> RequiredBy(OnboardingPartKey key) =>
        [.. Parts.Where(part => part.Requires.Contains(key)).Select(part => part.Key)];

    public static bool IsDeferrable(OnboardingPartKey key) => RequiredBy(key).Count == 0;
}
