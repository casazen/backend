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
/// Optional body of the commands (<c>discard</c> and <c>publish</c>): the <c>version</c> the editor read. When it is there it must
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

    /// <summary>What the supplier must and should have before it publishes: the editor's "what is missing" list.</summary>
    public ShowcaseReadinessDto Readiness { get; set; } = new();

    /// <summary>Whether the showcase takes bookings now and, if not, why.</summary>
    public ShowcaseBookingsDto Bookings { get; set; } = new();

    /// <summary>The address of the showcase on the web app (<c>/fornitori/{slug}</c>); <c>null</c> while the supplier has none (never activated).</summary>
    public string? PublicPath { get; set; }

    /// <summary>The absolute address (<c>App:PublicSiteBaseUrl</c> + path); <c>null</c> when there is no path or the public URL is not configured.</summary>
    public string? PublicUrl { get; set; }
}

/// <summary>One line of the readiness: a stable <c>code</c> the editor writes the text for, how it stands, and where to fix it.</summary>
public class ShowcaseReadinessItemDto
{
    /// <summary><c>services</c>, <c>zones</c>, <c>color</c> (required) and <c>hours</c>, <c>presentation</c>, <c>photos</c>, <c>faq</c>, <c>verified</c> (advised).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary><c>ok</c> (done), <c>warn</c> (advised, the showcase can be published without it) or <c>todo</c> (required: it cannot).</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>True for the items a publication needs.</summary>
    public bool Required { get; set; }

    /// <summary>The page of the web app where the supplier fixes it (a path, with <c>?step=</c> for the editor's own steps).</summary>
    public string Target { get; set; } = string.Empty;
}

/// <summary>What the supplier must and should have before it publishes its showcase.</summary>
public class ShowcaseReadinessDto
{
    /// <summary>No required item is still to do. The server judges it again at every publication.</summary>
    public bool CanPublish { get; set; }

    public IReadOnlyList<ShowcaseReadinessItemDto> Items { get; set; } = [];
}

/// <summary>Whether the showcase takes bookings, and if not why.</summary>
public class ShowcaseBookingsDto
{
    /// <summary>Published, not paused, the payments on, the account ready and the switch on.</summary>
    public bool Open { get; set; }

    /// <summary>
    /// Why not, in a stable order: <c>not_published</c>, <c>paused</c>, <c>payments_off</c>, <c>payments_account_not_ready</c>, and
    /// <c>bookings_off</c> (everything is ready but the switch: "Riattiva" turns it on). Empty when the bookings are open.
    /// </summary>
    public IReadOnlyList<string> BlockedBy { get; set; } = [];
}

/// <summary>
/// The short status of the showcase for the home and the checklist of the console (<c>GET api/supplier/showcase/status</c>): a few facts, no
/// document.
/// </summary>
public class ShowcaseStatusDto
{
    /// <summary>Something is published (even if it is paused).</summary>
    public bool Published { get; set; }

    /// <summary>The draft has changes a publication would publish (a draft that was never published has them as soon as it holds something).</summary>
    public bool HasDraft { get; set; }

    public bool Paused { get; set; }

    /// <summary>The absolute address of the showcase; <c>null</c> while the supplier has none (never activated) or the public URL is not configured.</summary>
    public string? Url { get; set; }

    /// <summary>The requests that came from the showcase in the last 30 days.</summary>
    public int Requests30d { get; set; }

    /// <summary>Of those, how many still wait for the supplier's answer.</summary>
    public int Pending30d { get; set; }

    /// <summary>The members of the contract in which the draft differs from what is published.</summary>
    public IReadOnlyList<string> Changes { get; set; } = [];

    public DateTime? PublishedAt { get; set; }

    public DateTime? SavedAt { get; set; }

    /// <summary>0 to 100: the share of the readiness items that are done.</summary>
    public int Completeness { get; set; }
}

/// <summary>The two true numbers of the showcase editor (<c>GET api/supplier/showcase/stats</c>).</summary>
public class ShowcaseStatsDto
{
    /// <summary>The period: the last <see cref="Days"/> days (30).</summary>
    public int Days { get; set; }

    public int Requests { get; set; }

    public int Pending { get; set; }
}

/// <summary>Maps the content of the showcase between the service and the API.</summary>/// <summary>Maps the content of the showcase between the service and the API.</summary>
public static class ShowcaseContentMapper
{
    public static ShowcaseStatusDto ToStatus(ShowcaseContentView view, ShowcaseRequestStats stats, string? url)
    {
        var items = view.Readiness.Items;
        return new ShowcaseStatusDto
        {
            Published = view.Published is not null,
            HasDraft = view.Changes.Count > 0,
            Paused = view.PausedAt is not null,
            Url = url,
            Requests30d = stats.Requests,
            Pending30d = stats.Pending,
            Changes = view.Changes,
            PublishedAt = view.PublishedAt,
            SavedAt = view.SavedAt,
            Completeness = items.Count == 0 ? 0 : (int)Math.Round(100.0 * items.Count(item => item.Level == ShowcaseReadinessLevel.Ok) / items.Count),
        };
    }

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
        Readiness = new ShowcaseReadinessDto
        {
            CanPublish = view.Readiness.CanPublish,
            Items = view.Readiness.Items
                .Select(item => new ShowcaseReadinessItemDto
                {
                    Code = item.Code,
                    Level = item.Level.ToString().ToLowerInvariant(),
                    Required = item.Required,
                    Target = item.Target,
                })
                .ToList(),
        },
        Bookings = new ShowcaseBookingsDto { Open = view.Bookings.Open, BlockedBy = view.Bookings.BlockedBy },
        PublicPath = publicPath,
        PublicUrl = publicUrl,
    };
}
