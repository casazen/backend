using System.Text.RegularExpressions;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Fills the placeholders of a legal document (LEGAL-TEXTS). The text files are HTML fragments with:
/// <list type="bullet">
///   <item><c>{{Token}}</c>: the value of <see cref="LegalVariables"/> (HTML-encoded);</item>
///   <item><c>{{#if Token}}…{{/if}}</c> and <c>{{#if Token}}…{{#else}}…{{/if}}</c>: a part shown only when the token has a
///   value (optional data, such as the DPO, and the "no period applied" wording of a retention category). Not nestable;</item>
///   <item><c>&lt;!-- … --&gt;</c>: a comment, removed before anything else (the metadata of the file).</item>
/// </list>
/// <b>Fail-closed.</b> A token used outside a condition with no value, an unknown token or a malformed placeholder makes
/// the document <b>not publishable</b>: <see cref="Result.IsComplete"/> is false and the caller serves no text, so the
/// public page stays "in preparation" and a placeholder is never shown to a reader. The result names the missing Railway
/// variables (never their values) for the health check and the startup log (decision D9).
/// </summary>
public static partial class LegalDocumentTemplate
{
    /// <param name="Html">The text with every placeholder resolved; meaningful only when <see cref="IsComplete"/>.</param>
    /// <param name="MissingConfiguration">Railway variables to set, sorted.</param>
    /// <param name="Problems">Authoring errors of the text itself: unknown or malformed placeholders.</param>
    public sealed record Result(string Html, IReadOnlyList<string> MissingConfiguration, IReadOnlyList<string> Problems)
    {
        public bool IsComplete => MissingConfiguration.Count == 0 && Problems.Count == 0;
    }

    public static Result Render(string template, LegalVariables variables)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var problems = new SortedSet<string>(StringComparer.Ordinal);

        // HTML comments are the metadata of the file (draft notice, version, rules): they may mention "{{...}}" and are
        // never part of the text.
        var text = CommentRegex().Replace(template, string.Empty);
        text = BlockRegex().Replace(text, match =>
        {
            var token = match.Groups["token"].Value;
            if (!variables.IsKnown(token))
            {
                problems.Add($"unknown placeholder '{token}'");
                return string.Empty;
            }

            return variables.Has(token) ? match.Groups["then"].Value : match.Groups["else"].Value;
        });

        text = ValueRegex().Replace(text, match =>
        {
            var token = match.Groups["token"].Value;
            if (!variables.IsKnown(token))
            {
                problems.Add($"unknown placeholder '{token}'");
                return string.Empty;
            }

            var value = variables.Get(token);
            if (value is null)
            {
                missing.Add(variables.ConfigurationName(token));
                return string.Empty;
            }

            return value;
        });

        // Anything left is a malformed placeholder (a nested or unclosed condition, a bad token): never shown to a reader.
        if (text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal))
            problems.Add("malformed placeholder syntax (unclosed, nested or badly named)");

        return new Result(text, [.. missing], [.. problems]);
    }

    /// <summary>Tokens (outside and inside conditions) the template refers to, for the checks of the texts.</summary>
    public static IReadOnlySet<string> TokensIn(string template)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in AnyTokenRegex().Matches(CommentRegex().Replace(template, string.Empty)))
            tokens.Add(match.Groups["token"].Value);
        return tokens;
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(
        @"\{\{#if\s+(?<token>[A-Za-z][A-Za-z0-9.]*)\s*\}\}(?<then>.*?)(?:\{\{#else\}\}(?<else>.*?))?\{\{/if\}\}",
        RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"\{\{\s*(?<token>[A-Za-z][A-Za-z0-9.]*)\s*\}\}")]
    private static partial Regex ValueRegex();

    [GeneratedRegex(@"\{\{\s*(?:#if\s+)?(?<token>[A-Za-z][A-Za-z0-9.]*)\s*\}\}")]
    private static partial Regex AnyTokenRegex();
}
