using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Assistant;

/// <summary>
/// What the assistant knows about one situation (E05/F05/US01/T03, NF-66, NF-67): its stable <see cref="Code"/>, the sensitive categories it carries by nature, the modes it
/// <see cref="AllowedModes">allows</see>, the mode a situation starts in, the topic of the knowledge base that answers it and the topics that must have data for the answer to be
/// complete (the «fact coverage» of the draft pipeline).
/// </summary>
/// <param name="Situation">The situation.</param>
/// <param name="Code">Stable text (<c>check_in_time</c>): what the routes accept and what the clients use as the key of the label they show.</param>
/// <param name="SensitiveCategories">The categories the situation carries by nature; none for a situation that is not sensitive.</param>
/// <param name="AllowedModes">The modes the situation may be set to: all four, or <see cref="AssistantMode.Escalate"/> and <see cref="AssistantMode.Draft"/> only.</param>
/// <param name="DefaultMode">The mode of a situation nobody configured: <see cref="AssistantMode.Draft"/>, and <see cref="AssistantMode.Escalate"/> for an emergency.</param>
/// <param name="SourceTopic">The topic of the knowledge base the answer comes from when there is no template; <c>null</c> when no topic answers it.</param>
/// <param name="RequiredTopics">The topics that must have at least one fact for the answer to be complete; empty when the answer needs none.</param>
public sealed record AssistantSituationInfo(
    AssistantSituation Situation,
    string Code,
    SensitiveCategory SensitiveCategories,
    IReadOnlyList<AssistantMode> AllowedModes,
    AssistantMode DefaultMode,
    KnowledgeTopic? SourceTopic,
    IReadOnlyList<KnowledgeTopic> RequiredTopics)
{
    /// <summary>The situation carries a sensitive category by nature (money, cancellation, complaint, damage, safety, legal, personal data).</summary>
    public bool IsSensitive => SensitiveCategories != SensitiveCategory.None;
}

/// <summary>
/// The one table of the situations the assistant recognizes (E05/F05/US01/T03, NF-66, NF-67), pure: the draft pipeline, the auto-send gate, the wizard and the tests all read it, so
/// a situation, its sensitive categories and its modes are written once. The 14 situations of NF-66 and <see cref="AssistantSituation.Other"/>, the one that matches none. The
/// labels shown to people are not here: the clients use the <see cref="AssistantSituationInfo.Code"/> as the key of their translations.
/// </summary>
/// <remarks>
/// <b>The rule of the modes.</b> A situation that carries a sensitive category, and <see cref="AssistantSituation.Other"/>, allows only <see cref="AssistantMode.Escalate"/> and
/// <see cref="AssistantMode.Draft"/>: it never goes automatic, whoever asks (AC-NF67-3). <see cref="AssistantModeRules"/> applies it.
/// </remarks>
public static class AssistantSituationCatalog
{
    private static readonly AssistantMode[] AllModes =
        [AssistantMode.Escalate, AssistantMode.Draft, AssistantMode.Automatic, AssistantMode.AutomaticWithExceptions];

    private static readonly AssistantMode[] NeverAutomatic = [AssistantMode.Escalate, AssistantMode.Draft];

    /// <summary>The 14 situations of NF-66 and the one that matches none, in the order the screens list them.</summary>
    public static IReadOnlyList<AssistantSituationInfo> All { get; } =
    [
        Info(AssistantSituation.CheckInTime, "check_in_time", SensitiveCategory.None, source: KnowledgeTopic.CheckIn, required: [KnowledgeTopic.CheckIn]),
        Info(
            AssistantSituation.EarlyArrivalOrLateCheckout,
            "early_arrival_or_late_checkout",
            SensitiveCategory.None,
            source: KnowledgeTopic.CheckIn,
            required: [KnowledgeTopic.CheckIn, KnowledgeTopic.CheckOut]),
        Info(AssistantSituation.Parking, "parking", SensitiveCategory.None, source: KnowledgeTopic.Parking, required: [KnowledgeTopic.Parking]),
        Info(AssistantSituation.WiFi, "wifi", SensitiveCategory.None, source: KnowledgeTopic.WiFi, required: [KnowledgeTopic.WiFi]),
        Info(AssistantSituation.Directions, "directions", SensitiveCategory.None, source: KnowledgeTopic.Directions, required: [KnowledgeTopic.Directions]),
        Info(AssistantSituation.Pets, "pets", SensitiveCategory.None, source: KnowledgeTopic.Pets, required: [KnowledgeTopic.Pets]),
        Info(AssistantSituation.InvoiceRequest, "invoice_request", SensitiveCategory.PersonalData | SensitiveCategory.Money),
        Info(AssistantSituation.ThankYou, "thank_you", SensitiveCategory.None),
        Info(AssistantSituation.Malfunction, "malfunction", SensitiveCategory.Damage),
        Info(AssistantSituation.Noise, "noise", SensitiveCategory.Complaint, source: KnowledgeTopic.HouseRules, required: [KnowledgeTopic.HouseRules]),
        Info(AssistantSituation.DiscountRequest, "discount_request", SensitiveCategory.Money),
        Info(AssistantSituation.CancellationOrRefund, "cancellation_or_refund", SensitiveCategory.CancellationRefund | SensitiveCategory.Money),
        Info(AssistantSituation.Complaint, "complaint", SensitiveCategory.Complaint),
        Info(
            AssistantSituation.Emergency,
            "emergency",
            SensitiveCategory.Safety,
            defaultMode: AssistantMode.Escalate,
            source: KnowledgeTopic.Contacts,
            required: [KnowledgeTopic.Contacts]),
        Info(AssistantSituation.Other, "other", SensitiveCategory.None, neverAutomatic: true),
    ];

    /// <summary>The entry of <paramref name="situation"/>; <see cref="ArgumentOutOfRangeException"/> for a value that is no member of <see cref="AssistantSituation"/>.</summary>
    public static AssistantSituationInfo Of(AssistantSituation situation) =>
        All.FirstOrDefault(info => info.Situation == situation)
        ?? throw new ArgumentOutOfRangeException(nameof(situation), situation, "Not a situation of the assistant.");

    /// <summary>The situation with this stable code (<c>check_in_time</c>), in any case; <c>false</c> for anything else.</summary>
    public static bool TryFromCode(string? code, out AssistantSituation situation)
    {
        var text = code?.Trim();
        var found = string.IsNullOrEmpty(text) ? null : All.FirstOrDefault(info => info.Code.Equals(text, StringComparison.OrdinalIgnoreCase));
        situation = found?.Situation ?? default;
        return found is not null;
    }

    /// <summary>
    /// The situation a route or a body names: its stable code (<c>check_in_time</c>) or the name of the enum member (<c>CheckInTime</c>), in any case. A number, and a name that
    /// is none of them, is <c>false</c>.
    /// </summary>
    public static bool TryParse(string? text, out AssistantSituation situation)
    {
        if (TryFromCode(text, out situation))
            return true;

        var name = text?.Trim();
        var found = string.IsNullOrEmpty(name)
            ? null
            : All.FirstOrDefault(info => info.Situation.ToString().Equals(name, StringComparison.OrdinalIgnoreCase));
        situation = found?.Situation ?? default;
        return found is not null;
    }

    private static AssistantSituationInfo Info(
        AssistantSituation situation,
        string code,
        SensitiveCategory sensitive,
        AssistantMode defaultMode = AssistantMode.Draft,
        KnowledgeTopic? source = null,
        KnowledgeTopic[]? required = null,
        bool neverAutomatic = false) => new(
            situation,
            code,
            sensitive,
            sensitive != SensitiveCategory.None || neverAutomatic ? NeverAutomatic : AllModes,
            defaultMode,
            source,
            required ?? []);
}
