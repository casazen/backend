using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US02/T02: the message templates the showcase writes for the demo host, written by the real seed code. The templates of the catalog are
/// valid for the real rules (the service that saves a template of the host accepts every one of them unchanged, and the renderer fills every variable
/// they use), they are marked as showcase data, a template of the host is never touched or replaced, and the reset deletes only the showcase ones,
/// of the org it is run for.
/// </summary>
public class DemoMessageTemplatesSeedTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private DemoEnvironmentService Seeder() => new(
        _db,
        Mock.Of<IFileStorage>(),
        Mock.Of<IPropertyComplianceStatusService>(),
        Options.Create(new ComplianceOptions()),
        Mock.Of<IDemoEnvironmentGate>(),
        NullLogger<DemoEnvironmentService>.Instance,
        new FixedTimeProvider(MessageHubTestKit.Now),
        Mock.Of<IServiceScopeFactory>(),
        Mock.Of<ICrmContactIndex>(),
        Mock.Of<IFeatureFlags>(),
        Mock.Of<IHostEnvironment>(e => e.EnvironmentName == Environments.Development));

    private async Task SeedAllAsync(Guid orgId)
    {
        var seeder = Seeder();
        foreach (var template in DemoShowcaseCatalog.HostMessageTemplates)
            await seeder.CreateHostMessageTemplateAsync(orgId, template, CancellationToken.None);
    }

    [Fact]
    public void TheCatalog_HasTemplatesInBothLanguages_AllSayingTest_AndTogetherUsingEveryVariable()
    {
        var templates = DemoShowcaseCatalog.HostMessageTemplates;
        Assert.Contains(DemoShowcaseCatalog.Scenario.MessageTemplates, DemoShowcaseCatalog.Scenarios);
        Assert.All(templates, t => Assert.Equal(DemoShowcaseCatalog.Scenario.MessageTemplates, t.Scenario));
        Assert.Equal(templates.Count, templates.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(templates.Count, templates.Select(t => (t.Name, t.Language)).Distinct().Count());
        Assert.Contains(templates, t => t.Language == "it");
        Assert.Contains(templates, t => t.Language == "en");

        // A stay with a value for every variable, so a template that names a variable the renderer does not have fails here.
        var values = new MessageTemplateValues(
            "Laura (TEST)", "Villa Lago (TEST)", "TEST-0001", new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 11), new TimeOnly(15, 0), new TimeOnly(10, 0), "Marco (TEST)");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var template in templates)
        {
            Assert.Contains("TEST", template.Name, StringComparison.Ordinal);
            Assert.Contains("TEST", template.Body, StringComparison.Ordinal);
            Assert.InRange(template.Name.Length, 1, MessageTemplate.NameMaxLength);
            Assert.InRange(template.Body.Length, 1, MessageTemplate.BodyMaxLength);
            used.UnionWith(MessageTemplateRenderer.VariablesOf(template.Body));

            var text = MessageTemplateRenderer.Render(template.Body, values, CultureInfo.GetCultureInfo(template.Language));
            Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
            Assert.Contains("TEST", text, StringComparison.Ordinal);
        }

        Assert.Equal(MessageTemplateRenderer.Variables.Order(StringComparer.Ordinal), used.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheTemplates_AreSavedUnchanged_ByTheServiceThatSavesTheTemplatesOfTheHost()
    {
        var service = new MessageTemplateService(
            _db,
            Mock.Of<IOctorateApiClient>(),
            new OctorateOrgScope(NullTenantContext.Instance),
            new TestFeatureFlags(FeatureFlags.OctorateConnector),
            NullLogger<MessageTemplateService>.Instance,
            new FixedTimeProvider(MessageHubTestKit.Now));

        foreach (var template in DemoShowcaseCatalog.HostMessageTemplates)
        {
            var saved = await service.CreateAsync(new HostScope(_world.OrgId), template.Name, template.Language, template.Body, true);

            Assert.Equal(template.Name, saved.Name);
            Assert.Equal(template.Language, saved.Language);
            Assert.Equal(template.Body, saved.Body);
        }
    }

    [Fact]
    public async Task TheShowcase_WritesTheTemplatesOfTheCatalog_MarksThem_AndNeverTouchesOneTheHostHas()
    {
        // The host has a template with the name and language of the first one of the showcase, and another of its own.
        var first = DemoShowcaseCatalog.HostMessageTemplates[0];
        _db.MessageTemplates.AddRange(
            new MessageTemplate { OrgId = _world.OrgId, Name = first.Name, Language = first.Language, Body = "Il mio testo" },
            new MessageTemplate { OrgId = _world.OrgId, Name = "Check-in autonomo", Language = "it", Body = "Ciao {{guest_name}}" });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await SeedAllAsync(_world.OrgId);

        var rows = await _db.MessageTemplates.AsNoTracking().Where(t => t.OrgId == _world.OrgId).ToListAsync();
        Assert.Equal(DemoShowcaseCatalog.HostMessageTemplates.Count + 1, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsActive));
        Assert.Equal("Il mio testo", Assert.Single(rows, r => r.Name == first.Name && r.Language == first.Language).Body);

        // Every showcase template but the one the host already had is marked; the rows of the host are not.
        var markers = await _db.TestDataMarkers.AsNoTracking().Where(m => m.EntityType == TestDataEntities.MessageTemplate).ToListAsync();
        Assert.Equal(DemoShowcaseCatalog.HostMessageTemplates.Count - 1, markers.Count);
        Assert.All(markers, m =>
        {
            Assert.Equal(TestDataOrigins.Showcase, m.Origin);
            Assert.Equal(_world.OrgId, m.OrgId);
        });
        var marked = rows.Where(r => markers.Any(m => m.EntityId == r.Id)).ToList();
        Assert.DoesNotContain(marked, r => r.Body == "Il mio testo" || r.Name == "Check-in autonomo");
        Assert.Equal(
            DemoShowcaseCatalog.HostMessageTemplates.Skip(1).Select(t => t.Key).Order(StringComparer.Ordinal),
            markers.Select(m => m.TemplateKey!).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// PROOF TEST: data rule (the reset deletes only showcase data). The reset of one org deletes the showcase templates of that org and their markers,
    /// and nothing else: not a template the host wrote (it has no marker), not the showcase templates of another org. Run again after the removal, the
    /// seed writes the same templates.
    /// </summary>
    [Fact]
    public async Task DemoReset_RemovesOnlyTheShowcaseTemplatesOfTheOrg_Test()
    {
        var otherOrg = Guid.NewGuid();
        await SeedAllAsync(_world.OrgId);
        await SeedAllAsync(otherOrg);
        _db.MessageTemplates.Add(new MessageTemplate { OrgId = _world.OrgId, Name = "Mio", Language = "it", Body = "Ciao" });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await Seeder().RemoveShowcaseMessageTemplatesAsync(_world.OrgId, CancellationToken.None);

        Assert.Equal(["Mio"], (await _db.MessageTemplates.AsNoTracking().Where(t => t.OrgId == _world.OrgId).ToListAsync()).Select(t => t.Name));
        Assert.Empty(await _db.TestDataMarkers.AsNoTracking().Where(m => m.OrgId == _world.OrgId && m.EntityType == TestDataEntities.MessageTemplate).ToListAsync());
        Assert.Equal(DemoShowcaseCatalog.HostMessageTemplates.Count, await _db.MessageTemplates.AsNoTracking().CountAsync(t => t.OrgId == otherOrg));
        Assert.Equal(
            DemoShowcaseCatalog.HostMessageTemplates.Count,
            await _db.TestDataMarkers.AsNoTracking().CountAsync(m => m.OrgId == otherOrg && m.EntityType == TestDataEntities.MessageTemplate));

        await SeedAllAsync(_world.OrgId);

        var again = await _db.MessageTemplates.AsNoTracking().Where(t => t.OrgId == _world.OrgId).Select(t => t.Name).ToListAsync();
        Assert.Equal(DemoShowcaseCatalog.HostMessageTemplates.Count + 1, again.Count);
        Assert.Equal(again.Count, again.Distinct(StringComparer.Ordinal).Count());
    }
}
