# Employee monitoring notice — TEMPLATE

> **This is a drafting template, not legal advice.** design.md §2 requires that this notice be
> reviewed by counsel before it is issued. India's Digital Personal Data Protection Act, 2023
> applies to the personal data processed here, and the requirements around notice, purpose
> limitation, retention and grievance redressal are specific. Do not issue this document as-is.
>
> Placeholders in `{{ }}` must be completed before circulation.

---

## 1. What is installed

The laptop issued to you by {{COMPANY NAME}} has **Employee File-Time Tracker** installed. It is
listed in **Settings → Apps → Installed apps** and its process, `FileTimeTracker.Agent.exe`, is
visible in **Task Manager**. Its scheduled tasks are visible in **Task Scheduler** under the
`FileTimeTracker` folder. Nothing about it is concealed.

## 2. What it records

Every {{SAMPLE INTERVAL, default 10}} seconds it notes which window is in the foreground and
records, for each document:

| Recorded | Detail |
| --- | --- |
| Computer No. | The asset number of this laptop, e.g. `PC-014` |
| Windows user name | The account signed in |
| Application | e.g. Word, Excel, Chrome, TallyPrime |
| Document or tab name | e.g. `Q3 Report.docx`. For websites and cloud apps this is the browser tab title, not the address |
| Full file path | {{ENABLED / DISABLED}} — controlled centrally by `capture_file_path` |
| Window title | {{ENABLED / DISABLED}} — controlled centrally by `capture_window_title` |
| Start and end time | Of each continuous period of focus |
| Active time | Foreground **and** keyboard or mouse input within the last {{IDLE THRESHOLD, default 5}} minutes |
| Idle time | Foreground but no input for {{IDLE THRESHOLD, default 5}} minutes or more |

## 3. What it does **not** record

- **No keystrokes.** No keylogging of any kind. Only *whether* input occurred, never what was typed.
- **No screen contents, screenshots or screen recording.**
- **No web addresses.** Browser activity is recorded at tab-title granularity only.
- **No email, message or document contents.**
- **No camera, microphone or location.**
- **No activity while the machine is locked, asleep, or at the sign-in screen.** Those periods are
  excluded from the totals entirely.

## 4. Why

{{STATE THE PURPOSE — e.g. project time allocation, client billing accuracy, workload distribution.
Be specific. A vague purpose such as "productivity monitoring" is unlikely to satisfy the DPDP
Act's purpose-limitation requirement.}}

## 5. Where the data goes and who can see it

Daily totals are consolidated into a workbook held in a {{COMPANY NAME}} Dropbox folder that only
{{ROLE, e.g. the IT administrator and the Operations Manager}} can access. You cannot see other
employees' data, and other employees cannot see yours. Only an administrator can change the
configuration or the destination.

## 6. How long it is kept

Per-machine detail and quarterly workbooks are retained for {{RETENTION PERIOD}}. The default
configuration keeps quarterly archives indefinitely; confirm the period that applies to you with
{{CONTACT}}.

## 7. Working hours

{{STATE WHETHER THE AGENT RUNS OUTSIDE WORKING HOURS. It records whenever the machine is in use
and unlocked. If personal use outside working hours is permitted on this device, say so and say how
that data is handled.}}

## 8. Your rights and how to raise a concern

Under the DPDP Act you may request access to, and correction of, your personal data, and may raise
a grievance. Contact {{DATA PROTECTION / GRIEVANCE CONTACT, NAME AND EMAIL}}.

## 9. Acknowledgement

{{DELETE IF NOT REQUIRED}}

I confirm I have read and understood this notice.

Name: ______________________  Computer No.: ____________  Date: ____________

Signature: ______________________
