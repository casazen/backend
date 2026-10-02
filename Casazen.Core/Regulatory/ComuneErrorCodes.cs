namespace Casazen.Core.Regulatory;

/// <summary>Stable error codes (API <c>code</c>) and message keys of the official comuni list (SU-04).</summary>
public static class ComuneErrorCodes
{
    /// <summary>422: the list is not imported yet, so no comune code can be validated or chosen (<c>docs/runbooks/comuni-istat.md</c>).</summary>
    public const string DatasetUnavailable = "comuni_dataset_unavailable";

    public const string DatasetUnavailableMessageKey = "ComuniDatasetUnavailable";

    /// <summary>422: the ISTAT code is not a comune of the list (or no longer an active one).</summary>
    public const string IstatUnknown = "comune_istat_unknown";

    public const string IstatUnknownMessageKey = "ComuneIstatUnknown";

    /// <summary>422: a supplier chose more comuni than <see cref="MaxSupplierComuni"/>.</summary>
    public const string TooMany = "comuni_too_many";

    public const string TooManyMessageKey = "ComuniTooMany";

    /// <summary>Comuni one supplier profile can list (a sanity limit of the application).</summary>
    public const int MaxSupplierComuni = 100;
}
