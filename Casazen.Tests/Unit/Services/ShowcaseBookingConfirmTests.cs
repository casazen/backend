using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10: the second half of the booking from a supplier's public showcase — the check of the e-mail address. Only then the
/// supplier receives a request (<c>Richiesto</c>, context <c>Showcase</c>), the hold becomes the request in one step, the customer
/// is created or brought up to date, and the check can be repeated without doing anything twice. The lock that makes two checks
/// at once end in one request needs PostgreSQL (<c>ShowcaseBookingConcurrencyPostgresTests</c>); here the rules run in sequence.
/// </summary>
public class ShowcaseBookingConfirmTests
{
    [Fact]
    public async Task ConfirmEmail_CreatesTheRequestOfTheSupplier_WithEverythingTheCustomerChose()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        var held = Assert.Single(await s.HoldsAsync());
        s.Clock.Advance(TimeSpan.FromMinutes(10));

        var confirmation = await s.Kit.Booking.ConfirmEmailAsync(await s.SupplierAsync(), hold.Id, token);

        var now = s.Clock.GetUtcNow().UtcDateTime;
        var request = Assert.Single(await ShowcaseRequestsAsync(s));
        var customer = Assert.Single(await s.CustomersAsync());
        Assert.Equal(ServiceRequestStatus.Richiesto, request.Status);
        Assert.Equal(ServiceRequestRentalContext.Showcase, request.RentalContext);
        Assert.Equal(ServiceRequestSource.Showcase, request.Source);
        Assert.Equal(s.SupplierOrgId, request.OrgId);
        Assert.Equal(s.SupplierOrgId, request.SupplierOrgId);
        Assert.Null(request.PropertyId);
        Assert.Null(request.BookingId);
        Assert.Equal(ServiceCategories.Cleaning, request.Category);
        Assert.Equal(s.ListingId, request.ServiceListingId);
        Assert.Equal(ServiceRequestScenario.ServiceName, request.ServiceNameSnapshot);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, request.EstimatedAmountCents);
        Assert.Null(request.QuotedAmountCents);
        Assert.Equal(ServiceRequestScenario.FridayAt10, request.ScheduledStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(ServiceRequestScenario.ServiceMinutes), request.ScheduledEndUtc);
        Assert.Equal(held.PublicCode, request.PublicCode);
        Assert.Equal(customer.Id, request.CustomerId);
        Assert.Equal(now, request.CreatedAt);
        Assert.Equal(now, request.UpdatedAt);
        Assert.Null(request.TakenAt);
        Assert.Null(request.ReminderSentAt);
        Assert.Equal(ShowcaseScenario.City, request.LocationCity);
        Assert.Equal(ShowcaseScenario.PostalCode, request.LocationPostalCode);
        Assert.Equal(ShowcaseScenario.Address, request.LocationAddress);
        Assert.Equal(ShowcaseScenario.Floor, request.LocationFloor);
        Assert.Equal(ShowcaseScenario.AccessNotes, request.LocationAccessNotes);

        Assert.Equal(
            new ShowcaseBookingConfirmation(
                held.PublicCode,
                ServiceRequestStatus.Richiesto,
                ServiceRequestScenario.ServiceName,
                request.ScheduledStartUtc!.Value,
                request.ScheduledEndUtc!.Value,
                request.ResponseDueAt,
                AlreadyConfirmed: false),
            confirmation);
    }

    [Fact]
    public async Task ConfirmEmail_TheSupplierHas180MinutesToAnswer_OrTheOnesItChose()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();

        var confirmation = await s.Kit.Booking.ConfirmEmailAsync(await s.SupplierAsync(), hold.Id, token);

        // Decision D8: SupplierSettings.RespondWithinMinutes, 180 by default.
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddMinutes(180), confirmation.RespondBy);

        var settings = await s.Db.SupplierSettings.SingleAsync(x => x.OrgId == s.SupplierOrgId);
        settings.RespondWithinMinutes = 90;
        await s.Db.SaveChangesAsync();
        var (second, secondToken) = await s.HoldAsync(await s.InputAsync(ServiceRequestScenario.FridayAt14));

        var again = await s.Kit.Booking.ConfirmEmailAsync(await s.SupplierAsync(), second.Id, secondToken);

        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddMinutes(90), again.RespondBy);
    }

    [Fact]
    public async Task ConfirmEmail_TheHoldBecomesTheRequest_ItsPayloadIsErased_TheTokenHashStaysForTheReplay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();

        await s.Kit.Booking.ConfirmEmailAsync(await s.SupplierAsync(), hold.Id, token);

        var consumed = Assert.Single(await s.HoldsAsync());
        var request = Assert.Single(await ShowcaseRequestsAsync(s));
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, consumed.ConsumedAt);
        Assert.Equal(request.Id, consumed.ServiceRequestId);
        Assert.Null(consumed.Payload);
        Assert.Equal(ShowcaseBookingTokens.Hash(token), consumed.TokenHash);
        // Kept until it would have lapsed: the unique index on the slot and the replay of the link both need it.
        Assert.Equal(hold.ExpiresAt, consumed.ExpiresAt);
    }

    [Fact]
    public async Task ConfirmEmail_TheRequestKeepsTheSlot_NobodyElseCanBookItAfterTheCheck()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());

        await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);

        // The hold stops counting and the request starts counting in the same step: the slot is taken before and after.
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());
        await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(email: "altra.persona@example.com")));
        // And it stays taken after the consumed hold would have lapsed: from then on only the request holds it.
        s.Clock.Advance(TimeSpan.FromHours(1));
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());
    }

    [Fact]
    public async Task ConfirmEmail_CreatesTheCustomerOfTheSupplier_WithTheDataAndTheConsent()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        var created = s.Clock.GetUtcNow().UtcDateTime;

        await s.BookedAsync(await s.InputAsync(change: i => i with { Locale = "en", Email = "Mario.Rossi@Example.com" }));

        var customer = Assert.Single(await s.CustomersAsync());
        Assert.Equal(s.SupplierOrgId, customer.OrgId);
        Assert.Equal(s.Kit.CustomerIndex.HashEmail("mario.rossi@example.com"), customer.EmailHash);
        Assert.Equal(ShowcaseScenario.CustomerName, customer.FullName);
        // The address as typed: the e-mails go to it. The index is made of its lowercase form.
        Assert.Equal("Mario.Rossi@Example.com", customer.Email);
        Assert.Equal("+393331234567", customer.Phone);
        Assert.Equal("en", customer.Locale);
        Assert.Equal(ServiceRequestTestKit.PrivacyNoticeVersion, customer.PrivacyNoticeVersion);
        Assert.Equal(created, customer.PrivacyAcceptedAt);
        Assert.Equal(ShowcaseScenario.ConsentIp, customer.ConsentIp);
        Assert.Null(customer.AnonymizedAt);
        Assert.Equal(created, customer.CreatedAt);
    }

    [Fact]
    public async Task ConfirmEmail_TheSameAddressAgain_IsTheSameCustomer_BroughtUpToDate()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (first, _) = await s.BookedAsync();
        var firstCustomerId = first.CustomerId;
        s.Clock.Advance(TimeSpan.FromDays(2));

        var (second, _) = await s.BookedAsync(
            await s.InputAsync(
                ServiceRequestScenario.FridayAt14.AddDays(7),
                email: "MARIO.ROSSI@example.com",
                change: i => i with { FullName = "Mario Rossi Junior", Phone = "0612345678", Locale = "en" }));

        var customer = Assert.Single(await s.CustomersAsync());
        Assert.Equal(firstCustomerId, customer.Id);
        Assert.Equal(firstCustomerId, second.CustomerId);
        Assert.Equal("Mario Rossi Junior", customer.FullName);
        Assert.Equal("MARIO.ROSSI@example.com", customer.Email);
        Assert.Equal("0612345678", customer.Phone);
        Assert.Equal("en", customer.Locale);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, customer.UpdatedAt);
        Assert.Equal(2, (await ShowcaseRequestsAsync(s)).Count);
    }

    [Fact]
    public async Task ConfirmEmail_TheSameAddressAtAnotherSupplier_IsAnotherCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        await s.BookedAsync();
        var (hold, token) = await s.HoldAsync(await s.InputAsync(service: other.ServiceSlug), other.Profile);

        await s.Kit.Booking.ConfirmEmailAsync(other.Profile, hold.Id, token);

        var mine = Assert.Single(await s.CustomersAsync());
        var theirs = Assert.Single(await s.CustomersAsync(other.OrgId));
        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Equal(mine.EmailHash, theirs.EmailHash);
        Assert.Equal(other.OrgId, theirs.OrgId);
    }

    [Fact]
    public async Task ConfirmEmail_ACustomerWhoseIndexIsOfAnotherAddress_StopsTheCheck_NeverAttachesTheRequestToSomebodyElse()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.BookedAsync();
        // A collision of the index, or a key that changed under existing customers: the row says another address.
        var customer = await s.Db.ServiceCustomers.SingleAsync();
        customer.Email = "qualcun.altro@example.com";
        await s.Db.SaveChangesAsync();
        var (hold, token) = await s.HoldAsync(await s.InputAsync(ServiceRequestScenario.FridayAt14));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token));

        s.Db.ChangeTracker.Clear();
        Assert.Single(await ShowcaseRequestsAsync(s));
        Assert.Null((await s.HoldsAsync()).Single(h => h.Id == hold.Id).ConsumedAt);
    }

    [Fact]
    public async Task ConfirmEmail_QueuesTheReceiptToTheCustomer_AndTheNewRequestToTheSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        s.ForgetNotifications();

        var confirmation = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);

        var emails = s.Emails.Snapshot();
        Assert.Equal(2, emails.Count);
        var receipt = Assert.Single(emails, e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingReceipt, receipt.Template);
        Assert.Contains("Supplier Srl", receipt.Content.Subject);
        Assert.Contains(BookingCodes.Format(confirmation.PublicCode), receipt.Content.HtmlBody);
        Assert.Contains("60,00 €", receipt.Content.HtmlBody);
        Assert.Contains("/fornitori/vetrina-test/richiesta", receipt.Content.HtmlBody);

        var toSupplier = Assert.Single(emails, e => e.To == "supplier@test.com");
        Assert.Equal(EmailTemplates.Names.SupplierBookingNewRequest, toSupplier.Template);
        Assert.Contains(ShowcaseScenario.City, toSupplier.Content.Subject);
        Assert.Contains("Mario R.", toSupplier.Content.HtmlBody);
    }

    [Fact]
    public async Task ConfirmEmail_TheSupplierIsToldTheComuneAndTheShortNameOnly_NeverTheAddressOrTheContacts()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        s.ForgetNotifications();

        await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);

        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com");
        var everything = toSupplier.Content.Subject + toSupplier.Content.HtmlBody;
        foreach (var secret in new[]
                 {
                     "Rossi", "Segretissima", "Piano 3", "Citofono", "cassetta", ShowcaseScenario.CustomerEmail, "3331234567",
                     ShowcaseScenario.PostalCode, ShowcaseScenario.ConsentIp,
                 })
        {
            Assert.DoesNotContain(secret, everything);
        }

        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), push.Audience);
        Assert.Equal(PushTypes.ServiceRequestCreated, push.Payload.Type);
        Assert.Contains(ShowcaseScenario.City, push.Payload.Title + push.Payload.Body);
        Assert.DoesNotContain("Mario", push.Payload.Title + push.Payload.Body);
        Assert.DoesNotContain("Segretissima", push.Payload.Title + push.Payload.Body);
        Assert.Equal(Assert.Single(await ShowcaseRequestsAsync(s)).Id, push.Payload.ServiceRequestId);
    }

    [Fact]
    public async Task ConfirmEmail_TheReceiptIsInTheLanguageOfTheCustomer_TheSupplierAlwaysInItalian()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync(await s.InputAsync(change: i => i with { Locale = "en" }));
        s.ForgetNotifications();

        await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);

        var receipt = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Contains("Request sent to Supplier Srl", receipt.Content.Subject);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com");
        Assert.Contains("Nuova richiesta dal tuo sito", toSupplier.Content.Subject);
    }

    [Fact]
    public async Task ConfirmEmail_ASecondClick_AnswersTheSame_AndDoesNothingAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        var first = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(5));

        var second = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);

        Assert.Equal(first with { AlreadyConfirmed = true }, second);
        Assert.Single(await ShowcaseRequestsAsync(s));
        Assert.Single(await s.CustomersAsync());
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task ConfirmEmail_TheReplayAfterTheSupplierTookTheRequest_ShowsTheStatusAsItIs_WithoutADeadline()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        var first = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);
        Assert.NotNull(first.RespondBy);
        var request = Assert.Single(await ShowcaseRequestsAsync(s));
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var again = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);

        Assert.True(again.AlreadyConfirmed);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, again.Status);
        Assert.Null(again.RespondBy);
        Assert.Equal(first.PublicCode, again.PublicCode);
    }

    [Fact]
    public async Task ConfirmEmail_AWrongToken_AnUnknownBooking_AndAnotherSuppliersBooking_AreTheSame404()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var (hold, token) = await s.HoldAsync();

        var wrongToken = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, ShowcaseBookingTokens.New()));
        var unknown = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, Guid.NewGuid(), token));
        var ofAnother = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(other.Profile, hold.Id, token));
        var noToken = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, null));
        var blank = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, "   "));
        var tooLong = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, new string('a', 5000)));

        foreach (var ex in new[] { wrongToken, unknown, ofAnother, noToken, blank, tooLong })
        {
            Assert.Equal("supplier_booking_link_invalid", ex.Code);
            Assert.Equal("SupplierBookingLinkInvalid", ex.MessageKey);
            Assert.Equal(wrongToken.Message, ex.Message);
        }

        // Nothing happened: the booking waits for the right link.
        Assert.Empty(await ShowcaseRequestsAsync(s));
        Assert.Null(Assert.Single(await s.HoldsAsync()).ConsumedAt);
        Assert.Empty(await s.HoldsAsync(other.OrgId));
    }

    [Fact]
    public async Task ConfirmEmail_ATokenWithSpacesAroundIt_IsTheSameToken()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();

        var confirmation = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, $"  {token}\n");

        Assert.False(confirmation.AlreadyConfirmed);
    }

    [Fact]
    public async Task ConfirmEmail_AfterThe30Minutes_Is409_AndCreatesNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(30));
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token));

        // The moment it expires is the end of it: at ExpiresAt the hold no longer holds anything.
        Assert.Equal("supplier_booking_link_expired", ex.Code);
        Assert.Empty(await ShowcaseRequestsAsync(s));
        Assert.Empty(await s.CustomersAsync());
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task ConfirmEmail_OneSecondBeforeTheEnd_StillWorks()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1));

        var confirmation = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);

        Assert.False(confirmation.AlreadyConfirmed);
    }

    [Fact]
    public async Task ConfirmEmail_AfterTheExpiry_TheSlotIsFreeAgainForAnotherCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(31));
        await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token));

        var next = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(email: "altra.persona@example.com"));

        Assert.NotEqual(hold.Id, next.Id);
    }

    [Fact]
    public async Task ConfirmEmail_ASupplierSuspendedMeanwhile_Is422_NoRequestIsCreated()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token));

        Assert.Equal("supplier_booking_supplier_unavailable", ex.Code);
        Assert.Empty(await ShowcaseRequestsAsync(s));
        Assert.Empty(await s.CustomersAsync());
        Assert.Null(Assert.Single(await s.HoldsAsync()).ConsumedAt);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task ConfirmEmail_APayloadThatCannotBeRead_IsTheSame404_AndCreatesNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        var saved = await s.Db.ShowcaseBookingHolds.SingleAsync();
        saved.Payload = "{ this is not a payload";
        await s.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token));

        Assert.Equal("supplier_booking_link_invalid", ex.Code);
        Assert.Empty(await ShowcaseRequestsAsync(s));
        Assert.Empty(await s.CustomersAsync());
    }

    [Fact]
    public async Task ConfirmEmail_ASaveThatFails_LeavesTheBookingWaitingAndQueuesNothing()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        s.ForgetNotifications();

        interceptor.Failure = new DbUpdateException("the database refused");
        await Assert.ThrowsAsync<DbUpdateException>(async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token));
        interceptor.Failure = null;
        s.Db.ChangeTracker.Clear();

        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
        Assert.Empty(await ShowcaseRequestsAsync(s));
        Assert.Null(Assert.Single(await s.HoldsAsync()).ConsumedAt);

        // The customer follows the link again and it works.
        var confirmation = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);
        Assert.False(confirmation.AlreadyConfirmed);
    }

    [Fact]
    public async Task ConfirmEmail_AConcurrentChangeOfTheRequest_IsAConflict_NotAnError()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        interceptor.Failure = new DbUpdateConcurrencyException("changed meanwhile");

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token));

        Assert.Equal(ServiceRequestErrorCodes.StateChanged, ex.Code);
    }

    [Fact]
    public async Task Bookings_LogOnlyIdsAndCounts_NeverANameAnAddressAnEmailAPhoneOrAToken()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var logger = new CapturingLogger<ShowcaseBookingService>();
        var service = new ShowcaseBookingService(
            s.Db,
            s.Catalog,
            s.Agenda,
            s.Kit.Matcher,
            s.Kit.CustomerIndex,
            s.Kit.ShowcaseNotifier,
            Options.Create(s.Kit.ShowcaseOptions),
            logger,
            s.Clock);

        var clientRequestId = Guid.NewGuid();
        var input = await s.InputAsync(clientRequestId: clientRequestId);
        var hold = await service.CreateHoldAsync(supplier, input);
        var token = s.VerificationTokenOf(hold.Id);

        // Every path that logs: a replay of the booking, a slot already held, a wrong token, the check, and the check again.
        await service.CreateHoldAsync(supplier, input);
        await Assert.ThrowsAsync<DomainConflictException>(
            async () => await service.CreateHoldAsync(supplier, await s.InputAsync(email: "altra.persona@example.com")));
        await Assert.ThrowsAsync<NotFoundException>(async () => await service.ConfirmEmailAsync(supplier, hold.Id, "token-sbagliato"));
        await service.ConfirmEmailAsync(supplier, hold.Id, token);
        await service.ConfirmEmailAsync(supplier, hold.Id, token);

        Assert.NotEmpty(logger.Entries);
        var output = logger.AllOutput;
        foreach (var secret in new[]
                 {
                     "Mario", "Rossi", ShowcaseScenario.CustomerEmail, "altra.persona", "3331234567", "333 123", "Segretissima", "Piano 3",
                     "Citofono", ShowcaseScenario.ConsentIp, ShowcaseScenario.PostalCode, token, ShowcaseBookingTokens.Hash(token),
                     "token-sbagliato",
                 })
        {
            Assert.DoesNotContain(secret, output, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Task<List<ServiceRequest>> ShowcaseRequestsAsync(ServiceRequestScenario s) => s.ShowcaseRequestsAsync();
}
