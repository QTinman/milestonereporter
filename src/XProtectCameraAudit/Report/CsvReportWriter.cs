using System;
using System.Collections.Generic;
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
            var header = new List<string>
            {
                "RecordingServer","Hardware","CameraName","CameraId",
                "Brand","IP","UserName"
            };
            if (options.IncludePasswords) header.Add("Password");
            header.AddRange(new[] { "SnapshotStatus", "FrameTimestampUtc", "LastSeenUtc" });
            sb.AppendLine(string.Join(",", header));

            foreach (var recorder in site.Recorders)
            foreach (var hw in recorder.Hardwares)
            foreach (var cam in hw.Cameras)
            {
                var row = new List<string>
                {
                    Csv(recorder.Name), Csv(hw.Name), Csv(cam.Name), cam.CameraGuid.ToString("D"),
                    Csv(hw.Model),
                    Csv(options.Redact ? "" : hw.Address),
                    Csv(options.Redact ? "" : hw.UserName)
                };
                if (options.IncludePasswords) row.Add(Csv(hw.Password));
                row.Add(Csv(cam.Snapshot.Status.ToString()));
                row.Add(Time(cam.Snapshot.FrameTimestampUtc));
                // When the camera last gave a picture: this run's frame, else the archived one.
                row.Add(Time(cam.Snapshot.FrameTimestampUtc ?? cam.Snapshot.ArchivedFrameUtc));
                sb.AppendLine(string.Join(",", row));
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        static string Time(DateTime? utc)
        {
            return utc.HasValue
                ? utc.Value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : "";
        }

        static string Csv(string value)
        {
            value = value ?? "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
