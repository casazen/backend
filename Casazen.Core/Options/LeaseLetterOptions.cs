namespace Casazen.Core.Options;

/// <summary>
/// The letters CasaZen prepares for the landlord of a long-term lease (LR-OPS-1): the notice of non-renewal, the acknowledgement of the
/// tenant's withdrawal, and the ones of the next operations. Same scheme as the contract templates (<see cref="LeaseTemplateOptions"/>): the
/// texts are files, <c>{LettersDirectory}/{Kind}/{VersionId}.md</c>, a letter is <b>not approved by default</b> and the PDF of a letter that is not
/// approved carries the watermark «BOZZA — testo non ancora approvato». The approval is a deliberate act of the product owner on the host
/// (environment variables <c>LeaseLetters__Variants__{Kind}__{VersionId,Approved,ApprovalReference,ApprovedAt}</c>): the repository never
/// approves a letter. Runbook: <c>docs/runbooks/lease-operations.md</c>.
/// </summary>
public class LeaseLetterOptions
{
    public const string SectionName = "LeaseLetters";
    public const string DefaultLettersDirectory = "LeaseLetters";

    /// <summary>Folder of the letter files, absolute or relative to the content root of the API.</summary>
    public string LettersDirectory { get; set; } = DefaultLettersDirectory;

    /// <summary>
    /// One entry per letter kind (the names of <c>LeaseLetterKind</c>). A kind without an entry, or without a <c>VersionId</c>, uses the
    /// draft text shipped with the API and is not approved; a <c>VersionId</c> names the file the product owner had reviewed.
    /// </summary>
    public Dictionary<string, LeaseTemplateVariantOptions> Variants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
