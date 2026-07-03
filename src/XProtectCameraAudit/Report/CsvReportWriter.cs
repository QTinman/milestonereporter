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
                "RecordingServer","Hardware","CameraName",
                "Brand","IP","UserName","Password",
                "SnapshotStatus","FrameTimestampUtc"
            }));

            foreach (var recorder in site.Recorders)
            foreach (var hw in recorder.Hardwares)
            foreach (var cam in hw.Cameras)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(recorder.Name), Csv(hw.Name), Csv(cam.Name),
                    Csv(hw.Model),
                    Csv(options.Redact ? "" : hw.Address),
                    Csv(options.Redact ? "" : hw.UserName),
                    Csv(options.Redact ? "" : hw.Password),
                    Csv(cam.Snapshot.Status.ToString()),
                    Csv(cam.Snapshot.FrameTimestampUtc.HasValue
                        ? cam.Snapshot.FrameTimestampUtc.Value.ToString("yyyy-MM-dd HH:mm:ss") : "")
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
