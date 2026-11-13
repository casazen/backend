using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

public class OpenapiRegistrationPreparationTests
{
    internal static OpenapiRegistrationData CompleteData() => new()
    {
        Values = new()
        {
            ["territorialOffice"] = "T9A", ["rentalContractType"] = "L1",
            ["contractStartDate"] = "2027-01-01", ["contractEndDate"] = "2030-12-31",
            ["contractPages"] = "8", ["contractCopies"] = "2", ["annualFee"] = "14400.00",
            ["contractDate"] = "2026-12-15", ["province"] = "MI", ["town"] = "Milano", ["address"] = "Via Roma 1",
            ["cadastralSheet"] = "10", ["cadastralParcel"] = "42", ["cadastralSub"] = "3",
            ["cadastralIncome"] = "500.00", ["cadastralCategory"] = "A2", ["taxRegime"] = "CEDOLARE",
            ["applicantName"] = "Mario Rossi", ["applicantPhone"] = "+393331234567", ["applicantEmail"] = "mario@example.com",
            ["privateApplicantTaxCode"] = "RSSMRA80A01H501U", ["landlordName"] = "Mario Rossi", ["landlordTaxCode"] = "RSSMRA80A01H501U",
            ["landlordPercentage"] = "100", ["conductorName"] = "Mario Rossi", ["conductorTaxCode"] = "RSSMRA80A01H501U",
            ["feesPaymentGuarantees"] = "NO", ["penaltyClauses"] = "NO",
        },
        Documents = new() { ["landlordDocument"] = Guid.NewGuid(), ["conductorDocument"] = Guid.NewGuid() },
    };

    [Fact]
    public void Schema_MatchesTheAuthenticatedRegistrationProduct()
    {
        var schema = OpenapiRegistrationSchema.Current;
        Assert.Equal("67e13cfd2cbb7e30d3d93e9d", schema.DocumentId);
        Assert.Equal(61, schema.Fields.Count);
        Assert.Equal("territorialOffice", schema.Fields[0].Name);
        Assert.Equal("rentalContractType", schema.Fields[1].Name);
        Assert.Equal("contractFile", schema.Fields[8].Name);
        Assert.Equal("landlordDocument", schema.Fields[27].Name);
        Assert.Equal("conductorDocument", schema.Fields[39].Name);
        Assert.Equal("thirdPropertyCadastralCategory", schema.Fields[60].Name);
    }

    [Fact]
    public void CompleteCedolare_IsValid_AndMapsFilesAndCodesToVerifiedPositions()
    {
        var data = CompleteData();
        Assert.Empty(OpenapiRegistrationValidation.Validate(data, true));
        var lease = new LeaseContract { OpenapiRegistrationData = data.Serialize() };
        var payload = OpenapiLeaseSearch.Build(lease, "%PDF-contract"u8.ToArray(), [],
            new Dictionary<string, string> { ["landlordDocument"] = "landlord-base64", ["conductorDocument"] = "tenant-base64" });
        Assert.Equal("T9A", payload["field0"]);
        Assert.Equal("L1", payload["field1"]);
        Assert.Equal("CEDOLARE", payload["field17"]);
        Assert.Equal(Convert.ToBase64String("%PDF-contract"u8.ToArray()), payload["field8"]);
        Assert.Equal("landlord-base64", payload["field27"]);
        Assert.Equal("tenant-base64", payload["field39"]);
        Assert.DoesNotContain(data.Documents["landlordDocument"].ToString(), payload.Values);
    }

    [Theory]
    [InlineData("territorialOffice")]
    [InlineData("contractCopies")]
    [InlineData("applicantPhone")]
    [InlineData("landlordPercentage")]
    public void MissingRequiredOrConditionalValue_IsRejected(string field)
    {
        var data = CompleteData(); data.Values.Remove(field);
        Assert.Contains(OpenapiRegistrationValidation.Validate(data, true), i => i.Field == field);
    }

    [Theory]
    [InlineData("contractCopies", "0")]
    [InlineData("contractCopies", "1.5")]
    [InlineData("contractStartDate", "2027-02-30")]
    [InlineData("taxRegime", "CedolareSecca")]
    [InlineData("rentalContractType", "Libero")]
    [InlineData("landlordPercentage", "101")]
    [InlineData("applicantEmail", "bad-address")]
    public void MalformedValue_IsRejected(string field, string value)
    {
        var data = CompleteData(); data.Values[field] = value;
        Assert.Contains(OpenapiRegistrationValidation.Validate(data, true), i => i.Field == field);
    }

    [Fact]
    public void Ordinary_RequiresBankDetailsAndTaxPayment_AndAcceptsValidIban()
    {
        var data = CompleteData(); data.Values["taxRegime"] = "ORDINARIO"; data.Values.Remove("landlordPercentage");
        var missing = OpenapiRegistrationValidation.Validate(data, true);
        Assert.Contains(missing, i => i.Field == "ibanCode");
        Assert.Contains(missing, i => i.Field == "accountHolderTaxCode");
        Assert.Contains(missing, i => i.Field == "taxPaymentType");
        data.Values["ibanCode"] = "IT60X0542811101000000123456";
        data.Values["accountHolderTaxCode"] = "RSSMRA80A01H501U";
        data.Values["taxPaymentType"] = "ANNUALMENTE";
        Assert.Empty(OpenapiRegistrationValidation.Validate(data, true));
        data.Values["ibanCode"] = "IT00X0542811101000000123456";
        Assert.Contains(OpenapiRegistrationValidation.Validate(data, true), i => i.Field == "ibanCode");
    }

    [Fact]
    public void GuaranteesAndAdditionalPartiesAndProperties_RequireTheirWholeGroup()
    {
        var data = CompleteData();
        data.Values["feesPaymentGuarantees"] = "SI";
        data.Values["secondLandlordName"] = "Anna Rossi";
        data.Values["thirdPropertyCadastralSheet"] = "1";
        var issues = OpenapiRegistrationValidation.Validate(data, true);
        Assert.Contains(issues, i => i.Field == "guaranteeProviderTaxCode");
        Assert.Contains(issues, i => i.Field == "maximumGuaranteeAmount");
        Assert.Contains(issues, i => i.Field == "secondLandlordDocument");
        Assert.Contains(issues, i => i.Field == "secondLandlordPercentage");
        Assert.Contains(issues, i => i.Field == "thirdPropertyCadastralSheet");
    }

    [Fact]
    public void FileContentsAndUnknownFields_CannotBeStoredAsText()
    {
        var data = CompleteData(); data.Values["landlordDocument"] = "https://example.test/document.pdf";
        data.Values["field0"] = "invented";
        var issues = OpenapiRegistrationValidation.Validate(data, true);
        Assert.Contains(issues, i => i.Field == "landlordDocument");
        Assert.Contains(issues, i => i.Field == "field0");
    }

    [Fact]
    public async Task Preparation_SavesCompleteData_ResolvesPrivateAttachments_AndRejectsStaleVersion()
    {
        await using var db = NewDb();
        var (lease, data, storage) = await SeedAsync(db);
        var service = Service(db, storage.Object);
        var result = await service.SaveAsync(lease.Id, data);
        Assert.True(result.Ready);
        Assert.Equal(1, result.Data.Version);
        await service.EnsureReadyAsync(lease);
        var payload = await service.BuildSearchAsync(lease, "%PDF-contract"u8.ToArray());
        Assert.Equal(Convert.ToBase64String("%PDF-identity"u8.ToArray()), payload["field27"]);
        Assert.Equal("8", payload["field4"]);
        Assert.DoesNotContain("%PDF-", lease.OpenapiRegistrationData!);
        data.Version = 0;
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => service.SaveAsync(lease.Id, data));
        Assert.Equal("openapi_registration_conflict", conflict.Code);
    }

    [Fact]
    public async Task Preparation_ClassifiesAnExistingOtherAttachmentAsAnIdentityDocument()
    {
        await using var db = NewDb(); var (lease, data, storage) = await SeedAsync(db);
        var identity = await db.LeaseDocuments.FirstAsync();
        identity.Kind = LeaseDocumentKind.Other;
        await db.SaveChangesAsync();
        await Service(db, storage.Object).SaveAsync(lease.Id, data);
        Assert.Equal(LeaseDocumentKind.RegistrationIdentity, identity.Kind);
        Assert.Equal(LeaseDocumentExtractionStatus.Skipped, identity.ExtractionStatus);
    }

    [Fact]
    public async Task Preparation_CannotReferenceAnotherLeasesDocument()
    {
        await using var db = NewDb(); var (lease, data, storage) = await SeedAsync(db);
        var foreign = new LeaseDocument { LeaseContractId = Guid.NewGuid(), OrgId = Guid.NewGuid(), StorageKey = "foreign.pdf" };
        db.LeaseDocuments.Add(foreign); await db.SaveChangesAsync();
        data.Documents["landlordDocument"] = foreign.Id;
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db, storage.Object).SaveAsync(lease.Id, data));
        Assert.Equal("openapi_registration_document_invalid", error.Code);
        storage.Verify(s => s.OpenReadAsync(StorageBucket.Private, "foreign.pdf", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Preparation_MissingOrDeletedFile_BlocksCheckoutReadiness()
    {
        await using var db = NewDb(); var (lease, data, storage) = await SeedAsync(db);
        await Service(db, storage.Object).SaveAsync(lease.Id, data);
        storage.Setup(s => s.ExistsAsync(StorageBucket.Private, "identity.pdf", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var service = Service(db, storage.Object);
        Assert.False((await service.GetAsync(lease.Id)).Ready);
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => service.EnsureReadyAsync(lease));
        Assert.Equal("openapi_registration_incomplete", error.Code);
    }

    [Fact]
    public async Task Preparation_CountsEveryFileFieldWhenTheSameAttachmentIsReused()
    {
        await using var db = NewDb(); var (lease, data, storage) = await SeedAsync(db);
        foreach (var document in await db.LeaseDocuments.ToListAsync()) document.SizeBytes = 20 * 1024 * 1024;
        await db.SaveChangesAsync();
        data.Documents["secondLandlordDocument"] = data.Documents["landlordDocument"];
        data.Documents["thirdLandlordDocument"] = data.Documents["landlordDocument"];
        var result = await Service(db, storage.Object).SaveAsync(lease.Id, data);
        Assert.False(result.Ready);
        Assert.Contains(result.Issues, i => i.Message.Contains("60 MB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preparation_CannotChangeSubmittedLease()
    {
        await using var db = NewDb(); var (lease, data, storage) = await SeedAsync(db);
        lease.Status = LeaseStatus.SentToProvider; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainConflictException>(() => Service(db, storage.Object).SaveAsync(lease.Id, data));
    }

    [Fact]
    public async Task Erasure_RemovesSupplementAndPrivateIdentityAttachments()
    {
        await using var db = NewDb(); var (lease, data, storage) = await SeedAsync(db);
        lease.OpenapiRegistrationData = data.Serialize();
        lease.EndDate = DateTime.UtcNow.AddYears(-1);
        await db.SaveChangesAsync();
        var service = new LeasePartyPrivacyService(db, Options.Create(new GdprOptions()), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LeasePartyPrivacyService>.Instance, storage.Object);
        await service.RequestErasureAsync(lease.Id);
        Assert.Null(lease.OpenapiRegistrationData);
        Assert.Empty(await db.LeaseDocuments.ToListAsync());
        storage.Verify(s => s.DeleteAsync(StorageBucket.Private, "identity.pdf", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static OpenapiRegistrationPreparation Service(AppDbContext db, IFileStorage storage) => new(db, storage,
        Options.Create(new OpenapiOptions { BaseUrl = "https://test.docuengine.openapi.com", Token = "test-token" }), Mock.Of<IApeComplianceService>());
    private static async Task<(LeaseContract, OpenapiRegistrationData, Mock<IFileStorage>)> SeedAsync(AppDbContext db)
    {
        var data = CompleteData();
        var orgId = Guid.NewGuid();
        var lease = new LeaseContract { Status = LeaseStatus.Signed, OrgId = orgId, SignedPdfStoragePath = "signed.pdf",
            Property = new Property { OrgId = orgId, Address = "Via Roma 1", City = "Milano" },
            StartDate = new DateTime(2027, 1, 1), EndDate = new DateTime(2030, 12, 31), MonthlyRent = 1200 };
        db.LeaseContracts.Add(lease);
        foreach (var id in data.Documents.Values)
            db.LeaseDocuments.Add(new LeaseDocument { Id = id, LeaseContractId = lease.Id, OrgId = lease.OrgId, StorageKey = "identity.pdf", Kind = LeaseDocumentKind.RegistrationIdentity });
        await db.SaveChangesAsync();
        var storage = new Mock<IFileStorage>();
        storage.Setup(s => s.ExistsAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        storage.Setup(s => s.OpenReadAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream("%PDF-identity"u8.ToArray()));
        return (lease, data, storage);
    }
}
