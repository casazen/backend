namespace Casazen.Core.Options;

/// <summary>
/// Delega shown before a provider filing (LT-01). Whether the provider path exists is not an option here: it is the
/// feature flag <c>Features:RliProvider</c> plus a configured provider (<c>RliProviderFiling</c>).
/// </summary>
public class RliOptions
{
    public const string SectionName = "Rli";

    public string TosVersion { get; set; } = "2026-08-rli-delega-bozza";

    public string AttestationText { get; set; } =
        "Dichiaro di essere il locatore o un intermediario da me autorizzato e che la responsabilita del deposito RLI resta a me / al mio intermediario abilitato. CasaZen agisce solo come software di supporto. Testo bozza da confermare con legale.";
}

/// <summary>
/// Lease contract templates (LT-03, A7-03). The clause texts are files provided by the product owner, one per
/// fiscal regime and version: <c>{TemplatesDirectory}/{FiscalRegime}/{VersionId}.md</c>. A regime produces the final
/// contract (signature, registration) only when its template is complete and approved; otherwise only a preview
/// marked BOZZA. Format and approval steps: <c>docs/runbooks/lease-contract-templates.md</c>.
/// </summary>
public class LeaseTemplateOptions
{
    public const string SectionName = "LeaseTemplates";
    public const string DefaultTemplatesDirectory = "LeaseTemplates";

    /// <summary>Folder of the template files, absolute or relative to the content root of the API.</summary>
    public string TemplatesDirectory { get; set; } = DefaultTemplatesDirectory;

    /// <summary>One entry per <c>FiscalRegime</c> name. A regime without an entry has no template.</summary>
    public Dictionary<string, LeaseTemplateVariantOptions> Variants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Template version in use for a fiscal regime and its approval. Not approved by default: the approval is a
/// deliberate act that names the exact version a lawyer reviewed and its evidence (reference or date).
/// </summary>
public class LeaseTemplateVariantOptions
{
    /// <summary>Version of the old bypass (A7-03). Never a valid approval; in Production it stops the startup.</summary>
    public const string DevStubVersionId = "dev-stub";

    /// <summary>Template file to use, without extension (<c>{FiscalRegime}/{VersionId}.md</c>). Empty: no template.</summary>
    public string VersionId { get; set; } = string.Empty;

    /// <summary>True only when a lawyer approved exactly <see cref="VersionId"/>.</summary>
    public bool Approved { get; set; }

    /// <summary>Evidence of the approval (who approved, opinion or protocol number). Required with
    /// <see cref="Approved"/> unless <see cref="ApprovedAt"/> is set.</summary>
    public string? ApprovalReference { get; set; }

    /// <summary>Date of the approval. Required with <see cref="Approved"/> unless <see cref="ApprovalReference"/> is set.</summary>
    public DateOnly? ApprovedAt { get; set; }
}
