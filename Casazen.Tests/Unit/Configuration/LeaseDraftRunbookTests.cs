using System.Reflection;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Web.BackgroundJobs;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// LR-12: the runbook of the guided lease draft must say what the code does: every error code of the API, every code of what a
/// step lacks and of the checks of the review (the interface turns each one into a sentence from this page), the job, the option,
/// the endpoints and the figures. The pages that list jobs, variables and encrypted columns carry the new ones.
/// </summary>
public class LeaseDraftRunbookTests
{
    private static string Runbooks => Path.Combine(FindRepositoryRoot(), "docs", "runbooks");

    private static string Runbook => File.ReadAllText(Path.Combine(Runbooks, "long-rent-lease-draft.md"));

    private static TheoryData<string> CodesOf(Type holder)
    {
        var codes = new TheoryData<string>();
        foreach (var field in holder.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
            codes.Add((string)field.GetRawConstantValue()!);

        return codes;
    }

    public static TheoryData<string> ErrorCodes() => CodesOf(typeof(LeaseDraftErrorCodes));

    public static TheoryData<string> NeedCodes() => CodesOf(typeof(LeaseNeedCodes));

    [Theory]
    [MemberData(nameof(ErrorCodes))]
    public void Runbook_EveryErrorCodeOfTheDraft_IsDocumented(string code) => Assert.Contains($"`{code}`", Runbook);

    [Theory]
    [MemberData(nameof(NeedCodes))]
    public void Runbook_EveryCodeOfWhatAStepLacksAndOfTheReview_IsDocumented(string code) => Assert.Contains($"`{code}`", Runbook);

    [Fact]
    public void Runbook_NamesTheOtherCodesOfTheEndpoints()
    {
        var runbook = Runbook;

        foreach (var code in new[]
                 {
                     LeasePartyErrorCodes.TooMany, "validation_error", "lease_not_found", "property_not_found", "contract_template_not_approved",
                     "contract_data_missing", "property_mode_blocked_by_draft_lease",
                 })
        {
            Assert.Contains($"`{code}`", runbook);
        }
    }

    [Fact]
    public void Runbook_NamesTheJobTheOptionTheEndpointsAndTheMigration()
    {
        var runbook = Runbook;

        Assert.Contains($"`{LeaseDraftRetentionJob.RecurringJobId}`", runbook);
        Assert.Contains($"`{LongRentLeaseOptions.SectionName}:DraftRetentionDays`", runbook);
        Assert.Contains($"`{LongRentLeaseOptions.SectionName}__DraftRetentionDays`", runbook);
        Assert.Contains("`POST /api/leases/drafts`", runbook);
        Assert.Contains("`GET /api/leases/{id}/draft`", runbook);
        Assert.Contains("`PATCH /api/leases/{id}/draft`", runbook);
        Assert.Contains("`GET /api/leases/{id}/review`", runbook);
        Assert.Contains("`POST /api/leases/{id}/review/confirm`", runbook);
        Assert.Contains("`DELETE /api/leases/{id}/review/confirm`", runbook);
        Assert.Contains("`POST /api/leases/{id}/sent`", runbook);
        Assert.Contains("`DELETE /api/leases/{id}/sent`", runbook);
        Assert.Contains("`DELETE /api/leases/{id}`", runbook);
        Assert.Contains("`GET /api/long-rent/landlord-defaults`", runbook);
        Assert.Contains("`GET /api/long-rent/rules`", runbook);
        Assert.Contains("`AddGuidedLeaseDraft`", runbook);
        Assert.Contains("BE-PM02-1", runbook);
        Assert.Contains("SELECT … FOR UPDATE", runbook);
        Assert.Contains("`ReviewConfirmed` 16", runbook);
    }

    [Fact]
    public void Runbook_TheFiguresAreTheOnesOfTheCode()
    {
        var runbook = Runbook;

        Assert.Equal(180, new LongRentLeaseOptions().DraftRetentionDays);
        Assert.Contains("default **180**", runbook);
        Assert.Contains("`termMonths { Libero: 48, Concordato: 36 }`", runbook);
        Assert.Contains("`renewalMonths { Libero: 48, Concordato: 24 }`", runbook);
        Assert.Equal(48, LongRentRules.Current.TermMonths["Libero"]);
        Assert.Equal(36, LongRentRules.Current.TermMonths["Concordato"]);
        Assert.Equal(48, LongRentRules.Current.RenewalMonths["Libero"]);
        Assert.Equal(24, LongRentRules.Current.RenewalMonths["Concordato"]);
        Assert.Contains($"`depositMaxRents: {LongRentRules.DepositMaxRents}`", runbook);
        Assert.Contains($"`noticeMonths: {LongRentRules.Current.NoticeMonths}`", runbook);
        Assert.Contains($"`registrationDays: {LongRentRules.Current.RegistrationDays}`", runbook);
        Assert.Equal("50 3 * * *", LeaseDraftRetentionJob.Cron);
        Assert.Contains("Every day at 03:50 UTC", runbook);
        Assert.Equal("SET LOCAL lock_timeout = '5s'", Casazen.Infrastructure.Services.LeaseDrafts.LeaseDraftLocking.SetLockTimeoutSql);
        Assert.Contains("limited to 5 seconds", runbook);

        // The numbers of the four new events are in the runbook (the rollback deletes them by number): the enum is append-only, so
        // they only move when another event is added before them, and then this page is to be updated.
        Assert.Equal(
            [16, 17, 18, 19],
            new[] { LeaseEventType.ReviewConfirmed, LeaseEventType.ReviewReopened, LeaseEventType.SentForSignature, LeaseEventType.SentRevoked }
                .Select(e => (int)e));
        Assert.Contains("IN (16, 17, 18, 19)", runbook);
    }

    [Fact]
    public void OtherRunbooks_ListTheJobTheVariableAndTheEncryptedColumns()
    {
        Assert.Contains(LeaseDraftRetentionJob.RecurringJobId, File.ReadAllText(Path.Combine(Runbooks, "hangfire.md")));

        var checklist = File.ReadAllText(Path.Combine(Runbooks, "deploy-checklist.md"));
        Assert.Contains($"`{LongRentLeaseOptions.SectionName}__DraftRetentionDays`", checklist);

        var encryption = File.ReadAllText(Path.Combine(Runbooks, "encryption.md"));
        Assert.Contains("`LeaseContracts.DraftData`", encryption);
        Assert.Contains("`Parties.Residence`", encryption);

        Assert.Contains("lease-draft-retention", File.ReadAllText(Path.Combine(Runbooks, "gdpr.md")));
        Assert.Contains("(long-rent-lease-draft.md)", File.ReadAllText(Path.Combine(Runbooks, "index.md")));
        Assert.Contains("/api/leases/drafts", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "TECHNICAL.md")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
