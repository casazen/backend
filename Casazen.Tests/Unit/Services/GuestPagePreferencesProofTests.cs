using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F04/US01/T04 (NF-90, CRM-03, AC-NF90-2): the proofs of the marketing choices on the guest page. A guest reads and changes only the choices
/// of their own booking, with the same 404 as everywhere in the guest area; an opt-out on the page is final. On EF InMemory, the real session
/// lookup, the real contact service over the real keyed index, the real confirmation service and the real suppression list.
/// </summary>
public class GuestPagePreferencesProofTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// Permission rule: the guest of one booking can neither read nor change the choices of another guest's booking of the same site, nor of a
    /// booking that does not exist. It is the same 404 for the read, the change and the resend, and nothing is written for either: no contact, no
    /// history, no e-mail.
    /// </summary>
    [Fact]
    public async Task GuestPagePreferences_BookingOfAnotherGuest_Is404_Test()
    {
        using var world = new World();
        var (org, house) = await world.AddSiteAsync();
        var mine = await world.AddBookingAsync(org, house, "anna@example.com");
        var theirs = await world.AddBookingAsync(org, house, "bruno@example.com");
        var ticket = world.TicketOf(org, "anna@example.com");

        // Her own booking answers: nothing ticked, her address masked.
        var own = await world.Service.GetAsync(ticket, BookingCodes.Format(mine.BookingCode), Ct);
        Assert.Equal(GuestMarketingStates.None, own.Email.State);
        Assert.Equal("a***@e***.com", own.Email.MaskedAddress);
        Assert.Equal(GuestMarketingStates.None, own.WhatsApp.State);

        // His booking and a code that does not exist: one answer, for the read, the change and the resend.
        foreach (var code in new[] { theirs.BookingCode, BookingCodes.New() })
        {
            var formatted = BookingCodes.Format(code);
            var read = await Assert.ThrowsAsync<NotFoundException>(() => world.Service.GetAsync(ticket, formatted, Ct));
            Assert.Equal(GuestBookingLookupErrorCodes.NotFound, read.Code);
            await Assert.ThrowsAsync<NotFoundException>(() => world.Service.SetAsync(ticket, formatted, World.Enable(), "203.0.113.9", Ct));
            await Assert.ThrowsAsync<NotFoundException>(() => world.Service.ResendConfirmationAsync(ticket, formatted, Ct));
        }

        Assert.Empty(await world.Db.GuestContacts.AsNoTracking().ToListAsync(Ct));
        Assert.Empty(await world.Db.GuestConsentRecords.AsNoTracking().ToListAsync(Ct));
        Assert.Empty(world.Queue.Snapshot());
    }

    /// <summary>
    /// Privacy rule: an opt-out on the guest page is final. Turning e-mail on makes a pending consent and one confirmation e-mail; turning it off
    /// suppresses the contact, puts the address on the suppression list and writes the withdrawal in the privacy history; turning it on again
    /// stores the new consent and lifts nothing: the state stays «withdrawn», no second e-mail goes out and the address is never marketable.
    /// The answer holds the address masked, never in clear.
    /// </summary>
    [Fact]
    public async Task GuestPagePreferences_Withdraw_SuppressesAndStaysSuppressed_Test()
    {
        using var world = new World();
        var (org, house) = await world.AddSiteAsync();
        var booking = await world.AddBookingAsync(org, house, "anna@example.com");
        var code = BookingCodes.Format(booking.BookingCode);
        var ticket = world.TicketOf(org, "anna@example.com");

        var on = await world.Service.SetAsync(ticket, code, World.Enable(), "203.0.113.9", Ct);

        Assert.Equal(GuestMarketingStates.Pending, on.Email.State);
        Assert.DoesNotContain("anna", JsonSerializer.Serialize(on), StringComparison.OrdinalIgnoreCase);
        Assert.Single(world.Queue.Snapshot());
        var consent = Assert.Single(await world.Db.GuestContacts.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(GuestContactOrigin.GuestPage, consent.Origin);
        Assert.Equal(GuestContactLegalBasis.Consent, consent.LegalBasis);
        Assert.Equal(GuestConsentSource.GuestPage, consent.ConsentSource);
        Assert.Equal(World.Version, consent.BasisVersion);

        var off = await world.Service.SetAsync(ticket, code, World.Disable(), "203.0.113.9", Ct);

        Assert.Equal(GuestMarketingStates.Withdrawn, off.Email.State);
        var withdrawn = Assert.Single(await world.Db.GuestContacts.AsNoTracking().ToListAsync(Ct));
        Assert.True(withdrawn.IsSuppressed);
        Assert.Single(await world.Db.SuppressionListEntries.AsNoTracking().ToListAsync(Ct));
        Assert.Contains(
            await world.Db.GuestConsentRecords.AsNoTracking().ToListAsync(Ct),
            record => record.Action == GuestConsentAction.Withdrawn && record.Source == GuestConsentSource.GuestPage);

        // On again: the consent is stored, nothing is lifted, nothing is sent.
        var again = await world.Service.SetAsync(ticket, code, World.Enable(), "203.0.113.9", Ct);

        Assert.Equal(GuestMarketingStates.Withdrawn, again.Email.State);
        Assert.False(again.Email.CanResend);
        var after = Assert.Single(await world.Db.GuestContacts.AsNoTracking().ToListAsync(Ct));
        Assert.True(after.IsSuppressed);
        Assert.False(CrmContactRules.CanReceiveMarketing(after));
        Assert.Single(world.Queue.Snapshot());
        Assert.Equal(MarketingConfirmationSendOutcome.NotRequired, await world.Service.ResendConfirmationAsync(ticket, code, Ct));
        Assert.Single(await world.Db.SuppressionListEntries.AsNoTracking().ToListAsync(Ct));
    }

    /// <summary>
    /// Privacy rule: a session proves the booking, not the ownership of another address, so a booking with a real address consents with that
    /// address only. An address typed next to it is ignored: the contact is the one of the booking, the confirmation e-mail goes to it and to
    /// nobody else, and the answer shows it masked.
    /// </summary>
    [Fact]
    public async Task GuestPagePreferences_ABookingWithARealAddress_ConsentsWithThatAddressOnly_Test()
    {
        using var world = new World();
        var (org, house) = await world.AddSiteAsync();
        var booking = await world.AddBookingAsync(org, house, "anna@example.com");
        var ticket = world.TicketOf(org, "anna@example.com");

        var on = await world.Service.SetAsync(ticket, BookingCodes.Format(booking.BookingCode), World.Enable("intruso@example.com"), "203.0.113.9", Ct);

        Assert.Equal(GuestMarketingStates.Pending, on.Email.State);
        Assert.Equal("a***@e***.com", on.Email.MaskedAddress);
        var contact = Assert.Single(await world.Db.GuestContacts.AsNoTracking().ToListAsync(Ct));
        Assert.Equal("anna@example.com", contact.Value);
        Assert.Equal("anna@example.com", Assert.Single(world.Queue.Snapshot()).To);
    }

    /// <summary>
    /// Privacy rule: when the booking has no real address (a relay of an online travel agency) the guest types one. Without it, or with another
    /// relay address, the change is refused (422) and nothing is stored; with a plain address the consent and its confirmation e-mail are for it.
    /// </summary>
    [Fact]
    public async Task GuestPagePreferences_ABookingWithARelayAddress_NeedsAPlainAddressTyped_Test()
    {
        using var world = new World(new CrmOptions { OtaForwardDomains = ["relay.example.org"] });
        var (org, house) = await world.AddSiteAsync();
        var booking = await world.AddBookingAsync(org, house, "res-77@relay.example.org");
        var ticket = world.TicketOf(org, "res-77@relay.example.org");
        var code = BookingCodes.Format(booking.BookingCode);

        foreach (var typed in new string?[] { null, "altro-77@relay.example.org", "non-un-indirizzo" })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => world.Service.SetAsync(ticket, code, World.Enable(typed), "203.0.113.9", Ct));
            Assert.Equal(GuestMarketingErrorCodes.EmailInvalid, refused.Code);
        }

        Assert.Empty(await world.Db.GuestContacts.AsNoTracking().ToListAsync(Ct));
        Assert.Empty(world.Queue.Snapshot());

        var on = await world.Service.SetAsync(ticket, code, World.Enable("vera@example.com"), "203.0.113.9", Ct);

        Assert.Equal(GuestMarketingStates.Pending, on.Email.State);
        Assert.Equal("vera@example.com", Assert.Single(await world.Db.GuestContacts.AsNoTracking().ToListAsync(Ct)).Value);
        Assert.Equal("vera@example.com", Assert.Single(world.Queue.Snapshot()).To);
    }

    /// <summary>The world of the proofs: a store, a clock, the real session lookup, the contact and confirmation services, the service under test.</summary>
    private sealed class World : IDisposable
    {
        public const string Version = "consent-v1";

        public World(CrmOptions? crmOptions = null)
        {
            Db = CrmTestWorld.Open(CrmTestWorld.NewStore());
            Clock = CrmTestWorld.Clock();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["SIGNED_LINK_SECRET"] = "a-long-random-secret-of-the-signed-links" })
                .Build();
            var lookup = new GuestBookingLookupService(
                Db,
                Mock.Of<IGuestCheckInService>(),
                Mock.Of<IGuestCheckInLinkEmailQueue>(),
                EmailTestHelpers.Links(),
                configuration,
                NullLogger<GuestBookingLookupService>.Instance,
                Clock);
            var sessions = new GuestSessionService(lookup, new GuestSessionTokens(new EphemeralDataProtectionProvider(), Clock), configuration, Clock);

            var crmValues = crmOptions ?? new CrmOptions();
            var crm = Options.Create(crmValues);
            var contacts = CrmTestWorld.Contacts(Db, Clock, crmValues);
            Queue = new RecordingEmailQueue();
            var confirmation = new MarketingConfirmationService(
                Db,
                new MarketingConfirmationLinks(configuration, crm, Clock),
                EmailTestHelpers.Links(),
                Queue,
                contacts,
                crm,
                Clock,
                NullLogger<MarketingConfirmationService>.Instance);
            Service = new GuestMarketingPreferencesService(
                sessions,
                Db,
                contacts,
                CrmTestIndex.Create(),
                confirmation,
                Options.Create(new GdprOptions { MarketingConsentVersion = Version }),
                crm,
                Clock,
                NullLogger<GuestMarketingPreferencesService>.Instance);
        }

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public RecordingEmailQueue Queue { get; }

        public GuestMarketingPreferencesService Service { get; }

        /// <summary>The guest turns e-mail on, with the text in force; <paramref name="address"/> is the address the guest typed, if any.</summary>
        public static GuestMarketingChange Enable(string? address = null) =>
            new(new GuestMarketingEmailChange(true, address), null, Version, "it");

        /// <summary>The guest turns e-mail off.</summary>
        public static GuestMarketingChange Disable() =>
            new(new GuestMarketingEmailChange(false, null), null, null, "it");

        /// <summary>What the session of the guest with this address proves, on the site of the org.</summary>
        public GuestSessionTicket TicketOf(OrgEntity org, string email) =>
            new(org.Id, GuestSessionTicket.HashEmail(email), Clock.GetUtcNow().UtcDateTime.AddHours(1));

        public async Task<(OrgEntity Org, Property House)> AddSiteAsync()
        {
            var org = new OrgEntity
            {
                Name = "Site Org",
                Slug = $"site-{Guid.NewGuid():N}",
                DisplayName = "Villa Prova",
                ContactEmail = "host@example.com",
                PlanTier = PlanTier.Starter,
                IsActive = true,
            };
            var house = new Property
            {
                OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
                OrgId = org.Id,
                Name = "Casa Prova",
                Slug = $"casa-{Guid.NewGuid():N}",
                Description = "Guest page test",
                Address = "Via Prova 1",
                City = "Como",
                PostalCode = "22100",
                MaxGuests = 4,
                NightlyRate = 100m,
                CleaningFee = 50m,
                IsActive = true,
            };
            Db.Orgs.Add(org);
            Db.Properties.Add(house);
            await Db.SaveChangesAsync(Ct);
            return (org, house);
        }

        /// <summary>A direct booking of the site, confirmed, whose guest has this address.</summary>
        public async Task<Booking> AddBookingAsync(OrgEntity org, Property house, string email)
        {
            var guest = new Guest
            {
                OrgId = org.Id,
                FirstName = "Anna",
                LastName = "Guest",
                Email = email,
                PhoneNumber = "+393331112222",
            };
            var checkIn = CrmTestWorld.Day(10);
            var booking = new Booking
            {
                PropertyId = house.Id,
                OrgId = org.Id,
                GuestId = guest.Id,
                CheckInDate = checkIn,
                CheckOutDate = checkIn.AddDays(3),
                NumberOfGuests = 2,
                NumberOfAdults = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                PaymentOption = PaymentOption.Immediate,
                BasePrice = 350m,
                CleaningFee = 50m,
                TotalPrice = 350m,
                CreatedAt = CrmTestWorld.Day(-1),
                UpdatedAt = CrmTestWorld.Day(-1),
            };
            Db.AddRange(guest, booking);
            await Db.SaveChangesAsync(Ct);
            return booking;
        }

        public void Dispose() => Db.Dispose();
    }
}
