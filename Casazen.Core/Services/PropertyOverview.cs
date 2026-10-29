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
    /// <summary>The activation is not complete (<see cref="PropertyComplianceStatus.Pending"/>): the host still has to finish it.</summary>
    NeedsAttention = 0,

    /// <summary>Activated and not paused.</summary>
    Active = 1,

    /// <summary>Paused by the host (PC-03): reversible, the slot and the history are kept.</summary>
    Paused = 2,

    /// <summary>A requirement was lost after the activation (<see cref="PropertyComplianceStatus.Suspended"/>, CO-06): the reasons come with it.</summary>
    Suspended = 3,
}

/// <summary>Where a short-stay property stands on the booking site (SR-D24): one word for the list, derived, never stored.</summary>
public enum PropertySiteVisibility
{
    /// <summary>On the site: <see cref="PublicListing.IsPublished"/>.</summary>
    Published = 0,

    /// <summary>Ready (activated) but not shown: the host paused it or switched «Mostra la casa sul sito» off.</summary>
    Hidden = 1,

    /// <summary>Not ready (activation pending, suspended or deactivated): it cannot be on the site yet.</summary>
    NotReady = 2,
}

/// <summary>The stable codes of what a property still lacks (<see cref="PropertyMissingItem"/>): part of the API contract, never rename one.</summary>
public static class PropertyMissingCodes
{
    /// <summary>Name, address, city, guests or nightly rate: the <c>base-data</c> step of the activation.</summary>
    public const string BaseData = "base_data";

    /// <summary>The CIN is missing or not valid: the <c>cin</c> step.</summary>
    public const string Cin = "cin";

    /// <summary>A document the region requires is not uploaded: the <c>documents</c> step.</summary>
    public const string Documents = "documents";

    /// <summary>The D.L. 145/2023 safety checklist is incomplete: the <c>safety</c> step.</summary>
    public const string Safety = "safety";

    /// <summary>
    /// Every requirement is met and only the host's confirmation is left (the terms): the same code the compliance cockpit uses
    /// (<see cref="ComplianceMissingCodes.ActivationNotConfirmed"/>).
    /// </summary>
    public const string ActivationNotConfirmed = ComplianceMissingCodes.ActivationNotConfirmed;

    /// <summary>Fewer photos than <see cref="PropertyLifecycle.RecommendedPhotoCount"/>: advice, never a blocker (D31).</summary>
    public const string Photos = "photos";

    /// <summary>No calendar feed (iCal) is connected: advice, never a blocker.</summary>
    public const string Calendar = "calendar";
}

/// <summary>One thing a property still lacks: a stable <see cref="Code"/> and whether it <see cref="Blocking">blocks</see> the activation.</summary>
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
public sealed record PropertyOverview(
    Guid PropertyId,
    PropertyLifecycleStatus? LifecycleStatus,
    IReadOnlyList<PropertyMissingItem>? Missing,
    int? Occupancy30,
    int PhotoCount,
    string? CoverPhotoUrl,
    PropertySiteVisibility? SiteVisibility,
    IReadOnlyList<string>? SuspensionReasons);

/// <summary>
/// The pure rules behind <see cref="PropertyOverview"/>: the state, the site visibility and the list of what is missing. No
/// database: the service that fills the list reads the facts once for the whole page and applies these.
/// </summary>
public static class PropertyLifecycle
{
    /// <summary>Photos a listing is advised to have (D31: advised, not required to activate).</summary>
    public const int RecommendedPhotoCount = 5;

    /// <summary>The state of a short-stay property.</summary>
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

    /// <summary>
    /// Where a short-stay property stands on the site. <see cref="PropertySiteVisibility.Published"/> is exactly
    /// <see cref="PublicListing.IsPublished"/>; a property that would be published but for the pause or the host's switch is
    /// <see cref="PropertySiteVisibility.Hidden"/>; any other is <see cref="PropertySiteVisibility.NotReady"/>.
    /// </summary>
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
    /// What a short-stay property lacks, from the blocking steps of its activation (<paramref name="blockingSteps"/>, the very
    /// ones <c>GET /api/properties/{id}/compliance/activation</c> shows) and the two pieces of advice: the blockers in the order
    /// of the wizard (<see cref="PropertyMissingCodes.BaseData"/>, <see cref="PropertyMissingCodes.Cin"/>,
    /// <see cref="PropertyMissingCodes.Documents"/>, <see cref="PropertyMissingCodes.Safety"/>), then — for a property that is not
    /// activated yet and has no blocker left — the confirmation, then the photos and the calendar.
    /// </summary>
    public static IReadOnlyList<PropertyMissingItem> MissingOf(
        Property property,
        IEnumerable<ComplianceActivationStep> blockingSteps,
        bool hasCalendarFeed)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(blockingSteps);

        var missing = blockingSteps
            .Where(step => step.Blocker && step.Status != "complete")
            .Select(step => new PropertyMissingItem(CodeOf(step.Id), Blocking: true))
            .ToList();

        if (missing.Count == 0 && property.ComplianceStatus != PropertyComplianceStatus.Active)
            missing.Add(new PropertyMissingItem(PropertyMissingCodes.ActivationNotConfirmed, Blocking: true));

        if (property.PhotoUrls.Count < RecommendedPhotoCount)
            missing.Add(new PropertyMissingItem(PropertyMissingCodes.Photos, Blocking: false));
        if (!hasCalendarFeed)
            missing.Add(new PropertyMissingItem(PropertyMissingCodes.Calendar, Blocking: false));

        return missing;
    }

    /// <summary>The code of a blocking step: its id with underscores, so <c>base-data</c> is <see cref="PropertyMissingCodes.BaseData"/>.</summary>
    private static string CodeOf(string stepId) => stepId.Replace('-', '_');
}

/// <summary>Works out the <see cref="PropertyOverview"/> of the properties of the host's list.</summary>
public interface IPropertyOverviewService
{
    /// <summary>
    /// The overview of each of <paramref name="properties"/>, in the same order. The properties are the ones the caller's scope
    /// already reached (the list reads them under <see cref="Authorization.HostScope"/>): nothing here widens it. A fixed number
    /// of queries whatever the number of properties: the activation facts of all of them, the calendar feeds, the nights.
    /// </summary>
    Task<IReadOnlyList<PropertyOverview>> DescribeAsync(
        IReadOnlyList<Property> properties,
        CancellationToken cancellationToken = default);
}
