using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

public sealed class DocumentArchiveOptions
{
    public const string SectionName = "DocumentArchive";

    public const int DefaultMaxExportFiles = 300;

    public const long DefaultMaxExportBytes = 200L * 1024 * 1024;

    public const int HighestMaxExportFiles = 2_000;

    public const long LowestMaxExportBytes = 1024L * 1024;

    public const long HighestMaxExportBytes = 2L * 1024 * 1024 * 1024;

    public const long UndeclaredDocumentBytes = 10L * 1024 * 1024;

    public int MaxExportFiles { get; set; } = DefaultMaxExportFiles;

    public long MaxExportBytes { get; set; } = DefaultMaxExportBytes;

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

public sealed class DocumentArchiveOptionsValidator : IValidateOptions<DocumentArchiveOptions>
{
    public ValidateOptionsResult Validate(string? name, DocumentArchiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
