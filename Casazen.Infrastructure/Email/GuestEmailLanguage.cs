using System.Globalization;
using Casazen.Core.Features;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// The language of an e-mail to a guest (E50/F01/US03/T02, NF-155, AC-NF155-3): one code path for every guest e-mail that exists in the five languages of the guest
/// (<see cref="GuestLanguages.Supported"/>: Italian, English, German, French, Spanish). Used by the services that send them, which take it as an optional dependency: without it, or
/// with <c>Features:GuestPortal</c> off, each keeps the culture it has always used.
/// </summary>
public interface IGuestEmailLanguageProvider
{
    /// <summary>
    /// The culture to write the e-mail to the guest of a booking in. While <c>Features:GuestPortal</c> is off it is <paramref name="todays"/>, the culture the caller has always used (the same
    /// instance, nothing read). With the portal on it is the language the guest chose on the check-in page (<paramref name="chosenLanguage"/> when the caller already has it, otherwise the
    /// latest session of the booking that holds one), else the one of the country on the booking, else the one of the host's account (<c>Org.DefaultLanguage</c>), else Italian
    /// (<see cref="GuestLanguages.ResolveForEmail"/>); a code that is not one of the five is skipped. Never throws except for a cancellation: a failure is logged and gives
    /// <paramref name="todays"/>.
    /// </summary>
    Task<CultureInfo> ForBookingAsync(
        Guid orgId,
        Guid bookingId,
        CultureInfo todays,
        string? chosenLanguage = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGuestEmailLanguageProvider"/>
/// <remarks>
/// <para>Read by the org and the booking together (the tenant filter is off, as for every read of a job or of a service that is called by id, and the org is named in the predicate): a
/// booking that is not the org's gives the host's account language, never another org's data. The logs carry the ids of the org and of the booking, never a language of a guest.</para>
/// <para><b>The gap</b>: <c>Booking</c> has no language column (this slice has no migration) and no public booking request carries one, so a confirmation written before any check-in knows the
/// language of the guest only by its country; without a country it is the language of the host's account.</para>
/// </remarks>
public sealed class GuestEmailLanguageProvider(
    AppDbContext db,
    IFeatureFlags featureFlags,
    ILogger<GuestEmailLanguageProvider> logger) : IGuestEmailLanguageProvider
{
    public async Task<CultureInfo> ForBookingAsync(
        Guid orgId,
        Guid bookingId,
        CultureInfo todays,
        string? chosenLanguage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(todays);
        if (!featureFlags.IsEnabled(FeatureFlags.GuestPortal))
            return todays;

        try
        {
            var facts = await db.Bookings
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(b => b.Id == bookingId && b.OrgId == orgId)
                .Select(b => new { b.Guest.Country, b.Org.DefaultLanguage })
                .FirstOrDefaultAsync(cancellationToken);

            var sessionLanguage = GuestLanguages.Normalize(chosenLanguage)
                                  ?? await db.GuestCheckInSessions
                                      .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                                      .AsNoTracking()
                                      .Where(s => s.OrgId == orgId && s.BookingId == bookingId && s.Language != null)
                                      .OrderByDescending(s => s.UpdatedAt)
                                      .ThenByDescending(s => s.Id)
                                      .Select(s => s.Language)
                                      .FirstOrDefaultAsync(cancellationToken);

            if (facts is null)
            {
                // Not a booking of the org: nothing of a guest to read; the language of the guest page is all there is.
                return GuestLanguages.CultureOf(GuestLanguages.Resolve(sessionLanguage, null));
            }

            return GuestLanguages.CultureOf(GuestLanguages.ResolveForEmail(sessionLanguage, facts.Country, facts.DefaultLanguage));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The language of the e-mail of booking {BookingId} of org {OrgId} could not be read: the usual one is used", bookingId, orgId);
            return todays;
        }
    }
}

/// <summary>The one way the services read the language when it is an optional dependency of theirs.</summary>
public static class GuestEmailLanguageProviderExtensions
{
    /// <summary>
    /// The culture of the e-mail to the guest of the booking: <paramref name="todays"/> when the service was built without a provider (an older test, a tool); otherwise what
    /// <see cref="IGuestEmailLanguageProvider.ForBookingAsync"/> says.
    /// </summary>
    public static async Task<CultureInfo> CultureOrAsync(
        this IGuestEmailLanguageProvider? provider,
        Guid orgId,
        Guid bookingId,
        CultureInfo todays,
        string? chosenLanguage = null,
        CancellationToken cancellationToken = default)
    {
        if (provider is null)
            return todays;

        return await provider.ForBookingAsync(orgId, bookingId, todays, chosenLanguage, cancellationToken);
    }
}
