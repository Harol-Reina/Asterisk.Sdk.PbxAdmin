# Spanish IVR Demo with Virtual Agents — Design Spec

**Author:** Harol Reina
**Date:** 2026-03-23
**Status:** Draft
**Goal:** Create a complete Spanish-language IVR demo on the realtime server with sub-menus per department, music on hold, virtual agents that greet and farewell callers, cross-server access from file server via trunk, and audible DTMF tones during call for IVR navigation.

---

## 1. Overview

A caller dials extension 200 from any server (realtime or file via trunk). They hear a Spanish greeting, navigate department sub-menus via DTMF, enter a queue with music on hold, and get answered by a virtual agent that greets with a personal name, simulates a brief conversation, says farewell, and hangs up.

---

## 2. IVR Menu Structure

### Main Menu (`ivr-empresa`, extension 200)

Greeting: "Bienvenido a nuestra empresa. Para Ventas marque 1. Para Soporte Técnico marque 2. Para Facturación marque 3. Para Recursos Humanos marque 4. Para el Directorio de Extensiones marque 5. Para repetir este menú marque 9."

| Digit | Destination | Type |
|-------|------------|------|
| 1 | ivr-ventas | Sub-menu |
| 2 | ivr-soporte | Sub-menu |
| 3 | ivr-facturacion | Sub-menu |
| 4 | Queue rrhh | Direct to queue |
| 5 | Directorio | WaitExten for extension input |
| 9 | ivr-empresa | Repeat main menu |

Timeout: 10 seconds, max retries: 3, timeout destination: hangup with farewell.
Invalid input: play "Opción no válida", retry.

### Sub-Menu: Ventas (`ivr-ventas`)

Greeting: "Bienvenido a Ventas. Para nuevo cliente marque 1. Para cliente existente marque 2. Para volver al menú principal marque 9."

| Digit | Destination | Type |
|-------|------------|------|
| 1 | Queue ventas-nuevos | Queue |
| 2 | Queue ventas-existentes | Queue |
| 9 | ivr-empresa | Back to main |

### Sub-Menu: Soporte (`ivr-soporte`)

Greeting: "Bienvenido a Soporte Técnico. Para problemas con su servicio marque 1. Para consultas generales marque 2. Para volver al menú principal marque 9."

| Digit | Destination | Type |
|-------|------------|------|
| 1 | Queue soporte-urgente | Queue |
| 2 | Queue soporte-general | Queue |
| 9 | ivr-empresa | Back to main |

### Sub-Menu: Facturación (`ivr-facturacion`)

Greeting: "Bienvenido a Facturación. Para consulta de saldo marque 1. Para pagos marque 2. Para volver al menú principal marque 9."

| Digit | Destination | Type |
|-------|------------|------|
| 1 | Queue facturacion | Queue |
| 2 | Queue facturacion | Queue |
| 9 | ivr-empresa | Back to main |

### RRHH (direct to queue, no sub-menu)

Pre-queue message: "Bienvenido a Recursos Humanos. Será transferido con un representante. Por favor espere."

### Directorio

Message: "Directorio de extensiones. Por favor marque el número de extensión deseado."
Then WaitExten → Dial(PJSIP/${EXTEN}) for any valid extension.

---

## 3. Queues with Virtual Agents

### New Queues (realtime server)

| Queue | Strategy | MOH | Virtual Agents |
|-------|----------|-----|----------------|
| ventas-nuevos | ringall | default | María, Carlos |
| ventas-existentes | leastrecent | default | Ana, Pedro |
| soporte-urgente | ringall | default | Carlos, Lucía |
| soporte-general | leastrecent | default | María, Ana |
| facturacion | ringall | default | Pedro, Lucía |
| rrhh | ringall | default | Ana, Carlos |

All queues: timeout=15s, retry=5s, maxlen=10, wrapuptime=2s, servicelevel=30s, ringinuse=no.

### Virtual Agent Context

A dedicated `[virtual-agent]` context in the realtime extensions.conf. Each agent has their own extension that plays a personalized greeting, simulates conversation (wait), plays farewell, and hangs up.

```
[virtual-agent]
exten => maria,1,Answer()
 same => n,Wait(1)
 same => n,Playback(es-custom/agent-maria)
 same => n,Wait(8)
 same => n,Playback(es-custom/agent-farewell)
 same => n,Wait(1)
 same => n,Hangup()
```

Queue members are `Local/{name}@virtual-agent` channels:
- `Local/maria@virtual-agent`
- `Local/carlos@virtual-agent`
- `Local/ana@virtual-agent`
- `Local/pedro@virtual-agent`
- `Local/lucia@virtual-agent`

### Virtual Agent Personalities

| Agent | Greeting |
|-------|----------|
| María | "Hola, bienvenido. Mi nombre es María, ¿en qué puedo ayudarle?" |
| Carlos | "Hola, bienvenido. Mi nombre es Carlos, ¿en qué puedo ayudarle?" |
| Ana | "Hola, bienvenida. Mi nombre es Ana, ¿en qué puedo ayudarle?" |
| Pedro | "Hola, bienvenido. Mi nombre es Pedro, ¿en qué puedo ayudarle?" |
| Lucía | "Hola, bienvenida. Mi nombre es Lucía, ¿en qué puedo ayudarle?" |

Shared farewell: "Fue un placer atenderle. Que tenga un excelente día. Hasta luego."

---

## 4. TTS Audio Files

Generated offline as .wav files, committed to `docker/sounds/es-custom/`.

### IVR Prompts

| File | Text |
|------|------|
| `ivr-main-greeting.wav` | "Bienvenido a nuestra empresa. Para Ventas marque 1. Para Soporte Técnico marque 2. Para Facturación marque 3. Para Recursos Humanos marque 4. Para el Directorio de Extensiones marque 5. Para repetir este menú marque 9." |
| `ivr-ventas.wav` | "Bienvenido a Ventas. Para nuevo cliente marque 1. Para cliente existente marque 2. Para volver al menú principal marque 9." |
| `ivr-soporte.wav` | "Bienvenido a Soporte Técnico. Para problemas con su servicio marque 1. Para consultas generales marque 2. Para volver al menú principal marque 9." |
| `ivr-facturacion.wav` | "Bienvenido a Facturación. Para consulta de saldo marque 1. Para pagos marque 2. Para volver al menú principal marque 9." |
| `ivr-rrhh.wav` | "Bienvenido a Recursos Humanos. Será transferido con un representante. Por favor espere." |
| `ivr-directorio.wav` | "Directorio de extensiones. Por favor marque el número de extensión deseado." |
| `ivr-invalid.wav` | "Opción no válida. Por favor intente nuevamente." |
| `ivr-timeout.wav` | "No hemos recibido su selección." |
| `ivr-goodbye.wav` | "Gracias por llamar. Hasta luego." |

### Agent Greetings

| File | Text |
|------|------|
| `agent-maria.wav` | "Hola, bienvenido. Mi nombre es María, ¿en qué puedo ayudarle?" |
| `agent-carlos.wav` | "Hola, bienvenido. Mi nombre es Carlos, ¿en qué puedo ayudarle?" |
| `agent-ana.wav` | "Hola, bienvenida. Mi nombre es Ana, ¿en qué puedo ayudarle?" |
| `agent-pedro.wav` | "Hola, bienvenido. Mi nombre es Pedro, ¿en qué puedo ayudarle?" |
| `agent-lucia.wav` | "Hola, bienvenida. Mi nombre es Lucía, ¿en qué puedo ayudarle?" |
| `agent-farewell.wav` | "Fue un placer atenderle. Que tenga un excelente día. Hasta luego." |

**Total: 15 audio files.** Format: 8000Hz mono 16-bit PCM WAV (Asterisk native format).

---

## 5. Docker Configuration

### Spanish Asterisk Sounds

Add to both `Dockerfile.asterisk-file` and `Dockerfile.asterisk-realtime`:

```dockerfile
# Asterisk core sounds (Spanish)
&& mkdir -p /var/lib/asterisk/sounds/es \
&& cd /var/lib/asterisk/sounds/es \
&& curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-es-ulaw-current.tar.gz | tar xz \
&& curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-es-gsm-current.tar.gz | tar xz \
&& curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-es-sln16-current.tar.gz | tar xz
```

### Custom Sound Mounting

In `docker-compose.pbxadmin.yml`, add to both Asterisk services:

```yaml
- ./sounds/es-custom:/var/lib/asterisk/sounds/es-custom:ro
```

IVR greetings reference sounds as `es-custom/ivr-main-greeting`, agent greetings as `es-custom/agent-maria`, etc.

### Cross-Server Access

In file server `extensions.conf`, add:

```
; Spanish IVR (via realtime trunk)
exten => 200,1,Dial(PJSIP/200@trunk-realtime,60)
 same => n,Hangup()
```

In realtime server, add inbound route via SQL seed:

```sql
INSERT INTO routes_inbound (server_id, name, did_pattern, destination_type, destination, priority, enabled)
VALUES ('pbx-realtime', 'IVR Empresa', '200', 'ivr', 'empresa', 5, true);
```

---

## 6. Softphone Improvements for IVR Navigation

The softphone needs several improvements to provide a good IVR navigation experience.

### 6.1 DTMF Tones Audible During Call (Critical)

The `SoftphoneCallView.razor` has a DTMF dialpad (button 🔢) but key presses are **silent locally**. The user doesn't know if the digit was sent.

**Fix:** Add `@inject IJSRuntime JS` to `SoftphoneCallView.razor`. Change each DTMF key handler to also play the local tone:

```razor
<div class="softphone-key" @onclick="() => SendDtmfWithTone(d)">@d</div>
```

```csharp
private async Task SendDtmfWithTone(string digit)
{
    await Phone.SendDtmfAsync(digit);
    await JS.InvokeVoidAsync("Softphone.playDtmfTone", digit);
    _dtmfInput += digit;
}
```

### 6.2 Visual DTMF Feedback (Critical)

During a call, there's no display of which digits the user has pressed. When navigating an IVR with sub-menus, the user needs to see "Pressed: 1 → 2" to know where they are.

**Fix:** Add a `_dtmfInput` string field that accumulates digits. Display it above the dialpad:

```razor
@if (_showDtmf)
{
    @if (_dtmfInput.Length > 0)
    {
        <div class="softphone-dtmf-display">@_dtmfInput</div>
    }
    <div class="softphone-dialpad">...</div>
}
```

CSS: monospace font, centered, accent color. Clear on call end or when dialpad is closed.

### 6.3 Sub-Letters on In-Call Dialpad (Low effort)

The pre-call dialer shows letters (ABC, DEF, etc.) under each digit, but the in-call dialpad shows only digits. For consistency and usability, add the same letter sublabels.

**Fix:** Replace the simple digit forEach with the same `_keys` array pattern used in `SoftphoneDialer.razor`:

```razor
@foreach (var key in _keys)
{
    <div class="softphone-key" @onclick="() => SendDtmfWithTone(key.Digit)">
        @key.Digit
        <div class="softphone-key-sub">@key.Letters</div>
    </div>
}
```

### 6.4 Incoming Call Ringtone (Nice-to-have)

When the softphone receives an incoming call (e.g., transfer from IVR), the `IncomingCallOverlay` shows visually but plays no ringtone. For the demo, add a simple ringtone using the same WAV blob approach as the ringback tone.

**Fix:** In `softphone.js`, add `_startRingtone()` / `_stopRingtone()` using a generated ringtone WAV (different cadence from ringback: shorter bursts). Call `_startRingtone()` in `_handleIncoming()`, `_stopRingtone()` when answered/rejected.

### 6.5 Volume Control (Nice-to-have)

Add a volume slider to the call view that controls the `<audio>` element volume (0.0 to 1.0). Simple range input.

```razor
<input type="range" min="0" max="100" value="@_volume" @oninput="OnVolumeChange" class="softphone-volume" />
```

```csharp
private int _volume = 80;
private async Task OnVolumeChange(ChangeEventArgs e)
{
    _volume = int.Parse(e.Value?.ToString() ?? "80");
    await JS.InvokeVoidAsync("Softphone.setVolume", _volume / 100.0);
}
```

In `softphone.js`, add:
```javascript
setVolume(level) {
    if (this._audioElement) this._audioElement.volume = level;
}
```

---

## 7. Seed Data (SQL)

### New SQL file: `013-ivr-demo-seed.sql`

Creates:
- 4 IVR menus: `empresa`, `ventas`, `soporte`, `facturacion`
- 6 queues: `ventas-nuevos`, `ventas-existentes`, `soporte-urgente`, `soporte-general`, `facturacion`, `rrhh`
- Queue members as `Local/{name}@virtual-agent` channels
- 1 inbound route: 200 → IVR empresa

### Extensions.conf changes (realtime)

Add `[virtual-agent]` context with 5 agent extensions.
Add `[ivr-directorio]` context for the directory feature (WaitExten → Dial PJSIP).

These go in the realtime `extensions.conf` since they're static infrastructure contexts (not generated by PbxAdmin).

---

## 8. Complete Call Flow

```
Softphone WebRTC → marca 200
  → Realtime server matches route 200 → IVR "empresa"
  → Playback(es-custom/ivr-main-greeting)
  → WaitExten(10)
  → Caller presses 1 (DTMF tone plays locally)
  → Goto(ivr-ventas,s,1)
  → Playback(es-custom/ivr-ventas)
  → WaitExten(10)
  → Caller presses 1
  → Goto(queues,ventas-nuevos,1)
  → Queue(ventas-nuevos) — MOH plays while waiting
  → ~5s later: Local/maria@virtual-agent answers
  → "Hola, bienvenido. Mi nombre es María..."
  → 8s simulated conversation
  → "Fue un placer atenderle..."
  → Hangup

From file server:
  Softphone → marca 200
  → File server: Dial(PJSIP/200@trunk-realtime)
  → Same IVR flow on realtime server
```

---

## 9. Tech Stack

- TTS: offline generation (pico2wave, espeak-ng, or any available TTS), committed as .wav
- Audio format: 8000Hz mono 16-bit PCM WAV
- Asterisk: Local channels for virtual agents, standard Queue() app
- Docker: volume mount for custom sounds
- PbxAdmin: IVR seed data via SQL, no code changes needed for IVR/queue logic
- Softphone: add JS interop for DTMF tone in active call view
