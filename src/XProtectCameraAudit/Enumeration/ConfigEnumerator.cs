using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VideoOS.Platform;
using VideoOS.Platform.ConfigurationItems;
using XProtectCameraAudit.Model;

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

            var masterSite = EnvironmentManager.Instance.MasterSite;
            site.SiteName = Safe(() => masterSite.Name, options.ServerUri.Host);

            var ms = new ManagementServer(masterSite);

            foreach (RecordingServer rs in ms.RecordingServerFolder.RecordingServers)
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
                        Enabled = Safe(() => hw.Enabled, false)
                    };
                    recorder.Hardwares.Add(hardware);

                    foreach (Camera cam in Safe(() => hw.CameraFolder.Cameras.ToList(), new List<Camera>()))
                    {
                        var audit = new CameraAudit
                        {
                            CameraGuid = ParseGuid(Safe(() => cam.Id, "")),
                            Name = Safe(() => cam.Name, "(unnamed camera)"),
                            Enabled = Safe(() => cam.Enabled, false),
                            Channel = Safe(() => cam.Channel, 0),
                            Description = Safe(() => cam.Description, ""),
                            GisPoint = NormalizeGis(Safe(() => cam.GisPoint, "")),
                            // Hint only — Corporate/Expert drive recording via rules,
                            // so this flag may be absent or non-authoritative (§3).
                            RecordingEnabledHint = ProbeRecordingEnabled(cam),
                            ConfiguredCodec = ProbeStreamProperty(cam, "Codec"),
                            ConfiguredResolution = ProbeStreamProperty(cam, "Resolution")
                        };
                        hardware.Cameras.Add(audit);
                    }
                }

                recorder.Hardwares = recorder.Hardwares
                    .OrderBy(h => h.Address, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var h in recorder.Hardwares)
                    h.Cameras = h.Cameras.OrderBy(c => c.Channel).ThenBy(c => c.Name).ToList();
            }

            site.Recorders = site.Recorders.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return site;
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
