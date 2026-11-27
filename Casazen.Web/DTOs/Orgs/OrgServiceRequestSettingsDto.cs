namespace Casazen.Web.DTOs.Orgs;

public class OrgServiceRequestSettingsDto
{
    /// <summary>
    /// Whether a request the org sends to a supplier is also sent to up to three backup suppliers, declared as such: the chosen supplier has
    /// one hour to take the job, and the first backup that accepts has it if the chosen supplier does not. <b>On by default</b>, because the
    /// rule is general. A single request can still turn it off (<c>useBackupSuppliers: false</c> in the body of the request). It only
    /// matters while the feature flag <c>SupplierRequestBackups</c> is on.
    /// </summary>
    public bool UseBackupSuppliers { get; set; } = true;
}
