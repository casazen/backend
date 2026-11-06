using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The sender of the police and ISTAT communications of a property with its state (<c>GET|PUT api/properties/{propertyId}/sender</c>,
/// <c>POST .../sender/declare-activated</c>, E03/F01/US02/T01). No personal data: the Questura credentials are only said to exist, never returned.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Sender">Who sends the Alloggiati Web communications: ChannelManager, CasaZen (not available in this release) or Manual.</param>
/// <param name="IstatSender">Who sends the ISTAT communications: always the same as <paramref name="Sender"/>.</param>
/// <param name="Source">Stored (a person chose it), DefaultConnected, DefaultNotConnected or NotApplicable.</param>
/// <param name="Applies">False for a property outside Italy: there is no sender to choose.</param>
/// <param name="State">Active (verified), Declared (the host's word), Unverified, Inactive or NotConfigured. Show «Active» only for Active.</param>
/// <param name="Verification">Verified, UserDeclared, Unverifiable or NotApplicable: how <paramref name="State"/> is known.</param>
/// <param name="Reason">A stable code that says why the state is not simply Active (for example <c>octorate_police_not_exposed</c>); <c>null</c> when there is nothing to say.</param>
/// <param name="Guidance">What the host can do about <paramref name="Reason"/>, in the language of the request; <c>null</c> when there is nothing to say.</param>
/// <param name="PoliceAccountValid">Octorate's <c>policeAccount.valid</c>; <c>null</c> = the API did not expose it.</param>
/// <param name="PoliceAutorun">Octorate's <c>policeAccount.autorun</c>; <c>null</c> = the API did not expose it.</param>
/// <param name="PoliceLastCron">When Octorate's daily job last ran (UTC); <c>null</c> = the API did not expose it.</param>
/// <param name="QuesturaCredentialsConfigured">True when Alloggiati Web credentials are stored for the property.</param>
/// <param name="ChannelManagerAvailable">False without a confirmed listing on a connected Octorate account, or while the Octorate connector is off.</param>
/// <param name="AvailableSenders">The senders the host can choose now; empty outside Italy.</param>
/// <param name="DelegationAccepted">True when the org accepted the current delegation text.</param>
/// <param name="DelegationTextVersion">The version of the delegation text to show and to accept before an automatic sender is set; the client sends it back as <c>delegationTextVersion</c> with <c>delegationConsent</c>.</param>
/// <param name="DeclaredAt">When the host declared the channel manager activated (UTC); <c>null</c> when it did not or a later change voided it.</param>
/// <param name="LastChangedAt">When a person last chose the sender (UTC); <c>null</c> for a default.</param>
public sealed record PropertySenderDto(
    Guid PropertyId,
    ComplianceSenderType Sender,
    ComplianceSenderType IstatSender,
    SenderSource Source,
    bool Applies,
    SenderState State,
    SenderVerification Verification,
    string? Reason,
    string? Guidance,
    bool? PoliceAccountValid,
    bool? PoliceAutorun,
    DateTime? PoliceLastCron,
    bool QuesturaCredentialsConfigured,
    bool ChannelManagerAvailable,
    IReadOnlyList<ComplianceSenderType> AvailableSenders,
    bool DelegationAccepted,
    string DelegationTextVersion,
    DateTime? DeclaredAt,
    DateTime? LastChangedAt)
{
    /// <summary>The DTO of a view, with the guidance already worded in the language of the request.</summary>
    public static PropertySenderDto From(PropertySenderView view, string? guidance)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new PropertySenderDto(
            view.PropertyId,
            view.Sender,
            view.IstatSender,
            view.Source,
            view.Applies,
            view.State,
            view.Verification,
            view.Reason,
            guidance,
            view.PoliceAccountValid,
            view.PoliceAutorun,
            view.PoliceLastCron,
            view.QuesturaCredentialsConfigured,
            view.ChannelManagerAvailable,
            view.AvailableSenders,
            view.DelegationAccepted,
            view.DelegationTextVersion,
            view.DeclaredAt,
            view.LastChangedAt);
    }
}

/// <summary>
/// The answer of the connection test of a sender (<c>POST api/properties/{propertyId}/sender/connection-test</c>). Nothing in it is stored.
/// «Active» may be shown only for <c>Verified</c>.
/// </summary>
/// <param name="Sender">The sender that was tested.</param>
/// <param name="Outcome">Verified (Octorate proved it), Inactive (Octorate says it does not send), Unverifiable (the API does not say), NotApplicable (manual) or NotAvailable (CasaZen).</param>
/// <param name="Reason">A stable code for the guidance when the outcome is not Verified; <c>null</c> otherwise.</param>
/// <param name="Guidance">What the host can do next, in the language of the request; <c>null</c> when Verified or when there is nothing to test.</param>
/// <param name="PoliceAccountValid">What Octorate answered for <c>policeAccount.valid</c>; <c>null</c> = the API did not expose it.</param>
/// <param name="PoliceAutorun">What Octorate answered for <c>policeAccount.autorun</c>; <c>null</c> = the API did not expose it.</param>
/// <param name="PoliceLastCron">What Octorate answered for <c>policeAccount.lastCron</c> (UTC); <c>null</c> = the API did not expose it.</param>
/// <param name="CheckedAt">When the test ran (UTC).</param>
public sealed record SenderConnectionTestDto(
    ComplianceSenderType Sender,
    SenderConnectionTestOutcome Outcome,
    string? Reason,
    string? Guidance,
    bool? PoliceAccountValid,
    bool? PoliceAutorun,
    DateTime? PoliceLastCron,
    DateTime CheckedAt)
{
    /// <summary>The DTO of a result, with the guidance already worded in the language of the request.</summary>
    public static SenderConnectionTestDto From(SenderConnectionTestResult result, string? guidance)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new SenderConnectionTestDto(
            result.Sender,
            result.Outcome,
            result.Reason,
            guidance,
            result.PoliceAccountValid,
            result.PoliceAutorun,
            result.PoliceLastCron,
            result.CheckedAt);
    }
}

/// <summary>
/// Changes who sends the police and ISTAT communications of a property (<c>PUT api/properties/{propertyId}/sender</c>). ISTAT always
/// follows Alloggiati Web, so there is no field to choose it: <c>istatSender</c> is accepted only to be refused when it differs.
/// </summary>
public sealed class SwitchSenderRequest
{
    /// <summary>The new sender: ChannelManager or Manual (CasaZen is refused: it is not available in this release).</summary>
    [Required(ErrorMessage = "SenderRequired")]
    public ComplianceSenderType? Sender { get; set; }

    /// <summary>Optional. When given it has to be the same as <see cref="Sender"/>, otherwise 422 <c>sender_istat_mismatch</c>.</summary>
    public ComplianceSenderType? IstatSender { get; set; }

    /// <summary>The host confirmed that the sender in effect will be off. Without it a change of sender answers 409 <c>sender_switch_confirmation_required</c>.</summary>
    public bool Confirmed { get; set; }

    /// <summary>The host accepts the delegation of the transmission (the text of <c>delegationTextVersion</c>). Needed once for an automatic sender.</summary>
    public bool DelegationConsent { get; set; }

    /// <summary>
    /// The version of the delegation text the host read and accepted: the <c>delegationTextVersion</c> of the sender the screen was loaded
    /// from. Optional, but required when <see cref="DelegationConsent"/> is true: 422 <c>alloggiati_delegation_text_version_required</c> without
    /// it, 409 <c>alloggiati_delegation_text_outdated</c> when it is not the version in force (the problem carries the current
    /// <c>delegationTextVersion</c>). The consent recorded is the consent to the text the person saw.
    /// </summary>
    [MaxLength(AlloggiatiDelegationTexts.VersionMaxLength, ErrorMessage = "AlloggiatiDelegationVersionInvalid")]
    public string? DelegationTextVersion { get; set; }
}
