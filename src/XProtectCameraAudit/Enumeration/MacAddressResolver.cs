using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VideoOS.Platform.ConfigurationItems;

namespace XProtectCameraAudit.Enumeration
{
    /// <summary>
    /// A device's MAC address, for matching it to a switch port later. First from
    /// XProtect's hardware driver settings (most drivers keep a "MacAddress" there,
    /// read from the device when it was added); else, when the device is on a subnet
    /// this machine is on, from ARP. Off-subnet ARP would answer with the router's
    /// MAC, which would point at the wrong switch port, so it is not tried.
    /// </summary>
    public static class MacAddressResolver
    {
        public static void Resolve(Hardware hw, string address, out string mac, out string source)
        {
            mac = "";
            source = "";
            // Virtual devices (the DirectShow server is "0.0.0.1") report the MAC of
            // the machine they run on, which would send someone to the wrong port.
            IPAddress ip = ParseIPv4(address);
            if (ip != null && (ip.GetAddressBytes()[0] == 0 || IPAddress.IsLoopback(ip))) return;

            mac = Normalize(FromDriverSettings(hw));
            source = mac.Length > 0 ? "XProtect" : "";
            if (mac.Length > 0) return;

            mac = Normalize(FromArp(address));
            source = mac.Length > 0 ? "ARP" : "";
        }

        /// <summary>"AA:BB:CC:DD:EE:FF", or "" when it is not a MAC.</summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var hex = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            if (hex.Length != 12 || hex.All(c => c == '0') || hex.All(c => c == 'F')) return "";
            return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
        }

        /// <summary>
        /// HardwareDriverSettingsFolder → HardwareDriverSettings → HardwareDriverSettingsChildItems
        /// → Properties, a key containing "MacAddress". Reflection, as the tree differs
        /// across SDK versions and drivers; any miss is "".
        /// </summary>
        static string FromDriverSettings(Hardware hw)
        {
            try
            {
                object folder = GetProp(hw, "HardwareDriverSettingsFolder");
                var settingsList = GetProp(folder, "HardwareDriverSettings") as IEnumerable;
                if (settingsList == null) return "";

                foreach (object settings in settingsList)
                {
                    var children = GetProp(settings, "HardwareDriverSettingsChildItems") as IEnumerable;
                    if (children == null) continue;
                    foreach (object child in children)
                    {
                        object props = GetProp(child, "Properties");
                        if (props == null) continue;
                        var keys = (GetProp(props, "KeysFullName") ?? GetProp(props, "Keys")) as IEnumerable;
                        var getValue = props.GetType().GetMethod("GetValue", new[] { typeof(string) });
                        if (keys == null || getValue == null) continue;

                        foreach (object keyObj in keys)
                        {
                            string key = keyObj as string;
                            if (key == null || key.IndexOf("MacAddress", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            var value = getValue.Invoke(props, new object[] { key }) as string;
                            if (Normalize(value).Length > 0) return value;
                        }
                    }
                }
            }
            catch
            {
                // driver/SDK variance — non-fatal by design
            }
            return "";
        }

        static string FromArp(string address)
        {
            try
            {
                IPAddress ip = ParseIPv4(address);
                if (ip == null || !OnLocalSubnet(ip)) return "";

                var buffer = new byte[6];
                int length = buffer.Length;
                int dest = BitConverter.ToInt32(ip.GetAddressBytes(), 0);
                if (SendARP(dest, 0, buffer, ref length) != 0 || length != 6) return "";
                return BitConverter.ToString(buffer);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>The IPv4 address in "http://192.168.1.64/", "192.168.1.64:80" or a host name.</summary>
        static IPAddress ParseIPv4(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            string host = address.Trim();
            Uri uri;
            if (Uri.TryCreate(host, UriKind.Absolute, out uri) && !string.IsNullOrEmpty(uri.Host)) host = uri.Host;
            else host = host.Split(':', '/')[0];

            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
                return ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;
            try
            {
                return Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch { return null; }
        }

        static bool OnLocalSubnet(IPAddress ip)
        {
            byte[] target = ip.GetAddressBytes();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                    byte[] local = unicast.Address.GetAddressBytes();
                    byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                    bool same = true;
                    for (int i = 0; i < 4 && same; i++)
                        same = (local[i] & mask[i]) == (target[i] & mask[i]);
                    if (same) return true;
                }
            }
            return false;
        }

        static object GetProp(object target, string name)
        {
            if (target == null) return null;
            var prop = target.GetType().GetProperty(name);
            return prop == null ? null : prop.GetValue(target, null);
        }

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int physicalAddrLen);
    }
}
