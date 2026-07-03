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
                    Console.WriteLine("NOTE: report and CSV include device IP/username/password in cleartext. " +
                                      "Store them securely or run with --redact.");

                if (!options.SkipSnapshots && cameraCount > 0)
                    new SnapshotFetcher(options, login).FetchAll(site);

                Directory.CreateDirectory(options.OutputDirectory);
                string stamp = site.GeneratedAtUtc.ToString("yyyyMMdd-HHmmss");
                var written = new List<string>();

                if (options.PagePerRecorder)
                {
                    var pages = new List<KeyValuePair<RecorderAudit, string>>();
                    int page = 0;
                    foreach (var recorder in site.Recorders)
                    {
                        // page counter keeps filenames unique for duplicate/empty recorder names
                        string file = string.Format("{0}-{1}-{2:D2}-{3}.html", options.ReportBaseName, stamp, ++page, Slug(recorder.Name));
                        File.WriteAllText(Path.Combine(options.OutputDirectory, file),
                            AuditReportGenerator.GenerateRecorderPage(site, recorder, options), Encoding.UTF8);
                        pages.Add(new KeyValuePair<RecorderAudit, string>(recorder, file));
                        written.Add(file);
                    }
                    string index = string.Format("{0}-{1}.html", options.ReportBaseName, stamp);
                    File.WriteAllText(Path.Combine(options.OutputDirectory, index),
                        AuditReportGenerator.GenerateIndex(site, options, pages), Encoding.UTF8);
                    written.Insert(0, index);
                }
                else
                {
                    string html = string.Format("{0}-{1}.html", options.ReportBaseName, stamp);
                    File.WriteAllText(Path.Combine(options.OutputDirectory, html),
                        AuditReportGenerator.GenerateCameraAudit(site, options), Encoding.UTF8);
                    written.Add(html);
                }

                string csv = string.Format("{0}-{1}.csv", options.ReportBaseName, stamp);
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

        static string Slug(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            return sb.ToString().Trim('-');
        }
    }
}
