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

    /// <summary>
    /// 422: the showcase cannot be published yet. The response has <c>fields</c>: the codes of the required items of the readiness that
    /// are still to do (<see cref="ShowcaseReadiness.Services"/>, <see cref="ShowcaseReadiness.Zones"/>, <see cref="ShowcaseReadiness.Color"/>).
    /// </summary>
    public const string NotPublishable = "showcase_not_publishable";

    /// <summary>422: only an active supplier can publish or reopen its showcase.</summary>
    public const string SupplierNotActive = "showcase_supplier_not_active";

    /// <summary>422: nothing is published yet, so there is nothing to pause or to reopen.</summary>
    public const string NotPublished = "showcase_not_published";

    /// <summary>409: the last publication can no longer be undone (the time has passed, or something else was changed since).</summary>
    public const string UndoUnavailable = "showcase_undo_unavailable";

    /// <summary>422: the AI help cannot answer now (no provider is configured, or the provider failed): the editor keeps the static starter.</summary>
    public const string AiUnavailable = "showcase_ai_unavailable";

    /// <summary>
    /// 422: there is nothing true to write from yet (no category, no zone, no service and no answer of the supplier): the editor asks for one
    /// of them before it asks the AI again.
    /// </summary>
    public const string SuggestionNotEnoughData = "showcase_suggestion_not_enough_data";

    /// <summary>
    /// 422: the AI wrote something, but every member was dropped by the checks (over its limit, or a claim the supplier did not make): the
    /// supplier asks again or writes the text.
    /// </summary>
    public const string SuggestionUnusable = "showcase_suggestion_unusable";

    /// <summary>The <c>SharedResources</c> keys the content uses (Italian and English).</summary>
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

    /// <summary>The 422 of one or more invalid values; <paramref name="suggestedColor"/> when the color is unreadable with white text.</summary>
    public static DomainRuleException InvalidFields(IReadOnlyList<string> fields, string? suggestedColor = null)
    {
        var extensions = new Dictionary<string, object?> { ["fields"] = fields };
        if (suggestedColor is not null)
            extensions["suggestedColor"] = suggestedColor;

        return new DomainRuleException(Invalid, "ShowcaseContentInvalid", string.Join(", ", fields)) { Extensions = extensions };
    }

    /// <summary>The 409 of a change made on a stale copy.</summary>
    public static DomainConflictException Conflict() => new DomainConflictException(VersionConflict, "ShowcaseVersionConflict");

    /// <summary>The 422 of a publication that lacks something required; <paramref name="missing"/> are the codes of the readiness items.</summary>
    public static DomainRuleException NotPublishableItems(IReadOnlyList<string> missing) =>
        new DomainRuleException(NotPublishable, "ShowcaseNotPublishable", string.Join(", ", missing))
        {
            Extensions = new Dictionary<string, object?> { ["fields"] = missing },
        };

    /// <summary>The 422 of a publication or a reopening by a supplier that is not active.</summary>
    public static DomainRuleException NotActive() => new DomainRuleException(SupplierNotActive, "ShowcaseSupplierNotActive");

    /// <summary>The 422 of a pause or a reopening while nothing is published.</summary>
    public static DomainRuleException NothingPublished() => new DomainRuleException(NotPublished, "ShowcaseNotPublished");

    /// <summary>The 409 of an undo that is no longer possible.</summary>
    public static DomainConflictException NothingToUndo() => new DomainConflictException(UndoUnavailable, "ShowcaseUndoUnavailable");

    /// <summary>The 422 of an AI help that cannot answer now.</summary>
    public static DomainRuleException AiNotAvailable() => new DomainRuleException(AiUnavailable, "ShowcaseAiUnavailable");

    /// <summary>The 422 of a suggestion asked for a supplier that has nothing true to write from.</summary>
    public static DomainRuleException NotEnoughData() => new DomainRuleException(SuggestionNotEnoughData, "ShowcaseSuggestionNotEnoughData");

    /// <summary>The 422 of a suggestion whose every member was dropped by the checks.</summary>
    public static DomainRuleException UnusableSuggestion() => new DomainRuleException(SuggestionUnusable, "ShowcaseSuggestionUnusable");
}
