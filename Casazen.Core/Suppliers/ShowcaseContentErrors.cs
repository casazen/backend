using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the errors of
/// the showcase content (SP-13a). 409 <see cref="VersionConflict"/> and <see cref="UndoUnavailable"/>, 422 every other one. The
/// codes are snake_case and never renamed: the editor branches on them.
/// </summary>
public static class ShowcaseContentErrors
{
    /// <summary>
    /// 422: a value of the content is not valid. The response has <c>fields</c>: the JSON paths at fault
    /// (<c>tagline</c>, <c>faq[2].a</c>, <c>color</c>), and, when the color is the one at fault because white text cannot be read
    /// on it, <c>suggestedColor</c>: the same hue, darker.
    /// </summary>
    public const string Invalid = "showcase_content_invalid";

    /// <summary>409: the content was changed since the client read it (another tab, the wizard, another member): reload it.</summary>
    public const string VersionConflict = "showcase_version_conflict";

    public const string NotPublishable = "showcase_not_publishable";

    public const string SupplierNotActive = "showcase_supplier_not_active";

    public const string NotPublished = "showcase_not_published";

    /// <summary>409: the last publication can no longer be undone (the time has passed, or something else was changed since).</summary>
    public const string UndoUnavailable = "showcase_undo_unavailable";

    public const string AiUnavailable = "showcase_ai_unavailable";

    public const string SuggestionNotEnoughData = "showcase_suggestion_not_enough_data";

    public const string SuggestionUnusable = "showcase_suggestion_unusable";

    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        "ShowcaseContentInvalid",
        "ShowcaseVersionConflict",
        "ShowcaseNotPublishable",
        "ShowcaseSupplierNotActive",
        "ShowcaseNotPublished",
        "ShowcaseUndoUnavailable",
        "ShowcaseAiUnavailable",
        "ShowcaseSuggestionNotEnoughData",
        "ShowcaseSuggestionUnusable",
    ];

    public static DomainRuleException InvalidFields(IReadOnlyList<string> fields, string? suggestedColor = null)
    {
        var extensions = new Dictionary<string, object?> { ["fields"] = fields };
        if (suggestedColor is not null)
            extensions["suggestedColor"] = suggestedColor;

        return new DomainRuleException(Invalid, "ShowcaseContentInvalid", string.Join(", ", fields)) { Extensions = extensions };
    }

    public static DomainConflictException Conflict() => new DomainConflictException(VersionConflict, "ShowcaseVersionConflict");

    public static DomainRuleException NotPublishableItems(IReadOnlyList<string> missing) =>
        new DomainRuleException(NotPublishable, "ShowcaseNotPublishable", string.Join(", ", missing))
        {
            Extensions = new Dictionary<string, object?> { ["fields"] = missing },
        };

    public static DomainRuleException NotActive() => new DomainRuleException(SupplierNotActive, "ShowcaseSupplierNotActive");

    public static DomainRuleException NothingPublished() => new DomainRuleException(NotPublished, "ShowcaseNotPublished");

    public static DomainConflictException NothingToUndo() => new DomainConflictException(UndoUnavailable, "ShowcaseUndoUnavailable");

    public static DomainRuleException AiNotAvailable() => new DomainRuleException(AiUnavailable, "ShowcaseAiUnavailable");

    public static DomainRuleException NotEnoughData() => new DomainRuleException(SuggestionNotEnoughData, "ShowcaseSuggestionNotEnoughData");

    public static DomainRuleException UnusableSuggestion() => new DomainRuleException(SuggestionUnusable, "ShowcaseSuggestionUnusable");
}
