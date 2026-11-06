using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// What the host may do with a booking and, when not, why and how to ask for it (E08/F03/US03/T02, NF-75, NF-76): <see cref="BookingChangeOptions"/> as the
/// API says it, so no screen guesses it. Every booking answer carries it; the codes are those of <see cref="BookingErrorCodes"/>.
/// </summary>
public sealed class BookingChangeOptionsDto
{
    /// <summary>The host may change the dates and the guests (what <c>PUT /api/bookings/{id}</c> accepts).</summary>
    public bool CanEdit { get; init; }

    /// <summary>The host may cancel the booking from CasaZen.</summary>
    public bool CanCancel { get; init; }

    /// <summary>Why not, for the first thing that is not allowed (the change, else the cancellation); <c>null</c> when both are allowed.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>The key of the one sentence that says how to ask the guest or the channel for that; <c>null</c> when the booking is not one of a channel or there is nothing to ask.</summary>
    public string? GuidanceKey { get; init; }

    /// <summary>The channel of a booking that came from one (<c>BookingCom</c>); <c>null</c> otherwise.</summary>
    public string? Channel { get; init; }

    /// <summary>Why the cancellation is not allowed, whatever the change; <c>null</c> when it is.</summary>
    public string? CancelReasonCode { get; init; }

    /// <summary>The key of the sentence about the cancellation on the channel, when the channel is why it is not allowed.</summary>
    public string? CancelGuidanceKey { get; init; }

    public static BookingChangeOptionsDto From(BookingChangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new BookingChangeOptionsDto
        {
            CanEdit = options.CanEdit,
            CanCancel = options.CanCancel,
            ReasonCode = options.ReasonCode,
            GuidanceKey = options.GuidanceKey,
            Channel = options.Channel,
            CancelReasonCode = options.CancelReasonCode,
            CancelGuidanceKey = options.CancelGuidanceKey,
        };
    }
}

/// <summary>
/// Who collects the payment of a booking that came from a channel, and whether the commission is already taken out of it (E08/F03/US03/T02, NF-77); each
/// <c>null</c> when it is not known (never read as «no»). Money data: the same reach as <see cref="BookingDetailDto.OtaAmounts"/>.
/// </summary>
public sealed class BookingCollectionDto
{
    /// <summary><c>true</c> when the channel (the company) collects the payment, <c>false</c> when the host does.</summary>
    public bool? CompanyCollect { get; init; }

    /// <summary><c>true</c> when the portal pays the host net of its commission, so the commission is already taken out of the payments.</summary>
    public bool? CommissionIncludedInPayments { get; init; }

    /// <summary>The flags of <paramref name="booking"/>; <c>null</c> for a booking that does not come from a channel, which has none.</summary>
    public static BookingCollectionDto? From(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);

        return OtaChannelActionPolicy.IsChannelBooking(booking)
            ? new BookingCollectionDto
            {
                CompanyCollect = booking.OtaCompanyCollect,
                CommissionIncludedInPayments = booking.OtaCommissionIncludedInPayments,
            }
            : null;
    }
}

/// <summary>
/// Where each figure of a booking comes from (E08/F03/US03/T02, NF-77): <c>Stripe</c> (the booking site), <c>ChannelManager</c> (Octorate) or <c>Manual</c> (the
/// host entered it). A figure the booking does not have is left out of the answer, not given a source. Money data: the same reach as <c>paidAmount</c>.
/// </summary>
public sealed class BookingAmountSourcesDto
{
    /// <summary>The price of the stay.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Total { get; init; }

    /// <summary>What came in.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Paid { get; init; }

    /// <summary>What is still to pay (the money of the channel manager, when Octorate gives it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Balance { get; init; }

    /// <summary>What the channel kept (the money of the channel manager, when Octorate gives it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Commission { get; init; }

    /// <summary>What the host receives (the money of the channel manager, when Octorate gives it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Net { get; init; }

    public static BookingAmountSourcesDto From(BookingAmountSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return new BookingAmountSourcesDto
        {
            Total = sources.Total?.ToString(),
            Paid = sources.Paid?.ToString(),
            Balance = sources.Balance?.ToString(),
            Commission = sources.Commission?.ToString(),
            Net = sources.Net?.ToString(),
        };
    }
}

/// <summary>
/// The mirror of a booking on the channel manager (E08/F03/US03/T02, NF-74): where the last operation CasaZen sent for it stands. <c>null</c> in the booking
/// answer when the booking is not mirrored. No personal data: a state, a stable code, instants and, in a conflict, the dates and guests Octorate holds.
/// </summary>
public sealed class BookingOctorateSyncDto
{
    /// <summary><c>Pending</c>, <c>Synced</c>, <c>Failed</c>, <c>Conflict</c> or <c>NotAllowedByChannel</c>.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>The last operation sent: <c>Create</c>, <c>Update</c>, <c>Cancel</c> or <c>Payment</c>.</summary>
    public string LastOperation { get; init; } = string.Empty;

    /// <summary>When Octorate last confirmed an operation (UTC); <c>null</c> until it did.</summary>
    public DateTime? LastSyncedAt { get; init; }

    /// <summary>Why the last operation failed: a stable code of CasaZen (<c>octorate_dates_unavailable</c>, ...), never a text of Octorate; <c>null</c> otherwise.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The host can ask for the push again (<c>POST /api/bookings/{id}/octorate-sync/retry</c>): the state is <c>Failed</c> or <c>Conflict</c> and the caller may write the booking.</summary>
    public bool CanRetry { get; init; }

    /// <summary>The dates and guests Octorate holds, only in the <c>Conflict</c> state and only when they were kept.</summary>
    public OctorateVersionDto? OctorateVersion { get; init; }

    /// <summary>The mirror as the API says it; <paramref name="canRetry"/> is the status's, narrowed by what the caller may do.</summary>
    public static BookingOctorateSyncDto From(BookingSyncStatus status, bool canRetry)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new BookingOctorateSyncDto
        {
            State = status.State.ToString(),
            LastOperation = status.LastOperation.ToString(),
            LastSyncedAt = status.LastSyncedAt,
            ErrorCode = status.ErrorCode,
            CanRetry = canRetry,
            OctorateVersion = status.OctorateVersion is null ? null : OctorateVersionDto.From(status.OctorateVersion),
        };
    }
}

/// <summary>
/// The stay as Octorate holds it after it refused a change (E08/F03/US03/T02): the days as the booking's own dates are written (midnight UTC of the stay
/// day), so a screen puts the two versions side by side. A member that was not kept is <c>null</c>.
/// </summary>
public sealed class OctorateVersionDto
{
    public DateTime? CheckIn { get; init; }

    public DateTime? CheckOut { get; init; }

    public int? Guests { get; init; }

    public static OctorateVersionDto From(OctorateVersionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new OctorateVersionDto
        {
            CheckIn = StayDay(snapshot.CheckIn),
            CheckOut = StayDay(snapshot.CheckOut),
            Guests = snapshot.Guests,
        };
    }

    private static DateTime? StayDay(DateOnly? day) =>
        day is { } value ? DateTime.SpecifyKind(value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc) : null;
}
