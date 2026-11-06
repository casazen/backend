using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>DocumentArchive</c> (NF-99, E07/F03/US03/T02): how much one export of the document archive may hold. An export is a ZIP streamed
/// from the storage to the response, so what bounds it is the time a download holds a connection: beyond either cap the export answers 422
/// <c>archive_export_too_large</c> with the number of documents found, before the first byte, and the host narrows the period or picks a type.
/// The numbers are checked at startup (<see cref="DocumentArchiveOptionsValidator"/>). Runbook: <c>docs/runbooks/deploy-checklist.md</c> § 2.10.
/// </summary>
public sealed class DocumentArchiveOptions
{
    public const string SectionName = "DocumentArchive";

    /// <summary>The documents of one export when nothing is configured.</summary>
    public const int DefaultMaxExportFiles = 300;

    /// <summary>The bytes of one export when nothing is configured: 200 MB.</summary>
    public const long DefaultMaxExportBytes = 200L * 1024 * 1024;

    /// <summary>The most documents the configuration may allow in one export.</summary>
    public const int HighestMaxExportFiles = 2_000;

    /// <summary>The fewest and the most bytes the configuration may allow in one export (1 MB, 2 GB).</summary>
    public const long LowestMaxExportBytes = 1024L * 1024;

    /// <inheritdoc cref="LowestMaxExportBytes"/>
    public const long HighestMaxExportBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// What a document that stores no size counts as in the byte cap of an export: 10 MB, the largest file an upload of a property document allows.
    /// The cap is an upper bound of the response, so a document of unknown size is counted at its worst case, never at zero.
    /// </summary>
    public const long UndeclaredDocumentBytes = 10L * 1024 * 1024;

    /// <summary>The most documents one export holds (1 to <see cref="HighestMaxExportFiles"/>). Env var <c>DocumentArchive__MaxExportFiles</c>, key <c>DocumentArchive:MaxExportFiles</c>.</summary>
    public int MaxExportFiles { get; set; } = DefaultMaxExportFiles;

    /// <summary>
    /// The most bytes one export holds, counted on the sizes the documents declare; a document that stores no size (every property document) counts
    /// as <see cref="UndeclaredDocumentBytes"/>, so with the default 200 MB an export of property documents holds at most 20 files. Env var
    /// <c>DocumentArchive__MaxExportBytes</c>, key <c>DocumentArchive:MaxExportBytes</c>.
    /// </summary>
    public long MaxExportBytes { get; set; } = DefaultMaxExportBytes;

    /// <summary>Configuration errors, empty when the section is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        if (MaxExportFiles is < 1 or > HighestMaxExportFiles)
            failures.Add($"DocumentArchive__MaxExportFiles must be between 1 and {HighestMaxExportFiles}.");

        if (MaxExportBytes is < LowestMaxExportBytes or > HighestMaxExportBytes)
            failures.Add($"DocumentArchive__MaxExportBytes must be between {LowestMaxExportBytes} and {HighestMaxExportBytes} bytes.");

        return failures;
    }
}

/// <summary>Refuses at startup a document archive configuration that is not valid, with the list of problems.</summary>
public sealed class DocumentArchiveOptionsValidator : IValidateOptions<DocumentArchiveOptions>
{
    public ValidateOptionsResult Validate(string? name, DocumentArchiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
