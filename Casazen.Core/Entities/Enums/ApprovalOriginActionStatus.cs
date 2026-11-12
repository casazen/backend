namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the action an approval queue item was waiting for stands (E05/F04/US01/T01, NF-68). Stored as its number in
/// <see cref="ApprovalQueueItem.OriginActionStatus"/>; the numbers never change.
/// </summary>
public enum ApprovalOriginActionStatus
{
    /// <summary>
    /// There is nothing to execute: the item is a pure decision (a type with no registered action), or it is an item of before the
    /// column existed (the migration gives every existing row this value).
    /// </summary>
    NotRequired = 0,

    /// <summary>The item has an action that runs when it is approved, and it has not run yet.</summary>
    Pending = 1,

    /// <summary>The action ran: the message went out, the campaign was approved and scheduled.</summary>
    Executed = 2,

    /// <summary>The last attempt of the action failed: the item stays pending, with the code in <see cref="ApprovalQueueItem.OriginActionError"/>.</summary>
    Failed = 3,
}
