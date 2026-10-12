using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Demo;

public class DemoShowcaseTests
{
    [Fact]
    public void Cin_MatchesTheOfficialFormat_AndNamesTheComune_AndSaysTest()
    {
        var cin = DemoCin.ForIstat("058091", 7);

        Assert.Equal("IT058091C2TEST07", cin);
        Assert.True(CinFormat.IsValid(cin));
        Assert.Equal("058091", CinFormat.GetIstatComuneCode(cin));
        Assert.Contains("TEST", cin, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_IsARealPdf_AndSaysItIsTestData()
    {
        var pdf = DemoDocumentPdf.Create("CERTIFICATO CIN DI TEST", DemoDocumentPdf.Marker + " CasaZen");
        var text = System.Text.Encoding.ASCII.GetString(pdf);

        Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
        Assert.Contains("%%EOF", text, StringComparison.Ordinal);
        Assert.Contains(DemoDocumentPdf.Marker, text, StringComparison.Ordinal);
    }

    [Fact]
    public void SafetyChecklist_SatisfiesTheActivationRules()
    {
        var checklist = new PropertySafetyChecklist { OrgId = Guid.NewGuid(), PropertyId = Guid.NewGuid() };
        DemoSafetyChecklist.Apply(checklist, new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));
        foreach (var item in DemoSafetyChecklist.Items(checklist.OrgId, new DateOnly(2026, 10, 12)))
        {
            item.ChecklistId = checklist.Id;
            checklist.Items.Add(item);
        }

        var evaluation = SafetyChecklistRules.Evaluate(checklist);

        Assert.True(evaluation.IsComplete);
        Assert.Empty(evaluation.Blockers);
    }

    [Fact]
    public void RequiredDocuments_FollowTheRegion()
    {
        var configured = new ComplianceOptions().RequiredDocuments;

        Assert.Contains(DocumentType.Ape, DemoRequiredDocuments.ForRegion(configured, "LOM"));
        Assert.Contains(DocumentType.PropertyLicense, DemoRequiredDocuments.ForRegion(configured, "laz"));
        Assert.Equal([DocumentType.CinCertificate], DemoRequiredDocuments.ForRegion(configured, "TOS"));
    }

    [Fact]
    public void Catalog_CoversEveryScenario_WithUniqueKeys()
    {
        var keys = DemoShowcaseCatalog.Properties.Select(p => p.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        var covered = DemoShowcaseCatalog.Properties
            .SelectMany(p => p.Scenarios.Concat(p.Bookings.Select(b => b.Scenario)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(DemoShowcaseCatalog.Scenarios, scenario => Assert.Contains(scenario, covered));
        Assert.Contains(DemoShowcaseCatalog.Properties, p => p.Publish);
        Assert.Contains(DemoShowcaseCatalog.Properties, p => p.Key == DemoShowcaseCatalog.DraftKey && !p.Publish);
        Assert.All(DemoShowcaseCatalog.Properties, p => Assert.Equal(6, p.Place.IstatCode.Length));
    }
}
