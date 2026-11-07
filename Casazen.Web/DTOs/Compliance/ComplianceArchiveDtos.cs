using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Compliance;

/// <summary>
/// One proof CasaZen holds (<c>GET api/compliance/archive</c>, E03/F02/US03/T01). It names no guest and no person: a property, a kind, a day or a
/// month, where it comes from and until when it is kept. <see cref="DownloadPath"/> is the way to the file: an authenticated request to it
/// answers with a short-lived signed address of the private bucket; there is no public address of a proof.
/// </summary>
/// <param name="Id">The id of the proof.</param>
/// <param name="Kind"><c>AlloggiatiReceipt</c>, <c>IstatProof</c>, <c>TouristTaxDeclaration</c> or <c>TouristTaxPayment</c>.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="PropertyName">Its name.</param>
/// <param name="Day">The day of the communication, or the first day of the month for the kinds that are about a month.</param>
/// <param name="Period">The month (<c>yyyy-MM</c>) for those kinds; <c>null</c> for a day.</param>
/// <param name="Source"><c>manual_upload</c> (the host uploaded it) or <c>casazen_receipt</c> (CasaZen downloaded it).</param>
/// <param name="ReceivedAt">When CasaZen received it (UTC).</param>
/// <param name="RetainUntil">Until when CasaZen keeps it (UTC): five years from the upload.</param>
/// <param name="ContentType"><c>application/pdf</c>, <c>image/jpeg</c> or <c>image/png</c>.</param>
/// <param name="DownloadPath">The path of the download (<c>/api/compliance/archive/{id}/download</c>).</param>
public sealed record ComplianceArchiveEntryDto(
    Guid Id,
    ManualProofKind Kind,
    Guid PropertyId,
    string PropertyName,
    DateOnly Day,
    string? Period,
    string Source,
    DateTime ReceivedAt,
    DateTime RetainUntil,
    string ContentType,
    string DownloadPath)
{
    /// <summary>The path a proof is downloaded from.</summary>
    public static string DownloadPathOf(Guid id) => $"/api/compliance/archive/{id}/download";

    public static ComplianceArchiveEntryDto From(ComplianceArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new ComplianceArchiveEntryDto(
            entry.Id,
            entry.Kind,
            entry.PropertyId,
            entry.PropertyName,
            entry.Day,
            entry.Period,
            entry.Source,
            entry.ReceivedAt,
            entry.RetainUntil,
            entry.ContentType,
            DownloadPathOf(entry.Id));
    }
}

/// <summary>One page of the archive, newest first, with the address of the Alloggiati Web portal where the host gets a receipt.</summary>
/// <param name="Items">The proofs of the page.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">The proofs of a page.</param>
/// <param name="TotalCount">The proofs that match, all pages together.</param>
/// <param name="AlloggiatiPortalUrl">The portal of Alloggiati Web (<c>https</c>, from the configuration): the guided «Get the receipt» step opens it.</param>
public sealed record ComplianceArchivePageDto(
    IReadOnlyList<ComplianceArchiveEntryDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    string AlloggiatiPortalUrl)
{
    public static ComplianceArchivePageDto From(ComplianceArchivePage page, string alloggiatiPortalUrl)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new ComplianceArchivePageDto(
            page.Items.Select(ComplianceArchiveEntryDto.From).ToList(),
            page.Page,
            page.PageSize,
            page.TotalCount,
            alloggiatiPortalUrl);
    }
}

/// <summary>One line of the send log of a stay: what was sent, when and by whom. No guest data.</summary>
/// <param name="Id">The id of the line.</param>
/// <param name="Kind">What was sent (<c>AlloggiatiGuests</c>, <c>IstatPeriod</c>, <c>TouristTaxPeriod</c>).</param>
/// <param name="Sender"><c>CasaZen</c>, <c>ChannelManager</c> or <c>Manual</c> (the host sent it by hand and declared it).</param>
/// <param name="SentAt">When (UTC); for a declaration of the host, the day it said it sent it.</param>
/// <param name="Reference">A receipt or confirmation id; <c>null</c> when there is none.</param>
/// <param name="SentBy">The name of the person of the org who declared it; <c>null</c> for CasaZen and the channel manager.</param>
public sealed record ComplianceSendLogLineDto(
    Guid Id,
    ComplianceSendKind Kind,
    ComplianceSender Sender,
    DateTime SentAt,
    string? Reference,
    string? SentBy)
{
    public static ComplianceSendLogLineDto From(ComplianceSendLogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new ComplianceSendLogLineDto(line.Id, line.Kind, line.Sender, line.SentAt, line.Reference, line.SentBy);
    }
}

/// <summary>What the archive holds about one stay (<c>GET api/bookings/{bookingId}/compliance</c>).</summary>
/// <param name="BookingId">The stay.</param>
/// <param name="PropertyId">Its property.</param>
/// <param name="Proofs">The proofs of its arrival day, newest first.</param>
/// <param name="SendLog">What was sent for it, newest first.</param>
public sealed record ComplianceBookingComplianceDto(
    Guid BookingId,
    Guid PropertyId,
    IReadOnlyList<ComplianceArchiveEntryDto> Proofs,
    IReadOnlyList<ComplianceSendLogLineDto> SendLog)
{
    public static ComplianceBookingComplianceDto From(ComplianceBookingCompliance view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new ComplianceBookingComplianceDto(
            view.BookingId,
            view.PropertyId,
            view.Proofs.Select(ComplianceArchiveEntryDto.From).ToList(),
            view.SendLog.Select(ComplianceSendLogLineDto.From).ToList());
    }
}

/// <summary>
/// One obligation of the coverage report (<c>GET api/compliance/report</c>). <see cref="EvidenceClass"/> is the state: <c>OfficialProof</c>,
/// <c>ExternalVerificationRequired</c> (the outcome is held only by Octorate: <see cref="HandoffTopic"/> says which check the host starts; nothing
/// is inferred), <c>Missing</c> or <c>NotApplicable</c> (nothing is owed).
/// </summary>
/// <param name="Kind"><c>Alloggiati</c> (a stay), <c>Istat</c> or <c>TouristTax</c> (a month of a property).</param>
/// <param name="PropertyId">The property.</param>
/// <param name="PropertyName">Its name.</param>
/// <param name="BookingId">The stay, for an Alloggiati row.</param>
/// <param name="BookingCode">The reference of that stay the host knows.</param>
/// <param name="Day">The arrival day of the stay.</param>
/// <param name="Period">The month (<c>yyyy-MM</c>) for ISTAT and tourist tax.</param>
/// <param name="EvidenceClass">The state of the obligation.</param>
/// <param name="EvidenceSource"><c>casazen_receipt</c>, <c>manual_upload</c> or <c>octorate</c>; <c>null</c> otherwise.</param>
/// <param name="ProofAt">When the proof was archived (UTC); <c>null</c> without one.</param>
/// <param name="DeadlineAt">The deadline (UTC) when CasaZen knows one.</param>
/// <param name="Action">What fixes it (<c>ComplianceCockpitAction</c> by name); <c>null</c> when nothing is left to do.</param>
/// <param name="Reason">A stable code the clients translate.</param>
/// <param name="HandoffTopic"><c>Alloggiati</c> or <c>Istat</c> for a row held only by Octorate; the destination comes from <c>POST api/compliance/octorate-handoffs</c>.</param>
public sealed record ComplianceReportRowDto(
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
    OctorateHandoffTopic? HandoffTopic)
{
    public static ComplianceReportRowDto From(ComplianceReportRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ComplianceReportRowDto(
            row.Kind,
            row.PropertyId,
            row.PropertyName,
            row.BookingId,
            row.BookingCode,
            row.Day,
            row.Period,
            row.EvidenceClass,
            row.EvidenceSource,
            row.ProofAt,
            row.DeadlineAt,
            row.Action,
            row.Reason,
            row.HandoffTopic);
    }
}

/// <summary>The coverage report of a period: complete only when every applicable obligation has its archived proof.</summary>
/// <param name="From">First day.</param>
/// <param name="To">Last day.</param>
/// <param name="PropertyId">The property asked for; <c>null</c> = all of the caller's reach.</param>
/// <param name="GeneratedAt">When the report was made (UTC).</param>
/// <param name="Complete">Whether every applicable obligation has its proof; false while any is missing or held only by Octorate.</param>
/// <param name="OfficialProof">Rows with an archived proof.</param>
/// <param name="ExternalVerificationRequired">Rows whose outcome is held only by Octorate.</param>
/// <param name="Missing">Rows without a proof.</param>
/// <param name="NotApplicable">Stays that owe nothing.</param>
/// <param name="Rows">The obligations, incomplete ones included.</param>
public sealed record ComplianceCoverageReportDto(
    DateOnly From,
    DateOnly To,
    Guid? PropertyId,
    DateTime GeneratedAt,
    bool Complete,
    int OfficialProof,
    int ExternalVerificationRequired,
    int Missing,
    int NotApplicable,
    IReadOnlyList<ComplianceReportRowDto> Rows)
{
    /// <summary>The answer for a report (named <c>Of</c>: <c>From</c> is the first day of the period).</summary>
    public static ComplianceCoverageReportDto Of(ComplianceCoverageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new ComplianceCoverageReportDto(
            report.From,
            report.To,
            report.PropertyId,
            report.GeneratedAt,
            report.Complete,
            report.Counts.OfficialProof,
            report.Counts.ExternalVerificationRequired,
            report.Counts.Missing,
            report.Counts.NotApplicable,
            report.Rows.Select(ComplianceReportRowDto.From).ToList());
    }
}

/// <summary>
/// The form of an upload (<c>POST api/compliance/archive/proofs</c>, <c>multipart/form-data</c>): the property, what the file proves, the day it
/// is about, and the file (a PDF, a JPEG or a PNG, judged on its content). The person who uploads it is the caller, never a field.
/// </summary>
public sealed class UploadComplianceProofForm
{
    /// <summary>The property the proof is about.</summary>
    [Required(ErrorMessage = "ComplianceProofPropertyRequired")]
    public Guid? PropertyId { get; set; }

    /// <summary><c>AlloggiatiReceipt</c>, <c>IstatProof</c>, <c>TouristTaxDeclaration</c> or <c>TouristTaxPayment</c>.</summary>
    [Required(ErrorMessage = "ComplianceProofKindRequired")]
    public ManualProofKind? Kind { get; set; }

    /// <summary>The day of the communication (<c>yyyy-MM-dd</c>); for ISTAT and tourist tax any day of the month, filed under its first day.</summary>
    [Required(ErrorMessage = "ComplianceProofDayRequired")]
    public DateOnly? Day { get; set; }

    /// <summary>The file.</summary>
    public IFormFile? File { get; set; }
}
