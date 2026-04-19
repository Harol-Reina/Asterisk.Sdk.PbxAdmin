# ADR-0003: Use Blazor Server (not WASM) for the admin UI

- **Status:** Accepted
- **Date:** 2026-03-24
- **Deciders:** Harol Reina (project lead)
- **Related:** ADR-0002

## Context

PbxAdmin's core value is **real-time visibility** into an Asterisk PBX: live call lists, queue state, channel monitors, and a WebRTC softphone. The server holds persistent AMI connections to one or more Asterisk instances, consumes high-frequency events, and has to broadcast state changes into the UI in sub-second time. The admin audience is small (operators, not end users), so per-connection server cost is acceptable.

Blazor offers two hosting models: WebAssembly (client-rendered, REST/SignalR for data) and Server (server-rendered with a SignalR circuit per user).

## Decision

We will host the admin UI as **Blazor Server** with a persistent SignalR circuit per user, driven directly by AMI event handlers on the server. The SIP.js-based softphone is the only client-side component, because WebRTC must run in the browser.

## Consequences

- **Positive:** AMI events push directly into the UI with no intermediate REST layer; a single authoritative state model lives on the server.
- **Positive:** No `HttpClient`-vs-SignalR duplication, no CORS, no client-side state reconciliation with server truth.
- **Positive:** Fits AOT: everything runs server-side; we avoid the WASM AOT tooling, which is less mature and produces much larger payloads.
- **Negative:** Scaling is vertical — each user holds a circuit, so very large tenant counts push you toward session affinity or sharding. Acceptable for the admin audience.
- **Negative:** Connection drops (laptop lid, network blip) end the circuit; we had to add a POST-based login endpoint to keep auth out of the circuit lifecycle (see `project_login_fix`).
- **Trade-off:** We accept a heavier server per user in exchange for a dramatically simpler data-flow model.

## Alternatives considered

- **Blazor WebAssembly + REST API:** rejected — doubles the surface area (API contracts on both sides), and real-time updates would need a parallel SignalR hub anyway, negating the only win.
- **React/Vue SPA + REST/WebSocket API:** rejected — same duplication as WASM, plus a second toolchain (Node) and a separate deploy artifact. The ecosystem is .NET-first; mixing a JS frontend would fragment the team.
- **Server-rendered MVC/Razor Pages:** rejected — no real-time story without bolting on SignalR manually, and no component model for the dense operator UI this project needs.
