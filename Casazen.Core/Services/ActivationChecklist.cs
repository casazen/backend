using Casazen.Core.Entities;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

public static class ActivationStepKeys
{
    public const string Account = "account";
    public const string Organization = "organization";
    public const string Property = "property";
    public const string Cin = "cin";
    public const string Payments = "payments";
    public const string SitePublished = "sitePublished";
    public const string FirstBooking = "firstBooking";
}

public static class ActivationStepStates
{
    public const string Done = "done";

    public const string Todo = "todo";

    public const string InProgress = "inProgress";

    public const string Blocked = "blocked";
}

public static class ActivationStepReasons
{
    public const string OnboardingIncomplete = "onboarding_incomplete";

    public const string OrgProfileIncomplete = "org_profile_incomplete";

    public const string NoProperty = "no_property";

    public const string CinMissingOrInvalid = "cin_missing_or_invalid";

    public const string ConnectNotStarted = "connect_not_started";

    public const string ConnectRequirementsDue = "connect_requirements_due";

    public const string ConnectPendingVerification = "connect_pending_verification";

    public const string OrgInactive = "org_inactive";

    public const string PropertiesPaused = "properties_paused";

    public const string CompliancePending = "compliance_pending";

    public const string PropertiesInactive = "properties_inactive";

    public const string PaymentsNotReady = "payments_not_ready";

    public const string SiteNotPublished = "site_not_published";

    public const string AwaitingFirstBooking = "awaiting_first_booking";
}

/// <summary>What the stored state says about the properties of the org (soft-deleted ones excluded).</summary>
/// <param name="Total">Properties of the org.</param>
/// <param name="WithValidCin">Properties whose CIN is valid (<see cref="Regulatory.CinFormat"/>, computed on read).</param>
/// <param name="Published">Properties a guest can open and book (<see cref="PublicListing.IsPublished"/>).</param>
/// <param name="PausedByHost">Active, compliant properties hidden only because the host paused them.</param>
/// <param name="ComplianceNotActive">Active properties whose compliance is pending or suspended.</param>
public sealed record ActivationPropertyFacts(
    int Total,
    int WithValidCin,
    int Published,
    int PausedByHost,
    int ComplianceNotActive)
{
    public static ActivationPropertyFacts None { get; } = new(0, 0, 0, 0, 0);
}

public sealed record ActivationFacts(
    bool RoleChosen,
    bool OrgProvisioned,
    bool ConsentsAccepted,
    Org? Org,
    ActivationPropertyFacts Properties,
    bool FirstBookingTaken,
    bool OnboardingCompleted);

/// <summary>
/// Rules of the host activation checklist (PL-15, A1-37, A3-26, PLG-AC10). Every step is derived from stored state and
/// says why it is not done: nothing is marked done on a start (a Connect onboarding begun, a property created) or on a
/// manual flag.
/// </summary>
/// <remarks>
/// Decision on paused properties (PC-03): a paused property is not a published site. The site counts as published only
/// when at least one property is active, not paused and compliance-activated (<see cref="PublicListing.IsPublished"/>,
/// the rule the public search, the property page and the checkout use) <b>and</b> the org can take payments
/// (<see cref="Org.CanTakeDirectPayments"/>, the rule of the checkout): a link that every guest answers with a 409 is
/// not a published site. <c>Activated</c> keeps its meaning (role, org, consents, property, published site, first
/// direct booking); the organization and CIN steps are guidance on top of it.
/// </remarks>
public static class ActivationChecklist
{
    public static OnboardingActivationStatus Build(ActivationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var org = facts.Org;
        var properties = org is null ? ActivationPropertyFacts.None : facts.Properties;
        var accountDone = facts.OnboardingCompleted && facts.OrgProvisioned && facts.ConsentsAccepted;

        var organization = OrganizationStep(org);
        var property = PropertyStep(org, properties);
        var cin = CinStep(org, properties);
        var payments = PaymentsStep(org);
        var site = SiteStep(org, properties);
        var firstBooking = FirstBookingStep(org, facts.FirstBookingTaken, site.State == ActivationStepStates.Done);

        var propertyCreated = properties.Total > 0;
        var sitePublished = site.State == ActivationStepStates.Done;

        var activated = accountDone && propertyCreated && sitePublished && facts.FirstBookingTaken;

        var steps = new[]
        {
            accountDone
                ? new ActivationChecklistStep(ActivationStepKeys.Account, ActivationStepStates.Done)
                : new ActivationChecklistStep(
                    ActivationStepKeys.Account, ActivationStepStates.Todo, ActivationStepReasons.OnboardingIncomplete),
            organization,
            property,
            cin,
            payments,
            site,
            firstBooking,
        };

        return new OnboardingActivationStatus(
            facts.RoleChosen,
            facts.OrgProvisioned,
            facts.ConsentsAccepted,
            propertyCreated,
            sitePublished,
            facts.FirstBookingTaken,
            activated,
            null)
        {
            Steps = steps,
        };
    }

    private static ActivationChecklistStep Blocked(string key, string reason) =>
        new(key, ActivationStepStates.Blocked, reason);

    private static ActivationChecklistStep OrganizationStep(Org? org)
    {
        if (org is null)
            return Blocked(ActivationStepKeys.Organization, ActivationStepReasons.OnboardingIncomplete);

        var configured = !string.IsNullOrWhiteSpace(org.Name)
            && !string.Equals(org.Name.Trim(), Org.PlaceholderName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(org.Slug)
            && !Utilities.OrgSlugHelper.IsNeutral(org.Slug);

        return configured
            ? new ActivationChecklistStep(ActivationStepKeys.Organization, ActivationStepStates.Done)
            : new ActivationChecklistStep(
                ActivationStepKeys.Organization, ActivationStepStates.Todo, ActivationStepReasons.OrgProfileIncomplete);
    }

    private static ActivationChecklistStep PropertyStep(Org? org, ActivationPropertyFacts properties)
    {
        if (org is null)
            return Blocked(ActivationStepKeys.Property, ActivationStepReasons.OnboardingIncomplete);

        return properties.Total > 0
            ? new ActivationChecklistStep(ActivationStepKeys.Property, ActivationStepStates.Done, null, properties.Total, properties.Total)
            : new ActivationChecklistStep(ActivationStepKeys.Property, ActivationStepStates.Todo, ActivationStepReasons.NoProperty);
    }

    private static ActivationChecklistStep CinStep(Org? org, ActivationPropertyFacts properties)
    {
        if (org is null)
            return Blocked(ActivationStepKeys.Cin, ActivationStepReasons.OnboardingIncomplete);
        if (properties.Total == 0)
            return Blocked(ActivationStepKeys.Cin, ActivationStepReasons.NoProperty);

        if (properties.WithValidCin >= properties.Total)
        {
            return new ActivationChecklistStep(
                ActivationStepKeys.Cin, ActivationStepStates.Done, null, properties.WithValidCin, properties.Total);
        }

        return new ActivationChecklistStep(
            ActivationStepKeys.Cin,
            properties.WithValidCin == 0 ? ActivationStepStates.Todo : ActivationStepStates.InProgress,
            ActivationStepReasons.CinMissingOrInvalid,
            properties.WithValidCin,
            properties.Total);
    }

    private static ActivationChecklistStep PaymentsStep(Org? org)
    {
        if (org is null)
            return Blocked(ActivationStepKeys.Payments, ActivationStepReasons.OnboardingIncomplete);

        if (org.CanTakeDirectPayments)
            return new ActivationChecklistStep(ActivationStepKeys.Payments, ActivationStepStates.Done);

        if (string.IsNullOrWhiteSpace(org.StripeConnectedAccountId))
        {
            return new ActivationChecklistStep(
                ActivationStepKeys.Payments, ActivationStepStates.Todo, ActivationStepReasons.ConnectNotStarted);
        }

        var requirementsDue = !org.ConnectDetailsSubmitted || !string.IsNullOrWhiteSpace(org.ConnectRequirementsDueJson);
        return new ActivationChecklistStep(
            ActivationStepKeys.Payments,
            ActivationStepStates.InProgress,
            requirementsDue ? ActivationStepReasons.ConnectRequirementsDue : ActivationStepReasons.ConnectPendingVerification);
    }

    private static ActivationChecklistStep SiteStep(Org? org, ActivationPropertyFacts properties)
    {
        if (org is null)
            return Blocked(ActivationStepKeys.SitePublished, ActivationStepReasons.OnboardingIncomplete);
        if (!org.IsActive)
            return Blocked(ActivationStepKeys.SitePublished, ActivationStepReasons.OrgInactive);
        if (properties.Total == 0)
            return Blocked(ActivationStepKeys.SitePublished, ActivationStepReasons.NoProperty);

        if (properties.Published == 0)
        {
            var reason = properties.PausedByHost > 0
                ? ActivationStepReasons.PropertiesPaused
                : properties.ComplianceNotActive > 0
                    ? ActivationStepReasons.CompliancePending
                    : ActivationStepReasons.PropertiesInactive;
            return new ActivationChecklistStep(
                ActivationStepKeys.SitePublished, ActivationStepStates.Todo, reason, 0, properties.Total);
        }

        if (!org.CanTakeDirectPayments)
        {
            return new ActivationChecklistStep(
                ActivationStepKeys.SitePublished,
                ActivationStepStates.InProgress,
                ActivationStepReasons.PaymentsNotReady,
                properties.Published,
                properties.Total);
        }

        return new ActivationChecklistStep(
            ActivationStepKeys.SitePublished, ActivationStepStates.Done, null, properties.Published, properties.Total);
    }

    private static ActivationChecklistStep FirstBookingStep(Org? org, bool firstBookingTaken, bool sitePublished)
    {
        if (org is null)
            return Blocked(ActivationStepKeys.FirstBooking, ActivationStepReasons.OnboardingIncomplete);

        // A first direct booking already taken stays a fact even if the site is hidden afterwards.
        if (firstBookingTaken)
            return new ActivationChecklistStep(ActivationStepKeys.FirstBooking, ActivationStepStates.Done);

        return sitePublished
            ? new ActivationChecklistStep(
                ActivationStepKeys.FirstBooking, ActivationStepStates.Todo, ActivationStepReasons.AwaitingFirstBooking)
            : Blocked(ActivationStepKeys.FirstBooking, ActivationStepReasons.SiteNotPublished);
    }
}
