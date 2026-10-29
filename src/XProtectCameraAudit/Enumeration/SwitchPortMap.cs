using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using XProtectCameraAudit.Model;

namespace XProtectCameraAudit.Enumeration
{
    /// <summary>
    /// Which switch and port each device is on, from switch exports (--switch-ports).
    /// One line per learned address, tab-separated, as the switches' ARP/MAC export
    /// gives it:
    ///
    ///     Internet  10.110.26.103  Gi1/0/5  a0bd.1df8.0149  ARPA  Vlan110  0102-s04 - 19009106
    ///
    /// Fields are recognised by their shape rather than their position (MAC, IPv4,
    /// port, "VlanNNN"; the switch is the last remaining field), so a column more or
    /// less, a header line or a different MAC notation does not break it.
    ///
    /// A device's MAC is also learned on every uplink between it and the switch the
    /// export came from. Its own port is the one with the fewest addresses on it, so
    /// that one wins.
    /// </summary>
    public sealed class SwitchPortMap
    {
        sealed class Entry
        {
            public string Mac = "";
            public string Ip = "";
            public string Port = "";
            public string Vlan = "";
            public string Switch = "";
        }

        static readonly Regex PortShape = new Regex(
            @"^(?!vlan)[A-Za-z][A-Za-z\-]*\s?\d+(/\d+)+(\.\d+)?$", RegexOptions.IgnoreCase);
        static readonly Regex VlanShape = new Regex(@"^vlan\s?\d+$", RegexOptions.IgnoreCase);
        static readonly Regex Ipv4Shape = new Regex(@"^\d{1,3}(\.\d{1,3}){3}$");

        readonly List<Entry> _entries = new List<Entry>();
        // Addresses per switch|port; an uplink carries many, an access port one or two.
        readonly Dictionary<string, int> _macsOnPort = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int Lines { get; private set; }
        public int Skipped { get; private set; }

        public static SwitchPortMap Load(IEnumerable<string> files)
        {
            var map = new SwitchPortMap();
            foreach (var file in files)
                foreach (var line in File.ReadAllLines(file))
                    map.Add(line);

            foreach (var group in map._entries.GroupBy(e => Key(e.Switch, e.Port), StringComparer.OrdinalIgnoreCase))
                map._macsOnPort[group.Key] = group.Select(e => e.Mac).Distinct().Count();
            return map;
        }

        void Add(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // Tabs as exported; runs of 2+ spaces when it was pasted through something that ate them.
            var fields = (line.IndexOf('\t') >= 0 ? line.Split('\t') : Regex.Split(line.Trim(), @"\s{2,}"))
                .Select(f => f.Trim()).Where(f => f.Length > 0).ToList();

            var entry = new Entry();
            var rest = new List<string>();
            foreach (var f in fields)
            {
                string mac = MacAddressResolver.Normalize(f);
                if (entry.Mac.Length == 0 && mac.Length > 0 && LooksLikeMac(f)) entry.Mac = mac;
                else if (entry.Ip.Length == 0 && Ipv4Shape.IsMatch(f)) entry.Ip = f;
                else if (entry.Vlan.Length == 0 && VlanShape.IsMatch(f)) entry.Vlan = f;
                else if (entry.Port.Length == 0 && PortShape.IsMatch(f)) entry.Port = f;
                else rest.Add(f);
            }

            // The switch is the last field that is none of the above ("Internet" and
            // "ARPA" come before it).
            entry.Switch = rest.Count > 1 ? rest[rest.Count - 1] : "";

            if (entry.Mac.Length == 0 || entry.Port.Length == 0)
            {
                // A header, or a line without a port (the switch's own addresses).
                if (Lines > 0 || entry.Mac.Length > 0) Skipped++;
                return;
            }
            Lines++;
            _entries.Add(entry);
        }

        /// <summary>Sets Switch/SwitchPort/SwitchVlan on every device found in the exports.</summary>
        public int Apply(SiteAudit site)
        {
            int found = 0;
            foreach (var hw in site.Recorders.SelectMany(r => r.Hardwares))
            {
                var candidates = hw.MacAddress.Length > 0
                    ? _entries.Where(e => e.Mac == hw.MacAddress).ToList()
                    : new List<Entry>();

                // No MAC from XProtect or ARP: the switch's ARP entry for the IP has it.
                // Not when the MAC is known but absent: the IP then belongs to
                // something else in the export.
                if (hw.MacAddress.Length == 0)
                {
                    string ip = HostIp(hw.Address);
                    if (ip.Length > 0) candidates = _entries.Where(e => e.Ip == ip).ToList();
                    if (candidates.Count > 0)
                    {
                        hw.MacAddress = candidates[0].Mac;
                        hw.MacSource = "switch";
                    }
                }
                if (candidates.Count == 0) continue;

                var best = candidates
                    .OrderBy(e => _macsOnPort[Key(e.Switch, e.Port)])
                    .First();
                hw.Switch = best.Switch;
                hw.SwitchPort = best.Port;
                hw.SwitchVlan = best.Vlan;
                found++;
            }
            return found;
        }

        /// <summary>
        /// Twelve hex digits alone are a MAC only when written as one (aabb.ccdd.eeff,
        /// aa:bb:.., aa-bb-..); a bare number of that length is something else.
        /// </summary>
        static bool LooksLikeMac(string field)
        {
            return field.IndexOf('.') > 0 || field.IndexOf(':') > 0 || field.IndexOf('-') > 0;
        }

        static string HostIp(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "";
            string host = address.Trim();
            Uri uri;
            if (Uri.TryCreate(host, UriKind.Absolute, out uri) && !string.IsNullOrEmpty(uri.Host)) host = uri.Host;
            IPAddress ip;
            return IPAddress.TryParse(host, out ip) ? ip.ToString() : "";
        }

        static string Key(string sw, string port)
        {
            return sw + "|" + port;
        }
    }
}
