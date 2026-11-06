using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Whether the police (Alloggiati Web) and ISTAT send of a property is really active, and whether CasaZen can download the receipts
/// (CHK-12 point 7, NF-52, AC-NF52-11, E03/F02/US01/T02). It reads what the sender service already knows (<see cref="IPropertySenderService"/>:
/// the sender of the property, the police account on Octorate and the credentials kept in CasaZen) and answers it as a state per
/// communication. What Octorate does not let CasaZen read stays <see cref="ConnectionHealthState.Unknown"/>: nothing is guessed and nothing
/// is shown as active on an assumption.
/// </summary>
/// <remarks>
/// <para><b>One read of the police account.</b> The police fields come from <see cref="IPropertySenderService.GetAsync"/>, which reads the
/// police account of the accommodation once, bounded in time (<c>GetPoliceAccountAsync</c> of <see cref="IOctorateApiClient"/>; a field the
/// API does not expose is <c>null</c>), and turns every failure of Octorate into an unknown reading. There is no second read path.</para>
/// <para><b>ISTAT follows Alloggiati</b> (ADR-029-R5): the sender is the same and so are the readings, because the police account is the only
/// thing the Octorate API exposes about the daily automatic send.</para>
/// <para><b>Tenant and roles.</b> A property of another org (or a deleted one) is not found; the service never checks roles, the web layer
/// authorizes the property. The answer holds no credential, token or web service key: the Questura credentials are only said to exist.</para>
/// </remarks>
public interface IComplianceConnectionHealthService
{
    /// <summary>
    /// The health of the connection of one property, read now. Throws <c>NotFoundException</c> (<c>property_not_found</c>) for a property the
    /// caller's org does not have.
    /// </summary>
    Task<ComplianceConnectionHealth> GetAsync(Guid propertyId, CancellationToken ct);

    /// <summary>
    /// The health of several properties of the caller's org, for the status engine, which asks for a whole cockpit at a time. Each answer is
    /// kept for <see cref="ComplianceConnectionHealthRules.CacheDuration"/>, and at most
    /// <see cref="ComplianceConnectionHealthRules.MaxLiveReadsPerCall"/> properties are read from Octorate in one call: a property that was not
    /// read yet, and has no answer in memory, is <see cref="ConnectionHealthState.Unknown"/> with the reason
    /// <see cref="ComplianceConnectionHealthRules.NotCheckedYetReason"/> (never an error and never active), and the next call reads it. A property
    /// the caller's org does not have is left out of the answer.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ComplianceConnectionHealth>> GetManyAsync(IReadOnlyCollection<Guid> propertyIds, CancellationToken ct);
}

/// <summary>The state of the automatic send of one communication of a property, as CasaZen can tell it.</summary>
public enum ConnectionHealthState
{
    /// <summary>The channel manager sends and Octorate itself says so: the police account is valid and its automatic send is on, both read.</summary>
    Active = 1,

    /// <summary>The channel manager is the sender and Octorate says it does not send (account not valid or automatic send off), or the credentials kept in CasaZen were refused.</summary>
    Inactive = 2,

    /// <summary>CasaZen cannot tell: the API does not expose it, Octorate could not be reached, or there is no usable connection to read it from. The host is asked to confirm.</summary>
    Unknown = 3,

    /// <summary>Nobody sends automatically: the host sends on the portals (or the property is outside Italy).</summary>
    Manual = 4,
}

/// <summary>One communication (Alloggiati Web or ISTAT) of a property: who sends it and what is known about that send.</summary>
/// <param name="Sender">Who sends it: the channel manager, CasaZen (not available in this release) or the host by hand.</param>
/// <param name="State">Active only on Octorate's own word; Unknown when it is not readable.</param>
/// <param name="ConnectionValid">Octorate's <c>policeAccount.valid</c>; <c>null</c> = the API did not expose it (or the sender is not the channel manager).</param>
/// <param name="Autorun">Octorate's <c>policeAccount.autorun</c> (the daily automatic send is on); <c>null</c> = not exposed.</param>
/// <param name="LastAutomaticRunAt">When Octorate's daily job last ran (UTC, <c>policeAccount.lastCron</c>); <c>null</c> = not exposed.</param>
/// <param name="Reason">A stable code that says why the state is not simply Active (<see cref="SenderReasonCodes"/>, <see cref="ComplianceConnectionHealthRules"/>); <c>null</c> when Active or when there is nothing to say.</param>
/// <param name="DeclaredAt">When the host said it saw «Activated» in the Octorate panel (UTC); <c>null</c> when it did not. The host's word, never a verification.</param>
public sealed record ConnectionHealthLine(
    ComplianceSenderType Sender,
    ConnectionHealthState State,
    bool? ConnectionValid,
    bool? Autorun,
    DateTime? LastAutomaticRunAt,
    string? Reason,
    DateTime? DeclaredAt);

/// <summary>
/// The connection health of a property (<c>GET api/properties/{propertyId}/compliance/connection-health</c>). No personal data and no
/// secret: the credentials are only said to exist.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Applies">False for a property outside Italy: there is no police or ISTAT sender to speak of (both lines are manual).</param>
/// <param name="Alloggiati">The police communication.</param>
/// <param name="Istat">The ISTAT communication: the same sender as <paramref name="Alloggiati"/> (ADR-029-R5).</param>
/// <param name="CredentialsInCasaZen">The Alloggiati Web (Questura) credentials of the property are kept in CasaZen; their values are never returned.</param>
/// <param name="CredentialsUsable">
/// The credentials exist AND a verification with the service passed, so CasaZen could download the receipts. Always <c>false</c> in this
/// release: the verification of the Questura credentials is postponed (plan of 2026-11-05), so CasaZen never says that receipts can be downloaded.
/// </param>
/// <param name="CheckedAt">When the health was read (UTC); for a cached answer, when it was read.</param>
public sealed record ComplianceConnectionHealth(
    Guid PropertyId,
    bool Applies,
    ConnectionHealthLine Alloggiati,
    ConnectionHealthLine Istat,
    bool CredentialsInCasaZen,
    bool CredentialsUsable,
    DateTime CheckedAt);

/// <summary>The rules and the limits of the connection health.</summary>
public static class ComplianceConnectionHealthRules
{
    /// <summary>How long an answer of <see cref="IComplianceConnectionHealthService.GetManyAsync"/> is reused before Octorate is asked again.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>The most properties <see cref="IComplianceConnectionHealthService.GetManyAsync"/> reads from Octorate in one call.</summary>
    public const int MaxLiveReadsPerCall = 5;

    /// <summary>The reason of a property that was not read yet (over the limit of a call, no answer in memory).</summary>
    public const string NotCheckedYetReason = "connection_not_checked_yet";

    /// <summary>
    /// The state of a communication from the sender of the property and what is known about it. Only the channel manager can be Active, and
    /// only on Octorate's word; the host's declaration and a reading that is missing or failed are Unknown. CasaZen as the sender is Active
    /// only with usable credentials (never in this release), Inactive when the credentials kept were refused, Unknown otherwise.
    /// </summary>
    public static ConnectionHealthState StateOf(
        ComplianceSenderType sender,
        bool applies,
        SenderState senderState,
        bool credentialsUsable,
        bool credentialsRefused)
    {
        if (!applies || sender == ComplianceSenderType.Manual)
            return ConnectionHealthState.Manual;

        if (sender == ComplianceSenderType.CasaZen)
        {
            if (credentialsUsable)
                return ConnectionHealthState.Active;

            return credentialsRefused ? ConnectionHealthState.Inactive : ConnectionHealthState.Unknown;
        }

        return senderState switch
        {
            SenderState.Active => ConnectionHealthState.Active,
            SenderState.Inactive => ConnectionHealthState.Inactive,
            _ => ConnectionHealthState.Unknown,
        };
    }
}
