using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using VideoOS.Platform;

namespace XProtectCameraAudit.Auth
{
    /// <summary>
    /// Standalone MIP SDK login (§2). Token refresh is handled by EnvironmentManager
    /// internally; on NotAuthorized mid-run the workers ask for exactly one re-login
    /// via <see cref="TryReloginOnce"/>, then hard-fail.
    /// </summary>
    public sealed class LoginManager
    {
        readonly AuditOptions _options;
        readonly object _gate = new object();
        bool _reloginUsed;

        public LoginManager(AuditOptions options)
        {
            _options = options;
        }

        public void InstallCertificatePolicy()
        {
            if (!_options.TrustAnyCertificate && string.IsNullOrEmpty(_options.TrustedCertThumbprint))
                return; // default OS validation

            Console.WriteLine("WARNING: custom certificate trust is active ({0}).",
                _options.TrustAnyCertificate ? "--trust-any-cert" : "thumbprint pin");

            ServicePointManager.ServerCertificateValidationCallback =
                (sender, cert, chain, errors) =>
                {
                    if (errors == SslPolicyErrors.None) return true;
                    var x2 = cert as X509Certificate2 ?? new X509Certificate2(cert);
                    if (!string.IsNullOrEmpty(_options.TrustedCertThumbprint))
                        return string.Equals(x2.Thumbprint, _options.TrustedCertThumbprint, StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine("  accepting certificate {0} (subject: {1}) despite: {2}",
                        x2.Thumbprint, x2.Subject, errors);
                    return true;
                };
        }

        public void Initialize()
        {
            VideoOS.Platform.SDK.Environment.Initialize();
            // JPEGVideoSource lives in the Export area; Media provides the toolkit
            // used to transcode non-JPEG codecs into JPEG frames.
            VideoOS.Platform.SDK.Export.Environment.Initialize();
            VideoOS.Platform.SDK.Media.Environment.Initialize();
        }

        public void Login()
        {
            var uri = _options.ServerUri;
            var cc = new CredentialCache();
            switch (_options.Auth)
            {
                case AuthMode.Basic:
                    cc.Add(uri, "Basic", new NetworkCredential(_options.User, _options.Password));
                    break;
                case AuthMode.Windows:
                    cc.Add(uri, "Negotiate", new NetworkCredential(_options.User, _options.Password, _options.Domain));
                    break;
                case AuthMode.WindowsCurrentUser:
                    cc.Add(uri, "Negotiate", CredentialCache.DefaultNetworkCredentials);
                    break;
            }

            VideoOS.Platform.SDK.Environment.RemoveAllServers();
            VideoOS.Platform.SDK.Environment.AddServer(uri, cc);
            VideoOS.Platform.SDK.Environment.Login(uri);

            var site = EnvironmentManager.Instance.MasterSite;
            Console.WriteLine("Logged in to '{0}' ({1}) as {2}",
                site != null ? site.Name : "?", uri,
                _options.Auth == AuthMode.WindowsCurrentUser ? Environment.UserName : _options.User);
        }

        /// <summary>One re-login for the whole run; second NotAuthorized is fatal (§2).</summary>
        public bool TryReloginOnce()
        {
            lock (_gate)
            {
                if (_reloginUsed) return false;
                _reloginUsed = true;
                Console.WriteLine("NotAuthorized received — re-logging in once...");
                try
                {
                    Login();
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Re-login failed: " + ex.Message);
                    return false;
                }
            }
        }

        public void Logout()
        {
            try
            {
                VideoOS.Platform.SDK.Environment.Logout(_options.ServerUri);
                VideoOS.Platform.SDK.Environment.RemoveAllServers();
            }
            catch
            {
                // best-effort; process is exiting
            }
        }
    }
}
