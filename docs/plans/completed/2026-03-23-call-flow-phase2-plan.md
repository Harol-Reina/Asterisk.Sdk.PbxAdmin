# Call Flow Phase 2 — Call Tracer + Dialplan Improvements

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the Call Tracer with step-through debugger and date/time picker, populate dialplan lines in flow nodes, improve `/dialplan` with type badges + humanization column + bidirectional links.

**Architecture:** `CallFlowService.TraceCallAsync` evaluates routes, time conditions, and IVR menus step-by-step against a given number + DateTime + override mode. Each step records the dialplan lines that would execute and the evaluation result. The `/dialplan` page gets a new "Description" column powered by `DialplanHumanizer` and type badges derived from context naming conventions.

**Tech Stack:** .NET 10, Blazor Server, xUnit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0. AOT-safe. Source-gen logging.

**Spec:** `docs/superpowers/specs/2026-03-23-call-flow-ux-design.md` (Sections 3.2, 4.2, 4.3, 4.4)

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `Tests/PbxAdmin.Tests/Services/CallFlow/CallFlowTraceTests.cs` | Tests for call tracing logic |

### Modified Files

| File | Change |
|------|--------|
| `Examples/PbxAdmin/Models/CallFlowModels.cs` | Add `CallFlowTrace`, `CallFlowTraceStep` types |
| `Examples/PbxAdmin/Services/CallFlow/CallFlowService.cs` | Add `TraceCallAsync` method, populate `DialplanLines` in graph building |
| `Examples/PbxAdmin/Components/Pages/CallFlow.razor` | Add Zone 1 (tracer header), trace results view with step debugger |
| `Examples/PbxAdmin/Components/Pages/Dialplan.razor` | Add type badges, humanization column, "View in Call Flow" links |
| `Examples/PbxAdmin/Resources/SharedStrings.resx` | Add CF_Trace* and DP_Humanized* keys (EN) |
| `Examples/PbxAdmin/Resources/SharedStrings.es.resx` | Same keys (ES) |

---

## Task 1: Add CallFlowTrace Model Types

**Files:**
- Modify: `Examples/PbxAdmin/Models/CallFlowModels.cs`

- [ ] **Step 1: Add trace types to the model file**

After `CallFlowGraph`, add:

```csharp
public sealed class CallFlowTrace
{
    public string InputNumber { get; init; } = "";
    public DateTime InputTime { get; init; }
    public string Direction { get; init; } = "";
    public string OverrideMode { get; init; } = "";
    public List<CallFlowTraceStep> Steps { get; init; } = [];
    public bool RouteFound { get; init; }
}

public sealed class CallFlowTraceStep
{
    public int StepNumber { get; init; }
    public string Description { get; init; } = "";
    public string? Evaluation { get; init; }
    public string Result { get; init; } = "";
    public string EntityType { get; init; } = "";
    public string EntityId { get; init; } = "";
    public string? EditUrl { get; init; }
    public List<string> DialplanLines { get; init; } = [];
}
```

Remove the Phase 2 deferral comment.

- [ ] **Step 2: Build and verify**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Commit**

```bash
git add Examples/PbxAdmin/Models/CallFlowModels.cs
git commit -m "feat(callflow): add CallFlowTrace and CallFlowTraceStep model types"
```

---

## Task 2: TraceCallAsync — Tests + Implementation

**Files:**
- Create: `Tests/PbxAdmin.Tests/Services/CallFlow/CallFlowTraceTests.cs`
- Modify: `Examples/PbxAdmin/Services/CallFlow/CallFlowService.cs`

- [ ] **Step 1: Write trace tests**

Use the same `BuildGraph` static method pattern — create a `TraceCall` internal static method that takes the graph + number + time + overrideMode and returns `CallFlowTrace`.

Test cases:

**Inbound traces:**
- `Trace_ShouldMatchInboundRoute_ByExactDid` — number "5551234" matches route with DID "5551234" → steps show route match + destination
- `Trace_ShouldEvaluateTimeCondition_WhenOpen` — route → TC, time is within range → steps show TC evaluation as "MATCH", follows open branch
- `Trace_ShouldEvaluateTimeCondition_WhenClosed` — route → TC, time is outside range → follows closed branch
- `Trace_ShouldRespectOverrideMode_AllOpen` — overrideMode="AllOpen", TC would be closed by schedule → forces open branch
- `Trace_ShouldRespectOverrideMode_AllClosed` — overrideMode="AllClosed", TC would be open → forces closed branch
- `Trace_ShouldRespectOverrideMode_Live` — overrideMode="Live", TC has override "OPEN" in overrides dict → follows open
- `Trace_ShouldTraverseIvrOptions` — route → IVR → shows IVR step with all digit options listed
- `Trace_ShouldReturnRouteNotFound_WhenNoMatch` — number "9999" doesn't match any route → RouteFound=false, steps has "No route found" step
- `Trace_ShouldCheckHolidays_BeforeRanges` — TC with holiday matching trace date → closed, step shows "Holiday match"
- `Trace_ShouldShowDialplanLines_PerStep` — each step has non-empty DialplanLines

**Outbound traces:**
- `Trace_ShouldMatchOutboundPattern` — number "18005551234" matches outbound pattern "_1NXXNXXXXXX" → steps show route match, number manipulation, trunk dial
- `Trace_ShouldShowNumberManipulation` — outbound route with prefix "9" and prepend "+1" → step shows before/after

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "CallFlowTrace"`
Expected: FAIL

- [ ] **Step 3: Implement TraceCallAsync**

Add to `CallFlowService`:

```csharp
public async Task<CallFlowTrace> TraceCallAsync(
    string serverId, string number, DateTime time,
    string overrideMode = "Live", CancellationToken ct = default)
```

And the internal static method:

```csharp
internal static CallFlowTrace TraceCall(
    CallFlowGraph graph,
    List<OutboundRouteConfig> outboundRoutes,
    Dictionary<string, string> tcOverrides,
    List<TimeConditionConfig> timeConditions,
    List<IvrMenuConfig> ivrMenus,
    string number, DateTime time, string overrideMode)
```

Logic:
1. Try inbound: find first DID route where pattern matches number (exact or Asterisk pattern match)
2. If inbound match: trace through destination chain step by step
   - Each TC: evaluate override mode first, then holidays, then time ranges
   - Each IVR: log the menu with all options (user doesn't "press" digits in simulation — just show the tree)
   - Each queue/extension: terminal step
3. If no inbound: try outbound pattern match
   - If match: show number manipulation step, then trunk chain
4. If neither: RouteFound = false

For Asterisk pattern matching (`_NXXNXXXXXX`): implement a simple `MatchesAsteriskPattern(string pattern, string number)` helper. Patterns use: `_` prefix, `X`=[0-9], `N`=[2-9], `Z`=[1-9], `.`=1+ chars, `!`=0+ chars.

For dialplan line generation per step: use `DialplanGenerator.ResolveDestination` for the Goto line, and construct the GotoIfTime/Set/GotoIf lines from TC config data.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "CallFlowTrace"`
Expected: ALL PASS

- [ ] **Step 5: Also populate DialplanLines during graph building**

In the existing `BuildGraph` method, when creating each node, populate `DialplanLines` using `DialplanGenerator.ResolveDestination` for the relevant context/extension. This makes the "Show dialplan" toggle in the existing Call Flow page actually show data.

- [ ] **Step 6: Run full test suite**

Run: `dotnet test Tests/PbxAdmin.Tests/`
Expected: ALL PASS

- [ ] **Step 7: Commit**

```bash
git add Examples/PbxAdmin/Services/CallFlow/CallFlowService.cs \
  Tests/PbxAdmin.Tests/Services/CallFlow/CallFlowTraceTests.cs
git commit -m "feat(callflow): add TraceCallAsync with TC evaluation, IVR traversal, pattern matching"
```

---

## Task 3: Call Tracer UI — Zone 1 Header + Trace Results

**Files:**
- Modify: `Examples/PbxAdmin/Components/Pages/CallFlow.razor`
- Modify: `Examples/PbxAdmin/Resources/SharedStrings.resx`
- Modify: `Examples/PbxAdmin/Resources/SharedStrings.es.resx`

- [ ] **Step 1: Add localization keys**

EN keys:
- `CF_TraceNumber` = "Trace a call to:"
- `CF_TraceTime` = "At time:"
- `CF_TraceOverride` = "Override mode:"
- `CF_TraceBtn` = "Trace"
- `CF_TraceClear` = "Clear"
- `CF_OverrideLive` = "Live (current)"
- `CF_OverrideNone` = "No overrides"
- `CF_OverrideAllOpen` = "All open"
- `CF_OverrideAllClosed` = "All closed"
- `CF_TraceResult` = "Trace Result"
- `CF_TraceNoRoute` = "No route found for this number."
- `CF_TraceInbound` = "Inbound"
- `CF_TraceOutbound` = "Outbound"
- `CF_TraceStep` = "Step"
- `CF_TraceInspect` = "Inspect dialplan"
- `CF_TraceMatched` = "Matched"
- `CF_TraceNotMatched` = "Not matched"
- `CF_TraceSkipped` = "Skipped"

ES: same keys with Spanish values.

- [ ] **Step 2: Add Zone 1 (tracer header) to CallFlow.razor**

Above Zone 2 (KPI dashboard), add:

```razor
<div class="cf-tracer">
    <div class="cf-tracer-inputs">
        <div class="cf-tracer-field">
            <label>@L["CF_TraceNumber"]</label>
            <input type="text" class="form-control" @bind="_traceNumber" placeholder="5551234" />
        </div>
        <div class="cf-tracer-field">
            <label>@L["CF_TraceTime"]</label>
            <input type="datetime-local" class="form-control" @bind="_traceTime" />
        </div>
        <div class="cf-tracer-field">
            <label>@L["CF_TraceOverride"]</label>
            <select class="form-control" @bind="_traceOverrideMode">
                <option value="Live">@L["CF_OverrideLive"]</option>
                <option value="None">@L["CF_OverrideNone"]</option>
                <option value="AllOpen">@L["CF_OverrideAllOpen"]</option>
                <option value="AllClosed">@L["CF_OverrideAllClosed"]</option>
            </select>
        </div>
        <button class="btn btn-blue" @onclick="ExecuteTrace" disabled="@string.IsNullOrWhiteSpace(_traceNumber)">@L["CF_TraceBtn"]</button>
        @if (_traceResult is not null)
        {
            <button class="btn" @onclick="ClearTrace">@L["CF_TraceClear"]</button>
        }
    </div>
</div>
```

- [ ] **Step 3: Add trace results view (replaces Zone 3 when active)**

When `_traceResult is not null`, replace the two-panel layout with trace results:

```razor
@if (_traceResult is not null)
{
    <div class="cf-trace-result">
        <h3>@L["CF_TraceResult"]: @_traceResult.InputNumber
            <span class="badge @(_traceResult.Direction == "Inbound" ? "badge-green" : "badge-blue")">
                @(_traceResult.Direction == "Inbound" ? L["CF_TraceInbound"] : L["CF_TraceOutbound"])
            </span>
        </h3>
        @if (!_traceResult.RouteFound)
        {
            <p class="cf-trace-no-route">@L["CF_TraceNoRoute"]</p>
        }
        else
        {
            @foreach (var step in _traceResult.Steps)
            {
                <div class="cf-trace-step @GetStepClass(step.Result)">
                    <div class="cf-trace-step-header">
                        <span class="cf-trace-step-num">@L["CF_TraceStep"] @step.StepNumber</span>
                        <span class="cf-trace-step-desc">@step.Description</span>
                        <span class="badge @GetResultBadge(step.Result)">@GetResultLabel(step.Result)</span>
                    </div>
                    @if (step.Evaluation is not null)
                    {
                        <div class="cf-trace-step-eval">@step.Evaluation</div>
                    }
                    @if (step.DialplanLines.Count > 0)
                    {
                        <details class="cf-trace-inspect">
                            <summary>@L["CF_TraceInspect"]</summary>
                            <pre>@string.Join("\n", step.DialplanLines)</pre>
                        </details>
                    }
                </div>
            }
        }
    </div>
}
else
{
    @* existing Zone 3 two-panel layout *@
}
```

- [ ] **Step 4: Add code section fields and methods**

```csharp
private string _traceNumber = "";
private DateTime _traceTime = DateTime.Now;
private string _traceOverrideMode = "Live";
private CallFlowTrace? _traceResult;

private async Task ExecuteTrace() { ... calls FlowSvc.TraceCallAsync ... }
private void ClearTrace() { _traceResult = null; }
private static string GetStepClass(string result) => result switch { "Matched" => "matched", "NotMatched" => "not-matched", _ => "skipped" };
private static string GetResultBadge(string result) => result switch { "Matched" => "badge-green", "NotMatched" => "badge-red", _ => "badge-muted" };
private string GetResultLabel(string result) => result switch { "Matched" => L["CF_TraceMatched"], "NotMatched" => L["CF_TraceNotMatched"], _ => L["CF_TraceSkipped"] };
```

- [ ] **Step 5: Add CSS for tracer**

```css
.cf-tracer { background: var(--card-bg); border: 1px solid var(--border); border-radius: 10px; padding: 1rem; margin-bottom: 1rem; }
.cf-tracer-inputs { display: flex; align-items: end; gap: 0.75rem; flex-wrap: wrap; }
.cf-tracer-field { display: flex; flex-direction: column; gap: 0.25rem; }
.cf-tracer-field label { font-size: 0.8rem; color: var(--text-muted); }
.cf-trace-result { background: var(--card-bg); border: 1px solid var(--border); border-radius: 10px; padding: 1.25rem; }
.cf-trace-step { border-left: 3px solid var(--border); padding: 0.75rem 1rem; margin-bottom: 0.5rem; }
.cf-trace-step.matched { border-left-color: #4ade80; }
.cf-trace-step.not-matched { border-left-color: #f87171; }
.cf-trace-step-header { display: flex; align-items: center; gap: 0.5rem; }
.cf-trace-step-num { font-weight: 700; color: var(--accent); min-width: 60px; }
.cf-trace-step-eval { font-size: 0.85rem; color: var(--text-muted); margin-top: 0.25rem; font-family: monospace; }
.cf-trace-inspect { margin-top: 0.5rem; }
.cf-trace-inspect pre { font-size: 0.8rem; background: rgba(0,0,0,0.15); padding: 0.5rem; border-radius: 4px; overflow-x: auto; }
.cf-trace-no-route { color: #f87171; font-size: 1.1rem; }
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 7: Commit**

```bash
git add Examples/PbxAdmin/Components/Pages/CallFlow.razor \
  Examples/PbxAdmin/Resources/SharedStrings.resx \
  Examples/PbxAdmin/Resources/SharedStrings.es.resx
git commit -m "feat(callflow): add Call Tracer with step-through debugger and override modes"
```

---

## Task 4: Dialplan Page — Type Badges + Humanization Column

**Files:**
- Modify: `Examples/PbxAdmin/Components/Pages/Dialplan.razor`

- [ ] **Step 1: Add type badges to context cards**

In the left panel context list, after the System/User badge, add a type badge based on context name:

```csharp
private static (string Label, string CssClass) GetContextType(string name) => name switch
{
    "from-trunk" => ("Inbound", "badge-green"),
    "outbound-routes" => ("Outbound", "badge-blue"),
    _ when name.StartsWith("tc-", StringComparison.Ordinal) => ("TC", "badge-yellow"),
    _ when name.StartsWith("ivr-", StringComparison.Ordinal) => ("IVR", "badge-purple"),
    "queues" => ("Queues", "badge-orange"),
    "default" => ("Main", "badge-muted"),
    _ => ("", "")
};
```

Display the badge next to the existing System/User badge if Label is not empty.

- [ ] **Step 2: Add humanized description column to extensions table**

In the right panel extensions table, add a "Description" column header after "Application". For each priority row, show `DialplanHumanizer.Humanize(p.Application, p.ApplicationData)`.

Add `@using PbxAdmin.Services.CallFlow` at the top.

- [ ] **Step 3: Add "View in Call Flow" link for PbxAdmin-managed contexts**

For contexts matching `from-trunk`, `outbound-routes`, `tc-*`, `ivr-*`, add a button in the context detail header:

```razor
@if (IsManagedContext(_selectedContext.Name))
{
    <a href="/call-flow" class="btn btn-sm">View in Call Flow</a>
}
```

```csharp
private static bool IsManagedContext(string name) =>
    name is "from-trunk" or "outbound-routes" ||
    name.StartsWith("tc-", StringComparison.Ordinal) ||
    name.StartsWith("ivr-", StringComparison.Ordinal);
```

- [ ] **Step 4: Add "Open in Adv. Dialplan" link in CallFlow.razor**

In CallFlow.razor, each flow card should have a secondary link "Open in Adv. Dialplan" that navigates to `/dialplan?context={contextName}`. Add query parameter support to Dialplan.razor:

In Dialplan.razor, add `[SupplyParameterFromQuery] public string? Context { get; set; }` and in `OnInitializedAsync`, if Context is set, auto-select it.

- [ ] **Step 5: Add CSS for new badges**

```css
.badge-yellow { background: #f59e0b22; color: #fbbf24; }
.badge-orange { background: #f9731622; color: #fb923c; }
.badge-purple { background: #a855f722; color: #c084fc; }
```

(These may already exist — check first, only add if missing.)

- [ ] **Step 6: Build and run all tests**

Run: `dotnet build Asterisk.Sdk.slnx && dotnet test Tests/PbxAdmin.Tests/`
Expected: 0 warnings, all pass

- [ ] **Step 7: Commit**

```bash
git add Examples/PbxAdmin/Components/Pages/Dialplan.razor \
  Examples/PbxAdmin/Components/Pages/CallFlow.razor
git commit -m "feat(dialplan): add type badges, humanization column, bidirectional Call Flow links"
```

---

## Task 5: Full Test Run + Fix Regressions

**Files:** Any files that need fixing

- [ ] **Step 1: Run full PbxAdmin test suite**

Run: `dotnet test Tests/PbxAdmin.Tests/`
Expected: ALL PASS

- [ ] **Step 2: Run full solution build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Fix any regressions**

- [ ] **Step 4: Commit fixes (if any)**

```bash
git add Tests/PbxAdmin.Tests/
git commit -m "test: fix regressions from call flow phase 2"
```
