using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-10 over the real pipeline on PostgreSQL: concurrent transitions (A4-19, <c>xmin</c>), validation of the bodies
/// (A4-18, 400 <c>validation_error</c> with localized messages) and typed errors (A4-17, 404 / 422 / 409, never 500).
/// </summary>
public class ServiceRequestTransitionsPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Host = "PropertyOwner";
    private const string Supplier = "Supplier";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CasazenWebApplicationFactory _factory;

    public ServiceRequestTransitionsPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    // ─── A4-19: concurrency ───

    [PostgresFact]
    public async Task TakeAndReject_InParallel_OneWinsTheOtherGets409AndOnlyTheWinnerNotifiesTheHost()
    {
        var w = await SeedWorldAsync();
        var id = await CreateRequestAsync(w);

        // Both requests read the request as "Richiesto" and are held before saving until both got there: the race of
        // two supplier members (or two tabs) clicking "take" and "reject" together, made deterministic.
        var rendezvous = new TransitionRendezvous(parties: 2);
        var emails = new RecordingEmailQueue();
        var pushes = new RecordingPushNotifications();
        await using var app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(rendezvous));
            services.RemoveAll<IEmailQueue>();
            services.AddSingleton<IEmailQueue>(emails);
            services.RemoveAll<IPushNotificationService>();
            services.AddSingleton<IPushNotificationService>(pushes);
        }));
        using var member1 = CreateClient(app, w.SupplierUserId, Supplier);
        using var member2 = CreateClient(app, w.SecondSupplierUserId, Supplier);

        var responses = await Task.WhenAll(
            member1.PostAsJsonAsync($"/api/service-requests/{id}/take", new { }),
            member2.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "Non disponibile" }));

        var take = responses[0];
        var reject = responses[1];
        var statuses = responses.Select(r => r.StatusCode).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, statuses);
        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        await AssertProblemAsync(loser, HttpStatusCode.Conflict, ServiceRequestErrorCodes.StateChanged);

        var takeWon = take.StatusCode == HttpStatusCode.OK;
        var stored = await ReadAsync(id);
        Assert.Equal(takeWon ? ServiceRequestStatus.PresoInCarico : ServiceRequestStatus.Rifiutato, stored.Status);
        // Nothing of the losing transition was saved.
        Assert.Equal(takeWon, stored.TakenAt is not null);
        Assert.Equal(takeWon, stored.RejectionReason is null);
        Assert.Equal(takeWon ? "PresoInCarico" : "Rifiutato", (await ReadJsonAsync(takeWon ? take : reject)).GetProperty("status").GetString());

        // One host email and one host push, for the winner's status (MO-04: a rejection is pushed too, A6-08).
        var hostEmail = Assert.Single(emails.Snapshot());
        Assert.Equal(w.HostContactEmail, hostEmail.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestStatusChanged, hostEmail.Template);
        var push = Assert.Single(pushes.Sent);
        Assert.Equal(id, push.Id);
        Assert.Equal(takeWon ? PushTypes.ServiceRequestTaken : PushTypes.ServiceRequestRejected, push.Type);
    }

    [PostgresFact]
    public async Task Take_TwoMembersInParallel_OneWinsTheOtherGets409()
    {
        var w = await SeedWorldAsync();
        var id = await CreateRequestAsync(w);
        var rendezvous = new TransitionRendezvous(parties: 2);
        var emails = new RecordingEmailQueue();
        await using var app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(rendezvous));
            services.RemoveAll<IEmailQueue>();
            services.AddSingleton<IEmailQueue>(emails);
        }));
        using var member1 = CreateClient(app, w.SupplierUserId, Supplier);
        using var member2 = CreateClient(app, w.SecondSupplierUserId, Supplier);

        var responses = await Task.WhenAll(
            member1.PostAsJsonAsync($"/api/service-requests/{id}/take", new { }),
            member2.PostAsJsonAsync($"/api/service-requests/{id}/take", new { }));

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
            responses.Select(r => r.StatusCode).OrderBy(s => s).ToArray());
        var winner = (await ReadJsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.OK))).GetProperty("takenByUserId").GetString();
        Assert.Equal(winner, (await ReadAsync(id)).TakenByUserId);
        Assert.Single(emails.Snapshot());
    }

    // ─── A4-17 / state machine: typed errors ───

    [PostgresTheory]
    [InlineData("take", Supplier)]
    [InlineData("complete", Supplier)]
    [InlineData("reject", Supplier)]
    [InlineData("mark-paid", Host)]
    public async Task Transition_UnknownRequest_Returns404ServiceRequestNotFound(string action, string actor)
    {
        var w = await SeedWorldAsync();
        using var client = actor == Host
            ? _factory.CreateAuthenticatedClient(w.OwnerId, Host)
            : _factory.CreateAuthenticatedClient(w.SupplierUserId, Supplier);

        var response = await client.PostAsJsonAsync(
            $"/api/service-requests/{Guid.NewGuid()}/{action}", new { reason = "Non disponibile" });

        await AssertProblemAsync(response, HttpStatusCode.NotFound, ServiceRequestErrorCodes.NotFound);
    }

    [PostgresFact]
    public async Task MarkPaid_LongRentUnknownRequest_Returns404ServiceRequestNotFound()
    {
        var w = await SeedWorldAsync();
        using var landlord = _factory.CreateAuthenticatedClient(w.OwnerId, "LongTermLandlord");

        var response = await landlord.PostAsync($"/api/long-rent/service-requests/{Guid.NewGuid()}/mark-paid", null);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, ServiceRequestErrorCodes.NotFound);
    }

    [PostgresTheory]
    [InlineData(ServiceRequestStatus.Richiesto, "complete")]
    [InlineData(ServiceRequestStatus.Richiesto, "mark-paid")]
    [InlineData(ServiceRequestStatus.PresoInCarico, "take")]
    [InlineData(ServiceRequestStatus.PresoInCarico, "reject")]
    [InlineData(ServiceRequestStatus.PresoInCarico, "mark-paid")]
    [InlineData(ServiceRequestStatus.Completato, "take")]
    [InlineData(ServiceRequestStatus.Completato, "reject")]
    [InlineData(ServiceRequestStatus.Completato, "complete")]
    [InlineData(ServiceRequestStatus.Rifiutato, "take")]
    [InlineData(ServiceRequestStatus.Rifiutato, "complete")]
    [InlineData(ServiceRequestStatus.Rifiutato, "reject")]
    [InlineData(ServiceRequestStatus.Rifiutato, "mark-paid")]
    [InlineData(ServiceRequestStatus.Pagato, "take")]
    [InlineData(ServiceRequestStatus.Pagato, "reject")]
    [InlineData(ServiceRequestStatus.Pagato, "complete")]
    [InlineData(ServiceRequestStatus.Pagato, "mark-paid")]
    public async Task Transition_NotAllowedFromTheCurrentStatus_Returns422AndChangesNothing(
        ServiceRequestStatus current,
        string action)
    {
        var w = await SeedWorldAsync();
        var id = await CreateRequestAsync(w);
        await SetStatusAsync(id, current);
        using var client = action == "mark-paid"
            ? _factory.CreateAuthenticatedClient(w.OwnerId, Host)
            : _factory.CreateAuthenticatedClient(w.SupplierUserId, Supplier);

        var response = await client.PostAsJsonAsync($"/api/service-requests/{id}/{action}", new { reason = "Non disponibile" });

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ServiceRequestErrorCodes.InvalidTransition);
        Assert.Equal(current, (await ReadAsync(id)).Status);
    }

    [PostgresFact]
    public async Task Reject_AfterTheRequestWasTaken_Returns422WithTheLocalizedReason()
    {
        var w = await SeedWorldAsync();
        var id = await CreateRequestAsync(w);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierUserId, Supplier);
        Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { })).StatusCode);

        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "Ci ho ripensato" });

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ServiceRequestErrorCodes.InvalidTransition);
        Assert.StartsWith("La richiesta non può essere rifiutata", problem.GetProperty("detail").GetString());
        var stored = await ReadAsync(id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, stored.Status);
        Assert.Null(stored.RejectionReason);
    }

    // ─── A4-18: validation of the bodies ───

    [PostgresFact]
    public async Task Create_NotesOf1500Characters_Returns400AndCreatesNothing()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);

        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = w.PropertyId,
            bookingId = w.BookingId,
            supplierOrgId = w.SupplierOrgId,
            category = "cleaning",
            notes = new string('n', 1500),
        });

        var errors = await AssertValidationErrorAsync(response);
        Assert.Equal("Le note possono avere al massimo 1000 caratteri.", errors.GetProperty("Notes")[0].GetString());
        Assert.Equal(0, await CountRequestsAsync(w.PropertyId));
    }

    [PostgresFact]
    public async Task Create_NotesOf1000Characters_Returns201WithTheNotes()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);

        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = w.PropertyId,
            bookingId = w.BookingId,
            supplierOrgId = w.SupplierOrgId,
            category = "cleaning",
            notes = new string('n', 1000),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1000, (await ReadJsonAsync(response)).GetProperty("notes").GetString()!.Length);
    }

    [PostgresFact]
    public async Task Create_EmptyIds_Returns400WithAnErrorPerIdAndCreatesNothing()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);

        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = Guid.Empty,
            bookingId = Guid.Empty,
            supplierOrgId = Guid.Empty,
            category = "cleaning",
        });

        var errors = await AssertValidationErrorAsync(response);
        Assert.Equal("Indica l'immobile della richiesta.", errors.GetProperty("PropertyId")[0].GetString());
        Assert.Equal("La prenotazione indicata non è valida.", errors.GetProperty("BookingId")[0].GetString());
        Assert.Equal("Scegli il fornitore a cui inviare la richiesta.", errors.GetProperty("SupplierOrgId")[0].GetString());
        Assert.Equal(0, await CountRequestsAsync(w.PropertyId));
    }

    [PostgresFact]
    public async Task Create_WithoutCategory_Returns400AndAnUnknownCategoryReturns422()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);

        var missing = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = w.PropertyId,
            bookingId = w.BookingId,
            supplierOrgId = w.SupplierOrgId,
        });
        var unknown = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = w.PropertyId,
            bookingId = w.BookingId,
            supplierOrgId = w.SupplierOrgId,
            category = new string('x', 150),
        });

        var errors = await AssertValidationErrorAsync(missing);
        Assert.Equal("Indica la categoria del servizio.", errors.GetProperty("Category")[0].GetString());
        await AssertProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, "invalid_service_category");
        Assert.Equal(0, await CountRequestsAsync(w.PropertyId));
    }

    [PostgresFact]
    public async Task Create_UnknownSupplier_Returns404SupplierNotFound()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);

        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = w.PropertyId,
            bookingId = w.BookingId,
            supplierOrgId = Guid.NewGuid(),
            category = "cleaning",
        });

        await AssertProblemAsync(response, HttpStatusCode.NotFound, ServiceRequestErrorCodes.SupplierNotFound);
        Assert.Equal(0, await CountRequestsAsync(w.PropertyId));
    }

    [PostgresTheory]
    [InlineData("{}")]
    [InlineData("""{"reason":null}""")]
    [InlineData("""{"reason":"   "}""")]
    public async Task Reject_WithoutReason_Returns400AndLeavesTheRequestNew(string body)
    {
        var w = await SeedWorldAsync();
        var id = await CreateRequestAsync(w);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierUserId, Supplier);

        var response = await supplier.PostAsync(
            $"/api/service-requests/{id}/reject", new StringContent(body, Encoding.UTF8, "application/json"));

        var errors = await AssertValidationErrorAsync(response);
        Assert.Equal("Indica il motivo del rifiuto.", errors.GetProperty("Reason")[0].GetString());
        var stored = await ReadAsync(id);
        Assert.Equal(ServiceRequestStatus.Richiesto, stored.Status);
        Assert.Null(stored.RejectionReason);
    }

    [PostgresFact]
    public async Task Reject_ReasonOver500Characters_Returns400()
    {
        var w = await SeedWorldAsync();
        var id = await CreateRequestAsync(w);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierUserId, Supplier);

        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = new string('r', 501) });

        var errors = await AssertValidationErrorAsync(response);
        Assert.Equal("Il motivo del rifiuto può avere al massimo 500 caratteri.", errors.GetProperty("Reason")[0].GetString());
        Assert.Equal(ServiceRequestStatus.Richiesto, (await ReadAsync(id)).Status);
    }

    [PostgresFact]
    public async Task Reject_WithReason_Returns200AndStoresTheTrimmedReason()
    {
        var w = await SeedWorldAsync();
        var id = await CreateRequestAsync(w);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierUserId, Supplier);

        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "  Non disponibile  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadAsync(id);
        Assert.Equal(ServiceRequestStatus.Rifiutato, stored.Status);
        Assert.Equal("Non disponibile", stored.RejectionReason);
    }

    [PostgresFact]
    public async Task Complete_NotesOf1500Characters_Returns400AndLeavesTheRequestTaken()
    {
        var w = await SeedWorldAsync();
        var id = await CreateRequestAsync(w);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierUserId, Supplier);
        Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { })).StatusCode);

        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { notes = new string('n', 1500) });

        var errors = await AssertValidationErrorAsync(response);
        Assert.True(errors.TryGetProperty("Notes", out _));
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await ReadAsync(id)).Status);
    }

    // ─── helpers ───

    private sealed record World(
        string OwnerId,
        string HostContactEmail,
        Guid PropertyId,
        Guid BookingId,
        Guid SupplierOrgId,
        string SupplierUserId,
        string SecondSupplierUserId);

    /// <summary>
    /// Holds every save that modifies a service request until <c>parties</c> of them arrived, so they all read the
    /// same state before any of them writes.
    /// </summary>
    private sealed class TransitionRendezvous(int parties) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var transition = eventData.Context?.ChangeTracker.Entries<ServiceRequest>()
                .Any(e => e.State == EntityState.Modified) == true;
            if (transition)
            {
                if (Interlocked.Increment(ref _arrived) >= parties)
                    _allArrived.TrySetResult();
                await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return result;
        }
    }

    private sealed class RecordingPushNotifications : IPushNotificationService
    {
        public ConcurrentQueue<(Guid Id, string Type)> Sent { get; } = new();

        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
        {
            Sent.Enqueue((payload.ServiceRequestId ?? Guid.Empty, payload.Type));
            return true;
        }
    }

    private static HttpClient CreateClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string userId, string roles)
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", userId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        return client;
    }

    private async Task<Guid> CreateRequestAsync(World w)
    {
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = w.PropertyId,
            bookingId = w.BookingId,
            supplierOrgId = w.SupplierOrgId,
            category = "cleaning",
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        var body = JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        return body;
    }

    /// <summary>400 <c>validation_error</c> (FD-05); returns the field errors.</summary>
    private static async Task<JsonElement> AssertValidationErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {text}");
        var body = JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
        return body.GetProperty("errors");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);

    private async Task<int> CountRequestsAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().CountAsync(r => r.PropertyId == propertyId);
    }

    private async Task<ServiceRequest> ReadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task SetStatusAsync(Guid id, ServiceRequestStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ServiceRequests.IgnoreQueryFilters()
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, status));
    }

    /// <summary>
    /// A host org with one property in comune H501 and one confirmed stay on it, and an active supplier (its own org)
    /// with two members, operating in H501 for cleaning.
    /// </summary>
    private async Task<World> SeedWorldAsync()
    {
        const string comune = "H501";
        var ownerId = $"auth0|su10-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = "Casa SU10",
            Address = $"Via SU10 {Guid.NewGuid():N}",
            City = comune,
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Ospite",
            Email = $"su10-{Guid.NewGuid():N}@example.com",
        };
        var stay = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(5),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(8),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };

        var supplierOrg = new OrgEntity
        {
            Name = "SU10 Supplier",
            Slug = $"su10-sup-{Guid.NewGuid():N}"[..25],
            DisplayName = "SU10 Supplier",
            ContactEmail = $"su10-supplier-{Guid.NewGuid():N}@example.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        User SupplierMember(string id, string name) => new()
        {
            Id = id,
            Email = $"{name}-{Guid.NewGuid():N}@example.com",
            FirstName = name,
            LastName = "Fornitore",
            OrgId = supplierOrg.Id,
            SupplierOrgId = supplierOrg.Id,
            IsActive = true,
        };
        var member1 = SupplierMember($"auth0|su10-supplier-{Guid.NewGuid():N}", "sara");
        var member2 = SupplierMember($"auth0|su10-supplier-{Guid.NewGuid():N}", "luca");
        var supplierProfile = new SupplierProfile
        {
            OrgId = supplierOrg.Id,
            Email = supplierOrg.ContactEmail,
            LegalName = "SU10 Pulizie Srl",
            Phone = "+39 06 000000",
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{comune}\"]",
            CategoriesJson = "[\"cleaning\"]",
            TosAcceptedAt = DateTime.UtcNow,
        };

        db.AddRange(property, guest, stay, supplierOrg, member1, member2, supplierProfile);
        await db.SaveChangesAsync();

        return new World(ownerId, org.ContactEmail, property.Id, stay.Id, supplierOrg.Id, member1.Id, member2.Id);
    }
}
