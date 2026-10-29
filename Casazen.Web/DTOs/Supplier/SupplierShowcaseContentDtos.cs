using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Casazen.Web.DTOs.Supplier;

// The content of the supplier's showcase (SP-13a). The document itself (ShowcaseContentDocument, schemaVersion 1) is the contract
// between this API and the editor: plain text, no contact data. The members below are the editor's: the state, the version to send
// back, what a publication would change and the public address.

/// <summary>
/// Body of <c>PATCH api/supplier/showcase/content</c>: the <c>version</c> the editor read and, next to it, the members of the content to
/// change, as a JSON merge patch (RFC 7396): a member that is there replaces the stored one, an object is merged member by member, a list
/// replaces the whole list, <c>null</c> clears the member. A member that is not in the contract is a 422 on that member.
/// </summary>
public class SaveShowcaseContentRequest
{
    /// <summary>The version of the last answer (<c>0</c> before the first save). Another one is a 409 <c>showcase_version_conflict</c>.</summary>
    [Required]
    public uint? Version { get; set; }

    /// <summary>Every other member of the body: the change to apply.</summary>
    [JsonExtensionData]
    [ValidateNever]
    public Dictionary<string, JsonElement>? Content { get; set; }

    /// <summary>The change as a JSON object, ready to be merged into the draft.</summary>
    public JsonObject ToPatch()
    {
        var patch = new JsonObject();
        if (Content is null)
            return patch;

        foreach (var (name, value) in Content)
            patch[name] = value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(value.GetRawText());

        return patch;
    }
}

/// <summary>
/// Optional body of the commands (<c>discard</c>, and the ones that follow): the <c>version</c> the editor read. When it is there it must
/// be the current one, so a command never acts on a draft the editor has not seen.
/// </summary>
public class ShowcaseCommandRequest
{
    public uint? Version { get; set; }
}

/// <summary>
/// The content of the showcase as the editor reads it: the draft, what is published, the state, what a publication would change, and where
/// the showcase is.
/// </summary>
public class ShowcaseContentResponse
{
    /// <summary>The format of <see cref="Draft"/> and <see cref="Published"/> (<see cref="ShowcaseContentLimits.SchemaVersion"/>).</summary>
    public int SchemaVersion { get; set; } = ShowcaseContentLimits.SchemaVersion;

    /// <summary><c>Draft</c> (nothing published), <c>Online</c>, <c>ChangesReady</c> (published, the draft has changes) or <c>Paused</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Send it back with the next save. <c>0</c> while the supplier never saved anything.</summary>
    public uint Version { get; set; }

    /// <summary>What the supplier is writing.</summary>
    public ShowcaseContentDocument Draft { get; set; } = new();

    /// <summary>What the customers read; <c>null</c> until the first publication.</summary>
    public ShowcaseContentDocument? Published { get; set; }

    /// <summary>The members of the contract in which the draft differs from what is published ("cosa cambia pubblicando").</summary>
    public IReadOnlyList<string> Changes { get; set; } = [];

    public DateTime? SavedAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? PausedAt { get; set; }

    /// <summary>Until when the last publication can be undone; <c>null</c> when it cannot.</summary>
    public DateTime? UndoUntil { get; set; }

    /// <summary>The address of the showcase on the web app (<c>/fornitori/{slug}</c>); <c>null</c> while the supplier has none (never activated).</summary>
    public string? PublicPath { get; set; }

    /// <summary>The absolute address (<c>App:PublicSiteBaseUrl</c> + path); <c>null</c> when there is no path or the public URL is not configured.</summary>
    public string? PublicUrl { get; set; }
}

/// <summary>Maps the content of the showcase between the service and the API.</summary>
public static class ShowcaseContentMapper
{
    public static ShowcaseContentResponse ToResponse(ShowcaseContentView view, string? publicPath, string? publicUrl) => new()
    {
        State = view.State.ToString(),
        Version = view.Version,
        Draft = view.Draft,
        Published = view.Published,
        Changes = view.Changes,
        SavedAt = view.SavedAt,
        PublishedAt = view.PublishedAt,
        PausedAt = view.PausedAt,
        UndoUntil = view.UndoUntil,
        PublicPath = publicPath,
        PublicUrl = publicUrl,
    };
}
