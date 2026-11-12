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
    /// <summary>The name of the service a path asks: a file that mentions it is a path that goes through the one place.</summary>
    private const string TheService = "INotificationPreferenceService";

    /// <summary>
    /// How a path that tells a host is recognised in the code. The pushes are found by the audience they name (the hosts of a booking, the hosts of
    /// a property, one user), the choice of people by the query of the audience, the emails by the template they queue (the emails to a host about
    /// the new booking, the cancellation, the guest messages and the legal deadlines), the counter of the bell and the popup alerts by the row they
    /// write. The task that adds a kind of event with its own notices (an approval to give, a draft ready, a scheduled message that failed, a sync
    /// error) adds them here in the same change.
    /// </summary>
    private static readonly string[] Notices =
    [
        // Pushes: every audience made of host users.
        "PushAudience.BookingHosts(",
        "PushAudience.PropertyHosts(",
        "PushAudience.User(",

        // The one query that says who may hear about a property (AM-03), outside the service.
        "HostNotificationAudience.UsersToTell(",
        "HostNotificationAudience.AdministratorsToTell(",

        // NewGuestMessage, UrgentGuestMessage.
        "EmailTemplates.Names.GuestMessageToHost",
        "EmailTemplates.Names.HostMessageReceived",

        // NewBooking, BookingCancelled.
        "EmailTemplates.Names.HostBookingConfirmed",
        "EmailTemplates.Names.HostBookingCancelledByGuest",
        "EmailTemplates.Names.HostDeferredChargeCancelled",

        // LegalDeadline.
        "EmailTemplates.Names.AlloggiatiDeadline",
        "EmailTemplates.Names.GuestCheckInIncomplete",

        // AiDraftToApprove (E05/F04): an item of the approval queue waits for a decision.
        "EmailTemplates.Names.HostApprovalToDecide",

        // The counter of the bell and the popup alerts: whoever writes a row of either for a host tells it something.
        "InAppNotifications.Add",
        "PopupAlerts.Add",
    ];

    /// <summary>
    /// The same, for the notices that a plain name cannot tell from a longer one: building a row of the bell or a popup alert (whatever way it is then
    /// added to the context), but not <c>InAppNotificationItem</c>, <c>PopupAlertMessage</c> and the like.
    /// </summary>
    private static readonly Regex[] NoticePatterns =
    [
        new(@"new\s+InAppNotification\b", RegexOptions.CultureInvariant),
        new(@"new\s+PopupAlert\b", RegexOptions.CultureInvariant),
    ];

    /// <summary>The notices a (comment-free) source contains, by name.</summary>
    private static List<string> NoticesIn(string code) =>
        [
            .. Notices.Where(notice => code.Contains(notice, StringComparison.Ordinal)),
            .. NoticePatterns.Where(pattern => pattern.IsMatch(code)).Select(pattern => pattern.ToString()),
        ];

    /// <summary>
    /// The paths that tell a host without asking the service, each with the reason and the epic that migrates it (every one of them keeps the
    /// audience it has always had until then). A path is taken off this list when it goes through the service, never added to it to make a test pass.
    /// </summary>
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
        ["Casazen.Infrastructure/Services/ServiceRequestNotifier.cs"] =
            ("the push to the hosts about a request to a supplier (taken, started, completed, a time proposed, a quote): outside the eight kinds of event of NF-65", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/ServiceRequestNotifier.Backups.cs"] =
            ("the push to the hosts about the backup suppliers of a request: outside the eight kinds of event of NF-65", "none planned (outside the eight kinds of event of NF-65)"),
        ["Casazen.Infrastructure/Services/AlertRouter.cs"] =
            ("the popup alerts (ADR-025): the router decides who is alerted and when, and borrows only the quiet hours of the preferences; it does not read the channel settings yet", "E01 (the alert router reads the preferences, assumption of NF-65)"),
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
        // Both sources of a message of a guest (a channel, a direct booking) alert through the one delivery, which asks the service.
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
        // The queue raises the alert of a new item through the one delivery, which asks the service (E05/F04): it no longer writes the notice of the bell to every member of the org.
        var code = CodeWithoutComments(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Casazen.Infrastructure", "Services", "ApprovalQueueService.cs")));

        Assert.Contains("ApprovalAlertDelivery", code, StringComparison.Ordinal);
        Assert.Empty(NoticesIn(code));
        Assert.DoesNotContain("OrgMembers", code, StringComparison.Ordinal);
    }

    // ─── helpers ───

    /// <summary>The source without the lines that are only a comment (documentation names things without using them).</summary>
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
