# AuditMonitor — Load Test Infrastructure Auditor

> **Date:** 2026-03-28
> **Status:** Approved
> **Scope:** `tests/PbxAdmin.LoadTests/Auditing/`

## Problem

Load tests run for 10-20 minutes. During that time, all 5 Docker containers (PSTN, Realtime, File, PostgreSQL, PbxAdmin) behave differently under pressure. Today we audit manually by running `docker stats`, `docker exec asterisk -rx "..."`, and `docker logs` in a terminal. This is slow, incomplete, and produces no artifact for post-analysis.

## Solution

A C# `AuditMonitorService` that runs as a background task inside the existing load test process. Every N seconds (configurable, default 10), it collects a full snapshot of all infrastructure components and writes it to disk. At the end, it produces a consolidated JSON report.

## Architecture

```
Program.cs
  └── AuditMonitorService.StartAsync(intervalSecs, outputPath)
        ├── DockerStatsCollector      → CPU, memory, network I/O (all containers)
        ├── AsteriskCliCollector       → channels, ODBC, queue, endpoints, RTP
        └── ContainerLogCollector      → new ERROR/WARNING lines since last snapshot
```

No new NuGet dependencies. All collection uses `Process.Start("docker", ...)` to execute `docker stats`, `docker exec`, and `docker logs`.

## File Map

| File | Responsibility |
|------|---------------|
| `Auditing/AuditMonitorService.cs` | Background loop, orchestration, JSONL/JSON output |
| `Auditing/AuditSnapshot.cs` | POCO model for one snapshot |
| `Auditing/DockerStatsCollector.cs` | Parses `docker stats --no-stream --format` for all containers |
| `Auditing/AsteriskCliCollector.cs` | Runs `docker exec` for Asterisk CLI commands, parses output |
| `Auditing/ContainerLogCollector.cs` | Runs `docker logs --since` to capture new errors |

## Snapshot Model

```
AuditSnapshot
├── Timestamp: DateTime
├── ElapsedSeconds: double
├── SequenceNumber: int
│
├── Containers: ContainerStats[]
│   ├── Name: string
│   ├── CpuPercent: double
│   ├── MemUsageMB: double
│   ├── MemLimitMB: double
│   ├── NetInputMB: double
│   └── NetOutputMB: double
│
├── AsteriskRealtime: AsteriskStats
│   ├── ActiveChannels: int
│   ├── ActiveCalls: int
│   ├── CallsProcessed: int
│   ├── OdbcActiveConnections: int
│   ├── OdbcMaxConnections: int
│   ├── EndpointCount: int
│   ├── Queue: QueueStats
│   │   ├── CallsWaiting: int
│   │   ├── Completed: int
│   │   ├── Abandoned: int
│   │   ├── Holdtime: int
│   │   ├── Talktime: int
│   │   ├── MembersIdle: int
│   │   ├── MembersInUse: int
│   │   ├── MembersRinging: int
│   │   └── MembersUnavailable: int
│   └── Rtp: RtpStats?
│       ├── StreamsActive: int
│       └── RawOutput: string
│
├── AsteriskPstn: AsteriskBasicStats
│   ├── ActiveChannels: int
│   ├── ActiveCalls: int
│   └── CallsProcessed: int
│
├── AsteriskFile: AsteriskBasicStats
│   ├── ActiveChannels: int
│   ├── ActiveCalls: int
│   └── CallsProcessed: int
│
└── Errors: ErrorEntry[]
    ├── Container: string
    ├── Timestamp: string
    └── Message: string
```

## Collection Strategy

### DockerStatsCollector

Single command for all containers:

```
docker stats --no-stream --format "{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}|{{.NetIO}}"
```

Parse each line: split on `|`, strip `%`, parse `123.4MiB / 60.49GiB`, parse `1.5MB / 2.3MB`.

### AsteriskCliCollector

Four parallel `docker exec` calls for realtime, one each for PSTN and file:

| Container | Command | Parses |
|-----------|---------|--------|
| demo-pbx-realtime | `asterisk -rx "core show channels count"` | 3 ints: active channels, active calls, processed |
| demo-pbx-realtime | `asterisk -rx "odbc show"` | active connections, max connections |
| demo-pbx-realtime | `asterisk -rx "queue show loadtest"` | header line + member status counts |
| demo-pbx-realtime | `asterisk -rx "pjsip show endpoints"` | last line "Objects found: N" |
| demo-pbx-realtime | `asterisk -rx "rtp show stats"` | stream count + raw output (best effort) |
| demo-pstn | `asterisk -rx "core show channels count"` | 3 ints |
| demo-pbx-file | `asterisk -rx "core show channels count"` | 3 ints |

Run realtime commands in parallel (`Task.WhenAll`). PSTN and file can run in parallel with each other and with realtime.

### ContainerLogCollector

For each container:

```
docker logs --since <lastSnapshotTimestamp> <container> 2>&1
```

Filter lines containing `ERROR` or `FATAL`. Store container name + line text. Track `--since` timestamp to avoid duplicates.

Containers to monitor: `demo-pbx-realtime`, `demo-pstn`, `demo-pbx-file`, `demo-postgres`, `asterisk-pbx-admin`.

## Output

### During test: JSONL streaming

File: `{outputPath}.audit.jsonl`

One JSON object per line, written after each snapshot completes. Safe for crash recovery (partial file is still valid JSONL).

### After test: Consolidated JSON

File: `{outputPath}.audit.json`

```json
{
  "testStarted": "2026-03-28T13:15:00Z",
  "testEnded": "2026-03-28T13:30:00Z",
  "intervalSeconds": 10,
  "snapshotCount": 90,
  "snapshots": [ ... all AuditSnapshot objects ... ]
}
```

## Integration in Program.cs

```csharp
// Before test — after BuildTestContext, before StartSdkAsync
var auditor = new AuditMonitorService(
    loggerFactory.CreateLogger<AuditMonitorService>());
await auditor.StartAsync(auditIntervalSecs, outputPath, cts.Token);

// ... existing test execution ...

// After test — after drain, before validation
var auditResult = await auditor.StopAsync();
logger.LogInformation("Audit: {Snapshots} snapshots, {Errors} errors captured",
    auditResult.SnapshotCount, auditResult.TotalErrors);
```

## CLI Flag

New optional argument: `--audit-interval <seconds>`

- Default: `10`
- `0` disables auditing entirely
- Minimum: `5` (lower values risk docker exec overhead affecting the test)

## Constants

Container names (matching docker-compose.pbxadmin.yml):

```csharp
private const string ContainerRealtime = "demo-pbx-realtime";
private const string ContainerPstn = "demo-pstn";
private const string ContainerFile = "demo-pbx-file";
private const string ContainerPostgres = "demo-postgres";
private const string ContainerPbxAdmin = "asterisk-pbx-admin";
private const string QueueName = "loadtest";
```

## Error Handling

- If a `docker exec` fails (container down, timeout), log a warning and record `null` for that section of the snapshot. Never crash the test.
- Process timeout: 5 seconds per docker command. If exceeded, kill and record timeout error.
- All collection runs in a `try/catch` — the auditor must never interfere with the load test itself.

## Testing

Unit tests for parsing logic only (no Docker dependency):
- `DockerStatsCollector.Parse(string output)` — test with sample docker stats output
- `AsteriskCliCollector.ParseChannelCount(string output)` — test with sample CLI output
- `AsteriskCliCollector.ParseOdbcShow(string output)` — test with sample ODBC output
- `AsteriskCliCollector.ParseQueueShow(string output)` — test with sample queue output

All parsers are `internal static` methods that take raw string input and return typed results. This makes them testable without Docker.
