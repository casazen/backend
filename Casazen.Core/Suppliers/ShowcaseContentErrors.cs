using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the errors of
/// the showcase content (SP-13a). 409 <see cref="VersionConflict"/>, 422 every other one. The codes are snake_case and never
/// renamed: the editor branches on them.
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

    /// <summary>The <c>SharedResources</c> keys the content uses (Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        "ShowcaseContentInvalid",
        "ShowcaseVersionConflict",
    ];

    /// <summary>The 422 of one or more invalid values; <paramref name="suggestedColor"/> when the color is unreadable with white text.</summary>
    public static DomainRuleException InvalidFields(IReadOnlyList<string> fields, string? suggestedColor = null)
    {
        var extensions = new Dictionary<string, object?> { ["fields"] = fields };
        if (suggestedColor is not null)
            extensions["suggestedColor"] = suggestedColor;

        return new DomainRuleException(Invalid, "ShowcaseContentInvalid", string.Join(", ", fields)) { Extensions = extensions };
    }

    /// <summary>The 409 of a save made on a stale copy.</summary>
    public static DomainConflictException Conflict() => new DomainConflictException(VersionConflict, "ShowcaseVersionConflict");
}
