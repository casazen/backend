using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The state of the owner sign-in on an agency's own domain (<see cref="Org.OwnerLoginHostState"/>, E40/F01/US02/T01, NF-141) and the live notices of the domain
/// to the administrators of the org: one place for the verification of the domain, the save of the owner portal, the agency's retry and the hand of a platform
/// administrator, so the four cannot disagree.
/// </summary>
/// <remarks>
/// <para><b>Automatic, with <c>WhiteLabelHost</c> AND <c>Auth0HostSync</c> on</b> (<see cref="IsAutomatic"/>). The registration (<see cref="IAuth0HostRegistrar"/>)
/// runs when the domain becomes verified, when the owner portal is saved on while the domain is verified and the state is NotRequested or Failed, and on the
/// agency's retry: Ready for Registered or AlreadyRegistered, Failed for Rejected or Unavailable, Pending for NotConfigured (<see cref="StateAfter"/>). With
/// either flag off nothing here calls Auth0.</para>
/// <para><b>By hand, with <c>Auth0HostSync</c> off.</b> The state waits in Pending (saving the owner portal, the retry) and a platform administrator sets it
/// (<see cref="ApplyAsync"/> through <c>PUT api/admin/orgs/{orgId}/owner-login-state</c>) once the three URLs are in Auth0 (<c>docs/runbooks/auth0.md</c> § 13).</para>
/// <para><b>Notices</b>, only with <c>WhiteLabelHost</c> on: <see cref="PushTypes.DomainVerified"/> once per verification, <see cref="PushTypes.OwnerLoginReady"/> and
/// <see cref="PushTypes.OwnerLoginFailed"/> on each change of the state to Ready or Failed, whoever made it, to <see cref="PushAudience.OrgAdmins"/>: through the
/// decorator of the push they are the bell entry, the pop-up and the live refresh of the open domain settings, never a push on a phone. No domain name and nothing
/// of a person in them. They are not one of the eight kinds of event of the notification preferences (NF-65).</para>
/// <para>Logs carry the org and the result, never the domain.</para>
/// </remarks>
public sealed class OwnerLoginHostSync(
    AppDbContext dbContext,
    IAuth0HostRegistrar registrar,
    IFeatureFlags featureFlags,
    IPushNotificationService pushNotifications,
    IPublicHostResolver publicHostResolver,
    TimeProvider timeProvider,
    ILogger<OwnerLoginHostSync> logger)
{
    public bool IsAutomatic =>
        featureFlags.IsEnabled(FeatureFlags.WhiteLabelHost) && featureFlags.IsEnabled(FeatureFlags.Auth0HostSync);

    public async Task DomainVerifiedAsync(Org org, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(org);
        if (!featureFlags.IsEnabled(FeatureFlags.WhiteLabelHost))
            return;

        var verifiedAt = org.DomainVerifiedAt ?? timeProvider.GetUtcNow().UtcDateTime;
        Tell(
            org.Id,
            PushTypes.DomainVerified,
            PushDeliveryKeys.DomainVerified(org.Id, verifiedAt),
            EmailTemplates.DomainVerifiedPush(EmailTemplates.DefaultCulture));

        if (IsAutomatic)
            await RegisterAsync(org, cancellationToken);
    }

    public async Task OwnerPortalSavedOnAsync(Org org, OwnerLoginHostState stateBefore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(org);
        if (IsAutomatic
            && org.OwnerPortalOnOwnDomain
            && stateBefore is (OwnerLoginHostState.NotRequested or OwnerLoginHostState.Failed)
            && HasVerifiedDomain(org))
        {
            await RegisterAsync(org, cancellationToken);
        }
    }

    /// <summary>
    /// The agency's retry (<c>POST api/orgs/{orgId}/domain/owner-login</c>) for an org whose custom domain is verified: nothing to do once the state is Ready (Auth0 is
    /// not asked again); otherwise, with both flags on, the registration runs again (idempotent), and with <c>Auth0HostSync</c> off a request that does not wait
    /// already (NotRequested, Failed) goes back to the queue of CasaZen (Pending).
    /// </summary>
    public async Task RetryAsync(Org org, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(org);
        if (org.OwnerLoginHostState == OwnerLoginHostState.Ready || !HasVerifiedDomain(org))
            return;

        if (IsAutomatic)
            await RegisterAsync(org, cancellationToken);
        else if (org.OwnerLoginHostState is OwnerLoginHostState.NotRequested or OwnerLoginHostState.Failed)
            await ApplyAsync(org, OwnerLoginHostState.Pending, cancellationToken);
    }

    public async Task<bool> ApplyAsync(Org org, OwnerLoginHostState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(org);
        var before = org.OwnerLoginHostState;
        var changed = before != state;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (changed)
        {
            org.OwnerLoginHostState = state;
            org.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (!string.IsNullOrEmpty(org.CustomDomain))
            publicHostResolver.InvalidateCacheForHost(org.CustomDomain);

        if (!changed)
            return false;

        logger.LogInformation("Owner sign-in on the domain of org {OrgId}: {Before} -> {State}", org.Id, before, state);
        if (featureFlags.IsEnabled(FeatureFlags.WhiteLabelHost))
        {
            if (state == OwnerLoginHostState.Ready)
            {
                Tell(
                    org.Id,
                    PushTypes.OwnerLoginReady,
                    PushDeliveryKeys.OwnerLoginReady(org.Id, now),
                    EmailTemplates.OwnerLoginReadyPush(EmailTemplates.DefaultCulture));
            }
            else if (state == OwnerLoginHostState.Failed)
            {
                Tell(
                    org.Id,
                    PushTypes.OwnerLoginFailed,
                    PushDeliveryKeys.OwnerLoginFailed(org.Id, now),
                    EmailTemplates.OwnerLoginFailedPush(EmailTemplates.DefaultCulture));
            }
        }

        return true;
    }

    /// <summary>The state of the owner sign-in after a registration: Ready when Auth0 has the domain, Pending while the registrar is not configured, Failed otherwise.</summary>
    public static OwnerLoginHostState StateAfter(Auth0HostRegistrationResult result) => result switch
    {
        Auth0HostRegistrationResult.Registered or Auth0HostRegistrationResult.AlreadyRegistered => OwnerLoginHostState.Ready,
        Auth0HostRegistrationResult.NotConfigured => OwnerLoginHostState.Pending,
        _ => OwnerLoginHostState.Failed,
    };

    private async Task RegisterAsync(Org org, CancellationToken cancellationToken)
    {
        if (!HasVerifiedDomain(org))
            return;

        var result = await registrar.RegisterAsync(org.CustomDomain!, cancellationToken);
        logger.LogInformation("Auth0 registration of the domain of org {OrgId}: {Result}", org.Id, result);
        await ApplyAsync(org, StateAfter(result), cancellationToken);
    }

    private static bool HasVerifiedDomain(Org org) =>
        org.PublicHostMode == PublicHostMode.CustomDomain
        && !string.IsNullOrEmpty(org.CustomDomain)
        && org.DomainVerificationStatus == DomainVerificationStatus.Verified;

    private void Tell(Guid orgId, string type, string deliveryKey, PushText text) =>
        pushNotifications.Enqueue(
            deliveryKey,
            PushAudience.OrgAdmins(orgId),
            new PushNotificationPayload(text.Title, text.Body, type, BookingId: null, Route: string.Empty));
}
