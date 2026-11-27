using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// Keeps the unified "To do" list (ADR-026, <see cref="IInternalActivityService"/>) in step with the compliance cockpit (E03/F02/US01/T03,
/// NF-52, AC-NF52-13): every red or yellow item of the compliance status becomes ONE "To do" with the action and the id of the cockpit item,
/// deduplicated by (action, target, period), and the item is closed by the system when its light turns green or the item no longer applies.
/// </summary>
/// <remarks>
/// <para><b>What is fed.</b> The compliance items: the Alloggiati communication of a stay (to send, to resolve, to send by hand, the receipt to
/// upload), the identity verification, the police send of the channel manager, the police credentials, ISTAT, the tourist tax of a month and the
/// tourist tax rate. Not the operational items of the cockpit (a property to activate, guest data to complete, a check-out, a turnover): their
/// own producers keep their own "To do".</para>
/// <para><b>Only what the caller reaches.</b> The items come from an evaluation of the caller's <see cref="HostScope"/> and the To do list is
/// reconciled only against the stays, properties and months that evaluation looked at: nothing is created for a property the caller does not
/// reach, and nothing outside what was looked at is closed.</para>
/// </remarks>
public interface IComplianceActivityFeeder
{
    /// <summary>
    /// Creates the "To do" of the items that need attention and are not on the list yet, and closes the open ones whose item is green or gone.
    /// Idempotent, and cheap when nothing changed (one read per kind of item); an org-and-scope that was fed a moment ago is skipped.
    /// </summary>
    Task FeedAsync(HostScope scope, ComplianceStatusResult status, CancellationToken ct);
}
