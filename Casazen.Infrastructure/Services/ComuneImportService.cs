using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Import of the official ISTAT comuni list (SU-04). The same code loads the file an admin uploads and the seed file of the
/// deploy. File format (<c>docs/runbooks/comuni-istat.md</c>): the "Elenco dei comuni italiani" CSV of ISTAT, with its own
/// headers, separator <c>;</c> (also <c>,</c>, tab, <c>|</c>), UTF-8 or Windows-1252; a header that differs in wording or order is
/// found by its synonyms (a variant of the same official list, e.g. the ANPR archive), a column that cannot be found is
/// refused by name, never guessed.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>All or nothing</b>: one invalid line rejects the file (errors by line and stable code, at most
/// <see cref="MaxReportedErrors"/> listed).</item>
/// <item><b>Idempotent</b>: rows are upserted by ISTAT code and a row that did not change is not written, so the same file
/// again changes nothing; the import is logged anyway (file, SHA-256, version, reference date, counts).</item>
/// <item><b>Full list by default</b>: the active comuni that the file no longer has are deactivated, never deleted (a stored
/// code keeps resolving); a file declared partial leaves them alone.</item>
/// <item>A list older (by reference date) than the one already imported is refused, so an old file never undoes a newer one.</item>
/// <item>One import at a time, across API instances (advisory lock).</item>
/// </list>
/// </remarks>
public class ComuneImportService(
    AppDbContext db,
    ILogger<ComuneImportService> logger,
    TimeProvider? timeProvider = null) : IComuneImportService
{
    /// <summary>Largest file accepted (the official list is about 1.2 MB).</summary>
    public const int MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>Line errors listed in a rejected import; the file is rejected as a whole anyway.</summary>
    public const int MaxReportedErrors = 50;

    public const string SeedFileName = "comuni-istat.csv";

    /// <summary>Manifest resource names of the seed file and of its metadata (<c>Data/Seeds</c>, embedded in the assembly).</summary>
    public const string SeedResourceName = "Casazen.Infrastructure.Data.Seeds.comuni-istat.csv";

    public const string SeedMetadataResourceName = "Casazen.Infrastructure.Data.Seeds.comuni-istat.source.json";

    private const string SystemUser = "system";

    private static readonly string[] IstatHeaders =
    [
        "CODICECOMUNEFORMATOALFANUMERICO", "CODICEISTAT", "CODICEISTATDELCOMUNE", "CODICEISTATCOMUNE",
        "CODICECOMUNEALFANUMERICO", "CODISTAT", "ISTAT", "CODICECOMUNE",
    ];

    // The numeric format drops the leading zeros of the code (1001 for 001001): accepted only when there is no alphanumeric one.
    private static readonly string[] IstatNumericHeaders = ["CODICECOMUNEFORMATONUMERICO", "CODICECOMUNENUMERICO"];

    private static readonly string[] NameHeaders =
    [
        "DENOMINAZIONEINITALIANO", "DENOMINAZIONEITALIANA", "DENOMINAZIONEIT", "DENOMINAZIONECOMUNE", "NOMECOMUNE",
        "DENOMINAZIONE", "COMUNE", "NOME",
    ];

    private static readonly string[] DisplayNameHeaders = ["DENOMINAZIONEITALIANAESTRANIERA", "DENOMINAZIONEITALIANAEALTRALINGUA"];

    private static readonly string[] CadastralHeaders =
    [
        "CODICECATASTALEDELCOMUNE", "CODICECATASTALE", "CODICEBELFIORE", "BELFIORE", "CODCATASTALE", "CATASTALE",
    ];

    private static readonly string[] ProvinceHeaders = ["SIGLAAUTOMOBILISTICA", "SIGLAPROVINCIA", "SIGLA", "TARGA", "PROVINCIA"];
    private static readonly string[] RegionCodeHeaders = ["CODICEREGIONE", "CODREGIONE"];
    private static readonly string[] RegionNameHeaders = ["DENOMINAZIONEREGIONE", "NOMEREGIONE", "REGIONE"];

    private static readonly string[] ValidFromHeaders =
        ["DATAISTITUZIONE", "DATAINIZIOVALIDITA", "DATAINIZIOVALIDITAAMMINISTRATIVA", "DATAINIZIO", "VALIDODAL"];

    private static readonly string[] ValidToHeaders = ["DATACESSAZIONE", "DATAFINEVALIDITA", "DATAFINE", "VALIDOAL"];

    private static readonly string[] DateFormats =
        ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy"];

    private static readonly char[] Delimiters = [';', ',', '\t', '|'];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ComuneImportResult> ImportAsync(
        ComuneImportRequest request,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);

        if (string.IsNullOrWhiteSpace(request.SourceVersion))
            return Reject(ComuneImportErrors.SourceVersionMissing);
        if (request.ReferenceDate > _clock.TodayInRomeAsDateOnly())
            return Reject(ComuneImportErrors.ReferenceDateInFuture);

        var bytes = await ReadAllAsync(content, cancellationToken);
        if (bytes is null)
            return Reject(ComuneImportErrors.FileTooLarge);
        if (bytes.Length == 0)
            return Reject(ComuneImportErrors.EmptyFile);

        var text = Decode(bytes);
        if (text is null)
            return Reject(ComuneImportErrors.UnreadableEncoding);

        var (rows, errors) = Parse(text, request.ReferenceDate);
        if (errors.Count > 0)
        {
            logger.LogWarning(
                "Comuni import rejected: {ErrorCount} errors (first: line {Line} {Error})",
                errors.Count, errors[0].Line, errors[0].Error);
            return ComuneImportResult.Rejected([.. errors]);
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var now = _clock.GetUtcNow().UtcDateTime;

        // One import at a time, across API instances: the diff below is computed from the stored rows.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.ComuneImport, "comuni"));

        var newestReference = await db.ComuneImports
            .AsNoTracking()
            .OrderByDescending(i => i.ReferenceDate)
            .Select(i => (DateOnly?)i.ReferenceDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (newestReference is { } newest && request.ReferenceDate < newest)
        {
            logger.LogWarning(
                "Comuni import refused: reference date {ReferenceDate} is older than the imported list ({Newest})",
                request.ReferenceDate, newest);
            return Reject(ComuneImportErrors.ReferenceDateOlderThanCurrent);
        }

        var stored = await db.Comuni.ToDictionaryAsync(c => c.IstatCode, StringComparer.Ordinal, cancellationToken);

        var import = new ComuneImport
        {
            Origin = request.Origin,
            SourceFileName = Truncate(Path.GetFileName(request.FileName ?? string.Empty), 255) is { Length: > 0 } name ? name : "upload",
            SourceVersion = Truncate(request.SourceVersion.Trim(), 300),
            ReferenceDate = request.ReferenceDate,
            Sha256 = sha256,
            RowCount = rows.Count,
            IsPartial = request.IsPartial,
            ImportedAt = now,
            ImportedBy = Truncate(string.IsNullOrWhiteSpace(request.ImportedBy) ? SystemUser : request.ImportedBy, 200),
            SourceUrl = TruncateOrNull(request.SourceUrl, 500),
            Authority = TruncateOrNull(request.Authority, 200),
            RetrievedAt = request.RetrievedAt,
        };

        var toInsert = new List<Comune>();
        var inFile = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            inFile.Add(row.IstatCode);
            if (!stored.TryGetValue(row.IstatCode, out var existing))
            {
                toInsert.Add(row.ToEntity(import.Id, now));
                import.InsertedCount++;
            }
            else if (row.ApplyTo(existing, import.Id, now))
            {
                import.UpdatedCount++;
            }
            else
            {
                import.UnchangedCount++;
            }
        }

        if (!request.IsPartial)
        {
            foreach (var missing in stored.Values.Where(c => c.IsActive && !inFile.Contains(c.IstatCode)))
            {
                missing.IsActive = false;
                missing.ValidTo ??= request.ReferenceDate;
                missing.SourceImportId = import.Id;
                missing.UpdatedAt = now;
                import.DeactivatedCount++;
            }
        }

        var clash = FindCadastralClash(stored, toInsert, rows);
        if (clash is not null)
        {
            // The rows were already applied to the tracked comuni: forget them, nothing is saved.
            db.ChangeTracker.Clear();
            logger.LogWarning("Comuni import rejected: the cadastral code of line {Line} is held by another active comune", clash.Value);
            return ComuneImportResult.Rejected(new ComuneImportLineError(clash.Value, ComuneImportErrors.ConflictsWithStoredComuni));
        }

        try
        {
            // Changes first (a comune that changes province keeps its cadastral code: the old row is deactivated, then
            // the new one is inserted), then the new rows.
            db.ComuneImports.Add(import);
            await db.SaveChangesAsync(cancellationToken);
            if (toInsert.Count > 0)
            {
                db.Comuni.AddRange(toInsert);
                await db.SaveChangesAsync(cancellationToken);
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Comuni import failed while saving: the file was not imported");
            db.ChangeTracker.Clear();
            return Reject(ComuneImportErrors.ConflictsWithStoredComuni);
        }

        db.ChangeTracker.Clear();
        logger.LogInformation(
            "Comuni list imported ({Origin}): {Rows} rows, {Inserted} new, {Updated} changed, {Unchanged} unchanged, {Deactivated} deactivated; reference date {ReferenceDate}, import {ImportId}",
            request.Origin, rows.Count, import.InsertedCount, import.UpdatedCount, import.UnchangedCount, import.DeactivatedCount,
            request.ReferenceDate, import.Id);

        return new ComuneImportResult(
            import.Id, rows.Count, import.InsertedCount, import.UpdatedCount, import.UnchangedCount, import.DeactivatedCount, []);
    }

    public async Task<ComuneSeedOutcome> ImportSeedIfNewerAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(ComuneImportService).Assembly;
        await using var csv = assembly.GetManifestResourceStream(SeedResourceName);
        if (csv is null)
        {
            logger.LogInformation("Comuni seed file is not part of this build: the list is imported by an admin (docs/runbooks/comuni-istat.md)");
            return ComuneSeedOutcome.NoSeedFile;
        }

        // The file never travels without its source and date: nothing is invented for it.
        await using var metadataStream = assembly.GetManifestResourceStream(SeedMetadataResourceName);
        var metadata = metadataStream is null ? null : await ReadSeedMetadataAsync(metadataStream, cancellationToken);
        if (metadata is null)
        {
            logger.LogError(
                "Comuni seed file found but {Metadata} is missing or invalid (sourceVersion, referenceDate yyyy-MM-dd): not imported",
                SeedMetadataResourceName);
            return ComuneSeedOutcome.Rejected;
        }

        var newest = await db.ComuneImports
            .AsNoTracking()
            .OrderByDescending(i => i.ReferenceDate)
            .Select(i => (DateOnly?)i.ReferenceDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (newest is { } current && current >= metadata.ReferenceDate)
        {
            logger.LogInformation(
                "Comuni seed file not imported: the database already has a list of {Current} (seed {Seed})",
                current, metadata.ReferenceDate);
            return ComuneSeedOutcome.AlreadyUpToDate;
        }

        var result = await ImportAsync(
            new ComuneImportRequest(
                SeedFileName,
                metadata.SourceVersion,
                metadata.ReferenceDate,
                SystemUser,
                ComuneImportOrigin.StartupSeed,
                SourceUrl: metadata.SourceUrl,
                Authority: metadata.Authority,
                RetrievedAt: metadata.RetrievedAt),
            csv,
            cancellationToken);
        if (result.Success)
            return ComuneSeedOutcome.Imported;

        logger.LogError(
            "Comuni seed file rejected ({Error}): the list stays as it was. Fix {File} or import the official file as admin",
            result.Errors[0].Error, SeedFileName);
        return ComuneSeedOutcome.Rejected;
    }

    private sealed record SeedMetadata(
        string SourceVersion,
        DateOnly ReferenceDate,
        string? SourceUrl,
        string? Authority,
        DateTime? RetrievedAt);

    private static async Task<SeedMetadata?> ReadSeedMetadataAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var version = root.TryGetProperty("sourceVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var date = root.TryGetProperty("referenceDate", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(version)
                || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var referenceDate))
                return null;

            var sourceUrl = root.TryGetProperty("sourceUrl", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
            var authority = root.TryGetProperty("authority", out var auth) && auth.ValueKind == JsonValueKind.String ? auth.GetString() : null;
            DateTime? retrievedAt = null;
            if (root.TryGetProperty("retrievedAt", out var retrieved) && retrieved.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(retrieved.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var retrievedDate))
                retrievedAt = DateTime.SpecifyKind(retrievedDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

            return new SeedMetadata(version.Trim(), referenceDate, sourceUrl?.Trim(), authority?.Trim(), retrievedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Line of the first row whose cadastral code would be held by another active comune after the import (the unique
    /// index of the active comuni); null when there is none. Checked in memory so a clash is a rejection, not a 500.
    /// </summary>
    private static int? FindCadastralClash(
        Dictionary<string, Comune> stored,
        List<Comune> toInsert,
        List<ParsedComune> rows)
    {
        var lineOf = rows.ToDictionary(r => r.IstatCode, r => r.Line, StringComparer.Ordinal);
        var holder = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var comune in stored.Values.Concat(toInsert).Where(c => c.IsActive && c.CadastralCode is not null))
        {
            if (holder.TryGetValue(comune.CadastralCode!, out var other) && other != comune.IstatCode)
            {
                var line = lineOf.GetValueOrDefault(comune.IstatCode, lineOf.GetValueOrDefault(other, 0));
                return line;
            }

            holder[comune.CadastralCode!] = comune.IstatCode;
        }

        return null;
    }

    private sealed record ParsedComune(
        int Line,
        string IstatCode,
        string? CadastralCode,
        string Name,
        string DisplayName,
        string ProvinceCode,
        string RegionIstatCode,
        string RegionName,
        DateOnly? ValidFrom,
        DateOnly? ValidTo,
        bool IsActive)
    {
        public Comune ToEntity(Guid importId, DateTime now) => new()
        {
            IstatCode = IstatCode,
            CadastralCode = CadastralCode,
            Name = Name,
            DisplayName = DisplayName,
            NormalizedName = Truncate(ComuneNames.Normalize(Name), ComuneRules.NameMaxLength),
            SearchText = Truncate(ComuneNames.Normalize(DisplayName), ComuneRules.DisplayNameMaxLength),
            ProvinceCode = ProvinceCode,
            RegionIstatCode = RegionIstatCode,
            RegionName = RegionName,
            IsActive = IsActive,
            ValidFrom = ValidFrom,
            ValidTo = ValidTo,
            SourceImportId = importId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        /// <summary>Copies the row onto the stored comune; true when something changed (an unchanged row is not touched).</summary>
        public bool ApplyTo(Comune stored, Guid importId, DateTime now)
        {
            var normalized = Truncate(ComuneNames.Normalize(Name), ComuneRules.NameMaxLength);
            var searchText = Truncate(ComuneNames.Normalize(DisplayName), ComuneRules.DisplayNameMaxLength);
            var unchanged = stored.CadastralCode == CadastralCode
                            && stored.Name == Name
                            && stored.DisplayName == DisplayName
                            && stored.NormalizedName == normalized
                            && stored.SearchText == searchText
                            && stored.ProvinceCode == ProvinceCode
                            && stored.RegionIstatCode == RegionIstatCode
                            && stored.RegionName == RegionName
                            && stored.IsActive == IsActive
                            && stored.ValidFrom == ValidFrom
                            && stored.ValidTo == ValidTo;
            if (unchanged)
                return false;

            stored.CadastralCode = CadastralCode;
            stored.Name = Name;
            stored.DisplayName = DisplayName;
            stored.NormalizedName = normalized;
            stored.SearchText = searchText;
            stored.ProvinceCode = ProvinceCode;
            stored.RegionIstatCode = RegionIstatCode;
            stored.RegionName = RegionName;
            stored.IsActive = IsActive;
            stored.ValidFrom = ValidFrom;
            stored.ValidTo = ValidTo;
            stored.SourceImportId = importId;
            stored.UpdatedAt = now;
            return true;
        }
    }

    private sealed record Columns(
        int Istat,
        bool IstatIsNumeric,
        int Name,
        int DisplayName,
        int Cadastral,
        int Province,
        int RegionCode,
        int RegionName,
        int ValidFrom,
        int ValidTo,
        int Count);

    private static (List<ParsedComune> Rows, List<ComuneImportLineError> Errors) Parse(string text, DateOnly referenceDate)
    {
        var rows = new List<ParsedComune>();
        var errors = new List<ComuneImportLineError>();
        var records = QuotedCsv.ReadRecords(text, Delimiters);
        if (records.Count == 0)
            return (rows, [new(0, ComuneImportErrors.EmptyFile)]);

        var headerRecord = records[0];
        var header = headerRecord.Cells.Select(HeaderKey).ToList();
        var columns = FindColumns(header, headerRecord.StartLine, errors);
        if (columns is null)
            return (rows, errors);

        var seenIstat = new HashSet<string>(StringComparer.Ordinal);
        var seenCadastral = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            var lineNumber = record.StartLine;
            var cells = record.Cells.ToList();
            var (row, error) = ParseRow(lineNumber, cells, columns, referenceDate);
            if (error is null && !seenIstat.Add(row!.IstatCode))
                error = ComuneImportErrors.DuplicateIstatCode;
            // The unique index is on the active comuni: a row that ceased may share the code of its successor.
            if (error is null && row!.IsActive && row.CadastralCode is not null && !seenCadastral.Add(row.CadastralCode))
                error = ComuneImportErrors.DuplicateCadastralCode;

            if (error is not null)
            {
                if (errors.Count < MaxReportedErrors)
                    errors.Add(new(lineNumber, error));
                continue;
            }

            rows.Add(row!);
        }

        if (rows.Count == 0 && errors.Count == 0)
            errors.Add(new(0, ComuneImportErrors.NoRows));

        return (rows, errors);
    }

    private static Columns? FindColumns(List<string> header, int headerLine, List<ComuneImportLineError> errors)
    {
        var istat = IndexOfAny(header, IstatHeaders);
        var istatNumeric = false;
        if (istat < 0)
        {
            istat = IndexOfAny(header, IstatNumericHeaders);
            istatNumeric = istat >= 0;
        }

        var name = IndexOfAny(header, NameHeaders);
        var cadastral = IndexOfAny(header, CadastralHeaders);
        var province = IndexOfAny(header, ProvinceHeaders);
        var regionCode = IndexOfAny(header, RegionCodeHeaders);
        var regionName = IndexOfAny(header, RegionNameHeaders);

        if (istat < 0)
            errors.Add(new(headerLine, ComuneImportErrors.IstatColumnMissing));
        if (name < 0)
            errors.Add(new(headerLine, ComuneImportErrors.NameColumnMissing));
        if (cadastral < 0)
            errors.Add(new(headerLine, ComuneImportErrors.CadastralColumnMissing));
        if (province < 0)
            errors.Add(new(headerLine, ComuneImportErrors.ProvinceColumnMissing));
        if (regionCode < 0 && regionName < 0)
            errors.Add(new(headerLine, ComuneImportErrors.RegionColumnMissing));
        if (errors.Count > 0)
            return null;

        return new Columns(
            istat,
            istatNumeric,
            name,
            IndexOfAny(header, DisplayNameHeaders),
            cadastral,
            province,
            regionCode,
            regionName,
            IndexOfAny(header, ValidFromHeaders),
            IndexOfAny(header, ValidToHeaders),
            header.Count);
    }

    private static int IndexOfAny(List<string> header, string[] synonyms)
    {
        foreach (var synonym in synonyms)
        {
            var index = header.IndexOf(synonym);
            if (index >= 0)
                return index;
        }

        return -1;
    }

    private static (ParsedComune? Row, string? Error) ParseRow(
        int line,
        List<string> cells,
        Columns columns,
        DateOnly referenceDate)
    {
        if (cells.Count != columns.Count)
            return (null, ComuneImportErrors.ColumnCountMismatch);

        var istat = NormalizeIstat(Cell(cells, columns.Istat));
        if (istat is null)
            return (null, ComuneImportErrors.IstatInvalid);

        var name = Cell(cells, columns.Name).Trim();
        if (name.Length == 0)
            return (null, ComuneImportErrors.NameMissing);
        if (name.Length > ComuneRules.NameMaxLength)
            return (null, ComuneImportErrors.NameTooLong);

        var displayName = columns.DisplayName >= 0 ? Cell(cells, columns.DisplayName).Trim() : string.Empty;
        if (displayName.Length == 0)
            displayName = name;
        if (displayName.Length > ComuneRules.DisplayNameMaxLength)
            return (null, ComuneImportErrors.NameTooLong);

        // "N.d." (not available) is how the official list says that the Agenzia delle Entrate has not assigned the code yet.
        var cadastralText = Cell(cells, columns.Cadastral).Trim();
        var cadastral = ComuneRules.NormalizeCadastralCode(cadastralText);
        if (cadastral is null && !IsNotAvailable(cadastralText))
            return (null, ComuneImportErrors.CadastralInvalid);

        var province = Cell(cells, columns.Province).Trim().ToUpperInvariant();
        if (!ComuneRules.IsProvinceCode(province))
            return (null, ComuneImportErrors.ProvinceInvalid);

        var (region, regionName, regionError) = ParseRegion(cells, columns);
        if (regionError is not null)
            return (null, regionError);

        var (validFrom, fromError) = ParseDate(columns.ValidFrom >= 0 ? Cell(cells, columns.ValidFrom) : null);
        var (validTo, toError) = ParseDate(columns.ValidTo >= 0 ? Cell(cells, columns.ValidTo) : null);
        if (fromError || toError)
            return (null, ComuneImportErrors.DateInvalid);
        if (validFrom is { } from && validTo is { } to && to < from)
            return (null, ComuneImportErrors.ValidityInconsistent);

        // A comune that ceased before the date of the list is in the file for the record, not to be selected.
        var active = validTo is null || validTo.Value >= referenceDate;

        return (new ParsedComune(
            line, istat, cadastral, name, displayName, province, region!.IstatCode, regionName!, validFrom, validTo, active), null);
    }

    private static (ItalianRegion? Region, string? Name, string? Error) ParseRegion(List<string> cells, Columns columns)
    {
        var writtenName = columns.RegionName >= 0 ? Cell(cells, columns.RegionName).Trim() : string.Empty;
        ItalianRegion? region;

        if (columns.RegionCode >= 0)
        {
            var code = Cell(cells, columns.RegionCode).Trim();
            if (code.Length is 0 or > ComuneRules.RegionIstatCodeLength || !code.All(char.IsAsciiDigit))
                return (null, null, ComuneImportErrors.RegionUnknown);

            region = ItalianRegions.FindByIstatCode(code.PadLeft(ComuneRules.RegionIstatCodeLength, '0'));
            if (region is null)
                return (null, null, ComuneImportErrors.RegionUnknown);

            // The code and the name of the same row must agree: a shifted column is not imported.
            if (writtenName.Length > 0 && ItalianRegions.FindByName(writtenName)?.IstatCode != region.IstatCode)
                return (null, null, ComuneImportErrors.RegionInconsistent);
        }
        else
        {
            region = ItalianRegions.FindByName(writtenName);
            if (region is null)
                return (null, null, ComuneImportErrors.RegionUnknown);
        }

        var name = writtenName.Length > 0 ? writtenName : region.Name;
        return name.Length > ComuneRules.RegionNameMaxLength ? (null, null, ComuneImportErrors.RegionUnknown) : (region, name, null);
    }

    /// <summary>
    /// Six digits as they are. Shorter all-digit values (4 to 5, a spreadsheet that dropped the leading zeros, or the numeric
    /// format of the list: 1001 for 001001) get the zeros back; anything else is not an ISTAT code.
    /// </summary>
    private static string? NormalizeIstat(string raw)
    {
        var value = raw.Trim();
        if (value.Length is < 4 or > ComuneRules.IstatCodeLength || !value.All(char.IsAsciiDigit))
            return null;

        return value.PadLeft(ComuneRules.IstatCodeLength, '0');
    }

    private static bool IsNotAvailable(string value) =>
        value.Replace(".", string.Empty, StringComparison.Ordinal).Trim().Equals("ND", StringComparison.OrdinalIgnoreCase);

    private static (DateOnly? Value, bool Invalid) ParseDate(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
            return (null, false);

        return DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? (date, false)
            : (null, true);
    }

    private static string Cell(List<string> cells, int index) => index >= 0 && index < cells.Count ? cells[index] : string.Empty;

    /// <summary>Header cell as a comparable key: accents removed, upper case, letters and digits only.</summary>
    private static string HeaderKey(string header)
    {
        var builder = new StringBuilder(header.Length);
        foreach (var c in header.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>UTF-8 (with or without BOM) when valid, otherwise Windows-1252; null when neither applies.</summary>
    private static string? Decode(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return text.TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            try
            {
                var windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(
                    1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return windows1252?.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }
    }

    private static async Task<byte[]?> ReadAllAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static ComuneImportResult Reject(string error) => ComuneImportResult.Rejected(new ComuneImportLineError(0, error));

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOrNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
