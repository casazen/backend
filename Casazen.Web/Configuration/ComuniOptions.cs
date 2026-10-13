namespace Casazen.Web.Configuration;

/// <summary>Section <c>Comuni</c> (SU-04): the official ISTAT comuni list. Runbook <c>docs/runbooks/comuni-istat.md</c>.</summary>
public class ComuniOptions
{
    public const string SectionName = "Comuni";

    /// <summary>
    /// Load the seed file of the deploy (<c>Data/Seeds/comuni-istat.csv</c>) at startup when the database has no list or an
    /// older one (by reference date). On by default; <c>Comuni__SeedOnStartup=false</c> leaves the import to an admin. Never
    /// replaces a list that is as recent or more.
    /// </summary>
    public bool SeedOnStartup { get; set; } = true;
}
