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
  per hardware → card per camera. Each card shows only: **name, snapshot,
  brand, IP, and Special notes (device username + password)**. The CSV carries
  the same fields for pivoting.

> ⚠ The report and CSV contain the device **username and password in
> cleartext** by design. Treat both files as secrets. Run with `--redact` to
> omit IP/username/password. Reading device passwords requires the login
> account to hold the **View Hardware Password** permission (XProtect 2022 R1+);
> without it the password shows as `(unavailable)` and everything else still
> works.

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

## Run

```
XProtectCameraAudit --server https://mgmt.example.com --auth basic --user auditor
XProtectCameraAudit --server https://mgmt.example.com --auth windows --user CORP\svc-audit --password-env AUDIT_PW
XProtectCameraAudit --server https://mgmt.example.com --auth windows-current --output C:\reports
```

| Flag | Default | Purpose |
|---|---|---|
| `--auth basic\|windows\|windows-current` | `basic` | Site auth mode. External IDP (Entra/Okta/Keycloak) is not supported in v1 — it requires a browser/device-code flow. |
| `--user`, `--password`, `--password-env`, `--domain` | prompt | Credentials. Password is prompted if not supplied. |
| `--trust-cert-thumbprint <sha1>` | off | Trust exactly one self-signed server certificate (preferred over the option below). |
| `--trust-any-cert` | off | Accept any certificate — explicit, logged, never silent. Test environments only. |
| `--staleness-minutes <n>` | 60 | The primary liveness criterion: a frame older than this is amber, not green. |
| `--max-concurrent <n>` | 8 | Parallel snapshot fetches per recording server. |
| `--skip-snapshots` | off | Inventory-only run (no ImageServer traffic). |
| `--page-per-recorder` | off | One HTML page per recording server plus an index — use at 500+ cameras to avoid browser limits. |
| `--redact` | off | Omit hardware addresses and recorder hostnames from HTML and CSV. |
| `--output`, `--name`, `--thumb-width` | `.`, `camera-audit`, 320 | Output location and naming. |

Exit codes: `0` every camera produced a frame, `2` completed but some cameras
have no frame or errored (check the red cards), `1` fatal (login, enumeration,
or repeated authorization failure).

## Output

- `camera-audit-<timestamp>.html` — the visual audit (self-contained, no
  external resources; safe to mail or archive as a single file).
- `camera-audit-<timestamp>.csv` — same rows, one per camera, for Excel/pivot.

## Notes for operators

- The account used should be a **read-only** role that can view all cameras and
  playback; the tool performs no writes.
- Cameras recording on motion/event will legitimately show amber with the last
  motion frame — the frame timestamp on the card makes the staleness visible.
- A red "no recorded frame" card means the camera does not record at all (or
  retention already purged everything); proving such a camera is *live* needs
  the Phase 2 `JPEGLiveSource` path.
- Approximate wall-clock: 500 cameras across 3 recording servers ≈ 60–90 s.
