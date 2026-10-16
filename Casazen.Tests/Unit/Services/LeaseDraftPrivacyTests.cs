using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-12: the anonymization of the parties (LT-12) takes the new data with it — the residence, the day the documents were checked and the
/// values the landlord typed in the wizard — and the readers that act on a date or send an e-mail leave a draft still being typed alone.
/// </summary>
public class LeaseDraftPrivacyTests
{
    [Fact]
    public void AnonymizeParty_ClearsTheResidenceAndTheDayTheDocumentsWereChecked()
    {
        var party = new Party
        {
            Role = PartyRole.Tenant,
            FirstName = "Giulia",
            LastName = "Verdi",
            FiscalCode = "VRDGLI85B02F205A",
            Citizenship = "IT",
            ContactEmail = "giulia@example.com",
            Residence = "Via Riservata 9, Bari",
            DocumentsVerifiedOn = new DateOnly(2026, 10, 1),
        };

        LeasePartyPrivacyService.AnonymizeParty(party, new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc));

        Assert.Null(party.Residence);
        Assert.Null(party.DocumentsVerifiedOn);
        Assert.Equal(LeasePartyPrivacyService.AnonymizedValue, party.LastName);
        Assert.NotNull(party.AnonymizedAt);
    }

    [Fact]
    public async Task AnErasureRequest_OnAnEndedLease_AlsoRemovesWhatWasTypedInTheWizard()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var now = new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
        var leaseId = Guid.NewGuid();
        await using (var seed = new AppDbContext(options))
        {
            var org = new OrgEntity { Name = "Org", Slug = $"org-{Guid.NewGuid():N}", ContactEmail = "org@example.com" };
            seed.Orgs.Add(org);
            var property = new Property
            {
                OrgId = org.Id,
                OwnerId = "auth0|owner",
                Name = "Casa",
                Address = "Via Roma 1",
                City = "Bari",
                PostalCode = "70121",
                Bedrooms = 1,
                Bathrooms = 1,
            };
            seed.Properties.Add(property);
            seed.LeaseContracts.Add(new LeaseContract
            {
                Id = leaseId,
                PropertyId = property.Id,
                OrgId = org.Id,
                Status = LeaseStatus.Registered,
                StartDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate = new DateTime(2023, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                MonthlyRent = 900m,
                DraftData = """{"landlords":[{"firstName":"Mario"}]}""",
                Parties =
                [
                    new Party
                    {
                        Role = PartyRole.Landlord,
                        FirstName = "Mario",
                        LastName = "Rossi",
                        FiscalCode = "RSSMRA80A01H501U",
                        Citizenship = "IT",
                        ContactEmail = "mario@example.com",
                        Residence = "Via Segretissima 7, Bari",
                        DocumentsVerifiedOn = new DateOnly(2019, 12, 1),
                    },
                ],
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var service = new LeasePartyPrivacyService(
                db,
                Options.Create(new GdprOptions()),
                new FakeTimeProvider(now),
                NullLogger<LeasePartyPrivacyService>.Instance);

            var result = await service.RequestErasureAsync(leaseId);

            Assert.Equal(LeaseErasureStatus.Anonymized, result.Status);
        }

        await using var read = new AppDbContext(options);
        var lease = await read.LeaseContracts.AsNoTracking().Include(l => l.Parties).SingleAsync(l => l.Id == leaseId);
        Assert.Null(lease.DraftData);
        var party = Assert.Single(lease.Parties);
        Assert.Null(party.Residence);
        Assert.Null(party.DocumentsVerifiedOn);
        Assert.Equal(LeasePartyPrivacyService.AnonymizedValue, party.FirstName);
    }
}
