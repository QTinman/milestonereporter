using System;
using System.IO;
using System.Text;
using XProtectCameraAudit.Model;

namespace XProtectCameraAudit.Report
{
    /// <summary>Companion CSV of the same rows for pivoting (§6) — no images.</summary>
    public static class CsvReportWriter
    {
        public static void Write(SiteAudit site, AuditOptions options, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", new[]
            {
                "RecordingServer","RecorderHost","RecorderReachable",
                "Hardware","HardwareModel","HardwareAddress","HardwareEnabled",
                "Channel","CameraName","CameraId","CameraEnabled","RecordingEnabledHint",
                "Resolution","ConfiguredCodec","GisPoint",
                "SnapshotStatus","FrameTimestampUtc","FrameAgeMinutes","FailureReason"
            }));

            foreach (var recorder in site.Recorders)
            foreach (var hw in recorder.Hardwares)
            foreach (var cam in hw.Cameras)
            {
                string age = "";
                if (cam.Snapshot.FrameTimestampUtc.HasValue)
                    age = ((site.GeneratedAtUtc - cam.Snapshot.FrameTimestampUtc.Value).TotalMinutes)
                        .ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

                string resolution = string.IsNullOrEmpty(cam.Snapshot.FrameResolution)
                    ? cam.ConfiguredResolution : cam.Snapshot.FrameResolution;

                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(recorder.Name), Csv(options.Redact ? "" : recorder.HostName), Csv(recorder.Reachable ? "yes" : "no"),
                    Csv(hw.Name), Csv(hw.Model), Csv(options.Redact ? "" : hw.Address), Csv(hw.Enabled ? "yes" : "no"),
                    Csv(cam.Channel.ToString()), Csv(cam.Name), Csv(cam.CameraGuid.ToString()),
                    Csv(cam.Enabled ? "yes" : "no"), Csv(cam.RecordingEnabledHint),
                    Csv(resolution), Csv(cam.ConfiguredCodec), Csv(cam.GisPoint),
                    Csv(cam.Snapshot.Status.ToString()),
                    Csv(cam.Snapshot.FrameTimestampUtc.HasValue
                        ? cam.Snapshot.FrameTimestampUtc.Value.ToString("yyyy-MM-dd HH:mm:ss") : ""),
                    Csv(age), Csv(cam.Snapshot.FailureReason ?? "")
                }));
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        static string Csv(string value)
        {
            value = value ?? "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
