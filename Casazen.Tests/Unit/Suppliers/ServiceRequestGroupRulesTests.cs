using System.Collections;
using System.Globalization;
using System.Resources;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Web.Resources;
using Xunit;
using static Casazen.Core.Suppliers.ServiceRequestGroupRules;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-BKP: the rules of the backup suppliers that decide who may take the job and when. Pure: the clock and the rows are arguments. The
/// service and the job that apply them are tested in <c>ServiceRequestBackupGroupTests</c>.
/// </summary>
public class ServiceRequestGroupRulesTests
{
    private static readonly DateTime Start = new(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PrimaryDue = Start.AddMinutes(60);

    private static Member Primary(
        ServiceRequestStatus status = ServiceRequestStatus.Richiesto,
        bool proposal = false,
        ServiceRequestActorParty? cancelledBy = null) =>
        new(Guid.NewGuid(), ServiceRequestGroupRole.Primary, status, null, proposal, cancelledBy);

    private static Member Backup(DateTime? acceptedAt = null, ServiceRequestStatus status = ServiceRequestStatus.Richiesto, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), ServiceRequestGroupRole.Backup, status, acceptedAt, false, null);

    [Fact]
    public void CanTake_ABackupDuringThePrimaryWindow_MustWait()
    {
        var primary = Primary();
        var backup = Backup(Start.AddMinutes(2));

        var verdict = CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, Start.AddMinutes(30), backup, primary, [backup]);

        Assert.Equal(TakeVerdict.BackupMustWaitForPrimary, verdict);
    }

    [Fact]
    public void CanTake_ThePrimaryDuringItsWindow_MayTake_AndAfterItOnlyIfNoBackupHoldsAPreApproval()
    {
        var primary = Primary();
        var accepted = Backup(Start.AddMinutes(2));
        var silent = Backup();

        Assert.Equal(
            TakeVerdict.Allowed,
            CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, Start.AddMinutes(59), primary, primary, [accepted]));
        Assert.Equal(
            TakeVerdict.PrimaryWindowPassed,
            CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, PrimaryDue, primary, primary, [accepted]));
        // Nobody accepted: the primary is still in the race after its hour, without any priority.
        Assert.Equal(
            TakeVerdict.Allowed,
            CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, PrimaryDue.AddMinutes(5), primary, primary, [silent]));
        Assert.Equal(
            TakeVerdict.Allowed,
            CanTake(ServiceRequestGroupPhase.BackupWindow, PrimaryDue, PrimaryDue.AddMinutes(30), primary, primary, [accepted]));
    }

    [Fact]
    public void CanTake_AfterThePrimaryWindow_TheEarliestPreApprovalHasTheJob_TheOthersMustWaitTheirTurn()
    {
        var primary = Primary();
        var later = Backup(Start.AddMinutes(10));
        var earlier = Backup(Start.AddMinutes(3));
        var notAccepted = Backup();
        var backups = new[] { later, earlier, notAccepted };
        var after = PrimaryDue.AddMinutes(1);

        Assert.Equal(TakeVerdict.Allowed, CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, after, earlier, primary, backups));
        Assert.Equal(TakeVerdict.NotYourTurn, CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, after, later, primary, backups));
        Assert.Equal(TakeVerdict.NotYourTurn, CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, after, notAccepted, primary, backups));
    }

    [Fact]
    public void CanTake_APrimaryThatRefused_OpensTheTurnToTheBackupsAtOnce()
    {
        var primary = Primary(ServiceRequestStatus.Rifiutato);
        var accepted = Backup(Start.AddMinutes(4));
        var other = Backup();

        Assert.Equal(
            TakeVerdict.Allowed,
            CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, Start.AddMinutes(10), accepted, primary, [accepted, other]));
        Assert.Equal(
            TakeVerdict.NotYourTurn,
            CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, Start.AddMinutes(10), other, primary, [accepted, other]));
    }

    [Fact]
    public void CanTake_ThePrimaryProposedAnotherTime_TheBackupsKeepWaitingForTheHost()
    {
        var primary = Primary(proposal: true);
        var backup = Backup(Start.AddMinutes(2));

        var verdict = CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, PrimaryDue.AddHours(3), backup, primary, [backup]);

        Assert.Equal(TakeVerdict.BackupMustWaitForPrimary, verdict);
    }

    [Fact]
    public void CanTake_TheSecondWindow_TheFirstToTakeHasIt_AndAClosedGroupHasNothingToTake()
    {
        var primary = Primary();
        var one = Backup();
        var two = Backup();

        Assert.Equal(
            TakeVerdict.Allowed,
            CanTake(ServiceRequestGroupPhase.BackupWindow, PrimaryDue, PrimaryDue.AddMinutes(10), two, primary, [one, two]));
        Assert.Equal(
            TakeVerdict.GroupClosed,
            CanTake(ServiceRequestGroupPhase.Closed, PrimaryDue, PrimaryDue.AddMinutes(10), two, primary, [one, two]));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico, null)]
    [InlineData(ServiceRequestStatus.InCorso, null)]
    [InlineData(ServiceRequestStatus.Annullato, ServiceRequestActorParty.Host)]
    public void CanTake_APrimaryThatTookTheJobOrWasCancelledByTheHost_LeavesNothingToTake(
        ServiceRequestStatus status,
        ServiceRequestActorParty? cancelledBy)
    {
        var primary = Primary(status, cancelledBy: cancelledBy);
        var backup = Backup(Start.AddMinutes(2));

        // Even before the group's own row says so: a take in that instant must not give the job twice.
        var verdict = CanTake(ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, PrimaryDue.AddMinutes(5), backup, primary, [backup]);

        Assert.Equal(TakeVerdict.GroupClosed, verdict);
    }

    [Fact]
    public void PreApprovedInOrder_IsATotalOrder_TheInstantThenTheId_AndLeavesOutTheOnesThatAreNotWaiting()
    {
        var low = new Guid("00000000-0000-0000-0000-000000000001");
        var high = new Guid("00000000-0000-0000-0000-000000000002");
        var sameInstant = Start.AddMinutes(5);
        var members = new[]
        {
            Backup(sameInstant, id: high),
            Backup(sameInstant, id: low),
            Backup(Start.AddMinutes(1), ServiceRequestStatus.Rifiutato),
            Backup(),
            Primary(),
        };

        var ordered = PreApprovedInOrder(members);

        Assert.Equal([low, high], ordered.Select(m => m.RequestId));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Rifiutato, null, 5, true)]
    [InlineData(ServiceRequestStatus.Annullato, ServiceRequestActorParty.Supplier, 5, true)]
    [InlineData(ServiceRequestStatus.Annullato, ServiceRequestActorParty.Host, 5, false)]
    [InlineData(ServiceRequestStatus.Richiesto, null, 59, false)]
    [InlineData(ServiceRequestStatus.Richiesto, null, 60, true)]
    [InlineData(ServiceRequestStatus.PresoInCarico, null, 90, false)]
    public void PrimaryIsOut_RefusedCancelledBySupplierOrSilentForTheHour(
        ServiceRequestStatus status,
        ServiceRequestActorParty? cancelledBy,
        int minutes,
        bool expected)
    {
        var primary = Primary(status, cancelledBy: cancelledBy);

        Assert.Equal(expected, PrimaryIsOut(primary, ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, Start.AddMinutes(minutes)));
    }

    [Fact]
    public void PrimaryIsOut_APrimaryWithATimeProposedIsTheHostsToDecide_NotOut()
    {
        Assert.False(PrimaryIsOut(Primary(proposal: true), ServiceRequestGroupPhase.PrimaryWindow, PrimaryDue, PrimaryDue.AddHours(2)));
    }

    [Theory]
    [InlineData(ServiceRequestGroupRole.None, true)]
    [InlineData(ServiceRequestGroupRole.Primary, true)]
    [InlineData(ServiceRequestGroupRole.PromotedBackup, true)]
    [InlineData(ServiceRequestGroupRole.Backup, false)]
    [InlineData(ServiceRequestGroupRole.ReplacedPrimary, false)]
    public void IsVisibleToHost_OnlyTheRequestTheHostFollowsIsListed(ServiceRequestGroupRole role, bool expected)
    {
        Assert.Equal(expected, IsVisibleToHost(role));
        // The compiled form and the expression the queries use say the same thing.
        var request = new Casazen.Core.Entities.ServiceRequest { GroupRole = role };
        Assert.Equal(expected, VisibleToHost.Compile()(request));
    }

    [Theory]
    [InlineData(ServiceRequestGroupRole.Backup, ServiceRequestGroupPhase.PrimaryWindow, false, SupplierBackupStates.Waiting)]
    [InlineData(ServiceRequestGroupRole.Backup, ServiceRequestGroupPhase.PrimaryWindow, true, SupplierBackupStates.Accepted)]
    [InlineData(ServiceRequestGroupRole.Backup, ServiceRequestGroupPhase.BackupWindow, false, SupplierBackupStates.Active)]
    [InlineData(ServiceRequestGroupRole.Backup, ServiceRequestGroupPhase.BackupWindow, true, SupplierBackupStates.Accepted)]
    [InlineData(ServiceRequestGroupRole.Backup, ServiceRequestGroupPhase.Closed, false, SupplierBackupStates.Closed)]
    [InlineData(ServiceRequestGroupRole.Primary, ServiceRequestGroupPhase.PrimaryWindow, false, SupplierBackupStates.Priority)]
    [InlineData(ServiceRequestGroupRole.Primary, ServiceRequestGroupPhase.BackupWindow, false, SupplierBackupStates.Late)]
    public void SupplierBackupStates_Of_SaysWhatTheSupplierCanDoNow(
        ServiceRequestGroupRole role,
        ServiceRequestGroupPhase phase,
        bool accepted,
        string expected)
    {
        var state = SupplierBackupStates.Of(
            role, ServiceRequestStatus.Richiesto, phase, PrimaryDue, Start.AddMinutes(10), accepted ? Start.AddMinutes(1) : null);

        Assert.Equal(expected, state);
    }

    [Fact]
    public void SupplierBackupStates_Of_ThePromotedBackupWonAndAnythingNotWaitingIsClosed()
    {
        Assert.Equal(
            SupplierBackupStates.Won,
            SupplierBackupStates.Of(
                ServiceRequestGroupRole.PromotedBackup, ServiceRequestStatus.PresoInCarico, ServiceRequestGroupPhase.Closed, PrimaryDue, Start, null));
        Assert.Equal(
            SupplierBackupStates.Closed,
            SupplierBackupStates.Of(
                ServiceRequestGroupRole.Backup, ServiceRequestStatus.Annullato, ServiceRequestGroupPhase.Closed, PrimaryDue, Start, null));
    }

    [Fact]
    public void MessageKeys_EveryRefusalOfTheBackupRules_ExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        Assert.All(ServiceRequestGroupErrorCodes.MessageKeys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.False(string.IsNullOrWhiteSpace(italian[key]), key);
            Assert.NotEqual(italian[key], english[key]);
        });
        Assert.Equal(
            ServiceRequestGroupErrorCodes.MessageKeys.Count,
            ServiceRequestGroupErrorCodes.MessageKeys.Distinct().Count());
    }

    [Fact]
    public void CancellationReasons_TheCodesOfTheGroupAreCodes_NotTextAPersonWrote()
    {
        Assert.All(
            new[]
            {
                ServiceRequestCancellationReasons.PrimaryTookIt,
                ServiceRequestCancellationReasons.TakenByBackup,
                ServiceRequestCancellationReasons.RequestCancelledByHost,
                ServiceRequestCancellationReasons.BackupSlotUnavailable,
            },
            reason => Assert.True(ServiceRequestCancellationReasons.IsCode(reason), reason));
        Assert.False(ServiceRequestCancellationReasons.IsCode("Il cliente ha cambiato idea"));
    }

    private static Dictionary<string, string> ReadEntries(CultureInfo culture)
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? string.Empty, StringComparer.Ordinal);
    }
}
