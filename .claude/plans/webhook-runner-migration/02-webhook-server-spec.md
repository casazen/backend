# Phase 2 — Casazen.WebhookRunner: Technical Spec

> Updated to incorporate patterns from `agentic-kanban`:
> - JSON rule config (like `config.json`)
> - Context variable `{{PLACEHOLDER}}` injection
> - Session store (JSONL, like `.agent-sessions/`)
> - `--output-format stream-json` for structured output parsing
> - `claude` CLI invoked directly (not via `npx`)
> - Kill agent endpoint

---

## Project Structure

```
Casazen.WebhookRunner/
├── Casazen.WebhookRunner.csproj
├── appsettings.json
├── appsettings.Development.json          ← gitignored, contains secrets
├── Program.cs
├── Config/
│   ├── WebhookRunnerOptions.cs
│   └── RuleConfig.cs                     ← JSON rule definitions (like config.json)
├── Verification/
│   └── GitHubSignatureVerifier.cs
├── Routing/
│   └── GitHubEventRouter.cs              ← config-driven, no hard-coded rules
├── Queue/
│   ├── ClaudeJob.cs
│   ├── IJobQueue.cs
│   ├── InMemoryJobQueue.cs
│   └── JobProcessor.cs
├── Runner/
│   ├── IClaudeCodeRunner.cs
│   └── ClaudeCodeRunner.cs               ← streams stream-json output
└── Sessions/
    ├── ISessionStore.cs
    └── JsonlSessionStore.cs              ← JSONL file store (like .agent-sessions/)
```

---

## Step 1 — Create the Project

```powershell
dotnet new web -n Casazen.WebhookRunner --framework net10.0
dotnet sln Casazen.sln add Casazen.WebhookRunner/Casazen.WebhookRunner.csproj
```

No references to other Casazen projects — standalone.

Add to `.gitignore`:
```
Casazen.WebhookRunner/appsettings.Development.json
Casazen.WebhookRunner/appsettings.Production.json
.agent-sessions/
.ngrok.log
```

---

## Step 2 — Rule Config (config.json pattern from agentic-kanban)

### `casazen-webhook-config.json` (committed, lives at repo root)

```json
{
  "models": {
    "default": "qwen-3.5-122b-sovereign",
    "step3":   "claude-haiku-4-5-20251001",
    "phaseE":  "claude-haiku-4-5-20251001"
  },
  "rules": [
    {
      "event":         "issues",
      "action":        "labeled",
      "label":         "raw-requirement",
      "prompt":        "/step1-refine {{ISSUE_NUMBER}}",
      "model":         "default",
      "tools":         ["Bash", "Read", "Grep", "Glob"],
      "agentTeams":    false,
      "promptCaching": false,
      "description":   "Step 1 — clarification"
    },
    {
      "event":         "issue_comment",
      "action":        "created",
      "authorType":    "User",
      "requiredLabel": "awaiting-clarification",
      "prompt":        "/step1-refine {{ISSUE_NUMBER}} mode=read-answers",
      "model":         "default",
      "tools":         ["Bash", "Read", "Grep", "Glob"],
      "agentTeams":    false,
      "promptCaching": false,
      "description":   "Step 1 — read PO answers"
    },
    {
      "event":         "issues",
      "action":        "labeled",
      "label":         "council-ready",
      "prompt":        "/step1-refine {{ISSUE_NUMBER}} mode=council",
      "model":         "default",
      "tools":         ["Bash", "Read", "Grep", "Glob"],
      "agentTeams":    true,
      "promptCaching": false,
      "description":   "Step 1 — council review"
    },
    {
      "event":         "issues",
      "action":        "labeled",
      "label":         "approved",
      "prompt":        "/step2-dispatch {{ISSUE_NUMBER}}",
      "model":         "default",
      "tools":         ["Bash", "Read", "Grep", "Glob"],
      "agentTeams":    true,
      "promptCaching": false,
      "description":   "Step 2 — task dispatcher"
    },
    {
      "event":            "issues",
      "action":           "labeled",
      "label":            "in-sprint",
      "prompt":           "/step3-implement {{ISSUE_NUMBER}}",
      "model":            "step3",
      "tools":            ["Bash", "Read", "Write", "Edit", "Grep", "Glob"],
      "skipPermissions":  true,
      "agentTeams":       false,
      "promptCaching":    true,
      "description":      "Step 3 — implementation"
    },
    {
      "event":           "pull_request",
      "action":          "closed",
      "merged":          true,
      "prompt":          "Phase E post-merge for task #{{TASK_NUMBER}} (PR #{{PR_NUMBER}} merged to main). Follow Phase E in .claude/workflows/step3-implementation.md exactly: (1) add label 'merged' to the task issue, (2) close the task issue with a comment referencing PR #{{PR_NUMBER}}, (3) find the Epic from the task body ('Part of: casazen/backend#N'), (4) check if ALL task issues of that Epic are closed, (5) if all are closed: close the Epic with a delivery summary and commit an update to .claude/context/codebase_map.md directly to main.",
      "model":           "phaseE",
      "tools":           ["Bash", "Read", "Write", "Edit", "Grep", "Glob"],
      "skipPermissions": true,
      "agentTeams":      false,
      "promptCaching":   true,
      "description":     "Phase E — post-merge closure"
    },
    {
      "event":         "issues",
      "action":        "closed",
      "stateReason":   "completed",
      "prompt":        "/step3-implement {{ISSUE_NUMBER}}",
      "model":         "default",
      "tools":         ["Bash", "Read", "Write", "Edit", "Grep", "Glob"],
      "agentTeams":    true,
      "promptCaching": true,
      "description":   "Auto-unblock on issue close"
    }
  ]
}
```

### `Config/RuleConfig.cs`

```csharp
namespace Casazen.WebhookRunner.Config;

public class RuleConfig
{
    public ModelConfig Models { get; set; } = new();
    public List<Rule>  Rules  { get; set; } = [];
}

public class ModelConfig
{
    public string Default { get; set; } = "qwen-3.5-122b-sovereign";
    public string Step3   { get; set; } = "claude-haiku-4-5-20251001";
    public string PhaseE  { get; set; } = "claude-haiku-4-5-20251001";

    public string Resolve(string key) => key switch
    {
        "step3"  => Step3,
        "phaseE" => PhaseE,
        _        => Default
    };
}

public class Rule
{
    public string   Event            { get; set; } = "";
    public string   Action           { get; set; } = "";
    public string?  Label            { get; set; }
    public string?  RequiredLabel    { get; set; }
    public string?  AuthorType       { get; set; }
    public bool?    Merged           { get; set; }
    public string?  StateReason      { get; set; }
    public string   Prompt           { get; set; } = "";
    public string   Model            { get; set; } = "default";
    public string[] Tools            { get; set; } = [];
    public bool     SkipPermissions  { get; set; }
    public bool     AgentTeams       { get; set; }
    public bool     PromptCaching    { get; set; }
    public string   Description      { get; set; } = "";
}
```

---

## Step 3 — appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Casazen.WebhookRunner": "Debug"
    }
  },
  "WebhookRunner": {
    "Port":              5050,
    "GitHubWebhookSecret": "",
    "GitHubToken":       "",
    "AnthropicApiKey":   "",
    "AnthropicBaseUrl":  "https://adesso-ai-hub.3asabc.de/v1",
    "WorkingDirectory":  "",
    "RuleConfigPath":    "casazen-webhook-config.json",
    "SessionStoreDir":   ".agent-sessions",
    "MaxConcurrentJobs": 2
  }
}
```

### `Config/WebhookRunnerOptions.cs`

```csharp
namespace Casazen.WebhookRunner.Config;

public class WebhookRunnerOptions
{
    public string GitHubWebhookSecret { get; set; } = "";
    public string GitHubToken         { get; set; } = "";
    public string AnthropicApiKey     { get; set; } = "";
    public string AnthropicBaseUrl    { get; set; } = "";
    public string WorkingDirectory    { get; set; } = "";
    public string RuleConfigPath      { get; set; } = "casazen-webhook-config.json";
    public string SessionStoreDir     { get; set; } = ".agent-sessions";
    public int    MaxConcurrentJobs   { get; set; } = 2;
}
```

---

## Step 4 — Program.cs

```csharp
using System.Text.Json;
using Casazen.WebhookRunner.Config;
using Casazen.WebhookRunner.Queue;
using Casazen.WebhookRunner.Routing;
using Casazen.WebhookRunner.Runner;
using Casazen.WebhookRunner.Sessions;
using Casazen.WebhookRunner.Verification;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration.GetSection("WebhookRunner");

builder.WebHost.UseUrls($"http://localhost:{cfg["Port"] ?? "5050"}");
builder.Services.Configure<WebhookRunnerOptions>(cfg);

// Load rule config from JSON file
var ruleConfigPath = cfg["RuleConfigPath"] ?? "casazen-webhook-config.json";
var ruleConfig = JsonSerializer.Deserialize<RuleConfig>(
    File.ReadAllText(ruleConfigPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException($"Failed to load rule config from {ruleConfigPath}");

builder.Services.AddSingleton(ruleConfig);
builder.Services.AddSingleton<IGitHubSignatureVerifier, GitHubSignatureVerifier>();
builder.Services.AddSingleton<IGitHubEventRouter, GitHubEventRouter>();
builder.Services.AddSingleton<IJobQueue, InMemoryJobQueue>();
builder.Services.AddSingleton<IClaudeCodeRunner, ClaudeCodeRunner>();
builder.Services.AddSingleton<ISessionStore, JsonlSessionStore>();
builder.Services.AddHostedService<JobProcessor>();

var app = builder.Build();

// ── Health ────────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

// ── Webhook ───────────────────────────────────────────────────────────────────
app.MapPost("/webhook", async (
    HttpRequest request,
    IGitHubSignatureVerifier verifier,
    IGitHubEventRouter router,
    IJobQueue queue,
    ISessionStore sessions,
    ILogger<Program> logger) =>
{
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    var rawBody = ms.ToArray();

    var signature  = request.Headers["X-Hub-Signature-256"].ToString();
    var eventType  = request.Headers["X-GitHub-Event"].ToString();
    var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();

    if (!verifier.IsValid(rawBody, signature))
    {
        logger.LogWarning("Rejected webhook delivery {DeliveryId}: invalid signature", deliveryId);
        return Results.Unauthorized();
    }

    using var doc = JsonDocument.Parse(rawBody);
    var job = router.Route(eventType, doc.RootElement);

    if (job is null)
    {
        logger.LogDebug("Delivery {DeliveryId} ({Event}) skipped — no matching rule", deliveryId, eventType);
        return Results.Ok();
    }

    job.DeliveryId = deliveryId;
    await sessions.CreateAsync(job);
    queue.Enqueue(job);
    logger.LogInformation("Queued {JobId} — {Description}", job.JobId, job.Description);

    return Results.Accepted();
});

// ── Kill agent ────────────────────────────────────────────────────────────────
app.MapDelete("/jobs/{jobId}", (
    string jobId,
    IClaudeCodeRunner runner,
    ILogger<Program> logger) =>
{
    var killed = runner.Kill(jobId);
    if (killed)
        logger.LogInformation("Killed job {JobId}", jobId);
    return killed ? Results.Ok() : Results.NotFound();
});

// ── Session list ──────────────────────────────────────────────────────────────
app.MapGet("/sessions", (ISessionStore sessions) =>
    Results.Ok(sessions.ListSessions()));

app.MapGet("/sessions/{sessionId}", async (string sessionId, ISessionStore sessions) =>
{
    var session = await sessions.ReadAsync(sessionId);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

app.Run();
```

---

## Step 5 — GitHubEventRouter.cs (config-driven)

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.WebhookRunner.Config;
using Casazen.WebhookRunner.Queue;

namespace Casazen.WebhookRunner.Routing;

public interface IGitHubEventRouter
{
    ClaudeJob? Route(string eventType, JsonElement body);
}

public partial class GitHubEventRouter : IGitHubEventRouter
{
    private readonly RuleConfig _config;

    public GitHubEventRouter(RuleConfig config) => _config = config;

    public ClaudeJob? Route(string eventType, JsonElement body)
    {
        foreach (var rule in _config.Rules)
        {
            if (!rule.Event.Equals(eventType, StringComparison.OrdinalIgnoreCase))
                continue;

            var vars = ExtractContextVariables(eventType, body);
            if (vars is null) continue;

            if (!MatchesRule(rule, eventType, body, vars)) continue;

            var prompt = InterpolatePrompt(rule.Prompt, vars);
            var model  = _config.Models.Resolve(rule.Model);

            return new ClaudeJob
            {
                Prompt          = prompt,
                Model           = model,
                AllowedTools    = rule.Tools,
                SkipPermissions = rule.SkipPermissions,
                AgentTeams      = rule.AgentTeams,
                PromptCaching   = rule.PromptCaching,
                Description     = InterpolatePrompt(rule.Description, vars),
                IssueNumber     = vars.TryGetValue("ISSUE_NUMBER", out var n) && int.TryParse(n, out var i) ? i : 0
            };
        }
        return null;
    }

    // Extract context variables from payload — same pattern as agentic-kanban contextVariables map
    private static Dictionary<string, string>? ExtractContextVariables(string eventType, JsonElement body)
    {
        var vars = new Dictionary<string, string>();

        switch (eventType)
        {
            case "issues":
            {
                if (!body.TryGetProperty("issue", out var issue)) return null;
                vars["ISSUE_NUMBER"] = issue.GetProperty("number").GetInt32().ToString();
                vars["ISSUE_TITLE"]  = issue.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                vars["ACTION"]       = body.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                vars["LABEL"]        = body.TryGetProperty("label", out var l)
                    ? l.GetProperty("name").GetString() ?? ""
                    : "";
                vars["STATE_REASON"] = issue.TryGetProperty("state_reason", out var sr)
                    ? sr.GetString() ?? ""
                    : "";
                // Collect all current labels on the issue
                vars["ISSUE_LABELS"] = string.Join(",",
                    issue.TryGetProperty("labels", out var labels)
                        ? labels.EnumerateArray().Select(lbl => lbl.GetProperty("name").GetString() ?? "")
                        : []);
                break;
            }
            case "issue_comment":
            {
                if (!body.TryGetProperty("issue", out var issue)) return null;
                if (!body.TryGetProperty("comment", out var comment)) return null;
                vars["ISSUE_NUMBER"] = issue.GetProperty("number").GetInt32().ToString();
                vars["AUTHOR_TYPE"]  = comment.GetProperty("user").GetProperty("type").GetString() ?? "";
                vars["ISSUE_LABELS"] = string.Join(",",
                    issue.TryGetProperty("labels", out var labels)
                        ? labels.EnumerateArray().Select(lbl => lbl.GetProperty("name").GetString() ?? "")
                        : []);
                break;
            }
            case "pull_request":
            {
                if (!body.TryGetProperty("pull_request", out var pr)) return null;
                vars["PR_NUMBER"] = pr.GetProperty("number").GetInt32().ToString();
                vars["MERGED"]    = pr.TryGetProperty("merged", out var m) ? m.GetBoolean().ToString() : "false";
                var prBody        = pr.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                var match         = TaskNumberRegex().Match(prBody);
                vars["TASK_NUMBER"] = match.Success ? match.Groups[1].Value : "";
                vars["ISSUE_NUMBER"] = vars["TASK_NUMBER"]; // alias for dedup key
                break;
            }
            default:
                return null;
        }
        return vars;
    }

    private static bool MatchesRule(Rule rule, string eventType, JsonElement body, Dictionary<string, string> vars)
    {
        if (!rule.Action.Equals(vars.GetValueOrDefault("ACTION", ""), StringComparison.OrdinalIgnoreCase)
            && eventType != "issue_comment" && eventType != "pull_request")
            return false;

        // issues.labeled
        if (rule.Label != null && !rule.Label.Equals(vars.GetValueOrDefault("LABEL"), StringComparison.OrdinalIgnoreCase))
            return false;

        // issues.closed stateReason filter
        if (rule.StateReason != null && !rule.StateReason.Equals(vars.GetValueOrDefault("STATE_REASON"), StringComparison.OrdinalIgnoreCase))
            return false;

        // issue_comment: skip bots
        if (eventType == "issue_comment")
        {
            if (vars.GetValueOrDefault("AUTHOR_TYPE") == "Bot") return false;
            if (rule.RequiredLabel != null)
            {
                var issueLabels = vars.GetValueOrDefault("ISSUE_LABELS", "").Split(',');
                if (!issueLabels.Contains(rule.RequiredLabel, StringComparer.OrdinalIgnoreCase))
                    return false;
            }
        }

        // pull_request: merged filter
        if (eventType == "pull_request")
        {
            if (rule.Merged.HasValue && rule.Merged.Value != bool.Parse(vars.GetValueOrDefault("MERGED", "false")))
                return false;
            if (string.IsNullOrEmpty(vars.GetValueOrDefault("TASK_NUMBER")))
                return false;
        }

        return true;
    }

    // Replace {{KEY}} with value — identical to agentic-kanban contextVariables substitution
    private static string InterpolatePrompt(string template, Dictionary<string, string> vars)
    {
        foreach (var (key, value) in vars)
            template = template.Replace($"{{{{{key}}}}}", value, StringComparison.OrdinalIgnoreCase);
        return template;
    }

    [GeneratedRegex(@"(?i)(?<=Closes #)(\d+)")]
    private static partial Regex TaskNumberRegex();
}
```

---

## Step 6 — ClaudeCodeRunner.cs (with stream-json + kill)

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Casazen.WebhookRunner.Config;
using Casazen.WebhookRunner.Queue;
using Casazen.WebhookRunner.Sessions;
using Microsoft.Extensions.Options;

namespace Casazen.WebhookRunner.Runner;

public interface IClaudeCodeRunner
{
    Task<int> RunAsync(ClaudeJob job, CancellationToken ct);
    bool Kill(string jobId);
}

public class ClaudeCodeRunner : IClaudeCodeRunner
{
    private readonly WebhookRunnerOptions _opts;
    private readonly ISessionStore        _sessions;
    private readonly ILogger<ClaudeCodeRunner> _logger;
    private readonly ConcurrentDictionary<string, Process> _active = new();

    public ClaudeCodeRunner(IOptions<WebhookRunnerOptions> opts, ISessionStore sessions,
        ILogger<ClaudeCodeRunner> logger)
    {
        _opts     = opts.Value;
        _sessions = sessions;
        _logger   = logger;
    }

    public async Task<int> RunAsync(ClaudeJob job, CancellationToken ct)
    {
        var tools = string.Join(",", job.AllowedTools.Length > 0
            ? job.AllowedTools
            : ["Bash", "Read", "Write", "Edit", "Grep", "Glob"]);

        // Use 'claude' CLI directly — same as agentic-kanban agent.ts spawn("claude", args)
        // Assumes claude-code is installed globally: npm install -g @anthropic-ai/claude-code
        var args = new List<string>
        {
            "--print",
            "--verbose",
            "--output-format", "stream-json",   // structured output, same as agentic-kanban
            "--model", job.Model,
            "--allowedTools", tools,
            "--permission-prompt-mode", job.SkipPermissions ? "acceptAll" : "acceptEdits",
            "-p", $"\"{EscapeArg(job.Prompt)}\""
        };

        var env = new Dictionary<string, string?>
        {
            ["ANTHROPIC_API_KEY"]  = _opts.AnthropicApiKey,
            ["ANTHROPIC_BASE_URL"] = _opts.AnthropicBaseUrl,
            ["GITHUB_TOKEN"]       = _opts.GitHubToken,
            ["CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS"] = job.AgentTeams    ? "1" : "0",
            ["CLAUDE_CODE_ENABLE_PROMPT_CACHING"]    = job.PromptCaching ? "1" : "0",
        };

        var psi = new ProcessStartInfo("claude", string.Join(" ", args))
        {
            WorkingDirectory       = _opts.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var (k, v) in env)
            psi.Environment[k] = v;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start claude process");

        _active[job.JobId] = process;
        await _sessions.UpdateStatusAsync(job.JobId, "running");

        // Stream stdout as JSONL — identical to agentic-kanban stdout handler
        process.OutputDataReceived += async (_, e) =>
        {
            if (e.Data is null) return;
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                var msgType = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

                // Extract text content for logging
                var text = ExtractText(doc.RootElement);
                if (text != null)
                    _logger.LogDebug("[{JobId}] {Text}", job.JobId, text);

                // Capture claudeSessionId from init message (enables future --resume)
                if (msgType == "init" && doc.RootElement.TryGetProperty("sessionId", out var sid))
                    await _sessions.UpdateClaudeSessionIdAsync(job.JobId, sid.GetString()!);

                // Capture final metrics from result message
                if (msgType == "result")
                    await _sessions.AppendMessageAsync(job.JobId, e.Data);
            }
            catch
            {
                // Non-JSON lines (plain log output)
                _logger.LogInformation("[{JobId}] {Line}", job.JobId, e.Data);
            }
        };

        process.ErrorDataReceived += async (_, e) =>
        {
            if (e.Data is null) return;
            _logger.LogWarning("[{JobId}] ERR {Line}", job.JobId, e.Data);
            await _sessions.AppendMessageAsync(job.JobId, $"{{\"type\":\"stderr\",\"data\":{JsonSerializer.Serialize(e.Data)}}}");
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        _active.TryRemove(job.JobId, out _);

        var status = process.ExitCode == 0 ? "completed" : "failed";
        await _sessions.UpdateStatusAsync(job.JobId, status);
        _logger.LogInformation("Job {JobId} {Status} (exit code {Code})", job.JobId, status, process.ExitCode);

        return process.ExitCode;
    }

    public bool Kill(string jobId)
    {
        if (!_active.TryRemove(jobId, out var process)) return false;
        process.Kill(entireProcessTree: true);
        return true;
    }

    private static string? ExtractText(JsonElement el)
    {
        if (!el.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                && block.TryGetProperty("text", out var text))
                return text.GetString();
        }
        return null;
    }

    private static string EscapeArg(string s) => s.Replace("\"", "\\\"");
}
```

---

## Step 7 — Session Store (JSONL, like agentic-kanban store.ts)

### `Sessions/ISessionStore.cs`

```csharp
namespace Casazen.WebhookRunner.Sessions;

public interface ISessionStore
{
    Task CreateAsync(Queue.ClaudeJob job);
    Task UpdateStatusAsync(string jobId, string status);
    Task UpdateClaudeSessionIdAsync(string jobId, string claudeSessionId);
    Task AppendMessageAsync(string jobId, string jsonLine);
    Task<SessionSummary?> ReadAsync(string sessionId);
    IEnumerable<SessionSummary> ListSessions();
}

public record SessionSummary(
    string   JobId,
    int      IssueNumber,
    string   Description,
    string   Status,
    string   Model,
    string?  ClaudeSessionId,
    DateTime StartedAt
);
```

### `Sessions/JsonlSessionStore.cs`

```csharp
using System.Text.Json;
using Casazen.WebhookRunner.Config;
using Microsoft.Extensions.Options;

namespace Casazen.WebhookRunner.Sessions;

public class JsonlSessionStore : ISessionStore
{
    private readonly string _dir;

    public JsonlSessionStore(IOptions<WebhookRunnerOptions> opts)
    {
        _dir = Path.IsPathRooted(opts.Value.SessionStoreDir)
            ? opts.Value.SessionStoreDir
            : Path.Combine(opts.Value.WorkingDirectory, opts.Value.SessionStoreDir);
        Directory.CreateDirectory(_dir);
    }

    private string SessionFile(string jobId) => Path.Combine(_dir, $"{jobId}.jsonl");

    public async Task CreateAsync(Queue.ClaudeJob job)
    {
        var header = JsonSerializer.Serialize(new
        {
            type        = "session_created",
            jobId       = job.JobId,
            issueNumber = job.IssueNumber,
            description = job.Description,
            model       = job.Model,
            prompt      = job.Prompt,
            status      = "queued",
            startedAt   = job.EnqueuedAt
        });
        await File.AppendAllTextAsync(SessionFile(job.JobId), header + "\n");
    }

    public async Task UpdateStatusAsync(string jobId, string status)
    {
        var line = JsonSerializer.Serialize(new { type = "status_update", status, ts = DateTime.UtcNow });
        await File.AppendAllTextAsync(SessionFile(jobId), line + "\n");
    }

    public async Task UpdateClaudeSessionIdAsync(string jobId, string claudeSessionId)
    {
        var line = JsonSerializer.Serialize(new { type = "claude_session_id", claudeSessionId });
        await File.AppendAllTextAsync(SessionFile(jobId), line + "\n");
    }

    public async Task AppendMessageAsync(string jobId, string jsonLine)
        => await File.AppendAllTextAsync(SessionFile(jobId), jsonLine + "\n");

    public async Task<SessionSummary?> ReadAsync(string sessionId)
    {
        var file = SessionFile(sessionId);
        if (!File.Exists(file)) return null;
        var lines = await File.ReadAllLinesAsync(file);
        return ParseSummary(lines);
    }

    public IEnumerable<SessionSummary> ListSessions()
    {
        foreach (var file in Directory.GetFiles(_dir, "*.jsonl").OrderByDescending(f => f))
        {
            var lines = File.ReadAllLines(file);
            var s = ParseSummary(lines);
            if (s != null) yield return s;
        }
    }

    private static SessionSummary? ParseSummary(string[] lines)
    {
        string? jobId = null, description = null, status = null, model = null, claudeSessionId = null;
        int issueNumber = 0;
        DateTime startedAt = default;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var t = doc.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                switch (t)
                {
                    case "session_created":
                        jobId       = doc.RootElement.GetProperty("jobId").GetString();
                        description = doc.RootElement.GetProperty("description").GetString();
                        model       = doc.RootElement.GetProperty("model").GetString();
                        issueNumber = doc.RootElement.GetProperty("issueNumber").GetInt32();
                        startedAt   = doc.RootElement.GetProperty("startedAt").GetDateTime();
                        status      = "queued";
                        break;
                    case "status_update":
                        status = doc.RootElement.GetProperty("status").GetString();
                        break;
                    case "claude_session_id":
                        claudeSessionId = doc.RootElement.GetProperty("claudeSessionId").GetString();
                        break;
                }
            }
            catch { }
        }

        return jobId is null ? null : new SessionSummary(jobId, issueNumber, description ?? "", status ?? "unknown", model ?? "", claudeSessionId, startedAt);
    }
}
```

---

## Step 8 — ClaudeJob.cs + InMemoryJobQueue.cs + JobProcessor.cs

These are identical to the original spec in the previous version of this file.
Refer to the inline code blocks there — no changes needed from the agentic-kanban integration.

---

## Prerequisite: Install claude-code globally

```powershell
# Required: claude CLI must be on PATH (same assumption as agentic-kanban)
npm install -g @anthropic-ai/claude-code

# Verify
claude --version
```

---

## Validation Checklist — Phase 2

- [ ] `dotnet build Casazen.WebhookRunner` — zero warnings
- [ ] `GET /health` returns 200
- [ ] `casazen-webhook-config.json` loaded at startup without error
- [ ] POST /webhook with valid HMAC → 202
- [ ] POST /webhook with invalid HMAC → 401
- [ ] `issues.labeled raw-requirement` payload → rule matched, `{{ISSUE_NUMBER}}` substituted
- [ ] `pull_request.closed merged=true` with `Closes #42` → `TASK_NUMBER=42`, `PR_NUMBER=N` substituted
- [ ] Duplicate events for same issue → only one job enqueued
- [ ] Session JSONL file created in `.agent-sessions/` on job enqueue
- [ ] `DELETE /jobs/{jobId}` kills the subprocess
- [ ] `GET /sessions` lists all sessions
