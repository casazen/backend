using System.Xml.Linq;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// The texts of the emails exist in both languages and once: <c>EmailTexts.resx</c> (Italian, the default) and <c>EmailTexts.en.resx</c>
/// have the same keys, none repeated. A missing English key is not an error at run time (the resource manager falls back to the Italian
/// text) and a repeated key is only a warning of the build, so without this test an English email could go out in Italian, or a merge
/// of two branches that add the same key could pass unnoticed.
/// </summary>
public class EmailTextsParityTests
{
    private static List<string> Keys(string fileName)
    {
        var path = Path.Combine(SolutionRoot(), "Casazen.Infrastructure", "Email", "Templates", fileName);
        return XDocument.Load(path).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToList();
    }

    [Fact]
    public void ItalianAndEnglish_HaveTheSameKeys()
    {
        var italian = Keys("EmailTexts.resx");
        var english = Keys("EmailTexts.en.resx");

        Assert.Empty(italian.Except(english));
        Assert.Empty(english.Except(italian));
    }

    [Theory]
    [InlineData("EmailTexts.resx")]
    [InlineData("EmailTexts.en.resx")]
    public void NoKeyIsRepeated(string fileName)
    {
        var keys = Keys(fileName);

        Assert.Empty(keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
