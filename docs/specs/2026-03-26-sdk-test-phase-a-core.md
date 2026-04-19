# Phase A: Core SDK Library Validation

**Date:** 2026-03-26
**Status:** Draft
**Scope:** Asterisk.Sdk.Hosting + Asterisk.Sdk.Sessions + Asterisk.Sdk.Live
**Prerequisite:** Existing Docker stack (no infra changes)

---

## 1. Objective

Replace the manual `AmiConnection` construction in the load test platform with the same SDK infrastructure that PbxAdmin uses (`AddAsteriskMultiServer`, `ICallSessionManager`, `AsteriskServer`). This closes the gap between "testing Asterisk behavior" and "testing SDK correctness" by exercising the three SDK libraries under load and comparing their output against Asterisk's ground truth (CDR, CEL, AMI CLI commands).

The load test currently references only `Asterisk.Sdk.Ami` (raw AMI transport). After Phase A it will also depend on `Asterisk.Sdk.Hosting` and `Asterisk.Sdk.Sessions`, which together provide DI wiring, connection management, call session tracking, and live state.

**Non-goal:** This phase does not change the PSTN emulator, SIP agent emulator, Docker Compose stack, or existing scenario logic. It adds SDK infrastructure alongside the existing code and introduces three new validation-focused scenarios.

---

## 2. Current State Analysis

### What exists today

| Component | How it works | Gap |
|-----------|-------------|-----|
| `CallGeneratorService` | Creates `SdkAmiConnection` manually with `PipelineSocketConnectionFactory` and `NullLogger`. Connects to PSTN emulator only. | Bypasses `IAmiConnectionFactory`, no DI, no auto-reconnect, no health checks |
| `SdkEventCapture` | Subscribes to raw `ManagerEvent` via `IAmiConnection.OnEvent`. Manually correlates by CallerIDNum into `SdkSnapshot`. | Does not use `ICallSessionManager`. `StartCapturing()` is never called (documented bug). No connection to target PBX. |
| `SessionValidator` (Layer 3) | Compares `SdkSnapshot` fields vs `CdrRecord`. | Compares hand-built snapshot, not SDK `CallSession` objects. Cannot detect session state machine bugs. |
| `LeakDetector` (Layer 3) | Queries AMI `core show channels count` to detect channel leaks. | No live state comparison. Does not use `AsteriskServer.Channels`. |
| `TestContext` | Flat property bag with `CallGenerator`, `AgentPool`, `EventCapture`, readers, metrics. | No `ICallSessionManager`, no `AsteriskServer`, no `IAmiConnectionFactory`. |
| `Program.cs` DI | `Host.CreateApplicationBuilder` with manual singleton registrations. No SDK service registration. | Missing `AddAsteriskMultiServer()`, `AddAsteriskSessionsMultiServer()`. |

### What PbxAdmin does that the load test should mirror

PbxAdmin's `Program.cs` registers:
```csharp
builder.Services.AddAsteriskMultiServer();          // IAmiConnectionFactory
builder.Services.AddAsteriskSessionsMultiServer(opts => {
    opts.InboundContextPatterns = ["from-trunk", ...];
    opts.CompletedRetention = TimeSpan.FromMinutes(5);
    opts.MaxCompletedSessions = 500;
});
```

PbxAdmin's `AsteriskMonitorService` then:
1. Calls `_factory.CreateAndConnectAsync(options, ct)` to get `IAmiConnection`
2. Creates `new AsteriskServer(connection, logger)` for live state
3. Calls `server.StartAsync(ct)` to begin event processing
4. Calls `_sessionManager.AttachToServer(server, serverId)` to enable session tracking
5. Subscribes an `IObserver<ManagerEvent>` for event logging

The load test should follow the same pattern, connecting to the **target PBX** (not just the PSTN emulator).

---

## 3. Package Changes

### PbxAdmin.LoadTests.csproj

Add two new NuGet references:

```xml
<PackageReference Include="Asterisk.Sdk.Hosting" Version="1.5.1" />
<PackageReference Include="Asterisk.Sdk.Sessions" Version="1.5.1" />
```

The existing `Asterisk.Sdk.Ami 1.5.1` reference remains. The Hosting and Sessions packages will pull in `Asterisk.Sdk.Live` transitively (it contains `AsteriskServer`).

---

## 4. File Structure

All new files go under `tests/PbxAdmin.LoadTests/`:

```
Sdk/
  SdkHostSetup.cs              -- DI registration for Hosting+Sessions+Live
  SessionCaptureService.cs     -- ICallSessionManager wrapper for validation
  LiveStateValidator.cs        -- Periodic AsteriskServer state comparison
Scenarios/Functional/
  SdkReconnectScenario.cs      -- Tests Hosting auto-reconnect
  SdkSessionAccuracyScenario.cs -- Tests Sessions state machine accuracy
  SdkLiveDriftScenario.cs      -- Tests Live state drift under load
Validation/Layer1/
  SessionValidator.cs          -- UPDATE existing file: add CallSession-based checks
```

Modified files:
- `Program.cs` -- Replace manual DI with SDK registration, wire `SdkHostSetup`
- `PbxAdmin.LoadTests.csproj` -- Add package references
- `Scenarios/TestContext.cs` -- Add new properties for SDK services
- `Scenarios/ScenarioRegistry.cs` -- Register 3 new scenarios
- `appsettings.json` -- Add `Asterisk:Servers` section for SDK configuration

---

## 5. Detailed Design

### 5.1 SdkHostSetup — DI Registration

**File:** `Sdk/SdkHostSetup.cs`
**Responsibility:** Register SDK services in the DI container and manage startup/shutdown lifecycle.

```
public static class SdkHostSetup
  static void ConfigureServices(IServiceCollection, IConfiguration)
  static async Task<SdkRuntime> StartAsync(IServiceProvider, CancellationToken)
  static async Task StopAsync(SdkRuntime)
```

**ConfigureServices** registers:
- `AddAsteriskMultiServer()` -- provides `IAmiConnectionFactory`
- `AddAsteriskSessionsMultiServer(opts => ...)` with:
  - `InboundContextPatterns = ["from-trunk", "from-pstn"]`
  - `CompletedRetention = TimeSpan.FromMinutes(10)` (longer than PbxAdmin because load tests run longer)
  - `MaxCompletedSessions = 5000` (higher for load tests)
- `SessionCaptureService` as singleton
- `LiveStateValidator` as singleton

**StartAsync** performs the connection sequence (mirroring `AsteriskMonitorService`):
1. Resolve `IAmiConnectionFactory` from DI
2. Read `LoadTestOptions.TargetPbxAmi` for host/port/credentials
3. Build `AmiConnectionOptions` with `AutoReconnect = true`
4. Call `factory.CreateAndConnectAsync(options, ct)` to get the target PBX `IAmiConnection`
5. Create `new AsteriskServer(connection, logger)` and call `server.StartAsync(ct)`
6. Resolve `ICallSessionManager` and call `sessionManager.AttachToServer(server, "target-pbx")`
7. Return `SdkRuntime` record containing the connection, server, and session manager references

**SdkRuntime** record:
```
sealed record SdkRuntime(
    IAmiConnection Connection,
    AsteriskServer Server,
    ICallSessionManager SessionManager)
  : IAsyncDisposable
```

**Configuration mapping:** The existing `LoadTestOptions.TargetPbxAmi` (host=localhost, port=5038, user=dashboard, pass=dashboard) maps directly to `AmiConnectionOptions`. No new config section needed for the connection -- we reuse the existing `LoadTest:TargetPbxAmi` values programmatically.

However, `AddAsteriskMultiServer()` reads from `Asterisk:Servers` config section. We have two options:
- **Option A:** Add an `Asterisk:Servers` section to `appsettings.json` (matches PbxAdmin pattern)
- **Option B:** Call `AddAsteriskMultiServer()` for factory registration only, then build `AmiConnectionOptions` manually from `LoadTestOptions.TargetPbxAmi`

**Decision: Option B.** The load test already has `TargetPbxAmi` config. We use `AddAsteriskMultiServer()` to register `IAmiConnectionFactory` in DI, but build the connection options manually from existing config. This avoids duplicating connection details across two config sections.

**PSTN emulator connection stays manual.** The `CallGeneratorService` connects to the PSTN emulator, not the target PBX. It continues using manual `SdkAmiConnection` construction because:
- The PSTN emulator is a different Asterisk instance with different credentials
- It only needs fire-and-forget Originate, not session tracking or live state
- Mixing it into the SDK infrastructure would complicate the test (two servers, one SDK)

### 5.2 SessionCaptureService — ICallSessionManager Wrapper

**File:** `Sdk/SessionCaptureService.cs`
**Responsibility:** Subscribe to `ICallSessionManager` session lifecycle events and collect `CallSession` snapshots for post-test validation.

```
public sealed class SessionCaptureService : IDisposable
  void Attach(ICallSessionManager sessionManager)
  IReadOnlyList<CallSessionSnapshot> GetCompletedSessions()
  CallSessionSnapshot? GetSessionByLinkedId(string linkedId)
  CallSessionSnapshot? GetSessionByCallerNumber(string callerNumber)
  int ActiveSessionCount { get; }
  int CompletedSessionCount { get; }
  long TotalSessionsProcessed { get; }
```

**CallSessionSnapshot** is a load-test-owned DTO that captures the SDK `CallSession` state at completion time:

```
sealed record CallSessionSnapshot
  string LinkedId
  string CallerNumber
  string Destination
  CallSessionState FinalState    -- Ringing, Answered, Bridged, Completed, Failed, TimedOut
  DateTime StartTime
  DateTime? AnswerTime
  DateTime? EndTime
  TimeSpan? Duration
  int EventCount
  List<CallSessionEventSnapshot> Events
```

**CallSessionEventSnapshot** captures each `CallSessionEvent`:
```
sealed record CallSessionEventSnapshot
  CallSessionEventType Type      -- Created, Dialing, Ringing, Connected, Hold, etc.
  DateTime Timestamp
  string? Channel
  string? Detail
```

**How it subscribes:**
The `ICallSessionManager` exposes session state changes. `SessionCaptureService.Attach()` subscribes to the session manager's session completed/failed callbacks and records each completed session's state into a thread-safe `ConcurrentDictionary<string, CallSessionSnapshot>` keyed by LinkedId.

**Why not reuse SdkEventCapture?**
`SdkEventCapture` processes raw `ManagerEvent` objects and builds hand-crafted `SdkSnapshot` records. `SessionCaptureService` processes the SDK's own `CallSession` objects -- the exact thing we want to validate. The two services run in parallel:
- `SdkEventCapture`: raw AMI events (Layer 1 truth from the wire)
- `SessionCaptureService`: SDK-processed sessions (Layer 1 truth from the SDK state machine)
- CDR/CEL/queue_log: Asterisk DB records (Layer 2 truth)

Layer 3 then compares `CallSessionSnapshot` vs `CdrRecord` to find SDK bugs.

### 5.3 LiveStateValidator — Periodic State Comparison

**File:** `Sdk/LiveStateValidator.cs`
**Responsibility:** Periodically compare `AsteriskServer` live state against AMI CLI command output to detect drift.

```
public sealed class LiveStateValidator : IAsyncDisposable
  Task StartAsync(AsteriskServer server, IAmiConnection connection, CancellationToken ct)
  Task StopAsync()
  IReadOnlyList<LiveStateSample> GetSamples()
  LiveStateSummary GetSummary()
```

**LiveStateSample** captures one point-in-time comparison:
```
sealed record LiveStateSample
  DateTime Timestamp
  int SdkChannelCount          -- from server.Channels.ChannelCount
  int AsteriskChannelCount     -- from AMI "core show channels count"
  int SdkQueueCallerCount      -- from server.Queues sum of callers
  int AsteriskQueueCallerCount -- from AMI "queue show" parsed
  int Drift                    -- abs(SDK - Asterisk) for channels
  bool WithinTolerance         -- Drift <= 2
```

**LiveStateSummary** aggregates all samples:
```
sealed record LiveStateSummary
  int TotalSamples
  int SamplesWithinTolerance
  int MaxDrift
  double AverageDrift
  double DriftRate              -- percentage of samples outside tolerance
  bool Passed                   -- DriftRate < 5%
```

**Sampling strategy:**
- `StartAsync` spawns a background `Task` that runs a sampling loop
- Default interval: every 5 seconds
- Each sample queries both SDK live state and AMI CLI in parallel
- Samples are stored in a `ConcurrentBag<LiveStateSample>`
- `StopAsync` cancels the background loop

**AMI queries for ground truth:**
| SDK property | AMI command | Parse strategy |
|---|---|---|
| `server.Channels.ChannelCount` | `core show channels count` | Parse first integer from first line |
| `server.Queues.Queues` (count callers) | `queue show` | Parse "callers" count per queue line |

**Channel count discrepancy note:** The SDK's `Channels.ChannelCount` may differ from `core show channels count` by 1-2 channels due to race conditions (a channel created/destroyed between the two queries). The tolerance of +/-2 accounts for this.

### 5.4 TestContext Updates

Add three new optional properties to `TestContext`:

```csharp
// SDK infrastructure (null when running without SDK validation)
public SdkRuntime? SdkRuntime { get; set; }
public SessionCaptureService? SessionCapture { get; set; }
public LiveStateValidator? LiveStateValidator { get; set; }
```

These are nullable because existing scenarios should continue to work without SDK wiring (backward compatibility). The three new SDK scenarios require them.

### 5.5 Program.cs Changes

The `BuildHost()` method gains SDK service registration:

```csharp
// Existing manual registrations stay (CallGeneratorService, AgentPoolService, etc.)
// Add SDK infrastructure
SdkHostSetup.ConfigureServices(builder.Services, builder.Configuration);
```

The `BuildTestContext()` method gains SDK runtime initialization:

```csharp
// After building TestContext, start SDK infrastructure
var sdkRuntime = await SdkHostSetup.StartAsync(host.Services, ct);
context.SdkRuntime = sdkRuntime;

var sessionCapture = host.Services.GetRequiredService<SessionCaptureService>();
sessionCapture.Attach(sdkRuntime.SessionManager);
context.SessionCapture = sessionCapture;

var liveValidator = host.Services.GetRequiredService<LiveStateValidator>();
context.LiveStateValidator = liveValidator;
```

The `finally` block adds SDK cleanup:

```csharp
if (context.SdkRuntime is not null)
    await SdkHostSetup.StopAsync(context.SdkRuntime);
```

**Ordering matters:** SDK runtime must start **before** agents register and calls begin, so the `ICallSessionManager` sees all events from the first call.

### 5.6 Updated SessionValidator (Layer 3)

**File:** `Validation/Layer3/SessionValidator.cs` (modify existing)

Add a new overload that validates `CallSessionSnapshot` against `CdrRecord`:

```
static ValidationResult ValidateSession(CallSessionSnapshot session, CdrRecord? cdr)
```

This method performs the same 7 checks as the existing `ValidateCall(SdkSnapshot, CdrRecord?)` but reads from `CallSessionSnapshot` fields instead of hand-built `SdkSnapshot` fields.

Additional checks unique to `CallSession` validation:

**Check 8: State machine consistency**
- If CDR disposition is `ANSWERED`, session `FinalState` must be `Completed` (not `Failed` or `TimedOut`)
- If CDR disposition is `NO ANSWER`, session `FinalState` must be `TimedOut` or `Failed` (not `Completed`)
- If CDR disposition is `BUSY`, session `FinalState` must be `Failed` with busy detail

**Check 9: Event sequence validity**
- A `Completed` session must have at least: `Created` -> `Ringing` or `Connected` -> `Completed`
- A session with `Connected` event must have had `Ringing` or `Dialing` before it
- No event timestamp should precede the previous event's timestamp

**Check 10: Duration accuracy**
- `CallSessionSnapshot.Duration` vs `CdrRecord.BillSec` within 2-second tolerance
- If the session has `AnswerTime` and `EndTime`, verify `Duration == EndTime - AnswerTime`

The existing `ValidateCall(SdkSnapshot, CdrRecord?)` method remains unchanged for backward compatibility.

---

## 6. New Scenarios

### 6.1 SdkReconnectScenario

**File:** `Scenarios/Functional/SdkReconnectScenario.cs`
**CLI name:** `sdk-reconnect`
**Duration:** ~2 minutes

**Purpose:** Verify `Asterisk.Sdk.Hosting` auto-reconnect works correctly under the same conditions PbxAdmin would face (AMI connection drop during active monitoring).

**Execution flow:**
1. Verify `SdkRuntime` is connected and `AsteriskServer` is processing events
2. Generate 2 inbound calls, verify they appear in `ICallSessionManager`
3. Force-close the AMI TCP connection via `AsteriskServer` connection kill:
   - Send AMI `Command("manager reload")` which drops all AMI connections
   - Alternative: `Command("module reload manager")` to force AMI restart
4. Wait 3 seconds for the SDK to detect the drop
5. Wait up to 10 seconds for auto-reconnect (poll `connection.IsConnected` every 500ms)
6. Generate 2 more inbound calls after reconnection
7. Verify the post-reconnect calls appear in `ICallSessionManager`

**Validation checks:**
| Check | Expected | Failure indicates |
|-------|----------|-------------------|
| `ReconnectDetected` | `connection.IsConnected == true` within 10s | SDK auto-reconnect broken |
| `PreDisconnectSessionsExist` | First 2 calls have `CallSessionSnapshot` records | Session manager lost state |
| `PostReconnectSessionsExist` | Last 2 calls have `CallSessionSnapshot` records | Session manager not reattached after reconnect |
| `ReconnectTimeMs` | < 5000ms | SDK reconnect too slow |
| `NoOrphanedSessions` | All sessions reached terminal state | Sessions stuck in non-terminal state |

**Risk:** The AMI reload command may not reliably disconnect the SDK's connection. If `manager reload` does not work, the alternative is to have the Docker host execute `docker exec demo-pbx-realtime asterisk -rx "manager reload"`. This requires the test to have Docker socket access, which is not available in the current setup. If neither approach works, this scenario falls back to a **documentation-only** test that verifies reconnect configuration is set correctly without actually triggering a disconnect.

### 6.2 SdkSessionAccuracyScenario

**File:** `Scenarios/Functional/SdkSessionAccuracyScenario.cs`
**CLI name:** `sdk-session-accuracy`
**Duration:** ~3 minutes

**Purpose:** Verify `Asterisk.Sdk.Sessions` `ICallSessionManager` accurately tracks call lifecycle by comparing `CallSession` state against CDR records for a batch of calls with known outcomes.

**Execution flow:**
1. Generate a controlled batch of calls with predictable outcomes:
   - 5 calls to extension 105 (loadtest queue, agents answer -> `ANSWERED`)
   - 3 calls to extension 105 with all agents paused (no one answers -> `NO ANSWER`)
   - 2 calls to a non-existent extension 999 (-> `FAILED`)
2. Space calls 2 seconds apart to avoid AMI event interleaving issues
3. Wait 60 seconds for all calls to complete (ring timeout + talk time + hangup)
4. Wait 3 seconds for CDR/CEL flush to PostgreSQL
5. Collect `SessionCaptureService.GetCompletedSessions()` and CDR records
6. Run `SessionValidator.ValidateSession()` for each pair

**Validation checks (per call):**
| Check | Expected | Failure indicates |
|-------|----------|-------------------|
| `SessionExists` | `CallSessionSnapshot` present for each call | Session manager missed a call |
| `StateMatchesDisposition` | `FinalState` aligns with CDR disposition | State machine bug |
| `DurationWithinTolerance` | `Duration` within 2s of CDR `billsec` | Timing bug |
| `EventSequenceValid` | Events in chronological order, required transitions present | Event processing bug |
| `CallerNumberMatch` | Snapshot caller matches CDR `src` | Correlation bug |
| `LinkedIdMatch` | Snapshot LinkedId matches CDR `linkedid` | Correlation bug |

**Aggregate checks:**
| Check | Expected | Failure indicates |
|-------|----------|-------------------|
| `AllSessionsTracked` | 10/10 calls have sessions | Session creation bug |
| `NoStuckSessions` | 0 sessions in non-terminal state after 60s | State machine leak |
| `MemoryBaseline` | `GC.GetTotalMemory()` delta < 10MB for 10 calls | Memory leak in session tracking |

**Agent pause/unpause:** Before generating the 3 `NO ANSWER` calls, pause all queue members via AMI `QueuePause` action. Unpause after those calls complete. This guarantees the timeout outcome.

### 6.3 SdkLiveDriftScenario

**File:** `Scenarios/Functional/SdkLiveDriftScenario.cs`
**CLI name:** `sdk-live-drift`
**Duration:** ~3 minutes

**Purpose:** Verify `Asterisk.Sdk.Live` (`AsteriskServer.Channels`, `.Queues`) accurately reflects Asterisk's real state during sustained call activity.

**Execution flow:**
1. Start `LiveStateValidator` with 3-second sampling interval (faster than default for this test)
2. Generate a sustained burst: 5 concurrent calls to extension 105 every 10 seconds for 2 minutes
3. After 2 minutes, stop generating calls
4. Wait 30 seconds for all calls to drain
5. Stop `LiveStateValidator`
6. Analyze collected samples

**Validation checks:**
| Check | Expected | Failure indicates |
|-------|----------|-------------------|
| `SufficientSamples` | >= 30 samples collected (2min / 3s interval + drain) | Validator not running |
| `ChannelDriftRate` | < 5% of samples have drift > 2 | SDK channel tracking broken |
| `MaxChannelDrift` | <= 4 channels | Severe tracking bug |
| `QueueCallerDriftRate` | < 10% of samples have queue caller drift > 1 | SDK queue tracking broken |
| `PeakChannelCount` | > 0 at some point during test | SDK not seeing any channels |
| `DrainToZero` | Last 3 samples show 0 channels (both SDK and Asterisk) | Channel leak in SDK or Asterisk |

**Why queue tolerance is looser (10% vs 5%):** Queue membership changes rapidly as agents pick up calls. The `queue show` AMI command and `server.Queues` may sample at slightly different moments, causing more legitimate discrepancies than channel counts.

---

## 7. appsettings.json Changes

No changes needed. The existing `LoadTest:TargetPbxAmi` section already contains the target PBX AMI credentials that `SdkHostSetup` will use programmatically:

```json
"TargetPbxAmi": {
  "Host": "localhost",
  "Port": 5038,
  "Username": "dashboard",
  "Password": "dashboard"
}
```

---

## 8. ScenarioRegistry Updates

Add three new entries:

```csharp
["sdk-reconnect"] = new SdkReconnectScenario(),
["sdk-session-accuracy"] = new SdkSessionAccuracyScenario(),
["sdk-live-drift"] = new SdkLiveDriftScenario(),
```

---

## 9. Success Criteria

### Must-have (Phase A is not complete without these)

1. **SDK DI wiring works.** `AddAsteriskMultiServer()` and `AddAsteriskSessionsMultiServer()` register correctly in the load test host. `IAmiConnectionFactory.CreateAndConnectAsync()` connects to the target PBX. Verified by any scenario running without DI resolution errors.

2. **ICallSessionManager processes events.** Every call generated by any scenario produces a `CallSessionSnapshot` in `SessionCaptureService`. Verified by `sdk-session-accuracy` scenario: 10/10 calls tracked.

3. **Session state matches CDR.** `CallSession.FinalState` aligns with CDR `disposition` for at least 95% of calls. Verified by `SessionValidator.ValidateSession()` checks in `sdk-session-accuracy`.

4. **Live state drift < 5%.** `AsteriskServer.Channels.ChannelCount` matches AMI `core show channels count` within +/-2 for at least 95% of samples. Verified by `sdk-live-drift` scenario.

5. **No session memory leak.** After 100+ calls complete, `SessionCaptureService.CompletedSessionCount` matches expected count and `GC.GetTotalMemory()` delta is reasonable (< 50MB). Verified by `sdk-session-accuracy`.

6. **Backward compatible.** All 19 existing scenarios continue to work unchanged. The new SDK properties on `TestContext` are nullable and existing scenarios ignore them.

### Nice-to-have

7. **Auto-reconnect works.** SDK reconnects within 5 seconds after AMI connection drop. Verified by `sdk-reconnect` scenario. (May not be testable without Docker socket access.)

8. **Agent state tracking.** `AsteriskServer.Agents` reflects correct agent states. (Dependent on SDK exposing agent tracking for the target PBX's queue configuration.)

---

## 10. Execution Order

This phase should be implemented in 4 tasks, each via a fresh subagent:

### Task 1: Package + DI foundation
- Add `Asterisk.Sdk.Hosting` and `Asterisk.Sdk.Sessions` to csproj
- Create `Sdk/SdkHostSetup.cs`
- Create `SdkRuntime` record
- Update `Program.cs` to call `SdkHostSetup.ConfigureServices()` and `StartAsync()`
- Update `TestContext` with nullable SDK properties
- Build passes, existing smoke test still works

### Task 2: Session capture
- Create `Sdk/SessionCaptureService.cs` with `CallSessionSnapshot` and `CallSessionEventSnapshot`
- Wire into `Program.cs` (attach after `SdkRuntime.StartAsync`)
- Update `SessionValidator.cs` with `ValidateSession(CallSessionSnapshot, CdrRecord?)` overload
- Create `Scenarios/Functional/SdkSessionAccuracyScenario.cs`
- Register in `ScenarioRegistry`
- Build passes

### Task 3: Live state validation
- Create `Sdk/LiveStateValidator.cs` with `LiveStateSample` and `LiveStateSummary`
- Create `Scenarios/Functional/SdkLiveDriftScenario.cs`
- Register in `ScenarioRegistry`
- Build passes

### Task 4: Reconnect scenario
- Create `Scenarios/Functional/SdkReconnectScenario.cs`
- Register in `ScenarioRegistry`
- Build passes
- If AMI disconnect is not testable, implement as config-only validation (verify `AutoReconnect = true` is set, verify `ConnectionLost` event handler is wired)

---

## 11. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| `AddAsteriskMultiServer()` requires config section we don't have | Build fails | Use Option B: register factory via DI but build connection options manually |
| `ICallSessionManager.AttachToServer()` requires specific event subscriptions we miss | Sessions never populated | Study PbxAdmin's `AsteriskMonitorService` pattern exactly; include `server.StartAsync()` before `AttachToServer()` |
| `AsteriskServer` live state not populated for queues without explicit subscription | Queue drift always 100% | Verify `server.StartAsync()` initializes queue polling; may need `QueueStatusAction` on connect |
| AMI `manager reload` does not disconnect our connection | `sdk-reconnect` untestable | Fall back to config validation only; document as known limitation |
| `CallSession` objects don't expose enough fields for comparison | Checks become trivial | Review SDK source to identify available properties before implementation |
| Two AMI connections to same PBX (existing `SdkEventCapture` + new SDK) cause event duplication | Double-counted events | `SdkEventCapture` connects to target PBX for raw events; SDK also connects. Each has its own subscription. No conflict -- they are independent connections. |

---

## 12. Dependency Map

```
PbxAdmin.LoadTests.csproj
  Asterisk.Sdk.Ami 1.5.1        (existing)
  Asterisk.Sdk.Hosting 1.5.1    (NEW)
  Asterisk.Sdk.Sessions 1.5.1   (NEW)
    -> Asterisk.Sdk.Live         (transitive)

Program.cs
  -> SdkHostSetup.ConfigureServices()  (NEW)
  -> SdkHostSetup.StartAsync()         (NEW)

TestContext
  -> SdkRuntime?                  (NEW, nullable)
  -> SessionCaptureService?       (NEW, nullable)
  -> LiveStateValidator?          (NEW, nullable)

ScenarioRegistry
  -> SdkReconnectScenario         (NEW)
  -> SdkSessionAccuracyScenario   (NEW)
  -> SdkLiveDriftScenario         (NEW)

SessionValidator
  -> ValidateSession() overload   (NEW, existing method unchanged)
```

---

## 13. Testing Strategy

Unit tests for the new code go in `tests/PbxAdmin.Tests/`:

| Test class | What it tests | Mock strategy |
|-----------|--------------|---------------|
| `SessionCaptureServiceTests` | Snapshot creation, thread safety, lookup by LinkedId/CallerNumber | Mock `ICallSessionManager` with fake session completed callbacks |
| `LiveStateSummaryTests` | `LiveStateSummary` aggregation math (drift rate, average, pass/fail threshold) | Pure calculation, no mocks |
| `SessionValidatorTests` | New `ValidateSession()` overload checks (state/disposition mapping, duration tolerance, event sequence) | Construct `CallSessionSnapshot` and `CdrRecord` manually |
| `SdkHostSetupTests` | `ConfigureServices` registers expected types in DI container | Real `ServiceCollection`, verify resolution |

Integration testing is done by running the three new scenarios against the Docker stack:

```bash
# Smoke: verify SDK wiring works
dotnet run --project tests/PbxAdmin.LoadTests/ -- --scenario sdk-session-accuracy --duration 3

# Live state under load
dotnet run --project tests/PbxAdmin.LoadTests/ -- --scenario sdk-live-drift --duration 3

# Reconnect (may be limited)
dotnet run --project tests/PbxAdmin.LoadTests/ -- --scenario sdk-reconnect --duration 2
```
