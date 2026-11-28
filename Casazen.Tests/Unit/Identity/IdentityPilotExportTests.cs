using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Identity;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E73/F01/US03/T02 (NF-196, decision D10): the CSV of the pilot, one row for each identity step opened in the period with the shadow decision of the policy next to the decision
/// of a person. The header is the contract; the file holds ids, codes, counts and times from closed alphabets and nothing else (no name, e-mail, document number, image, embedding or
/// score of a person, however it was stored); a cell a spreadsheet could read as a formula is neutralized; the scope, the period rule and the gate are those of the metrics; the
/// file adds no entry to the activity log and its content is never logged.
/// </summary>
public sealed class IdentityPilotExportTests
{
    private const string Header =
        "attempt_ref,property_id,opened_at,closed_at,format_id,channel,outcome_code,decision_mode,shadow_outcome,reason_codes,guided_retries,human_outcome,human_decided_at,review_seconds,duration_ms,policy_version,policy_hash_prefix";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly Dictionary<string, Regex> Alphabet = new(StringComparer.Ordinal)
    {
        ["attempt_ref"] = new("^[0-9a-f]{16}$"),
        ["property_id"] = new("^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$"),
        ["opened_at"] = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$"),
        ["closed_at"] = new(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)?$"),
        ["format_id"] = new("^[a-z0-9._-]+$"),
        ["channel"] = new("^web:[a-z-]+$"),
        ["outcome_code"] = new("^(auto_approved|review|assisted|consent_refused|format_unsupported|technical_error|unavailable|minor_excluded|abandoned|in_progress)$"),
        ["decision_mode"] = new("^(observation|automatic)?$"),
        ["shadow_outcome"] = new("^(approve|review)?$"),
        ["reason_codes"] = new(@"^([a-z0-9_]+:[a-z0-9_]+(\|[a-z0-9_]+:[a-z0-9_]+)*)?$"),
        ["guided_retries"] = new(@"^\d+$"),
        ["human_outcome"] = new("^(approve|reject)?$"),
        ["human_decided_at"] = new(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)?$"),
        ["review_seconds"] = new(@"^(\d+(\.\d{1,3})?)?$"),
        ["duration_ms"] = new(@"^\d*$"),
        ["policy_version"] = new("^[A-Za-z0-9._-]*$"),
        ["policy_hash_prefix"] = new("^[0-9a-f]{0,12}$"),
    };

    private static IdentityPilotExport Export(AppDbContext db) =>
        new(db, new FakeTimeProvider(IdentityMetricsWorld.Now), Options.Create(new IdentityVerificationOptions()));

    private static async Task<string> TextAsync(IdentityMetricsWorld world, HostScope? scope = null)
    {
        await world.SaveAsync();
        await using var db = world.NewDb();
        var file = await Export(db).OpenAsync(scope ?? world.OrgWide, IdentityMetricsWorld.From, IdentityMetricsWorld.To, Ct);
        return string.Concat(file.Lines());
    }

    private static List<List<string>> Parse(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var character = csv[i];
            if (quoted)
            {
                if (character != '"')
                    cell.Append(character);
                else if (i + 1 < csv.Length && csv[i + 1] == '"')
                    cell.Append(csv[++i]);
                else
                    quoted = false;
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (character == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else if (character != '\r')
            {
                cell.Append(character);
            }
        }

        Assert.False(quoted, "a quote is left open");
        Assert.Empty(row);
        Assert.Equal(0, cell.Length);
        return rows;
    }

    private static List<Dictionary<string, string>> Rows(string csv)
    {
        var table = Parse(csv);
        Assert.Equal(Header, string.Join(',', table[0]));
        Assert.All(table, row => Assert.Equal(table[0].Count, row.Count));
        return table.Skip(1).Select(row => table[0].Zip(row).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal)).ToList();
    }

    [Fact]
    public async Task IdentityPilotExport_TheHeaderRowIsExact_AndAnEmptyPeriodIsTheHeaderAlone()
    {
        Assert.Equal(Header, IdentityPilotExport.Header);
        Assert.Equal(17, IdentityPilotExport.Columns.Count);
        Assert.Equal(IdentityPilotExport.Columns.Count, IdentityPilotExport.Columns.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(IdentityPilotExport.Columns.Order(StringComparer.Ordinal), Alphabet.Keys.Order(StringComparer.Ordinal));

        var text = await TextAsync(new IdentityMetricsWorld());

        Assert.Equal(Header + "\r\n", text);
    }

    [Fact]
    public async Task IdentityPilotExport_OneRowForEachStepOpened_WithTheCellsOfTheContract()
    {
        var world = new IdentityMetricsWorld();
        var reviewedStay = world.AddStay(world.GrantedPropertyId);
        var reviewed = world.AddSession(reviewedStay, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.Review, guidedRetries: 1);
        world.AddDecision(
            reviewedStay,
            IdentityMetricsWorld.At(10, 10, 5),
            BiometricOutcome.Escalate,
            BiometricOutcome.Accept,
            durationMs: 1200,
            reasonsJson: "[{\"check\":\"presence_challenge\",\"result\":\"passed\",\"value\":0.99,\"threshold\":0.5},{\"check\":\"liveness\",\"result\":\"passed\",\"value\":0.93,\"threshold\":0.7},"
                         + "{\"check\":\"similarity\",\"result\":\"between_thresholds\",\"value\":0.78,\"threshold\":0.85},{\"check\":\"document_coherence\",\"result\":\"passed\"},"
                         + "{\"check\":\"observation_mode\",\"result\":\"segment_observation\"}]");
        world.AddOperator(reviewedStay, IdentityMetricsWorld.At(10, 10, 35), BiometricOutcome.Reject, note: "non corrisponde");

        var automaticStay = world.AddStay(world.GrantedPropertyId);
        var automatic = world.AddSession(automaticStay, IdentityMetricsWorld.At(11), IdentityMetricsWorld.At(11, 10, 5), IdentityOutcomeCodes.AutoApproved, channel: IdentityChannels.AndroidChrome);
        world.AddDecision(
            automaticStay,
            IdentityMetricsWorld.At(11, 10, 5),
            BiometricOutcome.Accept,
            BiometricOutcome.Accept,
            IdentityDecisionMode.Automatic,
            durationMs: 800,
            reasonsJson: "[{\"check\":\"liveness\",\"result\":\"passed\",\"value\":0.91,\"threshold\":0.7}]",
            channel: IdentityChannels.AndroidChrome);

        var refusedStay = world.AddStay(world.GrantedPropertyId);
        var refused = world.AddSession(refusedStay, IdentityMetricsWorld.At(12), IdentityMetricsWorld.At(12, 10, 1), IdentityOutcomeCodes.ConsentRefused, formatId: null, channel: null);
        var abandoned = world.AddSession(world.AddStay(world.GrantedPropertyId), IdentityMetricsWorld.Now.UtcDateTime.AddDays(-2));
        var inProgress = world.AddSession(world.AddStay(world.GrantedPropertyId), IdentityMetricsWorld.Now.UtcDateTime.AddHours(-1));

        world.AddSession(world.AddStay(world.GrantedPropertyId), IdentityMetricsWorld.From.AddSeconds(-1), IdentityMetricsWorld.From.AddMinutes(5), IdentityOutcomeCodes.Review);
        world.AddSession(world.AddStay(world.ForeignPropertyId), IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.Review);

        var text = await TextAsync(world);
        var rows = Rows(text);

        Assert.Equal(5, rows.Count);
        Assert.Equal(
            new[] { reviewed.Id, automatic.Id, refused.Id, abandoned.Id, inProgress.Id }.Select(id => IdentityPilotExport.AttemptRef(id, world.OrgId)),
            rows.Select(row => row["attempt_ref"]));

        var review = rows[0];
        Assert.Matches("^[0-9a-f]{16}$", review["attempt_ref"]);
        Assert.Equal(world.GrantedPropertyId.ToString("D"), review["property_id"]);
        Assert.Equal(("2026-11-10T10:00:00.000Z", "2026-11-10T10:05:00.000Z"), (review["opened_at"], review["closed_at"]));
        Assert.Equal(("it-cie-3.0", "web:ios-safari", "review"), (review["format_id"], review["channel"], review["outcome_code"]));
        Assert.Equal(("observation", "approve", "similarity:between_thresholds|observation_mode:segment_observation", "1"), (review["decision_mode"], review["shadow_outcome"], review["reason_codes"], review["guided_retries"]));
        Assert.Equal(("reject", "2026-11-10T10:35:00.000Z", "1800", "1200"), (review["human_outcome"], review["human_decided_at"], review["review_seconds"], review["duration_ms"]));
        Assert.Equal((IdentityMetricsWorld.PolicyVersion, IdentityMetricsWorld.PolicyHash[..12]), (review["policy_version"], review["policy_hash_prefix"]));

        var auto = rows[1];
        Assert.Equal(("auto_approved", "web:android-chrome", "automatic", "approve", string.Empty), (auto["outcome_code"], auto["channel"], auto["decision_mode"], auto["shadow_outcome"], auto["reason_codes"]));
        Assert.Equal((string.Empty, string.Empty, string.Empty, "800"), (auto["human_outcome"], auto["human_decided_at"], auto["review_seconds"], auto["duration_ms"]));

        var refusal = rows[2];
        Assert.Equal(("consent_refused", "unknown", "web:other", "2026-11-12T10:01:00.000Z"), (refusal["outcome_code"], refusal["format_id"], refusal["channel"], refusal["closed_at"]));
        Assert.All(
            new[] { "decision_mode", "shadow_outcome", "reason_codes", "human_outcome", "human_decided_at", "review_seconds", "duration_ms", "policy_version", "policy_hash_prefix" },
            column => Assert.Equal(string.Empty, refusal[column]));

        Assert.Equal(("abandoned", string.Empty), (rows[3]["outcome_code"], rows[3]["closed_at"]));
        Assert.Equal(("in_progress", string.Empty), (rows[4]["outcome_code"], rows[4]["closed_at"]));

        Assert.Equal(IdentityPilotExport.AttemptRef(reviewed.Id, world.OrgId), IdentityPilotExport.AttemptRef(reviewed.Id, world.OrgId));
        Assert.NotEqual(IdentityPilotExport.AttemptRef(reviewed.Id, world.OrgId), IdentityPilotExport.AttemptRef(reviewed.Id, world.OtherOrgId));
        Assert.DoesNotContain(reviewed.Id.ToString("N")[..8], text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IdentityPilotExport_TheRowsAreTheStepsOfTheCounters_ForTheSameScope()
    {
        var world = new IdentityMetricsWorld();
        foreach (var property in new[] { world.GrantedPropertyId, world.HiddenPropertyId, world.ForeignPropertyId })
        {
            var reviewed = world.AddStay(property);
            world.AddSession(reviewed, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.Review);
            world.AddDecision(reviewed, IdentityMetricsWorld.At(10, 10, 5), BiometricOutcome.Escalate, BiometricOutcome.Escalate);
            world.AddOperator(reviewed, IdentityMetricsWorld.At(10, 10, 35), BiometricOutcome.Accept);

            var automatic = world.AddStay(property);
            world.AddSession(automatic, IdentityMetricsWorld.At(11), IdentityMetricsWorld.At(11, 10, 5), IdentityOutcomeCodes.AutoApproved);
            world.AddDecision(automatic, IdentityMetricsWorld.At(11, 10, 5), BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic);

            world.AddSession(world.AddStay(property), IdentityMetricsWorld.Now.UtcDateTime.AddHours(-1));
        }

        world.AddSession(world.AddStay(world.GrantedPropertyId), IdentityMetricsWorld.At(13), IdentityMetricsWorld.At(13, 10, 5), IdentityOutcomeCodes.AutoApproved, orgId: world.OtherOrgId);
        await world.SaveAsync();

        (HostScope Scope, int Properties, Guid[] Allowed)[] scopes =
        [
            (world.OrgWide, 2, [world.GrantedPropertyId, world.HiddenPropertyId]),
            (world.Restricted, 1, [world.GrantedPropertyId]),
            (world.OwnedByTheOwnerOfTheHiddenProperty, 1, [world.HiddenPropertyId]),
            (new HostScope(world.OtherOrgId), 1, [world.ForeignPropertyId]),
        ];
        foreach (var (scope, properties, allowed) in scopes)
        {
            await using var db = world.NewDb();
            var text = string.Concat((await Export(db).OpenAsync(scope, IdentityMetricsWorld.From, IdentityMetricsWorld.To, Ct)).Lines());
            var metrics = await new IdentityVerificationMetricsService(db, new FakeTimeProvider(IdentityMetricsWorld.Now), Options.Create(new IdentityVerificationOptions()))
                .GetAsync(scope, IdentityMetricsWorld.From, IdentityMetricsWorld.To, Ct);
            var rows = Rows(text);

            Assert.Equal(3 * properties, rows.Count);
            Assert.Equal(metrics.Opened, rows.Count);
            Assert.All(rows, row => Assert.Contains(Guid.Parse(row["property_id"]), allowed));
            Assert.Equal(allowed.Length, rows.Select(row => row["property_id"]).Distinct(StringComparer.Ordinal).Count());
            foreach (var category in IdentityOutcomeCategories.All)
                Assert.Equal(metrics.Outcomes.Of(category), rows.Count(row => row["outcome_code"] == IdentityOutcomeCategories.CodeOf(category)));
        }
    }

    /// <summary>
    /// Proof (privacy): the file has no personal data. A guest with a name, an e-mail and a document number (on the guest and on the stay guest), a note of the operator with a
    /// name and a telephone number in it, and reasons that carry the scores of the person (and one that carries a name where a code should be) are all in the database; none of
    /// them is in the file. Every cell of every row belongs to the closed alphabet of its column, so nothing else can be in it either.
    /// </summary>
    [Fact]
    public async Task IdentityPilotExport_ContainsNoPersonalData()
    {
        var world = new IdentityMetricsWorld();
        var stay = world.AddStay(
            world.GrantedPropertyId,
            firstName: "Giulietta",
            lastName: "Capuleti",
            email: "giulietta.capuleti@example.com",
            documentNumber: "CA00000AA");
        world.Add(new StayGuest { BookingId = stay, OrgId = world.OrgId, Position = 1, FirstName = "Romeo", LastName = "Montecchi", DocumentNumber = "AX1234567" });
        var session = world.AddSession(stay, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.Review);
        var decision = world.AddDecision(
            stay,
            IdentityMetricsWorld.At(10, 10, 5),
            BiometricOutcome.Escalate,
            BiometricOutcome.Escalate,
            reasonsJson: "[{\"check\":\"similarity\",\"result\":\"failed\",\"value\":0.8421,\"threshold\":0.85},{\"check\":\"Giulietta Capuleti\",\"result\":\"CA00000AA\"},"
                         + "{\"check\":\"liveness\",\"result\":\"failed\",\"value\":0.6177,\"threshold\":0.7},{\"check\":\"processing\",\"result\":\"error\"}]");
        decision.ThresholdsJson = "{\"accept\":0.9137,\"escalate\":0.7771}";
        var person = world.AddOperator(stay, IdentityMetricsWorld.At(10, 10, 35), BiometricOutcome.Reject, note: "Il volto non corrisponde a Giulietta Capuleti, telefonare al +39 333 1234567");
        await world.SaveAsync();

        string text;
        await using (var db = world.NewDb())
            text = string.Concat((await Export(db).OpenAsync(world.OrgWide, IdentityMetricsWorld.From, IdentityMetricsWorld.To, Ct)).Lines());

        foreach (var forbidden in new[]
                 {
                     "Giulietta", "Capuleti", "Romeo", "Montecchi", "example.com", "giulietta", "CA00000AA", "AX1234567", "volto", "telefonare", "+39", "1234567",
                     "0.8421", "0.6177", "0.9137", "0.7771", "0.85", "auth0|", person.OperatorUserId!,
                     session.Id.ToString("D"), session.BookingId.ToString("D"), decision.GuestId.ToString("D"), session.Id.ToString("N"), decision.Id.ToString("D"), decision.IdempotencyKey!,
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }

        var row = Assert.Single(Rows(text));
        Assert.Equal("similarity:failed|liveness:failed|processing:error", row["reason_codes"]);

        foreach (var cells in Rows(text))
        {
            foreach (var (column, value) in cells)
                Assert.Matches(Alphabet[column], value);
        }
    }

    [Fact]
    public async Task IdentityPilotExport_AnAbandonedStepClosedAfterALaterDecision_HasNoDecision_AndTheLaterStepKeepsItsOwn()
    {
        var world = new IdentityMetricsWorld();
        var (abandoned, later, _) = world.AddAbandonedBeforeALaterDecision(world.GrantedPropertyId);

        var rows = Rows(await TextAsync(world));

        var first = Assert.Single(rows, row => row["attempt_ref"] == IdentityPilotExport.AttemptRef(abandoned.Id, world.OrgId));
        var second = Assert.Single(rows, row => row["attempt_ref"] == IdentityPilotExport.AttemptRef(later.Id, world.OrgId));
        Assert.Equal(("abandoned", "unknown", "web:ios-safari", "2026-11-10T10:40:00.000Z"), (first["outcome_code"], first["format_id"], first["channel"], first["closed_at"]));
        Assert.All(
            new[] { "decision_mode", "shadow_outcome", "reason_codes", "human_outcome", "human_decided_at", "review_seconds", "duration_ms", "policy_version", "policy_hash_prefix" },
            column => Assert.Equal(string.Empty, first[column]));
        Assert.Equal(("review", "it-cie-3.0", "web:android-chrome"), (second["outcome_code"], second["format_id"], second["channel"]));
        Assert.Equal(
            ("observation", "approve", "approve", "2026-11-10T10:35:00.000Z", "1800", "1100"),
            (second["decision_mode"], second["shadow_outcome"], second["human_outcome"], second["human_decided_at"], second["review_seconds"], second["duration_ms"]));
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "\"'\rcmd\"")]
    [InlineData("=SUM(A1,B1)", "\"'=SUM(A1,B1)\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("line one\nline two", "\"line one\nline two\"")]
    [InlineData("it-cie-3.0", "it-cie-3.0")]
    [InlineData("x=1+1", "x=1+1")]
    [InlineData("'=already", "'=already")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void IdentityPilotExport_Cell_NeutralizesFormulas_AndQuotesWhatNeedsIt(string? value, string expected)
    {
        Assert.Equal(expected, IdentityPilotExport.Cell(value));
    }

    /// <summary>
    /// The file neutralizes what the one rule of the files the host downloads says (<see cref="CsvWriter.StartsLikeAFormula"/>) and nothing else, whatever the first character is: for
    /// each of the 256 first characters, the cell read back as a spreadsheet would is the value with a leading apostrophe when the rule says so and the value as it is otherwise (the
    /// quoting of RFC 4180 never changes it); and the characters the rule neutralizes are the six of the contract: <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab and a carriage return.
    /// </summary>
    [Fact]
    public void IdentityPilotExport_Cell_FollowsTheOneRuleOfTheFilesTheHostDownloads_ForEveryFirstCharacter()
    {
        var neutralized = new List<char>();
        for (var code = 0; code < 256; code++)
        {
            var first = (char)code;
            var value = first + "x1";
            var rule = CsvWriter.StartsLikeAFormula(value);

            var readBack = Assert.Single(Assert.Single(Parse(IdentityPilotExport.Cell(value) + "\r\n")));

            Assert.Equal(rule ? "'" + value : value, readBack);
            if (rule)
                neutralized.Add(first);
        }

        Assert.Equal(new[] { '\t', '\r', '+', '-', '=', '@' }, neutralized);
    }

    [Fact]
    public async Task IdentityPilotExport_NeutralizesFormulas()
    {
        var world = new IdentityMetricsWorld();
        var hostile = new[] { "=HYPERLINK(\"http://example.invalid\",\"x\")", "@evil", "+1+1", "-2+3", "\tTAB", "\rCR" };
        for (var i = 0; i < hostile.Length; i++)
        {
            var stay = world.AddStay(world.GrantedPropertyId);
            var at = IdentityMetricsWorld.At(10 + i, 10, 5);
            world.AddSession(stay, at.AddMinutes(-5), at, IdentityOutcomeCodes.Review, formatId: hostile[i], channel: hostile[(i + 1) % hostile.Length]);
            var row = world.AddDecision(stay, at, BiometricOutcome.Escalate, BiometricOutcome.Escalate);
            row.PolicyVersion = hostile[(i + 2) % hostile.Length];
        }

        var text = await TextAsync(world);
        var table = Parse(text);

        Assert.Equal(hostile.Length + 1, table.Count);
        foreach (var cell in table.Skip(1).SelectMany(row => row))
            Assert.True(cell.Length == 0 || "=+-@\t\r".IndexOf(cell[0], StringComparison.Ordinal) < 0, $"a cell starts like a formula: {cell}");

        var formats = table.Skip(1).Select(row => row[4]).ToList();
        foreach (var value in hostile)
            Assert.Contains("'" + value, formats.Concat(table.Skip(1).Select(row => row[5])).Concat(table.Skip(1).Select(row => row[15])));

        Assert.Contains("\"'=HYPERLINK(\"\"http://example.invalid\"\",\"\"x\"\")\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportPilot_IsUnderThePolicyOfTheMetrics_BehindBothFlags()
    {
        var method = typeof(VerificationController).GetMethod(nameof(VerificationController.ExportPilot))!;
        var metrics = typeof(VerificationController).GetMethod(nameof(VerificationController.GetMetrics))!;

        Assert.Equal("metrics/export.csv", Assert.Single(method.GetCustomAttributes<HttpGetAttribute>()).Template);
        Assert.Equal(
            new[] { FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityVerification }.Order(),
            method.GetCustomAttributes<FeatureGateAttribute>().Select(gate => gate.Flag).Order());
        Assert.Equal([CasazenPolicies.VerificationDecision], typeof(VerificationController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(attribute => attribute.Policy));
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.DoesNotContain(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true), attribute => attribute.Policy != CasazenPolicies.VerificationDecision);

        Assert.Equal(
            metrics.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(attribute => attribute.Policy).Order(),
            method.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(attribute => attribute.Policy).Order());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task ExportPilot_WithAFlagOff_AnswersLikeARouteThatDoesNotExist(bool verification, bool selfHosted, bool reached)
    {
        var flags = new TestFeatureFlags();
        flags.Set(FeatureFlags.CheckInIdentityVerification, verification);
        flags.Set(FeatureFlags.CheckInIdentitySelfHosted, selfHosted);
        var next = 0;
        var middleware = new FeatureGateMiddleware(
            _ =>
            {
                next++;
                return Task.CompletedTask;
            },
            flags,
            NullLogger<FeatureGateMiddleware>.Instance);
        var gates = typeof(VerificationController).GetMethod(nameof(VerificationController.ExportPilot))!.GetCustomAttributes<FeatureGateAttribute>().Cast<object>().ToArray();
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(gates), "pilot-export"));

        await middleware.InvokeAsync(context);

        Assert.Equal(reached ? 1 : 0, next);
        Assert.Equal(reached ? StatusCodes.Status200OK : StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExportPilot_ADirectCallWithAFlagOff_Is404_AndReadsNothing(bool verification, bool selfHosted)
    {
        var flags = new List<string>();
        if (verification)
            flags.Add(FeatureFlags.CheckInIdentityVerification);
        if (selfHosted)
            flags.Add(FeatureFlags.CheckInIdentitySelfHosted);

        var world = new IdentityMetricsWorld();
        var harness = new VerificationEndpointHarness(world.OrgWide, [.. flags]);
        var db = world.NewDb();
        await db.DisposeAsync();

        var answer = await harness.Controller.ExportPilot(null, null, harness.Flags, Export(db), Ct);

        Assert.IsType<NotFoundResult>(answer);
        harness.Scopes.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("2025-11-27", "2026-11-27", true)]
    [InlineData("2025-11-26", "2026-11-27", false)]
    [InlineData("2026-11-28", "2026-11-27", false)]
    public async Task ExportPilot_ThePeriodRule_IsTheOneOfTheMetrics(string from, string to, bool allowed)
    {
        var world = new IdentityMetricsWorld();
        var harness = new VerificationEndpointHarness(world.OrgWide, FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted);
        await world.SaveAsync();
        await using var db = world.NewDb();

        var answer = await harness.Controller.ExportPilot(
            DateOnly.Parse(from, CultureInfo.InvariantCulture),
            DateOnly.Parse(to, CultureInfo.InvariantCulture),
            harness.Flags,
            Export(db),
            Ct);

        if (allowed)
        {
            var (response, body) = await VerificationEndpointHarness.ExecuteAsync(answer);
            Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
            Assert.NotEmpty(body);
            return;
        }

        var problem = Assert.IsType<ObjectResult>(answer);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("verification_metrics_period_invalid", Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    [Fact]
    public async Task ExportPilot_WithoutAScope_Is401_AndReadsNothing()
    {
        var world = new IdentityMetricsWorld();
        var harness = new VerificationEndpointHarness(null, FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted);
        var db = world.NewDb();
        await db.DisposeAsync();

        var answer = await harness.Controller.ExportPilot(null, null, harness.Flags, Export(db), Ct);

        Assert.IsType<UnauthorizedResult>(answer);
    }

    /// <summary>
    /// The answer: <c>text/csv; charset=utf-8</c> with a byte order mark, RFC 4180 with CRLF, the name of the file from the first and the last day of the period, never cached and
    /// never sniffed, and the body is the header and the rows of the scope of the caller only. The log has one line, with the user, the count and the period, and no cell; the file
    /// writes no entry of the activity log.
    /// </summary>
    [Fact]
    public async Task ExportPilot_AnswersTheCsvOfTheScope_WithItsHeaders_AndLogsNoContent()
    {
        var world = new IdentityMetricsWorld();
        var granted = world.AddStay(world.GrantedPropertyId, firstName: "Giulietta", lastName: "Capuleti", email: "giulietta.capuleti@example.com");
        var session = world.AddSession(granted, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.Review);
        world.AddDecision(granted, IdentityMetricsWorld.At(10, 10, 5), BiometricOutcome.Escalate, BiometricOutcome.Accept);
        var hidden = world.AddStay(world.HiddenPropertyId);
        world.AddSession(hidden, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.Review);
        await world.SaveAsync();
        var harness = new VerificationEndpointHarness(world.Restricted, FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted);
        await using var db = world.NewDb();

        var answer = await harness.Controller.ExportPilot(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), harness.Flags, Export(db), Ct);
        var (response, body) = await VerificationEndpointHarness.ExecuteAsync(answer);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", response.ContentType);
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"].ToString());
        var disposition = response.Headers.ContentDisposition.ToString();
        Assert.StartsWith("attachment", disposition, StringComparison.Ordinal);
        Assert.Contains("identity-verifications-2026-11-01-2026-11-30.csv", disposition, StringComparison.Ordinal);

        Assert.Equal([0xEF, 0xBB, 0xBF], body[..3]);
        var text = new UTF8Encoding(false).GetString(body, 3, body.Length - 3);
        Assert.StartsWith(Header + "\r\n", text, StringComparison.Ordinal);
        var rows = Rows(text);
        var only = Assert.Single(rows);
        Assert.Equal(IdentityPilotExport.AttemptRef(session.Id, world.OrgId), only["attempt_ref"]);
        Assert.Equal(world.GrantedPropertyId.ToString("D"), only["property_id"]);

        var line = Assert.Single(harness.Log.Entries);
        Assert.Contains(VerificationEndpointHarness.Operator, line.Message, StringComparison.Ordinal);
        Assert.Contains(" 1 rows", line.Message, StringComparison.Ordinal);
        foreach (var secret in new[] { only["attempt_ref"], session.Id.ToString("D"), "Giulietta", "Capuleti", "example.com" })
            Assert.DoesNotContain(secret, harness.Log.AllOutput, StringComparison.OrdinalIgnoreCase);

        Assert.False(await db.OrgActivityEntries.AnyAsync(Ct));
        Assert.DoesNotContain(typeof(IActivityLog), typeof(IdentityPilotExport).GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(typeof(IActivityLog), typeof(VerificationController).GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task IdentityPilotExport_OpenReadsEverything_ThenTheLinesComeOneByOne()
    {
        var world = new IdentityMetricsWorld();
        for (var i = 0; i < 25; i++)
            world.AddSession(world.AddStay(world.GrantedPropertyId), IdentityMetricsWorld.At(10, 10, i % 60), IdentityMetricsWorld.At(10, 11), IdentityOutcomeCodes.Review);

        await world.SaveAsync();
        var db = world.NewDb();
        var file = await Export(db).OpenAsync(world.OrgWide, IdentityMetricsWorld.From, IdentityMetricsWorld.To, Ct);
        await db.DisposeAsync();

        Assert.Equal(25, file.RowCount);
        var lines = file.Lines().ToList();
        Assert.Equal(26, lines.Count);
        Assert.Equal(Header + "\r\n", lines[0]);
        Assert.All(lines, line => Assert.EndsWith("\r\n", line, StringComparison.Ordinal));
        Assert.Equal(Header + "\r\n", file.Lines().First());
    }

    /// <summary>
    /// A date and a time are written with the invariant culture whatever the culture of the process: the name of the file and every instant of the file are the same under a culture
    /// whose calendar is not the Gregorian one (the Buddhist year of Thailand, the Hijri one of Saudi Arabia) and one that writes its digits differently.
    /// </summary>
    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public async Task IdentityPilotExport_DatesAreInvariant_WhateverTheCultureOfTheProcess(string culture)
    {
        var world = new IdentityMetricsWorld();
        var stay = world.AddStay(world.GrantedPropertyId);
        world.AddSession(stay, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.Review);
        world.AddDecision(stay, IdentityMetricsWorld.At(10, 10, 5), BiometricOutcome.Escalate, BiometricOutcome.Accept, durationMs: 1234);
        world.AddOperator(stay, IdentityMetricsWorld.At(10, 10, 35), BiometricOutcome.Accept);
        var invariant = await TextAsync(world);
        var current = CultureInfo.CurrentCulture;
        var currentUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal("identity-verifications-2026-11-01-2026-11-30.csv", IdentityPilotExport.FileName(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));

            await using var db = world.NewDb();
            var text = string.Concat((await Export(db).OpenAsync(world.OrgWide, IdentityMetricsWorld.From, IdentityMetricsWorld.To, Ct)).Lines());
            Assert.Equal(invariant, text);
            Assert.Contains("2026-11-10T10:00:00.000Z", text, StringComparison.Ordinal);
            Assert.Contains(",1800,1234,", text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = current;
            CultureInfo.CurrentUICulture = currentUi;
        }
    }

    [Fact]
    public void AddSelfHostedIdentityVerification_RegistersTheCountersAndTheExporter_Scoped()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(IdentityMetricsWorld.Now));
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase($"identity-metrics-registration-{Guid.NewGuid()}"));
        services.AddSelfHostedIdentityVerification();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        Assert.IsType<IdentityVerificationMetricsService>(scope.ServiceProvider.GetRequiredService<IIdentityVerificationMetricsService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IdentityPilotExport>());
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IIdentityVerificationMetricsService)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IdentityPilotExport)).Lifetime);
    }
}
