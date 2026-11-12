using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Compliance;

/// <summary>
/// Whether the police (Alloggiati Web) and ISTAT send of a property is active, and whether CasaZen can download the receipts
/// (<c>GET api/properties/{propertyId}/compliance/connection-health</c>, E03/F02/US01/T02, CHK-12 point 7, AC-NF52-11). What Octorate does not
/// let CasaZen read is <c>Unknown</c>, never <c>Active</c>. No personal data and no secret: the credentials are only said to exist.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Applies">False for a property outside Italy: both lines are manual.</param>
/// <param name="Alloggiati">The police communication.</param>
/// <param name="Istat">The ISTAT communication; it has the same sender as <paramref name="Alloggiati"/> (ISTAT follows Alloggiati).</param>
/// <param name="CredentialsInCasaZen">The Alloggiati Web credentials of the property are kept in CasaZen; their values are never returned.</param>
/// <param name="CredentialsUsable">The credentials exist and were verified, so the receipts could be downloaded. Always <c>false</c> in this release: show nothing that says receipts can be downloaded.</param>
/// <param name="CheckedAt">When the health was read (UTC).</param>
public sealed record ComplianceConnectionHealthDto(
    Guid PropertyId,
    bool Applies,
    ConnectionHealthLineDto Alloggiati,
    ConnectionHealthLineDto Istat,
    bool CredentialsInCasaZen,
    bool CredentialsUsable,
    DateTime CheckedAt)
{
    /// <summary>The DTO of a health read.</summary>
    public static ComplianceConnectionHealthDto From(ComplianceConnectionHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return new ComplianceConnectionHealthDto(
            health.PropertyId,
            health.Applies,
            ConnectionHealthLineDto.From(health.Alloggiati),
            ConnectionHealthLineDto.From(health.Istat),
            health.CredentialsInCasaZen,
            health.CredentialsUsable,
            health.CheckedAt);
    }
}

/// <summary>One communication of a property: who sends it and what is known about that send.</summary>
/// <param name="Sender">ChannelManager, CasaZen (not available in this release) or Manual.</param>
/// <param name="State">Active (Octorate itself says so), Inactive, Unknown (CasaZen cannot tell: ask the host to confirm) or Manual. Show «active» only for Active.</param>
/// <param name="ConnectionValid">Octorate's <c>policeAccount.valid</c>; <c>null</c> = the API did not expose it.</param>
/// <param name="Autorun">Octorate's <c>policeAccount.autorun</c>; <c>null</c> = the API did not expose it.</param>
/// <param name="LastAutomaticRunAt">When Octorate's daily job last ran (UTC); <c>null</c> = the API did not expose it.</param>
/// <param name="Reason">A stable code that says why the state is not simply Active (for example <c>octorate_police_not_exposed</c>); <c>null</c> when there is nothing to say.</param>
/// <param name="DeclaredAt">When the host said it saw «Activated» in the Octorate panel (UTC); <c>null</c> when it did not. The host's word, not a verification.</param>
public sealed record ConnectionHealthLineDto(
    ComplianceSenderType Sender,
    ConnectionHealthState State,
    bool? ConnectionValid,
    bool? Autorun,
    DateTime? LastAutomaticRunAt,
    string? Reason,
    DateTime? DeclaredAt)
{
    /// <summary>The DTO of a line.</summary>
    public static ConnectionHealthLineDto From(ConnectionHealthLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new ConnectionHealthLineDto(
            line.Sender,
            line.State,
            line.ConnectionValid,
            line.Autorun,
            line.LastAutomaticRunAt,
            line.Reason,
            line.DeclaredAt);
    }
}
