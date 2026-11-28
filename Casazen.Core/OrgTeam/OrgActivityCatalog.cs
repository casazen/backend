using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.OrgTeam;

public static class OrgActivityDetailKeys
{
    public const string Role = "role";

    public const string FromRole = "fromRole";
    public const string ToRole = "toRole";

    public const string Scope = "scope";

    public const string Granted = "granted";

    public const string Revoked = "revoked";

    /// <summary>
    /// How many properties lost their person in charge (<c>Property.ResponsibleUserId</c>) because the member no longer reaches
    /// them (AM-03b: its access was narrowed; AM-02c: it gave up an area, or the property changed mode). Written only when there
    /// were some; the details of a change that released nothing are as they were.
    /// </summary>
    public const string Released = "released";

    public const string FromTier = "fromTier";
    public const string ToTier = "toTier";

    public const string Source = "source";

    public const string RequestedArea = "requestedArea";

    public const string FromMode = "fromMode";
    public const string ToMode = "toMode";

    public const string EffectiveOn = "effectiveOn";

    public const string FromAreas = "fromAreas";

    public const string ToAreas = "toAreas";

    public const string Reminders = "reminders";

    /// <summary>
    /// Who sent the police and ISTAT communications of a property before a change (E03/F01/US05/T02): the name of the
    /// <see cref="ComplianceSenderType"/> (<c>ChannelManager</c>, <c>CasaZen</c>, <c>Manual</c>).
    /// </summary>
    public const string FromSender = "fromSender";

    public const string ToSender = "toSender";

    /// <summary>
    /// The version of the delegation text an org accepted when it delegated the Alloggiati transmission (a code of the legal
    /// documents, e.g. <c>2026-11-v1</c>), never the text. Always written: the delegation refuses a version of more than 40 characters.
    /// </summary>
    public const string DelegationVersion = "delegationVersion";

    /// <summary>The compliance topic a verification handed to the channel manager is about: <c>Alloggiati</c> or <c>Istat</c> (E03/F05/US01/T01).</summary>
    public const string Topic = "topic";

    public const string Booking = "booking";

    public const string Period = "period";

    public const string Handoff = "handoff";

    /// <summary>
    /// What a read of the contact of a guest was for: a code of the closed list <see cref="ContactReadPurposes"/> (E05/F02/US02/T01). It is not called
    /// «reason» because a free text must have no key to sit under; the value is always one of the codes of the list.
    /// </summary>
    public const string Purpose = "purpose";

    public const string Part = "part";

    public const string Context = "context";

    public const string Status = "status";

    public const string Rows = "rows";

    public const string Applied = "applied";

    public const string Fingerprint = "fingerprint";

    /// <summary>
    /// The kind of credential of the support vault CasaZen staff opened for a «Do it for me» request (<c>panel</c>, <c>police</c>, <c>regionalIstat</c>,
    /// <c>SupportVaultRules.CodeOf</c>), a code of a closed list (E20/F03/US01/T01, decision O5). The reason: the host is told, in its own log, that
    /// support used its credentials and which ones; the catalog is at its cap, so the opening is a line of the request's own event with this one more
    /// key instead of an event of its own. Never a value, never the reason the staff wrote.
    /// </summary>
    public const string Credential = "credential";

    public static IReadOnlyList<string> All { get; } =
    [
        Role, FromRole, ToRole, Scope, Granted, Revoked, Released, FromTier, ToTier, Source, RequestedArea, FromMode, ToMode, EffectiveOn,
        FromAreas, ToAreas, Reminders, FromSender, ToSender, DelegationVersion,
        Topic, Booking, Period, Handoff,
        Part, Context, Status, Credential,
        Purpose,
        Rows, Applied, Fingerprint,
    ];
}

public static class ContactReadPurposes
{
    public const string MessageToGuest = "message_to_guest";

    public const string CheckInLink = "check_in_link";

    public static IReadOnlyList<string> All { get; } = [MessageToGuest, CheckInLink];
}

public enum PlanChangeSource
{
    Org = 1,

    Staff = 2,

    Subscription = 3,
}

public static class PlanChangeSourceExtensions
{
    public static string Code(this PlanChangeSource source) => source switch
    {
        PlanChangeSource.Org => "org",
        PlanChangeSource.Staff => "staff",
        PlanChangeSource.Subscription => "subscription",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown plan change source"),
    };
}

/// <summary>What the activity log knows about one <see cref="OrgActivityType"/>.</summary>
/// <param name="DefaultArea">The area the line belongs to, unless the caller says otherwise (the events of the property mode: the area of the property).</param>
/// <param name="SubjectType">What the id of the line is.</param>
/// <param name="DetailKeys">The only keys a detail of this type may have (<see cref="OrgActivityDetailKeys"/>).</param>
/// <param name="Reserved">The code is fixed and nothing writes it yet.</param>
public sealed record OrgActivityTypeInfo(
    OrgActivityType Type,
    OrgActivityArea DefaultArea,
    OrgActivitySubjectType SubjectType,
    IReadOnlyList<string> DetailKeys,
    bool Reserved = false);

/// <summary>
/// The perimeter of the activity log (AM-02b, wave decision D17): every event it can record, with its area, the thing its id
/// points to and the few details it may carry; and the rules that keep the log free of personal data. The runbook
/// (<c>docs/runbooks/org-team.md</c>) lists the same events, and a test fails if it does not.
/// </summary>
/// <remarks>
/// <para><b>Why a catalog.</b> The log is read by people other than the one who acted, and kept for 12 months: whatever gets in
/// stays. So an event is described once, here, and <see cref="Validate"/> refuses every line that does not fit: an unknown type,
/// an empty or too long id, a detail the type does not allow, a value that is not a short code. A name, an email or a sentence
/// has no way in. The ids are opaque (<c>User.Id</c>, a GUID); the values are enum names, counts, dates and codes.</para>
/// </remarks>
public static partial class OrgActivityCatalog
{
    public const int MaxIdLength = 255;

    public const int MaxDetailValueLength = 40;

    private static readonly OrgActivityTypeInfo[] Entries =
    [
        new(OrgActivityType.MemberInvited, OrgActivityArea.Account, OrgActivitySubjectType.Invitation, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.InvitationRevoked, OrgActivityArea.Account, OrgActivitySubjectType.Invitation, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.InvitationAccepted, OrgActivityArea.Account, OrgActivitySubjectType.Invitation, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.MemberRoleChanged, OrgActivityArea.Account, OrgActivitySubjectType.Member, [OrgActivityDetailKeys.FromRole, OrgActivityDetailKeys.ToRole]),
        new(OrgActivityType.MemberDeactivated, OrgActivityArea.Account, OrgActivitySubjectType.Member, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.MemberReactivated, OrgActivityArea.Account, OrgActivitySubjectType.Member, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.MemberRemoved, OrgActivityArea.Account, OrgActivitySubjectType.Member, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.MemberPropertyAccessChanged, OrgActivityArea.Account, OrgActivitySubjectType.Member,
            [OrgActivityDetailKeys.Scope, OrgActivityDetailKeys.Granted, OrgActivityDetailKeys.Revoked, OrgActivityDetailKeys.Released]),
        new(OrgActivityType.AccessRequested, OrgActivityArea.Account, OrgActivitySubjectType.Org, [OrgActivityDetailKeys.RequestedArea]),
        new(OrgActivityType.MemberAreasChanged, OrgActivityArea.Account, OrgActivitySubjectType.Member,
            [OrgActivityDetailKeys.FromAreas, OrgActivityDetailKeys.ToAreas, OrgActivityDetailKeys.Released]),

        new(OrgActivityType.PlanChanged, OrgActivityArea.Account, OrgActivitySubjectType.Org,
            [OrgActivityDetailKeys.FromTier, OrgActivityDetailKeys.ToTier, OrgActivityDetailKeys.Source]),
        new(OrgActivityType.OrgNameChanged, OrgActivityArea.Account, OrgActivitySubjectType.Org, []),
        new(OrgActivityType.OrgSlugChanged, OrgActivityArea.Account, OrgActivitySubjectType.Org, []),
        new(OrgActivityType.TierAssigned, OrgActivityArea.Account, OrgActivitySubjectType.Org,
            [OrgActivityDetailKeys.ToTier], Reserved: true),
        new(OrgActivityType.OrgSettingsChanged, OrgActivityArea.Account, OrgActivitySubjectType.Org, [OrgActivityDetailKeys.Part]),

        // The mode change of a property (PM-02, written by PropertyModeService since AM-02c). The area is the one of the property,
        // which the service passes (ShortRent is only the default of the catalog).
        new(OrgActivityType.PropertyModeChangeScheduled, OrgActivityArea.ShortRent, OrgActivitySubjectType.Property,
            [OrgActivityDetailKeys.FromMode, OrgActivityDetailKeys.ToMode, OrgActivityDetailKeys.EffectiveOn]),
        new(OrgActivityType.PropertyModeChangeCancelled, OrgActivityArea.ShortRent, OrgActivitySubjectType.Property,
            [OrgActivityDetailKeys.FromMode, OrgActivityDetailKeys.ToMode]),
        new(OrgActivityType.PropertyModeChanged, OrgActivityArea.ShortRent, OrgActivitySubjectType.Property,
            [OrgActivityDetailKeys.FromMode, OrgActivityDetailKeys.ToMode, OrgActivityDetailKeys.Released]),

        new(OrgActivityType.PropertyActivatedWithReminders, OrgActivityArea.ShortRent, OrgActivitySubjectType.Property,
            [OrgActivityDetailKeys.Reminders]),

        new(OrgActivityType.TrustedSupplierAdded, OrgActivityArea.Supplier, OrgActivitySubjectType.Supplier, [], Reserved: true),
        new(OrgActivityType.TrustedSupplierRemoved, OrgActivityArea.Supplier, OrgActivitySubjectType.Supplier, [], Reserved: false),

        new(OrgActivityType.AdminGrantedConcordatoFacileGift, OrgActivityArea.Account, OrgActivitySubjectType.Org, []),

        // The police and ISTAT communications (E03/F01/US05/T02): who sends them for a property (ADR-029-R4), and the delegation of the
        // Alloggiati transmission of the org (AW-R02, ADR-015-R10).
        new(OrgActivityType.PropertySenderChanged, OrgActivityArea.ShortRent, OrgActivitySubjectType.Property,
            [OrgActivityDetailKeys.FromSender, OrgActivityDetailKeys.ToSender]),
        new(OrgActivityType.AlloggiatiDelegationGranted, OrgActivityArea.ShortRent, OrgActivitySubjectType.Org,
            [OrgActivityDetailKeys.DelegationVersion]),
        new(OrgActivityType.AlloggiatiDelegationRevoked, OrgActivityArea.ShortRent, OrgActivitySubjectType.Org, []),

        // The guided passage to the Octorate Cassetto Compliance (E03/F05/US01/T01, CHK-15): the line says that it started, with the
        // topic, the opaque handoff id and, when the request had them, the booking id and the period. Never a guest, never an outcome.
        new(OrgActivityType.OctorateComplianceHandoffStarted, OrgActivityArea.ShortRent, OrgActivitySubjectType.Property,
            [OrgActivityDetailKeys.Topic, OrgActivityDetailKeys.Handoff, OrgActivityDetailKeys.Booking, OrgActivityDetailKeys.Period]),

        // «Do it for me» (E10/F01/US03/T01, NF-15): a host asks CasaZen support to do a part or a panel step, and every staff action on the request.
        // The line says which part, which context and where the request stands: never the draft, a note, a credential or a person. An opening of the
        // support vault for the request (E20/F03/US01/T01) is a staff action too: its line says which kind of credential was used, never its value.
        new(OrgActivityType.OnboardingAssistRequested, OrgActivityArea.Account, OrgActivitySubjectType.Org,
            [OrgActivityDetailKeys.Part, OrgActivityDetailKeys.Context, OrgActivityDetailKeys.Status]),
        new(OrgActivityType.OnboardingAssistUpdated, OrgActivityArea.Account, OrgActivitySubjectType.Org,
            [OrgActivityDetailKeys.Part, OrgActivityDetailKeys.Context, OrgActivityDetailKeys.Status, OrgActivityDetailKeys.Credential]),

        // The read of the contact of the guest of one booking, to write to it (E05/F02/US02/T01): Octorate records every read, so CasaZen records who
        // asked and what for (the booking is the subject, the person is the actor). Never the address, the number or a word of the message.
        new(OrgActivityType.ContactInfoRead, OrgActivityArea.ShortRent, OrgActivitySubjectType.Booking, [OrgActivityDetailKeys.Purpose]),

        new(OrgActivityType.OwnerImportApplied, OrgActivityArea.Account, OrgActivitySubjectType.Org,
            [OrgActivityDetailKeys.Rows, OrgActivityDetailKeys.Applied, OrgActivityDetailKeys.Fingerprint]),
        new(OrgActivityType.OwnerInvitationChanged, OrgActivityArea.Account, OrgActivitySubjectType.OwnerInvitation, [OrgActivityDetailKeys.Status]),

        new(OrgActivityType.OwnerPortalChanged, OrgActivityArea.Account, OrgActivitySubjectType.OwnerPortalItem,
            [OrgActivityDetailKeys.Part, OrgActivityDetailKeys.Status]),

        new(OrgActivityType.IdentityEvidenceViewed, OrgActivityArea.ShortRent, OrgActivitySubjectType.Booking, [OrgActivityDetailKeys.Part]),
    ];

    public static IReadOnlyList<OrgActivityTypeInfo> All => Entries;

    public static OrgActivityTypeInfo Describe(OrgActivityType type) =>
        Entries.FirstOrDefault(e => e.Type == type)
        ?? throw new ArgumentException($"'{type}' is not an event of the activity log.", nameof(type));

    public static string AreaCode(OrgActivityArea area) => area switch
    {
        OrgActivityArea.Account => "account",
        OrgActivityArea.ShortRent => "short-rent",
        OrgActivityArea.LongRent => "long-rent",
        OrgActivityArea.Supplier => "supplier",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Unknown activity area"),
    };

    public static bool TryParseArea(string? code, out OrgActivityArea area)
    {
        foreach (var candidate in Enum.GetValues<OrgActivityArea>())
        {
            if (string.Equals(AreaCode(candidate), code?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                area = candidate;
                return true;
            }
        }

        area = default;
        return false;
    }

    public static bool TryParseType(string? name, out OrgActivityType type)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Type.ToString(), name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                type = entry.Type;
                return true;
            }
        }

        type = default;
        return false;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$")]
    private static partial Regex DetailValuePattern();

    public static bool IsSafeDetailValue(string? value) => value is not null && DetailValuePattern().IsMatch(value);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless the line fits the catalog: a known type, an actor and a subject that are
    /// ids (not empty, not longer than <see cref="MaxIdLength"/>), only the details the type allows, each a short code.
    /// </summary>
    public static void Validate(OrgActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.OrgId == Guid.Empty)
            throw new ArgumentException("An activity line belongs to an org.", nameof(activity));

        var info = Describe(activity.Type);

        if (activity.Area is { } area && !Enum.IsDefined(area))
            throw new ArgumentException($"'{area}' is not an activity area.", nameof(activity));

        if (activity.ActorUserId is { } actor && (actor.Length is 0 or > MaxIdLength))
            throw new ArgumentException("The actor of an activity line is an id of at most 255 characters, or nobody.", nameof(activity));

        if (string.IsNullOrWhiteSpace(activity.SubjectId) || activity.SubjectId.Length > MaxIdLength)
            throw new ArgumentException("The subject of an activity line is an id of at most 255 characters.", nameof(activity));

        foreach (var (key, value) in activity.Details ?? new Dictionary<string, string>())
        {
            if (!info.DetailKeys.Contains(key, StringComparer.Ordinal))
                throw new ArgumentException($"'{activity.Type}' does not take the detail '{key}'.", nameof(activity));

            if (!IsSafeDetailValue(value))
                throw new ArgumentException($"The detail '{key}' of '{activity.Type}' is not a short code.", nameof(activity));
        }
    }
}
