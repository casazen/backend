using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The session of the self-hosted identity verification (E70/F02/US01/T01, NF-188, <c>Features:CheckInIdentitySelfHosted</c> together with
/// <c>Features:CheckInIdentityVerification</c>): ONE verification per session, bound to ONE guest, opened once with the notice shown, the choice of the guest and the
/// family of the browser, and closed for good with a terminal outcome code at the first hand-off to a person. With the self-hosted flow off none of this runs and
/// the session reads exactly as before.
/// </summary>
/// <remarks>
/// <para><b>The bound guest</b> (<see cref="GuestCheckInSession.IdentityGuestId"/>): the guest the flow verifies (the first one who carries the document, else the
/// first one: the rule of <c>VerifyingGuestPosition</c>), by the id of the <see cref="Guest"/> of the booker that guest stands for (<see cref="StayGuest.GuestId"/>:
/// the first row of a stay is always the booker's, before and after the guests are registered, and it is the id the log rows carry in
/// <see cref="BiometricVerificationLog.GuestId"/>, a foreign key to the guests), or by the id of the row of the stay for a first guest with a document that is not the
/// booker's row. Set once, at the opening, never changed: an edit of the guest list does not move it. While it is set the flow reads the check-in data of that guest
/// only; when that guest is no longer among the guests of the stay the verification is not offered any more and goes to a person (<c>review</c>), it never moves to
/// somebody else.</para>
/// <para><b>Closed</b> (<see cref="GuestCheckInSession.IdentityClosedAt"/>, <see cref="GuestCheckInSession.IdentityOutcomeCode"/>): every hand-off of an opened session
/// to a person closes it with the code of its reason (<see cref="IdentityOutcomeOf"/>); the success path of the automatic approval (a later task) closes it with
/// <c>auto_approved</c> through <see cref="CloseIdentitySession"/>. A closed session takes no new identity step (the endpoints answer 409
/// <c>identity_already_closed</c>); the request for a person stays open.</para>
/// <para><b>Privacy</b>: the session keeps ids, codes, versions, hashes and times; the User-Agent never reaches this class (the endpoint passes the family only), and
/// the log names the session, the codes and the family.</para>
/// </remarks>
public sealed partial class IdentityVerificationFlowService
{
    private bool SelfHostedOn =>
        featureFlags is not null
        && featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification)
        && featureFlags.IsEnabled(FeatureFlags.CheckInIdentitySelfHosted);

    public Task<IdentityOpenResult> OpenIdentityAsync(GuestCheckInSession session, string? noticeVersion, string? channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var stale = new IdentityOpenResult(IdentityOpenStatus.SessionUnavailable, null);

        // The row of the session is locked (Postgres) or the session serialized (in memory) as for the presence challenge: two openings at the same time
        // record one.
        return WithLockedPoseSessionAsync(session, stale, async () =>
        {
            var context = await ContextAsync(session, ct);
            if (!SelfHostedOn)
                return new IdentityOpenResult(IdentityOpenStatus.Answered, State(session, context));

            if (session.IdentityClosedAt is not null)
                return new IdentityOpenResult(IdentityOpenStatus.Closed, State(session, context));

            var served = context.ActiveConsentText;
            var named = noticeVersion?.Trim();
            if (served is null || string.IsNullOrEmpty(named) || !string.Equals(served.Version, named, StringComparison.Ordinal))
                return new IdentityOpenResult(IdentityOpenStatus.NoticeVersionUnknown, State(session, context));

            if (TryOpen(session, context, served, channel, UtcNow()))
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "Identity verification of session {SessionId} opened: notice={NoticeVersion} channel={Channel}",
                    session.Id, session.IdentityNoticeVersion, session.IdentityChannel);
            }

            return new IdentityOpenResult(IdentityOpenStatus.Answered, State(session, context));
        }, ct);
    }

    internal static string IdentityOutcomeOf(string alertReason)
    {
        return alertReason switch
        {
            VerificationAlertReasons.Review or VerificationAlertReasons.AttemptsExhausted => IdentityOutcomeCodes.Review,
            VerificationAlertReasons.ConsentRefused => IdentityOutcomeCodes.ConsentRefused,
            VerificationAlertReasons.GuestRequest => IdentityOutcomeCodes.Assisted,
            VerificationAlertReasons.TechnicalError => IdentityOutcomeCodes.TechnicalError,
            VerificationAlertReasons.BiometricsDisabled or VerificationAlertReasons.LegalTextNotApproved => IdentityOutcomeCodes.Unavailable,
            VerificationAlertReasons.MinorExcluded => IdentityOutcomeCodes.MinorExcluded,
            _ => IdentityOutcomeCodes.Review,
        };
    }

    /// <summary>
    /// What the guest sees of a closed verification: <c>ok</c> for an automatic approval, <c>operator</c> for any other terminal code, which handed the verification to a person.
    /// Nothing for a verification the retention closed as <c>abandoned</c> (E73/F01/US02/T03) while nobody has it (<paramref name="handedToAPerson"/> false): no operator was told, so
    /// the state must not say that a person has it, only that it is closed (the page shows a verification closed with no positive outcome). When the guest then asks for a person the
    /// hand-off is real and the outcome is <c>operator</c>.
    /// </summary>
    internal static string? GuestOutcomeOf(string? outcomeCode, bool handedToAPerson)
    {
        if (string.Equals(outcomeCode, IdentityOutcomeCodes.AutoApproved, StringComparison.Ordinal))
            return IdentityVerificationGuestResults.Ok;

        return string.Equals(outcomeCode, IdentityOutcomeCodes.Abandoned, StringComparison.Ordinal) && !handedToAPerson
            ? null
            : IdentityVerificationGuestResults.Operator;
    }

    /// <summary>
    /// Opens the identity step of <paramref name="session"/> when it can be: the self-hosted flow is on, the step was never opened, the verification is not closed, did
    /// not pass and is not with a person already (the booking of another session included). Records the time, the notice and its hash, the channel (an unknown code is
    /// <c>web:other</c>) and binds the guest the flow verifies, once. True when it opened; nothing is saved here.
    /// </summary>
    private bool TryOpen(GuestCheckInSession session, FlowContext context, CheckInLegalVersion notice, string? channel, DateTime now)
    {
        if (!SelfHostedOn
            || session.IdentityOpenedAt is not null
            || session.IdentityClosedAt is not null
            || session.BiometricOutcome == BiometricOutcome.Accept
            || InOperatorPath(session, context))
        {
            return false;
        }

        session.IdentityOpenedAt = now;
        session.IdentityNoticeVersion = notice.Version;
        session.IdentityNoticeTextHash = notice.TextHash;
        session.IdentityChannel = channel is not null && IdentityChannels.All.Contains(channel, StringComparer.Ordinal) ? channel : IdentityChannels.Other;
        session.IdentityGuestId ??= context.VerifyingGuestId;
        session.UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Records the choice of the guest on the self-hosted flow (<c>automatic</c> for a consent, <c>assisted</c> for a refusal), opening the step first when it was not
    /// opened: the notice is then the version of the consent the registry serves, and a version the request names must be it. A consent to a stale version is no
    /// choice (it records nothing, as the consent itself); a refusal needs no version once the step is open. A closed verification, or one that cannot be opened,
    /// records nothing. Nothing is saved here: the caller's save carries it.
    /// </summary>
    private void NoteChoice(GuestCheckInSession session, FlowContext context, string choice, string? version, string? channel, DateTime now)
    {
        if (!SelfHostedOn || session.IdentityClosedAt is not null)
            return;

        var served = context.ActiveConsentText;
        var named = version?.Trim();
        var namesTheServedVersion = string.IsNullOrEmpty(named) || (served is not null && string.Equals(served.Version, named, StringComparison.Ordinal));
        if (string.Equals(choice, IdentityChoices.Automatic, StringComparison.Ordinal) && (string.IsNullOrEmpty(named) || !namesTheServedVersion))
            return;

        if (session.IdentityOpenedAt is null && (!namesTheServedVersion || served is null || !TryOpen(session, context, served, channel, now)))
            return;

        session.IdentityChoice = choice;
        session.IdentityChoiceAt = now;
        session.UpdatedAt = now;
    }

    private void CloseIdentitySession(GuestCheckInSession session, string outcomeCode)
    {
        if (!SelfHostedOn || session.IdentityOpenedAt is null || session.IdentityClosedAt is not null)
            return;

        var now = UtcNow();
        session.IdentityClosedAt = now;
        session.IdentityOutcomeCode = outcomeCode;
        session.UpdatedAt = now;
        logger.LogInformation("Identity verification of session {SessionId} closed: outcome={Outcome}", session.Id, outcomeCode);
    }

    private static StayGuest? BoundGuestOf(IReadOnlyList<StayGuest> guests, Guid bound)
    {
        return guests.FirstOrDefault(guest => guest.GuestId == bound)
            ?? guests.FirstOrDefault(guest => guest.Id != Guid.Empty && guest.Id == bound);
    }

    private static Guid BindingIdOf(StayGuest? verifying, Booking booking)
    {
        if (verifying?.GuestId is { } guestId)
            return guestId;

        return verifying is not null && verifying.Id != Guid.Empty ? verifying.Id : booking.GuestId;
    }
}
