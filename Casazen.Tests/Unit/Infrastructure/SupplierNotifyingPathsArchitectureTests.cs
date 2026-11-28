using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class SupplierNotifyingPathsArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, string[]> NoticesOfEachKind = new Dictionary<string, string[]>
    {
        ["NewRequest"] =
        [
            "PushTypes.ServiceRequestCreated",
            "PushTypes.ServiceRequestAutoAccepted",
            "EmailTemplates.Names.ServiceRequestCreated",
            "EmailTemplates.Names.ServiceRequestAutoAccepted",
            "EmailTemplates.Names.SupplierBookingNewRequest",
        ],
        ["DayBeforeReminder"] =
        [
            "PushTypes.ServiceRequestDayBefore",
            "EmailTemplates.Names.ServiceRequestDayBefore",
        ],
        ["PaymentReceived"] =
        [
            "PushTypes.ServiceRequestPaid",
            "PushTypes.ServicePaymentReceived",
            "EmailTemplates.Names.ServiceRequestPaid",
            "EmailTemplates.Names.ServicePaymentReceived",
        ],
    };

    private static readonly IReadOnlyDictionary<string, string[]> MayQueue = new Dictionary<string, string[]>
    {
        ["NewRequest"] =
        [
            "Casazen.Infrastructure/Services/ServiceRequestNotifier.cs",
            "Casazen.Infrastructure/Services/ShowcaseBookingNotifier.cs",
        ],
        ["DayBeforeReminder"] =
        [
            "Casazen.Infrastructure/Services/ServiceRequestNotifier.cs",
        ],
        ["PaymentReceived"] =
        [
            "Casazen.Infrastructure/Services/ServiceRequestNotifier.cs",
            "Casazen.Infrastructure/Services/SupplierPaymentService.Notices.cs",
        ],
    };

    private static readonly Regex MemberStart = new(@"^    (?:public|private|internal|protected)\b[^\n]*$", RegexOptions.Compiled | RegexOptions.Multiline);

    [Theory]
    [InlineData("NewRequest")]
    [InlineData("DayBeforeReminder")]
    [InlineData("PaymentReceived")]
    public void TheNoticesOfAKind_AreQueuedOnlyByTheListedFiles_AndEachMethodAsksTheSwitchOfItsKind(string kind)
    {
        var root = FindRepositoryRoot();
        var uses = 0;
        var offenders = new List<string>();

        foreach (var (relative, text) in ReadSources(root))
        {
            var code = CodeWithoutComments(text);
            var inThisFile = NoticesOfEachKind[kind].SelectMany(notice => IndexesOf(code, notice)).Order().ToList();
            if (inThisFile.Count == 0)
                continue;

            uses += inThisFile.Count;
            if (!MayQueue[kind].Contains(relative))
            {
                offenders.Add($"{relative} queues a notice of kind {kind} but is not in the list of files that may");
                continue;
            }

            var starts = MemberStart.Matches(code).Select(m => m.Index).ToList();
            foreach (var index in inThisFile)
            {
                var from = starts.LastOrDefault(start => start <= index);
                var to = starts.FirstOrDefault(start => start > index, code.Length);
                var member = code[from..to];
                if (!member.Contains("SupplierNotificationPreferences.AllowsAsync(", StringComparison.Ordinal)
                    || !member.Contains($"SupplierNotificationKind.{kind}", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: {Regex.Match(member, @"^    [^\n]*").Value.Trim()} queues a notice of kind {kind} without asking its switch");
                }
            }
        }

        Assert.True(uses > 0, $"The guard found no notice of kind {kind} at all: the names it looks for no longer match the code.");
        Assert.True(
            offenders.Count == 0,
            "A notice that the supplier can switch off has to ask SupplierNotificationPreferences.AllowsAsync with its kind in the method that queues it. "
            + string.Join(" | ", offenders));
    }

    [Fact]
    public void TheListOfFiles_IsFullOfFilesThatExist()
    {
        var root = FindRepositoryRoot();
        foreach (var file in MayQueue.Values.SelectMany(files => files).Distinct())
            Assert.True(File.Exists(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))), $"{file} is in the list but does not exist.");
    }

    private static IEnumerable<int> IndexesOf(string code, string needle)
    {
        var index = code.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            yield return index;
            index = code.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
    }

    private static string CodeWithoutComments(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

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
