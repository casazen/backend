using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class AccountantPackageSettingsTests : IDisposable
{
    private static readonly IDataProtectionProvider Provider = TestDataProtection.Shared;

    private const string UserId = "auth0|amministratrice";
    private const string Address = "  Commercialista@Studio-Rossi.example.com ";
    private const string NormalizedAddress = "commercialista@studio-rossi.example.com";

    private static readonly string[] AllKeys =
    [
        "guestDocuments", "ownerInvoices", "statements", "agencySummary", "vat", "withholding", "touristTax", "collections",
    ];

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private AccountantPackageSettingsService Service() => new(_db, NullLogger<AccountantPackageSettingsService>.Instance, _clock);

    private async Task<Guid> OrgAsync(PlanTier plan = PlanTier.Starter, bool downgraded = false)
    {
        var org = OrgTeamTestData.AddOrg(_db);
        org.PlanTier = plan;
        org.IsAgencyTierDowngraded = downgraded;
        await _db.SaveChangesAsync();
        return org.Id;
    }

    private static AccountantPackageSettingsChange Change(
        bool? enabled = true,
        string? email = Address,
        int? day = 5,
        IReadOnlyList<string>? contents = null,
        bool noContents = false,
        bool ownerAccounting = false) =>
        new(enabled, email, day, noContents ? null : contents ?? AllKeys, ownerAccounting);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccountantSettings_PartialScopeCannotSave_Test(bool grantedToTheMember)
    {
        var orgId = Guid.NewGuid();
        var partial = grantedToTheMember ? new HostScope(orgId, GrantedToUserId: UserId) : new HostScope(orgId, OwnerId: UserId);
        var harness = new ControllerHarness(orgId, partial);

        var saved = await harness.Controller.SaveSettings(WholeRequest(), CancellationToken.None);
        var read = await harness.Controller.GetSettings(CancellationToken.None);

        Assert.All(new[] { saved, read }, result => Assert.Equal((StatusCodes.Status403Forbidden, ProblemCodes.Forbidden), (StatusOf(result), CodeOf(result))));
        harness.Settings.VerifyNoOtherCalls();

        var whole = new ControllerHarness(orgId, new HostScope(orgId));
        whole.Settings
            .Setup(s => s.SaveAsync(orgId, It.IsAny<AccountantPackageSettingsChange>(), UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountantPackageSettingsService.ViewOf(null));

        var ok = await whole.Controller.SaveSettings(WholeRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(ok);
        whole.Settings.Verify(
            s => s.SaveAsync(
                orgId,
                It.Is<AccountantPackageSettingsChange>(c => c.Enabled == true && c.Email == Address && c.SendDay == 5 && c.Contents!.SequenceEqual(AllKeys)),
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Settings_ACallerWithNoOrgOrNoScope_Is403_AndNothingIsReadOrSaved()
    {
        var orgId = Guid.NewGuid();
        foreach (var harness in new[] { new ControllerHarness(orgId, scope: null), new ControllerHarness(orgId, new HostScope(orgId), hasOrg: false) })
        {
            var saved = await harness.Controller.SaveSettings(WholeRequest(), CancellationToken.None);
            var read = await harness.Controller.GetSettings(CancellationToken.None);

            Assert.All(new[] { saved, read }, result => Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result)));
            harness.Settings.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public void TheSettingsEndpoints_AreBehindTheFlagAndPaymentRead_AndOnlyTheSaveAsksPropertyWrite()
    {
        var controller = typeof(AccountantPackageController);
        var get = controller.GetMethod(nameof(AccountantPackageController.GetSettings))!;
        var put = controller.GetMethod(nameof(AccountantPackageController.SaveSettings))!;

        Assert.Equal(FeatureFlags.AccountantPackage, Assert.Single(controller.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.Equal([CasazenPolicies.PaymentRead], controller.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));
        Assert.Empty(get.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal([CasazenPolicies.PropertyWrite], put.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));
        Assert.All(new[] { get, put }, action => Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>()));
        Assert.Equal(["GET"], get.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods));
        Assert.Equal(["PUT"], put.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods));
        Assert.Equal("settings", get.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("settings", put.GetCustomAttribute<HttpPutAttribute>()!.Template);
    }

    [Fact]
    public async Task GetSettings_AnswersTheSettingsAsKeys_NeverCached()
    {
        var orgId = Guid.NewGuid();
        var harness = new ControllerHarness(orgId, new HostScope(orgId));
        harness.Settings
            .Setup(s => s.GetAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountantPackageSettingsView(
                true,
                NormalizedAddress,
                7,
                AccountantPackageFolders.Vat | AccountantPackageFolders.GuestDocuments,
                new AccountantPackageLastSend("2026-10", new DateTime(2026, 11, 7, 5, 0, 0, DateTimeKind.Utc), AccountantPackageSendResult.Failed, "package_too_large"),
                new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc)));

        var result = await harness.Controller.GetSettings(CancellationToken.None);

        var dto = Assert.IsType<AccountantPackageSettingsDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal((true, NormalizedAddress, 7), (dto.Enabled, dto.Email, dto.SendDay));
        Assert.Equal(["guestDocuments", "vat"], dto.Contents);
        Assert.Equal(("2026-10", "failed", "package_too_large"), (dto.LastSend!.Period, dto.LastSend.Result, dto.LastSend.Error));
        Assert.Equal("private, no-store", harness.Http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task AccountantSettings_PropertyManagerCannotSaveOwnerAccountingFolders_Test()
    {
        var orgId = await OrgAsync(PlanTier.Agency);
        var propertyManager = new ControllerHarness(orgId, new HostScope(orgId), holdsStatementView: false, service: Service());
        var administrator = new ControllerHarness(orgId, new HostScope(orgId), holdsStatementView: true, service: Service());

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => propertyManager.Controller.SaveSettings(WholeRequest(), CancellationToken.None));

        Assert.Equal(AccountantPackageErrors.OwnerAccountingRequired, refused.Code);
        Assert.Empty(_db.AccountantPackageSettings);

        var readable = new SaveAccountantPackageSettingsRequest { Enabled = true, Email = Address, SendDay = 5, Contents = ["guestDocuments", "touristTax", "collections"] };
        var saved = Assert.IsType<AccountantPackageSettingsDto>(Assert.IsType<OkObjectResult>(await propertyManager.Controller.SaveSettings(readable, CancellationToken.None)).Value);
        Assert.Equal(["guestDocuments", "touristTax", "collections"], saved.Contents);

        var everything = Assert.IsType<AccountantPackageSettingsDto>(Assert.IsType<OkObjectResult>(await administrator.Controller.SaveSettings(WholeRequest(), CancellationToken.None)).Value);
        Assert.Equal(AllKeys, everything.Contents);
    }

    [Theory]
    [InlineData("ownerInvoices")]
    [InlineData("statements")]
    [InlineData("withholding")]
    [InlineData("agencySummary")]
    [InlineData("vat")]
    public async Task Save_AFolderThatCarriesTheOwnerAccounting_IsRefusedToACallerWithoutTheStatementPermission_OnAnAgency(string folder)
    {
        var orgId = await OrgAsync(PlanTier.Agency);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service().SaveAsync(orgId, Change(contents: [folder, "collections"]), UserId));

        Assert.Equal(AccountantPackageErrors.OwnerAccountingRequired, refused.Code);
        Assert.DoesNotContain("studio", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_db.AccountantPackageSettings);

        var allowed = await Service().SaveAsync(orgId, Change(contents: ["guestDocuments", "collections"]), UserId);
        await Assert.ThrowsAsync<DomainRuleException>(() => Service().SaveAsync(orgId, Change(contents: [folder], day: 9), "auth0|altro"));
        Assert.Equal(allowed, await Service().GetAsync(orgId));

        var holder = await Service().SaveAsync(orgId, Change(contents: [folder, "collections"], ownerAccounting: true), "auth0|amministratrice");
        Assert.Contains(folder, AccountantPackageFolderRules.KeysOf(holder.Contents));
    }

    [Theory]
    [InlineData(PlanTier.Starter, false)]
    [InlineData(PlanTier.Pro, false)]
    [InlineData(PlanTier.Scale, false)]
    [InlineData(PlanTier.Agency, true)]
    public async Task Save_AnOrganizationWhosePackageHoldsNoOwnerAccounting_RefusesNobody(PlanTier plan, bool downgraded)
    {
        var orgId = await OrgAsync(plan, downgraded);

        var view = await Service().SaveAsync(orgId, Change(contents: AllKeys), UserId);

        Assert.Equal(AccountantPackageFolders.All, view.Contents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveSettings_TheStatementPermission_IsWhatTheServiceIsToldTheCallerMayChoose(bool holdsStatementView)
    {
        var orgId = Guid.NewGuid();
        var harness = new ControllerHarness(orgId, new HostScope(orgId), holdsStatementView: holdsStatementView);
        harness.Settings
            .Setup(s => s.SaveAsync(orgId, It.IsAny<AccountantPackageSettingsChange>(), UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountantPackageSettingsService.ViewOf(null));

        var result = await harness.Controller.SaveSettings(WholeRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        harness.Settings.Verify(s => s.SaveAsync(orgId, It.Is<AccountantPackageSettingsChange>(c => c.OwnerAccounting == holdsStatementView), UserId, It.IsAny<CancellationToken>()), Times.Once);
        harness.Authorization.Verify(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.AgencyStatementView), Times.Once);
    }

    /// <summary>
    /// PROOF TEST: privacy rule. The address of the accountant is in the list of the encrypted columns, with a purpose of its own, and what the converter of the column hands to the table is a Data Protection payload with
    /// none of the address in it; only the converter of that purpose reads it back, and the service saves and reads the address through the context that has the encryption configured. The EF InMemory database keeps the
    /// values the model hands it and not the stored text, so the proof is on the model and its converter (the same shape as the other encrypted columns).
    /// </summary>
    [Fact]
    public async Task AccountantPackageSettings_EmailEncryptedAtRest_Test()
    {
        Assert.Contains(
            EncryptedColumns.All,
            column => column.EntityType == typeof(AccountantPackageSettings)
                      && column.Property == nameof(AccountantPackageSettings.AccountantEmail)
                      && column.Purpose == EncryptedColumns.AccountantPackagePurpose);
        Assert.Equal("Casazen.AccountantPackage.Settings", EncryptedColumns.AccountantPackagePurpose);

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenantContext: null, Provider);
        var column = db.Model.FindEntityType(typeof(AccountantPackageSettings))!.FindProperty(nameof(AccountantPackageSettings.AccountantEmail))!;
        var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());
        Assert.Null(column.GetMaxLength());
        Assert.True(EncryptedColumns.IsConfigured(db));

        var stored = (string)converter.ConvertToProvider(NormalizedAddress)!;

        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(NormalizedAddress, stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("studio-rossi", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commercialista", stored, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(NormalizedAddress, converter.ConvertFromProvider(stored));
        Assert.ThrowsAny<CryptographicException>(() => Provider.CreateProtector(EncryptedColumns.GuestDocumentPurpose).Unprotect(stored));

        var org = OrgTeamTestData.AddOrg(db);
        await db.SaveChangesAsync();
        var service = new AccountantPackageSettingsService(db, NullLogger<AccountantPackageSettingsService>.Instance, _clock);
        await service.SaveAsync(org.Id, Change(), UserId);
        Assert.Equal(NormalizedAddress, (await service.GetAsync(org.Id)).Email);
    }

    [Fact]
    public async Task Get_AnOrgThatSavedNothing_HasTheDefaults_AndNothingIsWritten()
    {
        var orgId = await OrgAsync();

        var view = await Service().GetAsync(orgId);

        Assert.Equal(new AccountantPackageSettingsView(false, null, AccountantPackageSettings.DefaultSendDay, AccountantPackageFolders.All, null, null), view);
        Assert.Equal(3, AccountantPackageSettings.DefaultSendDay);
        Assert.Empty(_db.AccountantPackageSettings);
    }

    [Fact]
    public async Task Save_TheFirstSave_CreatesTheRow_KeepsTheAddressInItsPlainForm_AndGetReadsItBack()
    {
        var orgId = await OrgAsync();

        var saved = await Service().SaveAsync(orgId, Change(contents: ["vat", "guestDocuments"]), $"  {UserId} ");
        var read = await Service().GetAsync(orgId);

        Assert.Equal(saved, read);
        Assert.Equal((true, NormalizedAddress, 5), (read.Enabled, read.Email, read.SendDay));
        Assert.Equal(AccountantPackageFolders.Vat | AccountantPackageFolders.GuestDocuments, read.Contents);
        Assert.Null(read.LastSend);
        var row = await _db.AccountantPackageSettings.AsNoTracking().SingleAsync();
        Assert.Equal((orgId, UserId, new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc)), (row.OrgId, row.UpdatedByUserId, row.UpdatedAt));
        Assert.Equal(row.CreatedAt, row.UpdatedAt);
    }

    [Fact]
    public async Task Save_ASecondSave_ReplacesEveryField_InTheOneRow_AndLeavesTheHistoryOfTheSendAlone()
    {
        var orgId = await OrgAsync();
        await Service().SaveAsync(orgId, Change(), UserId);
        var row = await _db.AccountantPackageSettings.SingleAsync();
        row.LastSendPeriod = "2026-10";
        row.LastSendAt = new DateTime(2026, 11, 5, 5, 0, 0, DateTimeKind.Utc);
        row.LastSendResult = AccountantPackageSendResult.Sent;
        row.StoredPackagePeriod = "2026-10";
        row.StoredPackageAt = row.LastSendAt;
        await _db.SaveChangesAsync();
        _clock.Advance(TimeSpan.FromDays(1));

        var view = await Service().SaveAsync(orgId, Change(enabled: false, email: null, day: 28, contents: ["collections"]), "auth0|altro");

        Assert.Equal((false, (string?)null, 28, AccountantPackageFolders.Collections), (view.Enabled, view.Email, view.SendDay, view.Contents));
        Assert.Equal(("2026-10", AccountantPackageSendResult.Sent), (view.LastSend!.Period, view.LastSend.Result));
        var stored = await _db.AccountantPackageSettings.AsNoTracking().SingleAsync();
        Assert.Null(stored.AccountantEmail);
        Assert.Equal(("2026-10", "auth0|altro"), (stored.StoredPackagePeriod, stored.UpdatedByUserId));
        Assert.True(stored.UpdatedAt > stored.CreatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Save_AnEmptyAddress_IsStoredAsNothing(string? email)
    {
        var orgId = await OrgAsync();

        var view = await Service().SaveAsync(orgId, Change(enabled: false, email: email), UserId);

        Assert.Null(view.Email);
        Assert.Null((await _db.AccountantPackageSettings.AsNoTracking().SingleAsync()).AccountantEmail);
    }

    [Theory]
    [InlineData("missing-enabled", AccountantPackageErrors.SettingsInvalid)]
    [InlineData("missing-day", AccountantPackageErrors.SettingsInvalid)]
    [InlineData("missing-contents", AccountantPackageErrors.SettingsInvalid)]
    [InlineData("name", AccountantPackageErrors.RecipientInvalid)]
    [InlineData("spaces", AccountantPackageErrors.RecipientInvalid)]
    [InlineData("no-dot", AccountantPackageErrors.RecipientInvalid)]
    [InlineData("too-long", AccountantPackageErrors.RecipientInvalid)]
    [InlineData("on-without-address", AccountantPackageErrors.RecipientMissing)]
    [InlineData("day-0", AccountantPackageErrors.SendDayInvalid)]
    [InlineData("day-29", AccountantPackageErrors.SendDayInvalid)]
    [InlineData("contents-empty", AccountantPackageErrors.ContentsInvalid)]
    [InlineData("contents-unknown", AccountantPackageErrors.ContentsInvalid)]
    [InlineData("contents-repeated", AccountantPackageErrors.ContentsInvalid)]
    [InlineData("contents-case", AccountantPackageErrors.ContentsInvalid)]
    public async Task Save_AChangeThatIsRefused_SavesNothing(string what, string code)
    {
        var orgId = await OrgAsync();
        var change = what switch
        {
            "missing-enabled" => Change(enabled: null),
            "missing-day" => Change(day: null),
            "missing-contents" => Change(noContents: true),
            "name" => Change(email: "Commercialista <commercialista@studio.example.com>"),
            "spaces" => Change(email: "commercialista @studio.example.com"),
            "no-dot" => Change(email: "commercialista@studio"),
            "too-long" => Change(email: new string('a', 250) + "@studio.example.com"),
            "on-without-address" => Change(enabled: true, email: " "),
            "day-0" => Change(day: 0),
            "day-29" => Change(day: 29),
            "contents-empty" => Change(contents: []),
            "contents-unknown" => Change(contents: ["vat", "everything"]),
            "contents-repeated" => Change(contents: ["vat", "vat"]),
            "contents-case" => Change(contents: ["Vat"]),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Service().SaveAsync(orgId, change, UserId));

        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("studio", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_db.AccountantPackageSettings);
    }

    [Fact]
    public async Task Save_ARefusedChange_LeavesTheSettingsThatWereSavedAsTheyAre()
    {
        var orgId = await OrgAsync();
        var before = await Service().SaveAsync(orgId, Change(), UserId);

        await Assert.ThrowsAsync<DomainRuleException>(() => Service().SaveAsync(orgId, Change(day: 31), "auth0|altro"));

        Assert.Equal(before, await Service().GetAsync(orgId));
    }

    [Fact]
    public async Task Settings_OfAnotherOrg_AreNeverReadOrReplaced()
    {
        var first = await OrgAsync();
        var second = await OrgAsync();
        await Service().SaveAsync(first, Change(email: "primo@studio.example.com"), UserId);

        var otherBefore = await Service().GetAsync(second);
        await Service().SaveAsync(second, Change(email: "secondo@studio.example.com", day: 9, contents: ["vat"]), UserId);

        Assert.Null(otherBefore.Email);
        Assert.Equal("primo@studio.example.com", (await Service().GetAsync(first)).Email);
        Assert.Equal(("secondo@studio.example.com", 9), ((await Service().GetAsync(second)).Email, (await Service().GetAsync(second)).SendDay));
        Assert.Equal(2, await _db.AccountantPackageSettings.CountAsync());
    }

    [Fact]
    public async Task Save_WritesNoAddressToTheLog()
    {
        var orgId = await OrgAsync();
        var log = new Casazen.Tests.Unit.Logging.CapturingLogger<AccountantPackageSettingsService>();

        await new AccountantPackageSettingsService(_db, log, _clock).SaveAsync(orgId, Change(), UserId);

        var line = Assert.Single(log.Entries);
        Assert.Equal(
            (orgId, UserId, true, 5, 8),
            ((Guid)line.Values["OrgId"]!, (string)line.Values["UserId"]!, (bool)line.Values["Enabled"]!, (int)line.Values["SendDay"]!, (int)line.Values["Folders"]!));
        Assert.DoesNotContain("studio-rossi", line.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Folders_TheKeysAreTheFolderNamesOfThePackage_InItsOrder_AndRoundTrip()
    {
        Assert.Equal(AllKeys, AccountantPackageFolderRules.KeysOf(AccountantPackageFolders.All));
        Assert.True(AccountantPackageFolderRules.TryFromKeys(AllKeys, out var all));
        Assert.Equal(AccountantPackageFolders.All, all);
        Assert.Equal(
            [
                AccountantPackageStructure.GuestDocumentsFolder,
                AccountantPackageStructure.OwnerInvoicesFolder,
                AccountantPackageStructure.StatementsFolder,
                AccountantPackageStructure.AgencySummaryFolder,
                AccountantPackageStructure.VatFolder,
                AccountantPackageStructure.WithholdingFolder,
                AccountantPackageStructure.TouristTaxFolder,
                AccountantPackageStructure.CollectionsFolder,
            ],
            AccountantPackageFolderRules.Singles.Select(AccountantPackageFolderRules.FolderOf));
        Assert.Equal(
            AccountantPackageStructure.OwnerAccountingFolders.Order(),
            AccountantPackageFolderRules.Singles.Where(AccountantPackageFolderRules.IsOwnerAccounting).Select(AccountantPackageFolderRules.FolderOf).Order());
    }

    [Theory]
    [InlineData("vat,vat")]
    [InlineData("invoices")]
    [InlineData("")]
    [InlineData("vat,,collections")]
    [InlineData("Vat")]
    public void Folders_AWrongList_IsNeverASet(string keys)
    {
        Assert.False(AccountantPackageFolderRules.TryFromKeys(keys.Split(','), out var set));
        Assert.Equal(AccountantPackageFolders.None, set);
    }

    [Fact]
    public void Folders_AMissingOrEmptyList_IsNeverASet_NotEvenAll()
    {
        Assert.False(AccountantPackageFolderRules.TryFromKeys([], out var empty));
        Assert.Equal(AccountantPackageFolders.None, empty);
        Assert.False(AccountantPackageFolderRules.TryFromKeys(null, out var missing));
        Assert.Equal(AccountantPackageFolders.None, missing);
    }

    private static SaveAccountantPackageSettingsRequest WholeRequest() =>
        new() { Enabled = true, Email = Address, SendDay = 5, Contents = [.. AllKeys] };

    private static string CodeOf(IActionResult result)
    {
        var problem = Assert.IsAssignableFrom<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        return (string)problem.Extensions["code"]!;
    }

    private static int StatusOf(IActionResult result) => Assert.IsType<ObjectResult>(result).StatusCode!.Value;

    private sealed class ControllerHarness
    {
        public ControllerHarness(Guid orgId, HostScope? scope, bool hasOrg = true, bool holdsStatementView = true, IAccountantPackageSettingsService? service = null)
        {
            Authorization
                .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.AgencyStatementView))
                .ReturnsAsync(holdsStatementView ? AuthorizationResult.Success() : AuthorizationResult.Failed());
            var orgs = new Mock<IOrgContextResolver>();
            orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(hasOrg ? orgId : (Guid?)null);
            var scopes = new Mock<IHostScopeResolver>();
            scopes.Setup(s => s.ResolveAsync(UserId, It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);

            Http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", UserId)], "test")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            };
            Controller = new AccountantPackageController(
                Mock.Of<IAccountantPackageService>(),
                Mock.Of<IAgencySummaryService>(),
                service ?? Settings.Object,
                Mock.Of<IAccountantPackageDeliveryService>(),
                orgs.Object,
                scopes.Object,
                Authorization.Object,
                NullLogger<AccountantPackageController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = Http },
            };
        }

        public Mock<IAccountantPackageSettingsService> Settings { get; } = new();

        public Mock<IAuthorizationService> Authorization { get; } = new();

        public HttpContext Http { get; }

        public AccountantPackageController Controller { get; }
    }
}
