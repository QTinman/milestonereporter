using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using XProtectCameraAudit.Imaging;
using XProtectCameraAudit.Model;

namespace XProtectCameraAudit.Report
{
    /// <summary>
    /// Single self-contained HTML file (§6): base64-embedded thumbnails, no external
    /// resources. Structure: section per recording server → heading per hardware →
    /// card per camera. Layout/colour tokens follow the ANPRMib report shell.
    /// </summary>
    public static class AuditReportGenerator
    {
        public static string GenerateCameraAudit(SiteAudit site, AuditOptions options)
        {
            var sb = new StringBuilder(1 << 20);
            sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>" + H("Camera Audit — " + site.SiteName) + "</title>");
            sb.AppendLine("<style>" + GetReportCss() + "</style></head><body>");

            AppendHeader(sb, site, options);
            foreach (var recorder in site.Recorders)
                AppendRecorderSection(sb, recorder, options);

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        /// <summary>Index page for --page-per-recorder mode (500+ cameras, §7.4).</summary>
        public static string GenerateIndex(SiteAudit site, AuditOptions options, IList<KeyValuePair<RecorderAudit, string>> pages)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>" + H("Camera Audit — " + site.SiteName) + "</title>");
            sb.AppendLine("<style>" + GetReportCss() + "</style></head><body>");
            AppendHeader(sb, site, options);
            sb.AppendLine("<section class=\"recorder\"><h1>Recording servers</h1><ul class=\"index\">");
            foreach (var page in pages)
            {
                var cams = page.Key.Hardwares.SelectMany(h => h.Cameras).ToList();
                sb.AppendFormat("<li><a href=\"{0}\">{1}</a> <span class=\"muted\">{2} — {3} cameras</span></li>\n",
                    H(page.Value), H(page.Key.Name), H(page.Key.HostName), cams.Count);
            }
            sb.AppendLine("</ul></section></body></html>");
            return sb.ToString();
        }

        public static string GenerateRecorderPage(SiteAudit site, RecorderAudit recorder, AuditOptions options)
        {
            var sb = new StringBuilder(1 << 20);
            sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>" + H("Camera Audit — " + recorder.Name) + "</title>");
            sb.AppendLine("<style>" + GetReportCss() + "</style></head><body>");
            AppendHeader(sb, site, options);
            AppendRecorderSection(sb, recorder, options);
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        static void AppendHeader(StringBuilder sb, SiteAudit site, AuditOptions options)
        {
            var cams = site.Recorders.SelectMany(r => r.Hardwares).SelectMany(h => h.Cameras).ToList();
            int total = cams.Count;
            int enabled = cams.Count(c => c.Enabled);
            int fresh = cams.Count(c => c.Snapshot.Status == SnapshotStatus.Fresh);
            int stale = cams.Count(c => c.Snapshot.Status == SnapshotStatus.Stale);
            int noFrame = cams.Count(c => c.Snapshot.Status == SnapshotStatus.NoRecordedFrame ||
                                          c.Snapshot.Status == SnapshotStatus.Error);

            sb.AppendLine("<header>");
            sb.AppendLine("<h1>Camera Audit</h1>");
            // Milestone XProtect system info, up front.
            sb.AppendLine("<table class=\"sysinfo\">");
            Row(sb, "System", "Milestone XProtect");
            Row(sb, "Site", site.SiteName);
            if (!string.IsNullOrEmpty(site.ProductVersion)) Row(sb, "Version", site.ProductVersion);
            Row(sb, "Management server", site.ServerUri);
            Row(sb, "Generated", site.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm:ss") + "Z");
            Row(sb, "Cameras", total + " total, " + enabled + " enabled");
            sb.AppendLine("</table>");
            sb.AppendLine("<div class=\"counts\">");
            sb.AppendFormat("<span class=\"count ok\">{0} fresh</span>", fresh);
            sb.AppendFormat("<span class=\"count warn\">{0} stale</span>", stale);
            sb.AppendFormat("<span class=\"count bad\">{0} no frame</span>", noFrame);
            sb.AppendLine("</div></header>");
        }

        static void AppendRecorderSection(StringBuilder sb, RecorderAudit recorder, AuditOptions options)
        {
            sb.AppendLine("<section class=\"recorder\">");
            sb.AppendFormat("<h1>{0} <span class=\"muted\">{1}</span>{2}</h1>\n",
                H(recorder.Name),
                options.Redact ? "" : H(recorder.HostName),
                recorder.Reachable ? "" : " <span class=\"pill bad\">unreachable</span>");

            foreach (var hw in recorder.Hardwares)
            {
                sb.AppendFormat("<h2>{0}{1}</h2>\n",
                    H(hw.Name),
                    hw.Enabled ? "" : " <span class=\"pill bad\">hardware disabled</span>");

                sb.AppendLine("<div class=\"cards\">");
                foreach (var cam in hw.Cameras)
                    AppendCameraCard(sb, cam, hw, options);
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</section>");
        }

        static void AppendCameraCard(StringBuilder sb, CameraAudit cam, HardwareAudit hw, AuditOptions options)
        {
            string img = cam.Snapshot.ThumbnailBase64 != null
                ? "data:image/jpeg;base64," + cam.Snapshot.ThumbnailBase64
                : ImageHelper.PlaceholderDataUri(cam.Snapshot.FailureReason ?? "no image");

            string pillClass, pillText;
            switch (cam.Snapshot.Status)
            {
                case SnapshotStatus.Fresh: pillClass = "ok"; pillText = "fresh"; break;
                case SnapshotStatus.Stale: pillClass = "warn"; pillText = "stale"; break;
                case SnapshotStatus.Skipped: pillClass = "muted-pill"; pillText = "no snapshot"; break;
                default: pillClass = "bad"; pillText = cam.Snapshot.Status == SnapshotStatus.NoRecordedFrame ? "no frame" : "error"; break;
            }

            sb.AppendLine("<div class=\"card\">");
            sb.AppendFormat("<img src=\"{0}\" alt=\"{1}\" loading=\"lazy\">\n", img, H(cam.Name));
            sb.AppendLine("<div class=\"details\">");
            sb.AppendFormat("<div class=\"cam-name\">{0} <span class=\"pill {1}\">{2}</span></div>\n",
                H(cam.Name), pillClass, pillText);
            sb.AppendLine("<table>");
            Row(sb, "Brand", string.IsNullOrEmpty(hw.Model) ? "—" : hw.Model);
            Row(sb, "IP", options.Redact ? "(redacted)" : (string.IsNullOrEmpty(hw.Address) ? "—" : hw.Address));
            Row(sb, "Special notes", CredentialNote(hw, options));
            sb.AppendLine("</table></div></div>");
        }

        /// <summary>Device username/password for the Special notes field (§ report content).</summary>
        static string CredentialNote(HardwareAudit hw, AuditOptions options)
        {
            if (options.Redact) return "credentials redacted";
            string user = string.IsNullOrEmpty(hw.UserName) ? "—" : hw.UserName;
            string pass = string.IsNullOrEmpty(hw.Password) ? "(unavailable)" : hw.Password;
            return "User: " + user + "   Pass: " + pass;
        }

        static void Row(StringBuilder sb, string label, string value)
        {
            sb.AppendFormat("<tr><td>{0}</td><td>{1}</td></tr>\n", H(label), H(value));
        }

        static string H(string s)
        {
            return WebUtility.HtmlEncode(s ?? "");
        }

        /// <summary>Colour tokens per the ANPRMib report shell: green/amber/red status semantics.</summary>
        static string GetReportCss()
        {
            return @"
:root { --ok:#2e7d32; --warn:#ef6c00; --bad:#c62828; --ink:#1c2528; --muted:#68767d;
        --bg:#f4f6f7; --card:#ffffff; --line:#dde3e6; }
* { box-sizing:border-box; }
body { margin:0; padding:24px; font:14px/1.45 'Segoe UI',Arial,sans-serif; color:var(--ink); background:var(--bg); }
header h1, section.recorder>h1 { font-size:22px; margin:0 0 4px; }
h2 { font-size:15px; margin:18px 0 8px; border-bottom:1px solid var(--line); padding-bottom:4px; }
.muted { color:var(--muted); font-weight:normal; font-size:13px; }
.sysinfo { border-collapse:collapse; margin:8px 0 12px; font-size:13.5px; }
.sysinfo td { padding:2px 16px 2px 0; vertical-align:top; }
.sysinfo td:first-child { color:var(--muted); white-space:nowrap; font-weight:600; }
.counts { margin:10px 0 4px; }
.count { display:inline-block; background:var(--card); border:1px solid var(--line); border-radius:14px;
         padding:3px 12px; margin-right:8px; font-size:13px; }
.count.ok { border-color:var(--ok); color:var(--ok); }
.count.warn { border-color:var(--warn); color:var(--warn); }
.count.bad { border-color:var(--bad); color:var(--bad); }
section.recorder { background:var(--card); border:1px solid var(--line); border-radius:8px;
                   padding:16px 20px; margin-top:20px; }
.cards { display:flex; flex-wrap:wrap; gap:14px; }
.card { display:flex; gap:12px; border:1px solid var(--line); border-radius:8px; padding:10px;
        background:var(--bg); width:640px; max-width:100%; }
.card img { width:320px; height:180px; object-fit:contain; background:#20272b; border-radius:4px; flex-shrink:0; }
.details { min-width:0; }
.cam-name { font-weight:600; margin-bottom:6px; }
.details table { border-collapse:collapse; font-size:12.5px; }
.details td { padding:1px 10px 1px 0; vertical-align:top; word-break:break-word; }
.details td:first-child { color:var(--muted); white-space:nowrap; }
.pill { display:inline-block; border-radius:10px; padding:1px 10px; font-size:11.5px; font-weight:600;
        color:#fff; vertical-align:middle; }
.pill.ok { background:var(--ok); } .pill.warn { background:var(--warn); } .pill.bad { background:var(--bad); }
.pill.muted-pill { background:var(--muted); }
ul.index { font-size:15px; line-height:2; }
";
        }
    }
}
