using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// E50/F01/US03/T02 (NF-155, AC-NF155-3): one code path chooses the language of an e-mail to a guest: the language the guest chose on the check-in page, else the one of the country on
/// the booking, else the one of the host's account, Italian last; an unsupported code gives Italian; and with the guest portal off every e-mail keeps the culture it has always had.
/// </summary>
public sealed class GuestEmailLanguageTests : IDisposable
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();

    public void Dispose() => _db.Dispose();

    /// <summary>The rule itself, with no data: the first supported one of the session, the country, the account of the host, then Italian.</summary>
    [Theory]
    [InlineData("fr", "DE", "es", "fr")]
    [InlineData(" DE ", null, "it", "de")]
    [InlineData(null, "DE", "es", "de")]
    [InlineData(null, "Francia", "en", "fr")]
    [InlineData(null, "NL", "it", "en")]
    [InlineData(null, null, "es", "es")]
    [InlineData(null, "", "en", "en")]
    [InlineData(null, null, "it", "it")]
    [InlineData(null, null, null, "it")]
    [InlineData("xx", null, "xx", "it")]
    [InlineData("xx", "IT", "en", "it")]
    [InlineData("pt", null, "de-CH", "de")]
    public void ResolveForEmail_SessionThenCountryThenOrgDefaultThenItalian(string? session, string? country, string? orgDefault, string expected)
    {
        Assert.Equal(expected, GuestLanguages.ResolveForEmail(session, country, orgDefault));
    }

    /// <summary>
    /// PROOF TEST (what language a guest is written to in, and that the portal switch changes nothing while it is off): with the portal on the language comes from the check-in page,
    /// then the country on the booking, then the host's account, then Italian; an unsupported code is skipped; with the portal off the culture the caller has always used comes
    /// back untouched, whatever the booking says.
    /// </summary>
    [Fact]
    public async Task Language_FromSessionThenOrgDefaultThenItalian_FlagOff_Unchanged()
    {
        var flags = new TestFeatureFlags(FeatureFlags.GuestPortal);
        var provider = Provider(flags);

        // The guest chose German on the check-in page: it wins over the country and the host's account.
        var withSession = await SeedAsync(orgDefault: "en", country: "FR", sessionLanguage: "de");
        Assert.Equal("de", (await provider.ForBookingAsync(withSession.OrgId, withSession.BookingId, Italian)).Name);

        // The caller already knows the language of the session it is writing for (the check-in link of that session): it wins over the one stored.
        Assert.Equal("es", (await provider.ForBookingAsync(withSession.OrgId, withSession.BookingId, Italian, chosenLanguage: "es")).Name);

        // No session: the country on the booking, as the review invitation and the chat message of the check-in link read it.
        var withCountry = await SeedAsync(orgDefault: "en", country: "FR", sessionLanguage: null);
        Assert.Equal("fr", (await provider.ForBookingAsync(withCountry.OrgId, withCountry.BookingId, Italian)).Name);

        // No session and no country: the language of the host's account.
        var orgDefault = await SeedAsync(orgDefault: "en", country: "", sessionLanguage: null);
        Assert.Equal("en", (await provider.ForBookingAsync(orgDefault.OrgId, orgDefault.BookingId, Italian)).Name);

        // The host's account is Italian (the default of every account): Italian, with the culture of Italy.
        var italian = await SeedAsync(orgDefault: "it", country: "", sessionLanguage: null);
        Assert.Equal("it-IT", (await provider.ForBookingAsync(italian.OrgId, italian.BookingId, English)).Name);

        // A language the guest page does not offer is skipped at every step, and the last word is Italian.
        var unsupported = await SeedAsync(orgDefault: "xx", country: "", sessionLanguage: "xx");
        Assert.Equal("it-IT", (await provider.ForBookingAsync(unsupported.OrgId, unsupported.BookingId, English)).Name);

        // Portal off: the culture of today, the very same instance, whatever the booking holds.
        var off = Provider(new TestFeatureFlags());
        Assert.Same(English, await off.ForBookingAsync(withSession.OrgId, withSession.BookingId, English));
        Assert.Same(Italian, await off.ForBookingAsync(withCountry.OrgId, withCountry.BookingId, Italian));

        // No provider at all (a service built without it): the culture of today.
        IGuestEmailLanguageProvider? none = null;
        Assert.Same(English, await none.CultureOrAsync(withSession.OrgId, withSession.BookingId, English));
    }

    /// <summary>The booking is looked up in the org it was asked for: the data of another org's booking never decide the language.</summary>
    [Fact]
    public async Task Language_OfABookingOfAnotherOrg_IsNotRead()
    {
        var provider = Provider(new TestFeatureFlags(FeatureFlags.GuestPortal));
        var other = await SeedAsync(orgDefault: "en", country: "DE", sessionLanguage: "fr");
        var mine = await SeedAsync(orgDefault: "es", country: "", sessionLanguage: null);

        var culture = await provider.ForBookingAsync(mine.OrgId, other.BookingId, English);

        Assert.Equal("it-IT", culture.Name);
    }

    private GuestEmailLanguageProvider Provider(TestFeatureFlags flags) =>
        new(_db, flags, NullLogger<GuestEmailLanguageProvider>.Instance);

    private async Task<(Guid OrgId, Guid BookingId)> SeedAsync(string orgDefault, string country, string? sessionLanguage)
    {
        var org = HostScopeScenario.NewOrg("Casa Aurora", $"aurora-{Guid.NewGuid():N}"[..20]);
        org.DefaultLanguage = orgDefault;
        var property = HostScopeScenario.NewProperty(org.Id, "auth0|titolare-aurora", "Trullo Aurora");
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com", Country = country };
        var booking = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = HostScopeScenario.Day(2026, 10, 10),
            CheckOutDate = HostScopeScenario.Day(2026, 10, 13),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        _db.AddRange(org, property, guest, booking);
        if (sessionLanguage is not null)
        {
            _db.GuestCheckInSessions.Add(new GuestCheckInSession
            {
                OrgId = org.Id,
                BookingId = booking.Id,
                TokenHash = new string('a', 64),
                ExpiresAt = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
                Language = sessionLanguage,
            });
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return (org.Id, booking.Id);
    }
}
