# Progressive Agent Scaling — Design Spec

**Date:** 2026-03-28
**Goal:** Make the load test infrastructure scale to 300+ agents with human-like behavior by replacing the "all at once" approach with progressive agent onboarding, adaptive call scheduling, and behavioral variance.

## Problem Statement

The current load test fires 200-300 SIP registrations simultaneously, saturating SIPSorcery's UDP socket and Asterisk's realtime query pipeline. Calls start at full concurrency immediately, overwhelming the PSTN emulator (102% CPU at 200 agents). All agents ring/talk/hangup at identical intervals, creating thundering-herd patterns that exhaust RTP ports (3,152 errors in a 15-minute test).

### Audit Evidence (200 agents, 60s talk, 15 min)

| Metric | Value | Problem |
|--------|-------|---------|
| RTP port errors | 3,152 | Port pool exhaustion at peak concurrent |
| PSTN CPU peak | 102% | Emulator is the bottleneck |
| Queue SLA | 100% → 28% | Progressive degradation under load |
| Callers waiting (peak) | 119 | Queue can't distribute fast enough |
| ODBC connections | 14/20 | Approaching pool ceiling |
| Agents registered | 200/200 | Registration works but takes ~2 min |

## Design

### 1. Progressive Agent Registration (AgentPoolService)

Replace the current "register all agents → wait for readiness gate" with wave-based onboarding.

**Wave calculation:**
- Wave size: 20 agents (proven to register in ~5 seconds)
- Wave count: `ceil(agentCount / 20)`
- Inter-wave behavior: adaptive — poll until 80% of the wave is Idle, then wait 30s before next wave

**Flow per wave:**
1. Create 20 SipAgent instances (assign to shared SIPTransport)
2. Call `RegisterAsync()` on all 20 (fire-and-forget, SIPSorcery handles async)
3. Poll every 2s until ≥16/20 (80%) are Idle or 60s timeout
4. Log wave status
5. Wait 30s for system stabilization
6. Next wave

**Readiness gate becomes per-wave**, not global. The global gate is removed — the test starts generating calls as soon as wave 1 is ready.

**Re-registration overlap**: With staggered expiry (90-150s), wave 1 agents start re-registering around minute 1.5-2.5. At 300 agents (minute 14), there are ~180 re-registrations distributed across 60s (~3/second) overlapping with the last wave of 20 new registrations. SIPSorcery handles 3 concurrent transactions/second easily — the current failure was 300 transactions in 0 seconds.

**Files changed:**
- `tests/PbxAdmin.LoadTests/AgentEmulation/AgentPoolService.cs` — new `StartWithWavesAsync()` method replacing `StartAsync()` internals

### 2. Adaptive Call Scheduling (CallPatternScheduler)

The scheduler currently uses `CalculateRampTarget(elapsed, target, rampUpMinutes)` which ramps linearly over time. Replace with agent-availability-driven targeting.

**New target calculation:**
```
target = min(pool.IdleAgents + pool.InCallAgents, maxConcurrentCalls)
```

This naturally ramps as agents come online:
- Minute 0: 20 agents → target=20
- Minute 5: 120 agents → target=120
- Minute 14: 300 agents → target=300 (capped by max-concurrent)

**Max concurrent auto-cap**: `maxConcurrent = min(userSpecified, ceil(agentCount * 0.8))`. This prevents the PSTN emulator from being overwhelmed. At 300 agents, max 240 concurrent calls. The PSTN emulator handled 200 bridges at 86% CPU — 240 is the practical ceiling.

**Slot duration sync**: Already auto-tuned to `talk + ring + wrapup`. With variance (see section 3), slots get randomized durations, distributing releases over time instead of all at once.

**Files changed:**
- `tests/PbxAdmin.LoadTests/CallGeneration/CallPatternScheduler.cs` — modify `RunMainLoopAsync` to query agent pool for target
- `tests/PbxAdmin.LoadTests/Program.cs` — pass AgentPoolService reference to scheduler

### 3. Human-Like Agent Behavior (SipAgent + AgentBehaviorOptions)

Add variance to agent timing to eliminate synchronized behavior patterns.

**Randomized parameters:**

| Parameter | Current | New | Distribution |
|-----------|---------|-----|-------------|
| RingDelaySecs | Fixed 2s | 2-5s | Uniform random per call |
| TalkTimeSecs | Fixed (CLI value) | ±20% of CLI value | Uniform random per call |
| WrapupTimeSecs | Fixed 5s | 3-10s | Uniform random per call |

**Implementation:** `SipAgent.HandleIncomingInviteAsync` and `AutoHangupAfterTalkTimeAsync` apply variance at call time, not at agent creation. Each call gets different timing.

**Effect on thundering herd:** With 200 agents all answering at second 0 with fixed 60s talk time, all 200 hang up at second 60. With ±20% variance, hangups spread from second 48 to second 72 — a 24-second window instead of a 1-second spike.

**Files changed:**
- `tests/PbxAdmin.LoadTests/AgentEmulation/SipAgent.cs` — randomize delays in `HandleIncomingInviteAsync` and `AutoHangupAfterTalkTimeAsync`
- `tests/PbxAdmin.LoadTests/Configuration/AgentBehaviorOptions.cs` — add `RingDelayMaxSecs`, `TalkTimeVariancePercent`, `WrapupMaxSecs`

### 4. RTP Port Expansion

Expand from 1000 to 2000 ports. At 300 agents with 60s talk time, peak concurrent calls reach ~300 with each call using 2-4 RTP ports.

**Calculation:**
- 300 concurrent calls × 4 ports worst case = 1,200 ports
- Safety margin (50%): 1,800 ports
- Round to 2,000: range 20000-21999

**Files changed:**
- `docker/asterisk-config-realtime/rtp.conf` — `rtpend=21999`
- `docker/docker-compose.pbxadmin.yml` — port mapping `20000-21999:20000-21999/udp`

### 5. Queue Strategy for Scale

Replace `leastrecent` with `rrmemory` (round-robin with memory) for the loadtest queue. At 300 members, `leastrecent` does O(N) comparisons per call distribution vs O(1) for `rrmemory`.

**Impact:** 300 members × 4.5 calls/second = 1,350 comparisons/second with `leastrecent` vs 4.5 with `rrmemory`.

**Files changed:**
- `docker/sql/017-loadtest-agents.sql` — change strategy to `rrmemory`
- `tests/PbxAdmin.LoadTests/AgentEmulation/AgentProvisioningService.cs` — use `rrmemory` in the queues_config INSERT

### 6. Duration Auto-Calculation

Enforce minimum test duration based on agent count so the test doesn't end before all agents have registered.

**Formula:**
```
waveCount = ceil(agents / 20)
rampMinutes = waveCount  (1 minute per wave including registration + stabilization)
minDuration = rampMinutes + 5  (at least 5 min sustained at full capacity)
```

**Examples:**
- `--agents 50 --duration 5` → adjusted to 8 min (3 ramp + 5 sustain)
- `--agents 200 --duration 5` → adjusted to 15 min (10 ramp + 5 sustain)
- `--agents 300 --duration 5` → adjusted to 20 min (15 ramp + 5 sustain)
- `--agents 200 --duration 30` → kept at 30 min (10 ramp + 20 sustain)

Log a warning when duration is auto-adjusted.

**Files changed:**
- `tests/PbxAdmin.LoadTests/Program.cs` — auto-adjust duration after parsing CLI args

### 7. Queue Reload Timing

The `module reload app_queue.so` currently runs before SIP registration. Members load as "Invalid" because endpoints have no contacts yet. Members become valid as agents register, but the queue may not distribute to them until the next queue evaluation cycle.

**New flow:**
1. Provision all endpoints + queue members in DB (bulk insert, fast)
2. `module reload app_queue.so` — loads members as Invalid (Asterisk knows about them)
3. Start wave 1 registration — as agents register, Asterisk marks them "Not in use"
4. Calls start flowing to registered agents via queue
5. After last wave: `RequestInitialStateAsync()` so SDK has complete picture

The key insight: Asterisk realtime queue members transition from Invalid to Not in use automatically when the PJSIP endpoint registers. No per-wave reload needed.

**Files changed:**
- `tests/PbxAdmin.LoadTests/Program.cs` — move `RequestInitialStateAsync` to after final wave

### 8. RequestInitialStateAsync Resilience

The SDK AMI connection can drop during heavy SIP registration. `RequestInitialStateAsync` must handle this gracefully.

**For load test (immediate fix):**
- Wrap in try-catch in Program.cs
- If SDK connection is down, log warning and continue — metrics will be incomplete but test still runs

**For PbxAdmin (v2 feature):**
- Add periodic queue state refresh (every 30s when Queues page is open)
- Or detect `Reload` AMI event and trigger refresh
- Or add manual "Refresh" button on Queues page

**Files changed:**
- `tests/PbxAdmin.LoadTests/Program.cs` — try-catch around RequestInitialStateAsync
- (v2) `src/PbxAdmin/Services/AsteriskMonitorService.cs` — periodic or event-driven refresh

### 9. Metrics: Ramp vs Sustain Phases

Separate metrics collection into phases so validation only evaluates steady-state performance.

**Phases:**
1. **Ramp** (0 to rampMinutes): agents registering, calls building. Track: registration success rate, time to register per wave.
2. **Sustain** (rampMinutes to duration - drainTime): all agents active. Track: answer rate, calls/min, peak concurrent, agent utilization, SLA.
3. **Drain** (last 45s+): no new calls, active calls finishing. Track: drain time, leaked agents.

**Validation** only runs against sustain phase data. Ramp phase metrics are informational only.

**Files changed:**
- `tests/PbxAdmin.LoadTests/Metrics/MetricsCollector.cs` — add phase tracking
- `tests/PbxAdmin.LoadTests/Scenarios/Load/SustainedLoadScenario.cs` — mark phase transitions

## File Map

| File | Action | Changes |
|------|--------|---------|
| `AgentPoolService.cs` | Modify | Wave-based registration, remove global readiness gate |
| `SipAgent.cs` | Modify | Randomized ring/talk/wrapup timing |
| `AgentBehaviorOptions.cs` | Modify | Add variance config properties |
| `CallPatternScheduler.cs` | Modify | Agent-availability-driven targeting, accept AgentPoolService in constructor |
| `CallPatternOptions.cs` | Modify | Remove RampUpMinutes dependency |
| `Program.cs` | Modify | Duration auto-calc, wave orchestration, RequestInitialStateAsync timing |
| `AgentProvisioningService.cs` | Modify | Queue strategy to rrmemory |
| `MetricsCollector.cs` | Modify | Phase-aware metrics |
| `SustainedLoadScenario.cs` | Modify | Phase markers, sustain-only validation |
| `AgentPoolServiceTests.cs` | Modify | Update tests for wave-based registration |
| `rtp.conf` (realtime) | Modify | rtpend=21999 |
| `docker-compose.pbxadmin.yml` | Modify | RTP port mapping 20000-21999 |
| `017-loadtest-agents.sql` | Modify | Strategy rrmemory |

## Success Criteria

1. `--agents 200 --duration 15` passes with 0 RTP errors, 0 agent leaks, answer rate >80% in sustain phase
2. `--agents 300 --duration 20` passes with same criteria
3. PSTN emulator CPU stays below 90% throughout
4. No SIPSorcery registration failures (all agents reach Idle)
5. Queue members visible in PbxAdmin when page is refreshed (load test SDK calls RequestInitialStateAsync)
6. Ramp phase completes within calculated time (±10%)

## Out of Scope

- PbxAdmin auto-refresh of queue state (documented for v2, not implemented here)
- Multiple PSTN emulators for higher throughput
- SIPSorcery replacement with native SIP stack
- Agent crash/recovery scenarios during ramp phase
- Talk-time distributions beyond uniform random (log-normal, Poisson, etc.)
