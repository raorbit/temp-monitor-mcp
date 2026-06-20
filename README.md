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

## Antivirus & WinRing0

To read CPU and motherboard temperatures, LibreHardwareMonitor loads the **WinRing0** kernel driver.
Microsoft put WinRing0 on its vulnerable-driver blocklist — it has a known CVE (CVE-2020-14979) that lets
a local process reach kernel memory — so **Windows Defender flags it as `VulnerableDriver:WinNT/Winring0`
and removes it.** This is expected, is **not** specific to TempMon, and affects every WinRing0-based tool
(LibreHardwareMonitor, HWiNFO, FanControl, …). It is not a false positive (the driver really is
vulnerable) but the driver and TempMon are not malware.

If the driver is removed, TempMon **degrades honestly** rather than lying: GPU (NVML) and storage (SMART)
temperatures still read, while CPU and motherboard report as unavailable — the MCP tools return an
explicit `available: false` / `partial` verdict instead of a false "all clear".

To keep CPU/motherboard readings, **allow the detection**: Windows Security → *Virus & threat protection*
→ *Protection history* → the WinRing0 item → *Actions* → *Allow*. That re-permits a known-vulnerable
driver, so only do it on a machine you control; on a shared or internet-exposed box, leave it blocked and
accept that CPU/motherboard temperatures will be unavailable.

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
dotnet publish src/TempMon.Mcp     -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true
```

(The desktop pins `win-x64` in its csproj; the MCP server doesn't, so its single-file publish needs an
explicit `-r win-x64`.)

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
| `get_temperatures()` | the snapshot wrapped in a `{ schema_version, stale, age_seconds, data }` envelope (read the snapshot from `data`) |
| `get_summary()` | `cpu_c` / `gpu_c` / `max_drive_c` |
| `check_thresholds(cpuMax?, gpuMax?, driveMax?)` | sensors at/above the given limits (defaults: CPU 80, GPU 75, drive 60 °C) |

When the desktop is reachable but its sensors could not be read (the driver failed to load, or the app
is not elevated), `get_summary()` and `check_thresholds()` return
`{ "available": false, "reason": "sensors_not_readable", "message": "…" }` instead of a zeroed answer —
treat that as **unknown**, not as "all clear". A transport failure (the desktop isn't running) returns
`{ "ok": false, "error": "…" }`.

**Staleness:** the desktop app polls every few seconds. `get_temperatures()` **always** wraps the
snapshot as `{ "schema_version": N, "stale": <bool>, "age_seconds": N, "data": { …snapshot… } }` —
read the snapshot from `data`. `stale` flips to `true` (with a `hint`) once the snapshot is older than
30 s, meaning the poll loop has stalled or the desktop died after writing the discovery file; a missing
or unparseable timestamp yields `stale: false, age_seconds: null`. If the desktop process named in
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
