# Phase 6 — Grafana Observability Stack

## Goal

Add a full observability layer for `Casazen.WebhookRunner` with:
- **Metrics** (Prometheus): job counters, duration, cost, tokens, queue depth
- **Logs** (Loki + Promtail): JSONL session files shipped and queryable
- **Traces** (Tempo + OpenTelemetry): per-job distributed traces
- **Dashboards** (Grafana): 4 provisioned dashboards, zero manual setup

---

## Architecture

```
Casazen.WebhookRunner
  │
  ├── /metrics              → Prometheus scrapes every 15s
  ├── OTLP gRPC :4317       → Tempo receives traces
  └── .agent-sessions/*.jsonl
                            ← Promtail tails files → Loki

Grafana
  ├── datasource: Prometheus  → metrics dashboards
  ├── datasource: Loki        → log explorer + log panels
  └── datasource: Tempo       → trace explorer + trace panels
```

## Stack (all via Docker Compose)

| Service | Image | Port |
|---|---|---|
| Grafana | grafana/grafana:11.x | 3000 |
| Prometheus | prom/prometheus:v2.x | 9090 |
| Loki | grafana/loki:3.x | 3100 |
| Promtail | grafana/promtail:3.x | — |
| Tempo | grafana/tempo:2.x | 3200 (HTTP), 4317 (OTLP gRPC) |

---

## Step 1 — Add EventType to ClaudeJob

In `Queue/ClaudeJob.cs`, add a label for Prometheus:

```csharp
public class ClaudeJob
{
    // ... existing fields ...
    public string EventType { get; init; } = "";  // e.g. "issues.labeled.raw-requirement"
}
```

In `GitHubEventRouter.cs`, populate it from the matched rule description:

```csharp
return new ClaudeJob
{
    // ... existing fields ...
    EventType = $"{rule.Event}.{rule.Action}{(rule.Label != null ? $".{rule.Label}" : "")}"
};
```

---

## Step 2 — Add Prometheus Metrics to WebhookRunner

### 2a — NuGet package

```powershell
dotnet add Casazen.WebhookRunner package prometheus-net.AspNetCore
```

### 2b — Metrics definitions (new file: `Metrics/AgentMetrics.cs`)

```csharp
using Prometheus;

namespace Casazen.WebhookRunner.Metrics;

public static class AgentMetrics
{
    // Webhook
    public static readonly Counter WebhookRequestsTotal = Prometheus.Metrics
        .CreateCounter("casazen_webhook_requests_total", "Webhook requests received",
            new CounterConfiguration { LabelNames = ["event_type", "matched"] });

    // Jobs
    public static readonly Counter JobsTotal = Prometheus.Metrics
        .CreateCounter("casazen_jobs_total", "Jobs completed",
            new CounterConfiguration { LabelNames = ["status", "model", "event_type"] });

    public static readonly Gauge JobsActive = Prometheus.Metrics
        .CreateGauge("casazen_jobs_active", "Jobs currently running");

    public static readonly Gauge QueueDepth = Prometheus.Metrics
        .CreateGauge("casazen_queue_depth", "Jobs waiting in queue");

    // Duration
    public static readonly Histogram JobDurationMs = Prometheus.Metrics
        .CreateHistogram("casazen_job_duration_ms", "Total job wall-clock time",
            new HistogramConfiguration
            {
                LabelNames = ["model", "event_type"],
                Buckets = [1000, 5000, 15000, 30000, 60000, 120000, 300000]
            });

    public static readonly Histogram JobDurationApiMs = Prometheus.Metrics
        .CreateHistogram("casazen_job_duration_api_ms", "Claude API time within job",
            new HistogramConfiguration
            {
                LabelNames = ["model"],
                Buckets = [1000, 5000, 15000, 30000, 60000, 120000, 300000]
            });

    public static readonly Histogram JobNumTurns = Prometheus.Metrics
        .CreateHistogram("casazen_job_turns", "Number of conversation turns",
            new HistogramConfiguration
            {
                LabelNames = ["model"],
                Buckets = [1, 2, 5, 10, 20, 40, 80]
            });

    // Cost
    public static readonly Histogram JobCostUsd = Prometheus.Metrics
        .CreateHistogram("casazen_job_cost_usd", "Job cost in USD",
            new HistogramConfiguration
            {
                LabelNames = ["model", "event_type"],
                Buckets = [0.001, 0.005, 0.01, 0.05, 0.10, 0.25, 0.50, 1.00]
            });

    // Tokens
    public static readonly Counter TokensInputTotal = Prometheus.Metrics
        .CreateCounter("casazen_tokens_input_total", "Input tokens consumed",
            new CounterConfiguration { LabelNames = ["model", "event_type"] });

    public static readonly Counter TokensOutputTotal = Prometheus.Metrics
        .CreateCounter("casazen_tokens_output_total", "Output tokens generated",
            new CounterConfiguration { LabelNames = ["model", "event_type"] });

    public static readonly Counter TokensCacheReadTotal = Prometheus.Metrics
        .CreateCounter("casazen_tokens_cache_read_total", "Cache-read input tokens",
            new CounterConfiguration { LabelNames = ["model"] });

    public static readonly Counter TokensCacheCreationTotal = Prometheus.Metrics
        .CreateCounter("casazen_tokens_cache_creation_total", "Cache-creation input tokens",
            new CounterConfiguration { LabelNames = ["model"] });
}
```

### 2c — Register metrics endpoint in Program.cs

```csharp
// Add after builder.Services setup:
builder.Services.AddSingleton(Prometheus.Metrics.DefaultRegistry);

// Add after app.MapDelete:
app.MapMetrics("/metrics");   // prometheus-net built-in endpoint
```

### 2d — Record metrics in ClaudeCodeRunner

Add after parsing the `result` message from stream-json output (in `OutputDataReceived` handler):

```csharp
if (msgType == "result")
{
    await _sessions.AppendMessageAsync(job.JobId, e.Data);

    // Record Prometheus metrics from claude CLI result message
    var durationMs  = doc.RootElement.TryGetProperty("durationMs",    out var d)   ? d.GetDouble()  : 0;
    var durationApi = doc.RootElement.TryGetProperty("durationApiMs", out var da)  ? da.GetDouble() : 0;
    var costUsd     = doc.RootElement.TryGetProperty("totalCostUsd",  out var c)   ? c.GetDouble()  : 0;
    var numTurns    = doc.RootElement.TryGetProperty("numTurns",      out var nt)  ? nt.GetInt32()  : 0;
    var inputTok    = doc.RootElement.TryGetProperty("inputTokens",   out var it)  ? it.GetInt64()  : 0;
    var outputTok   = doc.RootElement.TryGetProperty("outputTokens",  out var ot)  ? ot.GetInt64()  : 0;

    AgentMetrics.JobDurationMs.WithLabels(job.Model, job.EventType).Observe(durationMs);
    AgentMetrics.JobDurationApiMs.WithLabels(job.Model).Observe(durationApi);
    AgentMetrics.JobCostUsd.WithLabels(job.Model, job.EventType).Observe(costUsd);
    AgentMetrics.JobNumTurns.WithLabels(job.Model).Observe(numTurns);
    AgentMetrics.TokensInputTotal.WithLabels(job.Model, job.EventType).Inc(inputTok);
    AgentMetrics.TokensOutputTotal.WithLabels(job.Model, job.EventType).Inc(outputTok);

    // Per-model cache tokens (from modelUsage map)
    if (doc.RootElement.TryGetProperty("modelUsage", out var modelUsage))
    {
        foreach (var modelEntry in modelUsage.EnumerateObject())
        {
            var mu = modelEntry.Value;
            var cacheRead = mu.TryGetProperty("cacheReadInputTokens",    out var cr) ? cr.GetInt64() : 0;
            var cacheCreate = mu.TryGetProperty("cacheCreationInputTokens", out var cc) ? cc.GetInt64() : 0;
            AgentMetrics.TokensCacheReadTotal.WithLabels(modelEntry.Name).Inc(cacheRead);
            AgentMetrics.TokensCacheCreationTotal.WithLabels(modelEntry.Name).Inc(cacheCreate);
        }
    }
}
```

Record job completion in `ClaudeCodeRunner.RunAsync` after exit:

```csharp
AgentMetrics.JobsTotal.WithLabels(status, job.Model, job.EventType).Inc();
AgentMetrics.JobsActive.Dec();
```

Record in `JobProcessor.ExecuteAsync` before running:

```csharp
AgentMetrics.JobsActive.Inc();
AgentMetrics.QueueDepth.Set(_queue.Count);
```

Record in `InMemoryJobQueue.Enqueue` (via interface addition):

```csharp
AgentMetrics.QueueDepth.Inc();
```

Record in POST /webhook handler after routing:

```csharp
AgentMetrics.WebhookRequestsTotal.WithLabels(eventType, job is null ? "false" : "true").Inc();
```

---

## Step 3 — Add OpenTelemetry Traces

### 3a — NuGet packages

```powershell
dotnet add Casazen.WebhookRunner package OpenTelemetry.Extensions.Hosting
dotnet add Casazen.WebhookRunner package OpenTelemetry.Instrumentation.AspNetCore
dotnet add Casazen.WebhookRunner package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

### 3b — ActivitySource definition (new file: `Tracing/AgentTracing.cs`)

```csharp
using System.Diagnostics;

namespace Casazen.WebhookRunner.Tracing;

public static class AgentTracing
{
    public static readonly ActivitySource Source = new("Casazen.WebhookRunner", "1.0.0");
}
```

### 3c — Register in Program.cs

```csharp
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("Casazen.WebhookRunner")
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri("http://localhost:4317");
            o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
        }));
```

### 3d — Instrument ClaudeCodeRunner

```csharp
using Casazen.WebhookRunner.Tracing;
using System.Diagnostics;

public async Task<int> RunAsync(ClaudeJob job, CancellationToken ct)
{
    using var activity = AgentTracing.Source.StartActivity(
        $"job.execute: {job.Description}",
        ActivityKind.Internal);

    activity?.SetTag("job.id",          job.JobId);
    activity?.SetTag("job.issue",       job.IssueNumber);
    activity?.SetTag("job.model",       job.Model);
    activity?.SetTag("job.event_type",  job.EventType);
    activity?.SetTag("job.agent_teams", job.AgentTeams);

    try
    {
        // ... existing subprocess logic ...
        activity?.SetStatus(ActivityStatusCode.Ok);
        return exitCode;
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        throw;
    }
}
```

Store `activity.TraceId` in the session JSONL for Loki ↔ Tempo correlation:

```csharp
await _sessions.AppendMessageAsync(job.JobId,
    $"{{\"type\":\"trace_id\",\"traceId\":\"{activity?.TraceId}\"}}");
```

---

## Step 4 — Docker Compose Observability Stack

See `observability/docker-compose.yml`.

Start with:
```powershell
cd observability
docker compose up -d
```

Access points:
- Grafana:    http://localhost:3000  (admin / admin)
- Prometheus: http://localhost:9090
- Tempo:      http://localhost:3200
- Loki:       http://localhost:3100

---

## Step 5 — Grafana Dashboards (auto-provisioned)

Four dashboards are provisioned automatically from `observability/grafana/dashboards/`:

| Dashboard | What it shows |
|---|---|
| `casazen-overview.json` | Job counts, success rate, active jobs, queue depth |
| `casazen-consumption.json` | Cost over time, tokens, cache hit ratio, cost by model |
| `casazen-performance.json` | Duration p50/p95/p99, turns histogram, slow jobs table |
| `casazen-logs.json` | Loki log explorer, per-job log timeline, error rate |

---

## Validation Checklist — Phase 6

- [ ] `docker compose up -d` in `observability/` starts all 5 services
- [ ] `GET http://localhost:5050/metrics` returns Prometheus text format
- [ ] After running one job: `casazen_jobs_total` counter increments
- [ ] After running one job: `casazen_job_cost_usd` histogram has an observation
- [ ] Promtail ships `.agent-sessions/*.jsonl` to Loki (verify in Grafana → Explore → Loki)
- [ ] Tempo receives a trace (verify in Grafana → Explore → Tempo)
- [ ] Overview dashboard loads without errors
- [ ] Consumption dashboard shows cost over time after 2+ jobs
- [ ] LogQL query `{job="casazen_agent"}` returns log lines in Loki
- [ ] Trace for a specific `jobId` is searchable in Tempo
