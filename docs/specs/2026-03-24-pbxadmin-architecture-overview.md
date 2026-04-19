# PbxAdmin — Architecture Overview

**Created:** 2026-03-24
**Version:** 1.8.0 (Docker), consumes Asterisk.Sdk 1.5.0 (NuGet)
**Repository:** https://github.com/Harol-Reina/Asterisk.Sdk.PbxAdmin

---

## What Was Built

### Core Platform (Phases 1-4, completed before 2026-03-22)
- 44 Blazor Server pages across 11 functional domains
- Real-time monitoring: calls, queues, channels, agents (2s refresh)
- Configuration CRUD: extensions, trunks, routes, IVR, time conditions, queues, conferences, recordings, MoH, feature codes, parking, voicemail
- Dual config mode: File-based (extensions.conf via AMI) + Realtime (PostgreSQL via Dapper)
- Multi-server support with independent config modes per server
- WebRTC softphone (SIP.js) with DTMF, ringback, hold, mute, transfer
- Localization: English + Spanish (800+ keys)
- Session Engine integration: call tracking, timeline, ladder diagrams

### Dialplan Discovery & Editor (2026-03-22)
- DialplanDiscoveryService: AMI ShowDialplan-based context browser with 5min TTL cache
- DialplanEditorService: dual File/Realtime persistence, circular include detection
- Context dropdowns in ExtensionEdit and TrunkEdit (replaces free-text input)
- Default context renamed from 'from-internal' to 'default'

### Call Flow & UX Improvements (2026-03-23, 3 phases)

**Phase 1 — Foundation:**
- CallFlowService: graph building from routes/TC/IVR/queues/extensions
- Health warnings P1: broken refs, empty queues, TC overrides, trunk down
- DialplanHumanizer: translate Asterisk apps to human-readable text
- DialPatternHumanizer: translate dial patterns to descriptions
- NumberManipulator: prepend/prefix number transformation
- /call-flow page: KPI dashboard + two-panel inbound flow visualization

**Phase 2 — Call Tracer:**
- TraceCallAsync: step-through debugger with date/time picker
- Override modes: Live, None, AllOpen, AllClosed
- Asterisk pattern matching (_NXZ. syntax)
- /dialplan improved: type badges, humanization column, bidirectional links

**Phase 3 — Routes & Cross-refs:**
- Outbound routes UX: pattern humanizer, trunk health dots, failover labels
- Cross-references in TC and IVR pages ("Used by:", "Referenced by:")
- Inline flow summary in inbound routes
- Orphan warnings ("Not referenced")

### Health Warnings P2 (2026-03-23)
- Overlapping outbound patterns detection
- IVR loop detection (self-loops and indirect cycles)
- TC without ranges (always closed)
- Unregistered extension destinations

### Docker & Audio Infrastructure (2026-03-22/23)
- Asterisk core + extra sounds (English + Spanish) in Dockerfiles
- PJSIP endpoint fixes: direct_media=no, rtp_symmetric=yes for Docker
- PSTN emulator: 10 call scenarios with realistic durations
- Softphone: ringback tone (WAV blob), DTMF dial tones, volume control

### Spanish IVR Demo (2026-03-23/24)
- 4 IVR menus: empresa (main), ventas, soporte, facturacion
- 5 virtual agents: Maria, Carlos, Ana, Pedro, Lucia (Local channels)
- 6 queues with virtual agent members
- 15 TTS audio files (espeak-ng generated, 8kHz WAV)
- Cross-server access: ext 200 from file server via trunk
- RealtimeDialplanProvider fix: writes to extensions.conf via AMI + DB

### Critical Fixes (2026-03-23/24)
- RealtimeDialplanProvider: dual write (DB + extensions.conf via AMI)
- PbxConfigManager.CreateSection: retry without delete for new sections
- IvrMenuService: root menu detection with mutual back-references
- AsteriskMonitorService: dialplan regeneration on startup
- Docker: writable extensions.conf for realtime server

---

## Test Coverage

| Category | Count | Framework |
|----------|-------|-----------|
| bUnit unit tests | 432 | xUnit + FluentAssertions + NSubstitute |
| Playwright E2E | 92 | Playwright + xUnit |
| **Total** | **524** | |

---

## Product Roadmap

### v1 (Current — SDK Demo/Showcase)
Complete. 44 pages, Call Flow with Tracer debugger, health warnings P1+P2, cross-references, Spanish IVR demo with virtual agents, WebRTC softphone.

### v2 (Future — SMB Deployable)
- CDR persistent in PostgreSQL + search/filter page
- Backup/Restore: JSON export/import of configuration
- 2 roles: Admin (full access) + Operator (dashboards + call control only)
- Basic reports: calls/day, queue SLA, top agents

### v3 (Future — Enterprise/SaaS)
- Full RBAC with user DB, custom roles, queue-scoped permissions
- Audit log: who changed what, when, old/new values
- REST API + webhooks for external integrations
- Email alerts (trunk down, queue empty, TC override forgotten)
- Historical reports: trends, drill-down, scheduled export
- Setup wizard, bulk operations, HA guidance

---

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Asterisk.Sdk.Hosting | 1.5.0 | AMI, AGI, ARI, Live, DI registration |
| Asterisk.Sdk.Sessions | 1.5.0 | Call session tracking, domain events |
| Npgsql | 9.0.x | PostgreSQL driver |
| Dapper | 2.1.x | Micro-ORM for DB queries |
| Serilog | 9.0.x | Structured logging |
| SIP.js | 0.21.2 | WebRTC softphone (client-side) |
