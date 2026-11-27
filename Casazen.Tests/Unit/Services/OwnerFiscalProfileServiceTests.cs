using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F05/US02/T01 (NF-109, NF-110, ADR-012): an owner is usable for the financial operations only with a complete profile and an approved rule set in
/// force of its own jurisdiction (no default rate, no jurisdiction inferred from a country), and only <c>agency.owner.financial.manage</c> sees the full
/// identifiers and bank data.
/// </summary>
public class OwnerFiscalProfileServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 10, 15);

    [Fact]
    public async Task OwnerFiscalPolicy_NoApprovedEffectiveRuleBlocksFinancialActivation_Test()
    {
        await using var db = NewDb();
        var owner = await SeedOwnerAsync(db);
        var service = Service(db);
        var italian = await service.CreatePolicyAsync(OrgId, ItalianRule(), "auth0|admin");

        var noRule = await Assert.ThrowsAsync<DomainRuleException>(() => service.ResolveAsync(OrgId, owner, Day));
        Assert.Equal(OwnerFiscalErrors.ProfileMissing, noRule.Code);

        await service.SaveProfileAsync(OrgId, owner, Profile("IT", italian.Id), "auth0|admin");
        var notApproved = await Assert.ThrowsAsync<DomainRuleException>(() => service.ResolveAsync(OrgId, owner, Day));
        Assert.Equal(OwnerFiscalErrors.PolicyNotApproved, notApproved.Code);

        await service.ApprovePolicyAsync(OrgId, italian.Id, "Dott.ssa TEST Commercialista, albo 0001", "auth0|admin");
        var before = await Assert.ThrowsAsync<DomainRuleException>(() => service.ResolveAsync(OrgId, owner, new DateOnly(2025, 12, 31)));
        Assert.Equal(OwnerFiscalErrors.ProfileMissing, before.Code);

        var resolution = await service.ResolveAsync(OrgId, owner, Day);
        Assert.Equal(italian.Id, resolution.Policy.Id);
        Assert.Equal(21m, resolution.Parameters.WithholdingRatePercent);
        await Assert.ThrowsAsync<DomainConflictException>(() => service.ApprovePolicyAsync(OrgId, italian.Id, "TEST", "auth0|admin"));

        var noRate = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreatePolicyAsync(
            OrgId, ItalianRule() with { Version = "2", Parameters = ItalianRule().Parameters! with { WithholdingRatePercent = null } }, null));
        Assert.Equal(OwnerFiscalErrors.PolicyInvalid, noRate.Code);
    }

    [Fact]
    public async Task OwnerFiscalPolicy_SwissProfileNeverSelectsItalianWithholding_Test()
    {
        await using var db = NewDb();
        var owner = await SeedOwnerAsync(db);
        var service = Service(db);
        var italian = await service.CreatePolicyAsync(OrgId, ItalianRule(), null);
        await service.ApprovePolicyAsync(OrgId, italian.Id, "TEST Commercialista", null);
        var swiss = await service.CreatePolicyAsync(OrgId, SwissRule(), null);
        await service.ApprovePolicyAsync(OrgId, swiss.Id, "TEST Treuhänder", null);

        // A Swiss owner given the Italian rule set is refused: the rule must be of the jurisdiction of the profile.
        await service.SaveProfileAsync(OrgId, owner, Profile("CH", italian.Id) with { ValidFrom = new DateOnly(2026, 1, 1) }, null);
        var mismatch = await Assert.ThrowsAsync<DomainRuleException>(() => service.ResolveAsync(OrgId, owner, Day));
        Assert.Equal(OwnerFiscalErrors.PolicyJurisdictionMismatch, mismatch.Code);

        // From October the Swiss owner follows its Swiss rule set: no withholding, whatever the org's Italian rule says.
        await service.SaveProfileAsync(OrgId, owner, Profile("CH", swiss.Id) with { ValidFrom = new DateOnly(2026, 10, 1) }, null);
        var resolution = await service.ResolveAsync(OrgId, owner, Day);
        Assert.Equal("CH", resolution.Policy.Jurisdiction);
        Assert.False(resolution.Parameters.WithholdingApplies);
        Assert.Null(resolution.Parameters.WithholdingTaxCode);
        var history = await service.ListProfilesAsync(OrgId, owner);
        Assert.Equal(new DateOnly(2026, 9, 30), history.Single(p => p.ValidFrom == new DateOnly(2026, 1, 1)).ValidTo);
    }

    [Fact]
    public async Task OwnerFiscalProfile_UnauthorizedRoleOnlyReceivesMaskedData_Test()
    {
        await using var db = NewDb();
        var owner = await SeedOwnerAsync(db);
        var profile = await Service(db).SaveProfileAsync(OrgId, owner, Profile("IT", null), null);

        var masked = OwnerFiscalProfileDto.From(profile, full: false);
        var full = OwnerFiscalProfileDto.From(profile, full: true);

        Assert.True(masked.Masked);
        Assert.EndsWith("7856", masked.FiscalCode, StringComparison.Ordinal);
        Assert.Equal(20, masked.FiscalCode!.Length);
        Assert.DoesNotContain("RSSMRA", masked.FiscalCode, StringComparison.Ordinal);
        Assert.EndsWith("3456", masked.BankAccountReference, StringComparison.Ordinal);
        Assert.DoesNotContain("0542811101", masked.BankAccountReference, StringComparison.Ordinal);
        Assert.DoesNotContain("Via del Proprietario", masked.LegalAddress, StringComparison.Ordinal);
        Assert.DoesNotContain("Mario", masked.LegalName, StringComparison.Ordinal);
        Assert.Equal("RSSMRA70A01H501W7856", full.FiscalCode);
        Assert.Equal("IT60X0542811101000000123456", full.BankAccountReference);
        Assert.False(full.Masked);

        // Only the owner and the administrators of the agency hold the permission that reads the full values.
        foreach (var role in new[] { OrgRole.PropertyManager, OrgRole.Collaborator, OrgRole.Accountant })
            Assert.DoesNotContain(AgencyPermissions.OwnerFinancialManage, OrgRoleCatalog.AgencyPermissionsOf(role));
        Assert.Contains(AgencyPermissions.OwnerFinancialManage, OrgRoleCatalog.AgencyPermissionsOf(OrgRole.Admin));
    }

    private static OwnerAccountingPolicyCommand ItalianRule() => new()
    {
        Jurisdiction = "IT",
        RuleCode = "TEST-IT-STR",
        Version = "1",
        ValidFrom = new DateOnly(2026, 1, 1),
        SourceUrl = "https://www.agenziaentrate.gov.it/test-source",
        SourceSha256 = new string('a', 64),
        Parameters = new OwnerAccountingRuleParameters
        {
            WithholdingApplies = true,
            WithholdingRatePercent = 21m,
            WithholdingTaxCode = "1919",
            GrossBaseComponents = ["rent", "cleaning"],
            Rounding = "half_up",
            RetentionYears = 10,
        },
    };

    private static OwnerAccountingPolicyCommand SwissRule() => new()
    {
        Jurisdiction = "CH",
        RuleCode = "TEST-CH-RENT",
        Version = "1",
        ValidFrom = new DateOnly(2026, 1, 1),
        SourceUrl = "https://www.estv.admin.ch/test-source",
        SourceSha256 = new string('b', 64),
        Parameters = new OwnerAccountingRuleParameters { WithholdingApplies = false, Rounding = "half_even", RetentionYears = 20 },
    };

    private static OwnerFiscalProfileCommand Profile(string jurisdiction, Guid? policy) => new()
    {
        ValidFrom = new DateOnly(2026, 1, 1),
        EntityKind = OwnerEntityKind.NaturalPerson,
        LegalName = "TEST Mario Proprietario",
        TaxResidenceCountry = jurisdiction,
        LegalAddress = "Via del Proprietario 1, 00100 Roma",
        FiscalCode = "RSSMRA70A01H501W7856",
        SettlementCurrency = jurisdiction == "CH" ? "CHF" : "EUR",
        BankAccountReference = "IT60X0542811101000000123456",
        RevenueRecognitionRule = "cash",
        TaxJurisdiction = jurisdiction,
        AccountingPolicyId = policy,
    };

    private static OwnerFiscalProfileService Service(AppDbContext db) =>
        new(db, new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero)), NullLogger<OwnerFiscalProfileService>.Instance);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerFiscal_{Guid.NewGuid()}").Options, new ProfileTenant(), new EphemeralDataProtectionProvider());

    private static async Task<Guid> SeedOwnerAsync(AppDbContext db)
    {
        var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = "Owner", Email = $"{Guid.NewGuid():N}@example.com" };
        db.PropertyOwners.Add(owner);
        await db.SaveChangesAsync();
        return owner.Id;
    }
}

file sealed class ProfileTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
