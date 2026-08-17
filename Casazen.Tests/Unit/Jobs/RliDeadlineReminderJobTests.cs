using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Jobs;

public class RliDeadlineReminderJobTests
{
    [Fact]
    public async Task ExecuteAsync_T15_SendsOnce_SecondRunIdempotent()
    {
        await using var db = CreateDb();
        var lease = SeedSignedLease(db, daysUntilDeadline: 15, extraEu: false, landlordEmail: "host@example.com");
        await db.SaveChangesAsync();
        var email = new Mock<IEmailService>();
        email.Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));
        var job = new RliDeadlineReminderJob(db, email.Object, Mock.Of<ILogger<RliDeadlineReminderJob>>());

        await job.ExecuteAsync();
        await job.ExecuteAsync();

        email.Verify(s => s.SendEmailAsync("host@example.com", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        Assert.Equal(1, db.LeaseEvents.Count(e =>
            e.LeaseContractId == lease.Id
            && e.EventType == LeaseEventType.DeadlineReminderSent
            && e.Payload == "t-15"));
    }

    [Fact]
    public async Task ExecuteAsync_ExtraEu_SendsDistinctReminder()
    {
        await using var db = CreateDb();
        SeedSignedLease(db, daysUntilDeadline: 20, extraEu: true, landlordEmail: "host@example.com");
        await db.SaveChangesAsync();
        var email = new Mock<IEmailService>();
        email.Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));
        var job = new RliDeadlineReminderJob(db, email.Object, Mock.Of<ILogger<RliDeadlineReminderJob>>());

        await job.ExecuteAsync();

        email.Verify(s => s.SendEmailAsync(
            "host@example.com",
            It.Is<string>(subj => subj.Contains("Questura", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string>()), Times.Once);
        Assert.Contains(db.LeaseEvents, e => e.Payload == "extra-eu");
    }

    [Fact]
    public async Task ExecuteAsync_EuOnly_NoExtraEuReminder()
    {
        await using var db = CreateDb();
        SeedSignedLease(db, daysUntilDeadline: 20, extraEu: false, landlordEmail: "host@example.com");
        await db.SaveChangesAsync();
        var email = new Mock<IEmailService>();
        email.Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));
        var job = new RliDeadlineReminderJob(db, email.Object, Mock.Of<ILogger<RliDeadlineReminderJob>>());

        await job.ExecuteAsync();

        Assert.DoesNotContain(db.LeaseEvents, e => e.Payload == "extra-eu");
        email.Verify(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options, NullTenantContext.Instance);
    }

    private static LeaseContract SeedSignedLease(AppDbContext db, int daysUntilDeadline, bool extraEu, string landlordEmail)
    {
        var orgId = Guid.NewGuid();
        var org = new OrgEntity { Id = orgId, Name = "Org", Slug = "org", DisplayName = "Org" };
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = "P",
            City = "Milano",
            Address = "Via Roma 1",
        };
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PropertyId = property.Id,
            Property = property,
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = DateTime.UtcNow.Date,
            EndDate = DateTime.UtcNow.Date.AddYears(4),
            MonthlyRent = 1000m,
            RegistrationDeadline = DateTime.UtcNow.Date.AddDays(daysUntilDeadline),
        };
        lease.Parties.Add(new Party
        {
            Role = PartyRole.Landlord,
            FirstName = "Mario",
            LastName = "Rossi",
            FiscalCode = "RSSMRA80A01H501U",
            Citizenship = "IT",
            ContactEmail = landlordEmail,
        });
        lease.Parties.Add(new Party
        {
            Role = PartyRole.Tenant,
            FirstName = "John",
            LastName = "Doe",
            FiscalCode = "XXXXXX00A00A000X",
            Citizenship = extraEu ? "US" : "IT",
            ContactEmail = "tenant@example.com",
            IsExtraEU = extraEu,
        });
        db.Orgs.Add(org);
        db.Properties.Add(property);
        db.LeaseContracts.Add(lease);
        return lease;
    }
}
