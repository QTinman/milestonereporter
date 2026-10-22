using System;
using System.Collections.Generic;
using System.Text;

namespace XProtectCameraAudit
{
    public enum AuthMode
    {
        /// <summary>XProtect Basic user (internal IDP).</summary>
        Basic,
        /// <summary>Explicit Windows/AD credentials.</summary>
        Windows,
        /// <summary>Windows credentials of the account running the tool.</summary>
        WindowsCurrentUser
        // External IDP (Entra/Okta/Keycloak) is intentionally not supported in v1:
        // it needs LoginProviderInformation plus a browser/device-code flow (§2).
    }

    public sealed class AuditOptions
    {
        public Uri ServerUri;
        public AuthMode Auth = AuthMode.Basic;
        public string User;
        public string Password;
        public string Domain = "";

        public string OutputDirectory = ".";
        public string ReportBaseName = "camera-audit";
        /// <summary>Customer/company name — shown in the header and baked into the filename.</summary>
        public string Customer = "";
        public bool PagePerRecorder;

        /// <summary>Primary liveness criterion (§4/§7): a frame older than this is amber, not green.</summary>
        public int StalenessMinutes = 60;
        /// <summary>Bounded pool per recording server; >8 tends to trigger ImageServer back-pressure (§5).</summary>
        public int MaxConcurrentPerRecorder = 8;
        public bool SkipSnapshots;
        public int ThumbnailWidth = 320;

        public bool TrustAnyCertificate;
        public string TrustedCertThumbprint;

        /// <summary>Omit hardware address/username-style fields from the report and CSV.</summary>
        public bool Redact;

        /// <summary>Include device passwords — the technician's copy, not the customer's.</summary>
        public bool IncludePasswords;

        /// <summary>
        /// Where each camera's last good thumbnail is kept between runs; null = none.
        /// Defaults to "snapshots" beside the report.
        /// </summary>
        public string ArchiveDirectory;
        bool _noArchive;

        public static AuditOptions Parse(string[] args, out string error)
        {
            error = null;
            var o = new AuditOptions();
            string passwordEnv = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next()
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("missing value for " + a);
                    return args[++i];
                }

                try
                {
                    switch (a)
                    {
                        case "--server": o.ServerUri = new Uri(Next(), UriKind.Absolute); break;
                        case "--auth":
                            string mode = Next().ToLowerInvariant();
                            if (mode == "basic") o.Auth = AuthMode.Basic;
                            else if (mode == "windows") o.Auth = AuthMode.Windows;
                            else if (mode == "windows-current") o.Auth = AuthMode.WindowsCurrentUser;
                            else { error = "unknown --auth mode '" + mode + "' (basic | windows | windows-current)"; return null; }
                            break;
                        case "--user": o.User = Next(); break;
                        case "--password": o.Password = Next(); break;
                        case "--password-env": passwordEnv = Next(); break;
                        case "--domain": o.Domain = Next(); break;
                        case "--output": o.OutputDirectory = Next(); break;
                        case "--name": o.ReportBaseName = Next(); break;
                        case "--customer": o.Customer = Next(); break;
                        case "--page-per-recorder": o.PagePerRecorder = true; break;
                        case "--staleness-minutes": o.StalenessMinutes = int.Parse(Next()); break;
                        case "--max-concurrent": o.MaxConcurrentPerRecorder = int.Parse(Next()); break;
                        case "--skip-snapshots": o.SkipSnapshots = true; break;
                        case "--thumb-width": o.ThumbnailWidth = int.Parse(Next()); break;
                        case "--trust-any-cert": o.TrustAnyCertificate = true; break;
                        case "--trust-cert-thumbprint": o.TrustedCertThumbprint = Next().Replace(" ", "").ToUpperInvariant(); break;
                        case "--redact": o.Redact = true; break;
                        case "--passwords": o.IncludePasswords = true; break;
                        case "--archive": o.ArchiveDirectory = Next(); break;
                        case "--no-archive": o._noArchive = true; break;
                        case "--help":
                        case "-h": error = ""; return null;
                        default: error = "unknown argument '" + a + "'"; return null;
                    }
                }
                catch (Exception ex)
                {
                    error = "bad value for " + a + ": " + ex.Message;
                    return null;
                }
            }

            if (o.ServerUri == null) { error = "--server is required"; return null; }
            if (o.Redact && o.IncludePasswords) { error = "--redact and --passwords exclude each other"; return null; }

            if (o._noArchive) o.ArchiveDirectory = null;
            else if (string.IsNullOrEmpty(o.ArchiveDirectory))
                o.ArchiveDirectory = System.IO.Path.Combine(o.OutputDirectory, "snapshots");

            if (o.Password == null && passwordEnv != null)
                o.Password = Environment.GetEnvironmentVariable(passwordEnv);

            if (o.Auth != AuthMode.WindowsCurrentUser)
            {
                if (string.IsNullOrEmpty(o.User)) { error = "--user is required for --auth " + o.Auth; return null; }
                if (o.Password == null)
                    o.Password = PromptForPassword(o.User);
            }

            // Windows users may pass DOMAIN\user in one token.
            if (o.Auth == AuthMode.Windows && o.User != null && o.User.Contains("\\") && o.Domain.Length == 0)
            {
                var parts = o.User.Split(new[] { '\\' }, 2);
                o.Domain = parts[0];
                o.User = parts[1];
            }

            return o;
        }

        static string PromptForPassword(string user)
        {
            Console.Write("Password for {0}: ", user);
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
                if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
                if (key.KeyChar != '\0') sb.Append(key.KeyChar);
            }
        }

        public static string Usage()
        {
            return string.Join(Environment.NewLine, new List<string>
            {
                "XProtectCameraAudit — read-only camera inventory + snapshot report for Milestone XProtect",
                "",
                "usage: XProtectCameraAudit --server https://mgmt-server [options]",
                "",
                "auth:",
                "  --auth basic|windows|windows-current   site auth mode (default: basic)",
                "  --user <name>                          user name (or DOMAIN\\user for windows)",
                "  --password <pass>                      password (omit to be prompted)",
                "  --password-env <VAR>                   read password from environment variable",
                "  --domain <domain>                      Windows domain (if not given via DOMAIN\\user)",
                "",
                "certificates (self-signed servers — explicit opt-in only, never silent):",
                "  --trust-cert-thumbprint <sha1-hex>     trust exactly this server certificate",
                "  --trust-any-cert                       trust any certificate (logs each one; test only)",
                "",
                "snapshots:",
                "  --staleness-minutes <n>                frame older than this = stale/amber (default: 60)",
                "  --max-concurrent <n>                   parallel fetches per recording server (default: 8)",
                "  --thumb-width <px>                     thumbnail width (default: 320)",
                "  --skip-snapshots                       inventory only, no frames",
                "  --archive <dir>                        keep each camera's last good snapshot here between runs,",
                "                                         shown when a camera later has none (default: <output>\\snapshots)",
                "  --no-archive                           do not read or keep the archive",
                "",
                "output:",
                "  --output <dir>                         output directory (default: current)",
                "  --customer <name>                      customer/company; shown in header, baked into filename",
                "  --name <base>                          report base name when --customer is not set (default: camera-audit)",
                "  --page-per-recorder                    one HTML page per recording server + index (500+ cams)",
                "  --redact                               omit hardware addresses/usernames from output",
                "  --passwords                            include device passwords (technician copy; needs the",
                "                                         View Hardware Password permission)",
            });
        }
    }
}
