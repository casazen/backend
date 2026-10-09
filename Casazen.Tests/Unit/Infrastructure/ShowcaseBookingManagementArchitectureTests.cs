using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-11: two guards on the source of the customer's own area of a booking, as the other architecture tests of the product keep a
/// rule from being forgotten. (1) What the customer does to a request (cancel, move, answer a proposed time) is done by
/// <c>ShowcaseBookingManager</c> and nobody else: the actions trust the id of the request they are given because the manager has
/// proved that the customer knows the code and the e-mail address, so no other code may call them. (2) Nothing a customer typed is
/// ever written to a log.
/// </summary>
public class ShowcaseBookingManagementArchitectureTests
{
    /// <summary>The names of the actions of the customer (<c>IShowcaseRequestCustomerActions</c>).</summary>
    private static readonly Regex CustomerActionCall = new(
        @"\b(Cancel|Reschedule|AcceptProposal|RejectProposal)AsCustomerAsync\b",
        RegexOptions.Compiled);

    private static readonly Regex CustomerActionsType = new(@"\bIShowcaseRequestCustomerActions\b", RegexOptions.Compiled);

    /// <summary>The files that may name the actions, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> MayNameTheActions = new Dictionary<string, string>
    {
        ["Casazen.Core/Services/IShowcaseBookingManager.cs"] = "Declares the interface of the actions, next to the manager's.",
        ["Casazen.Infrastructure/Services/ServiceRequestService.Customer.cs"] = "Implements the actions with the other transitions of a request.",
        ["Casazen.Infrastructure/Services/ShowcaseBookingManager.cs"] = "The only caller: it proves who is asking, then acts.",
        ["Casazen.Web/Extensions/ServiceCollectionExtensions.cs"] = "Registers the interface.",
    };

    [Fact]
    public void TheActionsOfTheCustomer_AreNamedOnlyByTheManagerTheirImplementationAndTheirRegistration()
    {
        var offenders = new List<string>();

        foreach (var (relative, text) in ReadSources(FindRepositoryRoot()))
        {
            var code = CodeWithoutComments(text);
            if (MayNameTheActions.ContainsKey(relative) || !(CustomerActionCall.IsMatch(code) || CustomerActionsType.IsMatch(code)))
                continue;

            offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "The actions of the customer of a showcase request trust the id they are given: only ShowcaseBookingManager, which has proved "
            + "who is asking, may call them. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheGuardOnTheGuard_TheManagerCallsAllFourActions_AndTheImplementationDeclaresThem()
    {
        var root = FindRepositoryRoot();
        var manager = CodeWithoutComments(File.ReadAllText(Path.Combine(root, "Casazen.Infrastructure", "Services", "ShowcaseBookingManager.cs")));
        var implementation = CodeWithoutComments(
            File.ReadAllText(Path.Combine(root, "Casazen.Infrastructure", "Services", "ServiceRequestService.Customer.cs")));

        var called = CustomerActionCall.Matches(manager).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        var declared = CustomerActionCall.Matches(implementation).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

        Assert.Equal(new[] { "AcceptProposal", "Cancel", "RejectProposal", "Reschedule" }, called);
        Assert.Equal(called, declared);
    }

    [Fact]
    public void NothingTheCustomerTyped_IsWrittenToALog()
    {
        // The statements that log, in the files that handle what the customer typed.
        var files = new[]
        {
            "Casazen.Infrastructure/Services/ShowcaseBookingManager.cs",
            "Casazen.Infrastructure/Services/ServiceRequestService.Customer.cs",
            "Casazen.Web/Controllers/PublicSupplierBookingManagementController.cs",
            "Casazen.Web/Infrastructure/PerEmailRateLimiter.cs",
            "Casazen.Web/Infrastructure/SupplierBookingManageEmailRateLimiter.cs",
        };
        var logStatement = new Regex(@"\blogger\s*\.\s*Log\w+\s*\((?<args>[^;]*)\)\s*;", RegexOptions.Compiled | RegexOptions.Singleline);
        var typed = new Regex(
            @"\b(credentials|email|address|code|slug|reason|text|fullName|phone|floor|accessNotes|PublicCode|Email|Slug|Reason)\b",
            RegexOptions.Compiled);
        var root = FindRepositoryRoot();
        var offenders = new List<string>();
        var statements = 0;

        foreach (var relative in files)
        {
            var code = CodeWithoutComments(File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))));
            foreach (Match statement in logStatement.Matches(code))
            {
                statements++;
                // The message template is text; what is passed after it is what gets written.
                var args = statement.Groups["args"].Value;
                if (typed.IsMatch(StripStrings(args)))
                    offenders.Add($"{relative}: {Regex.Replace(statement.Value, @"\s+", " ")}");
            }
        }

        Assert.True(statements > 0, "The guard found no log statement at all: the regex no longer matches the code.");
        Assert.True(offenders.Count == 0, "A log statement names what a customer typed: " + string.Join(" | ", offenders));
    }

    // ─── helpers ───

    /// <summary>The source without the string literals: the words of a message are not what is passed to it.</summary>
    private static string StripStrings(string code) => Regex.Replace(code, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");

    /// <summary>The source without the lines that are only a comment (documentation names things without using them).</summary>
    private static string CodeWithoutComments(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

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
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
