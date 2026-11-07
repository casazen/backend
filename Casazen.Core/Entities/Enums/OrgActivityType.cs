namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The stable code of what happened (<see cref="OrgActivityEntry.Type"/>, AM-02b). The list is the whole perimeter of the
/// activity log: an event is added here, in <c>OrgActivityCatalog</c> and in the runbook <c>docs/runbooks/org-team.md</c>, never
/// without (about 30 in the end, decision AM-D18 raised from 25: the catalog is closed and grows one row at a time, each written by the task that
/// introduces the action). The client composes the sentence from the type and the ids; nothing here carries a name, an email,
/// an amount or a free text. Persisted as int, explicit values in groups of ten: append-only, never reorder or reuse a
/// value. The ones marked <i>reserved</i> have their code fixed now and are written by the task that builds the feature.
/// </summary>
public enum OrgActivityType
{
    // ─── People (account) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A person was invited. Subject: the invitation. Details: the role.</summary>
    MemberInvited = 1,

    /// <summary>A pending invitation was withdrawn. Subject: the invitation. Details: the role.</summary>
    InvitationRevoked = 2,

    /// <summary>The invited person accepted and became a member (the actor is that person). Subject: the invitation. Details: the role.</summary>
    InvitationAccepted = 3,

    /// <summary>A member was given another role. Subject: the member. Details: the role before and after.</summary>
    MemberRoleChanged = 4,

    /// <summary>A member was deactivated. Subject: the member. Details: its role.</summary>
    MemberDeactivated = 5,

    /// <summary>A deactivated member got its access back. Subject: the member. Details: its role.</summary>
    MemberReactivated = 6,

    /// <summary>A member was taken out of the org. Subject: the member. Details: its role.</summary>
    MemberRemoved = 7,

    /// <summary>
    /// The properties a member reaches were changed (AM-03: who gave what to whom). Subject: the member. Details: the scope
    /// the member now has and how many properties were given and taken away.
    /// </summary>
    MemberPropertyAccessChanged = 8,

    /// <summary>
    /// A member asked the administrators for access to an area or a page. The actor is the member; the note it wrote goes in
    /// the email to the administrators and nowhere else. Subject: the org. Details: the area or page asked for.
    /// </summary>
    AccessRequested = 9,

    /// <summary>
    /// The areas a member works in were changed (AM-02c). Subject: the member. Details: the areas before and after, and how many
    /// properties lost their person in charge with it (only when some did).
    /// </summary>
    MemberAreasChanged = 10,

    // ─── Plan and organization (account) ─────────────────────────────────────────────────────────────────

    /// <summary>The plan of the org changed. Subject: the org. Details: the tier before and after, and who or what changed it.</summary>
    PlanChanged = 20,

    /// <summary>The name of the org was changed. Subject: the org. No details: the name itself is never recorded.</summary>
    OrgNameChanged = 21,

    /// <summary>The public address (slug) of the org was changed. Subject: the org. No details: the slug itself is never recorded.</summary>
    OrgSlugChanged = 22,

    /// <summary>Tier was assigned to the org (admin-only in phase 1, ADR-010-R7). Subject: the org. Details: to-tier.</summary>
    TierAssigned = 23,

    /// <summary>
    /// The data of the company was changed (AM-08b-b): the legal name or the registered office of the operator, the phone or the
    /// language of the account. Subject: the org. No details: neither what changed nor the values are recorded. The contact email
    /// is not part of it (it has never had a line: the log would carry an address).
    /// </summary>
    OrgSettingsChanged = 24,

    // ─── Property mode (PM-02, written since AM-02c) ─────────────────────────────────────────────────────

    /// <summary>
    /// A change of the rental mode of a property was scheduled (AM-02c, written by <c>PropertyModeService</c>). Subject: the
    /// property. Details: the mode before and after and the day it takes effect. The area is the one of the mode the property is in.
    /// </summary>
    PropertyModeChangeScheduled = 40,

    /// <summary>A scheduled change of mode was cancelled (AM-02c). Subject: the property. Details: the mode before and after. The area is the property's.</summary>
    PropertyModeChangeCancelled = 41,

    /// <summary>
    /// The mode of a property changed (AM-02c; the daily job: no actor). Subject: the property. Details: the mode before and
    /// after. The area is the one of the mode the property is in now.
    /// </summary>
    PropertyModeChanged = 42,

    /// <summary>
    /// A host activated a property while some of its compliance reminders were still open (the required documents, the safety
    /// sheet) and declared, with the activation, to know that they are the host's own obligations and to complete them later
    /// (PO decision 2026-11-01, option A: only the base data and the CIN block the activation). Written by
    /// <c>PropertyComplianceStatusService</c>. Subject: the property. Details: the reminders open at that moment.
    /// </summary>
    PropertyActivatedWithReminders = 43,

    // ─── Trusted suppliers (reserved: the host's trusted suppliers) ──────────────────────────────────────

    /// <summary><i>Reserved.</i> A supplier was added to the trusted ones of the org. Subject: the supplier.</summary>
    TrustedSupplierAdded = 60,

    /// <summary><i>Reserved.</i> A supplier was removed from the trusted ones. Subject: the supplier.</summary>
    TrustedSupplierRemoved = 61,

    // ─── ConcordatoFacile (account) ──────────────────────────────────────────────────────────────────────

    /// <summary>An admin manually granted a ConcordatoFacile gift to an org. Subject: the org. No details.</summary>
    AdminGrantedConcordatoFacileGift = 70,

    // ─── Police and ISTAT communications (short rent, E03/F01/US05/T02) ───────────────────────────────

    /// <summary>
    /// Who sends the police (Alloggiati Web) and ISTAT communications of a property changed (ADR-029-R4: the audit of the switch),
    /// written by <c>PropertySenderStore.SetAsync</c>, the one write path. Subject: the property. Details: the sender before and after
    /// (<c>ChannelManager</c>, <c>CasaZen</c>, <c>Manual</c>); before a first choice, the sender before is the default the property had.
    /// </summary>
    PropertySenderChanged = 80,

    /// <summary>
    /// The org delegated the Alloggiati transmission to CasaZen, its tool (AW-R02, ADR-015-R10), written by
    /// <c>AlloggiatiDelegationService.GrantAsync</c>. Subject: the org. Details: the version of the delegation text, never the text.
    /// </summary>
    AlloggiatiDelegationGranted = 81,

    /// <summary>
    /// The org revoked the delegation: no new send, the receipts already acquired stay retrievable. Written by
    /// <c>AlloggiatiDelegationService.RevokeAsync</c>. Subject: the org. No details.
    /// </summary>
    AlloggiatiDelegationRevoked = 82,

    // ─── Compliance handoff to the channel manager (E03/F05/US01/T01, CHK-15) ────────────────────────────

    /// <summary>
    /// A host started the guided check of an Alloggiati or ISTAT outcome in the connected area of Octorate (the «Cassetto Compliance»):
    /// CasaZen prepared the allowlisted address and the guide. Written by <c>OctorateComplianceHandoffService</c>, in the only save of the
    /// call; the handoff changes nothing else and records no outcome. Subject: the property. Details: the topic (<c>Alloggiati</c> or
    /// <c>Istat</c>), the opaque handoff id, and, when the request had them, the booking id and the period. The area is short-rent.
    /// </summary>
    OctorateComplianceHandoffStarted = 100,

    // ─── «Do it for me» (account, E10/F01/US03/T01, NF-15) ─────────────────────────────────────────────

    /// <summary>
    /// A host asked CasaZen support to do a part, or a step of the Octorate panel, of the activation for it («Do it for me», UX-15, AC-NF15-1).
    /// Written by <c>OnboardingAssistService.RequestAsync</c>, in the save that stores the request. Subject: the org. Details: the part, the context
    /// of the request and its status (<c>requested</c>), short codes only: never the draft, a note or a person. The actor is the host.
    /// </summary>
    OnboardingAssistRequested = 110,

    /// <summary>
    /// CasaZen staff took, completed or closed a «Do it for me» request, or changed its note (AC-NF15-3). Written by
    /// <c>OnboardingAssistService.UpdateAsync</c> in the <b>org of the request</b>, not in the staff user's, with the staff user as the actor.
    /// Subject: the org. Details: the part, the context and the new status (<c>inProgress</c>, <c>done</c>, <c>cancelled</c>); the note is never
    /// recorded.
    /// </summary>
    OnboardingAssistUpdated = 111,

    // ─── Messages to the guests (hub of messages, E05/F02/US02/T01) ──────────────────────────────────────

    /// <summary>
    /// The contact of the guest of ONE booking was read from the channel manager to write to the guest (<c>POST /rest/v2/reservation/{acc}/{refer}/contactInfo</c>:
    /// Octorate releases the address only on request and records every read, so CasaZen records who asked as well). Written by
    /// <c>MessageReplyService.StartConversationAsync</c>, in the save that follows the read, whether or not the guest had an address. Subject: the booking.
    /// Details: the purpose (<c>message_to_guest</c>). The area is short-rent. Never the address, the phone number or a word of the message.
    /// </summary>
    ContactInfoRead = 120,

    // ─── Owner onboarding of an agency (E10/F02/US02) ────────────────────────────────────────────────────

    /// <summary>
    /// An owner file was imported and changed something (owners created or completed, ownership periods written). Written by
    /// <c>OwnerOnboardingService.ImportAsync</c>, in the save of the import, only when something changed (an import that finds everything done
    /// records nothing, so a second import of the same file leaves no line). Subject: the org. Details: how many rows the file had, how many it
    /// changed and the first 32 characters of the fingerprint of the file. Never a name, an identifier or a cell of the file.
    /// </summary>
    OwnerImportApplied = 130,

    /// <summary>
    /// An invitation of a property owner to the owner portal changed: it was sent, sent again (which replaces the previous link), accepted, withdrawn
    /// or it lapsed (ADR-011-R11). Written by <c>OwnerInvitationService</c> in the unit of work of the change. Subject: the invitation. Details: the
    /// <c>status</c> (<c>sent</c>, <c>resent</c>, <c>accepted</c>, <c>revoked</c>). Never the address, the name of the owner or the link.
    /// </summary>
    OwnerInvitationChanged = 140,
}
