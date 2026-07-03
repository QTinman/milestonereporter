# # XProtect Camera Audit Tool — Build Spec

Read-only tool that logs into Milestone XProtect, enumerates every camera, and
emits a single self-contained report (per-camera details + snapshot thumbnail).
Reuses primitives from the existing ANPRMib plugin repo.

**Path:** MIP SDK native .NET, standalone application. No API Gateway, no WebRTC,
no PowerShell layer. One process, one login, all frames pulled from the Recording
Server via the MIP SDK.

> This spec supersedes the earlier thread output. The snapshot design there was
> built on `JPEGVideoSource.GetLive()`, which does not exist — corrected below.

---

## 1. Reuse map (from the ANPRMib repo)

| Concern | Action | Source |
|---|---|---|
| Config enumeration | Lift verbatim | `ResolveVideoCameraFromMetadata`, `ANPRViewItemWpfUserControl.xaml.cs:1168` |
| Recorded JPEG snapshot | Lift verbatim | `FetchRecordedFrame`, `xaml.cs:1104` (`JPEGVideoSource` → `Init()` → `GetAtOrBefore(ts)` → `ConvertToBitmapSource()`) |
| Image resize / re-encode | Lift | `ResizeImageBase64` (System.Drawing, q=70); move to `ANPRMib.Common` |
| HTML/CSS report shell | Steal structure | `ReportGenerator.GetReportCss()`, `ReportGenerator.cs:24` — write new `GenerateCameraAudit(...)` |
| Direct-from-camera HTTP JPEG | Skip for v1 | `FetchImageFromCamera` / `ResolveBasicAuthHeader`, `ANPRBackgroundPlugin.cs:2503` |

**Write new:** standalone login, field extraction (enabled/state/GPS), per-recording-
server grouping section, offline handling, placeholder image.

The snapshot primitive lives in the **SmartClient** assembly, which matters:
`JPEGVideoSource` is supported only in Smart Client and standalone environments —
**not** in a server/Event-Server plugin. A standalone `.exe` is a supported host,
so lifting from the SmartClient side into a standalone tool is correct.

---

## 2. Auth

Standalone client login:

```
VideoOS.Platform.SDK.Environment.Initialize();
VideoOS.Platform.SDK.Environment.AddServer(serverUri, credential);
EnvironmentManager.Instance.Login(serverUri);        // then read .MasterSite
```

Credential by site mode (confirm mode per deployment — see §7):
- **XProtect Basic (internal IDP):** `NetworkCredential(user, pass)`, Basic IDP.
- **Windows:** `CredentialCache.DefaultNetworkCredentials` or explicit `NetworkCredential`.
- **External IDP (Entra / Okta / Keycloak):** `LoginProviderInformation` with the
  `IdentityProviderId`; browser or device-code flow. Hardest to embed in a headless tool.

Token refresh is handled by `EnvironmentManager` internally. On a `NotAuthorizedException`
from any SDK call: re-`Login` once, then hard-fail.

---

## 3. Enumeration

Walk (Configuration Items API):

```
new ManagementServer(EnvironmentManager.Instance.MasterSite)
  .RecordingServerFolder.RecordingServers        // rs
    → rs.HardwareFolder.Hardwares                // hw
      → hw.CameraFolder.Cameras                  // camera
```

Collect per camera: `Id`, `Name`, `Enabled`, `Channel`, `Description`, `GisPoint`
(WKT or empty). Per hardware: `Model`, `Address`, `Id`. Per recording server:
`Name`, `HostName`, `Id`, `Enabled`.

Stream metadata (`Resolution`, `Codec`) via
`camera.StreamFolder.Streams[0].CameraSettingsFolder.CameraSettings[...]` — wrap
each property read in try/catch; drivers vary and missing props are non-fatal.

**Include disabled cameras**, with an Enabled column. "Licensed but forgotten"
is high audit value; filtering later is trivial.

> ⚠ **Verify `camera.RecordingEnabled` before relying on it.** In Corporate/Expert,
> recording is driven by the rule engine, not a single per-camera boolean, so this
> flag may not exist or may not be authoritative. Preferred signal: whether
> `GetAtOrBefore(recentBound)` returns data (see §4). Treat any config flag as a hint.

---

## 4. Snapshot (corrected — recorded-primary)

The reusable, supported, already-working primitive is the **recorded** source, and
it can serve near-live frames. `GetNearest` is documented to step back a few seconds
rather than return null when issued close to real time — so on a recording camera it
is effectively a live frame.

**Primary path (reuses `FetchRecordedFrame` verbatim):**

```
var src = new JPEGVideoSource(cameraItem);   // VideoOS.Platform.Data
src.Init();
var jpg = src.GetNearest(DateTime.UtcNow) as JPEGData;   // near-live on a recording cam
// fallback within staleness bound:
jpg ??= src.GetAtOrBefore(DateTime.UtcNow.AddMinutes(-STALENESS)) as JPEGData;
```

**Per-camera decision tree:**
- Recording continuously → `GetNearest(now)` gives a near-live frame. Done.
- Recording on motion/event → `GetAtOrBefore(now)` gives the last motion frame
  (may be stale). Report the frame timestamp so staleness is visible.
- Not recording at all → no recorded frame. v1: mark "no recorded frame" and use
  the placeholder. (True live-ness proof for these cameras is Phase 2, below.)
- Both return null → placeholder.

**Phase 2 (only if needed): live frames for non-recording cameras.** Live JPEGs come
from `JPEGLiveSource` (`VideoOS.Platform.Live`) — a **different, event-driven** class,
not a synchronous getter. Construct with the camera Item, `Init()`, subscribe to the
live-content event, capture one frame, dispose. Do not attempt this as a "tweak" to
`JPEGVideoSource`; it is separate code. Defer unless the audit must prove liveness on
cameras that don't record.

**Thumbnail:** 320 px wide, aspect preserved, JPEG q=70 (reuse `ResizeImageBase64`);
~15–30 KB each. 500 cameras ≈ 10–15 MB single-file HTML.

**Placeholder:** inline SVG camera icon with failure reason overlaid
("no recent frame" / "recording server unreachable"). No external `<img src>` — the
report must be one self-contained file.

---

## 5. Concurrency & threading

> ⚠ **Hard rule:** all calls to a given `JPEGVideoSource` instance must come from the
> **same thread**. You may run N threads in parallel, but each must own its own source
> instance end-to-end. Never share a source across threads — doing so causes
> intermittent, hard-to-repro failures.

- One `JPEGVideoSource` per camera, used entirely within one worker thread.
- Bounded pool: ~8 concurrent per recording server; more triggers `ImageServer`
  back-pressure.
- 500 cameras / 3 recording servers ≈ 60–90 s wall-clock.

---

## 6. Report

Single-file **HTML** with base64-embedded thumbnails.
- Not PDF (adds headless-Chrome/wkhtmltopdf dep, doubles size — add as a final
  render-to-PDF pass only if hard-required).
- Not XLSX for the visual audit (embedded-image handling is painful); emit a
  **companion CSV** of the same rows for pivoting.

**Structure:** `<section>` per recording server → `<h2>` per hardware → card per
camera. Sort cameras by channel, hardware by IP.

**Card:** 320×180 thumbnail left; right column Name / GUID / Enabled / Recording /
Resolution+Codec / GPS / frame-timestamp. Status pill: green = fresh frame,
amber = stale/fallback frame, red = placeholder. Reuse `GetReportCss()` colour tokens.

**Header:** site name, generated-at, counts (total / enabled / recording / offline).

---

## 7. Gating questions (answer these four before building)

1. **Site auth mode** — Basic / Windows / External IDP (which provider). Sets the
   credential class and whether a browser/device-code flow is needed.
2. **Certificate setup** — self-signed vs. trusted CA. If self-signed, the runtime
   needs an explicit `ServerCertificateValidationCallback` opt-in (never silent).
3. **Target runtime** — .NET Framework 4.8 (repo parity) or .NET 8.
4. **Camera count** — <100 single-threaded fine; 100–500 bounded concurrency;
   500+ page the report per recording server to avoid browser limits.

Also settle: **staleness bound** for a "recent" frame — this is now the *primary*
liveness criterion, not just a fallback, so it's more load-bearing than before
(5 min / 1 h / 24 h / never).

Non-blocking but relevant: XProtect version (pin the MIP SDK NuGet to match, e.g.
2023 R1+ for 25.3.*), where the tool runs (recording-server hostnames must resolve
and be reachable on port **7563**), sensitive-field redaction (`hw.UserName`,
hostnames), and trigger model (CLI / scheduled task / Management Client menu).

---

## 8. Estimate

Two-day build once §7 (1–4) are answered, reusing the four repo primitives. Phase 2
(`JPEGLiveSource` for non-recording cameras) adds ~half a day and is optional.
