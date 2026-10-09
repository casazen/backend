using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.OrgTeam;

/// <summary>The keys a detail of an activity line may have (<see cref="OrgActivityEntry.DetailsJson"/>). A closed list: a new key is a decision, with a reason, here.</summary>
public static class OrgActivityDetailKeys
{
    /// <summary>The org role of the person the line is about (<c>Admin</c>, <c>Collaborator</c>...).</summary>
    public const string Role = "role";

    public const string FromRole = "fromRole";
    public const string ToRole = "toRole";

    /// <summary>The property scope a member now has (<c>All</c> or <c>Selected</c>).</summary>
    public const string Scope = "scope";

    /// <summary>How many properties were given to the member in one change.</summary>
    public const string Granted = "granted";

    /// <summary>How many properties were taken away from the member in one change.</summary>
    public const string Revoked = "revoked";

    public const string FromTier = "fromTier";
    public const string ToTier = "toTier";

    /// <summary>Who or what changed the plan: <see cref="PlanChangeSource"/>.</summary>
    public const string Source = "source";

    /// <summary>The area or page a member asked access to (<see cref="OrgAccessRequestRules.Areas"/>): a code of a closed list.</summary>
    public const string RequestedArea = "requestedArea";

    public const string FromMode = "fromMode";
    public const string ToMode = "toMode";

    /// <summary>The day (<c>yyyy-MM-dd</c>) a scheduled change takes effect.</summary>
    public const string EffectiveOn = "effectiveOn";

    public static IReadOnlyList<string> All { get; } =
    [
        Role, FromRole, ToRole, Scope, Granted, Revoked, FromTier, ToTier, Source, RequestedArea, FromMode, ToMode, EffectiveOn,
    ];
}

/// <summary>Who or what changed the plan of an org (the <c>source</c> detail of <see cref="OrgActivityType.PlanChanged"/>).</summary>
public enum PlanChangeSource
{
    /// <summary>The org's own billing administrator (<c>PUT /api/orgs/me/plan</c>).</summary>
    Org = 1,

    /// <summary>CasaZen staff (<c>PATCH /api/admin/orgs/{id}/plan</c>).</summary>
    Staff = 2,

    /// <summary>The subscription: Stripe's webhook gave the tier of the price, or the subscription stopped paying and the plan fell back to Starter.</summary>
    Subscription = 3,
}

public static class PlanChangeSourceExtensions
{
    /// <summary>The code written in the log: <c>org</c>, <c>staff</c> or <c>subscription</c>.</summary>
    public static string Code(this PlanChangeSource source) => source switch
    {
        PlanChangeSource.Org => "org",
        PlanChangeSource.Staff => "staff",
        PlanChangeSource.Subscription => "subscription",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown plan change source"),
    };
}

/// <summary>What the activity log knows about one <see cref="OrgActivityType"/>.</summary>
/// <param name="DefaultArea">The area the line belongs to, unless the caller says otherwise (the reserved events of the property mode).</param>
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
    /// <summary>The longest id (an actor or a subject) a line holds.</summary>
    public const int MaxIdLength = 255;

    /// <summary>The longest value of a detail.</summary>
    public const int MaxDetailValueLength = 40;

    private static readonly OrgActivityTypeInfo[] Entries =
    [
        // People.
        new(OrgActivityType.MemberInvited, OrgActivityArea.Account, OrgActivitySubjectType.Invitation, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.InvitationRevoked, OrgActivityArea.Account, OrgActivitySubjectType.Invitation, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.InvitationAccepted, OrgActivityArea.Account, OrgActivitySubjectType.Invitation, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.MemberRoleChanged, OrgActivityArea.Account, OrgActivitySubjectType.Member, [OrgActivityDetailKeys.FromRole, OrgActivityDetailKeys.ToRole]),
        new(OrgActivityType.MemberDeactivated, OrgActivityArea.Account, OrgActivitySubjectType.Member, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.MemberReactivated, OrgActivityArea.Account, OrgActivitySubjectType.Member, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.MemberRemoved, OrgActivityArea.Account, OrgActivitySubjectType.Member, [OrgActivityDetailKeys.Role]),
        new(OrgActivityType.MemberPropertyAccessChanged, OrgActivityArea.Account, OrgActivitySubjectType.Member,
            [OrgActivityDetailKeys.Scope, OrgActivityDetailKeys.Granted, OrgActivityDetailKeys.Revoked]),
        new(OrgActivityType.AccessRequested, OrgActivityArea.Account, OrgActivitySubjectType.Org, [OrgActivityDetailKeys.RequestedArea]),

        // Plan and organization.
        new(OrgActivityType.PlanChanged, OrgActivityArea.Account, OrgActivitySubjectType.Org,
            [OrgActivityDetailKeys.FromTier, OrgActivityDetailKeys.ToTier, OrgActivityDetailKeys.Source]),
        new(OrgActivityType.OrgNameChanged, OrgActivityArea.Account, OrgActivitySubjectType.Org, []),
        new(OrgActivityType.OrgSlugChanged, OrgActivityArea.Account, OrgActivitySubjectType.Org, []),

        // Reserved: the area of a mode change is the one of the property, given by the task that writes it (PM-02).
        new(OrgActivityType.PropertyModeChangeScheduled, OrgActivityArea.ShortRent, OrgActivitySubjectType.Property,
            [OrgActivityDetailKeys.FromMode, OrgActivityDetailKeys.ToMode, OrgActivityDetailKeys.EffectiveOn], Reserved: true),
        new(OrgActivityType.PropertyModeChangeCancelled, OrgActivityArea.ShortRent, OrgActivitySubjectType.Property,
            [OrgActivityDetailKeys.FromMode, OrgActivityDetailKeys.ToMode], Reserved: true),
        new(OrgActivityType.PropertyModeChanged, OrgActivityArea.ShortRent, OrgActivitySubjectType.Property,
            [OrgActivityDetailKeys.FromMode, OrgActivityDetailKeys.ToMode], Reserved: true),

        // Reserved: the trusted suppliers of the host.
        new(OrgActivityType.TrustedSupplierAdded, OrgActivityArea.Supplier, OrgActivitySubjectType.Supplier, [], Reserved: true),
        new(OrgActivityType.TrustedSupplierRemoved, OrgActivityArea.Supplier, OrgActivitySubjectType.Supplier, [], Reserved: true),
    ];

    /// <summary>Every event the log can record, in the order of the runbook.</summary>
    public static IReadOnlyList<OrgActivityTypeInfo> All => Entries;

    public static OrgActivityTypeInfo Describe(OrgActivityType type) =>
        Entries.FirstOrDefault(e => e.Type == type)
        ?? throw new ArgumentException($"'{type}' is not an event of the activity log.", nameof(type));

    // ─── The codes the API speaks ───────────────────────────────────────────────────────────────────────

    /// <summary>The code of an area in the API and the CSV: the key of the context (<c>account</c>, <c>short-rent</c>, <c>long-rent</c>) or <c>supplier</c>.</summary>
    public static string AreaCode(OrgActivityArea area) => area switch
    {
        OrgActivityArea.Account => "account",
        OrgActivityArea.ShortRent => "short-rent",
        OrgActivityArea.LongRent => "long-rent",
        OrgActivityArea.Supplier => "supplier",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Unknown activity area"),
    };

    /// <summary>The area with this code (<see cref="AreaCode"/>, case-insensitive).</summary>
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

    /// <summary>The event named <paramref name="name"/> (its code, case-insensitive: <c>MemberRoleChanged</c>). A number is not a name.</summary>
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

    // ─── The rules that keep personal data out ──────────────────────────────────────────────────────────

    /// <summary>A short code: letters, digits and <c>. _ -</c>, starting with a letter or a digit. No space, no <c>@</c>, no sentence.</summary>
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
