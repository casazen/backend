using System.Collections.Frozen;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// The server side of the wizard drafts (ADR-024, NF-03, NF-06): the progress of a wizard is saved at every step and blur, found again
/// on any device and never lost. One draft per person, wizard and entity (<see cref="WizardDraft"/>).
/// </summary>
/// <remarks>
/// <para><b>The caller is resolved by the web layer.</b> There is no current-user service in this code base: the controller passes the
/// token's subject (<paramref name="userId"/>, a string) and the org (<paramref name="orgId"/>), as <see cref="IInAppNotificationService"/>
/// takes the user. A draft is reached only through both: the draft of another person, or of another org, does not exist for the caller
/// (<see cref="GetDraftAsync"/> answers <c>null</c>, never an error that tells it is there). An empty <paramref name="userId"/> is refused,
/// so a caller can never reach the anonymized rows, which have no user.</para>
/// <para><b>The data is split in two.</b> The fields that name a person (<see cref="WizardDraftPiiFields"/>, at any depth) go to the
/// encrypted half of the row; the rest to the plain half. A read puts the two together again.</para>
/// <para><b>Versions.</b> A save carries the version the client last saw. A client behind the server (another device saved first) gets
/// <see cref="DraftStatus.Conflict"/> with the server's draft and nothing is written. The check is atomic: two parallel saves of the same
/// version cannot both win (<c>xmin</c> on the row, the unique indexes for the first save).</para>
/// <para>The <see cref="JsonDocument"/> of a result belongs to the caller, who disposes it.</para>
/// </remarks>
public interface IWizardDraftService
{
    /// <summary>
    /// Saves the draft of the caller, creating it at the first save. Returns <see cref="DraftStatus.Saved"/> with the new version and the
    /// draft as saved (its <see cref="WizardDraftDto.LastSavedAt"/> is what the client shows as "saved at"), or
    /// <see cref="DraftStatus.Conflict"/> with the server's draft when the client is behind it. Every save sets the expiry to
    /// <see cref="WizardDraftLimits.RetentionDays"/> days after the save.
    /// </summary>
    /// <exception cref="DomainRuleException">The data is not a JSON object, or the type is not a declared one.</exception>
    /// <exception cref="DomainPayloadTooLargeException">The data is over <see cref="WizardDraftLimits.MaxDataBytes"/>.</exception>
    Task<WizardDraftResult> SaveDraftAsync(Guid orgId, string userId, SaveDraftRequest request, CancellationToken ct);

    /// <summary>The draft of the caller with the data put together again, or <c>null</c> when it has none (also: it expired, or it is another person's).</summary>
    Task<WizardDraftDto?> GetDraftAsync(Guid orgId, string userId, WizardType type, Guid? entityId, CancellationToken ct);

    /// <summary>
    /// Discards the draft of the caller: the data and the person are taken out of the row, which stays for the audit trail (as the
    /// retention does). Idempotent: no draft is not an error. Never touches the draft of another person.
    /// </summary>
    Task DeleteDraftAsync(Guid orgId, string userId, WizardType type, Guid? entityId, CancellationToken ct);
}

/// <summary>A save of a draft.</summary>
/// <param name="Type">The wizard.</param>
/// <param name="EntityId">What the wizard edits; <c>null</c> for a wizard that creates something.</param>
/// <param name="ClientVersion">The version of the draft the client last saw: 0 when it has none yet.</param>
/// <param name="Data">The typed data of the wizard, a JSON object; the personal fields are split off at any depth.</param>
public sealed record SaveDraftRequest(WizardType Type, Guid? EntityId, int ClientVersion, JsonDocument Data);

/// <summary>A draft as the client reads it.</summary>
/// <param name="Type">The wizard.</param>
/// <param name="EntityId">What the wizard edits; <c>null</c> for a wizard that creates something.</param>
/// <param name="Version">The counter of the draft: what the client sends back as <see cref="SaveDraftRequest.ClientVersion"/>.</param>
/// <param name="Data">The plain data put together with the decrypted personal fields.</param>
/// <param name="LastSavedAt">UTC instant of the last save.</param>
public sealed record WizardDraftDto(WizardType Type, Guid? EntityId, int Version, JsonDocument Data, DateTime LastSavedAt);

/// <summary>The outcome of a save.</summary>
/// <param name="Status">Saved, or conflict with a newer draft.</param>
/// <param name="ServerVersion">The version of the draft on the server after the save (saved) or as it is now (conflict).</param>
/// <param name="ServerDraft">The draft as saved, or the one the client must choose to keep or to take; <c>null</c> only when a conflict found no draft to show.</param>
public sealed record WizardDraftResult(DraftStatus Status, int ServerVersion, WizardDraftDto? ServerDraft);

public enum DraftStatus
{
    Saved,
    Conflict,
}

/// <summary>The <c>code</c> of the errors of the wizard drafts. Stable: the clients branch on them.</summary>
public static class WizardDraftErrors
{
    /// <summary>409: the client is behind the server (another device saved first), or lost a parallel save; the problem carries <c>serverDraft</c> and <c>serverVersion</c>.</summary>
    public const string Conflict = "draft_conflict";

    /// <summary>The data of a draft is not a JSON object.</summary>
    public const string DataInvalid = "wizard_draft_data_invalid";

    /// <summary>413: the data of a draft is over <see cref="WizardDraftLimits.MaxDataBytes"/>.</summary>
    public const string TooLarge = "wizard_draft_too_large";

    /// <summary>The wizard type is not a declared one.</summary>
    public const string TypeUnknown = "wizard_type_unknown";

    /// <summary>The type in the body of a save is not the one of the address.</summary>
    public const string TypeMismatch = "wizard_type_mismatch";

    /// <summary>404: the caller has no such draft (none saved, expired, or another person's: the same answer).</summary>
    public const string NotFound = "wizard_draft_not_found";
}

/// <summary>
/// The names of the fields that identify a person, split off the data of a draft and encrypted (ADR-024-R2). Matched whatever their case and
/// at any depth of the data, so a guest inside a list is covered too; the whole value under such a name is kept encrypted. The mobile app
/// mirrors this list: it has one home, here.
/// </summary>
public static class WizardDraftPiiFields
{
    public static readonly IReadOnlySet<string> PII_FIELD_NAMES = new[]
    {
        "guestName",
        "guestEmail",
        "guestPhone",
        "guestFiscalCode",
        "documentNumber",
        "documentType",
        "birthDate",
        "birthPlace",
        "citizenship",
        "address",
        "emergencyContact",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
