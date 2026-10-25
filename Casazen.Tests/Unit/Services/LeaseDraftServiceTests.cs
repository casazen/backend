using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services.LeaseContracts;
using Casazen.Tests.Unit.Leases;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-12: the guided draft of a lease over the EF InMemory database — create with only the property, save any value in any state,
/// take it up again identical (also from another context, as another browser would), the version that refuses a stale save, the review,
/// "downloaded for signature" with its template gate, the deletion, and the canone concordato. The races on real PostgreSQL are in
/// <c>LeaseDraftPostgresTests</c>.
/// </summary>
public class LeaseDraftServiceTests
{
    private static async Task<LeaseDraftHarness> NewAsync() => await new LeaseDraftHarness().SeedAsync();

    private static IReadOnlyList<string> Needs(LeaseDraftFlow flow, int step) => flow[step].Needs.Select(n => n.Code).ToList();

    // ---- creation ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithOnlyTheProperty_MakesADraftWithTheStartingValues_AndNothingIsAskedYet()
    {
        using var h = await NewAsync();

        var created = await h.CreateAsync();

        Assert.Equal(1, created.Version);
        var lease = await h.LoadAsync(created.Id);
        Assert.Equal(LeaseStatus.Draft, lease.Status);
        Assert.Equal(h.PropertyId, lease.PropertyId);
        Assert.Equal(h.OrgId, lease.OrgId);
        Assert.Equal(LeaseOrigin.Created, lease.Origin);
        Assert.Equal(1, lease.WizardMaxStep);
        Assert.Equal(1, lease.DraftVersion);
        Assert.Equal(LeaseDraftDefaults.PlaceholderDate, lease.StartDate);
        Assert.Equal(LeaseDraftDefaults.PlaceholderDate, lease.EndDate);
        Assert.Equal(0m, lease.MonthlyRent);
        Assert.Empty(lease.Parties);
        Assert.NotNull(lease.DraftData);
        Assert.Null(lease.ReviewConfirmedAt);
        Assert.Null(lease.SentForSignatureAt);
        var created1 = Assert.Single(lease.Events);
        Assert.Equal(LeaseEventType.Created, created1.EventType);
        // No APE, no parties, no dates were asked to create it: the flow says what is still to do.
        Assert.Equal(LeaseStepState.Done, created.Flow[1].State);
        Assert.NotEmpty(created.Flow[2].Needs);
        Assert.Equal(LeaseStepState.Locked, created.Flow[6].State);
    }

    [Fact]
    public async Task Create_ForAPropertyWithoutApe_StillWorks_TheApeIsAStep1Need()
    {
        using var h = await NewAsync();
        var bare = await h.SeedPropertyAsync(withApe: false, withCadastre: false);

        var created = await h.Service.CreateAsync(bare.Id, LeaseOrigin.Created);

        Assert.Equal([LeaseNeedCodes.ApeMissing, LeaseNeedCodes.CadastralMissing], Needs(created.Flow, 1));
    }

    [Fact]
    public async Task Create_ForAPropertyThatDoesNotExist_Is404()
    {
        using var h = await NewAsync();

        var error = await Assert.ThrowsAsync<NotFoundException>(() => h.Service.CreateAsync(Guid.NewGuid(), LeaseOrigin.Created));

        Assert.Equal("property_not_found", error.Code);
    }

    [Fact]
    public async Task Create_AnImportedDraft_KeepsItsOrigin_AndHasNoModelNoReview()
    {
        using var h = await NewAsync();

        var created = await h.CreateAsync(LeaseOrigin.Imported);

        Assert.Equal(LeaseOrigin.Imported, (await h.LoadAsync(created.Id)).Origin);
        Assert.Equal(LeaseStepState.NotApplicable, created.Flow[5].State);
        Assert.Equal(LeaseStepState.NotApplicable, created.Flow[6].State);
    }

    // ---- saving and taking it up again ---------------------------------------------------------------------------

    [Fact]
    public async Task Save_IncompleteAndWrongValues_AreKept_NoErrorOfAny_AndTheFlowSaysWhatIsMissing()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();

        var s2 = await h.SaveAsync(created.Id, created.Version, 2, """
            {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"RSSMRA80A01H501V","citizenship":"IT","residence":"Bari"}],
             "tenants":[{"firstName":"Giulia"}]}
            """);
        var s3 = await h.SaveAsync(created.Id, s2.Version, 3, """{"contractType":"Studenti","monthlyRent":-50,"securityDeposit":99}""");
        var s4 = await h.SaveAsync(created.Id, s3.Version, 4, """{"startDate":"next spring"}""");

        Assert.Equal([2, 3, 4], new[] { s2.Version, s3.Version, s4.Version });
        Assert.Equal([LeaseNeedCodes.LandlordIncomplete, LeaseNeedCodes.TenantIncomplete], Needs(s4.Flow, 2));
        Assert.Equal(
            [LeaseNeedCodes.LeaseTypeNotAvailable, LeaseNeedCodes.TaxRegimeMissing, LeaseNeedCodes.RentInvalid],
            Needs(s4.Flow, 3));
        Assert.Equal([LeaseNeedCodes.StartInvalid], Needs(s4.Flow, 4));
        var lease = await h.LoadAsync(created.Id);
        // Nothing wrong reached the columns, and no incomplete party became a row.
        Assert.Empty(lease.Parties);
        Assert.Equal(0m, lease.MonthlyRent);
        Assert.Equal(LeaseDraftDefaults.PlaceholderDate, lease.StartDate);
        Assert.Equal(LeaseContractType.Libero, lease.ContractType);
        Assert.Equal(4, lease.WizardMaxStep);
        Assert.NotNull(lease.DraftData);
    }

    [Fact]
    public async Task Get_TakesItUpIdentical_FromAnotherContextAsAnotherBrowserWould()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();
        var s2 = await h.SaveAsync(created.Id, created.Version, 2, """
            {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"RSSMRA80A01H501V","citizenship":"it","residence":"  Bari  ","contactEmail":"x"}],
             "tenants":[{"firstName":"Giulia","lastName":"Verdi"}]}
            """);
        var s3 = await h.SaveAsync(created.Id, s2.Version, 3, """{"contractType":"Libero","taxRegime":"x","monthlyRent":12.5,"securityDeposit":500}""");
        var s4 = await h.SaveAsync(created.Id, s3.Version, 4, """{"startDate":"1 novembre"}""");

        var other = h.NewService();
        var view = await other.GetAsync(created.Id);

        Assert.Equal(s4.Version, view.Version);
        Assert.Equal(LeaseOrigin.Created, view.Origin);
        Assert.Equal(4, view.WizardMaxStep);
        var landlord = Assert.Single(view.Values.Landlords);
        Assert.Equal(s2.LandlordIds.Single(), landlord.Id);
        Assert.Equal(("Mario", "Rossi", "RSSMRA80A01H501V", "it", "  Bari  ", "x"),
            (landlord.FirstName, landlord.LastName, landlord.FiscalCode, landlord.Citizenship, landlord.Residence, landlord.ContactEmail));
        var tenant = Assert.Single(view.Values.Tenants);
        Assert.Equal(s2.TenantIds.Single(), tenant.Id);
        Assert.Equal("Giulia", tenant.FirstName);
        Assert.Equal(("Libero", "x", 12.5m, 500m, "1 novembre"),
            (view.Values.ContractType, view.Values.TaxRegime, view.Values.MonthlyRent, view.Values.SecurityDeposit, view.Values.StartDate));
        Assert.Null(view.Computed.EndDate);
        Assert.Equal(Needs(s4.Flow, 2), Needs(view.Flow, 2));
        Assert.Equal(Needs(s4.Flow, 3), Needs(view.Flow, 3));
        Assert.Equal(Needs(s4.Flow, 4), Needs(view.Flow, 4));
    }

    [Fact]
    public async Task Save_TheEntriesOfThePartiesKeepTheirIds_AcrossSaves()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();
        var first = await h.SaveAsync(created.Id, created.Version, 2, """{"tenants":[{"firstName":"Giulia","lastName":"Verdi"}]}""");
        var id = first.TenantIds.Single();

        var second = await h.SaveAsync(created.Id, first.Version, 2, $$"""{"tenants":[{"id":"{{id}}","firstName":"Giulia","lastName":"Verdi","fiscalCode":"VRDGLI85B02F205A"}]}""");

        Assert.Equal([id], second.TenantIds);
        Assert.Equal([id], (await h.NewService().GetAsync(created.Id)).Values.Tenants.Select(t => t.Id));
    }

    [Fact]
    public async Task Save_Step5_RecordsTheDayTheDocumentsWereChecked_ByPartyId()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var tenantId = (await h.Service.GetAsync(created.Id)).Values.Tenants.Single().Id;

        var saved = await h.SaveAsync(created.Id, version, 5, $$"""{"parties":[{"id":"{{tenantId}}","documentsVerifiedOn":"2026-10-01"}]}""");

        var lease = await h.LoadAsync(created.Id);
        Assert.Equal(new DateOnly(2026, 10, 1), lease.Parties.Single(p => p.Role == PartyRole.Tenant).DocumentsVerifiedOn);
        Assert.Equal(version + 1, saved.Version);
        Assert.Null(lease.Parties.Single(p => p.Role == PartyRole.Landlord).DocumentsVerifiedOn);
    }

    [Fact]
    public async Task Save_ADocumentsDateInTheFuture_IsKeptInTheDraft_ReportedAndNotInTheRow()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var tenantId = (await h.Service.GetAsync(created.Id)).Values.Tenants.Single().Id;

        var saved = await h.SaveAsync(created.Id, version, 5, $$"""{"parties":[{"id":"{{tenantId}}","documentsVerifiedOn":"2026-12-25"}]}""");

        Assert.Equal([LeaseNeedCodes.PartyDocumentsDateInvalid], Needs(saved.Flow, 5));
        var lease = await h.LoadAsync(created.Id);
        Assert.Null(lease.Parties.Single(p => p.Role == PartyRole.Tenant).DocumentsVerifiedOn);
        Assert.NotNull(lease.DraftData); // the typed date lives on until it is corrected
        Assert.Equal("2026-12-25", (await h.Service.GetAsync(created.Id)).Values.Tenants.Single().DocumentsVerifiedOn);
    }

    // ---- completing it -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Save_TheLastValidValue_MovesTheDataIntoTheColumnsAndTheParties_AndDropsTheTypedState()
    {
        using var h = await NewAsync();

        var (created, version) = await h.CompleteAsync();

        var lease = await h.LoadAsync(created.Id);
        Assert.Null(lease.DraftData);
        Assert.Equal(version, lease.DraftVersion);
        Assert.Equal(LeaseContractType.Libero, lease.ContractType);
        Assert.Equal(LeaseTaxRegime.CedolareSecca, lease.TaxRegime);
        Assert.Equal(FiscalRegime.CedolareSecca, lease.FiscalRegime);
        Assert.Equal(900m, lease.MonthlyRent);
        Assert.Equal(1800m, lease.SecurityDeposit);
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), lease.StartDate);
        Assert.Equal(new DateTime(2030, 10, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        Assert.Equal(5, lease.WizardMaxStep);
        var landlord = Assert.Single(lease.Parties, p => p.Role == PartyRole.Landlord);
        var tenant = Assert.Single(lease.Parties, p => p.Role == PartyRole.Tenant);
        Assert.Equal(("Mario", "Rossi", "RSSMRA80A01H501U", "mario@example.com", "Bari, via Sparano 1"),
            (landlord.FirstName, landlord.LastName, landlord.FiscalCode, landlord.ContactEmail, landlord.Residence));
        Assert.Equal(string.Empty, tenant.ContactEmail);
        Assert.False(tenant.IsExtraEU);
        // A lease the other readers see as a normal draft: the term passes the rule of its type.
        LeaseContractTerms.EnsureTerm(lease.ContractType, lease.StartDate, lease.EndDate);
    }

    [Fact]
    public async Task ATransitoryDraft_WithItsEndAndItsNeed_KeepsThemInTheColumns_GivesThemBack_AndTheReviewCoversThem()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();

        // The landlord makes it a transitory contract: who has the need and what it is come with the type, the end and the documentation
        // of a term of more than 30 days with the dates.
        var typed = await h.SaveAsync(
            created.Id, version, 3, """{"contractType":"Transitorio","transitoryParty":"Tenant","transitoryNeed":"  Lavoro a Milano da novembre ad aprile  "}""");
        Assert.Empty(Needs(typed.Flow, 3));
        Assert.Equal([LeaseNeedCodes.EndMissing], Needs(typed.Flow, 4));
        var ended = await h.SaveAsync(created.Id, typed.Version, 4, """{"endDate":"2027-04-30"}""");
        Assert.Equal([LeaseNeedCodes.TransitoryDocumentationMissing], Needs(ended.Flow, 4));
        var done = await h.SaveAsync(created.Id, ended.Version, 4, """{"transitoryDocumentation":"Lettera di assunzione"}""");

        Assert.True(done.Flow.DataComplete);
        var lease = await h.LoadAsync(created.Id);
        Assert.Null(lease.DraftData);
        Assert.Equal(LeaseContractType.Transitorio, lease.ContractType);
        Assert.Equal(new DateTime(2027, 4, 30, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        Assert.Equal(
            (PartyRole.Tenant, "Lavoro a Milano da novembre ad aprile", "Lettera di assunzione"),
            (lease.TransitoryParty!.Value, lease.TransitoryNeed, lease.TransitoryDocumentation));
        LeaseContractTerms.EnsureTerm(lease.ContractType, lease.StartDate, lease.EndDate);

        // Taken up again from the columns, as another browser would: nothing was kept only in the typed state.
        var view = await h.NewService().GetAsync(created.Id);
        Assert.Equal(
            ("Tenant", "Lavoro a Milano da novembre ad aprile", "Lettera di assunzione", "2027-04-30"),
            (view.Values.TransitoryParty, view.Values.TransitoryNeed, view.Values.TransitoryDocumentation, view.Values.EndDate));
        Assert.Equal(new LeaseDraftComputed("2027-04-30", 6), view.Computed);
        Assert.All(Enumerable.Range(1, 5), n => Assert.Equal(LeaseStepState.Done, view.Flow[n].State));

        // The review tells the landlord to check the rent against the bands of the agreement (TR-FU1) and blocks nothing; the need is
        // printed in the contract, so changing it withdraws the confirmation.
        var review = await h.Service.GetReviewAsync(created.Id);
        var advisory = Assert.Single(review.Checks, c => c.Code == LeaseNeedCodes.TransitoryRentBandToCheck);
        Assert.Equal(LeaseReviewSeverity.Info, advisory.Severity);
        Assert.Equal(10_000, advisory.Params!["inhabitants"]);
        Assert.False(LeaseReview.HasBlock(review.Checks));
        var confirmed = await h.Service.ConfirmReviewAsync(created.Id, null);
        Assert.NotNull(confirmed.ReviewConfirmedAt);
        var changed = await h.SaveAsync(created.Id, confirmed.Version, 3, """{"transitoryNeed":"Trasferimento a Torino"}""");
        Assert.Contains(LeaseNeedCodes.ReviewNotConfirmed, Needs(changed.Flow, 6));
    }

    [Fact]
    public async Task Get_ACompleteDraft_GivesBackTheSameValuesFromTheColumnsAndTheParties_WithTheEndComputed()
    {
        using var h = await NewAsync();
        var (created, _) = await h.CompleteAsync();

        var view = await h.NewService().GetAsync(created.Id);

        Assert.Equal(("Libero", "CedolareSecca", 900m, 1800m, "2026-11-01"),
            (view.Values.ContractType, view.Values.TaxRegime, view.Values.MonthlyRent, view.Values.SecurityDeposit, view.Values.StartDate));
        Assert.Equal("Mario", view.Values.Landlords.Single().FirstName);
        Assert.Equal("Bari, via Argiro 7", view.Values.Tenants.Single().Residence);
        Assert.Equal(new LeaseDraftComputed("2030-10-31", 48), view.Computed);
        Assert.All(Enumerable.Range(1, 5), n => Assert.Equal(LeaseStepState.Done, view.Flow[n].State));
        Assert.Equal(LeaseStepState.Todo, view.Flow[6].State);
        Assert.True(view.Flow.DataComplete);
    }

    [Fact]
    public async Task Save_ClearingAValueOfACompleteDraft_PutsTheTypedStateBack_AndTheColumnTakesItsStartingValue()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();

        var saved = await h.SaveAsync(created.Id, version, 3, """{"monthlyRent":null}""");

        Assert.Equal([LeaseNeedCodes.RentMissing], Needs(saved.Flow, 3));
        var lease = await h.LoadAsync(created.Id);
        Assert.NotNull(lease.DraftData);
        Assert.Equal(LeaseDraftDefaults.PlaceholderRent, lease.MonthlyRent);
        // Everything else typed before is still there.
        var view = await h.NewService().GetAsync(created.Id);
        Assert.Equal("Mario", view.Values.Landlords.Single().FirstName);
        Assert.Equal("2026-11-01", view.Values.StartDate);
        Assert.Equal(1800m, view.Values.SecurityDeposit);
    }

    [Fact]
    public async Task Save_MovingThePartiesAround_DoesNotCollideWithThePositionsOfTheRows()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var s = await h.SaveAsync(created.Id, version, 2, """
            {"tenants":[{"firstName":"Giulia","lastName":"Verdi","fiscalCode":"VRDGLI85B02F205A","citizenship":"IT","residence":"Bari"},
                        {"firstName":"Marco","lastName":"Bianchi","fiscalCode":"{{LeaseDraftTestData.SecondTenantFiscalCode}}","citizenship":"IT","residence":"Lecce"}]}
            """);
        var ids = s.TenantIds;

        var swapped = await h.SaveAsync(created.Id, s.Version, 2, $$"""
            {"tenants":[{"id":"{{ids[1]}}","firstName":"Marco","lastName":"Bianchi","fiscalCode":"{{LeaseDraftTestData.SecondTenantFiscalCode}}","citizenship":"IT","residence":"Lecce"},
                        {"id":"{{ids[0]}}","firstName":"Giulia","lastName":"Verdi","fiscalCode":"VRDGLI85B02F205A","citizenship":"IT","residence":"Bari"}]}
            """);

        var tenants = (await h.LoadAsync(created.Id)).Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).ToList();
        Assert.Equal([ids[1], ids[0]], tenants.Select(t => t.Id));
        Assert.Equal([0, 1], tenants.Select(t => t.Position));
        Assert.Equal([ids[1], ids[0]], swapped.TenantIds);
    }

    // ---- the version ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Save_OnAnOldVersion_Is409_AndNothingIsWritten()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();
        var first = await h.SaveAsync(created.Id, created.Version, 3, """{"monthlyRent":900}""");

        // The second tab still has the version it opened with.
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => h.NewService().SaveAsync(created.Id, created.Version, 3, LeaseDraftHarness.Json("""{"monthlyRent":1000}""")));

        Assert.Equal("lease_draft_conflict", error.Code);
        var view = await h.NewService().GetAsync(created.Id);
        Assert.Equal(first.Version, view.Version);
        Assert.Equal(900m, view.Values.MonthlyRent);
    }

    [Fact]
    public async Task Save_TwoSavesOnTheSameVersion_OnlyTheFirstWins_NoValueIsLost()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();

        var winner = await h.NewService().SaveAsync(created.Id, created.Version, 3, LeaseDraftHarness.Json("""{"monthlyRent":900}"""));
        await Assert.ThrowsAsync<DomainConflictException>(
            () => h.NewService().SaveAsync(created.Id, created.Version, 4, LeaseDraftHarness.Json("""{"startDate":"2026-11-01"}""")));

        var view = await h.NewService().GetAsync(created.Id);
        Assert.Equal(winner.Version, view.Version);
        Assert.Equal(900m, view.Values.MonthlyRent);
        Assert.Null(view.Values.StartDate);
    }

    [Fact]
    public async Task Save_TheVersion_GoesUpByOneEachTime_EvenForTheSameValues()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();

        var one = await h.SaveAsync(created.Id, created.Version, 3, """{"monthlyRent":900}""");
        var two = await h.SaveAsync(created.Id, one.Version, 3, """{"monthlyRent":900}""");

        Assert.Equal([2, 3], new[] { one.Version, two.Version });
    }

    [Fact]
    public async Task Save_ValuesOfTheWrongShape_AreRefused_AndNothingIsWritten()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();

        await Assert.ThrowsAsync<LeaseDraftValuesException>(() => h.SaveAsync(created.Id, created.Version, 3, """{"monthlyRent":"900"}"""));

        var lease = await h.LoadAsync(created.Id);
        Assert.Equal(created.Version, lease.DraftVersion);
    }

    [Fact]
    public async Task Save_ALeaseThatIsNotADraft_Is409_AndNotFoundIsNotFound()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        await using (var db = h.SecondContext())
        {
            (await db.LeaseContracts.SingleAsync(l => l.Id == created.Id)).Status = LeaseStatus.Signed;
            await db.SaveChangesAsync();
        }

        var notDraft = await Assert.ThrowsAsync<DomainConflictException>(() => h.SaveAsync(created.Id, version, 3, """{"monthlyRent":1}"""));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => h.SaveAsync(Guid.NewGuid(), 1, 3, """{"monthlyRent":1}"""));

        Assert.Equal("lease_not_draft", notDraft.Code);
        Assert.Equal("lease_not_found", missing.Code);
    }

    [Fact]
    public async Task Get_ALeaseThatIsNotADraft_Is404WithTheCode()
    {
        using var h = await NewAsync();
        var (created, _) = await h.CompleteAsync();
        await using (var db = h.SecondContext())
        {
            (await db.LeaseContracts.SingleAsync(l => l.Id == created.Id)).Status = LeaseStatus.Registered;
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<NotFoundException>(() => h.Service.GetAsync(created.Id));
        var review = await Assert.ThrowsAsync<NotFoundException>(() => h.Service.GetReviewAsync(created.Id));

        Assert.Equal("lease_not_draft", error.Code);
        Assert.Equal("lease_not_draft", review.Code);
    }

    // ---- the property --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Save_Step1_ChangesTheProperty_ToAnotherOfTheOrg()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();
        var other = await h.SeedPropertyAsync(withApe: false, withCadastre: false);

        var saved = await h.SaveAsync(created.Id, created.Version, 1, $$"""{"propertyId":"{{other.Id}}"}""");

        Assert.Equal(other.Id, (await h.LoadAsync(created.Id)).PropertyId);
        Assert.Equal([LeaseNeedCodes.ApeMissing, LeaseNeedCodes.CadastralMissing], Needs(saved.Flow, 1));
    }

    [Fact]
    public async Task Save_Step1_APropertyThatDoesNotExist_Is404_AndTheLeaseKeepsItsProperty()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => h.SaveAsync(created.Id, created.Version, 1, $$"""{"propertyId":"{{Guid.NewGuid()}}"}"""));

        Assert.Equal("property_not_found", error.Code);
        Assert.Equal(h.PropertyId, (await h.LoadAsync(created.Id)).PropertyId);
    }

    // ---- the review ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_WithABlock_Is422_AndNothingChanges()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => h.Service.ConfirmReviewAsync(created.Id, null));

        Assert.Equal("lease_review_blocked", error.Code);
        var lease = await h.LoadAsync(created.Id);
        Assert.Null(lease.ReviewConfirmedAt);
        Assert.Equal(created.Version, lease.DraftVersion);
        Assert.DoesNotContain(lease.Events, e => e.EventType == LeaseEventType.ReviewConfirmed);
    }

    [Fact]
    public async Task Confirm_ADraftMadeInCasazenWithoutItsDeposit_IsBlocked_ZeroIsAnAnswer_AnImportedOneIsNotAskedForIt()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();

        // The model of the contract prints the deposit: left empty, the review cannot be confirmed (LR-DEP).
        var cleared = await h.SaveAsync(created.Id, version, 3, """{"securityDeposit":null}""");
        Assert.Equal([LeaseNeedCodes.DepositMissing], Needs(cleared.Flow, 3));
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => h.Service.ConfirmReviewAsync(created.Id, cleared.Version));
        Assert.Equal("lease_review_blocked", error.Code);
        Assert.Null((await h.LoadAsync(created.Id)).ReviewConfirmedAt);

        // "0 if there is none" is an answer.
        var none = await h.SaveAsync(created.Id, cleared.Version, 3, """{"securityDeposit":0}""");
        Assert.Empty(Needs(none.Flow, 3));
        Assert.NotNull((await h.Service.ConfirmReviewAsync(created.Id, none.Version)).ReviewConfirmedAt);

        // A contract already signed prints nothing: its deposit stays optional.
        var imported = await h.CreateAsync(LeaseOrigin.Imported);
        var parties = await h.SaveAsync(imported.Id, imported.Version, 2, ValidParties);
        var terms = await h.SaveAsync(imported.Id, parties.Version, 3, """{"contractType":"Libero","taxRegime":"CedolareSecca","monthlyRent":900}""");
        Assert.Empty(Needs(terms.Flow, 3));
    }

    [Fact]
    public async Task Confirm_ADraftMadeInCasazenNeedsTheCodeAndClassOfItsApe_TheContractPrintsTheLastIdentifiedOne_AnImportedOneIsNotAsked()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();

        // An APE the reading takes for valid (the mock) whose code and class were never saved: the model prints them, so the review
        // cannot be confirmed (LR-APE). It used to pass and end in 422 contract_data_missing at the download for signature.
        await h.SetApeIdentificationAsync(null, null);
        var saved = await h.SaveAsync(created.Id, version, 1, $$"""{"propertyId":"{{h.PropertyId}}"}""");
        Assert.Equal([LeaseNeedCodes.ApeIdentificationMissing], Needs(saved.Flow, 1));
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => h.Service.ConfirmReviewAsync(created.Id, saved.Version));
        Assert.Equal("lease_review_blocked", error.Code);
        var review = await h.Service.GetReviewAsync(created.Id);
        Assert.Equal(LeaseReviewSeverity.Block, Assert.Single(review.Checks, c => c.Code == LeaseNeedCodes.ApeIdentificationMissing).Severity);
        Assert.DoesNotContain(review.Checks, c => c.Code is LeaseNeedCodes.ApeOk or LeaseNeedCodes.ApeMissing);

        // A certificate the reading could not take whole asks for the same thing: its code and class, not another upload.
        h.Ape.Setup(a => a.EnsurePropertyHasValidApeAsync(It.IsAny<Guid>())).ThrowsAsync(ApeComplianceException.IdentificationRequired());
        var unreadable = await h.Service.GetReviewAsync(created.Id);
        Assert.Equal([LeaseNeedCodes.ApeIdentificationMissing], unreadable.Checks.Where(c => c.Severity == LeaseReviewSeverity.Block).Select(c => c.Code));
        h.Ape.Setup(a => a.EnsurePropertyHasValidApeAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);

        // A contract already signed prints nothing: its APE is not asked for.
        var imported = await h.CreateAsync(LeaseOrigin.Imported);
        Assert.Empty(Needs(imported.Flow, 1));

        // With the code and the class on the document the review can be confirmed.
        await h.SetApeIdentificationAsync(LeaseDraftHarness.SeededApeCode, LeaseDraftHarness.SeededApeEnergyClass);
        Assert.NotNull((await h.Service.ConfirmReviewAsync(created.Id, saved.Version)).ReviewConfirmedAt);

        // The contract prints the last APE that has them: a later one that has not does not take its place.
        await using var db = h.SecondContext();
        db.PropertyDocuments.Add(new PropertyDocument
        {
            PropertyId = h.PropertyId,
            OrgId = h.OrgId,
            FileName = "newer.pdf",
            StorageUrl = "properties/newer.pdf",
            DocumentType = DocumentType.Ape,
            UploadedBy = LeaseDraftHarness.OwnerId,
            UploadedAt = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();
        var lease = await db.LeaseContracts.Include(l => l.Property).ThenInclude(p => p.PropertyDocuments).SingleAsync(l => l.Id == created.Id);
        Assert.Equal(
            $"codice {LeaseDraftHarness.SeededApeCode}, classe energetica {LeaseDraftHarness.SeededApeEnergyClass}",
            LeaseContractDocument.ResolveData(lease)[LeaseContractPlaceholders.ApeData]);
    }

    [Fact]
    public async Task Confirm_ACompleteDraft_SavesTheMomentTheImpressionAndTheEvent()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();

        var confirmed = await h.Service.ConfirmReviewAsync(created.Id, version);

        Assert.Equal(version + 1, confirmed.Version);
        Assert.Equal(LeaseDraftHarness.Now.UtcDateTime, confirmed.ReviewConfirmedAt);
        Assert.Equal(LeaseStepState.Done, confirmed.Flow[6].State);
        Assert.Equal(LeaseStepState.Todo, confirmed.Flow[7].State);
        var lease = await h.LoadAsync(created.Id);
        Assert.Equal(LeaseDraftHarness.Now.UtcDateTime, lease.ReviewConfirmedAt);
        Assert.Matches("^[0-9a-f]{64}$", lease.ReviewFingerprint);
        Assert.Equal(6, lease.WizardMaxStep);
        Assert.Equal(1, lease.Events.Count(e => e.EventType == LeaseEventType.ReviewConfirmed));
    }

    [Fact]
    public async Task Confirm_Twice_ChangesNothing_NoSecondEvent()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var first = await h.Service.ConfirmReviewAsync(created.Id, version);

        var second = await h.Service.ConfirmReviewAsync(created.Id, null);

        Assert.Equal(first.Version, second.Version);
        Assert.Equal(1, (await h.LoadAsync(created.Id)).Events.Count(e => e.EventType == LeaseEventType.ReviewConfirmed));
    }

    [Fact]
    public async Task Confirm_OnAnOldVersion_Is409()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => h.Service.ConfirmReviewAsync(created.Id, version - 1));

        Assert.Equal("lease_draft_conflict", error.Code);
    }

    [Fact]
    public async Task Confirm_ABlockThatAppearedWithoutTheValuesChanging_IsStillABlock_TheApeWasRemoved()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        h.Ape.Setup(a => a.EnsurePropertyHasValidApeAsync(It.IsAny<Guid>())).ThrowsAsync(ApeComplianceException.Required());

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => h.Service.ConfirmReviewAsync(created.Id, version));

        Assert.Equal("lease_review_blocked", error.Code);
    }

    [Fact]
    public async Task Save_AChangeAfterTheConfirmation_WithdrawsIt_WithItsEvent()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var confirmed = await h.Service.ConfirmReviewAsync(created.Id, version);

        var saved = await h.SaveAsync(created.Id, confirmed.Version, 3, """{"monthlyRent":950}""");

        var lease = await h.LoadAsync(created.Id);
        Assert.Null(lease.ReviewConfirmedAt);
        Assert.Null(lease.ReviewFingerprint);
        var reopened = Assert.Single(lease.Events, e => e.EventType == LeaseEventType.ReviewReopened);
        Assert.Equal("changed", reopened.Payload);
        Assert.Equal(LeaseStepState.Todo, saved.Flow[6].State);
    }

    [Fact]
    public async Task Save_TheSameValuesAgain_AfterTheConfirmation_KeepsIt_TheAutosaveRepeatsWhatWasConfirmed()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var confirmed = await h.Service.ConfirmReviewAsync(created.Id, version);

        // The same rent, written with a decimal point; the same start with a space; the tenant typed again, with the same id.
        var tenantId = (await h.Service.GetAsync(created.Id)).Values.Tenants.Single().Id;
        var a = await h.SaveAsync(created.Id, confirmed.Version, 3, """{"monthlyRent":900.00,"contractType":"libero"}""");
        var b = await h.SaveAsync(created.Id, a.Version, 4, """{"startDate":" 2026-11-01 "}""");
        var c = await h.SaveAsync(created.Id, b.Version, 2, $$"""
            {"tenants":[{"id":"{{tenantId}}","firstName":"Giulia","lastName":"Verdi","fiscalCode":"vrdgli85b02f205a","citizenship":"it","residence":"Bari, via Argiro 7"}]}
            """);

        var lease = await h.LoadAsync(created.Id);
        Assert.NotNull(lease.ReviewConfirmedAt);
        Assert.DoesNotContain(lease.Events, e => e.EventType == LeaseEventType.ReviewReopened);
        Assert.Equal(LeaseStepState.Done, c.Flow[6].State);
    }

    [Fact]
    public async Task Reopen_WithdrawsTheConfirmation_AndWithdrawingWhatIsNotThereChangesNothing()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var confirmed = await h.Service.ConfirmReviewAsync(created.Id, version);

        var reopened = await h.Service.ReopenReviewAsync(created.Id, confirmed.Version);
        var again = await h.Service.ReopenReviewAsync(created.Id, null);

        Assert.Null(reopened.ReviewConfirmedAt);
        Assert.Equal(confirmed.Version + 1, reopened.Version);
        Assert.Equal(reopened.Version, again.Version);
        var lease = await h.LoadAsync(created.Id);
        var events = lease.Events.Where(e => e.EventType == LeaseEventType.ReviewReopened).ToList();
        Assert.Equal("withdrawn", Assert.Single(events).Payload);
    }

    [Fact]
    public async Task GetReview_ListsTheChecks_WorstFirst_AndCountsThem()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();

        var review = await h.Service.GetReviewAsync(created.Id);

        Assert.Equal(created.Version, review.Version);
        Assert.Null(review.ReviewConfirmedAt);
        Assert.Contains(review.Checks, c => c.Code == LeaseNeedCodes.RentMissing && c.Severity == LeaseReviewSeverity.Block);
        Assert.Equal(review.Checks.Count(c => c.Severity == LeaseReviewSeverity.Block), review.Counts.Block);
        Assert.Equal(review.Checks.Count(c => c.Severity == LeaseReviewSeverity.Ok), review.Counts.Ok);
        Assert.Equal(review.Checks.Select(c => (int)c.Severity).Order(), review.Checks.Select(c => (int)c.Severity));
    }

    [Fact]
    public async Task GetReview_WarnsAboutAnotherContractOfTheProperty_InTheSameDates_NotAboutADraftNobodyHandedOut()
    {
        using var h = await NewAsync();
        var (created, _) = await h.CompleteAsync();
        var otherProperty = await h.SeedPropertyAsync();
        LeaseContract Other(Guid propertyId, LeaseStatus status, DateTime start, DateTime end) => new()
        {
            PropertyId = propertyId,
            OrgId = h.OrgId,
            Status = status,
            StartDate = start,
            EndDate = end,
            MonthlyRent = 800m,
        };

        await using (var db = h.SecondContext())
        {
            DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

            // Overlaps, signed: a warning.
            db.LeaseContracts.Add(Other(h.PropertyId, LeaseStatus.Signed, Day(2026, 6, 1), Day(2030, 5, 31)));

            // Overlaps, a draft nobody handed out for signature: not a contract that stands.
            db.LeaseContracts.Add(Other(h.PropertyId, LeaseStatus.Draft, Day(2026, 11, 1), Day(2030, 10, 31)));

            // Rejected: it will never take effect.
            db.LeaseContracts.Add(Other(h.PropertyId, LeaseStatus.Rejected, Day(2026, 11, 1), Day(2030, 10, 31)));

            // Ended before this one starts.
            db.LeaseContracts.Add(Other(h.PropertyId, LeaseStatus.Registered, Day(2022, 1, 1), Day(2026, 10, 31)));

            // Another property.
            db.LeaseContracts.Add(Other(otherProperty.Id, LeaseStatus.Registered, Day(2026, 6, 1), Day(2030, 5, 31)));
            await db.SaveChangesAsync();
        }

        var review = await h.Service.GetReviewAsync(created.Id);

        var overlap = Assert.Single(review.Checks, c => c.Code == LeaseNeedCodes.LeaseOverlap);
        Assert.Equal(1, overlap.Params!["count"]); // only the signed one: the registered one ends the day before this one starts
        Assert.Equal(LeaseReviewSeverity.Warn, overlap.Severity);
    }

    [Fact]
    public async Task GetReview_ATemplateNotApproved_IsToKnow_ADraftWithTheShortModeIsWarned()
    {
        using var h = await NewAsync();
        var (created, _) = await h.CompleteAsync();
        h.Templates.Setup(t => t.GetFinalContractBlocker(It.IsAny<LeaseContract>())).Returns("contract_template_not_approved");
        await using (var db = h.SecondContext())
        {
            (await db.Properties.SingleAsync(p => p.Id == h.PropertyId)).RentalMode = RentalMode.Short;
            await db.SaveChangesAsync();
        }

        var review = await h.Service.GetReviewAsync(created.Id);

        Assert.Equal(LeaseReviewSeverity.Info, Assert.Single(review.Checks, c => c.Code == LeaseNeedCodes.TemplateNotApproved).Severity);
        Assert.Equal(LeaseReviewSeverity.Warn, Assert.Single(review.Checks, c => c.Code == LeaseNeedCodes.PropertyShortMode).Severity);
        Assert.Equal(0, review.Counts.Block);
    }

    // ---- downloaded for signature --------------------------------------------------------------------------------

    [Fact]
    public async Task Sent_BeforeTheReviewIsConfirmed_Is422()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => h.Service.MarkSentAsync(created.Id, version));

        Assert.Equal("lease_review_not_confirmed", error.Code);
        Assert.Null((await h.LoadAsync(created.Id)).SentForSignatureAt);
    }

    [Fact]
    public async Task Sent_WithATemplateNotApproved_Is422_AndTheStateDoesNotSaySomethingFalse()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var confirmed = await h.Service.ConfirmReviewAsync(created.Id, version);
        h.Templates.Setup(t => t.EnsureFinalContractAvailable(It.IsAny<LeaseContract>()))
            .Throws(new DomainRuleException("contract_template_not_approved", "LeaseContractTemplateNotApproved"));

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => h.Service.MarkSentAsync(created.Id, confirmed.Version));

        Assert.Equal("contract_template_not_approved", error.Code);
        var lease = await h.LoadAsync(created.Id);
        Assert.Null(lease.SentForSignatureAt);
        Assert.Equal(confirmed.Version, lease.DraftVersion);
        Assert.NotNull(lease.ReviewConfirmedAt);
        Assert.DoesNotContain(lease.Events, e => e.EventType == LeaseEventType.SentForSignature);
        Assert.Equal(LeaseStatus.Draft, lease.Status);
    }

    [Fact]
    public async Task Sent_WithAnApprovedTemplate_SetsTheMoment_KeepsTheStatus_AndRevokingTakesItBack()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var confirmed = await h.Service.ConfirmReviewAsync(created.Id, version);

        var sent = await h.Service.MarkSentAsync(created.Id, confirmed.Version);

        Assert.Equal(LeaseDraftHarness.Now.UtcDateTime, sent.SentForSignatureAt);
        Assert.Equal(confirmed.Version + 1, sent.Version);
        Assert.Equal([LeaseNeedCodes.SignedCopyMissing], Needs(sent.Flow, 7));
        var lease = await h.LoadAsync(created.Id);
        Assert.Equal(LeaseStatus.Draft, lease.Status);
        Assert.Equal(7, lease.WizardMaxStep);
        Assert.Equal(1, lease.Events.Count(e => e.EventType == LeaseEventType.SentForSignature));
        h.Templates.Verify(t => t.EnsureFinalContractAvailable(It.IsAny<LeaseContract>()), Times.Once);

        var again = await h.Service.MarkSentAsync(created.Id, null);
        Assert.Equal(sent.Version, again.Version);

        var revoked = await h.Service.RevokeSentAsync(created.Id, null);
        Assert.Null(revoked.SentForSignatureAt);
        Assert.Equal([LeaseNeedCodes.NotSent], Needs(revoked.Flow, 7));
        var after = await h.LoadAsync(created.Id);
        Assert.Equal("withdrawn", Assert.Single(after.Events, e => e.EventType == LeaseEventType.SentRevoked).Payload);
        Assert.NotNull(after.ReviewConfirmedAt);
        var twice = await h.Service.RevokeSentAsync(created.Id, null);
        Assert.Equal(revoked.Version, twice.Version);
    }

    [Fact]
    public async Task Sent_ReopeningTheReview_TakesBack_TheDownloadForSignature_Too()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var confirmed = await h.Service.ConfirmReviewAsync(created.Id, version);
        var sent = await h.Service.MarkSentAsync(created.Id, confirmed.Version);

        await h.SaveAsync(created.Id, sent.Version, 4, """{"startDate":"2026-12-01"}""");

        var lease = await h.LoadAsync(created.Id);
        Assert.Null(lease.ReviewConfirmedAt);
        Assert.Null(lease.SentForSignatureAt);
        Assert.Equal("review_reopened", Assert.Single(lease.Events, e => e.EventType == LeaseEventType.SentRevoked).Payload);
    }

    [Fact]
    public async Task Sent_ATermThatTheTypeDoesNotAccept_Is422_BeforeTheTemplateIsAsked()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var confirmed = await h.Service.ConfirmReviewAsync(created.Id, version);
        await using (var db = h.SecondContext())
        {
            // A column changed behind the draft's back (a bug elsewhere): the gate of the download still refuses it.
            (await db.LeaseContracts.SingleAsync(l => l.Id == created.Id)).EndDate = new DateTime(2027, 10, 31, 0, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => h.Service.MarkSentAsync(created.Id, confirmed.Version));

        Assert.Equal("lease_term_too_short", error.Code);
        h.Templates.Verify(t => t.EnsureFinalContractAvailable(It.IsAny<LeaseContract>()), Times.Never);
    }

    // ---- an imported draft ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnImportedDraft_HasNoReviewToConfirmAndNothingToDownload_Both422()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync(LeaseOrigin.Imported);

        var review = await Assert.ThrowsAsync<DomainRuleException>(() => h.Service.ConfirmReviewAsync(created.Id, null));
        var sent = await Assert.ThrowsAsync<DomainRuleException>(() => h.Service.MarkSentAsync(created.Id, null));

        Assert.Equal("lease_review_not_applicable", review.Code);
        Assert.Equal("lease_sent_not_applicable", sent.Code);
    }

    // ---- deletion ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ADraft_RemovesItWithItsPartiesAndEvents()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        await h.Service.ConfirmReviewAsync(created.Id, version);

        await h.Service.DeleteAsync(created.Id);

        await using var db = h.SecondContext();
        Assert.False(await db.LeaseContracts.AnyAsync(l => l.Id == created.Id));
        Assert.False(await db.Parties.AnyAsync(p => p.LeaseContractId == created.Id));
        Assert.False(await db.LeaseEvents.AnyAsync(e => e.LeaseContractId == created.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Service.GetAsync(created.Id));
        // The property is still there and has no lease any more.
        Assert.True(await db.Properties.AnyAsync(p => p.Id == h.PropertyId));
    }

    [Fact]
    public async Task Delete_TheFilesTiedToTheLease_AreDeletedToo()
    {
        using var h = await NewAsync();
        var (created, _) = await h.CompleteAsync();
        await using (var db = h.SecondContext())
        {
            var lease = await db.LeaseContracts.SingleAsync(l => l.Id == created.Id);
            lease.QuesturaCommunicationReceiptPath = $"leases/{h.OrgId}/{created.Id}/questura/receipt.pdf";
            db.LeaseRegistrations.Add(new LeaseRegistration
            {
                LeaseContractId = created.Id,
                ReceiptStoragePath = $"leases/{h.OrgId}/{created.Id}/registration/receipt.pdf",
            });
            await db.SaveChangesAsync();
        }

        await h.Service.DeleteAsync(created.Id);

        h.Storage.Verify(s => s.DeleteAsync(StorageBucket.Private, $"leases/{h.OrgId}/{created.Id}/questura/receipt.pdf", It.IsAny<CancellationToken>()), Times.Once);
        h.Storage.Verify(s => s.DeleteAsync(StorageBucket.Private, $"leases/{h.OrgId}/{created.Id}/registration/receipt.pdf", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("signed lease")]
    [InlineData("a draft with a signed copy")]
    [InlineData("a draft with a rent plan")]
    public async Task Delete_ANonDraftOrADraftWithASignedCopyOrARentPlan_Is409_AndNothingIsDeleted(string what)
    {
        using var h = await NewAsync();
        var (created, _) = await h.CompleteAsync();
        await using (var db = h.SecondContext())
        {
            var lease = await db.LeaseContracts.SingleAsync(l => l.Id == created.Id);
            switch (what)
            {
                case "signed lease":
                    lease.Status = LeaseStatus.Signed;
                    break;
                case "a draft with a signed copy":
                    lease.SignedPdfStoragePath = $"leases/{h.OrgId}/{created.Id}/signed-contract/c.pdf";
                    break;
                default:
                    db.RentSchedules.Add(new RentSchedule
                    {
                        OrgId = h.OrgId,
                        LeaseContractId = created.Id,
                        Cadence = RentCadence.Monthly,
                        BillingDayOfMonth = 5,
                        Amount = 900m,
                        NextRunDate = new DateOnly(2026, 12, 5),
                        IsActive = true,
                    });
                    break;
            }

            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => h.Service.DeleteAsync(created.Id));

        Assert.Equal("lease_not_deletable", error.Code);
        var kept = await h.LoadAsync(created.Id);
        Assert.Equal(2, kept.Parties.Count);
        h.Storage.Verify(s => s.DeleteAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_ALeaseThatDoesNotExist_Is404()
    {
        using var h = await NewAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => h.Service.DeleteAsync(Guid.NewGuid()));
    }

    // ---- canone concordato ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Concordato_ARentInsideTheRange_StoresTheAssessmentAndTheThreeYearsPeriod()
    {
        using var h = await NewAsync();
        h.SetupConcordatoRange(minMonthly: 700m, maxMonthly: 1000m);
        var (created, version) = await h.CompleteAsync();

        var saved = await h.SaveAsync(created.Id, version, 3, LeaseDraftHarness.ConcordatoStep3);

        Assert.Empty(Needs(saved.Flow, 3));
        var lease = await h.LoadAsync(created.Id);
        Assert.Equal(LeaseContractType.Concordato, lease.ContractType);
        Assert.Equal(FiscalRegime.CanoneConcordato, lease.FiscalRegime);
        Assert.Equal(new DateTime(2029, 10, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        Assert.NotNull(lease.ConcordatoAssessment);
        Assert.True(lease.ConcordatoAssessment!.RentWithinRange);
        Assert.Equal(700m, lease.ConcordatoAssessment.CanoneMinMensile);
        Assert.Equal(70m, lease.ConcordatoAssessment.Sqm);
        Assert.Null(lease.DraftData);
        // And the calculator was asked for a first period of 36 months, whatever the dates are.
        h.Eligibility.Verify(
            e => e.CalculateAsync(h.PropertyId, It.Is<RentBandCharacteristics>(c => c.Sqm == 70m && c.TypeAElementCount == 2), new LeaseTerm(36, 0), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Concordato_ARentOutsideAVerifiedRange_IsAnNeedOfTheStep_NeverStored()
    {
        using var h = await NewAsync();
        h.SetupConcordatoRange(minMonthly: 400m, maxMonthly: 700m);
        var (created, version) = await h.CompleteAsync();

        var saved = await h.SaveAsync(created.Id, version, 3, LeaseDraftHarness.ConcordatoStep3);

        var need = Assert.Single(saved.Flow[3].Needs);
        Assert.Equal(LeaseNeedCodes.RentOutOfBand, need.Code);
        Assert.Equal(400m, need.Params!["minMonthly"]);
        Assert.Equal(700m, need.Params["maxMonthly"]);
        var lease = await h.LoadAsync(created.Id);
        Assert.Null(lease.ConcordatoAssessment);
        Assert.NotNull(lease.DraftData);
        // The review cannot be confirmed, and the rent the landlord typed is still there to be corrected.
        await Assert.ThrowsAsync<DomainRuleException>(() => h.Service.ConfirmReviewAsync(created.Id, null));
        Assert.Equal(900m, (await h.Service.GetAsync(created.Id)).Values.MonthlyRent);
    }

    [Fact]
    public async Task Concordato_ARentOutsideAnIndicativeRange_IsStored_TheAssessmentRecordsIt_AndTheReviewWarns()
    {
        using var h = await NewAsync();
        h.SetupConcordatoRange(minMonthly: 400m, maxMonthly: 700m, indicative: true);
        var (created, version) = await h.CompleteAsync();

        var saved = await h.SaveAsync(created.Id, version, 3, LeaseDraftHarness.ConcordatoStep3);

        Assert.Empty(Needs(saved.Flow, 3));
        var lease = await h.LoadAsync(created.Id);
        Assert.False(lease.ConcordatoAssessment!.RentWithinRange);
        Assert.True(lease.ConcordatoAssessment.Indicative);
        var review = await h.Service.GetReviewAsync(created.Id);
        Assert.Contains(review.Checks, c => c.Code == LeaseNeedCodes.RentOutsideIndicativeBand && c.Severity == LeaseReviewSeverity.Warn);
        Assert.Equal(0, review.Counts.Block);
    }

    [Fact]
    public async Task Concordato_AComuneWithoutAgreementData_IsBlockedWithTheReason()
    {
        using var h = await NewAsync();
        h.SetupConcordatoUnavailable();
        var (created, version) = await h.CompleteAsync();

        var saved = await h.SaveAsync(created.Id, version, 3, LeaseDraftHarness.ConcordatoStep3);

        var need = Assert.Single(saved.Flow[3].Needs);
        Assert.Equal(LeaseNeedCodes.ConcordatoUnavailable, need.Code);
        Assert.Equal(CanoneConcordatoReasonCodes.DataUnavailable, need.Params!["reason"]);
        Assert.Null((await h.LoadAsync(created.Id)).ConcordatoAssessment);
    }

    [Fact]
    public async Task Concordato_WithoutItsCharacteristics_TheCalculatorIsNotAsked()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();

        var saved = await h.SaveAsync(created.Id, version, 3, """{"contractType":"Concordato"}""");

        Assert.Equal([LeaseNeedCodes.ConcordatoDataMissing], Needs(saved.Flow, 3));
        h.Eligibility.Verify(
            e => e.CalculateAsync(It.IsAny<Guid>(), It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Concordato_GoingBackToTheLibero_DropsTheAssessment_AndTheFourYearsPeriodComesBack()
    {
        using var h = await NewAsync();
        h.SetupConcordatoRange(700m, 1000m);
        var (created, version) = await h.CompleteAsync();
        var concordato = await h.SaveAsync(created.Id, version, 3, LeaseDraftHarness.ConcordatoStep3);

        await h.SaveAsync(created.Id, concordato.Version, 3, """{"contractType":"Libero"}""");

        var lease = await h.LoadAsync(created.Id);
        Assert.Null(lease.ConcordatoAssessment);
        Assert.Equal(LeaseContractType.Libero, lease.ContractType);
        Assert.Equal(new DateTime(2030, 10, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
    }

    // ---- what the review of the draft found ----------------------------------------------------------------------

    private const string ValidParties = """
        {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"RSSMRA80A01H501U","citizenship":"IT","residence":"Bari, via Sparano 1","contactEmail":"mario@example.com"}],
         "tenants":[{"firstName":"Giulia","lastName":"Verdi","fiscalCode":"VRDGLI85B02F205A","citizenship":"IT","residence":"Bari, via Argiro 7"}]}
        """;

    [Fact]
    public async Task Save_TheIdOfAnEntryOfTheOtherRole_MakesANewEntry_NeverTwoRowsWithOneKey_AndTheNextSaveStillWorks()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        var tenantId = (await h.Service.GetAsync(created.Id)).Values.Tenants.Single().Id;

        // The tenant's id inside the landlords, the tenants left alone: the same key would end in two rows of Parties (a 500), or in a
        // draft whose parties nobody can save any more.
        var saved = await h.SaveAsync(created.Id, version, 2, $$"""
            {"landlords":[{"id":"{{tenantId}}","firstName":"Mario","lastName":"Rossi","fiscalCode":"RSSMRA80A01H501U","citizenship":"IT","residence":"Bari, via Sparano 1"}]}
            """);

        var lease = await h.LoadAsync(created.Id);
        Assert.Equal(2, lease.Parties.Select(p => p.Id).Distinct().Count());
        Assert.NotEqual(tenantId, Assert.Single(saved.LandlordIds));
        Assert.Equal([tenantId], saved.TenantIds);
        var again = await h.SaveAsync(created.Id, saved.Version, 5, "{}");
        Assert.Equal(saved.Version + 1, again.Version);
    }

    [Fact]
    public async Task ConfirmReview_WhenTheDataBecameCompleteWithoutASave_WritesTheColumnsAndStopsKeepingTheTypedState()
    {
        using var h = await NewAsync();
        h.SetupConcordatoUnavailable();
        var created = await h.CreateAsync();
        var version = created.Version;
        version = (await h.SaveAsync(created.Id, version, 2, ValidParties)).Version;
        version = (await h.SaveAsync(created.Id, version, 3, LeaseDraftHarness.ConcordatoStep3)).Version;
        version = (await h.SaveAsync(created.Id, version, 4, """{"startDate":"2026-11-01"}""")).Version;
        var before = await h.LoadAsync(created.Id);
        Assert.NotNull(before.DraftData); // the comune has no agreement data: the typed state is kept, there is no range to store
        Assert.Null(before.ConcordatoAssessment);

        // The agreement data of the comune are loaded: nothing in the draft changed, and nothing blocks any more.
        h.SetupConcordatoRange(minMonthly: 800m, maxMonthly: 1000m);
        var confirmed = await h.Service.ConfirmReviewAsync(created.Id, version);

        var after = await h.LoadAsync(created.Id);
        Assert.Null(after.DraftData);
        Assert.NotNull(after.ConcordatoAssessment);
        Assert.Equal(LeaseContractType.Concordato, after.ContractType);
        Assert.Equal(900m, after.MonthlyRent);
        Assert.Equal(2, after.Parties.Count);
        Assert.NotNull(after.ReviewConfirmedAt);
        Assert.Equal(version + 1, confirmed.Version);
        Assert.Equal(version + 1, after.DraftVersion);
        Assert.Contains(after.Events, e => e.EventType == LeaseEventType.ReviewConfirmed);
        // And from now on a repeated autosave of the same values leaves the review as it is.
        var repeated = await h.SaveAsync(created.Id, confirmed.Version, 3, LeaseDraftHarness.ConcordatoStep3);
        Assert.NotNull((await h.LoadAsync(created.Id)).ReviewConfirmedAt);
        Assert.Equal(LeaseStepState.Done, repeated.Flow[6].State);
    }

    private static async Task<Guid> SeedOldFormDraftAsync(LeaseDraftHarness h, LeaseContractType type, DateTime end)
    {
        // A draft made by POST /api/leases before the guided draft existed: every column set, the parties without a residence (the old
        // form never asked for one), no typed state, no version.
        await using var db = h.SecondContext();
        var lease = new LeaseContract
        {
            PropertyId = h.PropertyId,
            OrgId = h.OrgId,
            Status = LeaseStatus.Draft,
            StartDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = end,
            MonthlyRent = 800m,
            WizardMaxStep = LeaseDraftDefaults.LastDataStep,
            Parties =
            [
                new Party { Role = PartyRole.Landlord, FirstName = "Mario", LastName = "Rossi", FiscalCode = "RSSMRA80A01H501U", Citizenship = "IT", ContactEmail = "mario@example.com" },
                new Party { Role = PartyRole.Tenant, FirstName = "Giulia", LastName = "Verdi", FiscalCode = "VRDGLI85B02F205A", Citizenship = "IT", ContactEmail = "giulia@example.com" },
            ],
        };
        lease.SetContractTerms(type, LeaseTaxRegime.CedolareSecca);
        db.LeaseContracts.Add(lease);
        await db.SaveChangesAsync();
        return lease.Id;
    }

    [Fact]
    public async Task Save_TheFirstSaveOfADraftMadeByTheOldForm_KeepsItsTransitoryTypeAndEnd_AndAsksForTheNeedTheOldFormNeverAsked()
    {
        using var h = await NewAsync();
        var id = await SeedOldFormDraftAsync(h, LeaseContractType.Transitorio, new DateTime(2027, 4, 30, 0, 0, 0, DateTimeKind.Utc));

        // The documents of the parties: nothing to do with the type or the term, which stay as the old form wrote them.
        var saved = await h.SaveAsync(id, 0, 5, "{}");

        var lease = await h.LoadAsync(id);
        Assert.Equal(LeaseContractType.Transitorio, lease.ContractType);
        Assert.Equal(new DateTime(2027, 4, 30, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        Assert.DoesNotContain(LeaseNeedCodes.LeaseTypeNotAvailable, Needs(saved.Flow, 3));
        Assert.Contains(LeaseNeedCodes.TransitoryPartyMissing, Needs(saved.Flow, 3));
        Assert.Contains(LeaseNeedCodes.TransitoryNeedMissing, Needs(saved.Flow, 3));

        // The landlord chooses a type that can be created: the term is the one of the type.
        await h.SaveAsync(id, saved.Version, 3, """{"contractType":"Libero"}""");
        lease = await h.LoadAsync(id);
        Assert.Equal(LeaseContractType.Libero, lease.ContractType);
        Assert.Equal(new DateTime(2030, 10, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
    }

    [Fact]
    public async Task Save_TheFirstSaveOfADraftMadeByTheOldForm_GivesALiberoTheTermOfItsType()
    {
        using var h = await NewAsync();
        var id = await SeedOldFormDraftAsync(h, LeaseContractType.Libero, new DateTime(2032, 10, 31, 0, 0, 0, DateTimeKind.Utc));

        var saved = await h.SaveAsync(id, 0, 5, "{}");

        // The wizard owns the term: it shows the end its type gives (computed.endDate), and the save writes the same.
        var lease = await h.LoadAsync(id);
        Assert.Equal(new DateTime(2030, 10, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        Assert.Equal("2030-10-31", (await h.Service.GetAsync(id)).Computed.EndDate);
        Assert.Equal(1, saved.Version);
    }

    [Fact]
    public async Task GetReview_ReadsThePropertyWithItsEnergyCertificate_TheDataTheTemplatePrints()
    {
        using var h = await NewAsync();
        var (created, _) = await h.CompleteAsync();
        var seen = -1;
        h.Templates.Setup(t => t.GetFinalContractBlocker(It.IsAny<LeaseContract>()))
            .Callback<LeaseContract>(l => seen = l.Property?.PropertyDocuments.Count(d => d.DocumentType == DocumentType.Ape) ?? -1)
            .Returns((string?)null);

        await h.Service.GetReviewAsync(created.Id);

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task MarkSent_ReadsThePropertyWithItsEnergyCertificate_BeforeItAsksTheTemplate()
    {
        using var h = await NewAsync();
        var (created, version) = await h.CompleteAsync();
        version = (await h.Service.ConfirmReviewAsync(created.Id, version)).Version;
        var seen = -1;
        h.Templates.Setup(t => t.EnsureFinalContractAvailable(It.IsAny<LeaseContract>()))
            .Callback<LeaseContract>(l => seen = l.Property?.PropertyDocuments.Count(d => d.DocumentType == DocumentType.Ape) ?? -1);

        await h.Service.MarkSentAsync(created.Id, version);

        Assert.Equal(1, seen);
    }

    // ---- no personal data in a log -------------------------------------------------------------------------------

    [Fact]
    public async Task TheLogs_OfAWholeLifeOfADraft_HoldNoPersonalData_OnlyIdsStepsAndCounts()
    {
        using var h = await NewAsync();
        var created = await h.CreateAsync();
        var version = created.Version;
        version = (await h.SaveAsync(created.Id, version, 2, """
            {"landlords":[{"firstName":"Zefiro","lastName":"Quarantaquattro","fiscalCode":"RSSMRA80A01H501U","citizenship":"IT","residence":"Via Segretissima 7, Pozzuoli","contactEmail":"zefiro@riservato.example"}],
             "tenants":[{"firstName":"Arianna","lastName":"Boldrini","fiscalCode":"VRDGLI85B02F205A","citizenship":"IT","residence":"Corso Nascosto 12, Taranto"}]}
            """)).Version;
        version = (await h.SaveAsync(created.Id, version, 3, """{"contractType":"Libero","taxRegime":"CedolareSecca","monthlyRent":901.5,"securityDeposit":1500}""")).Version;
        version = (await h.SaveAsync(created.Id, version, 4, """{"startDate":"2026-11-01"}""")).Version;
        version = (await h.Service.ConfirmReviewAsync(created.Id, version)).Version;
        version = (await h.Service.MarkSentAsync(created.Id, version)).Version;
        await Assert.ThrowsAsync<LeaseDraftValuesException>(() => h.SaveAsync(created.Id, version, 2, """{"landlords":[{"firstName":7,"lastName":"Quarantaquattro"}]}"""));
        await Assert.ThrowsAsync<DomainConflictException>(() => h.SaveAsync(created.Id, 1, 3, """{"monthlyRent":1}"""));
        await h.Service.DeleteAsync(created.Id);

        var logged = h.ServiceLog.AllOutput + h.DeleterLog.AllOutput;

        Assert.NotEmpty(h.ServiceLog.Entries);
        foreach (var secret in new[]
                 {
                     "Zefiro", "Quarantaquattro", "Arianna", "Boldrini", "RSSMRA80A01H501U", "VRDGLI85B02F205A", "Segretissima",
                     "Pozzuoli", "Nascosto", "Taranto", "riservato.example",
                 })
        {
            Assert.DoesNotContain(secret, logged, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(created.Id.ToString(), logged);
    }

    // ---- a company as a tenant -----------------------------------------------------------------------------------

    [Fact]
    public async Task ACompanyAsTenant_UnderTheCedolare_IsABlock_AndTheOrdinaryRegimeClearsIt()
    {
        using var h = await NewAsync();
        var (created, _) = await h.CompleteAsync(tenantFiscalCode: "1234567890" + CheckDigit("1234567890"));
        var blocked = await h.Service.GetAsync(created.Id);
        Assert.Equal([LeaseNeedCodes.CedolareNotAllowedCompany], Needs(blocked.Flow, 3));

        var fixedFlow = await h.SaveAsync(created.Id, blocked.Version, 3, """{"taxRegime":"Ordinario"}""");

        Assert.Empty(Needs(fixedFlow.Flow, 3));
    }

    private static string CheckDigit(string first10) =>
        Enumerable.Range(0, 10).Select(d => d.ToString()).Single(d => ItalianFiscalCode.IsValidNumericCode(first10 + d));
}
