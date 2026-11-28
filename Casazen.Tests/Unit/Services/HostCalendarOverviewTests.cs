using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-10b, the one proof the brief asks for: the export link of a property shows the dates taken to whoever has it, so the overview of the
/// calendars hands it only to who may read the property (the action asks for <c>property.read</c> of the short-rent area, like the single
/// <c>export-url</c> does) and only for the properties of the caller's scope, never for a property the collaborator «Solo alcuni» was not
/// given nor for another org's.
/// </summary>
public class HostCalendarOverviewTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task GetCalendarOverviewAsync_ExportLinks_OnlyWithPropertyReadAndOnlyForThePropertiesOfTheScope()
    {
        var action = typeof(PropertiesController).GetMethod(nameof(PropertiesController.GetCalendarOverview));
        Assert.NotNull(action);
        Assert.Contains(CasazenPolicies.PropertyRead, action.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));

        var tokens = new Dictionary<Guid, Guid>();
        foreach (var property in new[] { _world.Granted, _world.Hidden, _world.OtherOrgProperty })
        {
            var export = new PropertyICalExport { PropertyId = property.Id, OrgId = property.OrgId };
            _db.PropertyICalExports.Add(export);
            tokens[property.Id] = export.ExportToken;
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var restricted = await HostScopePoints.Dashboard(_db).GetCalendarOverviewAsync(_world.Restricted);
        var orgWide = await HostScopePoints.Dashboard(_db).GetCalendarOverviewAsync(_world.OrgWide);

        var granted = Assert.Single(restricted.Properties);
        Assert.Equal(_world.Granted.Id, granted.PropertyId);
        Assert.Equal(tokens[_world.Granted.Id], granted.ExportToken);
        Assert.Single(granted.Feeds);

        Assert.Equal(
            new[] { _world.Granted.Id, _world.Hidden.Id }.Order(),
            orgWide.Properties.Select(p => p.PropertyId).Order());
        Assert.Equal(
            new[] { tokens[_world.Granted.Id], tokens[_world.Hidden.Id] }.Order(),
            orgWide.Properties.Select(p => p.ExportToken!.Value).Order());
        Assert.DoesNotContain(tokens[_world.OtherOrgProperty.Id], orgWide.Properties.Select(p => p.ExportToken!.Value));
    }
}
