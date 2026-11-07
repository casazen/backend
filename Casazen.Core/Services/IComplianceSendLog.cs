using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The log of what was sent to the authorities, when and by whom (E03/F02/US03/T01, NF-47, CHK-08, AC-NF47-2). Every <b>successful</b>
/// send writes one line, from the place that knows it succeeded: the channel manager sending the guests of a stay
/// (<c>CheckInOctorateSyncService</c>, sender <see cref="ComplianceSender.ChannelManager"/>) and the host declaring that it sent them on the
/// portal (<c>AlloggiatiWebService.MarkSentManuallyAsync</c>, sender <see cref="ComplianceSender.Manual"/>). A failed send, a send that
/// sent nothing and a draft write no line. The transmission of CasaZen itself (sender <see cref="ComplianceSender.CasaZen"/>) is postponed
/// with the direct Alloggiati client (plan of 2026-11-05): the value and this log are ready for it, and nothing writes it today.
/// </summary>
/// <remarks>
/// A line holds no guest data: the property, the stay, the kind, the sender, the instant, the person and a receipt or confirmation id.
/// It is append only; nothing here updates or deletes one.
/// </remarks>
public interface IComplianceSendLog
{
    /// <summary>
    /// Writes <paramref name="entry"/> and saves the changes pending in the same unit of work, so a caller that has just changed the state of
    /// the send (the report declared sent) commits both together or neither. The org and the property of the line are the ones the caller holds
    /// from the stay it just sent: they are required, never looked up again.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The org or the property is missing, the kind or the sender is not one of the log's, the guests of a stay are logged without the stay, or
    /// the reference is longer than <see cref="ComplianceSendLogEntry.ReferenceMaxLength"/>.
    /// </exception>
    Task RecordAsync(ComplianceSendLogEntry entry, CancellationToken ct = default);
}
