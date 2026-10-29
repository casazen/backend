using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Casazen.Core.Suppliers;

/// <summary>
/// What the rules of the content need to know that is not in the content itself: whether an image URL is an object of the supplier's
/// own storage, and which services of the supplier are active.
/// </summary>
/// <param name="IsOwnImageUrl">True for the URL of an object in the supplier's own folder of the public storage.</param>
/// <param name="ActiveServiceSlugs">Slugs of the supplier's active services.</param>
public sealed record ShowcaseContentContext(Func<string, bool> IsOwnImageUrl, IReadOnlyCollection<string> ActiveServiceSlugs);

/// <summary>
/// The rules of the content of a showcase (SP-13a), in one place and without any dependency: reading and writing the JSON document,
/// applying a save (a JSON merge patch, RFC 7396) to the draft, validating every field of the contract, cleaning it for a
/// publication and telling what a publication would change.
/// </summary>
/// <remarks>
/// <para><b>A draft may be incomplete, a publication may not.</b> The editor autosaves while the supplier types, so a half-written
/// FAQ or a team member with no name is a valid draft: only the limits, the formats and the references (images, services) are checked
/// then. <see cref="ForPublication"/> drops the empty rows and refuses the half-filled ones.</para>
/// <para><b>Nothing the contract does not name gets in.</b> A member that is not in the contract (a phone number, a domain, a title
/// for the search engines) is a 422 on that member, not an ignored one: the format is versioned, so a client that thinks it can save
/// more than it can must be told.</para>
/// <para><b>Images</b> are references to objects of the supplier's own storage (PNG, JPG or WebP; never SVG, which can carry
/// scripts). The upload, with its checks on the content, is a task of its own (SP-13b).</para>
/// </remarks>
public static class ShowcaseContentRules
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // A save names the members of the contract exactly as they are written (camel case) and nothing else.
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // ─── Reading and writing ─────────────────────────────────────────────────────

    /// <summary>The document of a stored column; an empty document for a missing or unreadable one (a read never fails on old data).</summary>
    public static ShowcaseContentDocument Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ShowcaseContentDocument();

        try
        {
            return Complete(JsonSerializer.Deserialize<ShowcaseContentDocument>(json, StoredJson) ?? new ShowcaseContentDocument());
        }
        catch (JsonException)
        {
            return new ShowcaseContentDocument();
        }
    }

    /// <summary>The stored form of a document: compact camel-case JSON without the members that are not set.</summary>
    public static string Write(ShowcaseContentDocument document) => JsonSerializer.Serialize(Complete(document), StoredJson);

    // ─── A save ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The draft with a save applied: <paramref name="patch"/> is a JSON merge patch (RFC 7396) of the contract: a member that is
    /// there replaces the stored one, an object is merged member by member, a list replaces the whole list, <c>null</c> clears. The
    /// result is read in strict mode and validated as a whole.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="ShowcaseContentErrors.Invalid"/>, with the fields at fault.</exception>
    public static ShowcaseContentDocument ApplyPatch(string? draftJson, JsonObject patch, ShowcaseContentContext context)
    {
        ArgumentNullException.ThrowIfNull(patch);

        JsonObject target;
        try
        {
            target = (string.IsNullOrWhiteSpace(draftJson) ? null : JsonNode.Parse(draftJson)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            target = new JsonObject();
        }

        MergePatch(target, patch);

        ShowcaseContentDocument document;
        try
        {
            document = target.Deserialize<ShowcaseContentDocument>(StrictJson) ?? new ShowcaseContentDocument();
        }
        catch (JsonException ex)
        {
            throw ShowcaseContentErrors.InvalidFields([FieldOf(ex)]);
        }

        // A service that was paused or deleted since the draft was saved is a stale reference, not a mistake of this save: it leaves
        // the order quietly, so the autosave of a sentence never fails because of the catalog. A save that names the order is checked.
        if (!patch.ContainsKey("serviceOrder"))
        {
            document = document with
            {
                ServiceOrder = (document.ServiceOrder ?? []).Where(slug => slug is not null && context.ActiveServiceSlugs.Contains(slug)).ToList(),
            };
        }

        return Normalize(document, context);
    }

    private static void MergePatch(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch.ToList())
        {
            if (value is null)
            {
                target.Remove(name);
            }
            else if (value is JsonObject patchObject)
            {
                var merged = target[name] as JsonObject;
                if (merged is null)
                {
                    merged = new JsonObject();
                    target[name] = merged;
                }

                MergePatch(merged, patchObject);
            }
            else
            {
                target[name] = value.DeepClone();
            }
        }
    }

    /// <summary>The path of the member a JSON error is about (<c>$.team[0].role</c> becomes <c>team[0].role</c>); <c>content</c> for the document itself.</summary>
    private static string FieldOf(JsonException exception)
    {
        var path = exception.Path ?? string.Empty;
        if (path.StartsWith("$.", StringComparison.Ordinal))
            path = path[2..];
        else if (path == "$")
            path = string.Empty;

        return path.Length == 0 || path.Length > 100 ? "content" : path;
    }

    // ─── Validation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The document with every text cleaned and every field checked against the contract (limits, formats, the colors, the images, the
    /// services); an incomplete document is fine (a draft). Every field at fault is collected, so the editor can mark them all.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="ShowcaseContentErrors.Invalid"/>, with <c>fields</c>.</exception>
    public static ShowcaseContentDocument Normalize(ShowcaseContentDocument document, ShowcaseContentContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var checker = new Checker(context);
        var normalized = checker.Run(Complete(document));
        if (checker.Fields.Count > 0)
            throw ShowcaseContentErrors.InvalidFields(checker.Fields, checker.SuggestedColor);

        return normalized;
    }

    /// <summary>Replaces the lists and objects a JSON <c>null</c> may have left null with the empty ones.</summary>
    private static ShowcaseContentDocument Complete(ShowcaseContentDocument document) => document with
    {
        Team = document.Team ?? [],
        Values = document.Values ?? [],
        Certs = document.Certs ?? [],
        Faq = document.Faq ?? [],
        Gallery = document.Gallery ?? [],
        Prep = document.Prep ?? [],
        Sections = document.Sections ?? new ShowcaseSections(),
        ServiceOrder = document.ServiceOrder ?? [],
    };

    private sealed class Checker(ShowcaseContentContext context)
    {
        public List<string> Fields { get; } = [];

        public string? SuggestedColor { get; private set; }

        public ShowcaseContentDocument Run(ShowcaseContentDocument document) => new()
        {
            Style = Choice(document.Style, ShowcaseStyles.All, "style"),
            Font = Choice(document.Font, ShowcaseFonts.All, "font"),
            Color = Color(document.Color),
            Logo = Logo(document.Logo),
            Cover = Cover(document.Cover),
            Tagline = Line(document.Tagline, ShowcaseContentLimits.TaglineMax, "tagline"),
            Intro = Line(document.Intro, ShowcaseContentLimits.IntroMax, "intro"),
            Bio = Text(document.Bio, ShowcaseContentLimits.BioMax, "bio"),
            Team = Items(document.Team, ShowcaseContentLimits.TeamMax, "team", (m, path) => new ShowcaseTeamMember(
                Line(m.Name, ShowcaseContentLimits.TeamNameMax, path + ".name"),
                Line(m.Role, ShowcaseContentLimits.TeamRoleMax, path + ".role"))),
            Values = Strings(document.Values, ShowcaseContentLimits.ValuesMax, ShowcaseContentLimits.ValueMax, "values"),
            Guarantee = Guarantee(document.Guarantee),
            Certs = Items(document.Certs, ShowcaseContentLimits.CertsMax, "certs", (c, path) => new ShowcaseCertification(
                Line(c.Label, ShowcaseContentLimits.CertLabelMax, path + ".label"),
                Line(c.Detail, ShowcaseContentLimits.CertDetailMax, path + ".detail"))),
            Faq = Items(document.Faq, ShowcaseContentLimits.FaqMax, "faq", (f, path) => new ShowcaseFaq(
                Line(f.Q, ShowcaseContentLimits.FaqQuestionMax, path + ".q"),
                Text(f.A, ShowcaseContentLimits.FaqAnswerMax, path + ".a"))),
            Gallery = Items(document.Gallery, ShowcaseContentLimits.GalleryMax, "gallery", Work),
            Prep = Strings(document.Prep, ShowcaseContentLimits.PrepMax, ShowcaseContentLimits.PrepItemMax, "prep"),
            Sections = document.Sections,
            ServiceOrder = ServiceOrder(document.ServiceOrder),
        };

        private string? Choice(string? value, IReadOnlyList<string> allowed, string field)
        {
            var text = ShowcaseText.Line(value)?.ToLowerInvariant();
            if (text is null)
                return null;

            if (allowed.Contains(text))
                return text;

            Fields.Add(field);
            return null;
        }

        private string? Color(string? value)
        {
            var text = ShowcaseText.Line(value);
            if (text is null)
                return null;

            if (!ShowcaseColor.TryNormalize(text, out var hex))
            {
                Fields.Add("color");
                return null;
            }

            if (!ShowcaseColor.IsReadableWithWhite(hex))
            {
                Fields.Add("color");
                SuggestedColor = ShowcaseColor.Darkened(hex);
                return null;
            }

            return hex;
        }

        private ShowcaseLogo? Logo(ShowcaseLogo? logo)
        {
            if (logo is null)
                return null;

            var mode = Choice(logo.Mode, ShowcaseLogoModes.All, "logo.mode");
            var url = Url(logo.Url, "logo.url");
            if (mode is null && url is null)
                return null;

            if (mode is null)
            {
                Fields.Add("logo.mode");
                return null;
            }

            // Only an image logo has a file: switching to the initials or the name leaves the old file behind.
            return new ShowcaseLogo(mode, mode == ShowcaseLogoModes.Image ? url : null);
        }

        private ShowcaseCover? Cover(ShowcaseCover? cover)
        {
            if (cover is null)
                return null;

            var url = Url(cover.Url, "cover.url");
            var alt = Line(cover.Alt, ShowcaseContentLimits.AltMax, "cover.alt");
            return url is null && alt is null ? null : new ShowcaseCover(url, alt);
        }

        private ShowcaseGuarantee? Guarantee(ShowcaseGuarantee? guarantee)
        {
            if (guarantee is null)
                return null;

            var title = Line(guarantee.Title, ShowcaseContentLimits.GuaranteeTitleMax, "guarantee.title");
            var text = Line(guarantee.Text, ShowcaseContentLimits.GuaranteeTextMax, "guarantee.text");
            return title is null && text is null ? null : new ShowcaseGuarantee(title, text);
        }

        private ShowcaseWork Work(ShowcaseWork work, string path)
        {
            var photo = Url(work.PhotoUrl, path + ".photoUrl");
            var before = Url(work.BeforeUrl, path + ".beforeUrl");

            // A before/after needs two photos.
            if (before is not null && photo is null)
                Fields.Add(path + ".photoUrl");

            return new ShowcaseWork(
                photo,
                before,
                Line(work.Caption, ShowcaseContentLimits.CaptionMax, path + ".caption"),
                Line(work.Place, ShowcaseContentLimits.PlaceMax, path + ".place"),
                Line(work.Alt, ShowcaseContentLimits.AltMax, path + ".alt"));
        }

        private List<string> ServiceOrder(IReadOnlyList<string> slugs)
        {
            if (slugs.Count > ShowcaseContentLimits.ServiceOrderMax)
                Fields.Add("serviceOrder");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>(slugs.Count);
            for (var i = 0; i < slugs.Count && i < ShowcaseContentLimits.ServiceOrderMax; i++)
            {
                var slug = ShowcaseText.Line(slugs[i])?.ToLowerInvariant();

                // Only an active service of the supplier is shown, and once.
                if (slug is null || !context.ActiveServiceSlugs.Contains(slug) || !seen.Add(slug))
                {
                    Fields.Add($"serviceOrder[{i}]");
                    continue;
                }

                result.Add(slug);
            }

            return result;
        }

        private string? Url(string? value, string field)
        {
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;

            if (text.Length > ShowcaseContentLimits.UrlMax
                || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http")
                || !ImageExtensions.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase)
                || !context.IsOwnImageUrl(text))
            {
                Fields.Add(field);
                return null;
            }

            return text;
        }

        private string? Line(string? value, int max, string field) => Checked(ShowcaseText.Line(value), max, field);

        private string? Text(string? value, int max, string field) => Checked(ShowcaseText.Paragraphs(value), max, field);

        private string? Checked(string? text, int max, string field)
        {
            if (text is null)
                return null;

            if (text.Length > max || ShowcaseText.LooksLikeMarkup(text))
            {
                Fields.Add(field);
                return null;
            }

            return text;
        }

        private List<string> Strings(IReadOnlyList<string> values, int maxItems, int maxLength, string field)
        {
            if (values.Count > maxItems)
                Fields.Add(field);

            var result = new List<string>(values.Count);
            for (var i = 0; i < values.Count && i < maxItems; i++)
                result.Add(Line(values[i], maxLength, $"{field}[{i}]") ?? string.Empty);

            return result;
        }

        private List<T> Items<T>(IReadOnlyList<T> items, int max, string field, Func<T, string, T> check)
            where T : class
        {
            if (items.Count > max)
                Fields.Add(field);

            var result = new List<T>(items.Count);
            for (var i = 0; i < items.Count && i < max; i++)
            {
                if (items[i] is null)
                {
                    Fields.Add($"{field}[{i}]");
                    continue;
                }

                result.Add(check(items[i], $"{field}[{i}]"));
            }

            return result;
        }
    }

    // ─── Publication ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The draft without its empty rows (a team member with neither name nor role, a blank value, an empty question...): what the
    /// customers would read, and what is compared with the published version to tell what a publication changes.
    /// </summary>
    public static ShowcaseContentDocument Cleaned(ShowcaseContentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var complete = Complete(document);

        return complete with
        {
            Team = complete.Team.Where(m => m is not null && (Has(m.Name) || Has(m.Role))).ToList(),
            Values = complete.Values.Where(Has).ToList(),
            Certs = complete.Certs.Where(c => c is not null && (Has(c.Label) || Has(c.Detail))).ToList(),
            Faq = complete.Faq.Where(f => f is not null && (Has(f.Q) || Has(f.A))).ToList(),
            Gallery = complete.Gallery
                .Where(w => w is not null && (Has(w.PhotoUrl) || Has(w.BeforeUrl) || Has(w.Caption) || Has(w.Place) || Has(w.Alt)))
                .ToList(),
            Prep = complete.Prep.Where(Has).ToList(),
        };
    }

    /// <summary>
    /// The document as it is published: <see cref="Cleaned"/>, with the rows that are half filled refused and the services reduced to the
    /// ones that are active now (a service paused since the draft was saved is not shown). The draft was already validated when it was
    /// saved.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="ShowcaseContentErrors.Invalid"/>, with the incomplete fields.</exception>
    public static ShowcaseContentDocument ForPublication(ShowcaseContentDocument document, IReadOnlyCollection<string> activeServiceSlugs)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(activeServiceSlugs);
        var complete = Complete(document);

        var fields = new List<string>();
        for (var i = 0; i < complete.Team.Count; i++)
        {
            var member = complete.Team[i];
            if (member is not null && Has(member.Role) && !Has(member.Name))
                fields.Add($"team[{i}].name");
        }

        for (var i = 0; i < complete.Certs.Count; i++)
        {
            var cert = complete.Certs[i];
            if (cert is not null && Has(cert.Detail) && !Has(cert.Label))
                fields.Add($"certs[{i}].label");
        }

        for (var i = 0; i < complete.Faq.Count; i++)
        {
            var faq = complete.Faq[i];
            if (faq is null || (!Has(faq.Q) && !Has(faq.A)))
                continue;
            if (!Has(faq.Q))
                fields.Add($"faq[{i}].q");
            if (!Has(faq.A))
                fields.Add($"faq[{i}].a");
        }

        for (var i = 0; i < complete.Gallery.Count; i++)
        {
            var work = complete.Gallery[i];
            if (work is not null && !Has(work.PhotoUrl)
                && (Has(work.BeforeUrl) || Has(work.Caption) || Has(work.Place) || Has(work.Alt)))
                fields.Add($"gallery[{i}].photoUrl");
        }

        if (complete.Guarantee is { } guarantee && Has(guarantee.Title) != Has(guarantee.Text))
            fields.Add(Has(guarantee.Title) ? "guarantee.text" : "guarantee.title");

        if (complete.Logo is { Mode: ShowcaseLogoModes.Image } logo && !Has(logo.Url))
            fields.Add("logo.url");

        if (fields.Count > 0)
            throw ShowcaseContentErrors.InvalidFields(fields);

        var cleaned = Cleaned(complete);
        return cleaned with
        {
            ServiceOrder = cleaned.ServiceOrder.Where(activeServiceSlugs.Contains).ToList(),
        };
    }

    /// <summary>
    /// The members of the contract in which the draft (cleaned) differs from <paramref name="published"/> (nothing published = the empty
    /// document), in the order of the contract: "cosa cambia pubblicando".
    /// </summary>
    public static IReadOnlyList<string> Changes(ShowcaseContentDocument? published, ShowcaseContentDocument draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var before = Cleaned(published ?? new ShowcaseContentDocument());
        var after = Cleaned(draft);

        var changes = new List<string>();
        void Compare<T>(string name, Func<ShowcaseContentDocument, T> member)
        {
            if (!string.Equals(Serialize(member(before)), Serialize(member(after)), StringComparison.Ordinal))
                changes.Add(name);
        }

        Compare("style", d => d.Style);
        Compare("font", d => d.Font);
        Compare("color", d => d.Color);
        Compare("logo", d => d.Logo);
        Compare("cover", d => d.Cover);
        Compare("tagline", d => d.Tagline);
        Compare("intro", d => d.Intro);
        Compare("bio", d => d.Bio);
        Compare("team", d => d.Team);
        Compare("values", d => d.Values);
        Compare("guarantee", d => d.Guarantee);
        Compare("certs", d => d.Certs);
        Compare("faq", d => d.Faq);
        Compare("gallery", d => d.Gallery);
        Compare("prep", d => d.Prep);
        Compare("sections", d => d.Sections);
        Compare("serviceOrder", d => d.ServiceOrder);
        return changes;
    }

    /// <summary>The state the editor shows (never stored).</summary>
    public static ShowcaseContentState StateOf(bool published, bool paused, bool hasChanges)
    {
        if (!published)
            return ShowcaseContentState.Draft;

        if (paused)
            return ShowcaseContentState.Paused;

        return hasChanges ? ShowcaseContentState.ChangesReady : ShowcaseContentState.Online;
    }

    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, StoredJson);
}
