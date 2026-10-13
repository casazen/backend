namespace Casazen.Tests.Unit.Services;

/// <summary>Configuration and files shared by the tests of the legal documents (LEGAL-TEXTS).</summary>
internal static class LegalTextFixtures
{
    /// <summary>Version of the drafts shipped under <c>Casazen.Web/LegalDocuments</c>.</summary>
    public const string DraftVersion = "2026-10-v1";

    /// <summary>The folder the API ships (<c>Casazen.Web/LegalDocuments</c>), found from the test output up to the repository.</summary>
    public static string ShippedRoot() => Path.Combine(RepositoryRoot(), "Casazen.Web", "LegalDocuments");

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Casazen.Web", "LegalDocuments")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Casazen.Web/LegalDocuments was not found above the test output.");
    }

    /// <summary>Every value the three drafts need, with fake data, and the drafts' version for the three documents.</summary>
    public static Dictionary<string, string?> CompleteConfiguration() => new()
    {
        ["Legal:ContentPath"] = ShippedRoot(),
        ["Legal:Documents:Tos:Version"] = DraftVersion,
        ["Legal:Documents:Privacy:Version"] = DraftVersion,
        ["Legal:Documents:Dpa:Version"] = DraftVersion,
        ["Legal:Controller:Name"] = "Test Rentals S.r.l.",
        ["Legal:Controller:Address"] = "Via Prova 1, 20100 Milano (MI)",
        ["Legal:Controller:VatId"] = "IT12345678901",
        ["Legal:Controller:Pec"] = "pec@test-rentals.test",
        ["Legal:Controller:PrivacyEmail"] = "privacy@test-rentals.test",
        ["Legal:Terms:GoverningCourt"] = "Milano",
        ["Legal:Terms:ChangeNoticeDays"] = "30",
        ["Legal:Terms:TerminationNoticeDays"] = "30",
        ["Legal:Terms:LiabilityCapMonths"] = "12",
        ["Legal:Terms:DataReturnDays"] = "30",
        ["Legal:Dpa:BreachNotificationHours"] = "48",
        ["Legal:Dpa:SubprocessorNoticeDays"] = "30",
        ["Legal:Dpa:AuditNoticeDays"] = "30",
        ["App:PublicSiteBaseUrl"] = "https://app.test",
        ["Billing:Display:Starter:PriceMonthly"] = "29.00",
        ["Billing:Display:Pro:PriceMonthly"] = "79.00",
        ["Billing:Display:Scale:PriceMonthly"] = "199.00",
    };
}
