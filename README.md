# LyfStack.Agent.Windows

Windows companion agent for [LyfStack](https://github.com/usmansohee). Tracks foreground app usage on your PC, stores sessions locally, and syncs activity to your LyfStack API.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for building)

## Run (dev)

```powershell
dotnet run --project src/LyfStack.Agent.Windows
```

## Build production exe (`lyfstack_agent_win.exe`)

Self-contained single-file for Windows x64 (no .NET install needed on the target PC).  
**Not committed to git** (`publish/` is gitignored — the Release exe is ~150MB and over GitHub’s 100MB limit). Build it locally:

```powershell
cd path\to\lyfstack_agent_win

dotnet publish src/LyfStack.Agent.Windows `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:AssemblyName=lyfstack_agent_win `
  -o .\publish
```

Output:

```
publish\lyfstack_agent_win.exe
```

Optional: also copy native deps from `publish\` next to the exe if Windows asks for missing DLLs (SQLite / WPF natives are usually included with single-file).

Run it:

```powershell
.\publish\lyfstack_agent_win.exe
# or tray-only:
.\publish\lyfstack_agent_win.exe --tray
```

> **Note:** This publish output is a portable/runnable exe, **not** a Windows installer (`Setup.exe`).

### TODO — Inno Setup installer (later)

- [ ] Add an **Inno Setup** script to wrap `publish\` into an installable `LyfStack.Agent.Windows.Setup.exe`
- [ ] Install to something like `%LocalAppData%\LyfStack\Agent` (or Program Files)
- [ ] Start Menu shortcut + optional “Start with Windows”
- [ ] Uninstaller entry in Apps & features
- [ ] Document build: `publish` → compile `.iss` → ship `Setup.exe`

Until then, distribute/run `lyfstack_agent_win.exe` directly.

## How sync works (two directions)

### A) Sync from the `.exe` (simple — works today)

```
Windows Agent  ──POST──►  webhook / LyfStack API
                 HTTPS
```

You click **Sync now** in the tray app → agent uploads sessions to the **Sync endpoint** URL.  
No WebSocket needed. Works today with webhook.site (or your real API later).

### B) Sync from LyfStack website (phone / other PC) — WebSocket + same POST

The website **cannot** call your home PC. The `.exe` has **no public URL** (PC is behind NAT/router).

So the agent opens a long-lived **outbound** WebSocket to LyfStack. Website Sync Now talks to the **server**; the server taps that socket; then the agent uploads data the **same way as (A)**:

```
Phone / browser
      │
      │  "Sync Now"
      ▼
lyfstack.net  ──►  LyfStack API server
                         │
                         │  WebSocket already open (agent connected out)
                         │  { "type": "SYNC_NOW", "range": "since_last" }
                         ▼
                  Windows Agent (.exe)
                         │
                         │  then normal HTTPS upload (same as exe Sync now)
                         ▼
                  POST /api/v1/device-activity/sync
```

| Piece | Role |
|-------|------|
| **WebSocket** | Remote **control** only (`SYNC_NOW`, `PING`, `PAUSE`, `RESUME`) |
| **HTTPS POST** | Actual **data** transfer (sessions JSON) |

**Bottom line:** exe sync = push data. Website sync = server taps the open socket, then exe pushes data the same way.

### Is the previous POST still relevant for website Sync Now?

**Yes — required.** WebSocket does **not** replace POST.

- Website → server → WebSocket `SYNC_NOW` = “please sync now”
- Agent → `POST /api/v1/device-activity/sync?...` = “here is the activity data”

Whether sync is started from the **tray button** or from the **website**, both end with the **same POST**.  
Keep implementing/accepting that POST on LyfStack. Webhook.site is only a stand-in until the real API exists.

---

## Sync API (for LyfStack backend + website)

The agent **pushes** activity to LyfStack. The website does **not** call the PC directly.

### Data endpoint (HTTPS POST) — used by exe Sync now AND by website-triggered sync

```
POST /api/v1/device-activity/sync
```

Full URL example (set this in agent Settings → **SYNC ENDPOINT (HTTPS data)**):

```
https://api.lyfstack.app/api/v1/device-activity/sync
```

(Replace host with your real LyfStack API. For local testing you can keep a webhook.site URL.)

### Query parameters

| Param | Required | Description |
|-------|----------|-------------|
| `range` | no | Sync window. Default: `since_last` |
| `from` | no | ISO-8601 start (`2026-01-01` or `2026-01-01T00:00:00Z`). Used with `custom` or echoed for presets |
| `to` | no | ISO-8601 end (defaults to now when resolving custom) |

#### `range` values

| Value | Meaning |
|-------|---------|
| `since_last` | **Default.** Only sessions new/changed since last successful sync |
| `today` | Sessions started today (local day) |
| `week` | From Monday of this week |
| `month` | From 1st of this month |
| `year` | From Jan 1 of this year |
| `all` | All stored sessions |
| `custom` | Use `from` / `to` |

Aliases accepted by the agent: `last`/`pending`/`incremental` → `since_last`; `weekly` → `week`; `monthly` → `month`; `all_time` → `all`.

### Examples

```http
POST /api/v1/device-activity/sync?range=since_last
POST /api/v1/device-activity/sync?range=today
POST /api/v1/device-activity/sync?range=week
POST /api/v1/device-activity/sync?range=month
POST /api/v1/device-activity/sync?range=year
POST /api/v1/device-activity/sync?range=all
POST /api/v1/device-activity/sync?range=custom&from=2026-01-01&to=2026-01-31
```

### Device connection (WebSocket control) — for website → PC commands

Enable in agent Settings: **DEVICE CONNECTION** → Enabled + your `wss://…` URL.

1. Agent opens outbound `wss://api.lyfstack.app/device-connection`.
2. Website **Sync Now** tells the **server** to send `{ "type": "SYNC_NOW", "range": "since_last" }` on that socket.
3. Agent receives it, then `POST`s activity to `/api/v1/device-activity/sync` (same data endpoint as above).

Until LyfStack has a real WSS server, leave Device connection **Off**. Local Sync now still works via HTTPS POST.

#### WebSocket control protocol

Agent → server on connect:

```json
{ "type": "HELLO", "deviceId": "...", "device": "PC-NAME", "platform": "windows", "agentVersion": "1.0.0" }
```

Server → agent:

```json
{ "type": "SYNC_NOW", "range": "since_last", "requestId": "optional" }
{ "type": "SYNC_NOW", "range": "week" }
{ "type": "SYNC_NOW", "range": "custom", "from": "2026-01-01", "to": "2026-01-31" }
{ "type": "PING" }
{ "type": "PAUSE" }
{ "type": "RESUME" }
```

Agent → server replies: `PONG`, `SYNC_RESULT`, `STATUS`, `ERROR`.

Agent auto-reconnects with exponential backoff if the socket drops.

### Configure & test

| What | How |
|------|-----|
| **Data sync (exe → cloud)** | Set Sync endpoint (webhook or real API) → Sync now in agent → check receiver |
| **Remote Sync Now (web → PC)** | Needs LyfStack `wss://…/device-connection` first; enable Device connection; server sends `SYNC_NOW`; agent POSTs data |

When backend WebSocket is ready:

1. Agent Settings → Device connection **Online**
2. Server sends `{ "type": "SYNC_NOW", "range": "since_last" }`
3. Agent syncs and replies with `SYNC_RESULT`
4. Website reads stored activity from **your DB** (not from the PC)

### Request body (agent → LyfStack) — same for tray or website trigger

```json
{
  "source": "LyfStack.Agent.Windows",
  "deviceId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "device": "DESKTOP-NAME",
  "platform": "windows",
  "exportedAt": "2026-08-09T06:00:00Z",
  "aggregation": "usage_sessions",
  "sync": {
    "range": "since_last",
    "from": null,
    "to": null,
    "pendingOnly": true
  },
  "sessionCount": 2,
  "sessions": [
    {
      "id": "...",
      "applicationName": "Cursor",
      "processName": "Cursor",
      "processId": 1234,
      "startedAt": "2026-08-09T05:00:00Z",
      "endedAt": "2026-08-09T05:30:00Z",
      "activeDurationSeconds": 1500,
      "idleDurationSeconds": 300,
      "lastState": "Active",
      "isOpen": false
    }
  ]
}
```

### Suggested read API (website)

```
GET /api/v1/device-activity?deviceId={id}&range=week
GET /api/v1/device-activity?deviceId={id}&from=2026-01-01&to=2026-01-31
```

Same `range` / `from` / `to` semantics as above. Website displays what was already POSTed.

### Expected response (to POST)

- `2xx` — agent marks uploaded sessions as synced
- non-`2xx` — agent keeps them pending and shows failure

### What LyfStack backend must build

1. `wss://…/device-connection` — accept agent, map `deviceId` → socket  
2. Website **Sync Now** → find online device → send `SYNC_NOW`  
3. `POST /api/v1/device-activity/sync` — receive/store sessions (**still required**)  
4. Website reads activity from **DB**, not from the PC

## Agent CLI

```powershell
dotnet run --project src/LyfStack.Agent.Windows -- --tray
dotnet run --project src/LyfStack.Agent.Windows -- --headless
dotnet run --project src/LyfStack.Agent.Windows -- --install-startup
dotnet run --project src/LyfStack.Agent.Windows -- --uninstall-startup
dotnet run --project src/LyfStack.Agent.Windows -- --status
dotnet run --project src/LyfStack.Agent.Windows -- --stop
dotnet run --project src/LyfStack.Agent.Windows -- --list
```

| Flag | Description |
|------|-------------|
| `--interval <seconds>` | Sampling interval (default `5`) |
| `--idle-minutes <n>` | Idle threshold (default `5`) |
| `--webhook <url>` | Override sync endpoint for this run |
| `--db <path>` | Override SQLite database path |

## What it tracks

- Foreground window / process name
- Active vs idle time
- Categories (Work, Browser, Games, Entertainment, Communication, System, Other)
- Auto + manual category rules

**Privacy:** no keylogging, screenshots, or clipboard capture.

## Data location

```
%LocalAppData%\LyfStack\WindowsAgent\
  activity.db
  settings.json
  device.json
  last-sync.json
  agent.log
```

## Tests

```powershell
dotnet test LyfStack.Agent.Windows.sln
```

## Project layout

```
LyfStack.Agent.Windows.sln
src/LyfStack.Agent.Windows/
tests/LyfStack.Agent.Windows.Tests/
publish/   # local prod build only (gitignored) → lyfstack_agent_win.exe
```
