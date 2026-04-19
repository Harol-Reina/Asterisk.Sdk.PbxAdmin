# Call Flow Phase 3 — Routes Outbound UX + Cross-References

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Improve outbound routes readability (pattern humanizer, trunk health, failover labels, number manipulation preview) and add cross-references ("Used by: ...") across TC, IVR, Queues, Extensions, and Routes pages.

**Architecture:** Cross-references come from `CallFlowService.GetReferencesForAsync()` already implemented in Phase 1. The outbound improvements are purely UI changes in `Routes.razor` using `DialPatternHumanizer` and `NumberManipulator` from Phase 1. Inline flow summary in inbound routes uses the `CallFlowGraph` already available.

**Tech Stack:** .NET 10, Blazor Server, xUnit 2.9.3, FluentAssertions 7.1.0. AOT-safe.

**Spec:** `docs/superpowers/specs/2026-03-23-call-flow-ux-design.md` (Sections 5, 6)

---

## File Map

### Modified Files

| File | Change |
|------|--------|
| `Examples/PbxAdmin/Components/Pages/Routes.razor` | Outbound: pattern humanizer, trunk health, failover labels, manipulation preview. Inbound: inline flow summary. |
| `Examples/PbxAdmin/Components/Pages/TimeConditions.razor` | "Used by" cross-references |
| `Examples/PbxAdmin/Components/Pages/IvrMenus.razor` | "Referenced by" cross-references |
| `Examples/PbxAdmin/Resources/SharedStrings.resx` | Add XRef_* keys (EN) |
| `Examples/PbxAdmin/Resources/SharedStrings.es.resx` | Add XRef_* keys (ES) |

---

## Task 1: Outbound Routes UX Improvements

**Files:**
- Modify: `Examples/PbxAdmin/Components/Pages/Routes.razor`

- [ ] **Step 1: Read Routes.razor and understand outbound table structure**

Read the full file. The outbound table has columns: Priority, Name, Dial Pattern, Trunks, Prepend/Prefix, Status, Actions.

- [ ] **Step 2: Add pattern humanizer below dial patterns**

Add `@using PbxAdmin.Services.CallFlow` at top.

In the outbound table, below each dial pattern `<code>`:
```razor
<td>
    <code>@route.DialPattern</code>
    @{ var desc = DialPatternHumanizer.Describe(route.DialPattern ?? ""); }
    @if (desc != route.DialPattern)
    {
        <div class="text-muted" style="font-size:0.75rem;">@desc</div>
    }
</td>
```

- [ ] **Step 3: Add trunk health dots and failover labels**

Replace the flat trunk badge list with numbered sequence + health dots.

Need trunk status: load trunks via `TrunkSvc.GetTrunksAsync(serverId)` in `LoadRoutes()` and store as `Dictionary<string, bool> _trunkStatus` (name → isRegistered).

```razor
<td>
    @foreach (var trunk in route.Trunks.OrderBy(t => t.Sequence))
    {
        var isUp = _trunkStatus.TryGetValue(trunk.TrunkName, out var up) && up;
        <span class="badge badge-blue" style="margin-right: 0.25rem;">
            @(trunk.Sequence + 1). @trunk.TrunkName
            <span class="trunk-dot @(isUp ? "dot-green" : "dot-red")"></span>
        </span>
        @if (trunk.Sequence < route.Trunks.Count - 1) { <span class="text-muted">→</span> }
    }
</td>
```

- [ ] **Step 4: Improve number manipulation preview**

Replace the `P: / X:` notation with before/after preview using `NumberManipulator.Preview`:

```razor
<td>
    @{
        var prepend = _outboundConfigs.TryGetValue(route.Id, out var cfg) ? cfg.Prepend : null;
        var prefix = cfg?.Prefix;
        var preview = NumberManipulator.Preview(prefix, prepend);
    }
    @if (!string.IsNullOrEmpty(preview))
    {
        <code style="font-size:0.8rem;">@preview</code>
    }
    else
    {
        <span class="text-muted">—</span>
    }
</td>
```

- [ ] **Step 5: Add CSS for trunk health dots**

```css
.trunk-dot { display: inline-block; width: 6px; height: 6px; border-radius: 50%; margin-left: 4px; vertical-align: middle; }
.dot-green { background: #4ade80; }
.dot-red { background: #f87171; }
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 7: Commit**

```bash
git add Examples/PbxAdmin/Components/Pages/Routes.razor
git commit -m "feat(routes): add pattern humanizer, trunk health, failover labels, manipulation preview"
```

---

## Task 2: Inbound Routes — Inline Flow Summary

**Files:**
- Modify: `Examples/PbxAdmin/Components/Pages/Routes.razor`

- [ ] **Step 1: Add CallFlowService dependency**

Add `@inject CallFlowService FlowSvc` and `@using PbxAdmin.Services.CallFlow` at top.

In `LoadRoutes()`, after loading routes, also load the call flow graph:
```csharp
_graph = await FlowSvc.BuildFlowAsync(serverId);
```

Add field: `private CallFlowGraph? _graph;`

- [ ] **Step 2: Add inline flow summary below each inbound route**

In the inbound table, after the destination cell, add a small summary row or expand the destination cell:

```razor
<td>
    <span class="badge @GetDestBadge(route.DestinationType)">@GetDestTypeLabel(route.DestinationType)</span>
    @(route.DestinationLabel ?? route.Destination)
    @{ var flow = GetFlowSummary(route); }
    @if (flow is not null)
    {
        <div class="route-flow-summary">@flow</div>
    }
</td>
```

```csharp
private string? GetFlowSummary(InboundRouteViewModel route)
{
    if (_graph is null) return null;
    var did = _graph.InboundFlows.FirstOrDefault(d =>
        string.Equals(d.RouteName, route.Name, StringComparison.OrdinalIgnoreCase));
    if (did?.Destination is null) return null;

    return did.Destination switch
    {
        TimeConditionNode tc => $"→ TC {tc.Label}: Open → {DescribeNode(tc.OpenBranch)} / Closed → {DescribeNode(tc.ClosedBranch)}",
        IvrNode ivr => $"→ IVR {ivr.Label} ({ivr.Options.Count} options)",
        QueueNode q => $"→ Queue {q.Label}",
        ExtensionNode ext => $"→ Ext {ext.Number}",
        _ => null
    };
}

private static string DescribeNode(CallFlowNode? node) => node switch
{
    QueueNode q => $"Queue {q.Label}",
    ExtensionNode ext => $"Ext {ext.Number}",
    IvrNode ivr => $"IVR {ivr.Label}",
    VoicemailNode vm => $"VM {vm.Extension}",
    HangupNode => "Hangup",
    _ => "?"
};
```

- [ ] **Step 3: Add CSS**

```css
.route-flow-summary { font-size: 0.75rem; color: var(--text-muted); margin-top: 0.15rem; }
```

- [ ] **Step 4: Build and run tests**

Run: `dotnet build Asterisk.Sdk.slnx && dotnet test Tests/PbxAdmin.Tests/`
Expected: 0 warnings, all pass

- [ ] **Step 5: Commit**

```bash
git add Examples/PbxAdmin/Components/Pages/Routes.razor
git commit -m "feat(routes): add inline flow summary for inbound routes"
```

---

## Task 3: Cross-References — TC, IVR, Queues

**Files:**
- Modify: `Examples/PbxAdmin/Components/Pages/TimeConditions.razor`
- Modify: `Examples/PbxAdmin/Components/Pages/IvrMenus.razor`
- Modify: `Examples/PbxAdmin/Resources/SharedStrings.resx`
- Modify: `Examples/PbxAdmin/Resources/SharedStrings.es.resx`

- [ ] **Step 1: Add localization keys**

EN:
- `XRef_UsedBy` = "Used by:"
- `XRef_ReferencedBy` = "Referenced by:"
- `XRef_NotReferenced` = "Not referenced by any route"
- `XRef_ReceivesFrom` = "Receives calls from:"

ES:
- `XRef_UsedBy` = "Usado por:"
- `XRef_ReferencedBy` = "Referenciado por:"
- `XRef_NotReferenced` = "No referenciado por ninguna ruta"
- `XRef_ReceivesFrom` = "Recibe llamadas de:"

- [ ] **Step 2: Add cross-references to TimeConditions.razor**

Add `@inject CallFlowService FlowSvc` and `@using PbxAdmin.Services.CallFlow` + `@using PbxAdmin.Models`.

In `LoadConditions()`, also load references for each TC:
```csharp
_graph = await FlowSvc.BuildFlowAsync(serverId);
```

Below each TC card body, add:
```razor
<div class="xref-line">
    @{ var refs = GetRefsForTc(tc.Name); }
    @if (refs.Count > 0)
    {
        <span class="text-muted" style="font-size:0.8rem;">@L["XRef_UsedBy"] @string.Join(", ", refs.Select(r => r.SourceLabel))</span>
    }
    else
    {
        <span class="xref-warning">@L["XRef_NotReferenced"]</span>
    }
</div>
```

Helper:
```csharp
private CallFlowGraph? _graph;

private List<CrossReference> GetRefsForTc(string name)
{
    if (_graph is null) return [];
    return CallFlowService.GetReferencesFor(_graph, "time_condition", name);
}
```

- [ ] **Step 3: Add cross-references to IvrMenus.razor**

Same pattern. Inject `CallFlowService`, load graph, show "Referenced by:" below each IVR card.

Read the file first to understand its structure (card grid layout).

Helper:
```csharp
private List<CrossReference> GetRefsForIvr(string name)
{
    if (_graph is null) return [];
    return CallFlowService.GetReferencesFor(_graph, "ivr", name);
}
```

- [ ] **Step 4: Add CSS for cross-reference styling**

```css
.xref-line { padding: 0.25rem 1rem 0.5rem; font-size: 0.8rem; }
.xref-warning { color: #fbbf24; font-size: 0.8rem; font-style: italic; }
```

Add to both TimeConditions.razor and IvrMenus.razor style blocks.

- [ ] **Step 5: Build and run tests**

Run: `dotnet build Asterisk.Sdk.slnx && dotnet test Tests/PbxAdmin.Tests/`
Expected: 0 warnings, all pass

- [ ] **Step 6: Commit**

```bash
git add Examples/PbxAdmin/Components/Pages/TimeConditions.razor \
  Examples/PbxAdmin/Components/Pages/IvrMenus.razor \
  Examples/PbxAdmin/Resources/SharedStrings.resx \
  Examples/PbxAdmin/Resources/SharedStrings.es.resx
git commit -m "feat(xref): add cross-references to Time Conditions and IVR Menus pages"
```

---

## Task 4: Full Test Run + Fix Regressions

**Files:** Any files that need fixing

- [ ] **Step 1: Run full test suite**

Run: `dotnet test Tests/PbxAdmin.Tests/`
Expected: ALL PASS

- [ ] **Step 2: Run full solution build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Fix any regressions**

- [ ] **Step 4: Commit fixes (if any)**
