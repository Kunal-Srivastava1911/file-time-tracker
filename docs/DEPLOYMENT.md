# Deployment guide

Covers the one-time Dropbox and aggregator setup, then per-laptop install, then day-two operations.
Section references (§n) point at [design.md](../design.md).

---

## 0. Before anything technical

§2 requires a documented notice and counsel review. Do that first — it is the gating item, not a
formality, and the DPDP Act's notice requirements shape what you can switch on in §4.4.

Fill in [MONITORING-NOTICE.md](MONITORING-NOTICE.md), have it reviewed, issue it, and decide:

- Is `capture_window_title` on? Titles routinely contain client names and subject lines.
- Is `capture_file_path` on?
- Is `hash_sensitive_fields` on? Totals still work; document names stop being readable.
- What retention applies to quarterly archives?

Those four answers become `config.json` in step 2.

---

## 1. Create the Dropbox apps

Create **two** scoped apps at <https://www.dropbox.com/developers/apps>.

### 1a. The agent app — app folder scope

Least privilege: it can only ever see its own folder (§4.3).

1. **Scoped access** → **App folder** → name it e.g. `FileTimeTracker`.
2. Permissions tab: `files.content.read`, `files.content.write`, `files.metadata.read`. Submit.
3. Settings tab: note the **App key**.

**Use PKCE and do not create a client secret.** An app secret shipped to ~50 laptops is the §4.3
risk surface; PKCE removes half of it. If you must use a confidential app, keep the secret and pass
`--app-secret` at provisioning time.

To mint a refresh token (PKCE), in a browser open:

```
https://www.dropbox.com/oauth2/authorize?client_id=<APP_KEY>&response_type=code&token_access_type=offline&code_challenge_method=plain&code_challenge=<VERIFIER>
```

where `<VERIFIER>` is a random 43–128 character string you keep. Approve, copy the code, then:

```powershell
curl.exe -X POST https://api.dropboxapi.com/oauth2/token `
  -d code=<CODE> -d grant_type=authorization_code `
  -d client_id=<APP_KEY> -d code_verifier=<VERIFIER>
```

The `refresh_token` in the response is what goes to the laptops. It does not expire; short-lived
access tokens are minted from it on demand.

### 1b. The aggregator app — full Dropbox scope

The aggregator reads every machine's logs and writes the master workbook, so it needs broader
access than any agent (§4.5). Same steps, but choose **Full Dropbox** (or an app folder shared with
the agents, if your Dropbox plan supports that layout). It lives on exactly one admin machine,
which is what keeps its blast radius small.

---

## 2. Set up the aggregator

On the always-on admin machine or VM, from an **elevated** prompt:

```powershell
FileTimeTracker.Aggregator.exe provision `
  --app-key <ADMIN_APP_KEY> `
  --refresh-token <ADMIN_REFRESH_TOKEN> `
  --time-zone "India Standard Time"

FileTimeTracker.Aggregator.exe init-config
```

`init-config` writes a starter `/config.json` (§4.4). Edit it in Dropbox to set the privacy toggles
you decided in step 0 and to fill in `computer_employee_map`:

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
  "archive_grace_days": 30,
  "show_first_run_notice": false,
  "excluded_processes": [],
  "computer_employee_map": {
    "PC-014": "Asha Nair",
    "PC-020": "Ravi Kumar"
  }
}
```

Agents pick changes up within `config_poll_sec` with no restart. A malformed file is rejected and
the previous config kept, so a typo cannot brick the fleet.

Verify without writing anything:

```powershell
FileTimeTracker.Aggregator.exe run --dry-run --out C:\temp\preview.xlsx
```

Then schedule it hourly (§4.5):

```powershell
schtasks /Create /TN "FileTimeTracker\Aggregate" /SC HOURLY `
  /TR "\"C:\Program Files\FileTimeTracker\FileTimeTracker.Aggregator.exe\" run" `
  /RU SYSTEM /RL HIGHEST /F
```

---

## 3. Install on a laptop

Two artifacts, two jobs. Pick by whether you want to be prompted.

| Artifact | Use it when | Prompts for Computer No.? | Installs the .NET runtime? |
| --- | --- | --- | --- |
| `FileTimeTracker.msi` | Installing by hand, one laptop at a time | **Yes** | No — must already be present |
| `FileTimeTrackerSetup.exe` | Silent or RMM push, or the runtime is missing | No — pass it on the command line | **Yes** |

> Burn always drives a chained MSI with its own bootstrapper UI — WiX v4 removed
> `MsiPackage/@DisplayInternalUI` with no replacement — so the MSI's Computer No. dialog cannot
> appear from inside the bundle. On a machine that has no runtime and where you want the prompt,
> run the bundle once to lay the runtime down, then run the MSI.

### Interactive (prompts for Computer No.)

Right-click `FileTimeTracker.msi` → **Install**, and accept the UAC prompt. The
**Machine and destination** page asks for the Computer No., the Dropbox app key and the refresh
token.

### Silent, for an RMM push (§6)

```powershell
FileTimeTrackerSetup.exe -quiet `
  ComputerNo=PC-014 `
  DropboxAppKey=<APP_KEY> `
  DropboxRefreshToken=<REFRESH_TOKEN> `
  HashSalt=<FLEET_SALT> `
  TimeZoneId="India Standard Time"
```

Or the MSI directly, when the runtime is already present:

```powershell
msiexec /i FileTimeTracker.msi /quiet /l*v install.log `
  COMPUTERNO=PC-014 DROPBOXAPPKEY=<KEY> DROPBOXREFRESHTOKEN=<TOKEN> HASHSALT=<SALT>
```

Installing with no Computer No. is refused rather than silently producing unattributable rows.

> **`HashSalt` must be identical on every laptop.** It is only used when
> `hash_sensitive_fields` is on, but a per-machine salt would make the same document hash
> differently per machine and silently break the aggregator's grouping. Generate one, record it in
> your password manager, and reuse it. Omit it and each machine generates its own — fine only while
> hashing stays off.

The installer, while elevated:

1. Installs the agent and watchdog to `C:\Program Files\FileTimeTracker`.
2. Writes `C:\ProgramData\FileTimeTracker\machine.json` and the DPAPI-encrypted `dropbox.dat`.
3. ACLs the root administrators-write / users-read, and `data\` user-writable.
4. Registers `FileTimeTracker\Agent` (at logon) and `FileTimeTracker\Watchdog` (every 5 minutes).
5. Adds a Defender exclusion for the install folder, unless `DEFENDEREXCLUSION=0`.

Both tasks run as the **Users** group with an interactive token. That is required, not stylistic: a
Session 0 service cannot read the interactive desktop's foreground window (§6).

### Verify

```powershell
FileTimeTracker.Agent.exe status
schtasks /Query /TN "FileTimeTracker\Agent"
```

Sign out and back in to trigger the logon task, then re-check `status` — `Heartbeat` should be
under a minute old and `Pending intervals` should climb.

---

## 4. Day two

### The agent is not recording

```powershell
FileTimeTracker.Agent.exe status
Get-Content C:\ProgramData\FileTimeTracker\data\agent.log -Tail 40
Get-Content C:\ProgramData\FileTimeTracker\data\watchdog.log -Tail 40
```

- *Heartbeat is old and no agent process* — the watchdog restarts it within five minutes. If
  `watchdog.log` shows repeated restarts it has hit its backoff after five failures in an hour;
  the underlying fault is in `agent.log`.
- *"No Dropbox credentials are provisioned"* — re-run `provision`. Tracking continued locally
  meanwhile, so nothing was lost.
- *"Could not decrypt the Dropbox credential store"* — the DPAPI blob was restored from another
  machine's image. Re-run `provision` on this machine.

### Rotating a leaked token

Revoke it in the Dropbox app console, mint a new one (step 1a), then on each laptop:

```powershell
FileTimeTracker.Agent.exe provision --computer-no PC-014 --app-key <KEY> --refresh-token <NEW>
```

`provision` merges into existing settings, so the Computer No. and salt survive. Restart the agent
or wait for the next logon.

### A machine has gone quiet

The workbook's **Run Info** sheet lists every machine's last heartbeat and highlights anything
silent for over 24 hours in pink, including machines in `computer_employee_map` that have never
reported. That is what makes a gap visible rather than looking like a quiet week.

### Reading the reports

- **`DailyTotals`** — one row per computer × date × application × document.
- **`Summary`** — pivot over it, refreshing on open. Swap the column field between `Date` and
  `Month` to re-cut.
- **`Source`** column — `File (from title)` and `File (path resolved)` are real documents;
  `Web/cloud tab`, `App dataset` and `Application only` are degraded and must not be read as
  filenames (§9).

### Quarterly rollover

Automatic (§4.5). At the first run after a quarter ends the aggregator writes
`archive/TimeLog_<YYYY>-Q<n>.xlsx` and the master drops back to the current quarter. Logs move to
`archive/logs/<quarter>/` once `archive_grace_days` has passed. Archives are kept, never deleted.

### Uninstall

Programs & Features, or `msiexec /x FileTimeTracker.msi /quiet`. Removes both scheduled tasks, the
Defender exclusion, the binaries, and the credential and machine files — a decommissioned laptop
keeps no usable token. Data already in Dropbox is left alone (§6).

---

## 5. Pilot checklist (§8, §10)

Two or three laptops for one week before fleet rollout. What to actually check:

- [ ] Totals look right against what those people say they did. Run `--console` for an hour beside
      real work and watch the intervals close.
- [ ] `idle_threshold_sec` suits the work. 300s over-counts active time for reading-heavy roles and
      under-counts for data entry.
- [ ] The **TallyPrime** rows are usable. §9 leaves this open — capture the real title formats your
      installation produces and extend `AppRules` if `Tally: <company>` is too coarse.
- [ ] Per-app title parsing covers the apps actually in use. Anything unhandled shows up as an
      unexpected `app` or `file` value; add a rule and a test.
- [ ] Lock, sleep and lid-close produce no phantom time. Lock for ten minutes and confirm the
      totals do not move.
- [ ] Offline buffering works. Disable Wi-Fi for an hour, keep working, reconnect, confirm nothing
      is missing and nothing is double-counted.
- [ ] Defender and any other endpoint agent leave the binary alone.
- [ ] Employees can see the agent in Task Manager and have the notice (§2).

## 6. Code signing

§6's recommended upgrade path. An Authenticode signature removes the need for the Defender
exclusion and stops SmartScreen warnings:

```powershell
./build.ps1 -Package -SigningCertThumbprint <THUMBPRINT>
```

This signs the executables and the MSI. A Burn bundle needs its engine detached and reattached:

```powershell
wix burn detach artifacts\bundle\FileTimeTrackerSetup.exe -engine engine.exe
signtool sign /sha1 <THUMBPRINT> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 engine.exe
wix burn reattach artifacts\bundle\FileTimeTrackerSetup.exe -engine engine.exe -o Setup-signed.exe
signtool sign /sha1 <THUMBPRINT> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 Setup-signed.exe
```

Once signed, install with `DEFENDEREXCLUSION=0` and drop the exclusion from existing machines:

```powershell
FileTimeTracker.Agent.exe defender-exclusion --remove
```
