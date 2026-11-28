using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The ZIP of the document archive (NF-99, E07/F03/US03/T02), built with <c>System.IO.Compression</c> straight from the storage to the
/// destination: each file is read in chunks and written into its entry, so the archive is never held in memory and the files go one at a time (the
/// storage hands over one object at a time: in production S3 copies that single object in memory, 20 MB at most for a contract and 10 MB for a
/// property document; no disk is ever used). The
/// destination does not have to be seekable or to accept synchronous writes, and the response of the API is neither (see
/// <see cref="DeferredWriteStream"/>). A folder for each type, the files named <c>{date}-{title}</c> with the name made safe and unique, and
/// <c>indice.csv</c> at the root, written last because the SHA-256 of a file is known only once it was copied.
/// </summary>
internal static class DocumentArchiveExport
{
    public const string IndexFileName = "indice.csv";

    private const int CopyBufferBytes = 81_920;
    private const int MaxStemLength = 100;
    private const int MaxExtensionLength = 8;
    private const string FallbackStem = "documento";

    private static readonly object?[] IndexHeader =
        ["Tipo", "Data", "Immobile", "Codice prenotazione", "Titolo", "File", "Dimensione (byte)", "SHA-256"];

    public static string FolderOf(ArchiveDocumentType type) => type switch
    {
        ArchiveDocumentType.Invoice => "fatture",
        ArchiveDocumentType.Receipt => "ricevute",
        ArchiveDocumentType.CreditNote => "note-di-credito",
        ArchiveDocumentType.AlloggiatiReceipt => "ricevute-alloggiati",
        ArchiveDocumentType.IstatProof => "prove-istat",
        ArchiveDocumentType.TouristTax => "imposta-di-soggiorno",
        ArchiveDocumentType.Contract => "contratti",
        ArchiveDocumentType.PropertyDocument => "documenti-immobile",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown archive document type."),
    };

    public static async Task<DocumentArchiveExportResult> WriteAsync(
        IReadOnlyList<ArchiveRow> rows,
        Func<ArchiveRow, CancellationToken, Task<ArchiveFile?>> open,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(destination);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sink = new DeferredWriteStream(destination);
        var index = new List<IReadOnlyList<object?>>(rows.Count + 1) { IndexHeader };
        var files = 0;
        long total = 0;

        await using (var archive = await ZipArchive.CreateAsync(sink, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken))
        {
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? entryName = null;
                long? size = null;
                string? sha256 = null;
                var file = await open(row, cancellationToken);
                if (file is not null)
                {
                    await using var content = file.Content;
                    entryName = EntryName(row, file.FileName, names);
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    await using (var target = await entry.OpenAsync(cancellationToken))
                    {
                        (size, sha256) = await CopyAsync(content, target, cancellationToken);
                    }

                    files++;
                    total += size.Value;
                }

                index.Add([FolderOf(row.Type), Date(row.DocumentDate), row.PropertyName, row.BookingCode, row.Title, entryName, size, sha256]);
            }

            var indexEntry = archive.CreateEntry(IndexFileName, CompressionLevel.Fastest);
            await using (var target = await indexEntry.OpenAsync(cancellationToken))
            {
                await target.WriteAsync(CsvWriter.Write(index, CsvWriter.NoLimit), cancellationToken);
            }
        }

        await sink.FlushAsync(cancellationToken);
        return new DocumentArchiveExportResult(rows.Count, files, total);
    }

    internal static string EntryName(ArchiveRow row, string fileName, ISet<string> used)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(used);

        var extension = ExtensionOf(fileName);
        var title = row.Title.Trim();
        if (extension.Length > 0 && title.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            title = title[..^extension.Length];

        var stem = Sanitize(title);
        var prefix = $"{FolderOf(row.Type)}/{Date(row.DocumentDate)}-";
        for (var attempt = 1; ; attempt++)
        {
            var name = $"{prefix}{stem}{(attempt == 1 ? string.Empty : $"-{attempt}")}{extension}";
            if (used.Add(name))
                return name;
        }
    }

    internal static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var text = value;
        try
        {
            text = value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
        }

        var builder = new StringBuilder(text.Length);
        var separatorPending = false;
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '(' or ')' or '.')
            {
                if (separatorPending && builder.Length > 0)
                    builder.Append('-');

                builder.Append(character);
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
        }

        var stem = builder.ToString().Trim('.', '-');
        if (stem.Length > MaxStemLength)
            stem = stem[..MaxStemLength].TrimEnd('.', '-');

        return stem.Length == 0 ? FallbackStem : stem;
    }

    private static string ExtensionOf(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension.Length is > 1 and <= MaxExtensionLength && extension.Skip(1).All(char.IsAsciiLetterOrDigit) ? extension : string.Empty;
    }

    internal static string Date(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<(long Size, string Sha256)> CopyAsync(Stream source, Stream target, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long size = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                size += read;
            }

            return (size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal sealed class DeferredWriteStream(Stream destination) : Stream
    {
        private readonly ArrayBufferWriter<byte> _pending = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => _pending.Write(buffer);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await SendPendingAsync(cancellationToken);
            await destination.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await SendPendingAsync(cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private async Task SendPendingAsync(CancellationToken cancellationToken)
        {
            if (_pending.WrittenCount == 0)
                return;

            await destination.WriteAsync(_pending.WrittenMemory, cancellationToken);
            _pending.Clear();
        }
    }
}
