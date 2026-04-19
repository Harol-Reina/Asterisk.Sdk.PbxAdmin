# AuditMonitor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an infrastructure auditor that captures snapshots of all Docker stack components during load tests, writing JSONL streaming + consolidated JSON output.

**Architecture:** `AuditMonitorService` runs a background loop (configurable interval, default 10s) that collects Docker stats, Asterisk CLI metrics, and container error logs via `Process.Start("docker", ...)`. Reuses existing `DockerContainerNames` and `DockerStatsSample` parsing. Integrates into `Program.cs` as a fire-and-forget background task.

**Tech Stack:** .NET 10, System.Text.Json, System.Diagnostics.Process

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `tests/PbxAdmin.LoadTests/Auditing/AuditSnapshot.cs` | Create | POCO models for one snapshot (containers, Asterisk, errors) |
| `tests/PbxAdmin.LoadTests/Auditing/AsteriskCliCollector.cs` | Create | Runs `docker exec` for Asterisk CLI, parses output with `internal static` methods |
| `tests/PbxAdmin.LoadTests/Auditing/ContainerLogCollector.cs` | Create | Runs `docker logs --since` to capture new errors |
| `tests/PbxAdmin.LoadTests/Auditing/AuditMonitorService.cs` | Create | Background loop orchestrator, JSONL/JSON writer |
| `tests/PbxAdmin.LoadTests/Program.cs` | Modify | Add `--audit-interval` CLI option, start/stop auditor |
| `tests/PbxAdmin.Tests/LoadTests/AsteriskCliCollectorTests.cs` | Create | Unit tests for CLI output parsing |
| `tests/PbxAdmin.Tests/LoadTests/ContainerLogCollectorTests.cs` | Create | Unit tests for log filtering |

---

### Task 1: Snapshot model (AuditSnapshot.cs)

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Auditing/AuditSnapshot.cs`

- [ ] **Step 1: Create the snapshot POCO**

```csharp
using System.Text.Json.Serialization;

namespace PbxAdmin.LoadTests.Auditing;

/// <summary>One point-in-time snapshot of the entire Docker stack infrastructure.</summary>
public sealed record AuditSnapshot
{
    public required DateTime Timestamp { get; init; }
    public required double ElapsedSeconds { get; init; }
    public required int SequenceNumber { get; init; }
    public required ContainerSnapshot[] Containers { get; init; }
    public required AsteriskSnapshot Realtime { get; init; }
    public required AsteriskBasicSnapshot Pstn { get; init; }
    public required AsteriskBasicSnapshot File { get; init; }
    public required ErrorEntry[] Errors { get; init; }
}

public sealed record ContainerSnapshot
{
    public required string Name { get; init; }
    public double CpuPercent { get; init; }
    public double MemUsageMB { get; init; }
    public double MemLimitMB { get; init; }
    public double NetInputMB { get; init; }
    public double NetOutputMB { get; init; }
}

public sealed record AsteriskSnapshot
{
    public int ActiveChannels { get; init; }
    public int ActiveCalls { get; init; }
    public int CallsProcessed { get; init; }
    public int OdbcActiveConnections { get; init; }
    public int OdbcMaxConnections { get; init; }
    public int EndpointCount { get; init; }
    public QueueSnapshot? Queue { get; init; }
    public string? RtpRawOutput { get; init; }
}

public sealed record QueueSnapshot
{
    public int CallsWaiting { get; init; }
    public int Completed { get; init; }
    public int Abandoned { get; init; }
    public int Holdtime { get; init; }
    public int Talktime { get; init; }
    public int MembersIdle { get; init; }
    public int MembersInUse { get; init; }
    public int MembersRinging { get; init; }
    public int MembersUnavailable { get; init; }
}

public sealed record AsteriskBasicSnapshot
{
    public int ActiveChannels { get; init; }
    public int ActiveCalls { get; init; }
    public int CallsProcessed { get; init; }
}

public sealed record ErrorEntry
{
    public required string Container { get; init; }
    public required string Message { get; init; }
}

/// <summary>Wraps all snapshots for the consolidated JSON output.</summary>
public sealed record AuditReport
{
    public required DateTime TestStarted { get; init; }
    public required DateTime TestEnded { get; init; }
    public required int IntervalSeconds { get; init; }
    public required int SnapshotCount { get; init; }
    public required AuditSnapshot[] Snapshots { get; init; }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build PbxAdmin.slnx -v q`
Expected: 0 warnings, 0 errors

- [ ] **Step 3: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Auditing/AuditSnapshot.cs
git commit -m "feat(audit): add snapshot POCO models for infrastructure auditing"
```

---

### Task 2: Asterisk CLI collector + tests

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Auditing/AsteriskCliCollector.cs`
- Create: `tests/PbxAdmin.Tests/LoadTests/AsteriskCliCollectorTests.cs`

- [ ] **Step 1: Write parser tests**

Create `tests/PbxAdmin.Tests/LoadTests/AsteriskCliCollectorTests.cs`:

```csharp
using FluentAssertions;
using PbxAdmin.LoadTests.Auditing;

namespace PbxAdmin.Tests.LoadTests;

public sealed class AsteriskCliCollectorTests
{
    // ── ParseChannelCount ──────────────────────────────────────────────────

    [Fact]
    public void ParseChannelCount_ShouldParseAllThreeValues()
    {
        string output = """
            950 active channels
            750 active calls
            1103 calls processed
            """;

        var result = AsteriskCliCollector.ParseChannelCount(output);

        result.ActiveChannels.Should().Be(950);
        result.ActiveCalls.Should().Be(750);
        result.CallsProcessed.Should().Be(1103);
    }

    [Fact]
    public void ParseChannelCount_ShouldReturnZeros_WhenOutputIsEmpty()
    {
        var result = AsteriskCliCollector.ParseChannelCount("");

        result.ActiveChannels.Should().Be(0);
        result.ActiveCalls.Should().Be(0);
        result.CallsProcessed.Should().Be(0);
    }

    // ── ParseOdbcShow ──────────────────────────────────────────────────────

    [Fact]
    public void ParseOdbcShow_ShouldParseActiveAndMax()
    {
        string output = """
            ODBC DSN Settings
            -----------------

              Name:   asterisk
              DSN:    asterisk-connector
                Number of active connections: 27 (out of 30)
                Cache Type: stack (last release, first re-use)
                Cache Usage: 27 cached out of 30
                Logging: Disabled
            """;

        var (active, max) = AsteriskCliCollector.ParseOdbcShow(output);

        active.Should().Be(27);
        max.Should().Be(30);
    }

    [Fact]
    public void ParseOdbcShow_ShouldReturnZeros_WhenOutputIsEmpty()
    {
        var (active, max) = AsteriskCliCollector.ParseOdbcShow("");

        active.Should().Be(0);
        max.Should().Be(0);
    }

    // ── ParseQueueShow ─────────────────────────────────────────────────────

    [Fact]
    public void ParseQueueShow_ShouldParseHeaderAndMemberCounts()
    {
        string output = """
            loadtest has 3 calls (max unlimited) in 'rrmemory' strategy (12s holdtime, 45s talktime), W:0, C:150, A:5, SL:95.0%, SL2:90.0% within 20s
               Members:
                  Agent 1 (PJSIP/2100) (Not in use)
                  Agent 2 (PJSIP/2101) (In use)
                  Agent 3 (PJSIP/2102) (Ringing)
                  Agent 4 (PJSIP/2103) (Unavailable)
                  Agent 5 (PJSIP/2104) (Not in use)
            """;

        var result = AsteriskCliCollector.ParseQueueShow(output);

        result.CallsWaiting.Should().Be(3);
        result.Completed.Should().Be(150);
        result.Abandoned.Should().Be(5);
        result.Holdtime.Should().Be(12);
        result.Talktime.Should().Be(45);
        result.MembersIdle.Should().Be(2);
        result.MembersInUse.Should().Be(1);
        result.MembersRinging.Should().Be(1);
        result.MembersUnavailable.Should().Be(1);
    }

    [Fact]
    public void ParseQueueShow_ShouldReturnDefaults_WhenOutputIsEmpty()
    {
        var result = AsteriskCliCollector.ParseQueueShow("");

        result.CallsWaiting.Should().Be(0);
        result.Completed.Should().Be(0);
    }

    // ── ParseEndpointCount ─────────────────────────────────────────────────

    [Fact]
    public void ParseEndpointCount_ShouldParseObjectsFound()
    {
        string output = """
             Endpoint:  2100/2100       Not in use    0 of inf
             Endpoint:  2101/2101       Not in use    0 of inf

            Objects found: 208
            """;

        AsteriskCliCollector.ParseEndpointCount(output).Should().Be(208);
    }

    [Fact]
    public void ParseEndpointCount_ShouldReturn0_WhenNoMatch()
    {
        AsteriskCliCollector.ParseEndpointCount("").Should().Be(0);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PbxAdmin.Tests/ --filter "AsteriskCliCollectorTests" -v q`
Expected: Build failure — `AsteriskCliCollector` doesn't exist yet

- [ ] **Step 3: Create AsteriskCliCollector**

Create `tests/PbxAdmin.LoadTests/Auditing/AsteriskCliCollector.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Metrics;

namespace PbxAdmin.LoadTests.Auditing;

/// <summary>
/// Collects Asterisk metrics by running <c>docker exec</c> CLI commands and parsing output.
/// All parse methods are <c>internal static</c> for unit testing.
/// </summary>
public sealed class AsteriskCliCollector
{
    private const int ProcessTimeoutMs = 5000;
    private readonly ILogger _logger;

    public AsteriskCliCollector(ILogger logger)
    {
        _logger = logger;
    }

    // ── Public collection methods ──────────────────────────────────────────

    public async Task<AsteriskSnapshot> CollectRealtimeAsync(string queueName, CancellationToken ct)
    {
        var channelsTask = RunDockerExecAsync(DockerContainerNames.PbxRealtime, "core show channels count", ct);
        var odbcTask = RunDockerExecAsync(DockerContainerNames.PbxRealtime, "odbc show", ct);
        var queueTask = RunDockerExecAsync(DockerContainerNames.PbxRealtime, $"queue show {queueName}", ct);
        var endpointsTask = RunDockerExecAsync(DockerContainerNames.PbxRealtime, "pjsip show endpoints", ct);
        var rtpTask = RunDockerExecAsync(DockerContainerNames.PbxRealtime, "rtp show stats", ct);

        await Task.WhenAll(channelsTask, odbcTask, queueTask, endpointsTask, rtpTask);

        var channels = ParseChannelCount(await channelsTask);
        var (odbcActive, odbcMax) = ParseOdbcShow(await odbcTask);
        var queue = ParseQueueShow(await queueTask);
        int endpoints = ParseEndpointCount(await endpointsTask);
        string? rtpOutput = (await rtpTask)?.Trim();
        if (string.IsNullOrEmpty(rtpOutput)) rtpOutput = null;

        return new AsteriskSnapshot
        {
            ActiveChannels = channels.ActiveChannels,
            ActiveCalls = channels.ActiveCalls,
            CallsProcessed = channels.CallsProcessed,
            OdbcActiveConnections = odbcActive,
            OdbcMaxConnections = odbcMax,
            EndpointCount = endpoints,
            Queue = queue,
            RtpRawOutput = rtpOutput
        };
    }

    public async Task<AsteriskBasicSnapshot> CollectBasicAsync(string containerName, CancellationToken ct)
    {
        string output = await RunDockerExecAsync(containerName, "core show channels count", ct);
        var parsed = ParseChannelCount(output);
        return new AsteriskBasicSnapshot
        {
            ActiveChannels = parsed.ActiveChannels,
            ActiveCalls = parsed.ActiveCalls,
            CallsProcessed = parsed.CallsProcessed
        };
    }

    // ── Internal static parsers (testable) ─────────────────────────────────

    internal static AsteriskBasicSnapshot ParseChannelCount(string output)
    {
        int channels = 0, calls = 0, processed = 0;

        if (string.IsNullOrWhiteSpace(output))
            return new AsteriskBasicSnapshot();

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Contains("active channels", StringComparison.Ordinal))
                int.TryParse(ExtractLeadingInt(trimmed), out channels);
            else if (trimmed.Contains("active calls", StringComparison.Ordinal))
                int.TryParse(ExtractLeadingInt(trimmed), out calls);
            else if (trimmed.Contains("calls processed", StringComparison.Ordinal))
                int.TryParse(ExtractLeadingInt(trimmed), out processed);
        }

        return new AsteriskBasicSnapshot
        {
            ActiveChannels = channels,
            ActiveCalls = calls,
            CallsProcessed = processed
        };
    }

    internal static (int Active, int Max) ParseOdbcShow(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (0, 0);

        // "Number of active connections: 27 (out of 30)"
        var match = Regex.Match(output, @"active connections:\s*(\d+)\s*\(out of\s*(\d+)\)");
        if (match.Success)
        {
            int active = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int max = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            return (active, max);
        }

        return (0, 0);
    }

    internal static QueueSnapshot ParseQueueShow(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new QueueSnapshot();

        int callsWaiting = 0, completed = 0, abandoned = 0, holdtime = 0, talktime = 0;
        int idle = 0, inUse = 0, ringing = 0, unavailable = 0;

        // Header: "loadtest has 3 calls ... (12s holdtime, 45s talktime), W:0, C:150, A:5"
        var headerMatch = Regex.Match(output, @"has\s+(\d+)\s+calls");
        if (headerMatch.Success)
            callsWaiting = int.Parse(headerMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var holdMatch = Regex.Match(output, @"(\d+)s holdtime");
        if (holdMatch.Success)
            holdtime = int.Parse(holdMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var talkMatch = Regex.Match(output, @"(\d+)s talktime");
        if (talkMatch.Success)
            talktime = int.Parse(talkMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var cMatch = Regex.Match(output, @"C:(\d+)");
        if (cMatch.Success)
            completed = int.Parse(cMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var aMatch = Regex.Match(output, @"A:(\d+)");
        if (aMatch.Success)
            abandoned = int.Parse(aMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        // Member status counts
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Contains("(Not in use)", StringComparison.Ordinal)) idle++;
            else if (trimmed.Contains("(In use)", StringComparison.Ordinal)) inUse++;
            else if (trimmed.Contains("(Ringing)", StringComparison.Ordinal)) ringing++;
            else if (trimmed.Contains("(Unavailable)", StringComparison.Ordinal)) unavailable++;
        }

        return new QueueSnapshot
        {
            CallsWaiting = callsWaiting,
            Completed = completed,
            Abandoned = abandoned,
            Holdtime = holdtime,
            Talktime = talktime,
            MembersIdle = idle,
            MembersInUse = inUse,
            MembersRinging = ringing,
            MembersUnavailable = unavailable
        };
    }

    internal static int ParseEndpointCount(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return 0;

        var match = Regex.Match(output, @"Objects found:\s*(\d+)");
        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static string ExtractLeadingInt(string line)
    {
        var match = Regex.Match(line, @"^(\d+)");
        return match.Success ? match.Groups[1].Value : "0";
    }

    private async Task<string> RunDockerExecAsync(string container, string asteriskCmd, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"exec {container} asterisk -rx \"{asteriskCmd}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return "";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProcessTimeoutMs);

            string output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                _logger.LogWarning("docker exec timed out: {Container} {Cmd}", container, asteriskCmd);
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "docker exec failed: {Container} {Cmd}", container, asteriskCmd);
            return "";
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PbxAdmin.Tests/ --filter "AsteriskCliCollectorTests" -v q`
Expected: 8 tests pass

- [ ] **Step 5: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Auditing/AsteriskCliCollector.cs tests/PbxAdmin.Tests/LoadTests/AsteriskCliCollectorTests.cs
git commit -m "feat(audit): add Asterisk CLI collector with parser tests

Collects channels, ODBC pool, queue stats, endpoints, and RTP
via docker exec. All parsers are internal static for testability."
```

---

### Task 3: Container log collector + tests

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Auditing/ContainerLogCollector.cs`
- Create: `tests/PbxAdmin.Tests/LoadTests/ContainerLogCollectorTests.cs`

- [ ] **Step 1: Write parser tests**

Create `tests/PbxAdmin.Tests/LoadTests/ContainerLogCollectorTests.cs`:

```csharp
using FluentAssertions;
using PbxAdmin.LoadTests.Auditing;

namespace PbxAdmin.Tests.LoadTests;

public sealed class ContainerLogCollectorTests
{
    [Fact]
    public void FilterErrors_ShouldExtractErrorAndFatalLines()
    {
        string output = """
            [Mar 28 13:12:56] -- Remote UNIX connection
            [Mar 28 13:12:56] -- Remote UNIX connection disconnected
            [Mar 28 13:13:02] ERROR: res_pjsip_outbound_authenticator_digest.c:504 no auth ids
            [Mar 28 13:13:05] -- Accepting connection
            [Mar 28 13:13:07] FATAL: something terrible
            [Mar 28 13:13:08] WARNING: something concerning
            """;

        var errors = ContainerLogCollector.FilterErrors(output, "demo-pbx-realtime");

        errors.Should().HaveCount(3);
        errors[0].Container.Should().Be("demo-pbx-realtime");
        errors[0].Message.Should().Contain("ERROR");
        errors[1].Message.Should().Contain("FATAL");
        errors[2].Message.Should().Contain("WARNING");
    }

    [Fact]
    public void FilterErrors_ShouldReturnEmpty_WhenNoErrors()
    {
        string output = """
            [Mar 28 13:12:56] -- Remote UNIX connection
            [Mar 28 13:12:56] -- Remote UNIX connection disconnected
            """;

        var errors = ContainerLogCollector.FilterErrors(output, "demo-pstn");

        errors.Should().BeEmpty();
    }

    [Fact]
    public void FilterErrors_ShouldReturnEmpty_WhenOutputIsEmpty()
    {
        ContainerLogCollector.FilterErrors("", "demo-pstn").Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PbxAdmin.Tests/ --filter "ContainerLogCollectorTests" -v q`
Expected: Build failure — `ContainerLogCollector` doesn't exist yet

- [ ] **Step 3: Create ContainerLogCollector**

Create `tests/PbxAdmin.LoadTests/Auditing/ContainerLogCollector.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Metrics;

namespace PbxAdmin.LoadTests.Auditing;

/// <summary>
/// Captures ERROR, FATAL, and WARNING log lines from Docker containers
/// using <c>docker logs --since</c>.
/// </summary>
public sealed class ContainerLogCollector
{
    private const int ProcessTimeoutMs = 5000;
    private readonly ILogger _logger;
    private DateTime _lastCollected = DateTime.UtcNow;

    public ContainerLogCollector(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Collects new error/warning log lines from all monitored containers
    /// since the last collection.
    /// </summary>
    public async Task<ErrorEntry[]> CollectNewErrorsAsync(CancellationToken ct)
    {
        string since = _lastCollected.ToString("yyyy-MM-ddTHH:mm:ssZ");
        _lastCollected = DateTime.UtcNow;

        var tasks = DockerContainerNames.All.Select(container =>
            CollectContainerLogsAsync(container, since, ct));

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToArray();
    }

    /// <summary>Filters log output for ERROR, FATAL, and WARNING lines. Testable.</summary>
    internal static ErrorEntry[] FilterErrors(string output, string containerName)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var errors = new List<ErrorEntry>();

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("WARNING", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ErrorEntry
                {
                    Container = containerName,
                    Message = trimmed
                });
            }
        }

        return errors.ToArray();
    }

    private async Task<ErrorEntry[]> CollectContainerLogsAsync(
        string containerName, string since, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"logs --since {since} {containerName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return [];

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProcessTimeoutMs);

            // Docker logs writes to stderr for container output
            string stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
            string stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }

            string combined = stdout + "\n" + stderr;
            return FilterErrors(combined, containerName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "docker logs failed for {Container}", containerName);
            return [];
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PbxAdmin.Tests/ --filter "ContainerLogCollectorTests" -v q`
Expected: 3 tests pass

- [ ] **Step 5: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Auditing/ContainerLogCollector.cs tests/PbxAdmin.Tests/LoadTests/ContainerLogCollectorTests.cs
git commit -m "feat(audit): add container log collector for error capture

Collects ERROR, FATAL, WARNING lines from all containers via
docker logs --since. Uses incremental collection between snapshots."
```

---

### Task 4: AuditMonitorService (orchestrator + output)

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Auditing/AuditMonitorService.cs`

- [ ] **Step 1: Create the service**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Metrics;

namespace PbxAdmin.LoadTests.Auditing;

/// <summary>
/// Background service that periodically snapshots all Docker stack components
/// and writes results to JSONL (streaming) and JSON (consolidated) files.
/// </summary>
public sealed class AuditMonitorService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions JsonPrettyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly ILogger<AuditMonitorService> _logger;
    private readonly AsteriskCliCollector _asteriskCollector;
    private readonly ContainerLogCollector _logCollector;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _startedAt;
    private string _jsonlPath = "";
    private string _jsonPath = "";
    private int _intervalSeconds;
    private int _sequenceNumber;
    private readonly List<AuditSnapshot> _snapshots = [];
    private readonly string _queueName;

    public int SnapshotCount => _snapshots.Count;
    public int TotalErrors => _snapshots.Sum(s => s.Errors.Length);

    public AuditMonitorService(ILoggerFactory loggerFactory, string queueName = "loadtest")
    {
        _logger = loggerFactory.CreateLogger<AuditMonitorService>();
        _asteriskCollector = new AsteriskCliCollector(
            loggerFactory.CreateLogger<AsteriskCliCollector>());
        _logCollector = new ContainerLogCollector(
            loggerFactory.CreateLogger<ContainerLogCollector>());
        _queueName = queueName;
    }

    /// <summary>Starts the background audit loop.</summary>
    public Task StartAsync(int intervalSeconds, string outputBasePath, CancellationToken ct)
    {
        if (intervalSeconds <= 0)
        {
            _logger.LogInformation("Audit monitor disabled (interval=0)");
            return Task.CompletedTask;
        }

        _intervalSeconds = Math.Max(intervalSeconds, 5);
        _jsonlPath = outputBasePath + ".audit.jsonl";
        _jsonPath = outputBasePath + ".audit.json";
        _startedAt = DateTime.UtcNow;
        _sequenceNumber = 0;

        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunLoopAsync(_cts.Token);

        _logger.LogInformation(
            "Audit monitor started: interval={Interval}s, jsonl={Jsonl}, json={Json}",
            _intervalSeconds, _jsonlPath, _jsonPath);

        return Task.CompletedTask;
    }

    /// <summary>Stops the loop and writes the consolidated JSON report.</summary>
    public async Task<AuditReport> StopAsync()
    {
        if (_cts is not null)
            await _cts.CancelAsync();

        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
            _loop = null;
        }

        _cts?.Dispose();
        _cts = null;

        var report = new AuditReport
        {
            TestStarted = _startedAt,
            TestEnded = DateTime.UtcNow,
            IntervalSeconds = _intervalSeconds,
            SnapshotCount = _snapshots.Count,
            Snapshots = _snapshots.ToArray()
        };

        // Write consolidated JSON
        if (!string.IsNullOrEmpty(_jsonPath) && _snapshots.Count > 0)
        {
            try
            {
                string json = JsonSerializer.Serialize(report, JsonPrettyOptions);
                await File.WriteAllTextAsync(_jsonPath, json);
                _logger.LogInformation("Audit report written: {Path} ({Count} snapshots)",
                    _jsonPath, _snapshots.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write consolidated audit JSON");
            }
        }

        return report;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    // ── Background loop ───────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var snapshot = await CollectSnapshotAsync(ct);
                _snapshots.Add(snapshot);
                await AppendJsonlAsync(snapshot);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit snapshot #{Seq} failed", _sequenceNumber);
            }
        }
    }

    private async Task<AuditSnapshot> CollectSnapshotAsync(CancellationToken ct)
    {
        _sequenceNumber++;
        double elapsed = (DateTime.UtcNow - _startedAt).TotalSeconds;

        // Collect all in parallel
        var dockerStatsTask = CollectDockerStatsAsync(ct);
        var realtimeTask = _asteriskCollector.CollectRealtimeAsync(_queueName, ct);
        var pstnTask = _asteriskCollector.CollectBasicAsync(DockerContainerNames.Pstn, ct);
        var fileTask = _asteriskCollector.CollectBasicAsync(DockerContainerNames.PbxFile, ct);
        var errorsTask = _logCollector.CollectNewErrorsAsync(ct);

        await Task.WhenAll(dockerStatsTask, realtimeTask, pstnTask, fileTask, errorsTask);

        return new AuditSnapshot
        {
            Timestamp = DateTime.UtcNow,
            ElapsedSeconds = elapsed,
            SequenceNumber = _sequenceNumber,
            Containers = await dockerStatsTask,
            Realtime = await realtimeTask,
            Pstn = await pstnTask,
            File = await fileTask,
            Errors = await errorsTask
        };
    }

    private async Task<ContainerSnapshot[]> CollectDockerStatsAsync(CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"stats --no-stream --format \"{{{{.Name}}}}|{{{{.CPUPerc}}}}|{{{{.MemUsage}}}}|{{{{.NetIO}}}}\" {string.Join(' ', DockerContainerNames.All)}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return [];

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(10_000);

            string output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }

            return ParseDockerStats(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "docker stats collection failed");
            return [];
        }
    }

    /// <summary>Parses docker stats --format "{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}|{{.NetIO}}" output.</summary>
    internal static ContainerSnapshot[] ParseDockerStats(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var snapshots = new List<ContainerSnapshot>();

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            string[] parts = trimmed.Split('|');
            if (parts.Length < 4)
                continue;

            string name = parts[0].Trim();
            double cpu = DockerStatsSample.ParsePercent(parts[1]);

            // MemUsage: "83.71MiB / 60.49GiB"
            string[] memParts = parts[2].Split(" / ");
            double memUsage = memParts.Length >= 1
                ? DockerStatsSample.ParseByteSize(memParts[0].Trim()) / 1_048_576.0
                : 0;
            double memLimit = memParts.Length >= 2
                ? DockerStatsSample.ParseByteSize(memParts[1].Trim()) / 1_048_576.0
                : 0;

            // NetIO: "56.8MB / 111MB"
            string[] netParts = parts[3].Split(" / ");
            double netIn = netParts.Length >= 1
                ? DockerStatsSample.ParseByteSize(netParts[0].Trim()) / 1_048_576.0
                : 0;
            double netOut = netParts.Length >= 2
                ? DockerStatsSample.ParseByteSize(netParts[1].Trim()) / 1_048_576.0
                : 0;

            snapshots.Add(new ContainerSnapshot
            {
                Name = name,
                CpuPercent = cpu,
                MemUsageMB = Math.Round(memUsage, 2),
                MemLimitMB = Math.Round(memLimit, 2),
                NetInputMB = Math.Round(netIn, 2),
                NetOutputMB = Math.Round(netOut, 2)
            });
        }

        return snapshots.ToArray();
    }

    private async Task AppendJsonlAsync(AuditSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(_jsonlPath))
            return;

        try
        {
            string line = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.AppendAllTextAsync(_jsonlPath, line + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append audit JSONL");
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build PbxAdmin.slnx -v q`
Expected: 0 warnings, 0 errors

- [ ] **Step 3: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Auditing/AuditMonitorService.cs
git commit -m "feat(audit): add AuditMonitorService with background loop and JSONL/JSON output

Orchestrates parallel collection of Docker stats, Asterisk CLI metrics,
and container error logs. Writes streaming JSONL during test, consolidated
JSON at completion."
```

---

### Task 5: Integrate into Program.cs

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/Program.cs`

- [ ] **Step 1: Add --audit-interval CLI option**

After line 57 (the `maxConcurrentOption` definition), add:

```csharp
var auditIntervalOption = new Option<int>("--audit-interval")
{
    Description = "Audit snapshot interval in seconds (0 to disable, min 5)",
    DefaultValueFactory = _ => 10
};
```

Add it to `rootCommand` (after `maxConcurrentOption`):

```csharp
var rootCommand = new RootCommand("PbxAdmin SDK Test Platform — Asterisk load and validation harness")
{
    scenarioOption,
    agentsOption,
    targetOption,
    durationOption,
    outputOption,
    talkTimeOption,
    maxConcurrentOption,
    auditIntervalOption
};
```

In `SetAction`, after `int? maxConcurrent = ...`:

```csharp
    int auditInterval = parseResult.GetValue(auditIntervalOption);
```

Update `RunAsync` call to pass `auditInterval`:

```csharp
    Environment.ExitCode = await RunAsync(scenario, agents, target, duration, output, talkTime, maxConcurrent, auditInterval, ct);
```

- [ ] **Step 2: Update RunAsync signature**

Change `RunAsync` signature to:

```csharp
static async Task<int> RunAsync(
    string scenario,
    int agents,
    string target,
    int durationMinutes,
    string? outputPath,
    int? talkTime,
    int? maxConcurrent,
    int auditIntervalSecs,
    CancellationToken ct)
```

- [ ] **Step 3: Add auditor start/stop**

Add `using PbxAdmin.LoadTests.Auditing;` to the top of the file.

After `var context = BuildTestContext(host, loggerFactory);` (line 166), add:

```csharp
    // Infrastructure auditor — runs alongside the test
    AuditMonitorService? auditor = null;
    if (auditIntervalSecs > 0 && !string.IsNullOrWhiteSpace(outputPath))
    {
        auditor = new AuditMonitorService(loggerFactory);
        await auditor.StartAsync(auditIntervalSecs, outputPath, cts.Token);
    }
```

In the `finally` block, before the existing cleanup, add:

```csharp
        if (auditor is not null)
            try { await auditor.DisposeAsync(); } catch { /* best-effort */ }
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build PbxAdmin.slnx -v q && dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: 0 warnings, all tests pass

- [ ] **Step 5: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Program.cs
git commit -m "feat(audit): integrate AuditMonitorService into load test CLI

Adds --audit-interval flag (default 10s, 0=disabled). Auditor starts
after test context is built and stops in finally block. Outputs
{outputPath}.audit.jsonl and {outputPath}.audit.json."
```

---

### Task 6: Build and full test pass

- [ ] **Step 1: Full build**

Run: `dotnet build PbxAdmin.slnx -v q`
Expected: 0 warnings, 0 errors

- [ ] **Step 2: Full test suite**

Run: `dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: All tests pass (681 + 11 new = 692)

- [ ] **Step 3: Verify CLI help shows new option**

Run: `dotnet tests/PbxAdmin.LoadTests/bin/Debug/net10.0/PbxAdmin.LoadTests.dll --help`
Expected: Output includes `--audit-interval` with description
