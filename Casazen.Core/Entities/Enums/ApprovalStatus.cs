namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Status of an item in the approval queue (NF-06). Items requiring human decision before taking effect.
/// </summary>
public enum ApprovalStatus
{
    /// <summary>Awaiting approval.</summary>
    Pending = 0,

    /// <summary>Approved by a user with approval permission.</summary>
    Approved = 1,

    /// <summary>Rejected by a user with approval permission.</summary>
    Rejected = 2,

    /// <summary>Expired after 7 days without approval.</summary>
    Expired = 3,
}
