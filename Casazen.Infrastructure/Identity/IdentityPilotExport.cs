using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The CSV of the pilot of the self-hosted identity verification (E73/F01/US03/T02, NF-196, decision D10): one row for each identity step opened in the period, with what the
/// policy decided (its shadow outcome) next to what a person decided afterwards, for the pilot of 500 consecutive check-ins with a parallel human verification. It is made of
/// the same records as the counters (<see cref="IdentityVerificationMetricsService"/>), so the file and the dashboard cannot disagree.
/// </summary>
/// <remarks>
/// <para><b>No personal data.</b> The file holds ids, codes, counts and times: <c>attempt_ref</c> is the first 16 hexadecimal characters of the SHA-256 of the session id and the org
/// id (see <see cref="AttemptRef"/>), pseudonymous and stable, never the session id itself; there is no image, no name, no e-mail, no document number, no embedding and no score
/// of a person, and the reasons are written as codes (<c>check:result</c>), never with the number they were compared with. Only the codes of the reasons are read from the
/// stored reasons, and only a code (lower case letters, digits and underscores) is written. Nothing here logs, and nothing writes an entry of the activity log: like the other
/// exports of the application this one is not audited, and its content is never in a log.</para>
/// <para><b>Rows.</b> One for each step opened in <c>[from, to)</c> by the scope of the caller, oldest first. A step with no decision of the policy (a refusal of the consent, a
/// minor, the assisted path, a step still open, an abandonment) has its outcome and empty cells for the decision. <c>outcome_code</c> is the category of the counters (the codes of
/// <see cref="Casazen.Core.Identity.IdentityOutcomeCodes"/> and <c>in_progress</c>), <c>format_id</c> is <c>unknown</c> when the document was not read as a format of the registry,
/// <c>decision_mode</c> is <c>observation</c> or <c>automatic</c>, <c>shadow_outcome</c> and <c>human_outcome</c> are <c>approve</c> or <c>review</c> and <c>approve</c> or
/// <c>reject</c>, <c>reason_codes</c> are the checks that did not pass as <c>check:result</c> joined by <c>|</c>, <c>review_seconds</c> is the time from the escalation of the policy to
/// the decision of the person (a review of the policy only), and <c>policy_hash_prefix</c> the first 12 characters of the policy hash. Times are UTC, ISO 8601, to the millisecond.</para>
/// <para><b>Safe in a spreadsheet.</b> RFC 4180: a comma, with CRLF line ends, and a cell with a comma, a quote or a line break in quotes with the quotes doubled. A cell that a
/// spreadsheet could read as a formula (<see cref="CsvWriter.StartsLikeAFormula"/>, the one rule of every file the host downloads: it starts with <c>=</c>, <c>+</c>, <c>-</c>,
/// <c>@</c>, a tab or a carriage return) gets a leading apostrophe.</para>
/// <para><b>Streaming.</b> <see cref="OpenAsync"/> does every read, so an error of the database is an error before the first byte of the answer; the lines of the file are then
/// written one at a time (<see cref="IdentityPilotFile.Lines"/>), never as one text.</para>
/// </remarks>
public sealed class IdentityPilotExport(AppDbContext db, TimeProvider clock, IOptions<IdentityVerificationOptions> options)
{
    /// <summary>The content type of the file.</summary>
    public const string ContentType = "text/csv; charset=utf-8";

    /// <summary>The end of a line of the file (RFC 4180).</summary>
    public const string NewLine = "\r\n";

    /// <summary>How many characters of the SHA-256 of the session and the org <c>attempt_ref</c> keeps.</summary>
    public const int AttemptRefLength = 16;

    /// <summary>How many characters of the policy hash <c>policy_hash_prefix</c> keeps.</summary>
    public const int PolicyHashPrefixLength = 12;

    /// <summary>The longest code of a reason that is written (the longest column of a code of the identity tables): anything longer is not a code.</summary>
    private const int MaxCodeLength = 40;

    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string DayFormat = "yyyy-MM-dd";
    private const string SecondsFormat = "0.###";

    private const string Approve = "approve";
    private const string Review = "review";
    private const string Reject = "reject";

    /// <summary>The columns, in order. The header never changes with the data.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "attempt_ref", "property_id", "opened_at", "closed_at", "format_id", "channel", "outcome_code", "decision_mode", "shadow_outcome", "reason_codes", "guided_retries",
        "human_outcome", "human_decided_at", "review_seconds", "duration_ms", "policy_version", "policy_hash_prefix",
    ];

    /// <summary>The header line, without its end of line.</summary>
    public static string Header => string.Join(',', Columns);

    /// <summary>The name of the file: <c>identity-verifications-2026-11-01-2026-11-27.csv</c>, the first and the last day of the period (Europe/Rome), the way the metrics name them.</summary>
    public static string FileName(DateOnly first, DateOnly last) =>
        $"identity-verifications-{first.ToString(DayFormat, CultureInfo.InvariantCulture)}-{last.ToString(DayFormat, CultureInfo.InvariantCulture)}.csv";

    /// <summary>
    /// Reads the steps opened in <c>[fromUtc, toUtc)</c> in the scope (the org and the properties the caller reaches) and pairs them with the decisions: the one place the
    /// database is touched. The lines come from the file it returns.
    /// </summary>
    public async Task<IdentityPilotFile> OpenAsync(HostScope scope, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var records = await IdentityVerificationLedger.ReadAsync(
            db,
            scope,
            fromUtc,
            toUtc,
            clock.GetUtcNow().UtcDateTime,
            IdentityVerificationLedger.AbandonedAfterOf(options.Value),
            withReasons: true,
            cancellationToken);
        return new IdentityPilotFile(records);
    }

    /// <summary>
    /// The pseudonym of an attempt: the first <see cref="AttemptRefLength"/> hexadecimal characters (lower case) of the SHA-256 of <c>"{session id}:{org id}"</c> (both as 36
    /// characters in lower case), so the same step always has the same reference, two steps never share one in practice, and the id of the session cannot be read from it.
    /// </summary>
    public static string AttemptRef(Guid sessionId, Guid orgId)
    {
        var input = Encoding.UTF8.GetBytes(string.Concat(sessionId.ToString("D"), ":", orgId.ToString("D")));
        return Convert.ToHexStringLower(SHA256.HashData(input).AsSpan(0, AttemptRefLength / 2));
    }

    /// <summary>The line of a record, without its end of line: the cells in the order of <see cref="Columns"/>, each escaped by <see cref="Cell"/>.</summary>
    internal static string Line(IdentityVerificationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var decided = record.Decided;
        var human = record.Human;
        string[] cells =
        [
            AttemptRef(record.SessionId, record.OrgId),
            record.PropertyId.ToString("D"),
            Instant(record.OpenedAtUtc),
            record.ClosedAtUtc is { } closed ? Instant(closed) : string.Empty,
            record.FormatId,
            record.Channel,
            IdentityOutcomeCategories.CodeOf(record.Outcome),
            decided is null ? string.Empty : ModeCode(decided.Mode),
            decided?.Shadow is { } shadow ? (shadow == BiometricOutcome.Accept ? Approve : Review) : string.Empty,
            decided is null ? string.Empty : ReasonCodes(decided.ReasonsJson),
            record.GuidedRetries.ToString(CultureInfo.InvariantCulture),
            human is null ? string.Empty : (human.Approved ? Approve : Reject),
            human is null ? string.Empty : Instant(human.DecidedAtUtc),
            human?.ReviewSeconds is { } seconds ? seconds.ToString(SecondsFormat, CultureInfo.InvariantCulture) : string.Empty,
            decided?.DurationMs is { } duration ? duration.ToString(CultureInfo.InvariantCulture) : string.Empty,
            decided?.PolicyVersion ?? string.Empty,
            PolicyHashPrefix(decided?.PolicyHash),
        ];
        return string.Join(',', cells.Select(Cell));
    }

    /// <summary>
    /// One cell, escaped. <c>null</c> is an empty cell. A cell that a spreadsheet would evaluate as a formula (<see cref="CsvWriter.StartsLikeAFormula"/>, the one rule of every file
    /// the host downloads: it starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return) gets a leading apostrophe; a cell with a comma, a quote or a line break
    /// is put in quotes with the quotes doubled (RFC 4180: the separator of this file is the comma, not the semicolon of <see cref="CsvWriter"/>, so the quoting is done here and only the
    /// rule of the formulas is shared).
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var cell = CsvWriter.StartsLikeAFormula(value) ? "'" + value : value;
        return cell.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : cell;
    }

    private static string ModeCode(IdentityDecisionMode mode)
    {
        return mode switch
        {
            IdentityDecisionMode.Observation => "observation",
            IdentityDecisionMode.Automatic => "automatic",
            _ => "legacy",
        };
    }

    /// <summary>
    /// The checks that did not pass, as <c>check:result</c> joined by <c>|</c>, in the order the decision recorded them. Only the codes are taken from the stored reasons, never the
    /// number a check was compared with (a score of a person) and never a text that is not a code.
    /// </summary>
    private static string ReasonCodes(string? reasonsJson)
    {
        var codes = new List<string>();
        foreach (var reason in VerificationReasons.Parse(reasonsJson))
        {
            if (string.Equals(reason.Result, VerificationReasons.Results.Passed, StringComparison.Ordinal) || !IsCode(reason.Check) || !IsCode(reason.Result))
                continue;

            codes.Add(reason.Check + ":" + reason.Result);
        }

        return string.Join('|', codes);
    }

    /// <summary>A code: 1 to <see cref="MaxCodeLength"/> lower case letters, digits or underscores.</summary>
    private static bool IsCode(string value)
    {
        return value.Length is > 0 and <= MaxCodeLength && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }

    private static string PolicyHashPrefix(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
            return string.Empty;

        return hash.Length <= PolicyHashPrefixLength ? hash : hash[..PolicyHashPrefixLength];
    }

    /// <summary>A time in UTC, ISO 8601, to the millisecond, whatever the culture of the process (a value read from the database has no kind: it is UTC).</summary>
    private static string Instant(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return utc.ToString(InstantFormat, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The file of the pilot, read and paired (<see cref="IdentityPilotExport.OpenAsync"/>) and not written yet: the header and the lines come one at a time, so the answer streams and
/// nothing is built as one text.
/// </summary>
public sealed class IdentityPilotFile
{
    private readonly IReadOnlyList<IdentityVerificationRecord> _records;

    internal IdentityPilotFile(IReadOnlyList<IdentityVerificationRecord> records)
    {
        _records = records;
    }

    /// <summary>The rows of the file, the header not counted.</summary>
    public int RowCount => _records.Count;

    /// <summary>The header and then the lines of the rows, each ended by <see cref="IdentityPilotExport.NewLine"/>.</summary>
    public IEnumerable<string> Lines()
    {
        yield return IdentityPilotExport.Header + IdentityPilotExport.NewLine;
        foreach (var record in _records)
            yield return IdentityPilotExport.Line(record) + IdentityPilotExport.NewLine;
    }
}
