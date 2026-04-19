# Progressive Scaling Fixes — Post-Audit Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix 3 issues found in the first progressive-scaling audit so the 200-agent test completes with a report.

**Architecture:** The timeout, concurrent-call cap, and ODBC pool are independent fixes. Each targets a specific bottleneck identified in the audit.

**Tech Stack:** .NET 10, Docker Compose, Asterisk res_odbc

---

## Audit Evidence

| Issue | Evidence | Root Cause |
|-------|----------|------------|
| Validation timeout | `TaskCanceledException` at 07:48:06 in ValidateAsync | Program CTS `durationMinutes + 5` too short for 10min ramp + 5min sustain + drain + validation |
| 606 concurrent calls (cap was 160) | Docker stats show 606 active calls | Scheduler slot hold != actual call duration; Asterisk queue distributes beyond scheduler tracking |
| ODBC pool 20/20 | `odbc show` reports 20/20 throughout sustain phase | 200 realtime agents × concurrent lookups exceed pool |

---

## File Map

| File | Action | Changes |
|------|--------|---------|
| `tests/PbxAdmin.LoadTests/Program.cs` | Modify | Increase CTS timeout to include drain+validation margin |
| `tests/PbxAdmin.LoadTests/CallGeneration/CallPatternScheduler.cs` | Modify | Slot duration uses randomized variance matching agent behavior |
| `docker/asterisk-config-realtime/res_odbc.conf` | Modify | max_connections 20 → 30 |

---

### Task 1: Fix validation timeout

The Program.cs CTS on line 169 is `durationMinutes + 5`. With progressive scaling, `durationMinutes` already includes ramp time, but the +5 margin isn't enough for drain (60s talk + 10s wrapup) + validation (DB queries + 5s flush delay).

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/Program.cs:169`

- [ ] **Step 1: Increase timeout margin**

In `tests/PbxAdmin.LoadTests/Program.cs`, find line 169:

```csharp
    cts.CancelAfter(TimeSpan.FromMinutes(durationMinutes + 5));
```

Replace with:

```csharp
    // Margin: drain (talk + wrapup + 10s) + validation (DB flush 5s + queries ~30s)
    int drainSecs = agentBehaviorOpts.TalkTimeSecs + agentBehaviorOpts.WrapupMaxSecs + 10;
    int marginMinutes = (drainSecs / 60) + 2; // +2 for validation queries
    cts.CancelAfter(TimeSpan.FromMinutes(callPatternOpts.TestDurationMinutes + marginMinutes));
    logger.LogInformation("Test timeout: {Duration} min + {Margin} min margin = {Total} min",
        callPatternOpts.TestDurationMinutes, marginMinutes, callPatternOpts.TestDurationMinutes + marginMinutes);
```

Note: use `callPatternOpts.TestDurationMinutes` (already auto-adjusted) not `durationMinutes` (raw CLI value).

- [ ] **Step 2: Build and test**

Run: `dotnet build PbxAdmin.slnx -v q && dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: Build succeeded, all tests pass

- [ ] **Step 3: Commit**

```bash
git add tests/PbxAdmin.LoadTests/Program.cs
git commit -m "fix(loadtest): increase CTS timeout margin for drain and validation

With 60s talk time + 10s wrapup, drain takes ~80s. Previous +5 min margin
was consumed by ramp time. Now calculates margin from actual agent timing."
```

---

### Task 2: Increase ODBC pool

**Files:**
- Modify: `docker/asterisk-config-realtime/res_odbc.conf:5`

- [ ] **Step 1: Change max_connections**

In `docker/asterisk-config-realtime/res_odbc.conf`, change:

```ini
max_connections => 30
```

- [ ] **Step 2: Commit**

```bash
git add docker/asterisk-config-realtime/res_odbc.conf
git commit -m "fix(docker): increase ODBC pool to 30 for 200+ agent realtime queries

Pool was 20/20 saturated throughout 200-agent sustained phase.
30 connections provides headroom for concurrent PJSIP + queue lookups."
```

---

### Task 3: Sync scheduler slot with agent variance

The scheduler holds each call "slot" for `DefaultCallDurationSecs` (synced to `talk + ring + wrapup`). But with ±20% variance, actual call duration ranges from 48s to 72s (at 60s base). The fixed slot of 67s means some slots are held too long (agent is already idle but slot still blocks), preventing new calls from generating. Meanwhile Asterisk's queue independently distributes queued callers to free agents, bypassing the scheduler's tracking.

The fix: randomize the slot duration with the same variance as the agent.

**Files:**
- Modify: `tests/PbxAdmin.LoadTests/CallGeneration/CallPatternScheduler.cs`

- [ ] **Step 1: Add variance to slot duration in GenerateAndTrackCallAsync**

In `CallPatternScheduler.cs`, find `GenerateAndTrackCallAsync`. The call to `PickCallDuration` returns a fixed duration. Find the line:

```csharp
        int durationSecs = PickCallDuration(
            scenario,
            _options.DefaultCallDurationSecs,
            _options.MinCallDurationSecs,
            _options.MaxCallDurationSecs,
            _random);
```

Replace with:

```csharp
        // Apply ±20% variance to match agent behavior timing
        int baseDuration = _options.DefaultCallDurationSecs;
        int variance = baseDuration / 5; // 20%
        int durationSecs = baseDuration + _random.Next(-variance, variance + 1);
        durationSecs = Math.Max(_options.MinCallDurationSecs, Math.Min(durationSecs, _options.MaxCallDurationSecs));
```

- [ ] **Step 2: Build and test**

Run: `dotnet build PbxAdmin.slnx -v q && dotnet test tests/PbxAdmin.Tests/ -v q`
Expected: Build succeeded, all tests pass

- [ ] **Step 3: Commit**

```bash
git add tests/PbxAdmin.LoadTests/CallGeneration/CallPatternScheduler.cs
git commit -m "fix(loadtest): randomize scheduler slot duration to match agent variance

Fixed slot duration caused desync between scheduler tracking and actual
agent lifecycle. Now applies ±20% variance matching agent talk time."
```

---

### Task 4: Verify — run 200-agent test

- [ ] **Step 1: Rebuild Docker**

```bash
cd docker
docker compose -f docker-compose.pbxadmin.yml down -v
docker compose -f docker-compose.pbxadmin.yml build --no-cache
docker compose -f docker-compose.pbxadmin.yml up -d
```

Wait for all containers healthy.

- [ ] **Step 2: Run 200-agent test**

```bash
dotnet tests/PbxAdmin.LoadTests/bin/Debug/net10.0/PbxAdmin.LoadTests.dll \
  --scenario sustained-load \
  --agents 200 \
  --talk-time 60 \
  --duration 15 \
  --output /tmp/load-test-200-progressive-v2.json
```

Expected:
- Test completes without TaskCanceledException
- JSON report is written
- 0 RTP errors
- PSTN CPU < 100%
- ODBC pool not at 30/30
