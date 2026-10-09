using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Http = Casazen.Tests.Integration.SupplierBookingManageHttp;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-11 on PostgreSQL, the part that cannot be shown in memory: the customer and the supplier acting on one request at the same
/// moment, and the customers of one supplier acting on its calendar at the same moment. Every change of a request is saved only if
/// nobody changed it since it was read (<c>xmin</c>), and a change of a time is judged by the slot planner under the supplier's calendar
/// lock, after taking it: of two operations one wins, the other gets 409 and leaves everything as it was, only the winner tells anybody,
/// and none ends in a 500. The two sides of the race that both save a request are held before saving until both got there
/// (<see cref="SaveRendezvous"/>, one instance for the host), so the race really happens.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ShowcaseBookingManagementPostgresTests(PublicBookingFactory factory) : IClassFixture<PublicBookingFactory>
{
    // ─── The customer and the supplier at the same moment ────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Cancel_AndTheSupplierTaking_AtTheSameMoment_OneWins_AndOnlyTheWinnerIsTold()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var setup = factory.CreateClient();
        var booked = await Http.BookAsync(factory, setup, supplier, PublicBookingTestData.TuesdayAt9);
        var customerMails = factory.EmailsTo(booked.Email).Count;
        var supplierMails = factory.EmailsTo(supplier.ContactEmail).Count;

        await using var app = HoldingTwoSaves();
        using var customer = app.CreateClient();
        using var asSupplier = AppClient(app, supplier);
        var cancelTask = Task.Run(() => Http.PostAsync(customer, "cancel", booked.Access(new { reason = "Cambio programma" })));
        var takeTask = Task.Run(() => asSupplier.PostAsync($"/api/service-requests/{booked.RequestId}/take", content: null));

        var cancel = await cancelTask;
        var take = await takeTask;

        AssertOneWinsOneConflicts(cancel, take);
        var stored = await LoadRequestAsync(booked.RequestId);
        if (cancel.StatusCode == HttpStatusCode.OK)
        {
            // The customer was first: the supplier's take got 409 and nothing of it was saved or told.
            Assert.Equal(ServiceRequestStatus.Annullato, stored.Status);
            Assert.Equal(ServiceRequestActorParty.Customer, stored.CancelledBy);
            Assert.Null(stored.TakenAt);
            Assert.Single(factory.EmailsTo(booked.Email).Skip(customerMails), e => e.Template == EmailTemplates.Names.SupplierBookingCancellationReceipt);
            Assert.DoesNotContain(factory.EmailsTo(booked.Email), e => e.Template == EmailTemplates.Names.SupplierBookingAccepted);
            Assert.Single(factory.EmailsTo(supplier.ContactEmail).Skip(supplierMails), e => e.Template == EmailTemplates.Names.SupplierBookingCancelledByCustomer);
        }
        else
        {
            // The supplier was first: the cancellation got 409, the booking is taken, and the customer was told it was.
            Assert.Equal(ServiceRequestStatus.PresoInCarico, stored.Status);
            Assert.Null(stored.CancelledBy);
            Assert.Single(factory.EmailsTo(booked.Email).Skip(customerMails), e => e.Template == EmailTemplates.Names.SupplierBookingAccepted);
            Assert.DoesNotContain(factory.EmailsTo(booked.Email), e => e.Template == EmailTemplates.Names.SupplierBookingCancellationReceipt);
            Assert.DoesNotContain(factory.EmailsTo(supplier.ContactEmail), e => e.Template == EmailTemplates.Names.SupplierBookingCancelledByCustomer);
        }
    }

    [PostgresFact]
    public async Task Cancel_AndTheSupplierRefusing_AtTheSameMoment_OneWins_NeverA500()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var setup = factory.CreateClient();
        var booked = await Http.BookAsync(factory, setup, supplier, PublicBookingTestData.TuesdayAt9);

        await using var app = HoldingTwoSaves();
        using var customer = app.CreateClient();
        using var asSupplier = AppClient(app, supplier);
        var cancelTask = Task.Run(() => Http.PostAsync(customer, "cancel", booked.Access()));
        var rejectTask = Task.Run(() => asSupplier.PostAsJsonAsync($"/api/service-requests/{booked.RequestId}/reject", new { reason = "Siamo in ferie" }));

        var cancel = await cancelTask;
        var reject = await rejectTask;

        AssertOneWinsOneConflicts(cancel, reject);
        var stored = await LoadRequestAsync(booked.RequestId);
        Assert.Equal(
            cancel.StatusCode == HttpStatusCode.OK ? ServiceRequestStatus.Annullato : ServiceRequestStatus.Rifiutato,
            stored.Status);
        // The slot of a request that is closed either way is free again.
        var next = await setup.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings",
            PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail()));
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
    }

    [PostgresFact]
    public async Task Reschedule_AndTheSupplierTaking_AtTheSameMoment_OneWins_AndTheRequestIsWhollyOneOrTheOther()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var setup = factory.CreateClient();
        var booked = await Http.BookAsync(factory, setup, supplier, PublicBookingTestData.TuesdayAt9);
        var newStart = PublicBookingTestData.TuesdayAt9.AddDays(1);

        await using var app = HoldingTwoSaves();
        using var customer = app.CreateClient();
        using var asSupplier = AppClient(app, supplier);
        var moveTask = Task.Run(() => Http.PostAsync(customer, "reschedule", booked.Access(new { startUtc = newStart })));
        var takeTask = Task.Run(() => asSupplier.PostAsync($"/api/service-requests/{booked.RequestId}/take", content: null));

        var move = await moveTask;
        var take = await takeTask;

        AssertOneWinsOneConflicts(move, take);
        var stored = await LoadRequestAsync(booked.RequestId);
        if (move.StatusCode == HttpStatusCode.OK)
        {
            // Moved first: the request is still new, at the new time; the take got 409.
            Assert.Equal(ServiceRequestStatus.Richiesto, stored.Status);
            Assert.Equal(newStart, stored.ScheduledStartUtc);
            Assert.Null(stored.TakenAt);
        }
        else
        {
            // Taken first: the request is taken at the time it was asked for, and the move got 409 and changed nothing.
            Assert.Equal(ServiceRequestStatus.PresoInCarico, stored.Status);
            Assert.Equal(PublicBookingTestData.TuesdayAt9, stored.ScheduledStartUtc);
            Assert.DoesNotContain(factory.EmailsTo(supplier.ContactEmail), e => e.Template == EmailTemplates.Names.SupplierBookingRescheduledByCustomer);
        }

        // Whichever time the request ended up with, the other one is free: a new customer can book it.
        var freed = move.StatusCode == HttpStatusCode.OK ? PublicBookingTestData.TuesdayAt9 : newStart;
        var next = await setup.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings",
            PublicBookingTestData.Body(supplier.Service, freed, PublicBookingTestData.NewEmail()));
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
    }

    [PostgresFact]
    public async Task AcceptingTheProposal_AndTheSupplierCancelling_AtTheSameMoment_OneWins_NeverA500()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var setup = factory.CreateClient();
        var booked = await Http.BookAsync(factory, setup, supplier, PublicBookingTestData.TuesdayAt9);
        await Http.ProposeAsync(factory, supplier, booked, PublicBookingTestData.TuesdayAt9.AddDays(1), "Domani va meglio");

        await using var app = HoldingTwoSaves();
        using var customer = app.CreateClient();
        using var asSupplier = AppClient(app, supplier);
        var acceptTask = Task.Run(() => Http.PostAsync(customer, "proposal/accept", booked.Access()));
        var cancelTask = Task.Run(() => asSupplier.PostAsJsonAsync($"/api/service-requests/{booked.RequestId}/cancel", new { reason = "Guasto al furgone" }));

        var accept = await acceptTask;
        var cancel = await cancelTask;

        AssertOneWinsOneConflicts(accept, cancel);
        var stored = await LoadRequestAsync(booked.RequestId);
        Assert.Equal(
            accept.StatusCode == HttpStatusCode.OK ? ServiceRequestStatus.PresoInCarico : ServiceRequestStatus.Annullato,
            stored.Status);
        Assert.Null(stored.ProposedStartUtc);
    }

    // ─── The customers of one supplier at the same moment ────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Reschedule_TwoRequestsToTheSameSlot_AtTheSameMoment_OneGetsItAndTheOtherIs409()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var first = await Http.BookAsync(factory, client, supplier, PublicBookingTestData.TuesdayAt9);
        var second = await Http.BookAsync(factory, client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(1));
        var target = PublicBookingTestData.TuesdayAt9.AddDays(2);

        var responses = await InParallelAsync(
        [
            () => Http.PostAsync(client, "reschedule", first.Access(new { startUtc = target })),
            () => Http.PostAsync(client, "reschedule", second.Access(new { startUtc = target })),
        ]);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var lost = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
        Assert.Equal("supplier_slot_unavailable", (await lost.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var starts = new[] { (await LoadRequestAsync(first.RequestId)).ScheduledStartUtc, (await LoadRequestAsync(second.RequestId)).ScheduledStartUtc };
        Assert.Equal(1, starts.Count(start => start == target));
        // The loser stays where it was, and what the winner left is free.
        Assert.Contains(starts[0], new DateTime?[] { PublicBookingTestData.TuesdayAt9, target });
        Assert.Contains(starts[1], new DateTime?[] { PublicBookingTestData.TuesdayAt9.AddDays(1), target });
    }

    [PostgresFact]
    public async Task Reschedule_TheSameRequestTwice_AtTheSameMoment_ToTwoSlots_NeverA500_AndOnlyOneSlotIsKept()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await Http.BookAsync(factory, client, supplier, PublicBookingTestData.TuesdayAt9);
        var one = PublicBookingTestData.TuesdayAt9.AddDays(1);
        var two = PublicBookingTestData.TuesdayAt9.AddDays(2);

        var responses = await InParallelAsync(
        [
            () => Http.PostAsync(client, "reschedule", booked.Access(new { startUtc = one })),
            () => Http.PostAsync(client, "reschedule", booked.Access(new { startUtc = two })),
        ]);

        // The lock puts them one after the other: both can succeed (the second moves what the first moved), or the second can find
        // the request changed under it (409); neither can be a 500.
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)r.StatusCode}"));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        var stored = await LoadRequestAsync(booked.RequestId);
        Assert.Contains(stored.ScheduledStartUtc, new DateTime?[] { one, two });
        Assert.Equal(ServiceRequestStatus.Richiesto, stored.Status);

        // Only the slot it ended in is held: the old one and the other target are free.
        foreach (var free in new[] { PublicBookingTestData.TuesdayAt9, one, two }.Where(start => start != stored.ScheduledStartUtc))
        {
            var next = await client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings",
                PublicBookingTestData.Body(supplier.Service, free, PublicBookingTestData.NewEmail()));
            Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        }
    }

    [PostgresFact]
    public async Task AcceptingTheProposal_AndAnotherCustomerBookingThatSlot_AtTheSameMoment_OneGetsIt()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await Http.BookAsync(factory, client, supplier, PublicBookingTestData.TuesdayAt9);
        var proposed = PublicBookingTestData.TuesdayAt9.AddDays(1);
        await Http.ProposeAsync(factory, supplier, booked, proposed, null);
        var otherEmail = PublicBookingTestData.NewEmail();

        var responses = await InParallelAsync(
        [
            () => Http.PostAsync(client, "proposal/accept", booked.Access()),
            () => client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, proposed, otherEmail)),
        ]);

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        var accept = responses[0];
        var other = responses[1];
        var stored = await LoadRequestAsync(booked.RequestId);
        if (accept.StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
            Assert.Equal(proposed, stored.ScheduledStartUtc);
            Assert.Equal(ServiceRequestStatus.PresoInCarico, stored.Status);
            Assert.Empty(factory.EmailsTo(otherEmail));
        }
        else
        {
            // The other customer was first: the proposal stays, and the customer can still turn it down or cancel.
            Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
            Assert.Equal("supplier_slot_unavailable", (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
            Assert.Equal(ServiceRequestStatus.Richiesto, stored.Status);
            Assert.Equal(proposed, stored.ProposedStartUtc);
            Assert.Equal(PublicBookingTestData.TuesdayAt9, stored.ScheduledStartUtc);
        }
    }

    [PostgresFact]
    public async Task Cancel_TheSlotIsFreeAgainAtOnce_AndSeveralCustomersRushingForItGetOneBooking()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await Http.BookAsync(factory, client, supplier, PublicBookingTestData.TuesdayAt9);
        await Http.PostAsync(client, "cancel", booked.Access());

        var responses = await InParallelAsync(
            Enumerable.Range(0, 4).Select<int, Func<Task<HttpResponseMessage>>>(_ => () => client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings",
                PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail()))).ToArray());

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
    }

    [PostgresFact]
    public async Task Cancel_TwoCancellationsOfTheSameBooking_AtTheSameMoment_BothAnswerTheBooking_AndOneReceiptIsSent()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await Http.BookAsync(factory, client, supplier, PublicBookingTestData.TuesdayAt9);
        var mailsBefore = factory.EmailsTo(booked.Email).Count;

        var responses = await InParallelAsync(
        [
            () => Http.PostAsync(client, "cancel", booked.Access(new { reason = "Cambio programma" })),
            () => Http.PostAsync(client, "cancel", booked.Access(new { reason = "Cambio programma" })),
        ]);

        // The lock puts them one after the other: the second finds it cancelled by the customer and answers the same booking.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(factory.EmailsTo(booked.Email).Skip(mailsBefore), e => e.Template == EmailTemplates.Names.SupplierBookingCancellationReceipt);
        Assert.Equal(1, factory.EmailsTo(supplier.ContactEmail).Count(e => e.Template == EmailTemplates.Names.SupplierBookingCancelledByCustomer));
    }

    // ─── Isolation, encryption and time ──────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task TwoSuppliers_TheSameAddressBooksBoth_EachFindsItsOwnBookingOnly_AndCancellingOneLeavesTheOther()
    {
        var mine = await PublicBookingTestData.SeedAsync(factory);
        var theirs = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var email = PublicBookingTestData.NewEmail();
        var atMine = await Http.BookAsync(factory, client, mine, PublicBookingTestData.TuesdayAt9, email: email);
        var atTheirs = await Http.BookAsync(factory, client, theirs, PublicBookingTestData.TuesdayAt9, email: email);

        // The code of one with the slug of the other is nothing, even with the right address.
        var crossed = await Http.PostAsync(client, "lookup", new { slug = theirs.Slug, code = atMine.Code, email });
        var crossedCancel = await Http.PostAsync(client, "cancel", new { slug = mine.Slug, code = atTheirs.Code, email });
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossedCancel.StatusCode);

        var cancelled = await Http.PostAsync(client, "cancel", atMine.Access());
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var mineView = await (await Http.PostAsync(client, "lookup", atMine.Access())).Content.ReadFromJsonAsync<JsonElement>();
        var theirsView = await (await Http.PostAsync(client, "lookup", atTheirs.Access())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Annullato", mineView.GetProperty("status").GetString());
        Assert.Equal("Richiesto", theirsView.GetProperty("status").GetString());
        Assert.Equal(theirs.Slug, theirsView.GetProperty("supplier").GetProperty("slug").GetString());
        Assert.Equal(ServiceRequestStatus.Richiesto, (await LoadRequestAsync(atTheirs.RequestId)).Status);
        // Two customers, one per supplier: the data belongs to the supplier it was given to.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.ServiceCustomers.CountAsync(c => c.OrgId == mine.OrgId));
        Assert.Equal(1, await db.ServiceCustomers.CountAsync(c => c.OrgId == theirs.OrgId));
    }

    [PostgresFact]
    public async Task Lookup_TheAddressIsStoredEncrypted_AndTheBookingIsFoundByComparingItAfterTheDecryption()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await Http.BookAsync(factory, client, supplier, PublicBookingTestData.TuesdayAt9);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orgId = supplier.OrgId;
        var stored = await db.Database
            .SqlQuery<string>($"SELECT \"Email\" AS \"Value\" FROM \"ServiceCustomers\" WHERE \"OrgId\" = {orgId}")
            .ToListAsync();
        var storedAddress = Assert.Single(stored);
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, storedAddress, StringComparison.Ordinal);
        Assert.DoesNotContain(booked.Email, storedAddress, StringComparison.OrdinalIgnoreCase);

        var found = await Http.PostAsync(client, "lookup", booked.Access());
        var wrong = await Http.PostAsync(client, "lookup", booked.Access(new { email = "un.altro@example.com" }));

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
    }

    [PostgresFact]
    public async Task Cancel_TheInstantsAreKeptAtTheMicrosecond_WhatIsAnsweredIsWhatIsStored()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await Http.BookAsync(factory, client, supplier, PublicBookingTestData.TuesdayAt9);
        var original = factory.Clock.GetUtcNow();
        // A clock with ticks beyond the microsecond, which PostgreSQL cannot keep.
        factory.Clock.SetUtcNow(original.AddTicks(7));
        try
        {
            var response = await Http.PostAsync(client, "cancel", booked.Access());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var at = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancellation").GetProperty("at").GetDateTime().ToUniversalTime();
            var stored = (await LoadRequestAsync(booked.RequestId)).CancelledAt;
            Assert.Equal(UtcDateTime.TruncateToMicroseconds(factory.Clock.GetUtcNow().UtcDateTime), at);
            Assert.Equal(stored, at);
        }
        finally
        {
            factory.Clock.SetUtcNow(original);
        }
    }

    [PostgresFact]
    public async Task Reschedule_TheNewTimeIsKeptAtTheMicrosecond_AndTheDeadlineOfTheSupplierIsTheOneAnswered()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await Http.BookAsync(factory, client, supplier, PublicBookingTestData.TuesdayAt9);
        var original = factory.Clock.GetUtcNow();
        factory.Clock.SetUtcNow(original.AddTicks(7));
        try
        {
            var response = await Http.PostAsync(client, "reschedule", booked.Access(new { startUtc = PublicBookingTestData.TuesdayAt9.AddDays(1) }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var respondBy = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("respondBy").GetDateTime().ToUniversalTime();
            Assert.Equal((await LoadRequestAsync(booked.RequestId)).ResponseDueAt, respondBy);
            Assert.Equal(0, respondBy.Ticks % 10);
        }
        finally
        {
            factory.Clock.SetUtcNow(original);
        }
    }

    // ─── helpers ───

    /// <summary>The host of the test with every save of a service request held until two of them have arrived: the race made deterministic.</summary>
    private WebApplicationFactory<Program> HoldingTwoSaves() =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => SaveRendezvous.HoldParallelSaves(services, parties: 2)));

    /// <summary>One of the two answers is 200, the other 409, and none is a server error.</summary>
    private static void AssertOneWinsOneConflicts(HttpResponseMessage first, HttpResponseMessage second)
    {
        Assert.DoesNotContain(new[] { first, second }, r => (int)r.StatusCode >= 500);
        var statuses = new[] { first.StatusCode, second.StatusCode }.Order().ToArray();
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, statuses);
    }

    /// <summary>Runs every call at the same moment (they all wait at the gate), and returns the answers in order.</summary>
    private static async Task<HttpResponseMessage[]> InParallelAsync(IReadOnlyList<Func<Task<HttpResponseMessage>>> calls)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(async call =>
        {
            await gate.Task;
            return await call();
        }).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private async Task<ServiceRequest> LoadRequestAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static HttpClient AppClient(WebApplicationFactory<Program> app, BookableSupplier supplier)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", supplier.UserId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Supplier");
        return client;
    }
}

/// <summary>
/// SP-11 on PostgreSQL, the check of the row version against the upkeep: the job that cancels a showcase request nobody answered
/// and the customer who cancels it at the same moment. Both read the request as new and are held before saving until both got there;
/// the database lets one in (<c>xmin</c>) and the other leaves the request alone — a conflict, never a double change, never a 500, and
/// only the winner tells anybody. A class of its own with a database of its own: no other request is overdue, so the two saves of the
/// rendezvous are these two.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ShowcaseBookingManagementUpkeepRacePostgresTests(PublicBookingFactory factory) : IClassFixture<PublicBookingFactory>
{
    [PostgresFact]
    public async Task TheUpkeepAndTheCustomerCancelling_AtTheSameMoment_OneWinsAndTheOtherLeavesTheRequestAlone()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var setup = factory.CreateClient();
        var booked = await Http.BookAsync(factory, setup, supplier, PublicBookingTestData.TuesdayAt9);
        var customerMails = factory.EmailsTo(booked.Email).Count;

        using var moved = factory.MoveClock(TimeSpan.FromMinutes(181));
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            SaveRendezvous.HoldParallelSaves(services, parties: 2)));
        await using var scope = app.Services.CreateAsyncScope();
        var expiry = scope.ServiceProvider.GetRequiredService<IServiceRequestExpiryService>();
        using var customer = app.CreateClient();

        var runTask = expiry.RunAsync();
        var cancelTask = Task.Run(() => Http.PostAsync(customer, "cancel", booked.Access(new { reason = "Cambio programma" })));

        var run = await runTask;
        var cancel = await cancelTask;

        Assert.True((int)cancel.StatusCode < 500, $"{(int)cancel.StatusCode}");
        var stored = await LoadRequestAsync(booked.RequestId);
        var mails = factory.EmailsTo(booked.Email).Skip(customerMails).ToList();
        if (cancel.StatusCode == HttpStatusCode.OK)
        {
            // The customer was first: the upkeep counted a conflict and left the cancellation as it is.
            Assert.Equal(ServiceRequestStatus.Annullato, stored.Status);
            Assert.Equal(ServiceRequestActorParty.Customer, stored.CancelledBy);
            Assert.True(run.Conflicts >= 1, $"Conflicts {run.Conflicts}");
            Assert.Single(mails, e => e.Template == EmailTemplates.Names.SupplierBookingCancellationReceipt);
            Assert.DoesNotContain(mails, e => e.Template == EmailTemplates.Names.SupplierBookingExpired);
        }
        else
        {
            // The upkeep was first: the cancellation got 409 and nothing of it was saved or told.
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
            Assert.Equal(ServiceRequestStatus.Annullato, stored.Status);
            Assert.Equal(ServiceRequestActorParty.System, stored.CancelledBy);
            Assert.True(run.RequestsCancelled >= 1, $"Cancelled {run.RequestsCancelled}");
            Assert.Single(mails, e => e.Template == EmailTemplates.Names.SupplierBookingExpired);
            Assert.DoesNotContain(mails, e => e.Template == EmailTemplates.Names.SupplierBookingCancellationReceipt);
        }
    }

    private async Task<ServiceRequest> LoadRequestAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
    }
}
