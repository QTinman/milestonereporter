using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VideoOS.Platform;
using VideoOS.Platform.ConfigurationItems;
using XProtectCameraAudit.Model;
using XProtectCameraAudit.Snapshot;

namespace XProtectCameraAudit.Enumeration
{
    /// <summary>
    /// Configuration Items API walk (§3):
    /// ManagementServer → RecordingServerFolder → HardwareFolder → CameraFolder.
    /// Disabled cameras are included by design — "licensed but forgotten" is the
    /// high-value audit finding; filtering later is trivial.
    /// </summary>
    public static class ConfigEnumerator
    {
        public static SiteAudit Enumerate(AuditOptions options)
        {
            var site = new SiteAudit
            {
                ServerUri = options.ServerUri.ToString(),
                GeneratedAtUtc = DateTime.UtcNow
            };

            // MasterSite is an FQID (no Name). Resolve the display name via its Item.
            var masterSite = EnvironmentManager.Instance.MasterSite;
            site.SiteName = Safe(() => Configuration.Instance.GetItem(masterSite).Name, options.ServerUri.Host);

            var ms = new ManagementServer(masterSite);
            site.ProductVersion = ReadProductVersion(ms);

            var all = ms.RecordingServerFolder.RecordingServers.ToList();
            site.RecordersOnSite = all.Count;

            foreach (RecordingServer rs in SelectRecorders(all, options.Recorders))
            {
                var recorder = new RecorderAudit
                {
                    RecorderGuid = ParseGuid(Safe(() => rs.Id, "")),
                    Name = Safe(() => rs.Name, "(unnamed recorder)"),
                    HostName = Safe(() => rs.HostName, "")
                };
                site.Recorders.Add(recorder);

                List<Hardware> hardwares;
                try
                {
                    hardwares = rs.HardwareFolder.Hardwares.ToList();
                }
                catch (Exception ex)
                {
                    // Recorder config unreadable (offline / stopped). Keep the section so
                    // the report shows the outage instead of silently dropping cameras.
                    recorder.Reachable = false;
                    Console.WriteLine("  recorder '{0}': hardware enumeration failed: {1}", recorder.Name, ex.Message);
                    continue;
                }

                foreach (Hardware hw in hardwares)
                {
                    var hardware = new HardwareAudit
                    {
                        HardwareGuid = ParseGuid(Safe(() => hw.Id, "")),
                        Name = Safe(() => hw.Name, "(unnamed hardware)"),
                        Model = Safe(() => hw.Model, ""),
                        Address = options.Redact ? "" : Safe(() => hw.Address, ""),
                        Enabled = Safe(() => hw.Enabled, false),
                        UserName = options.Redact ? "" : Safe(() => hw.UserName, ""),
                        Password = options.IncludePasswords ? ReadHardwarePassword(hw) : ""
                    };
                    if (!options.Redact)
                        MacAddressResolver.Resolve(hw, hardware.Address, out hardware.MacAddress, out hardware.MacSource);
                    recorder.Hardwares.Add(hardware);

                    foreach (Camera cam in Safe(() => hw.CameraFolder.Cameras.ToList(), new List<Camera>()))
                    {
                        var audit = new CameraAudit
                        {
                            CameraGuid = ParseGuid(Safe(() => cam.Id, "")),
                            Name = Safe(() => cam.Name, "(unnamed camera)"),
                            // A camera on disabled hardware reports itself enabled, but
                            // XProtect serves it no more than a disabled one.
                            Enabled = hardware.Enabled && Safe(() => cam.Enabled, false),
                            Channel = Safe(() => cam.Channel, 0),
                            Description = Safe(() => cam.Description, ""),
                            GisPoint = NormalizeGis(Safe(() => cam.GisPoint, "")),
                            // Hint only — Corporate/Expert drive recording via rules,
                            // so this flag may be absent or non-authoritative (§3).
                            RecordingEnabledHint = ProbeRecordingEnabled(cam),
                            ConfiguredCodec = ProbeStreamProperty(cam, "Codec"),
                            ConfiguredResolution = ProbeStreamProperty(cam, "Resolution")
                        };
                        if (!audit.Enabled) audit.Snapshot = SnapshotResult.Disabled();
                        hardware.Cameras.Add(audit);
                    }
                }

                // Hardware with nothing enabled last: what a technician comes for is above it.
                recorder.Hardwares = recorder.Hardwares
                    .OrderBy(h => h.Cameras.Any(c => c.Enabled) ? 0 : 1)
                    .ThenBy(h => h.Address, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var h in recorder.Hardwares)
                    h.Cameras = h.Cameras.OrderBy(c => c.Channel).ThenBy(c => c.Name).ToList();
            }

            site.Recorders = site.Recorders.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return site;
        }

        /// <summary>
        /// The recording servers --recorder names, or all of them when none is given.
        /// A name that matches nothing stops the run: a typo should not produce a
        /// report that looks complete but is missing a server.
        /// </summary>
        static List<RecordingServer> SelectRecorders(List<RecordingServer> all, List<string> wanted)
        {
            if (wanted.Count == 0) return all;

            var chosen = new List<RecordingServer>();
            var unknown = new List<string>();
            foreach (string name in wanted)
            {
                var matches = all.Where(rs => RecorderMatches(rs, name)).ToList();
                if (matches.Count == 0) unknown.Add(name);
                chosen.AddRange(matches.Where(rs => !chosen.Contains(rs)));
            }

            if (unknown.Count > 0)
            {
                var available = all.Select(rs =>
                {
                    string name = Safe(() => rs.Name, "(unnamed recorder)");
                    string host = Safe(() => rs.HostName, "");
                    return host.Length > 0 && !host.Equals(name, StringComparison.OrdinalIgnoreCase)
                        ? name + " (" + host + ")" : name;
                });
                throw new AuditFatalException(
                    "no recording server matches --recorder " + string.Join(", ", unknown.Select(u => "'" + u + "'")) +
                    ". The site has: " + string.Join(", ", available), null);
            }
            return chosen;
        }

        /// <summary>Name or host name, ignoring case; a short host name matches the FQDN.</summary>
        static bool RecorderMatches(RecordingServer rs, string wanted)
        {
            string name = Safe(() => rs.Name, "");
            string host = Safe(() => rs.HostName, "");
            string shortHost = host.Split('.')[0];
            return wanted.Equals(name, StringComparison.OrdinalIgnoreCase)
                || wanted.Equals(host, StringComparison.OrdinalIgnoreCase)
                || (shortHost.Length > 0 && wanted.Equals(shortHost, StringComparison.OrdinalIgnoreCase));
        }

        static string NormalizeGis(string wkt)
        {
            if (string.IsNullOrWhiteSpace(wkt)) return "";
            return wkt.Trim().Equals("POINT EMPTY", StringComparison.OrdinalIgnoreCase) ? "" : wkt.Trim();
        }

        static Guid ParseGuid(string id)
        {
            Guid g;
            return Guid.TryParse(id, out g) ? g : Guid.Empty;
        }

        static T Safe<T>(Func<T> get, T fallback)
        {
            try { return get(); }
            catch { return fallback; }
        }

        /// <summary>
        /// The device password XProtect logs in with. Needs the View Hardware Password
        /// permission (2022 R1+); "" without it or when the task fails — non-fatal, the
        /// card then says "(unavailable)".
        /// </summary>
        static string ReadHardwarePassword(Hardware hw)
        {
            try
            {
                ServerTask task = hw.ReadPasswordHardware();
                for (int i = 0; i < 50 && task.State == StateEnum.InProgress; i++)
                {
                    System.Threading.Thread.Sleep(100);
                    task.UpdateState();
                }
                if (task.State != StateEnum.Success) return "";
                return task.GetProperty("Password") ?? "";
            }
            catch
            {
                return "";
            }
        }

        static string ReadProductVersion(ManagementServer ms)
        {
            foreach (var name in new[] { "Version", "ProductVersion", "ServerVersion" })
            {
                var value = Safe<string>(() =>
                {
                    var prop = ms.GetType().GetProperty(name);
                    var v = prop != null ? prop.GetValue(ms, null) : null;
                    return v == null ? null : v.ToString();
                }, null);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return "";
        }

        static string ProbeRecordingEnabled(Camera cam)
        {
            try
            {
                var prop = cam.GetType().GetProperty("RecordingEnabled");
                if (prop == null) return "";
                var value = prop.GetValue(cam, null);
                return value == null ? "" : value.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// Best-effort stream setting lookup via reflection over
        /// DeviceDriverSettingsFolder → DeviceDriverSettings → StreamChildItems → Properties.
        /// Drivers vary wildly and the property tree differs across SDK versions, so any
        /// miss returns "" (§3: missing props are non-fatal). The report's authoritative
        /// resolution comes from the decoded snapshot instead.
        /// </summary>
        static string ProbeStreamProperty(Camera cam, string keyContains)
        {
            try
            {
                object folder = GetProp(cam, "DeviceDriverSettingsFolder");
                object settingsList = GetProp(folder, "DeviceDriverSettings");
                object settings = FirstOrNull(settingsList);
                object streams = GetProp(settings, "StreamChildItems");
                if (streams == null) return "";

                foreach (object stream in (IEnumerable)streams)
                {
                    object props = GetProp(stream, "Properties");
                    if (props == null) continue;

                    var keys = (GetProp(props, "KeysFullName") ?? GetProp(props, "Keys")) as IEnumerable;
                    if (keys == null) continue;

                    var getValue = props.GetType().GetMethod("GetValue", new[] { typeof(string) });
                    if (getValue == null) continue;

                    foreach (object keyObj in keys)
                    {
                        string key = keyObj as string;
                        if (key == null || key.IndexOf(keyContains, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        var value = getValue.Invoke(props, new object[] { key }) as string;
                        if (!string.IsNullOrEmpty(value)) return value;
                    }
                }
            }
            catch
            {
                // driver/SDK variance — non-fatal by design
            }
            return "";
        }

        static object GetProp(object target, string name)
        {
            if (target == null) return null;
            var prop = target.GetType().GetProperty(name);
            return prop == null ? null : prop.GetValue(target, null);
        }

        static object FirstOrNull(object maybeEnumerable)
        {
            var enumerable = maybeEnumerable as IEnumerable;
            if (enumerable == null) return null;
            foreach (object item in enumerable) return item;
            return null;
        }
    }
}
