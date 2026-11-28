using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.LongRent;

/// <summary>
/// The landlord's reminder of one installment (<c>POST api/long-rent/rents/{id}/reminder</c>, LR-01): the body, and the note,
/// are optional. The note is shown to the tenant as written (HTML-encoded in the email), at most
/// <see cref="RentCharges.MaxReminderNoteLength"/> characters.
/// </summary>
public class RentReminderRequest
{
    [MaxLength(RentCharges.MaxReminderNoteLength, ErrorMessage = "RentReminderNoteTooLong")]
    public string? Note { get; set; }
}

public sealed class RentBulkReminderRequest : RentReminderRequest
{
    [Required(ErrorMessage = "RentReminderBatchInvalid")]
    public List<Guid>? InstallmentIds { get; set; }
}

public sealed record RentReminderSkipped(Guid InstallmentId, string Code);

/// <summary>
/// The outcome of a bulk reminder: how many installments were asked for (without repeats), the reminders sent and the installments
/// skipped with their code. A skipped installment never fails the others; the answer is 200 even if all are skipped.
/// </summary>
public sealed record RentBulkReminderResult(
    int Requested,
    IReadOnlyList<RentReminderResult> Sent,
    IReadOnlyList<RentReminderSkipped> Skipped);
