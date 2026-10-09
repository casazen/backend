namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The stable code of what happened (<see cref="OrgActivityEntry.Type"/>, AM-02b). The list is the whole perimeter of the
/// activity log: an event is added here, in <c>OrgActivityCatalog</c> and in the runbook <c>docs/runbooks/org-team.md</c> (~20 at
/// most), never without. The client composes the sentence from the type and the ids; nothing here carries a name, an email,
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

    // ─── Plan and organization (account) ─────────────────────────────────────────────────────────────────

    /// <summary>The plan of the org changed. Subject: the org. Details: the tier before and after, and who or what changed it.</summary>
    PlanChanged = 20,

    /// <summary>The name of the org was changed. Subject: the org. No details: the name itself is never recorded.</summary>
    OrgNameChanged = 21,

    /// <summary>The public address (slug) of the org was changed. Subject: the org. No details: the slug itself is never recorded.</summary>
    OrgSlugChanged = 22,

    // ─── Property mode (reserved: PM-02) ─────────────────────────────────────────────────────────────────

    /// <summary><i>Reserved.</i> A change of the rental mode of a property was scheduled. Subject: the property.</summary>
    PropertyModeChangeScheduled = 40,

    /// <summary><i>Reserved.</i> A scheduled change of mode was cancelled. Subject: the property.</summary>
    PropertyModeChangeCancelled = 41,

    /// <summary><i>Reserved.</i> The mode of a property changed (the daily job: no actor). Subject: the property.</summary>
    PropertyModeChanged = 42,

    // ─── Trusted suppliers (reserved: the host's trusted suppliers) ──────────────────────────────────────

    /// <summary><i>Reserved.</i> A supplier was added to the trusted ones of the org. Subject: the supplier.</summary>
    TrustedSupplierAdded = 60,

    /// <summary><i>Reserved.</i> A supplier was removed from the trusted ones. Subject: the supplier.</summary>
    TrustedSupplierRemoved = 61,
}
