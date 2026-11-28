using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestSessionService"/>
/// <remarks>
/// <para>The proof is the one of "Le mie prenotazioni" (<see cref="GuestBookingLookupService"/>): org of the site, booking code and
/// email in one query, so a code that does not exist, a code of another org and a wrong email take the same path and get the same
/// 404. With the last four digits of the phone (NF-143) the proof is <see cref="GuestBookingLookupService.LoadByPhoneAsync"/>, with the same 404 and
/// the same session. Nothing the guest typed is logged.</para>
/// <para><b>Length of a session</b> (decision DB-D21): <c>GuestArea:SessionMinutes</c>, 120 (two hours) by default, never more than
/// 1440 (24 hours) and never less than 5. There is no refresh: when it ends the guest proves the booking again.</para>
/// </remarks>
public sealed class GuestSessionService(
    GuestBookingLookupService lookup,
    GuestSessionTokens tokens,
    IConfiguration configuration,
    TimeProvider? timeProvider = null) : IGuestSessionService
{
    public const string SessionMinutesKey = "GuestArea:SessionMinutes";

    public const int DefaultSessionMinutes = 120;
    public const int MinSessionMinutes = 5;

    public const int MaxSessionMinutes = 24 * 60;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<GuestSessionOpened> OpenAsync(
        GuestBookingCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var booking = await lookup.LoadAsync(credentials, cancellationToken);
        return Open(booking);
    }

    public async Task<GuestSessionOpened> OpenAsync(
        GuestBookingPhoneCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var booking = await lookup.LoadByPhoneAsync(credentials, cancellationToken);
        return Open(booking);
    }

    private GuestSessionOpened Open(Booking booking)
    {
        var expiresAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(SessionMinutes());
        var token = tokens.Issue(booking.OrgId, GuestSessionTicket.HashEmail(booking.Guest.Email), expiresAt);
        return new GuestSessionOpened(token, expiresAt, BookingCodes.Format(booking.BookingCode));
    }

    public GuestSessionTicket? Validate(string? accessToken) => tokens.Read(accessToken);

    public async Task<GuestBookingView> GetBookingAsync(
        GuestSessionTicket ticket,
        string bookingCode,
        CancellationToken cancellationToken = default)
    {
        var booking = await lookup.LoadAsync(ticket, bookingCode, cancellationToken);
        return await lookup.ViewOfAsync(booking, cancellationToken);
    }

    public Task<Booking> FindBookingAsync(
        GuestSessionTicket ticket,
        string bookingCode,
        CancellationToken cancellationToken = default) =>
        lookup.LoadAsync(ticket, bookingCode, cancellationToken);

    public static int SessionMinutesOf(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue(SessionMinutesKey, DefaultSessionMinutes), MinSessionMinutes, MaxSessionMinutes);

    private int SessionMinutes() => SessionMinutesOf(configuration);
}
