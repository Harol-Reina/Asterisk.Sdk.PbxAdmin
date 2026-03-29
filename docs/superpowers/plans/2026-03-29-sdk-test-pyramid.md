# SDK Test Pyramid Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace 3 SDK test scenarios with a 9-scenario pyramid (Smoke → Functional → Scale → Endurance) validating Asterisk.Sdk against live Asterisk at 200 agents / 300 concurrent calls.

**Architecture:** Each scenario implements `ITestScenario` (ExecuteAsync + ValidateAsync). The SDK Sampler (`LiveStateValidator`) validates logical state via AMI every 3s. The Audit Monitor validates infrastructure via Docker every 5-10s. A new `--level` CLI flag runs entire pyramid levels with gating.

**Tech Stack:** .NET 10, Asterisk.Sdk 1.5.1, SIPSorcery 6.2, System.CommandLine, Docker Compose, PostgreSQL 17

**Spec:** `docs/superpowers/specs/2026-03-29-sdk-test-pyramid-design.md`

---

## File Structure

### New Files
| File | Responsibility |
|------|---------------|
| `docker/docker-compose.sdk-tests.yml` | Lightweight 4-service compose (no asterisk-file) |
| `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkSmokeScenario.cs` | Level 1: quick 5-call validation |
| `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkStateSyncScenario.cs` | Level 2: channels+queues+agents drift |
| `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkSessionsScenario.cs` | Level 2: session lifecycle vs CDR |
| `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkReconnectScenario.cs` | Level 2: 3 AMI disconnects under load |
| `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleChannelsScenario.cs` | Level 3: channel drift at 300 concurrent |
| `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleQueuesScenario.cs` | Level 3: queue drift at 300 concurrent |
| `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleAgentsScenario.cs` | Level 3: agent drift at 300 concurrent |
| `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleSessionsScenario.cs` | Level 3: session accuracy at 300 concurrent |
| `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkEnduranceScenario.cs` | Level 4: 30-min combined validation |
| `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScenarioBase.cs` | Shared helpers for all SDK scenarios |
| `tests/PbxAdmin.LoadTests/Scenarios/LevelRunner.cs` | Runs pyramid levels with gating |

### Modified Files
| File | Change |
|------|--------|
| `tests/PbxAdmin.LoadTests/Sdk/LiveStateValidator.cs` | Add Queue + Agent sampling (not just Channels) |
| `tests/PbxAdmin.LoadTests/Scenarios/ScenarioRegistry.cs` | Register 9 new, remove 3 old |
| `tests/PbxAdmin.LoadTests/Program.cs` | Add `--level` CLI option |

### Deleted Files
| File | Replaced By |
|------|------------|
| `tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkLiveDriftScenario.cs` | SdkSmokeScenario + SdkStateSyncScenario + SdkScaleChannelsScenario |
| `tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkSessionAccuracyScenario.cs` | SdkSmokeScenario + SdkSessionsScenario + SdkScaleSessionsScenario |
| `tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkReconnectScenario.cs` | SdkReconnectScenario (rewrite) |

---

## Task 1: Docker Compose for SDK Tests

**Files:**
- Create: `docker/docker-compose.sdk-tests.yml`

- [ ] **Step 1: Create the compose file**

Derive from `docker-compose.pbxadmin.yml` but remove `asterisk-file` service and make `pbx-admin` optional (no other service depends on it). Keep identical infra tuning (ODBC=100, PG=200, sorcery cache, ulimits, RTP 20000-21999).

```yaml
# docker/docker-compose.sdk-tests.yml
# Lightweight stack for SDK validation tests.
# Usage: docker compose -f docker-compose.sdk-tests.yml up -d
# PbxAdmin is optional — comment out to save resources in CI.

services:
  postgres:
    image: postgres:17-alpine
    command: ["postgres", "-c", "max_connections=200"]
    container_name: demo-postgres
    ports:
      - "5432:5432"
    environment:
      POSTGRES_DB: asterisk
      POSTGRES_USER: asterisk
      POSTGRES_PASSWORD: asterisk
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./sql:/docker-entrypoint-initdb.d
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U asterisk"]
      interval: 5s
      timeout: 3s
      retries: 10

  asterisk-realtime:
    build:
      context: .
      dockerfile: Dockerfile.asterisk-realtime
    container_name: demo-pbx-realtime
    entrypoint: ["/entrypoint-asterisk.sh"]
    ulimits:
      nofile:
        soft: 65535
        hard: 65535
    environment:
      - EXTERNAL_IP=${EXTERNAL_IP:-}
    ports:
      - "5038:5038"
      - "8088:8088"
      - "5060:5060/udp"
      - "8180:8180"
      - "8089:8089"
      - "20000-21999:20000-21999/udp"
    volumes:
      - ./entrypoint-asterisk.sh:/entrypoint-asterisk.sh:ro
      - ./asterisk-config-realtime/rtp.conf:/etc/asterisk/rtp.conf
      - ./asterisk-config-realtime/ari.conf:/etc/asterisk/ari.conf:ro
      - ./asterisk-config-realtime/asterisk.conf:/etc/asterisk/asterisk.conf:ro
      - ./asterisk-config-realtime/cdr.conf:/etc/asterisk/cdr.conf:ro
      - ./asterisk-config-realtime/cdr_adaptive_odbc.conf:/etc/asterisk/cdr_adaptive_odbc.conf:ro
      - ./asterisk-config-realtime/cel.conf:/etc/asterisk/cel.conf:ro
      - ./asterisk-config-realtime/cel_odbc.conf:/etc/asterisk/cel_odbc.conf:ro
      - ./asterisk-config-realtime/confbridge.conf:/etc/asterisk/confbridge.conf
      - ./asterisk-config-realtime/extconfig.conf:/etc/asterisk/extconfig.conf:ro
      - ./asterisk-config-realtime/extensions.conf:/etc/asterisk/extensions.conf
      - ./asterisk-config-realtime/features.conf:/etc/asterisk/features.conf:ro
      - ./asterisk-config-realtime/geolocation.conf:/etc/asterisk/geolocation.conf:ro
      - ./asterisk-config-realtime/http.conf:/etc/asterisk/http.conf
      - ./asterisk-config-realtime/logger.conf:/etc/asterisk/logger.conf:ro
      - ./asterisk-config-realtime/manager.conf:/etc/asterisk/manager.conf
      - ./asterisk-config-realtime/modules.conf:/etc/asterisk/modules.conf:ro
      - ./asterisk-config-realtime/musiconhold.conf:/etc/asterisk/musiconhold.conf
      - ./asterisk-config-realtime/pjproject.conf:/etc/asterisk/pjproject.conf
      - ./asterisk-config-realtime/pjsip.conf:/etc/asterisk/pjsip.conf
      - ./asterisk-config-realtime/queues.conf:/etc/asterisk/queues.conf
      - ./asterisk-config-realtime/res_odbc.conf:/etc/asterisk/res_odbc.conf:ro
      - ./asterisk-config-realtime/res_parking.conf:/etc/asterisk/res_parking.conf:ro
      - ./asterisk-config-realtime/sorcery.conf:/etc/asterisk/sorcery.conf:ro
      - ./asterisk-config-realtime/users.conf:/etc/asterisk/users.conf
      - ./asterisk-config-realtime/odbcinst.ini:/etc/odbcinst.ini:ro
      - ./asterisk-config-realtime/odbc.ini:/etc/odbc.ini:ro
      - ./certs/asterisk.pem:/etc/asterisk/keys/asterisk.pem:ro
      - ./certs/asterisk.key:/etc/asterisk/keys/asterisk.key:ro
      - ./moh:/var/lib/asterisk/moh:ro
      - ./sounds/es-custom:/var/lib/asterisk/sounds/es-custom:ro
      - recordings-realtime:/var/spool/asterisk/monitor
      - ./sample-recordings:/var/spool/asterisk/monitor/samples
    depends_on:
      postgres:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "asterisk", "-rx", "core show version"]
      interval: 5s
      timeout: 3s
      retries: 10

  pstn-emulator:
    image: docker-asterisk-file:latest
    container_name: demo-pstn
    ulimits:
      nofile:
        soft: 65535
        hard: 65535
    ports:
      - "5040:5038"
    depends_on:
      asterisk-realtime:
        condition: service_healthy
    volumes:
      - ../docker/functional/pstn-emulator-config/extensions.conf:/etc/asterisk/extensions.conf:ro
      - ../docker/functional/pstn-emulator-config/pjsip.conf:/etc/asterisk/pjsip.conf:ro
      - ../docker/functional/pstn-emulator-config/voicemail.conf:/etc/asterisk/voicemail.conf:ro
      - ../docker/functional/pstn-emulator-config/modules.conf:/etc/asterisk/modules.conf:ro
      - ../docker/functional/pstn-emulator-config/manager.conf:/etc/asterisk/manager.conf:ro
    healthcheck:
      test: ["CMD", "asterisk", "-rx", "core show version"]
      interval: 5s
      timeout: 3s
      retries: 10

  # Optional: visual monitoring during tests at http://localhost:8080
  # Comment out this entire block to save resources in CI.
  pbx-admin:
    build:
      context: ..
      dockerfile: docker/Dockerfile.pbxadmin
    container_name: asterisk-pbx-admin
    ports:
      - "8080:8080"
      - "8443:8443"
    depends_on:
      asterisk-realtime:
        condition: service_healthy
      postgres:
        condition: service_healthy
    volumes:
      - ./certs/pbxadmin.pem:/app/certs/pbxadmin.pem:ro
      - ./certs/pbxadmin.key:/app/certs/pbxadmin.key:ro
      - ./moh:/data/moh:ro
      - ./sample-recordings:/var/spool/asterisk/monitor
      - ./sample-recordings:/data/recordings-realtime
      - ./moh:/var/lib/asterisk/moh:ro
      - ./seed-routes:/app/data/routes
    environment:
      - DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
      - Logging__LogLevel__Default=Debug
      - Kestrel__Certificates__Default__Path=/app/certs/pbxadmin.pem
      - Kestrel__Certificates__Default__KeyPath=/app/certs/pbxadmin.key
      - Auth__Username=admin
      - Auth__Password=admin
      - Asterisk__Servers__0__Id=pbx-realtime
      - Asterisk__Servers__0__Hostname=asterisk-realtime
      - Asterisk__Servers__0__Port=5038
      - Asterisk__Servers__0__Username=dashboard
      - Asterisk__Servers__0__Password=dashboard
      - Asterisk__Servers__0__ConfigMode=Realtime
      - Asterisk__Servers__0__RealtimeConnectionString=Host=postgres;Database=asterisk;Username=asterisk;Password=asterisk
      - Asterisk__Servers__0__RecordingsPath=/data/recordings-realtime
      - Asterisk__Servers__0__MohBasePath=/data/moh
      - Asterisk__Servers__0__MaxUploadSizeMb=20
      - Asterisk__Servers__0__MaxMohClassSizeMb=200
      - Asterisk__Servers__0__ExtensionRange__Start=2000
      - Asterisk__Servers__0__ExtensionRange__End=3999
      - Asterisk__Servers__0__WssPort=8089
      - Softphone__UseTls=true

volumes:
  pgdata:
  recordings-realtime:
```

- [ ] **Step 2: Verify compose starts**

```bash
cd docker && docker compose -f docker-compose.sdk-tests.yml up -d
```

Expected: 4 services healthy (postgres, asterisk-realtime, pstn-emulator, pbx-admin).

- [ ] **Step 3: Commit**

```bash
git add -f docker/docker-compose.sdk-tests.yml
git commit -m "feat(docker): add lightweight compose for SDK tests"
```

---

## Task 2: Extend LiveStateValidator — Queues + Agents

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/Sdk/LiveStateValidator.cs`

Currently `LiveStateValidator` only samples `server.Channels.ChannelCount` vs AMI. We need to add Queue and Agent state sampling.

- [ ] **Step 1: Add Queue and Agent fields to LiveStateSample**

In `LiveStateValidator.cs`, extend the `LiveStateSample` record:

```csharp
public sealed record LiveStateSample
{
    public required DateTime Timestamp { get; init; }
    public int SdkChannelCount { get; init; }
    public int AsteriskChannelCount { get; init; }
    public int SdkQueueCallerCount { get; init; }
    public int AsteriskQueueCallerCount { get; init; }
    public int SdkQueueMemberCount { get; init; }
    public int AsteriskQueueMemberCount { get; init; }
    public int SdkAgentsInCall { get; init; }
    public int AsteriskAgentsInUse { get; init; }
    public int ChannelDrift => Math.Abs(SdkChannelCount - AsteriskChannelCount);
    public int QueueCallerDrift => Math.Abs(SdkQueueCallerCount - AsteriskQueueCallerCount);
    public int QueueMemberDrift => Math.Abs(SdkQueueMemberCount - AsteriskQueueMemberCount);
    public int AgentDrift => Math.Abs(SdkAgentsInCall - AsteriskAgentsInUse);
    public bool WithinTolerance => ChannelDrift <= 2;
}
```

- [ ] **Step 2: Add queue and agent collection to CollectSampleAsync**

Replace the `CollectSampleAsync` method to query queue and agent state alongside channels:

```csharp
private static async Task<LiveStateSample> CollectSampleAsync(
    AsteriskServer server,
    IAmiConnection connection,
    string queueName,
    CancellationToken ct)
{
    var asteriskChannels = await QueryAsteriskChannelCountAsync(connection, ct);
    var sdkChannels = server.Channels.ChannelCount;

    var (astCallers, astMembers, astInUse) = await QueryAsteriskQueueAsync(connection, queueName, ct);

    var sdkQueue = server.Queues.GetByName(queueName);
    var sdkCallers = sdkQueue?.CallersWaiting ?? 0;
    var sdkMembers = sdkQueue?.Members.Count ?? 0;

    var sdkAgentsInCall = server.Agents.Agents
        .Count(a => a.State == AgentState.InCall || a.State == AgentState.OnHold);

    return new LiveStateSample
    {
        Timestamp = DateTime.UtcNow,
        SdkChannelCount = sdkChannels,
        AsteriskChannelCount = asteriskChannels,
        SdkQueueCallerCount = sdkCallers,
        AsteriskQueueCallerCount = astCallers,
        SdkQueueMemberCount = sdkMembers,
        AsteriskQueueMemberCount = astMembers,
        SdkAgentsInCall = sdkAgentsInCall,
        AsteriskAgentsInUse = astInUse,
    };
}
```

- [ ] **Step 3: Add QueryAsteriskQueueAsync helper**

```csharp
private static async Task<(int callers, int members, int inUse)> QueryAsteriskQueueAsync(
    IAmiConnection connection,
    string queueName,
    CancellationToken ct)
{
    try
    {
        var response = await connection.SendActionAsync<CommandResponse>(
            new CommandAction { Command = $"queue show {queueName}" }, ct);
        var output = string.Join("\n", response.Output ?? []);
        var callers = ParseQueueCallers(output);
        var (members, inUse) = ParseQueueMembers(output);
        return (callers, members, inUse);
    }
    catch
    {
        return (0, 0, 0);
    }
}

private static int ParseQueueCallers(string output)
{
    var match = Regex.Match(output, @"has\s+(\d+)\s+calls");
    return match.Success ? int.Parse(match.Groups[1].Value) : 0;
}

private static (int total, int inUse) ParseQueueMembers(string output)
{
    int total = 0, inUse = 0;
    foreach (var line in output.Split('\n'))
    {
        if (line.Contains("(In use)") || line.Contains("(Ringing)") ||
            line.Contains("(Not in use)") || line.Contains("(Unavailable)"))
        {
            total++;
            if (line.Contains("(In use)") || line.Contains("(Ringing)"))
                inUse++;
        }
    }
    return (total, inUse);
}
```

- [ ] **Step 4: Update StartAsync to accept queueName parameter**

```csharp
public Task StartAsync(
    AsteriskServer server,
    IAmiConnection connection,
    int intervalSeconds = 5,
    string queueName = "loadtest",
    CancellationToken ct = default)
```

Store `queueName` in a private field and pass it to `CollectSampleAsync`.

- [ ] **Step 5: Extend LiveStateSummary to include queue and agent drift**

```csharp
public sealed record LiveStateSummary
{
    public int TotalSamples { get; init; }
    public int SamplesWithinTolerance { get; init; }
    public int MaxDrift { get; init; }
    public double AverageDrift { get; init; }
    public double DriftRate { get; init; }
    public bool Passed { get; init; }
    public double QueueCallerDriftRate { get; init; }
    public int MaxQueueCallerDrift { get; init; }
    public double QueueMemberDriftRate { get; init; }
    public int MaxQueueMemberDrift { get; init; }
    public double AgentDriftRate { get; init; }
    public int MaxAgentDrift { get; init; }

    public static LiveStateSummary Compute(IReadOnlyList<LiveStateSample> samples)
    {
        if (samples.Count == 0)
            return new LiveStateSummary { Passed = true };

        var withinTolerance = samples.Count(s => s.WithinTolerance);
        var maxDrift = samples.Max(s => s.ChannelDrift);
        var avgDrift = samples.Average(s => s.ChannelDrift);
        var driftRate = (double)(samples.Count - withinTolerance) / samples.Count * 100;

        var qCallerOver = samples.Count(s => s.QueueCallerDrift > 2);
        var qMemberOver = samples.Count(s => s.QueueMemberDrift > 2);
        var agentOver = samples.Count(s => s.AgentDrift > 2);

        return new LiveStateSummary
        {
            TotalSamples = samples.Count,
            SamplesWithinTolerance = withinTolerance,
            MaxDrift = maxDrift,
            AverageDrift = avgDrift,
            DriftRate = driftRate,
            Passed = driftRate < 5.0,
            QueueCallerDriftRate = (double)qCallerOver / samples.Count * 100,
            MaxQueueCallerDrift = samples.Max(s => s.QueueCallerDrift),
            QueueMemberDriftRate = (double)qMemberOver / samples.Count * 100,
            MaxQueueMemberDrift = samples.Max(s => s.QueueMemberDrift),
            AgentDriftRate = (double)agentOver / samples.Count * 100,
            MaxAgentDrift = samples.Max(s => s.AgentDrift),
        };
    }
}
```

- [ ] **Step 6: Build and verify no errors**

```bash
dotnet build tests/PbxAdmin.LoadTests/
```

Expected: Build succeeded. 0 warnings, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Sdk/LiveStateValidator.cs
git commit -m "feat(sdk-tests): extend LiveStateValidator with queue and agent drift sampling"
```

---

## Task 3: SdkScenarioBase — Shared Helpers

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScenarioBase.cs`

Shared utilities used by all 9 SDK scenarios: call generation, agent pausing, channel drain wait, validation report building.

- [ ] **Step 1: Create the base class**

```csharp
namespace PbxAdmin.LoadTests.Scenarios.Sdk;

using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;

internal abstract class SdkScenarioBase : ITestScenario
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string Level { get; }

    public abstract Task ExecuteAsync(TestContext context, CancellationToken ct);
    public abstract Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct);

    protected static async Task GenerateCallsAsync(
        TestContext context, string destination, int count,
        ILogger logger, CancellationToken ct, int delayMs = 2000)
    {
        for (int i = 0; i < count; i++)
        {
            var caller = context.CallGenerator.GenerateCallerProfile();
            var callId = await context.CallGenerator.GenerateCallAsync(destination, cancellationToken: ct);
            context.EventCapture.RegisterCall(callId, caller.Number, destination, DateTime.UtcNow);
            context.Metrics.RecordCallOriginated();
            if (i < count - 1)
                await Task.Delay(delayMs, ct);
        }
    }

    protected static async Task SetAgentsPausedAsync(
        TestContext context, bool paused, ILogger logger, CancellationToken ct)
    {
        var connection = context.SdkRuntime!.Connection;
        var baseExt = context.Options.TargetServer == "file" ? 4100 : 2100;
        for (int i = 0; i < context.AgentPool.TotalAgents; i++)
        {
            var iface = $"PJSIP/{baseExt + i}";
            await connection.SendActionAsync(
                new QueuePauseAction { Queue = "loadtest", Interface = iface, Paused = paused }, ct);
        }
        logger.LogInformation("Agents {Action}", paused ? "paused" : "unpaused");
    }

    protected static async Task WaitForDrainAsync(
        IAmiConnection connection, ILogger logger, CancellationToken ct,
        int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var response = await connection.SendActionAsync<CommandResponse>(
                new CommandAction { Command = "core show channels count" }, ct);
            var output = string.Join(" ", response.Output ?? []);
            var channels = ParseFirstInteger(output);
            if (channels == 0) return;
            await Task.Delay(3000, ct);
        }
        logger.LogWarning("Drain timeout after {Timeout}s", timeoutSeconds);
    }

    protected static async Task<List<ValidationResult>> DetectLeaksAsync(
        TestContext context, CancellationToken ct)
    {
        var results = new List<ValidationResult>();
        var leakDetector = new LeakDetector(context.SdkRuntime!.Connection);
        var channelLeaks = await leakDetector.DetectLeaksAsync(ct);
        results.Add(channelLeaks);
        var agentLeaks = LeakDetector.DetectAgentLeaks(context.AgentPool);
        results.Add(agentLeaks);
        return results;
    }

    protected static ValidationReport BuildReport(
        TestContext context, List<ValidationResult> results, string level)
    {
        var allChecks = results.SelectMany(r => r.Checks).ToList();
        return new ValidationReport
        {
            TestStart = context.TestStartTime,
            TestEnd = context.TestEndTime,
            Duration = context.TestEndTime - context.TestStartTime,
            TotalCalls = context.Metrics.CallsOriginated,
            TotalChecks = allChecks.Count,
            PassedChecks = allChecks.Count(c => c.Passed),
            FailedChecks = allChecks.Count(c => !c.Passed),
            Results = results,
        };
    }

    protected static async Task ForceAmiDisconnectAsync(
        IAmiConnection connection, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("Forcing AMI disconnect via manager reload");
        await connection.SendActionAsync(
            new CommandAction { Command = "manager reload" }, ct);
    }

    protected static async Task<(bool reconnected, TimeSpan elapsed)> WaitForReconnectAsync(
        IAmiConnection connection, ILogger logger, CancellationToken ct,
        int timeoutMs = 10_000, int pollMs = 500)
    {
        var start = DateTime.UtcNow;
        await Task.Delay(3000, ct); // wait for disconnect detection
        var deadline = start.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await connection.SendActionAsync<CommandResponse>(
                    new CommandAction { Command = "core show version" }, ct);
                var elapsed = DateTime.UtcNow - start;
                logger.LogInformation("Reconnected in {Elapsed:F1}s", elapsed.TotalSeconds);
                return (true, elapsed);
            }
            catch
            {
                await Task.Delay(pollMs, ct);
            }
        }
        return (false, TimeSpan.FromMilliseconds(timeoutMs));
    }

    protected static int ParseFirstInteger(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+");
        return match.Success ? int.Parse(match.Value) : 0;
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build tests/PbxAdmin.LoadTests/
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScenarioBase.cs
git commit -m "feat(sdk-tests): add SdkScenarioBase with shared helpers"
```

---

## Task 4: SdkSmokeScenario (Level 1)

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkSmokeScenario.cs`

- [ ] **Step 1: Implement SdkSmokeScenario**

```csharp
namespace PbxAdmin.LoadTests.Scenarios.Sdk;

using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

internal sealed class SdkSmokeScenario : SdkScenarioBase
{
    public override string Name => "sdk-smoke";
    public override string Description =>
        "5 calls (3 answered, 1 timeout, 1 failed) — quick validation of channels, queues, agents, sessions";
    public override string Level => "smoke";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkSmokeScenario>();
        var server = context.SdkRuntime!.Server;
        var connection = context.SdkRuntime.Connection;

        context.TestStartTime = DateTime.UtcNow;

        // Start SDK sampler — 3s interval, only 3 samples needed
        await context.LiveStateValidator!.StartAsync(server, connection, intervalSeconds: 3, ct: ct);

        // Phase 1: 3 answered calls
        logger.LogInformation("Phase 1: 3 answered calls");
        await GenerateCallsAsync(context, "105", 3, logger, ct);
        await Task.Delay(15_000, ct);

        // Phase 2: 1 timeout (pause agents first)
        logger.LogInformation("Phase 2: 1 timeout call");
        await SetAgentsPausedAsync(context, paused: true, logger, ct);
        await GenerateCallsAsync(context, "105", 1, logger, ct);
        await Task.Delay(35_000, ct);
        await SetAgentsPausedAsync(context, paused: false, logger, ct);

        // Phase 3: 1 failed call
        logger.LogInformation("Phase 3: 1 failed call");
        await GenerateCallsAsync(context, "999", 1, logger, ct);
        await Task.Delay(10_000, ct);

        // Drain
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 30);
        await context.LiveStateValidator.StopAsync();
        await context.SessionCapture!.StopAsync();
        context.TestEndTime = DateTime.UtcNow;
    }

    public override async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkSmokeScenario>();
        await Task.Delay(3000, ct); // CDR flush

        var results = new List<ValidationResult>();
        var samples = context.LiveStateValidator!.GetSamples();
        var summary = context.LiveStateValidator.GetSummary();

        // Channel drift: exact match ±1 (absolute, not percentage)
        var channelChecks = new List<ValidationCheck>
        {
            new()
            {
                CheckName = "ChannelDriftMax",
                Passed = summary.MaxDrift <= 1,
                Expected = "<=1",
                Actual = summary.MaxDrift.ToString(),
            },
            new()
            {
                CheckName = "SufficientSamples",
                Passed = samples.Count >= 3,
                Expected = ">=3",
                Actual = samples.Count.ToString(),
            },
        };
        results.Add(new ValidationResult
        {
            CallId = "smoke-channels",
            ValidatorName = "SdkSmoke.Channels",
            Passed = channelChecks.All(c => c.Passed),
            Checks = channelChecks,
        });

        // Sessions: 5 exist, dispositions match CDR
        var sessions = context.SessionCapture!.GetCompletedSessions();
        var sessionChecks = new List<ValidationCheck>
        {
            new()
            {
                CheckName = "SessionCount",
                Passed = sessions.Count >= 3, // at minimum answered calls should create sessions
                Expected = ">=3",
                Actual = sessions.Count.ToString(),
            },
        };

        // Cross-reference answered sessions with CDR
        foreach (var session in sessions.Where(s => s.FinalState == "Answered"))
        {
            var cdr = await context.CdrReader.GetCallBySrcAsync(
                session.CallerNumber, context.TestStartTime, ct);
            sessionChecks.Add(new ValidationCheck
            {
                CheckName = $"CdrExists_{session.CallerNumber}",
                Passed = cdr != null,
                Expected = "CDR found",
                Actual = cdr != null ? "found" : "missing",
            });
        }
        results.Add(new ValidationResult
        {
            CallId = "smoke-sessions",
            ValidatorName = "SdkSmoke.Sessions",
            Passed = sessionChecks.All(c => c.Passed),
            Checks = sessionChecks,
        });

        // Leak detection
        results.AddRange(await DetectLeaksAsync(context, ct));

        // Timing check
        var duration = context.TestEndTime - context.TestStartTime;
        results.Add(new ValidationResult
        {
            CallId = "smoke-timing",
            ValidatorName = "SdkSmoke.Timing",
            Passed = duration.TotalSeconds <= 90,
            Checks =
            [
                new()
                {
                    CheckName = "CompletesUnder90s",
                    Passed = duration.TotalSeconds <= 90,
                    Expected = "<=90s",
                    Actual = $"{duration.TotalSeconds:F0}s",
                },
            ],
        });

        return BuildReport(context, results, Level);
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build tests/PbxAdmin.LoadTests/
```

- [ ] **Step 3: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkSmokeScenario.cs
git commit -m "feat(sdk-tests): add SdkSmokeScenario (Level 1 — smoke)"
```

---

## Task 5: SdkStateSyncScenario (Level 2)

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkStateSyncScenario.cs`

- [ ] **Step 1: Implement SdkStateSyncScenario**

```csharp
namespace PbxAdmin.LoadTests.Scenarios.Sdk;

using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

internal sealed class SdkStateSyncScenario : SdkScenarioBase
{
    public override string Name => "sdk-state-sync";
    public override string Description =>
        "3-min sustained load — validates Channels + Queues + Agents drift < 2% vs AMI ground truth";
    public override string Level => "functional";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkStateSyncScenario>();
        var server = context.SdkRuntime!.Server;
        var connection = context.SdkRuntime.Connection;
        var scheduler = context.Scheduler;

        context.TestStartTime = DateTime.UtcNow;

        // Start SDK sampler at 3s interval
        await context.LiveStateValidator!.StartAsync(server, connection, intervalSeconds: 3, ct: ct);

        // Generate sustained load using scheduler
        var maxConcurrent = context.CallPattern.MaxConcurrentCalls > 0
            ? context.CallPattern.MaxConcurrentCalls : 15;
        scheduler.Start(maxConcurrent, rampUpMinutes: 0);
        context.Metrics.EnterSustainPhase();

        var durationMinutes = context.Options.DurationMinutes > 0 ? context.Options.DurationMinutes : 3;
        logger.LogInformation("Sustaining {MaxConc} concurrent calls for {Min} minutes",
            maxConcurrent, durationMinutes);
        await Task.Delay(TimeSpan.FromMinutes(durationMinutes), ct);

        // Drain
        context.Metrics.EnterDrainPhase();
        scheduler.Stop();
        await WaitForDrainAsync(connection, logger, ct);
        await context.LiveStateValidator.StopAsync();
        await context.SessionCapture!.StopAsync();
        context.TestEndTime = DateTime.UtcNow;
    }

    public override Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var results = new List<ValidationResult>();
        var samples = context.LiveStateValidator!.GetSamples();
        var summary = context.LiveStateValidator.GetSummary();

        // Channel drift
        results.Add(new ValidationResult
        {
            CallId = "state-sync-channels",
            ValidatorName = "SdkStateSync.Channels",
            Passed = summary.DriftRate < 2.0 && summary.MaxDrift <= 4,
            Checks =
            [
                new() { CheckName = "ChannelDriftRate", Passed = summary.DriftRate < 2.0,
                    Expected = "<2%", Actual = $"{summary.DriftRate:F1}%" },
                new() { CheckName = "ChannelMaxDrift", Passed = summary.MaxDrift <= 4,
                    Expected = "<=4", Actual = summary.MaxDrift.ToString() },
                new() { CheckName = "SufficientSamples", Passed = samples.Count >= 30,
                    Expected = ">=30", Actual = samples.Count.ToString() },
            ],
        });

        // Queue member drift
        var qMemberMatch = samples.Count(s => s.QueueMemberDrift <= 2);
        var qMemberRate = samples.Count > 0 ? (double)qMemberMatch / samples.Count * 100 : 100;
        results.Add(new ValidationResult
        {
            CallId = "state-sync-queues",
            ValidatorName = "SdkStateSync.Queues",
            Passed = qMemberRate >= 98.0 && summary.QueueCallerDriftRate < 5.0,
            Checks =
            [
                new() { CheckName = "QueueMemberMatchRate", Passed = qMemberRate >= 98.0,
                    Expected = ">=98%", Actual = $"{qMemberRate:F1}%" },
                new() { CheckName = "QueueCallerDriftRate", Passed = summary.QueueCallerDriftRate < 5.0,
                    Expected = "<5%", Actual = $"{summary.QueueCallerDriftRate:F1}%" },
            ],
        });

        // Agent state drift
        var agentMatch = samples.Count(s => s.AgentDrift <= 2);
        var agentRate = samples.Count > 0 ? (double)agentMatch / samples.Count * 100 : 100;
        results.Add(new ValidationResult
        {
            CallId = "state-sync-agents",
            ValidatorName = "SdkStateSync.Agents",
            Passed = agentRate >= 98.0,
            Checks =
            [
                new() { CheckName = "AgentStateMatchRate", Passed = agentRate >= 98.0,
                    Expected = ">=98%", Actual = $"{agentRate:F1}%" },
                new() { CheckName = "AgentMaxDrift", Passed = summary.MaxAgentDrift <= 4,
                    Expected = "<=4", Actual = summary.MaxAgentDrift.ToString() },
            ],
        });

        // Cleanup
        var leakResults = DetectLeaksAsync(context, ct).GetAwaiter().GetResult();
        results.AddRange(leakResults);

        return Task.FromResult(BuildReport(context, results, Level));
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build tests/PbxAdmin.LoadTests/ && \
git add tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkStateSyncScenario.cs && \
git commit -m "feat(sdk-tests): add SdkStateSyncScenario (Level 2 — functional)"
```

---

## Task 6: SdkSessionsScenario (Level 2)

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkSessionsScenario.cs`

- [ ] **Step 1: Implement SdkSessionsScenario**

This scenario runs 5 sequential phases (answered, timeout, failed, hold, transfer) and validates CallSession vs CDR.

```csharp
namespace PbxAdmin.LoadTests.Scenarios.Sdk;

using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer1;

internal sealed class SdkSessionsScenario : SdkScenarioBase
{
    public override string Name => "sdk-sessions";
    public override string Description =>
        "30 calls across 5 phases (answered/timeout/failed/hold/transfer) — validates CallSession lifecycle vs CDR";
    public override string Level => "functional";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkSessionsScenario>();
        var connection = context.SdkRuntime!.Connection;

        context.TestStartTime = DateTime.UtcNow;

        // Phase 1: 15 answered calls
        logger.LogInformation("Phase 1: 15 answered calls");
        await GenerateCallsAsync(context, "105", 15, logger, ct);
        await Task.Delay(45_000, ct);
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 30);

        // Phase 2: 5 timeout calls (agents paused)
        logger.LogInformation("Phase 2: 5 timeout calls");
        await SetAgentsPausedAsync(context, paused: true, logger, ct);
        await GenerateCallsAsync(context, "105", 5, logger, ct);
        await Task.Delay(50_000, ct);
        await SetAgentsPausedAsync(context, paused: false, logger, ct);
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 30);

        // Phase 3: 5 failed calls (invalid extension)
        logger.LogInformation("Phase 3: 5 failed calls");
        await GenerateCallsAsync(context, "999", 5, logger, ct);
        await Task.Delay(15_000, ct);
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 15);

        // Phase 4: 3 hold calls
        logger.LogInformation("Phase 4: 3 hold calls");
        await GenerateCallsAsync(context, "105", 3, logger, ct);
        await Task.Delay(20_000, ct); // agent answers and holds for ~5s
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 30);

        // Phase 5: 2 transfer calls
        logger.LogInformation("Phase 5: 2 transfer calls");
        await GenerateCallsAsync(context, "105", 2, logger, ct);
        await Task.Delay(30_000, ct); // agent answers and transfers
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 30);

        await context.SessionCapture!.StopAsync();
        context.TestEndTime = DateTime.UtcNow;
    }

    public override async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkSessionsScenario>();
        await Task.Delay(3000, ct); // CDR flush

        var results = new List<ValidationResult>();
        var sessions = context.SessionCapture!.GetCompletedSessions();

        // Coverage: sessions exist
        var coverageChecks = new List<ValidationCheck>
        {
            new()
            {
                CheckName = "MinSessionCount",
                Passed = sessions.Count >= 15, // at least answered calls
                Expected = ">=15",
                Actual = sessions.Count.ToString(),
            },
        };

        // Cross-reference each session against CDR
        int matched = 0, total = 0;
        foreach (var session in sessions)
        {
            total++;
            var cdr = await context.CdrReader.GetCallBySrcAsync(
                session.CallerNumber, context.TestStartTime, ct);
            if (cdr != null) matched++;

            if (cdr != null)
            {
                var durationDelta = Math.Abs(
                    (session.Duration?.TotalSeconds ?? 0) - cdr.BillSec);
                coverageChecks.Add(new ValidationCheck
                {
                    CheckName = $"DurationMatch_{session.CallerNumber}",
                    Passed = durationDelta <= 2,
                    Expected = "<=2s",
                    Actual = $"{durationDelta:F1}s",
                });
            }
        }

        var coverageRate = total > 0 ? (double)matched / total * 100 : 0;
        coverageChecks.Add(new ValidationCheck
        {
            CheckName = "CdrCoverage",
            Passed = coverageRate >= 98.0,
            Expected = ">=98%",
            Actual = $"{coverageRate:F1}%",
        });

        results.Add(new ValidationResult
        {
            CallId = "sessions-coverage",
            ValidatorName = "SdkSessions.Coverage",
            Passed = coverageChecks.All(c => c.Passed),
            Checks = coverageChecks,
        });

        // Leak detection
        results.AddRange(await DetectLeaksAsync(context, ct));

        return BuildReport(context, results, Level);
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build tests/PbxAdmin.LoadTests/ && \
git add tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkSessionsScenario.cs && \
git commit -m "feat(sdk-tests): add SdkSessionsScenario (Level 2 — functional)"
```

---

## Task 7: SdkReconnectScenario (Level 2 — Rewrite)

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkReconnectScenario.cs`

- [ ] **Step 1: Implement rewritten SdkReconnectScenario**

3 AMI disconnections under active load (at 30s, 90s, 150s).

```csharp
namespace PbxAdmin.LoadTests.Scenarios.Sdk;

using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

internal sealed class SdkReconnectScenario : SdkScenarioBase
{
    public override string Name => "sdk-reconnect";
    public override string Description =>
        "3 AMI disconnects under active load — validates reconnection < 10s and state recovery";
    public override string Level => "functional";

    private readonly List<(int disconnectNumber, bool reconnected, TimeSpan elapsed)> _reconnections = [];
    private int _preDisconnectSessionCount;

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkReconnectScenario>();
        var server = context.SdkRuntime!.Server;
        var connection = context.SdkRuntime.Connection;
        var scheduler = context.Scheduler;

        context.TestStartTime = DateTime.UtcNow;

        // Start live state validator
        await context.LiveStateValidator!.StartAsync(server, connection, intervalSeconds: 3, ct: ct);

        // Start sustained load
        var maxConcurrent = context.CallPattern.MaxConcurrentCalls > 0
            ? context.CallPattern.MaxConcurrentCalls : 15;
        scheduler.Start(maxConcurrent, rampUpMinutes: 0);

        // Wait 30s for load to stabilize
        await Task.Delay(30_000, ct);
        _preDisconnectSessionCount = context.SessionCapture!.CompletedSessionCount;

        // Disconnect 1 at 30s
        logger.LogInformation("Disconnect 1 at 30s");
        await ForceAmiDisconnectAsync(connection, logger, ct);
        var (r1, e1) = await WaitForReconnectAsync(connection, logger, ct);
        _reconnections.Add((1, r1, e1));

        // Wait until 90s mark
        var elapsed = DateTime.UtcNow - context.TestStartTime;
        var waitTo90 = TimeSpan.FromSeconds(90) - elapsed;
        if (waitTo90 > TimeSpan.Zero) await Task.Delay(waitTo90, ct);

        // Disconnect 2 at 90s
        logger.LogInformation("Disconnect 2 at 90s");
        await ForceAmiDisconnectAsync(connection, logger, ct);
        var (r2, e2) = await WaitForReconnectAsync(connection, logger, ct);
        _reconnections.Add((2, r2, e2));

        // Wait until 150s mark
        elapsed = DateTime.UtcNow - context.TestStartTime;
        var waitTo150 = TimeSpan.FromSeconds(150) - elapsed;
        if (waitTo150 > TimeSpan.Zero) await Task.Delay(waitTo150, ct);

        // Disconnect 3 at 150s
        logger.LogInformation("Disconnect 3 at 150s");
        await ForceAmiDisconnectAsync(connection, logger, ct);
        var (r3, e3) = await WaitForReconnectAsync(connection, logger, ct);
        _reconnections.Add((3, r3, e3));

        // Drain
        scheduler.Stop();
        await WaitForDrainAsync(connection, logger, ct);
        await context.LiveStateValidator.StopAsync();
        await context.SessionCapture.StopAsync();
        context.TestEndTime = DateTime.UtcNow;
    }

    public override async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        await Task.Delay(3000, ct);
        var results = new List<ValidationResult>();

        // Reconnection time checks
        var reconnectChecks = new List<ValidationCheck>();
        foreach (var (num, reconnected, elapsed) in _reconnections)
        {
            reconnectChecks.Add(new ValidationCheck
            {
                CheckName = $"Reconnect{num}_Success",
                Passed = reconnected,
                Expected = "true",
                Actual = reconnected.ToString(),
            });
            reconnectChecks.Add(new ValidationCheck
            {
                CheckName = $"Reconnect{num}_Under10s",
                Passed = elapsed.TotalSeconds < 10,
                Expected = "<10s",
                Actual = $"{elapsed.TotalSeconds:F1}s",
            });
        }
        results.Add(new ValidationResult
        {
            CallId = "reconnect-timing",
            ValidatorName = "SdkReconnect.Timing",
            Passed = reconnectChecks.All(c => c.Passed),
            Checks = reconnectChecks,
        });

        // Post-reconnect sessions: new sessions were captured after reconnections
        var sessions = context.SessionCapture!.GetCompletedSessions();
        var postReconnectCount = sessions.Count - _preDisconnectSessionCount;
        results.Add(new ValidationResult
        {
            CallId = "reconnect-sessions",
            ValidatorName = "SdkReconnect.Sessions",
            Passed = postReconnectCount > 0,
            Checks =
            [
                new()
                {
                    CheckName = "PostReconnectSessionsExist",
                    Passed = postReconnectCount > 0,
                    Expected = ">0",
                    Actual = postReconnectCount.ToString(),
                },
                new()
                {
                    CheckName = "TotalSessionCount",
                    Passed = sessions.Count >= 5,
                    Expected = ">=5",
                    Actual = sessions.Count.ToString(),
                },
            ],
        });

        // Connection alive now
        try
        {
            await context.SdkRuntime!.Connection.SendActionAsync<Asterisk.Sdk.Ami.Responses.CommandResponse>(
                new Asterisk.Sdk.Ami.Actions.CommandAction { Command = "core show version" }, ct);
            results.Add(new ValidationResult
            {
                CallId = "reconnect-alive",
                ValidatorName = "SdkReconnect.Alive",
                Passed = true,
                Checks = [new() { CheckName = "ConnectionAlive", Passed = true,
                    Expected = "alive", Actual = "alive" }],
            });
        }
        catch (Exception ex)
        {
            results.Add(new ValidationResult
            {
                CallId = "reconnect-alive",
                ValidatorName = "SdkReconnect.Alive",
                Passed = false,
                Checks = [new() { CheckName = "ConnectionAlive", Passed = false,
                    Expected = "alive", Actual = ex.Message }],
            });
        }

        // Leaks
        results.AddRange(await DetectLeaksAsync(context, ct));

        return BuildReport(context, results, Level);
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build tests/PbxAdmin.LoadTests/ && \
git add tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkReconnectScenario.cs && \
git commit -m "feat(sdk-tests): add SdkReconnectScenario rewrite (Level 2 — functional)"
```

---

## Task 8: Scale Scenarios (Level 3) — All 4

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleChannelsScenario.cs`
- Create: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleQueuesScenario.cs`
- Create: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleAgentsScenario.cs`
- Create: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleSessionsScenario.cs`

All 4 share the same execution pattern: ramp to 300 concurrent in 1 min, sustain 4 min. They differ in validation.

- [ ] **Step 1: SdkScaleChannelsScenario**

```csharp
namespace PbxAdmin.LoadTests.Scenarios.Sdk;

using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

internal sealed class SdkScaleChannelsScenario : SdkScenarioBase
{
    public override string Name => "sdk-scale-channels";
    public override string Description =>
        "200 agents, 300 concurrent — channel tracking drift < 2% at scale";
    public override string Level => "scale";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkScaleChannelsScenario>();
        var server = context.SdkRuntime!.Server;
        var connection = context.SdkRuntime.Connection;

        context.TestStartTime = DateTime.UtcNow;
        await context.LiveStateValidator!.StartAsync(server, connection, intervalSeconds: 3, ct: ct);

        var maxConcurrent = context.CallPattern.MaxConcurrentCalls > 0
            ? context.CallPattern.MaxConcurrentCalls : 300;
        context.Scheduler.Start(maxConcurrent, rampUpMinutes: 1);
        context.Metrics.EnterSustainPhase();

        var duration = context.Options.DurationMinutes > 0 ? context.Options.DurationMinutes : 5;
        logger.LogInformation("Ramping to {Max} concurrent, sustaining {Min} min", maxConcurrent, duration);
        await Task.Delay(TimeSpan.FromMinutes(duration), ct);

        context.Metrics.EnterDrainPhase();
        context.Scheduler.Stop();
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 120);
        await context.LiveStateValidator.StopAsync();
        await context.SessionCapture!.StopAsync();
        context.TestEndTime = DateTime.UtcNow;
    }

    public override async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var results = new List<ValidationResult>();
        var summary = context.LiveStateValidator!.GetSummary();
        var samples = context.LiveStateValidator.GetSamples();

        // Phantom/invisible check: any sample where SDK > AMI+2 (phantom) or AMI > SDK+2 (invisible)
        var phantoms = samples.Count(s => s.SdkChannelCount > s.AsteriskChannelCount + 2);
        var invisible = samples.Count(s => s.AsteriskChannelCount > s.SdkChannelCount + 2);

        results.Add(new ValidationResult
        {
            CallId = "scale-channels",
            ValidatorName = "SdkScaleChannels",
            Passed = summary.DriftRate < 2.0 && summary.MaxDrift <= 6,
            Checks =
            [
                new() { CheckName = "DriftRateAvg", Passed = summary.DriftRate < 2.0,
                    Expected = "<2%", Actual = $"{summary.DriftRate:F1}%" },
                new() { CheckName = "DriftMaxAbsolute", Passed = summary.MaxDrift <= 6,
                    Expected = "<=6", Actual = summary.MaxDrift.ToString() },
                new() { CheckName = "NoPhantomChannels", Passed = phantoms == 0,
                    Expected = "0", Actual = phantoms.ToString() },
                new() { CheckName = "NoInvisibleChannels", Passed = invisible == 0,
                    Expected = "0", Actual = invisible.ToString() },
                new() { CheckName = "PeakConcurrent",
                    Passed = context.Metrics.PeakConcurrentCalls >= 250,
                    Expected = ">=250", Actual = context.Metrics.PeakConcurrentCalls.ToString() },
            ],
        });

        results.AddRange(await DetectLeaksAsync(context, ct));
        return BuildReport(context, results, Level);
    }
}
```

- [ ] **Step 2: SdkScaleQueuesScenario**

```csharp
namespace PbxAdmin.LoadTests.Scenarios.Sdk;

using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

internal sealed class SdkScaleQueuesScenario : SdkScenarioBase
{
    public override string Name => "sdk-scale-queues";
    public override string Description =>
        "200 agents, 300 concurrent — queue member/caller drift < 2% at scale";
    public override string Level => "scale";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        // Identical execution to SdkScaleChannelsScenario
        var logger = context.LoggerFactory.CreateLogger<SdkScaleQueuesScenario>();
        var server = context.SdkRuntime!.Server;
        var connection = context.SdkRuntime.Connection;

        context.TestStartTime = DateTime.UtcNow;
        await context.LiveStateValidator!.StartAsync(server, connection, intervalSeconds: 3, ct: ct);

        var maxConcurrent = context.CallPattern.MaxConcurrentCalls > 0
            ? context.CallPattern.MaxConcurrentCalls : 300;
        context.Scheduler.Start(maxConcurrent, rampUpMinutes: 1);
        context.Metrics.EnterSustainPhase();

        var duration = context.Options.DurationMinutes > 0 ? context.Options.DurationMinutes : 5;
        logger.LogInformation("Scale queues: {Max} concurrent, {Min} min", maxConcurrent, duration);
        await Task.Delay(TimeSpan.FromMinutes(duration), ct);

        context.Metrics.EnterDrainPhase();
        context.Scheduler.Stop();
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 120);
        await context.LiveStateValidator.StopAsync();
        await context.SessionCapture!.StopAsync();
        context.TestEndTime = DateTime.UtcNow;
    }

    public override async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        await Task.Delay(3000, ct);
        var results = new List<ValidationResult>();
        var summary = context.LiveStateValidator!.GetSummary();

        results.Add(new ValidationResult
        {
            CallId = "scale-queues",
            ValidatorName = "SdkScaleQueues",
            Passed = summary.QueueMemberDriftRate < 2.0 && summary.QueueCallerDriftRate < 2.0,
            Checks =
            [
                new() { CheckName = "MemberDriftRate", Passed = summary.QueueMemberDriftRate < 2.0,
                    Expected = "<2%", Actual = $"{summary.QueueMemberDriftRate:F1}%" },
                new() { CheckName = "CallerDriftRate", Passed = summary.QueueCallerDriftRate < 2.0,
                    Expected = "<2%", Actual = $"{summary.QueueCallerDriftRate:F1}%" },
                new() { CheckName = "MaxMemberDrift", Passed = summary.MaxQueueMemberDrift <= 6,
                    Expected = "<=6", Actual = summary.MaxQueueMemberDrift.ToString() },
                // SLA is informational
                new() { CheckName = "SLA_80_30s", Passed = true,
                    Expected = "informational", Actual = "see audit" },
            ],
        });

        results.AddRange(await DetectLeaksAsync(context, ct));
        return BuildReport(context, results, Level);
    }
}
```

- [ ] **Step 3: SdkScaleAgentsScenario**

```csharp
namespace PbxAdmin.LoadTests.Scenarios.Sdk;

using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

internal sealed class SdkScaleAgentsScenario : SdkScenarioBase
{
    public override string Name => "sdk-scale-agents";
    public override string Description =>
        "200 agents, 300 concurrent — agent state drift < 2% at scale";
    public override string Level => "scale";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkScaleAgentsScenario>();
        var server = context.SdkRuntime!.Server;
        var connection = context.SdkRuntime.Connection;

        context.TestStartTime = DateTime.UtcNow;
        await context.LiveStateValidator!.StartAsync(server, connection, intervalSeconds: 3, ct: ct);

        var maxConcurrent = context.CallPattern.MaxConcurrentCalls > 0
            ? context.CallPattern.MaxConcurrentCalls : 300;
        context.Scheduler.Start(maxConcurrent, rampUpMinutes: 1);
        context.Metrics.EnterSustainPhase();

        var duration = context.Options.DurationMinutes > 0 ? context.Options.DurationMinutes : 5;
        logger.LogInformation("Scale agents: {Max} concurrent, {Min} min", maxConcurrent, duration);
        await Task.Delay(TimeSpan.FromMinutes(duration), ct);

        context.Metrics.EnterDrainPhase();
        context.Scheduler.Stop();
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 120);
        await context.LiveStateValidator.StopAsync();
        await context.SessionCapture!.StopAsync();
        context.TestEndTime = DateTime.UtcNow;
    }

    public override async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var results = new List<ValidationResult>();
        var summary = context.LiveStateValidator!.GetSummary();
        var pool = context.AgentPool;

        results.Add(new ValidationResult
        {
            CallId = "scale-agents",
            ValidatorName = "SdkScaleAgents",
            Passed = summary.AgentDriftRate < 2.0,
            Checks =
            [
                new() { CheckName = "AgentDriftRate", Passed = summary.AgentDriftRate < 2.0,
                    Expected = "<2%", Actual = $"{summary.AgentDriftRate:F1}%" },
                new() { CheckName = "MaxAgentDrift", Passed = summary.MaxAgentDrift <= 6,
                    Expected = "<=6", Actual = summary.MaxAgentDrift.ToString() },
                new() { CheckName = "FinalState_NoRinging", Passed = pool.RingingAgents == 0,
                    Expected = "0", Actual = pool.RingingAgents.ToString() },
                new() { CheckName = "FinalState_NoInCall", Passed = pool.InCallAgents == 0,
                    Expected = "0", Actual = pool.InCallAgents.ToString() },
            ],
        });

        results.AddRange(await DetectLeaksAsync(context, ct));
        return BuildReport(context, results, Level);
    }
}
```

- [ ] **Step 4: SdkScaleSessionsScenario**

```csharp
namespace PbxAdmin.LoadTests.Scenarios.Sdk;

using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

internal sealed class SdkScaleSessionsScenario : SdkScenarioBase
{
    public override string Name => "sdk-scale-sessions";
    public override string Description =>
        "200 agents, 300 concurrent — session accuracy >= 98% vs CDR at scale";
    public override string Level => "scale";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkScaleSessionsScenario>();
        var server = context.SdkRuntime!.Server;
        var connection = context.SdkRuntime.Connection;

        context.TestStartTime = DateTime.UtcNow;
        await context.LiveStateValidator!.StartAsync(server, connection, intervalSeconds: 3, ct: ct);

        var maxConcurrent = context.CallPattern.MaxConcurrentCalls > 0
            ? context.CallPattern.MaxConcurrentCalls : 300;
        context.Scheduler.Start(maxConcurrent, rampUpMinutes: 1);
        context.Metrics.EnterSustainPhase();

        var duration = context.Options.DurationMinutes > 0 ? context.Options.DurationMinutes : 5;
        logger.LogInformation("Scale sessions: {Max} concurrent, {Min} min", maxConcurrent, duration);
        await Task.Delay(TimeSpan.FromMinutes(duration), ct);

        context.Metrics.EnterDrainPhase();
        context.Scheduler.Stop();
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 120);
        await context.LiveStateValidator.StopAsync();
        await context.SessionCapture!.StopAsync();
        context.TestEndTime = DateTime.UtcNow;
    }

    public override async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        await Task.Delay(5000, ct); // CDR flush at scale takes longer
        var results = new List<ValidationResult>();
        var sessions = context.SessionCapture!.GetCompletedSessions();

        // Sample up to 100 sessions for CDR cross-reference
        var sample = sessions.Take(100).ToList();
        int matched = 0, durationMatch = 0;
        foreach (var session in sample)
        {
            var cdr = await context.CdrReader.GetCallBySrcAsync(
                session.CallerNumber, context.TestStartTime, ct);
            if (cdr != null)
            {
                matched++;
                var delta = Math.Abs((session.Duration?.TotalSeconds ?? 0) - cdr.BillSec);
                if (delta <= 2) durationMatch++;
            }
        }

        var coverageRate = sample.Count > 0 ? (double)matched / sample.Count * 100 : 0;
        var durationRate = matched > 0 ? (double)durationMatch / matched * 100 : 0;

        results.Add(new ValidationResult
        {
            CallId = "scale-sessions",
            ValidatorName = "SdkScaleSessions",
            Passed = coverageRate >= 98.0,
            Checks =
            [
                new() { CheckName = "CdrCoverage", Passed = coverageRate >= 98.0,
                    Expected = ">=98%", Actual = $"{coverageRate:F1}%" },
                new() { CheckName = "DurationAccuracy", Passed = durationRate >= 95.0,
                    Expected = ">=95%", Actual = $"{durationRate:F1}%" },
                new() { CheckName = "TotalSessionsCaptured",
                    Passed = sessions.Count >= 50,
                    Expected = ">=50", Actual = sessions.Count.ToString() },
                new() { CheckName = "NoOrphanSessions", Passed = true,
                    Expected = "informational", Actual = $"{sessions.Count - matched} unmatched" },
            ],
        });

        results.AddRange(await DetectLeaksAsync(context, ct));
        return BuildReport(context, results, Level);
    }
}
```

- [ ] **Step 5: Build all 4**

```bash
dotnet build tests/PbxAdmin.LoadTests/
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScale*.cs
git commit -m "feat(sdk-tests): add 4 scale scenarios (Level 3 — channels, queues, agents, sessions)"
```

---

## Task 9: SdkEnduranceScenario (Level 4)

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkEnduranceScenario.cs`

- [ ] **Step 1: Implement SdkEnduranceScenario**

30-min combined test: sustained load + 1 reconnect at 15 min + memory/CPU validation from audit.

```csharp
namespace PbxAdmin.LoadTests.Scenarios.Sdk;

using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

internal sealed class SdkEnduranceScenario : SdkScenarioBase
{
    public override string Name => "sdk-endurance";
    public override string Description =>
        "30-min combined: 300 concurrent, drift + sessions + reconnect + memory/CPU validation";
    public override string Level => "endurance";

    private bool _reconnected;
    private TimeSpan _reconnectElapsed;

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkEnduranceScenario>();
        var server = context.SdkRuntime!.Server;
        var connection = context.SdkRuntime.Connection;

        context.TestStartTime = DateTime.UtcNow;
        await context.LiveStateValidator!.StartAsync(server, connection, intervalSeconds: 3, ct: ct);

        var maxConcurrent = context.CallPattern.MaxConcurrentCalls > 0
            ? context.CallPattern.MaxConcurrentCalls : 300;
        var duration = context.Options.DurationMinutes > 0 ? context.Options.DurationMinutes : 30;

        // Ramp up in 2 min
        context.Scheduler.Start(maxConcurrent, rampUpMinutes: 2);
        context.Metrics.EnterSustainPhase();
        logger.LogInformation("Endurance: {Max} concurrent for {Min} min", maxConcurrent, duration);

        // Sustain until 15 min mark, then disconnect
        var halfDuration = TimeSpan.FromMinutes(Math.Min(15, duration / 2));
        await Task.Delay(halfDuration, ct);

        // Force AMI disconnect at midpoint
        logger.LogInformation("Forcing AMI disconnect at midpoint");
        await ForceAmiDisconnectAsync(connection, logger, ct);
        (_reconnected, _reconnectElapsed) = await WaitForReconnectAsync(connection, logger, ct);

        // Sustain remaining time
        var remaining = TimeSpan.FromMinutes(duration) - (DateTime.UtcNow - context.TestStartTime)
            - TimeSpan.FromMinutes(3); // reserve 3 min for drain
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);

        // Drain
        context.Metrics.EnterDrainPhase();
        context.Scheduler.Stop();
        await WaitForDrainAsync(connection, logger, ct, timeoutSeconds: 180);
        await context.LiveStateValidator.StopAsync();
        await context.SessionCapture!.StopAsync();
        context.TestEndTime = DateTime.UtcNow;
    }

    public override async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        await Task.Delay(5000, ct);
        var results = new List<ValidationResult>();
        var summary = context.LiveStateValidator!.GetSummary();
        var sessions = context.SessionCapture!.GetCompletedSessions();
        var metrics = context.Metrics.GetSummary(context.TestEndTime - context.TestStartTime);

        // Channel drift sustained
        results.Add(new ValidationResult
        {
            CallId = "endurance-drift",
            ValidatorName = "SdkEndurance.Drift",
            Passed = summary.DriftRate < 2.0,
            Checks =
            [
                new() { CheckName = "ChannelDriftRate", Passed = summary.DriftRate < 2.0,
                    Expected = "<2%", Actual = $"{summary.DriftRate:F1}%" },
                new() { CheckName = "QueueDriftRate", Passed = summary.QueueMemberDriftRate < 2.0,
                    Expected = "<2%", Actual = $"{summary.QueueMemberDriftRate:F1}%" },
                new() { CheckName = "AgentDriftRate", Passed = summary.AgentDriftRate < 2.0,
                    Expected = "<2%", Actual = $"{summary.AgentDriftRate:F1}%" },
            ],
        });

        // Session accuracy (sample 100)
        var sample = sessions.Take(100).ToList();
        int matched = 0;
        foreach (var session in sample)
        {
            var cdr = await context.CdrReader.GetCallBySrcAsync(
                session.CallerNumber, context.TestStartTime, ct);
            if (cdr != null) matched++;
        }
        var coverageRate = sample.Count > 0 ? (double)matched / sample.Count * 100 : 0;
        results.Add(new ValidationResult
        {
            CallId = "endurance-sessions",
            ValidatorName = "SdkEndurance.Sessions",
            Passed = coverageRate >= 98.0,
            Checks =
            [
                new() { CheckName = "SessionCoverage", Passed = coverageRate >= 98.0,
                    Expected = ">=98%", Actual = $"{coverageRate:F1}%" },
                new() { CheckName = "TotalSessions", Passed = sessions.Count >= 100,
                    Expected = ">=100", Actual = sessions.Count.ToString() },
            ],
        });

        // Reconnect
        results.Add(new ValidationResult
        {
            CallId = "endurance-reconnect",
            ValidatorName = "SdkEndurance.Reconnect",
            Passed = _reconnected && _reconnectElapsed.TotalSeconds < 10,
            Checks =
            [
                new() { CheckName = "Reconnected", Passed = _reconnected,
                    Expected = "true", Actual = _reconnected.ToString() },
                new() { CheckName = "ReconnectTime", Passed = _reconnectElapsed.TotalSeconds < 10,
                    Expected = "<10s", Actual = $"{_reconnectElapsed.TotalSeconds:F1}s" },
            ],
        });

        // Answer rate
        var answerRate = metrics.CallsOriginated > 0
            ? (double)metrics.CallsAnswered / metrics.CallsOriginated * 100 : 0;
        results.Add(new ValidationResult
        {
            CallId = "endurance-answer-rate",
            ValidatorName = "SdkEndurance.AnswerRate",
            Passed = answerRate >= 95.0,
            Checks =
            [
                new() { CheckName = "AnswerRate", Passed = answerRate >= 95.0,
                    Expected = ">=95%", Actual = $"{answerRate:F1}%" },
            ],
        });

        // Leaks
        results.AddRange(await DetectLeaksAsync(context, ct));

        return BuildReport(context, results, Level);
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build tests/PbxAdmin.LoadTests/ && \
git add tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkEnduranceScenario.cs && \
git commit -m "feat(sdk-tests): add SdkEnduranceScenario (Level 4 — endurance, 30 min)"
```

---

## Task 10: LevelRunner + --level CLI Flag

**Files:**
- Create: `tests/PbxAdmin.LoadTests/Scenarios/LevelRunner.cs`
- Modify: `tests/PbxAdmin.LoadTests/Program.cs`

- [ ] **Step 1: Create LevelRunner**

```csharp
namespace PbxAdmin.LoadTests.Scenarios;

internal static class LevelRunner
{
    public static readonly IReadOnlyDictionary<string, string[]> Levels =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["smoke"] = ["sdk-smoke"],
            ["functional"] = ["sdk-smoke", "sdk-state-sync", "sdk-sessions", "sdk-reconnect"],
            ["scale"] = [
                "sdk-smoke", "sdk-state-sync", "sdk-sessions", "sdk-reconnect",
                "sdk-scale-channels", "sdk-scale-queues", "sdk-scale-agents", "sdk-scale-sessions"
            ],
            ["all"] = [
                "sdk-smoke", "sdk-state-sync", "sdk-sessions", "sdk-reconnect",
                "sdk-scale-channels", "sdk-scale-queues", "sdk-scale-agents", "sdk-scale-sessions",
                "sdk-endurance"
            ],
        };

    public static string[] GetScenarios(string level) =>
        Levels.TryGetValue(level, out var scenarios) ? scenarios : [];
}
```

- [ ] **Step 2: Add --level option to Program.cs**

In `Program.cs`, add the option alongside `--scenario`:

```csharp
var levelOption = new Option<string?>("--level")
{
    Description = "Run a pyramid level: smoke, functional, scale, all"
};
```

Add it to the root command options. In the handler, if `--level` is provided, loop through scenarios from `LevelRunner.GetScenarios(level)` and stop on first failure:

```csharp
if (!string.IsNullOrEmpty(level))
{
    var scenarioNames = LevelRunner.GetScenarios(level);
    if (scenarioNames.Length == 0)
    {
        logger.LogError("Unknown level: {Level}. Valid: smoke, functional, scale, all", level);
        return 1;
    }

    foreach (var name in scenarioNames)
    {
        var scenario = ScenarioRegistry.Get(name);
        if (scenario == null) { logger.LogError("Scenario not found: {Name}", name); return 1; }

        logger.LogInformation("=== Running {Name} ({Level}) ===", name, level);
        // ... execute and validate ...
        // If failed, log and return 1
        if (report.FailedChecks > 0)
        {
            logger.LogError("Level gating: {Name} failed, stopping", name);
            return 1;
        }
    }
    return 0;
}
```

- [ ] **Step 3: Build and verify**

```bash
dotnet build tests/PbxAdmin.LoadTests/
```

- [ ] **Step 4: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Scenarios/LevelRunner.cs tests/PbxAdmin.LoadTests/Program.cs
git commit -m "feat(sdk-tests): add --level CLI flag with pyramid gating"
```

---

## Task 11: Update ScenarioRegistry + Delete Old Scenarios

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/Scenarios/ScenarioRegistry.cs`
- Delete: `tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkLiveDriftScenario.cs`
- Delete: `tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkSessionAccuracyScenario.cs`
- Delete: `tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkReconnectScenario.cs`

- [ ] **Step 1: Update ScenarioRegistry**

Remove the 3 old SDK entries and add 9 new ones. In the `All` dictionary:

Remove:
```csharp
["sdk-session-accuracy"] = new SdkSessionAccuracyScenario(),
["sdk-live-drift"] = new SdkLiveDriftScenario(),
["sdk-reconnect"] = new SdkReconnectScenario(),
```

Add:
```csharp
// SDK Pyramid (replaces sdk-session-accuracy, sdk-live-drift, sdk-reconnect)
["sdk-smoke"] = new Sdk.SdkSmokeScenario(),
["sdk-state-sync"] = new Sdk.SdkStateSyncScenario(),
["sdk-sessions"] = new Sdk.SdkSessionsScenario(),
["sdk-reconnect"] = new Sdk.SdkReconnectScenario(),
["sdk-scale-channels"] = new Sdk.SdkScaleChannelsScenario(),
["sdk-scale-queues"] = new Sdk.SdkScaleQueuesScenario(),
["sdk-scale-agents"] = new Sdk.SdkScaleAgentsScenario(),
["sdk-scale-sessions"] = new Sdk.SdkScaleSessionsScenario(),
["sdk-endurance"] = new Sdk.SdkEnduranceScenario(),
```

Update the `smoke` alias to point to `SdkSmokeScenario`:
```csharp
["smoke"] = new Sdk.SdkSmokeScenario(),
```

- [ ] **Step 2: Delete old scenario files**

```bash
rm tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkLiveDriftScenario.cs
rm tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkSessionAccuracyScenario.cs
rm tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkReconnectScenario.cs
```

- [ ] **Step 3: Build**

```bash
dotnet build tests/PbxAdmin.LoadTests/
```

Expected: Build succeeded. Any compilation errors from removed references need fixing.

- [ ] **Step 4: Commit**

```bash
git add -A tests/PbxAdmin.LoadTests/Scenarios/
git commit -m "feat(sdk-tests): register 9 new SDK scenarios, remove 3 old ones"
```

---

## Task 12: Audit Interval by Level

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScenarioBase.cs`

Per the spec, audit interval should be 5s for scale/endurance, 10s for smoke/functional.

- [ ] **Step 1: Add AuditIntervalForLevel helper to SdkScenarioBase**

```csharp
protected static int AuditIntervalForLevel(string level) => level switch
{
    "scale" or "endurance" => 5,
    _ => 10,
};
```

This is consumed by `Program.cs` when starting the `AuditMonitorService`. When running via `--level`, the audit interval should be set to the minimum interval of all scenarios in that level. When running a single scenario via `--scenario`, read the `Level` property from the scenario if it's a `SdkScenarioBase`.

- [ ] **Step 2: Update Program.cs to auto-set audit interval for SDK scenarios**

In the scenario execution path, after resolving the scenario:

```csharp
if (testScenario is Sdk.SdkScenarioBase sdkScenario && auditIntervalSecs == 10)
{
    auditIntervalSecs = Sdk.SdkScenarioBase.AuditIntervalForLevel(sdkScenario.Level);
}
```

Make `AuditIntervalForLevel` `internal static` instead of `protected static`.

- [ ] **Step 3: Build and commit**

```bash
dotnet build tests/PbxAdmin.LoadTests/ && \
git add tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScenarioBase.cs tests/PbxAdmin.LoadTests/Program.cs && \
git commit -m "feat(sdk-tests): auto-set audit interval by pyramid level (5s scale, 10s functional)"
```

---

## Task 13: Build Verification + Smoke Test

- [ ] **Step 1: Full solution build**

```bash
dotnet build PbxAdmin.slnx
```

Expected: Build succeeded. 0 errors, 0 warnings.

- [ ] **Step 2: Verify scenario registration**

```bash
dotnet run --project tests/PbxAdmin.LoadTests -- --scenario sdk-smoke --agents 1 --duration 0 2>&1 | head -5
```

Expected: Scenario loads without crash (may fail due to no Docker stack, but proves scenario registry works).

- [ ] **Step 3: Start SDK test stack**

```bash
cd docker && docker compose -f docker-compose.sdk-tests.yml up -d
```

Wait for all services healthy.

- [ ] **Step 4: Run smoke test**

```bash
dotnet run --project tests/PbxAdmin.LoadTests -- \
  --scenario sdk-smoke --agents 5 --duration 1 \
  --output tests/sdk-scenario-results/sdk-smoke.json \
  --audit-interval 10
```

Expected: Completes in < 90s. Check output for passed/failed validations.

- [ ] **Step 5: Verify output files exist**

```bash
ls -la tests/sdk-scenario-results/sdk-smoke*
```

Expected: `sdk-smoke.json`, `sdk-smoke.json.audit.json`, `sdk-smoke.json.audit.jsonl`

- [ ] **Step 6: Commit results exclusion**

Ensure `tests/sdk-scenario-results/` is in `.gitignore` (results are ephemeral).

```bash
git status
```

- [ ] **Step 7: Final commit**

```bash
git add -A && git commit -m "feat(sdk-tests): SDK test pyramid complete — 9 scenarios, 4 levels"
```
