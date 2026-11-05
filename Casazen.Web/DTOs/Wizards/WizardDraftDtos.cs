using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Casazen.Web.DTOs.Wizards;

/// <summary>
/// Body of <c>POST /api/wizards/{type}/draft</c> (ADR-024): the typed data of the wizard and the version of the draft the client last saw.
/// The wizard is the one of the address; a <c>type</c> in the body is optional and, when there, must be the same.
/// </summary>
public sealed class SaveWizardDraftRequest
{
    /// <summary>The wizard, optional: the address says it; a different one here is a 400 <c>wizard_type_mismatch</c>.</summary>
    public WizardType? Type { get; set; }

    /// <summary>What the wizard edits (the property, the booking...); <c>null</c> for a wizard that creates something.</summary>
    public Guid? EntityId { get; set; }

    /// <summary>The <c>version</c> of the draft the client last saw (the one it got from the last save or read); 0 when it has none yet.</summary>
    [Range(0, int.MaxValue, ErrorMessage = "WizardDraftClientVersionInvalid")]
    public int ClientVersion { get; set; }

    /// <summary>
    /// The typed data, a JSON object. Not model-validated: the fields of a draft are the wizard's own, and the personal ones (name, e-mail,
    /// phone, document...) are split off and stored encrypted.
    /// </summary>
    [ValidateNever]
    public JsonDocument? Data { get; set; }
}

/// <summary>
/// A draft as the client reads it: <c>data</c> is the typed data put together again (the plain half with the decrypted personal fields),
/// <c>version</c> is what the client sends back as <c>clientVersion</c>, <c>lastSavedAt</c> (UTC) is the "saved at" of the screen.
/// </summary>
public sealed record WizardDraftResponse(WizardType Type, Guid? EntityId, int Version, JsonElement Data, DateTime LastSavedAt)
{
    public static WizardDraftResponse From(WizardDraftDto draft) =>
        new(draft.Type, draft.EntityId, draft.Version, draft.Data.RootElement, draft.LastSavedAt);
}

/// <summary>
/// What the 409 <c>draft_conflict</c> carries next to <c>code</c> and <c>detail</c>, as members of the problem: the draft that is on the
/// server (the screen offers to keep the client's copy or to take this one) and its version. <c>serverDraft</c> is <c>null</c> only when
/// there is none left to show.
/// </summary>
public sealed record WizardDraftConflictResponse(WizardDraftResponse? ServerDraft, int ServerVersion);
