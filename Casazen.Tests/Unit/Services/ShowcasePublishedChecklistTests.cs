using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-13a: the step "Pubblica la vetrina" of the console checklist reads the real publication. A supplier that never used the editor keeps
/// the plain page of SU-13 (online for every active supplier with an address), so its step stays done; one that did is "published" only
/// when it published something that is not paused.
/// </summary>
public class ShowcasePublishedChecklistTests
{
    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task ShowcasePublished_FollowsThePublication(bool exists, bool published, bool paused, bool expected)
    {
        var checklist = await Service(exists, published, paused, SupplierStatus.Active).GetChecklistAsync(OrgId);

        Assert.Equal(expected, checklist!.ShowcasePublished);
    }

    [Fact]
    public async Task ShowcasePublished_ASupplierThatIsNotActive_HasNoPublishedPage_WhateverItPublished()
    {
        var checklist = await Service(exists: true, published: true, paused: false, SupplierStatus.Pending).GetChecklistAsync(OrgId);

        Assert.False(checklist!.ShowcasePublished);
    }

    private static SupplierTodayService Service(bool exists, bool published, bool paused, SupplierStatus status)
    {
        var suppliers = new Mock<ISupplierService>();
        suppliers
            .Setup(s => s.GetProfileAsync(OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SupplierProfile { OrgId = OrgId, Status = status, ShowcaseSlug = "fornitore" });
        suppliers
            .Setup(s => s.GetDashboardStatsAsync(OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SupplierDashboard(100, status.ToString(), 1.0, "None", null, null, null, "None", DateTime.UtcNow));

        var catalog = new Mock<ISupplierServiceCatalogService>();
        catalog.Setup(c => c.CountActiveAsync(OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var agenda = new Mock<ISupplierAgendaService>();
        agenda.Setup(a => a.GetHoursAsync(OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(new SupplierHours([], null));

        var showcase = new Mock<ISupplierShowcaseContentService>();
        showcase
            .Setup(s => s.GetPublicationAsync(OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShowcasePublication(exists, published, paused, null, null));

        return new SupplierTodayService(
            Mock.Of<ISupplierServiceRequestReader>(),
            Mock.Of<ISupplierKpiService>(),
            suppliers.Object,
            catalog.Object,
            agenda.Object,
            showcase: showcase.Object);
    }
}
