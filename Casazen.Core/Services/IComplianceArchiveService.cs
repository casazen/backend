using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The archive of the proofs CasaZen holds for the compliance obligations, the log of what was sent, the coverage report and the audit exports
/// (E03/F02/US03/T01, NF-47, NF-52, CHK-08, CHK-12 point 8). It reads and writes only what CasaZen owns: the files a host uploaded
/// (<see cref="ManualComplianceProof"/>: the receipt of an Alloggiati communication, the proof of an ISTAT communication, the receipt of a
/// tourist tax declaration or payment) and the lines of the send log. It never holds an Octorate screen, a user's attestation or the audit
/// of a handoff: where an outcome lives only in the Cassetto Compliance of Octorate the report says so and proves nothing.
/// </summary>
/// <remarks>
/// <para><b>Scope.</b> Every read and write is of the org of <see cref="HostScope"/> and of the properties it reaches (<c>InScope</c>): a proof,
/// a stay or a property of another org, or one outside the reach of a collaborator «Solo alcuni», answers <see cref="Exceptions.NotFoundException"/>
/// (404) exactly as one that does not exist. The service never checks roles.</para>
/// <para><b>Retention.</b> A proof is kept <see cref="ManualComplianceProof.RetentionYears"/> years from its upload and shown until the retention
/// job removes it: nothing here deletes a proof or a line of the log. A soft-deleted property keeps its proofs visible (compliance exports).</para>
/// <para><b>Money.</b> The receipt of a tourist tax declaration or payment is a record of money (<see cref="ComplianceArchiveRules.MoneyKinds"/>). The
/// service never checks roles, so the caller says whether its person may read money (<c>payment.read</c>, which the collaborator never holds):
/// without it those kinds are not listed, not counted, not exported and not downloadable (404, as for a proof that does not exist). Asking for
/// them is opt-in (<c>MoneyProofs</c> false by default, <c>moneyProofs</c> of the download has no default), so a new caller cannot read money by forgetting it.</para>
/// <para><b>What is not here.</b> The Alloggiati receipts CasaZen would download itself, the ISTAT proofs of the regional service and the files
/// of the Swiss municipalities belong to features postponed on 2026-11-05 (<c>kb/plan/deferred</c>): they join this list when those features
/// return. Their rows do not exist today.</para>
/// </remarks>
public interface IComplianceArchiveService
{
    /// <summary>
    /// The proofs held, newest first, one page of them. The receipts of a tourist tax declaration or payment are part of the list and of its
    /// <see cref="ComplianceArchivePage.TotalCount"/> only when <see cref="ComplianceArchiveQuery.MoneyProofs"/> is true.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">A property is asked for that the caller does not reach.</exception>
    Task<ComplianceArchivePage> ListAsync(HostScope scope, ComplianceArchiveQuery query, CancellationToken ct);

    /// <summary>The proofs of one stay (the Alloggiati receipts of its arrival day) and the log of what was sent for it.</summary>
    /// <exception cref="Exceptions.NotFoundException">The stay is not the caller's or is out of its reach.</exception>
    Task<ComplianceBookingCompliance> GetBookingComplianceAsync(HostScope scope, Guid bookingId, CancellationToken ct);

    /// <summary>
    /// How to hand a proof to the caller: a short-lived signed address of the private bucket, or the content itself when the storage cannot
    /// sign (development and tests). Never a public address.
    /// </summary>
    /// <param name="scope">The reach of the caller.</param>
    /// <param name="proofId">The proof.</param>
    /// <param name="moneyProofs">Whether the caller may read money (<c>payment.read</c>): without it the receipt of a tourist tax declaration or payment is not found.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="Exceptions.NotFoundException">The proof is not the caller's, is out of its reach, is a record of money the caller may not read, or its file is no longer there.</exception>
    Task<ComplianceArchiveDownload> OpenDownloadAsync(HostScope scope, Guid proofId, bool moneyProofs, CancellationToken ct);

    /// <summary>
    /// A ZIP of every proof of the period plus an <c>index.csv</c>, built on request and never stored. Bounded by
    /// <c>Compliance:Archive:ExportMaxFiles</c> and <c>ExportMaxMegabytes</c>. With <c>Features:ComplianceTrafficLight</c> on it also carries
    /// <c>rapporto-copertura.csv</c>, the coverage of the period (the rows held only by Octorate say «Da verificare nel Cassetto Compliance»). The
    /// receipts of a tourist tax declaration or payment are in the ZIP, in its index and in <see cref="ComplianceArchiveExportFile.FileCount"/> only
    /// when <see cref="ComplianceArchiveExportQuery.MoneyProofs"/> is true.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">The proofs of the period are more than the export carries (422 <c>compliance_export_too_large</c>).</exception>
    Task<ComplianceArchiveExportFile> ExportAsync(HostScope scope, ComplianceArchiveExportQuery query, CancellationToken ct);

    /// <summary>
    /// The obligations expected for every stay and period of the caller's reach (Alloggiati, ISTAT, tourist tax declaration and payment) with the
    /// proof CasaZen holds for each: <see cref="ComplianceEvidenceClass.OfficialProof"/>, <see cref="ComplianceEvidenceClass.ExternalVerificationRequired"/>
    /// (the outcome is held only by Octorate: the row carries the handoff topic and nothing is inferred), <see cref="ComplianceEvidenceClass.Missing"/>
    /// or <see cref="ComplianceEvidenceClass.NotApplicable"/> (nothing is owed: a no-show). Complete only when every applicable obligation has its proof.
    /// </summary>
    Task<ComplianceCoverageReport> GetCoverageReportAsync(HostScope scope, ComplianceReportQuery query, CancellationToken ct);

    /// <summary>The coverage report as a PDF or a CSV, built on request and never stored. It never claims to be issued by an authority.</summary>
    Task<ComplianceReportFile> ExportReportAsync(HostScope scope, ComplianceReportQuery query, ComplianceReportFormat format, CancellationToken ct);

    /// <summary>
    /// Stores a proof the host uploaded and lists it in the archive: a PDF, a JPEG or a PNG (judged on its content, not on its name or its
    /// declared type) of at most <c>Compliance:Archive:UploadMaxMegabytes</c>, in the private bucket under a name made by the server.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The property is not the caller's or is out of its reach.</exception>
    /// <exception cref="Exceptions.DomainRuleException">The kind or the day is not acceptable, or there is no file.</exception>
    /// <exception cref="Exceptions.DomainPayloadTooLargeException">The file is larger than the limit.</exception>
    /// <exception cref="Exceptions.DomainUnsupportedMediaTypeException">The file is not a PDF, a JPEG or a PNG.</exception>
    Task<ComplianceArchiveEntry> UploadProofAsync(HostScope scope, UploadComplianceProofRequest request, Stream content, CancellationToken ct);
}

/// <summary>Limits of the lists of the archive.</summary>
public static class ComplianceArchiveLimits
{
    /// <summary>The proofs of a page when the caller does not say.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>The most proofs of a page.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The first year a proof may be filed under: before it is a typing mistake.</summary>
    public const int MinYear = 2000;
}

/// <summary>Error codes of the archive (ProblemDetails <c>code</c>, FD-05).</summary>
public static class ComplianceArchiveErrorCodes
{
    /// <summary>404: no proof with that id for the caller (another org's, one out of its reach and an unknown one are the same answer).</summary>
    public const string ProofNotFound = "compliance_proof_not_found";

    /// <summary>404: no stay with that id for the caller (same answer for another org's and for one out of its reach).</summary>
    public const string BookingNotFound = "compliance_booking_not_found";

    /// <summary>422: the proof has no file, or an empty one.</summary>
    public const string ProofFileInvalid = "compliance_proof_file_invalid";

    /// <summary>415: the file is not a PDF, a JPEG or a PNG, whatever its name or its declared type say.</summary>
    public const string ProofTypeUnsupported = "compliance_proof_type_unsupported";

    /// <summary>413: the file is larger than <c>Compliance:Archive:UploadMaxMegabytes</c>.</summary>
    public const string ProofTooLarge = "compliance_proof_too_large";

    /// <summary>422: the day of the proof is in the future or before the first supported year.</summary>
    public const string ProofDayInvalid = "compliance_proof_day_invalid";

    /// <summary>422: the kind of the proof is not one of <see cref="ManualProofKind"/>.</summary>
    public const string ProofKindInvalid = "compliance_proof_kind_invalid";

    /// <summary>422: the proofs of the period are more than one export carries; the host asks for a shorter period.</summary>
    public const string ExportTooLarge = "compliance_export_too_large";
}

/// <summary>What the archive page is asked: one property, a range of days, a kind, and the page.</summary>
/// <param name="PropertyId">Only this property; <c>null</c> = every property of the caller's reach. It has to be one the caller reaches.</param>
/// <param name="From">The first day (included) of the day or period the proofs are about (<see cref="ComplianceArchiveEntry.Day"/>); <c>null</c> = no lower bound.</param>
/// <param name="To">The last day (included); <c>null</c> = no upper bound.</param>
/// <param name="Kind">Only this kind of proof; <c>null</c> = every kind.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">The proofs of a page, 1 to <see cref="ComplianceArchiveLimits.MaxPageSize"/>.</param>
/// <param name="MoneyProofs">
/// Whether the caller may read money (<c>payment.read</c>). Only then are the receipts of a tourist tax declaration or payment
/// (<see cref="ComplianceArchiveRules.MoneyKinds"/>) in the list and in its total; false (the default) leaves them out, whatever <paramref name="Kind"/> asks.
/// </param>
public sealed record ComplianceArchiveQuery(
    Guid? PropertyId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    ManualProofKind? Kind = null,
    int Page = 1,
    int PageSize = ComplianceArchiveLimits.DefaultPageSize,
    bool MoneyProofs = false);

/// <summary>
/// One proof CasaZen holds. It names no guest and no person: a property, a kind, a day, where it comes from and until when it is kept.
/// </summary>
/// <param name="Id">The id of the proof, the one of the download.</param>
/// <param name="Kind">What it proves.</param>
/// <param name="PropertyId">The property it is about.</param>
/// <param name="PropertyName">The name of that property.</param>
/// <param name="Day">The day of the communication (Alloggiati) or the first day of the month (ISTAT, tourist tax), a Europe/Rome calendar date.</param>
/// <param name="Period">The month (<c>yyyy-MM</c>) for the kinds that are about a period; <c>null</c> for a day.</param>
/// <param name="Source">Where it comes from: <see cref="ComplianceEvidenceSources.ManualUpload"/> (the host uploaded it).</param>
/// <param name="ReceivedAt">When CasaZen received it (UTC).</param>
/// <param name="RetainUntil">Until when CasaZen keeps it (UTC).</param>
/// <param name="ContentType"><c>application/pdf</c>, <c>image/jpeg</c> or <c>image/png</c>.</param>
public sealed record ComplianceArchiveEntry(
    Guid Id,
    ManualProofKind Kind,
    Guid PropertyId,
    string PropertyName,
    DateOnly Day,
    string? Period,
    string Source,
    DateTime ReceivedAt,
    DateTime RetainUntil,
    string ContentType);

/// <summary>One page of the archive.</summary>
/// <param name="Items">The proofs, newest first.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">The proofs of a page.</param>
/// <param name="TotalCount">The proofs that match the query, all pages together.</param>
public sealed record ComplianceArchivePage(IReadOnlyList<ComplianceArchiveEntry> Items, int Page, int PageSize, int TotalCount);

/// <summary>One line of the send log of a stay: what was sent, when, and by whom.</summary>
/// <param name="Id">The id of the line.</param>
/// <param name="Kind">What was sent.</param>
/// <param name="Sender">CasaZen, the channel manager or the host by hand.</param>
/// <param name="SentAt">When (UTC). For a communication the host declared it is the day it said it sent it (midnight UTC of the Rome day).</param>
/// <param name="Reference">A receipt or confirmation id; never guest data; <c>null</c> when there is none.</param>
/// <param name="SentBy">The name of the person who declared it, only for a member of the caller's own org; <c>null</c> for CasaZen and the channel manager.</param>
public sealed record ComplianceSendLogLine(
    Guid Id,
    ComplianceSendKind Kind,
    ComplianceSender Sender,
    DateTime SentAt,
    string? Reference,
    string? SentBy);

/// <summary>What the archive holds about one stay.</summary>
/// <param name="BookingId">The stay.</param>
/// <param name="PropertyId">Its property.</param>
/// <param name="Proofs">The proofs of its arrival day, newest first.</param>
/// <param name="SendLog">What was sent for it, newest first.</param>
public sealed record ComplianceBookingCompliance(
    Guid BookingId,
    Guid PropertyId,
    IReadOnlyList<ComplianceArchiveEntry> Proofs,
    IReadOnlyList<ComplianceSendLogLine> SendLog);

/// <summary>
/// How a proof is handed to the caller. Exactly one of <paramref name="SignedUrl"/> and <paramref name="Content"/> is set. The caller
/// disposes <paramref name="Content"/>.
/// </summary>
/// <param name="SignedUrl">A short-lived signed address of the private bucket (the caller redirects to it); <c>null</c> when the storage cannot sign.</param>
/// <param name="Content">The file, when there is no signed address.</param>
/// <param name="FileName">A name made by the server from the kind and the day.</param>
/// <param name="ContentType">What the file is.</param>
public sealed record ComplianceArchiveDownload(Uri? SignedUrl, Stream? Content, string FileName, string ContentType);

/// <summary>The proofs of a period to export: the days are Europe/Rome calendar dates, both included.</summary>
/// <param name="From">First day.</param>
/// <param name="To">Last day.</param>
/// <param name="PropertyId">Only this property; <c>null</c> = every property of the caller's reach.</param>
/// <param name="MoneyProofs">
/// Whether the caller may read money (<c>payment.read</c>). Only then are the receipts of a tourist tax declaration or payment in the ZIP, its
/// index and its file count; false (the default) leaves them out.
/// </param>
public sealed record ComplianceArchiveExportQuery(DateOnly From, DateOnly To, Guid? PropertyId = null, bool MoneyProofs = false);

/// <summary>An export built in memory: the ZIP, positioned at its start; the caller disposes it.</summary>
/// <param name="Content">The ZIP.</param>
/// <param name="FileName">A name made by the server from the period.</param>
/// <param name="FileCount">The proofs in it (the index is not counted).</param>
public sealed record ComplianceArchiveExportFile(Stream Content, string FileName, int FileCount) : IAsyncDisposable
{
    /// <summary>Disposes the ZIP.</summary>
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>What a proof is asked to be uploaded as. The file travels next to it.</summary>
/// <param name="PropertyId">The property the proof is about.</param>
/// <param name="Kind">What it proves.</param>
/// <param name="Day">The day of the communication, or any day of the month for a period kind (filed under the first day of that month).</param>
/// <param name="UploadedByUserId">The person who uploads it (<c>User.Id</c>).</param>
public sealed record UploadComplianceProofRequest(Guid PropertyId, ManualProofKind Kind, DateOnly Day, string UploadedByUserId);

/// <summary>The days a coverage report looks at: Europe/Rome calendar dates, both included.</summary>
/// <param name="From">First day.</param>
/// <param name="To">Last day.</param>
/// <param name="PropertyId">Only this property; <c>null</c> = every property of the caller's reach.</param>
public sealed record ComplianceReportQuery(DateOnly From, DateOnly To, Guid? PropertyId = null);

/// <summary>The reasons the coverage report adds to the stable codes of the traffic light (<see cref="ComplianceReasonCodes"/>).</summary>
public static class ComplianceReportReasons
{
    /// <summary>Nothing is owed for the stay: the guests never came (a no-show), so there is nobody to communicate.</summary>
    public const string NoShow = "no_show";
}

/// <summary>
/// One obligation of the coverage report. A row about a stay names it by its code and its arrival day, never by its guest.
/// </summary>
/// <param name="Kind"><see cref="ComplianceItemKind.Alloggiati"/> (a stay), <see cref="ComplianceItemKind.Istat"/> or <see cref="ComplianceItemKind.TouristTax"/> (a month of a property).</param>
/// <param name="PropertyId">The property.</param>
/// <param name="PropertyName">Its name.</param>
/// <param name="BookingId">The stay, for an Alloggiati row.</param>
/// <param name="BookingCode">The reference of that stay the host knows (<c>XXXXX-XXXXX</c>).</param>
/// <param name="Day">The arrival day of the stay (Alloggiati); <c>null</c> for a month.</param>
/// <param name="Period">The month (<c>yyyy-MM</c>) for ISTAT and tourist tax; <c>null</c> for a stay.</param>
/// <param name="EvidenceClass">The state of the obligation: proof, outcome held only by Octorate, missing, or nothing owed.</param>
/// <param name="EvidenceSource">Where the proof comes from (<see cref="ComplianceEvidenceSources"/>), or <c>octorate</c> for a row held only there; <c>null</c> otherwise.</param>
/// <param name="ProofAt">When the proof was archived (UTC); <c>null</c> without one.</param>
/// <param name="DeadlineAt">The deadline (UTC) when CasaZen knows one; <c>null</c> otherwise.</param>
/// <param name="Action">What fixes it (<see cref="ComplianceCockpitAction"/>); <c>null</c> when nothing is left to do.</param>
/// <param name="Reason">A stable code the clients translate (<see cref="ComplianceReasonCodes"/>, <see cref="ComplianceReportReasons"/>).</param>
/// <param name="HandoffTopic">For a row held only by Octorate: the topic of the controlled handoff; the client asks CHK-15 for the destination.</param>
public sealed record ComplianceReportRow(
    ComplianceItemKind Kind,
    Guid PropertyId,
    string PropertyName,
    Guid? BookingId,
    string? BookingCode,
    DateOnly? Day,
    string? Period,
    ComplianceEvidenceClass EvidenceClass,
    string? EvidenceSource,
    DateTime? ProofAt,
    DateTime? DeadlineAt,
    ComplianceCockpitAction? Action,
    string Reason,
    OctorateHandoffTopic? HandoffTopic);

/// <summary>How many rows of the report are in each state.</summary>
/// <param name="OfficialProof">Obligations with an archived proof.</param>
/// <param name="ExternalVerificationRequired">Obligations whose outcome is held only by Octorate.</param>
/// <param name="Missing">Obligations without a proof.</param>
/// <param name="NotApplicable">Stays that owe nothing.</param>
public sealed record ComplianceCoverageCounts(int OfficialProof, int ExternalVerificationRequired, int Missing, int NotApplicable);

/// <summary>
/// The coverage report of a period. <see cref="Complete"/> is true only when every applicable obligation has an archived proof: a row held
/// only by Octorate, a missing one and one still open all keep it false.
/// </summary>
/// <param name="From">First day.</param>
/// <param name="To">Last day.</param>
/// <param name="PropertyId">The property asked for; <c>null</c> = all of the caller's reach.</param>
/// <param name="GeneratedAt">When the report was made (UTC).</param>
/// <param name="Complete">Whether every applicable obligation has its proof.</param>
/// <param name="Counts">The rows by state.</param>
/// <param name="Rows">The obligations, incomplete ones included: stays by arrival day, then months.</param>
public sealed record ComplianceCoverageReport(
    DateOnly From,
    DateOnly To,
    Guid? PropertyId,
    DateTime GeneratedAt,
    bool Complete,
    ComplianceCoverageCounts Counts,
    IReadOnlyList<ComplianceReportRow> Rows);

/// <summary>The file formats of the report.</summary>
public enum ComplianceReportFormat
{
    /// <summary>A spreadsheet for Italian settings: UTF-8 with a BOM, <c>;</c>, one table.</summary>
    Csv,

    /// <summary>A PDF on A4.</summary>
    Pdf,
}

/// <summary>A report built on request: the bytes, what they are, and a name made by the server from the period.</summary>
/// <param name="Content">The file.</param>
/// <param name="ContentType"><c>text/csv</c> or <c>application/pdf</c>.</param>
/// <param name="FileName">The name to download it under.</param>
public sealed record ComplianceReportFile(byte[] Content, string ContentType, string FileName);

/// <summary>The rules of the proofs of the archive, apart from any storage.</summary>
public static class ComplianceArchiveRules
{
    /// <summary>
    /// The kinds that are records of money: the receipt of a tourist tax declaration and of a tourist tax payment (the same rule that asks
    /// <c>payment.write</c> to upload them). Reading them asks <c>payment.read</c>, which the collaborator never holds (AM-03). The one definition:
    /// the web layer decides with it who uploads, and the service leaves them out of every read that was not granted money.
    /// </summary>
    public static readonly ManualProofKind[] MoneyKinds = [ManualProofKind.TouristTaxDeclaration, ManualProofKind.TouristTaxPayment];

    /// <summary>True for a record of money (<see cref="MoneyKinds"/>).</summary>
    public static bool IsMoneyKind(ManualProofKind kind) => MoneyKinds.Contains(kind);

    /// <summary>True for the kinds that are about a month (ISTAT and tourist tax), filed under the first day of the month.</summary>
    public static bool IsPeriodKind(ManualProofKind kind) =>
        kind is ManualProofKind.IstatProof or ManualProofKind.TouristTaxDeclaration or ManualProofKind.TouristTaxPayment;

    /// <summary>The day a proof is filed under: the day itself for the Alloggiati receipt, the first day of its month for a period kind.</summary>
    public static DateOnly FileDay(ManualProofKind kind, DateOnly day) =>
        IsPeriodKind(kind) ? new DateOnly(day.Year, day.Month, 1) : day;

    /// <summary>The month (<c>yyyy-MM</c>) of a period kind; <c>null</c> for a day.</summary>
    public static string? PeriodOf(ManualProofKind kind, DateOnly day) =>
        IsPeriodKind(kind) ? ComplianceLightRules.PeriodKey(day) : null;

    /// <summary>The extension of a content type of the archive (<c>.pdf</c>, <c>.jpg</c>, <c>.png</c>).</summary>
    public static string ExtensionOf(string contentType) => contentType switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        _ => string.Empty,
    };

    /// <summary>A short stable word for a kind, for the names of files.</summary>
    public static string SlugOf(ManualProofKind kind) => kind switch
    {
        ManualProofKind.AlloggiatiReceipt => "alloggiati",
        ManualProofKind.IstatProof => "istat",
        ManualProofKind.TouristTaxDeclaration => "imposta-soggiorno-dichiarazione",
        ManualProofKind.TouristTaxPayment => "imposta-soggiorno-pagamento",
        _ => "prova",
    };

    /// <summary>
    /// The name a proof is downloaded under, made from what it is and not from what the host called the file:
    /// <c>alloggiati-2026-03-14.pdf</c>, <c>istat-2026-03.pdf</c>.
    /// </summary>
    public static string DownloadName(ManualProofKind kind, DateOnly day, string contentType) =>
        $"{SlugOf(kind)}-{(IsPeriodKind(kind) ? ComplianceLightRules.PeriodKey(day) : day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))}{ExtensionOf(contentType)}";

    /// <summary>Until when a proof uploaded at <paramref name="uploadedAt"/> is kept.</summary>
    public static DateTime RetainUntil(DateTime uploadedAt) => uploadedAt.AddYears(ManualComplianceProof.RetentionYears);
}
