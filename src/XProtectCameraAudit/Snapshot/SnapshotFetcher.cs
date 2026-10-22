using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoOS.Platform;
using VideoOS.Platform.Data;
using XProtectCameraAudit.Auth;
using XProtectCameraAudit.Imaging;
using XProtectCameraAudit.Model;

namespace XProtectCameraAudit.Snapshot
{
    /// <summary>Second NotAuthorized after the single re-login — abort the run (§2).</summary>
    public sealed class AuditFatalException : Exception
    {
        public AuditFatalException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Recorded-primary snapshot fetch (§4). GetNearest(now) steps back a few seconds on a
    /// recording camera — effectively a live frame. GetAtOrBefore(now) covers motion/event
    /// recording (last recorded frame, possibly stale). No frame at all → placeholder;
    /// proving liveness on non-recording cameras is Phase 2 (JPEGLiveSource) and out of v1.
    /// </summary>
    public sealed class SnapshotFetcher
    {
        readonly AuditOptions _options;
        readonly LoginManager _login;
        int _done;
        int _total;

        public SnapshotFetcher(AuditOptions options, LoginManager login)
        {
            _options = options;
            _login = login;
        }

        public void FetchAll(SiteAudit site)
        {
            var byRecorder = site.Recorders
                .Select(r => new
                {
                    Recorder = r,
                    // Disabled ones have no item to fetch from; they keep their Disabled status.
                    Cameras = r.Hardwares.SelectMany(h => h.Cameras).Where(c => c.Enabled).ToList()
                })
                .Where(x => x.Cameras.Count > 0)
                .ToList();

            _total = byRecorder.Sum(x => x.Cameras.Count);
            _done = 0;
            Console.WriteLine("Fetching snapshots for {0} cameras ({1} per recorder in parallel)...",
                _total, _options.MaxConcurrentPerRecorder);

            var tasks = new List<Task>();
            foreach (var group in byRecorder)
            {
                // Bounded pool per recording server (§5) — more than ~8 concurrent
                // pulls per recorder triggers ImageServer back-pressure.
                var gate = new SemaphoreSlim(_options.MaxConcurrentPerRecorder);
                foreach (var camera in group.Cameras)
                {
                    var cam = camera;
                    var rec = group.Recorder;
                    tasks.Add(Task.Run(() =>
                    {
                        gate.Wait();
                        try
                        {
                            // Entire body is synchronous: the JPEGVideoSource is created,
                            // used, and closed on this one thread (§5 hard rule).
                            cam.Snapshot = CaptureWithReloginRetry(cam, rec);
                        }
                        finally
                        {
                            gate.Release();
                            int n = Interlocked.Increment(ref _done);
                            Console.WriteLine("  [{0}/{1}] {2} — {3}", n, _total, cam.Name, Describe(cam.Snapshot));
                        }
                    }));
                }
            }

            try
            {
                Task.WaitAll(tasks.ToArray());
            }
            catch (AggregateException ax)
            {
                var fatal = ax.Flatten().InnerExceptions.OfType<AuditFatalException>().FirstOrDefault();
                if (fatal != null) throw fatal;
                throw;
            }
        }

        SnapshotResult CaptureWithReloginRetry(CameraAudit cam, RecorderAudit recorder)
        {
            try
            {
                return Capture(cam, recorder);
            }
            catch (Exception ex) when (IsNotAuthorized(ex))
            {
                if (!_login.TryReloginOnce())
                    throw new AuditFatalException("session not authorized and re-login already spent", ex);
                try
                {
                    return Capture(cam, recorder);
                }
                catch (Exception retryEx) when (IsNotAuthorized(retryEx))
                {
                    throw new AuditFatalException("still not authorized after re-login", retryEx);
                }
            }
        }

        SnapshotResult Capture(CameraAudit cam, RecorderAudit recorder)
        {
            if (cam.CameraGuid == Guid.Empty)
                return SnapshotResult.Failed("camera id missing in configuration");

            Item item = Configuration.Instance.GetItem(cam.CameraGuid, Kind.Camera);
            if (item == null)
                return SnapshotResult.Failed(recorder.Reachable
                    ? "camera item not available (disabled?)"
                    : "recording server unreachable");

            JPEGVideoSource source = null;
            try
            {
                source = new JPEGVideoSource(item);
                source.Init();

                var now = DateTime.UtcNow;
                var jpeg = source.GetNearest(now) as JPEGData;
                if (jpeg == null || jpeg.Bytes == null || jpeg.Bytes.Length == 0)
                    jpeg = source.GetAtOrBefore(now) as JPEGData;
                if (jpeg == null || jpeg.Bytes == null || jpeg.Bytes.Length == 0)
                    return SnapshotResult.NoFrame();

                string resolution;
                string thumb = ImageHelper.ResizeToBase64Jpeg(jpeg.Bytes, _options.ThumbnailWidth, out resolution);

                // MIP timestamps are UTC but may arrive with Kind=Unspecified; a blind
                // ToUniversalTime() would shift them by the local offset.
                var ts = jpeg.DateTime.Kind == DateTimeKind.Local
                    ? jpeg.DateTime.ToUniversalTime()
                    : DateTime.SpecifyKind(jpeg.DateTime, DateTimeKind.Utc);
                var age = now - ts;
                bool fresh = age <= TimeSpan.FromMinutes(_options.StalenessMinutes);
                return new SnapshotResult
                {
                    Status = fresh ? SnapshotStatus.Fresh : SnapshotStatus.Stale,
                    ThumbnailBase64 = thumb,
                    FrameTimestampUtc = ts,
                    FrameResolution = resolution,
                    FailureReason = fresh ? null : string.Format("last frame {0:F0} min old", age.TotalMinutes)
                };
            }
            catch (Exception ex) when (!IsNotAuthorized(ex))
            {
                return SnapshotResult.Failed(ShortReason(ex));
            }
            finally
            {
                try { if (source != null) source.Close(); }
                catch { /* teardown only */ }
            }
        }

        static bool IsNotAuthorized(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e.GetType().Name.IndexOf("NotAuthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        static string ShortReason(Exception ex)
        {
            var name = ex.GetType().Name;
            if (name.IndexOf("Communication", StringComparison.OrdinalIgnoreCase) >= 0)
                return "recording server unreachable";
            var msg = ex.Message ?? name;
            return msg.Length > 120 ? msg.Substring(0, 119) + "…" : msg;
        }

        static string Stamp(SnapshotResult result)
        {
            return result.FrameTimestampUtc.HasValue
                ? result.FrameTimestampUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z"
                : "?";
        }

        static string Describe(SnapshotResult result)
        {
            switch (result.Status)
            {
                case SnapshotStatus.Fresh: return "fresh (" + Stamp(result) + ")";
                case SnapshotStatus.Stale: return "STALE (" + Stamp(result) + ")";
                case SnapshotStatus.NoRecordedFrame: return "no recorded frame";
                case SnapshotStatus.Error: return "ERROR: " + result.FailureReason;
                default: return "skipped";
            }
        }
    }
}
