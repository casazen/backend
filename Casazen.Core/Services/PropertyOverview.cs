using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The one state of a short-stay property in the host's list (SR-07a): derived from the compliance status and the pause the
/// host set, never stored. The order of the checks is the one the frontend has always shown (<c>propertyStatus</c>): a
/// suspended property first, since it needs fixing, then the pause, then the activation.
/// </summary>
public enum PropertyLifecycleStatus
{
    NeedsAttention = 0,

    Active = 1,

    Paused = 2,

    Suspended = 3,
}

public enum PropertySiteVisibility
{
    Published = 0,

    Hidden = 1,

    NotReady = 2,
}

public static class PropertyMissingCodes
{
    public const string BaseData = "base_data";

    public const string Cin = "cin";

    public const string Documents = "documents";

    public const string Safety = "safety";

    public const string ActivationNotConfirmed = ComplianceMissingCodes.ActivationNotConfirmed;

    public const string Photos = "photos";

    public const string Calendar = "calendar";
}

public sealed record PropertyMissingItem(string Code, bool Blocking);

/// <summary>
/// What the host's list shows of a short-stay property besides its record (SR-07a, <c>GET /api/properties?view=overview</c>),
/// worked out for a whole page of properties at once. A property in long-term mode has only the photos: the state, the
/// activation and the site are short-stay concepts.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="LifecycleStatus">The one state; null for a property in long-term mode.</param>
/// <param name="Missing">What it lacks, blockers first (activation order) and then the advice; empty = nothing; null for a property in long-term mode.</param>
/// <param name="Occupancy30">Percent of the nights of the last 30 days taken (the Home's rule); null unless <see cref="PropertyLifecycleStatus.Active"/>, or when nothing was available.</param>
/// <param name="PhotoCount">Photos in the gallery.</param>
/// <param name="CoverPhotoUrl">The first photo, the cover; null without photos.</param>
/// <param name="SiteVisibility">Where it stands on the booking site; null for a property in long-term mode.</param>
/// <param name="SuspensionReasons">Why a suspended property was suspended (the blocker codes); null unless <see cref="PropertyLifecycleStatus.Suspended"/>.</param>
/// <param name="ScheduledModeChange">
/// The change of rental mode waiting for its day (PM-02, AM-13b), for a property in either mode; null when none is scheduled. Only a
/// <see cref="PropertyModeChangeStatus.Scheduled"/> one: an applied, cancelled or failed change is history, not news.
/// </param>
public sealed record PropertyOverview(
    Guid PropertyId,
    PropertyLifecycleStatus? LifecycleStatus,
    IReadOnlyList<PropertyMissingItem>? Missing,
    int? Occupancy30,
    int PhotoCount,
    string? CoverPhotoUrl,
    PropertySiteVisibility? SiteVisibility,
    IReadOnlyList<string>? SuspensionReasons,
    PropertyModeChange? ScheduledModeChange = null);

public static class PropertyLifecycle
{
    public const int RecommendedPhotoCount = 5;

    public static PropertyLifecycleStatus StatusOf(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (property.ComplianceStatus == PropertyComplianceStatus.Suspended)
            return PropertyLifecycleStatus.Suspended;
        if (property.IsPaused)
            return PropertyLifecycleStatus.Paused;
        return property.ComplianceStatus == PropertyComplianceStatus.Active
            ? PropertyLifecycleStatus.Active
            : PropertyLifecycleStatus.NeedsAttention;
    }

    public static PropertySiteVisibility VisibilityOf(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (PublicListing.Includes(property))
            return PropertySiteVisibility.Published;

        var ready = property.RentalMode == RentalMode.Short
            && property.IsActive
            && property.ComplianceStatus == PropertyComplianceStatus.Active;
        return ready ? PropertySiteVisibility.Hidden : PropertySiteVisibility.NotReady;
    }

    /// <summary>
    /// What a short-stay property lacks, from the compliance steps of its activation (<paramref name="blockingSteps"/>, the very
    /// ones <c>GET /api/properties/{id}/compliance/activation</c> shows) and the two pieces of advice: the blockers in the order
    /// of the wizard (<see cref="PropertyMissingCodes.BaseData"/>, <see cref="PropertyMissingCodes.Cin"/>), then — for a property
    /// that is not activated yet and has no blocker left — the confirmation, then the reminders still open
    /// (<see cref="PropertyMissingCodes.Documents"/>, <see cref="PropertyMissingCodes.Safety"/>: they do not block since the PO
    /// decision of 2026-11-01, an active property keeps them in its list), then the photos and the calendar.
    /// </summary>
    public static IReadOnlyList<PropertyMissingItem> MissingOf(
        Property property,
        IEnumerable<ComplianceActivationStep> blockingSteps,
        bool hasCalendarFeed)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(blockingSteps);

        var steps = blockingSteps.ToList();
        var missing = steps
            .Where(step => step.Blocker && step.Status != "complete")
            .Select(step => new PropertyMissingItem(CodeOf(step.Id), Blocking: true))
            .ToList();

        if (missing.Count == 0 && property.ComplianceStatus != PropertyComplianceStatus.Active)
            missing.Add(new PropertyMissingItem(PropertyMissingCodes.ActivationNotConfirmed, Blocking: true));

        missing.AddRange(steps
            .Where(step => !step.Blocker && step.Status != "complete")
            .Select(step => new PropertyMissingItem(CodeOf(step.Id), Blocking: false)));

        if (property.PhotoUrls.Count < RecommendedPhotoCount)
            missing.Add(new PropertyMissingItem(PropertyMissingCodes.Photos, Blocking: false));
        if (!hasCalendarFeed)
            missing.Add(new PropertyMissingItem(PropertyMissingCodes.Calendar, Blocking: false));

        return missing;
    }

    private static string CodeOf(string stepId) => stepId.Replace('-', '_');
}

public interface IPropertyOverviewService
{
    Task<IReadOnlyList<PropertyOverview>> DescribeAsync(
        IReadOnlyList<Property> properties,
        CancellationToken cancellationToken = default);
}
