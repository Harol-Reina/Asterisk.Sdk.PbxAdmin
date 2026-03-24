# Asterisk.Sdk.PbxAdmin

Blazor Server admin panel for Asterisk PBX — powered by [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk).

## Features

- **Real-time monitoring:** Live calls, queues, channels, agents with 2-second refresh
- **Configuration management:** Extensions, trunks, routes, IVR menus, time conditions, queues, conferences, recordings, MoH, feature codes, parking, voicemail
- **Call Flow visualization:** Inbound flow trees, health warnings, Call Tracer debugger with step-through
- **Advanced Dialplan:** Context browser with humanized app descriptions, type badges, bidirectional links
- **WebRTC softphone:** Built-in SIP.js softphone with DTMF tones, ringback, volume control
- **Multi-server:** Connect to multiple Asterisk servers with independent File/Realtime config modes
- **Spanish IVR demo:** 4 menus, 5 virtual agents, 6 queues, TTS audio files
- **Localization:** English + Spanish (800+ localized keys)
- **Dual config mode:** File-based (extensions.conf) + Realtime (PostgreSQL)

## Quick Start

```bash
cd docker
docker compose -f docker-compose.pbxadmin.yml up -d
```

Open http://localhost:8080 (admin/admin).

## Docker Image

```bash
docker pull hreina/asterisk-pbx-admin:1.8.0
```

## Requirements

- .NET 10.0.100+
- Docker (for demo stack)
- [Asterisk.Sdk](https://www.nuget.org/packages/Asterisk.Sdk.Hosting/) 1.4.0+ (NuGet)

## Project Structure

```
src/PbxAdmin/              # Blazor Server application
tests/PbxAdmin.Tests/      # bUnit unit tests (432)
tests/PbxAdmin.E2E.Tests/  # Playwright E2E tests (92)
docker/                    # Docker Compose, Asterisk configs, SQL seeds, sounds
```

## Testing

```bash
# Unit tests
dotnet test tests/PbxAdmin.Tests/

# E2E tests (requires Docker stack running)
dotnet test tests/PbxAdmin.E2E.Tests/
```

## Related Projects

- [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk) — .NET 10 Native AOT SDK for Asterisk PBX (MIT)
- [Asterisk.Sdk.Pro](https://github.com/Harol-Reina/Asterisk.Sdk.Pro) — Enterprise extensions (commercial)

## License

MIT
