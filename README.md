# LyfStack.Agent.Windows

Windows companion agent for [LyfStack](https://github.com/usmansohee). Tracks foreground app usage on your PC, stores sessions locally, and syncs activity to your LyfStack API.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for building)

## Run (dev)

```powershell
dotnet run --project src/LyfStack.Agent.Windows
```

### Publish self-contained exe

```powershell
dotnet publish src/LyfStack.Agent.Windows -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

Output: `publish\LyfStack.Agent.Windows.exe`

## Sync API (for LyfStack backend + website)

The agent **pushes** activity to LyfStack. The website does **not** call the PC directly.

### Endpoint

```
POST /api/v1/device-activity/sync
```

Full URL example (set this in agent Settings → Sync endpoint):

```
https://api.lyfstack.app/api/v1/device-activity/sync
```

(Replace host with your real LyfStack API.)

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

### Website “Sync now”

Recommended flow:

1. User clicks **Sync now** on LyfStack web (optionally with the same `range` query).
2. Website calls **your backend** (not the Windows PC), e.g. marks a sync request or simply refreshes stored activity.
3. The Windows agent either:
   - already auto-synced / user synced from the tray app, or
   - (future) polls `GET /api/v1/device-activity/sync-requests` and then `POST`s with the requested `range`.

Until pull-requests exist, treat website Sync now as **refresh from DB** after the agent has pushed.

### Request body (agent → LyfStack)

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

Same `range` / `from` / `to` semantics as above.

### Expected response

- `2xx` — agent marks uploaded sessions as synced
- non-`2xx` — agent keeps them pending and shows failure

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
publish/                         # local Release output (not committed)
```
