namespace Casazen.Web.DTOs.Legal;

public class LegalDocumentDto
{
    public string Key { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public DateTime? EffectiveAt { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    public string? DocumentUrl { get; set; }

    public bool Available { get; set; }

    public string? ContentHtml { get; set; }

    public string? ContentLanguage { get; set; }
}

public class SubprocessorItemDto
{
    public string? Key { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;

    public string? PurposeKey { get; set; }

    public string Region { get; set; } = string.Empty;
    public string? Website { get; set; }

    public string? Entity { get; set; }

    /// <summary>Legal basis of a transfer outside the EEA, when there is one (GDPR chapter V).</summary>
    public string? TransferMechanism { get; set; }

    public bool DetailsPending { get; set; }
}

public class SubprocessorsDocumentDto
{
    public string Version { get; set; } = string.Empty;
    public DateTime? EffectiveAt { get; set; }
    public IReadOnlyList<SubprocessorItemDto> Items { get; set; } = [];
}
