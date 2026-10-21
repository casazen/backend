using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class LeaseDocumentsControllerTests
{
    [Fact]
    public async Task ForbiddenLease_CannotListUploadOrDownloadAttachments()
    {
        var id = Guid.NewGuid();
        var workflow = new Mock<ILeaseWorkflowService>();
        workflow.Setup(s => s.GetLeaseDetailAsync(id)).ReturnsAsync(new LeaseContract
        { Id = id, OrgId = Guid.NewGuid(), Property = new Property { OwnerId = "another-owner" } });
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(AuthorizationResult.Failed());
        var documents = new Mock<ILeaseDocumentService>();
        var controller = new LeasesController(workflow.Object, Mock.Of<IComuneImuNotificationService>(), Mock.Of<ICedolareAdvisoryService>(),
            Mock.Of<IRliExportService>(), Mock.Of<IRliChecklistService>(), Mock.Of<IHostResourceLookup>(), auth.Object,
            Mock.Of<IOrgContextResolver>(), Mock.Of<IHostScopeResolver>(), Mock.Of<IStringLocalizer<SharedResources>>())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<ForbidResult>(await controller.GetDocuments(id, documents.Object, default));
        Assert.IsType<ForbidResult>(await controller.UploadDocument(id, new LeaseAttachmentForm(), documents.Object, default));
        Assert.IsType<ForbidResult>(await controller.DownloadDocument(id, Guid.NewGuid(), documents.Object, default));
        Assert.Empty(documents.Invocations);
    }
}
