# Call Flow & UX Improvements — Design Spec

**Author:** Harol Reina
**Date:** 2026-03-23
**Status:** Draft
**Goal:** Add a unified call flow visualization page, improve the dialplan browser for engineers, enhance outbound route readability, and add cross-references across all PBX management pages — bringing PbxAdmin's usability on par with established PBX admin UIs while offering a unique dialplan debugger.

---

## 1. Problem Statement

PbxAdmin has excellent individual pages for managing routes, time conditions, IVR menus, queues, and extensions. However:

1. **No unified view of call flow.** An admin must open 4-5 pages to trace how a DID reaches an agent. Competing products in the space solve this with flow visualization.

2. **The `/dialplan` page shows raw Asterisk data.** Contexts like `tc-business-hours`, applications like `GotoIfTime(09:00-18:00,mon,*,*?...)`, and priorities are meaningless to a PBX admin who configured everything through the UI.

3. **Outbound routes are cryptic.** Dial patterns (`_NXXNXXXXXX`), prepend/prefix notation (`P: +1 X: 9`), and trunk failover order lack human-readable explanations.

4. **No cross-references.** Deleting a queue that an IVR references produces no warning. The admin cannot see "which DIDs reach this queue?" without checking each route manually.

5. **No call simulation.** "What happens if someone calls 5551234 on Sunday at 23:00?" requires the admin to mentally trace the route, evaluate time conditions, and follow IVR branches.

---

## 2. Components

Four components, implemented together across three phases:

| Component | Location | Audience |
|-----------|----------|----------|
| `/call-flow` page | PBX Management (new) | Admin — daily use |
| `/dialplan` improvements | System (moved from PBX Management) | Engineer — debugging |
| `/routes` outbound improvements | PBX Management (existing) | Admin — daily use |
| Cross-references | Existing entity pages | Admin — daily use |

---

## 3. `/call-flow` Page

### 3.1 Page Structure

Three vertical zones:

**Zone 1: Header with Call Tracer**
- Input field: "Trace a call to:" with placeholder showing example DID
- Date/time picker: defaults to current time, allows any date/time for "what-if" simulation
- Override mode selector: "Live" (reads current AstDB overrides) | "No overrides" (ignores all) | "All open" | "All closed" — defaults to "Live"
- "Trace" button: executes the simulation
- Auto-detects direction: inbound if number matches a configured DID pattern (exact or wildcard); outbound if it matches an outbound dial pattern. If both match, inbound takes precedence (inbound routes are evaluated first in Asterisk). If neither matches, shows "No route found" message.
- When active, replaces Zone 3 with trace results

**Zone 2: Overview Dashboard**

KPI row (4 cards):
- DIDs: `{active}/{total}` enabled inbound routes
- Time Conditions: `{open} open · {closed} closed · {override} override`
- Queues: `{healthy} healthy · {empty} empty` (empty = 0 online agents)
- Trunks: `{up} registered · {down} down`

Health warnings row (P1 only):
- Cards with severity icon (red=error, yellow=warning) + description + click-to-navigate
- Categories: broken references, operational state, coverage gaps
- Examples:
  - ERROR: "Route 'Main Line' → TC 'old-hours' does not exist"
  - WARNING: "TC 'business-hours' has manual override CLOSED active"
  - WARNING: "Queue 'sales' has 0 agents online"
  - WARNING: "Trunk 'primary' is unreachable (used by 3 outbound routes)"

**Zone 3: Two-Panel Inbound Flow**

Left panel (300px):
- Search/filter input
- List of inbound routes ordered by priority
- Each item: DID pattern, route name, destination type badge (TC/Queue/Ext/IVR)
- Click selects and shows flow in right panel

Right panel:
- Horizontal flow of connected cards for the selected DID
- Card types by entity:
  - **DID card**: DID pattern, route name, priority
  - **TC card**: name, schedule summary, current state badge (OPEN green / CLOSED red / OVERRIDE yellow), branches below
  - **IVR card**: name, option count, expands to show digit → destination list
  - **Queue card**: name, strategy, agent count (online/total)
  - **Extension card**: number, name, registered badge
  - **Voicemail card**: extension, email
  - **Hangup card**: terminal node
- Cards connected by arrows (→), branches shown vertically at decision points
- Each card is clickable → navigates to the entity's edit page
- Each card has expandable "Show dialplan" → shows the DialplanGenerator output for that entity (D)

### 3.2 Call Tracer (replaces Zone 3 when active)

Input: number + DateTime

**Inbound trace** (number matches a known DID):

Vertical numbered steps:
```
Step 1: Inbound route matched
  Route: "Main Line" (DID 5551234, priority 100)
  [Inspect] → Goto(tc-business-hours,s,1)

Step 2: Enter time condition "business-hours"
  Override check: DB(TC_OVERRIDE/business-hours) = (empty) → no override
  [Inspect] → Set(OVERRIDE=${DB(TC_OVERRIDE/business-hours)})

Step 3: Evaluate schedule
  Current time: Tuesday 14:30
  ✅ GotoIfTime(09:00-17:00,tue,*,*) → MATCH → go to OPEN branch
  [Inspect] → GotoIfTime(09:00-17:00,tue,*,*?tc-business-hours-open,s,1)

Step 4: Open destination
  → Queue "sales" (3 agents online, strategy: ringall)
  [Inspect] → Goto(queues,sales,1)
```

**Outbound trace** (number matches an outbound pattern):

```
Step 1: Outbound route matched
  Route: "US Long Distance" (pattern _1NXXNXXXXXX, priority 100)

Step 2: Number manipulation
  Input: 18005551234
  Strip prefix "1" → 8005551234
  Prepend "+1" → +18005551234

Step 3: Dial trunk (primary)
  Trunk: trunk-primary (PJSIP) ● Registered
  → Dial(PJSIP/+18005551234@trunk-primary,60)

Step 4: Failover (if primary fails)
  Trunk: trunk-backup (PJSIP) ● Registered
  → Dial(PJSIP/+18005551234@trunk-backup,60)
```

Each step shows:
- Step number and description in plain language
- Result badge: green (matched/success), red (not matched/error), gray (skipped)
- "Inspect" button: expands to show the exact dialplan lines that execute
- The taken path highlighted in green, discarded branches in gray

**No match**: If the number doesn't match any inbound DID or outbound pattern, shows: "No route found for {number}. Check your inbound routes or outbound dial patterns."

### 3.3 Health Warnings — Priority Levels

**P1 (implemented in Fase 1):**

| Category | Check | Severity |
|----------|-------|----------|
| Broken ref | Inbound route → TC/IVR/Queue/Ext that doesn't exist | Error |
| Broken ref | IVR option → entity that doesn't exist | Error |
| Broken ref | TC open/closed dest → entity that doesn't exist | Error |
| Operational | Trunk unreachable used by outbound route | Error |
| Operational | TC override active (manual OPEN or CLOSED) | Warning |
| Operational | Queue with 0 online agents | Warning |
| Coverage | DID with no inbound route | Warning |
| Coverage | Outbound route with only 1 trunk (no failover) | Info |

**P2 (roadmap):**
- Outbound patterns that overlap with incorrect priority order
- IVR timeout/invalid destination creating infinite loops
- Extensions not registered that are direct destinations of inbound routes
- TC without any time ranges defined (always closed)

**P3 (roadmap — differentiator):**
- TC schedule gaps (e.g., lunch hour 12-13 not covered)
- Queue with ringall strategy but 10+ agents (performance concern)
- Outbound trunk without qualify_frequency (can't detect trunk failure)
- IVR greeting sound file not found in Asterisk

---

## 4. `/dialplan` Improvements

### 4.1 Move to System Section

Rename sidebar link from "Dialplan" to "Advanced Dialplan" and move from PBX Management to System section (after Console, before Traffic).

### 4.2 Context Type Badges

Automatic badges based on context naming pattern:

| Pattern | Badge | Color |
|---------|-------|-------|
| `from-trunk` | Inbound | Green |
| `outbound-routes` | Outbound | Blue |
| `tc-*` | Time Condition | Yellow |
| `ivr-*` | IVR | Purple |
| `queues` | Queues | Orange |
| `default` | Main | Gray |
| System registrar / `__*` / known system names | System | Gray dim |

Displayed next to the existing System/User badge.

### 4.3 Application Humanization

New column "Description" in the extensions table, generated by `DialplanHumanizer`:

| App + AppData | Humanized |
|---------------|-----------|
| `Goto(default,1001,1)` | Forward to ext 1001 |
| `Goto(queues,sales,1)` | Send to queue 'sales' |
| `Goto(tc-business-hours,s,1)` | Check time condition 'business-hours' |
| `Goto(ivr-main,s,1)` | Go to IVR 'main' |
| `Dial(PJSIP/${EXTEN}@trunk,60)` | Dial via trunk (60s) |
| `Queue(sales,,,,300)` | Queue 'sales' (300s timeout) |
| `GotoIfTime(09:00-18:00,mon,*,*?...)` | If Mon 09:00-18:00 → open |
| `Set(__ROUTE=X)` | Set route: X |
| `ExecIf($["${DIALSTATUS}"=...])` | If unavailable → try failover |
| `VoiceMail(1005@default,u)` | Voicemail for 1005 (unavailable) |
| `Hangup()` | Hang up |
| `Answer()` | Answer call |
| `Background(greeting)` | Play 'greeting' (wait for input) |
| `WaitExten(5)` | Wait 5s for digit |
| `Playback(option-is-invalid)` | Play 'invalid option' |
| `GotoIf($["${OVERRIDE}"="OPEN"]?...)` | If override=OPEN → go to open |
| `GotoIf($["${OVERRIDE}"="CLOSED"]?...)` | If override=CLOSED → go to closed |
| `GotoIf($[${IVR_RETRIES}<3]?s,2)` | If retries < 3 → replay menu |
| `Set(IVR_RETRIES=$[${IVR_RETRIES}+1])` | Increment retry counter |
| `Set(OUTNUM=...)` | Transform dialed number |
| Unrecognized | Show raw AppData |

### 4.4 Bidirectional Links (F)

**From `/dialplan` to Call Flow:**
- Contexts generated by PbxAdmin (`tc-*`, `ivr-*`, `from-trunk`, `outbound-routes`) show a button: "View in Call Flow" → navigates to `/call-flow` with the relevant DID or entity pre-selected.

**From Call Flow to `/dialplan`:**
- Each flow card has a secondary action: "Open in Advanced Dialplan" → navigates to `/dialplan` with the relevant context pre-selected in the left panel.

---

## 5. `/routes` Outbound Improvements

### 5.1 Pattern Humanizer

Below each dial pattern in the routes table, show a human-readable description:

| Pattern | Description |
|---------|-------------|
| `_NXXNXXXXXX` | 10-digit (e.g. 2125551234) |
| `_1NXXNXXXXXX` | 11-digit starting with 1 |
| `_NXXXXXX` | 7-digit local |
| `_00X.` | International (00 prefix) |
| `_011X.` | International (011 prefix) |
| `_9NXXNXXXXXX` | 10-digit after prefix 9 |
| `911` / `_N11` | Emergency / service code |
| `_X.` | Any number (catch-all) |
| Other | Show Asterisk syntax explanation |

Generated by `DialPatternHumanizer.Describe(pattern)`. The initial implementation covers NANP (North American) patterns. The humanizer is designed to be extensible — future versions can add international patterns (UK `_0X.`, E.164 `_+X.`, etc.) via additional pattern rules.

### 5.2 Trunk Health Dots

Next to each trunk badge in the routes table:
- Green dot (●): trunk registered/available
- Red dot (●): trunk unreachable/rejected
- Gray dot (●): trunk status unknown

Trunk status comes from `ITrunkService.GetTrunksAsync(serverId)` which already returns `TrunkViewModel` with registration status. This data is populated from `PJSIPShowRegistrationsAction` / AOR contact qualify. No new AMI queries needed.

### 5.3 Failover Labels

Replace flat badge list with numbered sequence:
- `1. trunk-primary ● → 2. trunk-backup ●`
- Arrow (→) between trunks indicates failover relationship

### 5.4 Number Manipulation Preview

Replace `P: +1 X: 9` with a transformation preview:
- `9XXXXXXXXXX → +1XXXXXXXXXX`
- Shows the before/after pattern so the admin understands the manipulation

---

## 6. Cross-References

### 6.1 Where They Appear

**Inbound Routes (`/routes` tab inbound):**
- Inline summary below each route: "→ TC business-hours → Open: Queue sales / Closed: Ext 1099"
- Single line, collapsed by default. Shows the immediate flow chain.

**Time Conditions (`/time-conditions`):**
- Below each TC card: "Used by: Main Line (DID 5551234), Branch Office (DID 5559999)"
- If unused: yellow warning badge "Not referenced by any inbound route"

**IVR Menus (`/ivr-menus`):**
- Below each IVR card: "Referenced by: TC business-hours (closed dest), ruta Support Line"
- If unused: yellow warning badge "Not referenced"

**Queues (queue config page):**
- Header area: "Receives calls from: TC business-hours (open), IVR main-menu (option 1)"

**Extensions (`/extensions`):**
- Badge on extension card if it's a direct DID destination: "DID: 5551234"

### 6.2 Data Source

Cross-references are computed by `CallFlowService` by traversing the route → TC → IVR → Queue/Extension graph. Cached with the same TTL as the dialplan discovery cache (5 minutes). Refreshed when any entity is saved.

---

## 7. Data Model

### 7.1 New Service: `CallFlowService`

Singleton. Dependencies: `RouteService`, `TimeConditionService`, `IvrMenuService`, `QueueConfigService`, `IExtensionService`, `DialplanDiscoveryService`, `AsteriskMonitorService`.

Does NOT access AMI or DB directly — consumes existing services.

**Data access:** `CallFlowService` needs raw config data (not just view models) to build the graph. It calls `RouteService.GetInboundRouteAsync(id)` / `GetOutboundRouteAsync(id)` for raw `InboundRouteConfig` / `OutboundRouteConfig` which include `DestinationType`, `Destination`, `Prepend`, `Prefix`, and `Trunks`. For bulk loading, add `RouteService.GetAllInboundConfigsAsync(serverId)` and `GetAllOutboundConfigsAsync(serverId)` that return `List<InboundRouteConfig>` / `List<OutboundRouteConfig>`.

**Cache invalidation:** `CallFlowService` exposes `InvalidateCache(serverId)`. Each service (`RouteService`, `TimeConditionService`, `IvrMenuService`, `QueueConfigService`) calls `_callFlowService.InvalidateCache(serverId)` after successful save/delete operations, alongside the existing `DialplanRegenerator.RegenerateAsync()` call. This is a simple, explicit approach — no event bus needed.

Key methods:
- `BuildFlowAsync(serverId)` → `CallFlowGraph` (all inbound flows + cross-refs, cached 5 min)
- `TraceCallAsync(serverId, number, dateTime, overrideMode)` → `CallFlowTrace`
- `GetHealthWarningsAsync(serverId)` → `List<HealthWarning>`
- `GetReferencesForAsync(serverId, entityType, entityId)` → `List<CrossReference>`
- `InvalidateCache(serverId)` → void (clears cached graph, forces rebuild on next call)

### 7.2 New Types

```csharp
// Notes:
// - EntityId is string for uniformity: routes/TCs/IVRs use int Id (.ToString()),
//   queues and extensions use name as ID. String accommodates both.
// - These types are never serialized over SignalR — used only in-memory for Blazor
//   Server rendering. No [JsonDerivedType] needed. If serialization is ever required,
//   add System.Text.Json polymorphic attributes with AOT-safe source generation.

// Flow graph nodes
public abstract class CallFlowNode
{
    public string EntityType { get; init; } = "";    // "route", "tc", "ivr", "queue", "extension", "voicemail", "hangup"
    public string EntityId { get; init; } = "";
    public string Label { get; init; } = "";
    public string? EditUrl { get; init; }
    public List<string> DialplanLines { get; init; } = [];  // generated slice
}

public sealed class DidNode : CallFlowNode
{
    public string DidPattern { get; init; } = "";
    public string RouteName { get; init; } = "";
    public int Priority { get; init; }
    public CallFlowNode? Destination { get; init; }
}

public sealed class TimeConditionNode : CallFlowNode
{
    public string ScheduleSummary { get; init; } = "";   // "Mon-Fri 09:00-17:00"
    public string CurrentState { get; init; } = "";       // "Open", "Closed", "Override:Open"
    public CallFlowNode? OpenBranch { get; init; }
    public CallFlowNode? ClosedBranch { get; init; }
}

public sealed class IvrNode : CallFlowNode
{
    public string? Greeting { get; init; }
    public int Timeout { get; init; }
    public List<IvrOptionNode> Options { get; init; } = [];
}

public sealed class IvrOptionNode
{
    public string Digit { get; init; } = "";
    public string? OptionLabel { get; init; }
    public CallFlowNode? Destination { get; init; }
}

public sealed class QueueNode : CallFlowNode
{
    public string Strategy { get; init; } = "";
    public int MemberCount { get; init; }
    public int OnlineCount { get; init; }
}

public sealed class ExtensionNode : CallFlowNode
{
    public string Number { get; init; } = "";
    public string? DisplayName { get; init; }
    public bool IsRegistered { get; init; }
    public string Technology { get; init; } = "";
}

public sealed class VoicemailNode : CallFlowNode
{
    public string Extension { get; init; } = "";
    public string? Email { get; init; }
}

public sealed class HangupNode : CallFlowNode { }

// Call trace
public sealed class CallFlowTrace
{
    public string InputNumber { get; init; } = "";
    public DateTime InputTime { get; init; }
    public string Direction { get; init; } = "";       // "Inbound" or "Outbound"
    public string OverrideMode { get; init; } = "";    // "Live", "None", "AllOpen", "AllClosed"
    public List<CallFlowTraceStep> Steps { get; init; } = [];
    public bool RouteFound { get; init; }
}

public sealed class CallFlowTraceStep
{
    public int StepNumber { get; init; }
    public string Description { get; init; } = "";
    public string? Evaluation { get; init; }       // "GotoIfTime 09:00-17:00 tue → TRUE"
    public string Result { get; init; } = "";       // "Matched", "NotMatched", "Skipped"
    public string EntityType { get; init; } = "";
    public string EntityId { get; init; } = "";
    public List<string> DialplanLines { get; init; } = [];
}

// Health
public sealed class HealthWarning
{
    public string Severity { get; init; } = "";     // "Error", "Warning", "Info"
    public string Category { get; init; } = "";     // "BrokenRef", "Operational", "Coverage"
    public string Message { get; init; } = "";
    public string EntityType { get; init; } = "";
    public string EntityId { get; init; } = "";
    public string? NavigateUrl { get; init; }
}

// Cross-reference
public sealed class CrossReference
{
    public string SourceType { get; init; } = "";   // "route", "tc", "ivr"
    public string SourceId { get; init; } = "";
    public string SourceLabel { get; init; } = "";
    public string Relationship { get; init; } = ""; // "destination", "open_branch", "option_1"
}
```

### 7.3 New Helpers

**`DialplanHumanizer`** — static class
- `Humanize(string app, string appData) → string`
- Pattern matching on known apps, fallback to raw text
- Used by `/dialplan` improved and Call Tracer inspect

**`DialPatternHumanizer`** — static class
- `Describe(string pattern) → string`
- Translates Asterisk pattern syntax to human-readable description
- `Example(string pattern) → string?` — generates an example number that matches

**`NumberManipulator`** — static class
- `Apply(string number, string? prefix, string? prepend) → string`
- Extracted from `DialplanGenerator.GenerateOutboundRoutes()` logic to avoid duplication
- Used by: Call Tracer (outbound trace step 2), Routes table manipulation preview
- `DialplanGenerator` should be refactored to call this shared method

---

## 8. Implementation Phases

### Phase 1: Foundation + Call Flow Page
- `CallFlowNode` types, `CallFlowTrace`, `HealthWarning`, `CrossReference`
- `CallFlowService`: `BuildFlowAsync`, `GetHealthWarningsAsync`, `GetReferencesForAsync`
- `DialplanHumanizer`, `DialPatternHumanizer`
- Unit tests for all above
- `/call-flow` page: Zone 2 (overview dashboard) + Zone 3 (two-panel inbound flow)
- Health warnings P1
- Nav link in sidebar (PBX Management, after Time Conditions)

### Phase 2: Call Tracer + Dialplan Improvements
- `CallFlowService.TraceCallAsync` with full TC evaluation + IVR traversal
- Call Tracer UI: Zone 1 header + trace results with step-through debugger
- Inspect dialplan lines per step (E)
- Dialplan slice inline in Call Flow nodes (D)
- `/dialplan` moved to System: badges + humanization column + bidirectional links (F)

### Phase 3: Routes + Cross-References
- `/routes` outbound: pattern humanizer, trunk health dots, failover labels, manipulation preview
- Cross-references in `/time-conditions`, `/ivr-menus`, queue config, `/extensions`
- Inline flow summary in `/routes` inbound
- "Not referenced" warnings on orphan entities

### Roadmap (future, not implemented now)
- Health warnings P2: overlapping patterns, IVR loops, unregistered extension destinations, TC with no ranges
- Health warnings P3: TC schedule gaps, inappropriate queue strategy, missing greeting files
- Outbound flow visualization in Call Flow (pattern → trunk chain as horizontal cards)
- Export call flow as diagram/PDF
- Call flow diff: "what changed since last save"

---

## 9. Roadmap Reference

Add to `docs/specs/2026-03-16-sdk-next-level-design.md` under a new section:

```
### PbxAdmin: Call Flow & UX Improvements (post Fase 7)

**Phase 1 — Foundation:** CallFlowService, DialplanHumanizer, /call-flow page, Health P1
**Phase 2 — Call Tracer:** Debugger step-through, /dialplan improvements, bidirectional links
**Phase 3 — Routes & Cross-refs:** Outbound UX, cross-references, orphan warnings
**Future:** Health P2-P3, outbound flow visualization, export diagrams
```

---

## 10. Tech Stack

- .NET 10, Blazor Server, Asterisk.Sdk.Ami
- xUnit, FluentAssertions, NSubstitute
- AOT-safe (no reflection)
- Source-gen logging (`[LoggerMessage]`)
- Existing PbxAdmin services, CSS variables, component patterns
- No new NuGet packages required
