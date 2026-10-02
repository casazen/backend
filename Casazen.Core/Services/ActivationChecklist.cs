using Casazen.Core.Entities;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>Stable keys of the activation checklist steps (PL-15): never rename one.</summary>
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

/// <summary>Stable states of an activation checklist step (PL-15): never rename one.</summary>
public static class ActivationStepStates
{
    /// <summary>The stored state proves the step is complete.</summary>
    public const string Done = "done";

    /// <summary>Nothing done yet and the host can start it now.</summary>
    public const string Todo = "todo";

    /// <summary>Started, and still missing something (a CIN, a Stripe verification, a payment capability).</summary>
    public const string InProgress = "inProgress";

    /// <summary>Another step comes first: the host cannot do this one yet.</summary>
    public const string Blocked = "blocked";
}

/// <summary>Stable reasons a checklist step is not done (PL-15): never rename one.</summary>
public static class ActivationStepReasons
{
    /// <summary>Role, consents or org are missing: the onboarding wizard is not completed.</summary>
    public const string OnboardingIncomplete = "onboarding_incomplete";

    /// <summary>The org still has the generated name or the generated slug (the host has not chosen them).</summary>
    public const string OrgProfileIncomplete = "org_profile_incomplete";

    public const string NoProperty = "no_property";

    /// <summary>At least one property has no valid CIN (missing or malformed).</summary>
    public const string CinMissingOrInvalid = "cin_missing_or_invalid";

    /// <summary>No Stripe connected account is linked yet.</summary>
    public const string ConnectNotStarted = "connect_not_started";

    /// <summary>Stripe still asks for data or documents on the connected account.</summary>
    public const string ConnectRequirementsDue = "connect_requirements_due";

    /// <summary>Everything is submitted but Stripe has not enabled charges on the account yet.</summary>
    public const string ConnectPendingVerification = "connect_pending_verification";

    /// <summary>The org itself is disabled: its public pages answer 404.</summary>
    public const string OrgInactive = "org_inactive";

    /// <summary>Every property that could be published is paused by its host (PC-03).</summary>
    public const string PropertiesPaused = "properties_paused";

    /// <summary>The properties are waiting for (or lost) the compliance activation (CO-06).</summary>
    public const string CompliancePending = "compliance_pending";

    /// <summary>The properties are deactivated.</summary>
    public const string PropertiesInactive = "properties_inactive";

    /// <summary>A property is published, but guests cannot pay: Stripe charges are not enabled.</summary>
    public const string PaymentsNotReady = "payments_not_ready";

    public const string SiteNotPublished = "site_not_published";

    /// <summary>The site is open and bookable: the step only waits for a guest.</summary>
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

/// <summary>Everything the activation checklist reads, loaded by the service (so the rules stay pure).</summary>
/// <param name="Org">The org of the caller; null when the caller has none yet.</param>
public sealed record ActivationFacts(
    bool RoleChosen,
    bool OrgProvisioned,
    bool ConsentsAccepted,
    Org? Org,
    ActivationPropertyFacts Properties,
    bool FirstBookingTaken);

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
        var accountDone = facts.RoleChosen && facts.OrgProvisioned && facts.ConsentsAccepted;

        var organization = OrganizationStep(org);
        var property = PropertyStep(org, properties);
        var cin = CinStep(org, properties);
        var payments = PaymentsStep(org);
        var site = SiteStep(org, properties);
        var firstBooking = FirstBookingStep(org, facts.FirstBookingTaken, site.State == ActivationStepStates.Done);

        var propertyCreated = properties.Total > 0;
        var sitePublished = site.State == ActivationStepStates.Done;

        // The six milestones of PLG-AC6: unchanged meaning, a real "site published" (A1-37).
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

        // The generated name and the generated slug are what a new org starts with: only the host choosing them is
        // evidence that the profile is set. A slug the host never picked is never "configured".
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

        // An account exists but cannot charge: started is not done. Stripe either asks for more or is still checking.
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

        // A published property nobody can pay for: the link exists but every checkout would be refused.
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
