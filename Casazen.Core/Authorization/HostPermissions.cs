namespace Casazen.Core.Authorization;

/// <summary>
/// The finer host permissions of AM-03, all of the <c>short-rent</c> context. They were carved out of the broad ones
/// (<c>property.write</c>, <c>guest.write</c>, <c>booking.write</c>) so that a role can be given the one action without the
/// rest: the Collaborator creates interventions but cannot change prices or CIN, handles guests but cannot erase them.
/// </summary>
public static class HostPermissions
{
    /// <summary>
    /// Create a request to a supplier (an intervention) for a stay, find a supplier for it, mark it paid. It used to be
    /// <c>property.write</c>. Given to the owner, the property managers and the collaborator.
    /// </summary>
    public const string ServiceRequestWrite = "servicerequest.write";

    /// <summary>
    /// Erase or anonymize a guest and change its consents (art. 17, 7 GDPR): the destructive and legal acts on a guest's data. It
    /// used to be <c>guest.write</c>, which also lets a person register and correct a guest. Given to the owner and the property
    /// managers, not to the collaborator.
    /// </summary>
    public const string GuestManage = "guest.manage";

    /// <summary>
    /// Register the guests of a stay for the police communication and declare the Alloggiati communication sent. It used to be
    /// <c>booking.write</c>, which also creates, cancels and moves bookings. Given to the owner and the property managers; the
    /// collaborator does not have it (a decision of the product owner whether it should).
    /// </summary>
    public const string AlloggiatiSubmit = "alloggiati.submit";

    /// <summary>
    /// Decide the items of the approval queue (E05/F04/US01/T01, NF-68, AC-NF68-5): list them, edit the text of a draft, approve it (which runs the
    /// action it was waiting for: a message goes to the guest, a campaign is approved) or reject it with a reason. It used to be the broad
    /// <c>property.write</c>, which a collaborator holds only in part. Given to the owner and the property managers (the administrators are property
    /// managers of the short-rent context); the collaborator and the accountant do not have it. It is one of <see cref="ShortRentFine"/>: the model
    /// seeds it for the owner's role and for the property manager's, and the migration <c>AddApprovalDecisionAndOriginAction</c> adds those rows.
    /// </summary>
    public const string ApprovalDecide = "approval.decide";

    /// <summary>
    /// What the short-rent owner and the property manager hold on top of the permissions they always had: the three that AM-03 carved out of the
    /// broad ones (the migration <c>AddPropertyMemberAccess</c> added them) and, since E05/F04/US01/T01, <see cref="ApprovalDecide"/> (the migration
    /// <c>AddApprovalDecisionAndOriginAction</c>).
    /// </summary>
    public static IReadOnlyList<string> ShortRentFine { get; } = [ServiceRequestWrite, GuestManage, AlloggiatiSubmit, ApprovalDecide];

    /// <summary>
    /// Decide the identity verification of a guest from the operator queue (NF-50, NF-24, ADR-013-R9, E03/F04/US02/T01): see the reasons of the
    /// automatic outcome, approve (which releases the lock code to the guest) or reject. Given to the owner, the administrators and the property
    /// managers, never to the collaborator nor to the accountant (AC-NF24-3). It is not one of <see cref="ShortRentFine"/>: those are the ones
    /// the migration of AM-03 added, this one comes with the migration <c>AddIdentityVerificationFields</c>.
    /// </summary>
    public const string VerificationDecision = "verification.decision";
}
