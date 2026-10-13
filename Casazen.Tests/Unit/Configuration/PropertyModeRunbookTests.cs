using System.Reflection;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// PM-02: the runbook of the scheduled change of mode must say what the code does: every error code of the API, the flag, the
/// job, the endpoints, the reason of the calendar block. The pages that list jobs, flags and e-mails carry the new ones.
/// </summary>
public class PropertyModeRunbookTests
{
    private static string Runbooks => Path.Combine(FindRepositoryRoot(), "docs", "runbooks");

    private static string RentalModeRunbook => File.ReadAllText(Path.Combine(Runbooks, "property-rental-mode.md"));

    public static TheoryData<string> ErrorCodes()
    {
        var codes = new TheoryData<string>();
        foreach (var field in typeof(PropertyModeErrorCodes)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f is { IsLiteral: true } && !f.Name.EndsWith("MessageKey", StringComparison.Ordinal)))
        {
            codes.Add((string)field.GetRawConstantValue()!);
        }

        return codes;
    }

    [Theory]
    [MemberData(nameof(ErrorCodes))]
    public void Runbook_EveryErrorCodeOfTheChange_IsDocumented(string code)
    {
        // property_not_found is the shared 404 of every property endpoint, written in the API table as well.
        Assert.Contains($"`{code}`", RentalModeRunbook);
    }

    [Fact]
    public void Runbook_NamesTheFlagTheJobTheEndpointsAndTheBlockReason()
    {
        var runbook = RentalModeRunbook;

        Assert.Contains($"`Features:{FeatureFlags.PropertyModeChange}`", runbook);
        Assert.Contains($"`Features__{FeatureFlags.PropertyModeChange}`", runbook);
        Assert.Contains($"`{PropertyModeChangeJob.RecurringJobId}`", runbook);
        Assert.Contains("GET /api/properties/{id}/mode`", runbook);
        Assert.Contains("GET /api/properties/{id}/mode/preview", runbook);
        Assert.Contains("POST /api/properties/{id}/mode/change`", runbook);
        Assert.Contains("DELETE /api/properties/{id}/mode/change/{changeId}`", runbook);
        Assert.Contains("UIX_PropertyModeChanges_PropertyId_Scheduled", runbook);
        Assert.Contains("ModeChange` = 3", runbook);
        Assert.Contains("calendar_block_held_by_mode_change", runbook);
        Assert.Contains("ReevaluateAsync", runbook);
    }

    [Fact]
    public void Runbook_TheNumbersOfTheRulesAreTheOnesOfTheCode()
    {
        var runbook = RentalModeRunbook;

        Assert.Contains($"`PropertyModeRules.MaxYearsAhead`", runbook);
        Assert.Contains($"{PropertyModeRules.MaxYearsAhead}", runbook);
        Assert.Contains($"two years (`PropertyModeRules.CalendarBlockYears`", runbook);
        Assert.Equal(2, PropertyModeRules.CalendarBlockYears);
    }

    [Fact]
    public void OtherRunbooks_ListTheJobTheFlagAndTheEmails()
    {
        Assert.Contains("property-mode-change", File.ReadAllText(Path.Combine(Runbooks, "hangfire.md")));
        var flags = File.ReadAllText(Path.Combine(Runbooks, "feature-flags.md"));
        Assert.Contains($"`{FeatureFlags.PropertyModeChange}`", flags);
        Assert.Contains($"`Features__{FeatureFlags.PropertyModeChange}`", flags);
        var emails = File.ReadAllText(Path.Combine(Runbooks, "email.md"));
        foreach (var template in new[]
                 {
                     "property-mode-change-scheduled",
                     "property-mode-change-applied",
                     "property-mode-change-failed",
                 })
        {
            Assert.Contains($"`{template}`", emails);
        }

        Assert.Contains("PM-01, PM-02", File.ReadAllText(Path.Combine(Runbooks, "index.md")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
