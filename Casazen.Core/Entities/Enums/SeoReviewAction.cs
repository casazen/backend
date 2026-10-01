namespace Casazen.Core.Entities.Enums;

/// <summary>Action recorded in the review audit of an SEO page (<see cref="Casazen.Core.Entities.SeoContentReviewEvent"/>). Stored as a string.</summary>
public enum SeoReviewAction
{
    /// <summary>A revision was approved and is now the published one.</summary>
    Approved = 0,

    /// <summary>The published revision was withdrawn: the page is back to draft and no longer public.</summary>
    Withdrawn = 1,
}
