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
            .Concat(DemoShowcaseCatalog.HostInvites.Select(i => i.Scenario))
            .Concat(DemoShowcaseCatalog.PropertyOwners.Select(o => o.Scenario))
            .Concat(DemoShowcaseCatalog.GuestSegments.Select(s => s.Scenario))
            .Concat(DemoShowcaseCatalog.Campaigns.Select(c => c.Scenario))
            .Concat(DemoShowcaseCatalog.AgencyInvoices.Select(inv => inv.Scenario))
            .Concat(DemoShowcaseCatalog.StaffMembers.Select(staff => staff.Scenario))
            .Concat(DemoShowcaseCatalog.Suppliers.Select(sup => sup.Scenario))
            .Concat(DemoShowcaseCatalog.CheckIns.Select(ci => ci.Scenario))
            .Concat(DemoShowcaseCatalog.AlloggiatiReports.Select(ar => ar.Scenario))
            .Concat(DemoShowcaseCatalog.Statements.Select(st => st.Scenario))
            .Concat(DemoShowcaseCatalog.WithholdingSummaries.Select(wh => wh.Scenario))
            .Concat(DemoShowcaseCatalog.F24Records.Select(f24 => f24.Scenario))
            .Concat(DemoShowcaseCatalog.WireBatches.Select(wb => wb.Scenario))
            .Concat(DemoShowcaseCatalog.Routines.Select(rt => rt.Scenario))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(DemoShowcaseCatalog.Scenarios, scenario => Assert.Contains(scenario, covered));
        Assert.Contains(DemoShowcaseCatalog.Properties, p => p.Publish);
        Assert.Contains(DemoShowcaseCatalog.Properties, p => p.Key == DemoShowcaseCatalog.DraftKey && !p.Publish);
        Assert.All(DemoShowcaseCatalog.Properties, p => Assert.Equal(6, p.Place.IstatCode.Length));
    }

    [Fact]
    public void HostInvites_EveryTemplate_IsObviouslyTestData_WithUniqueKeys_AndEveryStateIsCovered()
    {
        var invites = DemoShowcaseCatalog.HostInvites;

        Assert.Equal(invites.Count, invites.Select(i => i.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(invites, i => Assert.EndsWith("@example.com", i.Email, StringComparison.Ordinal));
        Assert.All(invites, i => Assert.True(i.CreatedOffsetDays < 0));
        Assert.Equal(
            Enum.GetValues<DemoHostInviteState>().Order(),
            invites.Select(i => i.State).Distinct().Order());
        Assert.Contains(DemoShowcaseCatalog.Scenario.HostInvitePending, DemoShowcaseCatalog.Scenarios);
    }

    [Fact]
    public void GuestAppAccount_TheDemoGuestOfTheMobileApp_HasUpcomingPastAndCancelledStaysOfThePublicCheckout()
    {
        var stays = DemoShowcaseCatalog.Properties
            .SelectMany(p => p.Bookings.Where(b => b.Scenario == DemoShowcaseCatalog.Scenario.GuestAppAccount))
            .ToList();

        Assert.Contains(DemoShowcaseCatalog.Scenario.GuestAppAccount, DemoShowcaseCatalog.Scenarios);
        // Obviously test data, and the email of the account the store reviewers sign in with (created by hand in Auth0).
        Assert.EndsWith("@example.com", DemoShowcaseCatalog.GuestAppEmail, StringComparison.Ordinal);
        Assert.All(stays, s => Assert.Equal(DemoShowcaseCatalog.GuestAppEmail, s.Email));
        // «Le mie prenotazioni» of the app lists the bookings of the public checkout only, and the demo must not reach Stripe: paid at the property.
        Assert.All(stays, s => Assert.Equal(BookingSource.Direct, s.Source));
        Assert.All(stays, s => Assert.Equal(PaymentOption.OnSite, s.PaymentOption));
        Assert.Equal(stays.Count, stays.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(stays, s => s.CheckInOffsetDays > 0 && s.Status == BookingStatus.Confirmed);
        Assert.Contains(stays, s => s.CheckInOffsetDays < 0 && s.Status == BookingStatus.CheckedOut);
        Assert.Contains(stays, s => s.Status == BookingStatus.Cancelled && s.CancellationReason == BookingCancellationReason.GuestRequested);
    }
}
