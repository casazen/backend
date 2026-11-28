using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class BookingChangeOptionsDto
{
    public bool CanEdit { get; init; }

    public bool CanCancel { get; init; }

    public string? ReasonCode { get; init; }

    public string? GuidanceKey { get; init; }

    /// <summary>The channel of a booking that came from one (<c>BookingCom</c>); <c>null</c> otherwise.</summary>
    public string? Channel { get; init; }

    public string? CancelReasonCode { get; init; }

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

public sealed class BookingCollectionDto
{
    public bool? CompanyCollect { get; init; }

    public bool? CommissionIncludedInPayments { get; init; }

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

public sealed class BookingAmountSourcesDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Total { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Paid { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Balance { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Commission { get; init; }

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

public sealed class BookingOctorateSyncDto
{
    public string State { get; init; } = string.Empty;

    public string LastOperation { get; init; } = string.Empty;

    public DateTime? LastSyncedAt { get; init; }

    /// <summary>Why the last operation failed: a stable code of CasaZen (<c>octorate_dates_unavailable</c>, ...), never a text of Octorate; <c>null</c> otherwise.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The host can ask for the push again (<c>POST /api/bookings/{id}/octorate-sync/retry</c>): the state is <c>Failed</c> or <c>Conflict</c> and the caller may write the booking.</summary>
    public bool CanRetry { get; init; }

    public OctorateVersionDto? OctorateVersion { get; init; }

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
