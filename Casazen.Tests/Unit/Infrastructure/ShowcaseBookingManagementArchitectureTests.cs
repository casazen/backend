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
        @"\b(Cancel|Reschedule|AcceptProposal|RejectProposal|AcceptQuote|RejectQuote|ReadThread|PostMessage)AsCustomerAsync\b",
        RegexOptions.Compiled);

    private static readonly Regex CustomerActionsType = new(@"\bIShowcaseRequestCustomerActions\b", RegexOptions.Compiled);

    /// <summary>The files that may name the actions, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> MayNameTheActions = new Dictionary<string, string>
    {
        ["Casazen.Core/Services/IShowcaseBookingManager.cs"] = "Declares the interface of the actions, next to the manager's.",
        ["Casazen.Infrastructure/Services/ServiceRequestService.Customer.cs"] = "Implements the actions with the other transitions of a request.",
        ["Casazen.Infrastructure/Services/ServiceRequestService.Thread.cs"] = "Implements the actions of the conversation of a request on measure (SP-10b).",
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

    private static readonly Regex FundsType = new(@"\bISupplierFundsService\b", RegexOptions.Compiled);

    private static readonly Regex ConfirmOkName = new(@"\bConfirmOkAsync\b", RegexOptions.Compiled);

    /// <summary>The files that may name the money CasaZen holds (SP-15c-a), each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> MayNameTheFunds = new Dictionary<string, string>
    {
        ["Casazen.Core/Services/ISupplierFundsService.cs"] = "Declares the interface.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.cs"] = "Implements it.",
        ["Casazen.Infrastructure/Services/ShowcaseBookingService.cs"] = "Blocks the customer's card for a hold, gives the request its payment and releases the block of a hold that lapsed.",
        ["Casazen.Infrastructure/Services/ShowcaseBookingManager.cs"] = "The only caller of the payer's «all fine»: it proves who is asking first.",
        ["Casazen.Infrastructure/Services/ServiceRequestService.cs"] = "Charges the card when the supplier accepts and settles the money of a request that closes.",
        ["Casazen.Infrastructure/Services/ServiceRequestAutoCancelService.cs"] = "Settles the money of the requests it cancels.",
        ["Casazen.Web/Extensions/ServiceCollectionExtensions.cs"] = "Registers it.",
    };

    /// <summary>The files that may name the payer's «all fine» (it releases the money to the supplier at once), each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> MayNameConfirmOk = new Dictionary<string, string>
    {
        ["Casazen.Core/Services/ISupplierFundsService.cs"] = "Declares what the funds service does.",
        ["Casazen.Core/Services/IShowcaseBookingManager.cs"] = "Declares what the customer's own area does.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.FundsRelease.cs"] = "Implements it, under the payment lock.",
        ["Casazen.Infrastructure/Services/ShowcaseBookingManager.cs"] = "Proves that the customer knows the code and the e-mail address, then asks for it.",
        ["Casazen.Infrastructure/Services/ServiceRequestService.Payments.cs"] = "The host's all fine (SP-15c-b): the host proved who it is by owning the request (another org's, and a showcase one, are not found).",
        ["Casazen.Web/Controllers/PublicSupplierBookingManagementController.cs"] = "The public door of the customer's own area.",
    };

    [Fact]
    public void TheMoneyCasaZenHolds_IsMovedOnlyByTheServicesThatOwnIt_AndAllFineOnlyAfterTheCustomerProvedWhoItIs()
    {
        var offenders = new List<string>();
        var confirmOk = new List<string>();

        foreach (var (relative, text) in ReadSources(FindRepositoryRoot()))
        {
            var code = CodeWithoutComments(text);
            if (!MayNameTheFunds.ContainsKey(relative) && FundsType.IsMatch(code))
                offenders.Add(relative);

            if (!MayNameConfirmOk.ContainsKey(relative) && ConfirmOkName.IsMatch(code))
                confirmOk.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "The money CasaZen holds is charged, released and given back only by the services that own it. Another reader of "
            + "ISupplierFundsService must be added to MayNameTheFunds with its reason: " + string.Join(", ", offenders));
        Assert.True(
            confirmOk.Count == 0,
            "The payer's «all fine» releases the money to the supplier at once, so it may be asked for only after the payer proved who it is "
            + "(the manager of the customer's own area). Offenders: " + string.Join(", ", confirmOk));
    }

    [Fact]
    public void TheGuardOnTheGuard_TheManagerCallsAllTheActions_AndTheImplementationDeclaresThem()
    {
        var root = FindRepositoryRoot();
        var manager = CodeWithoutComments(File.ReadAllText(Path.Combine(root, "Casazen.Infrastructure", "Services", "ShowcaseBookingManager.cs")));
        var implementation = CodeWithoutComments(
            File.ReadAllText(Path.Combine(root, "Casazen.Infrastructure", "Services", "ServiceRequestService.Customer.cs"))
            + File.ReadAllText(Path.Combine(root, "Casazen.Infrastructure", "Services", "ServiceRequestService.Thread.cs")));

        var called = CustomerActionCall.Matches(manager).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        var declared = CustomerActionCall.Matches(implementation).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

        Assert.Equal(new[] { "AcceptProposal", "AcceptQuote", "Cancel", "PostMessage", "ReadThread", "RejectProposal", "RejectQuote", "Reschedule" }, called);
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
            "Casazen.Infrastructure/Services/ServiceRequestService.Thread.cs",
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
