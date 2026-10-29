using System;
using System.Collections.Generic;

namespace XProtectCameraAudit.Model
{
    public enum SnapshotStatus
    {
        /// <summary>Frame timestamp within the staleness bound — green pill.</summary>
        Fresh,
        /// <summary>A recorded frame exists but is older than the staleness bound — amber pill.</summary>
        Stale,
        /// <summary>Camera has no recorded frame at all — red pill, placeholder image.</summary>
        NoRecordedFrame,
        /// <summary>Fetch failed (recorder unreachable, item unavailable, ...) — red pill, placeholder image.</summary>
        Error,
        /// <summary>Snapshot phase skipped (--skip-snapshots).</summary>
        Skipped,
        /// <summary>Camera or its hardware is disabled in XProtect — not fetched, not a fault.</summary>
        Disabled
    }

    public sealed class SnapshotResult
    {
        public SnapshotStatus Status;
        /// <summary>320px-wide JPEG, base64. Null when Status is NoRecordedFrame/Error/Skipped.</summary>
        public string ThumbnailBase64;
        /// <summary>Timestamp of the recorded frame the thumbnail came from (UTC).</summary>
        public DateTime? FrameTimestampUtc;
        /// <summary>Actual decoded frame size, e.g. "1920x1080". Authoritative, unlike driver config.</summary>
        public string FrameResolution;
        public string FailureReason;

        /// <summary>
        /// The camera's last good thumbnail from an earlier run (SnapshotArchive), set when
        /// this run got no frame: a camera broken for longer than the recordings are kept
        /// still shows where it points, so it can be found.
        /// </summary>
        public string ArchivedThumbnailBase64;
        public DateTime? ArchivedFrameUtc;

        public bool HasNoFrame
        {
            get { return Status == SnapshotStatus.NoRecordedFrame || Status == SnapshotStatus.Error; }
        }

        public static SnapshotResult Disabled()
        {
            return new SnapshotResult { Status = SnapshotStatus.Disabled, FailureReason = "disabled in XProtect" };
        }

        public static SnapshotResult Skipped()
        {
            return new SnapshotResult { Status = SnapshotStatus.Skipped, FailureReason = "snapshots skipped" };
        }

        public static SnapshotResult NoFrame()
        {
            return new SnapshotResult { Status = SnapshotStatus.NoRecordedFrame, FailureReason = "no recorded frame" };
        }

        public static SnapshotResult Failed(string reason)
        {
            return new SnapshotResult { Status = SnapshotStatus.Error, FailureReason = reason };
        }
    }

    public sealed class CameraAudit
    {
        public Guid CameraGuid;
        public string Name;
        /// <summary>The camera and its hardware both enabled; XProtect serves neither otherwise.</summary>
        public bool Enabled;
        public int Channel;
        public string Description;
        /// <summary>WKT, e.g. "POINT (12.34 56.78)"; empty when unset.</summary>
        public string GisPoint;
        /// <summary>Config hint only — in Corporate/Expert the rule engine drives recording,
        /// so recorded-frame presence (SnapshotResult) is the authoritative signal.</summary>
        public string RecordingEnabledHint;
        /// <summary>Best-effort from driver stream settings; drivers vary, may be empty.</summary>
        public string ConfiguredCodec;
        public string ConfiguredResolution;

        public SnapshotResult Snapshot = SnapshotResult.Skipped();
    }

    public sealed class HardwareAudit
    {
        public Guid HardwareGuid;
        public string Name;
        /// <summary>Device model/brand, e.g. "AXIS P3245-LVE".</summary>
        public string Model;
        /// <summary>Device IP/host as XProtect connects to it.</summary>
        public string Address;
        /// <summary>"AA:BB:CC:DD:EE:FF", for finding the switch port; "" when unknown or redacted.</summary>
        public string MacAddress = "";
        /// <summary>Where MacAddress came from: "XProtect" (driver settings) or "ARP".</summary>
        public string MacSource = "";
        public bool Enabled;
        /// <summary>Device login user XProtect uses — aids reconfiguring a replacement.</summary>
        public string UserName = "";
        /// <summary>Device password, read only with --passwords (needs the View Hardware
        /// Password permission); "" otherwise or when it could not be read.</summary>
        public string Password = "";
        public List<CameraAudit> Cameras = new List<CameraAudit>();
    }

    public sealed class RecorderAudit
    {
        public Guid RecorderGuid;
        public string Name;
        public string HostName;
        public bool Reachable = true;
        public List<HardwareAudit> Hardwares = new List<HardwareAudit>();
    }

    public sealed class SiteAudit
    {
        public string SiteName;
        /// <summary>XProtect product/version string for the report header; "" if unavailable.</summary>
        public string ProductVersion = "";
        public string ServerUri;
        public DateTime GeneratedAtUtc;
        public List<RecorderAudit> Recorders = new List<RecorderAudit>();
        /// <summary>How many recording servers the site has; more than Recorders.Count when --recorder narrowed it.</summary>
        public int RecordersOnSite;
    }
}
