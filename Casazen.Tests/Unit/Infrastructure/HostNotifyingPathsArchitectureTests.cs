using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// NF-65 (E05/F03/US01/T02), modeled on <see cref="SupplierNotifyingPathsArchitectureTests"/>: what a host user hears about is decided in <b>one
/// place</b>, <c>INotificationPreferenceService</c>, so a choice of the user (the channels, the quiet hours) or a scope (a collaborator «Solo alcuni») cannot
/// be forgotten by a path written later. Every place of the product that tells a host user about one of the eight kinds of event
/// (<c>NotificationEventType</c>) — it sends a push to the hosts of a property or booking or to one user, picks the people who may hear about a property
/// (<c>HostNotificationAudience</c>), queues one of the emails addressed to a host about those events or writes the counter of the bell or a popup
/// alert (<c>InAppNotification</c>, <c>PopupAlert</c>) — has to ask the service, <b>or be listed
/// below as a legacy path, with its reason and the epic that moves it</b>. A file that does neither fails the test: a new path starts inside the rule.
/// </summary>
public class HostNotifyingPathsArchitectureTests
{
    private const string TheService = "INotificationPreferenceService";

    private static readonly string[] Notices =
    [
        "PushAudience.BookingHosts(",
        "PushAudience.PropertyHosts(",
        "PushAudience.User(",
        "PushAudience.OrgAdmins(",

        "HostNotificationAudience.UsersToTell(",
        "HostNotificationAudience.AdministratorsToTell(",

        "EmailTemplates.Names.GuestMessageToHost",
        "EmailTemplates.Names.HostMessageReceived",

        "EmailTemplates.Names.HostBookingConfirmed",
        "EmailTemplates.Names.HostBookingCancelledByGuest",
        "EmailTemplates.Names.HostDeferredChargeCancelled",

        "EmailTemplates.Names.AlloggiatiDeadline",
        "EmailTemplates.Names.GuestCheckInIncomplete",

        "EmailTemplates.Names.HostApprovalToDecide",

        "EmailTemplates.Names.HostScheduledMessageIssue",

        "InAppNotifications.Add",
        "PopupAlerts.Add",
    ];

    private static readonly Regex[] NoticePatterns =
    [
        new(@"new\s+InAppNotification\b", RegexOptions.CultureInvariant),
        new(@"new\s+PopupAlert\b", RegexOptions.CultureInvariant),
    ];

    private static List<string> NoticesIn(string code) =>
        [
            .. Notices.Where(notice => code.Contains(notice, StringComparison.Ordinal)),
            .. NoticePatterns.Where(pattern => pattern.IsMatch(code)).Select(pattern => pattern.ToString()),
        ];

    private static readonly IReadOnlyDictionary<string, (string Reason, string Epic)> Legacy = new Dictionary<string, (string, string)>
    {
        ["Casazen.Infrastructure/Services/BookingNotifier.cs"] =
            ("the new booking, the cancellation by the guest and the failed deferred charge: the push to the hosts of the booking and the emails to the org's address", "E08 (NF-79, booking events)"),
        ["Casazen.Infrastructure/Services/NotificationService.cs"] =
            ("the stay alerts: the Alloggiati deadline and the guest data still missing (push and email), the stay of a channel to verify, the identity check that waits for a decision", "E03 (legal deadlines)"),
        ["Casazen.Infrastructure/Push/PushDeliveryJob.cs"] =
            ("the devices of the audiences «hosts of a booking» and «hosts of a property», read when the job runs; the pushes of the booking events and of the stay alerts that use those audiences", "E08 (NF-79, booking events) and E03 (legal deadlines)"),
        ["Casazen.Infrastructure/Push/InAppNotificationJob.cs"] =
            ("the notifications of the bell that are written next to those pushes, for the same audiences", "E08 (NF-79, booking events) and E03 (legal deadlines)"),
        ["Casazen.Infrastructure/Services/GuestCheckInService.cs"] =
            ("the push «the guest completed the online check-in»: not one of the eight kinds of event of NF-65, it keeps the hosts of the booking", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/FiscalDocumentLiveRefresh.cs"] =
            ("the pushes «a fiscal document was issued» (E12/F01/US01/T02) and «the SDI rejected an invoice» (E17/F01/US02/T03): they make the pages open on the documents and on the booking read them again, and are not one of the eight kinds of event of NF-65, they keep the hosts of the booking", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/CheckInLinkDeliveryService.cs"] =
            ("the push «the check-in link could not be taken to the guest» (E23/F01/US01/T01, NF-122): the bell, the pop-up and the live update of the open pages of the hosts of the booking, once for each link that failed for good; not one of the eight kinds of event of NF-65, it keeps the hosts of the booking", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/OnboardingAssistOutcomeNotice.cs"] =
            ("«Fatto» on a «Fallo per me» request (E20/F03/US01/T02): the push, the entry of the bell and the live update «il collegamento è attivo» or «il supporto dichiara di aver collegato» to the administrators of the org, who alone can ask for help; not one of the eight kinds of event of NF-65", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/TouristTaxLiveRefresh.cs"] =
            ("the push «the guest paid the tourist tax online» (E25/F01/US01/T01): it makes the pages open on the booking read it again, and is not one of the eight kinds of event of NF-65, it keeps the hosts of the booking", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/OtaReviewLiveRefresh.cs"] =
            ("the push «a guest left a review on a channel» and «a reply or the host's own review did not reach the channel» (E05/F06/US01/T02, NF-69): the bell, the pop-up and the live update of the hosts of the booking, once for each review and for each attempt; not one of the eight kinds of event of NF-65, it keeps the hosts of the booking", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/BookingFlowLiveRefresh.cs"] =
            ("the push «CasaZen did a step of the stay in the host's place» (casazen/backend#978: the arrival or the departure registered, the property declared ready): the bell, the pop-up and the live update of the open pages of the hosts of the booking; not one of the eight kinds of event of NF-65, it keeps the hosts of the booking", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/TicinoDeclarationLiveRefresh.cs"] =
            ("the push «a person declared an arrival day of a Ticino property as uploaded to the police portal, or undid the declaration» (E30/F02/US02/T01, NF-128): the bell, the pop-up and the live update of the open pages of the hosts of the property, so the cockpit of the other hosts stops (or starts again) listing the day; and, in the same file, the same push for the declaration of an arrival day of a property of another canton as notified to the cantonal authority (E31/F02/US01/T01, NF-131), which makes the section swissNotifications of the cockpit of the other hosts read again; not one of the eight kinds of event of NF-65, it keeps the hosts of the property like the Ticino alerts of E30/F02/US01/T01", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/ServiceRequestNotifier.cs"] =
            ("the push to the hosts about a request to a supplier (taken, started, completed, a time proposed, a quote): outside the eight kinds of event of NF-65", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/ServiceRequestNotifier.Backups.cs"] =
            ("the push to the hosts about the backup suppliers of a request: outside the eight kinds of event of NF-65", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/AlertRouter.cs"] =
            ("the popup alerts (ADR-025): the router decides who is alerted and when, and borrows only the quiet hours of the preferences; it does not read the channel settings yet", "E01 (the alert router reads the preferences, assumption of NF-65)"),
        ["Casazen.Infrastructure/Services/OwnerLoginHostSync.cs"] =
            ("the notices of the agency's own domain (E40/F01/US02/T01): «domain verified», «owner sign-in ready», «owner sign-in failed», the bell, the pop-up and the live refresh of the domain settings of the administrators of the org, who alone open them; not one of the eight kinds of event of NF-65", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/DemoEnvironmentService.cs"] =
            ("the sample popup alert it writes for the showcase org: test data of the demo, not an event told to a host", "none planned (demo data, not a notice)"),
    };

    [Fact]
    public void EveryPathThatTellsAHost_AsksTheService_OrIsListedAsLegacy()
    {
        var offenders = new List<string>();
        var uses = 0;

        foreach (var (relative, text) in ReadSources(FindRepositoryRoot()))
        {
            var code = CodeWithoutComments(text);
            var found = NoticesIn(code);
            if (found.Count == 0)
                continue;

            uses++;
            if (code.Contains(TheService, StringComparison.Ordinal) || Legacy.ContainsKey(relative))
                continue;

            offenders.Add($"{relative} tells a host ({string.Join(", ", found)}) without asking {TheService} and is not a listed legacy path");
        }

        Assert.True(uses > 0, "The guard found no path that tells a host at all: the names it looks for no longer match the code.");
        Assert.True(
            offenders.Count == 0,
            $"A path that tells a host user about one of the eight kinds of event has to ask {TheService} (who is told, through which channel, when). "
            + "Do not add a file to the legacy list to pass: route it through the service. " + string.Join(" | ", offenders));
    }

    [Fact]
    public void TheLegacyList_HasNoStaleEntry_AndEveryEntryHasItsReasonAndItsEpic()
    {
        var root = FindRepositoryRoot();
        var stale = new List<string>();

        foreach (var (file, (reason, epic)) in Legacy)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{file} is listed without a reason.");
            Assert.False(string.IsNullOrWhiteSpace(epic), $"{file} is listed without the epic that migrates it.");

            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{file} is in the legacy list but does not exist.");

            var code = CodeWithoutComments(File.ReadAllText(path));
            if (code.Contains(TheService, StringComparison.Ordinal))
                stale.Add($"{file} asks {TheService} now: take it off the legacy list");
            else if (NoticesIn(code).Count == 0)
                stale.Add($"{file} no longer tells a host: take it off the legacy list");
        }

        Assert.True(stale.Count == 0, string.Join(" | ", stale));
    }

    [Theory]
    [InlineData("Casazen.Infrastructure/Services/NotificationPreferenceService.cs")]
    [InlineData("Casazen.Infrastructure/Services/HostMessageAlertDelivery.cs")]
    [InlineData("Casazen.Infrastructure/Services/ApprovalAlertDelivery.cs")]
    [InlineData("Casazen.Infrastructure/Services/ScheduledMessageAlertDelivery.cs")]
    public void TheService_AndTheDeliveryOfTheMessageAlerts_AreInsideTheRule(string file)
    {
        var code = CodeWithoutComments(File.ReadAllText(Path.Combine(FindRepositoryRoot(), file.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Contains(TheService, code, StringComparison.Ordinal);
        Assert.NotEmpty(NoticesIn(code));
        Assert.False(Legacy.ContainsKey(file), $"{file} goes through the service: it is not a legacy path.");
    }

    [Fact]
    public void ThePathsOfTheGuestMessages_TellTheHostsOnlyThroughTheDelivery()
    {
        var root = FindRepositoryRoot();
        foreach (var file in new[] { "Casazen.Infrastructure/Services/GuestMessageService.cs", "Casazen.Infrastructure/Services/MessageReceivedNotifier.cs" })
        {
            var code = CodeWithoutComments(File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))));

            Assert.Contains("HostMessageAlertDelivery", code, StringComparison.Ordinal);
            Assert.DoesNotContain("PushAudience.", code, StringComparison.Ordinal);
            Assert.DoesNotContain("EmailTemplates.Names.GuestMessageToHost", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheApprovalQueue_TellsTheHostsOnlyThroughTheDelivery()
    {
        var code = CodeWithoutComments(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Casazen.Infrastructure", "Services", "ApprovalQueueService.cs")));

        Assert.Contains("ApprovalAlertDelivery", code, StringComparison.Ordinal);
        Assert.Empty(NoticesIn(code));
        Assert.DoesNotContain("OrgMembers", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Casazen.Infrastructure/Services/ScheduledMessageDispatcher.cs")]
    [InlineData("Casazen.Infrastructure/Services/ScheduledMessageService.cs")]
    public void TheScheduledMessages_TellTheHostsOnlyThroughTheDelivery(string file)
    {
        var code = CodeWithoutComments(File.ReadAllText(Path.Combine(FindRepositoryRoot(), file.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Contains("ScheduledMessageAlertDelivery", code, StringComparison.Ordinal);
        Assert.Empty(NoticesIn(code));
        Assert.DoesNotContain("OrgMembers", code, StringComparison.Ordinal);
    }

    private static string CodeWithoutComments(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                                                              && !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));

    private static IEnumerable<(string Relative, string Text)> ReadSources(string root)
    {
        foreach (var project in new[] { "Casazen.Core", "Casazen.Infrastructure", "Casazen.Web" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/Migrations/", StringComparison.Ordinal))
                    continue;

                yield return (relative, File.ReadAllText(path));
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
