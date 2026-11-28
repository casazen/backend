namespace Casazen.Core.Options;

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

    public string TemplatesDirectory { get; set; } = DefaultTemplatesDirectory;

    public Dictionary<string, LeaseTemplateVariantOptions> Variants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class LeaseTemplateVariantOptions
{
    public const string DevStubVersionId = "dev-stub";

    public string VersionId { get; set; } = string.Empty;

    public bool Approved { get; set; }

    /// <summary>Evidence of the approval (who approved, opinion or protocol number). Required with
    /// <see cref="Approved"/> unless <see cref="ApprovedAt"/> is set.</summary>
    public string? ApprovalReference { get; set; }

    /// <summary>Date of the approval. Required with <see cref="Approved"/> unless <see cref="ApprovalReference"/> is set.</summary>
    public DateOnly? ApprovedAt { get; set; }
}
