using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Casazen.Web.DTOs.Supplier;

public class SaveShowcaseContentRequest
{
    [Required]
    public uint? Version { get; set; }

    [JsonExtensionData]
    [ValidateNever]
    public Dictionary<string, JsonElement>? Content { get; set; }

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

public class ShowcaseCommandRequest
{
    public uint? Version { get; set; }
}

public class ShowcaseContentResponse
{
    public int SchemaVersion { get; set; } = ShowcaseContentLimits.SchemaVersion;

    public string State { get; set; } = string.Empty;

    public uint Version { get; set; }

    public ShowcaseContentDocument Draft { get; set; } = new();

    public ShowcaseContentDocument? Published { get; set; }

    public IReadOnlyList<string> Changes { get; set; } = [];

    public DateTime? SavedAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? PausedAt { get; set; }

    public DateTime? UndoUntil { get; set; }

    public ShowcaseReadinessDto Readiness { get; set; } = new();

    public ShowcaseBookingsDto Bookings { get; set; } = new();

    public string? PublicPath { get; set; }

    public string? PublicUrl { get; set; }
}

public class ShowcaseReadinessItemDto
{
    public string Code { get; set; } = string.Empty;

    public string Level { get; set; } = string.Empty;

    public bool Required { get; set; }

    public string Target { get; set; } = string.Empty;
}

public class ShowcaseReadinessDto
{
    public bool CanPublish { get; set; }

    public IReadOnlyList<ShowcaseReadinessItemDto> Items { get; set; } = [];
}

public class ShowcaseBookingsDto
{
    public bool Open { get; set; }

    public IReadOnlyList<string> BlockedBy { get; set; } = [];
}

public class ShowcaseStatusDto
{
    /// <summary>Something is published (even if it is paused).</summary>
    public bool Published { get; set; }

    public bool HasDraft { get; set; }

    public bool Paused { get; set; }

    public string? Url { get; set; }

    public int Requests30d { get; set; }

    public int Pending30d { get; set; }

    public IReadOnlyList<string> Changes { get; set; } = [];

    public DateTime? PublishedAt { get; set; }

    public DateTime? SavedAt { get; set; }

    public int Completeness { get; set; }
}

public class ShowcaseStatsDto
{
    public int Days { get; set; }

    public int Requests { get; set; }

    public int Pending { get; set; }
}

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
