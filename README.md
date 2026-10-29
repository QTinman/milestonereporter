# XProtect Camera Audit Tool

Read-only standalone tool that logs into a Milestone XProtect site, enumerates
every camera (including disabled ones), pulls a snapshot per camera from the
Recording Server via the MIP SDK, and emits a single self-contained HTML report
plus a companion CSV.

One process, one login, no API Gateway, no WebRTC, no PowerShell layer.

## How it works

- **Auth** — standalone MIP SDK login (`Environment.Initialize` → `AddServer` →
  `Login`). Token refresh is handled by `EnvironmentManager`; on a mid-run
  `NotAuthorized`, the tool re-logs-in exactly once, then hard-fails.
- **Enumeration** — Configuration Items API walk:
  `ManagementServer → RecordingServerFolder → HardwareFolder → CameraFolder`.
  Disabled cameras are included with an Enabled column ("licensed but
  forgotten" is high audit value). `RecordingEnabled` is reported as a *hint*
  only — in Corporate/Expert the rule engine drives recording, so the
  authoritative liveness signal is whether a recent recorded frame exists.
- **Snapshots (recorded-primary)** — one `JPEGVideoSource` per camera:
  `GetNearest(now)` (near-live on a recording camera), falling back to
  `GetAtOrBefore(now)`. Frame timestamp within the staleness bound → **green**;
  older frame → **amber**; no frame / fetch error → **red** with an inline SVG
  placeholder stating the reason. Reported resolution is decoded from the
  actual frame, not driver config. Live frames for non-recording cameras
  (`JPEGLiveSource`, event-driven) are Phase 2 and intentionally not in v1.
- **Threading** — hard rule honored: every `JPEGVideoSource` is created, used,
  and closed on a single thread. Concurrency is a bounded pool per recording
  server (default 8; more triggers ImageServer back-pressure).
- **Report** — one HTML file, base64-embedded 320px JPEG thumbnails (q=70,
  ~15–30 KB each; 500 cameras ≈ 10–15 MB). It opens with a **Milestone
  XProtect system-info block** (system, site, version, management server,
  generated-at, camera counts), then a section per recording server → heading
  per hardware → card per camera. Each card shows **name, snapshot, frame time,
  brand, IP, and user** (plus the device password with `--passwords`) — enough
  to recognize a physical camera in the field and source/configure a
  replacement. Disabled cameras (camera or hardware) are counted apart and
  listed last, not as faults. The CSV carries the same fields for pivoting.
- **Snapshot archive** — every run keeps each camera's last good thumbnail in
  `<output>\snapshots\<camera id>.jpg` (file time = frame time). A camera that
  breaks and stays broken longer than the recordings are kept still shows its
  **last known view** with its date, so the technician can find where it
  hangs and what it covered. Run the tool regularly (e.g. a weekly scheduled
  task) to keep the archive current.

> Two copies from one tool: the **customer copy** (`--redact`, or the default
> IP + username) and the **technician copy** (`--passwords`, device passwords in
> cleartext — treat the HTML and CSV as secrets). Reading passwords needs the
> **View Hardware Password** permission (2022 R1+); without it they show as
> `(unavailable)`.

## Build

Requires Windows and the .NET SDK (or Visual Studio). Target framework is
**.NET Framework 4.8**, x64 (MIP SDK native components are 64-bit only).

```
dotnet build XProtectCameraAudit.sln -c Release
```

Pin `MilestoneSystems.VideoOS.Platform.SDK` in
`src/XProtectCameraAudit/XProtectCameraAudit.csproj` to match the site's
XProtect version (23.x ≈ 2023, 24.x ≈ 2024, 26.x ≈ 2026). To target .NET 8
instead, switch `TargetFramework` to `net8.0-windows` and use an SDK package
version that ships .NET 8 assets (2024 R1+).

The machine running the tool must resolve and reach every recording server on
port **7563** (ImageServer), and the management server on 443/80.

## Deployment

**Deploy the whole build-output folder — the `.exe` will not run on its own.**
The MIP SDK loads ~60 dependencies from beside the executable (`Autofac.dll`,
`VideoOS.*.dll`, media toolkits, VC++ natives) and resolves most of them
dynamically at runtime, so there is no supported way to trim the set down to a
handful. Copy the entire `bin\Release\net48\` folder to the target machine
and run the exe from inside it. Copying only the exe fails immediately with
`Could not load file or assembly 'Autofac …'`.

The target machine also needs the **Visual C++ 2015–2022 x64 redistributable**
installed (the `*140*.dll` native runtime depends on it).

You can slim the deployed folder by deleting the locale subfolders
(`ar-SA` … `zh-TW`), `*.xml`, `*.pdb`, and — since the tool is headless — the
WPF UI assemblies (`VideoOS.Platform.SDK.UI.dll`, `VideoOS.UI.Common.WPF*.dll`,
`VideoOS.UI.manifest`). If you only ever run `--skip-snapshots`, the media
decode group (`libmfx*.dll`, `cudart*`, `nppc*`, `CoreToolkits.dll`,
`VideoOS.Toolkit.dll`, `AVIExporterClientMW.dll`, `xerces*`, `xqilla*`, the
`SDK.Export`/`SDK.Media` DLLs) can go too. Everything else is required.

```powershell
# Copy a slim deployment (no locales / docs / symbols) to C:\camreport\app
robocopy .\bin\Release\net48 C:\camreport\app /E /XF *.pdb *.xml *.txt `
  /XD ar-SA bg-BG cs-CZ da-DK de-DE es-ES fa-IR fi-FI fr-FR he-IL hi-IN hr-HR `
      hu-HU is-IS it-IT ja-JP ko-KR nb-NO nl-NL pl-PL pt-BR ru-RU sk-SK `
      sr-Latn-RS sv-SE th-TH tr-TR uk-UA zh-CN zh-TW
```

## Run

```
XProtectCameraAudit --server https://mgmt.example.com --auth basic --user auditor --customer Acme
XProtectCameraAudit --server https://mgmt.example.com --auth windows --user CORP\svc-audit --password-env AUDIT_PW
XProtectCameraAudit --server https://mgmt.example.com --auth windows-current --output C:\reports
```

| Flag | Default | Purpose |
|---|---|---|
| `--auth basic\|windows\|windows-current` | `basic` | Site auth mode. External IDP (Entra/Okta/Keycloak) is not supported in v1 — it requires a browser/device-code flow. |
| `--user`, `--password`, `--password-env`, `--domain` | prompt | Credentials. Password is prompted if not supplied. |
| `--customer <name>` | — | Customer/company name. Shown in the report header and baked into the filename (`acme-<site>-20260703.html`) so files self-identify in your records. |
| `--trust-cert-thumbprint <sha1>` | off | Trust exactly one self-signed server certificate (preferred over the option below). |
| `--trust-any-cert` | off | Accept any certificate — explicit, logged, never silent. Test environments only. |
| `--staleness-minutes <n>` | 60 | The primary liveness criterion: a frame older than this is amber, not green. |
| `--max-concurrent <n>` | 8 | Parallel snapshot fetches per recording server. |
| `--skip-snapshots` | off | Inventory-only run (no ImageServer traffic). |
| `--recorder <name\|host>` | all | Only this recording server, by name or host name (a short host name matches the FQDN). Repeat for several. A name that matches nothing stops the run and lists the site's servers. The report header and file name then say which servers it covers. |
| `--page-per-recorder` | off | One HTML page per recording server plus an index — use at 500+ cameras to avoid browser limits. |
| `--redact` | off | Omit hardware addresses and usernames from HTML and CSV. |
| `--passwords` | off | Include device passwords (technician copy, file name ends `-technician`). Not with `--redact`. |
| `--pdf` | off | Also write each HTML page as an A4 PDF, printed by Edge (or Chrome) headless — no extra install. |
| `--archive <dir>` / `--no-archive` | `<output>\snapshots` | Where each camera's last good snapshot is kept between runs, or keep none. |
| `--output`, `--name`, `--thumb-width` | `.`, `camera-audit`, 320 | Output location, base filename (when `--customer` is unset), and thumbnail width. |

The report opens with a **Needs attention** worklist: every camera with no
snapshot (offline or in need of replacement) is listed up front — camera name,
recording server, hardware, brand, IP, reason, and when it was last seen
(from the archive) — so a field engineer sees
what to visit before scrolling the full inventory. With `--customer` set, the
filename is `customer-site-yyyyMMdd`; a same-day re-run overwrites that day's
file (drop `--customer` for a timestamped name that never collides).

Exit codes: `0` every camera produced a frame, `2` completed but some cameras
have no frame or errored (check the red cards), `1` fatal (login, enumeration,
or repeated authorization failure).

## Output

- `camera-audit-<timestamp>.html` — the visual audit (self-contained, no
  external resources; safe to mail or archive as a single file).
- `camera-audit-<timestamp>.csv` — same rows, one per camera, for Excel/pivot,
  with the camera id (the archive's file name) and `LastSeenUtc`.
- `snapshots\` — the archive; keep it between runs, it is what remembers a
  dead camera's view.

## Notes for operators

- The account used should be a **read-only** role that can view all cameras and
  playback; the tool performs no writes.
- Cameras recording on motion/event will legitimately show amber with the last
  motion frame — the frame timestamp on the card makes the staleness visible.
- A red "no recorded frame" card means the camera does not record at all (or
  retention already purged everything); proving such a camera is *live* needs
  the Phase 2 `JPEGLiveSource` path.
- Approximate wall-clock: 500 cameras across 3 recording servers ≈ 60–90 s.
