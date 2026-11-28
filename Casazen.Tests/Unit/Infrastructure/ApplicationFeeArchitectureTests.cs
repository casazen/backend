using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// Supplier commissions are limited to the supplier gateway (SP-15a, D2). The contract-bound tenant SEPA gateway
/// additionally retains the fixed EUR 1 transaction fee documented in docs/runbooks/tenant-rent-sepa.md.
/// Guest bookings and legacy rent charges remain fee-free. Refund fees stay limited to the supplier gateway.
/// </summary>
public class ApplicationFeeArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/External/StripeSupplierPaymentGateway.cs"] =
            "The gateway of the supplier payments: the commission of the direct charge on the supplier's account (decision D2).",
    };

    private static readonly Regex FeeAssignment = new(@"\bApplicationFeeAmount\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex FeeParameterLiteral = new(@"""application_fee_(amount|percent)""", RegexOptions.Compiled);

    private static readonly Regex FeePercentAssignment = new(@"\bApplicationFeePercent\s*=(?!=)", RegexOptions.Compiled);

    [Fact]
    public void ApplicationFee_IsSetOnlyByTheApprovedPaymentGateways()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var (relative, code) in ReadCode(root))
        {
            if (AllowedFiles.ContainsKey(relative)
                || relative == "Casazen.Infrastructure/External/StripeRentSepaGateway.cs")
                continue;

            if (FeeAssignment.IsMatch(code) || FeeParameterLiteral.IsMatch(code) || FeePercentAssignment.IsMatch(code))
                offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "Application fees are limited to supplier commissions and the fixed tenant SEPA transaction fee; guest bookings, " +
            "deferred charges and legacy rent remain fee-free. Offending files: " + string.Join(", ", offenders));
    }

    private static readonly Regex RefundFeeAssignment = new(@"\bRefundApplicationFee\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex RefundFeeParameterLiteral = new(@"""refund_application_fee""", RegexOptions.Compiled);

    [Fact]
    public void RefundApplicationFee_IsAskedOnlyByTheGatewayOfTheSupplierPayments()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var (relative, code) in ReadCode(root))
        {
            if (AllowedFiles.ContainsKey(relative))
                continue;

            if (RefundFeeAssignment.IsMatch(code) || RefundFeeParameterLiteral.IsMatch(code))
                offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "The commission goes back with a refund (refund_application_fee) only in StripeSupplierPaymentGateway: the refunds of guest " +
            "bookings and rent have none to give back. Offending files: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheGateway_AsksForTheRefundOfTheFee_OnlyWhenThePaymentCarriedOne()
    {
        var code = StripComments(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Casazen.Infrastructure", "External", "StripeSupplierPaymentGateway.cs")));

        Assert.Matches(RefundFeeAssignment, code);
        Assert.Contains("if (request.RefundApplicationFee)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefundDetector_SeesAnAssignment_AndIgnoresCommentsAndComparisons()
    {
        Assert.Matches(RefundFeeAssignment, StripComments("var options = new RefundCreateOptions { RefundApplicationFee = true };"));
        Assert.Matches(RefundFeeAssignment, StripComments("options.RefundApplicationFee = true;"));
        Assert.DoesNotMatch(RefundFeeAssignment, StripComments("// RefundApplicationFee = true is only for the supplier payments"));
        Assert.DoesNotMatch(RefundFeeAssignment, StripComments("if (options.RefundApplicationFee == true) return;"));
        Assert.DoesNotMatch(RefundFeeAssignment, StripComments("var refund = RefundApplicationFeeOf(charge);"));
        Assert.Matches(RefundFeeParameterLiteral, "values[\"refund_application_fee\"] = true;");
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
            Assert.Contains("SupplierCommission.IsSendable(", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePlatformGateway_SetsNoFee_AsksNoFeeRefund_AndNeverUsesAConnectedAccount()
    {
        var code = StripComments(File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Casazen.Infrastructure", "External", "StripePlatformPaymentGateway.cs")));

        Assert.DoesNotMatch(FeeAssignment, code);
        Assert.DoesNotMatch(FeePercentAssignment, code);
        Assert.DoesNotMatch(FeeParameterLiteral, code);
        Assert.DoesNotMatch(RefundFeeAssignment, code);
        Assert.DoesNotMatch(RefundFeeParameterLiteral, code);
        Assert.DoesNotContain("StripeAccount", code, StringComparison.Ordinal);
        Assert.DoesNotContain("on_behalf_of", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OnBehalfOf", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExistingPaymentIntentsOfCasaZen_StillSetNoFee()
    {
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
