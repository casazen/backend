using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-11 (A3-10, R-06) on real PostgreSQL: "Le mie prenotazioni" finds a booking of the site with its booking code and the
/// guest's email and shows state, stay, amounts, check-in and the host's contact without personal data. A wrong email, an
/// unknown code, a malformed code and a code of another site get the same answer; the lookup is limited per IP and per
/// email; a site never shows a booking of another org, even with the same code.
/// </summary>
public class GuestBookingLookupPostgresTests : IClassFixture<GuestBookingLookupPostgresTests.LookupFactory>
{
    private const string LookupPath = "/api/public/bookings/lookup";
    private const string CheckInLinkPath = "/api/public/bookings/lookup/check-in-link";
    private const string SessionPath = "/api/public/guest/session";
    private const string GuestBookingsPath = "/api/public/guest/bookings";

    private static int _nextPeer;

    private readonly LookupFactory _factory;

    public GuestBookingLookupPostgresTests(LookupFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task LookupGuestBooking_CodeAndEmail_ReturnsTheBookingWithoutPersonalData()
    {
        var org = await SeedOrgAsync();
        var seed = await SeedBookingAsync(org, BookingStatus.Confirmed, checkInInDays: 40, paid: true);

        // As the guest types them: code in lower case with a space instead of the dash, email in upper case.
        var response = await PostAsync(LookupPath, new
        {
            orgSlug = org.Slug,
            bookingCode = BookingCodes.Format(seed.Code).ToLowerInvariant().Replace('-', ' '),
            email = $"  {seed.Email.ToUpperInvariant()} ",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(BookingCodes.Format(seed.Code), root.GetProperty("bookingCode").GetString());
        Assert.Equal("Confirmed", root.GetProperty("status").GetString());
        Assert.Equal("Immediate", root.GetProperty("paymentOption").GetString());
        Assert.Equal(org.PropertyName, root.GetProperty("propertyName").GetString());
        Assert.Equal("Roma", root.GetProperty("propertyCity").GetString());
        Assert.Equal(seed.CheckIn.ToString("yyyy-MM-dd"), root.GetProperty("checkInDate").GetString());
        Assert.Equal(seed.CheckIn.AddDays(3).ToString("yyyy-MM-dd"), root.GetProperty("checkOutDate").GetString());
        Assert.Equal(2, root.GetProperty("numberOfAdults").GetInt32());
        Assert.Equal(300m, root.GetProperty("lodging").GetDecimal());
        Assert.Equal(50m, root.GetProperty("cleaningFee").GetDecimal());
        Assert.Equal(12m, root.GetProperty("touristTax").GetDecimal());
        Assert.Equal(362m, root.GetProperty("totalPrice").GetDecimal());
        Assert.Equal(362m, root.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(0m, root.GetProperty("refundedAmount").GetDecimal());
        Assert.Equal("Villa Lookup Srl", root.GetProperty("host").GetProperty("name").GetString());
        Assert.Equal(org.ContactEmail, root.GetProperty("host").GetProperty("email").GetString());
        // Arrival in 40 days: the check-in link is emailed 3 days before (CheckIn:SendWindowDays).
        var checkIn = root.GetProperty("checkIn");
        Assert.Equal("NotYetOpen", checkIn.GetProperty("status").GetString());
        Assert.Equal(seed.CheckIn.AddDays(-3).ToString("yyyy-MM-dd"), checkIn.GetProperty("opensOn").GetString());

        // Nothing personal, and not the internal booking id.
        Assert.DoesNotContain(seed.Email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Giulia", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Bianchi", body, StringComparison.Ordinal);
        Assert.DoesNotContain("+393331112222", body, StringComparison.Ordinal);
        Assert.DoesNotContain(seed.BookingId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task LookupGuestBooking_WrongEmailUnknownOrMalformedCode_SameNotFoundAnswer()
    {
        var org = await SeedOrgAsync();
        var seed = await SeedBookingAsync(org, BookingStatus.Confirmed, checkInInDays: 20);
        var otherOrg = await SeedOrgAsync();

        var answers = new[]
        {
            // Right code, email of someone else.
            await PostAsync(LookupPath, new { orgSlug = org.Slug, bookingCode = seed.Code, email = $"other.{Guid.NewGuid():N}@example.com" }),
            // Right email, code that does not exist.
            await PostAsync(LookupPath, new { orgSlug = org.Slug, bookingCode = UnusedCode(seed.Code), email = seed.Email }),
            // Right email, not a code at all (e.g. the booking id).
            await PostAsync(LookupPath, new { orgSlug = org.Slug, bookingCode = seed.BookingId.ToString("D"), email = seed.Email }),
            // Right code and email, on the site of another org.
            await PostAsync(LookupPath, new { orgSlug = otherOrg.Slug, bookingCode = seed.Code, email = $"x.{Guid.NewGuid():N}@example.com" }),
        };

        var bodies = new List<string>();
        foreach (var answer in answers)
        {
            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
            var problem = JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(GuestBookingLookupErrorCodes.NotFound, problem.GetProperty("code").GetString());
            bodies.Add(JsonSerializer.Serialize(new
            {
                status = problem.GetProperty("status").GetInt32(),
                code = problem.GetProperty("code").GetString(),
                title = problem.GetProperty("title").GetString(),
                detail = problem.GetProperty("detail").GetString(),
                keys = problem.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray(),
            }));
        }

        // Identical answers (the trace id aside): nothing tells which bookings exist.
        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
    }

    [PostgresFact]
    public async Task LookupGuestBooking_SameCodeInAnotherOrg_EachSiteShowsOnlyItsOwnBooking()
    {
        var orgA = await SeedOrgAsync();
        var orgB = await SeedOrgAsync();
        var bookingA = await SeedBookingAsync(orgA, BookingStatus.Confirmed, checkInInDays: 30);
        var bookingB = await SeedBookingAsync(orgB, BookingStatus.Confirmed, checkInInDays: 50, code: bookingA.Code);
        Assert.Equal(bookingA.Code, bookingB.Code);

        var aOnA = await PostAsync(LookupPath, new { orgSlug = orgA.Slug, bookingCode = bookingA.Code, email = bookingA.Email });
        var bOnB = await PostAsync(LookupPath, new { orgSlug = orgB.Slug, bookingCode = bookingB.Code, email = bookingB.Email });
        var bOnA = await PostAsync(LookupPath, new { orgSlug = orgA.Slug, bookingCode = bookingA.Code, email = bookingB.Email });
        var aOnB = await PostAsync(LookupPath, new { orgSlug = orgB.Slug, bookingCode = bookingB.Code, email = bookingA.Email });

        Assert.Equal(orgA.PropertyName, await PropertyNameAsync(aOnA));
        Assert.Equal(orgB.PropertyName, await PropertyNameAsync(bOnB));
        Assert.Equal(HttpStatusCode.NotFound, bOnA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, aOnB.StatusCode);
        var body = await bOnA.Content.ReadAsStringAsync() + await aOnB.Content.ReadAsStringAsync();
        Assert.DoesNotContain(orgA.PropertyName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(orgB.PropertyName, body, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task LookupGuestBooking_PreviousSlugOfTheOrg_StillFindsTheBooking()
    {
        // PL-04 (A1-23): the host changed the public slug; links in emails sent before keep working.
        var org = await SeedOrgAsync();
        var seed = await SeedBookingAsync(org, BookingStatus.Confirmed, checkInInDays: 30);
        var previousSlug = $"old-{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.OrgSlugAliases.Add(new OrgSlugAlias { Slug = previousSlug, OrgId = org.OrgId, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var response = await PostAsync(LookupPath, new { orgSlug = previousSlug, bookingCode = seed.Code, email = seed.Email });

        Assert.Equal(org.PropertyName, await PropertyNameAsync(response));
    }

    [PostgresFact]
    public async Task LookupGuestBooking_TooManyAttemptsForOneEmailFromManyIps_Returns429()
    {
        var org = await SeedOrgAsync();
        var seed = await SeedBookingAsync(org, BookingStatus.Confirmed, checkInInDays: 20);

        // LookupFactory: 3 attempts per email and window, whatever the IP.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var guess = await PostAsync(LookupPath, new { orgSlug = org.Slug, bookingCode = UnusedCode(seed.Code), email = seed.Email });
            Assert.Equal(HttpStatusCode.NotFound, guess.StatusCode);
        }

        // Even the right code is not checked once the email has no attempt left.
        var limited = await PostAsync(LookupPath, new { orgSlug = org.Slug, bookingCode = seed.Code, email = seed.Email.ToUpperInvariant() });
        var problem = await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(limited);
        Assert.DoesNotContain(seed.Email, problem.ToString(), StringComparison.OrdinalIgnoreCase);

        var otherEmail = await PostAsync(LookupPath, new { orgSlug = org.Slug, bookingCode = seed.Code, email = $"other.{Guid.NewGuid():N}@example.com" });
        Assert.Equal(HttpStatusCode.NotFound, otherEmail.StatusCode);
    }

    [PostgresFact]
    public async Task LookupGuestBooking_TooManyAttemptsFromOneIp_Returns429()
    {
        var org = await SeedOrgAsync();
        var seed = await SeedBookingAsync(org, BookingStatus.Confirmed, checkInInDays: 20);
        var peer = NextPeer();

        // LookupFactory: 4 attempts per IP and window, whatever the email.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var guess = await PostAsync(
                LookupPath,
                new { orgSlug = org.Slug, bookingCode = seed.Code, email = $"guess{attempt}.{Guid.NewGuid():N}@example.com" },
                peer);
            Assert.Equal(HttpStatusCode.NotFound, guess.StatusCode);
        }

        await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(
            await PostAsync(LookupPath, new { orgSlug = org.Slug, bookingCode = seed.Code, email = seed.Email }, peer));
        await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(
            await PostAsync(CheckInLinkPath, new { orgSlug = org.Slug, bookingCode = seed.Code, email = seed.Email }, peer));

        var otherIp = await PostAsync(LookupPath, new { orgSlug = org.Slug, bookingCode = seed.Code, email = seed.Email });
        Assert.Equal(HttpStatusCode.OK, otherIp.StatusCode);
    }

    [PostgresFact]
    public async Task LookupGuestBooking_ExpiredHoldAndCancelledBooking_ShowTheirRealState()
    {
        var org = await SeedOrgAsync();
        var expiredHold = await SeedBookingAsync(
            org, BookingStatus.Pending, checkInInDays: 20, createdMinutesAgo: 120, unpaidIntent: true);
        var cancelled = await SeedBookingAsync(org, BookingStatus.Cancelled, checkInInDays: 25, paid: true, refunded: true);

        var hold = await PostAsync(LookupPath, new { orgSlug = org.Slug, bookingCode = expiredHold.Code, email = expiredHold.Email });
        var refunded = await PostAsync(LookupPath, new { orgSlug = org.Slug, bookingCode = cancelled.Code, email = cancelled.Email });

        var holdRoot = JsonDocument.Parse(await hold.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Expired", holdRoot.GetProperty("status").GetString());
        Assert.Equal("NotApplicable", holdRoot.GetProperty("checkIn").GetProperty("status").GetString());
        var refundedRoot = JsonDocument.Parse(await refunded.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Cancelled", refundedRoot.GetProperty("status").GetString());
        Assert.Equal(362m, refundedRoot.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(362m, refundedRoot.GetProperty("refundedAmount").GetDecimal());
    }

    [PostgresFact]
    public async Task SendGuestCheckInLink_CheckInOpen_EmailsANewLinkToTheAddressOfTheBookingOnly()
    {
        var org = await SeedOrgAsync();
        var seed = await SeedBookingAsync(org, BookingStatus.Confirmed, checkInInDays: 2);
        var credentials = new { orgSlug = org.Slug, bookingCode = BookingCodes.Format(seed.Code), email = seed.Email.ToUpperInvariant() };

        var before = JsonDocument.Parse(await (await PostAsync(LookupPath, credentials)).Content.ReadAsStringAsync()).RootElement;
        var response = await PostAsync(CheckInLinkPath, credentials);

        Assert.Equal("Open", before.GetProperty("checkIn").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("checkIn").GetProperty("linkSentAt").ValueKind);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.DoesNotContain("/checkin/", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // CO-09: the link email goes through its own job, which records the real outcome for the host.
        await _factory.RunCheckInLinkEmailJobsAsync(seed.BookingId);
        var email = Assert.Single(_factory.LinkEmails.Snapshot(), e => e.To == seed.Email);
        Assert.Contains("Completa il check-in", email.Subject, StringComparison.Ordinal);
        Assert.Contains("https://casazen-app.vercel.app/checkin/", email.HtmlBody, StringComparison.Ordinal);

        var after = JsonDocument.Parse(await (await PostAsync(LookupPath, credentials)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Open", after.GetProperty("checkIn").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, after.GetProperty("checkIn").GetProperty("linkSentAt").ValueKind);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.GuestCheckInSessions.IgnoreQueryFilters().CountAsync(s => s.BookingId == seed.BookingId));
    }

    [PostgresFact]
    public async Task SendGuestCheckInLink_ArrivalFarAwayOrWrongEmail_SendsNothing()
    {
        var org = await SeedOrgAsync();
        var seed = await SeedBookingAsync(org, BookingStatus.Confirmed, checkInInDays: 40);

        var tooEarly = await PostAsync(CheckInLinkPath, new { orgSlug = org.Slug, bookingCode = seed.Code, email = seed.Email });
        var wrongEmail = await PostAsync(CheckInLinkPath, new { orgSlug = org.Slug, bookingCode = seed.Code, email = $"x.{Guid.NewGuid():N}@example.com" });

        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal(
            GuestBookingLookupErrorCodes.CheckInLinkUnavailable,
            JsonDocument.Parse(await tooEarly.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, wrongEmail.StatusCode);
        Assert.DoesNotContain(_factory.Emails.Snapshot(), e => e.To == seed.Email);
        await _factory.RunCheckInLinkEmailJobsAsync(seed.BookingId);
        Assert.DoesNotContain(_factory.LinkEmails.Snapshot(), e => e.To == seed.Email);
    }

    [PostgresFact]
    public async Task GuestSession_AGuestNeverReadsTheBookingOfAnotherGuestOrSite_AndEveryRefusalLooksAlike()
    {
        // DB-06a: two sites, and on each a guest with THE SAME email and THE SAME booking code (codes are unique per org only), plus
        // another guest of the first site.
        var orgA = await SeedOrgAsync();
        var orgB = await SeedOrgAsync();
        var own = await SeedBookingAsync(orgA, BookingStatus.Confirmed, checkInInDays: 30);
        var otherGuestOfTheSite = await SeedBookingAsync(orgA, BookingStatus.Confirmed, checkInInDays: 40);
        var sameEmailOtherSite = await SeedBookingAsync(orgB, BookingStatus.Confirmed, checkInInDays: 50, code: own.Code, email: own.Email);

        var opened = await PostAsync(SessionPath, new
        {
            orgSlug = orgA.Slug,
            bookingCode = BookingCodes.Format(own.Code).ToLowerInvariant().Replace('-', ' '),
            email = $"  {own.Email.ToUpperInvariant()} ",
        });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var openedBody = await opened.Content.ReadAsStringAsync();
        var session = JsonDocument.Parse(openedBody).RootElement;
        var token = session.GetProperty("accessToken").GetString()!;
        Assert.Equal(BookingCodes.Format(own.Code), session.GetProperty("bookingCode").GetString());
        Assert.InRange(session.GetProperty("expiresAt").GetDateTime(), DateTime.UtcNow, DateTime.UtcNow.AddHours(24));
        Assert.DoesNotContain(own.Email, openedBody + token, StringComparison.OrdinalIgnoreCase);

        // The guest reads their own booking; the same code on the other site (same email) is not reachable with this token.
        Assert.Equal(orgA.PropertyName, await PropertyNameAsync(await GetAsync($"{GuestBookingsPath}/{BookingCodes.Format(own.Code)}", token)));
        var otherSiteSession = await PostAsync(SessionPath, new { orgSlug = orgB.Slug, bookingCode = sameEmailOtherSite.Code, email = own.Email });
        var otherSiteToken = JsonDocument.Parse(await otherSiteSession.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
        Assert.Equal(orgB.PropertyName, await PropertyNameAsync(await GetAsync($"{GuestBookingsPath}/{sameEmailOtherSite.Code}", otherSiteToken)));

        // Another guest of the site, a code that does not exist, not a code, a wrong email at the door: the same 404, nothing leaked.
        var refusals = new[]
        {
            await GetAsync($"{GuestBookingsPath}/{otherGuestOfTheSite.Code}", token),
            await GetAsync($"{GuestBookingsPath}/{UnusedCode(own.Code)}", token),
            await GetAsync($"{GuestBookingsPath}/not-a-code", token),
            await PostAsync(SessionPath, new { orgSlug = orgA.Slug, bookingCode = own.Code, email = $"other.{Guid.NewGuid():N}@example.com" }),
        };
        var bodies = new List<string>();
        foreach (var refusal in refusals)
        {
            Assert.Equal(HttpStatusCode.NotFound, refusal.StatusCode);
            var text = await refusal.Content.ReadAsStringAsync();
            var problem = JsonDocument.Parse(text).RootElement;
            Assert.Equal(GuestBookingLookupErrorCodes.NotFound, problem.GetProperty("code").GetString());
            Assert.DoesNotContain(orgA.PropertyName, text, StringComparison.Ordinal);
            Assert.DoesNotContain(orgB.PropertyName, text, StringComparison.Ordinal);
            bodies.Add(JsonSerializer.Serialize(new
            {
                status = problem.GetProperty("status").GetInt32(),
                code = problem.GetProperty("code").GetString(),
                title = problem.GetProperty("title").GetString(),
                detail = problem.GetProperty("detail").GetString(),
            }));
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));

        // No token, an altered token and a token of nothing: 401, never a booking.
        var path = $"{GuestBookingsPath}/{BookingCodes.Format(own.Code)}";
        var altered = token[..20] + (token[20] == 'A' ? 'B' : 'A') + token[21..];
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(path, altered)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(path, "gs1.not-a-real-token")).StatusCode);
    }

    [PostgresFact]
    public async Task GuestMessages_AGuestNeverReachesTheThreadOfAnotherGuestOrSite_AndAHostNeverListsTheThreadOfAnotherOrg()
    {
        // DB-14a: two sites with a guest each on THE SAME booking code and THE SAME email (codes are unique per org only), plus another
        // guest of the first site. The free text of a message is personal data: it stays in its own thread and never goes in the email.
        var orgA = await SeedOrgAsync();
        var orgB = await SeedOrgAsync();
        var own = await SeedBookingAsync(orgA, BookingStatus.Confirmed, checkInInDays: 30);
        var otherGuestOfTheSite = await SeedBookingAsync(orgA, BookingStatus.Confirmed, checkInInDays: 40);
        var sameCodeOtherSite = await SeedBookingAsync(orgB, BookingStatus.Confirmed, checkInInDays: 50, code: own.Code, email: own.Email);
        var token = await OpenGuestSessionAsync(orgA, own);
        var otherGuestToken = await OpenGuestSessionAsync(orgA, otherGuestOfTheSite);
        var otherSiteToken = await OpenGuestSessionAsync(orgB, sameCodeOtherSite);

        var path = $"{GuestBookingsPath}/{BookingCodes.Format(own.Code)}/messages";
        var marker = $"marker-{Guid.NewGuid():N}";
        var sent = await PostMessageAsync(path, token, $"{marker} IBAN IT60X0542811101000000123456 grazie", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var sentBody = JsonDocument.Parse(await sent.Content.ReadAsStringAsync()).RootElement;
        Assert.True(sentBody.GetProperty("redacted").GetBoolean());
        var stored = sentBody.GetProperty("message").GetProperty("text").GetString()!;
        Assert.Contains(marker, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("IT60X0542811101000000123456", stored, StringComparison.Ordinal);

        // The host is told by email that the guest wrote, and the email does not hold the text.
        var queued = _factory.Emails.Queued.Where(e => e.To == orgA.ContactEmail).ToList();
        var email = Assert.Single(queued);
        Assert.DoesNotContain(marker, email.Content.HtmlBody + email.Content.Subject, StringComparison.Ordinal);

        // The guest reads their thread; the other guest of the site and the guest of the other site (same code, same email) do not see it.
        var own1 = JsonDocument.Parse(await (await GetAsync(path, token)).Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(marker, own1.GetProperty("messages")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        var otherSiteView = await GetAsync(path, otherSiteToken);
        Assert.Equal(HttpStatusCode.OK, otherSiteView.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await otherSiteView.Content.ReadAsStringAsync()).RootElement.GetProperty("messages").GetArrayLength());

        // Another guest of the site, a code that does not exist, not a code: neither read nor write, and every refusal looks alike.
        var refusals = new[]
        {
            await GetAsync(path, otherGuestToken),
            await PostMessageAsync(path, otherGuestToken, "ciao", Guid.NewGuid().ToString()),
            await GetAsync($"{GuestBookingsPath}/{UnusedCode(own.Code)}/messages", token),
            await GetAsync($"{GuestBookingsPath}/not-a-code/messages", token),
        };
        var bodies = new List<string>();
        foreach (var refusal in refusals)
        {
            Assert.Equal(HttpStatusCode.NotFound, refusal.StatusCode);
            var text = await refusal.Content.ReadAsStringAsync();
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
            var problem = JsonDocument.Parse(text).RootElement;
            Assert.Equal(GuestBookingLookupErrorCodes.NotFound, problem.GetProperty("code").GetString());
            bodies.Add(JsonSerializer.Serialize(new
            {
                status = problem.GetProperty("status").GetInt32(),
                code = problem.GetProperty("code").GetString(),
                detail = problem.GetProperty("detail").GetString(),
            }));
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(path, null)).StatusCode);

        // The host's side: the guest of the other site writes too; each org lists only its own thread, and a collaborator who reaches
        // another property lists none.
        var otherSitePath = $"{GuestBookingsPath}/{BookingCodes.Format(sameCodeOtherSite.Code)}/messages";
        var otherMarker = $"other-{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.OK, (await PostMessageAsync(otherSitePath, otherSiteToken, otherMarker, Guid.NewGuid().ToString())).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IGuestMessageService>();
        var listA = await messages.ListForHostAsync(new HostScope(orgA.OrgId), needsReply: true, propertyId: null, page: 1, pageSize: 20);
        var rowA = Assert.Single(listA.Items);
        Assert.Equal(own.BookingId, rowA.BookingId);
        Assert.True(rowA.NeedsReply);
        Assert.Equal(1, rowA.UnreadCount);
        Assert.DoesNotContain(otherMarker, rowA.LastGuestMessagePreview, StringComparison.Ordinal);
        var listB = await messages.ListForHostAsync(new HostScope(orgB.OrgId), needsReply: null, propertyId: null, page: 1, pageSize: 20);
        var rowB = Assert.Single(listB.Items);
        Assert.Equal(sameCodeOtherSite.BookingId, rowB.BookingId);
        Assert.DoesNotContain(marker, rowB.LastGuestMessagePreview, StringComparison.Ordinal);
        var restricted = await messages.ListForHostAsync(
            new HostScope(orgA.OrgId, GrantedToUserId: $"auth0|nobody-{Guid.NewGuid():N}"),
            needsReply: null, propertyId: null, page: 1, pageSize: 20);
        Assert.Empty(restricted.Items);
    }

    private async Task<string> OpenGuestSessionAsync(OrgSeed org, BookingSeed booking)
    {
        var opened = await PostAsync(SessionPath, new { orgSlug = org.Slug, bookingCode = booking.Code, email = booking.Email });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        return JsonDocument.Parse(await opened.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
    }

    /// <summary>POST of a message from a client IP of its own, with the guest-session token as the bearer and the key of the send.</summary>
    private async Task<HttpResponseMessage> PostMessageAsync(string path, string bearerToken, string text, string idempotencyKey)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { text }) };
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, NextPeer());
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        return await client.SendAsync(request);
    }

    [PostgresFact]
    public async Task BackfillBookingCodesSql_BookingsWithoutCode_GetUniqueReadableCodesAndOthersKeepTheirs()
    {
        var orgA = await SeedOrgAsync();
        var orgB = await SeedOrgAsync();
        var withoutCodeA = await SeedBookingAsync(orgA, BookingStatus.Confirmed, checkInInDays: 30);
        var withoutCodeB = await SeedBookingAsync(orgB, BookingStatus.Confirmed, checkInInDays: 30);
        var kept = await SeedBookingAsync(orgA, BookingStatus.Confirmed, checkInInDays: 60);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // As the migration finds them: the column added with an empty default.
            await db.Bookings.IgnoreQueryFilters()
                .Where(b => b.Id == withoutCodeA.BookingId || b.Id == withoutCodeB.BookingId)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.BookingCode, string.Empty));

            await db.Database.ExecuteSqlRawAsync(
                Casazen.Infrastructure.Migrations.AddBookingCode.BackfillBookingCodesSql);
        }

        var codeA = await CodeOfAsync(withoutCodeA.BookingId);
        var codeB = await CodeOfAsync(withoutCodeB.BookingId);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{10}$", codeA);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{10}$", codeB);
        Assert.NotEqual(codeA, codeB);
        Assert.Equal(kept.Code, await CodeOfAsync(kept.BookingId));
    }

    private static string UnusedCode(string code) =>
        string.Concat(code.Select(c => BookingCodes.Alphabet[(BookingCodes.Alphabet.IndexOf(c) + 1) % BookingCodes.Alphabet.Length]));

    private static string NextPeer()
    {
        var n = Interlocked.Increment(ref _nextPeer);
        return $"198.18.{n / 250}.{(n % 250) + 1}";
    }

    /// <summary>POST from a client IP of its own (a new one unless <paramref name="peer"/> is given).</summary>
    private async Task<HttpResponseMessage> PostAsync(string path, object body, string? peer = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, peer ?? NextPeer());
        return await client.SendAsync(request);
    }

    /// <summary>GET from a client IP of its own, with the guest-session token (if any) as the bearer.</summary>
    private async Task<HttpResponseMessage> GetAsync(string path, string? bearerToken)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, NextPeer());
        if (bearerToken is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        return await client.SendAsync(request);
    }

    private static async Task<string?> PropertyNameAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("propertyName").GetString();
    }

    private async Task<string> CodeOfAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.IgnoreQueryFilters().Where(b => b.Id == bookingId).Select(b => b.BookingCode).SingleAsync();
    }

    private sealed record OrgSeed(Guid OrgId, string Slug, string ContactEmail, Guid PropertyId, string PropertyName);

    private sealed record BookingSeed(Guid BookingId, string Code, string Email, DateTime CheckIn);

    private async Task<OrgSeed> SeedOrgAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Villa Lookup Srl",
            Slug = $"lookup-{Guid.NewGuid():N}",
            DisplayName = "Villa Lookup Srl",
            ContactEmail = $"host.{Guid.NewGuid():N}@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        var property = new Property
        {
            OwnerId = $"auth0|bk11-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = $"Casa {Guid.NewGuid():N}"[..20],
            Description = "BK-11",
            Address = $"Via Lookup {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            MaxGuests = 4,
            NightlyRate = 100m,
            CleaningFee = 50m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
        };
        db.AddRange(org, property);
        await db.SaveChangesAsync();
        return new OrgSeed(org.Id, org.Slug, org.ContactEmail, property.Id, property.Name);
    }

    /// <summary>A booking of the public checkout: 3 nights, 300 lodging + 50 cleaning + 12 tourist tax = 362.</summary>
    private async Task<BookingSeed> SeedBookingAsync(
        OrgSeed org,
        BookingStatus status,
        int checkInInDays,
        bool paid = false,
        bool refunded = false,
        int createdMinutesAgo = 60,
        string? code = null,
        bool unpaidIntent = false,
        string? email = null)
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-createdMinutesAgo);
        var checkIn = TimeProvider.System.TodayInRome().AddDays(checkInInDays);
        var guest = new Guest
        {
            OrgId = org.OrgId,
            FirstName = "Giulia",
            LastName = "Bianchi",
            Email = email ?? $"giulia.{Guid.NewGuid():N}@example.com",
            PhoneNumber = "+393331112222",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var booking = new Booking
        {
            PropertyId = org.PropertyId,
            OrgId = org.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = status,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = 350m,
            CleaningFee = 50m,
            TouristTax = 12m,
            TouristTaxAmount = 12m,
            TotalPrice = 362m,
            FreeRefundDeadline = checkIn.AddDays(-7),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        if (code is not null)
            booking.BookingCode = code;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AddRange(guest, booking);
        if (paid)
        {
            db.Payments.Add(new Payment
            {
                BookingId = booking.Id,
                OrgId = org.OrgId,
                Amount = booking.TotalPrice,
                RefundedAmount = refunded ? booking.TotalPrice : 0m,
                Status = refunded ? PaymentStatus.Refunded : PaymentStatus.Completed,
                Method = PaymentMethod.CreditCard,
                TransactionId = $"pi_bk11_{Guid.NewGuid():N}",
                StripePaymentIntentId = $"pi_bk11_{Guid.NewGuid():N}",
                ProcessedAt = createdAt,
            });
        }

        if (unpaidIntent)
        {
            // A checkout hold: its PaymentIntent was created, nothing was paid.
            db.Payments.Add(new Payment
            {
                BookingId = booking.Id,
                OrgId = org.OrgId,
                Amount = booking.TotalPrice,
                Status = PaymentStatus.Pending,
                Method = PaymentMethod.CreditCard,
                TransactionId = $"pi_bk11_{Guid.NewGuid():N}",
                StripePaymentIntentId = $"pi_bk11_{Guid.NewGuid():N}",
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });
        }

        await db.SaveChangesAsync();
        return new BookingSeed(booking.Id, booking.BookingCode, guest.Email, checkIn);
    }

    /// <summary>
    /// The integration host with a recording email queue, client IPs from <see cref="TestPeerIpStartupFilter"/> and low
    /// limits for "Le mie prenotazioni": 4 attempts per IP, 3 per email.
    /// </summary>
    public sealed class LookupFactory : CasazenWebApplicationFactory
    {
        internal RecordingEmailQueue Emails { get; } = new();

        /// <summary>Emails handed to the provider by the check-in link email job (CO-09).</summary>
        internal RecordingEmailService LinkEmails { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:PublicGuestBookingLookup:PermitLimit"] = "4",
                ["RateLimiting:GuestBookingLookupPerEmail:PermitLimit"] = "3",
                ["CheckIn:SendWindowDays"] = "3",
                ["Email:Provider"] = "Resend",
                ["Email:ApiKey"] = "re_test_lookup",
                ["Email:FromAddress"] = "noreply@casazen.test",
            }));
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IEmailQueue>(services);
                services.AddSingleton<IEmailQueue>(Emails);
                RemoveAllOf<IEmailService>(services);
                services.AddSingleton<IEmailService>(LinkEmails);
                services.AddSingleton<IStartupFilter, TestPeerIpStartupFilter>();
            });
        }

        /// <summary>Runs the check-in link email jobs queued for the sessions of <paramref name="bookingId"/>.</summary>
        internal async Task RunCheckInLinkEmailJobsAsync(Guid bookingId)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // No request here: the filter is off anyway, explicit for the reader.
            var sessionIds = await db.GuestCheckInSessions.IgnoreQueryFilters()
                .Where(s => s.BookingId == bookingId)
                .Select(s => s.Id)
                .ToListAsync();
            var jobs = BackgroundJobClientMock.Invocations
                .Where(i => i.Method.Name == "Create")
                .Select(i => (Hangfire.Common.Job)i.Arguments[0])
                .Where(j => j.Type == typeof(GuestCheckInLinkEmailJob) && sessionIds.Contains((Guid)j.Args[0]))
                .ToList();
            foreach (var job in jobs)
            {
                await scope.ServiceProvider.GetRequiredService<GuestCheckInLinkEmailJob>()
                    .SendAsync((Guid)job.Args[0], (string)job.Args[1], (int)job.Args[2]);
            }
        }
    }

    internal sealed class RecordingEmailService : IEmailService
    {
        private readonly List<(string To, string Subject, string HtmlBody)> _sent = [];

        public Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent)
        {
            lock (_sent)
                _sent.Add((to, subject, htmlContent));
            return Task.FromResult(EmailSendResult.Sent());
        }

        public IReadOnlyList<(string To, string Subject, string HtmlBody)> Snapshot()
        {
            lock (_sent)
                return _sent.ToList();
        }
    }
}
