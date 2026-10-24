using System.Globalization;
using System.Reflection;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>The push types, keys and lock-screen texts of every host-facing event: stable, distinct, translated, without personal data.</summary>
public class HostEventPushTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime At = new(2026, 10, 9, 8, 30, 15, DateTimeKind.Utc);

    [Fact]
    public void PushTypes_AreDistinctKebabCase_AndFitTheNotificationColumn()
    {
        var values = typeof(PushTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(values, v =>
        {
            Assert.Matches("^[a-z]+(-[a-z]+)*$", v);
            Assert.InRange(v.Length, 1, Casazen.Core.Entities.InAppNotification.TypeMaxLength);
        });
        Assert.Contains("guest-message", values);
        Assert.Contains("booking-request", values);
    }

    [Fact]
    public void DeliveryKeys_OfTheNewEvents_AreStableAndDistinct()
    {
        var keys = new[]
        {
            PushDeliveryKeys.BookingRequest(Id),
            PushDeliveryKeys.BookingCancelledByGuest(Id),
            PushDeliveryKeys.DeferredChargeFailed(Id, true),
            PushDeliveryKeys.DeferredChargeFailed(Id, false),
            PushDeliveryKeys.DeferredChargeCancelled(Id),
            PushDeliveryKeys.PropertyModeChange(Id, "scheduled"),
            PushDeliveryKeys.PropertyModeChange(Id, "applied"),
            PushDeliveryKeys.PropertyComplianceSuspended(Id, At),
            PushDeliveryKeys.CinDeadline(Id, new DateOnly(2026, 11, 1), 7),
            PushDeliveryKeys.CinDeadline(Id, null, null),
            PushDeliveryKeys.RentPaymentReceived(Id),
            PushDeliveryKeys.ServicePaymentRequested(Id, 1, At),
            PushDeliveryKeys.ServicePaymentFailed(Id, 1, At),
            PushDeliveryKeys.ServicePaymentReceived(Id),
            PushDeliveryKeys.ServicePaymentRefunded(Id, toSupplier: false),
            PushDeliveryKeys.ServicePaymentRefunded(Id, toSupplier: true),
            PushDeliveryKeys.ServicePaymentOfflineRecorded(Id),
        };

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.InRange(k.Length, 1, Casazen.Core.Entities.InAppNotification.DeliveryKeyMaxLength));
        Assert.Equal("booking:11111111222233334444555555555555:request", PushDeliveryKeys.BookingRequest(Id));
        Assert.Equal(PushDeliveryKeys.ServicePaymentRequested(Id, 2, At), PushDeliveryKeys.ServicePaymentRequested(Id, 2, At));
        Assert.NotEqual(PushDeliveryKeys.ServicePaymentRequested(Id, 2, At), PushDeliveryKeys.ServicePaymentRequested(Id, 3, At.AddDays(3)));
        Assert.NotEqual(PushDeliveryKeys.CinDeadline(Id, new DateOnly(2026, 11, 1), 7), PushDeliveryKeys.CinDeadline(Id, new DateOnly(2026, 11, 1), 0));
        Assert.NotEqual(PushDeliveryKeys.PropertyComplianceSuspended(Id, At), PushDeliveryKeys.PropertyComplianceSuspended(Id, At.AddDays(1)));
    }

    [Theory]
    [InlineData("it")]
    [InlineData("en")]
    public void Texts_OfTheNewEvents_ExistInBothLanguages_NameTheCallersFieldsOnly(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        var checkIn = new DateTime(2026, 10, 5);
        var checkOut = new DateTime(2026, 10, 8);
        var texts = new[]
        {
            EmailTemplates.BookingRequestPush(culture, "Villa Rosa", checkIn, checkOut, 2),
            EmailTemplates.BookingCancelledByGuestPush(culture, "Villa Rosa", checkIn, checkOut),
            EmailTemplates.DeferredChargeFailedPush(culture, "Villa Rosa", checkIn, checkOut),
            EmailTemplates.DeferredChargeCancelledPush(culture, "Villa Rosa", checkIn, checkOut),
            EmailTemplates.PropertyModeChangePush(culture, "Scheduled", "Villa Rosa"),
            EmailTemplates.PropertyModeChangePush(culture, "Applied", "Villa Rosa"),
            EmailTemplates.PropertyModeChangePush(culture, "Failed", "Villa Rosa"),
            EmailTemplates.PropertyComplianceSuspendedPush(culture, "Villa Rosa"),
            EmailTemplates.CinDeadlinePush(culture, "Villa Rosa", new DateOnly(2026, 11, 1)),
            EmailTemplates.CinDeadlinePush(culture, "Villa Rosa", null),
            EmailTemplates.RentPaymentReceivedPush(culture, "Villa Rosa"),
            EmailTemplates.ServicePaymentPush(culture, "Push_ServicePaymentRequested", "Villa Rosa"),
            EmailTemplates.ServicePaymentPush(culture, "Push_ServicePaymentFailed", "Villa Rosa"),
            EmailTemplates.ServicePaymentPush(culture, "Push_ServicePaymentReceived", "Villa Rosa"),
            EmailTemplates.ServicePaymentPush(culture, "Push_ServicePaymentRefunded", "Villa Rosa"),
            EmailTemplates.ServicePaymentPush(culture, "Push_ServicePaymentOfflineRecorded", "Villa Rosa"),
        };

        Assert.All(texts, text =>
        {
            Assert.False(string.IsNullOrWhiteSpace(text.Title));
            Assert.Contains("Villa Rosa", text.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Push_", text.Title + text.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("{", text.Title + text.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void BookingRequestPush_Italian_ShowsPropertyDatesAndGuestCount_NoName()
    {
        var push = EmailTemplates.BookingRequestPush(EmailTemplates.DefaultCulture, "Villa Rosa", new DateTime(2026, 10, 5), new DateTime(2026, 10, 8), 3);

        Assert.Equal("Nuova richiesta di prenotazione", push.Title);
        Assert.Equal("Villa Rosa: dal 05/10/2026 al 08/10/2026, ospiti: 3. Rispondi entro la scadenza.", push.Body);
    }
}
