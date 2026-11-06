using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The sender of the police (Alloggiati Web) and ISTAT communications of a property, as the host sees it and changes it (ADR-029, NF-42,
/// E03/F01/US02/T01): the state of the sender, the switch with its confirmations, the connection test and the host's declaration that
/// the channel manager is activated. It sits on top of <see cref="IPropertySenderStore"/> (the one write path, which also records the
/// audit of the switch) and <see cref="IAlloggiatiDelegationService"/> (the delegation of the transmission).
/// </summary>
/// <remarks>
/// <para><b>Senders offered in this release.</b> The channel manager (while the property has a confirmed listing on a connected Octorate
/// account and <c>Features:OctorateConnector</c> is on) and manual (the host sends the file). The sender «CasaZen sends» is not available:
/// the Questura accounts it needs are postponed (plan of 2026-11-05), so it cannot be set (422 <see cref="SenderErrorCodes.CasaZenNotAvailable"/>)
/// and its connection test answers <see cref="SenderConnectionTestOutcome.NotAvailable"/>. Two automatic senders can therefore never be on
/// together, and the guard against switching to CasaZen while the Octorate police account is still valid (ADR-029-R2) comes back with that
/// sender.</para>
/// <para><b>Never a false «Active».</b> <see cref="SenderState.Active"/> is answered only when Octorate itself says the police account is
/// valid and its daily automatic send is on, both members read: a member the API does not expose is no proof. What the API does not expose
/// stays unknown: the state is then <see cref="SenderState.Unverified"/>, or <see cref="SenderState.Declared"/> when the host said it saw
/// «Activated», and that is the host's word, never a verification.</para>
/// <para><b>Tenant and roles.</b> A property of another org is not found (<c>property_not_found</c>), as in the store. The service never
/// checks roles: the web layer authorizes the property (<c>property.read</c> or <c>property.write</c> on it).</para>
/// </remarks>
public interface IPropertySenderService
{
    /// <summary>
    /// The sender of the property with its state and verification, the police fields Octorate exposes (one read of the accommodation,
    /// only for the channel manager and bounded in time: a failure leaves them <c>null</c> and never fails the call), whether Questura
    /// credentials exist (never their value) and whether the channel manager can be chosen. Throws <c>NotFoundException</c>
    /// (<c>property_not_found</c>) for a property the caller's org does not have.
    /// </summary>
    Task<PropertySenderView> GetAsync(Guid propertyId, CancellationToken ct);

    /// <summary>
    /// Changes the sender of the property. In order: 422 <see cref="SenderErrorCodes.IstatMismatch"/> when <paramref name="istatSender"/> is given and is
    /// not <paramref name="newSender"/> (ISTAT always follows Alloggiati); 404 <c>property_not_found</c>; 422 <c>sender_not_applicable</c> outside Italy;
    /// 422 <see cref="SenderErrorCodes.CasaZenNotAvailable"/> for CasaZen; 422 <see cref="SenderErrorCodes.ChannelManagerNotAvailable"/> for a channel manager
    /// the property cannot use; 409 <see cref="SenderErrorCodes.SwitchConfirmationRequired"/> when the sender really changes and
    /// <paramref name="confirmed"/> is false (the problem carries <c>currentSender</c> and <c>newSender</c>); 403
    /// <see cref="SenderErrorCodes.DelegationRequired"/> when an automatic sender is asked and the org has not accepted the current delegation text and
    /// <paramref name="delegationConsent"/> is false. Only then are the consent recorded (<c>IAlloggiatiDelegationService.GrantAsync</c>) and the sender
    /// written (<c>IPropertySenderStore.SetAsync</c>, which records the audit line of the change in the same transaction). A repeat of the sender
    /// already in effect changes and records nothing.
    /// </summary>
    Task<PropertySenderView> SwitchAsync(
        Guid propertyId,
        ComplianceSenderType newSender,
        bool confirmed,
        bool delegationConsent,
        string userId,
        CancellationToken ct,
        ComplianceSenderType? istatSender = null);

    /// <summary>
    /// Tests the sender. Channel manager: reads the accommodation on Octorate and answers <see cref="SenderConnectionTestOutcome.Verified"/> only when the
    /// police account is valid and its automatic send is on (both read), <see cref="SenderConnectionTestOutcome.Inactive"/> when it is not, and
    /// <see cref="SenderConnectionTestOutcome.Unverifiable"/> when the API does not expose it or cannot be reached. CasaZen:
    /// <see cref="SenderConnectionTestOutcome.NotAvailable"/>. Manual: <see cref="SenderConnectionTestOutcome.NotApplicable"/>. Nothing is stored or transmitted.
    /// </summary>
    Task<SenderConnectionTestResult> TestConnectionAsync(Guid propertyId, CancellationToken ct);

    /// <summary>
    /// The host says it saw «Activated» in the Octorate panel: stored with the day and the account as a declaration (<see cref="SenderState.Declared"/>,
    /// <see cref="SenderVerification.UserDeclared"/>), never as <see cref="SenderVerification.Verified"/>. It counts while no later change of
    /// sender voids it. Only for the channel manager (422 <see cref="SenderErrorCodes.DeclarationNotApplicable"/>) and only once the delegation is accepted
    /// (403 <see cref="SenderErrorCodes.DelegationRequired"/>).
    /// </summary>
    Task<PropertySenderView> DeclareActivatedAsync(Guid propertyId, string userId, CancellationToken ct);
}

/// <summary>The state of a sender, as the host reads it. «Active» is shown only for a sender that was verified.</summary>
public enum SenderState
{
    /// <summary>Octorate says the police account is valid and its automatic send is on: verified.</summary>
    Active = 1,

    /// <summary>The host said it saw «Activated» in the Octorate panel and Octorate does not tell: the host's word, not verified.</summary>
    Declared = 2,

    /// <summary>The channel manager sends, but nothing proves it: Octorate does not expose it (or cannot be reached) and the host did not declare it.</summary>
    Unverified = 3,

    /// <summary>The sender chosen does not send: Octorate says the police account is not valid or its automatic send is off (or the sender is not available).</summary>
    Inactive = 4,

    /// <summary>No automatic sender is set up: the host sends the file (manual), or the property is outside Italy.</summary>
    NotConfigured = 5,
}

/// <summary>How the state of a sender is known.</summary>
public enum SenderVerification
{
    /// <summary>Read from Octorate just now (the state can be Active or Inactive).</summary>
    Verified = 1,

    /// <summary>The host's declaration; nothing confirmed it.</summary>
    UserDeclared = 2,

    /// <summary>CasaZen cannot know: the API does not expose it or could not be reached.</summary>
    Unverifiable = 3,

    /// <summary>There is no automatic sender to verify.</summary>
    NotApplicable = 4,
}

/// <summary>The answer of the connection test of a sender.</summary>
public enum SenderConnectionTestOutcome
{
    /// <summary>Octorate says the police account is valid and its automatic send is on.</summary>
    Verified = 1,

    /// <summary>Octorate says the police account is not valid or its automatic send is off: the host has to fix it in the panel.</summary>
    Inactive = 2,

    /// <summary>The API does not expose the police account, or could not be reached: the host can declare what it saw.</summary>
    Unverifiable = 3,

    /// <summary>Manual sender (or a property outside Italy): there is nothing to test.</summary>
    NotApplicable = 4,

    /// <summary>The sender «CasaZen sends» cannot be activated or tested in this release.</summary>
    NotAvailable = 5,
}

/// <summary>
/// The sender of a property with its state. No personal data: the credentials are only said to exist.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Sender">Who sends the Alloggiati Web communications.</param>
/// <param name="IstatSender">Who sends the ISTAT communications: always the same as <paramref name="Sender"/>.</param>
/// <param name="Source">A person's choice, one of the two defaults (PO-02) or not applicable.</param>
/// <param name="Applies">False for a property outside Italy: there is no sender to choose.</param>
/// <param name="State">Active, Declared, Unverified, Inactive or NotConfigured.</param>
/// <param name="Verification">How <paramref name="State"/> is known.</param>
/// <param name="Reason">A stable code that says why the state is not simply Active (<see cref="SenderReasonCodes"/>); <c>null</c> when Active or when there is nothing to say.</param>
/// <param name="PoliceAccountValid">Octorate's <c>policeAccount.valid</c>; <c>null</c> = the API did not expose it (or the property is not a channel-manager one).</param>
/// <param name="PoliceAutorun">Octorate's <c>policeAccount.autorun</c>; <c>null</c> = not exposed.</param>
/// <param name="PoliceLastCron">When Octorate's daily job last ran (UTC); <c>null</c> = not exposed.</param>
/// <param name="QuesturaCredentialsConfigured">True when Alloggiati Web credentials are stored for the property; their values are never returned.</param>
/// <param name="ChannelManagerAvailable">False without a confirmed listing on a connected Octorate account, or while <c>Features:OctorateConnector</c> is off.</param>
/// <param name="AvailableSenders">The senders the host can choose now (empty outside Italy).</param>
/// <param name="DelegationAccepted">True when the org accepted the current delegation text (<see cref="AlloggiatiDelegationTexts.CurrentVersion"/>).</param>
/// <param name="DelegationTextVersion">The version of the delegation text the org has to accept; the client shows that text.</param>
/// <param name="DeclaredAt">When the host declared the channel manager activated (UTC); <c>null</c> when it did not, or a later change of sender voided it.</param>
/// <param name="LastChangedAt">When a person last chose the sender (UTC); <c>null</c> for a default.</param>
public sealed record PropertySenderView(
    Guid PropertyId,
    ComplianceSenderType Sender,
    ComplianceSenderType IstatSender,
    SenderSource Source,
    bool Applies,
    SenderState State,
    SenderVerification Verification,
    string? Reason,
    bool? PoliceAccountValid,
    bool? PoliceAutorun,
    DateTime? PoliceLastCron,
    bool QuesturaCredentialsConfigured,
    bool ChannelManagerAvailable,
    IReadOnlyList<ComplianceSenderType> AvailableSenders,
    bool DelegationAccepted,
    string DelegationTextVersion,
    DateTime? DeclaredAt,
    DateTime? LastChangedAt);

/// <summary>The result of testing the sender of a property. Nothing in it is stored.</summary>
/// <param name="Sender">The sender that was tested.</param>
/// <param name="Outcome">Verified only with Octorate's proof.</param>
/// <param name="Reason">A stable code for the guidance when the outcome is not Verified (<see cref="SenderReasonCodes"/>); <c>null</c> otherwise.</param>
/// <param name="PoliceAccountValid">What Octorate answered for <c>policeAccount.valid</c>; <c>null</c> = not exposed.</param>
/// <param name="PoliceAutorun">What Octorate answered for <c>policeAccount.autorun</c>; <c>null</c> = not exposed.</param>
/// <param name="PoliceLastCron">What Octorate answered for <c>policeAccount.lastCron</c> (UTC); <c>null</c> = not exposed.</param>
/// <param name="CheckedAt">When the test ran (UTC).</param>
public sealed record SenderConnectionTestResult(
    ComplianceSenderType Sender,
    SenderConnectionTestOutcome Outcome,
    string? Reason,
    bool? PoliceAccountValid,
    bool? PoliceAutorun,
    DateTime? PoliceLastCron,
    DateTime CheckedAt);

/// <summary>The stable codes of the errors of the sender endpoints (<c>code</c> of the problem the API answers with).</summary>
public static class SenderErrorCodes
{
    /// <summary>409: the sender really changes and the host did not confirm that the other one will be off.</summary>
    public const string SwitchConfirmationRequired = "sender_switch_confirmation_required";

    /// <summary>422: ISTAT always follows Alloggiati Web; a request that names another sender for ISTAT is refused (ADR-029-R5).</summary>
    public const string IstatMismatch = "sender_istat_mismatch";

    /// <summary>403: an automatic sender needs the delegation of the transmission accepted first (ADR-015-R10).</summary>
    public const string DelegationRequired = "alloggiati_delegation_required";

    /// <summary>422: the sender «CasaZen sends» is not available in this release.</summary>
    public const string CasaZenNotAvailable = "sender_casazen_not_available";

    /// <summary>422: the channel manager cannot send for this property (no confirmed listing on a connected account, or the connector is off).</summary>
    public const string ChannelManagerNotAvailable = "sender_channel_manager_not_available";

    /// <summary>422: the declaration «Activated» is about the channel manager only.</summary>
    public const string DeclarationNotApplicable = "sender_declaration_not_applicable";
}

/// <summary>The stable codes that say why a sender is not simply Active (<see cref="PropertySenderView.Reason"/>, <see cref="SenderConnectionTestResult.Reason"/>).</summary>
public static class SenderReasonCodes
{
    /// <summary>Octorate says the police account of the accommodation is not valid.</summary>
    public const string PoliceAccountInvalid = "octorate_police_account_invalid";

    /// <summary>The police account is valid but Octorate's daily automatic send is off.</summary>
    public const string PoliceAutorunOff = "octorate_police_autorun_off";

    /// <summary>
    /// Octorate answered without the police account, or without the state of its automatic send (<c>valid</c> true and <c>autorun</c> left out):
    /// the API does not expose it for this accommodation, so nothing is proven.
    /// </summary>
    public const string PoliceNotExposed = "octorate_police_not_exposed";

    /// <summary>There is no usable connection to Octorate for the property (not connected, not mapped, or the connector is off).</summary>
    public const string NotConnected = "octorate_not_connected";

    /// <summary>Octorate could not be reached or refused the call: try again later.</summary>
    public const string Unavailable = "octorate_unavailable";

    /// <summary>The sender «CasaZen sends» is not available in this release.</summary>
    public const string CasaZenNotAvailable = "sender_casazen_not_available";
}

/// <summary>
/// The delegation text the sender wizard shows (E03/F01/US02/T02, AW-R02, ADR-015-R10). The text itself belongs to the legal documents; the
/// code of its version is what an org accepts and what the audit line keeps. Raise it when the text changes: every org is then asked
/// to accept it again before the next automatic sender is set.
/// </summary>
public static class AlloggiatiDelegationTexts
{
    /// <summary>The version of the delegation text in force (a code of at most 40 characters, see <see cref="IAlloggiatiDelegationService"/>).</summary>
    public const string CurrentVersion = "2026-11-v1";
}
