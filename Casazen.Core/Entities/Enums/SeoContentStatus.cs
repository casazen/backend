namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a stored SEO revision holds (SE-01, A8-06). Only <see cref="Generated"/> can be approved and published; every
/// other value is an explicit "content not generated" state kept as a draft, so the admin sees why the page has no text.
/// Stored as a string.
/// </summary>
public enum SeoContentStatus
{
    /// <summary>Text of a configured AI provider that passed the output checks (HTML allowlist, structure, length).</summary>
    Generated = 0,

    /// <summary>No external AI provider is configured (<c>Ai:Provider</c> Stub, or DeepSeek without key): no text.</summary>
    AiProviderNotConfigured = 1,

    /// <summary>The provider answered with an empty text (or only markup).</summary>
    EmptyOutput = 2,

    /// <summary>The provider answered, but the text failed the checks (markdown, missing sections, too short).</summary>
    InvalidOutput = 3,

    /// <summary>Template text of the old stub provider, found and withdrawn by the SE-01 migration.</summary>
    Placeholder = 4,
}
