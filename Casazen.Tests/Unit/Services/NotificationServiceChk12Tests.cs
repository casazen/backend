using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F02/US02/T01 (NF-52, CHK-12): the delivery of the new stay stages and of the alerts about a period and the connection. The proof of the privacy rule:
/// the texts name the property and the day and <b>never guest data</b> (a name, an address, a booking code); an email goes to each address it is given, the push
/// to the hosts of the property once per stage.
/// </summary>
public class NotificationServiceChk12Tests : IDisposable
{
    private const string GuestFirstName = "Anna";
    private const string GuestLastName = "Bianchi";
    private const string GuestEmail = "anna.bianchi@example.com";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly RecordingEmailQueue _emails = new();
    private readonly RecordingPushQueue _push = new();

    public void Dispose() => _db.Dispose();

    private NotificationService Service() =>
        new(_db, _emails, _push, EmailTestHelpers.Links(), NullLogger<NotificationService>.Instance);

    private async Task<(Booking Booking, Guid PropertyId)> SeedAsync()
    {
        var org = new OrgEntity { Name = "Test Org", Slug = $"org-{Guid.NewGuid():N}", DisplayName = "Test Org", ContactEmail = "host@example.com" };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = "Villa Rosa",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            MaxGuests = 4,
            IsActive = true,
        };
        var guest = new Guest { OrgId = org.Id, FirstName = GuestFirstName, LastName = GuestLastName, Email = GuestEmail };
        var booking = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
        };
        _db.AddRange(org, property, guest, booking);
        await _db.SaveChangesAsync();
        return (booking, property.Id);
    }

    [Theory]
    [InlineData(StayAlertKind.ReceiptMissing, "receipt-missing", "receipt-missing", "Ricevuta Alloggiati Web mancante")]
    [InlineData(StayAlertKind.ShortStayAtRisk, "short-stay-at-risk", "short-stay-at-risk", "Soggiorno breve")]
    [InlineData(StayAlertKind.OverThirtyNights, "over-thirty-nights", "over-thirty-nights", "Soggiorno oltre 30 notti")]
    public async Task ComplianceAlerts_StayAlertTextsNameNoGuestPersonalData_Test(StayAlertKind kind, string template, string pushType, string subjectStart)
    {
        var (booking, _) = await SeedAsync();

        await Service().SendStayAlertAsync(new StayAlert(booking.Id, kind, ShortStay: kind == StayAlertKind.ShortStayAtRisk));

        var email = Assert.Single(_emails.Queued);
        Assert.Equal("host@example.com", email.To);
        Assert.Equal(template, email.Template);
        Assert.StartsWith(subjectStart, email.Content.Subject, StringComparison.Ordinal);
        var push = Assert.Single(_push.Queued);
        Assert.Equal(pushType, push.Payload.Type);
        Assert.Equal(PushAudience.BookingHosts(booking.Id), push.Audience);
        Assert.Equal($"/bookings/{booking.Id}", push.Payload.Route);

        // What the host reads: the property and the day, and nothing of the guest, in the email and in the push. The service writes in the default culture
        // (Italian); the English texts of the same templates, with the inputs the service gives them, are scanned by the next tests.
        AssertNoGuestData(new[] { email.Content.Subject, email.Content.HtmlBody, push.Payload.Title, push.Payload.Body }, booking);
        Assert.Contains("Villa Rosa", email.Content.Subject, StringComparison.Ordinal);
        Assert.Contains("05/11/2026", email.Content.Subject, StringComparison.Ordinal);
        Assert.Contains("Villa Rosa", push.Payload.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StayAlertKind.ReceiptMissing)]
    [InlineData(StayAlertKind.ShortStayAtRisk)]
    [InlineData(StayAlertKind.OverThirtyNights)]
    public async Task ComplianceAlerts_StayAlertTextsNameNoGuestPersonalData_InEnglishToo_Test(StayAlertKind kind)
    {
        var (booking, _) = await SeedAsync();
        var english = CultureInfo.GetCultureInfo("en");
        var link = EmailTestHelpers.Links().HostBooking(booking.Id);

        // The inputs NotificationService gives the templates: the property, the arrival day, the legal deadline of a short stay and the link; never the guest.
        var email = kind switch
        {
            StayAlertKind.ReceiptMissing => EmailTemplates.ReceiptMissing(english, "Villa Rosa", booking.CheckInDate, link),
            StayAlertKind.ShortStayAtRisk => EmailTemplates.ShortStayAtRisk(english, "Villa Rosa", booking.CheckInDate, new DateTime(2026, 11, 5, 19, 0, 0, DateTimeKind.Utc), link),
            _ => EmailTemplates.OverThirtyNights(english, "Villa Rosa", booking.CheckInDate, link),
        };
        var push = EmailTemplates.StayAlertPush(english, kind, "Villa Rosa", booking.CheckInDate);

        Assert.Contains("<html lang=\"en\">", email.HtmlBody, StringComparison.Ordinal);
        AssertNoGuestData(new[] { email.Subject, email.HtmlBody, push.Title, push.Body }, booking);
        Assert.Contains("Villa Rosa", email.Subject, StringComparison.Ordinal);
        Assert.Contains("5 November 2026", email.Subject, StringComparison.Ordinal);
        Assert.Contains("Villa Rosa", push.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public async Task ComplianceAlerts_PeriodAndConnectionTextsNameNoGuestPersonalData_InBothLanguages_Test(string cultureName)
    {
        var (booking, propertyId) = await SeedAsync();
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var link = EmailTestHelpers.Links().HostCompliance();
        var period = new ComplianceAlert(
            ComplianceAlertType.IstatPeriod, propertyId, ComplianceAlertKind.PeriodOverdue, "2026-10", new DateOnly(2026, 11, 10), ReminderNumber: 1, MaxReminders: 3);
        var connection = new ComplianceAlert(ComplianceAlertType.ConnectionInactive, propertyId, ComplianceAlertKind.ConnectionInactive, ReminderNumber: 1, MaxReminders: 1);

        var emails = new[]
        {
            EmailTemplates.CompliancePeriodAlert(culture, period, "Villa Rosa", link),
            EmailTemplates.ComplianceConnectionAlert(culture, connection, "Villa Rosa", link),
        };
        var pushes = new[] { EmailTemplates.CompliancePush(culture, period, "Villa Rosa"), EmailTemplates.CompliancePush(culture, connection, "Villa Rosa") };

        Assert.Contains($"<html lang=\"{culture.TwoLetterISOLanguageName}\">", emails[0].HtmlBody, StringComparison.Ordinal);
        AssertNoGuestData(
            emails.SelectMany(e => new[] { e.Subject, e.HtmlBody }).Concat(pushes.SelectMany(p => new[] { p.Title, p.Body })),
            booking);
        Assert.All(emails, e => Assert.Contains("Villa Rosa", e.Subject, StringComparison.Ordinal));
    }

    /// <summary>What the host reads names the property and the day and nothing of the guest: no name, no address, no booking code.</summary>
    private static void AssertNoGuestData(IEnumerable<string> texts, Booking booking) =>
        Assert.All(texts, text =>
        {
            Assert.DoesNotContain(GuestFirstName, text, StringComparison.Ordinal);
            Assert.DoesNotContain(GuestLastName, text, StringComparison.Ordinal);
            Assert.DoesNotContain(GuestEmail, text, StringComparison.Ordinal);
            Assert.DoesNotContain(booking.BookingCode, text, StringComparison.Ordinal);
        });

    [Fact]
    public async Task SendStayAlertAsync_TheNewStagesUseTheirOwnPushKeyPerStage()
    {
        var (booking, _) = await SeedAsync();

        await Service().SendStayAlertAsync(new StayAlert(booking.Id, StayAlertKind.ReceiptMissing));
        await Service().SendStayAlertAsync(new StayAlert(booking.Id, StayAlertKind.OverThirtyNights));

        Assert.Equal(
            [
                PushDeliveryKeys.StayAlert(booking.Id, StayAlertKind.ReceiptMissing, booking.CheckInDate, 0),
                PushDeliveryKeys.StayAlert(booking.Id, StayAlertKind.OverThirtyNights, booking.CheckInDate, 0),
            ],
            _push.Queued.Select(p => p.DeliveryKey).ToArray());
    }

    [Fact]
    public async Task SendComplianceAlertAsync_APeriod_QueuesTheEmailToEachAddressAndThePushToTheHostsOfTheProperty()
    {
        var (_, propertyId) = await SeedAsync();
        var alert = new ComplianceAlert(
            ComplianceAlertType.IstatPeriod,
            propertyId,
            ComplianceAlertKind.PeriodOverdue,
            "2026-10",
            new DateOnly(2026, 11, 10),
            ReminderNumber: 1,
            MaxReminders: 3);
        var key = PushDeliveryKeys.ComplianceAlert(propertyId, alert.Type, "2026-10", ComplianceAlertStage.OverdueReminder(1), new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc));

        await Service().SendComplianceAlertAsync(alert, ["host@example.com", "HOST@example.com", "", "admin@example.com"], key);

        // Distinct addresses only (the same address in another case is the same one), none blank.
        Assert.Equal(["host@example.com", "admin@example.com"], _emails.Queued.Select(e => e.To!).ToArray());
        var email = _emails.Queued[0];
        Assert.Equal("compliance-period-alert", email.Template);
        Assert.Equal("ISTAT scaduta - Villa Rosa (ottobre 2026)", email.Content.Subject);
        Assert.Contains("<strong>10/11/2026</strong>", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Promemoria 1 di 3.", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains($"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/app/short-rent/compliance\"", email.Content.HtmlBody, StringComparison.Ordinal);
        var push = Assert.Single(_push.Queued);
        Assert.Equal(key, push.DeliveryKey);
        Assert.Equal(PushAudience.PropertyHosts(propertyId), push.Audience);
        Assert.Equal(PushTypes.IstatDeadline, push.Payload.Type);
        Assert.Equal(PushRoutes.Properties, push.Payload.Route);
        Assert.Null(push.Payload.BookingId);
        Assert.Contains("Villa Rosa", push.Payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendComplianceAlertAsync_TheConnection_HasItsOwnTemplateAndPushType_AndNamesNothingOfAGuest()
    {
        var (_, propertyId) = await SeedAsync();
        var alert = new ComplianceAlert(ComplianceAlertType.ConnectionInactive, propertyId, ComplianceAlertKind.ConnectionInactive);

        await Service().SendComplianceAlertAsync(alert, ["host@example.com"], "key-connection");

        var email = Assert.Single(_emails.Queued);
        Assert.Equal("compliance-connection-alert", email.Template);
        Assert.Equal("Invio automatico non attivo - Villa Rosa", email.Content.Subject);
        Assert.DoesNotContain(GuestFirstName, email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(PushTypes.ConnectionInactive, Assert.Single(_push.Queued).Payload.Type);
    }

    [Fact]
    public async Task SendComplianceAlertAsync_APropertyThatIsGone_SendsNothing()
    {
        await Service().SendComplianceAlertAsync(
            new ComplianceAlert(ComplianceAlertType.IstatPeriod, Guid.NewGuid(), ComplianceAlertKind.PeriodDeadlineApproaching, "2026-10", new DateOnly(2026, 11, 10)),
            ["host@example.com"],
            "key");

        Assert.Empty(_emails.Queued);
        Assert.Empty(_push.Queued);
    }

    [Fact]
    public void TheKeyOfAComplianceAlert_TellsTwoIncidentsOfTheConnectionApart_AndStaysStableForARetry()
    {
        var property = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
        var first = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

        var one = PushDeliveryKeys.ComplianceAlert(property, ComplianceAlertType.ConnectionInactive, "connection", 1, first);

        Assert.Equal(one, PushDeliveryKeys.ComplianceAlert(property, ComplianceAlertType.ConnectionInactive, "connection", 1, first));
        Assert.NotEqual(one, PushDeliveryKeys.ComplianceAlert(property, ComplianceAlertType.ConnectionInactive, "connection", 1, first.AddDays(20)));
        Assert.NotEqual(one, PushDeliveryKeys.ComplianceAlert(property, ComplianceAlertType.ConnectionInactive, "connection", 2, first));
    }

    [Fact]
    public void ThePushTypes_OfTheNewStagesAndAlerts_AreDeclared()
    {
        Assert.Equal("receipt-missing", PushTypes.ForStayAlert(StayAlertKind.ReceiptMissing));
        Assert.Equal("short-stay-at-risk", PushTypes.ForStayAlert(StayAlertKind.ShortStayAtRisk));
        Assert.Equal("over-thirty-nights", PushTypes.ForStayAlert(StayAlertKind.OverThirtyNights));
        Assert.Equal("istat-deadline", PushTypes.ForComplianceAlert(ComplianceAlertType.IstatPeriod));
        Assert.Equal("tourist-tax-deadline", PushTypes.ForComplianceAlert(ComplianceAlertType.TouristTaxPeriod));
        Assert.Equal("connection-inactive", PushTypes.ForComplianceAlert(ComplianceAlertType.ConnectionInactive));
        Assert.True(PushRoutes.IsAppRoute(PushRoutes.Properties));
    }
}
