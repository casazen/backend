namespace Casazen.Core.Entities.Enums;

public enum PropertyICalImportStatus
{
    Success = 0,
    PartialFailure = 1,
    Failure = 2,

    /// <summary>A new import URL was saved and its first sync is queued (FD-16): the download runs in a background job.</summary>
    Syncing = 3,
}
