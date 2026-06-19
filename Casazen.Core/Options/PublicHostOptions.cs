namespace Casazen.Core.Options;

public class PublicHostOptions
{
    public const string SectionName = "PublicHost";

    public string BaseDomain { get; set; } = "casazen.it";

    public string[] ReservedSubdomains { get; set; } =
    [
        "www",
        "api",
        "app",
        "admin",
        "staging",
        "test",
        "mail",
    ];
}
