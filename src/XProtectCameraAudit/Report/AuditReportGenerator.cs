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
            AppendNeedsAttention(sb, site, options, null);
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
            // Map each recorder to its page so attention rows can deep-link.
            var links = pages.ToDictionary(p => p.Key, p => p.Value);
            AppendNeedsAttention(sb, site, options, links);
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
            int noFrame = cams.Count(c => c.Snapshot.HasNoFrame);
            int disabled = cams.Count(c => !c.Enabled);

            sb.AppendLine("<header>");
            sb.AppendLine("<h1>Camera Audit</h1>");
            // Milestone XProtect system info, up front.
            sb.AppendLine("<table class=\"sysinfo\">");
            if (!string.IsNullOrEmpty(options.Customer)) Row(sb, "Customer", options.Customer);
            if (options.IncludePasswords) Row(sb, "Copy", "Technician — contains device passwords, not for the customer");
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
            if (disabled > 0)
                sb.AppendFormat("<span class=\"count\">{0} disabled</span>", disabled);
            sb.AppendLine("</div></header>");
        }

        /// <summary>
        /// Replacement worklist: cameras with no snapshot (no recorded frame or fetch error)
        /// listed up front so a field engineer sees what needs a physical visit before
        /// scrolling the full inventory. In page-per-recorder mode each row deep-links to
        /// the recorder's page. Renders nothing when every camera produced a frame.
        /// </summary>
        static void AppendNeedsAttention(StringBuilder sb, SiteAudit site, AuditOptions options,
            IDictionary<RecorderAudit, string> recorderLinks)
        {
            var rows = new List<Tuple<RecorderAudit, HardwareAudit, CameraAudit>>();
            foreach (var recorder in site.Recorders)
                foreach (var hw in recorder.Hardwares)
                    foreach (var cam in hw.Cameras)
                        if (cam.Snapshot.HasNoFrame)
                            rows.Add(Tuple.Create(recorder, hw, cam));

            if (rows.Count == 0) return;

            sb.AppendLine("<section class=\"attention\">");
            sb.AppendFormat("<h1>Needs attention <span class=\"pill bad\">{0}</span></h1>\n", rows.Count);
            sb.AppendLine("<p class=\"muted\">Cameras with no snapshot — likely offline or in need of replacement.</p>");
            sb.AppendLine("<table class=\"worklist\"><thead><tr>" +
                "<th>Camera</th><th>Recording server</th><th>Hardware</th><th>Brand</th><th>IP</th><th>Reason</th><th>Last seen</th>" +
                "</tr></thead><tbody>");
            foreach (var row in rows)
            {
                string recorderCell = H(row.Item1.Name);
                string link;
                if (recorderLinks != null && recorderLinks.TryGetValue(row.Item1, out link))
                    recorderCell = "<a href=\"" + H(link) + "\">" + recorderCell + "</a>";

                var archived = row.Item3.Snapshot.ArchivedFrameUtc;
                sb.AppendFormat("<tr><td>{0}</td><td>{1}</td><td>{2}</td><td>{3}</td><td>{4}</td><td>{5}</td><td>{6}</td></tr>\n",
                    H(row.Item3.Name), recorderCell, H(row.Item2.Name),
                    H(string.IsNullOrEmpty(row.Item2.Model) ? "—" : row.Item2.Model),
                    options.Redact ? "(redacted)" : H(string.IsNullOrEmpty(row.Item2.Address) ? "—" : row.Item2.Address),
                    H(row.Item3.Snapshot.FailureReason ?? "no snapshot"),
                    archived.HasValue ? H(Utc(archived.Value)) : "<span class=\"muted\">no snapshot kept</span>");
            }
            sb.AppendLine("</tbody></table></section>");
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
            var snap = cam.Snapshot;
            // Without a frame this run, the archived one shows where the camera points -
            // labelled, so it is not taken for the camera working.
            string img, caption = null;
            if (snap.ThumbnailBase64 != null)
                img = "data:image/jpeg;base64," + snap.ThumbnailBase64;
            else if (snap.ArchivedThumbnailBase64 != null)
            {
                img = "data:image/jpeg;base64," + snap.ArchivedThumbnailBase64;
                caption = "Last known view, " + Utc(snap.ArchivedFrameUtc.Value);
            }
            else
                img = ImageHelper.PlaceholderDataUri(snap.FailureReason ?? "no image");

            string pillClass, pillText;
            switch (snap.Status)
            {
                case SnapshotStatus.Fresh: pillClass = "ok"; pillText = "fresh"; break;
                case SnapshotStatus.Stale: pillClass = "warn"; pillText = "stale"; break;
                case SnapshotStatus.Skipped: pillClass = "muted-pill"; pillText = "no snapshot"; break;
                case SnapshotStatus.Disabled: pillClass = "muted-pill"; pillText = "disabled"; break;
                default: pillClass = "bad"; pillText = snap.Status == SnapshotStatus.NoRecordedFrame ? "no frame" : "error"; break;
            }

            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine("<figure>");
            // Not loading="lazy": a page printed to PDF would leave the cards below
            // the first screen without their picture.
            sb.AppendFormat("<img src=\"{0}\" alt=\"{1}\"{2}>\n", img, H(cam.Name),
                caption != null ? " class=\"archived\"" : "");
            if (caption != null) sb.AppendFormat("<figcaption>{0}</figcaption>\n", H(caption));
            sb.AppendLine("</figure>");
            sb.AppendLine("<div class=\"details\">");
            sb.AppendFormat("<div class=\"cam-name\">{0} <span class=\"pill {1}\">{2}</span></div>\n",
                H(cam.Name), pillClass, pillText);
            sb.AppendLine("<table>");
            Row(sb, "Brand", string.IsNullOrEmpty(hw.Model) ? "—" : hw.Model);
            Row(sb, "IP", options.Redact ? "(redacted)" : (string.IsNullOrEmpty(hw.Address) ? "—" : hw.Address));
            Row(sb, "User", options.Redact ? "(redacted)" : (string.IsNullOrEmpty(hw.UserName) ? "—" : hw.UserName));
            if (options.IncludePasswords)
                Row(sb, "Password", string.IsNullOrEmpty(hw.Password) ? "(unavailable)" : hw.Password);
            if (snap.FrameTimestampUtc.HasValue)
                Row(sb, "Frame", Utc(snap.FrameTimestampUtc.Value) + " (" + Age(snap.FrameTimestampUtc.Value) + " old)");
            else if (snap.Status != SnapshotStatus.Skipped && snap.Status != SnapshotStatus.Disabled)
                Row(sb, "Problem", snap.FailureReason ?? "no snapshot");
            sb.AppendLine("</table></div></div>");
        }

        static string Utc(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        }

        static string Age(DateTime utc)
        {
            var age = DateTime.UtcNow - utc;
            if (age.TotalMinutes < 90) return Math.Max(0, (int)age.TotalMinutes) + " min";
            if (age.TotalHours < 48) return (int)age.TotalHours + " h";
            return (int)age.TotalDays + " days";
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
.card figure { margin:0; flex-shrink:0; }
.card img { width:320px; height:180px; object-fit:contain; background:#20272b; border-radius:4px; display:block; }
.card img.archived { outline:3px solid var(--bad); outline-offset:-3px; filter:grayscale(60%); }
.card figcaption { font-size:12px; color:var(--bad); font-weight:600; margin-top:3px; }
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
section.attention { background:var(--card); border:1px solid var(--bad); border-left-width:4px;
                    border-radius:8px; padding:14px 20px; margin-top:20px; }
section.attention h1 { font-size:18px; margin:0 0 4px; }
table.worklist { border-collapse:collapse; width:100%; margin-top:8px; font-size:13px; }
table.worklist th { text-align:left; color:var(--muted); border-bottom:1px solid var(--line);
                    padding:4px 12px 4px 0; font-weight:600; }
table.worklist td { padding:4px 12px 4px 0; border-bottom:1px solid var(--line); vertical-align:top; }
table.worklist tr:last-child td { border-bottom:none; }
@page { size:A4; margin:12mm; }
@media print {
  body { padding:0; background:#fff; font-size:12px; }
  section.recorder, section.attention { border:none; padding:0; margin-top:12px; }
  .card { break-inside:avoid; width:100%; background:#fff; }
  h2 { break-after:avoid; }
  table.worklist tr { break-inside:avoid; }
  * { -webkit-print-color-adjust:exact; print-color-adjust:exact; }
}
";
        }
    }
}
