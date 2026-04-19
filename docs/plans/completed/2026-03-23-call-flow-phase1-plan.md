# Call Flow & UX Improvements — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the CallFlowService foundation, DialplanHumanizer/DialPatternHumanizer helpers, and the `/call-flow` page with overview dashboard + two-panel inbound flow visualization + P1 health warnings.

**Architecture:** `CallFlowService` is a singleton that consumes existing services (`RouteService`, `TimeConditionService`, `IvrMenuService`, `IQueueConfigService`, `IExtensionService`, `ITrunkService`, `DialplanDiscoveryService`) to build a graph of `CallFlowNode` objects. The graph is cached per-server with 5-minute TTL, invalidated on entity saves. The `/call-flow` page renders the graph using Blazor Server components with the same two-panel pattern as the existing `/dialplan` page.

**Tech Stack:** .NET 10, Blazor Server, xUnit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0. AOT-safe. Source-gen logging.

**Spec:** `docs/superpowers/specs/2026-03-23-call-flow-ux-design.md`

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `Examples/PbxAdmin/Models/CallFlowModels.cs` | `CallFlowNode` hierarchy, `CallFlowGraph`, `HealthWarning`, `CrossReference` types |
| `Examples/PbxAdmin/Services/CallFlow/CallFlowService.cs` | Build flow graph, health warnings, cross-references, cache |
| `Examples/PbxAdmin/Services/CallFlow/DialplanHumanizer.cs` | Translate Asterisk app+appData to human-readable text |
| `Examples/PbxAdmin/Services/CallFlow/DialPatternHumanizer.cs` | Translate Asterisk dial patterns to descriptions + examples |
| `Examples/PbxAdmin/Services/CallFlow/NumberManipulator.cs` | Shared prepend/prefix number transformation logic |
| `Examples/PbxAdmin/Components/Pages/CallFlow.razor` | Call Flow page with dashboard + two-panel inbound flow |
| `Tests/PbxAdmin.Tests/Services/CallFlow/DialplanHumanizerTests.cs` | Tests for app humanization |
| `Tests/PbxAdmin.Tests/Services/CallFlow/DialPatternHumanizerTests.cs` | Tests for pattern description + examples |
| `Tests/PbxAdmin.Tests/Services/CallFlow/NumberManipulatorTests.cs` | Tests for number transformation |
| `Tests/PbxAdmin.Tests/Services/CallFlow/CallFlowServiceTests.cs` | Tests for graph building + health warnings |
| `Tests/PbxAdmin.Tests/Components/CallFlowPageTests.cs` | bUnit tests for Call Flow page rendering |

### Modified Files

| File | Change |
|------|--------|
| `Examples/PbxAdmin/Services/RouteService.cs` | Add `GetAllInboundConfigsAsync`, `GetAllOutboundConfigsAsync` |
| `Examples/PbxAdmin/Components/Layout/MainLayout.razor` | Add "Call Flow" nav link, move "Dialplan" to System |
| `Examples/PbxAdmin/Program.cs` | Register `CallFlowService` |
| `Examples/PbxAdmin/Resources/SharedStrings.resx` | Add CF_* localization keys (EN) |
| `Examples/PbxAdmin/Resources/SharedStrings.es.resx` | Add CF_* localization keys (ES) |

---

## Task 1: CallFlowNode Model Types

**Files:**
- Create: `Examples/PbxAdmin/Models/CallFlowModels.cs`

> **Note:** `CallFlowTrace` and `CallFlowTraceStep` types are deferred to Phase 2 — they are not consumed by any Phase 1 code.

- [ ] **Step 1: Create the model file with all types**

```csharp
namespace PbxAdmin.Models;

// Notes:
// - EntityId is string for uniformity: routes/TCs/IVRs use int Id (.ToString()),
//   queues and extensions use name as ID.
// - These types are never serialized over SignalR — used only in-memory for Blazor rendering.

public abstract class CallFlowNode
{
    public string EntityType { get; init; } = "";
    public string EntityId { get; init; } = "";
    public string Label { get; init; } = "";
    public string? EditUrl { get; init; }
    public List<string> DialplanLines { get; init; } = [];
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
    public string ScheduleSummary { get; init; } = "";
    public string CurrentState { get; init; } = "";
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

public sealed class CallFlowGraph
{
    public string ServerId { get; init; } = "";
    public DateTime BuiltAt { get; init; }
    public List<DidNode> InboundFlows { get; init; } = [];
    public List<HealthWarning> Warnings { get; init; } = [];
}

public sealed class HealthWarning
{
    public string Severity { get; init; } = "";
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string EntityType { get; init; } = "";
    public string EntityId { get; init; } = "";
    public string? NavigateUrl { get; init; }
}

public sealed class CrossReference
{
    public string SourceType { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string SourceLabel { get; init; } = "";
    public string Relationship { get; init; } = "";
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Commit**

```bash
git add Examples/PbxAdmin/Models/CallFlowModels.cs
git commit -m "feat(callflow): add CallFlowNode model hierarchy and HealthWarning types"
```

---

## Task 2: DialplanHumanizer — Tests + Implementation

**Files:**
- Create: `Tests/PbxAdmin.Tests/Services/CallFlow/DialplanHumanizerTests.cs`
- Create: `Examples/PbxAdmin/Services/CallFlow/DialplanHumanizer.cs`

- [ ] **Step 1: Write tests**

Test cases:
- `Humanize_Goto_Extension` — `Goto("default,1001,1")` → `"Forward to ext 1001"`
- `Humanize_Goto_Queue` — `Goto("queues,sales,1")` → `"Send to queue 'sales'"`
- `Humanize_Goto_TimeCondition` — `Goto("tc-business-hours,s,1")` → `"Check time condition 'business-hours'"`
- `Humanize_Goto_Ivr` — `Goto("ivr-main,s,1")` → `"Go to IVR 'main'"`
- `Humanize_Dial_Trunk` — `Dial("PJSIP/${EXTEN}@trunk-primary,60")` → `"Dial via trunk-primary (60s)"`
- `Humanize_Queue_App` — `Queue("sales,,,,300")` → `"Queue 'sales' (300s timeout)"`
- `Humanize_GotoIfTime` — `GotoIfTime("09:00-18:00,mon,*,*?tc-bh-open,s,1")` → `"If Mon 09:00-18:00 → open"`
- `Humanize_Set_Route` — `Set("__ROUTE=To-PSTN")` → `"Set route: To-PSTN"`
- `Humanize_Set_Override` — `GotoIf("$[\"${OVERRIDE}\"=\"OPEN\"]?tc-bh-open,s,1")` → `"If override=OPEN → go to open"`
- `Humanize_Hangup` — `Hangup("")` → `"Hang up"`
- `Humanize_Answer` — `Answer("")` → `"Answer call"`
- `Humanize_VoiceMail` — `VoiceMail("1005@default,u")` → `"Voicemail for 1005 (unavailable)"`
- `Humanize_Unknown_App` — `AGI("custom-script.agi")` → `"AGI(custom-script.agi)"` (raw fallback)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "DialplanHumanizer"`
Expected: FAIL

- [ ] **Step 3: Implement DialplanHumanizer**

Static class with `Humanize(string app, string appData) → string`. Pattern matching using `switch` on app name, then regex/string parsing on appData for known patterns. Fallback: `$"{app}({appData})"` or `app` if appData is empty.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "DialplanHumanizer"`
Expected: ALL PASS

- [ ] **Step 5: Commit**

```bash
git add Examples/PbxAdmin/Services/CallFlow/DialplanHumanizer.cs \
  Tests/PbxAdmin.Tests/Services/CallFlow/DialplanHumanizerTests.cs
git commit -m "feat(callflow): add DialplanHumanizer for human-readable app descriptions"
```

---

## Task 3: DialPatternHumanizer — Tests + Implementation

**Files:**
- Create: `Tests/PbxAdmin.Tests/Services/CallFlow/DialPatternHumanizerTests.cs`
- Create: `Examples/PbxAdmin/Services/CallFlow/DialPatternHumanizer.cs`

- [ ] **Step 1: Write tests**

Test cases for `Describe(pattern)`:
- `Describe_NXXNXXXXXX` — `"_NXXNXXXXXX"` → `"10-digit (e.g. 2125551234)"`
- `Describe_1NXXNXXXXXX` — `"_1NXXNXXXXXX"` → `"11-digit starting with 1"`
- `Describe_NXXXXXX` — `"_NXXXXXX"` → `"7-digit local"`
- `Describe_00X` — `"_00X."` → `"International (00 prefix)"`
- `Describe_011X` — `"_011X."` → `"International (011 prefix)"`
- `Describe_911` — `"911"` → `"Emergency 911"`
- `Describe_N11` — `"_N11"` → `"Service code (N11)"`
- `Describe_CatchAll` — `"_X."` → `"Any number (catch-all)"`
- `Describe_Exact` — `"5551234567"` → `"Exact: 5551234567"`
- `Describe_Unknown` — `"_[2-9]XX."` → shows Asterisk syntax as-is

Test cases for `Example(pattern)`:
- `Example_NXXNXXXXXX` — returns a valid 10-digit example like `"2125551234"`
- `Example_Exact` — returns the exact number itself
- `Example_CatchAll` — returns a generic example like `"12345"`

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "DialPatternHumanizer"`
Expected: FAIL

- [ ] **Step 3: Implement DialPatternHumanizer**

Static class. `Describe(string pattern) → string` — ordered pattern matching on known NANP patterns, fallback to raw. `Example(string pattern) → string?` — generates a sample number replacing `N`→`2`, `X`→`5`, etc.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "DialPatternHumanizer"`
Expected: ALL PASS

- [ ] **Step 5: Commit**

```bash
git add Examples/PbxAdmin/Services/CallFlow/DialPatternHumanizer.cs \
  Tests/PbxAdmin.Tests/Services/CallFlow/DialPatternHumanizerTests.cs
git commit -m "feat(callflow): add DialPatternHumanizer for human-readable dial patterns"
```

---

## Task 4: NumberManipulator — Tests + Implementation + Refactor DialplanGenerator

**Files:**
- Create: `Tests/PbxAdmin.Tests/Services/CallFlow/NumberManipulatorTests.cs`
- Create: `Examples/PbxAdmin/Services/CallFlow/NumberManipulator.cs`

> **Note:** `NumberManipulator` performs runtime number transformation (strip prefix, prepend string) for the Call Tracer and routes display. `DialplanGenerator` generates Asterisk expression syntax (`Set(OUTNUM={prepend}${EXTEN:{prefixLen}})`) which is a different operation. They share the same concept but are not interchangeable — no refactoring of `DialplanGenerator` is needed.

- [ ] **Step 1: Write tests**

Test cases:
- `Apply_NoPrefixNoPrepend` — `("18005551234", null, null)` → `"18005551234"`
- `Apply_PrependOnly` — `("8005551234", null, "+1")` → `"+18005551234"`
- `Apply_PrefixOnly` — `("98005551234", "9", null)` → `"8005551234"`
- `Apply_PrefixAndPrepend` — `("918005551234", "9", "+1")` → `"+18005551234"`
- `Apply_EmptyStrings` — `("5551234", "", "")` → `"5551234"`
- `Preview_ShowsTransformation` — `("9", "+1")` → `"9XXXXXXX → +1XXXXXXX"` (for UI display)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "NumberManipulator"`
Expected: FAIL

- [ ] **Step 3: Implement NumberManipulator**

```csharp
namespace PbxAdmin.Services.CallFlow;

public static class NumberManipulator
{
    public static string Apply(string number, string? prefix, string? prepend)
    {
        var result = number;
        if (!string.IsNullOrEmpty(prefix) && result.StartsWith(prefix, StringComparison.Ordinal))
            result = result[prefix.Length..];
        if (!string.IsNullOrEmpty(prepend))
            result = prepend + result;
        return result;
    }

    public static string Preview(string? prefix, string? prepend)
    {
        var prefixLen = prefix?.Length ?? 0;
        var before = (prefix ?? "") + new string('X', 7);
        var after = (prepend ?? "") + new string('X', 7);
        if (prefixLen == 0 && string.IsNullOrEmpty(prepend))
            return "";
        return $"{before} → {after}";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "NumberManipulator"`
Expected: ALL PASS

- [ ] **Step 5: Commit**

```bash
git add Examples/PbxAdmin/Services/CallFlow/NumberManipulator.cs \
  Tests/PbxAdmin.Tests/Services/CallFlow/NumberManipulatorTests.cs
git commit -m "feat(callflow): add NumberManipulator for number transformation"
```

---

## Task 5: RouteService — Add Bulk Config Methods

**Files:**
- Modify: `Examples/PbxAdmin/Services/RouteService.cs`

> **Why new methods:** The existing `GetInboundRoutesAsync` returns `List<InboundRouteViewModel>` (display-oriented) which lacks raw `DestinationType`/`Destination` fields. The existing `GetOutboundRoutesAsync` returns `List<OutboundRouteViewModel>` which lacks `Prepend`/`Prefix`/`Trunks`. `CallFlowService` needs the full config objects to build the flow graph. Before implementing, verify that the ViewModel types do not already expose these fields — if they do, use the existing methods and skip this task.

- [ ] **Step 1: Add `GetAllInboundConfigsAsync` method**

After existing `GetInboundRouteAsync` (around line 94), add:

```csharp
public async Task<List<InboundRouteConfig>> GetAllInboundConfigsAsync(string serverId, CancellationToken ct = default)
{
    var repo = _repoResolver.GetRepository(serverId);
    return await repo.GetInboundRoutesAsync(serverId, ct);
}
```

- [ ] **Step 2: Add `GetAllOutboundConfigsAsync` method**

After existing `GetOutboundRouteAsync` (around line 191), add:

```csharp
public async Task<List<OutboundRouteConfig>> GetAllOutboundConfigsAsync(string serverId, CancellationToken ct = default)
{
    var repo = _repoResolver.GetRepository(serverId);
    return await repo.GetOutboundRoutesAsync(serverId, ct);
}
```

- [ ] **Step 3: Build and run all tests**

Run: `dotnet build Asterisk.Sdk.slnx && dotnet test Tests/PbxAdmin.Tests/`
Expected: 0 warnings, all tests pass

- [ ] **Step 4: Commit**

```bash
git add Examples/PbxAdmin/Services/RouteService.cs
git commit -m "feat(callflow): add bulk config loading to RouteService"
```

---

## Task 6: CallFlowService — Graph Building + Health Warnings + Tests

**Files:**
- Create: `Examples/PbxAdmin/Services/CallFlow/CallFlowService.cs`
- Create: `Tests/PbxAdmin.Tests/Services/CallFlow/CallFlowServiceTests.cs`

- [ ] **Step 1: Write graph building tests**

Test cases:
- `BuildFlow_ShouldCreateDidNode_ForInboundRoute` — one route with extension destination → DidNode with ExtensionNode child
- `BuildFlow_ShouldCreateTcNode_WhenDestIsTimeCondition` — route → TC → open (queue) + closed (extension)
- `BuildFlow_ShouldCreateIvrNode_WhenDestIsIvr` — route → IVR with 3 options (extension, queue, sub-ivr)
- `BuildFlow_ShouldSetCurrentState_OnTcNode` — TC node reflects evaluated state (open/closed) for given time
- `BuildFlow_ShouldHandleMissingDestination_Gracefully` — route points to nonexistent TC → DidNode with null Destination
- `BuildFlow_ShouldSetEditUrls` — each node has correct edit URL (`/routes/inbound/edit/{id}`, `/time-conditions/edit/{id}`, etc.)

Mock pattern: mock `RouteService`, `TimeConditionService`, `IvrMenuService`, `IQueueConfigService`, `IExtensionService`, `ITrunkService` using NSubstitute. Return predefined configs. Use `NullLogger<CallFlowService>.Instance`.

- [ ] **Step 2: Write health warning tests**

Test cases:
- `Health_ShouldWarnBrokenRef_WhenRouteTcNotFound` — route → TC "old-hours" that doesn't exist → Error warning
- `Health_ShouldWarnBrokenRef_WhenIvrOptionExtNotFound` — IVR option targets ext 9999 that doesn't exist → Error warning
- `Health_ShouldWarnBrokenRef_WhenTcDestNotFound` — TC open dest → queue "deleted" that doesn't exist → Error warning
- `Health_ShouldWarnTcOverride` — TC with active override → Warning
- `Health_ShouldWarnEmptyQueue` — queue with 0 online agents → Warning
- `Health_ShouldWarnTrunkDown_WhenUsedByOutboundRoute` — outbound route trunk unreachable → Error
- `Health_ShouldWarnDidWithoutRoute` — configured DID without matching inbound route → Warning
- `Health_ShouldWarnSingleTrunkRoute` — outbound route with only 1 trunk → Info

- [ ] **Step 3: Write cross-reference tests**

Test cases:
- `GetReferences_ForTc_ShouldReturnRoutesThatUseIt` — TC referenced by 2 inbound routes → 2 CrossReference objects
- `GetReferences_ForQueue_ShouldReturnTcsAndIvrs` — queue referenced by TC (open branch) and IVR (option 1) → 2 CrossReference objects
- `GetReferences_ForUnusedEntity_ShouldReturnEmpty` — entity not referenced → empty list

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "CallFlowService"`
Expected: FAIL

- [ ] **Step 5: Implement CallFlowService**

Singleton service. Constructor takes all service dependencies. Uses `[LoggerMessage]` source-gen logging.

Key implementation details:
- `BuildFlowAsync(serverId)`: load all inbound configs, for each build DidNode → resolve destination recursively (TC→branches, IVR→options, Queue/Ext as leaf). Cache result per server.
- `GetHealthWarningsAsync(serverId)`: build flow first, then walk the graph checking for nulls (broken refs), query trunk/queue status, check TC overrides.
- `GetReferencesForAsync(serverId, entityType, entityId)`: walk all flows, collect nodes that reference the given entity.
- `InvalidateCache(serverId)`: remove cached graph for server.
- Cache: `ConcurrentDictionary<string, CallFlowGraph>` with 5-minute TTL (check `BuiltAt` on access). Thread-safe for concurrent Blazor circuits.

TC state evaluation: use `TimeConditionService.EvaluateState()` (static method at line 62) passing the TC's ranges, holidays, and `DateTime.UtcNow`.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "CallFlowService"`
Expected: ALL PASS

- [ ] **Step 7: Commit**

```bash
git add Examples/PbxAdmin/Services/CallFlow/CallFlowService.cs \
  Tests/PbxAdmin.Tests/Services/CallFlow/CallFlowServiceTests.cs
git commit -m "feat(callflow): add CallFlowService with graph building, health warnings, cross-references"
```

---

## Task 7: DI Registration + Nav Changes

**Files:**
- Modify: `Examples/PbxAdmin/Program.cs`
- Modify: `Examples/PbxAdmin/Components/Layout/MainLayout.razor`

- [ ] **Step 1: Register CallFlowService in Program.cs**

After the `DialplanEditorService` registration (around line 71), add:

```csharp
builder.Services.AddSingleton<CallFlowService>();
```

Add `using PbxAdmin.Services.CallFlow;` if not present.

- [ ] **Step 2: Add Call Flow nav link and move Dialplan**

In `MainLayout.razor`:

After `Time Conds.` nav link (line 34), add:
```razor
<NavLink href="/call-flow" class="nav-item">@L["Nav_CallFlow"]</NavLink>
```

Move the Dialplan link from PBX Management (line 35) to System section (after Console, line 48):
```razor
<NavLink href="/dialplan" class="nav-item">@L["Nav_AdvDialplan"]</NavLink>
```

- [ ] **Step 3: Add localization keys**

In `SharedStrings.resx` (EN):
- `Nav_CallFlow` = "Call Flow"
- `Nav_AdvDialplan` = "Adv. Dialplan"
- `CF_Title` = "Call Flow"
- `CF_Heading` = "Call Flow Overview"
- `CF_ActiveDids` = "Active DIDs"
- `CF_TcStatus` = "Time Conditions"
- `CF_QueueHealth` = "Queue Health"
- `CF_TrunkStatus` = "Trunk Status"
- `CF_Open` = "open"
- `CF_Closed` = "closed"
- `CF_Override` = "override"
- `CF_Healthy` = "healthy"
- `CF_Empty` = "empty"
- `CF_Up` = "registered"
- `CF_Down` = "down"
- `CF_HealthWarnings` = "Health Warnings"
- `CF_NoWarnings` = "All systems healthy. No issues detected."
- `CF_InboundFlow` = "Inbound Call Flow"
- `CF_SearchDids` = "Search DIDs..."
- `CF_SelectDid` = "Select an inbound route to view its call flow."
- `CF_NoDids` = "No inbound routes configured."
- `CF_ShowDialplan` = "Show dialplan"
- `CF_HideDialplan` = "Hide dialplan"
- `CF_EditEntity` = "Edit"
- `CF_FlowFor` = "Call Flow for"

In `SharedStrings.es.resx` (ES) — same keys with Spanish translations.

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build Asterisk.Sdk.slnx && dotnet test Tests/PbxAdmin.Tests/`
Expected: 0 warnings, all pass

- [ ] **Step 5: Commit**

```bash
git add Examples/PbxAdmin/Program.cs \
  Examples/PbxAdmin/Components/Layout/MainLayout.razor \
  Examples/PbxAdmin/Resources/SharedStrings.resx \
  Examples/PbxAdmin/Resources/SharedStrings.es.resx
git commit -m "feat(callflow): register service, add nav link, move dialplan to System"
```

---

## Task 8: Call Flow Page — Dashboard + Two-Panel

**Files:**
- Create: `Examples/PbxAdmin/Components/Pages/CallFlow.razor`
- Create: `Tests/PbxAdmin.Tests/Components/CallFlowPageTests.cs`

> **Zone 1 (Call Tracer header):** Omitted in Phase 1. The page starts with Zone 2 (dashboard). Phase 2 will add the tracer header above the dashboard.

- [ ] **Step 1: Create the page with zones 2 and 3**

Page structure:
- `@page "/call-flow"`
- Inject: `CallFlowService`, `ISelectedServerService`, `IStringLocalizer<SharedStrings>`, `IToastService`
- Zone 2: KPI cards row (DIDs, TCs, Queues, Trunks) using data from `CallFlowService`
- Zone 2b: Health warnings row (if any)
- Zone 3: Two-panel layout — left: DID list with search, right: horizontal flow for selected DID

Follow existing PbxAdmin patterns:
- `_loading`, `OnInitializedAsync`, `StateHasChanged()`
- KPI cards: reuse `.kpi-row`, `.kpi-card` CSS pattern from Dialplan.razor
- Two-panel: reuse `.dp-layout` pattern (grid with 300px left, 1fr right)
- Health warning cards: colored borders (red for error, yellow for warning, blue for info)

Flow cards in right panel:
- Each card shows: entity type label (colored), name, relevant state info
- Cards connected by `→` arrows
- TC cards branch vertically into Open/Closed sub-cards
- IVR cards show digit options as a small list
- Click on any card navigates to its edit URL
- "Show dialplan" expandable per card shows `node.DialplanLines` in `<pre>` block

Use inline `<style>` for page-specific CSS (same pattern as existing pages).

- [ ] **Step 2: Build and verify**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Write bUnit tests for the page**

Create `Tests/PbxAdmin.Tests/Components/CallFlowPageTests.cs` with:
- `CallFlowPage_ShouldRenderKpiCards` — page renders with 4 KPI cards showing counts
- `CallFlowPage_ShouldRenderHealthWarnings` — warnings display with correct severity badges
- `CallFlowPage_ShouldRenderDidList` — left panel shows DID routes
- `CallFlowPage_ShouldShowFlowOnSelect` — selecting a DID renders flow cards in right panel
- `CallFlowPage_ShouldShowNoWarningsMessage_WhenHealthy` — "All systems healthy" when no warnings

Register mocked `CallFlowService` (returns predefined `CallFlowGraph` with 2 DID flows and 1 warning) and other required services (`ISelectedServerService`, `IStringLocalizer`, `IToastService`, `IConfigOperationState`).

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "CallFlowPage"`
Expected: ALL PASS

- [ ] **Step 5: Commit**

```bash
git add Examples/PbxAdmin/Components/Pages/CallFlow.razor \
  Tests/PbxAdmin.Tests/Components/CallFlowPageTests.cs
git commit -m "feat(callflow): add Call Flow page with dashboard, health warnings, inbound flow"
```

---

## Task 9: Cache Invalidation Integration

**Files:**
- Modify: `Examples/PbxAdmin/Services/RouteService.cs`
- Modify: `Examples/PbxAdmin/Services/TimeConditionService.cs`
- Modify: `Examples/PbxAdmin/Services/IvrMenuService.cs`

> **Pattern:** Uses nullable constructor parameter (`CallFlowService? callFlowService = null`) for backward compatibility. In production DI, all singletons are resolved — the null default only protects existing unit tests that construct services manually. If tests start constructing these services, they should pass `null` or a mock. This is the same pattern used for `DialplanDiscoveryService?` in `DialplanRegenerator` and `AsteriskMonitorService`.

- [ ] **Step 1: Add nullable CallFlowService parameter to RouteService**

Add `CallFlowService? callFlowService = null` as constructor parameter. After each successful create/update/delete that calls `_regenerator.RegenerateAsync`, add:

```csharp
callFlowService?.InvalidateCache(serverId);
```

- [ ] **Step 2: Same for TimeConditionService**

Add nullable `CallFlowService?` constructor parameter. After create/update/delete, call `InvalidateCache`.

- [ ] **Step 3: Same for IvrMenuService**

Add nullable `CallFlowService?` constructor parameter. After create/update/delete, call `InvalidateCache`.

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build Asterisk.Sdk.slnx && dotnet test Tests/PbxAdmin.Tests/`
Expected: 0 warnings, all pass. Existing tests should still pass since the parameter is nullable with default.

- [ ] **Step 5: Commit**

```bash
git add Examples/PbxAdmin/Services/RouteService.cs \
  Examples/PbxAdmin/Services/TimeConditionService.cs \
  Examples/PbxAdmin/Services/IvrMenuService.cs
git commit -m "feat(callflow): integrate cache invalidation in Route, TC, IVR services"
```

---

## Task 10: Full Test Run + Fix Regressions

**Files:** Any files that need fixing

- [ ] **Step 1: Run full PbxAdmin test suite**

Run: `dotnet test Tests/PbxAdmin.Tests/`
Expected: ALL PASS

- [ ] **Step 2: Run full solution build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Fix any regressions**

If bUnit tests fail due to missing `CallFlowService` DI registration, add mock registrations to affected test constructors.

- [ ] **Step 4: Commit fixes (if any)**

```bash
git add Tests/PbxAdmin.Tests/
git commit -m "test: fix regressions from call flow integration"
```
