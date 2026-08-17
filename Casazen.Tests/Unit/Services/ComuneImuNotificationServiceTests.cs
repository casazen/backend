using System.Security.Claims;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ComuneImuNotificationServiceTests
{
    private const string OwnerId = "auth0|host";

    [Fact]
    public async Task ExportAsync_RegisteredSeveso_ReturnsPdfWithChannelUncertainty()
    {
        var (sut, events) = CreateSut(BuildLease("Seveso", LeaseStatus.Registered));

        var result = await sut.ExportAsync(Guid.NewGuid(), OwnerId);

        Assert.NotNull(result);
        Assert.True(result.PdfBytes.Length > 4);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(result.PdfBytes, 0, 4));
        var text = Encoding.ASCII.GetString(result.PdfBytes);
        Assert.Contains("Incertezza", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NON univoco", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protocollo@comune.seveso.mb.it", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SPID", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("canale ufficiale e'", text, StringComparison.OrdinalIgnoreCase);
        events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e => e.EventType == LeaseEventType.ImuNotificationExported)), Times.Once);
        events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e => e.EventType == LeaseEventType.ImuNotificationMarkedSent)), Times.Never);
    }

    [Fact]
    public async Task ExportAsync_RegisteredCesano_LabelsDerivedImu()
    {
        var (sut, _) = CreateSut(BuildLease("Cesano Maderno", LeaseStatus.Registered));

        var result = await sut.ExportAsync(Guid.NewGuid(), OwnerId);

        Assert.NotNull(result);
        var text = Encoding.ASCII.GetString(result.PdfBytes);
        Assert.Contains("valore derivato", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2025", text);
        Assert.Contains("0,78", text);
        Assert.DoesNotContain("aliquota ufficiale", text.ToLowerInvariant().Replace("non e' un'aliquota ufficiale", ""));
    }

    [Fact]
    public async Task ExportAsync_NotRegistered_ThrowsNotReady()
    {
        var (sut, events) = CreateSut(BuildLease("Seveso", LeaseStatus.Draft));

        await Assert.ThrowsAsync<ImuNotificationNotReadyException>(
            () => sut.ExportAsync(Guid.NewGuid(), OwnerId));
        events.Verify(r => r.AddAsync(It.IsAny<LeaseEvent>()), Times.Never);
    }

    [Fact]
    public async Task ExportAsync_NonCanoneConcordatoLease_ThrowsNotReadyWithoutEvent()
    {
        var (sut, events) = CreateSut(BuildLease(
            "Seveso",
            LeaseStatus.Registered,
            FiscalRegime.CedolareSecca));

        await Assert.ThrowsAsync<ImuNotificationNotReadyException>(
            () => sut.ExportAsync(Guid.NewGuid(), OwnerId));
        events.Verify(r => r.AddAsync(It.IsAny<LeaseEvent>()), Times.Never);
    }

    [Fact]
    public async Task ExportAsync_OtherOwner_ReturnsNull()
    {
        var (sut, events) = CreateSut(BuildLease("Seveso", LeaseStatus.Registered));

        var result = await sut.ExportAsync(Guid.NewGuid(), "auth0|other");

        Assert.Null(result);
        events.Verify(r => r.AddAsync(It.IsAny<LeaseEvent>()), Times.Never);
    }

    [Fact]
    public async Task Controller_Export_NotRegistered_Returns409()
    {
        var imu = new Mock<IComuneImuNotificationService>();
        imu.Setup(s => s.ExportAsync(It.IsAny<Guid>(), OwnerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ImuNotificationNotReadyException());
        var controller = CreateController(imu.Object);

        var result = await controller.ExportImuNotification(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Controller_Export_OtherOwner_Returns404()
    {
        var imu = new Mock<IComuneImuNotificationService>();
        imu.Setup(s => s.ExportAsync(It.IsAny<Guid>(), OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ImuNotificationExportResult?)null);
        var controller = CreateController(imu.Object);

        var result = await controller.ExportImuNotification(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task MarkSentAsync_Registered_EmitsMarkedSentOnly()
    {
        var (sut, events) = CreateSut(BuildLease("Seveso", LeaseStatus.Registered));

        var result = await sut.MarkSentAsync(Guid.NewGuid(), OwnerId);

        Assert.True(result);
        events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e => e.EventType == LeaseEventType.ImuNotificationMarkedSent)), Times.Once);
        events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e => e.EventType == LeaseEventType.ImuNotificationExported)), Times.Never);
    }

    [Fact]
    public async Task MarkSentAsync_NotRegistered_ThrowsAndDoesNotEmit()
    {
        var (sut, events) = CreateSut(BuildLease("Seveso", LeaseStatus.Signed));

        await Assert.ThrowsAsync<ImuNotificationNotReadyException>(
            () => sut.MarkSentAsync(Guid.NewGuid(), OwnerId));
        events.Verify(r => r.AddAsync(It.IsAny<LeaseEvent>()), Times.Never);
    }

    [Fact]
    public async Task MarkSentAsync_NonCanoneConcordatoLease_ThrowsAndDoesNotEmit()
    {
        var (sut, events) = CreateSut(BuildLease(
            "Seveso",
            LeaseStatus.Registered,
            FiscalRegime.RegimeOrdinario));

        await Assert.ThrowsAsync<ImuNotificationNotReadyException>(
            () => sut.MarkSentAsync(Guid.NewGuid(), OwnerId));
        events.Verify(r => r.AddAsync(It.IsAny<LeaseEvent>()), Times.Never);
    }

    [Fact]
    public async Task Controller_MarkSent_OtherOwner_Returns404()
    {
        var imu = new Mock<IComuneImuNotificationService>();
        imu.Setup(s => s.MarkSentAsync(It.IsAny<Guid>(), OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        var controller = CreateController(imu.Object);

        var result = await controller.MarkImuNotificationSent(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Controller_MarkSent_NotRegistered_Returns409()
    {
        var imu = new Mock<IComuneImuNotificationService>();
        imu.Setup(s => s.MarkSentAsync(It.IsAny<Guid>(), OwnerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ImuNotificationNotReadyException());
        var controller = CreateController(imu.Object);

        var result = await controller.MarkImuNotificationSent(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public void Controller_MarkSent_RequiresLeaseRegisterPolicy()
    {
        var method = typeof(LeasesController).GetMethod(nameof(LeasesController.MarkImuNotificationSent));
        var policies = method!.GetCustomAttributes(typeof(AuthorizeAttribute), false)
            .Cast<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToArray();
        Assert.Contains("RequireContext:long-rent:lease.register", policies);
        Assert.DoesNotContain(policies, p => p != null && p.Contains("property.read", StringComparison.Ordinal));
    }

    [Fact]
    public void Controller_Export_UsesClassLeaseRead_NotPropertyRead()
    {
        var method = typeof(LeasesController).GetMethod(nameof(LeasesController.ExportImuNotification));
        var methodPolicies = method!.GetCustomAttributes(typeof(AuthorizeAttribute), false)
            .Cast<AuthorizeAttribute>()
            .Select(a => a.Policy);
        var classPolicies = typeof(LeasesController).GetCustomAttributes(typeof(AuthorizeAttribute), false)
            .Cast<AuthorizeAttribute>()
            .Select(a => a.Policy);
        Assert.Contains("RequireContext:long-rent:lease.read", classPolicies);
        Assert.DoesNotContain(classPolicies.Concat(methodPolicies), p => p != null && p.Contains("property.read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Controller_Export_Registered_ReturnsPdfFile()
    {
        var imu = new Mock<IComuneImuNotificationService>();
        imu.Setup(s => s.ExportAsync(It.IsAny<Guid>(), OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImuNotificationExportResult("%PDF-1.4 test"u8.ToArray(), "comunicazione-imu.pdf"));
        var controller = CreateController(imu.Object);

        var result = await controller.ExportImuNotification(Guid.NewGuid(), CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(file.FileContents, 0, 4));
    }

    private static (ComuneImuNotificationService Sut, Mock<ILeaseEventRepository> Events) CreateSut(LeaseContract lease)
    {
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByIdWithDetailsAsync(It.IsAny<Guid>())).ReturnsAsync(lease);
        var events = new Mock<ILeaseEventRepository>();
        events.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);
        return (new ComuneImuNotificationService(leases.Object, events.Object), events);
    }

    private static LeasesController CreateController(IComuneImuNotificationService imu)
    {
        var controller = new LeasesController(Mock.Of<ILeaseWorkflowService>(), imu);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, OwnerId)], "test")),
            },
        };
        return controller;
    }

    private static LeaseContract BuildLease(
        string city,
        LeaseStatus status,
        FiscalRegime fiscalRegime = FiscalRegime.CanoneConcordato) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        FiscalRegime = fiscalRegime,
        StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2029, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        MonthlyRent = 400m,
        Property = new Property { OwnerId = OwnerId, City = city, Name = "Alloggio" },
    };
}
