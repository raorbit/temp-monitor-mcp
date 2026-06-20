# TempMon

[![CI](https://github.com/raorbit/temp-monitor-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/raorbit/temp-monitor-mcp/actions/workflows/ci.yml)

A Windows hardware-temperature monitor with a desktop dashboard and an [MCP](https://modelcontextprotocol.io)
server, so Claude Code (or any MCP client) can read your CPU / GPU / motherboard / storage
temperatures.

Built on [LibreHardwareMonitorLib](https://github.com/LibreHardware/LibreHardwareMonitor).

## The one rule

**Exactly one process reads the hardware.** LibreHardwareMonitor drives the WinRing0 kernel driver
under a global lock — two sensor-reading processes conflict. So the **desktop app is the sole
sensor authority**; the MCP server never touches the hardware. It reads a cached snapshot from the
desktop app over localhost HTTP.

```
 Hardware ──elevated──▶  TempMon.Desktop (WPF, runs elevated)
                           ├─ TempMon.Core      poller · global lock · builds the snapshot
                           └─ Kestrel :8757     127.0.0.1 only · serves the cached snapshot
                                   │ HTTP GET
                                   ▼
                         TempMon.Mcp (stdio · no admin)   endpoint.json → HTTP → JSON
                                   │
                                   ▼
                               Claude Code
```

## Projects

| Project | Type | Role |
|---|---|---|
| `TempMon.Core` | class library (`net8.0-windows`) | LibreHardwareMonitor wrapper, the poller, the global lock, builds the immutable snapshot. The only code that reads hardware. |
| `TempMon.Desktop` | WPF app (`net8.0-windows`, **elevated**) | Polls Core on a timer, renders the dashboard, hosts the HTTP server, writes the discovery file. |
| `TempMon.Mcp` | console / stdio (`net8.0`, **no admin**) | Reads `endpoint.json`, HTTP-GETs the snapshot, exposes three MCP tools. |

The MCP server deliberately does **not** reference Core — it can never load the sensor stack.

## Requirements

- Windows 10/11, x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download) or newer to build
  (the apps roll forward to a newer installed runtime — `RollForward=LatestMajor`)
- Administrator rights to read sensors (the desktop app requests elevation via its manifest)

## Build

```sh
dotnet build TempMon.slnx -c Release
```

## Run

1. **Start the desktop app elevated** (it will prompt for UAC):

   ```sh
   dotnet run --project src/TempMon.Desktop -c Release
   ```

   It opens the dashboard, starts the HTTP server on `http://127.0.0.1:8757` (falling back up the
   range if the port is taken), and writes `%PROGRAMDATA%\TempMon\endpoint.json`.

   Quick check, from any shell:

   ```sh
   curl http://127.0.0.1:8757/health     # {"ok":true,"schema_version":1,"sensors_available":true,"elevated":true,"snapshot_at":"2026-06-18T14:32:05Z"}
   curl http://127.0.0.1:8757/temps      # full snapshot
   ```

2. **The MCP server** is launched by your MCP client (see below). It needs no admin and finds the
   port automatically via the discovery file.

## System tray & auto-start

TempMon lives in the notification area:

- **Close or minimize** the window and it hides to the tray (it does **not** exit) — the poll loop
  and the HTTP server keep running.
- **Double-click the tray icon**, or **Open dashboard** in the flyout, to bring the window back.
  Re-launching the exe resurfaces the running instance instead of starting a second one.
- **Auto-start at logon** (flyout toggle) registers a **Task Scheduler** ONLOGON task with *Run
  Level Highest*, so the elevated app starts at logon with **no UAC prompt** (a plain `Run` key
  would prompt or be blocked every logon). Toggling it off removes the task; a moved/reinstalled
  exe self-repairs its task path on the next elevated launch.
- **Exit** (flyout only) fully shuts down: disposes the poller and HTTP server, deletes
  `endpoint.json`, and removes the tray icon.

A single named mutex enforces the one-reader rule — only one `TempMon.Desktop` runs at a time.

## Publish (single file)

LibreHardwareMonitor self-extracts its native `WinRing0x64.sys`, so single-file publish must keep
native libraries on disk (`IncludeNativeLibrariesForSelfExtract`, already set in the csproj):

```sh
dotnet publish src/TempMon.Desktop -c Release -p:PublishSingleFile=true
dotnet publish src/TempMon.Mcp     -c Release -p:PublishSingleFile=true
```

Smoke-test the published desktop exe on a clean machine to confirm the driver extracts.

## Register with Claude Code

```sh
claude mcp add tempmon C:\path\to\TempMon.Mcp.exe
```

or add it to `.mcp.json` (see [`.mcp.json.example`](./.mcp.json.example)):

```jsonc
{ "mcpServers": { "tempmon": { "command": "C:\\path\\to\\TempMon.Mcp.exe", "args": [] } } }
```

### MCP tools

| Tool | Returns |
|---|---|
| `get_temperatures()` | the full snapshot payload (JSON) |
| `get_summary()` | `cpu_c` / `gpu_c` / `max_drive_c` |
| `check_thresholds(cpuMax?, gpuMax?, driveMax?)` | sensors at/above the given limits (defaults: CPU 80, GPU 75, drive 60 °C) |

**Staleness:** the desktop app polls every few seconds. If its poll loop has stalled (or it died
after writing the discovery file), `get_temperatures()` wraps the payload as
`{ "stale": true, "age_seconds": N, "hint": "...", "data": { …snapshot… } }` once the snapshot is
older than 30 s — a fresh snapshot is returned verbatim. If the desktop process named in
`endpoint.json` is gone, the tools fail fast with a friendly message instead of waiting out the HTTP
timeout.

## HTTP API

`GET /temps` — the snapshot below. `GET /health` —
`{"ok":true,"schema_version":1,"sensors_available":true,"elevated":true,"snapshot_at":"<ISO-8601>"}`
(`snapshot_at` is the cached snapshot's own timestamp — a frozen value means the poll loop has stalled;
`sensors_available` is `false` when the hardware reader never opened, so the values are unreadable, not safe).

```json
{
  "schema_version": 1,
  "sensors_available": true,
  "timestamp": "2026-06-18T14:32:05Z",
  "host": "DESKTOP-XYZ",
  "summary": { "cpu_c": 62.5, "gpu_c": 51.0, "max_drive_c": 44.0 },
  "sensors": [
    { "component": "CPU", "device": "AMD Ryzen 9 7950X",
      "name": "Core (Tctl/Tdie)", "value": 62.5, "min": 35.0, "max": 78.2 }
  ]
}
```

`schema_version` is the wire-contract version (bumped only on a breaking shape change).
`sensors_available` is `false` when the hardware reader never opened (driver blocked, or the app is
not elevated) — the values are then unreadable, not a safe reading. `component` is one of
`CPU | GPU | Motherboard | Storage`. Any value is `null` if the read failed or the desktop app is not elevated.

## Privacy & licensing

No telemetry, no outbound network. The HTTP server binds `127.0.0.1` only. The discovery file under
`%PROGRAMDATA%` holds just the local base URL.

TempMon is licensed **MIT** — see [`LICENSE`](./LICENSE). LibreHardwareMonitorLib is licensed
**MPL-2.0** — see [`NOTICE`](./NOTICE).
