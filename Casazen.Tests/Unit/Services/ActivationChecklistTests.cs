using Casazen.Core.Entities;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The activation checklist (PL-15, A1-37, A3-26, PLG-AC10): every step comes from stored state, and a start (a Connect
/// onboarding begun, a property created) never counts as done.
/// </summary>
public class ActivationChecklistTests
{
    private static OrgEntity ConfiguredOrg(
        string? connectedAccountId = "acct_ready",
        bool chargesEnabled = true,
        bool detailsSubmitted = true,
        string? requirementsDueJson = null,
        bool isActive = true) => new()
    {
        Name = "Casa Rossi",
        Slug = "casa-rossi",
        IsActive = isActive,
        StripeConnectedAccountId = connectedAccountId,
        ConnectChargesEnabled = chargesEnabled,
        ConnectDetailsSubmitted = detailsSubmitted,
        ConnectRequirementsDueJson = requirementsDueJson,
    };

    private static ActivationFacts Facts(
        OrgEntity? org,
        ActivationPropertyFacts? properties = null,
        bool firstBooking = false,
        bool account = true) => new(account, account, account, org, properties ?? ActivationPropertyFacts.None, firstBooking);

    private static ActivationChecklistStep Step(OnboardingActivationStatus status, string key) =>
        Assert.Single(status.Steps, s => s.Key == key);

    private static ActivationPropertyFacts OnePublished() => new(Total: 1, WithValidCin: 1, Published: 1, PausedByHost: 0, ComplianceNotActive: 0);

    [Fact]
    public void Build_ReturnsTheStepsInTheOrderTheHostDoesThem()
    {
        var status = ActivationChecklist.Build(Facts(ConfiguredOrg()));

        Assert.Equal(
            ["account", "organization", "property", "cin", "payments", "sitePublished", "firstBooking"],
            status.Steps.Select(s => s.Key));
    }

    [Fact]
    public void Build_NoOrgYet_EveryStepButTheAccountIsBlockedByTheOnboarding()
    {
        var status = ActivationChecklist.Build(Facts(null, account: false));

        Assert.False(status.Activated);
        Assert.False(status.SitePublished);
        Assert.Equal(ActivationStepStates.Todo, Step(status, "account").State);
        Assert.Equal(ActivationStepReasons.OnboardingIncomplete, Step(status, "account").Reason);
        foreach (var step in status.Steps.Where(s => s.Key != "account"))
        {
            Assert.Equal(ActivationStepStates.Blocked, step.State);
            Assert.Equal(ActivationStepReasons.OnboardingIncomplete, step.Reason);
        }
    }

    // ─── Organization (name and slug) ────────────────────────────────────────────

    [Fact]
    public void Build_OrgWithGeneratedSlug_OrganizationIsTodo()
    {
        var org = ConfiguredOrg();
        org.Slug = "org-abcd2345";

        var step = Step(ActivationChecklist.Build(Facts(org)), "organization");

        Assert.Equal(ActivationStepStates.Todo, step.State);
        Assert.Equal(ActivationStepReasons.OrgProfileIncomplete, step.Reason);
    }

    [Theory]
    [InlineData(OrgEntity.PlaceholderName)]
    [InlineData("  la mia organizzazione  ")]
    [InlineData("   ")]
    public void Build_OrgWithPlaceholderOrBlankName_OrganizationIsTodo(string name)
    {
        var org = ConfiguredOrg();
        org.Name = name;

        var step = Step(ActivationChecklist.Build(Facts(org)), "organization");

        Assert.Equal(ActivationStepStates.Todo, step.State);
    }

    [Fact]
    public void Build_OrgWithChosenNameAndSlug_OrganizationIsDone()
    {
        var step = Step(ActivationChecklist.Build(Facts(ConfiguredOrg())), "organization");

        Assert.Equal(ActivationStepStates.Done, step.State);
        Assert.Null(step.Reason);
    }

    // ─── Property and CIN ────────────────────────────────────────────────────────

    [Fact]
    public void Build_NoProperty_PropertyIsTodoAndCinAndSiteAreBlocked()
    {
        var status = ActivationChecklist.Build(Facts(ConfiguredOrg()));

        Assert.False(status.PropertyCreated);
        Assert.Equal(ActivationStepStates.Todo, Step(status, "property").State);
        Assert.Equal(ActivationStepReasons.NoProperty, Step(status, "property").Reason);
        Assert.Equal(ActivationStepStates.Blocked, Step(status, "cin").State);
        Assert.Equal(ActivationStepStates.Blocked, Step(status, "sitePublished").State);
        Assert.Equal(ActivationStepReasons.NoProperty, Step(status, "sitePublished").Reason);
    }

    [Theory]
    [InlineData(0, 3, ActivationStepStates.Todo)]
    [InlineData(2, 3, ActivationStepStates.InProgress)]
    [InlineData(3, 3, ActivationStepStates.Done)]
    public void Build_CinOnSomePropertiesOnly_IsDoneOnlyWhenEveryPropertyHasAValidOne(int withValidCin, int total, string expectedState)
    {
        var properties = new ActivationPropertyFacts(total, withValidCin, 0, 0, total);

        var step = Step(ActivationChecklist.Build(Facts(ConfiguredOrg(), properties)), "cin");

        Assert.Equal(expectedState, step.State);
        Assert.Equal(withValidCin, step.Done);
        Assert.Equal(total, step.Total);
        Assert.Equal(
            expectedState == ActivationStepStates.Done ? null : ActivationStepReasons.CinMissingOrInvalid,
            step.Reason);
    }

    // ─── Payments (Stripe Connect) ───────────────────────────────────────────────

    [Fact]
    public void Build_NoConnectedAccount_PaymentsIsTodo()
    {
        var org = ConfiguredOrg(connectedAccountId: null, chargesEnabled: false, detailsSubmitted: false);

        var step = Step(ActivationChecklist.Build(Facts(org)), "payments");

        Assert.Equal(ActivationStepStates.Todo, step.State);
        Assert.Equal(ActivationStepReasons.ConnectNotStarted, step.Reason);
    }

    [Fact]
    public void Build_ConnectOnboardingStartedButStripeAsksForMore_PaymentsIsInProgressNotDone()
    {
        var org = ConfiguredOrg(chargesEnabled: false, detailsSubmitted: false, requirementsDueJson: "[\"individual.verification.document\"]");

        var step = Step(ActivationChecklist.Build(Facts(org)), "payments");

        Assert.Equal(ActivationStepStates.InProgress, step.State);
        Assert.Equal(ActivationStepReasons.ConnectRequirementsDue, step.Reason);
    }

    [Fact]
    public void Build_DetailsSubmittedButChargesNotEnabled_PaymentsWaitsForStripeVerification()
    {
        var org = ConfiguredOrg(chargesEnabled: false, detailsSubmitted: true);

        var step = Step(ActivationChecklist.Build(Facts(org)), "payments");

        Assert.Equal(ActivationStepStates.InProgress, step.State);
        Assert.Equal(ActivationStepReasons.ConnectPendingVerification, step.Reason);
    }

    [Fact]
    public void Build_ChargesEnabledWithoutAConnectedAccount_PaymentsIsNotDone()
    {
        // The checkout refuses it too (Org.CanTakeDirectPayments): a flag alone is no evidence.
        var org = ConfiguredOrg(connectedAccountId: null, chargesEnabled: true);

        var step = Step(ActivationChecklist.Build(Facts(org)), "payments");

        Assert.NotEqual(ActivationStepStates.Done, step.State);
    }

    [Fact]
    public void Build_ChargesEnabledOnAConnectedAccount_PaymentsIsDone()
    {
        var step = Step(ActivationChecklist.Build(Facts(ConfiguredOrg())), "payments");

        Assert.Equal(ActivationStepStates.Done, step.State);
        Assert.Null(step.Reason);
    }

    // ─── Site published ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_PublishedPropertyAndChargesEnabled_SiteIsPublished()
    {
        var status = ActivationChecklist.Build(Facts(ConfiguredOrg(), OnePublished()));

        Assert.True(status.SitePublished);
        var step = Step(status, "sitePublished");
        Assert.Equal(ActivationStepStates.Done, step.State);
        Assert.Equal(1, step.Done);
        Assert.Equal(1, step.Total);
    }

    [Fact]
    public void Build_PublishedPropertyButChargesDisabled_SiteIsNotPublished()
    {
        // A3-26: the link exists but every guest would get a 409 at the checkout.
        var org = ConfiguredOrg(chargesEnabled: false);

        var status = ActivationChecklist.Build(Facts(org, OnePublished()));

        Assert.False(status.SitePublished);
        var step = Step(status, "sitePublished");
        Assert.Equal(ActivationStepStates.InProgress, step.State);
        Assert.Equal(ActivationStepReasons.PaymentsNotReady, step.Reason);
    }

    [Fact]
    public void Build_ConnectAccountCreatedButNotVerified_SiteIsNotPublished()
    {
        var org = ConfiguredOrg(chargesEnabled: false, detailsSubmitted: false);

        var status = ActivationChecklist.Build(Facts(org, OnePublished()));

        Assert.False(status.SitePublished);
    }

    [Fact]
    public void Build_OnlyPausedProperties_SiteIsNotPublishedAndSaysTheyArePaused()
    {
        // PC-03: a paused property is hidden from the public site, so it does not make a published site.
        var properties = new ActivationPropertyFacts(Total: 2, WithValidCin: 2, Published: 0, PausedByHost: 2, ComplianceNotActive: 0);

        var status = ActivationChecklist.Build(Facts(ConfiguredOrg(), properties));

        Assert.True(status.PropertyCreated);
        Assert.False(status.SitePublished);
        var step = Step(status, "sitePublished");
        Assert.Equal(ActivationStepStates.Todo, step.State);
        Assert.Equal(ActivationStepReasons.PropertiesPaused, step.Reason);
        Assert.Equal(0, step.Done);
        Assert.Equal(2, step.Total);
    }

    [Fact]
    public void Build_OnePausedAndOnePublished_SiteIsPublished()
    {
        var properties = new ActivationPropertyFacts(Total: 2, WithValidCin: 2, Published: 1, PausedByHost: 1, ComplianceNotActive: 0);

        var status = ActivationChecklist.Build(Facts(ConfiguredOrg(), properties));

        Assert.True(status.SitePublished);
        Assert.Equal(1, Step(status, "sitePublished").Done);
        Assert.Equal(2, Step(status, "sitePublished").Total);
    }

    [Fact]
    public void Build_PropertiesWaitingForTheComplianceActivation_SiteSaysCompliancePending()
    {
        var properties = new ActivationPropertyFacts(Total: 1, WithValidCin: 0, Published: 0, PausedByHost: 0, ComplianceNotActive: 1);

        var step = Step(ActivationChecklist.Build(Facts(ConfiguredOrg(), properties)), "sitePublished");

        Assert.Equal(ActivationStepStates.Todo, step.State);
        Assert.Equal(ActivationStepReasons.CompliancePending, step.Reason);
    }

    [Fact]
    public void Build_PausedAndCompliancePending_SiteAsksToReactivateFirst()
    {
        var properties = new ActivationPropertyFacts(Total: 2, WithValidCin: 1, Published: 0, PausedByHost: 1, ComplianceNotActive: 1);

        var step = Step(ActivationChecklist.Build(Facts(ConfiguredOrg(), properties)), "sitePublished");

        Assert.Equal(ActivationStepReasons.PropertiesPaused, step.Reason);
    }

    [Fact]
    public void Build_OnlyDeactivatedProperties_SiteSaysPropertiesInactive()
    {
        var properties = new ActivationPropertyFacts(Total: 1, WithValidCin: 1, Published: 0, PausedByHost: 0, ComplianceNotActive: 0);

        var step = Step(ActivationChecklist.Build(Facts(ConfiguredOrg(), properties)), "sitePublished");

        Assert.Equal(ActivationStepReasons.PropertiesInactive, step.Reason);
    }

    [Fact]
    public void Build_DisabledOrg_SiteIsBlockedEvenWithAPublishedProperty()
    {
        var status = ActivationChecklist.Build(Facts(ConfiguredOrg(isActive: false), OnePublished()));

        Assert.False(status.SitePublished);
        Assert.Equal(ActivationStepStates.Blocked, Step(status, "sitePublished").State);
        Assert.Equal(ActivationStepReasons.OrgInactive, Step(status, "sitePublished").Reason);
    }

    // ─── First booking and activation ────────────────────────────────────────────

    [Fact]
    public void Build_SiteNotPublished_FirstBookingIsBlocked()
    {
        var step = Step(ActivationChecklist.Build(Facts(ConfiguredOrg(), ActivationPropertyFacts.None)), "firstBooking");

        Assert.Equal(ActivationStepStates.Blocked, step.State);
        Assert.Equal(ActivationStepReasons.SiteNotPublished, step.Reason);
    }

    [Fact]
    public void Build_SitePublishedWithoutBookings_FirstBookingWaitsForAGuest()
    {
        var step = Step(ActivationChecklist.Build(Facts(ConfiguredOrg(), OnePublished())), "firstBooking");

        Assert.Equal(ActivationStepStates.Todo, step.State);
        Assert.Equal(ActivationStepReasons.AwaitingFirstBooking, step.Reason);
    }

    [Fact]
    public void Build_Everything_IsActivatedAndEveryStepIsDone()
    {
        var status = ActivationChecklist.Build(Facts(ConfiguredOrg(), OnePublished(), firstBooking: true));

        Assert.True(status.Activated);
        Assert.True(status.FirstBookingTaken);
        Assert.All(status.Steps, step => Assert.Equal(ActivationStepStates.Done, step.State));
    }

    [Fact]
    public void Build_FirstBookingTakenButSiteNowPaused_IsNotActivatedButKeepsTheFirstBookingDone()
    {
        var properties = new ActivationPropertyFacts(Total: 1, WithValidCin: 1, Published: 0, PausedByHost: 1, ComplianceNotActive: 0);

        var status = ActivationChecklist.Build(Facts(ConfiguredOrg(), properties, firstBooking: true));

        Assert.False(status.Activated);
        Assert.Equal(ActivationStepStates.Done, Step(status, "firstBooking").State);
    }

    [Fact]
    public void Build_PublishedSiteWithTheGeneratedSlug_IsActivatedButOrganizationStaysTodo()
    {
        // The organization and CIN steps guide the host; they do not change what "activated" has always meant.
        var org = ConfiguredOrg();
        org.Slug = "org-abcd2345";

        var status = ActivationChecklist.Build(Facts(org, OnePublished(), firstBooking: true));

        Assert.True(status.Activated);
        Assert.Equal(ActivationStepStates.Todo, Step(status, "organization").State);
    }

    [Fact]
    public void Build_AccountIncomplete_IsNeverActivated()
    {
        var facts = Facts(ConfiguredOrg(), OnePublished(), firstBooking: true, account: true) with { ConsentsAccepted = false };

        var status = ActivationChecklist.Build(facts);

        Assert.False(status.Activated);
        Assert.Equal(ActivationStepStates.Todo, Step(status, "account").State);
    }
}
