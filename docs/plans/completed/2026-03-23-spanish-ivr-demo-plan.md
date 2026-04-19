# Spanish IVR Demo with Virtual Agents — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a complete Spanish IVR demo with 5 departments, sub-menus, virtual agents, music on hold, cross-server trunk access, TTS audio files, and softphone improvements for IVR navigation.

**Architecture:** TTS .wav files committed to `docker/sounds/es-custom/`, mounted as volume. IVR menus seeded via SQL in realtime DB. Virtual agents use `Local/` channels in a dedicated `[virtual-agent]` dialplan context. Softphone gets audible DTMF + visual feedback + volume control.

**Tech Stack:** Asterisk dialplan, PostgreSQL seed SQL, Dapper, Docker volumes, Web Audio API (DTMF tones), Blazor Server.

**Spec:** `docs/superpowers/specs/2026-03-23-spanish-ivr-demo-design.md`

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `docker/sounds/es-custom/*.wav` | 15 TTS audio files (9 IVR prompts + 5 agent greetings + 1 farewell) |
| `docker/sql/013-ivr-demo-seed.sql` | IVR menus, queues, queue members, inbound route for realtime server |

### Modified Files

| File | Change |
|------|--------|
| `docker/Dockerfile.asterisk-file` | Add Spanish sound downloads |
| `docker/Dockerfile.asterisk-realtime` | Add Spanish sound downloads |
| `docker/docker-compose.pbxadmin.yml` | Mount es-custom sounds volume |
| `docker/asterisk-config-realtime/extensions.conf` | Add `[virtual-agent]` + `[ivr-directorio]` contexts |
| `docker/asterisk-config-file/extensions.conf` | Add extension 200 → trunk to realtime IVR |
| `Examples/PbxAdmin/Components/Shared/SoftphoneCallView.razor` | DTMF tones + visual feedback + letters + volume |
| `Examples/PbxAdmin/wwwroot/js/softphone.js` | Add `setVolume()` method |

---

## Task 1: Generate TTS Audio Files

**Files:**
- Create: `docker/sounds/es-custom/*.wav` (15 files)

- [ ] **Step 1: Check available TTS tools**

Run: `which pico2wave espeak-ng espeak flite 2>/dev/null` to find what's available on the system.

- [ ] **Step 2: Generate IVR prompt files**

Use the best available TTS to generate 8kHz mono WAV files. If `pico2wave` is available (best quality for Spanish):

```bash
mkdir -p docker/sounds/es-custom

pico2wave -l es-ES -w docker/sounds/es-custom/ivr-main-greeting.wav \
  "Bienvenido a nuestra empresa. Para Ventas marque 1. Para Soporte Técnico marque 2. Para Facturación marque 3. Para Recursos Humanos marque 4. Para el Directorio de Extensiones marque 5. Para repetir este menú marque 9."

pico2wave -l es-ES -w docker/sounds/es-custom/ivr-ventas.wav \
  "Bienvenido a Ventas. Para nuevo cliente marque 1. Para cliente existente marque 2. Para volver al menú principal marque 9."

pico2wave -l es-ES -w docker/sounds/es-custom/ivr-soporte.wav \
  "Bienvenido a Soporte Técnico. Para problemas con su servicio marque 1. Para consultas generales marque 2. Para volver al menú principal marque 9."

pico2wave -l es-ES -w docker/sounds/es-custom/ivr-facturacion.wav \
  "Bienvenido a Facturación. Para consulta de saldo marque 1. Para pagos marque 2. Para volver al menú principal marque 9."

pico2wave -l es-ES -w docker/sounds/es-custom/ivr-rrhh.wav \
  "Bienvenido a Recursos Humanos. Será transferido con un representante. Por favor espere."

pico2wave -l es-ES -w docker/sounds/es-custom/ivr-directorio.wav \
  "Directorio de extensiones. Por favor marque el número de extensión deseado."

pico2wave -l es-ES -w docker/sounds/es-custom/ivr-invalid.wav \
  "Opción no válida. Por favor intente nuevamente."

pico2wave -l es-ES -w docker/sounds/es-custom/ivr-timeout.wav \
  "No hemos recibido su selección."

pico2wave -l es-ES -w docker/sounds/es-custom/ivr-goodbye.wav \
  "Gracias por llamar. Hasta luego."
```

If `pico2wave` is not available, use `espeak-ng`:
```bash
espeak-ng -v es -s 140 -w docker/sounds/es-custom/ivr-main-greeting.wav "Bienvenido a nuestra empresa..."
```

- [ ] **Step 3: Generate agent greeting files**

```bash
pico2wave -l es-ES -w docker/sounds/es-custom/agent-maria.wav \
  "Hola, bienvenido. Mi nombre es María, en qué puedo ayudarle?"

pico2wave -l es-ES -w docker/sounds/es-custom/agent-carlos.wav \
  "Hola, bienvenido. Mi nombre es Carlos, en qué puedo ayudarle?"

pico2wave -l es-ES -w docker/sounds/es-custom/agent-ana.wav \
  "Hola, bienvenida. Mi nombre es Ana, en qué puedo ayudarle?"

pico2wave -l es-ES -w docker/sounds/es-custom/agent-pedro.wav \
  "Hola, bienvenido. Mi nombre es Pedro, en qué puedo ayudarle?"

pico2wave -l es-ES -w docker/sounds/es-custom/agent-lucia.wav \
  "Hola, bienvenida. Mi nombre es Lucía, en qué puedo ayudarle?"

pico2wave -l es-ES -w docker/sounds/es-custom/agent-farewell.wav \
  "Fue un placer atenderle. Que tenga un excelente día. Hasta luego."
```

- [ ] **Step 4: Convert to 8kHz mono if needed**

Asterisk works best with 8000Hz mono 16-bit. If the TTS outputs a different rate:
```bash
for f in docker/sounds/es-custom/*.wav; do
  sox "$f" -r 8000 -c 1 -b 16 "/tmp/$(basename $f)" && mv "/tmp/$(basename $f)" "$f"
done
```

If `sox` is not available, use `ffmpeg`:
```bash
for f in docker/sounds/es-custom/*.wav; do
  ffmpeg -y -i "$f" -ar 8000 -ac 1 -sample_fmt s16 "/tmp/$(basename $f)" && mv "/tmp/$(basename $f)" "$f"
done
```

- [ ] **Step 5: Verify all 15 files exist**

```bash
ls -la docker/sounds/es-custom/*.wav | wc -l
# Expected: 15
```

- [ ] **Step 6: Commit**

```bash
git add docker/sounds/es-custom/
git commit -m "feat(demo): add 15 Spanish TTS audio files for IVR and virtual agents"
```

---

## Task 2: Docker — Spanish Sounds + Volume Mount

**Files:**
- Modify: `docker/Dockerfile.asterisk-file`
- Modify: `docker/Dockerfile.asterisk-realtime`
- Modify: `docker/docker-compose.pbxadmin.yml`

- [ ] **Step 1: Add Spanish core sounds to both Dockerfiles**

After the English extra sounds download block, add (in BOTH Dockerfiles):

```dockerfile
    # Asterisk core sounds (Spanish, ulaw + gsm + sln16)
    && mkdir -p /var/lib/asterisk/sounds/es \
    && cd /var/lib/asterisk/sounds/es \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-es-ulaw-current.tar.gz | tar xz \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-es-gsm-current.tar.gz | tar xz \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-es-sln16-current.tar.gz | tar xz \
```

- [ ] **Step 2: Mount custom sounds in docker-compose.pbxadmin.yml**

Add to both `asterisk-realtime` and `asterisk-file` volume sections:

```yaml
      - ./sounds/es-custom:/var/lib/asterisk/sounds/es-custom:ro
```

- [ ] **Step 3: Commit**

```bash
git add docker/Dockerfile.asterisk-file docker/Dockerfile.asterisk-realtime docker/docker-compose.pbxadmin.yml
git commit -m "feat(docker): add Spanish Asterisk sounds and custom sound volume mount"
```

---

## Task 3: Dialplan — Virtual Agent Context + Directory + Extensions

**Files:**
- Modify: `docker/asterisk-config-realtime/extensions.conf`
- Modify: `docker/asterisk-config-file/extensions.conf`

- [ ] **Step 1: Add virtual-agent context to realtime extensions.conf**

Read the file first. Add at the end:

```
[virtual-agent]
; Virtual agents that greet callers, simulate conversation, and hang up
exten => maria,1,Answer()
 same => n,Wait(1)
 same => n,Playback(es-custom/agent-maria)
 same => n,Wait(8)
 same => n,Playback(es-custom/agent-farewell)
 same => n,Wait(1)
 same => n,Hangup()

exten => carlos,1,Answer()
 same => n,Wait(1)
 same => n,Playback(es-custom/agent-carlos)
 same => n,Wait(8)
 same => n,Playback(es-custom/agent-farewell)
 same => n,Wait(1)
 same => n,Hangup()

exten => ana,1,Answer()
 same => n,Wait(1)
 same => n,Playback(es-custom/agent-ana)
 same => n,Wait(8)
 same => n,Playback(es-custom/agent-farewell)
 same => n,Wait(1)
 same => n,Hangup()

exten => pedro,1,Answer()
 same => n,Wait(1)
 same => n,Playback(es-custom/agent-pedro)
 same => n,Wait(8)
 same => n,Playback(es-custom/agent-farewell)
 same => n,Wait(1)
 same => n,Hangup()

exten => lucia,1,Answer()
 same => n,Wait(1)
 same => n,Playback(es-custom/agent-lucia)
 same => n,Wait(8)
 same => n,Playback(es-custom/agent-farewell)
 same => n,Wait(1)
 same => n,Hangup()

[ivr-directorio]
; Extension directory — caller dials any known extension
exten => _2XXX,1,Dial(PJSIP/${EXTEN},30)
 same => n,Hangup()
exten => _3XXX,1,Dial(PJSIP/${EXTEN},30)
 same => n,Hangup()
exten => i,1,Playback(es-custom/ivr-invalid)
 same => n,Goto(ivr-directorio,s,1)
exten => s,1,Playback(es-custom/ivr-directorio)
 same => n,WaitExten(10)
```

- [ ] **Step 2: Add extension 200 to file server extensions.conf**

In the `[default]` context of the file server, add:

```
; Spanish IVR (via realtime trunk)
exten => 200,1,Dial(PJSIP/200@trunk-realtime,60)
 same => n,Hangup()
```

Also add to the realtime server `[default]` context (so local extensions can reach it too):

```
; Spanish IVR entry point
exten => 200,1,Goto(ivr-empresa,s,1)
```

- [ ] **Step 3: Commit**

```bash
git add docker/asterisk-config-realtime/extensions.conf docker/asterisk-config-file/extensions.conf
git commit -m "feat(demo): add virtual-agent and ivr-directorio contexts, ext 200 for IVR"
```

---

## Task 4: SQL Seed — IVR Menus + Queues + Route

**Files:**
- Create: `docker/sql/013-ivr-demo-seed.sql`

- [ ] **Step 1: Create the seed SQL file**

```sql
-- ============================================================
-- Spanish IVR Demo: menus, queues with virtual agents, route
-- ============================================================

-- 6 new queues with virtual agent members
INSERT INTO queues_config (server_id, name, strategy, timeout, retry, maxlen, wrapuptime, servicelevel, musiconhold, joinempty, leavewhenempty, ringinuse, enabled, notes)
VALUES
  ('pbx-realtime', 'ventas-nuevos', 'ringall', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Ventas nuevos clientes'),
  ('pbx-realtime', 'ventas-existentes', 'leastrecent', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Ventas clientes existentes'),
  ('pbx-realtime', 'soporte-urgente', 'ringall', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Soporte urgente'),
  ('pbx-realtime', 'soporte-general', 'leastrecent', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Soporte general'),
  ('pbx-realtime', 'facturacion', 'ringall', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Facturación'),
  ('pbx-realtime', 'rrhh', 'ringall', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Recursos Humanos');

-- Queue members (virtual agents using Local channels)
INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/maria@virtual-agent', 'María', 'Local/maria@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'ventas-nuevos' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/carlos@virtual-agent', 'Carlos', 'Local/carlos@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'ventas-nuevos' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/ana@virtual-agent', 'Ana', 'Local/ana@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'ventas-existentes' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/pedro@virtual-agent', 'Pedro', 'Local/pedro@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'ventas-existentes' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/carlos@virtual-agent', 'Carlos', 'Local/carlos@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'soporte-urgente' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/lucia@virtual-agent', 'Lucía', 'Local/lucia@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'soporte-urgente' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/maria@virtual-agent', 'María', 'Local/maria@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'soporte-general' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/ana@virtual-agent', 'Ana', 'Local/ana@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'soporte-general' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/pedro@virtual-agent', 'Pedro', 'Local/pedro@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'facturacion' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/lucia@virtual-agent', 'Lucía', 'Local/lucia@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'facturacion' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/ana@virtual-agent', 'Ana', 'Local/ana@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'rrhh' AND qc.server_id = 'pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT qc.id, 'Local/carlos@virtual-agent', 'Carlos', 'Local/carlos@virtual-agent', 0, false
FROM queues_config qc WHERE qc.name = 'rrhh' AND qc.server_id = 'pbx-realtime';

-- IVR menus
INSERT INTO ivr_menus (server_id, name, label, greeting, timeout, max_retries, timeout_dest_type, timeout_dest, invalid_dest_type, invalid_dest, enabled, notes)
VALUES
  ('pbx-realtime', 'empresa', 'Menú Principal', 'es-custom/ivr-main-greeting', 10, 3, 'hangup', '', 'hangup', '', true, 'IVR Demo: menú principal en español'),
  ('pbx-realtime', 'ventas', 'Sub-menú Ventas', 'es-custom/ivr-ventas', 10, 3, 'ivr', 'empresa', 'ivr', 'empresa', true, 'IVR Demo: sub-menú ventas'),
  ('pbx-realtime', 'soporte', 'Sub-menú Soporte', 'es-custom/ivr-soporte', 10, 3, 'ivr', 'empresa', 'ivr', 'empresa', true, 'IVR Demo: sub-menú soporte'),
  ('pbx-realtime', 'facturacion', 'Sub-menú Facturación', 'es-custom/ivr-facturacion', 10, 3, 'ivr', 'empresa', 'ivr', 'empresa', true, 'IVR Demo: sub-menú facturación');

-- Main menu items
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '1', 'Ventas', 'ivr', 'ventas', NULL FROM ivr_menus m WHERE m.name = 'empresa' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '2', 'Soporte Técnico', 'ivr', 'soporte', NULL FROM ivr_menus m WHERE m.name = 'empresa' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '3', 'Facturación', 'ivr', 'facturacion', NULL FROM ivr_menus m WHERE m.name = 'empresa' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '4', 'Recursos Humanos', 'queue', 'rrhh', NULL FROM ivr_menus m WHERE m.name = 'empresa' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '5', 'Directorio', 'extension', 's@ivr-directorio', NULL FROM ivr_menus m WHERE m.name = 'empresa' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '9', 'Repetir menú', 'ivr', 'empresa', NULL FROM ivr_menus m WHERE m.name = 'empresa' AND m.server_id = 'pbx-realtime';

-- Ventas sub-menu items
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '1', 'Nuevo cliente', 'queue', 'ventas-nuevos', NULL FROM ivr_menus m WHERE m.name = 'ventas' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '2', 'Cliente existente', 'queue', 'ventas-existentes', NULL FROM ivr_menus m WHERE m.name = 'ventas' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '9', 'Menú principal', 'ivr', 'empresa', NULL FROM ivr_menus m WHERE m.name = 'ventas' AND m.server_id = 'pbx-realtime';

-- Soporte sub-menu items
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '1', 'Problemas servicio', 'queue', 'soporte-urgente', NULL FROM ivr_menus m WHERE m.name = 'soporte' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '2', 'Consultas generales', 'queue', 'soporte-general', NULL FROM ivr_menus m WHERE m.name = 'soporte' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '9', 'Menú principal', 'ivr', 'empresa', NULL FROM ivr_menus m WHERE m.name = 'soporte' AND m.server_id = 'pbx-realtime';

-- Facturación sub-menu items
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '1', 'Consulta de saldo', 'queue', 'facturacion', NULL FROM ivr_menus m WHERE m.name = 'facturacion' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '2', 'Pagos', 'queue', 'facturacion', NULL FROM ivr_menus m WHERE m.name = 'facturacion' AND m.server_id = 'pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT m.id, '9', 'Menú principal', 'ivr', 'empresa', NULL FROM ivr_menus m WHERE m.name = 'facturacion' AND m.server_id = 'pbx-realtime';

-- Inbound route: extension 200 → IVR empresa
INSERT INTO routes_inbound (server_id, name, did_pattern, destination_type, destination, priority, enabled, notes)
VALUES ('pbx-realtime', 'IVR Empresa Español', '200', 'ivr', 'empresa', 5, true, 'Spanish IVR demo entry point');
```

- [ ] **Step 2: Commit**

```bash
git add docker/sql/013-ivr-demo-seed.sql
git commit -m "feat(demo): add SQL seed for Spanish IVR menus, queues, virtual agents, route"
```

---

## Task 5: Softphone — DTMF Tones + Visual Feedback + Volume

**Files:**
- Modify: `Examples/PbxAdmin/Components/Shared/SoftphoneCallView.razor`
- Modify: `Examples/PbxAdmin/wwwroot/js/softphone.js`

- [ ] **Step 1: Read SoftphoneCallView.razor**

- [ ] **Step 2: Add IJSRuntime and DTMF improvements**

Add `@inject IJSRuntime JS` at top.

Replace the DTMF section with audible tones, visual display, and letter sublabels:

```razor
@if (_showDtmf)
{
    @if (_dtmfInput.Length > 0)
    {
        <div class="softphone-dtmf-display">@_dtmfInput</div>
    }
    <div class="softphone-dialpad" style="padding-bottom:0.5rem;">
        @foreach (var key in _keys)
        {
            <div class="softphone-key" @onclick="() => SendDtmfWithTone(key.Digit)">
                @key.Digit
                <div class="softphone-key-sub">@key.Letters</div>
            </div>
        }
    </div>
}
```

Add to `@code`:

```csharp
private string _dtmfInput = "";

private static readonly (string Digit, string Letters)[] _keys =
[
    ("1", " "), ("2", "ABC"), ("3", "DEF"),
    ("4", "GHI"), ("5", "JKL"), ("6", "MNO"),
    ("7", "PQRS"), ("8", "TUV"), ("9", "WXYZ"),
    ("*", ""), ("0", "+"), ("#", "")
];

private async Task SendDtmfWithTone(string digit)
{
    await Phone.SendDtmfAsync(digit);
    await JS.InvokeVoidAsync("Softphone.playDtmfTone", digit);
    _dtmfInput += digit;
}
```

Reset `_dtmfInput` when DTMF pad is toggled closed:
```csharp
private void ToggleDtmf()
{
    _showDtmf = !_showDtmf;
    if (!_showDtmf) _dtmfInput = "";
}
```

- [ ] **Step 3: Add volume control**

Below the control buttons, add a volume slider:

```razor
<div class="softphone-volume-wrap">
    <span>🔈</span>
    <input type="range" min="0" max="100" value="@_volume" @oninput="OnVolumeChange" class="softphone-volume" />
    <span>🔊</span>
</div>
```

```csharp
private int _volume = 80;
private async Task OnVolumeChange(ChangeEventArgs e)
{
    _volume = int.Parse(e.Value?.ToString() ?? "80");
    await JS.InvokeVoidAsync("Softphone.setVolume", _volume / 100.0);
}
```

- [ ] **Step 4: Add setVolume to softphone.js**

In `softphone.js`, add method:

```javascript
setVolume(level) {
    if (this._audioElement) this._audioElement.volume = level;
},
```

- [ ] **Step 5: Add CSS**

In existing softphone CSS (or inline in CallView):

```css
.softphone-dtmf-display {
    text-align: center;
    font-family: monospace;
    font-size: 1.1rem;
    color: var(--accent);
    padding: 0.25rem;
    letter-spacing: 2px;
}
.softphone-volume-wrap {
    display: flex;
    align-items: center;
    gap: 0.35rem;
    padding: 0.25rem 0.75rem;
    font-size: 0.75rem;
}
.softphone-volume {
    flex: 1;
    height: 4px;
    accent-color: var(--accent);
}
```

- [ ] **Step 6: Build and run tests**

Run: `dotnet build Asterisk.Sdk.slnx && dotnet test Tests/PbxAdmin.Tests/`
Expected: 0 warnings, all pass

- [ ] **Step 7: Commit**

```bash
git add Examples/PbxAdmin/Components/Shared/SoftphoneCallView.razor \
  Examples/PbxAdmin/wwwroot/js/softphone.js
git commit -m "feat(softphone): add audible DTMF in call, visual feedback, volume control"
```

---

## Task 6: Full Build + Docker Test

- [ ] **Step 1: Build full solution**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test Tests/PbxAdmin.Tests/`
Expected: ALL PASS

- [ ] **Step 3: Clean and rebuild Docker**

```bash
cd docker
docker compose -f docker-compose.pbxadmin.yml down -v --rmi local
docker compose -f docker-compose.pbxadmin.yml build --no-cache
docker compose -f docker-compose.pbxadmin.yml up -d
```

- [ ] **Step 4: Verify PbxAdmin starts without errors**

```bash
sleep 5 && docker logs asterisk-pbx-admin 2>&1 | grep -i "error\|exception\|circular"
```
Expected: no errors

- [ ] **Step 5: Verify Spanish sounds exist in container**

```bash
docker exec demo-pbx-realtime ls /var/lib/asterisk/sounds/es-custom/
docker exec demo-pbx-realtime ls /var/lib/asterisk/sounds/es/ | head -5
```
Expected: 15 custom files, Spanish core sounds present

- [ ] **Step 6: Verify virtual-agent context loaded**

```bash
docker exec demo-pbx-realtime asterisk -rx "dialplan show virtual-agent"
```
Expected: 5 agent extensions (maria, carlos, ana, pedro, lucia)

- [ ] **Step 7: Verify IVR menus in database**

```bash
docker exec demo-postgres psql -U asterisk -c "SELECT name, label FROM ivr_menus WHERE server_id='pbx-realtime'"
```
Expected: empresa, ventas, soporte, facturacion

- [ ] **Step 8: Verify queues loaded**

```bash
docker exec demo-pbx-realtime asterisk -rx "queue show"
```
Expected: ventas-nuevos, ventas-existentes, soporte-urgente, soporte-general, facturacion, rrhh with Local/ members

- [ ] **Step 9: Commit fixes (if any)**
