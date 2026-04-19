# SDK Test Platform - Phase B: Protocol Libraries

**Date:** 2026-03-26
**Status:** Draft
**Depends on:** Phase A complete (19 scenarios, smoke test passing)

---

## Goal

Test three SDK libraries that provide alternative Asterisk interfaces beyond AMI: ARI (REST + WebSocket), Config (file parser/generator), and AGI (FastAGI server). These libraries represent different protocol families that the SDK exposes, and validating them ensures the SDK correctly implements each protocol specification.

Phase B requires only minor Docker stack extensions. ARI is already configured and exposed. Config tests are mostly offline. Only AGI needs new infrastructure (a FastAGI server embedded in the load test app plus a dialplan extension on the PBX).

---

## Table of Contents

1. [Library 4: Asterisk.Sdk.Ari](#library-4-asterisksdk-ari)
2. [Library 5: Asterisk.Sdk.Config](#library-5-asterisksdk-config)
3. [Library 6: Asterisk.Sdk.Agi](#library-6-asterisksdk-agi)
4. [Docker Stack Changes](#docker-stack-changes)
5. [File Structure](#file-structure)
6. [TestContext Extensions](#testcontext-extensions)
7. [NuGet Dependencies](#nuget-dependencies)
8. [Implementation Order](#implementation-order)
9. [Success Criteria](#success-criteria)
10. [Risk Register](#risk-register)

---

## Library 4: Asterisk.Sdk.Ari

### Background

ARI (Asterisk REST Interface) is a REST + WebSocket API that gives external applications full control over channels, bridges, playback, recording, and device state. It is the modern alternative to AMI for call control. The SDK wraps this in `Asterisk.Sdk.Ari`.

### Current State

- ARI is enabled on both PBX instances (`ari.conf` with user `dashboard` / password `dashboard`).
- HTTP is bound on port 8088 (realtime PBX exposed as host 8088, file PBX as 8188).
- WSS is available on port 8089 (realtime) / 8190 (file).
- Neither PbxAdmin nor the load test platform currently uses ARI.

### Service: AriClientService

A wrapper around the SDK's ARI client that manages:
- WebSocket connection lifecycle (connect, reconnect, dispose).
- Stasis application registration (`loadtest` app name).
- REST calls to `/ari/channels`, `/ari/bridges`, `/ari/playbacks`.
- Event deserialization from the WebSocket stream into strongly-typed objects.

```
Location: tests/PbxAdmin.LoadTests/Sdk/AriClientService.cs
```

**Responsibilities:**

| Method | Description |
|--------|-------------|
| `ConnectAsync(baseUrl, username, password, appName, ct)` | Open WebSocket, subscribe to Stasis events |
| `CreateChannelAsync(endpoint, context, ct)` | POST /ari/channels — originate a channel into Stasis |
| `CreateBridgeAsync(bridgeType, ct)` | POST /ari/bridges — create a mixing bridge |
| `AddChannelToBridgeAsync(bridgeId, channelId, ct)` | POST /ari/bridges/{id}/addChannel |
| `PlaybackAsync(channelId, mediaUri, ct)` | POST /ari/channels/{id}/play — play audio |
| `HangupChannelAsync(channelId, ct)` | DELETE /ari/channels/{id} |
| `GetEventsAsync(ct)` | Yield ARI events from the WebSocket as `IAsyncEnumerable<AriEvent>` |
| `DisconnectAsync()` | Close WebSocket gracefully |

**Reconnection logic:** If the WebSocket drops, wait 2 seconds and reconnect automatically up to 5 times. Log each reconnection attempt. After 5 failures, throw `AriConnectionException` and let the scenario handle it.

### Scenario: AriChannelScenario

```
CLI name: ari-channel
Location: tests/PbxAdmin.LoadTests/Scenarios/Functional/AriChannelScenario.cs
```

**What it tests:** Creating and managing channels via ARI REST, receiving lifecycle events via WebSocket.

**Steps:**

1. Connect ARI WebSocket to realtime PBX (`ws://asterisk-realtime:8088/ari/events?app=loadtest`).
2. Create 5 outbound channels sequentially via `POST /ari/channels`:
   - Endpoint: `PJSIP/2100` (a registered load test agent).
   - The channel enters the `loadtest` Stasis application.
3. For each channel, verify these ARI WebSocket events arrive in order:
   - `StasisStart` (channel entered the app)
   - `ChannelStateChange` to `Up` (after Answer)
4. Play audio on each channel: `POST /ari/channels/{id}/play?media=sound:hello-world`.
5. Verify `PlaybackStarted` and `PlaybackFinished` events.
6. Hangup each channel via `DELETE /ari/channels/{id}`.
7. Verify `StasisEnd` events.

**Validation:**
- All 5 channels created successfully (HTTP 200).
- All expected ARI events received within 30-second timeout.
- No orphan channels left after test (query `GET /ari/channels` returns 0 for our app).

### Scenario: AriBridgeScenario

```
CLI name: ari-bridge
Location: tests/PbxAdmin.LoadTests/Scenarios/Functional/AriBridgeScenario.cs
```

**What it tests:** Bridge creation, adding/removing channels, and bridge event correlation.

**Steps:**

1. Connect ARI WebSocket.
2. Create 2 outbound channels into Stasis (endpoints `PJSIP/2100` and `PJSIP/2101`).
3. Wait for both `StasisStart` events.
4. Create a mixing bridge: `POST /ari/bridges?type=mixing`.
5. Add both channels to the bridge: `POST /ari/bridges/{id}/addChannel?channel={ch1},{ch2}`.
6. Verify `ChannelEnteredBridge` events for both channels.
7. Wait 5 seconds (simulating a bridged call).
8. Remove one channel: `POST /ari/bridges/{id}/removeChannel?channel={ch1}`.
9. Verify `ChannelLeftBridge` event.
10. Destroy bridge: `DELETE /ari/bridges/{id}`.
11. Hangup remaining channel.

**Validation:**
- Bridge created with correct type.
- Both channels entered bridge (events received).
- Channel removal produced `ChannelLeftBridge`.
- Bridge destruction produced `BridgeDestroyed`.
- AMI events (via existing `SdkEventCapture`) match ARI events for the same channels — same channel IDs, same timestamps within 1-second tolerance.

### Scenario: AriStressScenario

```
CLI name: ari-stress
Location: tests/PbxAdmin.LoadTests/Scenarios/Load/AriStressScenario.cs
```

**What it tests:** ARI under concurrent load — 50 simultaneous channels.

**Steps:**

1. Connect ARI WebSocket.
2. Fire 50 `POST /ari/channels` requests in parallel (10 batches of 5, 500ms between batches).
3. Collect all `StasisStart` events into a `ConcurrentDictionary<channelId, timestamp>`.
4. After all 50 channels are in Stasis, hangup all 50 in parallel.
5. Collect all `StasisEnd` events.

**Validation:**
- All 50 channels created (HTTP 200, no 503 or timeout).
- All 50 `StasisStart` events received within 60 seconds.
- All 50 `StasisEnd` events received after hangup.
- WebSocket stayed connected throughout (no reconnection needed).
- No duplicate events (each channel ID appears exactly once in start/end).

### Scenario: AriReconnectScenario

```
CLI name: ari-reconnect
Location: tests/PbxAdmin.LoadTests/Scenarios/Chaos/AriReconnectScenario.cs
```

**What it tests:** ARI WebSocket reconnection resilience.

**Steps:**

1. Connect ARI WebSocket and create 1 channel in Stasis.
2. Force-close the WebSocket connection (simulate network drop).
3. Verify `AriClientService` reconnects automatically within 5 seconds.
4. After reconnection, create another channel — verify events still flow.
5. Hangup both channels.

**Validation:**
- Reconnection happened within 5 seconds.
- Second channel's events received on the new WebSocket.
- No events lost for the first channel (it should still be in Stasis after reconnect).

### Validator: AriEventValidator

```
Location: tests/PbxAdmin.LoadTests/Validation/AriEventValidator.cs
```

Compares ARI events against AMI events captured by `SdkEventCapture` for the same call.

| Check | Description |
|-------|-------------|
| `ChannelIdMatch` | ARI channel.id matches AMI Uniqueid for the same call |
| `StateTransitionMatch` | ARI ChannelStateChange events match AMI Newstate events |
| `TimestampAlignment` | ARI event timestamps within 1s of AMI event timestamps |
| `EventCompleteness` | Every AMI Newchannel has a corresponding ARI StasisStart (for Stasis-routed calls) |

---

## Library 5: Asterisk.Sdk.Config

### Background

`Asterisk.Sdk.Config` parses and generates Asterisk `.conf` files programmatically. It handles sections, key-value pairs, templates (`(!)`), objects (`;--` blocks), comments, `#include` directives, and multi-line values.

### Current State

- Not used anywhere in the project.
- PbxAdmin uses AMI `GetConfig`/`UpdateConfig` actions instead of direct file parsing.
- The Docker stack has 15+ `.conf` files available for parsing tests.

### Service: ConfigValidator

```
Location: tests/PbxAdmin.LoadTests/Sdk/ConfigValidator.cs
```

Not a long-lived service — a static utility class with parse/serialize/compare methods.

| Method | Description |
|--------|-------------|
| `ParseFile(path)` | Parse a .conf file into the SDK's config model |
| `SerializeToString(config)` | Serialize config model back to .conf format |
| `RoundTrip(path)` | Parse, serialize, re-parse, compare — returns diff if any |
| `CompareConfigs(a, b)` | Deep comparison of two parsed config objects |
| `ValidateAgainstAmi(parsedConfig, amiConfig)` | Compare parsed file against AMI GetConfig response |

### Scenario: ConfigRoundTripScenario

```
CLI name: config-roundtrip
Location: tests/PbxAdmin.LoadTests/Scenarios/Functional/ConfigRoundTripScenario.cs
```

**What it tests:** Lossless round-trip parsing and serialization of all Asterisk config file formats.

**Steps:**

1. Copy config files from the Docker containers to a temp directory via `docker cp` (or read them via AMI `GetConfig` and write to disk).
2. For each config file, perform a round-trip test:
   - Parse the file with `Asterisk.Sdk.Config`.
   - Serialize back to string.
   - Parse the serialized output again.
   - Compare the two parsed objects — they must be structurally identical.
3. Test these specific files (covering different .conf features):

| File | Features Tested |
|------|-----------------|
| `pjsip.conf` | Templates (`(!)`), object sections, multi-value keys |
| `extensions.conf` | `same =>` continuation, `exten =>` lines, context sections |
| `queues.conf` | Member directives, key-value options |
| `manager.conf` | Permit/deny lists, multi-line read/write |
| `ari.conf` | Simple key-value |
| `modules.conf` | `preload`, `load`, `noload` directives |
| `musiconhold.conf` | Directory paths, mode settings |
| `features.conf` | Feature map key combos |
| `res_odbc.conf` | DSN connection strings |
| `http.conf` | TLS cert/key paths |

4. Additionally, generate a synthetic `pjsip.conf` endpoint entry:
   - Create an endpoint + auth + AOR programmatically using the SDK Config API.
   - Serialize to string.
   - Apply via AMI `UpdateConfig` to a running PBX.
   - Query Asterisk via AMI `PJSIPShowEndpoint` to verify it loaded.

**Validation:**
- Round-trip produces structurally identical output for all 10 files.
- Comments are preserved in round-trip (or explicitly documented as not preserved).
- Synthetic endpoint appears in Asterisk after `UpdateConfig` + `dialplan reload`.
- No data loss: section count, key count, and value content match before and after.

### Edge Cases to Cover

| Case | Input | Expected |
|------|-------|----------|
| Blank lines between sections | `[a]\nk=v\n\n[b]\nk=v` | Both sections parsed, blank lines ignored |
| Inline comments | `key = value ; comment` | Value is `value`, comment preserved or stripped (document which) |
| Template inheritance | `[endpoint](!)` then `[ep1](endpoint)` | `ep1` inherits from `endpoint` template |
| Multi-line values | `key = line1\n line2` (leading whitespace = continuation) | Value is `line1\nline2` |
| `#include` directives | `#include "other.conf"` | Directive preserved as-is (not followed, since file may not exist) |
| `switch =>` in extensions | `switch => Realtime/@` | Parsed as a special directive, not a key-value |
| Empty sections | `[empty]\n[next]` | Empty section exists with 0 keys |
| Duplicate keys | `key = val1\nkey = val2` | Both preserved (Asterisk allows duplicates) |

---

## Library 6: Asterisk.Sdk.Agi

### Background

AGI (Asterisk Gateway Interface) allows external programs to control call flow. FastAGI is the TCP-based variant: Asterisk connects to an external server via `AGI(agi://host:port/script)` and sends/receives commands over the socket. The SDK provides a FastAGI server that handles multiple concurrent sessions.

### Current State

- No AGI server exists in the stack.
- No AGI dialplan extensions configured.
- The load test app is a console application that can host additional TCP listeners.

### Service: AgiServerService

```
Location: tests/PbxAdmin.LoadTests/Sdk/AgiServerService.cs
```

A hosted service (`IHostedService`) that runs a FastAGI server inside the load test process.

**Configuration:**

| Setting | Value | Notes |
|---------|-------|-------|
| Listen address | `0.0.0.0` | Accept connections from Docker network |
| Listen port | `4573` | Standard FastAGI port |
| Max concurrent sessions | `100` | Bounded to prevent resource exhaustion |
| Session timeout | `60s` | Auto-close sessions that exceed timeout |

**Script handlers:** The server dispatches incoming AGI sessions by script name (the path in `agi://host:port/script`).

| Script | Behavior | Purpose |
|--------|----------|---------|
| `/loadtest` | Answer, play hello-world, collect 4 DTMF digits via GetData, set channel variable `AGI_RESULT=<digits>`, hangup | Tests core AGI command flow |
| `/echo` | Answer, read `AGI_ARG_1` variable, set `AGI_ECHO=<value>`, hangup | Tests variable passing |
| `/slow` | Answer, sleep 10 seconds, hangup | Tests long-running sessions |
| `/error` | Return AGI failure code immediately | Tests error handling |

**Session tracking:** Each AGI session is tracked in a `ConcurrentDictionary<string, AgiSessionInfo>` keyed by Asterisk channel name. `AgiSessionInfo` records:
- Channel name
- Script name
- Start/end timestamps
- Commands executed (list of command/response pairs)
- Final disposition (completed, timeout, error)

**Lifecycle:**
1. `StartAsync`: bind TCP listener on port 4573, begin accepting connections.
2. For each connection: read AGI environment variables (channel, callerid, context, extension, etc.), dispatch to script handler, execute commands, close.
3. `StopAsync`: stop accepting, drain active sessions (5-second grace), force-close remaining.

### Docker Stack Changes for AGI

The realtime PBX needs a new dialplan extension that routes to the FastAGI server. The load test app needs its FastAGI port reachable from the PBX container.

**Option A — Host networking for load test (preferred for development):**
The load test runs on the host. The PBX container reaches it via `host.docker.internal` (Docker Desktop) or the host's Docker bridge IP (`172.17.0.1` on Linux).

**Option B — Load test as a Docker service:**
If the load test ever runs in a container, expose port 4573 and use the service name.

For this spec, we use Option A. The dialplan references `host.docker.internal` with a fallback environment variable `${AGI_HOST}` for Linux hosts where `host.docker.internal` is not available.

**New dialplan extensions (added to `[default]` context in realtime PBX `extensions.conf`):**

```ini
; FastAGI load test extensions
exten => 110,1,AGI(agi://${AGI_HOST}:4573/loadtest)
same => n,Hangup()

exten => 111,1,AGI(agi://${AGI_HOST}:4573/echo,${ARG1})
same => n,Hangup()

exten => 112,1,AGI(agi://${AGI_HOST}:4573/slow)
same => n,Hangup()

exten => 113,1,AGI(agi://${AGI_HOST}:4573/error)
same => n,Hangup()
```

The `AGI_HOST` variable is set in `[globals]`:

```ini
[globals]
AGI_HOST = 172.17.0.1
```

This can be overridden via an environment variable in `docker-compose.pbxadmin.yml` or via AMI `UpdateConfig` at test startup.

### Scenario: AgiLoadScenario

```
CLI name: agi-load
Location: tests/PbxAdmin.LoadTests/Scenarios/Functional/AgiLoadScenario.cs
```

**What it tests:** FastAGI server handling concurrent AGI sessions with correct command execution.

**Steps:**

1. Ensure `AgiServerService` is running and listening on port 4573.
2. Generate 20 concurrent inbound calls to extension 110 (the `/loadtest` script) via the PSTN emulator AMI Originate.
3. For each call, the PSTN emulator dials into the realtime PBX, which connects to the FastAGI server.
4. The `/loadtest` script handler:
   - Sends `ANSWER` command, expects `200 result=0`.
   - Sends `GET DATA hello-world 5000 4`, waits for DTMF (will timeout since PSTN emulator sends none).
   - Sends `SET VARIABLE AGI_RESULT timeout`.
   - Sends `HANGUP`.
5. Wait for all 20 sessions to complete (30-second timeout).

**Validation:**
- All 20 AGI sessions started (20 entries in session tracker).
- All 20 sessions completed without error.
- Each session executed the expected command sequence: ANSWER, GET DATA, SET VARIABLE, HANGUP.
- No sessions leaked (tracker empty after test).
- AMI events show 20 calls to extension 110 with `AGI` application.

### Scenario: AgiCommandScenario

```
CLI name: agi-commands
Location: tests/PbxAdmin.LoadTests/Scenarios/Functional/AgiCommandScenario.cs
```

**What it tests:** Individual AGI command correctness.

**Steps:**

1. Call extension 111 with argument "test-value-123" (the `/echo` script).
   - Script reads `AGI_ARG_1`, sets `AGI_ECHO=test-value-123`.
   - After hangup, verify the channel variable was set via AMI `GetVar` (or verify in CEL `CHAN_SET` event).

2. Call extension 112 (the `/slow` script).
   - Script takes 10 seconds to complete.
   - Verify the AGI session stayed open for ~10 seconds.
   - Verify no timeout error (session timeout is 60s).

3. Call extension 113 (the `/error` script).
   - Script returns failure immediately.
   - Verify the AGI session ended with error disposition.
   - Verify Asterisk continued dialplan execution after AGI failure (or hung up, depending on config).

**Validation:**
- Echo: channel variable `AGI_ECHO` equals `test-value-123`.
- Slow: session duration between 9 and 12 seconds.
- Error: session disposition is `Error`, Asterisk handled gracefully (no crash, no hung channel).

### Validator: AgiSessionValidator

```
Location: tests/PbxAdmin.LoadTests/Validation/AgiSessionValidator.cs
```

| Check | Description |
|-------|-------------|
| `SessionCompleteness` | All expected AGI sessions started and finished |
| `CommandSequence` | Commands executed in correct order per script |
| `ResponseCorrectness` | AGI response codes match expected values (200=success, 510=command not found) |
| `SessionCleanup` | No sessions remain in tracker after test completes |
| `VariableMatch` | AGI-set variables match AMI/CEL data for the same channel |
| `ConcurrencyLimit` | Peak concurrent sessions never exceeded configured max (100) |

---

## Docker Stack Changes

### Summary of Changes

| Change | Service | File | Impact |
|--------|---------|------|--------|
| Add AGI extensions to dialplan | `asterisk-realtime` | `extensions.conf` | 4 new extensions (110-113) |
| Add `AGI_HOST` global variable | `asterisk-realtime` | `extensions.conf` | 1 line in `[globals]` |
| Add `AGI_HOST` env var | `asterisk-realtime` | `docker-compose.pbxadmin.yml` | Optional override |
| Add AGI route to `from-trunk` | `asterisk-realtime` | `extensions.conf` | So PSTN emulator can reach ext 110-113 |

### No Changes Needed

- **ARI:** Already configured and exposed (port 8088/8188). No Docker changes.
- **Config:** Tests read existing files or use AMI. No Docker changes.
- **PSTN emulator:** Already supports Originate to any extension. No changes needed.

### Detailed extensions.conf Additions

Add to `[globals]`:
```ini
AGI_HOST = 172.17.0.1
```

Add to `[default]` context (after the parking section, before the agent login section):
```ini
; FastAGI test extensions
exten => 110,1,AGI(agi://${AGI_HOST}:4573/loadtest)
same => n,Hangup()
exten => 111,1,AGI(agi://${AGI_HOST}:4573/echo,${ARG1})
same => n,Hangup()
exten => 112,1,AGI(agi://${AGI_HOST}:4573/slow)
same => n,Hangup()
exten => 113,1,AGI(agi://${AGI_HOST}:4573/error)
same => n,Hangup()
```

Add to `[from-trunk]` context:
```ini
exten = 110,1,Goto(default,110,1)
exten = 111,1,Goto(default,111,1)
exten = 112,1,Goto(default,112,1)
exten = 113,1,Goto(default,113,1)
```

---

## File Structure

```
tests/PbxAdmin.LoadTests/
├── Sdk/
│   ├── AriClientService.cs          # ARI WebSocket + REST wrapper
│   ├── AgiServerService.cs          # FastAGI server (IHostedService)
│   ├── AgiScriptHandler.cs          # Base class for AGI script handlers
│   ├── AgiSessionInfo.cs            # Session tracking model
│   └── ConfigValidator.cs           # .conf parse/serialize round-trip utility
├── Scenarios/Functional/
│   ├── AriChannelScenario.cs        # Create/manage channels via ARI
│   ├── AriBridgeScenario.cs         # Bridge operations via ARI
│   ├── AgiLoadScenario.cs           # 20 concurrent AGI sessions
│   ├── AgiCommandScenario.cs        # Individual AGI command tests
│   └── ConfigRoundTripScenario.cs   # Parse/serialize 10 config files
├── Scenarios/Load/
│   └── AriStressScenario.cs         # 50 concurrent ARI channels
├── Scenarios/Chaos/
│   └── AriReconnectScenario.cs      # WebSocket reconnection test
└── Validation/
    ├── AriEventValidator.cs         # Compare ARI vs AMI events
    └── AgiSessionValidator.cs       # Validate AGI command results
```

**New files: 12**
**Modified files: 3** (ScenarioRegistry.cs, TestContext.cs, PbxAdmin.LoadTests.csproj)

---

## TestContext Extensions

`TestContext` needs two new optional properties for Phase B services:

```csharp
// In TestContext.cs — add these properties
public AriClientService? AriClient { get; init; }
public AgiServerService? AgiServer { get; init; }
```

These are nullable because Phase A scenarios do not use them. Phase B scenarios check for null and throw a descriptive error if the required service is not configured.

The `ConfigValidator` is a static utility class and does not need a `TestContext` entry.

---

## NuGet Dependencies

Add to `PbxAdmin.LoadTests.csproj`:

```xml
<PackageReference Include="Asterisk.Sdk.Ari" Version="1.5.1" />
<PackageReference Include="Asterisk.Sdk.Config" Version="1.5.1" />
<PackageReference Include="Asterisk.Sdk.Agi" Version="1.5.1" />
```

No other new dependencies. ARI uses `System.Net.WebSockets` (built-in). AGI uses `System.Net.Sockets` (built-in).

---

## ScenarioRegistry Additions

```csharp
// Phase B: Protocol scenarios
["ari-channel"] = new AriChannelScenario(),
["ari-bridge"] = new AriBridgeScenario(),
["ari-stress"] = new AriStressScenario(),
["ari-reconnect"] = new AriReconnectScenario(),
["config-roundtrip"] = new ConfigRoundTripScenario(),
["agi-load"] = new AgiLoadScenario(),
["agi-commands"] = new AgiCommandScenario(),
```

Total scenarios after Phase B: 19 (Phase A) + 7 (Phase B) = **26 scenarios**.

---

## Implementation Order

Phase B is divided into 3 independent tracks that can be implemented in any order. Within each track, the order is sequential.

### Track 1: ARI (estimated 3 tasks)

1. **AriClientService** — WebSocket connection, REST methods, event deserialization, reconnection logic.
2. **AriChannelScenario + AriBridgeScenario + AriEventValidator** — Functional scenarios with cross-protocol validation.
3. **AriStressScenario + AriReconnectScenario** — Load and chaos scenarios.

### Track 2: Config (estimated 1 task)

1. **ConfigValidator + ConfigRoundTripScenario** — Parse/serialize utility, round-trip tests for 10 config files, synthetic endpoint generation.

### Track 3: AGI (estimated 3 tasks)

1. **AgiServerService + AgiScriptHandler + AgiSessionInfo** — FastAGI server infrastructure, script handlers, session tracking.
2. **Docker stack changes** — Add AGI extensions to `extensions.conf`, add `AGI_HOST` global, update `from-trunk`.
3. **AgiLoadScenario + AgiCommandScenario + AgiSessionValidator** — Functional scenarios and validation.

### Track 4: Integration (estimated 1 task)

1. **Wire everything** — Update `TestContext`, `ScenarioRegistry`, `PbxAdmin.LoadTests.csproj`. Startup logic to optionally start `AgiServerService` and `AriClientService` when Phase B scenarios are selected.

**Total: 8 implementation tasks**, each suitable for a single subagent session.

---

## Success Criteria

### ARI

| Criterion | Threshold |
|-----------|-----------|
| WebSocket connection established | Within 5 seconds |
| WebSocket stays connected under load | 50 channels, 0 reconnections |
| Channel creation via REST | 50/50 succeed (HTTP 200) |
| ARI events match AMI events | 100% channel ID match, timestamps within 1s |
| Bridge operations complete | Create, add, remove, destroy all succeed |
| Reconnection after drop | Within 5 seconds, events resume |
| No orphan channels | 0 channels in Stasis after each scenario |

### Config

| Criterion | Threshold |
|-----------|-----------|
| Round-trip lossless | 10/10 files produce identical parsed output |
| Section count preserved | Exact match before/after |
| Key count preserved | Exact match before/after |
| Template inheritance parsed | Parent and child sections correctly linked |
| Synthetic endpoint loads in Asterisk | `PJSIPShowEndpoint` returns the generated endpoint |

### AGI

| Criterion | Threshold |
|-----------|-----------|
| FastAGI server accepts connections | Within 1 second of call reaching extension |
| 20 concurrent sessions | All 20 complete without error |
| Command execution correctness | ANSWER, GET DATA, SET VARIABLE, HANGUP all return expected codes |
| Session cleanup | 0 sessions in tracker after test |
| Variable propagation | AGI-set variables visible in AMI/CEL |
| Error handling | `/error` script terminates gracefully, no hung channel |
| Long session support | `/slow` script runs 10s without timeout |

---

## Risk Register

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| `Asterisk.Sdk.Ari` WebSocket format differs from Asterisk 22 | ARI events fail to deserialize | Medium | Test against actual Asterisk 22 early; file SDK bug if needed |
| `host.docker.internal` not available on Linux | AGI connections fail | High | Use `172.17.0.1` (Docker bridge IP) as default, document override via `AGI_HOST` env var |
| ARI port 8088 bound to HTTP only (no auth by default) | Security concern in non-demo environments | Low | This is a test platform, not production; document the risk |
| FastAGI port 4573 firewall blocked | AGI connections timeout | Medium | Document that the port must be open on the host when running load tests |
| `Asterisk.Sdk.Config` does not handle `same =>` syntax | extensions.conf round-trip fails | Medium | File SDK bug; implement workaround in `ConfigValidator` |
| AGI session leak under crash | Memory grows over long tests | Low | 60-second session timeout + explicit cleanup in `StopAsync` |
| ARI stress test overwhelms Asterisk | PBX crashes or stops accepting connections | Medium | Batch channel creation (10 batches of 5) with 500ms delay between batches |

---

## Appendix: ARI Connection Details

### Realtime PBX

| Property | Value |
|----------|-------|
| HTTP URL | `http://asterisk-realtime:8088` (from Docker) or `http://localhost:8088` (from host) |
| WebSocket URL | `ws://asterisk-realtime:8088/ari/events?app=loadtest` |
| Username | `dashboard` |
| Password | `dashboard` |
| Auth method | HTTP Basic (base64 in `Authorization` header) |

### File PBX

| Property | Value |
|----------|-------|
| HTTP URL | `http://asterisk-file:8088` (from Docker) or `http://localhost:8188` (from host) |
| WebSocket URL | `ws://asterisk-file:8088/ari/events?app=loadtest` |
| Username | `dashboard` |
| Password | `dashboard` |

### ARI Endpoints Used

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/ari/channels` | POST | Create channel |
| `/ari/channels/{id}` | DELETE | Hangup channel |
| `/ari/channels/{id}/play` | POST | Play audio |
| `/ari/bridges` | POST | Create bridge |
| `/ari/bridges/{id}/addChannel` | POST | Add channel to bridge |
| `/ari/bridges/{id}/removeChannel` | POST | Remove channel from bridge |
| `/ari/bridges/{id}` | DELETE | Destroy bridge |
| `/ari/channels` | GET | List active channels (cleanup check) |

---

## Appendix: AGI Protocol Reference

### FastAGI Connection Flow

1. Asterisk opens TCP connection to `agi://host:port/script`.
2. Asterisk sends environment variables (one per line, blank line terminates):
   ```
   agi_request: agi://host:4573/loadtest
   agi_channel: PJSIP/2100-00000001
   agi_callerid: 3101234567
   agi_context: default
   agi_extension: 110
   agi_priority: 1
   ...
   (blank line)
   ```
3. The AGI server sends commands, Asterisk responds:
   ```
   ANSWER
   200 result=0
   GET DATA hello-world 5000 4
   200 result= (timeout)
   SET VARIABLE AGI_RESULT timeout
   200 result=1
   HANGUP
   200 result=1
   ```
4. Connection closes after HANGUP or when Asterisk hangs up the channel.

### AGI Response Codes

| Code | Meaning |
|------|---------|
| `200` | Success |
| `510` | Invalid or unknown command |
| `511` | Command not permitted on a dead channel |
| `520` | Usage error (wrong number of arguments) |
