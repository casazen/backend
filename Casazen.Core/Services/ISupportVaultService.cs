using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The support vault (E20/F03/US01/T01, decision O5, PO-19, ONB-04, UX-15): the credentials CasaZen support needs to link the police and ISTAT
/// in the channel manager panel for a host, kept encrypted and opened only on an open «Do it for me» request, with a reason, under a limit, and
/// with an audit line that the host sees. The credentials of a structure CasaZen created arrive only at the CasaZen address of the org (PO-19):
/// support copies them here and deletes the e-mail (<c>docs/runbooks/onboarding-assist.md</c>), so they never stay in a mailbox.
/// </summary>
/// <remarks>
/// <para><b>Who.</b> Every operation is for the platform administrators only: the web layer decides (<c>CasazenPolicies.AdminOnly</c>), the
/// service never checks roles. Staff works from the context of its own org, so every read lifts the tenant filter by name and names the org of
/// the request or of the credential in the query.</para>
/// <para><b>Nothing leaks.</b> The values are never logged, never put in an exception, a ProblemDetails, a Hangfire argument or an activity line;
/// the inputs and the opened values hide themselves from <c>ToString</c>. The only answer with a value is <see cref="RevealAsync"/>, once.</para>
/// </remarks>
public interface ISupportVaultService
{
    /// <summary>
    /// Stores, or replaces, a credential of an org: for one of its properties or for the whole org. 404 <see cref="SupportVaultCodes.OrgNotFound"/>
    /// for an org that is not a host org, 404 <c>property_not_found</c> for a property that is not of the org; 422 for a value that is missing or too
    /// long. Answers the presence, never the values.
    /// </summary>
    Task<SupportVaultSecretPresence> StoreAsync(string staffUserId, SupportVaultSecretInput input, CancellationToken ct);

    /// <summary>What the vault holds for an org: one presence for each credential (kind, property, dates, who), never a value.</summary>
    Task<IReadOnlyList<SupportVaultSecretPresence>> ListAsync(Guid orgId, CancellationToken ct);

    /// <summary>Removes a credential of the vault; false when there was none.</summary>
    Task<bool> DeleteAsync(string staffUserId, Guid secretId, CancellationToken ct);

    /// <summary>
    /// The controlled opening: the values of one credential, in clear, for an OPEN «Do it for me» request (<c>Requested</c> or <c>InProgress</c>),
    /// of the org and the property of that request only. Under the lock of the request: at most <see cref="SupportVaultLimits.MaxOpeningsPerRequestPerHour"/>
    /// openings for each request in an hour. Every opening writes a <see cref="SupportVaultAccess"/> (who, when, request, kind, reason; never the value)
    /// and the line <c>OnboardingAssistUpdated</c> with the kind of credential in the log of the org of the request, which the host reads.
    /// 404 <c>assist_request_not_found</c>, 409 <see cref="SupportVaultCodes.RequestNotOpen"/>, 422 <see cref="SupportVaultCodes.ReasonInvalid"/> or
    /// <see cref="SupportVaultCodes.KindNotForRequest"/>, 429 <see cref="SupportVaultCodes.TooManyOpenings"/>, 404 <see cref="SupportVaultCodes.SecretMissing"/>.
    /// </summary>
    Task<SupportVaultReveal> RevealAsync(string staffUserId, Guid requestId, SupportVaultCredentialKind kind, string? reason, CancellationToken ct);
}

/// <summary>A credential staff stores in the vault, in clear (never logged, never returned).</summary>
/// <param name="OrgId">The org the credential belongs to (the org of the CasaZen address the welcome e-mail arrived at).</param>
/// <param name="PropertyId">The structure; <c>null</c> for a credential of the whole org.</param>
/// <param name="Kind">What it opens.</param>
/// <param name="Username">The user name.</param>
/// <param name="Password">The password, kept exactly as typed.</param>
/// <param name="ExtraCode">A further code (the code of the structure, a PIN); <c>null</c> or blank when there is none.</param>
public sealed record SupportVaultSecretInput(
    Guid OrgId,
    Guid? PropertyId,
    SupportVaultSecretKind Kind,
    string Username,
    string Password,
    string? ExtraCode)
{
    /// <summary>Hides the values from logs and debugger output.</summary>
    public override string ToString() => nameof(SupportVaultSecretInput) + " { *** }";
}

/// <summary>What anybody may know about a credential of the vault: that it is there, for what, since when and who stored it last. Never a value.</summary>
public sealed record SupportVaultSecretPresence(
    Guid Id,
    Guid OrgId,
    Guid? PropertyId,
    SupportVaultSecretKind Kind,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string UpdatedByUserId);

/// <summary>
/// The values of one opening, in clear, answered once and never stored again. <see cref="VisibleUntil"/> is the end of the minute the support
/// person has to use them (decision O5): the client hides them then.
/// </summary>
public sealed record SupportVaultReveal(
    SupportVaultCredentialKind Kind,
    string Username,
    string Password,
    string? ExtraCode,
    DateTime OpenedAt,
    DateTime VisibleUntil)
{
    /// <summary>Hides the values from logs and debugger output.</summary>
    public override string ToString() => nameof(SupportVaultReveal) + " { *** }";
}

/// <summary>The stable codes of the support vault. Never rename one.</summary>
public static class SupportVaultCodes
{
    /// <summary>422: the reason of an opening is missing, shorter than 10 or longer than 300 characters.</summary>
    public const string ReasonInvalid = "support_vault_reason_invalid";

    /// <summary>422: the kind of credential is not one of the three, or not one of the two a credential of the vault can be.</summary>
    public const string KindInvalid = "support_vault_kind_invalid";

    /// <summary>422: the credential is not one the request needs (the police for a request about ISTAT, a credential of a property for a request of the whole org).</summary>
    public const string KindNotForRequest = "support_vault_kind_not_for_request";

    /// <summary>409: the request is done or cancelled: a credential is opened only for an open request.</summary>
    public const string RequestNotOpen = "support_vault_request_not_open";

    /// <summary>429: the request had its openings of the hour already.</summary>
    public const string TooManyOpenings = "support_vault_too_many_openings";

    /// <summary>404: the vault (or the host's form) holds no such credential for the org and the property of the request.</summary>
    public const string SecretMissing = "support_vault_secret_missing";

    /// <summary>404: no host org with that id.</summary>
    public const string OrgNotFound = "support_vault_org_not_found";

    /// <summary>422: the user name or the password is missing or too long, or the further code is too long.</summary>
    public const string ValueInvalid = "support_vault_value_invalid";
}

/// <summary>The limits of the controlled opening (decision O5). Not configurable: they are the rule.</summary>
public static class SupportVaultLimits
{
    /// <summary>The most openings of one request in an hour, whatever the kind.</summary>
    public const int MaxOpeningsPerRequestPerHour = 5;

    /// <summary>Seconds the opened values are meant to stay on the screen.</summary>
    public const int VisibleSeconds = 60;
}

/// <summary>The rules of the support vault that do not need the database.</summary>
public static class SupportVaultRules
{
    /// <summary>
    /// True when a request of <paramref name="part"/> and <paramref name="context"/>, about <paramref name="propertyId"/>, needs the credential
    /// <paramref name="kind"/>. The panel of the structure serves every request; the police credentials serve the police connection (or the whole
    /// compliance part) of a property, the ISTAT credentials the ISTAT connection (or the whole compliance part) of a property. The least the work
    /// needs, never more: a request about a channel never opens the police credentials.
    /// </summary>
    public static bool KindFits(OnboardingPartKey part, string? context, Guid? propertyId, SupportVaultCredentialKind kind) => kind switch
    {
        SupportVaultCredentialKind.OctoratePanel => true,
        SupportVaultCredentialKind.Police => propertyId is not null
                                             && (context == OnboardingAssistContexts.PanelPolice
                                                 || (context == OnboardingAssistContexts.Part && part == OnboardingPartKey.Compliance)),
        SupportVaultCredentialKind.RegionalIstat => propertyId is not null
                                                    && (context == OnboardingAssistContexts.PanelIstat
                                                        || (context == OnboardingAssistContexts.Part && part == OnboardingPartKey.Compliance)),
        _ => false,
    };

    /// <summary>
    /// The code of a kind of credential in the activity log of the org (<c>panel</c>, <c>police</c>, <c>regionalIstat</c>): a code of a closed list
    /// the client turns into its sentence, with no name of a supplier.
    /// </summary>
    public static string CodeOf(SupportVaultCredentialKind kind) => kind switch
    {
        SupportVaultCredentialKind.OctoratePanel => "panel",
        SupportVaultCredentialKind.Police => "police",
        SupportVaultCredentialKind.RegionalIstat => "regionalIstat",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of credential of the vault."),
    };

    /// <summary>The reason of an opening, trimmed; false when it is missing, shorter than 10 or longer than 300 characters.</summary>
    public static bool TryNormalizeReason(string? reason, out string normalized)
    {
        normalized = reason?.Trim() ?? string.Empty;
        return normalized.Length is >= SupportVaultAccess.ReasonMinLength and <= SupportVaultAccess.ReasonMaxLength;
    }
}
