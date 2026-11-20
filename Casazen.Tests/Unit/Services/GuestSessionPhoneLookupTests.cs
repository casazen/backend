using System.Text.Json;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E40/F01/US03/T01 (NF-143): the guest opens the session of the guest area with the booking code and the last four digits of the phone instead of
/// the e-mail, and gets the same session. The proofs of the privacy rules: the answer never tells whether a code exists, the digits never leave the
/// server, and with the flag <c>WhiteLabelHost</c> off nothing changes. On EF InMemory with the real services (<see cref="GuestSessionPhoneWorld"/>).
/// </summary>
public class GuestSessionPhoneLookupTests
{
    private const string Code = "7K2XM-9QD4T";

    /// <summary>AC1: the right digits open the session of the e-mail proof: the same body, the same guest of the same site, the same booking.</summary>
    [Fact]
    public async Task RightDigits_OpenTheSameSessionAsTheEmail()
    {
        using var world = new GuestSessionPhoneWorld();
        var (org, house) = await world.AddSiteAsync();
        var booking = await world.AddBookingAsync(org, house, "anna@example.com", GuestSessionPhoneWorld.Phone);

        var byPhone = await world.OpenAsync(
            GuestSessionPhoneWorld.PhoneBody(org, BookingCodes.Format(booking.BookingCode).ToLowerInvariant(), GuestSessionPhoneWorld.RightDigits));
        var byEmail = await world.OpenAsync(GuestSessionPhoneWorld.EmailBody(org, booking.BookingCode, "ANNA@example.com"));

        Assert.Equal(StatusCodes.Status200OK, byPhone.Status);
        Assert.Equal(StatusCodes.Status200OK, byEmail.Status);
        var phone = JsonDocument.Parse(byPhone.Body).RootElement;
        var email = JsonDocument.Parse(byEmail.Body).RootElement;
        Assert.Equal(email.EnumerateObject().Select(p => p.Name), phone.EnumerateObject().Select(p => p.Name));
        Assert.Equal(BookingCodes.Format(booking.BookingCode), phone.GetProperty("bookingCode").GetString());
        var phoneTicket = world.Sessions.Validate(phone.GetProperty("accessToken").GetString());
        var emailTicket = world.Sessions.Validate(email.GetProperty("accessToken").GetString());
        Assert.NotNull(phoneTicket);
        Assert.NotNull(emailTicket);
        Assert.Equal((emailTicket.OrgId, emailTicket.EmailHash), (phoneTicket.OrgId, phoneTicket.EmailHash));
        Assert.Equal(booking.Id, (await world.Sessions.FindBookingAsync(phoneTicket, booking.BookingCode)).Id);
    }

    /// <summary>
    /// Privacy rule (AC2, AC-NF143-2): the lookup never reveals whether a code exists. Wrong digits, a code that does not exist, the code of another
    /// site (with the right digits of that booking), a string that is not a code and a booking without a phone (with the digits that stand in for a
    /// missing phone) get the answer of a wrong e-mail: the same status, the same headers, the same body.
    /// </summary>
    [Fact]
    public async Task WrongDigits_UnknownCode_NoPhone_SameResponse()
    {
        using var world = new GuestSessionPhoneWorld();
        var (org, house) = await world.AddSiteAsync();
        var (otherOrg, otherHouse) = await world.AddSiteAsync();
        var booking = await world.AddBookingAsync(org, house, "anna@example.com", GuestSessionPhoneWorld.Phone);
        var withoutPhone = await world.AddBookingAsync(org, house, "bruno@example.com", phone: string.Empty);
        var otherSite = await world.AddBookingAsync(otherOrg, otherHouse, "carla@example.com", GuestSessionPhoneWorld.Phone);

        var refusals = new[]
        {
            await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.BookingCode, GuestSessionPhoneWorld.WrongDigits)),
            await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, GuestSessionPhoneWorld.OtherCode(booking.BookingCode), GuestSessionPhoneWorld.RightDigits)),
            await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, otherSite.BookingCode, GuestSessionPhoneWorld.RightDigits)),
            await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.Id.ToString("D"), GuestSessionPhoneWorld.RightDigits)),
            await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, withoutPhone.BookingCode, "0000")),
            await world.OpenAsync(GuestSessionPhoneWorld.EmailBody(org, booking.BookingCode, "someone.else@example.com")),
        };

        Assert.All(refusals, refusal => Assert.Equal(StatusCodes.Status404NotFound, refusal.Status));
        Assert.All(refusals, refusal => Assert.True(refusal.ActionRan));
        Assert.Single(refusals.Select(refusal => refusal.Fingerprint).Distinct(StringComparer.Ordinal));
        Assert.Equal(GuestBookingLookupErrorCodes.NotFound, JsonDocument.Parse(refusals[0].Body).RootElement.GetProperty("code").GetString());

        // The same booking does open with its right digits, and the booking without a phone with its e-mail (AC-NF143-4).
        Assert.Equal(
            StatusCodes.Status200OK,
            (await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.BookingCode, GuestSessionPhoneWorld.RightDigits))).Status);
        Assert.Equal(
            StatusCodes.Status200OK,
            (await world.OpenAsync(GuestSessionPhoneWorld.EmailBody(org, withoutPhone.BookingCode, "bruno@example.com"))).Status);
    }

    /// <summary>
    /// Privacy rule (AC7): the phone never leaves the server. Through five wrong attempts (404) and the lock that follows (429), neither the digits the
    /// guest typed, nor the digits of the booking, nor the code, nor the client IP appear in any log line or in any answer (headers and bodies).
    /// </summary>
    [Fact]
    public async Task PhoneDigits_NeverInLogsOrBodies()
    {
        using var world = new GuestSessionPhoneWorld();
        var (org, house) = await world.AddSiteAsync();
        var booking = await world.AddBookingAsync(org, house, "anna@example.com", GuestSessionPhoneWorld.Phone);

        var answers = new List<SessionAnswer>();
        for (var attempt = 0; attempt < 5; attempt++)
            answers.Add(await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.BookingCode, GuestSessionPhoneWorld.WrongDigits)));
        answers.Add(await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.BookingCode, GuestSessionPhoneWorld.RightDigits)));

        Assert.Equal(new[] { 404, 404, 404, 404, 404, 429 }, answers.Select(answer => answer.Status));

        // Every refusal is in the log (the 404s by the error middleware, the lock by its filter), and none of them carries what the guest typed.
        Assert.Equal(5, world.ErrorLogs.Entries.Count);
        Assert.Contains(GuestPhoneLookupLockout.PolicyName, world.FilterLogs.AllOutput, StringComparison.Ordinal);
        var everything = world.AllLogs + string.Concat(answers.Select(answer => answer.Fingerprint));
        Assert.DoesNotContain(GuestSessionPhoneWorld.WrongDigits, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(GuestSessionPhoneWorld.RightDigits, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(booking.BookingCode, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BookingCodes.Format(booking.BookingCode), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(GuestSessionPhoneWorld.ClientIp, everything, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC5: exactly one of the e-mail and the digits, and the digits are four ASCII digits; nothing in the messages names a booking. With
    /// <c>WhiteLabelHost</c> off the digits are not read: a body with only the digits gets, field by field and word by word, the 400 of the lookup's
    /// body without the e-mail, and a body with both is the e-mail proof of always. Through the model validation of MVC, as the endpoint runs it.
    /// </summary>
    [Fact]
    public void ExactlyOneProof_FourAsciiDigits_AndWithTheFlagOffTheDigitsAreRefusedLikeAMissingEmail()
    {
        using var on = new MvcValidation(whiteLabelHost: true);
        var texts = on.Texts;

        Assert.Empty(on.Errors(Body(digits: "7364")));
        Assert.Empty(on.Errors(Body(email: "anna@example.com")));
        Assert.Equal(Only("Email", texts["GuestSessionProofRequired"]), on.Errors(Body()));
        Assert.Equal(Only("Email", texts["GuestSessionProofRequired"]), on.Errors(Body(digits: "   ")));
        Assert.Equal(Only("Email", texts["GuestSessionProofExclusive"]), on.Errors(Body(email: "anna@example.com", digits: "7364")));
        foreach (var digits in new[] { "736", "73645", "73 4", " 7364", "+364", "736a", "٧٣٦٤", "７３６４" })
            Assert.Equal(Only("PhoneLast4", texts["GuestSessionPhoneLast4Invalid"]), on.Errors(Body(digits: digits)));

        using var off = new MvcValidation(whiteLabelHost: false);
        var lookupWithoutEmail = off.Errors(new GuestBookingLookupRequest { OrgSlug = "villa", BookingCode = Code });
        Assert.Equal(new[] { "Email" }, lookupWithoutEmail.Keys);
        Assert.Equal(lookupWithoutEmail, off.Errors(Body(digits: "7364")));
        Assert.Equal(
            off.Errors(new GuestBookingLookupRequest { OrgSlug = "villa", BookingCode = Code, Email = "not-an-address" }),
            off.Errors(Body(email: "not-an-address", digits: "7364")));
        Assert.Empty(off.Errors(Body(email: "anna@example.com", digits: "not digits")));
    }

    /// <summary>
    /// With <c>WhiteLabelHost</c> off nothing changes for anybody: a body that carries digits next to the e-mail is the e-mail proof of always (the
    /// right digits do not help a wrong address, wrong digits do not stop the right one) and the lock of the codes counts nothing.
    /// </summary>
    [Fact]
    public async Task FlagOff_TheDigitsAreNeverRead_TheEmailDecides_AndNothingIsCounted()
    {
        using var world = new GuestSessionPhoneWorld(whiteLabelHost: false);
        var (org, house) = await world.AddSiteAsync();
        var booking = await world.AddBookingAsync(org, house, "anna@example.com", GuestSessionPhoneWorld.Phone);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var wrongAddress = GuestSessionPhoneWorld.EmailBody(org, booking.BookingCode, "someone.else@example.com");
            wrongAddress.PhoneLast4 = GuestSessionPhoneWorld.RightDigits;
            Assert.Equal(StatusCodes.Status404NotFound, (await world.OpenAsync(wrongAddress)).Status);
        }

        var rightAddress = GuestSessionPhoneWorld.EmailBody(org, booking.BookingCode, "anna@example.com");
        rightAddress.PhoneLast4 = GuestSessionPhoneWorld.WrongDigits;
        Assert.Equal(StatusCodes.Status200OK, (await world.OpenAsync(rightAddress)).Status);
        Assert.Equal(0, world.Lockout.TrackedCodes);
        Assert.Empty(world.FilterLogs.Entries);
    }

    /// <summary>The body of the session as the binder leaves it: the e-mail empty when it is left out, the digits null.</summary>
    private static GuestSessionRequest Body(string? email = null, string? digits = null)
    {
        var body = new GuestSessionRequest { OrgSlug = "villa", BookingCode = Code, PhoneLast4 = digits };
        if (email is not null)
            body.Email = email;
        return body;
    }

    private static Dictionary<string, string[]> Only(string field, LocalizedString message) => new() { [field] = [message.Value] };

    /// <summary>The model validation of MVC with the data annotations localized from <c>SharedResources</c>, as <c>Program.cs</c> sets it up.</summary>
    private sealed class MvcValidation : IDisposable
    {
        private readonly ServiceProvider _provider;

        public MvcValidation(bool whiteLabelHost)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalization();
            services.AddSingleton<IFeatureFlags>(whiteLabelHost ? new TestFeatureFlags(FeatureFlags.WhiteLabelHost) : new TestFeatureFlags());
            services.AddControllers()
                .AddDataAnnotationsLocalization(options =>
                    options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResources)));
            _provider = services.BuildServiceProvider();
        }

        public IStringLocalizer Texts => _provider.GetRequiredService<IStringLocalizer<SharedResources>>();

        /// <summary>The errors of <paramref name="body"/> by field, each with its messages in order.</summary>
        public Dictionary<string, string[]> Errors(object body)
        {
            var context = new ActionContext(new DefaultHttpContext { RequestServices = _provider }, new RouteData(), new ActionDescriptor());
            _provider.GetRequiredService<IObjectModelValidator>().Validate(context, validationState: null, prefix: string.Empty, model: body);
            return context.ModelState
                .Where(entry => entry.Value is { Errors.Count: > 0 })
                .ToDictionary(entry => entry.Key, entry => entry.Value!.Errors.Select(error => error.ErrorMessage).ToArray());
        }

        public void Dispose() => _provider.Dispose();
    }
}
