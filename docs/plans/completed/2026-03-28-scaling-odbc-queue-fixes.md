# ODBC Pool + Queue Timeout Scaling Fixes

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the two root causes behind all four 200-agent load test problems: ODBC pool exhaustion causing PJSIP distributor deadlock, and queue timeout causing channel accumulation beyond the scheduler's tracking window.

**Architecture:** Three Asterisk config changes (ODBC pool size, sorcery memory cache, queue timeout) plus one Docker Compose change (PostgreSQL max_connections). All are configuration-only — no C# code changes. The sorcery cache intercepts PJSIP endpoint/auth/AOR lookups and serves them from memory after first DB load, reducing ODBC query volume by ~70-80%.

**Tech Stack:** Asterisk 22 configuration (res_odbc, sorcery, extensions.conf), Docker Compose, PostgreSQL 17

---

## File Structure

| File | Change | Purpose |
|------|--------|---------|
| `docker/asterisk-config-realtime/res_odbc.conf` | Modify | Increase max_connections 30 → 100 |
| `docker/docker-compose.pbxadmin.yml` | Modify | PostgreSQL max_connections 100 → 200 |
| `docker/asterisk-config-realtime/sorcery.conf` | Modify | Add memory cache for endpoints/auths/AORs |
| `docker/asterisk-config-realtime/modules.conf` | Modify | Preload res_sorcery_memory_cache.so |
| `docker/asterisk-config-realtime/extensions.conf` | Modify | Loadtest queue timeout 300 → 45 |

---

### Task 1: Increase ODBC Connection Pool

**Files:**
- Modify: `docker/asterisk-config-realtime/res_odbc.conf`

- [ ] **Step 1: Edit res_odbc.conf**

Change `max_connections` from 30 to 100. This is the Asterisk ODBC connection pool size. When exhausted, PJSIP distributor threads block indefinitely (`ast_cond_wait` with no timeout in `res_odbc.c`), causing a SIP processing deadlock cascade.

Replace the entire file content with:

```ini
[asterisk]
enabled => yes
dsn => asterisk-connector
pre-connect => yes
max_connections => 100
```

- [ ] **Step 2: Commit**

```bash
git add docker/asterisk-config-realtime/res_odbc.conf
git commit -m "fix(docker): increase ODBC pool from 30 to 100 connections

Under 200-agent load, the 30-connection pool saturated (30/30),
blocking PJSIP distributor threads indefinitely. Each SIP REGISTER
triggers ~5 DB queries and each INVITE ~7-9 queries without sorcery
caching. 100 connections provides headroom for burst registration
events (200 agents × 5 queries)."
```

---

### Task 2: Increase PostgreSQL max_connections

**Files:**
- Modify: `docker/docker-compose.pbxadmin.yml`

The PostgreSQL default is 100 connections. With ODBC pool at 100 + PbxAdmin Dapper connections + admin tools, we need more headroom.

- [ ] **Step 1: Add command override to postgres service**

In `docker/docker-compose.pbxadmin.yml`, add a `command` line to the `postgres` service, right after the `image` line:

```yaml
  postgres:
    image: postgres:17-alpine
    command: ["postgres", "-c", "max_connections=200"]
    container_name: demo-postgres
```

- [ ] **Step 2: Commit**

```bash
git add docker/docker-compose.pbxadmin.yml
git commit -m "fix(docker): increase PostgreSQL max_connections to 200

ODBC pool is now 100 connections. PostgreSQL default of 100 leaves
no headroom for PbxAdmin Dapper queries and admin tools."
```

---

### Task 3: Add Sorcery Memory Cache

**Files:**
- Modify: `docker/asterisk-config-realtime/sorcery.conf`
- Modify: `docker/asterisk-config-realtime/modules.conf`

The sorcery memory cache intercepts PJSIP object lookups and serves endpoints, auths, and AORs from memory after the first database load. This eliminates ~70-80% of ODBC queries during call processing. Contacts remain in astdb (they change on every REGISTER).

- [ ] **Step 1: Edit modules.conf — preload cache module**

In `docker/asterisk-config-realtime/modules.conf`, add `res_sorcery_memory_cache.so` to the preload block, right after the existing `res_sorcery_memory.so` line:

```ini
preload = res_sorcery_memory_cache.so
```

The full preload block should look like:

```ini
preload = res_odbc.so
preload = res_config_odbc.so
preload = res_sorcery_config.so
preload = res_sorcery_memory.so
preload = res_sorcery_memory_cache.so
preload = res_sorcery_realtime.so
```

- [ ] **Step 2: Edit sorcery.conf — add cache layers**

Replace the entire `docker/asterisk-config-realtime/sorcery.conf` with:

```ini
[res_pjsip]
; Cached PJSIP objects — first lookup hits DB, subsequent reads served from memory.
; object_lifetime_maximum=900 (15 min) forces periodic refresh from DB.
; Cache eliminates ~70-80% of ODBC queries during call processing.
endpoint=cache,sorcery_memory_cache,pjsip_endpoint_cache
endpoint=realtime,ps_endpoints

auth=cache,sorcery_memory_cache,pjsip_auth_cache
auth=realtime,ps_auths

aor=cache,sorcery_memory_cache,pjsip_aor_cache
aor=realtime,ps_aors

; Contacts change on every REGISTER — no cache, use astdb as before
contact=astdb,registrator

[res_pjsip_endpoint_identifier_ip]
identify=realtime,ps_endpoint_id_ips

; Cache tuning — 1000 objects covers up to 500 endpoints (endpoint + auth + AOR)
; with generous headroom for trunks and internal objects.
[pjsip_endpoint_cache]
type=sorcery_memory_cache_config
maximum_objects=1000
object_lifetime_maximum=900
expire_on_reload=yes

[pjsip_auth_cache]
type=sorcery_memory_cache_config
maximum_objects=1000
object_lifetime_maximum=900
expire_on_reload=yes

[pjsip_aor_cache]
type=sorcery_memory_cache_config
maximum_objects=1000
object_lifetime_maximum=900
expire_on_reload=yes
```

Key design decisions:
- `maximum_objects=1000` — enough for 300+ endpoints with headroom for trunks
- `object_lifetime_maximum=900` (15 min) — objects refresh from DB periodically so changes propagate
- `expire_on_reload=yes` — `module reload res_pjsip.so` flushes the cache immediately
- Contacts are NOT cached — they change on every SIP REGISTER

- [ ] **Step 3: Commit**

```bash
git add docker/asterisk-config-realtime/sorcery.conf docker/asterisk-config-realtime/modules.conf
git commit -m "feat(docker): add sorcery memory cache for PJSIP objects

Caches endpoints, auths, and AORs in memory after first DB lookup.
Reduces ODBC query volume by ~70-80% during call processing.
Each SIP REGISTER was triggering ~5 DB queries and each INVITE ~7-9.
With caching, most lookups are served from memory.

Cache config: 1000 objects, 15-min TTL, expire-on-reload for
immediate propagation after module reload."
```

---

### Task 4: Reduce Loadtest Queue Timeout

**Files:**
- Modify: `docker/asterisk-config-realtime/extensions.conf`

The loadtest queue has a 300-second (5 min) timeout. When calls can't find an available agent, they sit in the queue consuming channels while the scheduler has already released their slot (37s). This causes channel accumulation far beyond the target concurrent calls. Reduce to 45s (slightly longer than the scheduler's 37s slot cycle).

- [ ] **Step 1: Edit extensions.conf — loadtest queue in [default] context**

In `docker/asterisk-config-realtime/extensions.conf`, find line 27:

```
same => n,Queue(loadtest,,,,300)
```

Change to:

```
same => n,Queue(loadtest,,,,45)
```

- [ ] **Step 2: Edit extensions.conf — loadtest queue in [queues] context**

In the same file, find the `[queues]` context (near line 263):

```
exten = loadtest,1,Answer
same = n,Queue(loadtest,,,,300)
same = n,Hangup
```

Change the Queue line to:

```
same = n,Queue(loadtest,,,,45)
```

- [ ] **Step 3: Verify no other loadtest queue references have 300s timeout**

Run: `grep -n "Queue(loadtest" docker/asterisk-config-realtime/extensions.conf`

Expected output should show exactly 2 lines, both with timeout 45:
```
27:same => n,Queue(loadtest,,,,45)
264:same = n,Queue(loadtest,,,,45)
```

- [ ] **Step 4: Commit**

```bash
git add docker/asterisk-config-realtime/extensions.conf
git commit -m "fix(docker): reduce loadtest queue timeout from 300s to 45s

The scheduler tracks call slots via Task.Delay(37s). Calls that
can't find an available agent sat in the queue for up to 300s,
consuming Asterisk channels while the scheduler had already released
their slot and generated replacement calls. This caused channel
count to grow to 755 (vs 320 expected for 160 target concurrent).

45s gives a small buffer beyond the 37s slot cycle. Calls that
can't be answered within 45s are dropped, keeping channel count
aligned with the scheduler's tracking."
```

---

### Task 5: Validate with Docker Rebuild

- [ ] **Step 1: Rebuild the Docker stack**

```bash
cd docker && docker compose -f docker-compose.pbxadmin.yml down -v --rmi local
docker compose -f docker-compose.pbxadmin.yml build --no-cache
docker compose -f docker-compose.pbxadmin.yml up -d
```

Wait for all services to be healthy (~30s).

- [ ] **Step 2: Verify ODBC pool size**

```bash
docker exec demo-pbx-realtime asterisk -rx "odbc show"
```

Expected: `Number of active connections: X (out of 100)`

- [ ] **Step 3: Verify sorcery cache is active**

```bash
docker exec demo-pbx-realtime asterisk -rx "sorcery memory cache show pjsip_endpoint_cache"
```

Expected: Cache info output showing `Maximum Object Count: 1000` and `Object Lifetime Maximum: 900`

- [ ] **Step 4: Verify PostgreSQL max_connections**

```bash
docker exec demo-postgres psql -U asterisk -c "SHOW max_connections;"
```

Expected: `200`

- [ ] **Step 5: Verify queue timeout**

```bash
docker exec demo-pbx-realtime asterisk -rx "dialplan show 105@default"
```

Expected: Queue line should show `Queue(loadtest,,,,45)`

- [ ] **Step 6: Run 200-agent load test**

```bash
cd <repo-root>
dotnet run --project tests/PbxAdmin.LoadTests -- \
  --scenario sustained-load \
  --agents 200 \
  --duration 15 \
  --target realtime \
  --talk-time 30 \
  --output /tmp/load-test-200-scaling-fix.json \
  --audit-interval 10
```

Monitor during the test:
- ODBC connections should stay well below 100 (with sorcery cache, expect 10-30 active)
- Active channels should stay near 320 (160 target × 2 channels per call)
- PSTN CPU should drop from 252% to ~100-150%
- Queue members should appear in audit snapshots (not 0)

- [ ] **Step 7: Analyze audit results**

After the test completes, check the audit JSONL:

```bash
cat /tmp/load-test-200-scaling-fix.json.audit.jsonl | head -5
```

Verify:
1. `realtime.odbcActiveConnections` < 50 (was 30/30 saturated)
2. `realtime.activeChannels` ≤ 400 (was 755)
3. `realtime.queue.membersIdle + membersInUse + membersRinging` > 0 (was always 0)
4. Container CPU for demo-pstn < 200% (was 252%)
