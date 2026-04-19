# SDK Test Pyramid — Design Spec

**Date:** 2026-03-29
**Status:** Draft
**Replaces:** sdk-session-accuracy, sdk-live-drift, sdk-reconnect (3 existing scenarios)

## Summary

Rewrite the SDK test suite as a 4-level pyramid (Smoke → Functional → Scale → Endurance) with 9 scenarios that validate the Asterisk.Sdk NuGet library against a live Asterisk instance. Tests are SDK-only — PbxAdmin is included in the Docker stack as a passive observer for visual monitoring but is never a test subject.

## Goals

1. Validate SDK state accuracy (Channels, Queues, Agents) against AMI ground truth
2. Validate CallSession lifecycle and CDR accuracy across all dispositions
3. Validate AMI reconnection resilience under load
4. Scale to 200 agents / 300 concurrent calls with enterprise-grade thresholds
5. Detect memory leaks, CPU regression, and resource exhaustion over 30 minutes

## Non-Goals

- Testing PbxAdmin Blazor UI, services, or SignalR circuits
- Testing Asterisk dialplan correctness (covered by functional scenarios)
- Audio/voice path quality validation
- Testing against asterisk-file server (realtime-only)

## Industry-Aligned Thresholds

Based on RFC 6076 (SIP Performance Metrics), enterprise contact center benchmarks (80/20 service level, 2-5% abandonment), and Asterisk capacity studies (4,500 concurrent call limit).

| Metric | Threshold | Industry Reference |
|--------|-----------|-------------------|
| Answer rate | >= 95% | Enterprise CC: 95-98% |
| Channel drift (SDK vs AMI) | < 2% | Real-time sync expectation |
| Queue state drift | < 2% | Same as channels |
| Agent state drift | < 2% | Same as channels |
| Session accuracy vs CDR | >= 98% | CDR = billing source of truth |
| AMI reconnection time | < 10 seconds | Enterprise failover: < 5s |
| Agent leak post-test | 0 | Zero tolerance |
| Channel leak post-test | 0 | Zero tolerance |
| SDK CPU usage | < 30% avg | Asterisk sizing: < 50% rule |
| SDK memory growth | < 1.5x over 30 min | Stable = no linear growth |

## Pyramid Structure

```
                    ┌─────────────┐
                    │  Endurance  │  1 scenario, 30 min, 200 agents
                    │  (pre-pub)  │  Everything combined
                    └──────┬──────┘
                   ┌───────┴────────┐
                   │     Scale      │  4 scenarios, 5 min each, 200 agents
                   │  (release-rc)  │  Channels, Queues, Agents, Sessions
                   └───────┬────────┘
              ┌────────────┴─────────────┐
              │        Functional        │  3 scenarios, 3 min each, 20 agents
              │       (pre-release)      │  State sync, Sessions, Reconnect
              └────────────┬─────────────┘
         ┌─────────────────┴──────────────────┐
         │              Smoke                  │  1 scenario, 1 min, 5 agents
         │           (every commit)            │  Quick validation of everything
         └─────────────────────────────────────┘
```

Each level requires the previous level to pass. If smoke fails, functional does not run.

## Scenario Details

### Level 1: Smoke

#### sdk-smoke (5 agents, 5 concurrent, 1 min)

The fast guardian. If this fails, nothing else runs.

**Flow:**
1. Register 5 SIP agents
2. Originate 5 calls (3 answered, 1 timeout, 1 failed)
3. Sample SDK state 3 times during active calls
4. Wait for drain (all calls end)
5. Validate

**Validations:**
- Channels: SDK count vs AMI `core show channels count` in each sample (exact match ±1, absolute — 2% is meaningless at 5 channels)
- Queues: SDK queue members vs AMI `queue show` (correct state)
- Agents: SDK agent states vs actual states (idle/incall match)
- Sessions: 5 CallSessions exist, dispositions match CDR
- Cleanup: 0 channels, 0 stuck agents, 0 parked calls
- Timing: completes in < 90 seconds

### Level 2: Functional

#### sdk-state-sync (20 agents, 15 concurrent, 3 min)

Validates the SDK's in-memory model mirrors Asterisk reality.

**Flow:**
1. Register 20 agents
2. Generate constant load: 15 concurrent calls for 3 min
3. Every 3 seconds, capture parallel snapshot:
   - SDK: `server.Channels.ChannelCount`, `server.Queues`, `server.Agents`
   - AMI: `core show channels count`, `queue show <name>`, agent states
4. Compare ~60 snapshots post-test

**Validations:**
- Channel drift: < 2% average, max absolute <= 4 channels
- Queue member count: SDK vs AMI match in >= 98% of samples
- Queue callers waiting: SDK vs AMI match in >= 95% of samples
- Agent states: idle/incall/paused match in >= 98% of samples
- No phantom channels (SDK reports channel AMI doesn't have)
- No invisible channels (AMI has channel SDK doesn't report)
- Cleanup: everything drains to 0

#### sdk-sessions (20 agents, 15 concurrent, 3 min)

Validates CallSession accuracy across complex call scenarios.

**Flow (sequential phases, each waits for drain before next):**
1. Register 20 agents
2. Phase 1 — Answered (15 calls): inbound → queue → agent answers → talk → hangup
3. Phase 2 — Timeout (5 calls): pause all agents → inbound → queue → no answer → timeout → unpause
4. Phase 3 — Failed (5 calls): inbound → invalid extension 999 → congestion
5. Phase 4 — Hold (3 calls): inbound → answer → agent hold 5s → resume → hangup
6. Phase 5 — Transfer (2 calls): inbound → answer → blind transfer to idle agent → second agent answers → hangup
7. Collect all CallSessions from SDK
8. Read CDRs from PostgreSQL
9. Cross-reference by LinkedId

**Validations:**
- Coverage: >= 98% of CDRs have a corresponding CallSession
- Disposition match: SDK state == CDR disposition (ANSWERED/NO ANSWER/FAILED)
- Duration match: |SDK duration - CDR billsec| <= 2 seconds
- Caller match: SDK CallerIdNum == CDR src
- Hold sessions: SDK records hold/unhold event, duration includes hold time
- Transfer sessions: SDK generates 2 legs with same LinkedId
- Participant count: answered calls have >= 2 participants
- No orphan sessions: every CallSession has a corresponding CDR

#### sdk-reconnect (20 agents, 15 concurrent, 3 min)

Validates recovery from AMI disconnections under active load.

**Flow:**
1. Register 20 agents
2. Generate load: 15 concurrent calls
3. At 30s: force AMI disconnect (`manager reload`)
   - Capture disconnect timestamp
   - Measure time to reconnection
   - Snapshot state pre/post reconnection
4. At 90s: second disconnect (different active calls)
5. At 150s: third disconnect
6. Drain

**Validations:**
- Reconnect time: < 10s for all 3 disconnections
- Connection alive: SDK reports connected after each reconnection
- State recovery: channels/queues/agents re-synchronize post-reconnection
- Session continuity: calls active during disconnect complete and have CDR
- Post-reconnect sessions: new calls after reconnection are tracked correctly
- No duplicate sessions: reconnection doesn't create duplicate sessions
- Cleanup: 0 leaks

### Level 3: Scale

All 4 scale scenarios share: 200 agents, ramp up to 300 concurrent in 1 min, sustain 4 min.

#### sdk-scale-channels (200 agents, 300 concurrent, 5 min)

**Validations:**
- Drift average < 2%
- Drift max absolute <= 6 channels (2% of 300)
- No phantom channels
- No invisible channels
- Channel state distribution: SDK Up/Ringing/Down proportions match AMI
- Peak concurrent >= 250 (confirms load arrived)

#### sdk-scale-queues (200 agents, 300 concurrent, 5 min)

**Validations:**
- Member count drift < 2%
- Callers waiting drift < 2%
- Queue strategy applied: distribution not concentrated on 1 agent
- Completed calls per queue: SDK vs QueueLog CONNECT events >= 98%
- Abandoned calls: SDK vs QueueLog ABANDON events >= 95%
- SLA: 80% answered in 30s (informational, non-blocking)

#### sdk-scale-agents (200 agents, 300 concurrent, 5 min)

**Validations:**
- Agent state drift < 2%
- State transitions: idle→ringing→incall→wrapup→idle correct sequence
- No stuck agents during test (informational)
- Agent utilization distribution: no agent with 0 calls while others have 20+
- Final state: all idle or offline, 0 in ringing/incall
- Agent count: SDK total agents == AMI total agents in >= 98% samples

#### sdk-scale-sessions (200 agents, 300 concurrent, 5 min)

**Validations:**
- Coverage >= 98% (CDRs with session match)
- Disposition accuracy >= 98%
- Duration accuracy: |delta| <= 2s in >= 95% of sessions
- No orphan sessions
- No duplicate sessions (same LinkedId, duplicated)
- Throughput: sessions/min stable (doesn't decrease over time)
- Session create latency: SDK detects call <= 1s after Newchannel event

### Level 4: Endurance

#### sdk-endurance (200 agents, 300 concurrent, 30 min)

The final test. Everything combined for sustained duration.

**Flow:**
1. Register 200 agents
2. Ramp up to 300 concurrent in 2 min
3. Sustain 25 min with constant load
4. At 15 min: force 1 AMI disconnect (reconnection under load)
5. Drain 3 min
6. Capture infrastructure metrics every 30s (Docker stats)

**Validations (all previous thresholds plus):**

From SDK Sampler:
- Channel drift < 2% sustained (doesn't grow over time)
- Session accuracy >= 98% sustained
- Reconnect at 15 min: < 10s, state recovered
- 0 agent leaks
- 0 channel leaks

From Audit Monitor:
- Memory: SDK process final < 1.5x SDK process initial
- CPU: SDK process average < 30%
- ODBC pool: peak utilization < 70% (informational)
- Asterisk memory: stable (no linear growth)
- Error rate: < 1% container errors relative to calls originated

From Metrics Collector:
- Answer rate >= 95%

## Data Collection: Two Systems, Two Purposes

SDK tests use two independent data collection systems running in parallel. They do NOT duplicate work — each has a distinct role.

### SDK Sampler (logical state validation)

**Purpose:** Compare SDK in-memory state against Asterisk AMI ground truth.
**Mechanism:** AMI commands via the existing `IAmiConnection` (~5ms latency).
**Interval:** Every 3 seconds during active test phases.

Collects:
- `server.Channels.ChannelCount` vs AMI `core show channels count`
- `server.Queues` members/callers vs AMI `queue show <name>`
- `server.Agents` states vs AMI agent state queries
- `ICallSessionManager` sessions vs CDR database post-test

This is the **validation data** — what determines pass/fail for drift and accuracy checks.

### Audit Monitor (infrastructure context)

**Purpose:** Monitor physical infrastructure health alongside the test.
**Mechanism:** `docker exec` and `docker stats` (~200ms latency per command).
**Interval:** Every 5 seconds for scale/endurance scenarios, 10 seconds for smoke/functional.

Collects:
- Docker stats: CPU%, RAM, network I/O per container
- ODBC pool: active connections vs max (`odbc show`)
- PJSIP endpoints: registration count (`pjsip show endpoints`)
- Container errors: ERROR/FATAL/WARNING from all container logs
- RTP stats: raw `pjsip show channelstats` output

This is the **context data** — explains WHY something failed, not WHETHER it failed.

### How each scenario uses both systems

| Validation | Source | Example |
|-----------|--------|---------|
| Channel drift < 2% | **SDK Sampler** | `server.Channels` vs AMI |
| Queue state drift < 2% | **SDK Sampler** | `server.Queues` vs AMI |
| Agent state drift < 2% | **SDK Sampler** | `server.Agents` vs AMI |
| Session accuracy >= 98% | **SDK Sampler** | `CallSession` vs CDR (post-test) |
| Reconnect < 10s | **SDK Sampler** | Connection state timestamp |
| CPU < 30% | **Audit Monitor** | Docker stats for SDK process |
| Memory < 1.5x | **Audit Monitor** | Docker stats trending |
| ODBC pool < 70% | **Audit Monitor** | `odbc show` active/max |
| Error correlation | **Audit Monitor** | Container logs |
| Answer rate >= 95% | **Metrics Collector** | Existing call generation metrics |

### Why two systems instead of one

- **Precision:** SDK sampler uses AMI direct (~5ms). Audit uses `docker exec` (~200ms). At 300 concurrent calls, 200ms of lag could mean 10 calls started/ended between measurement and report — unacceptable for 2% drift validation.
- **No duplication:** SDK sampler measures logical state (channels, queues, agents, sessions). Audit measures physical infrastructure (CPU, RAM, ODBC, network, errors). Different data, different purpose.
- **Independence:** If audit crashes, SDK validations still pass/fail correctly. If SDK sampler has a bug, audit data helps diagnose it.

### Audit interval by level

| Level | Audit Interval | SDK Sample Interval | Rationale |
|-------|---------------|-------------------|-----------|
| Smoke | 10s | 3s | Low load, context is secondary |
| Functional | 10s | 3s | Moderate load, standard monitoring |
| Scale | 5s | 3s | High load, need ODBC/CPU visibility |
| Endurance | 5s | 3s | 30 min sustained, need trending data |

## Infrastructure

### docker-compose.sdk-tests.yml

4 services (vs 5 in production compose):

| Service | Purpose | Required |
|---------|---------|----------|
| `postgres` | Realtime backend, CDR/CEL/QueueLog storage | Yes |
| `asterisk-realtime` | Target PBX for SDK connection | Yes |
| `pstn-emulator` | Call generation via AMI Originate | Yes |
| `pbx-admin` | Visual monitoring at localhost:8080 | No (optional observer) |

**No asterisk-file** — all SDK tests target realtime server only.

**PbxAdmin as passive observer:**
- Connects to same `asterisk-realtime` as the test
- If it crashes, the SDK test continues unaffected
- No `depends_on` from other services toward pbx-admin
- Accessible at `http://localhost:8080` during tests

**Same infrastructure tuning as production compose:**
- ODBC: max_connections = 100
- PostgreSQL: max_connections = 200
- Sorcery memory cache: 1000 objects, 900s TTL
- RTP range: 20000-21999 (2000 ports)
- ulimits nofile: 65535

### CLI Interface

Individual scenario execution (existing pattern):

```bash
dotnet run --project tests/PbxAdmin.LoadTests -- \
  --scenario sdk-smoke --agents 5 --duration 1 \
  --output tests/sdk-scenario-results/sdk-smoke.json
```

New `--level` flag to run an entire pyramid level:

```bash
--level smoke        # sdk-smoke only
--level functional   # smoke + sdk-state-sync + sdk-sessions + sdk-reconnect
--level scale        # smoke + functional + 4 scale scenarios
--level all          # smoke + functional + scale + sdk-endurance
```

Each level gates on the previous: if smoke fails, functional does not start.

### Output Format

Each scenario produces 3 files:

```
tests/sdk-scenario-results/
├── <scenario>.json              # Result + metrics + validations
├── <scenario>.json.audit.json   # Consolidated audit (Docker stats, Asterisk CLI)
└── <scenario>.json.audit.jsonl  # Streaming audit (per-snapshot)
```

Main `.json` structure:

```json
{
  "scenario": "sdk-scale-channels",
  "level": "scale",
  "startTime": "2026-03-29T10:00:00Z",
  "endTime": "2026-03-29T10:05:00Z",
  "config": {
    "agents": 200,
    "maxConcurrent": 300,
    "duration": 5
  },
  "metrics": {
    "callsOriginated": 850,
    "callsAnswered": 812,
    "answerRate": 0.955,
    "peakConcurrentCalls": 298
  },
  "validations": [
    { "name": "ChannelDriftAvg", "expected": "<2%", "actual": "1.3%", "passed": true },
    { "name": "NoPhantomChannels", "expected": 0, "actual": 0, "passed": true }
  ],
  "passed": true,
  "sdkBugs": []
}
```

## Scenarios Removed

The following 3 scenarios are deleted and replaced by this pyramid:

| Old Scenario | Replaced By |
|-------------|------------|
| `sdk-session-accuracy` | `sdk-smoke` (smoke), `sdk-sessions` (functional), `sdk-scale-sessions` (scale) |
| `sdk-live-drift` | `sdk-smoke` (smoke), `sdk-state-sync` (functional), `sdk-scale-channels/queues/agents` (scale) |
| `sdk-reconnect` | `sdk-reconnect` (functional, rewritten with 3 disconnects under load) |

## File Changes

### New Files
- `docker/docker-compose.sdk-tests.yml` — Lightweight 4-service compose
- `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkSmokeScenario.cs`
- `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkStateSyncScenario.cs`
- `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkSessionsScenario.cs`
- `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkReconnectScenario.cs` (rewrite)
- `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleChannelsScenario.cs`
- `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleQueuesScenario.cs`
- `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleAgentsScenario.cs`
- `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkScaleSessionsScenario.cs`
- `tests/PbxAdmin.LoadTests/Scenarios/Sdk/SdkEnduranceScenario.cs`

### Modified Files
- `tests/PbxAdmin.LoadTests/Scenarios/ScenarioRegistry.cs` — Register 9 new scenarios, remove 3 old
- `tests/PbxAdmin.LoadTests/Program.cs` — Add `--level` CLI flag
- `tests/PbxAdmin.LoadTests/Sdk/LiveStateValidator.cs` — Extend to validate Queues + Agents (not just Channels)
- `tests/PbxAdmin.LoadTests/Auditing/AuditMonitorService.cs` — Support configurable interval per level (5s for scale/endurance)
- `tests/PbxAdmin.LoadTests/Auditing/DockerContainerNames.cs` — Update container list for sdk-tests compose (remove demo-pbx-file)

### Deleted Files
- `tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkSessionAccuracyScenario.cs`
- `tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkLiveDriftScenario.cs`
- `tests/PbxAdmin.LoadTests/Scenarios/Functional/SdkReconnectScenario.cs`

## Dependencies

- **Asterisk.Sdk v1.5.1** — No SDK changes required; tests validate existing API surface
- **SIPSorcery 6.2.x** — Agent emulation (existing)
- **Npgsql 9.0.x + Dapper 2.1.x** — CDR/CEL/QueueLog reading (existing)
- **Docker** — Required for all levels
