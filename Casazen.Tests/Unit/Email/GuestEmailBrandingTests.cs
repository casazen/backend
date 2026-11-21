using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Push;
using Casazen.Tests.Unit.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// E50/F01/US03/T01 (NF-155, D3, AC-NF155-1, AC-NF155-2): the e-mails to a guest carry the brand of the host (logo, color, name; sent in its name, answers to it) while the
/// guest portal is on, and are the CasaZen e-mails they have always been otherwise; the e-mails of the host, of the staff and of the suppliers never change; and nothing a host
/// types in its settings can inject markup, CSS or a header.
/// </summary>
public sealed class GuestEmailBrandingTests : IDisposable
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly DateTime CheckIn = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Expires = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
    private const string CheckInUrl = "https://app.casazen.test/checkin/abc";

    private static readonly GuestEmailBranding Aurora = new("Casa Aurora", "https://cdn.casazen.test/logos/aurora.png", "#336699", "host@aurora.test");

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();

    public void Dispose() => _db.Dispose();

    // ---- AC1: logo, color and name appear, the sender is the host ------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST (privacy/data: what a guest sees of the host): a guest template rendered with the brand of an org shows its logo (https, alt text = the name, bounded), its color on the
    /// header bar and on the buttons with the text that reads on it, its name, the small «powered by CasaZen» footer instead of the CasaZen one, and the content carries the host's name as
    /// the sender and the host as reply-to.
    /// </summary>
    [Fact]
    public void GuestTemplate_WithBranding_ShowsLogoColorAndHostName()
    {
        var content = EmailTemplates.GuestCheckInLink(Italian, "Anna", "Villa Rosa", CheckIn, CheckInUrl, Expires, Aurora);

        var html = content.HtmlBody;
        Assert.Contains("<img src=\"https://cdn.casazen.test/logos/aurora.png\" alt=\"Casa Aurora\" height=\"40\"", html, StringComparison.Ordinal);
        Assert.Contains("max-height:40px;max-width:160px", html, StringComparison.Ordinal);
        Assert.Contains("<span style=\"font-size:18px;font-weight:bold;vertical-align:middle;\">Casa Aurora</span>", html, StringComparison.Ordinal);
        Assert.Contains("background:#336699;color:#ffffff;padding:14px 20px", html, StringComparison.Ordinal);
        Assert.Contains("background:#336699;color:#ffffff;text-decoration:none", html, StringComparison.Ordinal);
        Assert.Contains("Powered by CasaZen</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("gestione affitti brevi", html, StringComparison.Ordinal);
        Assert.DoesNotContain("#0d8abc", html, StringComparison.Ordinal);

        Assert.Equal("Casa Aurora", content.FromName);
        Assert.Equal("host@aurora.test", content.ReplyTo);
    }

    /// <summary>A light color gets dark text and a dark one white text: the button always reads (at least 4.5 to 1).</summary>
    [Theory]
    [InlineData("#ffe066", "#000000")]
    [InlineData("#003366", "#ffffff")]
    [InlineData("#FFFFFF", "#000000")]
    [InlineData("#000000", "#ffffff")]
    public void ButtonText_IsTheOneThatReadsOnThePrimaryColor(string color, string expectedText)
    {
        var content = EmailTemplates.GuestCheckInLink(Italian, "Anna", "Villa Rosa", CheckIn, CheckInUrl, Expires, new GuestEmailBranding("Casa Aurora", null, color, null));

        Assert.Contains($"background:{color.ToLowerInvariant()};color:{expectedText};text-decoration:none", content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Branding_WithoutLogo_ShowsTheNameOnly_AndWithoutColor_KeepsTheCasaZenBlue()
    {
        var content = EmailTemplates.GuestCheckInLink(Italian, "Anna", "Villa Rosa", CheckIn, CheckInUrl, Expires, new GuestEmailBranding("Casa Aurora", null, null, null));

        var html = content.HtmlBody;
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains(">Casa Aurora</span>", html, StringComparison.Ordinal);
        Assert.Contains("background:#0d8abc;color:#ffffff;padding:14px 20px", html, StringComparison.Ordinal);
        Assert.Contains("background:#0d8abc;color:#ffffff;text-decoration:none", html, StringComparison.Ordinal);
        Assert.Equal("Casa Aurora", content.FromName);
        Assert.Null(content.ReplyTo);
    }

    [Fact]
    public void BrandedName_IsHtmlEncoded()
    {
        var content = EmailTemplates.GuestCheckInLink(
            Italian, "Anna", "Villa Rosa", CheckIn, CheckInUrl, Expires, new GuestEmailBranding("Casa & Figli", null, null, null));

        Assert.Contains(">Casa &amp; Figli</span>", content.HtmlBody, StringComparison.Ordinal);
    }

    // ---- AC2: host, staff and supplier e-mails render exactly as before; no brand = the CasaZen e-mail -----------------------------------

    /// <summary>
    /// PROOF TEST (trust: the CasaZen e-mails are not touched): one e-mail of the host, one of the staff and one of a supplier keep the layout of today to the byte at its two ends (the
    /// head and the opening of the body with no header bar, the CasaZen footer, the CasaZen blue on the buttons), carry no sender name and no reply-to, and say nothing of «powered by».
    /// </summary>
    [Fact]
    public void HostAndStaffTemplates_AreUnchanged()
    {
        var summary = new BookingEmailSummary("ABC123", "Villa Rosa", CheckIn, CheckIn.AddDays(3), 2, 300m, 50m, 0m, 350m);
        var host = EmailTemplates.HostBookingConfirmed(
            Italian, "Anna Verdi", summary, BookingConfirmationKind.PaidOnline, 350m, null, "https://app.casazen.test/app/short-rent/bookings/1");
        var staff = EmailTemplates.VerificationOperatorAlert(
            Italian, "Villa Rosa", CheckIn, VerificationAlertReasons.Review, "https://app.casazen.test/app/short-rent/bookings/1");
        var supplier = EmailTemplates.ServiceRequestCreated(Italian, "Mario", "cleaning", "Ostuni", "https://app.casazen.test/app/supplier/inbox");

        foreach (var content in new[] { host, staff, supplier })
            AssertCasaZenLayout(content);

        Assert.Contains("background:#0d8abc;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold;", host.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("background:#0d8abc;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold;", supplier.HtmlBody, StringComparison.Ordinal);
    }

    /// <summary>The whole document of a short e-mail, as it has always been written: nothing was added to the layout of an e-mail that has no brand.</summary>
    [Fact]
    public void LayoutWithoutBrand_IsByteForByteTheOneOfToday()
    {
        var content = new EmailHtmlBuilder(Italian)
            .Paragraph("Booking_Greeting", "Anna")
            .Button("Booking_MyBookingsCta", "https://app.casazen.test/my-bookings")
            .Build("GuestRefundConfirmed_Subject", "Casa Lago");

        var expected =
            "<!DOCTYPE html>\n"
            + "<html lang=\"it\">\n"
            + "<head><meta charset=\"utf-8\" /><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />"
            + $"<title>{EmailHtmlBuilder.Encode(content.Subject)}</title></head>\n"
            + "<body style=\"font-family:Arial,sans-serif;color:#18181b;line-height:1.5;\">\n"
            + "<p>Gentile Anna,</p>\n"
            + "<p><a href=\"https://app.casazen.test/my-bookings\" style=\"display:inline-block;padding:12px 20px;background:#0d8abc;"
            + "color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold;\">Le mie prenotazioni</a></p>\n"
            + "<p style=\"font-size:12px;color:#a1a1aa;\">CasaZen — gestione affitti brevi</p>\n"
            + "</body>\n</html>\n";
        Assert.Equal(expected, content.HtmlBody);
        Assert.Null(content.ReplyTo);
        Assert.Null(content.FromName);

        // A brand with nothing usable in it is no brand: the same document.
        var empty = new EmailHtmlBuilder(Italian, new GuestEmailBranding(null, "javascript:alert(1)", "red", "not an address"))
            .Paragraph("Booking_Greeting", "Anna")
            .Button("Booking_MyBookingsCta", "https://app.casazen.test/my-bookings")
            .Build("GuestRefundConfirmed_Subject", "Casa Lago");
        Assert.Equal(expected, empty.HtmlBody);
    }

    // ---- AC3: a hostile logo, color, name or address cannot inject anything --------------------------------------------------------------

    /// <summary>
    /// PROOF TEST (security: what a host types never becomes markup, CSS or a header): a logo that is a <c>javascript:</c> address, a color that tries to add a rule, a name with a line
    /// break, markup and a second header, and a reply-to with a second header are dropped (or cleaned), and the e-mail keeps the CasaZen blue and carries no foreign header.
    /// </summary>
    [Fact]
    public void HostileValues_AreEncodedOrDropped()
    {
        var hostile = new GuestEmailBranding(
            "Casa <script>alert(1)</script>\r\nBcc: evil@x.test",
            "javascript:alert(1)",
            "red;background:url(x)",
            "host@x.test\r\nBcc: evil@y.test");

        var content = EmailTemplates.GuestCheckInLink(Italian, "Anna", "Villa Rosa", CheckIn, CheckInUrl, Expires, hostile);

        var html = content.HtmlBody;
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("background:url", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("red;", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil@", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("background:#0d8abc;color:#ffffff;text-decoration:none", html, StringComparison.Ordinal);

        Assert.NotNull(content.FromName);
        Assert.All(content.FromName!, c => Assert.False(char.IsControl(c), "a control character in the sender's name"));
        Assert.DoesNotContain("<", content.FromName, StringComparison.Ordinal);
        Assert.DoesNotContain("@", content.FromName, StringComparison.Ordinal);
        Assert.Null(content.ReplyTo);
    }

    [Theory]
    [InlineData("http://cdn.casazen.test/logo.png")]
    [InlineData("//cdn.casazen.test/logo.png")]
    [InlineData("/logos/logo.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("https://cdn.casazen.test/logo.png\" onerror=\"alert(1)")]
    [InlineData("https://cdn.casazen.test/logo.png onerror=alert(1)")]
    [InlineData("https://user:secret@cdn.casazen.test/logo.png")]
    [InlineData("https://cdn.casazen.test/<b>.png")]
    public void LogoThatIsNotAPlainHttpsAddress_IsDropped(string logo)
    {
        var content = EmailTemplates.GuestCheckInLink(
            Italian, "Anna", "Villa Rosa", CheckIn, CheckInUrl, Expires, new GuestEmailBranding("Casa Aurora", logo, null, null));

        Assert.DoesNotContain("<img", content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("onerror", content.HtmlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("#abc")]
    [InlineData("#12345g")]
    [InlineData("#1234567")]
    [InlineData("rgb(0,0,0)")]
    [InlineData("#336699;background:url(x)")]
    [InlineData("")]
    public void ColorThatIsNotExactlyHashAndSixHexDigits_IsDropped(string color)
    {
        Assert.Null(GuestEmailBranding.CleanColor(color));

        var content = EmailTemplates.GuestCheckInLink(
            Italian, "Anna", "Villa Rosa", CheckIn, CheckInUrl, Expires, new GuestEmailBranding("Casa Aurora", null, color, null));
        Assert.Contains("background:#0d8abc;color:#ffffff;text-decoration:none", content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Name_IsCleanedAndBounded()
    {
        Assert.Equal("Casa Aurora", GuestEmailBranding.CleanName("  \"Casa\t[Aurora]\"\u202E  "));
        Assert.Equal("Dell'Orto B&B", GuestEmailBranding.CleanName("Dell'Orto B&B"));
        Assert.Null(GuestEmailBranding.CleanName("<>[]\"\r\n"));
        Assert.Null(GuestEmailBranding.CleanName("   "));

        var long64 = GuestEmailBranding.CleanName(new string('a', 100));
        Assert.Equal(GuestEmailBranding.NameMaxLength, long64!.Length);

        // Never in the middle of a surrogate pair.
        var emoji = GuestEmailBranding.CleanName(new string('a', 63) + "\U0001F3E0");
        Assert.Equal(new string('a', 63), emoji);
    }

    // ---- The provider: the values of the org's showcase, only while the portal is on -------------------------------------------------

    [Fact]
    public async Task Provider_FlagOff_GivesNoBrand_SoEveryGuestEmailIsTheCasaZenOne()
    {
        var org = await SeedOrgAsync();

        var provider = new GuestEmailBrandingProvider(_db, new TestFeatureFlags(), NullLogger<GuestEmailBrandingProvider>.Instance);

        Assert.Null(await provider.ForOrgAsync(org.Id));
    }

    [Fact]
    public async Task Provider_FlagOn_GivesTheHostNameLogoColorAndContactOfTheShowcase()
    {
        var org = await SeedOrgAsync();
        var provider = new GuestEmailBrandingProvider(_db, new TestFeatureFlags(FeatureFlags.GuestPortal), NullLogger<GuestEmailBrandingProvider>.Instance);

        var branding = await provider.ForOrgAsync(org.Id);

        Assert.Equal(new GuestEmailBranding("Giulia", "https://cdn.casazen.test/logos/aurora.png", "#336699", "host@aurora.test"), branding);
        Assert.Null(await provider.ForOrgAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Provider_HostileHostName_FallsBackToTheDisplayName_AndAHostileLogoIsDropped()
    {
        var org = await SeedOrgAsync();
        org.HostName = "<>\r\n";
        org.LogoUrl = "javascript:alert(1)";
        org.ContactEmail = "not an address";
        await _db.SaveChangesAsync();
        var provider = new GuestEmailBrandingProvider(_db, new TestFeatureFlags(FeatureFlags.GuestPortal), NullLogger<GuestEmailBrandingProvider>.Instance);

        var branding = await provider.ForOrgAsync(org.Id);

        Assert.Equal(new GuestEmailBranding("Casa Aurora", null, "#336699", null), branding);
    }

    // ---- The sender: the host's name as the display name, the address is the configured one ---------------------------------------------

    [Fact]
    public void BrandedEmail_IsQueuedAsTheBrandedJob_WithTheHostNameAndReplyTo()
    {
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");
        var queue = new HangfireEmailQueue(client.Object, EmailTestHelpers.ConfiguredEmail(), NullLogger<HangfireEmailQueue>.Instance);
        var content = EmailTemplates.GuestCheckInLink(Italian, "Anna", "Villa Rosa", CheckIn, CheckInUrl, Expires, Aurora);

        var queued = queue.Enqueue(" guest@example.com ", content, "template-x");

        Assert.True(queued);
        client.Verify(
            c => c.Create(
                It.Is<Job>(job =>
                    job.Type == typeof(EmailDeliveryJob)
                    && job.Method.Name == nameof(EmailDeliveryJob.SendBrandedAsync)
                    && (string)job.Args[0] == "guest@example.com"
                    && (string)job.Args[1] == content.Subject
                    && (string)job.Args[2] == content.HtmlBody
                    && (string)job.Args[3] == "template-x"
                    && (string)job.Args[4] == "host@aurora.test"
                    && (string)job.Args[5] == "Casa Aurora"),
                It.IsAny<EnqueuedState>()),
            Times.Once);
    }

    [Fact]
    public void BrandedConfirmationWithACheckInLink_IsQueuedAsTheBrandedConfirmationJob()
    {
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");
        var queue = new HangfireEmailQueue(client.Object, EmailTestHelpers.ConfiguredEmail(), NullLogger<HangfireEmailQueue>.Instance);
        var bookingId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var content = EmailTemplates.GuestCheckInLink(Italian, "Anna", "Villa Rosa", CheckIn, CheckInUrl, Expires, Aurora) with
        {
            CheckInLink = new CheckInLinkEmail(bookingId, sessionId),
        };

        Assert.True(queue.Enqueue("guest@example.com", content, "template-x"));

        client.Verify(
            c => c.Create(
                It.Is<Job>(job =>
                    job.Type == typeof(BookingConfirmationEmailJob)
                    && job.Method.Name == nameof(BookingConfirmationEmailJob.SendBrandedAsync)
                    && (Guid)job.Args[4] == bookingId
                    && (Guid)job.Args[5] == sessionId
                    && (int)job.Args[6] == 1
                    && (string)job.Args[7] == "host@aurora.test"
                    && (string)job.Args[8] == "Casa Aurora"),
                It.IsAny<EnqueuedState>()),
            Times.Once);
    }

    [Fact]
    public void EmailWithoutABrand_IsQueuedAsTheJobItAlwaysWas()
    {
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");
        var queue = new HangfireEmailQueue(client.Object, EmailTestHelpers.ConfiguredEmail(), NullLogger<HangfireEmailQueue>.Instance);
        var content = EmailTemplates.GuestCheckInLink(Italian, "Anna", "Villa Rosa", CheckIn, CheckInUrl, Expires);

        Assert.True(queue.Enqueue("guest@example.com", content, "template-x"));

        client.Verify(
            c => c.Create(
                It.Is<Job>(job => job.Type == typeof(EmailDeliveryJob) && job.Method.Name == nameof(EmailDeliveryJob.SendAsync) && job.Args.Count == 4),
                It.IsAny<EnqueuedState>()),
            Times.Once);
    }

    /// <summary>The jobs already waiting in the queue during a deploy were created with these signatures: they never change, the new ones are new methods.</summary>
    [Fact]
    public void ExistingHangfireJobMethods_KeepTheirSignatures()
    {
        var text = typeof(string);
        AssertSignature(typeof(EmailDeliveryJob), nameof(EmailDeliveryJob.SendAsync), text, text, text, text);
        AssertSignature(typeof(EmailDeliveryJob), nameof(EmailDeliveryJob.SendWithReplyToAsync), text, text, text, text, text);
        AssertSignature(
            typeof(EmailDeliveryJob),
            nameof(EmailDeliveryJob.SendAccountInvitationAsync),
            text,
            text,
            text,
            text,
            typeof(AccountInvitationEmail),
            typeof(CancellationToken));
        AssertSignature(typeof(BookingConfirmationEmailJob), nameof(BookingConfirmationEmailJob.SendAsync), text, text, text, text, typeof(Guid), typeof(Guid), typeof(int));
        AssertSignature(typeof(GuestCheckInLinkEmailJob), nameof(GuestCheckInLinkEmailJob.SendAsync), typeof(Guid), typeof(string), typeof(int));

        // And the new ones are methods of their own.
        AssertSignature(typeof(EmailDeliveryJob), nameof(EmailDeliveryJob.SendBrandedAsync), text, text, text, text, text, text);
        AssertSignature(
            typeof(BookingConfirmationEmailJob),
            nameof(BookingConfirmationEmailJob.SendBrandedAsync),
            text,
            text,
            text,
            text,
            typeof(Guid),
            typeof(Guid),
            typeof(int),
            text,
            text);
    }

    [Fact]
    public async Task BrandedJob_SendsInTheHostsName_AndTheCasaZenJobsAsBefore()
    {
        var emailService = new Mock<IEmailService>();
        emailService
            .Setup(s => s.SendEmailAsync("guest@example.com", "S", "<p/>", "host@aurora.test", "Casa Aurora"))
            .ReturnsAsync(EmailSendResult.Sent());
        emailService
            .Setup(s => s.SendEmailAsync("guest@example.com", "S", "<p/>"))
            .ReturnsAsync(EmailSendResult.Sent());
        var job = new EmailDeliveryJob(emailService.Object, NullLogger<EmailDeliveryJob>.Instance);

        await job.SendBrandedAsync("guest@example.com", "S", "<p/>", "t", "host@aurora.test", "Casa Aurora");
        await job.SendAsync("guest@example.com", "S", "<p/>", "t");

        emailService.Verify(s => s.SendEmailAsync("guest@example.com", "S", "<p/>", "host@aurora.test", "Casa Aurora"), Times.Once);
        emailService.Verify(s => s.SendEmailAsync("guest@example.com", "S", "<p/>"), Times.Once);
    }

    // ---- The callers: the confirmation of a booking, end to end ------------------------------------------------------------------------

    /// <summary>
    /// With the portal on the confirmation of a booking is sent in the name of the host with the host as reply-to, with its logo and color; with the portal off it is the CasaZen
    /// e-mail of today, to the byte: no header bar, the CasaZen footer, no sender name, no reply-to.
    /// </summary>
    [Fact]
    public async Task Confirmation_FlagOn_IsSentInTheHostsName_FlagOff_IsTheCasaZenEmail()
    {
        var bookingId = await SeedBookingAsync();

        var flags = new TestFeatureFlags(FeatureFlags.GuestPortal);
        var on = new RecordingEmailQueue();
        await Notifier(on, flags).BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);
        flags.Set(FeatureFlags.GuestPortal, false);
        var off = new RecordingEmailQueue();
        await Notifier(off, flags).BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);

        var branded = Assert.Single(on.Snapshot(), e => e.To == "anna.verdi@example.com").Content;
        Assert.Equal("Giulia", branded.FromName);
        Assert.Equal("host@aurora.test", branded.ReplyTo);
        Assert.Contains("https://cdn.casazen.test/logos/aurora.png", branded.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("background:#336699", branded.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Powered by CasaZen", branded.HtmlBody, StringComparison.Ordinal);

        var plain = Assert.Single(off.Snapshot(), e => e.To == "anna.verdi@example.com").Content;
        Assert.Null(plain.FromName);
        Assert.Null(plain.ReplyTo);
        AssertCasaZenLayout(plain);
        Assert.DoesNotContain("cdn.casazen.test", plain.HtmlBody, StringComparison.Ordinal);

        // The host's own e-mail about the same booking is the CasaZen one in both runs.
        foreach (var hostEmail in on.Snapshot().Concat(off.Snapshot()).Where(e => e.Template == EmailTemplates.Names.HostBookingConfirmed))
            AssertCasaZenLayout(hostEmail.Content);
    }

    // ---- The data every test runs on ---------------------------------------------------------------------------------------------------

    private BookingNotifier Notifier(RecordingEmailQueue emails, TestFeatureFlags flags) =>
        new(
            _db,
            emails,
            EmailTestHelpers.Links(),
            new RecordingPushQueue(),
            NullLogger<BookingNotifier>.Instance,
            emailBranding: new GuestEmailBrandingProvider(_db, flags, NullLogger<GuestEmailBrandingProvider>.Instance));

    private async Task<OrgEntity> SeedOrgAsync()
    {
        var org = HostScopeScenario.NewOrg("Casa Aurora", $"aurora-{Guid.NewGuid():N}"[..20]);
        org.HostName = "Giulia";
        org.LogoUrl = "https://cdn.casazen.test/logos/aurora.png";
        org.ThemeColor = "#336699";
        org.ContactEmail = "host@aurora.test";
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        return org;
    }

    private async Task<Guid> SeedBookingAsync()
    {
        var org = await SeedOrgAsync();
        var property = HostScopeScenario.NewProperty(org.Id, "auth0|titolare-aurora", "Trullo Aurora");
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = "anna.verdi@example.com" };
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
        _db.AddRange(property, guest, booking);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return booking.Id;
    }

    /// <summary>What every e-mail that carries no brand has always looked like at both ends, and no sender name or reply-to.</summary>
    private static void AssertCasaZenLayout(EmailContent content)
    {
        var html = content.HtmlBody;
        Assert.StartsWith(
            "<!DOCTYPE html>\n<html lang=\"it\">\n<head><meta charset=\"utf-8\" /><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" /><title>",
            html,
            StringComparison.Ordinal);
        Assert.Contains("<body style=\"font-family:Arial,sans-serif;color:#18181b;line-height:1.5;\">\n<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div style=\"background:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Powered by CasaZen", html, StringComparison.Ordinal);
        Assert.EndsWith(
            "<p style=\"font-size:12px;color:#a1a1aa;\">CasaZen — gestione affitti brevi</p>\n</body>\n</html>\n",
            html,
            StringComparison.Ordinal);
        Assert.Null(content.FromName);
        Assert.Null(content.ReplyTo);
    }

    private static void AssertSignature(Type type, string method, params Type[] parameters)
    {
        var found = type.GetMethod(method, parameters);
        Assert.True(found is not null, $"{type.Name}.{method} lost the signature the queued jobs were created with");
    }
}
