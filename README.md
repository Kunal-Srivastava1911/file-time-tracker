# Employee File-Time Tracker

Per-laptop tracking of how long each file is the **active foreground window**, split into **Active**
and **Idle** time, consolidated into a master Excel workbook in Dropbox.

Implements [design.md](design.md). Section references below (§n) point back to it.

---

## What is here

| Project | What it is |
| --- | --- |
| [src/FileTimeTracker.Core/](src/FileTimeTracker.Core/) | Domain: interval model, config, title parsing, the timing accumulator, SQLite buffer, Dropbox storage, DPAPI credential store |
| [src/FileTimeTracker.Agent/](src/FileTimeTracker.Agent/) | Per-laptop agent (§4.1–§4.3). Win32 capture, UI Automation, upload, heartbeat, deployment verbs |
| [src/FileTimeTracker.Tray/](src/FileTimeTracker.Tray/) | Desktop app: a person's own Start/Stop window. Same capture engine as the agent, but a session is started and stopped by hand, and each session is written as its own spreadsheet |
| [src/FileTimeTracker.Aggregator/](src/FileTimeTracker.Aggregator/) | Admin-machine aggregator (§4.5). Daily totals, master workbook, quarterly rollover |
| [src/FileTimeTracker.Watchdog/](src/FileTimeTracker.Watchdog/) | Liveness check that restarts a dead or wedged agent (§6) |
| [tests/FileTimeTracker.Tests/](tests/FileTimeTracker.Tests/) | 212 tests covering the §8 matrix |
| [installer/](installer/) | WiX v4 MSI and the single setup `.exe` that chains the .NET runtime (§6) |
| [docs/](docs/) | [Deployment guide](docs/DEPLOYMENT.md) and the [monitoring notice template](docs/MONITORING-NOTICE.md) |

The installer projects are deliberately outside `FileTimeTracker.sln` so the everyday
build-and-test loop does not pay for the WiX restore; `build.ps1 -Package` builds them.

## Build

Needs the .NET 8 SDK. For the installer also needs WiX v4:
`dotnet tool install --global wix --version 4.0.5`

```powershell
./build.ps1                    # build, test, publish
./build.ps1 -Package           # also build the MSI and setup .exe
./build.ps1 -SkipTests         # skip the test run
```

Output lands in `artifacts/`:

| Artifact | Role |
| --- | --- |
| `msi/FileTimeTracker.msi` | The interactive installer. Its UI prompts for the Computer No. (§6). Needs the .NET 8 Desktop Runtime already present. |
| `bundle/FileTimeTrackerSetup.exe` | The single setup `.exe` (§6). Chains the runtime, then installs the MSI with parameters — the silent/RMM path. |

Burn always drives a chained MSI with its own bootstrapper UI (WiX v4 removed
`MsiPackage/@DisplayInternalUI` with no replacement), so the Computer No. dialog cannot appear from
inside the bundle. That is why both artifacts exist rather than just the `.exe`; see
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#3-install-on-a-laptop).

## Try it without installing anything

```powershell
FileTimeTracker.Agent.exe provision --computer-no PC-TEST --time-zone "India Standard Time"
FileTimeTracker.Agent.exe --console
```

With no Dropbox credentials the agent tracks locally and prints each interval as it closes — this
is milestone 1 from §10, for validating accuracy before wiring up upload:

```
2026-07-20  09:14:30-09:22:10  Word               Q3 Report.docx        active=  460s idle=    0s  [DesktopTitle]
2026-07-20  09:22:10-09:27:10  Chrome             Timesheet - Intranet  active=    0s idle=  300s  [BrowserTab]
```

`FileTimeTracker.Agent.exe status` shows provisioning state, buffer depth and last heartbeat.

## How the timing works

The parts most worth understanding before trusting the numbers:

**Attribution is hold-forward.** The slice between two samples is credited to the state observed at
the *start* of the slice, because that is the state we knew held. Total wall time is conserved
exactly across a switch, and the error at any boundary is bounded by one sample interval.

**Active vs idle follows §3 literally.** "Input occurred within `idle_threshold`" is a *trailing
window*, so the five minutes before someone stops typing are genuinely active and are not
retroactively reclassified. The interval closes at the crossing and an idle one opens.

**Time nobody is at the machine is discarded, twice over.** Lock, sign-out and sleep are detected
from session and power notifications *and* the input desktop is re-checked every sample, so a
missed event self-heals within one tick. Separately, any gap between samples larger than
`MaxSampleGap` is dropped rather than credited — the safety net for a suspend the notification
missed.

**Intervals are split at local midnight.** §4.5 groups by local date; an interval left spanning
midnight would have to be assigned to one day arbitrarily.

**Interval duration is derived from `End - Start` at close time**, never by summing per-slice
roundings, so no rounding bias can accumulate over an eight-hour day.

## How delivery stays correct

The interval `id` (a UUID) is the idempotency key from the agent's SQLite buffer through to the
workbook. Rows are marked uploaded only *after* Dropbox acknowledges the write, so a crash in
between re-sends them — at-least-once on the wire, de-duplicated by id in the aggregator, which
adds up to exactly-once totals. Offline is not an error: rows stay buffered until the laptop
reconnects.

## Deliberate deviations from design.md

Three, each for a concrete reason. Everything else follows the spec.

**1. Per-machine logs are append-only chunks, not one file per machine (§4.3).**
The spec says the agent "appends" to `logs/<computer_no>.jsonl`. The Dropbox API has no append
operation, so that wording can only be implemented as download-modify-reupload of the whole file
every five minutes. By late in a quarter that is a tens-of-megabytes file, giving each laptop
gigabytes of daily upload for kilobytes of new data — and a connection dropped mid-write would
truncate that machine's entire history. Instead each upload writes an immutable chunk under
`logs/<computer_no>/<timestamp>-<id>.jsonl`. Only new bytes are sent, no write can damage existing
data, and two agents can never race. The aggregator reads *both* layouts, so a hand-placed flat
file still works. See [LogLayout](src/FileTimeTracker.Core/Storage/LogLayout.cs).

**2. `archive_grace_days` (default 30) was added to the config.**
§4.5 archives logs at the quarter boundary, but §4.3 allows a laptop to be offline indefinitely. A
machine that reconnects in week one of the new quarter uploads intervals dated to the old one, and
moving the logs on day zero would leave that data outside the workbook covering it. During the
grace window the archived workbook is rebuilt each run; afterwards the logs move and it is final.
Set it to `0` for the literal §4.5 behaviour.

**3. `DailyTotals` has a `Month` column, and Application is part of the grouping key.**
§5 asks the pivot to cut columns "by Date or Month", which needs a Month field to exist — ClosedXML
cannot author Excel's date grouping. And since §5 gives each row a single Application column, a
file opened in two applications on one day has to either split into two rows or have one
application chosen arbitrarily; splitting is the only option that keeps the column truthful. Totals
per computer/file/date are unchanged.

## Known limits

- **Full file paths are best-effort and usually blank** (§4.1 says as much). Office exposes no
  dependable document path through UI Automation. Paths are resolved reliably only for editors that
  put one in the title bar, such as Notepad++. The `source` column reports honestly which case a row is.
- **Browser URLs are deliberately not captured.** The address bar is reachable via UI Automation,
  but a full URL is a significant privacy escalation over the tab name §9 asks for, and §2's
  minimization posture does not sanction it. Web apps stay at tab-title granularity.
- **TallyPrime is represented as `Tally: <company>`** with `source = AppContext`, since it operates
  on a dataset rather than files. §9 flags this as a pilot decision — validate the title formats
  your installation actually produces and extend [AppRules](src/FileTimeTracker.Core/Parsing/AppRules.cs).
- **The embedded Dropbox credential across ~50 machines remains the main risk surface** (§4.3).
  Mitigated by app-folder scoping, DPAPI encryption, admin-only ACLs on the credential file, and
  rotation via re-running `provision`. Prefer a **PKCE** Dropbox app so no client secret is embedded
  at all — omit `--app-secret` and it is not stored.
- **What has and has not been run.** The agent was run end-to-end on Windows 11 and captured real
  intervals; all 155 tests pass; the MSI builds with every ICE validation passing and its file,
  property, custom-action, sequencing and launch-condition tables were verified. Not exercised
  here, because they need an elevated install on a target machine: the MSI's interactive dialog
  navigation, scheduled-task registration, the Defender exclusion, and a live Dropbox round trip.
  Walk those once during the pilot (§10) before fleet rollout.
- **The clean report layout is desktop-mode only, for now.** In single-user desktop mode the
  spreadsheets are the *only* thing written to Dropbox (see below); in fleet mode the sessions
  still land in `/sessions/` alongside the logs, config and heartbeat in the admin's app folder.
  Unifying the two is a known follow-up.

## Two ways it runs

The capture engine is shared; only *who decides when* and *where files land* differ.

**Fleet mode (§4.3)** is the headless deployment: an agent provisioned by the installer runs in the
background on every laptop, uploads to the admin's Dropbox over the API, and the admin runs the
aggregator for the consolidated workbook. This is what [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)
covers.

**Single-user desktop mode** is the [tray app](src/FileTimeTracker.Tray/): one person tracks
themselves, starting and stopping by hand. It needs no cloud credential — it writes straight to a
local folder, which is normally inside the user's own Dropbox. Two rules keep that folder readable:

- **Only finished spreadsheets go to Dropbox**, filed as `Employee Timeslots/<person>/TimeLog_…xlsx`
  — one folder per person, the name spelled out in full. The working files (the raw JSONL logs, the
  heartbeat and `config.json`) stay on the machine under `%LOCALAPPDATA%`, never in Dropbox. See
  [DropboxFolderLocator](src/FileTimeTracker.Core/Storage/DropboxFolderLocator.cs) and
  [StorageMigration](src/FileTimeTracker.Tray/StorageMigration.cs), which moves an older install
  onto this split on first launch.
- **Stopping is silent.** The spreadsheet is written and the window closes — no dialog, nothing
  opens. A write *failure* is still reported, because losing a session quietly would be worse.

## Legal

Employees are in India, devices are company-owned, and §2 requires a documented notice.
[docs/MONITORING-NOTICE.md](docs/MONITORING-NOTICE.md) is a drafting template with the technical
facts filled in correctly. **It is not legal advice and must be reviewed by counsel** before it is
issued — India's DPDP Act, 2023 applies.

There is no stealth behaviour anywhere in this codebase: the agent appears in Task Manager, its
tasks are visible and unhidden in Task Scheduler, and it is listed in Installed apps.
