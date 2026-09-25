using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>CO-07 (A5-21): saving the D.L. 145/2023 safety checklist of a property.</summary>
public class PropertySafetyChecklistServiceTests
{
    // 2026-09-24 00:30 in Rome is still 2026-09-23 in UTC: "today" for the check dates is the Rome date.
    private static readonly TimeProvider RomeJustAfterMidnight =
        new FixedTimeProvider(new DateTimeOffset(2026, 9, 23, 22, 30, 0, TimeSpan.Zero));

    private static AppDbContext CreateDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static PropertySafetyChecklistService CreateService(AppDbContext db) =>
        new(db, Mock.Of<IPropertyComplianceStatusService>(), NullLogger<PropertySafetyChecklistService>.Instance, RomeJustAfterMidnight);

    [Fact]
    public async Task SaveAsync_AllElectricHome_StoresAnswersAndGasAndCoAreNotApplicable()
    {
        await using var db = CreateDb(nameof(SaveAsync_AllElectricHome_StoresAnswersAndGasAndCoAreNotApplicable));
        var property = await SeedPropertyAsync(db);

        var view = await CreateService(db).SaveAsync(property.Id, "auth0|host", SafetyChecklistTestData.AllElectricInput());

        Assert.True(view.Evaluation.IsComplete);
        var gas = view.Evaluation.Items.Single(i => i.Code == SafetyItemCode.GasDetector);
        Assert.Equal((SafetyItemStatus.NotApplicable, SafetyNotApplicableReason.NoGasNoCombustion), (gas.Status, gas.NotApplicableReason));

        db.ChangeTracker.Clear();
        var stored = await db.PropertySafetyChecklists.Include(c => c.Items).SingleAsync();
        Assert.Equal(property.OrgId, stored.OrgId);
        Assert.Equal(SafetyChecklistRules.Items.Count, stored.Items.Count);
        Assert.All(stored.Items, i => Assert.Equal(property.OrgId, i.OrgId));
        // "Not applicable" is never stored: it follows from the facts.
        Assert.Null(stored.Items.Single(i => i.Code == SafetyItemCode.GasDetector).Answer);
        Assert.Equal((false, false), (stored.Entrepreneurial, stored.HasGasSupply));
        Assert.Equal(new DateTime(2026, 9, 23, 22, 30, 0, DateTimeKind.Utc), stored.ConfirmedAt);
        Assert.Equal(("auth0|host", SafetyChecklistRules.DeclarationTextVersion), (stored.ConfirmedBy, stored.ConfirmedTextVersion));
        Assert.Equal(SafetyChecklistRules.SchemaVersion, stored.SchemaVersion);
    }

    [Fact]
    public async Task SaveAsync_WithoutConfirm_ClearsThePreviousConfirmation()
    {
        await using var db = CreateDb(nameof(SaveAsync_WithoutConfirm_ClearsThePreviousConfirmation));
        var property = await SeedPropertyAsync(db);
        var service = CreateService(db);
        await service.SaveAsync(property.Id, "auth0|host", SafetyChecklistTestData.AllElectricInput(confirm: true));

        var view = await service.SaveAsync(property.Id, "auth0|host", SafetyChecklistTestData.AllElectricInput(confirm: false));

        Assert.Null(view.Checklist!.ConfirmedAt);
        Assert.Equal(["safety_confirmation_missing"], view.Evaluation.Blockers.Select(b => b.Code));
    }

    [Fact]
    public async Task SaveAsync_ImportedAnswerNotAnswered_StaysToReviewUntilTheHostAnswers()
    {
        await using var db = CreateDb(nameof(SaveAsync_ImportedAnswerNotAnswered_StaysToReviewUntilTheHostAnswers));
        var property = await SeedPropertyAsync(db);
        var imported = SafetyChecklistTestData.CompleteAllElectric(property.Id, property.OrgId);
        imported.SchemaVersion = SafetyChecklistRules.LegacySchemaVersion;
        imported.Items.Single(i => i.Code == SafetyItemCode.SystemsCompliance).Answer = SafetyItemAnswer.ToReview;
        imported.Items.Single(i => i.Code == SafetyItemCode.FireExtinguishers).Answer = SafetyItemAnswer.ToReview;
        db.PropertySafetyChecklists.Add(imported);
        await db.SaveChangesAsync();

        var input = SafetyChecklistTestData.AllElectricInput() with { Facts = SafetyChecklistTestData.AllElectricInput().Facts with { Entrepreneurial = true } };
        var view = await CreateService(db).SaveAsync(property.Id, "auth0|host", input);

        var items = view.Checklist!.Items.ToDictionary(i => i.Code);
        Assert.Equal(SafetyItemAnswer.ToReview, items[SafetyItemCode.SystemsCompliance].Answer);
        Assert.Equal(SafetyItemAnswer.Present, items[SafetyItemCode.FireExtinguishers].Answer);
        Assert.Equal(["safety_systems_compliance_review"], view.Evaluation.Blockers.Select(b => b.Code));
        Assert.Equal(SafetyChecklistRules.SchemaVersion, view.Checklist.SchemaVersion);
    }

    [Fact]
    public async Task SaveAsync_ToReviewSentByClient_Rejected()
    {
        await using var db = CreateDb(nameof(SaveAsync_ToReviewSentByClient_Rejected));
        var property = await SeedPropertyAsync(db);
        var input = SafetyChecklistTestData.AllElectricInput() with
        {
            Items = [SafetyChecklistTestData.Item(SafetyItemCode.FireExtinguishers, SafetyItemAnswer.ToReview, quantity: 1)],
        };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService(db).SaveAsync(property.Id, "auth0|host", input));

        Assert.Equal("safety_checklist_invalid", error.Code);
    }

    [Fact]
    public async Task SaveAsync_ExtinguishersPresentWithoutNumber_Rejected()
    {
        await using var db = CreateDb(nameof(SaveAsync_ExtinguishersPresentWithoutNumber_Rejected));
        var property = await SeedPropertyAsync(db);
        var input = SafetyChecklistTestData.AllElectricInput() with
        {
            Items = [SafetyChecklistTestData.Item(SafetyItemCode.FireExtinguishers, SafetyItemAnswer.Present)],
        };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService(db).SaveAsync(property.Id, "auth0|host", input));

        Assert.Equal("safety_extinguisher_quantity_required", error.Code);
        Assert.Empty(db.PropertySafetyChecklists);
    }

    [Fact]
    public async Task SaveAsync_CheckDateAfterTodayInRome_Rejected()
    {
        await using var db = CreateDb(nameof(SaveAsync_CheckDateAfterTodayInRome_Rejected));
        var property = await SeedPropertyAsync(db);
        var service = CreateService(db);
        SafetyChecklistInput WithCheck(DateOnly day) => SafetyChecklistTestData.AllElectricInput() with
        {
            Items = [SafetyChecklistTestData.Item(SafetyItemCode.FireExtinguishers, SafetyItemAnswer.Present, 1, checkedOn: day)],
        };

        // Today in Rome is 2026-09-24 although UTC is still on the 23rd.
        var view = await service.SaveAsync(property.Id, "auth0|host", WithCheck(new DateOnly(2026, 9, 24)));
        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(property.Id, "auth0|host", WithCheck(new DateOnly(2026, 9, 25))));

        Assert.Equal(new DateOnly(2026, 9, 24), view.Checklist!.Items.Single(i => i.Code == SafetyItemCode.FireExtinguishers).CheckedOn);
        Assert.Equal("safety_date_in_future", error.Code);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(21, null)]
    [InlineData(2, new[] { 80.0 })]
    [InlineData(1, new[] { 0.0 })]
    public async Task SaveAsync_InvalidFloors_Rejected(int floors, double[]? areas)
    {
        await using var db = CreateDb($"{nameof(SaveAsync_InvalidFloors_Rejected)}-{floors}-{areas?.Length}-{areas?.FirstOrDefault()}");
        var property = await SeedPropertyAsync(db);
        var input = SafetyChecklistTestData.AllElectricInput() with
        {
            Facts = SafetyChecklistTestData.AllElectricInput().Facts with
            {
                FloorCount = floors,
                FloorAreasSqm = areas?.Select(a => (decimal)a).ToList(),
            },
        };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService(db).SaveAsync(property.Id, "auth0|host", input));

        Assert.Equal("safety_floors_invalid", error.Code);
    }

    [Fact]
    public async Task SaveAsync_EvidenceOfTheProperty_KeptWithItsFileName()
    {
        await using var db = CreateDb(nameof(SaveAsync_EvidenceOfTheProperty_KeptWithItsFileName));
        var property = await SeedPropertyAsync(db);
        var photo = AddDocument(db, property, "estintore.jpg");
        await db.SaveChangesAsync();
        var input = SafetyChecklistTestData.AllElectricInput() with
        {
            Items = [SafetyChecklistTestData.Item(SafetyItemCode.FireExtinguishers, SafetyItemAnswer.Present, 1, evidenceDocumentId: photo.Id)],
        };

        var view = await CreateService(db).SaveAsync(property.Id, "auth0|host", input);

        Assert.Equal(photo.Id, view.Checklist!.Items.Single(i => i.Code == SafetyItemCode.FireExtinguishers).EvidenceDocumentId);
        Assert.Equal("estintore.jpg", view.EvidenceFileNames[photo.Id]);
    }

    [Fact]
    public async Task SaveAsync_EvidenceOfAnotherProperty_Rejected()
    {
        await using var db = CreateDb(nameof(SaveAsync_EvidenceOfAnotherProperty_Rejected));
        var property = await SeedPropertyAsync(db);
        var other = await SeedPropertyAsync(db);
        var foreign = AddDocument(db, other, "altro.pdf");
        await db.SaveChangesAsync();
        var input = SafetyChecklistTestData.AllElectricInput() with
        {
            Items = [SafetyChecklistTestData.Item(SafetyItemCode.FireExtinguishers, SafetyItemAnswer.Present, 1, evidenceDocumentId: foreign.Id)],
        };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService(db).SaveAsync(property.Id, "auth0|host", input));

        Assert.Equal("safety_evidence_not_found", error.Code);
    }

    [Fact]
    public async Task GetAsync_UnknownProperty_ThrowsNotFound()
    {
        await using var db = CreateDb(nameof(GetAsync_UnknownProperty_ThrowsNotFound));

        await Assert.ThrowsAsync<NotFoundException>(() => CreateService(db).GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetAsync_NothingSaved_ReturnsTheBlockersOfAnEmptyChecklist()
    {
        await using var db = CreateDb(nameof(GetAsync_NothingSaved_ReturnsTheBlockersOfAnEmptyChecklist));
        var property = await SeedPropertyAsync(db);

        var view = await CreateService(db).GetAsync(property.Id);

        Assert.Null(view.Checklist);
        Assert.Contains(view.Evaluation.Blockers, b => b.Code == "safety_extinguishers_missing");
    }

    private static PropertyDocument AddDocument(AppDbContext db, Property property, string fileName)
    {
        var document = new PropertyDocument
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            FileName = fileName,
            StorageUrl = $"properties/{property.Id}/documents/{fileName}",
            DocumentType = DocumentType.SafetyCompliance,
            UploadedBy = property.OwnerId,
        };
        db.PropertyDocuments.Add(document);
        return document;
    }

    private static async Task<Property> SeedPropertyAsync(AppDbContext db)
    {
        var org = new OrgEntity { Name = "Org", Slug = $"org-{Guid.NewGuid():N}" };
        db.Orgs.Add(org);
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|host",
            Name = "Casa",
            Address = "Via Roma 1",
            City = "Roma",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }
}
