using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-07a: the host's switch «Mostra la casa sul sito» (<see cref="Property.PublishedOnSite"/>) is one more condition of the
/// publication, never a way around the activation; the state and the site visibility of the host's list are the rule of the
/// publication itself; and what the list says is missing is what the activation says, in the order of the wizard.
/// </summary>
public class PropertyOverviewRulesTests
{
    public static TheoryData<RentalMode, PropertyComplianceStatus, bool, bool> EveryCombination()
    {
        var data = new TheoryData<RentalMode, PropertyComplianceStatus, bool, bool>();
        foreach (var mode in Enum.GetValues<RentalMode>())
        {
            foreach (var compliance in Enum.GetValues<PropertyComplianceStatus>())
            {
                foreach (var paused in new[] { false, true })
                {
                    foreach (var shown in new[] { false, true })
                        data.Add(mode, compliance, paused, shown);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void Published_NeedsEveryCondition_TheSwitchOnlyTakesAPropertyOffTheSite(
        RentalMode mode, PropertyComplianceStatus compliance, bool paused, bool shownByHost)
    {
        var property = NewProperty(mode, compliance, paused, shownByHost);
        var ready = mode == RentalMode.Short && compliance == PropertyComplianceStatus.Active;
        var published = ready && !paused && shownByHost;

        // The very same expression, as the database reads it and as the code that holds the entity reads it.
        Assert.Equal(published, PublicListing.IsPublished.Compile()(property));
        Assert.Equal(published, PublicListing.Includes(property));

        // The word of the host's list: published, or ready but kept off by the pause or the switch, or not ready at all.
        var expected = published
            ? PropertySiteVisibility.Published
            : ready ? PropertySiteVisibility.Hidden : PropertySiteVisibility.NotReady;
        Assert.Equal(expected, PropertyLifecycle.VisibilityOf(property));
    }

    [Theory]
    [InlineData(PropertyComplianceStatus.Pending, false, PropertyLifecycleStatus.NeedsAttention)]
    [InlineData(PropertyComplianceStatus.Pending, true, PropertyLifecycleStatus.Paused)]
    [InlineData(PropertyComplianceStatus.Active, false, PropertyLifecycleStatus.Active)]
    [InlineData(PropertyComplianceStatus.Active, true, PropertyLifecycleStatus.Paused)]
    [InlineData(PropertyComplianceStatus.Suspended, false, PropertyLifecycleStatus.Suspended)]
    [InlineData(PropertyComplianceStatus.Suspended, true, PropertyLifecycleStatus.Suspended)]
    public void Status_SuspendedFirst_ThenThePause_ThenTheActivation(
        PropertyComplianceStatus compliance, bool paused, PropertyLifecycleStatus expected)
    {
        var property = NewProperty(RentalMode.Short, compliance, paused, shownByHost: true);

        Assert.Equal(expected, PropertyLifecycle.StatusOf(property));
    }

    [Fact]
    public void Missing_ListsTheBlockersInTheOrderOfTheWizard_ThenTheAdvice()
    {
        var property = NewProperty(RentalMode.Short, PropertyComplianceStatus.Pending, paused: false, shownByHost: true);
        property.PhotoUrls = ["a.jpg", "b.jpg"];
        var steps = new[]
        {
            Step("base-data", complete: false),
            Step("cin", complete: true),
            Step("documents", complete: false),
            Step("safety", complete: false),
        };

        var missing = PropertyLifecycle.MissingOf(property, steps, hasCalendarFeed: false);

        var expected = new[] { ("base_data", true), ("documents", true), ("safety", true), ("photos", false), ("calendar", false) };
        Assert.Equal(expected, missing.Select(m => (m.Code, m.Blocking)).ToArray());
    }

    [Fact]
    public void Missing_AllRequirementsMetButNotActivated_OnlyTheConfirmationIsLeft()
    {
        var property = NewProperty(RentalMode.Short, PropertyComplianceStatus.Pending, paused: false, shownByHost: true);
        property.PhotoUrls = [.. Enumerable.Range(1, PropertyLifecycle.RecommendedPhotoCount).Select(i => $"{i}.jpg")];
        var steps = new[] { Step("base-data", true), Step("cin", true), Step("documents", true), Step("safety", true) };

        var missing = PropertyLifecycle.MissingOf(property, steps, hasCalendarFeed: true);

        var only = Assert.Single(missing);
        Assert.Equal((ComplianceMissingCodes.ActivationNotConfirmed, true), (only.Code, only.Blocking));
    }

    [Fact]
    public void Missing_AnActivatedPropertyWithEverything_LacksNothing()
    {
        var property = NewProperty(RentalMode.Short, PropertyComplianceStatus.Active, paused: false, shownByHost: true);
        property.PhotoUrls = [.. Enumerable.Range(1, PropertyLifecycle.RecommendedPhotoCount).Select(i => $"{i}.jpg")];
        var steps = new[] { Step("base-data", true), Step("cin", true), Step("documents", true), Step("safety", true) };

        Assert.Empty(PropertyLifecycle.MissingOf(property, steps, hasCalendarFeed: true));
    }

    private static Property NewProperty(RentalMode mode, PropertyComplianceStatus compliance, bool paused, bool shownByHost) => new()
    {
        OrgId = Guid.NewGuid(),
        OwnerId = "auth0|owner",
        Name = "Casa",
        Address = "Via Roma 1",
        City = "Como",
        IsActive = true,
        RentalMode = mode,
        ComplianceStatus = compliance,
        IsPaused = paused,
        PublishedOnSite = shownByHost,
    };

    private static ComplianceActivationStep Step(string id, bool complete) =>
        new(id, id, complete ? "complete" : "pending", Blocker: true);
}
