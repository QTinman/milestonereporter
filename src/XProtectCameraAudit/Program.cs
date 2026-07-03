using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XProtectCameraAudit.Auth;
using XProtectCameraAudit.Enumeration;
using XProtectCameraAudit.Model;
using XProtectCameraAudit.Report;
using XProtectCameraAudit.Snapshot;

namespace XProtectCameraAudit
{
    /// <summary>
    /// Read-only XProtect camera audit: one process, one login, config enumeration via
    /// the Configuration Items API, recorded-primary snapshots via JPEGVideoSource,
    /// single self-contained HTML report + companion CSV.
    /// Exit codes: 0 = all cameras produced a frame, 2 = completed with gaps, 1 = fatal.
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            string parseError;
            var options = AuditOptions.Parse(args, out parseError);
            if (options == null)
            {
                if (!string.IsNullOrEmpty(parseError)) Console.Error.WriteLine("error: " + parseError + Environment.NewLine);
                Console.WriteLine(AuditOptions.Usage());
                return string.IsNullOrEmpty(parseError) ? 0 : 1;
            }

            var login = new LoginManager(options);
            try
            {
                login.InstallCertificatePolicy();
                login.Initialize();
                login.Login();

                Console.WriteLine("Enumerating configuration...");
                var site = ConfigEnumerator.Enumerate(options);
                int cameraCount = site.Recorders.SelectMany(r => r.Hardwares).Sum(h => h.Cameras.Count);
                Console.WriteLine("Found {0} recording servers, {1} cameras.", site.Recorders.Count, cameraCount);

                if (!options.Redact)
                    Console.WriteLine("NOTE: report and CSV include device IP and username (no passwords). " +
                                      "Run with --redact to omit them.");

                if (!options.SkipSnapshots && cameraCount > 0)
                    new SnapshotFetcher(options, login).FetchAll(site);

                Directory.CreateDirectory(options.OutputDirectory);
                string baseName = BuildBaseName(options, site);
                var written = new List<string>();

                if (options.PagePerRecorder)
                {
                    var pages = new List<KeyValuePair<RecorderAudit, string>>();
                    int page = 0;
                    foreach (var recorder in site.Recorders)
                    {
                        // page counter keeps filenames unique for duplicate/empty recorder names
                        string file = string.Format("{0}-{1:D2}-{2}.html", baseName, ++page, Slug(recorder.Name));
                        File.WriteAllText(Path.Combine(options.OutputDirectory, file),
                            AuditReportGenerator.GenerateRecorderPage(site, recorder, options), Encoding.UTF8);
                        pages.Add(new KeyValuePair<RecorderAudit, string>(recorder, file));
                        written.Add(file);
                    }
                    string index = baseName + ".html";
                    File.WriteAllText(Path.Combine(options.OutputDirectory, index),
                        AuditReportGenerator.GenerateIndex(site, options, pages), Encoding.UTF8);
                    written.Insert(0, index);
                }
                else
                {
                    string html = baseName + ".html";
                    File.WriteAllText(Path.Combine(options.OutputDirectory, html),
                        AuditReportGenerator.GenerateCameraAudit(site, options), Encoding.UTF8);
                    written.Add(html);
                }

                string csv = baseName + ".csv";
                CsvReportWriter.Write(site, options, Path.Combine(options.OutputDirectory, csv));
                written.Add(csv);

                Console.WriteLine();
                foreach (var file in written)
                    Console.WriteLine("Wrote " + Path.Combine(options.OutputDirectory, file));

                var cams = site.Recorders.SelectMany(r => r.Hardwares).SelectMany(h => h.Cameras).ToList();
                bool gaps = cams.Any(c => c.Snapshot.Status == SnapshotStatus.NoRecordedFrame ||
                                          c.Snapshot.Status == SnapshotStatus.Error);
                return gaps ? 2 : 0;
            }
            catch (AuditFatalException ex)
            {
                Console.Error.WriteLine("FATAL: " + ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL: " + ex.Message);
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
            finally
            {
                login.Logout();
            }
        }

        /// <summary>
        /// Self-identifying file stem. With --customer: "customer-site-yyyyMMdd"
        /// (e.g. acme-northgate-20260703) so files sort and read as records. A same-day
        /// re-run overwrites the day's file by design; drop --customer to fall back to a
        /// timestamped "base-yyyyMMdd-HHmmss" name that never collides.
        /// </summary>
        static string BuildBaseName(AuditOptions options, SiteAudit site)
        {
            if (!string.IsNullOrEmpty(options.Customer))
            {
                string stem = Slug(options.Customer);
                string sitePart = Slug(site.SiteName);
                if (sitePart.Length > 0 && sitePart != stem) stem += "-" + sitePart;
                return stem + "-" + site.GeneratedAtUtc.ToString("yyyyMMdd");
            }
            return options.ReportBaseName + "-" + site.GeneratedAtUtc.ToString("yyyyMMdd-HHmmss");
        }

        static string Slug(string name)
        {
            var sb = new StringBuilder();
            char prev = '-';
            foreach (char c in (name ?? "").ToLowerInvariant())
            {
                char m = char.IsLetterOrDigit(c) ? c : '-';
                if (m == '-' && prev == '-') continue; // collapse runs of separators
                sb.Append(m);
                prev = m;
            }
            return sb.ToString().Trim('-');
        }
    }
}
