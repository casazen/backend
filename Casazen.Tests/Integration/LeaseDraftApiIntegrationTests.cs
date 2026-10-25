using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Casazen.Tests.Integration.LeaseDraftHttp;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-12 over the real pipeline (PostgreSQL in CI, EF InMemory in a local run): the guided draft of a lease through its endpoints —
/// create with only the property, save any value, take it up again identical, the version that refuses a stale save, the review,
/// "downloaded for signature" and its template gate, the deletion, and who may do what to which property. The world is
/// <see cref="LongRentWorld"/> (owner A with P1 and P2, colleague B with P3, one org), "today" is 2026-10-09.
/// </summary>
public class LeaseDraftApiIntegrationTests(LeaseDraftFactory factory) : IClassFixture<LeaseDraftFactory>, IAsyncLifetime
{
    private LongRentWorld _world = null!;

    public async Task InitializeAsync()
    {
        _world = await LongRentWorld.SeedAsync(factory);
        foreach (var property in new[] { _world.P1, _world.P2, _world.P3 })
            await factory.MakeReadyAsync(property);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Owner() => _world.OwnerClient(factory);

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>Another property of the world's org, long-term and ready (its APE and its cadastral data), owned by <paramref name="ownerId"/>.</summary>
    private async Task<Guid> AddPropertyAsync(string ownerId)
    {
        var id = await WithDbAsync(async db =>
        {
            var property = new Property
            {
                OwnerId = ownerId,
                OrgId = _world.OrgId,
                Name = "Casa aggiunta",
                Description = "Property added by the guided draft tests",
                Address = $"Via Aggiunta {Guid.NewGuid():N}",
                City = "Bari",
                PostalCode = "70121",
                Bedrooms = 2,
                Bathrooms = 1,
                RentalMode = RentalMode.Long,
                IsActive = true,
            };
            db.Properties.Add(property);
            await db.SaveChangesAsync();
            return property.Id;
        });
        await factory.MakeReadyAsync(id);
        return id;
    }

    private Task<LeaseContract> LoadAsync(Guid id) =>
        WithDbAsync(db => db.LeaseContracts.IgnoreQueryFilters().AsNoTracking().Include(l => l.Parties).Include(l => l.Events).SingleAsync(l => l.Id == id));

    // ---- access --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHost_EncryptsTheDraftAndTheResidence_TheWayItDoesInProduction()
    {
        // The Data Protection provider of the host reaches the context: the two columns have their converter, so the table never
        // holds the typed state or a residence in clear (what the table really holds is proved on PostgreSQL, LeaseDraftPostgresTests).
        var converters = await WithDbAsync(db => Task.FromResult(new object?[]
        {
            db.Model.FindEntityType(typeof(LeaseContract))!.FindProperty(nameof(LeaseContract.DraftData))!.GetValueConverter(),
            db.Model.FindEntityType(typeof(Party))!.FindProperty(nameof(Party.Residence))!.GetValueConverter(),
        }));

        Assert.All(converters, converter => Assert.IsType<EncryptedStringConverter>(converter));
    }

    [Fact]
    public async Task EveryEndpoint_WithoutAToken_Is401()
    {
        using var anonymous = factory.CreateClient();
        var id = Guid.NewGuid();

        foreach (var response in new[]
                 {
                     await anonymous.PostAsync("/api/leases/drafts", Body($$"""{"propertyId":"{{_world.P1}}"}""")),
                     await anonymous.GetAsync($"/api/leases/{id}/draft"),
                     await anonymous.PatchAsync($"/api/leases/{id}/draft", Body("""{"version":1,"step":2,"values":{}}""")),
                     await anonymous.GetAsync($"/api/leases/{id}/review"),
                     await anonymous.PostAsync($"/api/leases/{id}/review/confirm", Body("{}")),
                     await anonymous.DeleteAsync($"/api/leases/{id}/review/confirm"),
                     await anonymous.PostAsync($"/api/leases/{id}/sent", Body("{}")),
                     await anonymous.DeleteAsync($"/api/leases/{id}/sent"),
                     await anonymous.DeleteAsync($"/api/leases/{id}"),
                     await anonymous.GetAsync("/api/long-rent/landlord-defaults"),
                     await anonymous.GetAsync("/api/long-rent/rules"),
                 })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task EveryEndpoint_ARoleWithoutTheLeasePermissions_Is403()
    {
        // A short-term host has no long-rent context: the policy refuses before the row is looked at.
        var shortHostId = $"auth0|lr12-short-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(shortHostId);
        using var shortHost = factory.CreateAuthenticatedClient(shortHostId, "PropertyOwner");
        var id = Guid.NewGuid();

        foreach (var response in new[]
                 {
                     await shortHost.PostAsync("/api/leases/drafts", Body($$"""{"propertyId":"{{_world.P1}}"}""")),
                     await shortHost.GetAsync($"/api/leases/{id}/draft"),
                     await shortHost.PatchAsync($"/api/leases/{id}/draft", Body("""{"version":1,"step":2,"values":{}}""")),
                     await shortHost.GetAsync($"/api/leases/{id}/review"),
                     await shortHost.PostAsync($"/api/leases/{id}/review/confirm", Body("{}")),
                     await shortHost.PostAsync($"/api/leases/{id}/sent", Body("{}")),
                     await shortHost.DeleteAsync($"/api/leases/{id}"),
                     await shortHost.GetAsync("/api/long-rent/landlord-defaults"),
                     await shortHost.GetAsync("/api/long-rent/rules"),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // ---- create --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithOnlyTheProperty_Is201_WithTheVersionTheFlowAndALocation()
    {
        using var client = Owner();

        var response = await client.PostAsync("/api/leases/drafts", Body($$"""{"propertyId":"{{_world.P1}}"}"""));

        var created = await ExpectAsync(response, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(1, created.GetProperty("version").GetInt32());
        Assert.EndsWith($"/api/leases/{id}/draft", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var flow = created.GetProperty("flow");
        Assert.Equal(8, flow.GetProperty("steps").GetArrayLength());
        Assert.Equal("Done", StateOf(flow, 1));
        Assert.Equal(["landlord_missing", "tenant_missing"], NeedCodes(flow, 2));
        Assert.Equal("Locked", StateOf(flow, 6));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var lease = await LoadAsync(id);
        Assert.Equal((LeaseStatus.Draft, LeaseOrigin.Created, 1), (lease.Status, lease.Origin, lease.WizardMaxStep));
        Assert.Equal(_world.OrgId, lease.OrgId);
        Assert.Equal([LeaseEventType.Created], lease.Events.Select(e => e.EventType));
    }

    [Fact]
    public async Task Create_AnImportedDraft_KeepsTheOrigin_AndTheFlowHasNoReview()
    {
        using var client = Owner();

        var (id, _) = await CreateAsync(client, _world.P1, origin: "Imported");

        Assert.Equal(LeaseOrigin.Imported, (await LoadAsync(id)).Origin);
        var draft = await JsonAsync(await client.GetAsync($"/api/leases/{id}/draft"));
        Assert.Equal("Imported", draft.GetProperty("origin").GetString());
        Assert.Equal("NotApplicable", StateOf(draft.GetProperty("flow"), 6));
    }

    [Fact]
    public async Task Create_AnOriginThatDoesNotExist_Is400()
    {
        using var client = Owner();

        var problem = await ExpectAsync(
            await client.PostAsync("/api/leases/drafts", Body($$"""{"propertyId":"{{_world.P1}}","origin":"Rented"}""")),
            HttpStatusCode.BadRequest);

        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_ForAPropertyOfAnotherOrg_Is404_AndNothingIsCreated()
    {
        var stranger = await factory.SeedPropertyAsync($"auth0|lr12-stranger-{Guid.NewGuid():N}");
        await factory.MakeReadyAsync(stranger.Id);
        using var client = Owner();
        var before = await WithDbAsync(db => db.LeaseContracts.IgnoreQueryFilters().CountAsync(l => l.PropertyId == stranger.Id));

        await ExpectProblemAsync(
            await client.PostAsync("/api/leases/drafts", Body($$"""{"propertyId":"{{stranger.Id}}"}""")),
            HttpStatusCode.NotFound,
            "property_not_found");

        Assert.Equal(before, await WithDbAsync(db => db.LeaseContracts.IgnoreQueryFilters().CountAsync(l => l.PropertyId == stranger.Id)));
    }

    [Fact]
    public async Task Create_AMemberLimitedToTheirOwnProperties_CannotCreateOnAnother_ButAnOrgWideMemberCan()
    {
        using var colleague = _world.ColleagueClient(factory);
        using var manager = _world.ManagerClient(factory);

        // The colleague owns P3 only: P1 is another property of the org (403, not 404: it exists for them).
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await colleague.PostAsync("/api/leases/drafts", Body($$"""{"propertyId":"{{_world.P1}}"}"""))).StatusCode);
        await CreateAsync(colleague, _world.P3);
        await CreateAsync(manager, _world.P1);
    }

    [Fact]
    public async Task Create_ForAPropertyLetForShortStays_IsAllowed_TheReviewWarnsLater()
    {
        var shortProperty = await factory.SeedPropertyAsync(_world.OwnerId);
        await factory.MakeReadyAsync(shortProperty.Id, mode: RentalMode.Short);
        using var client = Owner();

        var (id, _) = await CreateAsync(client, shortProperty.Id);

        var review = await JsonAsync(await client.GetAsync($"/api/leases/{id}/review"));
        var warn = Assert.Single(review.GetProperty("checks").EnumerateArray(), c => c.GetProperty("code").GetString() == "property_short_mode");
        Assert.Equal("Warn", warn.GetProperty("severity").GetString());
    }

    // ---- save and resume -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Save_IncompleteAndWrongValues_Are200_NeverA4xx_AndTheFlowSaysWhatIsMissing()
    {
        using var client = Owner();
        var (id, version) = await CreateAsync(client, _world.P1);

        var s2 = await PatchAsync(client, id, version, 2, """
            {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"NOT-A-CODE","citizenship":"IT","residence":"Bari"}],"tenants":[{"firstName":"Giulia"}]}
            """);
        var s3 = await PatchAsync(client, id, s2.GetProperty("version").GetInt32(), 3, """{"contractType":"Studenti","monthlyRent":-50,"securityDeposit":99}""");
        var s4 = await PatchAsync(client, id, s3.GetProperty("version").GetInt32(), 4, """{"startDate":"next spring"}""");

        var flow = s4.GetProperty("flow");
        Assert.Equal(4, s4.GetProperty("version").GetInt32());
        Assert.Equal(["landlord_incomplete", "tenant_incomplete"], NeedCodes(flow, 2));
        Assert.Equal(["lease_type_not_available", "tax_regime_missing", "rent_invalid"], NeedCodes(flow, 3));
        Assert.Equal(["start_invalid"], NeedCodes(flow, 4));
        Assert.True(DateTime.TryParse(s4.GetProperty("updatedAt").GetString(), out _));
        Assert.Equal(1, s4.GetProperty("landlordIds").GetArrayLength());
        Assert.Equal(1, s4.GetProperty("tenantIds").GetArrayLength());
    }

    [Fact]
    public async Task Get_TakesItUpIdentical_ValuesFlowVersionOriginAndTheStepReached()
    {
        using var client = Owner();
        var (id, version) = await CreateAsync(client, _world.P1);
        var s2 = await PatchAsync(client, id, version, 2, """
            {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"RSSMRA80A01H501U","citizenship":"it","residence":"  Bari  ","contactEmail":"x"}],
             "tenants":[{"firstName":"Giulia","lastName":"Verdi"}]}
            """);
        var s3 = await PatchAsync(client, id, s2.GetProperty("version").GetInt32(), 3, """{"contractType":"Libero","taxRegime":"x","monthlyRent":12.5,"securityDeposit":500}""");
        var s4 = await PatchAsync(client, id, s3.GetProperty("version").GetInt32(), 4, """{"startDate":"1 novembre"}""");

        // Another browser: a new client, the same user.
        using var other = Owner();
        var response = await other.GetAsync($"/api/leases/{id}/draft");

        var draft = await ExpectAsync(response, HttpStatusCode.OK);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(id, draft.GetProperty("id").GetGuid());
        Assert.Equal(s4.GetProperty("version").GetInt32(), draft.GetProperty("version").GetInt32());
        Assert.Equal("Created", draft.GetProperty("origin").GetString());
        Assert.Equal(4, draft.GetProperty("wizardMaxStep").GetInt32());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("reviewConfirmedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("sentForSignatureAt").ValueKind);
        var values = draft.GetProperty("values");
        Assert.Equal(_world.P1, values.GetProperty("propertyId").GetGuid());
        var landlord = values.GetProperty("landlords")[0];
        Assert.Equal(s2.GetProperty("landlordIds")[0].GetGuid(), landlord.GetProperty("id").GetGuid());
        Assert.Equal(("Mario", "Rossi", "RSSMRA80A01H501U", "it", "  Bari  ", "x"),
            (landlord.GetProperty("firstName").GetString(), landlord.GetProperty("lastName").GetString(), landlord.GetProperty("fiscalCode").GetString(),
             landlord.GetProperty("citizenship").GetString(), landlord.GetProperty("residence").GetString(), landlord.GetProperty("contactEmail").GetString()));
        Assert.Equal("Giulia", values.GetProperty("tenants")[0].GetProperty("firstName").GetString());
        Assert.Equal(("Libero", "x", 12.5m, 500m, "1 novembre"),
            (values.GetProperty("contractType").GetString(), values.GetProperty("taxRegime").GetString(), values.GetProperty("monthlyRent").GetDecimal(),
             values.GetProperty("securityDeposit").GetDecimal(), values.GetProperty("startDate").GetString()));
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("computed").GetProperty("endDate").ValueKind);
        Assert.Equal(NeedCodes(s4.GetProperty("flow"), 3), NeedCodes(draft.GetProperty("flow"), 3));
    }

    [Fact]
    public async Task Save_TheLastValidValue_MakesALeaseTheOtherReadersSeeAsAnOrdinaryDraft()
    {
        using var client = Owner();

        var (id, _) = await CompleteAsync(client, _world.P1);

        var lease = await LoadAsync(id);
        Assert.Null(lease.DraftData);
        Assert.Equal((LeaseContractType.Libero, 900m, 1800m), (lease.ContractType, lease.MonthlyRent, lease.SecurityDeposit));
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), lease.StartDate);
        Assert.Equal(new DateTime(2030, 10, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        Assert.Equal(2, lease.Parties.Count);
        // It is in the list as every draft has always been, with the tenant's name, in preparation.
        var list = await JsonAsync(await client.GetAsync("/api/leases?view=InPreparation"));
        var row = Assert.Single(list.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal("Draft", row.GetProperty("status").GetString());
        Assert.Equal("Giulia", row.GetProperty("tenantFirstName").GetString());
        Assert.Equal(900m, row.GetProperty("monthlyRent").GetDecimal());
    }

    [Fact]
    public async Task Save_AnIncompleteDraft_IsInTheListWithItsStartingValues_NotAsAnythingElse()
    {
        using var client = Owner();
        var (id, _) = await CreateAsync(client, _world.P1);

        var list = await JsonAsync(await client.GetAsync("/api/leases"));

        var row = Assert.Single(list.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal("Draft", row.GetProperty("status").GetString());
        Assert.Equal(0m, row.GetProperty("monthlyRent").GetDecimal());
        Assert.StartsWith("3000-01-01", row.GetProperty("startDate").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("registrationDeadline").ValueKind);
        var preparation = await JsonAsync(await client.GetAsync("/api/leases?view=InPreparation"));
        Assert.Contains(preparation.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        foreach (var view in new[] { "Active", "Expiring", "Ended" })
        {
            var other = await JsonAsync(await client.GetAsync($"/api/leases?view={view}"));
            Assert.DoesNotContain(other.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        }
    }

    [Fact]
    public async Task Save_OnAnOldVersion_Is409WithTheCode_AndNothingIsWritten()
    {
        using var client = Owner();
        var (id, version) = await CreateAsync(client, _world.P1);
        var first = await PatchAsync(client, id, version, 3, """{"monthlyRent":900}""");

        // The second tab still has the version it opened with.
        using var secondTab = Owner();
        await ExpectProblemAsync(
            await secondTab.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 3, """{"monthlyRent":1000}""")),
            HttpStatusCode.Conflict,
            "lease_draft_conflict");

        var draft = await JsonAsync(await client.GetAsync($"/api/leases/{id}/draft"));
        Assert.Equal(first.GetProperty("version").GetInt32(), draft.GetProperty("version").GetInt32());
        Assert.Equal(900m, draft.GetProperty("values").GetProperty("monthlyRent").GetDecimal());
    }

    [Theory]
    [InlineData("""{"version":1,"step":6,"values":{}}""")]
    [InlineData("""{"version":1,"step":0,"values":{}}""")]
    [InlineData("""{"step":3,"values":{}}""")]
    [InlineData("""{"version":1,"values":{}}""")]
    [InlineData("""{"version":1,"step":3}""")]
    [InlineData("""{"version":1,"step":3,"values":[]}""")]
    [InlineData("""{"version":1,"step":3,"values":{"monthlyRent":"900"}}""")]
    [InlineData("""{"version":1,"step":3,"values":{"startDate":"2026-11-01"}}""")]
    [InlineData("""{"version":1,"step":2,"values":{"landlords":[{"nickname":"x"}]}}""")]
    public async Task Save_AWrongShape_Is400(string body)
    {
        using var client = Owner();
        var (id, _) = await CreateAsync(client, _world.P1);

        var response = await client.PatchAsync($"/api/leases/{id}/draft", Body(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, (await LoadAsync(id)).DraftVersion);
    }

    [Theory]
    [InlineData("""{"version":1,"step":3,"values":[]}""")]
    [InlineData("""{"version":1,"step":3,"values":{"monthlyRent":"900"}}""")]
    [InlineData("""{"version":1,"step":3,"values":{"startDate":"2026-11-01"}}""")]
    [InlineData("""{"version":1,"step":2,"values":{"landlords":[{"nickname":"x"}]}}""")]
    public async Task Save_ValuesThatAreNotTheShapeOfTheStep_AreAProblemWithTheValidationCode_ThatNamesNoValue(string body)
    {
        using var client = Owner();
        var (id, _) = await CreateAsync(client, _world.P1);

        var problem = await ExpectProblemAsync(await client.PatchAsync($"/api/leases/{id}/draft", Body(body)), HttpStatusCode.BadRequest, "validation_error");

        Assert.DoesNotContain("900", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_MoreThanTenTenants_Is422()
    {
        using var client = Owner();
        var (id, version) = await CreateAsync(client, _world.P1);
        var eleven = string.Join(",", Enumerable.Range(0, 11).Select(i => $$"""{"firstName":"T{{i}}"}"""));

        await ExpectProblemAsync(
            await client.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 2, $$"""{"tenants":[{{eleven}}]}""")),
            HttpStatusCode.UnprocessableEntity,
            "lease_parties_too_many");
    }

    [Fact]
    public async Task Save_ANumberTooBigToHold_IsAWrongValueReportedInTheFlow_NotA400()
    {
        using var client = Owner();
        var (id, version) = await CreateAsync(client, _world.P1);

        var saved = await PatchAsync(client, id, version, 3, """{"monthlyRent":1e30,"securityDeposit":-1e30}""");

        Assert.Equal(["contract_type_missing", "tax_regime_missing", "rent_invalid", "deposit_invalid"], NeedCodes(saved.GetProperty("flow"), 3));
    }

    [Fact]
    public async Task Save_AnythingOver256KB_IsRefused()
    {
        using var client = Owner();
        var (id, version) = await CreateAsync(client, _world.P1);
        var huge = new string('a', 300 * 1024);

        var response = await client.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 2, $$"""{"landlords":[{"firstName":"{{huge}}"}]}"""));

        Assert.True(response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, $"got {(int)response.StatusCode}");
        Assert.Equal(version, (await LoadAsync(id)).DraftVersion);
    }

    [Fact]
    public async Task Save_AtStep1_APropertyOfAnotherOrg_Is404_OneTheCallerMayNotHandle_Is403_OneOfTheirOwn_IsFine()
    {
        var stranger = await factory.SeedPropertyAsync($"auth0|lr12-stranger-{Guid.NewGuid():N}");
        using var colleague = _world.ColleagueClient(factory);
        var (id, version) = await CreateAsync(colleague, _world.P3);
        var mine = await AddPropertyAsync(_world.ColleagueId);

        await ExpectProblemAsync(
            await colleague.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 1, PropertyValues(stranger.Id))),
            HttpStatusCode.NotFound,
            "property_not_found");
        // P1 belongs to the owner of the org, the colleague is not org-wide.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await colleague.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 1, PropertyValues(_world.P1)))).StatusCode);
        Assert.Equal(_world.P3, (await LoadAsync(id)).PropertyId);
        Assert.Equal(version, (await LoadAsync(id)).DraftVersion);
        _ = await ExpectAsync(
            await colleague.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 1, PropertyValues(mine))),
            HttpStatusCode.OK);
        Assert.Equal(mine, (await LoadAsync(id)).PropertyId);
    }

    // ---- scope: a member limited to some properties --------------------------------------------------------------

    [Fact]
    public async Task ADraftOfAnotherProperty_IsInvisibleToAMemberWhoCannotHandleIt_EveryEndpointSays403_AndAnotherOrgSays404()
    {
        using var owner = Owner();
        var (id, version) = await CompleteAsync(owner, _world.P1);
        using var colleague = _world.ColleagueClient(factory);
        var strangerId = $"auth0|lr12-other-org-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(strangerId);
        using var stranger = factory.CreateAuthenticatedClient(strangerId, "LongTermLandlord");

        foreach (var response in new[]
                 {
                     await colleague.GetAsync($"/api/leases/{id}/draft"),
                     await colleague.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 3, """{"monthlyRent":1}""")),
                     await colleague.GetAsync($"/api/leases/{id}/review"),
                     await colleague.PostAsync($"/api/leases/{id}/review/confirm", Body("{}")),
                     await colleague.DeleteAsync($"/api/leases/{id}/review/confirm"),
                     await colleague.PostAsync($"/api/leases/{id}/sent", Body("{}")),
                     await colleague.DeleteAsync($"/api/leases/{id}/sent"),
                     await colleague.DeleteAsync($"/api/leases/{id}"),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        foreach (var response in new[]
                 {
                     await stranger.GetAsync($"/api/leases/{id}/draft"),
                     await stranger.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 3, """{"monthlyRent":1}""")),
                     await stranger.DeleteAsync($"/api/leases/{id}"),
                 })
        {
            await ExpectProblemAsync(response, HttpStatusCode.NotFound, "lease_not_found");
        }

        // Nothing changed.
        var lease = await LoadAsync(id);
        Assert.Equal((version, 900m), (lease.DraftVersion, lease.MonthlyRent));
    }

    // ---- not a draft ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALeaseThatIsNotADraft_Get404_Patch409_Delete409_EverythingElseSaysNotADraft()
    {
        using var owner = Owner();

        await ExpectProblemAsync(await owner.GetAsync($"/api/leases/{_world.Active}/draft"), HttpStatusCode.NotFound, "lease_not_draft");
        await ExpectProblemAsync(await owner.GetAsync($"/api/leases/{_world.Active}/review"), HttpStatusCode.NotFound, "lease_not_draft");
        await ExpectProblemAsync(
            await owner.PatchAsync($"/api/leases/{_world.Active}/draft", Body("""{"version":1,"step":3,"values":{"monthlyRent":1}}""")),
            HttpStatusCode.Conflict,
            "lease_not_draft");
        await ExpectProblemAsync(await owner.PostAsync($"/api/leases/{_world.Active}/review/confirm", Body("{}")), HttpStatusCode.Conflict, "lease_not_draft");
        await ExpectProblemAsync(await owner.PostAsync($"/api/leases/{_world.Active}/sent", Body("{}")), HttpStatusCode.Conflict, "lease_not_draft");
        await ExpectProblemAsync(await owner.DeleteAsync($"/api/leases/{_world.Active}"), HttpStatusCode.Conflict, "lease_not_deletable");
        await ExpectProblemAsync(await owner.DeleteAsync($"/api/leases/{_world.Signed}"), HttpStatusCode.Conflict, "lease_not_deletable");
        Assert.Equal(900m, (await LoadAsync(_world.Active)).MonthlyRent);
    }

    [Fact]
    public async Task ADraftCreatedWithTheCompleteCreation_CanBeOpenedInTheWizard_AllItsDataStepsDone()
    {
        using var owner = Owner();
        var response = await owner.PostAsync("/api/leases", Body($$"""
            {"propertyId":"{{_world.P1}}","contractType":"Libero","taxRegime":"CedolareSecca","startDate":"2026-12-01T00:00:00Z","endDate":"2030-11-30T00:00:00Z",
             "monthlyRent":950,"securityDeposit":1900,
             "parties":[{"role":"Landlord","firstName":"Mario","lastName":"Rossi","fiscalCode":"RSSMRA80A01H501U","citizenship":"IT","contactEmail":"mario@example.com"},
                        {"role":"Tenant","firstName":"Giulia","lastName":"Verdi","fiscalCode":"VRDGLI85B02F205A","citizenship":"IT","contactEmail":"giulia@example.com"}]}
            """));
        var created = await ExpectAsync(response, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        // The response of the complete creation is the masked detail, with the new fields at the end.
        Assert.Equal("Created", created.GetProperty("origin").GetString());
        Assert.Equal(5, created.GetProperty("wizardMaxStep").GetInt32());

        var draft = await JsonAsync(await owner.GetAsync($"/api/leases/{id}/draft"));

        var flow = draft.GetProperty("flow");
        // The residence was never asked by that form: the parties are incomplete for the wizard, which asks for it.
        Assert.Equal(["landlord_incomplete", "tenant_incomplete"], NeedCodes(flow, 2));
        Assert.Equal(["Done", "Todo", "Done", "Done", "Done"], Enumerable.Range(1, 5).Select(n => StateOf(flow, n)));
        Assert.Equal(950m, draft.GetProperty("values").GetProperty("monthlyRent").GetDecimal());
        Assert.Equal("2026-12-01", draft.GetProperty("values").GetProperty("startDate").GetString());
    }

    // ---- the review ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Review_WithBlocks_CannotBeConfirmed_422_AndTheCompleteOneCan_ThenAChangeWithdrawsIt()
    {
        using var client = Owner();
        var (blockedId, _) = await CreateAsync(client, _world.P1);
        var (id, version) = await CompleteAsync(client, _world.P1);

        await ExpectProblemAsync(await client.PostAsync($"/api/leases/{blockedId}/review/confirm", Body("{}")), HttpStatusCode.UnprocessableEntity, "lease_review_blocked");
        var review = await JsonAsync(await client.GetAsync($"/api/leases/{id}/review"));
        Assert.Equal(0, review.GetProperty("counts").GetProperty("block").GetInt32());
        Assert.Contains(review.GetProperty("checks").EnumerateArray(), c => c.GetProperty("code").GetString() == "start_ok");

        var confirmed = await ExpectAsync(await client.PostAsync($"/api/leases/{id}/review/confirm", Body($$"""{"version":{{version}}}""")), HttpStatusCode.OK);
        Assert.Equal(version + 1, confirmed.GetProperty("version").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, confirmed.GetProperty("reviewConfirmedAt").ValueKind);
        Assert.Equal("Done", StateOf(confirmed.GetProperty("flow"), 6));

        // A value of the first five steps changes: the confirmation is withdrawn.
        var saved = await PatchAsync(client, id, version + 1, 3, """{"monthlyRent":950}""");
        Assert.Equal("Todo", StateOf(saved.GetProperty("flow"), 6));
        var lease = await LoadAsync(id);
        Assert.Null(lease.ReviewConfirmedAt);
        Assert.Contains(lease.Events, e => e.EventType == LeaseEventType.ReviewReopened && e.Payload == "changed");
        var blockedAgain = await JsonAsync(await client.GetAsync($"/api/leases/{blockedId}/review"));
        Assert.True(blockedAgain.GetProperty("counts").GetProperty("block").GetInt32() > 0);
    }

    [Fact]
    public async Task Review_ConfirmWithAnOldVersion_Is409_AndWithdrawingWorks()
    {
        using var client = Owner();
        var (id, version) = await CompleteAsync(client, _world.P1);

        await ExpectProblemAsync(
            await client.PostAsync($"/api/leases/{id}/review/confirm", Body($$"""{"version":{{version - 1}}}""")),
            HttpStatusCode.Conflict,
            "lease_draft_conflict");
        var confirmed = await ExpectAsync(await client.PostAsync($"/api/leases/{id}/review/confirm", Body("{}")), HttpStatusCode.OK);

        var reopened = await ExpectAsync(await client.DeleteAsync($"/api/leases/{id}/review/confirm"), HttpStatusCode.OK);

        Assert.Equal(confirmed.GetProperty("version").GetInt32() + 1, reopened.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("reviewConfirmedAt").ValueKind);
    }

    // ---- downloaded for signature --------------------------------------------------------------------------------

    [Fact]
    public async Task Sent_WithATemplateNotApproved_Is422_AndTheStateDoesNotChange()
    {
        using var client = Owner();
        var (id, _) = await CompleteAsync(client, _world.P1);
        await ExpectProblemAsync(await client.PostAsync($"/api/leases/{id}/sent", Body("{}")), HttpStatusCode.UnprocessableEntity, "lease_review_not_confirmed");
        var confirmed = await ExpectAsync(await client.PostAsync($"/api/leases/{id}/review/confirm", Body("{}")), HttpStatusCode.OK);

        // The real gate of the real template service, which is not approved (the models are drafts, LR-D28).
        await ExpectProblemAsync(await client.PostAsync($"/api/leases/{id}/sent", Body("{}")), HttpStatusCode.UnprocessableEntity, "contract_template_not_approved");

        var lease = await LoadAsync(id);
        Assert.Null(lease.SentForSignatureAt);
        Assert.Equal(confirmed.GetProperty("version").GetInt32(), lease.DraftVersion);
        Assert.NotNull(lease.ReviewConfirmedAt);
        Assert.Equal(LeaseStatus.Draft, lease.Status);
        Assert.DoesNotContain(lease.Events, e => e.EventType == LeaseEventType.SentForSignature);
        // The same gate answers the download of the contract itself.
        await ExpectProblemAsync(await client.GetAsync($"/api/leases/{id}/contract.pdf"), HttpStatusCode.UnprocessableEntity, "contract_template_not_approved");
    }

    // ---- deletion ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ADraft_Is204_AndItIsGone_WithItsPartiesAndEvents()
    {
        using var client = Owner();
        var (id, _) = await CompleteAsync(client, _world.P1);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/leases/{id}")).StatusCode);

        await ExpectProblemAsync(await client.GetAsync($"/api/leases/{id}"), HttpStatusCode.NotFound, "lease_not_found");
        await ExpectProblemAsync(await client.GetAsync($"/api/leases/{id}/draft"), HttpStatusCode.NotFound, "lease_not_found");
        var left = await WithDbAsync(async db => (
            await db.Parties.IgnoreQueryFilters().CountAsync(p => p.LeaseContractId == id),
            await db.LeaseEvents.IgnoreQueryFilters().CountAsync(e => e.LeaseContractId == id)));
        Assert.Equal((0, 0), left);
        await ExpectProblemAsync(await client.DeleteAsync($"/api/leases/{id}"), HttpStatusCode.NotFound, "lease_not_found");
    }

    [Fact]
    public async Task Delete_TheOnlyDraftOfAProperty_LetsItChangeModeAgain_BEPM021()
    {
        var property = await factory.SeedPropertyAsync($"auth0|lr12-mode-{Guid.NewGuid():N}");
        await factory.MakeReadyAsync(property.Id);
        using var client = factory.CreateAuthenticatedClient(property.OwnerId, "LongTermLandlord");
        var (id, _) = await CreateAsync(client, property.Id);
        var day = new DateTime(2026, 10, 9).AddDays(20).ToString("yyyy-MM-dd");

        // With the draft there, PM-02 refuses the way back to short stays: the draft "must be deleted".
        await ExpectProblemAsync(
            await client.PostAsync($"/api/properties/{property.Id}/mode/change", Body($$"""{"to":"Short","effectiveDate":"{{day}}"}""")),
            HttpStatusCode.Conflict,
            "property_mode_blocked_by_draft_lease");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/leases/{id}")).StatusCode);

        var scheduled = await client.PostAsync($"/api/properties/{property.Id}/mode/change", Body($$"""{"to":"Short","effectiveDate":"{{day}}"}"""));
        Assert.Equal(HttpStatusCode.Created, scheduled.StatusCode);
    }

    // ---- the masked detail ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMaskedDetail_GainsTheNewFieldsAtTheEnd_AndNeverShowsTheResidenceTheFiscalCodeOrTheEmail()
    {
        using var client = Owner();
        var (id, version) = await CompleteAsync(client, _world.P1);
        var tenantId = (await JsonAsync(await client.GetAsync($"/api/leases/{id}/draft"))).GetProperty("values").GetProperty("tenants")[0].GetProperty("id").GetGuid();
        await PatchAsync(client, id, version, 5, $$"""{"parties":[{"id":"{{tenantId}}","documentsVerifiedOn":"2026-10-01"}]}""");

        var response = await client.GetAsync($"/api/leases/{id}");

        var text = await response.Content.ReadAsStringAsync();
        var detail = JsonDocument.Parse(text).RootElement;
        Assert.Equal("Created", detail.GetProperty("origin").GetString());
        Assert.Equal(5, detail.GetProperty("wizardMaxStep").GetInt32());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("reviewConfirmedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("sentForSignatureAt").ValueKind);
        var tenant = detail.GetProperty("parties").EnumerateArray().Single(p => p.GetProperty("role").GetString() == "Tenant");
        Assert.Equal("2026-10-01", tenant.GetProperty("documentsVerifiedOn").GetString());
        Assert.True(tenant.GetProperty("hasResidence").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("parties").EnumerateArray().Single(p => p.GetProperty("role").GetString() == "Landlord").GetProperty("documentsVerifiedOn").ValueKind);
        foreach (var secret in new[] { "via Argiro 7", "via Sparano 1", "VRDGLI85B02F205A", "RSSMRA80A01H501U", "mario@example.com" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.False(tenant.TryGetProperty("residence", out _));
        // Nothing that was there changed.
        foreach (var name in new[] { "id", "propertyId", "property", "status", "fiscalRegime", "contractType", "taxRegime", "startDate", "endDate", "monthlyRent", "securityDeposit",
                     "concordatoAssessment", "stipulaDate", "registrationDeadline", "hasSignedPdf", "hasExtraEUTenant", "parties", "registration", "events", "createdAt", "updatedAt" })
        {
            Assert.True(detail.TryGetProperty(name, out _), $"{name} is still there");
        }
    }

    // ---- the data of the landlord and the rules ------------------------------------------------------------------

    [Fact]
    public async Task LandlordDefaults_AreTheLandlordOfTheLastLeaseTheCallerReaches_NotMasked()
    {
        // An org of its own: the landlord offered is the one of the only lease it has, whatever the clock of the other tests says.
        var ownerId = $"auth0|lr12-defaults-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(ownerId);
        await factory.MakeReadyAsync(property.Id);
        using var owner = factory.CreateAuthenticatedClient(ownerId, "LongTermLandlord");
        await CompleteAsync(owner, property.Id);

        var defaults = await ExpectAsync(await owner.GetAsync("/api/long-rent/landlord-defaults"), HttpStatusCode.OK);

        Assert.Equal("Mario", defaults.GetProperty("firstName").GetString());
        Assert.Equal("Rossi", defaults.GetProperty("lastName").GetString());
        Assert.Equal("RSSMRA80A01H501U", defaults.GetProperty("fiscalCode").GetString());
        Assert.Equal("Bari, via Sparano 1", defaults.GetProperty("residence").GetString());
        Assert.Equal("mario@example.com", defaults.GetProperty("contactEmail").GetString());
    }

    [Fact]
    public async Task LandlordDefaults_ForAMemberWhoReachesOnlyTheirOwnProperty_NeverCarryTheResidenceOfAnotherMembersLease()
    {
        using var owner = Owner();
        await CompleteAsync(owner, _world.P1);
        using var colleague = _world.ColleagueClient(factory);

        var theirs = await ExpectAsync(await colleague.GetAsync("/api/long-rent/landlord-defaults"), HttpStatusCode.OK);

        // The colleague reaches P3 only: the landlord of its lease (no residence on record) is the most they can be offered.
        Assert.NotEqual("Bari, via Sparano 1", theirs.GetProperty("residence").GetString());
        Assert.NotEqual("mario@example.com", theirs.GetProperty("contactEmail").GetString());
    }

    [Fact]
    public async Task LandlordDefaults_ForAnOrgWithoutLeases_AreTheFiscalDataOfTheOrgAndTheNameOfTheCaller_FieldsMayBeNull()
    {
        var ownerId = $"auth0|lr12-new-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(ownerId);
        using var client = factory.CreateAuthenticatedClient(ownerId, "LongTermLandlord");

        var defaults = await ExpectAsync(await client.GetAsync("/api/long-rent/landlord-defaults"), HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, defaults.GetProperty("residence").ValueKind);
        foreach (var name in new[] { "firstName", "lastName", "fiscalCode", "residence", "contactEmail" })
            Assert.True(defaults.TryGetProperty(name, out _), name);
    }

    [Fact]
    public async Task Rules_AreTheServerSidePlainFigures_ForAnyoneWhoMayReadLeases()
    {
        using var colleague = _world.ColleagueClient(factory);

        var rules = await ExpectAsync(await colleague.GetAsync("/api/long-rent/rules"), HttpStatusCode.OK);

        Assert.Equal(48, rules.GetProperty("termMonths").GetProperty("Libero").GetInt32());
        Assert.Equal(36, rules.GetProperty("termMonths").GetProperty("Concordato").GetInt32());
        Assert.Equal(48, rules.GetProperty("renewalMonths").GetProperty("Libero").GetInt32());
        Assert.Equal(24, rules.GetProperty("renewalMonths").GetProperty("Concordato").GetInt32());
        Assert.Equal(6, rules.GetProperty("noticeMonths").GetInt32());
        Assert.Equal(30, rules.GetProperty("registrationDays").GetInt32());
        Assert.Equal(3, rules.GetProperty("depositMaxRents").GetInt32());
        Assert.Equal(["Libero", "Concordato", "Transitorio"], rules.GetProperty("availableTypes").EnumerateArray().Select(t => t.GetString()));
    }
}

/// <summary>LR-12 with the contract templates approved: the download for signature can be marked and taken back.</summary>
public class LeaseDraftSentIntegrationTests(LeaseDraftApprovedTemplatesFactory factory) : IClassFixture<LeaseDraftApprovedTemplatesFactory>, IAsyncLifetime
{
    private LongRentWorld _world = null!;

    public async Task InitializeAsync()
    {
        _world = await LongRentWorld.SeedAsync(factory);
        await factory.MakeReadyAsync(_world.P1);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sent_WithAnApprovedTemplate_SetsTheMoment_KeepsTheStatus_AndItCanBeTakenBack()
    {
        using var client = _world.OwnerClient(factory);
        var (id, _) = await CompleteAsync(client, _world.P1);
        var confirmed = await ExpectAsync(await client.PostAsync($"/api/leases/{id}/review/confirm", Body("{}")), HttpStatusCode.OK);
        Assert.Equal(["not_sent"], NeedCodes(confirmed.GetProperty("flow"), 7));

        var sent = await ExpectAsync(await client.PostAsync($"/api/leases/{id}/sent", Body("{}")), HttpStatusCode.OK);

        Assert.NotEqual(JsonValueKind.Null, sent.GetProperty("sentForSignatureAt").ValueKind);
        Assert.Equal(confirmed.GetProperty("version").GetInt32() + 1, sent.GetProperty("version").GetInt32());
        Assert.Equal(["signed_copy_missing"], NeedCodes(sent.GetProperty("flow"), 7));
        var draft = await ExpectAsync(await client.GetAsync($"/api/leases/{id}/draft"), HttpStatusCode.OK);
        Assert.NotEqual(JsonValueKind.Null, draft.GetProperty("sentForSignatureAt").ValueKind);
        Assert.Equal(7, draft.GetProperty("wizardMaxStep").GetInt32());
        var detail = await ExpectAsync(await client.GetAsync($"/api/leases/{id}"), HttpStatusCode.OK);
        Assert.Equal("Draft", detail.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("sentForSignatureAt").ValueKind);

        // Marking again changes nothing; taking it back leaves the confirmation as it was.
        var again = await ExpectAsync(await client.PostAsync($"/api/leases/{id}/sent", Body("{}")), HttpStatusCode.OK);
        Assert.Equal(sent.GetProperty("version").GetInt32(), again.GetProperty("version").GetInt32());
        var revoked = await ExpectAsync(await client.DeleteAsync($"/api/leases/{id}/sent"), HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, revoked.GetProperty("sentForSignatureAt").ValueKind);
        Assert.Equal(["not_sent"], NeedCodes(revoked.GetProperty("flow"), 7));
        Assert.Equal("Done", StateOf(revoked.GetProperty("flow"), 6));
    }

    [Fact]
    public async Task Sent_AValueChangedAfterwards_WithdrawsTheConfirmationAndTheDownloadWithIt()
    {
        using var client = _world.OwnerClient(factory);
        var (id, _) = await CompleteAsync(client, _world.P1);
        await ExpectAsync(await client.PostAsync($"/api/leases/{id}/review/confirm", Body("{}")), HttpStatusCode.OK);
        var sent = await ExpectAsync(await client.PostAsync($"/api/leases/{id}/sent", Body("{}")), HttpStatusCode.OK);

        // The copy handed out for signature is of a contract the review no longer stands behind.
        var saved = await PatchAsync(client, id, sent.GetProperty("version").GetInt32(), 3, """{"monthlyRent":950}""");

        Assert.Equal("Todo", StateOf(saved.GetProperty("flow"), 6));
        Assert.Equal("Locked", StateOf(saved.GetProperty("flow"), 7));
        var draft = await ExpectAsync(await client.GetAsync($"/api/leases/{id}/draft"), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("reviewConfirmedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("sentForSignatureAt").ValueKind);
        await ExpectProblemAsync(await client.PostAsync($"/api/leases/{id}/sent", Body("{}")), HttpStatusCode.UnprocessableEntity, "lease_review_not_confirmed");
    }
}
