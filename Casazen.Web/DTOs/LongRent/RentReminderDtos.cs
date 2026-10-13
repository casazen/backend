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

/// <summary>
/// The reminder of several installments at once (<c>POST api/long-rent/rents/reminders</c>): from one to
/// <see cref="RentCharges.MaxBulkReminders"/> installment ids, the same optional note for all of them.
/// </summary>
public sealed class RentBulkReminderRequest : RentReminderRequest
{
    [Required(ErrorMessage = "RentReminderBatchInvalid")]
    public List<Guid>? InstallmentIds { get; set; }
}

/// <summary>An installment of a bulk reminder that was not reminded, and why (the stable code of the error it would have answered).</summary>
public sealed record RentReminderSkipped(Guid InstallmentId, string Code);

/// <summary>
/// The outcome of a bulk reminder: how many installments were asked for (without repeats), the reminders sent and the installments
/// skipped with their code. A skipped installment never fails the others; the answer is 200 even if all are skipped.
/// </summary>
public sealed record RentBulkReminderResult(
    int Requested,
    IReadOnlyList<RentReminderResult> Sent,
    IReadOnlyList<RentReminderSkipped> Skipped);
