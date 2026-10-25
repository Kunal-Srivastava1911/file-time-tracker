# design.md — Employee File-Time Tracker (Windows 11)

Build specification. Turn this into a signed installer `.exe` for a fleet of under 50 company-owned Windows 11 laptops. All decisions below are locked from requirements gathering; items marked *pilot* are defaults to validate in a small trial before fleet rollout.

---

## 1. Purpose & scope
Track, per company-owned Windows 11 laptop, how long each file is the **active foreground window** during the workday — split into **Active** and **Idle** time — and consolidate **daily totals** into a master Excel workbook in Dropbox. Under 50 laptops, one person per laptop, installed manually with local admin.

## 2. Legal & privacy posture
- Employees are in India and will be **formally informed**; devices are **company-owned**. Operate under a documented notice/policy. **Get that policy reviewed by counsel — this document is not legal advice** (India's DPDP Act applies).
- **Data minimization:** `Window Title` and `File Path` are capturable but gated behind config toggles (`capture_window_title`, `capture_file_path`) that can hash or omit them.
- **No stealth / anti-detection.** The agent is visible in Task Manager and listed under installed programs. An optional first-run notice banner can be enabled.
- Only an **admin** controls configuration and data destination.

## 3. Definitions
- **Active time** — the file's window is the foreground window **and** keyboard/mouse input occurred within `idle_threshold` (default 300s).
- **Idle time** — the file's window is foreground but no input for ≥ `idle_threshold`.
- **Not counted** — screen locked, machine asleep/hibernating, no foreground window, or the logon screen.
- **"File"** — the document backing the foreground window, resolved best-effort (§4.1). Desktop apps that show the filename in the title bar are reliable; web/cloud apps degrade to tab/site name and are flagged in `source`.

## 4. Architecture
Three parts:
1. **Agent** — per laptop, runs in the interactive user session; samples, resolves, times, buffers locally, uploads.
2. **Aggregator** — one always-on admin machine (or small VM), scheduled; pulls all per-machine logs, builds daily totals, writes/rolls the master workbook.
3. **Central config** — `config.json` in the admin Dropbox app folder; controls all agents; admin-only.

**Data flow:** Agent → local SQLite buffer → per-machine log uploaded via Dropbox API → Aggregator downloads all → master `.xlsx` in Dropbox.

**Why not write one shared Excel directly:** concurrent writers corrupt `.xlsx` and create Dropbox "conflicted copy" files. Per-machine logs + central aggregation eliminates this.

### 4.1 Agent — capture
- Poll `GetForegroundWindow` every `sample_interval` (default 10s).
- Resolve process (`GetWindowThreadProcessId` → process name/path) and window title (`GetWindowText`).
- Extract filename from the title (app-specific parsing; strip ` - AppName` suffixes) and, where possible, the full path via **UI Automation** (best-effort; may be blank).
- Classify active vs idle via `GetLastInputInfo`.
- Detect lock/sleep via session + power notifications (`WTSRegisterSessionNotification`, `SystemEvents`) and pause accrual.
- Accumulate contiguous same-file focus into an **interval**; close it on file/app switch, idle transition, lock, or sleep.

### 4.2 Agent — local store (SQLite)
Table `intervals`:
`id (uuid) | computer_no | win_user | app | file | file_path | window_title | start_utc | end_utc | active_sec | idle_sec | source | uploaded (bool)`

SQLite is the crash-safe local buffer and survives offline periods.

### 4.3 Agent — uploader (Dropbox API — Option B)
- Uses a **scoped Dropbox app** (app-folder access) with a **refresh token**; short-lived access tokens are refreshed on demand.
- Token/refresh secret stored locally **encrypted with Windows DPAPI** (per-machine).
- Every `upload_interval` (default 5 min), append not-yet-uploaded intervals to a per-machine file `logs/<computer_no>.jsonl` via the Dropbox API; mark rows uploaded. **Idempotent** by interval `id`.
- Offline → rows stay buffered and upload on reconnect.
- **Security caveat:** an embedded credential across ~50 machines is the main risk surface. Mitigate with app-folder scoping (least privilege), DPAPI encryption, and rotation if a token leaks. Employees have no Dropbox folder access and cannot see each other's data or change the destination.

### 4.4 Config (`config.json`, admin Dropbox app folder)
Agents fetch on start and every `config_poll` (default 15 min); **hot-reload** without restart.

```json
{
  "sample_interval_sec": 10,
  "upload_interval_sec": 300,
  "idle_threshold_sec": 300,
  "config_poll_sec": 900,
  "capture_window_title": true,
  "capture_file_path": true,
  "hash_sensitive_fields": false,
  "logs_path": "/logs",
  "master_workbook_path": "/TimeLog_Current.xlsx",
  "archive_path": "/archive",
  "computer_employee_map": { "PC-014": "" }
}
```

Changing `master_workbook_path` / `logs_path` is the **"change destination"** capability — admin-only, via this file.

### 4.5 Aggregator
- Runs on one always-on admin machine (or small VM) as a Scheduled Task, e.g. hourly.
- Uses the **admin** Dropbox app (broader scope) to list/download all `logs/*` and read `config.json`.
- Computes **daily totals**: group by `computer_no × file × date(local)` → sum active/idle minutes.
- Writes the master workbook (§5). Recompute from source each run (or incremental).
- **Quarterly rollover:** at each quarter boundary, copy the current master to `archive/TimeLog_<YYYY>-Q<n>.xlsx`, then start a fresh current workbook; per-machine logs archived on the same cadence. **Archives kept by default** (switch to auto-delete-after-N if desired).

## 5. Master workbook
- **Sheet `DailyTotals`:** `Computer No. | Employee | Date | File / Document | Application | Active (min) | Idle (min)`
- **Sheet `Summary`:** PivotTable over `DailyTotals` (rows Computer/Employee + File; columns Date or Month; values sum of Active/Idle) — refreshes on open.
- `Employee` filled from `computer_employee_map`; blank if unmapped (keyed by Computer No. for now).

Per-machine raw log (`<computer_no>.jsonl`) retains full interval detail for drill-down:
`id | computer_no | win_user | app | file | file_path | window_title | start_utc | end_utc | active_sec | idle_sec | source`

## 6. Installer & autostart
- **Package:** MSI (WiX) or a single installer `.exe`; requires admin (UAC).
- Prompts for **Computer No.** (stamped on all rows); writes local machine config + DPAPI-encrypted Dropbox refresh token.
- **Autostart:** Scheduled Task **"At log on"** for the current user — must run in the interactive session to read the foreground window (a Session 0 service cannot). Runs hidden, restart-on-failure.
- **Watchdog:** a second Scheduled Task (or lightweight service) verifies the agent is alive every few minutes and restarts it; the agent writes a heartbeat so data gaps are visible.
- **Microsoft Defender:** add a documented folder/publisher **exclusion** for the agent path during the elevated install. Tradeoff — the excluded path isn't scanned, so lock it to admin-only write. **Upgrade path:** Authenticode **code-signing** removes the need for exclusions and stops SmartScreen warnings; recommended if the fleet grows.
- **Silent install** supported (`/quiet` + params) so a future RMM can push it.
- **Uninstall:** standard Programs & Features entry; removes tasks + agent (leaves synced data).

## 7. Recommended stack
- **C# / .NET 8 (Windows)** — Win32 interop (`user32.dll`) for foreground/input, **UI Automation** for paths, `Microsoft.Data.Sqlite`, official **Dropbox.NET SDK**, **ClosedXML** for `.xlsx`, **WiX v4** for the MSI, Task Scheduler API for autostart. Robust for a long-running agent; produces clean signed executables.
- **Alternative:** Python + `pywin32` + `uiautomation` + `dropbox` + `openpyxl`, packaged with **PyInstaller**. Faster to prototype; larger binary, weaker as a persistent agent.

## 8. Testing
- **Unit:** title→filename parsing per app (Word, Excel, PDF, code editors, TallyPrime); active/idle transitions; lock/sleep exclusion; idempotent upload.
- **Integration:** offline buffering + reconnect; config hot-reload; concurrent uploads from multiple machines; aggregation correctness against known intervals; quarter rollover.
- **Pilot:** 2–3 laptops for one week before fleet rollout.

## 9. Open items / assumptions
- Idle threshold and sample/upload intervals are sensible defaults — tune in the pilot.
- Archive retention = keep (change to auto-delete-after-N if desired).
- App-specific title parsing needs a per-app rule set. **TallyPrime** operates on a company dataset (vouchers/reports), not files — decide how to represent it (e.g. dataset/voucher context). *pilot*
- Web/cloud apps degrade to tab/site name — reflected honestly in reports.
- Dropbox token secret management across ~50 machines is the primary security caveat.

## 10. Milestones
1. Agent capture + SQLite + local console output (validate accuracy).
2. Dropbox upload + config + offline buffering.
3. Aggregator + master workbook + quarterly rollover.
4. Installer + autostart + watchdog + Defender step.
5. Pilot (2–3 machines, 1 week) → fix → fleet rollout.
