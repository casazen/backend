using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-07a: <c>GET api/long-rent/properties</c> over the real pipeline, on the world of <see cref="LongRentWorld"/> ("today" is 2026-10-09 in
/// Rome). Two rules where a mistake costs the landlord or somebody else: the list reaches only the properties of the caller (the colleague's
/// property is not the owner's, and the other way round), and it says who holds each property and from when it is free without naming
/// a tenant; and "then free" is said only when the lease does not go on, never for a lease that renews by itself.
/// </summary>
public class LongRentPropertiesIntegrationTests(LongRentAggregatesFactory factory) : IClassFixture<LongRentAggregatesFactory>, IAsyncLifetime
{
    private LongRentWorld _world = null!;

    public async Task InitializeAsync()
    {
        _world = await LongRentWorld.SeedAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // P1: an APE with its code and class, and the cadastral data of the contract. P2: an APE whose code and class are not on record, and a
        // change of mode programmed. P3: nothing.
        db.PropertyDocuments.Add(Ape(_world.P1, "12345-678", "b"));
        db.PropertyDocuments.Add(Ape(_world.P2, null, null));
        var p1 = await db.Properties.SingleAsync(p => p.Id == _world.P1);
        p1.CadastralSheet = "12";
        p1.CadastralParcel = "345";
        p1.CadastralCategory = "A/2";
        p1.CadastralIncome = 540m;
        db.PropertyModeChanges.Add(new PropertyModeChange
        {
            OrgId = _world.OrgId,
            PropertyId = _world.P2,
            FromMode = RentalMode.Long,
            ToMode = RentalMode.Short,
            EffectiveDate = LongRentWorld.Day("2026-12-01"),
            CreatedByUserId = _world.OwnerId,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task List_StaysInTheReachOfTheCaller_AndSaysWhoHoldsEachPropertyWithoutNamingAnyone()
    {
        using var owner = _world.OwnerClient(factory);
        using var colleague = _world.ColleagueClient(factory);

        var ownerBody = await owner.GetStringAsync("/api/long-rent/properties");
        var colleagueBody = await colleague.GetStringAsync("/api/long-rent/properties");

        var rows = RowsOf(ownerBody);
        Assert.Equal(new[] { "Bilocale Sparano", "Trilocale Murat" }, rows.Select(r => r.GetProperty("name").GetString()).ToArray());
        Assert.Equal(new[] { "Casa Corso" }, RowsOf(colleagueBody).Select(r => r.GetProperty("name").GetString()).ToArray());

        // P1: held by the lease that started last of the three in force; it renews by itself, so there is no "then free".
        var first = rows[0];
        Assert.Equal("Rented", first.GetProperty("status").GetString());
        Assert.Equal(_world.Active, first.GetProperty("currentLease").GetProperty("leaseId").GetGuid());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("currentLease").GetProperty("freeFrom").ValueKind);
        Assert.Equal(("Identified", true, "B", true), (first.GetProperty("ape").GetString(), first.GetProperty("apeValid").GetBoolean(), first.GetProperty("apeClass").GetString(), first.GetProperty("cadastralComplete").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("scheduledModeChange").ValueKind);

        // P2: held by the transitory lease, which does not renew: free again the day after its last day. Its APE is to be confirmed, and a
        // change of mode is programmed.
        var second = rows[1];
        Assert.Equal(_world.Transitory, second.GetProperty("currentLease").GetProperty("leaseId").GetGuid());
        Assert.Equal("2027-03-01", second.GetProperty("currentLease").GetProperty("freeFrom").GetString());
        Assert.Equal(("ToConfirm", false, false), (second.GetProperty("ape").GetString(), second.GetProperty("apeValid").GetBoolean(), second.GetProperty("cadastralComplete").GetBoolean()));
        Assert.Equal("Short", second.GetProperty("scheduledModeChange").GetProperty("toMode").GetString());

        // Nobody is named: no tenant, no e-mail, no amount of any lease.
        foreach (var body in new[] { ownerBody, colleagueBody })
        {
            foreach (var name in new[] { "Giulia", "Verdi", "Sara", "Gallo", "Valentina", "Marra", "Marco", "Bianchi", "@tenants.example", "monthlyRent" })
                Assert.DoesNotContain(name, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task List_Views_NarrowByWhetherALeaseHoldsTheProperty_AndAnUnknownViewIs400()
    {
        using var owner = _world.OwnerClient(factory);

        Assert.Equal(2, RowsOf(await owner.GetStringAsync("/api/long-rent/properties?view=Rented")).Count);
        Assert.Empty(RowsOf(await owner.GetStringAsync("/api/long-rent/properties?view=Free")));
        Assert.Single(RowsOf(await owner.GetStringAsync("/api/long-rent/properties?q=murat")));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/long-rent/properties?view=Libero")).StatusCode);
    }

    private PropertyDocument Ape(Guid propertyId, string? code, string? energyClass) => new()
    {
        PropertyId = propertyId,
        OrgId = _world.OrgId,
        FileName = "ape.pdf",
        StorageUrl = $"ape/{propertyId:N}.pdf",
        DocumentType = DocumentType.Ape,
        UploadedBy = _world.OwnerId,
        ApeCode = code,
        ApeEnergyClass = energyClass,
    };

    private static List<JsonElement> RowsOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateArray().Select(row => row.Clone()).ToList();
    }
}
