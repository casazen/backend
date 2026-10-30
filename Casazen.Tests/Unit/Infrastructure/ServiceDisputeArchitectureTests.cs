using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-15c-c: guards on the source of the complaint and the dispute of a job paid through CasaZen, as the other architecture tests of the product keep a
/// rule from being forgotten. (1) Who may do what: the payer's complaint only after the payer proved who it is, the supplier's reply only through the
/// supplier's own door, the admin's decision only through the admin's. (2) A payment is stopped (<c>Disputed</c>) by one piece of code only. (3) The
/// dispute never calls Stripe by itself: the only money it moves is a refund row it reserves and the refund machinery sends. (4) The words of the parties
/// are in no log.
/// </summary>
public class ServiceDisputeArchitectureTests
{
    private static readonly Regex PayerOperation = new(@"\b(OpenDisputeAsync|WithdrawDisputeAsync)\b", RegexOptions.Compiled);

    private static readonly Regex SupplierOperation = new(@"\b(ReplyToDisputeAsync|FindDisputeForSupplierAsync)\b", RegexOptions.Compiled);

    private static readonly Regex AdminOperation = new(@"\b(ResolveDisputeAsync|ListDisputesAsync|GetDisputeAsync)\b", RegexOptions.Compiled);

    private static readonly Regex DisputeServiceType = new(@"\bIServiceDisputeService\b", RegexOptions.Compiled);

    private static readonly Regex PenaltiesType = new(@"\bISupplierDisputePenalties\b", RegexOptions.Compiled);

    /// <summary>The files that may name the payer's operations (they stop the money), each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> MayNamePayerOperations = new Dictionary<string, string>
    {
        ["Casazen.Core/Services/IServiceDisputeService.cs"] = "Declares what the dispute service does.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Disputes.cs"] = "Implements it, under the payment lock.",
        ["Casazen.Infrastructure/Services/ShowcaseBookingManager.cs"] = "The customer of a showcase: it proves that the customer knows the code and the e-mail address, then asks for it.",
        ["Casazen.Web/Controllers/HostServiceRequestDisputesController.cs"] = "The host: it proves who it is by owning the request and holding the permission on its property.",
        ["Casazen.Core/Services/IShowcaseBookingManager.cs"] = "Documents what the customer's own area does (a cref, not a call).",
    };

    private static readonly IReadOnlyDictionary<string, string> MayNameSupplierOperations = new Dictionary<string, string>
    {
        ["Casazen.Core/Services/IServiceDisputeService.cs"] = "Declares what the dispute service does.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Disputes.cs"] = "Implements it, under the payment lock, for the supplier org it is given.",
        ["Casazen.Web/Controllers/SupplierServiceDisputesController.cs"] = "The supplier's door: the org comes only from the caller's own supplier link.",
    };

    private static readonly IReadOnlyDictionary<string, string> MayNameAdminOperations = new Dictionary<string, string>
    {
        ["Casazen.Core/Services/IServiceDisputeService.cs"] = "Declares what the dispute service does.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.DisputesAdmin.cs"] = "Implements the queue and the decision, under the payment lock.",
        ["Casazen.Web/Controllers/AdminServiceDisputesController.cs"] = "The admin's door: AdminOnly.",
    };

    private static readonly IReadOnlyDictionary<string, string> MayNameTheDisputeService = new Dictionary<string, string>
    {
        ["Casazen.Core/Services/IServiceDisputeService.cs"] = "Declares it.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.cs"] = "Implements it.",
        ["Casazen.Infrastructure/Services/ShowcaseBookingManager.cs"] = "Reads the customer's complaint and opens or withdraws it after the customer proved who it is.",
        ["Casazen.Web/Controllers/HostServiceRequestDisputesController.cs"] = "The host's door.",
        ["Casazen.Web/Controllers/SupplierServiceDisputesController.cs"] = "The supplier's door.",
        ["Casazen.Web/Controllers/AdminServiceDisputesController.cs"] = "The admin's door.",
        ["Casazen.Web/Extensions/ServiceCollectionExtensions.cs"] = "Registers it.",
    };

    private static readonly IReadOnlyDictionary<string, string> MayNameThePenalties = new Dictionary<string, string>
    {
        ["Casazen.Core/Services/IServiceDisputeService.cs"] = "Declares the sink next to the dispute service.",
        ["Casazen.Infrastructure/Services/LoggingSupplierDisputePenalties.cs"] = "The default sink: it only logs.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.cs"] = "Receives the sink.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.DisputesAdmin.cs"] = "Records the penalty once, after the decision for the customer was committed.",
        ["Casazen.Web/Extensions/ServiceCollectionExtensions.cs"] = "Registers it.",
    };

    [Fact]
    public void ThePayersComplaint_IsOpenedAndWithdrawnOnlyAfterThePayerProvedWhoItIs()
    {
        var offenders = new List<string>();
        foreach (var (relative, text) in ReadSources(FindRepositoryRoot()))
        {
            if (!MayNamePayerOperations.ContainsKey(relative) && PayerOperation.IsMatch(CodeWithoutComments(text)))
                offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "A complaint stops the money CasaZen holds, so it may be opened or withdrawn only after the payer proved who it is (the manager of the "
            + "customer's own area, the host's door). Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheSuppliersReply_GoesThroughTheSuppliersOwnDoor()
    {
        var offenders = new List<string>();
        foreach (var (relative, text) in ReadSources(FindRepositoryRoot()))
        {
            if (!MayNameSupplierOperations.ContainsKey(relative) && SupplierOperation.IsMatch(CodeWithoutComments(text)))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0, "Only the supplier's own door may reply to a dispute or read it as the supplier: " + string.Join(", ", offenders));

        var controller = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "Controllers", "SupplierServiceDisputesController.cs"));
        Assert.Contains("[Authorize(Policy = CasazenPolicies.Supplier)]", controller, StringComparison.Ordinal);
        Assert.Contains("GetLinkedSupplierOrgIdAsync", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDecision_GoesThroughTheAdminsDoor_WhichIsAdminOnly()
    {
        var offenders = new List<string>();
        foreach (var (relative, text) in ReadSources(FindRepositoryRoot()))
        {
            if (!MayNameAdminOperations.ContainsKey(relative) && AdminOperation.IsMatch(CodeWithoutComments(text)))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0, "Only the admin's door may decide a dispute or read the queue: " + string.Join(", ", offenders));

        var controller = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "Controllers", "AdminServiceDisputesController.cs"));
        Assert.Contains("[Authorize(Policy = CasazenPolicies.AdminOnly)]", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostsDoor_ChecksOwnershipAndThePermissionBeforeItActs()
    {
        var controller = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "Controllers", "HostServiceRequestDisputesController.cs"));

        // The request must be the caller's own (another org's, and a showcase one, are not found) and the caller must be allowed on its property.
        Assert.Contains("GetByIdForHostAsync", controller, StringComparison.Ordinal);
        Assert.Contains("IsAuthorizedAsync(User, resource, WriteOperation)", controller, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = CasazenPolicies.ServiceRequestWrite)]", controller, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDisputeService_IsNamedOnlyByTheDoorsThatProveWhoIsAsking_ItsImplementationAndItsRegistration()
    {
        var offenders = new List<string>();
        foreach (var (relative, text) in ReadSources(FindRepositoryRoot()))
        {
            if (!MayNameTheDisputeService.ContainsKey(relative) && DisputeServiceType.IsMatch(CodeWithoutComments(text)))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0, "Another reader of IServiceDisputeService must be added with its reason: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ThePenalty_IsRecordedOnlyByTheDecision()
    {
        var offenders = new List<string>();
        foreach (var (relative, text) in ReadSources(FindRepositoryRoot()))
        {
            if (!MayNameThePenalties.ContainsKey(relative) && PenaltiesType.IsMatch(CodeWithoutComments(text)))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0, "Only the decision of a dispute records the penalty of a supplier: " + string.Join(", ", offenders));
    }

    [Fact]
    public void AnyPaymentIsStopped_ByTheDisputeCodeAlone()
    {
        var assignment = new Regex(@"\.Status\s*=\s*ServicePaymentStatus\.Disputed\b", RegexOptions.Compiled);
        var offenders = new List<string>();
        var found = false;

        foreach (var (relative, text) in ReadSources(FindRepositoryRoot()))
        {
            if (!assignment.IsMatch(CodeWithoutComments(text)))
                continue;

            found = true;
            if (relative != "Casazen.Infrastructure/Services/SupplierPaymentService.Disputes.cs")
                offenders.Add(relative);
        }

        Assert.True(found, "The guard found no place that stops a payment: the regex no longer matches the code.");
        Assert.True(offenders.Count == 0, "Only the dispute code may move a payment to Disputed (it does so under the payment lock): " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.Disputes.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.DisputesAdmin.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.DisputesJobs.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.DisputesNotices.cs")]
    public void TheDisputeCode_NeverCallsStripeItself(string relative)
    {
        var code = CodeWithoutComments(File.ReadAllText(Path.Combine(FindRepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar))));

        // The only money it moves is a refund row it reserves (ReserveHeld) and the refund machinery sends (SubmitRefundAsync).
        Assert.DoesNotContain("Platform.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Stripe.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTransferAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseAsync(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDecisionForTheCustomer_ReservesItsRefundBeforeItAsksStripe_AndKeepsTheCommission()
    {
        var code = CodeWithoutComments(File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Casazen.Infrastructure", "Services", "SupplierPaymentService.DisputesAdmin.cs")));

        // The commission is never given back by a dispute: the refund is reserved with the rule of the supplier's part only.
        Assert.Contains("keepCommission: true", code, StringComparison.Ordinal);
        Assert.Contains("ReserveHeld(", code, StringComparison.Ordinal);

        // The refund is reserved (by DecideForCustomerAsync, which adds it to the context) inside the transaction of the decision, and asked of Stripe
        // (SubmitRefundAsync) only after that transaction is committed.
        var decide = code.IndexOf("await DecideForCustomerAsync(", StringComparison.Ordinal);
        var commit = code.IndexOf("await CommitAsync(transaction, cancellationToken);", StringComparison.Ordinal);
        var submit = code.IndexOf("await SubmitRefundAsync(", StringComparison.Ordinal);
        Assert.True(decide > 0 && commit > decide && submit > commit, "The refund must be reserved before the commit and sent after it.");
    }

    [Fact]
    public void NothingTheParties_WroteIsInALog()
    {
        var files = new[]
        {
            "Casazen.Infrastructure/Services/SupplierPaymentService.Disputes.cs",
            "Casazen.Infrastructure/Services/SupplierPaymentService.DisputesAdmin.cs",
            "Casazen.Infrastructure/Services/SupplierPaymentService.DisputesJobs.cs",
            "Casazen.Infrastructure/Services/SupplierPaymentService.DisputesNotices.cs",
            "Casazen.Infrastructure/Services/LoggingSupplierDisputePenalties.cs",
            "Casazen.Web/Controllers/HostServiceRequestDisputesController.cs",
            "Casazen.Web/Controllers/SupplierServiceDisputesController.cs",
            "Casazen.Web/Controllers/AdminServiceDisputesController.cs",
        };
        var logStatement = new Regex(@"\blogger\s*\.\s*Log\w+\s*\((?<args>[^;]*)\)\s*;", RegexOptions.Compiled | RegexOptions.Singleline);
        var words = new Regex(
            @"\b(CustomerText|SupplierReply|AdminNote|text|Text|reply|Reply|note|Note|request\.Note|body)\b",
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
                if (words.IsMatch(StripStrings(statement.Groups["args"].Value)))
                    offenders.Add($"{relative}: {Regex.Replace(statement.Value, @"\s+", " ")}");
            }
        }

        Assert.True(statements > 0, "The guard found no log statement at all: the regex no longer matches the code.");
        Assert.True(offenders.Count == 0, "A log statement names what a party wrote: " + string.Join(" | ", offenders));
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
