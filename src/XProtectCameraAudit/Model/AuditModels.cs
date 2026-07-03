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
        Skipped
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
        public string Model;
        public string Address;
        public bool Enabled;
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
        public string ServerUri;
        public DateTime GeneratedAtUtc;
        public List<RecorderAudit> Recorders = new List<RecorderAudit>();
    }
}
