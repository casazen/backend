using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public sealed class TenantAreaControllerTests
{
    [Fact]
    public async Task List_ReturnsOnlyLeasesWhoseTenantMatchesTheVerifiedAccountEmail()
    {
        await using var db = Database();
        var visible = Lease("Via Roma", "tenant@example.test");
        var hidden = Lease("Via Milano", "other@example.test");
        db.AddRange(visible, hidden);
        await db.SaveChangesAsync();

        var controller = Controller(db, ("TENANT@example.test", true));

        var result = Assert.IsType<OkObjectResult>(await controller.List(default));
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains(visible.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hidden.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_RejectsAnUnverifiedEmailBeforeReadingLeases()
    {
        await using var db = Database();
        db.Add(Lease("Via Roma", "tenant@example.test"));
        await db.SaveChangesAsync();

        var result = await Controller(db, ("tenant@example.test", false)).List(default);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
    }

    private static AppDbContext Database() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static TenantAreaController Controller(AppDbContext db, (string Email, bool Verified) account)
    {
        var emails = new Mock<IAccountEmailResolver>();
        emails.Setup(x => x.ResolveAsync(It.IsAny<ClaimsPrincipal>(), "auth0|tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        return new TenantAreaController(
            db,
            emails.Object,
            Mock.Of<ILeaseMaintenanceService>(),
            Mock.Of<IFileStorage>(),
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "auth0|tenant")], "test")),
                    RequestServices = services,
                },
            },
        };
    }

    private static LeaseContract Lease(string propertyName, string tenantEmail)
    {
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            Property = new Property { Id = Guid.NewGuid(), Name = propertyName, Address = propertyName },
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            CurrentTermEndDate = new DateTime(2030, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        };
        lease.PropertyId = lease.Property.Id;
        lease.Property.OrgId = lease.OrgId;
        lease.Parties.Add(new Party
        {
            LeaseContractId = lease.Id,
            Role = PartyRole.Tenant,
            ContactEmail = tenantEmail,
            FirstName = "Test",
            LastName = "Tenant",
            FiscalCode = "RSSMRA80A01H501U",
            Citizenship = "IT",
        });
        return lease;
    }
}
