using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOnboardingBulkImportService" />
/// <remarks>
/// <para><b>Validation, then writing.</b> The whole file is read and every row judged first, with a fixed number of queries (the properties of the
/// rows by id and by CIN in one query each, the comuni in one). A row is judged on its own: the status is <c>ok</c> or the code of its first error,
/// and an error of one row never stops another. With <c>dryRun</c> nothing else happens. Otherwise each valid row is written in one unit of work of
/// its own (the property, its onboarding data and its access code in one <c>SaveChanges</c>), after the property is looked up again in the same
/// reach; a row whose property has gone since the check is reported <see cref="BulkImportRowCodes.PropertyNotFound"/> and not written.</para>
/// <para><b>Reach.</b> The properties come from the org of the scope and from what the scope reaches (<c>InScope</c>); a property of another
/// org, one outside the reach and one that does not exist all give <see cref="BulkImportRowCodes.PropertyNotFound"/>. Nothing in the answer
/// distinguishes them.</para>
/// <para><b>The lock code.</b> It is written to <see cref="PropertyAccessCode.EncryptedCode"/>, which the EF value converter encrypts under the
/// Data Protection purpose of the access codes (<c>PropertyAccessCodeEncryptionPurpose</c>), exactly as <c>AccessCodeService</c> does. The parsed row
/// holds it only for the call (a class that prints nothing), the answer carries only <c>lockCodeProvided</c>, and no log line has a cell of the file:
/// only the org and counts.</para>
/// <para><b>What a row writes.</b> The comune writes <c>Property.ComuneIstatCode</c> and, with it, the region of that comune
/// (<c>Property.RegionCode</c>), as the property wizard does; a region alone is only checked, because a region is the trusted one of a comune the
/// property already has. The room numbers go to <c>PropertyOnboardingData</c> as a JSON array, the lock code to the property's access code. The
/// city and every other field of the property are untouched.</para>
/// </remarks>
public sealed class OnboardingBulkImportService(
    AppDbContext db,
    IComuneDirectory comuni,
    TimeProvider timeProvider,
    ILogger<OnboardingBulkImportService> logger) : IOnboardingBulkImportService
{
    /// <summary>The longest text of the reference a row names its property by that the answer echoes.</summary>
    private const int MaxReferenceLength = 64;

    public async Task<BulkImportResult> ImportAsync(
        HostScope scope,
        string actorUserId,
        string fileName,
        byte[] content,
        bool dryRun,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentNullException.ThrowIfNull(content);
        if (actorUserId.Length > PropertyOnboardingData.UserIdMaxLength)
            throw new ArgumentException("Not a user id.", nameof(actorUserId));

        var parsed = OnboardingBulkImportParser.Parse(ReadTable(fileName, content));
        var rows = await EvaluateAsync(scope, parsed, ct);

        var applied = new HashSet<int>();
        if (!dryRun)
        {
            var now = UtcDateTime.TruncateToMicroseconds(timeProvider.GetUtcNow().UtcDateTime);
            foreach (var row in rows.Where(r => r.Errors.Count == 0))
            {
                // Applied is what changed something: a valid row that holds what is stored already, or that has only its key, writes nothing.
                var written = await ApplyAsync(scope, actorUserId, row, now, ct);
                if (written == RowWrite.Written)
                    applied.Add(row.Number);
                else if (written == RowWrite.PropertyGone)
                    row.Errors.Add(new BulkImportRowError(row.KeyColumn, BulkImportRowCodes.PropertyNotFound));
            }
        }

        var answer = rows
            .Select(row => new BulkImportRow(
                row.Number,
                row.PropertyRef,
                row.Property?.Id,
                row.Errors.Count == 0 ? BulkImportRowCodes.Ok : row.Errors[0].Code,
                [.. row.Errors],
                row.LockCodeProvided,
                applied.Contains(row.Number)))
            .ToList();
        var valid = answer.Count(row => row.Status == BulkImportRowCodes.Ok);
        var summary = new BulkImportSummary(answer.Count, valid, answer.Count - valid, applied.Count);

        // Counts only: no cell of the file, no name, no code.
        logger.LogInformation(
            "Bulk import for org {OrgId}: {Rows} rows, {Valid} valid, {Applied} written, nothing written {DryRun}",
            scope.OrgId,
            summary.Rows,
            summary.Valid,
            summary.Applied,
            dryRun);
        return new BulkImportResult(dryRun, summary, parsed.UnknownColumns, answer);
    }

    // ---------------------------------------------------------------- the file

    /// <summary>
    /// The table of the file, or the refusal: 415 for a name that is not <c>.csv</c> or <c>.xlsx</c> and for content that is not the type the name
    /// says (an archive called <c>.csv</c>, a text called <c>.xlsx</c>), 413 for more than 2 MB. The file is read from memory and not kept.
    /// </summary>
    internal static List<ImportTableRow> ReadTable(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (extension is not (".csv" or ".xlsx"))
            throw Unsupported();

        if (content.Length > BulkImportLimits.MaxFileBytes)
        {
            throw new DomainPayloadTooLargeException(
                BulkImportCodes.FileTooLarge, "OnboardingImportFileTooLarge", BulkImportLimits.MaxFileBytes / (1024 * 1024));
        }

        if (content.Length == 0)
            throw new DomainRuleException(BulkImportCodes.FileEmpty, "OnboardingImportFileEmpty");

        if (extension == ".xlsx")
            return OnboardingXlsxReader.LooksLikeZip(content) ? OnboardingXlsxReader.Read(content) : throw Unsupported();

        return OnboardingBulkImportParser.LooksBinary(content) ? throw Unsupported() : OnboardingBulkImportParser.ReadCsv(content);
    }

    private static DomainUnsupportedMediaTypeException Unsupported() =>
        new(BulkImportCodes.FileTypeUnsupported, "OnboardingImportFileTypeUnsupported");

    // ---------------------------------------------------------------- validation

    private async Task<List<EvaluatedRow>> EvaluateAsync(HostScope scope, ParsedImport parsed, CancellationToken ct)
    {
        var keyColumn = parsed.Columns.PropertyId is not null ? BulkImportColumns.PropertyId : BulkImportColumns.CinCode;

        // The properties the rows name, in two queries: by id and by CIN, both inside the reach of the caller.
        var wantedIds = new HashSet<Guid>();
        var wantedCins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in parsed.Rows)
        {
            if (row.PropertyId is { } rawId)
            {
                if (Guid.TryParse(rawId, out var id) && id != Guid.Empty)
                    wantedIds.Add(id);
            }
            else if (CinFormat.Normalize(row.Cin) is { } cin)
            {
                wantedCins.Add(cin);
            }
        }

        var reachable = db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId).InScope(scope);
        var byId = wantedIds.Count == 0
            ? new Dictionary<Guid, PropertyFacts>()
            : await reachable
                .Where(p => wantedIds.Contains(p.Id))
                .Select(p => new PropertyFacts(p.Id, p.CinCode, p.RegionCode))
                .ToDictionaryAsync(p => p.Id, ct);
        var byCin = wantedCins.Count == 0
            ? new Dictionary<string, List<PropertyFacts>>()
            : (await reachable
                    .Where(p => p.CinCode != null && wantedCins.Contains(p.CinCode))
                    .Select(p => new PropertyFacts(p.Id, p.CinCode, p.RegionCode))
                    .ToListAsync(ct))
                .GroupBy(p => p.CinCode!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var comuniByCode = await FindComuniAsync(parsed, ct);

        var seen = new HashSet<Guid>();
        var evaluated = new List<EvaluatedRow>(parsed.Rows.Count);
        foreach (var row in parsed.Rows)
            evaluated.Add(Evaluate(row, keyColumn, byId, byCin, comuniByCode, seen));

        return evaluated;
    }

    /// <summary>
    /// The comuni the rows name, from the official list the property wizard uses. When a row names a comune and the list is not imported, nothing can
    /// be judged on it: 422, not a status that would blame the row.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, Comune>> FindComuniAsync(ParsedImport parsed, CancellationToken ct)
    {
        if (parsed.Rows.All(row => row.Comune is null))
            return new Dictionary<string, Comune>();

        if (!await comuni.IsAvailableAsync(ct))
            throw new DomainRuleException(ComuneErrorCodes.DatasetUnavailable, ComuneErrorCodes.DatasetUnavailableMessageKey);

        var codes = parsed.Rows.Select(row => row.Comune).Where(ComuneRules.IsIstatCode).Select(code => code!).Distinct(StringComparer.Ordinal).ToList();
        return codes.Count == 0 ? new Dictionary<string, Comune>() : await comuni.GetByIstatCodesAsync(codes, ct);
    }

    private static EvaluatedRow Evaluate(
        ParsedImportRow row,
        string defaultKeyColumn,
        IReadOnlyDictionary<Guid, PropertyFacts> byId,
        IReadOnlyDictionary<string, List<PropertyFacts>> byCin,
        IReadOnlyDictionary<string, Comune> comuniByCode,
        HashSet<Guid> seen)
    {
        var evaluated = new EvaluatedRow(row.Number, defaultKeyColumn) { LockCodeProvided = row.LockCode is not null };

        // The property: by id when the row has one, else by CIN. Not found is the answer for everything that is not the caller's.
        if (row.PropertyId is { } rawId)
        {
            evaluated.KeyColumn = BulkImportColumns.PropertyId;
            evaluated.PropertyRef = Clip(rawId);
            if (Guid.TryParse(rawId, out var id) && byId.TryGetValue(id, out var found))
                evaluated.Property = found;
        }
        else if (row.Cin is { } rawCin)
        {
            evaluated.KeyColumn = BulkImportColumns.CinCode;
            var cin = CinFormat.Normalize(rawCin);
            evaluated.PropertyRef = Clip(cin ?? rawCin);
            if (cin is not null && byCin.TryGetValue(cin, out var matches))
            {
                if (matches.Count == 1)
                {
                    evaluated.Property = matches[0];
                }
                else
                {
                    evaluated.Errors.Add(new BulkImportRowError(BulkImportColumns.CinCode, BulkImportRowCodes.PropertyAmbiguous));
                }
            }
        }

        if (evaluated.Property is null && evaluated.Errors.Count == 0)
            evaluated.Errors.Add(new BulkImportRowError(evaluated.KeyColumn, BulkImportRowCodes.PropertyNotFound));

        // The same property twice: only the first row is judged and written, the others say so and nothing more.
        if (evaluated.Property is { } property && !seen.Add(property.Id))
        {
            evaluated.Errors.Add(new BulkImportRowError(evaluated.KeyColumn, BulkImportRowCodes.DuplicateRow));
            return evaluated;
        }

        // The comune: an active comune of the official list whose region CasaZen knows.
        string? comuneRegion = null;
        if (row.Comune is { } rawComune)
        {
            if (ComuneRules.IsIstatCode(rawComune)
                && comuniByCode.TryGetValue(rawComune, out var comune)
                && comune.IsActive
                && comune.RegionCode is { } ofComune)
            {
                evaluated.Comune = comune;
                comuneRegion = ofComune;
            }
            else
            {
                evaluated.Errors.Add(new BulkImportRowError(BulkImportColumns.ComuneIstatCode, BulkImportRowCodes.ComuneUnknown));
            }
        }

        // The region has to be the comune's: the comune of the row, or, when the row has none, the one the property already has. A region the
        // property cannot be checked against (no comune anywhere) is refused. A comune that is itself an error is not judged again here.
        if (row.Region is { } rawRegion)
        {
            var named = ItalianRegions.FindByCode(rawRegion);
            if (named is null)
            {
                evaluated.Errors.Add(new BulkImportRowError(BulkImportColumns.RegionCode, BulkImportRowCodes.RegionMismatch));
            }
            else if (row.Comune is not null)
            {
                if (comuneRegion is not null && !string.Equals(comuneRegion, named.Code, StringComparison.Ordinal))
                    evaluated.Errors.Add(new BulkImportRowError(BulkImportColumns.RegionCode, BulkImportRowCodes.RegionMismatch));
            }
            else if (evaluated.Property is not null && !string.Equals(evaluated.Property.RegionCode, named.Code, StringComparison.Ordinal))
            {
                evaluated.Errors.Add(new BulkImportRowError(BulkImportColumns.RegionCode, BulkImportRowCodes.RegionMismatch));
            }
        }

        // The Alloggiati Web room numbers: up to 20 distinct codes of 1 to 20 letters, digits or hyphens, separated by «;».
        if (row.Rooms is { } rawRooms)
        {
            var rooms = rawRooms.Split(';').Select(room => room.Trim()).ToList();
            if (rooms.Count <= PropertyOnboardingData.MaxRoomNumbers
                && rooms.All(IsRoomNumber)
                && rooms.Distinct(StringComparer.OrdinalIgnoreCase).Count() == rooms.Count)
            {
                evaluated.Rooms = rooms;
            }
            else
            {
                evaluated.Errors.Add(new BulkImportRowError(BulkImportColumns.AlloggiatiRoomNumbers, BulkImportRowCodes.RoomNumbersInvalid));
            }
        }

        // The lock code: 3 to 20 characters, none of them a control character. The only place it is read.
        if (row.LockCode is { } rawLock)
        {
            if (rawLock.Length is >= BulkImportLimits.LockCodeMinLength and <= BulkImportLimits.LockCodeMaxLength && !rawLock.Any(char.IsControl))
                evaluated.LockCode = rawLock;
            else
                evaluated.Errors.Add(new BulkImportRowError(BulkImportColumns.LockCode, BulkImportRowCodes.LockCodeInvalid));
        }

        return evaluated;
    }

    private static bool IsRoomNumber(string room) =>
        room.Length is >= 1 and <= BulkImportLimits.RoomNumberMaxLength && room.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

    private static string Clip(string value) => value.Length <= MaxReferenceLength ? value : value[..MaxReferenceLength];

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Writes one valid row in one unit of work: the property (comune and region), its onboarding data (room numbers) and its access code (lock
    /// code). The property is looked up again, tracked, in the same reach. Only what differs from the stored value is written: a row that repeats
    /// what the property holds already, or that has only its key, writes nothing (no save, not even a date) and is <see cref="RowWrite.Unchanged"/>.
    /// </summary>
    private async Task<RowWrite> ApplyAsync(HostScope scope, string actorUserId, EvaluatedRow row, DateTime now, CancellationToken ct)
    {
        var propertyId = row.Property!.Id;
        var property = await db.Properties
            .Where(p => p.OrgId == scope.OrgId && p.Id == propertyId)
            .InScope(scope)
            .FirstOrDefaultAsync(ct);
        if (property is null)
            return RowWrite.PropertyGone;

        var changed = false;
        if (row.Comune is not null
            && (!string.Equals(property.ComuneIstatCode, row.Comune.IstatCode, StringComparison.Ordinal)
                || !string.Equals(property.RegionCode, row.Comune.RegionCode, StringComparison.Ordinal)))
        {
            // The region follows the comune, as in the wizard (Property.RegionCode is set together with Property.ComuneIstatCode).
            property.ComuneIstatCode = row.Comune.IstatCode;
            property.RegionCode = row.Comune.RegionCode;
            property.UpdatedAt = now;
            changed = true;
        }

        if (row.Rooms is not null)
        {
            var data = await db.PropertyOnboardingData.FirstOrDefaultAsync(d => d.OrgId == scope.OrgId && d.PropertyId == propertyId, ct);
            if (data is null)
            {
                // Through the DbSet: the Guid of the row is preset, so a row attached any other way would be tracked as modified.
                db.PropertyOnboardingData.Add(new PropertyOnboardingData
                {
                    OrgId = scope.OrgId,
                    PropertyId = propertyId,
                    AlloggiatiRoomNumbersJson = JsonSerializer.Serialize(row.Rooms),
                    UpdatedAt = now,
                    UpdatedByUserId = actorUserId,
                });
                changed = true;
            }
            else if (!SameRoomNumbers(data.AlloggiatiRoomNumbersJson, row.Rooms))
            {
                data.AlloggiatiRoomNumbersJson = JsonSerializer.Serialize(row.Rooms);
                data.UpdatedAt = now;
                data.UpdatedByUserId = actorUserId;
                changed = true;
            }
        }

        if (row.LockCode is not null)
        {
            var access = await db.PropertyAccessCodes.FirstOrDefaultAsync(a => a.OrgId == scope.OrgId && a.PropertyId == propertyId, ct);
            if (access is null)
            {
                // The EF converter of the column encrypts it on the way in (purpose of the access codes); nothing here logs or returns it.
                db.PropertyAccessCodes.Add(new PropertyAccessCode
                {
                    OrgId = scope.OrgId,
                    PropertyId = propertyId,
                    EncryptedCode = row.LockCode,
                    UpdatedAt = now,
                });
                changed = true;
            }
            else if (!string.Equals(access.EncryptedCode, row.LockCode, StringComparison.Ordinal))
            {
                access.EncryptedCode = row.LockCode;
                access.UpdatedAt = now;
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync(ct);

        // One unit of work for each row: nothing of this one stays tracked for the next.
        db.ChangeTracker.Clear();
        return changed ? RowWrite.Written : RowWrite.Unchanged;
    }

    /// <summary>
    /// True when the stored JSON array holds exactly these room numbers, in this order. The values are compared, not the text: PostgreSQL's
    /// <c>jsonb</c> gives an array back normalized (<c>["101", "102"]</c>, with a space after the comma), which is never the text
    /// <see cref="JsonSerializer"/> writes, so comparing the text would rewrite an unchanged row (and its date and author) at every import.
    /// A value that is not an array of strings (or is not JSON at all) is different, and is replaced.
    /// </summary>
    private static bool SameRoomNumbers(string? stored, IReadOnlyList<string> rooms)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return false;

        try
        {
            var current = JsonSerializer.Deserialize<string[]>(stored);
            return current is not null && current.SequenceEqual(rooms, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>What <see cref="ApplyAsync"/> did with a valid row.</summary>
    private enum RowWrite
    {
        /// <summary>The property is not there any more (or no longer within the reach of the caller): nothing was written.</summary>
        PropertyGone,

        /// <summary>The property holds what the row says already, or the row says nothing but the key: nothing was written.</summary>
        Unchanged,

        /// <summary>At least one value differed from the stored one and was written.</summary>
        Written,
    }

    private sealed record PropertyFacts(Guid Id, string? CinCode, string? RegionCode);

    /// <summary>
    /// A row after its check. A class: the lock code is a secret that only <see cref="ApplyAsync"/> reads, and nothing prints a class that does not say
    /// how.
    /// </summary>
    private sealed class EvaluatedRow(int number, string keyColumn)
    {
        public int Number { get; } = number;

        public string KeyColumn { get; set; } = keyColumn;

        public string? PropertyRef { get; set; }

        public PropertyFacts? Property { get; set; }

        public List<BulkImportRowError> Errors { get; } = [];

        public bool LockCodeProvided { get; init; }

        public Comune? Comune { get; set; }

        public IReadOnlyList<string>? Rooms { get; set; }

        public string? LockCode { get; set; }
    }
}
