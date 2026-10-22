using System;
using System.Diagnostics;
using System.IO;

namespace XProtectCameraAudit.Report
{
    /// <summary>
    /// Prints a written HTML report to PDF with Microsoft Edge in headless mode - on
    /// every Windows 10/11 machine, so the tool needs no PDF library of its own.
    /// Chrome is used when Edge is missing.
    /// </summary>
    public static class PdfWriter
    {
        static readonly string[] Browsers =
        {
            @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
            @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe",
            @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
            @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
            @"%LocalAppData%\Google\Chrome\Application\chrome.exe"
        };

        /// <summary>Writes <paramref name="pdfPath"/>; false, with the reason on the console, when it could not.</summary>
        public static bool Write(string htmlPath, string pdfPath)
        {
            string browser = null;
            foreach (var candidate in Browsers)
            {
                var path = Environment.ExpandEnvironmentVariables(candidate);
                if (File.Exists(path)) { browser = path; break; }
            }
            if (browser == null)
            {
                Console.WriteLine("PDF not written: neither Edge nor Chrome found.");
                return false;
            }

            htmlPath = Path.GetFullPath(htmlPath);
            pdfPath = Path.GetFullPath(pdfPath);
            if (File.Exists(pdfPath)) File.Delete(pdfPath);

            // A profile of its own: a headless start beside a running Edge otherwise
            // hands the job to that window and exits without printing.
            string profile = Path.Combine(Path.GetTempPath(), "XProtectCameraAudit-pdf");
            var psi = new ProcessStartInfo(browser,
                "--headless=new --disable-gpu --no-first-run --no-pdf-header-footer" +
                " --user-data-dir=\"" + profile + "\"" +
                " --print-to-pdf=\"" + pdfPath + "\" \"" + new Uri(htmlPath).AbsoluteUri + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            try
            {
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(180000))
                    {
                        try { p.Kill(); } catch { }
                        Console.WriteLine("PDF not written: the browser did not finish within 3 minutes.");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("PDF not written: " + ex.Message);
                return false;
            }

            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
            {
                Console.WriteLine("PDF not written: the browser produced no file.");
                return false;
            }
            return true;
        }
    }
}
