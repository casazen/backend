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
    private const string TenantEmail = "tenant@example.test";

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

    [Fact]
    public async Task List_HidesTheDraftsOfTheTenantAndKeepsTheOtherLeases()
    {
        await using var db = Database();
        var draft = Lease("Via Roma", TenantEmail, LeaseStatus.Draft);
        var registered = Lease("Via Milano", TenantEmail, LeaseStatus.Registered);
        db.AddRange(draft, registered);
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await Controller(db, (TenantEmail, true)).List(default));

        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains(registered.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(draft.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(draft.Property.Name, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_IsEmptyWhenTheOnlyLeaseOfTheTenantIsStillADraft()
    {
        await using var db = Database();
        db.Add(Lease("Via Roma", TenantEmail, LeaseStatus.Draft));
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await Controller(db, (TenantEmail, true)).List(default));

        Assert.Equal("""{"contracts":[]}""", JsonSerializer.Serialize(result.Value));
    }

    [Theory]
    [InlineData(LeaseStatus.AwaitingSignature)]
    [InlineData(LeaseStatus.PartiallySigned)]
    [InlineData(LeaseStatus.Signed)]
    [InlineData(LeaseStatus.RegistrationPending)]
    [InlineData(LeaseStatus.SentToProvider)]
    [InlineData(LeaseStatus.Registered)]
    [InlineData(LeaseStatus.Rejected)]
    public async Task List_ShowsEveryLeaseThatIsNotADraft(LeaseStatus status)
    {
        await using var db = Database();
        var lease = Lease("Via Roma", TenantEmail, status);
        db.Add(lease);
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await Controller(db, (TenantEmail, true)).List(default));

        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains(lease.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"\"status\":\"{status}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetContract_AnswersNotFoundForADraftWhoseIdTheTenantKnows_AndServesAnActiveLease()
    {
        await using var db = Database();
        var draft = Lease("Via Roma", TenantEmail, LeaseStatus.Draft);
        var registered = Lease("Via Milano", TenantEmail, LeaseStatus.Registered);
        db.AddRange(draft, registered);
        await db.SaveChangesAsync();
        var controller = Controller(db, (TenantEmail, true));

        Assert.IsType<NotFoundResult>(await controller.GetContract(draft.Id, default));
        var served = Assert.IsType<OkObjectResult>(await controller.GetContract(registered.Id, default));
        Assert.Contains(registered.Id.ToString(), JsonSerializer.Serialize(served.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadDocument_AnswersNotFoundForTheContractOfADraft_AndServesThatOfAnActiveLease()
    {
        await using var db = Database();
        var draft = Lease("Via Roma", TenantEmail, LeaseStatus.Draft);
        var registered = Lease("Via Milano", TenantEmail, LeaseStatus.Registered);
        var draftDocument = Document(draft, "bozza.pdf");
        var registeredDocument = Document(registered, "contratto.pdf");
        db.AddRange(draft, registered, draftDocument, registeredDocument);
        await db.SaveChangesAsync();
        var storage = new Mock<IFileStorage>(MockBehavior.Strict);
        storage.Setup(x => x.OpenReadAsync(StorageBucket.Private, registeredDocument.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([1, 2, 3]));
        var controller = Controller(db, (TenantEmail, true), storage: storage.Object);

        Assert.IsType<NotFoundResult>(await controller.DownloadDocument(draft.Id, draftDocument.Id, default));
        var file = Assert.IsType<FileStreamResult>(await controller.DownloadDocument(registered.Id, registeredDocument.Id, default));
        Assert.Equal("contratto.pdf", file.FileDownloadName);
        storage.Verify(x => x.OpenReadAsync(StorageBucket.Private, draftDocument.StorageKey, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EveryOtherRoute_AnswersNotFoundForADraft_WithoutReachingAnyService()
    {
        await using var db = Database();
        var draft = Lease("Via Roma", TenantEmail, LeaseStatus.Draft);
        db.Add(draft);
        await db.SaveChangesAsync();
        var maintenance = new Mock<ILeaseMaintenanceService>(MockBehavior.Strict);
        var storage = new Mock<IFileStorage>(MockBehavior.Strict);
        var sepa = new Mock<ITenantRentSepaService>(MockBehavior.Strict).Object;
        var controller = Controller(db, (TenantEmail, true), maintenance.Object, storage.Object);
        var otherId = Guid.NewGuid();

        Assert.IsType<NotFoundResult>(await controller.ListMaintenance(draft.Id, default));
        Assert.IsType<NotFoundResult>(await controller.GetMaintenance(draft.Id, otherId, default));
        Assert.IsType<NotFoundResult>(await controller.CreateMaintenance(draft.Id, default));
        Assert.IsType<NotFoundResult>(await controller.DeclareTransfer(draft.Id, otherId, default));
        Assert.IsType<NotFoundResult>(await controller.GetSepa(draft.Id, sepa, default));
        Assert.IsType<NotFoundResult>(await controller.SetupSepa(draft.Id, sepa, default));
        Assert.IsType<NotFoundResult>(await controller.AutomaticSepa(draft.Id, new SepaAutomaticRequest(true), sepa, default));
        Assert.IsType<NotFoundResult>(await controller.PaySepa(draft.Id, otherId, sepa, default));
    }

    private static AppDbContext Database() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static TenantAreaController Controller(
        AppDbContext db,
        (string Email, bool Verified) account,
        ILeaseMaintenanceService? maintenance = null,
        IFileStorage? storage = null)
    {
        var emails = new Mock<IAccountEmailResolver>();
        emails.Setup(x => x.ResolveAsync(It.IsAny<ClaimsPrincipal>(), "auth0|tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        return new TenantAreaController(
            db,
            emails.Object,
            maintenance ?? Mock.Of<ILeaseMaintenanceService>(),
            storage ?? Mock.Of<IFileStorage>(),
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

    // A lease that is already in force unless a test says otherwise: LeaseContract starts as a Draft, which the tenant area never shows.
    private static LeaseContract Lease(string propertyName, string tenantEmail, LeaseStatus status = LeaseStatus.Registered)
    {
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            Status = status,
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

    private static LeaseDocument Document(LeaseContract lease, string fileName) => new()
    {
        LeaseContractId = lease.Id,
        OrgId = lease.OrgId,
        Kind = LeaseDocumentKind.Contract,
        FileName = fileName,
        StorageKey = $"leases/{lease.Id:N}/{fileName}",
        UploadedBy = "auth0|landlord",
    };
}
