# Progressive Agent Scaling — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scale the load test platform to 300+ agents with progressive onboarding, adaptive scheduling, and human-like behavior variance.

**Architecture:** Replace the "all at once" registration + scheduling approach with wave-based agent onboarding (20 per wave, adaptive interval), agent-availability-driven call scheduling, and randomized agent timing. Infrastructure changes expand RTP capacity and optimize queue distribution.

**Tech Stack:** .NET 10, SIPSorcery (SIP registration), Asterisk.Sdk.Ami (AMI connection), Dapper (PostgreSQL), xUnit + FluentAssertions + NSubstitute, Docker Compose

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `docker/asterisk-config-realtime/rtp.conf` | Modify | RTP port range 20000-21999 |
| `docker/docker-compose.pbxadmin.yml` | Modify | Port mapping for expanded RTP |
| `docker/sql/017-loadtest-agents.sql` | Modify | Queue strategy to rrmemory |
| `tests/PbxAdmin.LoadTests/Configuration/AgentBehaviorOptions.cs` | Modify | Variance config properties |
| `tests/PbxAdmin.LoadTests/AgentEmulation/SipAgent.cs` | Modify | Randomized ring/talk/wrapup |
| `tests/PbxAdmin.LoadTests/AgentEmulation/AgentPoolService.cs` | Modify | Wave-based registration |
| `tests/PbxAdmin.LoadTests/AgentEmulation/AgentProvisioningService.cs` | Modify | Queue strategy rrmemory |
| `tests/PbxAdmin.LoadTests/CallGeneration/CallPatternScheduler.cs` | Modify | Agent-availability targeting |
| `tests/PbxAdmin.LoadTests/Metrics/MetricsCollector.cs` | Modify | Phase-aware metrics |
| `tests/PbxAdmin.LoadTests/Scenarios/Load/SustainedLoadScenario.cs` | Modify | Phase markers, ramp-aware validation |
| `tests/PbxAdmin.LoadTests/Program.cs` | Modify | Duration auto-calc, orchestration, RequestInitialStateAsync |
| `tests/PbxAdmin.Tests/LoadTests/AgentPoolServiceTests.cs` | Modify | Wave calculation tests |
| `tests/PbxAdmin.Tests/LoadTests/AgentBehaviorOptionsTests.cs` | Create | Variance property tests |

---

### Task 1: Infrastructure — RTP ports and queue strategy

Config-only changes. No code, no tests — verified by Docker rebuild.

**Files:**
- Modify: `docker/asterisk-config-realtime/rtp.conf:5`
- Modify: `docker/docker-compose.pbxadmin.yml:34,92`
- Modify: `docker/sql/017-loadtest-agents.sql:5`
- Modify: `tests/PbxAdmin.LoadTests/AgentEmulation/AgentProvisioningService.cs:139`

- [ ] **Step 1: Expand RTP port range**

In `docker/asterisk-config-realtime/rtp.conf`, change line 5:

```ini
rtpend=21999
```

Update the comment on line 3 to:

```ini
; 2000 ports supports ~300+ concurrent bridged calls.
```

- [ ] **Step 2: Update Docker port mapping for realtime server**

In `docker/docker-compose.pbxadmin.yml`, change the RTP port mapping (line 34):

```yaml
      - "20000-21999:20000-21999/udp"   # RTP media
```

- [ ] **Step 3: Change loadtest queue strategy to rrmemory**

In `docker/sql/017-loadtest-agents.sql`, change line 5-6:

```sql
INSERT INTO queue_table (name, strategy, timeout, ringinuse, wrapuptime, servicelevel, maxlen) VALUES
    ('loadtest', 'rrmemory', 15, 'no', 5, 20, 0)
ON CONFLICT (name) DO NOTHING;
```

- [ ] **Step 4: Update provisioning to use rrmemory**

In `tests/PbxAdmin.LoadTests/AgentEmulation/AgentProvisioningService.cs`, change the queues_config INSERT (line 139) from `'leastrecent'` to `'rrmemory'`:

```csharp
int queueConfigId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
    """
    INSERT INTO queues_config (server_id, name, strategy, timeout, wrapuptime, servicelevel, ringinuse, notes)
    VALUES (@ServerId, @QueueName, 'rrmemory', 15, 5, 20, 'no', 'Auto-created by load test provisioning')
    ON CONFLICT (server_id, name) DO UPDATE SET notes = EXCLUDED.notes
    RETURNING id
    """,
    new { ServerId = _serverId, QueueName },
    cancellationToken: ct));
```

- [ ] **Step 5: Build to verify no errors**

Run: `dotnet build PbxAdmin.slnx -v q`
Expected: Build succeeded, 0 warnings, 0 errors

- [ ] **Step 6: Commit**

```bash
git add docker/asterisk-config-realtime/rtp.conf docker/docker-compose.pbxadmin.yml docker/sql/017-loadtest-agents.sql tests/PbxAdmin.LoadTests/AgentEmulation/AgentProvisioningService.cs
git commit -m "fix(docker): expand RTP to 2000 ports and switch loadtest queue to rrmemory

RTP range 20000-21999 supports 300+ concurrent bridged calls.
Queue strategy rrmemory is O(1) vs O(N) leastrecent at 300 members."
```

---

### Task 2: Agent behavior variance configuration

Add properties to `AgentBehaviorOptions` for randomized timing. Tests first.

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/Configuration/AgentBehaviorOptions.cs`
- Create: `tests/PbxAdmin.Tests/LoadTests/AgentBehaviorOptionsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/PbxAdmin.Tests/LoadTests/AgentBehaviorOptionsTests.cs`:

```csharp
using FluentAssertions;
using PbxAdmin.LoadTests.Configuration;

namespace PbxAdmin.Tests.LoadTests;

public sealed class AgentBehaviorOptionsTests
{
    [Fact]
    public void RingDelayMaxSecs_ShouldDefaultTo5()
    {
        var opts = new AgentBehaviorOptions();
        opts.RingDelayMaxSecs.Should().Be(5);
    }

    [Fact]
    public void TalkTimeVariancePercent_ShouldDefaultTo20()
    {
        var opts = new AgentBehaviorOptions();
        opts.TalkTimeVariancePercent.Should().Be(20);
    }

    [Fact]
    public void WrapupMaxSecs_ShouldDefaultTo10()
    {
        var opts = new AgentBehaviorOptions();
        opts.WrapupMaxSecs.Should().Be(10);
    }

    [Fact]
    public void WaveSize_ShouldDefaultTo20()
    {
        var opts = new AgentBehaviorOptions();
        opts.WaveSize.Should().Be(20);
    }

    [Fact]
    public void WaveStabilizationSecs_ShouldDefaultTo30()
    {
        var opts = new AgentBehaviorOptions();
        opts.WaveStabilizationSecs.Should().Be(30);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PbxAdmin.Tests/ --filter "AgentBehaviorOptionsTests" -v n`
Expected: FAIL — properties don't exist

- [ ] **Step 3: Add the new properties**

In `tests/PbxAdmin.LoadTests/Configuration/AgentBehaviorOptions.cs`, add after `WrapupTimeSecs`:

```csharp
public sealed class AgentBehaviorOptions
{
    public const string SectionName = "AgentBehavior";

    public int AgentCount { get; init; } = 20;
    public int MinAgents { get; init; } = 1;
    public int MaxAgents { get; init; } = 300;
    public int RingDelaySecs { get; init; } = 2;
    public int RingDelayMaxSecs { get; init; } = 5;
    public int TalkTimeSecs { get; set; } = 30;
    public int TalkTimeVariancePercent { get; init; } = 20;
    public int WrapupTimeSecs { get; init; } = 5;
    public int WrapupMaxSecs { get; init; } = 10;
    public bool AutoAnswer { get; init; } = true;
    public int WaveSize { get; init; } = 20;
    public int WaveStabilizationSecs { get; init; } = 30;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PbxAdmin.Tests/ --filter "AgentBehaviorOptionsTests" -v n`
Expected: PASS (5 tests)

- [ ] **Step 5: Run full test suite**

Run: `dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: All tests pass

- [ ] **Step 6: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Configuration/AgentBehaviorOptions.cs tests/PbxAdmin.Tests/LoadTests/AgentBehaviorOptionsTests.cs
git commit -m "feat(loadtest): add agent behavior variance and wave sizing config

RingDelayMaxSecs (2-5s range), TalkTimeVariancePercent (±20%),
WrapupMaxSecs (5-10s range), WaveSize (20), WaveStabilizationSecs (30)."
```

---

### Task 3: Human-like agent timing

Add randomized ring delay, talk time, and wrapup to SipAgent.

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/AgentEmulation/SipAgent.cs:31,269-275,422-438,448-474`

- [ ] **Step 1: Add Random field to SipAgent**

In `SipAgent.cs`, add a `Random` field after the existing private fields (after line 32):

```csharp
    private readonly Random _random = new();
```

- [ ] **Step 2: Randomize ring delay in HandleIncomingInviteAsync**

In `SipAgent.cs`, replace the fixed ring delay (line 269-275):

```csharp
        if (_behavior.AutoAnswer)
        {
            int ringDelay = _random.Next(_behavior.RingDelaySecs, _behavior.RingDelayMaxSecs + 1);
            await Task.Delay(TimeSpan.FromSeconds(ringDelay));

            if (_state == AgentState.Ringing && !_inviteCancelled)
            {
                await AnswerAsync();
            }
        }
```

- [ ] **Step 3: Randomize talk time in AutoHangupAfterTalkTimeAsync**

In `SipAgent.cs`, replace the fixed talk time delay in `AutoHangupAfterTalkTimeAsync` (line 422-438):

```csharp
    private async Task AutoHangupAfterTalkTimeAsync(CancellationToken ct)
    {
        // Apply ±variance% to talk time for human-like behavior
        int baseTalkTime = _behavior.TalkTimeSecs;
        double variance = baseTalkTime * _behavior.TalkTimeVariancePercent / 100.0;
        int actualTalkTime = baseTalkTime + _random.Next((int)-variance, (int)variance + 1);
        actualTalkTime = Math.Max(1, actualTalkTime);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(actualTalkTime), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_state == AgentState.InCall || _state == AgentState.OnHold)
        {
            _logger.LogInformation("Agent {Ext}: talk time elapsed, hanging up", ExtensionId);
            await HangupAsync();
        }
    }
```

- [ ] **Step 4: Randomize wrapup time in BeginWrapupAsync**

In `SipAgent.cs`, replace the fixed wrapup delay in `BeginWrapupAsync` (line 448-474):

```csharp
    private async Task BeginWrapupAsync()
    {
        TransitionTo(AgentState.Wrapup);

        if (_wrapupCts is not null)
        {
            await _wrapupCts.CancelAsync();
            _wrapupCts.Dispose();
        }
        _wrapupCts = new CancellationTokenSource();
        var token = _wrapupCts.Token;

        int wrapupTime = _random.Next(_behavior.WrapupTimeSecs, _behavior.WrapupMaxSecs + 1);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(wrapupTime), token);

            if (!token.IsCancellationRequested)
            {
                TransitionTo(AgentState.Idle);
                _logger.LogDebug("Agent {Ext}: wrapup complete, now idle", ExtensionId);
            }
        }
        catch (OperationCanceledException)
        {
            // Wrapup cancelled — state managed externally (e.g. DisposeAsync)
        }
    }
```

- [ ] **Step 5: Build and run full test suite**

Run: `dotnet build PbxAdmin.slnx -v q && dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: Build succeeded, all tests pass

- [ ] **Step 6: Commit**

```bash
git add tests/PbxAdmin.LoadTests/AgentEmulation/SipAgent.cs
git commit -m "feat(loadtest): randomize ring/talk/wrapup timing for human-like behavior

Ring delay: 2-5s random. Talk time: ±20% variance. Wrapup: 3-10s random.
Eliminates thundering-herd hangup spikes at fixed intervals."
```

---

### Task 4: Wave-based agent registration

Replace the "register all → wait" approach with progressive waves in AgentPoolService.

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/AgentEmulation/AgentPoolService.cs:28-31,106-168,196-290`
- Modify: `tests/PbxAdmin.Tests/LoadTests/AgentPoolServiceTests.cs`

- [ ] **Step 1: Write wave calculation test**

Add to `tests/PbxAdmin.Tests/LoadTests/AgentPoolServiceTests.cs`:

```csharp
    // -------------------------------------------------------------------------
    // Wave calculation
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(10, 20, 1)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    [InlineData(50, 20, 3)]
    [InlineData(100, 20, 5)]
    [InlineData(200, 20, 10)]
    [InlineData(300, 20, 15)]
    public void CalculateWaveCount_ShouldCeilDivide(int agentCount, int waveSize, int expectedWaves)
    {
        int waves = AgentPoolService.CalculateWaveCount(agentCount, waveSize);
        waves.Should().Be(expectedWaves);
    }

    [Theory]
    [InlineData(50, 20, 8)]    // 3 waves + 5 sustain
    [InlineData(100, 20, 10)]  // 5 waves + 5
    [InlineData(200, 20, 15)]  // 10 waves + 5
    [InlineData(300, 20, 20)]  // 15 waves + 5
    public void CalculateMinDuration_ShouldIncludeRampAndSustain(int agentCount, int waveSize, int expectedMinutes)
    {
        int min = AgentPoolService.CalculateMinDurationMinutes(agentCount, waveSize);
        min.Should().Be(expectedMinutes);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PbxAdmin.Tests/ --filter "CalculateWaveCount|CalculateMinDuration" -v n`
Expected: FAIL — methods don't exist

- [ ] **Step 3: Add wave calculation static methods**

In `AgentPoolService.cs`, replace the existing constants (lines 28-31) and add the new static methods after `CalculateBatchSize` (around line 346):

Replace constants:
```csharp
    // Readiness gate configuration (per-wave, not global)
    internal const int WaveReadyPercent = 80;
    internal const int WaveReadinessTimeoutSecs = 60;
    internal const int WaveReadinessPollSecs = 2;
```

Add static methods:
```csharp
    internal static int CalculateWaveCount(int agentCount, int waveSize)
        => (agentCount + waveSize - 1) / waveSize;

    internal static int CalculateMinDurationMinutes(int agentCount, int waveSize)
        => CalculateWaveCount(agentCount, waveSize) + 5;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PbxAdmin.Tests/ --filter "CalculateWaveCount|CalculateMinDuration" -v n`
Expected: PASS

- [ ] **Step 5: Rewrite StartAsync with wave-based registration**

Replace the entire `StartAsync` method body (lines 106-168) with:

```csharp
    public async Task StartAsync(int agentCount, CancellationToken ct)
    {
        if (agentCount < _behaviorOptions.MinAgents)
            throw new ArgumentOutOfRangeException(nameof(agentCount),
                $"agentCount {agentCount} is below MinAgents {_behaviorOptions.MinAgents}.");

        if (agentCount > _behaviorOptions.MaxAgents)
            throw new ArgumentOutOfRangeException(nameof(agentCount),
                $"agentCount {agentCount} exceeds MaxAgents {_behaviorOptions.MaxAgents}.");

        _transport = new SIPTransport();
        _transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Any, 0));
        _transport.SIPTransportRequestReceived += OnTransportRequestReceived;

        _agents = new List<SipAgent>(agentCount);
        var serverHost = _loadOptions.TargetPbxAmi.Host;
        int sipPort = GetSipPort(_loadOptions.TargetServer);

        // Create all agents upfront (lightweight, no registration yet)
        for (int i = 0; i < agentCount; i++)
        {
            var (extensionId, password) = GetAgentCredentials(i, _loadOptions.TargetServer);
            var agentLogger = _loggerFactory.CreateLogger($"SipAgent.{extensionId}");

            var agent = new SipAgent(
                extensionId, password, serverHost, sipPort,
                _transport, _behaviorOptions, agentLogger);

            _agents.Add(agent);
        }

        // Wire metrics before waves so all state transitions are tracked
        _metrics?.SetTotalAgents(agentCount);
        foreach (var agent in _agents)
            WireAgentMetrics(agent);
        _started = true;

        // Register in progressive waves
        int waveSize = _behaviorOptions.WaveSize;
        int waveCount = CalculateWaveCount(agentCount, waveSize);

        _logger.LogInformation(
            "Registering {N} agents in {Waves} waves of {Size} (adaptive interval)",
            agentCount, waveCount, waveSize);

        for (int w = 0; w < waveCount; w++)
        {
            ct.ThrowIfCancellationRequested();
            int start = w * waveSize;
            int end = Math.Min(start + waveSize, agentCount);
            var wave = _agents.GetRange(start, end - start);

            _logger.LogInformation("Wave {Wave}/{Total}: registering agents {First}-{Last}",
                w + 1, waveCount, _agents[start].ExtensionId, _agents[end - 1].ExtensionId);

            // Fire registration for all agents in wave
            foreach (var agent in wave)
                await agent.RegisterAsync(ct);

            // Poll until wave settles
            await WaitForWaveReadyAsync(wave, w + 1, ct);

            // Stabilization delay between waves (skip after last wave)
            if (w < waveCount - 1)
                await Task.Delay(TimeSpan.FromSeconds(_behaviorOptions.WaveStabilizationSecs), ct);
        }

        int idle = IdleAgents;
        int errors = _agents.Count(a => a.State == AgentState.Error);
        _logger.LogInformation("All waves complete: {N} agents ({Idle} idle, {Errors} errors)", agentCount, idle, errors);
    }
```

- [ ] **Step 6: Replace WaitForReadyAsync with WaitForWaveReadyAsync**

Replace the old `WaitForReadyAsync`, `PollUntilSettledAsync`, and `RetryErrorAgentsAsync` methods with a single wave-aware method:

```csharp
    private async Task WaitForWaveReadyAsync(List<SipAgent> wave, int waveNumber, CancellationToken ct)
    {
        int total = wave.Count;
        int required = (int)Math.Ceiling(total * WaveReadyPercent / 100.0);
        var deadline = DateTime.UtcNow.AddSeconds(WaveReadinessTimeoutSecs);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(WaveReadinessPollSecs), ct);

            int idle = wave.Count(a => a.State == AgentState.Idle);
            int errors = wave.Count(a => a.State == AgentState.Error);
            int registering = wave.Count(a => a.State == AgentState.Registering);

            if (idle >= required || registering == 0)
            {
                _logger.LogInformation(
                    "Wave {Wave} ready: {Idle}/{Total} Idle, {Errors} Error",
                    waveNumber, idle, total, errors);
                return;
            }
        }

        // Timeout — log warning but continue (agents will register via SIPSorcery retry)
        int finalIdle = wave.Count(a => a.State == AgentState.Idle);
        _logger.LogWarning(
            "Wave {Wave} timeout: {Idle}/{Total} Idle after {Timeout}s — continuing",
            waveNumber, finalIdle, total, WaveReadinessTimeoutSecs);
    }
```

- [ ] **Step 7: Update existing tests for renamed constants**

In `tests/PbxAdmin.Tests/LoadTests/AgentPoolServiceTests.cs`, update the readiness constant tests:

Replace `MinReadyPercent_ShouldDefaultTo80`:
```csharp
    [Fact]
    public void WaveReadyPercent_ShouldDefaultTo80()
    {
        AgentPoolService.WaveReadyPercent.Should().Be(80);
    }
```

Replace `ReadinessTimeoutSecs_ShouldDefaultTo60`:
```csharp
    [Fact]
    public void WaveReadinessTimeoutSecs_ShouldDefaultTo60()
    {
        AgentPoolService.WaveReadinessTimeoutSecs.Should().Be(60);
    }
```

Replace `ReadinessPollIntervalSecs_ShouldDefaultTo2`:
```csharp
    [Fact]
    public void WaveReadinessPollSecs_ShouldDefaultTo2()
    {
        AgentPoolService.WaveReadinessPollSecs.Should().Be(2);
    }
```

Remove the `MaxRetryWaves_ShouldDefaultTo2` test (retry waves are no longer used — per-wave timeout handles it).

- [ ] **Step 8: Build and run full test suite**

Run: `dotnet build PbxAdmin.slnx -v q && dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: Build succeeded, all tests pass

- [ ] **Step 9: Commit**

```bash
git add tests/PbxAdmin.LoadTests/AgentEmulation/AgentPoolService.cs tests/PbxAdmin.Tests/LoadTests/AgentPoolServiceTests.cs
git commit -m "feat(loadtest): wave-based agent registration with adaptive readiness

Agents register in waves of 20 with per-wave readiness polling and 30s
stabilization between waves. Eliminates SIPSorcery UDP saturation and
enables 300+ agents via progressive onboarding."
```

---

### Task 5: Adaptive call scheduling

Make the scheduler target follow agent availability instead of a time-based ramp.

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/CallGeneration/CallPatternScheduler.cs:50-61,225-254`
- Modify: `tests/PbxAdmin.LoadTests/Program.cs` (DI wiring)

- [ ] **Step 1: Add AgentPoolService dependency to scheduler**

In `CallPatternScheduler.cs`, add a field and modify the constructor:

Add field after `_metrics` (around line 21):
```csharp
    private readonly AgentPoolService? _agentPool;
```

Add `using PbxAdmin.LoadTests.AgentEmulation;` to the top of the file.

Change the constructor to accept optional AgentPoolService:
```csharp
    public CallPatternScheduler(
        IOptions<CallPatternOptions> options,
        CallGeneratorService generator,
        ILogger<CallPatternScheduler> logger,
        AgentPoolService? agentPool = null)
    {
        _options = options.Value;
        _generator = generator;
        _logger = logger;
        _agentPool = agentPool;

        foreach (var scenario in _options.ScenarioMix.Keys)
            _scenarioCounts[scenario] = 0;
    }
```

- [ ] **Step 2: Modify RunMainLoopAsync to use agent-availability targeting**

In `CallPatternScheduler.cs`, replace the target calculation in `RunMainLoopAsync` (lines 225-254):

```csharp
    private async Task RunMainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan elapsed = DateTime.UtcNow - _startedAt;

            // Target follows agent availability when pool is connected,
            // otherwise falls back to time-based ramp
            int currentTarget;
            if (_agentPool is not null)
            {
                int available = _agentPool.IdleAgents + _agentPool.InCallAgents
                              + _agentPool.RingingAgents;
                currentTarget = Math.Min(available, _targetConcurrent);
            }
            else
            {
                currentTarget = CalculateRampTarget(elapsed, _targetConcurrent, _options.RampUpMinutes);
            }

            int active = Volatile.Read(ref _activeCalls);
            int deficit = currentTarget - active;

            if (deficit > 0)
            {
                _logger.LogDebug(
                    "Deficit={Deficit}, Active={Active}, Target={Target}",
                    deficit, active, currentTarget);

                for (int i = 0; i < deficit; i++)
                    _ = GenerateAndTrackCallAsync(ct);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
```

- [ ] **Step 3: Wire AgentPoolService in Program.cs DI**

In `tests/PbxAdmin.LoadTests/Program.cs`, find where `CallPatternScheduler` is registered in the DI host builder (search for `AddSingleton<CallPatternScheduler>`). Add `AgentPoolService` as a dependency.

If the scheduler is created manually (not via DI), pass the `AgentPoolService` instance when constructing it. Find the line where `context.Scheduler` is assigned and ensure the `AgentPoolService` is passed.

In the host builder section, register `AgentPoolService` as singleton if not already done, and update the scheduler registration to inject it.

- [ ] **Step 4: Build and run full test suite**

Run: `dotnet build PbxAdmin.slnx -v q && dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: Build succeeded, all tests pass (existing scheduler tests use the default `agentPool: null` parameter)

- [ ] **Step 5: Commit**

```bash
git add tests/PbxAdmin.LoadTests/CallGeneration/CallPatternScheduler.cs tests/PbxAdmin.LoadTests/Program.cs
git commit -m "feat(loadtest): adaptive call scheduling driven by agent availability

Scheduler target follows pool.IdleAgents + InCallAgents instead of
time-based ramp. Naturally scales calls with progressive agent waves."
```

---

### Task 6: Duration auto-calculation and orchestration

Add minimum duration enforcement and restructure the main flow for wave-based operation.

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/Program.cs`

- [ ] **Step 1: Add duration auto-calculation after CLI parsing**

In `Program.cs`, after the CLI options are parsed and before `RunAsync` is called, add duration adjustment. Find where `callPatternOpts.TestDurationMinutes` is set (around line 118) and add after it:

```csharp
        // Auto-adjust duration for progressive agent onboarding
        int waveCount = AgentPoolService.CalculateWaveCount(agents, agentBehaviorOpts.WaveSize);
        int minDuration = AgentPoolService.CalculateMinDurationMinutes(agents, agentBehaviorOpts.WaveSize);

        if (callPatternOpts.TestDurationMinutes < minDuration)
        {
            logger.LogWarning(
                "{Agents} agents need at least {Min} min ({Waves} waves + 5 min sustained). Adjusting from {Current} to {Min} min",
                agents, minDuration, waveCount, callPatternOpts.TestDurationMinutes, minDuration);
            callPatternOpts.TestDurationMinutes = minDuration;
        }
```

Add `using PbxAdmin.LoadTests.AgentEmulation;` if not present.

- [ ] **Step 2: Add max-concurrent auto-cap**

In `Program.cs`, find the existing auto-tune of `MaxConcurrentCalls` (around line 122-127) and replace with:

```csharp
        // Cap max concurrent to 80% of agents (PSTN emulator ceiling)
        int maxConcurrentCap = (int)Math.Ceiling(agents * 0.8);
        if (callPatternOpts.MaxConcurrentCalls > maxConcurrentCap)
        {
            logger.LogInformation("Auto-tune: MaxConcurrentCalls {Old} → {New} (80% of {Agents} agents)",
                callPatternOpts.MaxConcurrentCalls, maxConcurrentCap, agents);
            callPatternOpts.MaxConcurrentCalls = maxConcurrentCap;
        }
```

- [ ] **Step 3: Move RequestInitialStateAsync to after waves complete**

In `Program.cs`, find where `RequestInitialStateAsync` is called (inside `ProvisionAgentsAsync`, around line 315). Remove it from there. Instead, add it after `StartAgentsAsync` returns in the main flow:

```csharp
        await StartAgentsAsync(context, agents, logger, cts.Token);

        // Refresh SDK live state after all waves have registered
        if (context.SdkRuntime is not null)
        {
            try
            {
                await context.SdkRuntime.Server.RequestInitialStateAsync(cts.Token);
                logger.LogInformation("SDK live state refreshed with {Agents} registered agents", agents);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SDK state refresh failed — metrics may be incomplete");
            }
        }

        await ConnectPstnEmulatorAsync(context, logger, cts.Token);
```

- [ ] **Step 4: Build and run full test suite**

Run: `dotnet build PbxAdmin.slnx -v q && dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: Build succeeded, all tests pass

- [ ] **Step 5: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Program.cs
git commit -m "feat(loadtest): auto-calculate duration and cap concurrent calls

Duration auto-adjusts to waves + 5min sustained. Max concurrent capped
at 80% of agents to protect PSTN emulator. RequestInitialStateAsync
moved to after all waves complete."
```

---

### Task 7: Phase-aware metrics

Add test phase tracking to MetricsCollector so validation can distinguish ramp from sustain.

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/Metrics/MetricsCollector.cs`

- [ ] **Step 1: Add phase enum and tracking**

In `MetricsCollector.cs`, add after the class opening:

```csharp
    public enum TestPhase { Ramp, Sustain, Drain }

    private TestPhase _currentPhase = TestPhase.Ramp;
    private DateTime _sustainStart;
    private DateTime _sustainEnd;

    public TestPhase CurrentPhase => _currentPhase;
    public DateTime SustainStart => _sustainStart;
    public DateTime SustainEnd => _sustainEnd;

    public void EnterSustainPhase()
    {
        _currentPhase = TestPhase.Sustain;
        _sustainStart = DateTime.UtcNow;
        _logger.LogInformation("[Metrics] Entered SUSTAIN phase");
    }

    public void EnterDrainPhase()
    {
        _sustainEnd = DateTime.UtcNow;
        _currentPhase = TestPhase.Drain;
        _logger.LogInformation("[Metrics] Entered DRAIN phase (sustain lasted {Duration:mm\\:ss})",
            _sustainEnd - _sustainStart);
    }
```

- [ ] **Step 2: Build and run full test suite**

Run: `dotnet build PbxAdmin.slnx -v q && dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: Build succeeded, all tests pass

- [ ] **Step 3: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Metrics/MetricsCollector.cs
git commit -m "feat(loadtest): add phase tracking to MetricsCollector

Tracks Ramp → Sustain → Drain phase transitions with timestamps.
Validation can use SustainStart/SustainEnd to evaluate steady-state only."
```

---

### Task 8: Sustained load scenario — phase markers and ramp-aware validation

Wire phase transitions into the scenario and adjust validation.

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/Scenarios/Load/SustainedLoadScenario.cs`

- [ ] **Step 1: Add phase transitions to ExecuteAsync**

In `SustainedLoadScenario.cs`, in `ExecuteAsync`:

After `context.Scheduler.StartAsync(...)` (line 50), add ramp tracking:

```csharp
        // Calculate when ramp ends and sustain begins
        int waveCount = AgentPoolService.CalculateWaveCount(
            context.AgentPool.TotalAgents, context.AgentBehavior.WaveSize);
        var rampEndEstimate = context.TestStartTime + TimeSpan.FromMinutes(waveCount);
```

Inside the main while loop, after the progress logging (around line 95), add phase transition detection:

```csharp
                // Transition to sustain phase when all agents have registered
                if (context.Metrics.CurrentPhase == MetricsCollector.TestPhase.Ramp)
                {
                    int totalAgents = context.AgentPool.TotalAgents;
                    int activeAgents = poolStats.Idle + poolStats.InCall + poolStats.Ringing + poolStats.Wrapup;
                    if (activeAgents >= totalAgents * 0.8 || DateTime.UtcNow > rampEndEstimate)
                    {
                        context.Metrics.EnterSustainPhase();
                    }
                }
```

Add `using PbxAdmin.LoadTests.AgentEmulation;` and `using PbxAdmin.LoadTests.Metrics;` to imports.

Before the drain phase (around line 104, after `context.Scheduler.StopAsync()`), enter drain:

```csharp
        await context.Scheduler.StopAsync();
        context.Metrics.EnterDrainPhase();
```

- [ ] **Step 2: Remove the forced RampUpMinutes=0 override**

In `SustainedLoadScenario.cs`, remove or comment out the line that forces `RampUpMinutes = 0` (around line 48-49). The scheduler now uses agent availability, not time-based ramp:

```csharp
        // Scheduling is now driven by agent availability — no ramp override needed
        await context.Scheduler.StartAsync(context.CallPattern.MaxConcurrentCalls, ct);
```

- [ ] **Step 3: Build and run full test suite**

Run: `dotnet build PbxAdmin.slnx -v q && dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: Build succeeded, all tests pass

- [ ] **Step 4: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Scenarios/Load/SustainedLoadScenario.cs
git commit -m "feat(loadtest): add phase transitions to sustained-load scenario

Tracks Ramp → Sustain → Drain phases. Sustain starts when 80% of agents
are active or ramp time expires. Validation focuses on sustain phase."
```

---

### Task 9: Integration verification

Rebuild Docker, run 200-agent test, validate all improvements.

**Files:** None (verification only)

- [ ] **Step 1: Full solution build**

Run: `dotnet build PbxAdmin.slnx -v q`
Expected: Build succeeded, 0 warnings

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: All tests pass

- [ ] **Step 3: Rebuild Docker stack**

```bash
cd docker
docker compose -f docker-compose.pbxadmin.yml down -v --rmi local
docker compose -f docker-compose.pbxadmin.yml build --no-cache
docker compose -f docker-compose.pbxadmin.yml up -d
```

Wait for all containers healthy.

- [ ] **Step 4: Verify RTP and queue config**

```bash
docker exec demo-pbx-realtime asterisk -rx "rtp show settings" | grep "Port end"
# Expected: Port end: 21999

docker exec demo-pbx-realtime sh -c "ulimit -n"
# Expected: 65535
```

- [ ] **Step 5: Run 200-agent sustained load test**

```bash
dotnet tests/PbxAdmin.LoadTests/bin/Debug/net10.0/PbxAdmin.LoadTests.dll \
  --scenario sustained-load \
  --agents 200 \
  --talk-time 60 \
  --duration 15 \
  --output /tmp/load-test-200-progressive.json
```

Expected:
- Duration auto-adjusted to 15 min (10 ramp + 5 sustain)
- 10 waves of 20 agents, each registering in ~5s
- Calls ramp progressively with agent availability
- 0 RTP port errors
- 0 agent leaks
- PSTN CPU < 90%
- Answer rate > 80% in sustain phase
- RESULT: PASSED

- [ ] **Step 6: Run 300-agent test**

```bash
dotnet tests/PbxAdmin.LoadTests/bin/Debug/net10.0/PbxAdmin.LoadTests.dll \
  --scenario sustained-load \
  --agents 300 \
  --talk-time 60 \
  --duration 20 \
  --output /tmp/load-test-300-progressive.json
```

Expected:
- Duration auto-adjusted to 20 min (15 ramp + 5 sustain)
- 15 waves of 20 agents
- All 300 agents reach Idle
- RESULT: PASSED

- [ ] **Step 7: Commit any remaining fixes**

If any tests fail, fix and commit. Otherwise, no action needed.

---

## Summary of Changes

| Change | Before | After | Impact |
|--------|--------|-------|--------|
| Agent registration | 200 at once, readiness gate | 20/wave, adaptive interval | SIPSorcery no longer saturates |
| Call scheduling | Time-based ramp or instant | Agent-availability driven | Calls only when agents ready |
| Ring delay | Fixed 2s | Random 2-5s | No synchronized answer pattern |
| Talk time | Fixed value | ±20% variance | Hangups spread over 24s window |
| Wrapup | Fixed 5s | Random 3-10s | No synchronized availability spike |
| RTP ports | 1000 (20000-20999) | 2000 (20000-21999) | Supports 300 concurrent calls |
| Queue strategy | leastrecent O(N) | rrmemory O(1) | 300x faster distribution |
| Max concurrent | Manual or agent count | 80% of agents auto-cap | PSTN emulator protected |
| Duration | Fixed by CLI | Auto-calculated minimum | Tests can't end before ramp |
| Metrics | Single phase | Ramp/Sustain/Drain | Validation on sustain only |
| SDK refresh | Before registration | After all waves | Complete member visibility |
