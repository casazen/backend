namespace Casazen.Web.DTOs.Legal;

public class LegalDocumentDto
{
    /// <summary><c>tos</c>, <c>privacy</c> or <c>dpa</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>Date the version is in force; null while not configured.</summary>
    public DateTime? EffectiveAt { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    /// <summary>Optional external copy of the official text (https).</summary>
    public string? DocumentUrl { get; set; }

    /// <summary>True when the text (<see cref="ContentHtml"/>) or an external copy exists; false: "in preparation".</summary>
    public bool Available { get; set; }

    /// <summary>Sanitized HTML text of the version, provided by the product owner; null while missing.</summary>
    public string? ContentHtml { get; set; }

    /// <summary>Language of <see cref="ContentHtml"/>: the requested one, or Italian when no translation exists.</summary>
    public string? ContentLanguage { get; set; }
}

public class SubprocessorItemDto
{
    /// <summary>Stable identifier of the provider (e.g. <c>auth0</c>).</summary>
    public string? Key { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Stable identifier of the purpose for localized labels; null when the purpose is configured text.</summary>
    public string? PurposeKey { get; set; }

    public string Region { get; set; } = string.Empty;
    public string? Website { get; set; }

    /// <summary>Legal entity and registered office, as configured by the product owner.</summary>
    public string? Entity { get; set; }

    /// <summary>Legal basis of a transfer outside the EEA, when there is one (GDPR chapter V).</summary>
    public string? TransferMechanism { get; set; }

    /// <summary>Legal entity, location or transfer basis still to be completed by the product owner.</summary>
    public bool DetailsPending { get; set; }
}

public class SubprocessorsDocumentDto
{
    public string Version { get; set; } = string.Empty;
    public DateTime? EffectiveAt { get; set; }
    public IReadOnlyList<SubprocessorItemDto> Items { get; set; } = [];
}
