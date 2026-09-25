namespace Casazen.Web.Configuration;

public class SeoBootstrapOptions
{
    public const string SectionName = "Seo";

    /// <summary>
    /// When true and no SEO pages exist, enqueue generation for all registry comuni on startup. The pages are drafts:
    /// nothing is published before an admin approves it (SE-01; the old <c>AutoApproveAfterBootstrap</c> is gone).
    /// </summary>
    public bool BootstrapOnStartup { get; set; }
}
