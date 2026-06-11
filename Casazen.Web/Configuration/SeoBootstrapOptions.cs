namespace Casazen.Web.Configuration;

public class SeoBootstrapOptions
{
    public const string SectionName = "Seo";

    /// <summary>When true and no SEO pages exist, enqueue generation for all registry comuni on startup.</summary>
    public bool BootstrapOnStartup { get; set; }

    /// <summary>After bootstrap generation, auto-publish Draft pages (counsel gate satisfied for batch &lt; 100).</summary>
    public bool AutoApproveAfterBootstrap { get; set; } = true;

    /// <summary>AI provider: Stub (template) until OpenAI/Azure wiring ships.</summary>
    public string AiProvider { get; set; } = "Stub";
}
