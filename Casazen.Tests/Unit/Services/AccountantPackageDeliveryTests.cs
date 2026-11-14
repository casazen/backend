using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.BackgroundJobs;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Infrastructure;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The data of the tests of the monthly package of the accountant (E15/F01/US02/T02): the org of an agency with its settings, the package service as a stand-in that writes a few bytes (what it holds is proved
/// elsewhere), a private bucket in memory, a recorder for the queue of the e-mails and a clock that says 06:30 in Rome on 3 November 2026, so the previous month is October. EF InMemory.
/// </summary>
internal sealed class AccountantPackageDeliveryWorld : IDisposable
{
    public const string Secret = "a-long-random-secret-of-the-signed-links";
    public const string ApiBase = "https://api.casazen.test";
    public const string Accountant = "commercialista@studio-rossi.example.com";

    public static readonly byte[] ZipBytes = Encoding.ASCII.GetBytes("PK-the-package-of-the-month");

    public static readonly DateOnly Today = new(2026, 11, 3);

    public AccountantPackageDeliveryWorld(string? secret = Secret, string? apiBase = ApiBase)
    {
        Flags.Setup(f => f.IsEnabled(FeatureFlags.AccountantPackage)).Returns(true);
        Packages
            .Setup(p => p.BuildAsync(It.IsAny<HostScope>(), It.IsAny<AccountantPackageAccess>(), It.IsAny<AccountantPackagePeriod>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<HostScope, AccountantPackageAccess, AccountantPackagePeriod, Stream, CancellationToken>((scope, access, period, destination, _) =>
            {
                Built.Add((scope, access, period));
                destination.Write(ZipBytes);
            })
            .ReturnsAsync(new AccountantPackageResult(2, 5, 1024, []));

        Links = new AccountantPackageLinks(Configuration(secret, apiBase), Clock);
    }

    public AppDbContext Db { get; } = OrgTeamTestData.NewDb();

    /// <summary>06:30 in Rome, 3 November 2026.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 3, 5, 30, 0, TimeSpan.Zero));

    public Mock<IAccountantPackageService> Packages { get; } = new();

    public InMemoryFileStorage Storage { get; } = new();

    public RecordingEmailQueue Emails { get; } = new();

    public Mock<IFeatureFlags> Flags { get; } = new();

    public AccountantPackageLinks Links { get; }

    /// <summary>What the package service was asked for, in order.</summary>
    public List<(HostScope Scope, AccountantPackageAccess Access, AccountantPackagePeriod Period)> Built { get; } = [];

    public static IConfiguration Configuration(string? secret = Secret, string? apiBase = ApiBase)
    {
        var settings = new Dictionary<string, string?>();
        if (secret is not null)
            settings["SIGNED_LINK_SECRET"] = secret;
        if (apiBase is not null)
            settings["App:ApiBaseUrl"] = apiBase;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    public AccountantPackageDelivery Delivery(IOptions<EmailOptions>? email = null, IFileStorage? storage = null) =>
        new(Db, Packages.Object, storage ?? Storage, Emails, email ?? EmailTestHelpers.ConfiguredEmail(), Links, Flags.Object, NullLogger<AccountantPackageDelivery>.Instance, Clock);

    public async Task<Guid> SeedAsync(
        string? email = Accountant,
        bool enabled = true,
        int day = 3,
        AccountantPackageFolders contents = AccountantPackageFolders.All,
        string language = "it",
        string name = "Casa Rossi Srl",
        bool active = true)
    {
        var org = OrgTeamTestData.AddOrg(Db);
        org.Name = name;
        org.DisplayName = name;
        org.DefaultLanguage = language;
        org.IsActive = active;
        Db.AccountantPackageSettings.Add(new AccountantPackageSettings
        {
            OrgId = org.Id,
            AccountantEmail = email,
            MonthlySendEnabled = enabled,
            SendDayOfMonth = day,
            Contents = contents,
        });
        await Db.SaveChangesAsync();
        return org.Id;
    }

    public async Task<AccountantPackageSettings> RowAsync(Guid orgId) =>
        await Db.AccountantPackageSettings.AsNoTracking().SingleAsync(s => s.OrgId == orgId);

    public static string KeyOf(Guid orgId, string period) => StorageKeys.AccountantPackage(orgId, period);

    public void Dispose() => Db.Dispose();
}

/// <summary>
/// E15/F01/US02/T02 (NF-101): the package of the previous month goes to the accountant of every organization that asked for it, once for each organization and month, only to the address saved in its settings, with a
/// signed link and no attachment; what fails is recorded and tried again, never swallowed; the package waits in the private storage for its link and no longer; the link opens that package for that org and that month and
/// nothing else, and every link that does not open is the same 404.
/// </summary>
public class AccountantPackageDeliveryTests : IDisposable
{
    private static readonly DateOnly Today = AccountantPackageDeliveryWorld.Today;

    private readonly AccountantPackageDeliveryWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ---------------------------------------------------------------- the data rule (the proof)

    /// <summary>
    /// PROOF TEST: data rule. However many times the job runs on the days from the due day on, a retry, a second instance or a «send a test» reach a month, the accountant of an organization gets one e-mail for
    /// that month, only at the address saved in its own settings; an organization that has not turned the send on, has no address, is not active or whose day has not come gets none, and the next month is another send.
    /// </summary>
    [Fact]
    public async Task AccountantPackageJob_OncePerOrganizationAndMonth_Test()
    {
        var first = await _world.SeedAsync("primo@studio.example.com");
        var second = await _world.SeedAsync("secondo@studio.example.com");
        var off = await _world.SeedAsync("spento@studio.example.com", enabled: false);
        var later = await _world.SeedAsync("dopo@studio.example.com", day: 5);
        var inactive = await _world.SeedAsync("inattiva@studio.example.com", active: false);
        var withoutAddress = await _world.SeedAsync(email: null);

        var due = await _world.Delivery().DueOrganizationsAsync(Today);
        Assert.Equal(new[] { first, second }.Order(), due.Order());
        Assert.DoesNotContain(inactive, due);
        foreach (var org in due)
            Assert.Equal(AccountantPackageMonthlyOutcome.Sent, await _world.Delivery().SendMonthlyAsync(org, Today));

        // The ones that are not due say why, and send nothing.
        Assert.Equal(AccountantPackageMonthlyOutcome.NotDue, await _world.Delivery().SendMonthlyAsync(later, Today));
        Assert.Equal(AccountantPackageMonthlyOutcome.NotConfigured, await _world.Delivery().SendMonthlyAsync(off, Today));
        Assert.Equal(AccountantPackageMonthlyOutcome.NotConfigured, await _world.Delivery().SendMonthlyAsync(withoutAddress, Today));

        // One e-mail for each, to its own address and nobody else's, and the package is kept under the key of the org and the month.
        var sent = _world.Emails.Snapshot();
        Assert.Equal(new[] { "primo@studio.example.com", "secondo@studio.example.com" }, sent.Select(e => e.To!).Order().ToArray());
        Assert.All(sent, e => Assert.Equal(EmailTemplates.Names.AccountantPackage, e.Template));
        foreach (var org in new[] { first, second })
            Assert.True(await _world.Storage.ExistsAsync(StorageBucket.Private, AccountantPackageDeliveryWorld.KeyOf(org, "2026-10")));

        var row = await _world.RowAsync(first);
        Assert.Equal(("2026-10", AccountantPackageSendResult.Sent, (string?)null, "2026-10"), (row.LastSendPeriod, row.LastSendResult, row.LastSendError, row.StoredPackagePeriod));

        // Again: the same day and the last day of the month do not make, store or queue anything.
        foreach (var day in new[] { Today, new DateOnly(2026, 11, 30) })
        {
            var stillDue = await _world.Delivery().DueOrganizationsAsync(day);
            Assert.DoesNotContain(first, stillDue);
            Assert.DoesNotContain(second, stillDue);
            foreach (var org in new[] { first, second })
                Assert.Equal(AccountantPackageMonthlyOutcome.AlreadySent, await _world.Delivery().SendMonthlyAsync(org, day));
        }

        Assert.Equal(2, _world.Emails.Snapshot().Count);
        Assert.Equal(2, _world.Built.Count);

        // The organization whose day is the 5th is due then, and only then, once.
        Assert.Equal(new[] { later }, await _world.Delivery().DueOrganizationsAsync(new DateOnly(2026, 11, 5)));
        Assert.Equal(AccountantPackageMonthlyOutcome.Sent, await _world.Delivery().SendMonthlyAsync(later, new DateOnly(2026, 11, 5)));
        Assert.Equal(AccountantPackageMonthlyOutcome.AlreadySent, await _world.Delivery().SendMonthlyAsync(later, new DateOnly(2026, 11, 20)));
        Assert.Equal(3, _world.Emails.Snapshot().Count);

        // December: the previous month is November, another package and another e-mail for each.
        var december = new DateOnly(2026, 12, 3);
        Assert.Equal(new[] { first, second }.Order(), (await _world.Delivery().DueOrganizationsAsync(december)).Order());
        Assert.Equal(AccountantPackageMonthlyOutcome.Sent, await _world.Delivery().SendMonthlyAsync(first, december));
        Assert.Equal(4, _world.Emails.Snapshot().Count);
        Assert.Equal("2026-11", (await _world.RowAsync(first)).LastSendPeriod);
    }

    [Fact]
    public async Task SendMonthly_QueuesOneEmailToTheStoredAddress_WithAWorkingSignedLink_AndNoAttachment()
    {
        var org = await _world.SeedAsync(contents: AccountantPackageFolders.Vat | AccountantPackageFolders.Collections);

        Assert.Equal(AccountantPackageMonthlyOutcome.Sent, await _world.Delivery().SendMonthlyAsync(org, Today));

        var (to, content, template) = Assert.Single(_world.Emails.Snapshot());
        Assert.Equal((AccountantPackageDeliveryWorld.Accountant, EmailTemplates.Names.AccountantPackage), (to, template));
        Assert.Equal("Pacchetto per il commercialista: ottobre 2026 di Casa Rossi Srl", content.Subject);
        Assert.Contains("Casa Rossi Srl", content.HtmlBody);
        Assert.DoesNotContain(AccountantPackageDeliveryWorld.Accountant, content.HtmlBody);

        // The link in it is the one the check opens, for this org and this month, until the end of its 30 days (shown in Italian time).
        var (urlOrg, period, expiresAt, signature) = LinkOf(content);
        Assert.Equal((org, "2026-10"), (urlOrg, period));
        Assert.Equal("2026-12-03T05:30:00Z", expiresAt);
        Assert.True(_world.Links.Verify(urlOrg, period, expiresAt, signature));
        Assert.Contains("03/12/2026 06:30", content.HtmlBody);
        Assert.Contains("non è allegato", content.HtmlBody);

        // The package was made for the whole org with the folders of the settings, and kept under the key of the org and the month.
        var built = Assert.Single(_world.Built);
        Assert.Equal((org, true, "2026-10"), (built.Scope.OrgId, built.Scope.IsOrgWide, built.Period.Key));
        Assert.Equal(AccountantPackageFolders.Vat | AccountantPackageFolders.Collections, built.Access.Folders);
        Assert.True(built.Access.OwnerAccounting);
        await using var stored = await _world.Storage.OpenReadAsync(StorageBucket.Private, AccountantPackageDeliveryWorld.KeyOf(org, "2026-10"));
        Assert.Equal(AccountantPackageDeliveryWorld.ZipBytes, ReadAll(stored!));

        // The link opens that package and nothing else.
        var file = await _world.Delivery().OpenAsync(urlOrg, period, expiresAt, signature);
        await using (file!.Content)
            Assert.Equal(AccountantPackageDeliveryWorld.ZipBytes, ReadAll(file.Content));
        Assert.Equal("casazen-commercialista-2026-10.zip", file.FileName);
    }

    [Fact]
    public async Task SendMonthly_AnOrgInEnglish_GetsTheEnglishEmail_AndEveryValueIsEncoded()
    {
        var org = await _world.SeedAsync(language: "en", name: "<b>Rossi</b> & Co");

        await _world.Delivery().SendMonthlyAsync(org, Today);

        var (_, content, _) = Assert.Single(_world.Emails.Snapshot());
        Assert.Equal("Accountant package: October 2026 of <b>Rossi</b> & Co", content.Subject);
        Assert.Contains("<html lang=\"en\">", content.HtmlBody);
        Assert.Contains("Download the package", content.HtmlBody);
        Assert.Contains("&lt;b&gt;Rossi&lt;/b&gt;", content.HtmlBody);
        Assert.DoesNotContain("<b>Rossi</b>", content.HtmlBody);
    }

    // ---------------------------------------------------------------- failures are recorded, never swallowed

    [Fact]
    public async Task SendMonthly_APackageOverTheCaps_IsRecordedAsFailed_NothingIsSent_AndTheNextRunTriesAgain()
    {
        var org = await _world.SeedAsync();
        _world.Packages
            .Setup(p => p.BuildAsync(It.IsAny<HostScope>(), It.IsAny<AccountantPackageAccess>(), It.IsAny<AccountantPackagePeriod>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException(AccountantPackageErrors.TooLarge, "AccountantPackageTooLarge", 400, 300, 200, 256));

        Assert.Equal(AccountantPackageMonthlyOutcome.Failed, await _world.Delivery().SendMonthlyAsync(org, Today));

        var row = await _world.RowAsync(org);
        Assert.Equal(("2026-10", AccountantPackageSendResult.Failed, AccountantPackageSendErrors.PackageTooLarge), (row.LastSendPeriod, row.LastSendResult, row.LastSendError));
        Assert.Null(row.StoredPackagePeriod);
        Assert.Empty(_world.Emails.Snapshot());
        Assert.Equal(new[] { org }, await _world.Delivery().DueOrganizationsAsync(Today));

        // The next run, with the caps raised, sends it, and the failure is gone from the settings.
        _world.Packages
            .Setup(p => p.BuildAsync(It.IsAny<HostScope>(), It.IsAny<AccountantPackageAccess>(), It.IsAny<AccountantPackagePeriod>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountantPackageResult(1, 1, 1, []));
        Assert.Equal(AccountantPackageMonthlyOutcome.Sent, await _world.Delivery().SendMonthlyAsync(org, Today));
        var sent = await _world.RowAsync(org);
        Assert.Equal((AccountantPackageSendResult.Sent, (string?)null), (sent.LastSendResult, sent.LastSendError));
        Assert.Single(_world.Emails.Snapshot());
    }

    [Fact]
    public async Task SendMonthly_AnErrorOfThePackage_IsAFailureWithItsCode_NotASuccessAndNotLost()
    {
        var org = await _world.SeedAsync();
        _world.Packages
            .Setup(p => p.BuildAsync(It.IsAny<HostScope>(), It.IsAny<AccountantPackageAccess>(), It.IsAny<AccountantPackagePeriod>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the reports are down"));

        Assert.Equal(AccountantPackageMonthlyOutcome.Failed, await _world.Delivery().SendMonthlyAsync(org, Today));

        var row = await _world.RowAsync(org);
        Assert.Equal((AccountantPackageSendResult.Failed, AccountantPackageSendErrors.PackageFailed), (row.LastSendResult, row.LastSendError));
        Assert.Empty(_world.Emails.Snapshot());
    }

    [Fact]
    public async Task SendMonthly_AStorageThatFails_IsRecorded_AndNoEmailPointsToAMissingPackage()
    {
        var org = await _world.SeedAsync();
        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.PutAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("the bucket is not there"));

        Assert.Equal(AccountantPackageMonthlyOutcome.Failed, await _world.Delivery(storage: storage.Object).SendMonthlyAsync(org, Today));

        var row = await _world.RowAsync(org);
        Assert.Equal((AccountantPackageSendResult.Failed, AccountantPackageSendErrors.StorageFailed, (string?)null), (row.LastSendResult, row.LastSendError, row.StoredPackagePeriod));
        Assert.Empty(_world.Emails.Snapshot());
    }

    [Fact]
    public async Task SendMonthly_AQueueThatRefuses_IsAFailure_ThePackageStaysNamedByItsRow_AndTheNextRunSendsOnce()
    {
        var org = await _world.SeedAsync();
        _world.Emails.Refuse = true;

        Assert.Equal(AccountantPackageMonthlyOutcome.Failed, await _world.Delivery().SendMonthlyAsync(org, Today));

        var failed = await _world.RowAsync(org);
        Assert.Equal((AccountantPackageSendResult.Failed, AccountantPackageSendErrors.EmailNotQueued, "2026-10"), (failed.LastSendResult, failed.LastSendError, failed.StoredPackagePeriod));
        Assert.Empty(_world.Emails.Snapshot());

        _world.Emails.Refuse = false;
        Assert.Equal(AccountantPackageMonthlyOutcome.Sent, await _world.Delivery().SendMonthlyAsync(org, Today));
        Assert.Equal(AccountantPackageMonthlyOutcome.AlreadySent, await _world.Delivery().SendMonthlyAsync(org, Today));
        Assert.Single(_world.Emails.Snapshot());
    }

    /// <summary>A test of a person that fails leaves the send of the month as it is: the settings keep saying the accountant has it, and the monthly run still sends nothing more.</summary>
    [Fact]
    public async Task ATestThatFails_NeverReplacesTheSendOfTheSameMonth()
    {
        var org = await _world.SeedAsync();
        Assert.Equal(AccountantPackageMonthlyOutcome.Sent, await _world.Delivery().SendMonthlyAsync(org, Today));
        _world.Emails.Refuse = true;

        await Assert.ThrowsAsync<DomainRuleException>(() => _world.Delivery().SendNowAsync(org, AccountantPackageAccess.Full));
        Assert.Equal(AccountantPackageMonthlyOutcome.AlreadySent, await _world.Delivery().SendMonthlyAsync(org, Today));

        var row = await _world.RowAsync(org);
        Assert.Equal((AccountantPackageSendResult.Sent, (string?)null), (row.LastSendResult, row.LastSendError));
    }

    [Fact]
    public async Task SendMonthly_WithoutTheSecretOfTheLinksOrTheAddressOfTheApi_FailsWithItsCode_AndMakesNothing()
    {
        foreach (var world in new[] { new AccountantPackageDeliveryWorld(secret: null), new AccountantPackageDeliveryWorld(apiBase: "http://not-https.test"), new AccountantPackageDeliveryWorld(apiBase: null) })
        {
            using (world)
            {
                var org = await world.SeedAsync();

                Assert.Equal(AccountantPackageMonthlyOutcome.Failed, await world.Delivery().SendMonthlyAsync(org, Today));

                var row = await world.RowAsync(org);
                Assert.Equal((AccountantPackageSendResult.Failed, AccountantPackageSendErrors.LinkNotConfigured), (row.LastSendResult, row.LastSendError));
                Assert.Empty(world.Built);
                Assert.Empty(world.Emails.Snapshot());
            }
        }
    }

    [Fact]
    public async Task SendMonthly_WithoutAnEmailProvider_FailsWithItsCode_AndMakesNothing()
    {
        var org = await _world.SeedAsync();

        var outcome = await _world.Delivery(Options.Create(new EmailOptions())).SendMonthlyAsync(org, Today);

        Assert.Equal(AccountantPackageMonthlyOutcome.Failed, outcome);
        Assert.Equal(AccountantPackageSendErrors.EmailNotConfigured, (await _world.RowAsync(org)).LastSendError);
        Assert.Empty(_world.Built);
    }

    [Fact]
    public async Task WhileTheFlagIsOff_NothingIsDue_NothingIsMade_AndNothingIsSent()
    {
        var org = await _world.SeedAsync();
        _world.Flags.Setup(f => f.IsEnabled(FeatureFlags.AccountantPackage)).Returns(false);

        Assert.Empty(await _world.Delivery().DueOrganizationsAsync(Today));
        Assert.Equal(AccountantPackageMonthlyOutcome.NotConfigured, await _world.Delivery().SendMonthlyAsync(org, Today));
        await Assert.ThrowsAsync<DomainRuleException>(() => _world.Delivery().SendNowAsync(org, AccountantPackageAccess.Full));

        Assert.Empty(_world.Built);
        Assert.Empty(_world.Emails.Snapshot());
    }

    // ---------------------------------------------------------------- «send a test»

    [Fact]
    public async Task SendNow_SendsThePreviousMonthWhateverTheDayAndTheSwitch_ForTheFoldersAndTheAccessItIsGiven_AndItIsTheSendOfThatMonth()
    {
        var org = await _world.SeedAsync(enabled: false, day: 28, contents: AccountantPackageFolders.OwnerInvoices | AccountantPackageFolders.Vat);

        var result = await _world.Delivery().SendNowAsync(org, AccountantPackageAccess.WithoutOwnerAccounting);

        Assert.Equal(("2026-10", new DateTime(2026, 12, 3, 5, 30, 0, DateTimeKind.Utc)), (result.Period, result.LinkExpiresAt));
        var (to, _, _) = Assert.Single(_world.Emails.Snapshot());
        Assert.Equal(AccountantPackageDeliveryWorld.Accountant, to);

        // The person does not hold the owner accounting: the package has no more than it may read, and only the folders of the settings.
        var built = Assert.Single(_world.Built);
        Assert.False(built.Access.OwnerAccounting);
        Assert.Equal(AccountantPackageFolders.OwnerInvoices | AccountantPackageFolders.Vat, built.Access.Folders);

        // It is the send of the month: the monthly run, once the send is on and the day has come, does not send it again.
        var row = await _world.Db.AccountantPackageSettings.SingleAsync();
        row.MonthlySendEnabled = true;
        row.SendDayOfMonth = 1;
        await _world.Db.SaveChangesAsync();
        Assert.Equal(AccountantPackageMonthlyOutcome.AlreadySent, await _world.Delivery().SendMonthlyAsync(org, Today));
        Assert.Single(_world.Emails.Snapshot());
    }

    [Fact]
    public async Task SendNow_WithNoAddress_Is422_AndTheFailuresAreTheAnswer_NotAnEntryOfTheSettings()
    {
        var withoutAddress = await _world.SeedAsync(email: null, enabled: false);
        var recipient = await Assert.ThrowsAsync<DomainRuleException>(() => _world.Delivery().SendNowAsync(withoutAddress, AccountantPackageAccess.Full));
        Assert.Equal(AccountantPackageErrors.RecipientMissing, recipient.Code);

        // The package over the caps is the original error, with its numbers; a queue that refuses is «not sent»; neither is written in the settings.
        var org = await _world.SeedAsync();
        _world.Packages
            .Setup(p => p.BuildAsync(It.IsAny<HostScope>(), It.IsAny<AccountantPackageAccess>(), It.IsAny<AccountantPackagePeriod>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException(AccountantPackageErrors.TooLarge, "AccountantPackageTooLarge", 400, 300, 200, 256));
        var tooLarge = await Assert.ThrowsAsync<DomainRuleException>(() => _world.Delivery().SendNowAsync(org, AccountantPackageAccess.Full));
        Assert.Equal((AccountantPackageErrors.TooLarge, 400), (tooLarge.Code, (int)tooLarge.MessageArgs[0]));

        _world.Packages
            .Setup(p => p.BuildAsync(It.IsAny<HostScope>(), It.IsAny<AccountantPackageAccess>(), It.IsAny<AccountantPackagePeriod>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountantPackageResult(1, 1, 1, []));
        _world.Emails.Refuse = true;
        var notSent = await Assert.ThrowsAsync<DomainRuleException>(() => _world.Delivery().SendNowAsync(org, AccountantPackageAccess.Full));
        Assert.Equal(AccountantPackageErrors.NotSent, notSent.Code);

        var row = await _world.RowAsync(org);
        Assert.Null(row.LastSendResult);
        Assert.Empty(_world.Emails.Snapshot());
    }

    // ---------------------------------------------------------------- the package waits for its link and no longer

    [Fact]
    public async Task ANewerPackage_ReplacesTheOlderOne_SoOnlyOnePackageOfAnOrgIsInTheStorage()
    {
        var org = await _world.SeedAsync();
        var row = await _world.Db.AccountantPackageSettings.SingleAsync();
        row.StoredPackagePeriod = "2026-09";
        row.StoredPackageAt = new DateTime(2026, 10, 3, 5, 0, 0, DateTimeKind.Utc);
        _world.Storage.Add(AccountantPackageDeliveryWorld.KeyOf(org, "2026-09"), [1, 2, 3]);
        await _world.Db.SaveChangesAsync();

        Assert.Equal(AccountantPackageMonthlyOutcome.Sent, await _world.Delivery().SendMonthlyAsync(org, Today));

        Assert.False(await _world.Storage.ExistsAsync(StorageBucket.Private, AccountantPackageDeliveryWorld.KeyOf(org, "2026-09")));
        Assert.True(await _world.Storage.ExistsAsync(StorageBucket.Private, AccountantPackageDeliveryWorld.KeyOf(org, "2026-10")));
        Assert.Equal("2026-10", (await _world.RowAsync(org)).StoredPackagePeriod);
    }

    [Fact]
    public async Task DeleteExpiredPackages_DeletesWhatItsLinkHasOutlived_AndForgetsItInTheRow_AndKeepsTheRest()
    {
        var old = await _world.SeedAsync();
        var young = await _world.SeedAsync();
        var none = await _world.SeedAsync();
        foreach (var (org, period, storedAt) in new[] { (old, "2026-09", new DateTime(2026, 10, 3, 5, 30, 0, DateTimeKind.Utc)), (young, "2026-10", new DateTime(2026, 10, 20, 5, 30, 0, DateTimeKind.Utc)) })
        {
            var row = await _world.Db.AccountantPackageSettings.SingleAsync(s => s.OrgId == org);
            row.StoredPackagePeriod = period;
            row.StoredPackageAt = storedAt;
            _world.Storage.Add(AccountantPackageDeliveryWorld.KeyOf(org, period), [9]);
        }

        await _world.Db.SaveChangesAsync();

        // 3 November: the first was stored 31 days ago (its link expired on 2 November), the second 14 days ago.
        Assert.Equal(1, await _world.Delivery().DeleteExpiredPackagesAsync());

        Assert.False(await _world.Storage.ExistsAsync(StorageBucket.Private, AccountantPackageDeliveryWorld.KeyOf(old, "2026-09")));
        Assert.True(await _world.Storage.ExistsAsync(StorageBucket.Private, AccountantPackageDeliveryWorld.KeyOf(young, "2026-10")));
        var forgotten = await _world.RowAsync(old);
        Assert.Null(forgotten.StoredPackagePeriod);
        Assert.Null(forgotten.StoredPackageAt);
        Assert.Equal("2026-10", (await _world.RowAsync(young)).StoredPackagePeriod);
        Assert.Null((await _world.RowAsync(none)).StoredPackagePeriod);

        // Nothing is left to do the next night.
        Assert.Equal(0, await _world.Delivery().DeleteExpiredPackagesAsync());
    }

    [Fact]
    public async Task DeleteExpiredPackages_AnObjectThatCannotBeDeleted_StaysNamedByItsRow_AndDoesNotStopTheOthers()
    {
        var stuck = await _world.SeedAsync();
        var fine = await _world.SeedAsync();
        foreach (var org in new[] { stuck, fine })
        {
            var row = await _world.Db.AccountantPackageSettings.SingleAsync(s => s.OrgId == org);
            row.StoredPackagePeriod = "2026-09";
            row.StoredPackageAt = new DateTime(2026, 10, 1, 5, 30, 0, DateTimeKind.Utc);
            _world.Storage.Add(AccountantPackageDeliveryWorld.KeyOf(org, "2026-09"), [9]);
        }

        await _world.Db.SaveChangesAsync();
        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.DeleteAsync(StorageBucket.Private, AccountantPackageDeliveryWorld.KeyOf(stuck, "2026-09"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("the bucket does not answer"));
        storage
            .Setup(s => s.DeleteAsync(StorageBucket.Private, AccountantPackageDeliveryWorld.KeyOf(fine, "2026-09"), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var failure = await Assert.ThrowsAsync<AggregateException>(() => _world.Delivery(storage: storage.Object).DeleteExpiredPackagesAsync());

        Assert.Single(failure.InnerExceptions);
        Assert.Equal("2026-09", (await _world.RowAsync(stuck)).StoredPackagePeriod);
        Assert.Null((await _world.RowAsync(fine)).StoredPackagePeriod);
    }

    // ---------------------------------------------------------------- the link (the privacy proof)

    /// <summary>
    /// PROOF TEST: privacy rule. The link opens the package of the org and the month it was made for, and nothing else: a link that is changed in any part, made for another org (which has a package of its own) or another
    /// month, expired, made with another secret or another kind of signature, missing a part, or whose package is gone, is the same 404 with the same body: nothing says which part was wrong, and no package is read.
    /// </summary>
    [Fact]
    public async Task AccountantPackageLink_ExpiredOrForeignTokenSame404_Test()
    {
        var org = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var gone = Guid.NewGuid();
        _world.Storage.Add(AccountantPackageDeliveryWorld.KeyOf(org, "2026-10"), AccountantPackageDeliveryWorld.ZipBytes);
        _world.Storage.Add(AccountantPackageDeliveryWorld.KeyOf(org, "2026-09"), Encoding.ASCII.GetBytes("the month before"));
        _world.Storage.Add(AccountantPackageDeliveryWorld.KeyOf(foreign, "2026-10"), Encoding.ASCII.GetBytes("the package of another org"));
        var (_, _, expiresAt, signature) = LinkOf(_world.Links.Create(org, "2026-10").Url);

        // A link that is right for an org whose package is gone (never stored, or deleted by the nightly run).
        var (_, _, goneExpires, goneSignature) = LinkOf(_world.Links.Create(gone, "2026-10").Url);
        var otherSecret = new AccountantPackageLinks(AccountantPackageDeliveryWorld.Configuration(secret: "another-secret-of-the-signed-links"), _world.Clock);
        var (_, _, otherExpires, otherSignature) = LinkOf(otherSecret.Create(org, "2026-10").Url);
        var laterExpiry = DateTime.ParseExact(expiresAt, AccountantPackageLinks.ExpiresAtFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            .AddDays(30)
            .ToString(AccountantPackageLinks.ExpiresAtFormat, CultureInfo.InvariantCulture);

        var cases = new (string Name, string OrgId, string Period, string? ExpiresAt, string? Signature)[]
        {
            ("another org, whose package exists", foreign.ToString("D"), "2026-10", expiresAt, signature),
            ("another month, whose package exists", org.ToString("D"), "2026-09", expiresAt, signature),
            ("a changed expiry", org.ToString("D"), "2026-10", laterExpiry, signature),
            ("a changed signature", org.ToString("D"), "2026-10", expiresAt, (signature[0] == 'a' ? "b" : "a") + signature[1..]),
            ("a short signature", org.ToString("D"), "2026-10", expiresAt, signature[..10]),
            ("another secret", org.ToString("D"), "2026-10", otherExpires, otherSignature),
            ("a signature of the same parts without the purpose", org.ToString("D"), "2026-10", expiresAt, SignatureWithoutPurpose(org, "2026-10", expiresAt)),
            ("no signature", org.ToString("D"), "2026-10", expiresAt, null),
            ("no expiry", org.ToString("D"), "2026-10", null, signature),
            ("nothing", org.ToString("D"), "2026-10", null, null),
            ("an expiry that is not a time", org.ToString("D"), "2026-10", "tomorrow", signature),
            ("an org that is not an id", "not-an-id", "2026-10", expiresAt, signature),
            ("a period that is not a month", org.ToString("D"), "2026", expiresAt, signature),
            ("a package that is gone", gone.ToString("D"), "2026-10", goneExpires, goneSignature),
        };

        var controller = ControllerOf(_world.Delivery());
        var answers = new List<(int Status, string? Code, string? Title, string? Detail)>();
        foreach (var (name, orgId, period, expires, sign) in cases)
        {
            var answer = Answer(await controller.Download(orgId, period, expires, sign, CancellationToken.None));

            Assert.True(answer.Status == StatusCodes.Status404NotFound && answer.Code == AccountantPackageErrors.LinkInvalid, name);
            answers.Add(answer);
        }

        Assert.Single(answers.Distinct());

        // Expired: the same link, 31 days later.
        _world.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(answers[0], Answer(await controller.Download(org.ToString("D"), "2026-10", expiresAt, signature, CancellationToken.None)));

        // The link that is right opens its package, on the day it was made, and nothing else.
        _world.Clock.Advance(TimeSpan.FromDays(-31));
        var opened = await controller.Download(org.ToString("D"), "2026-10", expiresAt, signature, CancellationToken.None);
        var stream = Assert.IsType<FileStreamResult>(opened);
        await using (stream.FileStream)
            Assert.Equal(AccountantPackageDeliveryWorld.ZipBytes, ReadAll(stream.FileStream));
        Assert.Equal(("application/zip", "casazen-commercialista-2026-10.zip"), (stream.ContentType, stream.FileDownloadName));
        Assert.Equal("private, no-store", controller.HttpContext.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void TheLink_IsNamedByTheOrgTheMonthAndTheExpiry_ValidThirtyDays_UnderTheApiAddress()
    {
        var org = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");

        var link = _world.Links.Create(org, "2026-10");

        Assert.StartsWith($"{AccountantPackageDeliveryWorld.ApiBase}/api/public/accountant-package/{org:D}/2026-10?expiresAt=", link.Url);
        Assert.Equal(new DateTime(2026, 12, 3, 5, 30, 0, DateTimeKind.Utc), link.ExpiresAt);
        Assert.Equal(TimeSpan.FromDays(30), AccountantPackageLinkRules.Lifetime);
        Assert.Equal(AccountantPackageLinkRules.Lifetime, AccountantPackageLinkRules.Retention);
        var (_, _, expiresAt, signature) = LinkOf(link.Url);
        Assert.Matches("^[0-9a-f]{64}$", signature);
        Assert.Equal(SignatureWithPurpose(org, "2026-10", expiresAt), signature);

        // Valid to the last second, then not.
        _world.Clock.SetUtcNow(new DateTimeOffset(link.ExpiresAt, TimeSpan.Zero));
        Assert.True(_world.Links.Verify(org, "2026-10", expiresAt, signature));
        _world.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_world.Links.Verify(org, "2026-10", expiresAt, signature));
    }

    [Fact]
    public void TheLink_CannotBeMadeWithoutTheSecretOrAnHttpsAddress_AndNeverOpensWithoutTheSecret()
    {
        using var noSecret = new AccountantPackageDeliveryWorld(secret: null);
        using var noApi = new AccountantPackageDeliveryWorld(apiBase: "http://localhost:5000");

        Assert.False(noSecret.Links.IsConfigured);
        Assert.False(noApi.Links.IsConfigured);
        Assert.Throws<EmailConfigurationException>(() => noSecret.Links.Create(Guid.NewGuid(), "2026-10"));
        Assert.Throws<EmailConfigurationException>(() => noApi.Links.Create(Guid.NewGuid(), "2026-10"));

        var org = Guid.NewGuid();
        var (_, _, expiresAt, signature) = LinkOf(_world.Links.Create(org, "2026-10").Url);
        Assert.False(noSecret.Links.Verify(org, "2026-10", expiresAt, signature));
    }

    [Fact]
    public async Task TheLink_AFileThatCannotBeFound_IsTheSameAnswerAsAWrongLink_AndNothingIsLoggedButTheOrgAndTheMonth()
    {
        var org = Guid.NewGuid();
        var log = new CapturingLogger<AccountantPackageDelivery>();
        var delivery = new AccountantPackageDelivery(
            _world.Db, _world.Packages.Object, _world.Storage, _world.Emails, EmailTestHelpers.ConfiguredEmail(), _world.Links, _world.Flags.Object, log, _world.Clock);
        var (_, _, expiresAt, signature) = LinkOf(_world.Links.Create(org, "2026-10").Url);

        Assert.Null(await delivery.OpenAsync(org, "2026-10", expiresAt, signature));
        Assert.Null(await delivery.OpenAsync(Guid.NewGuid(), "2026-10", expiresAt, signature));

        Assert.DoesNotContain(signature, log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(expiresAt, log.AllOutput, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- nothing personal in a log

    [Fact]
    public async Task ASend_WritesNoAddressAndNoLinkToTheLog()
    {
        var org = await _world.SeedAsync();
        var log = new CapturingLogger<AccountantPackageDelivery>();
        var delivery = new AccountantPackageDelivery(
            _world.Db, _world.Packages.Object, _world.Storage, _world.Emails, EmailTestHelpers.ConfiguredEmail(), _world.Links, _world.Flags.Object, log, _world.Clock);

        await delivery.SendMonthlyAsync(org, Today);

        var (_, content, _) = Assert.Single(_world.Emails.Snapshot());
        var (_, _, _, signature) = LinkOf(content);
        Assert.NotEmpty(log.Entries);
        Assert.DoesNotContain(AccountantPackageDeliveryWorld.Accountant, log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("studio-rossi", log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(signature, log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountantPackageDeliveryWorld.Secret, log.AllOutput, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static PublicAccountantPackageController ControllerOf(IAccountantPackageDeliveryService delivery) =>
        new(delivery)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider() },
            },
        };

    private static (int Status, string? Code, string? Title, string? Detail) Answer(IActionResult result)
    {
        var problem = Assert.IsAssignableFrom<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        return (problem.Status!.Value, (string?)problem.Extensions["code"], problem.Title, problem.Detail);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static (Guid OrgId, string Period, string ExpiresAt, string Signature) LinkOf(EmailContent content)
    {
        var href = Regex.Match(content.HtmlBody, "href=\"([^\"]+)\"").Groups[1].Value;
        return LinkOf(WebUtility.HtmlDecode(href));
    }

    private static (Guid OrgId, string Period, string ExpiresAt, string Signature) LinkOf(string url)
    {
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = HttpUtility.ParseQueryString(uri.Query);
        return (Guid.Parse(segments[^2]), segments[^1], query["expiresAt"]!, query["signature"]!);
    }

    private static string SignatureWithPurpose(Guid orgId, string period, string expiresAt) =>
        Sign($"{AccountantPackageLinkRules.SignaturePurpose}.{orgId:D}.{period}.{expiresAt}");

    /// <summary>What a signature of the same org, month and expiry would be if it did not name the kind of link: it must never open the package.</summary>
    private static string SignatureWithoutPurpose(Guid orgId, string period, string expiresAt) => Sign($"{orgId:D}.{period}.{expiresAt}");

    private static string Sign(string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(AccountantPackageDeliveryWorld.Secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
}

/// <summary>
/// E15/F01/US02/T02: the queries of the monthly send run on the PostgreSQL provider without a server (see <see cref="NpgsqlTranslationProbe"/>). The proofs of the send run on InMemory, which accepts what Npgsql cannot
/// turn into SQL; this fails here, before the CI, when the list of the organizations that are due (the address asked as <c>IS NOT NULL</c>, the month and its result, the org that is active) or the list of the packages
/// to delete has no translation.
/// </summary>
public class AccountantPackageDeliveryNpgsqlTranslationTests
{
    private static AccountantPackageDelivery DeliveryOver(AppDbContext db)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.AccountantPackage)).Returns(true);
        return new AccountantPackageDelivery(
            db,
            Mock.Of<IAccountantPackageService>(),
            new InMemoryFileStorage(),
            new RecordingEmailQueue(),
            EmailTestHelpers.ConfiguredEmail(),
            new AccountantPackageLinks(AccountantPackageDeliveryWorld.Configuration(), TimeProvider.System),
            flags.Object,
            NullLogger<AccountantPackageDelivery>.Instance);
    }

    [Fact]
    public async Task TheListOfTheOrganizationsThatAreDue_IsTranslatedToSqlByNpgsql()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => DeliveryOver(db).DueOrganizationsAsync(AccountantPackageDeliveryWorld.Today));

        var sql = Assert.Single(statements);
        Assert.Contains("\"AccountantPackageSettings\"", sql);
        Assert.Contains("IS NOT NULL", sql);
    }

    [Fact]
    public async Task TheListOfThePackagesToDelete_IsTranslatedToSqlByNpgsql()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => DeliveryOver(db).DeleteExpiredPackagesAsync());

        Assert.Single(statements);
    }

    [Fact]
    public async Task TheSettingsOfAnOrg_AreReadByItsId()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => DeliveryOver(db).SendMonthlyAsync(Guid.NewGuid(), AccountantPackageDeliveryWorld.Today));

        Assert.Contains(statements, sql => sql.Contains("\"AccountantPackageSettings\"", StringComparison.Ordinal) && sql.Contains("\"OrgId\"", StringComparison.Ordinal));
    }
}

/// <summary>
/// E15/F01/US02/T02: the job that sends the package of the month: registered every day at 06:00 in Rome only while the package of the accountant is on, each organization in a scope of its own under its own tenant,
/// an organization that fails does not stop the others, and the run fails (it is not swallowed) once every organization was tried, so Hangfire runs it again.
/// </summary>
public class AccountantPackageMonthlyJobTests
{
    private static readonly DateOnly Today = AccountantPackageDeliveryWorld.Today;

    private sealed class EnteredRecorder(List<Guid> entered) : IBackgroundTenantScope
    {
        public void EnterOrg(Guid orgId) => entered.Add(orgId);
    }

    private static AccountantPackageMonthlyJob JobOver(Mock<IAccountantPackageDeliveryService> delivery, List<Guid> entered, bool flagOn = true)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.AccountantPackage)).Returns(flagOn);
        var services = new ServiceCollection()
            .AddSingleton(delivery.Object)
            .AddScoped<IBackgroundTenantScope>(_ => new EnteredRecorder(entered))
            .BuildServiceProvider();
        return new AccountantPackageMonthlyJob(
            services.GetRequiredService<IServiceScopeFactory>(),
            flags.Object,
            NullLogger<AccountantPackageMonthlyJob>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 5, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Execute_EachDueOrganizationIsSentInAScopeOfItsOwnUnderItsOwnTenant_AndTheExpiredPackagesAreDeleted()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var entered = new List<Guid>();
        var delivery = new Mock<IAccountantPackageDeliveryService>();
        delivery.Setup(d => d.DueOrganizationsAsync(Today, It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        delivery.Setup(d => d.SendMonthlyAsync(It.IsAny<Guid>(), Today, It.IsAny<CancellationToken>())).ReturnsAsync(AccountantPackageMonthlyOutcome.Sent);
        delivery.Setup(d => d.DeleteExpiredPackagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await JobOver(delivery, entered).ExecuteAsync(CancellationToken.None);

        Assert.Equal([first, second], entered);
        delivery.Verify(d => d.SendMonthlyAsync(first, Today, It.IsAny<CancellationToken>()), Times.Once);
        delivery.Verify(d => d.SendMonthlyAsync(second, Today, It.IsAny<CancellationToken>()), Times.Once);
        delivery.Verify(d => d.DeleteExpiredPackagesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_OneOrganizationThatFails_DoesNotStopTheOthers_AndTheRunFailsAtTheEnd()
    {
        var failed = Guid.NewGuid();
        var thrown = Guid.NewGuid();
        var fine = Guid.NewGuid();
        var delivery = new Mock<IAccountantPackageDeliveryService>();
        delivery.Setup(d => d.DueOrganizationsAsync(Today, It.IsAny<CancellationToken>())).ReturnsAsync([failed, thrown, fine]);
        delivery.Setup(d => d.SendMonthlyAsync(failed, Today, It.IsAny<CancellationToken>())).ReturnsAsync(AccountantPackageMonthlyOutcome.Failed);
        delivery.Setup(d => d.SendMonthlyAsync(thrown, Today, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("the database is down"));
        delivery.Setup(d => d.SendMonthlyAsync(fine, Today, It.IsAny<CancellationToken>())).ReturnsAsync(AccountantPackageMonthlyOutcome.Sent);
        delivery.Setup(d => d.DeleteExpiredPackagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var error = await Assert.ThrowsAsync<AggregateException>(() => JobOver(delivery, []).ExecuteAsync(CancellationToken.None));

        Assert.Equal(2, error.InnerExceptions.Count);
        delivery.Verify(d => d.SendMonthlyAsync(fine, Today, It.IsAny<CancellationToken>()), Times.Once);
        delivery.Verify(d => d.DeleteExpiredPackagesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_ADeletionThatFails_IsNotSwallowed_AndTheSendsWereMadeFirst()
    {
        var org = Guid.NewGuid();
        var delivery = new Mock<IAccountantPackageDeliveryService>();
        delivery.Setup(d => d.DueOrganizationsAsync(Today, It.IsAny<CancellationToken>())).ReturnsAsync([org]);
        delivery.Setup(d => d.SendMonthlyAsync(org, Today, It.IsAny<CancellationToken>())).ReturnsAsync(AccountantPackageMonthlyOutcome.Sent);
        delivery.Setup(d => d.DeleteExpiredPackagesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new AggregateException(new IOException("no bucket")));

        await Assert.ThrowsAsync<AggregateException>(() => JobOver(delivery, []).ExecuteAsync(CancellationToken.None));

        delivery.Verify(d => d.SendMonthlyAsync(org, Today, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_WithTheFlagOff_DoesNothing()
    {
        var delivery = new Mock<IAccountantPackageDeliveryService>(MockBehavior.Strict);

        await JobOver(delivery, [], flagOn: false).ExecuteAsync(CancellationToken.None);

        delivery.VerifyNoOtherCalls();
    }

    [Fact]
    public void Configure_TheFlagOn_SchedulesTheJobEveryDayAt0600InRome_AndItNeverRunsTwiceAtOnce()
    {
        var (manager, scheduled) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false, accountantPackage: true));

        Assert.Equal("accountant-package-monthly", AccountantPackageMonthlyJob.RecurringJobId);
        var job = scheduled[AccountantPackageMonthlyJob.RecurringJobId];
        Assert.Equal(("0 6 * * *", RomeCalendar.TimeZone), (job.Cron, job.Options.TimeZone));
        Assert.Equal((typeof(AccountantPackageMonthlyJob), nameof(AccountantPackageMonthlyJob.ExecuteAsync)), (job.Job.Type, job.Job.Method.Name));
        manager.Verify(m => m.RemoveIfExists(AccountantPackageMonthlyJob.RecurringJobId), Times.Never);
        var concurrency = job.Job.Method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(concurrency);
        Assert.InRange(concurrency.TimeoutSec, 1, JobLockTimeouts.DefaultSeconds);
    }

    [Fact]
    public void Configure_TheFlagOff_DoesNotScheduleTheJob_AndRemovesTheScheduleOfAnEarlierDeploy()
    {
        var (manager, scheduled) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false));

        Assert.DoesNotContain(AccountantPackageMonthlyJob.RecurringJobId, scheduled.Keys);
        manager.Verify(m => m.RemoveIfExists(AccountantPackageMonthlyJob.RecurringJobId), Times.Once);
    }

    private static (Mock<IRecurringJobManager> Manager, Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)> Scheduled) Manager()
    {
        var scheduled = new Dictionary<string, (Job, string, RecurringJobOptions)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => scheduled[id] = (job, cron, options));
        return (manager, scheduled);
    }
}

/// <summary>
/// E15/F01/US02/T02: the e-mail to the accountant is Italian and English, every dynamic value is HTML-encoded, it carries the link and its last day and says there is no attachment, and it holds no figure and no
/// name of a guest or of an owner (it has no such value to hold).
/// </summary>
public class AccountantPackageEmailTests
{
    private const string Url = "https://api.casazen.test/api/public/accountant-package/7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11/2026-10?expiresAt=2026-12-03T05%3A30%3A00Z&signature=abc";
    private const string Payload = "<a href=\"https://phish.example\">Casa</a>";

    [Theory]
    [InlineData("it-IT", "Pacchetto per il commercialista: ottobre 2026 di Casa Rossi", "Scarica il pacchetto", "non è allegato", "03/12/2026 06:30")]
    [InlineData("en", "Accountant package: October 2026 of Casa Rossi", "Download the package", "is not attached", "3 December 2026, 06:30")]
    public void TheEmail_IsCompleteInEachLanguage(string culture, string subject, string button, string noAttachment, string expiry)
    {
        var content = EmailTemplates.AccountantPackage(CultureInfo.GetCultureInfo(culture), "Casa Rossi", "2026-10", Url, new DateTime(2026, 12, 3, 5, 30, 0, DateTimeKind.Utc));

        Assert.Equal(subject, content.Subject);
        Assert.Contains(button, content.HtmlBody);
        Assert.Contains(noAttachment, content.HtmlBody);
        Assert.Contains(expiry, content.HtmlBody);
        Assert.Contains("href=\"https://api.casazen.test/api/public/accountant-package/7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11/2026-10?expiresAt=2026-12-03T05%3A30%3A00Z&amp;signature=abc\"", content.HtmlBody);
        Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
        Assert.Contains($"<html lang=\"{culture[..2]}\">", content.HtmlBody);
        Assert.Null(content.ReplyTo);
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void TheEmail_EveryDynamicValueIsHtmlEncoded(string culture)
    {
        var content = EmailTemplates.AccountantPackage(CultureInfo.GetCultureInfo(culture), Payload, "2026-10", Url, new DateTime(2026, 12, 3, 5, 30, 0, DateTimeKind.Utc));

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.Contains("&lt;a href=&quot;https://phish.example&quot;&gt;Casa&lt;/a&gt;", content.HtmlBody);
    }

    [Fact]
    public void TheTextsOfTheEmail_ExistInBothLanguages()
    {
        foreach (var key in new[] { "Subject", "Greeting", "Body", "Cta", "LinkFallback", "Validity", "NoAttachment", "NotYou" })
        {
            Assert.NotEqual(
                EmailTexts.Get($"AccountantPackage_{key}", CultureInfo.GetCultureInfo("it-IT")),
                EmailTexts.Get($"AccountantPackage_{key}", CultureInfo.GetCultureInfo("en")));
        }
    }
}

/// <summary>
/// E15/F01/US02/T02: what the registration of the application says about the monthly package: the delivery service and the job, once and scoped, the signed link once and as a singleton (it holds no state of a
/// request), the two limits of the endpoints (the public link per client IP, the test per person), and the key of the storage.
/// </summary>
public class AccountantPackageDeliveryRegistrationTests
{
    [Fact]
    public void AddCasazenServices_RegistersTheSettingsTheDeliveryAndTheJob_OnceAndScoped()
    {
        var services = new ServiceCollection();

        Casazen.Web.Extensions.ServiceCollectionExtensions.AddCasazenServices(services);

        foreach (var (service, implementation) in new[]
                 {
                     (typeof(IAccountantPackageSettingsService), typeof(AccountantPackageSettingsService)),
                     (typeof(IAccountantPackageDeliveryService), typeof(AccountantPackageDelivery)),
                     (typeof(AccountantPackageMonthlyJob), typeof(AccountantPackageMonthlyJob)),
                 })
        {
            var registered = Assert.Single(services, d => d.ServiceType == service);
            Assert.Equal(implementation, registered.ImplementationType);
            Assert.Equal(ServiceLifetime.Scoped, registered.Lifetime);
        }
    }

    [Fact]
    public void AddCasazenEmail_RegistersTheSignedLinkOfThePackage_OnceAsASingleton()
    {
        var services = new ServiceCollection();

        services.AddCasazenEmail(new ConfigurationBuilder().Build(), Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Development"));

        var registered = Assert.Single(services, d => d.ServiceType == typeof(AccountantPackageLinks));
        Assert.Equal(ServiceLifetime.Singleton, registered.Lifetime);
    }

    [Fact]
    public void ThePolicies_TheLinkIsPerClientIp_TheTestIsPerPerson_AndTheEndpointsUseThem()
    {
        var link = Assert.Single(RateLimitingServiceCollectionExtensions.Policies, p => p.Name == RateLimitPolicies.PublicAccountantPackage);
        var send = Assert.Single(RateLimitingServiceCollectionExtensions.Policies, p => p.Name == RateLimitPolicies.AccountantPackageSend);

        Assert.Equal(("PublicAccountantPackage", 20, TimeSpan.FromMinutes(10), false), (link.Name, link.DefaultPermitLimit, link.DefaultWindow, link.PartitionByUser));
        Assert.Equal(("AccountantPackageSend", 5, TimeSpan.FromMinutes(10), true), (send.Name, send.DefaultPermitLimit, send.DefaultWindow, send.PartitionByUser));
        Assert.Equal(RateLimitPolicies.PublicAccountantPackage, typeof(PublicAccountantPackageController).GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal(RateLimitPolicies.AccountantPackageSend, typeof(AccountantPackageController).GetMethod(nameof(AccountantPackageController.Send))!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
    }

    [Fact]
    public void ThePublicController_IsAnonymous_BehindTheFlag_NeverCached_AndOnlyReads()
    {
        var controller = typeof(PublicAccountantPackageController);

        Assert.NotNull(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(FeatureFlags.AccountantPackage, Assert.Single(controller.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.NotNull(controller.GetCustomAttribute<PrivateAnswerAttribute>());
        Assert.Equal("api/public/accountant-package", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        var download = Assert.Single(controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Equal(["GET"], download.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods));
        Assert.Equal("{orgId}/{period}", download.GetCustomAttribute<HttpGetAttribute>()!.Template);
    }

    [Fact]
    public void TheStorageKey_IsMadeOfTheOrgAndTheMonth_AndNothingElse()
    {
        var org = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");

        Assert.Equal("accountant-packages/7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11/2026-10.zip", StorageKeys.AccountantPackage(org, "2026-10"));
        Assert.Throws<ArgumentException>(() => StorageKeys.AccountantPackage(org, "../2026-10"));
        Assert.Throws<ArgumentException>(() => StorageKeys.AccountantPackage(org, "2026 10"));
    }
}
