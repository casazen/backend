using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-12: the guided draft of a lease on the EF InMemory database, with a clock the test moves and the collaborators of the service
/// (the calculator of the canone concordato, the APE check, the contract template, the storage) as mocks. "Today" in Rome is 9 October
/// 2026. The org has a property with its cadastral data and an energy certificate, so the first step has nothing missing.
/// </summary>
internal sealed class LeaseDraftHarness : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    public const string OwnerId = "auth0|lr12-owner";

    private readonly DbContextOptions<AppDbContext> _options;

    public LeaseDraftHarness()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        // No Data Protection provider, like every other unit test (see PropertyModeHarness): the encryption has its own test.
        Db = new AppDbContext(_options);
        Clock = new FakeTimeProvider(Now);
        Ape.Setup(a => a.EnsurePropertyHasValidApeAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        Templates.Setup(t => t.GetFinalContractBlocker(It.IsAny<LeaseContract>())).Returns((string?)null);
        Storage.Setup(s => s.DeleteAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Service = NewService(Db);
    }

    public AppDbContext Db { get; }

    public FakeTimeProvider Clock { get; }

    public Mock<ICanoneConcordatoEligibilityService> Eligibility { get; } = new();

    public Mock<IApeComplianceService> Ape { get; } = new();

    public Mock<ILeaseTemplateService> Templates { get; } = new();

    public Mock<IFileStorage> Storage { get; } = new();

    public LeaseDraftService Service { get; }

    public Guid OrgId { get; private set; }

    public Guid PropertyId { get; private set; }

    /// <summary>A service on another context over the same database, as a second request, a second tab or a second device would have.</summary>
    public LeaseDraftService NewService(AppDbContext? db = null)
    {
        db ??= new AppDbContext(_options);
        return new LeaseDraftService(
            db,
            Eligibility.Object,
            Ape.Object,
            Templates.Object,
            new LeaseDraftDeleter(db, Storage.Object, NullLogger<LeaseDraftDeleter>.Instance),
            NullLogger<LeaseDraftService>.Instance,
            Clock);
    }

    public AppDbContext SecondContext() => new(_options);

    public void Dispose() => Db.Dispose();

    // ---- seed ----------------------------------------------------------------------------------------------------

    /// <summary>The org, and a property with its cadastral data and an APE: nothing is missing at step 1.</summary>
    public async Task<LeaseDraftHarness> SeedAsync()
    {
        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Org", Slug = $"org-{Guid.NewGuid():N}", ContactEmail = "host@org.test", FiscalCode = "RSSMRA80A01H501U" };
        Db.Orgs.Add(org);
        OrgId = org.Id;
        PropertyId = (await SeedPropertyAsync(withApe: true, withCadastre: true)).Id;
        return this;
    }

    public async Task<Property> SeedPropertyAsync(
        bool withApe = true, bool withCadastre = true, RentalMode mode = RentalMode.Long, Guid? orgId = null)
    {
        var property = new Property
        {
            OrgId = orgId ?? OrgId,
            OwnerId = OwnerId,
            Name = "Bilocale Sparano",
            Description = "Long-term property of the draft tests",
            Address = $"Via Sparano {Guid.NewGuid():N}",
            City = "Bari",
            PostalCode = "70121",
            Bedrooms = 2,
            Bathrooms = 1,
            RentalMode = mode,
            IsActive = true,
            CadastralSheet = withCadastre ? "14" : null,
            CadastralParcel = withCadastre ? "233" : null,
            CadastralCategory = withCadastre ? "A/2" : null,
            CadastralIncome = withCadastre ? 640m : null,
        };
        Db.Properties.Add(property);
        if (withApe)
        {
            Db.PropertyDocuments.Add(new PropertyDocument
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                FileName = "ape.pdf",
                StorageUrl = $"properties/{property.Id}/documents/ape.pdf",
                DocumentType = DocumentType.Ape,
                UploadedBy = OwnerId,
            });
        }

        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return property;
    }

    // ---- shortcuts -----------------------------------------------------------------------------------------------

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public Task<LeaseDraftSaved> SaveAsync(Guid leaseId, int version, int step, string json) =>
        Service.SaveAsync(leaseId, version, step, Json(json));

    /// <summary>A draft with only the property, as the wizard creates it.</summary>
    public async Task<LeaseDraftCreated> CreateAsync(LeaseOrigin origin = LeaseOrigin.Created) =>
        await Service.CreateAsync(PropertyId, origin);

    /// <summary>The lease as the database has it now, with its parties and events, read on a context of its own.</summary>
    public async Task<LeaseContract> LoadAsync(Guid leaseId)
    {
        await using var db = SecondContext();
        return await db.LeaseContracts.AsNoTracking()
            .Include(l => l.Parties)
            .Include(l => l.Events)
            .SingleAsync(l => l.Id == leaseId);
    }

    /// <summary>Saves every data step with valid values (one landlord, one tenant, a canone libero from 1 November 2026) and returns the new version.</summary>
    public async Task<(LeaseDraftCreated Created, int Version)> CompleteAsync(string tenantFiscalCode = "VRDGLI85B02F205A")
    {
        var created = await CreateAsync();
        var version = created.Version;
        version = (await SaveAsync(created.Id, version, 1, $$"""{"propertyId":"{{PropertyId}}"}""")).Version;
        version = (await SaveAsync(created.Id, version, 2, $$"""
            {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"RSSMRA80A01H501U","citizenship":"IT","residence":"Bari, via Sparano 1","contactEmail":"mario@example.com"}],
             "tenants":[{"firstName":"Giulia","lastName":"Verdi","fiscalCode":"{{tenantFiscalCode}}","citizenship":"IT","residence":"Bari, via Argiro 7"}]}
            """)).Version;
        version = (await SaveAsync(created.Id, version, 3, """{"contractType":"Libero","taxRegime":"CedolareSecca","monthlyRent":900,"securityDeposit":1800}""")).Version;
        version = (await SaveAsync(created.Id, version, 4, """{"startDate":"2026-11-01"}""")).Version;
        version = (await SaveAsync(created.Id, version, 5, "{}")).Version;
        return (created, version);
    }

    /// <summary>The range the calculator gives for a concordato: available, with the given monthly bounds.</summary>
    public void SetupConcordatoRange(decimal minMonthly, decimal maxMonthly, bool indicative = false) =>
        Eligibility
            .Setup(e => e.CalculateAsync(It.IsAny<Guid>(), It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanoneConcordatoEligibilityDto(
                Available: true,
                Reason: null,
                Comune: "Monza",
                Zone: "Centro",
                SubFascia: 2,
                CanoneMinAnnuo: minMonthly * 12,
                CanoneMaxAnnuo: maxMonthly * 12,
                CanoneMinMensile: minMonthly,
                CanoneMaxMensile: maxMonthly,
                DataCompleteness: indicative ? DataCompleteness.Partial : DataCompleteness.Complete,
                ImuAppliesTheoretical: false,
                AtaApplies: false,
                AttestationRequired: false,
                Disclaimer: string.Empty)
            {
                ContractYears = 3,
                UsableSqm = 70m,
                Indicative = indicative,
            });

    /// <summary>The calculator has no range: the reason it gives.</summary>
    public void SetupConcordatoUnavailable(string reasonCode = CanoneConcordatoReasonCodes.DataUnavailable) =>
        Eligibility
            .Setup(e => e.CalculateAsync(It.IsAny<Guid>(), It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanoneConcordatoEligibilityDto(
                Available: false,
                Reason: "no data",
                Comune: "Bari",
                Zone: null,
                SubFascia: null,
                CanoneMinAnnuo: null,
                CanoneMaxAnnuo: null,
                CanoneMinMensile: null,
                CanoneMaxMensile: null,
                DataCompleteness: DataCompleteness.Missing,
                ImuAppliesTheoretical: false,
                AtaApplies: false,
                AttestationRequired: false,
                Disclaimer: string.Empty)
            {
                ReasonCode = reasonCode,
            });

    public static readonly string ConcordatoStep3 =
        """{"contractType":"Concordato","taxRegime":"CedolareSecca","monthlyRent":900,"concordato":{"sqm":70,"typeAElementCount":2}}""";
}
