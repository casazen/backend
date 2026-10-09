using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-11: the customer finds its booking again with the code and the e-mail address, and nobody finds anyone else's. One answer
/// (404 <c>supplier_booking_not_found</c>) for everything that does not identify a booking — the same code, the same message, and the
/// same statements run before it — the address compared after it was decrypted, and a view made only of what the customer may read:
/// never another customer's data, never what the supplier noted for itself, and — before the supplier took the request — never the
/// exact address.
/// </summary>
public class ShowcaseBookingManagerLookupTests
{
    // ─── The booking, as the customer sees it ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_ANewBooking_IsFoundWithTheCodeAndTheAddress_WithWhatTheCustomerBooked()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();

        var view = await s.Kit.Manager.LookupAsync(credentials);

        Assert.Equal(request.PublicCode, view.PublicCode);
        Assert.Equal(ServiceRequestStatus.Richiesto, view.Status);
        Assert.Equal(ServiceRequestScenario.ServiceName, view.Service.Name);
        Assert.Equal(await s.ListingSlugAsync(), view.Service.Slug);
        Assert.Equal(new ShowcaseBookingSupplierInfo("Supplier Srl", ShowcaseScenario.Slug), view.Supplier);
        Assert.Equal(ServiceRequestScenario.FridayAt10, view.StartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(ServiceRequestScenario.ServiceMinutes), view.EndUtc);
        Assert.Equal("Monza", view.Place.City);
        Assert.Equal("20900", view.Place.PostalCode);
        Assert.Equal((6000, (int?)null, (int?)null), (view.Price.EstimatedAmountCents, view.Price.QuotedAmountCents, view.Price.FinalAmountCents));
        Assert.Equal((6000, "estimate"), (view.Price.AmountCents, view.Price.Basis));
        // A flat price keeps no quantity among the choices of the booking: its base is the line of the service, so the lines add up.
        Assert.Equal([new ShowcaseBookingPriceLine("service", ServiceRequestScenario.ServiceName, 1, 6000, 6000)], view.Price.Lines);
        // The supplier has its 180 minutes from the moment the address was checked.
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddMinutes(180), view.RespondBy);
        Assert.Null(view.Proposal);
        Assert.Null(view.Cancellation);
        Assert.Null(view.RejectionReason);
        Assert.Equal(new ShowcaseBookingActions(CanCancel: true, CanReschedule: true, CanRespondToProposal: false), view.Actions);
    }

    [Fact]
    public async Task Lookup_TheTermsOfCancelling_NameTheFreeNoticeBeforeTheWork_AndSayWhetherItIsStillFree()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        // Friday 10:00 is 22 hours away at the clock of the scenario; Saturday 09:00 is 45 hours away.
        var (_, soon) = await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt10);
        var (_, later) = await s.BookedForManagementAsync(ServiceRequestScenario.SaturdayAt09, "anna.verdi@example.com");

        var soonView = await s.Kit.Manager.LookupAsync(soon);
        var laterView = await s.Kit.Manager.LookupAsync(later);

        Assert.Equal(ServiceRequestScenario.FridayAt10.AddHours(-24), soonView.CancellationTerms.FreeUntilUtc);
        Assert.False(soonView.CancellationTerms.IsFree);
        Assert.Equal(ServiceRequestScenario.SaturdayAt09.AddHours(-24), laterView.CancellationTerms.FreeUntilUtc);
        Assert.True(laterView.CancellationTerms.IsFree);
        // Not free any more is not the end of cancelling: a late cancellation costs nothing either in v1 (decision D6).
        Assert.True(soonView.Actions.CanCancel);
    }

    [Fact]
    public async Task Lookup_TheFreeNoticeIsConfiguration()
    {
        using var s = await ServiceRequestScenario.CreateAsync(
            showcaseOptions: new ShowcaseBookingOptions
            {
                PrivacyNoticeVersion = ServiceRequestTestKit.PrivacyNoticeVersion,
                FreeCancellationHours = 12,
            });
        await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync();

        var view = await s.Kit.Manager.LookupAsync(credentials);

        Assert.Equal(ServiceRequestScenario.FridayAt10.AddHours(-12), view.CancellationTerms.FreeUntilUtc);
        Assert.True(view.CancellationTerms.IsFree); // 22 hours away: more than 12
    }

    [Fact]
    public async Task Lookup_TheChoicesOfTheCustomer_AreTheLinesOfThePrice()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.SetSupplementsAsync(
            s.ListingId,
            new SupplierServiceSupplement("bagno", "Bagno in più", 1500, SupplierServiceSupplementUnits.Bathroom, 3),
            new SupplierServiceSupplement("pesanti", "Pulizie pesanti", 2000, SupplierServiceSupplementUnits.Flat, null));
        var input = await s.InputAsync(change: i => i with
        {
            Options = [new SupplierQuoteOption("bagno", 2), new SupplierQuoteOption("pesanti", null)],
        });
        var (_, confirmation) = await s.BookedAsync(input, supplier);

        var view = await s.Kit.Manager.LookupAsync(ShowcaseManageScenario.CredentialsOf(confirmation.PublicCode));

        Assert.Equal(6000 + 2 * 1500 + 2000, view.Price.EstimatedAmountCents);
        Assert.Equal(
            [
                new ShowcaseBookingPriceLine("service", ServiceRequestScenario.ServiceName, 1, 6000, 6000),
                new ShowcaseBookingPriceLine("option", "Bagno in più", 2, 1500, 3000),
                new ShowcaseBookingPriceLine("option", "Pulizie pesanti", 1, 2000, 2000),
            ],
            view.Price.Lines);
        // The lines of the estimate add up to it, whatever the unit of the price.
        Assert.Equal(view.Price.EstimatedAmountCents, view.Price.Lines.Sum(line => line.AmountCents));
    }

    [Fact]
    public async Task Lookup_APricePerHour_ShowsTheHoursAsTheLineOfTheService()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var hourly = await s.AddListingAsync("A ore", priceFromCents: 2500, priceUnit: SupplierServicePriceUnit.PerHour, durationMinutes: 180);
        var input = await s.InputAsync(service: await s.ListingSlugAsync(hourly), change: i => i with { Quantity = 3 });
        var (_, confirmation) = await s.BookedAsync(input, supplier);

        var view = await s.Kit.Manager.LookupAsync(ShowcaseManageScenario.CredentialsOf(confirmation.PublicCode));

        Assert.Equal(7500, view.Price.AmountCents);
        Assert.Equal([new ShowcaseBookingPriceLine("service", "A ore", 3, 2500, 7500)], view.Price.Lines);
        Assert.Equal(view.Price.EstimatedAmountCents, view.Price.Lines.Sum(line => line.AmountCents));
        Assert.Equal(TimeSpan.FromHours(3), view.EndUtc - view.StartUtc);
    }

    [Fact]
    public async Task Lookup_AServiceOnQuote_HasNoAmountToShow()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var onQuote = await s.AddListingAsync("Su preventivo", requiresQuote: true);
        var input = await s.InputAsync(service: await s.ListingSlugAsync(onQuote));
        var (_, confirmation) = await s.BookedAsync(input, supplier);

        var view = await s.Kit.Manager.LookupAsync(ShowcaseManageScenario.CredentialsOf(confirmation.PublicCode));

        Assert.Null(view.Price.AmountCents);
        Assert.Null(view.Price.Basis);
        Assert.Empty(view.Price.Lines);
    }

    // ─── The place: the exact address only once the supplier took the request ───────────────────────────────────────────

    [Fact]
    public async Task Lookup_BeforeTheTake_TheStreetTheFloorAndTheNotesAreNotThere_AfterTheTakeTheyAre()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();

        var before = await s.Kit.Manager.LookupAsync(credentials);
        await s.TakeAsSupplierAsync(request.Id, new TakeServiceRequestCommand(QuotedAmountCents: 7000));
        var after = await s.Kit.Manager.LookupAsync(credentials);

        Assert.Equal(new ShowcaseBookingPlace("Monza", "20900", null, null, null), before.Place);
        Assert.DoesNotContain(ShowcaseScenario.Address, JsonSerializer.Serialize(before));
        Assert.DoesNotContain(ShowcaseScenario.AccessNotes, JsonSerializer.Serialize(before));
        Assert.Equal(
            new ShowcaseBookingPlace("Monza", "20900", ShowcaseScenario.Address, ShowcaseScenario.Floor, ShowcaseScenario.AccessNotes),
            after.Place);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, after.Status);
        Assert.Equal((7000, "quote"), (after.Price.AmountCents, after.Price.Basis));
        Assert.Null(after.RespondBy);
    }

    [Fact]
    public async Task Lookup_ARefusedOrCancelledRequest_NeverShowsTheExactAddress()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (refused, refusedCredentials) = await s.BookedForManagementAsync();
        var (cancelled, cancelledCredentials) = await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt14, "anna.verdi@example.com");
        await s.Service.RejectAsync(refused.Id, s.SupplierOrgId, "Siamo in ferie");
        await s.Service.CancelAsSupplierAsync(cancelled.Id, s.SupplierOrgId, "Guasto al furgone");

        var refusedView = await s.Kit.Manager.LookupAsync(refusedCredentials);
        var cancelledView = await s.Kit.Manager.LookupAsync(cancelledCredentials);

        Assert.Equal(ServiceRequestStatus.Rifiutato, refusedView.Status);
        Assert.Equal("Siamo in ferie", refusedView.RejectionReason);
        Assert.Equal(ServiceRequestStatus.Annullato, cancelledView.Status);
        Assert.Equal(new ShowcaseBookingCancellation(s.Clock.GetUtcNow().UtcDateTime, ServiceRequestActorParty.Supplier, "Guasto al furgone"), cancelledView.Cancellation);
        foreach (var view in new[] { refusedView, cancelledView })
        {
            Assert.Null(view.Place.Address);
            Assert.Null(view.Place.Floor);
            Assert.Null(view.Place.AccessNotes);
            Assert.Equal(new ShowcaseBookingActions(false, false, false), view.Actions);
        }
    }

    [Fact]
    public async Task Lookup_ACancellationNobodyWrote_HasNoReason_WhoeverCancelled()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(181));
        await s.ExpiryJob().RunAsync();

        var view = await s.Kit.Manager.LookupAsync(credentials);

        // The code NoResponse is not a sentence and is not handed to the customer as one; the party says what happened.
        Assert.Equal(ServiceRequestStatus.Annullato, view.Status);
        Assert.Equal(ServiceRequestActorParty.System, view.Cancellation!.By);
        Assert.Null(view.Cancellation.Reason);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, (await s.ReadAsync(request.Id)).CancellationReason);
    }

    [Fact]
    public async Task Lookup_ACompletedRequest_ShowsTheFinalAmountAndItsLines_NotWhatTheSupplierNotedForItself()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.TakeAsSupplierAsync(request.Id, new TakeServiceRequestCommand(QuotedAmountCents: 6000));
        await s.Service.StartAsync(request.Id, s.SupplierOrgId);
        await s.Service.CompleteAsync(
            request.Id,
            s.SupplierOrgId,
            new CompleteServiceRequestCommand(
                "Citofono rotto: la signora ha aperto con le chiavi di scorta",
                null,
                [new ServiceRequestExtra("Un bagno in più", 1500)]));

        var view = await s.Kit.Manager.LookupAsync(credentials);

        Assert.Equal(ServiceRequestStatus.Completato, view.Status);
        Assert.Equal((7500, "final"), (view.Price.AmountCents, view.Price.Basis));
        Assert.Equal(
            [
                new ShowcaseBookingPriceLine("service", ServiceRequestScenario.ServiceName, 1, 6000, 6000),
                new ShowcaseBookingPriceLine("extra", "Un bagno in più", 1, 1500, 1500),
            ],
            view.Price.Lines);
        Assert.Equal(new ShowcaseBookingActions(false, false, false), view.Actions);
        // What the supplier wrote when it completed the work is for the supplier.
        Assert.DoesNotContain("Citofono rotto", JsonSerializer.Serialize(view));
        Assert.DoesNotContain("chiavi di scorta", JsonSerializer.Serialize(view));
    }

    [Fact]
    public async Task Lookup_TheViewCarriesNothingPersonalOfTheCustomer_NoNameNoAddressNoPhoneNoId()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.TakeAsSupplierAsync(request.Id);

        var json = JsonSerializer.Serialize(await s.Kit.Manager.LookupAsync(credentials));

        foreach (var secret in new[]
                 {
                     ShowcaseScenario.CustomerName, "Mario", ShowcaseScenario.CustomerEmail, "3331234567", "333 123",
                     ShowcaseScenario.ConsentIp, request.Id.ToString(), request.CustomerId!.Value.ToString(), request.SupplierOrgId.ToString(),
                 })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ─── Who is asking ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_EverythingThatDoesNotIdentifyABooking_IsTheSameAnswer_AndRunsTheSameStatements()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var (_, mine) = await s.BookedForManagementAsync();
        var (_, theirs) = await BookAtAsync(s, other);
        var spy = new CountingCustomerReader(s.Kit.Customers);
        var manager = new ShowcaseBookingManager(
            s.Db,
            spy,
            s.Catalog,
            s.Service,
            Microsoft.Extensions.Options.Options.Create(s.Kit.ShowcaseOptions),
            s.Clock);

        var attempts = new Dictionary<string, ShowcaseBookingCredentials>
        {
            ["a code that does not exist"] = mine with { Code = "7K2XM-9QD4T" },
            ["a code that is not a code"] = mine with { Code = "non-un-codice" },
            ["no code"] = mine with { Code = null },
            ["another address"] = mine with { Email = "un.altro@example.com" },
            ["no address"] = mine with { Email = "  " },
            ["a supplier that does not exist"] = mine with { Slug = "non-esiste" },
            ["no supplier"] = mine with { Slug = null },
            ["a slug that cannot be one"] = mine with { Slug = "vetrina\0test" },
            ["the code of another supplier's booking"] = theirs with { Slug = mine.Slug },
            ["the slug of another supplier"] = mine with { Slug = theirs.Slug },
            ["nothing at all"] = new(null, null, null),
        };

        var answers = new List<(string Code, string? Key, string Message)>();
        foreach (var (name, credentials) in attempts)
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(() => manager.LookupAsync(credentials));
            answers.Add((refused.Code!, refused.MessageKey, refused.Message));
            Assert.True(refused.Code == ShowcaseBookingManagementErrors.NotFound, name);
        }

        // One answer for all of them: the same code, the same key, the same message.
        Assert.Single(answers.Distinct());
        Assert.Equal(("supplier_booking_not_found", "SupplierBookingNotFound"), (answers[0].Code, answers[0].Key));
        // And the same work for all of them: the customer's contact is read for every attempt, found or not, valid or not.
        Assert.Equal(attempts.Count, spy.Calls);
        // The right credentials do find the booking, through the same manager.
        Assert.Equal(ServiceRequestStatus.Richiesto, (await manager.LookupAsync(mine)).Status);
        Assert.Equal(attempts.Count + 1, spy.Calls);
    }

    [Theory]
    [InlineData("MARIO.ROSSI@EXAMPLE.COM")]
    [InlineData("  mario.rossi@example.com  ")]
    [InlineData("Mario.Rossi@Example.com")]
    public async Task Lookup_TheAddress_IsComparedWithoutCaseAndWithoutTheSpacesAroundIt(string typed)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync();

        var view = await s.Kit.Manager.LookupAsync(credentials with { Email = typed });

        Assert.Equal(ServiceRequestStatus.Richiesto, view.Status);
    }

    [Theory]
    [InlineData("mario.rossi@example.co")]
    [InlineData("mario.rossi@example.com.")]
    [InlineData("mario.rossi")]
    [InlineData("rio.rossi@example.com")]
    [InlineData("mario.rossi+prova@example.com")]
    [InlineData("mario.rossi@example.com,altro@example.com")]
    public async Task Lookup_AnAddressThatIsNotTheBookings_IsNotFound_NotEvenWhenItIsAPartOfIt(string typed)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => s.Kit.Manager.LookupAsync(credentials with { Email = typed }));
    }

    [Fact]
    public async Task Lookup_TheCode_IsReadTheWayPeopleWriteIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        var plain = request.PublicCode!;
        var forms = new[]
        {
            plain,
            plain.ToLowerInvariant(),
            $"  {plain}  ",
            $"{plain[..5]} {plain[5..]}",
            $"{plain[..5]}-{plain[5..]}".ToLowerInvariant(),
            $"{plain[..5]}–{plain[5..]}",
        };

        foreach (var form in forms)
        {
            var view = await s.Kit.Manager.LookupAsync(credentials with { Code = form });
            Assert.Equal(plain, view.PublicCode);
        }
    }

    [Fact]
    public async Task Lookup_TwoCustomersOfTheSameSupplier_NeverFindEachOthersBooking()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (_, mario) = await s.BookedForManagementAsync();
        var (_, anna) = await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt14, "anna.verdi@example.com");

        var marioView = await s.Kit.Manager.LookupAsync(mario);
        var annaView = await s.Kit.Manager.LookupAsync(anna);

        Assert.Equal(ServiceRequestScenario.FridayAt10, marioView.StartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt14, annaView.StartUtc);
        Assert.NotEqual(marioView.PublicCode, annaView.PublicCode);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Kit.Manager.LookupAsync(mario with { Email = "anna.verdi@example.com" }));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Kit.Manager.LookupAsync(anna with { Email = ShowcaseScenario.CustomerEmail }));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Kit.Manager.LookupAsync(anna with { Code = mario.Code }));
    }

    [Fact]
    public async Task Lookup_TheSameAddressAtTwoSuppliers_FindsEachBookingOnlyAtItsOwnSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var (_, mine) = await s.BookedForManagementAsync();
        var (_, theirs) = await BookAtAsync(s, other);

        var mineView = await s.Kit.Manager.LookupAsync(mine);
        var theirsView = await s.Kit.Manager.LookupAsync(theirs);

        Assert.Equal(ShowcaseScenario.Slug, mineView.Supplier.Slug);
        Assert.Equal("altra-vetrina", theirsView.Supplier.Slug);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Kit.Manager.LookupAsync(mine with { Slug = "altra-vetrina" }));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Kit.Manager.LookupAsync(theirs with { Slug = ShowcaseScenario.Slug }));
    }

    [Fact]
    public async Task Lookup_ACustomerTheRetentionAnonymized_IsNotFound_WhateverIsTyped()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        var customer = await s.Db.ServiceCustomers.SingleAsync(c => c.Id == request.CustomerId);
        customer.FullName = ServiceCustomerPrivacyService.AnonymizedValue;
        customer.Email = ServiceCustomerPrivacyService.AnonymizedEmail(customer.Id);
        customer.EmailHash = ServiceCustomerPrivacyService.AnonymizedEmailHash(customer.Id);
        customer.AnonymizedAt = s.Clock.GetUtcNow().UtcDateTime;
        await s.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => s.Kit.Manager.LookupAsync(credentials));
        await Assert.ThrowsAsync<NotFoundException>(
            () => s.Kit.Manager.LookupAsync(credentials with { Email = ServiceCustomerPrivacyService.AnonymizedEmail(customer.Id) }));
    }

    // ─── The supplier that is not active any more ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_ASuspendedSupplier_DoesNotTakeTheBookingAwayFromTheCustomer_ButItCanOnlyBeCancelled()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        var view = await s.Kit.Manager.LookupAsync(credentials);

        Assert.Equal(ServiceRequestStatus.Richiesto, view.Status);
        Assert.NotNull(view.Proposal);
        Assert.Equal(new ShowcaseBookingActions(CanCancel: true, CanReschedule: false, CanRespondToProposal: false), view.Actions);
    }

    [Fact]
    public async Task Lookup_AServiceThatIsNotPublishedAnymore_HasNoSlugToAskTheSlotsWith_AndTheTimeCannotBeMoved()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync();
        var listing = await s.Db.SupplierServiceListings.SingleAsync(l => l.Id == s.ListingId);
        listing.Status = SupplierServiceListingStatus.Paused;
        await s.Db.SaveChangesAsync();

        var view = await s.Kit.Manager.LookupAsync(credentials);

        Assert.Equal(ServiceRequestScenario.ServiceName, view.Service.Name);
        Assert.Null(view.Service.Slug);
        Assert.False(view.Actions.CanReschedule);
        Assert.True(view.Actions.CanCancel);
    }

    // ─── The proposal ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_AProposalThatWaits_IsInTheView_WithItsDeadlineAndMessage_AndTheSupplierDeadlineIsSpent()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(30));
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14, "Il mattino sono già impegnato");

        var view = await s.Kit.Manager.LookupAsync(credentials);

        var proposal = Assert.IsType<ShowcaseBookingProposal>(view.Proposal);
        Assert.Equal(ServiceRequestScenario.FridayAt14, proposal.StartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt14.AddMinutes(ServiceRequestScenario.ServiceMinutes), proposal.EndUtc);
        Assert.Equal("Il mattino sono già impegnato", proposal.Message);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, proposal.ProposedAt);
        // The customer has a day to answer.
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddHours(24), proposal.AnswerBy);
        // The request keeps the time it was asked for until the customer accepts, and it is not waiting for the supplier anymore.
        Assert.Equal(ServiceRequestScenario.FridayAt10, view.StartUtc);
        Assert.Null(view.RespondBy);
        Assert.Equal(new ShowcaseBookingActions(CanCancel: true, CanReschedule: true, CanRespondToProposal: true), view.Actions);
    }

    [Fact]
    public async Task Lookup_AProposalWhoseDeadlineHasPassed_CanNoLongerBeAnswered()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
        s.Clock.Advance(TimeSpan.FromHours(24));

        var view = await s.Kit.Manager.LookupAsync(credentials);

        Assert.NotNull(view.Proposal);
        Assert.False(view.Actions.CanRespondToProposal);
        // Until the upkeep job cancels the request the customer can still cancel it, or move it.
        Assert.True(view.Actions.CanCancel);
    }

    // ─── Where the SQL looks ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_NeverWritesAnything()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        var before = await s.ReadAsync(request.Id);
        s.ForgetNotifications();

        await s.Kit.Manager.LookupAsync(credentials);
        await s.Kit.Manager.LookupAsync(credentials);

        var after = await s.ReadAsync(request.Id);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Status, after.Status);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
        Assert.False(s.Db.ChangeTracker.HasChanges());
    }

    // ─── helpers ───

    /// <summary>A booking at the other supplier of <see cref="ShowcaseScenario.AddBookableSupplierAsync"/>, by the same customer.</summary>
    private static async Task<(ServiceRequest Request, ShowcaseBookingCredentials Credentials)> BookAtAsync(
        ServiceRequestScenario s,
        (Guid OrgId, SupplierProfile Profile, string ServiceSlug) other)
    {
        var input = await s.InputAsync(service: other.ServiceSlug);
        var (request, confirmation) = await s.BookedAsync(input, other.Profile);
        return (request, ShowcaseManageScenario.CredentialsOf(confirmation.PublicCode, ShowcaseScenario.CustomerEmail, other.Profile.ShowcaseSlug!));
    }

    private sealed class CountingCustomerReader(IServiceCustomerReader inner) : IServiceCustomerReader
    {
        public int Calls { get; private set; }

        public Task<ServiceCustomerContact?> FindContactAsync(Guid supplierOrgId, Guid customerId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.FindContactAsync(supplierOrgId, customerId, cancellationToken);
        }
    }
}
