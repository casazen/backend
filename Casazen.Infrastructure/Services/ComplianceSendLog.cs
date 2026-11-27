using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceSendLog" />
/// <remarks>
/// It writes through the context of the caller and saves it, so the line and the change that made the send true (the report declared
/// sent) travel in one <c>SaveChanges</c>. The line is added with <c>DbSet.Add</c>: its id is set before it is added, and an entity with a
/// preset key that is only attached to a collection would be tracked as modified. Logs carry ids and codes, never a name or a document.
/// </remarks>
public sealed class ComplianceSendLog(AppDbContext db, ILogger<ComplianceSendLog> logger) : IComplianceSendLog
{
    public async Task RecordAsync(ComplianceSendLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.OrgId == Guid.Empty)
            throw new ArgumentException("A line of the send log belongs to an org.", nameof(entry));
        if (entry.PropertyId == Guid.Empty)
            throw new ArgumentException("A line of the send log belongs to a property.", nameof(entry));
        if (!Enum.IsDefined(entry.Kind))
            throw new ArgumentException("Unknown kind of send.", nameof(entry));
        if (!Enum.IsDefined(entry.Sender))
            throw new ArgumentException("Unknown sender.", nameof(entry));
        if (entry.Kind == ComplianceSendKind.AlloggiatiGuests && entry.BookingId is null)
            throw new ArgumentException("The guests of a stay are logged with the stay.", nameof(entry));

        // A receipt or a confirmation id: trimmed, and never longer than its column (a longer one is a mistake of the caller, not a reason to lose the line).
        var reference = string.IsNullOrWhiteSpace(entry.Reference) ? null : entry.Reference.Trim();
        if (reference?.Length > ComplianceSendLogEntry.ReferenceMaxLength)
            throw new ArgumentException("The reference of a send is a receipt or confirmation id.", nameof(entry));
        entry.Reference = reference;

        db.ComplianceSendLogEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Send logged. EntryId={EntryId} OrgId={OrgId} PropertyId={PropertyId} BookingId={BookingId} Kind={Kind} Sender={Sender}",
            entry.Id, entry.OrgId, entry.PropertyId, entry.BookingId, entry.Kind, entry.Sender);
    }
}
