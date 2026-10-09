using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-15a, decision D2: the commission of CasaZen exists only on the payment of a service a supplier completed. The application
/// fee (<c>ApplicationFeeAmount</c>, <c>application_fee_amount</c>) is set in <b>one</b> place, the gateway of the supplier
/// payments, and nowhere else: this is what keeps true that CasaZen takes no commission on the guests' bookings and on the rent
/// (<see cref="StripeServiceApplicationFeeTests"/>, A3-40). A new PaymentIntent anywhere else in the code that carries a fee fails
/// this test; the way to a new fee is a decision of the product owner and a change to this allow-list, not a quiet line.
/// </summary>
public class ApplicationFeeArchitectureTests
{
    /// <summary>The only files that set the application fee, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/External/StripeSupplierPaymentGateway.cs"] =
            "The gateway of the supplier payments: the commission of the direct charge on the supplier's account (decision D2).",
    };

    // The property assigned (in an initializer or after a variable: "ApplicationFeeAmount = ..."), and the raw Stripe parameter.
    private static readonly Regex FeeAssignment = new(@"\bApplicationFeeAmount\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex FeeParameterLiteral = new(@"""application_fee_(amount|percent)""", RegexOptions.Compiled);

    // A fee on the Stripe objects that take a percentage (subscriptions, invoices) is not allowed either.
    private static readonly Regex FeePercentAssignment = new(@"\bApplicationFeePercent\s*=(?!=)", RegexOptions.Compiled);

    [Fact]
    public void ApplicationFee_IsSetOnlyByTheGatewayOfTheSupplierPayments()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var (relative, code) in ReadCode(root))
        {
            if (AllowedFiles.ContainsKey(relative))
                continue;

            if (FeeAssignment.IsMatch(code) || FeeParameterLiteral.IsMatch(code) || FeePercentAssignment.IsMatch(code))
                offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "The application fee (the commission) is set in StripeSupplierPaymentGateway only: guest bookings, deferred charges and " +
            "rent never carry one (A3-40, StripeServiceApplicationFeeTests). Offending files: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheGateway_SetsTheFee_AndOnlyUnderTheCheckOfSupplierCommission()
    {
        var root = FindRepositoryRoot();

        foreach (var file in AllowedFiles.Keys)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{file} no longer exists: remove it from AllowedFiles.");

            var code = StripComments(File.ReadAllText(path));
            Assert.Matches(FeeAssignment, code);
            // Never an unconditional assignment: the fee goes through the one rule that drops a zero or a fee not below the amount.
            Assert.Contains("SupplierCommission.IsSendable(", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheExistingPaymentIntentsOfCasaZen_StillSetNoFee()
    {
        // The guarantee of A3-40 in the code that creates guest and rent PaymentIntents: none of these files sets a fee.
        var root = FindRepositoryRoot();
        foreach (var file in new[]
                 {
                     "Casazen.Infrastructure/External/StripeService.cs",
                     "Casazen.Infrastructure/Services/RentBillingService.cs",
                     "Casazen.Infrastructure/Services/BookingService.cs",
                 })
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{file} no longer exists: update this test.");
            Assert.DoesNotMatch(FeeAssignment, StripComments(File.ReadAllText(path)));
        }
    }

    [Fact]
    public void TheDetector_SeesAnAssignment_AndIgnoresCommentsAndComparisons()
    {
        Assert.Matches(FeeAssignment, StripComments("var options = new PaymentIntentCreateOptions { ApplicationFeeAmount = 100 };"));
        Assert.Matches(FeeAssignment, StripComments("options.ApplicationFeeAmount = fee;"));
        Assert.DoesNotMatch(FeeAssignment, StripComments("// ApplicationFeeAmount = 100 is not sent"));
        Assert.DoesNotMatch(FeeAssignment, StripComments("/// <c>ApplicationFeeAmount = 0</c> is never sent"));
        Assert.DoesNotMatch(FeeAssignment, StripComments("/* ApplicationFeeAmount = 1 */ var x = 1;"));
        Assert.DoesNotMatch(FeeAssignment, StripComments("if (options.ApplicationFeeAmount == null) return;"));
        Assert.DoesNotMatch(FeeAssignment, StripComments("Assert.Null(options.ApplicationFeeAmount);"));
        Assert.Matches(FeeParameterLiteral, "values[\"application_fee_amount\"] = 10;");
    }

    /// <summary>The code of every application source file (no tests, no migrations), without comments.</summary>
    private static IEnumerable<(string Relative, string Code)> ReadCode(string root)
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

                yield return (relative, StripComments(File.ReadAllText(path)));
            }
        }
    }

    /// <summary>Removes <c>//</c> and <c>///</c> lines and <c>/* */</c> blocks: what a comment says about the fee is not a use of it.</summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
