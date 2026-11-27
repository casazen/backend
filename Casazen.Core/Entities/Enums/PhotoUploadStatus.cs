namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the sending of one photo of a property to the channel manager stands (NF-32, ADR-036). Stored as an integer in
/// <c>PhotoUploadTasks.Status</c>, <b>append only</b>: never renumber a member.
/// </summary>
public enum PhotoUploadStatus
{
    /// <summary>The photo has to be sent.</summary>
    Pending = 0,

    /// <summary>The channel manager took the address and downloads the photo: its queue says when it is done.</summary>
    Uploading = 1,

    /// <summary>The photo is on the channel manager (its photo name is kept, so it is never sent again).</summary>
    Done = 2,

    /// <summary>The channel manager could not take the photo; the reason is kept.</summary>
    Error = 3,

    /// <summary>The photo left the gallery of the property: it has to be removed from the channel manager.</summary>
    RemovePending = 4,

    /// <summary>The photo was removed from the channel manager.</summary>
    Removed = 5,
}
