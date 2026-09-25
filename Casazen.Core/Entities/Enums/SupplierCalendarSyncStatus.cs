namespace Casazen.Core.Entities.Enums;

/// <summary>State of the iCal sync of a supplier (SU-15), like <see cref="PropertyICalImportStatus"/> for a property feed.</summary>
public enum SupplierCalendarSyncStatus
{
    /// <summary>No feed, or a feed never synced.</summary>
    None = 0,

    /// <summary>A sync is queued or running (URL saved, "sync now"): the download runs in a Hangfire job.</summary>
    Syncing = 1,

    /// <summary>The last sync applied the feed.</summary>
    Success = 2,

    /// <summary>The last sync failed; <c>SupplierProfile.CalendarSyncError</c> holds the stable code.</summary>
    Failure = 3,
}
