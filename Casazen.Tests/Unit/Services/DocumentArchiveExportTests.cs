using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-99, E07/F03/US03/T02: the ZIP of the document archive. The service tests run on the InMemory data of <see cref="DocumentArchiveWorld"/>;
/// the builder of the ZIP is also run alone on handmade rows, to prove the names, the index and the streaming.
/// </summary>
public class DocumentArchiveExportTests
{
    private static DocumentArchiveQuery EveryDocument { get; } = new(null, null, null, null, null, null, 1, DocumentArchiveQuery.MaxPageSize);

    private static async Task<(byte[] Zip, DocumentArchiveExportResult Result)> ExportAsync(
        DocumentArchiveWorld world,
        HostScope scope,
        DocumentArchiveAccess access,
        DocumentArchiveQuery? query = null,
        DocumentArchiveOptions? limits = null)
    {
        using var destination = new MemoryStream();
        var result = await world.Service(limits).ExportAsync(scope, access, query ?? EveryDocument, destination);
        return (destination.ToArray(), result);
    }

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e => Read(e), StringComparer.Ordinal);
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The rows of <c>indice.csv</c> (the header first): the cells split on the separator, the BOM dropped.</summary>
    private static List<string[]> IndexOf(byte[] zip)
    {
        var text = Encoding.UTF8.GetString(Entries(zip)[DocumentArchiveExport.IndexFileName]).TrimStart((char)0xFEFF);
        return text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(';')).ToList();
    }

    private static ArchiveRow Row(ArchiveDocumentType type, string title, DateOnly day, string? property = null, string? booking = null) => new()
    {
        Type = type,
        Id = Guid.NewGuid(),
        Title = title,
        DocumentDate = day,
        PropertyName = property,
        BookingCode = booking,
    };

    private static Func<ArchiveRow, CancellationToken, Task<ArchiveFile?>> Opening(string fileName, byte[] content) =>
        (_, _) => Task.FromResult<ArchiveFile?>(new ArchiveFile(new MemoryStream(content), fileName, "application/pdf"));

    // ---------------------------------------------------------------- what the ZIP holds

    /// <summary>
    /// PROOF TEST: permission rule. The ZIP holds exactly what the list shows to the same caller: no document of a type the caller may not read
    /// (even if the request asks for it), none of a property outside its scope, none of another org.
    /// </summary>
    [Fact]
    public async Task DocumentArchiveExport_OnlyWhatTheCallerMayRead_Test()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var withoutPayments = new DocumentArchiveAccess(
            new HashSet<ArchiveDocumentType> { ArchiveDocumentType.PropertyDocument, ArchiveDocumentType.Contract });

        // A collaborator «Solo alcuni» who may read the property documents and the leases, not the payments.
        var (zip, result) = await ExportAsync(world, world.Hosts.Restricted, withoutPayments);
        var names = Entries(zip).Keys.Order(StringComparer.Ordinal).ToList();

        Assert.Equal(
            [
                "contratti/2026-09-01-contratto-trullo.pdf",
                "documenti-immobile/2026-10-05-APE-Trullo.pdf",
                DocumentArchiveExport.IndexFileName,
            ],
            names);
        Assert.Equal(new DocumentArchiveExportResult(2, 2, result.Bytes), result);

        // The same documents the list shows to that caller, and the index names no other.
        var listed = await world.Service().ListAsync(world.Hosts.Restricted, withoutPayments, EveryDocument);
        Assert.Equal(listed.Items.Select(i => i.Title).Order(), IndexOf(zip).Skip(1).Select(row => row[4]).Order());

        // Asking for a type it may not read gives an archive with the index only: nothing of that type, nothing of the others.
        var asked = EveryDocument with { Types = [ArchiveDocumentType.TouristTax] };
        var (nothing, nothingResult) = await ExportAsync(world, world.Hosts.OrgWide, withoutPayments, asked);
        Assert.Equal([DocumentArchiveExport.IndexFileName], Entries(nothing).Keys);
        Assert.Equal(new DocumentArchiveExportResult(0, 0, 0), nothingResult);

        // And what is in another org or behind the property the caller was not given is nowhere in the file, names, bytes or index.
        var everything = Entries((await ExportAsync(world, world.Hosts.Restricted, DocumentArchiveWorld.Everything)).Zip);
        var text = string.Join('\n', everything.Keys) + string.Join('\n', everything.Values.Select(Encoding.UTF8.GetString));
        Assert.Contains("Trullo", text, StringComparison.Ordinal);
        foreach (var other in new[] { "Altrove", "Casa-Bianca", "Casa Bianca", "Ostuni", "Brindisi", "Milano" })
            Assert.DoesNotContain(other, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_IsAZipWithAFolderForEachType_TheFilesAsTheyWere_AndAnIndexWithTheirHash()
    {
        var world = await DocumentArchiveWorld.CreateAsync();

        var (zip, result) = await ExportAsync(world, world.Hosts.OrgWide, DocumentArchiveWorld.Everything);
        var entries = Entries(zip);

        Assert.Equal(
            [
                "imposta-di-soggiorno/2026-08-15-Ostuni.pdf",
                "contratti/2026-09-01-contratto-trullo.pdf",
                "contratti/2026-09-02-contratto-casa-bianca.pdf",
                "documenti-immobile/2026-10-05-APE-Trullo.pdf",
                "documenti-immobile/2026-10-06-Licenza-Casa-Bianca.pdf",
                DocumentArchiveExport.IndexFileName,
            ],
            ZipNamesInOrder(zip));
        Assert.Equal(DocumentArchiveWorld.Pdf("ricevuta Ostuni"), entries["imposta-di-soggiorno/2026-08-15-Ostuni.pdf"]);
        Assert.Equal(DocumentArchiveWorld.Pdf("contratto-trullo.pdf"), entries["contratti/2026-09-01-contratto-trullo.pdf"]);
        Assert.Equal(DocumentArchiveWorld.Pdf("Licenza-Casa-Bianca.pdf"), entries["documenti-immobile/2026-10-06-Licenza-Casa-Bianca.pdf"]);

        // The index at the root: type, date, property, booking code, title, file, size and SHA-256 of the file, in the order of the archive.
        var index = IndexOf(zip);
        Assert.Equal(["Tipo", "Data", "Immobile", "Codice prenotazione", "Titolo", "File", "Dimensione (byte)", "SHA-256"], index[0]);
        Assert.Equal(6, index.Count);
        var propertyRow = Assert.Single(index, row => row[5] == "documenti-immobile/2026-10-05-APE-Trullo.pdf");
        var propertyBytes = DocumentArchiveWorld.Pdf("APE-Trullo.pdf");
        Assert.Equal(
            ["documenti-immobile", "2026-10-05", "Trullo", string.Empty, "APE-Trullo.pdf", "documenti-immobile/2026-10-05-APE-Trullo.pdf",
                propertyBytes.Length.ToString(CultureInfo.InvariantCulture), Convert.ToHexStringLower(SHA256.HashData(propertyBytes))],
            propertyRow);
        var taxRow = Assert.Single(index, row => row[0] == "imposta-di-soggiorno");
        Assert.Equal(string.Empty, taxRow[2]);
        Assert.Equal("Ostuni", taxRow[4]);

        Assert.Equal(5, result.Documents);
        Assert.Equal(5, result.Files);
        Assert.Equal(entries.Where(e => e.Key != DocumentArchiveExport.IndexFileName).Sum(e => (long)e.Value.Length), result.Bytes);
    }

    [Fact]
    public async Task Export_IsFilteredLikeTheList_ByTypeDaysAndProperty()
    {
        var world = await DocumentArchiveWorld.CreateAsync();

        var contracts = await ExportAsync(
            world, world.Hosts.OrgWide, DocumentArchiveWorld.Everything, EveryDocument with { Types = [ArchiveDocumentType.Contract] });
        var september = await ExportAsync(
            world,
            world.Hosts.OrgWide,
            DocumentArchiveWorld.Everything,
            EveryDocument with { From = new DateOnly(2026, 9, 1), To = new DateOnly(2026, 9, 1) });
        var ofTheTrullo = await ExportAsync(
            world, world.Hosts.OrgWide, DocumentArchiveWorld.Everything, EveryDocument with { PropertyId = world.Hosts.Granted.Id });

        Assert.Equal(
            ["contratti/2026-09-01-contratto-trullo.pdf", "contratti/2026-09-02-contratto-casa-bianca.pdf"],
            ZipNamesInOrder(contracts.Zip).Where(n => n != DocumentArchiveExport.IndexFileName));
        Assert.Equal(["contratti/2026-09-01-contratto-trullo.pdf"], ZipNamesInOrder(september.Zip).Where(n => n != DocumentArchiveExport.IndexFileName));
        Assert.Equal(
            ["contratti/2026-09-01-contratto-trullo.pdf", "documenti-immobile/2026-10-05-APE-Trullo.pdf"],
            ZipNamesInOrder(ofTheTrullo.Zip).Where(n => n != DocumentArchiveExport.IndexFileName));
    }

    // ---------------------------------------------------------------- the caps

    [Fact]
    public async Task Export_BeyondTheFileCap_AnswersTheNumberFound_BeforeAByteIsWritten()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        using var destination = new MemoryStream();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service(new DocumentArchiveOptions { MaxExportFiles = 3 })
                .ExportAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, EveryDocument, destination));

        Assert.Equal(DocumentArchiveService.ExportTooLargeCode, refused.Code);
        Assert.Equal(5, refused.Extensions["fileCount"]);
        Assert.Equal(3, refused.Extensions["maxFiles"]);
        Assert.Equal(5, refused.MessageArgs[0]);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task Export_BeyondTheByteCap_IsRefusedTheSameWay_BeforeAByteIsWritten()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        using var destination = new MemoryStream();

        // The files of the leases and the receipt declare their size, and the property documents count as the largest upload; together they are
        // far above ten bytes.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service(new DocumentArchiveOptions { MaxExportBytes = 10 })
                .ExportAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, EveryDocument, destination));

        Assert.Equal(DocumentArchiveService.ExportTooLargeCode, refused.Code);
        Assert.Equal(5, refused.Extensions["fileCount"]);
        Assert.Equal(0, destination.Length);
    }

    /// <summary>
    /// A property document stores no size, so the byte cap counts it as the largest upload (10 MB), never as zero: the two property documents of
    /// the world do not fit in a cap of 15 MB, and fit exactly in one of 20 MB (a cap is exceeded, not reached).
    /// </summary>
    [Fact]
    public async Task Export_APropertyDocumentWithNoSize_CountsAsTheLargestUploadInTheByteCap()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var propertyDocuments = EveryDocument with { Types = [ArchiveDocumentType.PropertyDocument] };
        using var destination = new MemoryStream();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service(new DocumentArchiveOptions { MaxExportBytes = 15L * 1024 * 1024 })
                .ExportAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, propertyDocuments, destination));

        Assert.Equal(DocumentArchiveService.ExportTooLargeCode, refused.Code);
        Assert.Equal(2, refused.Extensions["fileCount"]);
        Assert.Equal(15L * 1024 * 1024, refused.Extensions["maxBytes"]);
        Assert.Equal(0, destination.Length);

        var (_, result) = await ExportAsync(
            world,
            world.Hosts.OrgWide,
            DocumentArchiveWorld.Everything,
            propertyDocuments,
            new DocumentArchiveOptions { MaxExportBytes = 2 * DocumentArchiveOptions.UndeclaredDocumentBytes });
        Assert.Equal(2, result.Documents);
    }

    /// <summary>
    /// With the default caps (300 files, 200 MB) an export of property documents is refused from the 21st file, far below the file cap: 21 times
    /// 10 MB is more than 200 MB. Twenty are allowed. Nothing is written before the refusal.
    /// </summary>
    [Fact]
    public async Task Export_OfPropertyDocumentsWithNoSize_IsRefusedByTheDefaultByteCapFromTheTwentyFirst()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var propertyDocuments = EveryDocument with { Types = [ArchiveDocumentType.PropertyDocument] };

        await world.AddPropertyDocumentsAsync(18);
        var (_, atTheCap) = await ExportAsync(world, world.Hosts.OrgWide, DocumentArchiveWorld.Everything, propertyDocuments);

        await world.AddPropertyDocumentsAsync(1);
        using var destination = new MemoryStream();
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service().ExportAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, propertyDocuments, destination));

        Assert.Equal(20, atTheCap.Documents);
        Assert.Equal(DocumentArchiveService.ExportTooLargeCode, refused.Code);
        Assert.Equal(21, refused.Extensions["fileCount"]);
        Assert.Equal(DocumentArchiveOptions.DefaultMaxExportFiles, refused.Extensions["maxFiles"]);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task Export_AtTheCaps_IsAllowed()
    {
        var world = await DocumentArchiveWorld.CreateAsync();

        var (zip, result) = await ExportAsync(
            world, world.Hosts.OrgWide, DocumentArchiveWorld.Everything, limits: new DocumentArchiveOptions { MaxExportFiles = 5 });

        Assert.Equal(5, result.Documents);
        Assert.Equal(6, Entries(zip).Count);
    }

    // ---------------------------------------------------------------- the builder of the ZIP

    [Fact]
    public async Task Export_ADocumentWithoutAFile_IsInTheIndexWithAnEmptyFileName()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        Assert.True(world.Storage.Remove(world.KeysByDocument[world.GrantedPropertyDocument]));

        var (zip, result) = await ExportAsync(
            world, world.Hosts.OrgWide, DocumentArchiveWorld.Everything, EveryDocument with { Types = [ArchiveDocumentType.PropertyDocument] });
        var index = IndexOf(zip);

        Assert.Equal(2, result.Documents);
        Assert.Equal(1, result.Files);
        Assert.Equal(["documenti-immobile/2026-10-06-Licenza-Casa-Bianca.pdf", DocumentArchiveExport.IndexFileName], ZipNamesInOrder(zip));
        var missing = Assert.Single(index, row => row[4] == "APE-Trullo.pdf");
        Assert.Equal([string.Empty, string.Empty, string.Empty], missing[5..]);
    }

    [Fact]
    public async Task Builder_WritesToAStreamThatCannotSeekNorWriteSynchronously_AndReadsEachFileInChunks()
    {
        var content = new byte[300_000];
        new Random(7).NextBytes(content);
        var reads = new List<int>();
        var rows = new[] { Row(ArchiveDocumentType.Contract, "grande.pdf", new DateOnly(2026, 1, 2)) };
        await using var destination = new ForwardOnlyStream();

        var result = await DocumentArchiveExport.WriteAsync(
            rows,
            (_, _) => Task.FromResult<ArchiveFile?>(new ArchiveFile(new RecordingReadStream(content, reads), "grande.pdf", "application/pdf")),
            destination,
            CancellationToken.None);

        Assert.False(destination.CanSeek);
        Assert.Equal(new DocumentArchiveExportResult(1, 1, content.Length), result);
        // The file was never read whole: many reads, none larger than one chunk.
        Assert.True(reads.Count > 3, $"read in {reads.Count} calls");
        Assert.All(reads, size => Assert.True(size <= 81_920, $"a read asked for {size} bytes"));
        Assert.Equal(content, Entries(destination.ToArray())["contratti/2026-01-02-grande.pdf"]);
    }

    [Fact]
    public async Task Builder_TheIndexNeutralisesATitleASpreadsheetWouldRunAsAFormula()
    {
        var rows = new[] { Row(ArchiveDocumentType.PropertyDocument, "=HYPERLINK(\"http://example.com\")", new DateOnly(2026, 1, 2), property: "+Casa") };
        using var destination = new MemoryStream();

        await DocumentArchiveExport.WriteAsync(rows, (_, _) => Task.FromResult<ArchiveFile?>(null), destination, CancellationToken.None);

        var text = Encoding.UTF8.GetString(Entries(destination.ToArray())[DocumentArchiveExport.IndexFileName]);
        Assert.Contains("'=HYPERLINK", text, StringComparison.Ordinal);
        Assert.Contains("'+Casa", text, StringComparison.Ordinal);
        Assert.DoesNotContain(";=HYPERLINK", text, StringComparison.Ordinal);
        Assert.DoesNotContain(";\"=HYPERLINK", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Builder_TwoDocumentsWithTheSameDayAndTitle_GetTwoNames()
    {
        var day = new DateOnly(2026, 3, 4);
        var rows = new[]
        {
            Row(ArchiveDocumentType.PropertyDocument, "APE.pdf", day),
            Row(ArchiveDocumentType.PropertyDocument, "ape.PDF", day),
            Row(ArchiveDocumentType.PropertyDocument, "APE.pdf", day),
        };
        using var destination = new MemoryStream();

        await DocumentArchiveExport.WriteAsync(rows, Opening("APE.pdf", [1, 2, 3]), destination, CancellationToken.None);

        Assert.Equal(
            [
                "documenti-immobile/2026-03-04-APE.pdf",
                "documenti-immobile/2026-03-04-ape-2.pdf",
                "documenti-immobile/2026-03-04-APE-3.pdf",
                DocumentArchiveExport.IndexFileName,
            ],
            ZipNamesInOrder(destination.ToArray()));
    }

    [Theory]
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData("a:b*c?d", "a-b-c-d")]
    [InlineData("S. Giovanni", "S.-Giovanni")]
    [InlineData("Tassa (2026)_bis", "Tassa-(2026)_bis")]
    [InlineData("  ...  ", "documento")]
    [InlineData("<>|\"", "documento")]
    [InlineData("Città di Forlì", "Città-di-Forlì")]
    public void Sanitize_KeepsLettersDigitsAndThePunctuationOfAName_AndNothingThatCanLeaveItsFolder(string title, string expected)
    {
        var name = DocumentArchiveExport.Sanitize(title);

        Assert.Equal(expected, name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.False(name.StartsWith('.'));
    }

    [Fact]
    public void Sanitize_CutsAVeryLongTitle()
    {
        var name = DocumentArchiveExport.Sanitize(new string('a', 400));

        Assert.Equal(100, name.Length);
    }

    [Fact]
    public void EntryName_TakesTheExtensionFromTheRealFile_NotFromTheTitle()
    {
        var used = new HashSet<string>();

        var ofACommune = DocumentArchiveExport.EntryName(
            Row(ArchiveDocumentType.TouristTax, "Ostuni", new DateOnly(2026, 8, 15)), "ricevuta.PDF", used);
        var withItsExtension = DocumentArchiveExport.EntryName(
            Row(ArchiveDocumentType.PropertyDocument, "APE casa.pdf", new DateOnly(2026, 8, 15)), "APE casa.pdf", used);
        var oddExtension = DocumentArchiveExport.EntryName(
            Row(ArchiveDocumentType.PropertyDocument, "nota", new DateOnly(2026, 8, 15)), "nota.a-b-c", used);

        Assert.Equal("imposta-di-soggiorno/2026-08-15-Ostuni.pdf", ofACommune);
        Assert.Equal("documenti-immobile/2026-08-15-APE-casa.pdf", withItsExtension);
        Assert.Equal("documenti-immobile/2026-08-15-nota", oddExtension);
    }

    /// <summary>The dates of the names and of the index are ISO whatever the culture of the server (here one with another calendar).</summary>
    [Fact]
    public void Date_IsIsoWhateverTheCultureOfTheServer()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");

            Assert.Equal("2026-11-06", DocumentArchiveExport.Date(new DateOnly(2026, 11, 6)));
            Assert.Equal("casazen-documenti-20260101-20261231.zip", DocumentArchiveController.ExportFileName(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void EveryFolderOfTheArchive_IsDefinedForEveryType_AndTheyAreAllDifferent()
    {
        var folders = Enum.GetValues<ArchiveDocumentType>().Select(DocumentArchiveExport.FolderOf).ToList();

        Assert.Equal(folders.Count, folders.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ["fatture", "ricevute", "note-di-credito", "ricevute-alloggiati", "prove-istat", "imposta-di-soggiorno", "contratti", "documenti-immobile"],
            folders);
    }

    private static List<string> ZipNamesInOrder(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>
    /// A destination that is written forward only, as the response of the API is: no seek, no length, no position, and no synchronous write or
    /// flush (Kestrel refuses them unless <c>AllowSynchronousIO</c> is on, and a ZIP written with the synchronous calls would fail in production).
    /// </summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _inner.ToArray();

        public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed. Call FlushAsync or set AllowSynchronousIO to true instead.");

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous operations are disallowed. Call WriteAsync or set AllowSynchronousIO to true instead.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);
    }

    /// <summary>A source file that records the size of every read asked of it.</summary>
    private sealed class RecordingReadStream(byte[] content, List<int> reads) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            reads.Add(count);
            return _inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reads.Add(buffer.Length);
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
