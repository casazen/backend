using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Casazen.Web.DTOs.Wizards;

public sealed class SaveWizardDraftRequest
{
    public WizardType? Type { get; set; }

    public Guid? EntityId { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "WizardDraftClientVersionInvalid")]
    public int ClientVersion { get; set; }

    [ValidateNever]
    public JsonDocument? Data { get; set; }
}

public sealed record WizardDraftResponse(WizardType Type, Guid? EntityId, int Version, JsonElement Data, DateTime LastSavedAt)
{
    public static WizardDraftResponse From(WizardDraftDto draft) =>
        new(draft.Type, draft.EntityId, draft.Version, draft.Data.RootElement, draft.LastSavedAt);
}

public sealed record WizardDraftConflictResponse(WizardDraftResponse? ServerDraft, int ServerVersion);
