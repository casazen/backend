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
    /// The sender of the property with its state and verification, the police fields Octorate exposes (one read of the police account of the
    /// accommodation, only for the channel manager and bounded in time: a failure leaves them <c>null</c> and never fails the call), whether Questura
    /// credentials exist (never their value) and whether the channel manager can be chosen. Throws <c>NotFoundException</c>
    /// (<c>property_not_found</c>) for a property the caller's org does not have.
    /// </summary>
    Task<PropertySenderView> GetAsync(Guid propertyId, CancellationToken ct);

    /// <summary>
    /// Changes the sender of the property. In order: 422 <see cref="SenderErrorCodes.IstatMismatch"/> when <paramref name="istatSender"/> is given and is
    /// not <paramref name="newSender"/> (ISTAT always follows Alloggiati); when <paramref name="delegationConsent"/> is true, 422
    /// <see cref="SenderErrorCodes.DelegationTextVersionRequired"/> without <paramref name="delegationTextVersion"/> and 409
    /// <see cref="SenderErrorCodes.DelegationTextOutdated"/> when it is not <see cref="AlloggiatiDelegationTexts.CurrentVersion"/> (the consent
    /// proves the text the person read, so it names it, and it has to be the text in force); 404 <c>property_not_found</c>; 422
    /// <c>sender_not_applicable</c> outside Italy; 422 <see cref="SenderErrorCodes.CasaZenNotAvailable"/> for CasaZen; 422
    /// <see cref="SenderErrorCodes.ChannelManagerNotAvailable"/> for a channel manager the property cannot use; 409
    /// <see cref="SenderErrorCodes.SwitchConfirmationRequired"/> when the sender really changes and
    /// <paramref name="confirmed"/> is false (the problem carries <c>currentSender</c> and <c>newSender</c>); 403
    /// <see cref="SenderErrorCodes.DelegationRequired"/> when an automatic sender is asked and the org has not accepted the current delegation text and
    /// <paramref name="delegationConsent"/> is false. Only then are the consent recorded (<c>IAlloggiatiDelegationService.GrantAsync</c>, with
    /// the version the person read) and the sender written (<c>IPropertySenderStore.SetAsync</c>, which records the audit line of the change
    /// in the same transaction). A repeat of the sender already in effect changes and records nothing.
    /// </summary>
    Task<PropertySenderView> SwitchAsync(
        Guid propertyId,
        ComplianceSenderType newSender,
        bool confirmed,
        bool delegationConsent,
        string userId,
        CancellationToken ct,
        ComplianceSenderType? istatSender = null,
        string? delegationTextVersion = null);

    /// <summary>
    /// <see cref="SwitchAsync"/> without the view: the same checks, in the same order and with the same errors, the same consent and the same write
    /// (<c>IPropertySenderStore.SetAsync</c> with its audit line), and it answers the sender as stored, <b>without the read of the police account on
    /// Octorate</b> that builds the view. For a caller that discards the view, like the bulk configuration of an agency (E10/F02/US01/T01), which
    /// switches hundreds of properties one after the other in one request: the read would cost up to eight seconds of waiting for each of them for
    /// an answer nobody uses.
    /// </summary>
    Task<PropertySenderInfo> SwitchWithoutViewAsync(
        Guid propertyId,
        ComplianceSenderType newSender,
        bool confirmed,
        bool delegationConsent,
        string userId,
        CancellationToken ct,
        ComplianceSenderType? istatSender = null,
        string? delegationTextVersion = null);

    /// <summary>
    /// What <see cref="SwitchAsync"/> would do for <paramref name="newSender"/> if the host confirmed the switch and gave no new consent to the
    /// delegation (E10/F02/US01/T01, the bulk configuration): the same checks in the same order, with the confirmation taken as given, and
    /// <b>nothing is written</b>: no sender row (not even for a property that only has the default), no consent, no audit line, and no call to
    /// Octorate. It throws what <see cref="SwitchAsync"/> would throw (<c>property_not_found</c>, <c>sender_not_applicable</c>,
    /// <see cref="SenderErrorCodes.IstatMismatch"/>, <see cref="SenderErrorCodes.CasaZenNotAvailable"/>,
    /// <see cref="SenderErrorCodes.ChannelManagerNotAvailable"/>, <see cref="SenderErrorCodes.DelegationRequired"/>) and otherwise answers the sender in
    /// effect and the one asked, so the caller knows whether the switch would change anything.
    /// </summary>
    Task<SenderSwitchCheck> CheckSwitchAsync(
        Guid propertyId,
        ComplianceSenderType newSender,
        CancellationToken ct,
        ComplianceSenderType? istatSender = null);

    /// <summary>
    /// Tests the sender. Channel manager: reads the police account of the accommodation on Octorate (bounded in time, like the read of the
    /// sender: a slow Octorate is <see cref="SenderConnectionTestOutcome.Unverifiable"/>, not an error) and answers <see cref="SenderConnectionTestOutcome.Verified"/> only when the
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

public enum SenderState
{
    Active = 1,

    Declared = 2,

    Unverified = 3,

    Inactive = 4,

    NotConfigured = 5,
}

public enum SenderVerification
{
    Verified = 1,

    UserDeclared = 2,

    Unverifiable = 3,

    NotApplicable = 4,
}

public enum SenderConnectionTestOutcome
{
    Verified = 1,

    Inactive = 2,

    Unverifiable = 3,

    NotApplicable = 4,

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
/// <param name="AutoSendGuestsToOctorate">
/// An org-wide setting, not the property's (E20/F01/US01/T01, decision O1): the org lets CasaZen write the guests of a stay to the channel manager as
/// soon as their data is complete and the identity check passed (<see cref="IOrgGuestAutoSendSetting"/>). Off by default.
/// </param>
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
    DateTime? LastChangedAt,
    bool AutoSendGuestsToOctorate = false);

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

/// <summary>
/// The answer of <see cref="IPropertySenderService.CheckSwitchAsync"/>: the sender in effect (the stored one, or the default) and the one asked.
/// </summary>
/// <param name="Current">The sender in effect for the Alloggiati and ISTAT communications.</param>
/// <param name="New">The sender asked.</param>
public sealed record SenderSwitchCheck(ComplianceSenderType Current, ComplianceSenderType New)
{
    public bool WouldChange => Current != New;
}

public static class SenderErrorCodes
{
    public const string SwitchConfirmationRequired = "sender_switch_confirmation_required";

    /// <summary>422: ISTAT always follows Alloggiati Web; a request that names another sender for ISTAT is refused (ADR-029-R5).</summary>
    public const string IstatMismatch = "sender_istat_mismatch";

    public const string DelegationRequired = "alloggiati_delegation_required";

    public const string DelegationTextVersionRequired = "alloggiati_delegation_text_version_required";

    /// <summary>
    /// 409: the consent is about another version than the text in force (it changed since the screen was loaded, or the client is old): the
    /// client reads the sender again and shows the text of <see cref="PropertySenderView.DelegationTextVersion"/>.
    /// </summary>
    public const string DelegationTextOutdated = "alloggiati_delegation_text_outdated";

    public const string CasaZenNotAvailable = "sender_casazen_not_available";

    public const string ChannelManagerNotAvailable = "sender_channel_manager_not_available";

    public const string DeclarationNotApplicable = "sender_declaration_not_applicable";
}

public static class SenderReasonCodes
{
    public const string PoliceAccountInvalid = "octorate_police_account_invalid";

    public const string PoliceAutorunOff = "octorate_police_autorun_off";

    public const string PoliceNotExposed = "octorate_police_not_exposed";

    public const string NotConnected = "octorate_not_connected";

    public const string Unavailable = "octorate_unavailable";

    public const string CasaZenNotAvailable = "sender_casazen_not_available";
}

/// <summary>
/// The delegation text the sender wizard shows (E03/F01/US02/T02, AW-R02, ADR-015-R10). The text itself belongs to the legal documents; the
/// code of its version is what an org accepts and what the audit line keeps. Raise it when the text changes: every org is then asked
/// to accept it again before the next automatic sender is set, and a client that still shows the old text is refused (409
/// <see cref="SenderErrorCodes.DelegationTextOutdated"/>) instead of recording a consent to a text the person did not read.
/// </summary>
public static class AlloggiatiDelegationTexts
{
    public const string CurrentVersion = "2026-11-v1";

    public const int VersionMaxLength = 40;
}
