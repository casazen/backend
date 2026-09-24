using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Official Alloggiati code tables (CO-12). The files are downloaded by an admin from the "Area Download Tabelle" of the
/// Alloggiati portal (RS-1) and uploaded here; CasaZen never ships nor invents a code. File format (documented in
/// <c>docs/runbooks/alloggiati.md</c>): delimited text (<c>;</c>, tab, <c>|</c> or <c>,</c>, detected from the header),
/// UTF-8 or Windows-1252, a header row naming the columns <c>Codice</c> and <c>Descrizione</c> (also <c>Code</c>,
/// <c>Description</c>) and, for comuni, optionally <c>Provincia</c> (<c>Province</c>, <c>SiglaProvincia</c>). Other
/// columns are ignored.
/// </summary>
public class AlloggiatiCodeTableService(
    AppDbContext db,
    ILogger<AlloggiatiCodeTableService> logger,
    TimeProvider? timeProvider = null) : IAlloggiatiCodeTableService
{
    /// <summary>Largest file accepted (the comuni table is the largest one).</summary>
    public const int MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>Line errors listed in a rejected import; the file is rejected as a whole anyway.</summary>
    public const int MaxReportedErrors = 50;

    public const int MaxSearchResults = 50;

    private const int MaxDescriptionLength = 200;

    private static readonly string[] CodeHeaders = ["CODICE", "CODE"];
    private static readonly string[] DescriptionHeaders = ["DESCRIZIONE", "DESCRIPTION"];
    private static readonly string[] ProvinceHeaders = ["PROVINCIA", "PROVINCE", "SIGLAPROVINCIA"];
    private static readonly char[] Delimiters = [';', '\t', '|', ','];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<AlloggiatiCodeTableStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var counts = await RowCountsAsync(cancellationToken);
        var imports = await db.AlloggiatiCodeTableImports.AsNoTracking().ToListAsync(cancellationToken);

        return Enum.GetValues<AlloggiatiCodeTable>()
            .Select(table =>
            {
                var last = imports.Where(i => i.Table == table).MaxBy(i => i.ImportedAt);
                return new AlloggiatiCodeTableStatus(
                    table,
                    counts.GetValueOrDefault(table),
                    last?.ImportedAt,
                    last?.SourceVersion,
                    last?.SourceFileName);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<AlloggiatiCodeEntryInfo>> SearchAsync(
        IReadOnlyList<AlloggiatiCodeTable> tables,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var key = AlloggiatiRecordRules.NormalizeDescription(query);
        if (key.Length == 0 || tables.Count == 0)
            return [];

        limit = Math.Clamp(limit, 1, MaxSearchResults);
        var tableList = tables.Distinct().ToList();
        var code = AlloggiatiRecordRules.NormalizeCode(query);
        var scope = db.AlloggiatiCodeEntries.AsNoTracking().Where(e => tableList.Contains(e.Table));

        var results = await scope
            .Where(e => e.NormalizedDescription.StartsWith(key) || e.Code == code)
            .OrderBy(e => e.NormalizedDescription.Length)
            .ThenBy(e => e.NormalizedDescription)
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (results.Count < limit)
        {
            var found = results.Select(e => e.Id).ToList();
            results.AddRange(await scope
                .Where(e => !found.Contains(e.Id) && e.NormalizedDescription.Contains(key))
                .OrderBy(e => e.NormalizedDescription)
                .Take(limit - results.Count)
                .ToListAsync(cancellationToken));
        }

        return results.Select(e => new AlloggiatiCodeEntryInfo(e.Table, e.Code, e.Description, e.Province)).ToList();
    }

    public async Task<AlloggiatiCodeBook> LoadCodeBookAsync(IEnumerable<StayGuest> guests, CancellationToken cancellationToken = default)
    {
        var counts = await RowCountsAsync(cancellationToken);
        if (counts.Count == 0)
            return AlloggiatiCodeBook.Empty;

        var entries = new List<AlloggiatiCodeEntry>();
        foreach (var (table, keys) in AlloggiatiCodeBook.KeysOf(guests))
        {
            if (counts.GetValueOrDefault(table) == 0 || (keys.Codes.Count == 0 && keys.Names.Count == 0))
                continue;

            var codes = keys.Codes.ToList();
            var names = keys.Names.ToList();
            entries.AddRange(await db.AlloggiatiCodeEntries
                .AsNoTracking()
                .Where(e => e.Table == table && (codes.Contains(e.Code) || names.Contains(e.NormalizedDescription)))
                .ToListAsync(cancellationToken));
        }

        return new AlloggiatiCodeBook(counts, entries);
    }

    public async Task<AlloggiatiCodeImportResult> ImportAsync(
        AlloggiatiCodeTable table,
        Stream content,
        string fileName,
        string sourceVersion,
        string importedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceVersion))
            return Rejected(table, AlloggiatiCodeImportErrors.SourceVersionMissing);

        var bytes = await ReadAllAsync(content, cancellationToken);
        if (bytes is null)
            return Rejected(table, AlloggiatiCodeImportErrors.FileTooLarge);
        if (bytes.Length == 0)
            return Rejected(table, AlloggiatiCodeImportErrors.EmptyFile);

        var text = Decode(bytes);
        if (text is null)
            return Rejected(table, AlloggiatiCodeImportErrors.UnreadableEncoding);

        var (rows, errors) = Parse(table, text);
        if (errors.Count > 0)
        {
            logger.LogWarning(
                "Alloggiati {Table} import rejected: {ErrorCount} errors (first: line {Line} {Error})",
                table, errors.Count, errors[0].Line, errors[0].Error);
            return new AlloggiatiCodeImportResult(table, 0, null, errors);
        }

        var import = new AlloggiatiCodeTableImport
        {
            Table = table,
            SourceFileName = Truncate(Path.GetFileName(fileName ?? string.Empty), 255) is { Length: > 0 } name ? name : "upload",
            SourceVersion = Truncate(sourceVersion.Trim(), 100),
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            RowCount = rows.Count,
            ImportedAt = _clock.GetUtcNow().UtcDateTime,
            ImportedBy = Truncate(importedBy, 200),
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.AlloggiatiCodeEntries.Where(e => e.Table == table).ExecuteDeleteAsync(cancellationToken);
        db.AlloggiatiCodeTableImports.Add(import);
        foreach (var row in rows)
        {
            db.AlloggiatiCodeEntries.Add(new AlloggiatiCodeEntry
            {
                Table = table,
                Code = row.Code,
                Description = row.Description,
                NormalizedDescription = Truncate(AlloggiatiRecordRules.NormalizeDescription(row.Description), MaxDescriptionLength),
                Province = row.Province,
                ImportId = import.Id,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();

        logger.LogInformation(
            "Alloggiati {Table} table imported: {RowCount} codes, version {SourceVersion}, import {ImportId}",
            table, rows.Count, import.SourceVersion, import.Id);
        return new AlloggiatiCodeImportResult(table, rows.Count, import.Id, []);
    }

    private async Task<Dictionary<AlloggiatiCodeTable, int>> RowCountsAsync(CancellationToken cancellationToken) =>
        await db.AlloggiatiCodeEntries
            .AsNoTracking()
            .GroupBy(e => e.Table)
            .Select(g => new { Table = g.Key, Rows = g.Count() })
            .ToDictionaryAsync(x => x.Table, x => x.Rows, cancellationToken);

    private sealed record ParsedRow(string Code, string Description, string? Province);

    private static (List<ParsedRow> Rows, List<AlloggiatiCodeImportLineError> Errors) Parse(AlloggiatiCodeTable table, string text)
    {
        var rows = new List<ParsedRow>();
        var errors = new List<AlloggiatiCodeImportLineError>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var headerIndex = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        if (headerIndex < 0)
            return (rows, [new(0, AlloggiatiCodeImportErrors.EmptyFile)]);

        var delimiter = Delimiters.MaxBy(d => lines[headerIndex].Count(c => c == d));
        var header = SplitLine(lines[headerIndex], delimiter)
            .Select(AlloggiatiRecordRules.NormalizeDescription)
            .ToList();
        var codeColumn = header.FindIndex(h => CodeHeaders.Contains(h));
        var descriptionColumn = header.FindIndex(h => DescriptionHeaders.Contains(h));
        var provinceColumn = table == AlloggiatiCodeTable.Comuni ? header.FindIndex(h => ProvinceHeaders.Contains(h)) : -1;

        if (codeColumn < 0)
            errors.Add(new(headerIndex + 1, AlloggiatiCodeImportErrors.CodeColumnMissing));
        if (descriptionColumn < 0)
            errors.Add(new(headerIndex + 1, AlloggiatiCodeImportErrors.DescriptionColumnMissing));
        if (errors.Count > 0)
            return (rows, errors);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var lineNumber = i + 1;
            var cells = SplitLine(lines[i], delimiter);
            var code = AlloggiatiRecordRules.NormalizeCode(Cell(cells, codeColumn));
            var description = Cell(cells, descriptionColumn).Trim();
            var province = provinceColumn >= 0 ? AlloggiatiRecordRules.NormalizeCode(Cell(cells, provinceColumn)) : null;

            var error = code is null ? AlloggiatiCodeImportErrors.CodeMissing
                : !AlloggiatiRecordRules.IsValidCodeShape(table, code) ? AlloggiatiCodeImportErrors.CodeInvalid
                : description.Length == 0 ? AlloggiatiCodeImportErrors.DescriptionMissing
                : description.Length > MaxDescriptionLength ? AlloggiatiCodeImportErrors.DescriptionTooLong
                : province is not null && !AlloggiatiRecordRules.IsValidProvince(province) ? AlloggiatiCodeImportErrors.ProvinceInvalid
                : !seen.Add(code) ? AlloggiatiCodeImportErrors.DuplicateCode
                : null;

            if (error is not null)
            {
                if (errors.Count < MaxReportedErrors)
                    errors.Add(new(lineNumber, error));
                continue;
            }

            rows.Add(new ParsedRow(code!, description, province));
        }

        if (rows.Count == 0 && errors.Count == 0)
            errors.Add(new(0, AlloggiatiCodeImportErrors.NoRows));

        return (rows, errors);
    }

    private static string Cell(IReadOnlyList<string> cells, int index) => index < cells.Count ? cells[index] : string.Empty;

    /// <summary>Splits a delimited line; double quotes enclose a cell and <c>""</c> is a literal quote.</summary>
    private static List<string> SplitLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"' && current.ToString().Trim().Length == 0)
            {
                current.Clear();
                quoted = true;
            }
            else if (c == delimiter)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }

    /// <summary>UTF-8 (with or without BOM) when valid, otherwise Windows-1252; null when neither applies.</summary>
    private static string? Decode(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return text.TrimStart('﻿');
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

    private static AlloggiatiCodeImportResult Rejected(AlloggiatiCodeTable table, string error) =>
        new(table, 0, null, [new AlloggiatiCodeImportLineError(0, error)]);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
